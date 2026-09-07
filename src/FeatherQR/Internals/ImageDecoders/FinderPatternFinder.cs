#if NET8_0_OR_GREATER
using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
#endif

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// A detected finder pattern candidate (center in pixel coordinates).
/// </summary>
internal struct FinderPattern
{
    public float X;
    public float Y;
    public float ModuleSize;
    public int Count;
}

/// <summary>
/// Locates the three 7×7 finder patterns in a binarized luminance image.
/// </summary>
/// <remarks>
/// Scans rows for the characteristic 1:1:3:1:1 dark/light run ratio, then cross-checks each hit vertically, horizontally and diagonally before accepting it as a candidate (the standard ZXing-style detection approach).
/// Designed for Tier-1 inputs, clean, well-lit, screen-rendered or scanned images with mild rotation, not for low-contrast photos.
/// <para>
/// Two entry points: TryFind (three patterns, Standard QR) and FindCandidates (one pattern, Micro QR and rMQR).
/// Both stride over rows, and both are widened to a full sweep when the symbol was not read — but only TryFind can decide that for itself, because "no consistent triple" is a question about the symbol.
/// A single candidate list cannot answer the same question, so FindCandidates has no fallback of its own and its callers re-run it strideless instead.
/// See each method.
/// </para>
/// <para>
/// The scan strides over rows: the band of rows showing the 1:1:3:1:1 signature is 3 modules tall for an axis-aligned symbol (under rotation the ratios drift off-centre and it narrows — see CandidateRowStride), and although the module size is unknown before detection, the worst case (a version-40 symbol filling the frame) bounds it from below, so a stride of 3·height/(4·177) hits the band of every supported axis-aligned symbol.
/// When TryFind's stride pass cannot select a consistent triple, the rows it skipped are scanned as a complementary pass, together exactly one full-image sweep, so its striding cannot lose a symbol a full scan would find — that fallback, not the stride arithmetic, is what makes TryFind safe under rotation too.
/// On net8.0+ each row is classified into a dark bitmask with SIMD compares (AVX2, NEON, or any 128-bit acceleration) and walked run-by-run via trailing-zero counts instead of pixel-by-pixel (measured ~11x combined on the found path on x64 and 3.3-4.1x on Apple M2).
/// </para>
/// </remarks>
internal static class FinderPatternFinder
{
    private const int MaxCandidates = 32;

    /// <summary>Version 40, the largest supported symbol, is 177 modules wide.</summary>
    private const int MaxSymbolModules = 177;

    /// <summary>
    /// Searches the image for finder patterns and returns the best three.
    /// </summary>
    /// <param name="luminance">Grayscale pixels, row-major, width × height bytes.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="threshold">Binarization threshold: a pixel is dark when luminance &lt; threshold.</param>
    /// <param name="patterns">Receives the three finder patterns (top-left first is NOT guaranteed).</param>
    /// <returns>True when at least three mutually consistent finder patterns were found.</returns>
    public static bool TryFind(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, Span<FinderPattern> patterns)
        => TryFindCore(luminance, width, height, threshold, forceScalar: false, patterns);

    /// <summary>Scalar-kernel entry for parity tests; behavior-identical to <see cref="TryFind"/>.</summary>
    internal static bool TryFindScalar(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, Span<FinderPattern> patterns)
        => TryFindCore(luminance, width, height, threshold, forceScalar: true, patterns);

    /// <summary>
    /// Row stride for <see cref="FindCandidates"/>.
    /// The band of rows carrying the 1:1:3:1:1 signature is 3 modules tall only while the symbol is axis-aligned: under rotation the run ratios drift off-centre and the band that survives the cross-checks narrows.
    /// Measured over module sizes 3-8 px at every rotation, its floor is 5 rows at the 3 px/module bottom of the decode envelope, so a stride of 4 lands in every in-envelope band with a row to spare and 6 does not.
    /// The caller's strideless retry is what makes detection correct either way; this value decides how often that retry has to be paid.
    /// </summary>
    private const int CandidateRowStride = 4;

    /// <summary>
    /// Collects every cross-checked finder pattern candidate, without the three-pattern selection.
    /// Used by the Micro QR and rMQR image decoders, where a symbol carries a single finder pattern.
    /// </summary>
    /// <remarks>
    /// Strided like <see cref="TryFind"/>, but with no fallback of its own: this scan has only a flat candidate list, and every signal available inside it is a statement about the image rather than about the symbol being looked for.
    /// A confirmation test (any candidate seen on two or more rows) reads like a per-symbol signal but is not one — a second QR code, a printed logo, or salt-and-pepper noise confirms by itself and would suppress the pass the real symbol needed.
    /// The only question that distinguishes them is "did anything actually decode", which only the caller can answer, so the widening lives there: the image decoders run this scan first and re-run <see cref="FindCandidatesFullSweep"/> when nothing decoded.
    /// Skipping three rows in four is most of the rMQR image path: end to end the span decode of the widest symbol is 2.7x a strideless build's (see the plan's benchmark tables).
    /// </remarks>
    /// <param name="luminance">Grayscale pixels, row-major, width × height bytes.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="threshold">Binarization threshold: a pixel is dark when luminance &lt; threshold.</param>
    /// <param name="candidates">Receives merged candidates; <see cref="MaxFinderCandidates"/> entries suffice.</param>
    /// <returns>The number of candidates written.</returns>
    internal static int FindCandidates(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, Span<FinderPattern> candidates)
        => FindCandidatesCore(luminance, width, height, threshold, candidates, CandidateRowStride);

    /// <summary>
    /// Every row, no stride.
    /// The widening step of <see cref="FindCandidates"/>: the image decoders re-run the scan through this entry when the strided pass produced nothing that decoded, which is what keeps the detection envelope from ever being narrower than a full sweep's.
    /// </summary>
    /// <param name="luminance">Grayscale pixels, row-major, width × height bytes.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="threshold">Binarization threshold: a pixel is dark when luminance &lt; threshold.</param>
    /// <param name="candidates">Receives merged candidates; <see cref="MaxFinderCandidates"/> entries suffice.</param>
    /// <returns>The number of candidates written.</returns>
    internal static int FindCandidatesFullSweep(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, Span<FinderPattern> candidates)
        => FindCandidatesCore(luminance, width, height, threshold, candidates, stride: 1);

    private static int FindCandidatesCore(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, Span<FinderPattern> candidates, int stride)
    {
        var candidateCount = 0;
        for (var y = 0; y < height; y += stride)
        {
            ScanRow(luminance, width, height, threshold, y, forceScalar: false, candidates, ref candidateCount);
        }
        return candidateCount;
    }

    /// <summary>Capacity to provide for <see cref="FindCandidates"/>'s candidate buffer.</summary>
    internal const int MaxFinderCandidates = MaxCandidates;

    private static bool TryFindCore(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, bool forceScalar, Span<FinderPattern> patterns)
    {
        // Row stride bound: a v40 symbol filling the frame has module size
        // height/177, so its 3-module center band is 3·height/177 px tall and a
        // stride of a quarter of that hits it ≥ 4 times (≥ 2 when the symbol
        // occupies half the frame), enough for the Count-based confirmation in
        // TrySelectBestThree. Smaller strides than 3 don't pay for themselves.
        var stride = Math.Max(3, 3 * height / (4 * MaxSymbolModules));

        Span<FinderPattern> candidates = stackalloc FinderPattern[MaxCandidates];
        var candidateCount = 0;

        for (var y = 0; y < height; y += stride)
        {
            ScanRow(luminance, width, height, threshold, y, forceScalar, candidates, ref candidateCount);
        }

        if (stride > 1)
        {
            // Select on a copy: TrySelectBestThree compacts and sorts in place,
            // and a failed selection must leave the list intact for the rescan.
            Span<FinderPattern> scratch = stackalloc FinderPattern[MaxCandidates];
            candidates.Slice(0, candidateCount).CopyTo(scratch);
            if (TrySelectBestThree(scratch.Slice(0, candidateCount), patterns))
                return true;

            // Complementary rescan: only the rows the stride pass skipped, keeping
            // its candidates. Covers exactly the rows of a full scan, so the
            // detection envelope cannot regress; costs one full sweep in total.
            for (var baseY = 0; baseY < height; baseY += stride)
            {
                var limit = Math.Min(baseY + stride, height);
                for (var y = baseY + 1; y < limit; y++)
                {
                    ScanRow(luminance, width, height, threshold, y, forceScalar, candidates, ref candidateCount);
                }
            }
        }

        return TrySelectBestThree(candidates.Slice(0, candidateCount), patterns);
    }

    private static void ScanRow(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int y, bool forceScalar, Span<FinderPattern> candidates, ref int candidateCount)
    {
#if NET8_0_OR_GREATER
        // SIMD path: classify pixels into a dark bitmask with vector compares
        // (32 per AVX2 compare, 64 per NEON fold, 16 per 128-bit compare), then
        // walk RUNS via tzcnt instead of pixels, result bit-identical to the
        // scalar walk. Vector256 acceleration implies Vector128, so one gate covers
        // x64, ARM64 and WASM SIMD.
        if (!forceScalar && Vector128.IsHardwareAccelerated && width >= 16)
        {
            ScanRowMask(luminance, width, height, threshold, y, candidates, ref candidateCount);
            return;
        }
#endif
        _ = forceScalar;
        ScanRowScalar(luminance, width, height, threshold, y, candidates, ref candidateCount);
    }

    private static void ScanRowScalar(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int y, Span<FinderPattern> candidates, ref int candidateCount)
    {
        Span<int> runs = stackalloc int[5];
        var row = luminance.Slice(y * width, width);
        var runIndex = 0;
        var currentDark = false;

        for (var x = 0; x < width; x++)
        {
            var dark = row[x] < threshold;
            if (x == 0)
            {
                currentDark = dark;
                runs[0] = 1;
                runIndex = 0;
                // A pattern window must start with a dark run
                if (!dark)
                    runIndex = -1;
                continue;
            }

            if (dark == currentDark)
            {
                if (runIndex >= 0)
                    runs[runIndex]++;
                continue;
            }

            // Color flipped: advance the run window
            currentDark = dark;
            if (runIndex < 0)
            {
                // Waiting for the first dark run
                if (dark)
                {
                    runIndex = 0;
                    runs[0] = 1;
                }
                continue;
            }

            if (runIndex < 4)
            {
                runIndex++;
                runs[runIndex] = 1;
                continue;
            }

            // Window full (5 runs) and the 5th (dark) run just completed: evaluate,
            // then shift out the oldest dark/light pair; the window still starts
            // with a dark run and the incoming light run continues at index 3.
            if (IsFinderRatio(runs))
            {
                TryAddCandidate(luminance, width, height, threshold, runs, x, y, candidates, ref candidateCount);
            }

            // Shift out the oldest dark/light pair; the window still starts
            // with a dark run and the incoming light run continues at index 3.
            runs[0] = runs[2];
            runs[1] = runs[3];
            runs[2] = runs[4];
            runs[3] = 1;
            runs[4] = 0;
            runIndex = 3;
        }

        // End of row: evaluate a complete trailing window
        if (runIndex == 4 && IsFinderRatio(runs))
        {
            TryAddCandidate(luminance, width, height, threshold, runs, width, y, candidates, ref candidateCount);
        }
    }

#if NET8_0_OR_GREATER
    /// <summary>Per-byte bit weights [1,2,4,...,128] repeated: dark byte i contributes bit (i mod 8) of its half.</summary>
    private static readonly Vector128<byte> NeonBitWeights = Vector128.Create(
        (byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);

    /// <summary>
    /// Mask-based row scan: vector compares (32 px AVX2, 64 px NEON fold, 16 px otherwise) produce a dark bitmask; runs are walked via trailing-zero counts.
    /// The 1:1:3:1:1 window is evaluated at the end of every dark run from the third onward, exactly the positions and order the scalar walk evaluates, so the result is bit-identical.
    /// </summary>
    private static void ScanRowMask(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int y, Span<FinderPattern> candidates, ref int candidateCount)
    {
        // The mask covers a full row; keep the common case on the stack (512 B
        // covers rows up to ~4000 px) and rent for wider images.
        var maskLength = ((width + 63) >> 6) + 1;
        ulong[]? rented = maskLength > 64 ? ArrayPool<ulong>.Shared.Rent(maskLength) : null;
        Span<ulong> mask = rented is null ? stackalloc ulong[64] : rented;
        mask = mask.Slice(0, maskLength);
        mask.Clear();

        try
        {
            // Build the dark bitmask (bit i = pixel i of this row is dark);
            // threshold == 0 means nothing is dark, so the compare loops can skip.
            var row = luminance.Slice(y * width, width);
            var i = 0;
            if (threshold > 0)
            {
                ref var rowRef = ref MemoryMarshal.GetReference(row);
                if (Vector256.IsHardwareAccelerated && width >= 32)
                {
                    // x64 has no unsigned byte compare: unsigned v < t ⟺ min(v, t-1) == v
                    var thresholdMinus1 = Vector256.Create((byte)(threshold - 1));
                    for (; i + 32 <= width; i += 32)
                    {
                        var v = Vector256.LoadUnsafe(ref rowRef, (nuint)i);
                        var dark = Vector256.Equals(Vector256.Min(v, thresholdMinus1), v);
                        mask[i >> 6] |= (ulong)dark.ExtractMostSignificantBits() << (i & 63);
                    }
                }
                else
                {
                    // 128-bit lanes: LessThan on byte lanes is an unsigned compare
                    // (cmhi on NEON), so no min-trick is needed.
                    var thr = Vector128.Create(threshold);
                    if (AdvSimd.Arm64.IsSupported)
                    {
                        // NEON has no movemask; fold 64 pixels straight into one mask
                        // word instead: 4 compares select per-byte bit weights, 3
                        // pairwise adds reduce them (simdjson bulk-movemask shape).
                        // Measured ~8-11% over per-16 ExtractMostSignificantBits and
                        // 3.3-4.1x over the scalar walk on Apple M2
                        for (; i + 64 <= width; i += 64)
                        {
                            var d0 = Vector128.LessThan(Vector128.LoadUnsafe(ref rowRef, (nuint)i), thr) & NeonBitWeights;
                            var d1 = Vector128.LessThan(Vector128.LoadUnsafe(ref rowRef, (nuint)(i + 16)), thr) & NeonBitWeights;
                            var d2 = Vector128.LessThan(Vector128.LoadUnsafe(ref rowRef, (nuint)(i + 32)), thr) & NeonBitWeights;
                            var d3 = Vector128.LessThan(Vector128.LoadUnsafe(ref rowRef, (nuint)(i + 48)), thr) & NeonBitWeights;
                            var s = AdvSimd.Arm64.AddPairwise(AdvSimd.Arm64.AddPairwise(d0, d1), AdvSimd.Arm64.AddPairwise(d2, d3));
                            s = AdvSimd.Arm64.AddPairwise(s, s);
                            // i is a multiple of 64 here, so this writes the whole word
                            mask[i >> 6] = s.AsUInt64().ToScalar();
                        }
                    }
                    for (; i + 16 <= width; i += 16)
                    {
                        var dark = Vector128.LessThan(Vector128.LoadUnsafe(ref rowRef, (nuint)i), thr);
                        mask[i >> 6] |= (ulong)dark.ExtractMostSignificantBits() << (i & 63);
                    }
                }
            }
            for (; i < width; i++)
            {
                if (row[i] < threshold)
                    mask[i >> 6] |= 1ul << (i & 63);
            }

            // Walk dark runs. The scalar window is [dark, light, dark, light, dark],
            // evaluated whenever its 5th run (a dark run) completes, at its
            // dark→light transition or at the end of the row. That is: at the end
            // of every dark run from the third onward, with the window being that
            // run plus the two dark runs (and light gaps) before it.
            var darkStart = NextBit(mask, 0, width, set: true);
            var dPrev2 = 0; // dark run k-2
            var gPrev1 = 0; // light gap between k-2 and k-1
            var dPrev1 = 0; // dark run k-1
            var gCur = 0;   // light gap between k-1 and k
            var darkRuns = 0;

            Span<int> runs = stackalloc int[5];
            while (darkStart < width)
            {
                var darkEnd = NextBit(mask, darkStart, width, set: false);
                var dCur = darkEnd - darkStart;

                if (darkRuns >= 2 && IsFinderRatio(dPrev2, gPrev1, dPrev1, gCur, dCur))
                {
                    runs[0] = dPrev2;
                    runs[1] = gPrev1;
                    runs[2] = dPrev1;
                    runs[3] = gCur;
                    runs[4] = dCur;
                    TryAddCandidate(luminance, width, height, threshold, runs, darkEnd, y, candidates, ref candidateCount);
                }

                var nextDark = NextBit(mask, darkEnd, width, set: true);
                dPrev2 = dPrev1;
                gPrev1 = gCur;
                dPrev1 = dCur;
                gCur = nextDark - darkEnd;
                darkRuns++;
                darkStart = nextDark;
            }
        }
        finally
        {
            if (rented is not null)
                ArrayPool<ulong>.Shared.Return(rented);
        }
    }

    /// <summary>Int-argument twin of the span <see cref="IsFinderRatio(ReadOnlySpan{int})"/> with identical float math.</summary>
    private static bool IsFinderRatio(int r0, int r1, int r2, int r3, int r4)
    {
        // Runs from the mask walk are never zero (a gap between two dark runs is
        // at least one light pixel); the total check mirrors the span version.
        var total = r0 + r1 + r2 + r3 + r4;
        if (total < 7)
            return false;

        var moduleSize = total / 7f;
        var maxVariance = moduleSize / 2f;
        return Math.Abs(moduleSize - r0) < maxVariance
            && Math.Abs(moduleSize - r1) < maxVariance
            && Math.Abs(3f * moduleSize - r2) < 3f * maxVariance
            && Math.Abs(moduleSize - r3) < maxVariance
            && Math.Abs(moduleSize - r4) < maxVariance;
    }

    /// <summary>Index of the next set (or clear) bit at or after <paramref name="from"/>, or <paramref name="length"/>.</summary>
    private static int NextBit(ReadOnlySpan<ulong> mask, int from, int length, bool set)
    {
        while (from < length)
        {
            var word = mask[from >> 6];
            if (!set)
                word = ~word;
            word &= ulong.MaxValue << (from & 63);
            if (word != 0)
            {
                var index = (from & ~63) + BitOperations.TrailingZeroCount(word);
                return Math.Min(index, length);
            }
            from = (from & ~63) + 64;
        }
        return length;
    }
#endif

    /// <summary>
    /// Checks the 1:1:3:1:1 ratio with 50% per-module tolerance.
    /// </summary>
    private static bool IsFinderRatio(ReadOnlySpan<int> runs)
    {
        var total = 0;
        for (var i = 0; i < 5; i++)
        {
            if (runs[i] == 0)
                return false;
            total += runs[i];
        }
        if (total < 7)
            return false;

        var moduleSize = total / 7f;
        var maxVariance = moduleSize / 2f;
        return Math.Abs(moduleSize - runs[0]) < maxVariance
            && Math.Abs(moduleSize - runs[1]) < maxVariance
            && Math.Abs(3f * moduleSize - runs[2]) < 3f * maxVariance
            && Math.Abs(moduleSize - runs[3]) < maxVariance
            && Math.Abs(moduleSize - runs[4]) < maxVariance;
    }

    /// <summary>
    /// Cross-checks a horizontal hit vertically, then horizontally again, then diagonally; merges the refined center into the candidate list.
    /// </summary>
    private static void TryAddCandidate(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, ReadOnlySpan<int> runs, int endX, int y, Span<FinderPattern> candidates, ref int candidateCount)
    {
        var total = runs[0] + runs[1] + runs[2] + runs[3] + runs[4];
        var centerX = endX - runs[4] - runs[3] - runs[2] / 2f;

        var centerY = CrossCheck(luminance, width, height, threshold, (int)centerX, y, vertical: true, total, out _);
        if (float.IsNaN(centerY))
            return;

        centerX = CrossCheck(luminance, width, height, threshold, (int)centerX, (int)centerY, vertical: false, total, out var refinedTotal);
        if (float.IsNaN(centerX))
            return;

        if (!CrossCheckDiagonal(luminance, width, height, threshold, (int)centerX, (int)centerY))
            return;

        var moduleSize = refinedTotal / 7f;

        // Merge with an existing candidate when centers and module sizes agree
        for (var i = 0; i < candidateCount; i++)
        {
            ref var existing = ref candidates[i];
            if (Math.Abs(existing.X - centerX) <= existing.ModuleSize
                && Math.Abs(existing.Y - centerY) <= existing.ModuleSize
                && Math.Abs(existing.ModuleSize - moduleSize) <= existing.ModuleSize / 2f)
            {
                var weight = existing.Count;
                existing.X = (existing.X * weight + centerX) / (weight + 1);
                existing.Y = (existing.Y * weight + centerY) / (weight + 1);
                existing.ModuleSize = (existing.ModuleSize * weight + moduleSize) / (weight + 1);
                existing.Count++;
                return;
            }
        }

        if (candidateCount < candidates.Length)
        {
            candidates[candidateCount++] = new FinderPattern { X = centerX, Y = centerY, ModuleSize = moduleSize, Count = 1 };
        }
    }

    /// <summary>
    /// Walks outwards from a supposed center along one axis and re-validates the 1:1:3:1:1 ratio.
    /// Returns the refined center coordinate on that axis, or NaN.
    /// </summary>
    private static float CrossCheck(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int centerX, int centerY, bool vertical, int expectedTotal, out int total)
    {
        total = 0;
        var limit = vertical ? height : width;
        var center = vertical ? centerY : centerX;

        Span<int> runs = stackalloc int[5];

        // Middle dark run: walk both directions from the center
        var i = center;
        while (i >= 0 && IsDark(luminance, width, vertical ? centerX : i, vertical ? i : centerY, threshold))
        {
            runs[2]++;
            i--;
        }
        if (i < 0)
            return float.NaN;

        // Light then dark run above/left
        while (i >= 0 && !IsDark(luminance, width, vertical ? centerX : i, vertical ? i : centerY, threshold) && runs[1] <= expectedTotal)
        {
            runs[1]++;
            i--;
        }
        while (i >= 0 && IsDark(luminance, width, vertical ? centerX : i, vertical ? i : centerY, threshold) && runs[0] <= expectedTotal)
        {
            runs[0]++;
            i--;
        }

        i = center + 1;
        while (i < limit && IsDark(luminance, width, vertical ? centerX : i, vertical ? i : centerY, threshold))
        {
            runs[2]++;
            i++;
        }
        if (i >= limit)
            return float.NaN;

        while (i < limit && !IsDark(luminance, width, vertical ? centerX : i, vertical ? i : centerY, threshold) && runs[3] <= expectedTotal)
        {
            runs[3]++;
            i++;
        }
        while (i < limit && IsDark(luminance, width, vertical ? centerX : i, vertical ? i : centerY, threshold) && runs[4] <= expectedTotal)
        {
            runs[4]++;
            i++;
        }

        total = runs[0] + runs[1] + runs[2] + runs[3] + runs[4];

        // Reject when the cross section is wildly different from the row hit
        if (5 * Math.Abs(total - expectedTotal) >= 2 * expectedTotal)
            return float.NaN;

        if (!IsFinderRatio(runs))
            return float.NaN;

        return i - runs[4] - runs[3] - runs[2] / 2f;
    }

    /// <summary>
    /// Validates the 1:1:3:1:1 ratio along the top-left → bottom-right diagonal, killing false positives that pass both axis checks (e.g. dense data areas).
    /// </summary>
    private static bool CrossCheckDiagonal(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int centerX, int centerY)
    {
        Span<int> runs = stackalloc int[5];

        var i = 0;
        while (centerX - i >= 0 && centerY - i >= 0 && IsDark(luminance, width, centerX - i, centerY - i, threshold))
        {
            runs[2]++;
            i++;
        }
        if (centerX - i < 0 || centerY - i < 0)
            return false;

        while (centerX - i >= 0 && centerY - i >= 0 && !IsDark(luminance, width, centerX - i, centerY - i, threshold))
        {
            runs[1]++;
            i++;
        }
        while (centerX - i >= 0 && centerY - i >= 0 && IsDark(luminance, width, centerX - i, centerY - i, threshold))
        {
            runs[0]++;
            i++;
        }

        i = 1;
        while (centerX + i < width && centerY + i < height && IsDark(luminance, width, centerX + i, centerY + i, threshold))
        {
            runs[2]++;
            i++;
        }
        if (centerX + i >= width || centerY + i >= height)
            return false;

        while (centerX + i < width && centerY + i < height && !IsDark(luminance, width, centerX + i, centerY + i, threshold))
        {
            runs[3]++;
            i++;
        }
        while (centerX + i < width && centerY + i < height && IsDark(luminance, width, centerX + i, centerY + i, threshold))
        {
            runs[4]++;
            i++;
        }

        return IsFinderRatio(runs);
    }

    private static bool IsDark(ReadOnlySpan<byte> luminance, int width, int x, int y, byte threshold)
        => luminance[y * width + x] < threshold;

    /// <summary>
    /// Picks the three candidates with the most consistent module size, preferring repeatedly confirmed ones.
    /// </summary>
    private static bool TrySelectBestThree(Span<FinderPattern> candidates, Span<FinderPattern> patterns)
    {
        // Confirmed candidates (seen in multiple rows) are far more trustworthy
        var confirmed = 0;
        for (var i = 0; i < candidates.Length; i++)
        {
            if (candidates[i].Count >= 2)
                confirmed++;
        }

        // Compact to the confirmed subset when it is large enough to choose from
        if (confirmed >= 3 && confirmed < candidates.Length)
        {
            var w = 0;
            for (var i = 0; i < candidates.Length; i++)
            {
                if (candidates[i].Count >= 2)
                    candidates[w++] = candidates[i];
            }
            candidates = candidates.Slice(0, w);
        }

        if (candidates.Length < 3)
            return false;

        if (candidates.Length == 3)
        {
            candidates.CopyTo(patterns);
            return true;
        }

        // More than 3: sort by module size and take the most similar window of 3,
        // breaking ties toward higher confirmation counts.
        // Insertion sort: netstandard2.0 has no Span.Sort, and the list is tiny (≤ 32).
        for (var i = 1; i < candidates.Length; i++)
        {
            var current = candidates[i];
            var j = i - 1;
            while (j >= 0 && candidates[j].ModuleSize > current.ModuleSize)
            {
                candidates[j + 1] = candidates[j];
                j--;
            }
            candidates[j + 1] = current;
        }

        var bestIndex = 0;
        var bestScore = float.MaxValue;
        for (var i = 0; i + 3 <= candidates.Length; i++)
        {
            var spread = candidates[i + 2].ModuleSize - candidates[i].ModuleSize;
            var count = candidates[i].Count + candidates[i + 1].Count + candidates[i + 2].Count;
            var score = spread / count;
            if (score < bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        candidates.Slice(bestIndex, 3).CopyTo(patterns);
        return true;
    }
}
