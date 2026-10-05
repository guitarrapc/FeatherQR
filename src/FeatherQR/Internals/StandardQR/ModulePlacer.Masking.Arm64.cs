#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Vectorized mask pattern selection for ARM64 with AdvSimd (NEON).
/// Selected at runtime by <see cref="ModulePlacer.MaskCode"/>; produces byte-identical matrices and identical pattern selections to the scalar bit-packed implementation in ModulePlacer.Masking.cs (verified by ModulePlacerMaskAdvSimdParityTest).
///
/// Two tiers, split at 64 modules as <see cref="MaskCodeAdvSimd"/> does:
/// - Versions 1-11: the AVX2 tiers' earlier lane-per-row form on 128-bit vectors (Vector128&lt;ulong&gt; = 2 rows per iteration, one ulong per row).
///   The AVX2 tier has since moved to four candidates per vector; that design has not been measured on ARM64.
/// - Versions 12-40: the transposed scorer (the design is in ModulePlacer.Masking.Transposed.cs). It masks and scores with the 128-bit tier's
///   code (ModulePlacer.Masking.Transposed.Vector128.cs), whose popcount is cnt and uadalp here, and packs rows and applies the winner
///   with this file's 16-module steps. On Apple M2 it took 0.90-0.97 of the two- and three-word SoA tiers' mask selection time at versions
///   12-27 and 0.42-0.47 at 28-40 on the JIT, 0.87-0.98 and 0.48-0.57 on NativeAOT, and replaced them (2026-10-05). The 128-bit tier as it
///   stands, with a SWAR popcount, movemask packing and an eight-module unpack, read 1.24-1.39 at 12-27, and with the NEON popcount alone 0.89-1.01.
/// NEON-specific choices:
/// - Popcount is native (cnt.16b); one uaddlp widens the per-byte counts to
///   ushort lanes, which accumulate directly in Vector128&lt;ushort&gt;
///   accumulators (2 instructions per popcount vs 3 for a full per-qword
///   widen). Worst-case lane sums stay far below ushort range, and totals
///   reduce once per score via uaddlv.
/// - Byte&lt;-&gt;bit edges run 16 modules per step: packing gathers per-byte bit
///   weights (cmeq+bic) and reduces with a uaddlp chain; unpacking broadcasts
///   the 16-bit delta chunk and replicates bytes with tbl + cmtst (the same
///   sequence as MicroQRModulePlacer.Unpack16).
///
/// This file only executes under AdvSimd.Arm64.IsSupported, so memory order is always little-endian and the SWAR tail reads skip endianness normalization.
///
/// The single-word tier, measured on Apple M2 vs the scalar bit-packed paths (MaskCodeArm findings log): v1 2.4x, v10 3.0x, zero allocations.
/// The ushort accumulate beat the per-qword AVX2-shaped accumulate by ~8% and the SIMD edges beat the SWAR edges by ~5-11%; the scalar scorer's early-exit is intentionally absent (structurally incompatible with vector accumulators, and the vector throughput win dwarfs it, same conclusion as the x64 loop).
/// </summary>
internal static partial class ModulePlacer
{
    /// <summary>Entry point for the NEON tiers. Caller guarantees AdvSimd.Arm64.IsSupported.</summary>
    internal static int MaskCodeAdvSimd(Span<byte> buffer, int size, int version, ReadOnlySpan<byte> blockedMask, QREccLevel eccLevel)
    {
        return size <= 64
            ? MaskCode64AdvSimd(buffer, size, version, blockedMask, eccLevel)
            : MaskCodeTransposedAdvSimd(buffer, size, version, blockedMask, eccLevel);
    }

    // ---------------------------------
    // Shared NEON pieces
    // ---------------------------------
    // Operand-order note: Vector128.AndNot(left, right) computes left & ~right (same helper convention as the Vector256 helper used in the AVX2 file);
    // the JIT emits bic. Every AndNot in this file is the cross-platform helper.

    /// <summary>
    /// Per-byte-pair popcount of a 128-bit vector as 8 ushort lanes (cnt.16b + uaddlp).
    /// Lanes accumulate across calls and reduce once per score via <see cref="SumAcc"/>; only the total is meaningful.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Pop16(Vector128<ulong> v)
        => AdvSimd.AddPairwiseWidening(AdvSimd.PopCount(v.AsByte()));

    /// <summary>Reduces a ushort popcount accumulator to its total (uaddlv).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SumAcc(Vector128<ushort> acc)
        => (int)AdvSimd.Arm64.AddAcrossWidening(acc).ToScalar();

    /// <summary>Per-byte bit weights [1,2,4,...,128] repeated: byte i of a 16-module chunk contributes bit i.</summary>
    private static readonly Vector128<byte> PackBitSel16 = Vector128.Create(
        (byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);

    /// <summary>Byte-replicate table indices: delta byte 0 -&gt; output bytes 0..7, delta byte 1 -&gt; bytes 8..15 (for tbl).</summary>
    private static readonly Vector128<byte> UnpackSel16 = Vector128.Create(
        (byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1);

    /// <summary>
    /// Packs 16 module bytes (0/1) into 16 bits: non-zero bytes select their bit weight (cmeq+bic), then a uaddlp chain sums each 8-byte half into the low and high result bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Pack16(Vector128<byte> v)
    {
        var zero = Vector128.Equals(v, Vector128<byte>.Zero);
        var bits = Vector128.AndNot(PackBitSel16, zero);
        var s = AdvSimd.AddPairwiseWidening(AdvSimd.AddPairwiseWidening(AdvSimd.AddPairwiseWidening(bits)));
        return s.GetElement(0) | (s.GetElement(1) << 8);
    }

    /// <summary>
    /// Packs a row of 0/1 module bytes into bits, 16 modules per SIMD step, then an 8-module SWAR step and a scalar tail.
    /// Bit c = row[c], same contract as PackRowBits64.
    /// </summary>
    internal static ulong PackRowBits64AdvSimd(ReadOnlySpan<byte> row)
    {
        ulong w = 0;
        var c = 0;
        ref var r = ref MemoryMarshal.GetReference(row);
        for (; c + 16 <= row.Length; c += 16)
        {
            w |= Pack16(Vector128.LoadUnsafe(ref r, (nuint)c)) << c;
        }
        for (; c + 8 <= row.Length; c += 8)
        {
            // ARM64 only (AdvSimd-guarded), so the load is little-endian by definition.
            var u = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, c));
            w |= ((u * 0x0102040810204080UL) >> 56) << c;
        }
        for (; c < row.Length; c++)
        {
            if (row[c] != 0)
            {
                w |= 1ul << c;
            }
        }
        return w;
    }

    /// <summary>
    /// Triple-word NEON row packer.
    /// The 16-module chunks are 16-aligned so they never straddle a word boundary; the 8-module SWAR steps stay within a word for the same reason.
    /// </summary>
    private static Row192 PackRowBits192AdvSimd(ReadOnlySpan<byte> row)
    {
        ulong w0 = 0, w1 = 0, w2 = 0;
        var c = 0;
        ref var r = ref MemoryMarshal.GetReference(row);
        for (; c + 16 <= row.Length; c += 16)
        {
            var bits = Pack16(Vector128.LoadUnsafe(ref r, (nuint)c));
            if (c < 64) w0 |= bits << c;
            else if (c < 128) w1 |= bits << (c - 64);
            else w2 |= bits << (c - 128);
        }
        for (; c + 8 <= row.Length; c += 8)
        {
            var u = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, c));
            var bits = (u * 0x0102040810204080UL) >> 56;
            if (c < 64) w0 |= bits << c;
            else if (c < 128) w1 |= bits << (c - 64);
            else w2 |= bits << (c - 128);
        }
        for (; c < row.Length; c++)
        {
            if (row[c] != 0)
            {
                if (c < 64) w0 |= 1ul << c;
                else if (c < 128) w1 |= 1ul << (c - 64);
                else w2 |= 1ul << (c - 128);
            }
        }
        return new Row192(w0, w1, w2);
    }

    /// <summary>Expands a 16-bit delta chunk to 16 bytes of 0/1 (tbl byte-replicate + cmtst bit test).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Unpack16(ushort bits16)
    {
        var src = Vector128.Create(bits16).AsByte();
        var repl = AdvSimd.Arm64.VectorTableLookup(src, UnpackSel16);
        return AdvSimd.CompareTest(repl, PackBitSel16) & Vector128.Create((byte)1);
    }

    /// <summary>XORs a packed 0/1 delta into a byte row, 16 modules per SIMD step, SWAR + scalar tails.</summary>
    internal static void XorUnpackRow64AdvSimd(Span<byte> row, ulong delta)
    {
        var c = 0;
        ref var r = ref MemoryMarshal.GetReference(row);
        for (; c + 16 <= row.Length; c += 16)
        {
            var bits01 = Unpack16((ushort)(delta >> c));
            var cur = Vector128.LoadUnsafe(ref r, (nuint)c);
            (cur ^ bits01).StoreUnsafe(ref r, (nuint)c);
        }
        for (; c + 8 <= row.Length; c += 8)
        {
            var b = (delta >> c) & 0xFF;
            var spread = (b * 0x0101010101010101UL) & 0x8040201008040201UL;
            spread |= spread >> 4;
            spread |= spread >> 2;
            spread |= spread >> 1;
            spread &= 0x0101010101010101UL;
            ref var p = ref Unsafe.Add(ref r, c);
            var cur = Unsafe.ReadUnaligned<ulong>(ref p);
            Unsafe.WriteUnaligned(ref p, cur ^ spread);
        }
        for (; c < row.Length; c++)
        {
            if (((delta >> c) & 1) != 0)
            {
                row[c] ^= 1;
            }
        }
    }

    /// <summary>Triple-word NEON delta unpack (16-aligned chunks never straddle words), SWAR + scalar tails.</summary>
    private static void XorUnpackRow192AdvSimd(ref byte rowRef, int len, in Row192 delta)
    {
        var c = 0;
        for (; c + 16 <= len; c += 16)
        {
            var bits01 = Unpack16((ushort)(delta.WordAt(c >> 6) >> (c & 63)));
            ref var p = ref Unsafe.Add(ref rowRef, c);
            var cur = Vector128.LoadUnsafe(ref p);
            (cur ^ bits01).StoreUnsafe(ref p);
        }
        for (; c + 8 <= len; c += 8)
        {
            var b = (delta.WordAt(c >> 6) >> (c & 63)) & 0xFF;
            var spread = (b * 0x0101010101010101UL) & 0x8040201008040201UL;
            spread |= spread >> 4;
            spread |= spread >> 2;
            spread |= spread >> 1;
            spread &= 0x0101010101010101UL;
            ref var p = ref Unsafe.Add(ref rowRef, c);
            var cur = Unsafe.ReadUnaligned<ulong>(ref p);
            Unsafe.WriteUnaligned(ref p, cur ^ spread);
        }
        for (; c < len; c++)
        {
            if (((delta.WordAt(c >> 6) >> (c & 63)) & 1) != 0)
            {
                Unsafe.Add(ref rowRef, c) ^= 1;
            }
        }
    }

    // ---------------------------------
    // Single-word tier (versions 1-11)
    // ---------------------------------

    internal static int MaskCode64AdvSimd(Span<byte> buffer, int size, int version, ReadOnlySpan<byte> blockedMask, QREccLevel eccLevel)
    {
        Span<ulong> packed = stackalloc ulong[64];
        Span<ulong> allowed = stackalloc ulong[64];
        Span<ulong> masked = stackalloc ulong[64];
        Span<ulong> nmasked = stackalloc ulong[64];
        Span<ulong> eqScratch = stackalloc ulong[64];
        Span<ulong> v5Scratch = stackalloc ulong[64];
        packed = packed[..size];
        allowed = allowed[..size];
        masked = masked[..size];
        nmasked = nmasked[..size];

        for (var y = 0; y < size; y++)
        {
            packed[y] = PackRowBits64AdvSimd(buffer.Slice(y * size, size));
        }

        // Version bits sit in blocked areas, hence identical for every pattern.
        if (version >= 7)
        {
            var versionBits = QRCodeConstants.GetVersionBits(version);
            for (var x = 0; x < 6; x++)
            {
                for (var y = 0; y < 3; y++)
                {
                    var bit = (versionBits & (1u << (x * 3 + y))) != 0;
                    packed[y + size - 11] = WithBit64(packed[y + size - 11], x, bit);
                    packed[x] = WithBit64(packed[x], y + size - 11, bit);
                }
            }
        }

        // Each allowed row (~blocked) is a contiguous bit slice of the blocked bitmask; a padded copy makes the two 8-byte slice reads always legal.
        Span<byte> padded = stackalloc byte[blockedMask.Length + 16];
        padded.Clear();
        blockedMask.CopyTo(padded);
        var rowMask = size == 64 ? ulong.MaxValue : (1ul << size) - 1;
        for (var y = 0; y < size; y++)
        {
            var bitOffset = y * size;
            var byteOff = bitOffset >> 3;
            var sh = bitOffset & 7;
            var u0 = Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(padded.Slice(byteOff)));
            var u1 = Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(padded.Slice(byteOff + 8)));
            var blocked = sh == 0 ? u0 : (u0 >> sh) | (u1 << (64 - sh));
            allowed[y] = ~blocked & rowMask;
        }

        var templates = _maskTemplates64;
        var bestPatternIndex = 0;
        var bestScore = int.MaxValue;
        for (var patternIndex = 0; patternIndex < 8; patternIndex++)
        {
            var tplBase = patternIndex * 12;
            for (int y = 0, tplRow = 0; y < size; y++)
            {
                masked[y] = packed[y] ^ (templates[tplBase + tplRow] & allowed[y]);
                if (++tplRow == 12) tplRow = 0;
            }
            PokeFormatBits64(masked, size, QRCodeConstants.GetFormatBits(eccLevel, patternIndex));

            var score = CalculateScore64AdvSimd(masked, nmasked, eqScratch, v5Scratch, size);
            if (score < bestScore)
            {
                bestPatternIndex = patternIndex;
                bestScore = score;
            }
        }

        // Apply the winner to the byte buffer: unpack the XOR delta 16 modules at a time.
        {
            var tplBase = bestPatternIndex * 12;
            for (int y = 0, tplRow = 0; y < size; y++)
            {
                XorUnpackRow64AdvSimd(buffer.Slice(y * size, size), _maskTemplates64[tplBase + tplRow] & allowed[y]);
                if (++tplRow == 12) tplRow = 0;
            }
        }

        return bestPatternIndex;
    }

    /// <summary>
    /// Lane-per-row Vector128 penalty scorer for single-word rows (structure mirrors the AVX2 tier's row-lane scorer, 2 rows per iteration).
    /// Row-direction rules run with per-lane shifts and native popcount; column-direction rules materialize eq (vertical run continuation) and v5 (4-deep AND window) arrays with vector passes, then score them with offset loads.
    /// Scalar tails reuse the scalar expressions.
    /// Popcounts accumulate in weight-grouped ushort accumulators (rule-1 ones / twos, rule-2 x3, rule-3 x40, balance) and reduce once per score.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static int CalculateScore64AdvSimd(ReadOnlySpan<ulong> rows, Span<ulong> nrows, Span<ulong> eqArr, Span<ulong> v5Arr, int size)
    {
        var rowMask = size == 64 ? ulong.MaxValue : (1ul << size) - 1;
        var startMaskP3 = (1ul << (size - 10)) - 1;
        var maskN1 = (1ul << (size - 1)) - 1;

        ref var rowsRef = ref MemoryMarshal.GetReference(rows);
        ref var nrowsRef = ref MemoryMarshal.GetReference(nrows);
        ref var eqRef = ref MemoryMarshal.GetReference(eqArr);
        ref var v5Ref = ref MemoryMarshal.GetReference(v5Arr);

        var score1 = 0;
        var score2 = 0;
        var score3 = 0;
        var blackModules = 0;

        var rowMaskV = Vector128.Create(rowMask);
        var startMaskV = Vector128.Create(startMaskP3);
        var maskN1V = Vector128.Create(maskN1);

        var accOnes = Vector128<ushort>.Zero;  // rule-1 weight-1 counts (y5 positions)
        var accTwos = Vector128<ushort>.Zero;  // rule-1 weight-2 counts (run starts)
        var accP2 = Vector128<ushort>.Zero;    // rule-2 blocks (x3 at reduce)
        var accP3 = Vector128<ushort>.Zero;    // rule-3 windows (x40 at reduce)
        var accBlack = Vector128<ushort>.Zero;

        // nrows = ~rows & rowMask
        var y0 = 0;
        for (; y0 + 2 <= size; y0 += 2)
        {
            var x = Vector128.LoadUnsafe(ref rowsRef, (nuint)y0);
            Vector128.AndNot(rowMaskV, x).StoreUnsafe(ref nrowsRef, (nuint)y0);
        }
        for (; y0 < size; y0++)
        {
            nrows[y0] = ~rows[y0] & rowMask;
        }

        // Row-direction rules 1 and 3 + balance popcount, 2 rows per iteration.
        var y = 0;
        for (; y + 2 <= size; y += 2)
        {
            var x = Vector128.LoadUnsafe(ref rowsRef, (nuint)y);
            var nx = Vector128.LoadUnsafe(ref nrowsRef, (nuint)y);

            accBlack += Pop16(x);

            var y2 = x & Vector128.ShiftRightLogical(x, 1);
            var y4 = y2 & Vector128.ShiftRightLogical(y2, 2);
            var y5 = y4 & Vector128.ShiftRightLogical(x, 4);
            var st = Vector128.AndNot(y5, Vector128.ShiftLeft(y5, 1));
            var n2 = nx & Vector128.ShiftRightLogical(nx, 1);
            var n4 = n2 & Vector128.ShiftRightLogical(n2, 2);
            var n5 = n4 & Vector128.ShiftRightLogical(nx, 4);
            var nst = Vector128.AndNot(n5, Vector128.ShiftLeft(n5, 1));
            accOnes += Pop16(y5) + Pop16(n5);
            accTwos += Pop16(st) + Pop16(nst);

            // Rule 3 from the light run and the core (see CalculateScorePacked): the two windows never share a start, one popcount.
            var core = x & Vector128.ShiftRightLogical(nx, 1) & Vector128.ShiftRightLogical(y2, 2) & Vector128.ShiftRightLogical(x, 4)
                     & Vector128.ShiftRightLogical(nx, 5) & Vector128.ShiftRightLogical(x, 6);
            accP3 += Pop16(((n4 & Vector128.ShiftRightLogical(core, 4)) | (core & Vector128.ShiftRightLogical(n4, 7))) & startMaskV);
        }
        for (; y < size; y++)
        {
            var x = rows[y];
            var nx = nrows[y];

            blackModules += PopCount(x);
            score1 += ScoreRuns64(x) + ScoreRuns64(nx);
            score3 += MatchFinderRow64(x, nx, startMaskP3);
        }

        // eqArr[i] = ~(rows[i] ^ rows[i+1]) & rowMask, i in 0..size-2
        // (vertical run continuation between adjacent rows; reused by rules 1 and 2).
        var i = 0;
        for (; i + 2 <= size - 1; i += 2)
        {
            var a = Vector128.LoadUnsafe(ref rowsRef, (nuint)i);
            var b = Vector128.LoadUnsafe(ref rowsRef, (nuint)(i + 1));
            (~(a ^ b) & rowMaskV).StoreUnsafe(ref eqRef, (nuint)i);
        }
        for (; i < size - 1; i++)
        {
            eqArr[i] = ~(rows[i] ^ rows[i + 1]) & rowMask;
        }

        // Rule 2 (2x2 blocks): m = eqh & eq & (eq >> 1) & maskN1 per row pair.
        // eqArr is masked with rowMask ⊇ maskN1, so reusing it is exact.
        y = 0;
        for (; y + 2 <= size - 1; y += 2)
        {
            var x = Vector128.LoadUnsafe(ref rowsRef, (nuint)y);
            var eqv = Vector128.LoadUnsafe(ref eqRef, (nuint)y);
            var eqh = ~(x ^ Vector128.ShiftRightLogical(x, 1));
            var m = eqh & eqv & Vector128.ShiftRightLogical(eqv, 1) & maskN1V;
            accP2 += Pop16(m);
        }
        for (; y < size - 1; y++)
        {
            var x = rows[y];
            var eqv = eqArr[y];
            var eqh = ~(x ^ (x >> 1));
            var m = eqh & eqv & (eqv >> 1) & maskN1;
            score2 += 3 * PopCount(m);
        }

        // Column rule 1: v5Arr[y] = AND of eqArr[y-4..y-1] for y in 4..size-1
        // (vertical 5-run marker); v5Arr[3] = 0 backs the prev-load at y = 4.
        v5Arr[3] = 0;
        y = 4;
        for (; y + 2 <= size; y += 2)
        {
            var e1 = Vector128.LoadUnsafe(ref eqRef, (nuint)(y - 4));
            var e2 = Vector128.LoadUnsafe(ref eqRef, (nuint)(y - 3));
            var e3 = Vector128.LoadUnsafe(ref eqRef, (nuint)(y - 2));
            var e4 = Vector128.LoadUnsafe(ref eqRef, (nuint)(y - 1));
            (e1 & e2 & e3 & e4).StoreUnsafe(ref v5Ref, (nuint)y);
        }
        for (; y < size; y++)
        {
            v5Arr[y] = eqArr[y - 4] & eqArr[y - 3] & eqArr[y - 2] & eqArr[y - 1];
        }

        y = 4;
        for (; y + 2 <= size; y += 2)
        {
            var v5 = Vector128.LoadUnsafe(ref v5Ref, (nuint)y);
            var prev = Vector128.LoadUnsafe(ref v5Ref, (nuint)(y - 1));
            accOnes += Pop16(v5);
            accTwos += Pop16(Vector128.AndNot(v5, prev));
        }
        for (; y < size; y++)
        {
            var v5 = v5Arr[y];
            score1 += PopCount(v5) + 2 * PopCount(v5 & ~v5Arr[y - 1]);
        }

        // Column rule 3 from the same terms, one per row: v5Arr[y] becomes the core from row y down and eqArr[y] the light run
        // from row y down (both are done with here), so each is built once and read by both windows that use it.
        var t = 0;
        for (; t + 2 <= size - 6; t += 2)
        {
            (Vector128.LoadUnsafe(ref rowsRef, (nuint)t) & Vector128.LoadUnsafe(ref nrowsRef, (nuint)(t + 1))
                & Vector128.LoadUnsafe(ref rowsRef, (nuint)(t + 2)) & Vector128.LoadUnsafe(ref rowsRef, (nuint)(t + 3))
                & Vector128.LoadUnsafe(ref rowsRef, (nuint)(t + 4)) & Vector128.LoadUnsafe(ref nrowsRef, (nuint)(t + 5))
                & Vector128.LoadUnsafe(ref rowsRef, (nuint)(t + 6))).StoreUnsafe(ref v5Ref, (nuint)t);
        }
        for (; t <= size - 7; t++)
        {
            v5Arr[t] = rows[t] & nrows[t + 1] & rows[t + 2] & rows[t + 3] & rows[t + 4] & nrows[t + 5] & rows[t + 6];
        }
        t = 0;
        for (; t + 2 <= size - 3; t += 2)
        {
            (Vector128.LoadUnsafe(ref nrowsRef, (nuint)t) & Vector128.LoadUnsafe(ref nrowsRef, (nuint)(t + 1))
                & Vector128.LoadUnsafe(ref nrowsRef, (nuint)(t + 2)) & Vector128.LoadUnsafe(ref nrowsRef, (nuint)(t + 3))).StoreUnsafe(ref eqRef, (nuint)t);
        }
        for (; t <= size - 4; t++)
        {
            eqArr[t] = nrows[t] & nrows[t + 1] & nrows[t + 2] & nrows[t + 3];
        }

        var b0 = 0;
        for (; b0 + 2 <= size - 10; b0 += 2)
        {
            var mf = Vector128.LoadUnsafe(ref eqRef, (nuint)b0) & Vector128.LoadUnsafe(ref v5Ref, (nuint)(b0 + 4));
            var mb = Vector128.LoadUnsafe(ref v5Ref, (nuint)b0) & Vector128.LoadUnsafe(ref eqRef, (nuint)(b0 + 7));
            accP3 += Pop16(mf | mb); // the two finder-like orientations are disjoint
        }
        for (; b0 <= size - 11; b0++)
        {
            score3 += 40 * PopCount((eqArr[b0] & v5Arr[b0 + 4]) | (v5Arr[b0] & eqArr[b0 + 7]));
        }

        score1 += SumAcc(accOnes) + 2 * SumAcc(accTwos);
        score2 += 3 * SumAcc(accP2);
        score3 += 40 * SumAcc(accP3);
        blackModules += SumAcc(accBlack);

        return score1 + score2 + score3 + CalculateBalanceScore(blackModules, size);
    }

    // ---------------------------------
    // Transposed tier (versions 12-40)
    // ---------------------------------

    /// <summary>Mask selection for versions 12-40 on ARM64: scores all eight candidates transposed, applies the winner to <paramref name="buffer"/>, returns it.</summary>
    internal static int MaskCodeTransposedAdvSimd(Span<byte> buffer, int size, int version, ReadOnlySpan<byte> blockedMask, QREccLevel eccLevel)
    {
        // The per-version tables come from the version's canonical blocked mask; every production caller passes that mask.
        var layout = GetTransposedLayout(version, size);
        System.Diagnostics.Debug.Assert(blockedMask.SequenceEqual(GetLayout(version).BlockedMask), "MaskCodeTransposedAdvSimd requires the version's canonical blocked mask");
        if (buffer.Length < size * size)
            throw new ArgumentException($"buffer too small: required {size * size}, got {buffer.Length}", nameof(buffer));
        if ((uint)eccLevel > 3)
            throw new ArgumentOutOfRangeException(nameof(eccLevel), eccLevel, "QREccLevel was out of range");

        var plane = layout.Words * layout.Stride;
        var rent = System.Buffers.ArrayPool<ulong>.Shared.Rent(4 * plane + WorkLength(layout));
        try
        {
            var all = rent.AsSpan();
            var r = all.Slice(0, plane);
            var c = all.Slice(plane, plane);
            var rp = all.Slice(2 * plane, plane);
            var cp = all.Slice(3 * plane, plane);
            var work = all.Slice(4 * plane, WorkLength(layout));

            PackTransposedAdvSimd(buffer, layout, version, r, c);
            var bestPattern = 0;
            var bestScore = int.MaxValue;
            for (var pattern = 0; pattern < 8; pattern++)
            {
                MaskCandidateTransposedVector128(r, c, layout, pattern, eccLevel, rp, cp);
                var score = ScoreTransposedVector128(rp, cp, layout, work, bestScore);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestPattern = pattern;
                }
            }

            ApplyWinnerTransposedAdvSimd(buffer, layout, bestPattern);
            return bestPattern;
        }
        finally
        {
            System.Buffers.ArrayPool<ulong>.Shared.Return(rent);
        }
    }

    /// <summary>
    /// Every candidate's score of the unmasked <paramref name="buffer"/> with one abort bound for all, the buffer untouched: the scores
    /// <see cref="MaskCodeTransposedAdvSimd"/> compares, for the parity tests.
    /// </summary>
    internal static void ScoreCandidatesTransposedAdvSimd(ReadOnlySpan<byte> buffer, int version, QREccLevel eccLevel, int abortAbove, Span<int> scores)
    {
        var size = QRCodeData.SizeFromVersion(version);
        var layout = GetTransposedLayout(version, size);
        var plane = layout.Words * layout.Stride;
        var rent = System.Buffers.ArrayPool<ulong>.Shared.Rent(4 * plane + WorkLength(layout));
        try
        {
            var all = rent.AsSpan();
            var r = all.Slice(0, plane);
            var c = all.Slice(plane, plane);
            var rp = all.Slice(2 * plane, plane);
            var cp = all.Slice(3 * plane, plane);
            var work = all.Slice(4 * plane, WorkLength(layout));

            PackTransposedAdvSimd(buffer, layout, version, r, c);
            for (var pattern = 0; pattern < 8; pattern++)
            {
                MaskCandidateTransposedVector128(r, c, layout, pattern, eccLevel, rp, cp);
                scores[pattern] = ScoreTransposedVector128(rp, cp, layout, work, abortAbove);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<ulong>.Shared.Return(rent);
        }
    }

    /// <summary>
    /// Packs the unmasked symbol into the row planes sixteen modules a step (padding rows zero), adds the version information, and
    /// transposes it into the column planes with the 128-bit tier's 64x64 block transpose.
    /// </summary>
    private static void PackTransposedAdvSimd(ReadOnlySpan<byte> buffer, TransposedLayout layout, int version, Span<ulong> r, Span<ulong> c)
    {
        var size = layout.Size;
        var stride = layout.Stride;
        var words = layout.Words;
        for (var y = 0; y < size; y++)
        {
            var row = PackRowBits192AdvSimd(buffer.Slice(y * size, size));
            r[y] = row.W0;
            r[stride + y] = row.W1;
            if (words == 3) r[2 * stride + y] = row.W2;
        }
        FinishRowPlanes(r, layout, version);

        for (var kr = 0; kr < words; kr++)
        {
            for (var kc = 0; kc < words; kc++)
            {
                var block = c.Slice(kr * stride + 64 * kc, 64);
                r.Slice(kc * stride + 64 * kr, 64).CopyTo(block);
                Transpose64Vector128(ref MemoryMarshal.GetReference(block));
            }
            c.Slice(kr * stride + 64 * words, stride - 64 * words).Clear();
        }
    }

    /// <summary>Applies the winning pattern's packed XOR delta to the byte buffer, sixteen modules a step.</summary>
    private static void ApplyWinnerTransposedAdvSimd(Span<byte> buffer, TransposedLayout layout, int bestPattern)
    {
        var size = layout.Size;
        ref var bufRef = ref MemoryMarshal.GetReference(buffer);
        for (int y = 0, tplRow = 0; y < size; y++)
        {
            XorUnpackRow192AdvSimd(ref Unsafe.Add(ref bufRef, y * size), size, WinnerDelta(layout, bestPattern, y, tplRow));
            if (++tplRow == 12) tplRow = 0;
        }
    }

    /// <summary><see cref="ApplyMaskPattern"/> unpacking 16 modules a step, for both row widths.</summary>
    internal static void ApplyMaskPatternAdvSimd(Span<byte> buffer, int size, ReadOnlySpan<byte> blockedMask, int patternIndex)
    {
        // As in ApplyMaskPatternScalar: the unblocked rows are bit slices of a padded copy of the blocked bitmask.
        Span<byte> padded = stackalloc byte[blockedMask.Length + 32];
        blockedMask.CopyTo(padded);
        padded.Slice(blockedMask.Length).Clear();

        var tplBase = patternIndex * 12;
        if (size <= 64)
        {
            var rowMask = size == 64 ? ulong.MaxValue : (1ul << size) - 1;
            for (int y = 0, tplRow = 0; y < size; y++)
            {
                XorUnpackRow64AdvSimd(buffer.Slice(y * size, size), _maskTemplates64[tplBase + tplRow] & AllowedRow64(padded, y * size, rowMask));
                if (++tplRow == 12) tplRow = 0;
            }
            return;
        }

        var rowMask192 = Row192.MaskLow(size);
        ref var bufRef = ref MemoryMarshal.GetReference(buffer);
        for (int y = 0, tplRow = 0; y < size; y++)
        {
            XorUnpackRow192AdvSimd(ref Unsafe.Add(ref bufRef, y * size), size, _maskTemplates[tplBase + tplRow] & Row192.FromBitSlice(padded, y * size).AndNot(rowMask192));
            if (++tplRow == 12) tplRow = 0;
        }
    }
}
#endif
