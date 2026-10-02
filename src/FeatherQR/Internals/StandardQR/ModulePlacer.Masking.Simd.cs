#if NET8_0_OR_GREATER
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Wasm;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// 128-bit mask pattern selection for the targets with neither the AVX2 nor the ARM64 tier: x64 without AVX2, and WebAssembly.
/// Versions 1-11 run the AVX2 tier's lane-per-pattern scorer with two candidates a vector, four groups a call. Larger versions keep the
/// scalar bit-packed paths, which the AVX2 tier's SoA scorers on two rows a vector did not beat on either target.
/// </summary>
/// <remarks>
/// Popcounts accumulate in 16-bit lanes and reduce once a group: WebAssembly's byte popcount and pairwise widening add, the nibble-table
/// popcount and psadbw with SSSE3, a SWAR count of each 16-bit lane elsewhere.
/// </remarks>
internal static partial class ModulePlacer
{
    /// <summary>Entry point for the 128-bit tier.</summary>
    internal static int MaskCodeVector128(Span<byte> buffer, int size, int version, ReadOnlySpan<byte> blockedMask, QREccLevel eccLevel)
        => size <= 64
            ? MaskCode64Vector128(buffer, size, version, blockedMask, eccLevel)
            : MaskCode192(buffer, size, version, blockedMask, eccLevel);

    /// <summary>Nibble table for the SSSE3 popcount.</summary>
    private static readonly Vector128<byte> PopLut128 = Vector128.Create((byte)0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4);

    /// <summary>Adds the set bits of <paramref name="v"/> to <paramref name="acc"/>; each 64-bit lane's total is the sum of its four 16-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> AddPopCount(Vector128<ushort> acc, Vector128<ulong> v)
    {
        if (PackedSimd.IsSupported)
            return acc + PackedSimd.AddPairwiseWidening(PackedSimd.PopCount(v.AsByte()));
        if (Ssse3.IsSupported)
        {
            var lowNibbles = Vector128.Create((byte)0x0F);
            var counts = Ssse3.Shuffle(PopLut128, v.AsByte() & lowNibbles) + Ssse3.Shuffle(PopLut128, Vector128.ShiftRightLogical(v, 4).AsByte() & lowNibbles);
            return acc + Sse2.SumAbsoluteDifferences(counts, Vector128<byte>.Zero);
        }
        var x = v.AsUInt16();
        x -= (x >> 1) & Vector128.Create((ushort)0x5555);
        x = (x & Vector128.Create((ushort)0x3333)) + ((x >> 2) & Vector128.Create((ushort)0x3333));
        x = (x + (x >> 4)) & Vector128.Create((ushort)0x0F0F);
        return acc + ((x + (x >> 8)) & Vector128.Create((ushort)0x001F));
    }

    /// <summary>Each 64-bit lane's total of an <see cref="AddPopCount"/> accumulator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> LaneTotals(Vector128<ushort> acc)
    {
        var a = acc.AsUInt64();
        var pairs = (a & Vector128.Create(0x0000FFFF0000FFFFul)) + ((a >> 16) & Vector128.Create(0x0000FFFF0000FFFFul));
        return (pairs & Vector128.Create(0xFFFFFFFFul)) + (pairs >> 32);
    }

    // ---------------------------------
    // Single-word tier (versions 1-11)
    // ---------------------------------

    private sealed class MaskLayout64Vector128
    {
        public readonly Vector128<ulong>[] Pre;    // [group * size + y], patterns 2 * group and 2 * group + 1
        public readonly Vector128<ulong>[] Fmt;    // [(ecc * 4 + group) * size + y]
        public readonly ulong[] PreScalar;         // [pattern * size + y] for the winner's unpack

        public MaskLayout64Vector128(Vector128<ulong>[] pre, Vector128<ulong>[] fmt, ulong[] preScalar)
        {
            Pre = pre;
            Fmt = fmt;
            PreScalar = preScalar;
        }
    }

    private static readonly MaskLayout64Vector128?[] maskLayouts64Vector128 = new MaskLayout64Vector128?[12];

    private static MaskLayout64Vector128 GetMaskLayout64Vector128(int version, int size)
    {
        // The tables are cached per version, so a version/size mismatch must never reach the builder (it would poison the slot for every later caller).
        if (version < 1 || version > 11 || size != QRCodeData.SizeFromVersion(version))
            throw new ArgumentException($"size {size} does not match a single-word version {version} (1-11)", nameof(size));
        ref var slot = ref maskLayouts64Vector128[version];
        var layout = Volatile.Read(ref slot);
        if (layout is not null) return layout;
        var (preScalar, fmtScalar) = BuildMaskRows64(version, size);
        var pre = new Vector128<ulong>[4 * size];
        for (var g = 0; g < 4; g++)
        {
            for (var y = 0; y < size; y++)
            {
                pre[g * size + y] = Vector128.Create(preScalar[(2 * g) * size + y], preScalar[(2 * g + 1) * size + y]);
            }
        }
        var fmt = new Vector128<ulong>[16 * size];
        for (var e = 0; e < 4; e++)
        {
            for (var g = 0; g < 4; g++)
            {
                var lane0 = (e * 8 + 2 * g) * size;
                for (var y = 0; y < size; y++)
                {
                    fmt[(e * 4 + g) * size + y] = Vector128.Create(fmtScalar[lane0 + y], fmtScalar[lane0 + size + y]);
                }
            }
        }
        layout = new MaskLayout64Vector128(pre, fmt, preScalar);
        Volatile.Write(ref slot, layout);
        return layout;
    }

    internal static int MaskCode64Vector128(Span<byte> buffer, int size, int version, ReadOnlySpan<byte> blockedMask, QREccLevel eccLevel)
    {
        // The per-version tables come from the version's canonical blocked mask, as in the AVX2 tier; every production caller passes that mask.
        var layout = GetMaskLayout64Vector128(version, size);
        System.Diagnostics.Debug.Assert(blockedMask.SequenceEqual(GetLayout(version).BlockedMask), "MaskCode64Vector128 requires the version's canonical blocked mask");

        // Defense in depth for the unchecked wide loads / table indexing below; the production caller already guarantees both.
        if (buffer.Length < size * size)
            throw new ArgumentException($"buffer too small: required {size * size}, got {buffer.Length}", nameof(buffer));
        if ((uint)eccLevel > 3)
            throw new ArgumentOutOfRangeException(nameof(eccLevel), eccLevel, "QREccLevel was out of range");

        Span<ulong> packed = stackalloc ulong[64];
        packed = packed[..size];
        PackRows64Vector128(buffer, size, packed);

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

        // rows2[y] = row y of the group's two candidates (masked, format bits in)
        Span<Vector128<ulong>> rows2 = stackalloc Vector128<ulong>[64];
        Span<Vector128<ulong>> nrows2 = stackalloc Vector128<ulong>[64];
        Span<Vector128<ulong>> eq2 = stackalloc Vector128<ulong>[64];

        var bestPatternIndex = 0;
        var bestScore = int.MaxValue;
        ref var pre = ref MemoryMarshal.GetReference(layout.Pre.AsSpan());
        ref var fmt = ref MemoryMarshal.GetReference(layout.Fmt.AsSpan());
        for (var g = 0; g < 4; g++)
        {
            var preBase = g * size;
            var fmtBase = ((int)eccLevel * 4 + g) * size;
            for (var y = 0; y < size; y++)
            {
                rows2[y] = (Vector128.Create(packed[y]) ^ Unsafe.Add(ref pre, preBase + y)) | Unsafe.Add(ref fmt, fmtBase + y);
            }
            var scores = ScoreLanes64Vector128(rows2, nrows2, eq2, size, g == 0 ? int.MaxValue : bestScore);
            if (scores.Item1 < bestScore)
            {
                bestScore = scores.Item1;
                bestPatternIndex = 2 * g;
            }
            if (scores.Item2 < bestScore)
            {
                bestScore = scores.Item2;
                bestPatternIndex = 2 * g + 1;
            }
        }

        // Apply the winner to the byte buffer.
        var preScalar = layout.PreScalar;
        var baseIdx = bestPatternIndex * size;
        for (var y = 0; y < size; y++)
        {
            XorUnpackRow64(buffer.Slice(y * size, size), preScalar[baseIdx + y]);
        }

        return bestPatternIndex;
    }

    /// <summary>
    /// Packs every row into bits, 16 modules a compare and movemask; rows 0..size-2 may read past their own end into the next row (masked
    /// off), the last row takes the exact-length packer so nothing is read past the buffer.
    /// </summary>
    private static void PackRows64Vector128(ReadOnlySpan<byte> buffer, int size, Span<ulong> packed)
    {
        ref var b = ref MemoryMarshal.GetReference(buffer);
        var rowMask = size == 64 ? ulong.MaxValue : (1ul << size) - 1;
        var chunks = (size + 15) >> 4;
        for (var y = 0; y < size - 1; y++)
        {
            ref var r = ref Unsafe.Add(ref b, y * size);
            ulong w = 0;
            for (var k = 0; k < chunks; k++)
            {
                w |= (ulong)(ushort)~Vector128.Equals(Vector128.LoadUnsafe(ref r, (nuint)(16 * k)), Vector128<byte>.Zero).ExtractMostSignificantBits() << (16 * k);
            }
            packed[y] = w & rowMask;
        }
        packed[size - 1] = PackRowBits64(buffer.Slice((size - 1) * size, size));
    }

    /// <summary>
    /// Logical right shift of each 64-bit lane: WebAssembly's i64x2.shr_u, since the portable form of this lane width is a call into a
    /// software fallback on WebAssembly AOT.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> Shr(Vector128<ulong> v, [ConstantExpected] byte count)
        => PackedSimd.IsSupported ? PackedSimd.ShiftRightLogical(v, count) : Vector128.ShiftRightLogical(v, count);

    /// <summary>Left shift of each 64-bit lane, WebAssembly's i64x2.shl for the reason of <see cref="Shr"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> Shl(Vector128<ulong> v, [ConstantExpected] byte count)
        => PackedSimd.IsSupported ? PackedSimd.ShiftLeft(v, count) : Vector128.ShiftLeft(v, count);

    /// <summary>
    /// Lane-per-pattern penalty scorer: <paramref name="rows"/>[y] holds row y of two candidates; returns their two ISO/IEC 18004 penalty scores.
    /// The AVX2 tier's <see cref="ScoreLanes64"/> on half the lanes, down to the checkpoint that skips column rule 3 when both lanes already exceed <paramref name="abortAbove"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static (int, int) ScoreLanes64Vector128(Span<Vector128<ulong>> rows, Span<Vector128<ulong>> nrows, Span<Vector128<ulong>> eq, int size, int abortAbove)
    {
        var rowMaskV = Vector128.Create(size == 64 ? ulong.MaxValue : (1ul << size) - 1);
        var startMaskV = Vector128.Create((1ul << (size - 10)) - 1);
        var maskN1V = Vector128.Create((1ul << (size - 1)) - 1);

        var accOnes = Vector128<ushort>.Zero;  // rule-1 weight-1 counts (5-run positions)
        var accTwos = Vector128<ushort>.Zero;  // rule-1 weight-2 counts (run starts)
        var accP2 = Vector128<ushort>.Zero;    // rule-2 blocks (x3)
        var accP3 = Vector128<ushort>.Zero;    // rule-3 windows (x40)
        var accBlack = Vector128<ushort>.Zero;

        // Row-direction rules 1 and 3, balance popcount, complements.
        for (var y = 0; y < size; y++)
        {
            var x = rows[y];
            var nx = Vector128.AndNot(rowMaskV, x);
            nrows[y] = nx;
            accBlack = AddPopCount(accBlack, x);
            var y2 = x & Shr(x, 1);
            var y4 = y2 & Shr(y2, 2);
            var y5 = y4 & Shr(x, 4);
            var st = Vector128.AndNot(y5, Shl(y5, 1));
            var n2 = nx & Shr(nx, 1);
            var n4 = n2 & Shr(n2, 2);
            var n5 = n4 & Shr(nx, 4);
            var nst = Vector128.AndNot(n5, Shl(n5, 1));
            // a position is inside a dark 5-run or a light 5-run, never both: one popcount
            accOnes = AddPopCount(accOnes, y5 | n5);
            accTwos = AddPopCount(accTwos, st | nst);
            var mf = nx & Shr(nx, 1) & Shr(nx, 2) & Shr(nx, 3)
                   & Shr(x, 4) & Shr(nx, 5) & Shr(x, 6) & Shr(x, 7)
                   & Shr(x, 8) & Shr(nx, 9) & Shr(x, 10) & startMaskV;
            var mb = x & Shr(nx, 1) & Shr(x, 2) & Shr(x, 3)
                   & Shr(x, 4) & Shr(nx, 5) & Shr(x, 6) & Shr(nx, 7)
                   & Shr(nx, 8) & Shr(nx, 9) & Shr(nx, 10) & startMaskV;
            // mf needs the window's first module light, mb needs it dark: disjoint
            accP3 = AddPopCount(accP3, mf | mb);
        }

        // eq[y] = ~(rows[y] ^ rows[y+1]) & rowMask (vertical run continuation), rule 2 in the same pass.
        for (var y = 0; y < size - 1; y++)
        {
            var x = rows[y];
            var eqv = ~(x ^ rows[y + 1]) & rowMaskV;
            eq[y] = eqv;
            var eqh = ~(x ^ Shr(x, 1));
            accP2 = AddPopCount(accP2, eqh & eqv & Shr(eqv, 1) & maskN1V);
        }

        // Column rule 1: v5[y] = AND of eq[y-4..y-1], run starts vs the previous marker.
        var prev = Vector128<ulong>.Zero;
        for (var y = 4; y < size; y++)
        {
            var cur = eq[y - 4] & eq[y - 3] & eq[y - 2] & eq[y - 1];
            accOnes = AddPopCount(accOnes, cur);
            accTwos = AddPopCount(accTwos, Vector128.AndNot(cur, prev));
            prev = cur;
        }

        var ones = LaneTotals(accOnes);
        var twos = LaneTotals(accTwos);
        var p2 = LaneTotals(accP2);

        // Checkpoint (later groups only): the remaining rule-3-column and balance terms are non-negative, so the partial is a lower bound of each lane's total.
        if (abortAbove != int.MaxValue)
        {
            var p3Partial = LaneTotals(accP3);
            var partial = ones + (twos << 1) + p2 + (p2 << 1) + (p3Partial << 5) + (p3Partial << 3);
            if ((long)partial.GetElement(0) > abortAbove && (long)partial.GetElement(1) > abortAbove)
            {
                return (int.MaxValue, int.MaxValue);
            }
        }

        // Column rule 3: 11-row windows.
        for (var b0 = 0; b0 <= size - 11; b0++)
        {
            var mf = nrows[b0] & nrows[b0 + 1] & nrows[b0 + 2] & nrows[b0 + 3] & rows[b0 + 4] & nrows[b0 + 5] & rows[b0 + 6] & rows[b0 + 7] & rows[b0 + 8] & nrows[b0 + 9] & rows[b0 + 10];
            var mb = rows[b0] & nrows[b0 + 1] & rows[b0 + 2] & rows[b0 + 3] & rows[b0 + 4] & nrows[b0 + 5] & rows[b0 + 6] & nrows[b0 + 7] & nrows[b0 + 8] & nrows[b0 + 9] & nrows[b0 + 10];
            accP3 = AddPopCount(accP3, mf | mb);
        }

        var p3 = LaneTotals(accP3);
        var totals = ones + (twos << 1) + p2 + (p2 << 1) + (p3 << 5) + (p3 << 3);
        var black = LaneTotals(accBlack);
        return ((int)totals.GetElement(0) + CalculateBalanceScore((int)black.GetElement(0), size),
                (int)totals.GetElement(1) + CalculateBalanceScore((int)black.GetElement(1), size));
    }
}
#endif
