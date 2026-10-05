#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Vectorized mask pattern selection for x86/x64 with AVX2.
/// Selected at runtime by <see cref="ModulePlacer.MaskCode"/>; produces byte-identical matrices and identical pattern selections to the scalar bit-packed implementation in ModulePlacer.Masking.cs (verified by ModulePlacerMaskSimdParityTest).
///
/// Architecture:
/// - Versions 1-11 (one ulong per row) run lane-per-PATTERN: a Vector256&lt;ulong&gt;
///   holds the same row of four candidates, so every scoring pass is a plain
///   row loop with no scalar tail and no per-pattern reduction; masking and
///   format bits are one XOR / OR per row from per-version tables (see the
///   single-word tier below).
/// - Versions 12-40 (two or three words per row) hold each candidate as row
///   words and as column words and score every rule along the word index
///   (ModulePlacer.Masking.Transposed.X86.cs), which replaced the lane-per-row SoA
///   tiers for both widths.
/// - .NET has no VPOPCNTDQ intrinsic, so vector popcount is the Mula sequence
///   (2x vpshufb nibble LUT + vpsadbw), accumulated in weight-grouped vector
///   accumulators and reduced once per score.
/// - Byte&lt;-&gt;bit edges are native SIMD: packing 0/1 bytes is pcmpeqb+pmovmskb
///   (32 modules per step), unpacking the winner's XOR delta is
///   broadcast+vpshufb+vpcmpeqb (32 modules per step).
/// - Abort: no per-row threshold (measured, reduction cost eats the win), but
///   each scorer checkpoints once before its last finder-window loop — the partial
///   score is a lower bound (the remaining terms are non-negative), so a pattern
///   already above the best total skips that loop without changing the selection
///   (the lane-per-pattern tier skips only when all four candidates in the
///   group are above). Evaluation order stays 0..7: mask totals sit too close
///   together for win-frequency ordering to matter (measured, mask-order
///   findings log).
///
/// This file only executes under Avx2.IsSupported (x86/x64), so memory order is always little-endian and the SWAR tail reads skip endianness normalization.
/// </summary>
internal static partial class ModulePlacer
{
    /// <summary>Entry point for the vectorized tiers. Caller guarantees Avx2.IsSupported.</summary>
    internal static int MaskCodeSimd(Span<byte> buffer, int size, int version, ReadOnlySpan<byte> blockedMask, QREccLevel eccLevel)
    {
        if (size <= 64)
        {
            return MaskCode64Simd(buffer, size, version, blockedMask, eccLevel);
        }
        return MaskCodeTransposed(buffer, size, version, blockedMask, eccLevel);
    }

    // ---------------------------------
    // Shared SIMD pieces
    // ---------------------------------
    // Operand-order note: the cross-platform helper Vector256.AndNot(left, right) computes left & ~right ("bitwise-and of a given vector and the ones complement of another vector").
    // This is the OPPOSITE operand convention of the hardware intrinsic Avx2.AndNot(left, right) = ~left & right (vpandn) — the JIT swaps the operands when it emits vpandn for the helper.
    // Every AndNot in this file is the cross-platform helper, so
    // e.g. Vector256.AndNot(rowMask, x) == ~x & rowMask and Vector256.AndNot(y5, y5 << 1) == y5 & ~(y5 << 1),
    // matching the scalar code (verified byte-for-byte by ModulePlacerMaskSimdParityTest).

    /// <summary>Nibble LUT for the Mula vector popcount (per-4-bit set-bit counts).</summary>
    private static readonly Vector256<byte> PopLut256 = Vector256.Create(
        (byte)0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4,
        0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4);

    /// <summary>Per-qword popcount of 4 lanes (Mula: two vpshufb nibble lookups + vpsadbw).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> Pop256(Vector256<ulong> v)
    {
        var lowMask = Vector256.Create((byte)0x0F);
        var lo = v.AsByte() & lowMask;
        var hi = Vector256.ShiftRightLogical(v, 4).AsByte() & lowMask;
        var cnt = Avx2.Shuffle(PopLut256, lo) + Avx2.Shuffle(PopLut256, hi);
        return Avx2.SumAbsoluteDifferences(cnt, Vector256<byte>.Zero).AsUInt64();
    }

    /// <summary>Byte-replicate shuffle control: delta byte k -&gt; output bytes 8k..8k+7 (lane-local indices for vpshufb).</summary>
    private static readonly Vector256<byte> UnpackShuffle = Vector256.Create(
        (byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
        2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3);

    /// <summary>Per-byte bit selectors [1,2,4,...,128] repeated: bit i of the delta byte selects output byte i.</summary>
    private static readonly Vector256<byte> UnpackBitSel = Vector256.Create(
        (byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128,
        1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);

    /// <summary>
    /// Packs a row of 0/1 module bytes into bits: pcmpeqb+pmovmskb handles 32 modules per step (vs 8 per SWAR multiply), then 16-byte, 8-byte-SWAR and scalar tails.
    /// Bit c = row[c], same contract as PackRowBits64.
    /// </summary>
    internal static ulong PackRowBits64Simd(ReadOnlySpan<byte> row)
    {
        ulong w = 0;
        var c = 0;
        ref var r = ref MemoryMarshal.GetReference(row);
        for (; c + 32 <= row.Length; c += 32)
        {
            var v = Vector256.LoadUnsafe(ref r, (nuint)c);
            var zeroMask = (uint)Avx2.MoveMask(Vector256.Equals(v, Vector256<byte>.Zero));
            w |= (ulong)~zeroMask << c;
        }
        if (c + 16 <= row.Length)
        {
            var v = Vector128.LoadUnsafe(ref r, (nuint)c);
            var zeroMask = (uint)Sse2.MoveMask(Vector128.Equals(v, Vector128<byte>.Zero));
            w |= (ulong)(ushort)~zeroMask << c;
            c += 16;
        }
        for (; c + 8 <= row.Length; c += 8)
        {
            // x86 only (Avx2-guarded), so the load is little-endian by definition.
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
    /// Triple-word SIMD row packer.
    /// The 32-module chunks are 32-aligned so they never straddle a word boundary; the 16/8-module tails stay within a word for the same reason.
    /// </summary>
    private static Row192 PackRowBits192Simd(ReadOnlySpan<byte> row)
    {
        ulong w0 = 0, w1 = 0, w2 = 0;
        var c = 0;
        ref var r = ref MemoryMarshal.GetReference(row);
        for (; c + 32 <= row.Length; c += 32)
        {
            var v = Vector256.LoadUnsafe(ref r, (nuint)c);
            var bits = (ulong)(uint)~Avx2.MoveMask(Vector256.Equals(v, Vector256<byte>.Zero));
            if (c < 64) w0 |= bits << c;
            else if (c < 128) w1 |= bits << (c - 64);
            else w2 |= bits << (c - 128);
        }
        if (c + 16 <= row.Length)
        {
            var v = Vector128.LoadUnsafe(ref r, (nuint)c);
            var bits = (ulong)(ushort)~Sse2.MoveMask(Vector128.Equals(v, Vector128<byte>.Zero));
            if (c < 64) w0 |= bits << c;
            else if (c < 128) w1 |= bits << (c - 64);
            else w2 |= bits << (c - 128);
            c += 16;
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

    /// <summary>XORs a packed 0/1 delta into a byte row, 32 modules per SIMD step (broadcast+vpshufb+vpcmpeqb), SWAR + scalar tails.</summary>
    internal static void XorUnpackRow64Simd(Span<byte> row, ulong delta)
    {
        var c = 0;
        ref var r = ref MemoryMarshal.GetReference(row);
        var ones = Vector256.Create((byte)1);
        for (; c + 32 <= row.Length; c += 32)
        {
            var chunk = (uint)(delta >> c);
            var repl = Avx2.Shuffle(Vector256.Create(chunk).AsByte(), UnpackShuffle);
            var sel = repl & UnpackBitSel;
            var bits01 = Vector256.Equals(sel, UnpackBitSel) & ones;
            var cur = Vector256.LoadUnsafe(ref r, (nuint)c);
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

    /// <summary>Triple-word SIMD delta unpack (32-aligned chunks never straddle words), SWAR + scalar tails.</summary>
    private static void XorUnpackRow192Simd(ref byte rowRef, int len, in Row192 delta)
    {
        var c = 0;
        var ones = Vector256.Create((byte)1);
        for (; c + 32 <= len; c += 32)
        {
            var chunk = (uint)(delta.WordAt(c >> 6) >> (c & 63));
            var repl = Avx2.Shuffle(Vector256.Create(chunk).AsByte(), UnpackShuffle);
            var sel = repl & UnpackBitSel;
            var bits01 = Vector256.Equals(sel, UnpackBitSel) & ones;
            ref var p = ref Unsafe.Add(ref rowRef, c);
            var cur = Vector256.LoadUnsafe(ref p);
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
    //
    // Lane-per-PATTERN layout (second round of this tier): a Vector256<ulong> holds
    // the same row of four candidate patterns, two groups (0-3, 4-7) per call. Every
    // scorer pass is then a plain loop over rows — no scalar tails, no per-pattern
    // horizontal reductions (each lane is its own accumulator) — and masking + format
    // bits become one XOR / OR per row from per-version tables:
    //   pre[g][y]  = template[p][y % 12] & allowed[y] for the group's four patterns,
    //   fmt[e][g][y] = the ECC level's format-information bits that land in row y
    //                  (both copies), per lane.
    // The tables are built once per version from the canonical blocked mask
    // (ModulePlacer.GetLayout) and published with a volatile write. Precondition of
    // this tier (guaranteed by the placement pipeline, not by the scalar tiers, which
    // set-or-clear these bits): the 30 format-information modules are light on entry
    // — they are reserved modules that no painter or data placement writes — so the
    // overlay is an OR.
    // Popcounts of provably disjoint bit sets (dark 5-run vs light 5-run markers,
    // their run starts, the two finder-like orientations) are fused into one Mula
    // popcount of the OR. Byte->bit packing reads 32 (rows <= 32) or 64 bytes per row
    // for every row but the last (never past the buffer end).
    // Measured over the previous lane-per-row tier: v1 1.86x, v2 1.87x, v6 1.64x,
    // v10 1.58x (kernel), zero allocations after the one-time tables.

    private sealed class MaskLayout64
    {
        public readonly Vector256<ulong>[] Pre;    // [group * size + y]
        public readonly Vector256<ulong>[] Fmt;    // [(ecc * 2 + group) * size + y]
        public readonly ulong[] PreScalar;         // [pattern * size + y] for the winner's unpack

        public MaskLayout64(Vector256<ulong>[] pre, Vector256<ulong>[] fmt, ulong[] preScalar)
        {
            Pre = pre;
            Fmt = fmt;
            PreScalar = preScalar;
        }
    }

    private static readonly MaskLayout64?[] maskLayouts64 = new MaskLayout64?[12];

    private static MaskLayout64 GetMaskLayout64(int version, int size)
    {
        // The tables are cached per version, so a version/size mismatch must never reach the builder (it would poison the slot for every later caller).
        if (version < 1 || version > 11 || size != QRCodeData.SizeFromVersion(version))
            throw new ArgumentException($"size {size} does not match a single-word version {version} (1-11)", nameof(size));
        ref var slot = ref maskLayouts64[version];
        var layout = Volatile.Read(ref slot);
        if (layout is not null) return layout;
        layout = BuildMaskLayout64(version, size);
        Volatile.Write(ref slot, layout);
        return layout;
    }

    private static MaskLayout64 BuildMaskLayout64(int version, int size)
    {
        var (preScalar, fmtScalar) = BuildMaskRows64(version, size);
        var pre = new Vector256<ulong>[2 * size];
        for (var g = 0; g < 2; g++)
        {
            for (var y = 0; y < size; y++)
            {
                pre[g * size + y] = Vector256.Create(preScalar[(4 * g) * size + y], preScalar[(4 * g + 1) * size + y], preScalar[(4 * g + 2) * size + y], preScalar[(4 * g + 3) * size + y]);
            }
        }
        var fmt = new Vector256<ulong>[8 * size];
        for (var e = 0; e < 4; e++)
        {
            for (var g = 0; g < 2; g++)
            {
                var lane0 = (e * 8 + 4 * g) * size;
                for (var y = 0; y < size; y++)
                {
                    fmt[(e * 2 + g) * size + y] = Vector256.Create(fmtScalar[lane0 + y], fmtScalar[lane0 + size + y], fmtScalar[lane0 + 2 * size + y], fmtScalar[lane0 + 3 * size + y]);
                }
            }
        }

        return new MaskLayout64(pre, fmt, preScalar);
    }

    /// <summary>
    /// The single-word tiers' per-version rows: each pattern's mask restricted to the data area ([pattern * size + y]), and the rows of each
    /// (ECC level, pattern) format information, both copies ([(ecc * 8 + pattern) * size + y]).
    /// </summary>
    private static (ulong[] Pre, ulong[] Fmt) BuildMaskRows64(int version, int size)
    {
        var blockedMask = GetLayout(version).BlockedMask;
        var rowMask = size == 64 ? ulong.MaxValue : (1ul << size) - 1;

        // allowed[y] = ~blocked & rowMask, straight from the canonical bitmask
        var allowed = new ulong[size];
        for (var y = 0; y < size; y++)
        {
            ulong blocked = 0;
            for (var x = 0; x < size; x++)
            {
                if (IsModuleBlocked(blockedMask, y * size + x)) blocked |= 1ul << x;
            }
            allowed[y] = ~blocked & rowMask;
        }

        var pre = new ulong[8 * size];
        for (var p = 0; p < 8; p++)
        {
            for (var y = 0; y < size; y++)
            {
                pre[p * size + y] = _maskTemplates64[p * 12 + (y % 12)] & allowed[y];
            }
        }

        // format overlays: copy 1 at (FormatXs1[i], FormatYs1[i]), copy 2 at (size-1-i, 8) for i < 8 and (8, size-15+i) for i >= 8 — the same coordinates PokeFormatBits64 uses.
        var fmt = new ulong[32 * size];
        for (var e = 0; e < 4; e++)
        {
            for (var p = 0; p < 8; p++)
            {
                var rowsOverlay = fmt.AsSpan((e * 8 + p) * size, size);
                var bits = QRCodeConstants.GetFormatBits((QREccLevel)e, p);
                for (var i = 0; i < 15; i++)
                {
                    if ((bits & (1 << i)) == 0) continue;
                    rowsOverlay[FormatYs1[i]] |= 1ul << FormatXs1[i];
                    if (i < 8) rowsOverlay[8] |= 1ul << (size - 1 - i);
                    else rowsOverlay[size - 15 + i] |= 1ul << 8;
                }
            }
        }
        return (pre, fmt);
    }

    internal static int MaskCode64Simd(Span<byte> buffer, int size, int version, ReadOnlySpan<byte> blockedMask, QREccLevel eccLevel)
    {
        // The per-version tables are derived from the version's canonical blocked mask, not from the parameter (kept for signature parity with the scalar and ARM tiers, which do read it): callers must pass that same mask, which every production caller does (WriteQRMatrix hands over layout.BlockedMask).
        var layout = GetMaskLayout64(version, size);
        System.Diagnostics.Debug.Assert(blockedMask.SequenceEqual(GetLayout(version).BlockedMask), "MaskCode64Simd requires the version's canonical blocked mask");

        // Defense in depth for the unchecked wide loads / table indexing below; the production caller already guarantees both.
        if (buffer.Length < size * size)
            throw new ArgumentException($"buffer too small: required {size * size}, got {buffer.Length}", nameof(buffer));
        if ((uint)eccLevel > 3)
            throw new ArgumentOutOfRangeException(nameof(eccLevel), eccLevel, "QREccLevel was out of range");

        // Stack buffers are zeroed on every call, and that zeroing was a measurable share of a small symbol's
        // selection, so they come in two constant sizes: 32 rows for versions 1-3, 64 above. (Skipping the
        // zeroing with [SkipLocalsInit] was measured and is not used: see Decisions in specs/standardqr-encoder.md.)
        Span<ulong> packed = size <= 32 ? stackalloc ulong[32] : stackalloc ulong[64];
        packed = packed[..size];
        PackRows64Wide(buffer, size, packed);

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

        // rows4[y] = row y of the group's four candidates (masked, format bits in);
        // scratch for the vertical-equality rows (the 5-run marker is a rolling register in the scorer, and the
        // complements are computed where they are read: a third buffer cost more to zero than they cost to redo)
        Span<Vector256<ulong>> rows4 = size <= 32 ? stackalloc Vector256<ulong>[32] : stackalloc Vector256<ulong>[64];
        Span<Vector256<ulong>> eq4 = size <= 32 ? stackalloc Vector256<ulong>[32] : stackalloc Vector256<ulong>[64];

        var bestPatternIndex = 0;
        var bestScore = int.MaxValue;
        ref var pre = ref MemoryMarshal.GetReference(layout.Pre.AsSpan());
        ref var fmt = ref MemoryMarshal.GetReference(layout.Fmt.AsSpan());
        for (var g = 0; g < 2; g++)
        {
            var preBase = g * size;
            var fmtBase = ((int)eccLevel * 2 + g) * size;
            for (var y = 0; y < size; y++)
            {
                rows4[y] = (Vector256.Create(packed[y]) ^ Unsafe.Add(ref pre, preBase + y)) | Unsafe.Add(ref fmt, fmtBase + y);
            }
            var scores = ScoreLanes64(rows4, eq4, size, g == 0 ? int.MaxValue : bestScore);
            for (var lane = 0; lane < 4; lane++)
            {
                var s = scores.GetElement(lane);
                if (s < bestScore)
                {
                    bestScore = s;
                    bestPatternIndex = 4 * g + lane;
                }
            }
        }

        // Apply the winner to the byte buffer: unpack the XOR delta 32 modules at a time.
        var preScalar = layout.PreScalar;
        var baseIdx = bestPatternIndex * size;
        for (var y = 0; y < size; y++)
        {
            XorUnpackRow64Simd(buffer.Slice(y * size, size), preScalar[baseIdx + y]);
        }

        return bestPatternIndex;
    }

    /// <summary>
    /// Packs every row into bits with one (size &lt;= 32) or two 32-byte compare+movemask loads; rows 0..size-2 may read past their own end into the next row (masked off), the last row uses the exact-length packer so nothing is read past the buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PackRows64Wide(ReadOnlySpan<byte> buffer, int size, Span<ulong> packed)
    {
        ref var b = ref MemoryMarshal.GetReference(buffer);
        var rowMask = size == 64 ? ulong.MaxValue : (1ul << size) - 1;
        for (var y = 0; y < size - 1; y++)
        {
            ref var r = ref Unsafe.Add(ref b, y * size);
            ulong w = (uint)~Avx2.MoveMask(Vector256.Equals(Vector256.LoadUnsafe(ref r), Vector256<byte>.Zero));
            if (size > 32)
            {
                w |= (ulong)(uint)~Avx2.MoveMask(Vector256.Equals(Vector256.LoadUnsafe(ref r, 32), Vector256<byte>.Zero)) << 32;
            }
            packed[y] = w & rowMask;
        }
        packed[size - 1] = PackRowBits64Simd(buffer.Slice((size - 1) * size, size));
    }

    /// <summary>
    /// Lane-per-pattern penalty scorer: <paramref name="rows"/>[y] holds row y of four candidates; returns their four ISO/IEC 18004 penalty scores.
    /// Same rule derivations as <see cref="CalculateScorePacked"/>; the accumulators are per lane, so no horizontal reduction happens until the end.
    /// When all four lanes' partials exceed <paramref name="abortAbove"/> at the checkpoint, every lane reports int.MaxValue (pass int.MaxValue to disable, as for the first group).
    /// <paramref name="rows"/> is used as scratch for the column finder windows: the caller rebuilds it for each group.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static Vector128<int> ScoreLanes64(Span<Vector256<ulong>> rows, Span<Vector256<ulong>> eq, int size, int abortAbove)
    {
        var rowMaskV = Vector256.Create(size == 64 ? ulong.MaxValue : (1ul << size) - 1);
        var startMaskV = Vector256.Create((1ul << (size - 10)) - 1);
        var maskN1V = Vector256.Create((1ul << (size - 1)) - 1);

        var accOnes = Vector256<ulong>.Zero;  // rule-1 weight-1 counts (5-run positions)
        var accTwos = Vector256<ulong>.Zero;  // rule-1 weight-2 counts (run starts)
        var accP2 = Vector256<ulong>.Zero;    // rule-2 blocks (x3)
        var accP3 = Vector256<ulong>.Zero;    // rule-3 windows (x40)
        var accBlack = Vector256<ulong>.Zero;

        // Row-direction rules 1 and 3, balance popcount.
        for (var y = 0; y < size; y++)
        {
            var x = rows[y];
            var nx = Vector256.AndNot(rowMaskV, x);
            accBlack += Pop256(x);
            var y2 = x & Vector256.ShiftRightLogical(x, 1);
            var y4 = y2 & Vector256.ShiftRightLogical(y2, 2);
            var y5 = y4 & Vector256.ShiftRightLogical(x, 4);
            var st = Vector256.AndNot(y5, Vector256.ShiftLeft(y5, 1));
            var n2 = nx & Vector256.ShiftRightLogical(nx, 1);
            var n4 = n2 & Vector256.ShiftRightLogical(n2, 2);
            var n5 = n4 & Vector256.ShiftRightLogical(nx, 4);
            var nst = Vector256.AndNot(n5, Vector256.ShiftLeft(n5, 1));
            // a position is inside a dark 5-run or a light 5-run, never both: one popcount
            accOnes += Pop256(y5 | n5);
            accTwos += Pop256(st | nst);
            // Rule 3 from the light run and the core (see CalculateScorePacked): the two windows never share a start, one popcount.
            var core = x & Vector256.ShiftRightLogical(nx, 1) & Vector256.ShiftRightLogical(y2, 2) & Vector256.ShiftRightLogical(x, 4)
                     & Vector256.ShiftRightLogical(nx, 5) & Vector256.ShiftRightLogical(x, 6);
            accP3 += Pop256(((n4 & Vector256.ShiftRightLogical(core, 4)) | (core & Vector256.ShiftRightLogical(n4, 7))) & startMaskV);
        }

        // eq[y] = ~(rows[y] ^ rows[y+1]) & rowMask (vertical run continuation), rule 2 in the same pass.
        for (var y = 0; y < size - 1; y++)
        {
            var x = rows[y];
            var eqv = ~(x ^ rows[y + 1]) & rowMaskV;
            eq[y] = eqv;
            var eqh = ~(x ^ Vector256.ShiftRightLogical(x, 1));
            accP2 += Pop256(eqh & eqv & Vector256.ShiftRightLogical(eqv, 1) & maskN1V);
        }

        // Column rule 1: v5[y] = AND of eq[y-4..y-1], run starts vs the previous marker.
        var prev = Vector256<ulong>.Zero;
        for (var y = 4; y < size; y++)
        {
            var cur = eq[y - 4] & eq[y - 3] & eq[y - 2] & eq[y - 1];
            accOnes += Pop256(cur);
            accTwos += Pop256(Vector256.AndNot(cur, prev));
            prev = cur;
        }

        // Checkpoint (second group only): the remaining rule-3-column and balance terms are non-negative, so the partial is a lower bound of each lane's total — if every lane already exceeds the best total, skip the loop.
        if (abortAbove != int.MaxValue)
        {
            var partial = accOnes + Vector256.ShiftLeft(accTwos, 1)
                        + accP2 + Vector256.ShiftLeft(accP2, 1)
                        + Vector256.ShiftLeft(accP3, 5) + Vector256.ShiftLeft(accP3, 3);
            var gt = Vector256.GreaterThan(partial.AsInt64(), Vector256.Create((long)abortAbove));
            if (gt == Vector256<long>.AllBitsSet)
            {
                return Vector128.Create(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);
            }
        }

        // Column rule 3 from the same terms, one per row: eq[y] (column rule 1 is done with it) becomes the light run from row y
        // down, and rows[y] the core from row y down. Ascending, a row is overwritten after its own terms are taken and no later
        // row reads it. The light run is rows y..y+3 all light: the complement of their OR.
        var t = 0;
        for (; t <= size - 7; t++)
        {
            var r0 = rows[t]; var r1 = rows[t + 1]; var r2 = rows[t + 2]; var r3 = rows[t + 3];
            eq[t] = Vector256.AndNot(rowMaskV, r0 | r1 | r2 | r3);
            rows[t] = Vector256.AndNot(r0, r1) & r2 & r3 & rows[t + 4] & Vector256.AndNot(rows[t + 6], rows[t + 5]);
        }
        for (; t <= size - 4; t++)
        {
            eq[t] = Vector256.AndNot(rowMaskV, rows[t] | rows[t + 1] | rows[t + 2] | rows[t + 3]);
        }
        for (var b0 = 0; b0 <= size - 11; b0++)
        {
            accP3 += Pop256((eq[b0] & rows[b0 + 4]) | (rows[b0] & eq[b0 + 7]));
        }

        Span<int> result = stackalloc int[4];
        for (var lane = 0; lane < 4; lane++)
        {
            var s = accOnes.GetElement(lane) + 2 * accTwos.GetElement(lane) + 3 * accP2.GetElement(lane) + 40 * accP3.GetElement(lane);
            result[lane] = (int)s + CalculateBalanceScore((int)accBlack.GetElement(lane), size);
        }
        return Vector128.Create(result[0], result[1], result[2], result[3]);
    }

    // ---------------------------------
    // Pinned mask (ApplyMaskPattern)
    // ---------------------------------

    /// <summary><see cref="ApplyMaskPattern"/> unpacking 32 modules a step, for both row widths.</summary>
    internal static void ApplyMaskPatternSimd(Span<byte> buffer, int size, ReadOnlySpan<byte> blockedMask, int patternIndex)
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
                XorUnpackRow64Simd(buffer.Slice(y * size, size), _maskTemplates64[tplBase + tplRow] & AllowedRow64(padded, y * size, rowMask));
                if (++tplRow == 12) tplRow = 0;
            }
            return;
        }

        var rowMask192 = Row192.MaskLow(size);
        ref var bufRef = ref MemoryMarshal.GetReference(buffer);
        for (int y = 0, tplRow = 0; y < size; y++)
        {
            XorUnpackRow192Simd(ref Unsafe.Add(ref bufRef, y * size), size, _maskTemplates[tplBase + tplRow] & Row192.FromBitSlice(padded, y * size).AndNot(rowMask192));
            if (++tplRow == 12) tplRow = 0;
        }
    }
}
#endif
