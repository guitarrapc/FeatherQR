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
/// Port of the AVX2 architecture in ModulePlacer.Masking.Simd.cs to 128-bit vectors: the scorer runs lane-per-row (Vector128&lt;ulong&gt; = 2 rows per iteration) and the same three width tiers apply (1 ulong per row for versions 1-11, 2-word SoA for 12-29, 3-word SoA for 30-40).
/// NEON-specific choices:
/// - Popcount is native (cnt.16b); one uaddlp widens the per-byte counts to
///   ushort lanes, which accumulate directly in Vector128&lt;ushort&gt;
///   accumulators (2 instructions per popcount vs 3 for a full per-qword
///   widen). Worst-case lane sums stay far below ushort range (&lt; 17k for
///   version 40), and totals reduce once per score via uaddlv.
/// - Byte&lt;-&gt;bit edges run 16 modules per step: packing gathers per-byte bit
///   weights (cmeq+bic) and reduces with a uaddlp chain; unpacking broadcasts
///   the 16-bit delta chunk and replicates bytes with tbl + cmtst (the same
///   sequence as MicroQRModulePlacer.Unpack16).
///
/// This file only executes under AdvSimd.Arm64.IsSupported, so memory order is always little-endian and the SWAR tail reads skip endianness normalization.
///
/// Measured on Apple M2 vs the scalar bit-packed paths (MaskCodeArm findings log): v1 2.4x, v10 3.0x, v20 1.20x, v40 1.14x, zero allocations.
/// The ushort accumulate beat the per-qword AVX2-shaped accumulate by ~8% and the SIMD edges beat the SWAR edges by ~5-11%; the scalar scorer's early-exit is intentionally absent (structurally incompatible with vector accumulators, and the vector throughput win dwarfs it, same conclusion as the x64 loop).
/// </summary>
internal static partial class ModulePlacer
{
    /// <summary>Entry point for the NEON tiers. Caller guarantees AdvSimd.Arm64.IsSupported.</summary>
    internal static int MaskCodeAdvSimd(Span<byte> buffer, int size, int version, ReadOnlySpan<byte> blockedMask, QREccLevel eccLevel)
    {
        if (size <= 64)
        {
            return MaskCode64AdvSimd(buffer, size, version, blockedMask, eccLevel);
        }
        return size <= 128
            ? MaskCode128AdvSimd(buffer, size, version, blockedMask, eccLevel)
            : MaskCode192AdvSimd(buffer, size, version, blockedMask, eccLevel);
    }

    // ---------------------------------
    // Shared NEON pieces
    // ---------------------------------
    // Operand-order note: Vector128.AndNot(left, right) computes left & ~right
    // (same helper convention as the Vector256 helper used in the AVX2 file);
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

        // Each allowed row (~blocked) is a contiguous bit slice of the blocked
        // bitmask; a padded copy makes the two 8-byte slice reads always legal.
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

            var mf = nx & Vector128.ShiftRightLogical(nx, 1) & Vector128.ShiftRightLogical(nx, 2) & Vector128.ShiftRightLogical(nx, 3)
                   & Vector128.ShiftRightLogical(x, 4) & Vector128.ShiftRightLogical(nx, 5) & Vector128.ShiftRightLogical(x, 6) & Vector128.ShiftRightLogical(x, 7)
                   & Vector128.ShiftRightLogical(x, 8) & Vector128.ShiftRightLogical(nx, 9) & Vector128.ShiftRightLogical(x, 10) & startMaskV;
            var mb = x & Vector128.ShiftRightLogical(nx, 1) & Vector128.ShiftRightLogical(x, 2) & Vector128.ShiftRightLogical(x, 3)
                   & Vector128.ShiftRightLogical(x, 4) & Vector128.ShiftRightLogical(nx, 5) & Vector128.ShiftRightLogical(x, 6) & Vector128.ShiftRightLogical(nx, 7)
                   & Vector128.ShiftRightLogical(nx, 8) & Vector128.ShiftRightLogical(nx, 9) & Vector128.ShiftRightLogical(nx, 10) & startMaskV;
            accP3 += Pop16(mf) + Pop16(mb);
        }
        for (; y < size; y++)
        {
            var x = rows[y];
            var nx = nrows[y];

            blackModules += PopCount(x);
            score1 += ScoreRuns64(x) + ScoreRuns64(nx);

            var mf = nx & (nx >> 1) & (nx >> 2) & (nx >> 3)
                   & (x >> 4) & (nx >> 5) & (x >> 6) & (x >> 7)
                   & (x >> 8) & (nx >> 9) & (x >> 10) & startMaskP3;
            var mb = x & (nx >> 1) & (x >> 2) & (x >> 3)
                   & (x >> 4) & (nx >> 5) & (x >> 6) & (nx >> 7)
                   & (nx >> 8) & (nx >> 9) & (nx >> 10) & startMaskP3;
            score3 += 40 * (PopCount(mf) + PopCount(mb));
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

        // Column rule 3: 11-row windows, start rows b in 0..size-11.
        var b0 = 0;
        for (; b0 + 2 <= size - 10; b0 += 2)
        {
            var r0 = Vector128.LoadUnsafe(ref rowsRef, (nuint)b0);
            var r2 = Vector128.LoadUnsafe(ref rowsRef, (nuint)(b0 + 2));
            var r3 = Vector128.LoadUnsafe(ref rowsRef, (nuint)(b0 + 3));
            var r4 = Vector128.LoadUnsafe(ref rowsRef, (nuint)(b0 + 4));
            var r6 = Vector128.LoadUnsafe(ref rowsRef, (nuint)(b0 + 6));
            var r7 = Vector128.LoadUnsafe(ref rowsRef, (nuint)(b0 + 7));
            var r8 = Vector128.LoadUnsafe(ref rowsRef, (nuint)(b0 + 8));
            var r10 = Vector128.LoadUnsafe(ref rowsRef, (nuint)(b0 + 10));
            var n0 = Vector128.LoadUnsafe(ref nrowsRef, (nuint)b0);
            var n1 = Vector128.LoadUnsafe(ref nrowsRef, (nuint)(b0 + 1));
            var n2c = Vector128.LoadUnsafe(ref nrowsRef, (nuint)(b0 + 2));
            var n3 = Vector128.LoadUnsafe(ref nrowsRef, (nuint)(b0 + 3));
            var n5c = Vector128.LoadUnsafe(ref nrowsRef, (nuint)(b0 + 5));
            var n7 = Vector128.LoadUnsafe(ref nrowsRef, (nuint)(b0 + 7));
            var n8 = Vector128.LoadUnsafe(ref nrowsRef, (nuint)(b0 + 8));
            var n9 = Vector128.LoadUnsafe(ref nrowsRef, (nuint)(b0 + 9));
            var n10 = Vector128.LoadUnsafe(ref nrowsRef, (nuint)(b0 + 10));

            var mf = n0 & n1 & n2c & n3 & r4 & n5c & r6 & r7 & r8 & n9 & r10;
            var mb = r0 & n1 & r2 & r3 & r4 & n5c & r6 & n7 & n8 & n9 & n10;
            accP3 += Pop16(mf) + Pop16(mb);
        }
        for (; b0 <= size - 11; b0++)
        {
            var mf = nrows[b0] & nrows[b0 + 1] & nrows[b0 + 2] & nrows[b0 + 3]
                   & rows[b0 + 4] & nrows[b0 + 5] & rows[b0 + 6] & rows[b0 + 7]
                   & rows[b0 + 8] & nrows[b0 + 9] & rows[b0 + 10];
            var mb = rows[b0] & nrows[b0 + 1] & rows[b0 + 2] & rows[b0 + 3]
                   & rows[b0 + 4] & nrows[b0 + 5] & rows[b0 + 6] & nrows[b0 + 7]
                   & nrows[b0 + 8] & nrows[b0 + 9] & nrows[b0 + 10];
            score3 += 40 * (PopCount(mf) + PopCount(mb));
        }

        score1 += SumAcc(accOnes) + 2 * SumAcc(accTwos);
        score2 += 3 * SumAcc(accP2);
        score3 += 40 * SumAcc(accP3);
        blackModules += SumAcc(accBlack);

        return score1 + score2 + score3 + CalculateBalanceScore(blackModules, size);
    }

    // ---------------------------------
    // Two-word SoA tier (versions 12-29, size 65..128)
    // ---------------------------------

    internal static int MaskCode128AdvSimd(Span<byte> buffer, int size, int version, ReadOnlySpan<byte> blockedMask, QREccLevel eccLevel)
    {
        var packedRent = System.Buffers.ArrayPool<Row192>.Shared.Rent(2 * size);
        var wordsRent = System.Buffers.ArrayPool<ulong>.Shared.Rent(8 * size);
        try
        {
            var packed = packedRent.AsSpan(0, size);
            var allowed = packedRent.AsSpan(size, size);

            PackAllAdvSimd(buffer, blockedMask, size, version, packed, allowed);

            // SoA partitions: masked words, ~masked words, eq words, v5 words.
            var mw0 = wordsRent.AsSpan(0, size);
            var mw1 = wordsRent.AsSpan(size, size);
            var nw0 = wordsRent.AsSpan(2 * size, size);
            var nw1 = wordsRent.AsSpan(3 * size, size);
            var eq0 = wordsRent.AsSpan(4 * size, size);
            var eq1 = wordsRent.AsSpan(5 * size, size);
            var v50 = wordsRent.AsSpan(6 * size, size);
            var v51 = wordsRent.AsSpan(7 * size, size);

            var templates = _maskTemplates;
            var bestPatternIndex = 0;
            var bestScore = int.MaxValue;
            for (var patternIndex = 0; patternIndex < 8; patternIndex++)
            {
                var tplBase = patternIndex * 12;
                for (int y = 0, tplRow = 0; y < size; y++)
                {
                    var t = templates[tplBase + tplRow];
                    var a = allowed[y];
                    var p = packed[y];
                    mw0[y] = p.W0 ^ (t.W0 & a.W0);
                    mw1[y] = p.W1 ^ (t.W1 & a.W1);
                    if (++tplRow == 12) tplRow = 0;
                }
                PokeFormatBitsSoA2(mw0, mw1, size, QRCodeConstants.GetFormatBits(eccLevel, patternIndex));

                var score = CalculateScore128AdvSimd(mw0, mw1, nw0, nw1, eq0, eq1, v50, v51, size);
                if (score < bestScore)
                {
                    bestPatternIndex = patternIndex;
                    bestScore = score;
                }
            }

            ApplyWinnerAdvSimd(buffer, size, bestPatternIndex, allowed);
            return bestPatternIndex;
        }
        finally
        {
            System.Buffers.ArrayPool<Row192>.Shared.Return(packedRent);
            System.Buffers.ArrayPool<ulong>.Shared.Return(wordsRent);
        }
    }

    /// <summary>2 rows x 128 bits in SoA form: word k of rows y..y+1 in one vector.</summary>
    private readonly struct RN128
    {
        public readonly Vector128<ulong> A, B;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RN128(Vector128<ulong> a, Vector128<ulong> b) { A = a; B = b; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RN128 Load(ref ulong w0, ref ulong w1, int y)
            => new(Vector128.LoadUnsafe(ref w0, (nuint)y), Vector128.LoadUnsafe(ref w1, (nuint)y));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Store(ref ulong w0, ref ulong w1, int y)
        {
            A.StoreUnsafe(ref w0, (nuint)y);
            B.StoreUnsafe(ref w1, (nuint)y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RN128 operator &(in RN128 x, in RN128 y) => new(x.A & y.A, x.B & y.B);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RN128 operator ^(in RN128 x, in RN128 y) => new(x.A ^ y.A, x.B ^ y.B);

        /// <summary>~(this ^ other).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RN128 Xnor(in RN128 o) => new(~(A ^ o.A), ~(B ^ o.B));

        /// <summary>this &amp; ~other (Vector128.AndNot(left, right) == left &amp; ~right; see the operand-order note above).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RN128 AndNotWith(in RN128 o) => new(Vector128.AndNot(A, o.A), Vector128.AndNot(B, o.B));

        /// <summary>Logical shift right by k bits (1..63), pulling neighbor-word bits in at the top of each lane.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RN128 ShiftRight(int k)
            => new(Vector128.ShiftRightLogical(A, k) | Vector128.ShiftLeft(B, 64 - k),
                   Vector128.ShiftRightLogical(B, k));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RN128 ShiftLeft1()
            => new(Vector128.ShiftLeft(A, 1),
                   Vector128.ShiftLeft(B, 1) | Vector128.ShiftRightLogical(A, 63));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector128<ushort> Pop() => Pop16(A) + Pop16(B);
    }

    /// <summary>Two-word SoA Vector128 penalty scorer (structure mirrors <see cref="CalculateScore64AdvSimd"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static int CalculateScore128AdvSimd(
        Span<ulong> rw0, Span<ulong> rw1,
        Span<ulong> nw0, Span<ulong> nw1,
        Span<ulong> eq0, Span<ulong> eq1,
        Span<ulong> v50, Span<ulong> v51, int size)
    {
        var rowMaskR = Row192.MaskLow(size);
        var startMaskR = Row192.MaskLow(size - 10);
        var maskN1R = Row192.MaskLow(size - 1);

        ref var rw0R = ref MemoryMarshal.GetReference(rw0);
        ref var rw1R = ref MemoryMarshal.GetReference(rw1);
        ref var nw0R = ref MemoryMarshal.GetReference(nw0);
        ref var nw1R = ref MemoryMarshal.GetReference(nw1);
        ref var eq0R = ref MemoryMarshal.GetReference(eq0);
        ref var eq1R = ref MemoryMarshal.GetReference(eq1);
        ref var v50R = ref MemoryMarshal.GetReference(v50);
        ref var v51R = ref MemoryMarshal.GetReference(v51);

        var score1 = 0;
        var score2 = 0;
        var score3 = 0;
        var blackModules = 0;

        var rowMaskV = new RN128(Vector128.Create(rowMaskR.W0), Vector128.Create(rowMaskR.W1));
        var startMaskV = new RN128(Vector128.Create(startMaskR.W0), Vector128.Create(startMaskR.W1));
        var maskN1V = new RN128(Vector128.Create(maskN1R.W0), Vector128.Create(maskN1R.W1));

        var accOnes = Vector128<ushort>.Zero;
        var accTwos = Vector128<ushort>.Zero;
        var accP2 = Vector128<ushort>.Zero;
        var accP3 = Vector128<ushort>.Zero;
        var accBlack = Vector128<ushort>.Zero;

        var y0 = 0;
        for (; y0 + 2 <= size; y0 += 2)
        {
            var x = RN128.Load(ref rw0R, ref rw1R, y0);
            new RN128(Vector128.AndNot(rowMaskV.A, x.A), Vector128.AndNot(rowMaskV.B, x.B)).Store(ref nw0R, ref nw1R, y0);
        }
        for (; y0 < size; y0++)
        {
            nw0[y0] = ~rw0[y0] & rowMaskR.W0;
            nw1[y0] = ~rw1[y0] & rowMaskR.W1;
        }

        var y = 0;
        for (; y + 2 <= size; y += 2)
        {
            var x = RN128.Load(ref rw0R, ref rw1R, y);
            var nx = RN128.Load(ref nw0R, ref nw1R, y);

            accBlack += x.Pop();

            var y2 = x & x.ShiftRight(1);
            var y4 = y2 & y2.ShiftRight(2);
            var y5 = y4 & x.ShiftRight(4);
            var st = y5.AndNotWith(y5.ShiftLeft1());
            var n2 = nx & nx.ShiftRight(1);
            var n4 = n2 & n2.ShiftRight(2);
            var n5 = n4 & nx.ShiftRight(4);
            var nst = n5.AndNotWith(n5.ShiftLeft1());
            accOnes += y5.Pop() + n5.Pop();
            accTwos += st.Pop() + nst.Pop();

            var mf = nx & nx.ShiftRight(1) & nx.ShiftRight(2) & nx.ShiftRight(3)
                   & x.ShiftRight(4) & nx.ShiftRight(5) & x.ShiftRight(6) & x.ShiftRight(7)
                   & x.ShiftRight(8) & nx.ShiftRight(9) & x.ShiftRight(10) & startMaskV;
            var mb = x & nx.ShiftRight(1) & x.ShiftRight(2) & x.ShiftRight(3)
                   & x.ShiftRight(4) & nx.ShiftRight(5) & x.ShiftRight(6) & nx.ShiftRight(7)
                   & nx.ShiftRight(8) & nx.ShiftRight(9) & nx.ShiftRight(10) & startMaskV;
            accP3 += mf.Pop() + mb.Pop();
        }
        for (; y < size; y++)
        {
            var x = new Row192(rw0[y], rw1[y], 0);
            var nx = new Row192(nw0[y], nw1[y], 0);
            blackModules += x.PopCount();
            score1 += ScoreRuns(x) + ScoreRuns(nx);
            score3 += MatchFinderRowSimd(x, nx, startMaskR);
        }

        var i = 0;
        for (; i + 2 <= size - 1; i += 2)
        {
            var a = RN128.Load(ref rw0R, ref rw1R, i);
            var b = RN128.Load(ref rw0R, ref rw1R, i + 1);
            (a.Xnor(b) & rowMaskV).Store(ref eq0R, ref eq1R, i);
        }
        for (; i < size - 1; i++)
        {
            eq0[i] = ~(rw0[i] ^ rw0[i + 1]) & rowMaskR.W0;
            eq1[i] = ~(rw1[i] ^ rw1[i + 1]) & rowMaskR.W1;
        }

        y = 0;
        for (; y + 2 <= size - 1; y += 2)
        {
            var x = RN128.Load(ref rw0R, ref rw1R, y);
            var eqv = RN128.Load(ref eq0R, ref eq1R, y);
            var eqh = x.Xnor(x.ShiftRight(1));
            var m = eqh & eqv & eqv.ShiftRight(1) & maskN1V;
            accP2 += m.Pop();
        }
        for (; y < size - 1; y++)
        {
            var x = new Row192(rw0[y], rw1[y], 0);
            var eqv = new Row192(eq0[y], eq1[y], 0);
            var eqh = x.Xnor(x.ShiftRight(1));
            var m = eqh & eqv & eqv.ShiftRight(1) & maskN1R;
            score2 += 3 * m.PopCount();
        }

        v50[3] = 0;
        v51[3] = 0;
        y = 4;
        for (; y + 2 <= size; y += 2)
        {
            var e1 = RN128.Load(ref eq0R, ref eq1R, y - 4);
            var e2 = RN128.Load(ref eq0R, ref eq1R, y - 3);
            var e3 = RN128.Load(ref eq0R, ref eq1R, y - 2);
            var e4 = RN128.Load(ref eq0R, ref eq1R, y - 1);
            (e1 & e2 & e3 & e4).Store(ref v50R, ref v51R, y);
        }
        for (; y < size; y++)
        {
            v50[y] = eq0[y - 4] & eq0[y - 3] & eq0[y - 2] & eq0[y - 1];
            v51[y] = eq1[y - 4] & eq1[y - 3] & eq1[y - 2] & eq1[y - 1];
        }

        y = 4;
        for (; y + 2 <= size; y += 2)
        {
            var v5 = RN128.Load(ref v50R, ref v51R, y);
            var prev = RN128.Load(ref v50R, ref v51R, y - 1);
            accOnes += v5.Pop();
            accTwos += v5.AndNotWith(prev).Pop();
        }
        for (; y < size; y++)
        {
            var v5 = new Row192(v50[y], v51[y], 0);
            var prev = new Row192(v50[y - 1], v51[y - 1], 0);
            score1 += v5.PopCount() + 2 * v5.AndNotWith(prev).PopCount();
        }

        var b0 = 0;
        for (; b0 + 2 <= size - 10; b0 += 2)
        {
            var r0 = RN128.Load(ref rw0R, ref rw1R, b0);
            var r2 = RN128.Load(ref rw0R, ref rw1R, b0 + 2);
            var r3 = RN128.Load(ref rw0R, ref rw1R, b0 + 3);
            var r4 = RN128.Load(ref rw0R, ref rw1R, b0 + 4);
            var r6 = RN128.Load(ref rw0R, ref rw1R, b0 + 6);
            var r7 = RN128.Load(ref rw0R, ref rw1R, b0 + 7);
            var r8 = RN128.Load(ref rw0R, ref rw1R, b0 + 8);
            var r10 = RN128.Load(ref rw0R, ref rw1R, b0 + 10);
            var n0 = RN128.Load(ref nw0R, ref nw1R, b0);
            var n1 = RN128.Load(ref nw0R, ref nw1R, b0 + 1);
            var n2c = RN128.Load(ref nw0R, ref nw1R, b0 + 2);
            var n3 = RN128.Load(ref nw0R, ref nw1R, b0 + 3);
            var n5c = RN128.Load(ref nw0R, ref nw1R, b0 + 5);
            var n7 = RN128.Load(ref nw0R, ref nw1R, b0 + 7);
            var n8 = RN128.Load(ref nw0R, ref nw1R, b0 + 8);
            var n9 = RN128.Load(ref nw0R, ref nw1R, b0 + 9);
            var n10 = RN128.Load(ref nw0R, ref nw1R, b0 + 10);

            var mf = n0 & n1 & n2c & n3 & r4 & n5c & r6 & r7 & r8 & n9 & r10;
            var mb = r0 & n1 & r2 & r3 & r4 & n5c & r6 & n7 & n8 & n9 & n10;
            accP3 += mf.Pop() + mb.Pop();
        }
        for (; b0 <= size - 11; b0++)
        {
            var mfw0 = nw0[b0] & nw0[b0 + 1] & nw0[b0 + 2] & nw0[b0 + 3]
                     & rw0[b0 + 4] & nw0[b0 + 5] & rw0[b0 + 6] & rw0[b0 + 7]
                     & rw0[b0 + 8] & nw0[b0 + 9] & rw0[b0 + 10];
            var mfw1 = nw1[b0] & nw1[b0 + 1] & nw1[b0 + 2] & nw1[b0 + 3]
                     & rw1[b0 + 4] & nw1[b0 + 5] & rw1[b0 + 6] & rw1[b0 + 7]
                     & rw1[b0 + 8] & nw1[b0 + 9] & rw1[b0 + 10];
            var mbw0 = rw0[b0] & nw0[b0 + 1] & rw0[b0 + 2] & rw0[b0 + 3]
                     & rw0[b0 + 4] & nw0[b0 + 5] & rw0[b0 + 6] & nw0[b0 + 7]
                     & nw0[b0 + 8] & nw0[b0 + 9] & nw0[b0 + 10];
            var mbw1 = rw1[b0] & nw1[b0 + 1] & rw1[b0 + 2] & rw1[b0 + 3]
                     & rw1[b0 + 4] & nw1[b0 + 5] & rw1[b0 + 6] & nw1[b0 + 7]
                     & nw1[b0 + 8] & nw1[b0 + 9] & nw1[b0 + 10];
            score3 += 40 * (PopCount(mfw0) + PopCount(mfw1) + PopCount(mbw0) + PopCount(mbw1));
        }

        score1 += SumAcc(accOnes) + 2 * SumAcc(accTwos);
        score2 += 3 * SumAcc(accP2);
        score3 += 40 * SumAcc(accP3);
        blackModules += SumAcc(accBlack);

        return score1 + score2 + score3 + CalculateBalanceScore(blackModules, size);
    }

    // ---------------------------------
    // Three-word SoA tier (versions 30-40, size > 128)
    // ---------------------------------

    internal static int MaskCode192AdvSimd(Span<byte> buffer, int size, int version, ReadOnlySpan<byte> blockedMask, QREccLevel eccLevel)
    {
        var packedRent = System.Buffers.ArrayPool<Row192>.Shared.Rent(2 * size);
        var wordsRent = System.Buffers.ArrayPool<ulong>.Shared.Rent(12 * size);
        try
        {
            var packed = packedRent.AsSpan(0, size);
            var allowed = packedRent.AsSpan(size, size);

            PackAllAdvSimd(buffer, blockedMask, size, version, packed, allowed);

            var mw0 = wordsRent.AsSpan(0, size);
            var mw1 = wordsRent.AsSpan(size, size);
            var mw2 = wordsRent.AsSpan(2 * size, size);
            var nw0 = wordsRent.AsSpan(3 * size, size);
            var nw1 = wordsRent.AsSpan(4 * size, size);
            var nw2 = wordsRent.AsSpan(5 * size, size);
            var eq0 = wordsRent.AsSpan(6 * size, size);
            var eq1 = wordsRent.AsSpan(7 * size, size);
            var eq2 = wordsRent.AsSpan(8 * size, size);
            var v50 = wordsRent.AsSpan(9 * size, size);
            var v51 = wordsRent.AsSpan(10 * size, size);
            var v52 = wordsRent.AsSpan(11 * size, size);

            var templates = _maskTemplates;
            var bestPatternIndex = 0;
            var bestScore = int.MaxValue;
            for (var patternIndex = 0; patternIndex < 8; patternIndex++)
            {
                var tplBase = patternIndex * 12;
                for (int y = 0, tplRow = 0; y < size; y++)
                {
                    var t = templates[tplBase + tplRow];
                    var a = allowed[y];
                    var p = packed[y];
                    mw0[y] = p.W0 ^ (t.W0 & a.W0);
                    mw1[y] = p.W1 ^ (t.W1 & a.W1);
                    mw2[y] = p.W2 ^ (t.W2 & a.W2);
                    if (++tplRow == 12) tplRow = 0;
                }
                PokeFormatBitsSoA3(mw0, mw1, mw2, size, QRCodeConstants.GetFormatBits(eccLevel, patternIndex));

                var score = CalculateScore192AdvSimd(mw0, mw1, mw2, nw0, nw1, nw2, eq0, eq1, eq2, v50, v51, v52, size);
                if (score < bestScore)
                {
                    bestPatternIndex = patternIndex;
                    bestScore = score;
                }
            }

            ApplyWinnerAdvSimd(buffer, size, bestPatternIndex, allowed);
            return bestPatternIndex;
        }
        finally
        {
            System.Buffers.ArrayPool<Row192>.Shared.Return(packedRent);
            System.Buffers.ArrayPool<ulong>.Shared.Return(wordsRent);
        }
    }

    /// <summary>Packs buffer rows (NEON 16-module steps), pokes version bits once, and builds allowed rows (bit slices).</summary>
    private static void PackAllAdvSimd(ReadOnlySpan<byte> buffer, ReadOnlySpan<byte> blockedMask, int size, int version,
        Span<Row192> packed, Span<Row192> allowed)
    {
        for (var y = 0; y < size; y++)
        {
            packed[y] = PackRowBits192AdvSimd(buffer.Slice(y * size, size));
        }

        // This path only serves size > 64, i.e. version >= 12, so always poke.
        var versionBits = QRCodeConstants.GetVersionBits(version);
        for (var x = 0; x < 6; x++)
        {
            for (var y = 0; y < 3; y++)
            {
                var bit = (versionBits & (1u << (x * 3 + y))) != 0;
                packed[y + size - 11] = packed[y + size - 11].WithBit(x, bit);
                packed[x] = packed[x].WithBit(y + size - 11, bit);
            }
        }

        // Padded copy so the four 8-byte slice reads per row never overrun.
        Span<byte> padded = stackalloc byte[blockedMask.Length + 32];
        padded.Clear();
        blockedMask.CopyTo(padded);
        var rowMask = Row192.MaskLow(size);
        for (var y = 0; y < size; y++)
        {
            allowed[y] = Row192.FromBitSlice(padded, y * size).AndNot(rowMask);
        }
    }

    /// <summary>Applies the winning pattern's packed XOR delta to the byte buffer, 16 modules per step.</summary>
    private static void ApplyWinnerAdvSimd(Span<byte> buffer, int size, int bestPatternIndex, ReadOnlySpan<Row192> allowed)
    {
        var tplBase = bestPatternIndex * 12;
        ref var bufRef = ref MemoryMarshal.GetReference(buffer);
        for (int y = 0, tplRow = 0; y < size; y++)
        {
            var delta = _maskTemplates[tplBase + tplRow] & allowed[y];
            XorUnpackRow192AdvSimd(ref Unsafe.Add(ref bufRef, y * size), size, delta);
            if (++tplRow == 12) tplRow = 0;
        }
    }

    /// <summary>2 rows x 192 bits in SoA form: word k of rows y..y+1 in one vector.</summary>
    private readonly struct RN192
    {
        public readonly Vector128<ulong> A, B, C;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RN192(Vector128<ulong> a, Vector128<ulong> b, Vector128<ulong> c) { A = a; B = b; C = c; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RN192 Load(ref ulong w0, ref ulong w1, ref ulong w2, int y)
            => new(Vector128.LoadUnsafe(ref w0, (nuint)y), Vector128.LoadUnsafe(ref w1, (nuint)y), Vector128.LoadUnsafe(ref w2, (nuint)y));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Store(ref ulong w0, ref ulong w1, ref ulong w2, int y)
        {
            A.StoreUnsafe(ref w0, (nuint)y);
            B.StoreUnsafe(ref w1, (nuint)y);
            C.StoreUnsafe(ref w2, (nuint)y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RN192 operator &(in RN192 x, in RN192 y) => new(x.A & y.A, x.B & y.B, x.C & y.C);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RN192 operator ^(in RN192 x, in RN192 y) => new(x.A ^ y.A, x.B ^ y.B, x.C ^ y.C);

        /// <summary>~(this ^ other).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RN192 Xnor(in RN192 o) => new(~(A ^ o.A), ~(B ^ o.B), ~(C ^ o.C));

        /// <summary>this &amp; ~other (Vector128.AndNot(left, right) == left &amp; ~right; see the operand-order note above).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RN192 AndNotWith(in RN192 o) => new(Vector128.AndNot(A, o.A), Vector128.AndNot(B, o.B), Vector128.AndNot(C, o.C));

        /// <summary>Logical shift right by k bits (1..63), pulling neighbor-word bits in at the top of each lane.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RN192 ShiftRight(int k)
            => new(Vector128.ShiftRightLogical(A, k) | Vector128.ShiftLeft(B, 64 - k),
                   Vector128.ShiftRightLogical(B, k) | Vector128.ShiftLeft(C, 64 - k),
                   Vector128.ShiftRightLogical(C, k));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RN192 ShiftLeft1()
            => new(Vector128.ShiftLeft(A, 1),
                   Vector128.ShiftLeft(B, 1) | Vector128.ShiftRightLogical(A, 63),
                   Vector128.ShiftLeft(C, 1) | Vector128.ShiftRightLogical(B, 63));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector128<ushort> Pop() => Pop16(A) + Pop16(B) + Pop16(C);
    }

    /// <summary>Three-word SoA Vector128 penalty scorer (structure mirrors <see cref="CalculateScore64AdvSimd"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static int CalculateScore192AdvSimd(
        Span<ulong> rw0, Span<ulong> rw1, Span<ulong> rw2,
        Span<ulong> nw0, Span<ulong> nw1, Span<ulong> nw2,
        Span<ulong> eq0, Span<ulong> eq1, Span<ulong> eq2,
        Span<ulong> v50, Span<ulong> v51, Span<ulong> v52, int size)
    {
        var rowMaskR = Row192.MaskLow(size);
        var startMaskR = Row192.MaskLow(size - 10);
        var maskN1R = Row192.MaskLow(size - 1);

        ref var rw0R = ref MemoryMarshal.GetReference(rw0);
        ref var rw1R = ref MemoryMarshal.GetReference(rw1);
        ref var rw2R = ref MemoryMarshal.GetReference(rw2);
        ref var nw0R = ref MemoryMarshal.GetReference(nw0);
        ref var nw1R = ref MemoryMarshal.GetReference(nw1);
        ref var nw2R = ref MemoryMarshal.GetReference(nw2);
        ref var eq0R = ref MemoryMarshal.GetReference(eq0);
        ref var eq1R = ref MemoryMarshal.GetReference(eq1);
        ref var eq2R = ref MemoryMarshal.GetReference(eq2);
        ref var v50R = ref MemoryMarshal.GetReference(v50);
        ref var v51R = ref MemoryMarshal.GetReference(v51);
        ref var v52R = ref MemoryMarshal.GetReference(v52);

        var score1 = 0;
        var score2 = 0;
        var score3 = 0;
        var blackModules = 0;

        var rowMaskV = new RN192(Vector128.Create(rowMaskR.W0), Vector128.Create(rowMaskR.W1), Vector128.Create(rowMaskR.W2));
        var startMaskV = new RN192(Vector128.Create(startMaskR.W0), Vector128.Create(startMaskR.W1), Vector128.Create(startMaskR.W2));
        var maskN1V = new RN192(Vector128.Create(maskN1R.W0), Vector128.Create(maskN1R.W1), Vector128.Create(maskN1R.W2));

        var accOnes = Vector128<ushort>.Zero;
        var accTwos = Vector128<ushort>.Zero;
        var accP2 = Vector128<ushort>.Zero;
        var accP3 = Vector128<ushort>.Zero;
        var accBlack = Vector128<ushort>.Zero;

        var y0 = 0;
        for (; y0 + 2 <= size; y0 += 2)
        {
            var x = RN192.Load(ref rw0R, ref rw1R, ref rw2R, y0);
            var nx = new RN192(Vector128.AndNot(rowMaskV.A, x.A), Vector128.AndNot(rowMaskV.B, x.B), Vector128.AndNot(rowMaskV.C, x.C));
            nx.Store(ref nw0R, ref nw1R, ref nw2R, y0);
        }
        for (; y0 < size; y0++)
        {
            nw0[y0] = ~rw0[y0] & rowMaskR.W0;
            nw1[y0] = ~rw1[y0] & rowMaskR.W1;
            nw2[y0] = ~rw2[y0] & rowMaskR.W2;
        }

        var y = 0;
        for (; y + 2 <= size; y += 2)
        {
            var x = RN192.Load(ref rw0R, ref rw1R, ref rw2R, y);
            var nx = RN192.Load(ref nw0R, ref nw1R, ref nw2R, y);

            accBlack += x.Pop();

            var y2 = x & x.ShiftRight(1);
            var y4 = y2 & y2.ShiftRight(2);
            var y5 = y4 & x.ShiftRight(4);
            var st = y5.AndNotWith(y5.ShiftLeft1());
            var n2 = nx & nx.ShiftRight(1);
            var n4 = n2 & n2.ShiftRight(2);
            var n5 = n4 & nx.ShiftRight(4);
            var nst = n5.AndNotWith(n5.ShiftLeft1());
            accOnes += y5.Pop() + n5.Pop();
            accTwos += st.Pop() + nst.Pop();

            var mf = nx & nx.ShiftRight(1) & nx.ShiftRight(2) & nx.ShiftRight(3)
                   & x.ShiftRight(4) & nx.ShiftRight(5) & x.ShiftRight(6) & x.ShiftRight(7)
                   & x.ShiftRight(8) & nx.ShiftRight(9) & x.ShiftRight(10) & startMaskV;
            var mb = x & nx.ShiftRight(1) & x.ShiftRight(2) & x.ShiftRight(3)
                   & x.ShiftRight(4) & nx.ShiftRight(5) & x.ShiftRight(6) & nx.ShiftRight(7)
                   & nx.ShiftRight(8) & nx.ShiftRight(9) & nx.ShiftRight(10) & startMaskV;
            accP3 += mf.Pop() + mb.Pop();
        }
        for (; y < size; y++)
        {
            var x = new Row192(rw0[y], rw1[y], rw2[y]);
            var nx = new Row192(nw0[y], nw1[y], nw2[y]);
            blackModules += x.PopCount();
            score1 += ScoreRuns(x) + ScoreRuns(nx);
            score3 += MatchFinderRowSimd(x, nx, startMaskR);
        }

        var i = 0;
        for (; i + 2 <= size - 1; i += 2)
        {
            var a = RN192.Load(ref rw0R, ref rw1R, ref rw2R, i);
            var b = RN192.Load(ref rw0R, ref rw1R, ref rw2R, i + 1);
            (a.Xnor(b) & rowMaskV).Store(ref eq0R, ref eq1R, ref eq2R, i);
        }
        for (; i < size - 1; i++)
        {
            eq0[i] = ~(rw0[i] ^ rw0[i + 1]) & rowMaskR.W0;
            eq1[i] = ~(rw1[i] ^ rw1[i + 1]) & rowMaskR.W1;
            eq2[i] = ~(rw2[i] ^ rw2[i + 1]) & rowMaskR.W2;
        }

        y = 0;
        for (; y + 2 <= size - 1; y += 2)
        {
            var x = RN192.Load(ref rw0R, ref rw1R, ref rw2R, y);
            var eqv = RN192.Load(ref eq0R, ref eq1R, ref eq2R, y);
            var eqh = x.Xnor(x.ShiftRight(1));
            var m = eqh & eqv & eqv.ShiftRight(1) & maskN1V;
            accP2 += m.Pop();
        }
        for (; y < size - 1; y++)
        {
            var x = new Row192(rw0[y], rw1[y], rw2[y]);
            var eqv = new Row192(eq0[y], eq1[y], eq2[y]);
            var eqh = x.Xnor(x.ShiftRight(1));
            var m = eqh & eqv & eqv.ShiftRight(1) & maskN1R;
            score2 += 3 * m.PopCount();
        }

        v50[3] = 0;
        v51[3] = 0;
        v52[3] = 0;
        y = 4;
        for (; y + 2 <= size; y += 2)
        {
            var e1 = RN192.Load(ref eq0R, ref eq1R, ref eq2R, y - 4);
            var e2 = RN192.Load(ref eq0R, ref eq1R, ref eq2R, y - 3);
            var e3 = RN192.Load(ref eq0R, ref eq1R, ref eq2R, y - 2);
            var e4 = RN192.Load(ref eq0R, ref eq1R, ref eq2R, y - 1);
            (e1 & e2 & e3 & e4).Store(ref v50R, ref v51R, ref v52R, y);
        }
        for (; y < size; y++)
        {
            v50[y] = eq0[y - 4] & eq0[y - 3] & eq0[y - 2] & eq0[y - 1];
            v51[y] = eq1[y - 4] & eq1[y - 3] & eq1[y - 2] & eq1[y - 1];
            v52[y] = eq2[y - 4] & eq2[y - 3] & eq2[y - 2] & eq2[y - 1];
        }

        y = 4;
        for (; y + 2 <= size; y += 2)
        {
            var v5 = RN192.Load(ref v50R, ref v51R, ref v52R, y);
            var prev = RN192.Load(ref v50R, ref v51R, ref v52R, y - 1);
            accOnes += v5.Pop();
            accTwos += v5.AndNotWith(prev).Pop();
        }
        for (; y < size; y++)
        {
            var v5 = new Row192(v50[y], v51[y], v52[y]);
            var prev = new Row192(v50[y - 1], v51[y - 1], v52[y - 1]);
            score1 += v5.PopCount() + 2 * v5.AndNotWith(prev).PopCount();
        }

        var b0 = 0;
        for (; b0 + 2 <= size - 10; b0 += 2)
        {
            var r0 = RN192.Load(ref rw0R, ref rw1R, ref rw2R, b0);
            var r2 = RN192.Load(ref rw0R, ref rw1R, ref rw2R, b0 + 2);
            var r3 = RN192.Load(ref rw0R, ref rw1R, ref rw2R, b0 + 3);
            var r4 = RN192.Load(ref rw0R, ref rw1R, ref rw2R, b0 + 4);
            var r6 = RN192.Load(ref rw0R, ref rw1R, ref rw2R, b0 + 6);
            var r7 = RN192.Load(ref rw0R, ref rw1R, ref rw2R, b0 + 7);
            var r8 = RN192.Load(ref rw0R, ref rw1R, ref rw2R, b0 + 8);
            var r10 = RN192.Load(ref rw0R, ref rw1R, ref rw2R, b0 + 10);
            var n0 = RN192.Load(ref nw0R, ref nw1R, ref nw2R, b0);
            var n1 = RN192.Load(ref nw0R, ref nw1R, ref nw2R, b0 + 1);
            var n2c = RN192.Load(ref nw0R, ref nw1R, ref nw2R, b0 + 2);
            var n3 = RN192.Load(ref nw0R, ref nw1R, ref nw2R, b0 + 3);
            var n5c = RN192.Load(ref nw0R, ref nw1R, ref nw2R, b0 + 5);
            var n7 = RN192.Load(ref nw0R, ref nw1R, ref nw2R, b0 + 7);
            var n8 = RN192.Load(ref nw0R, ref nw1R, ref nw2R, b0 + 8);
            var n9 = RN192.Load(ref nw0R, ref nw1R, ref nw2R, b0 + 9);
            var n10 = RN192.Load(ref nw0R, ref nw1R, ref nw2R, b0 + 10);

            var mf = n0 & n1 & n2c & n3 & r4 & n5c & r6 & r7 & r8 & n9 & r10;
            var mb = r0 & n1 & r2 & r3 & r4 & n5c & r6 & n7 & n8 & n9 & n10;
            accP3 += mf.Pop() + mb.Pop();
        }
        for (; b0 <= size - 11; b0++)
        {
            var mf = new Row192(nw0[b0], nw1[b0], nw2[b0])
                   & new Row192(nw0[b0 + 1], nw1[b0 + 1], nw2[b0 + 1])
                   & new Row192(nw0[b0 + 2], nw1[b0 + 2], nw2[b0 + 2])
                   & new Row192(nw0[b0 + 3], nw1[b0 + 3], nw2[b0 + 3])
                   & new Row192(rw0[b0 + 4], rw1[b0 + 4], rw2[b0 + 4])
                   & new Row192(nw0[b0 + 5], nw1[b0 + 5], nw2[b0 + 5])
                   & new Row192(rw0[b0 + 6], rw1[b0 + 6], rw2[b0 + 6])
                   & new Row192(rw0[b0 + 7], rw1[b0 + 7], rw2[b0 + 7])
                   & new Row192(rw0[b0 + 8], rw1[b0 + 8], rw2[b0 + 8])
                   & new Row192(nw0[b0 + 9], nw1[b0 + 9], nw2[b0 + 9])
                   & new Row192(rw0[b0 + 10], rw1[b0 + 10], rw2[b0 + 10]);
            var mb = new Row192(rw0[b0], rw1[b0], rw2[b0])
                   & new Row192(nw0[b0 + 1], nw1[b0 + 1], nw2[b0 + 1])
                   & new Row192(rw0[b0 + 2], rw1[b0 + 2], rw2[b0 + 2])
                   & new Row192(rw0[b0 + 3], rw1[b0 + 3], rw2[b0 + 3])
                   & new Row192(rw0[b0 + 4], rw1[b0 + 4], rw2[b0 + 4])
                   & new Row192(nw0[b0 + 5], nw1[b0 + 5], nw2[b0 + 5])
                   & new Row192(rw0[b0 + 6], rw1[b0 + 6], rw2[b0 + 6])
                   & new Row192(nw0[b0 + 7], nw1[b0 + 7], nw2[b0 + 7])
                   & new Row192(nw0[b0 + 8], nw1[b0 + 8], nw2[b0 + 8])
                   & new Row192(nw0[b0 + 9], nw1[b0 + 9], nw2[b0 + 9])
                   & new Row192(nw0[b0 + 10], nw1[b0 + 10], nw2[b0 + 10]);
            score3 += 40 * (mf.PopCount() + mb.PopCount());
        }

        score1 += SumAcc(accOnes) + 2 * SumAcc(accTwos);
        score2 += 3 * SumAcc(accP2);
        score3 += 40 * SumAcc(accP3);
        blackModules += SumAcc(accBlack);

        return score1 + score2 + score3 + CalculateBalanceScore(blackModules, size);
    }
}
#endif
