#if NET8_0_OR_GREATER
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Transposed mask selection for versions 12-40 on AVX2, four rows or columns per vector (the design is in ModulePlacer.Masking.Transposed.cs).
/// </summary>
/// <remarks>
/// Measured against the SoA tiers it replaced: 0.82 to 0.90 of their mask selection time at versions 12 to 27 and 0.36 to 0.44 at
/// 28 to 40 on the JIT (2026-10-04). Full per-pattern tables instead of the periodic ones gained 1 to 4 % for eight times the memory,
/// and the scalar 64x64 transpose was 7 to 16 % slower.
/// </remarks>
internal static partial class ModulePlacer
{
    /// <summary>Mask selection for versions 12-40 on AVX2: scores all eight candidates transposed, applies the winner to <paramref name="buffer"/>, returns it.</summary>
    internal static int MaskCodeTransposed(Span<byte> buffer, int size, int version, ReadOnlySpan<byte> blockedMask, QREccLevel eccLevel)
    {
        // The per-version tables come from the version's canonical blocked mask; every production caller passes that mask.
        var layout = GetTransposedLayout(version, size);
        System.Diagnostics.Debug.Assert(blockedMask.SequenceEqual(GetLayout(version).BlockedMask), "MaskCodeTransposed requires the version's canonical blocked mask");
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

            PackTransposed(buffer, layout, version, r, c);
            var bestPattern = 0;
            var bestScore = int.MaxValue;
            for (var pattern = 0; pattern < 8; pattern++)
            {
                MaskCandidateTransposed(r, c, layout, pattern, eccLevel, rp, cp);
                var score = ScoreTransposed(rp, cp, layout, work, bestScore);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestPattern = pattern;
                }
            }

            ApplyWinnerTransposed(buffer, layout, bestPattern);
            return bestPattern;
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(rent);
        }
    }

    /// <summary>
    /// <see cref="MaskCodeTransposed"/> from the interleaved stream instead of a placed buffer: the stream is placed straight into the column
    /// planes, the row planes are their transpose, and the winner is written to <paramref name="buffer"/> whole, every module of the symbol
    /// but the format information, which the caller places. The buffer needs no template or placement before.
    /// </summary>
    internal static int MaskCodeTransposedFromStream(Span<byte> buffer, int version, ReadOnlySpan<byte> interleavedData, QREccLevel eccLevel)
    {
        var size = QRCodeData.SizeFromVersion(version);
        var layout = GetTransposedLayout(version, size);
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

            PlaceStreamColumns(interleavedData, GetStreamPlacement(version, layout), layout.Stride, c);
            TransposeColumnsToRows(c, layout, r);
            var bestPattern = 0;
            var bestScore = int.MaxValue;
            for (var pattern = 0; pattern < 8; pattern++)
            {
                MaskCandidateTransposed(r, c, layout, pattern, eccLevel, rp, cp);
                var score = ScoreTransposed(rp, cp, layout, work, bestScore);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestPattern = pattern;
                }
            }

            WriteWinnerTransposed(buffer, r, layout, bestPattern);
            return bestPattern;
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(rent);
        }
    }

    /// <summary>The row planes as the transpose of the column planes, one 64x64 block at a time (the inverse of <see cref="PackTransposed"/>'s step), padding rows zero.</summary>
    private static void TransposeColumnsToRows(ReadOnlySpan<ulong> c, TransposedLayout layout, Span<ulong> r)
    {
        var stride = layout.Stride;
        var words = layout.Words;
        for (var kc = 0; kc < words; kc++)
        {
            for (var kr = 0; kr < words; kr++)
            {
                var block = r.Slice(kc * stride + 64 * kr, 64);
                c.Slice(kr * stride + 64 * kc, 64).CopyTo(block);
                Transpose64(ref MemoryMarshal.GetReference(block));
            }
            r.Slice(kc * stride + 64 * words, stride - 64 * words).Clear();
        }
    }

    /// <summary>Writes every row of the winning candidate, the unmasked row planes XOR the winner's delta, to the byte buffer, 32 modules per step.</summary>
    private static void WriteWinnerTransposed(Span<byte> buffer, ReadOnlySpan<ulong> r, TransposedLayout layout, int bestPattern)
    {
        var size = layout.Size;
        var stride = layout.Stride;
        var three = layout.Words == 3;
        ref var bufRef = ref MemoryMarshal.GetReference(buffer);
        for (int y = 0, tplRow = 0; y < size; y++)
        {
            var row = new Row192(r[y], r[stride + y], three ? r[2 * stride + y] : 0);
            UnpackRow192Simd(ref Unsafe.Add(ref bufRef, y * size), size, row ^ WinnerDelta(layout, bestPattern, y, tplRow));
            if (++tplRow == 12) tplRow = 0;
        }
    }

    /// <summary>Triple-word unpack of a row's bits to 0/1 bytes (32-aligned chunks never straddle words), SWAR and scalar tails.</summary>
    private static void UnpackRow192Simd(ref byte rowRef, int len, in Row192 bits)
    {
        var c = 0;
        var ones = Vector256.Create((byte)1);
        for (; c + 32 <= len; c += 32)
        {
            var chunk = (uint)(bits.WordAt(c >> 6) >> (c & 63));
            var repl = Avx2.Shuffle(Vector256.Create(chunk).AsByte(), UnpackShuffle);
            (Vector256.Equals(repl & UnpackBitSel, UnpackBitSel) & ones).StoreUnsafe(ref Unsafe.Add(ref rowRef, c));
        }
        for (; c + 8 <= len; c += 8)
        {
            var b = (bits.WordAt(c >> 6) >> (c & 63)) & 0xFF;
            var spread = (b * 0x0101010101010101UL) & 0x8040201008040201UL;
            spread |= spread >> 4;
            spread |= spread >> 2;
            spread |= spread >> 1;
            spread &= 0x0101010101010101UL;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref rowRef, c), spread);
        }
        for (; c < len; c++)
        {
            Unsafe.Add(ref rowRef, c) = (byte)((bits.WordAt(c >> 6) >> (c & 63)) & 1);
        }
    }

    /// <summary>
    /// Every candidate's score of the unmasked <paramref name="buffer"/> with one abort bound for all, the buffer untouched: the scores
    /// <see cref="MaskCodeTransposed"/> compares, for the parity tests.
    /// </summary>
    internal static void ScoreCandidatesTransposed(ReadOnlySpan<byte> buffer, int version, QREccLevel eccLevel, int abortAbove, Span<int> scores)
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

            PackTransposed(buffer, layout, version, r, c);
            for (var pattern = 0; pattern < 8; pattern++)
            {
                MaskCandidateTransposed(r, c, layout, pattern, eccLevel, rp, cp);
                scores[pattern] = ScoreTransposed(rp, cp, layout, work, abortAbove);
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
    private static void PackTransposed(ReadOnlySpan<byte> buffer, TransposedLayout layout, int version, Span<ulong> r, Span<ulong> c)
    {
        var size = layout.Size;
        var stride = layout.Stride;
        var words = layout.Words;
        for (var y = 0; y < size; y++)
        {
            var row = PackRowBits192Simd(buffer.Slice(y * size, size));
            r[y] = row.W0;
            r[stride + y] = row.W1;
            if (words == 3) r[2 * stride + y] = row.W2;
        }
        FinishRowPlanes(r, layout, version);

        // Block (kr, kc) of R is rows 64kr.. of word kc; transposed it is columns 64kc.. of word kr in C.
        for (var kr = 0; kr < words; kr++)
        {
            for (var kc = 0; kc < words; kc++)
            {
                var block = c.Slice(kr * stride + 64 * kc, 64);
                r.Slice(kc * stride + 64 * kr, 64).CopyTo(block);
                Transpose64(ref MemoryMarshal.GetReference(block));
            }
            c.Slice(kr * stride + 64 * words, stride - 64 * words).Clear();
        }
    }

    /// <summary>
    /// In-place transpose of a 64x64 bit block (bit c of word r becomes bit r of word c): the recursive swap of halves, quarters and so on,
    /// four words per vector. The 32- to 4-row swaps pair whole vectors, the 2- and 1-row swaps pair lanes of one vector.
    /// </summary>
    private static void Transpose64(ref ulong a)
    {
        TransposeSwap(ref a, 32, 0x00000000FFFFFFFFul);
        TransposeSwap(ref a, 16, 0x0000FFFF0000FFFFul);
        TransposeSwap(ref a, 8, 0x00FF00FF00FF00FFul);
        TransposeSwap(ref a, 4, 0x0F0F0F0F0F0F0F0Ful);
        var m2 = Vector256.Create(0x3333333333333333ul, 0x3333333333333333ul, 0, 0);
        var m1 = Vector256.Create(0x5555555555555555ul, 0, 0x5555555555555555ul, 0);
        for (nuint k = 0; k < 64; k += 4)
        {
            var v = Vector256.LoadUnsafe(ref a, k);
            // lanes 0, 1 swap with lanes 2, 3
            var t = (Vector256.ShiftRightLogical(v, 2) ^ Avx2.Permute4x64(v, 0b01_00_11_10)) & m2;
            v ^= Vector256.ShiftLeft(t, 2) | Avx2.Permute4x64(t, 0b01_00_11_10);
            // lanes 0, 2 swap with lanes 1, 3
            t = (Vector256.ShiftRightLogical(v, 1) ^ Avx2.Permute4x64(v, 0b10_11_00_01)) & m1;
            v ^= Vector256.ShiftLeft(t, 1) | Avx2.Permute4x64(t, 0b10_11_00_01);
            v.StoreUnsafe(ref a, k);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TransposeSwap(ref ulong a, int j, ulong mask)
    {
        var m = Vector256.Create(mask);
        for (var b = 0; b < 64; b += 2 * j)
        {
            for (var k = b; k < b + j; k += 4)
            {
                var x = Vector256.LoadUnsafe(ref a, (nuint)k);
                var y = Vector256.LoadUnsafe(ref a, (nuint)(k + j));
                var t = (Vector256.ShiftRightLogical(x, j) ^ y) & m;
                (x ^ Vector256.ShiftLeft(t, j)).StoreUnsafe(ref a, (nuint)k);
                (y ^ t).StoreUnsafe(ref a, (nuint)(k + j));
            }
        }
    }

    /// <summary>Candidate <paramref name="pattern"/> in both orientations: the planes XOR the pattern's template on the data area, then its format information.</summary>
    private static void MaskCandidateTransposed(ReadOnlySpan<ulong> r, ReadOnlySpan<ulong> c, TransposedLayout layout, int pattern, QREccLevel eccLevel, Span<ulong> rp, Span<ulong> cp)
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
            // Four rows from a multiple of 4 take template rows t..t+3, t = y % 12 in {0, 4, 8}.
            for (int y = 0, t = 0; y < layout.ReadRows; y += 4, t = t == 8 ? 0 : t + 4)
            {
                (Vector256.LoadUnsafe(ref src, (nuint)y) ^ (Vector256.LoadUnsafe(ref tplR, (nuint)t) & Vector256.LoadUnsafe(ref aR, (nuint)y))).StoreUnsafe(ref dst, (nuint)y);
                (Vector256.LoadUnsafe(ref srcC, (nuint)y) ^ (Vector256.LoadUnsafe(ref tplC, (nuint)t) & Vector256.LoadUnsafe(ref aC, (nuint)y))).StoreUnsafe(ref dstC, (nuint)y);
            }
        }
        SetFormatTransposed(rp, cp, layout, pattern, eccLevel);
    }

    /// <summary>
    /// The candidate's penalty score: the rules along the row planes (column direction, the 2x2 rule, the dark count), then along the column
    /// planes (row direction). Returns int.MaxValue without the column planes' finder windows once everything else with the balance exceeds
    /// <paramref name="abortAbove"/>: the rest is non-negative, so the candidate cannot win or tie.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int ScoreTransposed(ReadOnlySpan<ulong> rp, ReadOnlySpan<ulong> cp, TransposedLayout layout, Span<ulong> work, int abortAbove)
    {
        var size = layout.Size;
        var stride = layout.Stride;
        var words = layout.Words;
        var length = stride + 8;
        ref var eq = ref MemoryMarshal.GetReference(work);
        ref var cv = ref Unsafe.Add(ref eq, length);
        ref var n4 = ref Unsafe.Add(ref eq, 2 * length);
        ref var h = ref Unsafe.Add(ref eq, 3 * length);

        var ones = Vector256<ulong>.Zero;
        var twos = Vector256<ulong>.Zero;
        var finders = Vector256<ulong>.Zero;
        var blocks = Vector256<ulong>.Zero;
        var dark = Vector256<ulong>.Zero;

        ref var rRef = ref MemoryMarshal.GetReference(rp);
        for (var k = 0; k < words; k++)
        {
            ref var a = ref Unsafe.Add(ref rRef, k * stride);
            ScoreRuns(ref a, size, layout.Valid[k], ref eq, ref ones, ref twos, ref dark, countDark: true);
            ScoreBlocks(ref a, ref Unsafe.Add(ref a, stride), k + 1 < words, size, layout.ValidN1[k], ref eq, ref h, ref blocks);
            ScoreFinders(ref a, size, layout.Valid[k], ref cv, ref n4, ref finders);
        }
        ref var cRef = ref MemoryMarshal.GetReference(cp);
        for (var k = 0; k < words; k++)
        {
            ScoreRuns(ref Unsafe.Add(ref cRef, k * stride), size, layout.Valid[k], ref eq, ref ones, ref twos, ref dark, countDark: false);
        }

        var black = (int)Vector256.Sum(dark);
        if (abortAbove != int.MaxValue)
        {
            var partial = (int)Vector256.Sum(ones) + 2 * (int)Vector256.Sum(twos) + 3 * (int)Vector256.Sum(blocks) + 40 * (int)Vector256.Sum(finders)
                        + CalculateBalanceScore(black, size);
            if (partial > abortAbove)
                return int.MaxValue;
        }

        for (var k = 0; k < words; k++)
        {
            ScoreFinders(ref Unsafe.Add(ref cRef, k * stride), size, layout.Valid[k], ref cv, ref n4, ref finders);
        }

        var total = (int)Vector256.Sum(ones) + 2 * (int)Vector256.Sum(twos) + 3 * (int)Vector256.Sum(blocks) + 40 * (int)Vector256.Sum(finders);
        return total + CalculateBalanceScore(black, size);
    }

    /// <summary>
    /// Rule 1 along one word plane (index i = row of R or column of C, four per vector), and the dark count when asked. Leaves the
    /// vertical equality eq(i) = (word i == word i + 1) in <paramref name="eq"/> at index i + 4, zero from the last word on.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScoreRuns(ref ulong a, int size, ulong valid, ref ulong eq,
        ref Vector256<ulong> ones, ref Vector256<ulong> twos, ref Vector256<ulong> dark, bool countDark)
    {
        var validV = Vector256.Create(valid);

        // eq(-4..-1) = 0 below the first word, so rule 1 reads nothing there.
        Vector256<ulong>.Zero.StoreUnsafe(ref eq);
        var d = dark;
        for (var i = 0; i < size; i += 4)
        {
            var x = Vector256.LoadUnsafe(ref a, (nuint)i);
            Vector256.AndNot(validV, x ^ Vector256.LoadUnsafe(ref a, (nuint)(i + 1))).StoreUnsafe(ref eq, (nuint)(i + 4));
            if (countDark) d += Pop256(x);
        }
        dark = d;
        // The last word compared itself with the zero padding: nothing follows it.
        for (var i = size + 3; i < size + 12; i++) Unsafe.Add(ref eq, i) = 0;

        // Rule 1: v5(i) = eq(i-4..i-1) marks a fifth equal module, a run starts where v5(i-1) does not.
        var o = ones;
        var t2 = twos;
        for (var i = 4; i < size; i += 4)
        {
            var e0 = Vector256.LoadUnsafe(ref eq, (nuint)i);
            var e1 = Vector256.LoadUnsafe(ref eq, (nuint)(i + 1));
            var e2 = Vector256.LoadUnsafe(ref eq, (nuint)(i + 2));
            var t = e0 & e1 & e2;
            var cur = t & Vector256.LoadUnsafe(ref eq, (nuint)(i + 3));
            var prev = t & Vector256.LoadUnsafe(ref eq, (nuint)(i - 1));
            o += Pop256(cur);
            t2 += Pop256(Vector256.AndNot(cur, prev));
        }
        ones = o;
        twos = t2;
    }

    /// <summary>Rule 3 along one word plane, from the shared terms (see CalculateScorePacked): the core from index i, and four light modules from index i.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScoreFinders(ref ulong a, int size, ulong valid, ref ulong cv, ref ulong n4, ref Vector256<ulong> finders)
    {
        var validV = Vector256.Create(valid);
        for (var i = 0; i < size; i += 4)
        {
            var a0 = Vector256.LoadUnsafe(ref a, (nuint)i);
            var a1 = Vector256.LoadUnsafe(ref a, (nuint)(i + 1));
            var a2 = Vector256.LoadUnsafe(ref a, (nuint)(i + 2));
            var a3 = Vector256.LoadUnsafe(ref a, (nuint)(i + 3));
            var n1 = Vector256.AndNot(validV, a1);
            (Vector256.AndNot(a0 & a2 & a3 & Vector256.LoadUnsafe(ref a, (nuint)(i + 4)) & Vector256.LoadUnsafe(ref a, (nuint)(i + 6)), Vector256.LoadUnsafe(ref a, (nuint)(i + 5))) & n1)
                .StoreUnsafe(ref cv, (nuint)i);
            Vector256.AndNot(validV, a0 | a1 | a2 | a3).StoreUnsafe(ref n4, (nuint)i);
        }
        // A light run cannot reach past the last word, and no core starts in the padding (a core ends dark, so one past the end is zero already).
        for (var i = size - 3; i < size + 12; i++) Unsafe.Add(ref n4, i) = 0;
        for (var i = size; i < size + 12; i++) Unsafe.Add(ref cv, i) = 0;

        var f = finders;
        for (var b = 0; b < size; b += 4)
        {
            var m = (Vector256.LoadUnsafe(ref n4, (nuint)b) & Vector256.LoadUnsafe(ref cv, (nuint)(b + 4)))
                  | (Vector256.LoadUnsafe(ref cv, (nuint)b) & Vector256.LoadUnsafe(ref n4, (nuint)(b + 7)));
            f += Pop256(m); // a forward window starts light, a backward one dark: disjoint, one popcount
        }
        finders = f;
    }

    /// <summary>Rule 2 along one row plane: h(y) marks columns equal to their right neighbour (bit 63 against bit 0 of the next word), a block is h(y) and h(y+1) where row y equals row y+1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScoreBlocks(ref ulong a, ref ulong next, bool hasNext, int size, ulong validN1, ref ulong eq, ref ulong h, ref Vector256<ulong> blocks)
    {
        var validN1V = Vector256.Create(validN1);
        for (var i = 0; i < size + 4; i += 4)
        {
            var x = Vector256.LoadUnsafe(ref a, (nuint)i);
            var right = Vector256.ShiftRightLogical(x, 1);
            if (hasNext) right |= Vector256.ShiftLeft(Vector256.LoadUnsafe(ref next, (nuint)i), 63);
            Vector256.AndNot(validN1V, x ^ right).StoreUnsafe(ref h, (nuint)i);
        }
        var bl = blocks;
        for (var i = 0; i < size; i += 4)
        {
            bl += Pop256(Vector256.LoadUnsafe(ref h, (nuint)i) & Vector256.LoadUnsafe(ref h, (nuint)(i + 1)) & Vector256.LoadUnsafe(ref eq, (nuint)(i + 4)));
        }
        blocks = bl;
    }

    /// <summary>Applies the winning pattern's packed XOR delta to the byte buffer, 32 modules per step.</summary>
    private static void ApplyWinnerTransposed(Span<byte> buffer, TransposedLayout layout, int bestPattern)
    {
        var size = layout.Size;
        ref var bufRef = ref MemoryMarshal.GetReference(buffer);
        for (int y = 0, tplRow = 0; y < size; y++)
        {
            XorUnpackRow192Simd(ref Unsafe.Add(ref bufRef, y * size), size, WinnerDelta(layout, bestPattern, y, tplRow));
            if (++tplRow == 12) tplRow = 0;
        }
    }
}
#endif
