#if NET8_0_OR_GREATER
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Transposed mask selection for versions 12-40 on 128-bit vectors, two rows or columns per vector (the design is in
/// ModulePlacer.Masking.Transposed.cs), for the builds the 128-bit tier serves: x64 without AVX2, and WebAssembly. ARM64's transposed tier
/// (ModulePlacer.Masking.Arm64.cs) masks and scores with this file's code around its own row packing and winner unpack.
/// </summary>
/// <remarks>
/// Same planes, tables, rules and checkpoint as the AVX2 tier. Popcounts accumulate in the 16-bit lanes of <see cref="AddPopCount"/> and
/// reduce only at the checkpoint and at the end of a candidate, and the 64-bit lane shifts go through <see cref="Shr"/> and
/// <see cref="Shl"/>, WebAssembly's own where the portable ones fall back to software.
/// </remarks>
internal static partial class ModulePlacer
{
    /// <summary>Mask selection for versions 12-40 on 128-bit vectors: scores all eight candidates transposed, applies the winner to <paramref name="buffer"/>, returns it.</summary>
    internal static int MaskCodeTransposedVector128(Span<byte> buffer, int size, int version, ReadOnlySpan<byte> blockedMask, QREccLevel eccLevel)
    {
        // The per-version tables come from the version's canonical blocked mask; every production caller passes that mask.
        var layout = GetTransposedLayout(version, size);
        System.Diagnostics.Debug.Assert(blockedMask.SequenceEqual(GetLayout(version).BlockedMask), "MaskCodeTransposedVector128 requires the version's canonical blocked mask");
        if (buffer.Length < size * size)
            throw new ArgumentException($"buffer too small: required {size * size}, got {buffer.Length}", nameof(buffer));
        if ((uint)eccLevel > 3)
            throw new ArgumentOutOfRangeException(nameof(eccLevel), eccLevel, "QREccLevel was out of range");

        var plane = layout.Words * layout.Stride;
        var rent = ArrayPool<ulong>.Shared.Rent(4 * plane + WorkLength(layout));
        try
        {
            var all = rent.AsSpan();
            var r = all.Slice(0, plane);
            var c = all.Slice(plane, plane);
            var rp = all.Slice(2 * plane, plane);
            var cp = all.Slice(3 * plane, plane);
            var work = all.Slice(4 * plane, WorkLength(layout));

            PackTransposedVector128(buffer, layout, version, r, c);
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

            ApplyWinnerTransposedVector128(buffer, layout, bestPattern);
            return bestPattern;
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(rent);
        }
    }

    /// <summary>
    /// Every candidate's score of the unmasked <paramref name="buffer"/> with one abort bound for all, the buffer untouched: the scores
    /// <see cref="MaskCodeTransposedVector128"/> compares, for the parity tests.
    /// </summary>
    internal static void ScoreCandidatesTransposedVector128(ReadOnlySpan<byte> buffer, int version, QREccLevel eccLevel, int abortAbove, Span<int> scores)
    {
        var size = QRCodeData.SizeFromVersion(version);
        var layout = GetTransposedLayout(version, size);
        var plane = layout.Words * layout.Stride;
        var rent = ArrayPool<ulong>.Shared.Rent(4 * plane + WorkLength(layout));
        try
        {
            var all = rent.AsSpan();
            var r = all.Slice(0, plane);
            var c = all.Slice(plane, plane);
            var rp = all.Slice(2 * plane, plane);
            var cp = all.Slice(3 * plane, plane);
            var work = all.Slice(4 * plane, WorkLength(layout));

            PackTransposedVector128(buffer, layout, version, r, c);
            for (var pattern = 0; pattern < 8; pattern++)
            {
                MaskCandidateTransposedVector128(r, c, layout, pattern, eccLevel, rp, cp);
                scores[pattern] = ScoreTransposedVector128(rp, cp, layout, work, abortAbove);
            }
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(rent);
        }
    }

    /// <summary>
    /// Packs the unmasked symbol into the row planes (padding rows zero), adds the version information, and transposes it into the column
    /// planes, one 64x64 block at a time.
    /// </summary>
    private static void PackTransposedVector128(ReadOnlySpan<byte> buffer, TransposedLayout layout, int version, Span<ulong> r, Span<ulong> c)
    {
        var size = layout.Size;
        var stride = layout.Stride;
        var words = layout.Words;
        for (var y = 0; y < size; y++)
        {
            var row = PackRowBits192Vector128(buffer.Slice(y * size, size));
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

    /// <summary>Packs a row of 0/1 module bytes into bits, 16 modules a compare and movemask, then 8-byte SWAR and scalar tails.</summary>
    private static Row192 PackRowBits192Vector128(ReadOnlySpan<byte> row)
    {
        ulong w0 = 0, w1 = 0, w2 = 0;
        var c = 0;
        ref var r = ref MemoryMarshal.GetReference(row);
        for (; c + 16 <= row.Length; c += 16)
        {
            // 16-module chunks never straddle a word: 64 is a multiple of 16.
            var bits = (ulong)(ushort)~Vector128.Equals(Vector128.LoadUnsafe(ref r, (nuint)c), Vector128<byte>.Zero).ExtractMostSignificantBits();
            if (c < 64) w0 |= bits << c;
            else if (c < 128) w1 |= bits << (c - 64);
            else w2 |= bits << (c - 128);
        }
        for (; c + 8 <= row.Length; c += 8)
        {
            var u = NormalizeEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, c)));
            var bits = (u * 0x0102040810204080UL) >> 56;
            if (c < 64) w0 |= bits << c;
            else if (c < 128) w1 |= bits << (c - 64);
            else w2 |= bits << (c - 128);
        }
        for (; c < row.Length; c++)
        {
            if (row[c] == 0) continue;
            if (c < 64) w0 |= 1ul << c;
            else if (c < 128) w1 |= 1ul << (c - 64);
            else w2 |= 1ul << (c - 128);
        }
        return new Row192(w0, w1, w2);
    }

    /// <summary>In-place transpose of a 64x64 bit block, two words per vector: the 32- to 2-row swaps pair whole vectors, the 1-row swap pairs the two lanes of one.</summary>
    private static void Transpose64Vector128(ref ulong a)
    {
        TransposeSwapVector128(ref a, 32, 0x00000000FFFFFFFFul);
        TransposeSwapVector128(ref a, 16, 0x0000FFFF0000FFFFul);
        TransposeSwapVector128(ref a, 8, 0x00FF00FF00FF00FFul);
        TransposeSwapVector128(ref a, 4, 0x0F0F0F0F0F0F0F0Ful);
        TransposeSwapVector128(ref a, 2, 0x3333333333333333ul);
        var m1 = Vector128.Create(0x5555555555555555ul, 0);
        var swap = Vector128.Create(1ul, 0ul);
        for (nuint k = 0; k < 64; k += 2)
        {
            var v = Vector128.LoadUnsafe(ref a, k);
            var t = (Shr(v, 1) ^ Vector128.Shuffle(v, swap)) & m1;
            (v ^ (Shl(t, 1) | Vector128.Shuffle(t, swap))).StoreUnsafe(ref a, k);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TransposeSwapVector128(ref ulong a, [System.Diagnostics.CodeAnalysis.ConstantExpected] byte j, ulong mask)
    {
        var m = Vector128.Create(mask);
        for (var b = 0; b < 64; b += 2 * j)
        {
            for (var k = b; k < b + j; k += 2)
            {
                var x = Vector128.LoadUnsafe(ref a, (nuint)k);
                var y = Vector128.LoadUnsafe(ref a, (nuint)(k + j));
                var t = (Shr(x, j) ^ y) & m;
                (x ^ Shl(t, j)).StoreUnsafe(ref a, (nuint)k);
                (y ^ t).StoreUnsafe(ref a, (nuint)(k + j));
            }
        }
    }

    /// <summary>Candidate <paramref name="pattern"/> in both orientations: the planes XOR the pattern's template on the data area, then its format information.</summary>
    private static void MaskCandidateTransposedVector128(ReadOnlySpan<ulong> r, ReadOnlySpan<ulong> c, TransposedLayout layout, int pattern, QREccLevel eccLevel, Span<ulong> rp, Span<ulong> cp)
    {
        var stride = layout.Stride;
        ref var allowedR = ref MemoryMarshal.GetArrayDataReference(layout.AllowedR);
        ref var allowedC = ref MemoryMarshal.GetArrayDataReference(layout.AllowedC);
        for (var k = 0; k < layout.Words; k++)
        {
            ref var tplR = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_maskTemplatePlanesR), k * 96 + pattern * 12);
            ref var tplC = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_maskTemplatePlanesC), k * 96 + pattern * 12);
            ref var src = ref Unsafe.Add(ref MemoryMarshal.GetReference(r), k * stride);
            ref var srcC = ref Unsafe.Add(ref MemoryMarshal.GetReference(c), k * stride);
            ref var dst = ref Unsafe.Add(ref MemoryMarshal.GetReference(rp), k * stride);
            ref var dstC = ref Unsafe.Add(ref MemoryMarshal.GetReference(cp), k * stride);
            ref var aR = ref Unsafe.Add(ref allowedR, k * stride);
            ref var aC = ref Unsafe.Add(ref allowedC, k * stride);
            // Two rows from an even row take template rows t and t + 1, t = y % 12.
            for (int y = 0, t = 0; y < layout.ReadRows; y += 2, t = t == 10 ? 0 : t + 2)
            {
                (Vector128.LoadUnsafe(ref src, (nuint)y) ^ (Vector128.LoadUnsafe(ref tplR, (nuint)t) & Vector128.LoadUnsafe(ref aR, (nuint)y))).StoreUnsafe(ref dst, (nuint)y);
                (Vector128.LoadUnsafe(ref srcC, (nuint)y) ^ (Vector128.LoadUnsafe(ref tplC, (nuint)t) & Vector128.LoadUnsafe(ref aC, (nuint)y))).StoreUnsafe(ref dstC, (nuint)y);
            }
        }
        SetFormatTransposed(rp, cp, layout, pattern, eccLevel);
    }

    /// <summary>The candidate's penalty score, as the AVX2 tier's ScoreTransposed computes it, with the same checkpoint.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int ScoreTransposedVector128(ReadOnlySpan<ulong> rp, ReadOnlySpan<ulong> cp, TransposedLayout layout, Span<ulong> work, int abortAbove)
    {
        var size = layout.Size;
        var stride = layout.Stride;
        var words = layout.Words;
        var length = stride + 8;
        ref var eq = ref MemoryMarshal.GetReference(work);
        ref var cv = ref Unsafe.Add(ref eq, length);
        ref var n4 = ref Unsafe.Add(ref eq, 2 * length);
        ref var h = ref Unsafe.Add(ref eq, 3 * length);

        // A 16-bit lane holds a whole candidate. An add puts at most 64 into a lane (SSSE3's byte sums, 16 on the other paths), and an
        // accumulator takes at most 534 adds (finders: 89 vectors a plane at version 40, 3 words, 2 orientations), so at most 34,176.
        var ones = Vector128<ushort>.Zero;
        var twos = Vector128<ushort>.Zero;
        var finders = Vector128<ushort>.Zero;
        var blocks = Vector128<ushort>.Zero;
        var dark = Vector128<ushort>.Zero;

        ref var rRef = ref MemoryMarshal.GetReference(rp);
        for (var k = 0; k < words; k++)
        {
            ref var a = ref Unsafe.Add(ref rRef, k * stride);
            ScoreRunsVector128(ref a, size, layout.Valid[k], ref eq, ref ones, ref twos, ref dark, countDark: true);
            ScoreBlocksVector128(ref a, ref Unsafe.Add(ref a, stride), k + 1 < words, size, layout.ValidN1[k], ref eq, ref h, ref blocks);
            ScoreFindersVector128(ref a, size, layout.Valid[k], ref cv, ref n4, ref finders);
        }
        ref var cRef = ref MemoryMarshal.GetReference(cp);
        for (var k = 0; k < words; k++)
        {
            ScoreRunsVector128(ref Unsafe.Add(ref cRef, k * stride), size, layout.Valid[k], ref eq, ref ones, ref twos, ref dark, countDark: false);
        }

        var black = Total(dark);
        if (abortAbove != int.MaxValue)
        {
            var partial = Total(ones) + 2 * Total(twos) + 3 * Total(blocks) + 40 * Total(finders) + CalculateBalanceScore(black, size);
            if (partial > abortAbove)
                return int.MaxValue;
        }

        for (var k = 0; k < words; k++)
        {
            ScoreFindersVector128(ref Unsafe.Add(ref cRef, k * stride), size, layout.Valid[k], ref cv, ref n4, ref finders);
        }

        return Total(ones) + 2 * Total(twos) + 3 * Total(blocks) + 40 * Total(finders) + CalculateBalanceScore(black, size);

        static int Total(Vector128<ushort> acc)
        {
            var lanes = LaneTotals(acc);
            return (int)(lanes.GetElement(0) + lanes.GetElement(1));
        }
    }

    /// <summary>Rule 1 along one word plane, two indexes per vector, and the dark count when asked (as the AVX2 tier's ScoreRuns).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScoreRunsVector128(ref ulong a, int size, ulong valid, ref ulong eq,
        ref Vector128<ushort> ones, ref Vector128<ushort> twos, ref Vector128<ushort> dark, bool countDark)
    {
        var validV = Vector128.Create(valid);

        // eq(-4..-1) = 0 below the first word, so rule 1 reads nothing there.
        Vector128<ulong>.Zero.StoreUnsafe(ref eq);
        Vector128<ulong>.Zero.StoreUnsafe(ref eq, 2);
        var d = dark;
        for (var i = 0; i < size; i += 2)
        {
            var x = Vector128.LoadUnsafe(ref a, (nuint)i);
            Vector128.AndNot(validV, x ^ Vector128.LoadUnsafe(ref a, (nuint)(i + 1))).StoreUnsafe(ref eq, (nuint)(i + 4));
            if (countDark) d = AddPopCount(d, x);
        }
        dark = d;
        // The last word compared itself with the zero padding: nothing follows it.
        for (var i = size + 3; i < size + 12; i++) Unsafe.Add(ref eq, i) = 0;

        var o = ones;
        var t2 = twos;
        for (var i = 4; i < size; i += 2)
        {
            var e0 = Vector128.LoadUnsafe(ref eq, (nuint)i);
            var e1 = Vector128.LoadUnsafe(ref eq, (nuint)(i + 1));
            var e2 = Vector128.LoadUnsafe(ref eq, (nuint)(i + 2));
            var t = e0 & e1 & e2;
            var cur = t & Vector128.LoadUnsafe(ref eq, (nuint)(i + 3));
            var prev = t & Vector128.LoadUnsafe(ref eq, (nuint)(i - 1));
            o = AddPopCount(o, cur);
            t2 = AddPopCount(t2, Vector128.AndNot(cur, prev));
        }
        ones = o;
        twos = t2;
    }

    /// <summary>Rule 3 along one word plane, two indexes per vector (as the AVX2 tier's ScoreFinders).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScoreFindersVector128(ref ulong a, int size, ulong valid, ref ulong cv, ref ulong n4, ref Vector128<ushort> finders)
    {
        var validV = Vector128.Create(valid);
        for (var i = 0; i < size; i += 2)
        {
            var a0 = Vector128.LoadUnsafe(ref a, (nuint)i);
            var a1 = Vector128.LoadUnsafe(ref a, (nuint)(i + 1));
            var a2 = Vector128.LoadUnsafe(ref a, (nuint)(i + 2));
            var a3 = Vector128.LoadUnsafe(ref a, (nuint)(i + 3));
            var n1 = Vector128.AndNot(validV, a1);
            (Vector128.AndNot(a0 & a2 & a3 & Vector128.LoadUnsafe(ref a, (nuint)(i + 4)) & Vector128.LoadUnsafe(ref a, (nuint)(i + 6)), Vector128.LoadUnsafe(ref a, (nuint)(i + 5))) & n1)
                .StoreUnsafe(ref cv, (nuint)i);
            Vector128.AndNot(validV, a0 | a1 | a2 | a3).StoreUnsafe(ref n4, (nuint)i);
        }
        // A light run cannot reach past the last word, and no core starts in the padding (a core ends dark, so one past the end is zero already).
        for (var i = size - 3; i < size + 12; i++) Unsafe.Add(ref n4, i) = 0;
        for (var i = size; i < size + 12; i++) Unsafe.Add(ref cv, i) = 0;

        var f = finders;
        for (var b = 0; b < size; b += 2)
        {
            var m = (Vector128.LoadUnsafe(ref n4, (nuint)b) & Vector128.LoadUnsafe(ref cv, (nuint)(b + 4)))
                  | (Vector128.LoadUnsafe(ref cv, (nuint)b) & Vector128.LoadUnsafe(ref n4, (nuint)(b + 7)));
            f = AddPopCount(f, m); // a forward window starts light, a backward one dark: disjoint, one popcount
        }
        finders = f;
    }

    /// <summary>Rule 2 along one row plane, two rows per vector (as the AVX2 tier's ScoreBlocks).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScoreBlocksVector128(ref ulong a, ref ulong next, bool hasNext, int size, ulong validN1, ref ulong eq, ref ulong h, ref Vector128<ushort> blocks)
    {
        var validN1V = Vector128.Create(validN1);
        for (var i = 0; i < size + 2; i += 2)
        {
            var x = Vector128.LoadUnsafe(ref a, (nuint)i);
            var right = Shr(x, 1);
            if (hasNext) right |= Shl(Vector128.LoadUnsafe(ref next, (nuint)i), 63);
            Vector128.AndNot(validN1V, x ^ right).StoreUnsafe(ref h, (nuint)i);
        }
        var bl = blocks;
        for (var i = 0; i < size; i += 2)
        {
            bl = AddPopCount(bl, Vector128.LoadUnsafe(ref h, (nuint)i) & Vector128.LoadUnsafe(ref h, (nuint)(i + 1)) & Vector128.LoadUnsafe(ref eq, (nuint)(i + 4)));
        }
        blocks = bl;
    }

    /// <summary>Applies the winning pattern's packed XOR delta to the byte buffer, the scalar tier's unpack (eight modules a step).</summary>
    private static void ApplyWinnerTransposedVector128(Span<byte> buffer, TransposedLayout layout, int bestPattern)
    {
        var size = layout.Size;
        ref var bufRef = ref MemoryMarshal.GetReference(buffer);
        for (int y = 0, tplRow = 0; y < size; y++)
        {
            XorUnpackRow192(ref Unsafe.Add(ref bufRef, y * size), size, WinnerDelta(layout, bestPattern, y, tplRow));
            if (++tplRow == 12) tplRow = 0;
        }
    }
}
#endif
