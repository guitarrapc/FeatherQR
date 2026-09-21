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
/// Which row kernel the finder search runs. Everything but <see cref="Auto"/> exists for the parity tests: the three kernels leave the same candidates behind.
/// </summary>
internal enum FinderRowKernel
{
    /// <summary>The fastest kernel the machine and the row width allow.</summary>
    Auto,

    /// <summary>The pixel-by-pixel row walk, and the reference walks in the cross-checks: the reference for the whole search.</summary>
    Scalar,

    /// <summary>The run-by-run walk over a dark bitmask; the scalar walk on rows too narrow for it.</summary>
    MaskWalk,

    /// <summary>All edges of the row at once and sixteen windows a step; the mask walk where <see cref="FinderPatternFinder.IsEdgeListKernelSupported"/> is false or the row is too wide or too narrow for it.</summary>
    EdgeList,
}

/// <summary>
/// Locates the three 7×7 finder patterns in a binarized luminance image.
/// </summary>
/// <remarks>
/// Scans rows for the characteristic 1:1:3:1:1 dark/light run ratio, then cross-checks each hit vertically, horizontally and diagonally before accepting it as a candidate (the standard ZXing-style detection approach).
/// Designed for Tier-1 inputs, clean, well-lit, screen-rendered or scanned images with mild rotation, not for low-contrast photos.
/// <para>
/// The ratio is checked on whole-pixel runs first. An anti-aliased edge leaves a grey pixel that a threshold rounds to a whole one, which at about 2 px/module is half a module, so runs that miss by less than 1.5 px, the same budget on every run, are measured again from <see cref="GreyLevels"/> and held to the strict tolerance, in the row scan and in all three cross-checks.
/// The tolerance is what keeps data runs out of the candidate list, so it is the measurement that is repaired and never the check; with no grey in the image the second look could only repeat the whole-pixel runs and is switched off.
/// </para>
/// <para>
/// Two entry points: TryFind (three patterns, Standard QR) and FindCandidates (one pattern, Micro QR and rMQR).
/// Both stride over rows, and both are widened to a full sweep when the symbol was not read — but only TryFind can decide that for itself, because "no consistent triple" is a question about the symbol.
/// A single candidate list cannot answer the same question, so FindCandidates has no fallback of its own and its callers re-run it strideless instead.
/// See each method.
/// </para>
/// <para>
/// The scan strides over rows: the band of rows showing the 1:1:3:1:1 signature is 3 modules tall for an axis-aligned symbol (under rotation the ratios drift off-centre and it narrows — see CandidateRowStride), and although the module size is unknown before detection, the worst case (a version-40 symbol filling the frame) bounds it from below, so a stride of 3·height/(4·177) hits the band of every supported axis-aligned symbol.
/// When TryFind's stride pass cannot select a consistent triple, or selects a poor one with once-seen candidates left out, the rows it skipped are scanned as a complementary pass, together exactly one full-image sweep, so its striding cannot lose a symbol a full scan would find — that fallback, not the stride arithmetic, is what makes TryFind safe under rotation too.
/// On net8.0+ each row is classified into a dark bitmask with SIMD compares (AVX2, NEON, or any 128-bit acceleration) and walked run-by-run via trailing-zero counts instead of pixel-by-pixel (measured ~11x combined on the found path on x64 and 3.3-4.1x on Apple M2).
/// </para>
/// </remarks>
internal static partial class FinderPatternFinder
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
    /// <param name="grey">Grey levels for re-measuring a near miss; <c>default</c> measures whole pixels only.</param>
    /// <returns>True when at least three mutually consistent finder patterns were found.</returns>
    public static bool TryFind(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, Span<FinderPattern> patterns, in GreyLevels grey)
        => TryFindCore(luminance, width, height, threshold, grey, FinderRowKernel.Auto, patterns);

    /// <summary>Reference entry for parity tests: the scalar row walk and the reference cross-check walks; behavior-identical to <see cref="TryFind"/>.</summary>
    internal static bool TryFindScalar(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, Span<FinderPattern> patterns, in GreyLevels grey)
        => TryFindCore(luminance, width, height, threshold, grey, FinderRowKernel.Scalar, patterns);

    /// <summary>Kernel-selecting entry for parity tests; behavior-identical to <see cref="TryFind"/> under every kernel.</summary>
    internal static bool TryFindWith(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, Span<FinderPattern> patterns, in GreyLevels grey, FinderRowKernel kernel)
        => TryFindCore(luminance, width, height, threshold, grey, kernel, patterns);

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
    /// <param name="grey">Grey levels for re-measuring a near miss; <c>default</c> measures whole pixels only.</param>
    /// <returns>The number of candidates written.</returns>
    internal static int FindCandidates(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, Span<FinderPattern> candidates, in GreyLevels grey)
        => FindCandidatesCore(luminance, width, height, threshold, grey, candidates, CandidateRowStride, FinderRowKernel.Auto);

    /// <summary>
    /// Every row, no stride.
    /// The widening step of <see cref="FindCandidates"/>: the image decoders re-run the scan through this entry when the strided pass produced nothing that decoded, which is what keeps the detection envelope from ever being narrower than a full sweep's.
    /// </summary>
    /// <param name="luminance">Grayscale pixels, row-major, width × height bytes.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="threshold">Binarization threshold: a pixel is dark when luminance &lt; threshold.</param>
    /// <param name="candidates">Receives merged candidates; <see cref="MaxFinderCandidates"/> entries suffice.</param>
    /// <param name="grey">Grey levels for re-measuring a near miss; <c>default</c> measures whole pixels only.</param>
    /// <returns>The number of candidates written.</returns>
    internal static int FindCandidatesFullSweep(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, Span<FinderPattern> candidates, in GreyLevels grey)
        => FindCandidatesCore(luminance, width, height, threshold, grey, candidates, stride: 1, FinderRowKernel.Auto);

    /// <summary>Stride- and kernel-selecting entry for parity tests.</summary>
    internal static int FindCandidatesWith(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, Span<FinderPattern> candidates, in GreyLevels grey, int stride, FinderRowKernel kernel)
        => FindCandidatesCore(luminance, width, height, threshold, grey, candidates, stride, kernel);

    private static int FindCandidatesCore(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, Span<FinderPattern> candidates, int stride, FinderRowKernel kernel)
    {
        var rentedEdges = RentEdgeBuffer(kernel, width);
        try
        {
            var candidateCount = 0;
            for (var y = 0; y < height; y += stride)
            {
                ScanRow(luminance, width, height, threshold, grey, y, kernel, rentedEdges, candidates, ref candidateCount);
            }
            return candidateCount;
        }
        finally
        {
            ReturnEdgeBuffer(rentedEdges);
        }
    }

    /// <summary>Capacity to provide for <see cref="FindCandidates"/>'s candidate buffer.</summary>
    internal const int MaxFinderCandidates = MaxCandidates;

    private static bool TryFindCore(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, FinderRowKernel kernel, Span<FinderPattern> patterns)
    {
        // One rental a search, not a row: the edge-list kernel's buffer
        var rentedEdges = RentEdgeBuffer(kernel, width);
        try
        {
            return TryFindRows(luminance, width, height, threshold, grey, kernel, rentedEdges, patterns);
        }
        finally
        {
            ReturnEdgeBuffer(rentedEdges);
        }
    }

    private static bool TryFindRows(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, FinderRowKernel kernel, Span<short> edges, Span<FinderPattern> patterns)
    {
        // Row stride bound: a v40 symbol filling the frame has module size height/177.
        // Its 3-module center band is 3·height/177 px tall and a stride of a quarter of that hits it ≥ 4 times (≥ 2 when the symbol occupies half the frame), enough for the Count-based confirmation in TrySelectBestThree.
        // Smaller strides than 3 don't pay for themselves.
        var stride = Math.Max(3, 3 * height / (4 * MaxSymbolModules));

        Span<FinderPattern> candidates = stackalloc FinderPattern[MaxCandidates];
        var candidateCount = 0;

        for (var y = 0; y < height; y += stride)
        {
            ScanRow(luminance, width, height, threshold, grey, y, kernel, edges, candidates, ref candidateCount);
        }

        if (stride > 1)
        {
            // Select on a copy: TrySelectBestThree compacts and sorts in place, and a failed selection must leave the list intact for the rescan.
            Span<FinderPattern> scratch = stackalloc FinderPattern[MaxCandidates];
            candidates.Slice(0, candidateCount).CopyTo(scratch);
            // A stride can hit a real finder's band once while a false candidate is confirmed; a poor triple with candidates left out is not an answer yet.
            var strideSelected = TrySelectBestThree(scratch.Slice(0, candidateCount), patterns, out var unconfirmedLeftOut);
            if (strideSelected && !unconfirmedLeftOut)
                return true;

            // Complementary rescan: only the rows the stride pass skipped, keeping its candidates. Covers exactly the rows of a full scan, so the
            // detection envelope cannot regress. Costs one full sweep in total.
            for (var baseY = 0; baseY < height; baseY += stride)
            {
                var limit = Math.Min(baseY + stride, height);
                for (var y = baseY + 1; y < limit; y++)
                {
                    ScanRow(luminance, width, height, threshold, grey, y, kernel, edges, candidates, ref candidateCount);
                }
            }

            if (strideSelected)
            {
                // A keystoned real triple scores poorly too, and the rows that confirm its finders confirm false candidates inside the symbol with them, over less of their height. The sweep's triple has to be confirmed over as much.
                var strideHeight = ConfirmedHeight(candidates.Slice(0, candidateCount), patterns);
                Span<FinderPattern> swept = stackalloc FinderPattern[3];
                if (TrySelectBestThree(candidates.Slice(0, candidateCount), swept)
                    && ConfirmedHeight(swept, swept) >= strideHeight)
                {
                    swept.CopyTo(patterns);
                }
                return true;
            }
        }

        return TrySelectBestThree(candidates.Slice(0, candidateCount), patterns);
    }

    /// <summary>
    /// Height the full sweep confirmed a triple over, in modules: each pattern's rows are read from the swept candidate it merged into.
    /// In modules because a false candidate of twice the module size is confirmed on as many rows as a real finder.
    /// </summary>
    private static float ConfirmedHeight(ReadOnlySpan<FinderPattern> candidates, ReadOnlySpan<FinderPattern> triple)
    {
        var modules = 0f;
        for (var t = 0; t < 3; t++)
        {
            for (var i = 0; i < candidates.Length; i++)
            {
                if (Math.Abs(candidates[i].X - triple[t].X) <= candidates[i].ModuleSize && Math.Abs(candidates[i].Y - triple[t].Y) <= candidates[i].ModuleSize)
                {
                    modules += candidates[i].Count / candidates[i].ModuleSize;
                    break;
                }
            }
        }
        return modules;
    }

    private static void ScanRow(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, int y, FinderRowKernel kernel, Span<short> edges, Span<FinderPattern> candidates, ref int candidateCount)
    {
#if NET8_0_OR_GREATER
        // A buffer means the search chose the edge-list kernel for this width
        if (!edges.IsEmpty)
        {
            ScanRowEdges(luminance, width, height, threshold, grey, y, edges, candidates, ref candidateCount);
            return;
        }

        // SIMD path: classify pixels into a dark bitmask with vector compares (32 per AVX2 compare, 64 per NEON fold, 16 per 128-bit compare), then walk RUNS via tzcnt instead of pixels, result bit-identical to the scalar walk. Vector256 acceleration implies Vector128, so one gate covers x64, ARM64 and WASM SIMD.
        if (kernel != FinderRowKernel.Scalar && Vector128.IsHardwareAccelerated && width >= 16)
        {
            ScanRowMask(luminance, width, height, threshold, grey, y, candidates, ref candidateCount);
            return;
        }
#endif
        ScanRowScalar(luminance, width, height, threshold, grey, y, referenceWalk: kernel == FinderRowKernel.Scalar, candidates, ref candidateCount);
    }

    private static void ScanRowScalar(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, int y, bool referenceWalk, Span<FinderPattern> candidates, ref int candidateCount)
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

            // Window full (5 runs) and the 5th (dark) run just completed: evaluate, then shift out the oldest dark/light pair; the window still starts with a dark run and the incoming light run continues at index 3.
            if (IsFinderRatio(runs) || IsFinderRatioByCoverage(luminance, width, height, grey, runs, x, y, 1, 0) || IsRepeatedNearFinderRatio(luminance, width, height, threshold, runs[0], runs[1], runs[2], runs[3], runs[4], x, y))
            {
                TryAddCandidate(luminance, width, height, threshold, grey, runs, x, y, referenceWalk, candidates, ref candidateCount);
            }

            // Shift out the oldest dark/light pair; the window still starts with a dark run and the incoming light run continues at index 3.
            runs[0] = runs[2];
            runs[1] = runs[3];
            runs[2] = runs[4];
            runs[3] = 1;
            runs[4] = 0;
            runIndex = 3;
        }

        // End of row: evaluate a complete trailing window
        if (runIndex == 4 && (IsFinderRatio(runs) || IsFinderRatioByCoverage(luminance, width, height, grey, runs, width, y, 1, 0) || IsRepeatedNearFinderRatio(luminance, width, height, threshold, runs[0], runs[1], runs[2], runs[3], runs[4], width, y)))
        {
            TryAddCandidate(luminance, width, height, threshold, grey, runs, width, y, referenceWalk, candidates, ref candidateCount);
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
    private static void ScanRowMask(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, int y, Span<FinderPattern> candidates, ref int candidateCount)
    {
        // The mask covers a full row; keep the common case on the stack (512 B covers rows up to ~4000 px) and rent for wider images.
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
                    // 128-bit lanes: LessThan on byte lanes is an unsigned compare (cmhi on NEON), so no min-trick is needed.
                    var thr = Vector128.Create(threshold);
                    if (AdvSimd.Arm64.IsSupported)
                    {
                        // NEON has no movemask; fold 64 pixels straight into one mask word instead: 4 compares select per-byte bit weights, 3 pairwise adds reduce them (simdjson bulk-movemask shape).
                        // Measured ~8-11% over per-16 ExtractMostSignificantBits and 3.3-4.1x over the scalar walk on Apple M2
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

            // Walk dark runs. The scalar window is [dark, light, dark, light, dark], evaluated whenever its 5th run (a dark run) completes, at its dark→light transition or at the end of the row. That is: at the end of every dark run from the third onward, with the window being that run plus the two dark runs (and light gaps) before it.
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

                if (darkRuns >= 2)
                {
                    if (IsFinderRatio(dPrev2, gPrev1, dPrev1, gCur, dCur)
                        || (grey.IsEnabled && IsNearFinderRatio(dPrev2, gPrev1, dPrev1, gCur, dCur) && IsFinderRatioByCoverage(luminance, width, height, grey, dPrev2, gPrev1, dPrev1, gCur, dCur, darkEnd, y, 1, 0))
                        || IsRepeatedNearFinderRatio(luminance, width, height, threshold, dPrev2, gPrev1, dPrev1, gCur, dCur, darkEnd, y))
                    {
                        runs[0] = dPrev2;
                        runs[1] = gPrev1;
                        runs[2] = dPrev1;
                        runs[3] = gCur;
                        runs[4] = dCur;
                        TryAddCandidate(luminance, width, height, threshold, grey, runs, darkEnd, y, referenceWalk: false, candidates, ref candidateCount);
                    }
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
        // Runs from the mask walk are never zero (a gap between two dark runs is at least one light pixel); the total check mirrors the span version.
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
    internal static bool IsFinderRatio(ReadOnlySpan<int> runs)
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
    /// The tolerance a near miss is held to, the largest whole seventh below 1.5 px: an edge pixel on the wrong side of the threshold moves a run by one.
    /// </summary>
    private const int NearMissSevenths = 10;

    /// <summary>
    /// The centre run gets the same budget, not three times it. The tolerance is an absolute number of pixels, because it stands for edge pixels landing on the wrong side of the threshold, and a run has two edges however many modules wide it is.
    /// Scaling it by the run's width, as the strict check scales its own per-module tolerance, would admit a window whose centre is nothing like three modules: a 1:1:1:1:1 run is then a near miss, and the diagonal cross-check exists to refuse exactly that.
    /// </summary>
    private const int NearMissCentreSevenths = NearMissSevenths;

    /// <summary>
    /// Runs within 1.5 px of the 1:1:3:1:1 ratio on every one of the five, asked only of runs that failed the half-module check.
    /// From about 2.9 px/module half a module is the wider of the two tolerances, so nothing that failed is near: this reaches low densities only.
    /// </summary>
    internal static bool IsNearFinderRatio(int r0, int r1, int r2, int r3, int r4)
    {
        var total = r0 + r1 + r2 + r3 + r4;
        if (total < 7)
            return false;

        // In sevenths of a pixel, against a module size of total/7: |total − 7r| <= 10, the same budget on every run.
        // Written as an unsigned range test because Math.Abs(int) has to branch for int.MinValue, and this runs on every window.
        return (uint)(total - 7 * r0 + NearMissSevenths) <= 2 * NearMissSevenths
            && (uint)(total - 7 * r1 + NearMissSevenths) <= 2 * NearMissSevenths
            && (uint)(3 * total - 7 * r2 + NearMissCentreSevenths) <= 2 * NearMissCentreSevenths
            && (uint)(total - 7 * r3 + NearMissSevenths) <= 2 * NearMissSevenths
            && (uint)(total - 7 * r4 + NearMissSevenths) <= 2 * NearMissSevenths;
    }

    /// <summary>
    /// The 1:1:3:1:1 check on runs measured from grey levels, for a window whose whole-pixel runs are a near miss.
    /// The window ends just before pixel (<paramref name="endX"/>, <paramref name="endY"/>) and runs back along (<paramref name="stepX"/>, <paramref name="stepY"/>).
    /// </summary>
    /// <remarks>
    /// Each edge moves from its pixel boundary by the coverage of the two pixels beside it, which is exact for an edge a box filter drew.
    /// The tolerance stays the strict one, so a window of whole pixels measures as it did and is refused as it was.
    /// </remarks>
    private static bool IsFinderRatioByCoverage(ReadOnlySpan<byte> luminance, int width, int height, in GreyLevels grey, ReadOnlySpan<int> runs, int endX, int endY, int stepX, int stepY)
        => grey.IsEnabled
            && runs[0] != 0 && runs[1] != 0 && runs[3] != 0 && runs[4] != 0
            && IsNearFinderRatio(runs[0], runs[1], runs[2], runs[3], runs[4])
            && IsFinderRatioByCoverage(luminance, width, height, grey, runs[0], runs[1], runs[2], runs[3], runs[4], endX, endY, stepX, stepY);

    /// <summary>The measurement itself, for runs already known to be a near miss.</summary>
    private static bool IsFinderRatioByCoverage(ReadOnlySpan<byte> luminance, int width, int height, in GreyLevels grey, int r0, int r1, int r2, int r3, int r4, int endX, int endY, int stepX, int stepY)
    {
        var total = r0 + r1 + r2 + r3 + r4;

        // Outer edges first: they give the module size every run is held to
        var b1 = r0;
        var b2 = b1 + r1;
        var b3 = b2 + r2;
        var b4 = b3 + r3;
        var e0 = Edge(luminance, width, height, grey, endX, endY, stepX, stepY, total, 0, rising: true);
        var e5 = Edge(luminance, width, height, grey, endX, endY, stepX, stepY, total, total, rising: false);
        var moduleSize = (e5 - e0) / 7f;
        var maxVariance = moduleSize / 2f;

        var e1 = Edge(luminance, width, height, grey, endX, endY, stepX, stepY, total, b1, rising: false);
        if (!(Math.Abs(moduleSize - (e1 - e0)) < maxVariance))
            return false;
        var e4 = Edge(luminance, width, height, grey, endX, endY, stepX, stepY, total, b4, rising: true);
        if (!(Math.Abs(moduleSize - (e5 - e4)) < maxVariance))
            return false;
        var e2 = Edge(luminance, width, height, grey, endX, endY, stepX, stepY, total, b2, rising: true);
        if (!(Math.Abs(moduleSize - (e2 - e1)) < maxVariance))
            return false;
        var e3 = Edge(luminance, width, height, grey, endX, endY, stepX, stepY, total, b3, rising: false);
        return Math.Abs(3f * moduleSize - (e3 - e2)) < 3f * maxVariance
            && Math.Abs(moduleSize - (e4 - e3)) < maxVariance;

        // The edge at whole-pixel boundary `boundary` (pixels from the window's start), moved by the coverage of the pixel on each side; rising is light to dark
        static float Edge(ReadOnlySpan<byte> luminance, int width, int height, in GreyLevels grey, int endX, int endY, int stepX, int stepY, int total, int boundary, bool rising)
        {
            var back = total - boundary;
            var after = Darkness(luminance, width, height, grey, endX - back * stepX, endY - back * stepY);
            var before = Darkness(luminance, width, height, grey, endX - (back + 1) * stepX, endY - (back + 1) * stepY);
            return rising ? boundary + (1f - after) - before : boundary - (1f - before) + after;
        }

        static float Darkness(ReadOnlySpan<byte> luminance, int width, int height, in GreyLevels grey, int x, int y)
            => x < 0 || y < 0 || x >= width || y >= height ? 0f : grey.Darkness(luminance[y * width + x]);
    }

    /// <summary>
    /// Cross-checks a horizontal hit vertically, then horizontally again, then diagonally; merges the refined center into the candidate list.
    /// </summary>
    private static void TryAddCandidate(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, ReadOnlySpan<int> runs, int endX, int y, bool referenceWalk, Span<FinderPattern> candidates, ref int candidateCount)
    {
        var total = runs[0] + runs[1] + runs[2] + runs[3] + runs[4];
        var rowCenterX = endX - runs[4] - runs[3] - runs[2] / 2f;

        if (!TryCrossCheck(luminance, width, height, threshold, grey, rowCenterX, y, total, referenceWalk, out var centerX, out var centerY, out var refinedTotal)
            && !TryCrossCheckWholePattern(luminance, width, height, threshold, runs, endX, y, referenceWalk, out centerX, out centerY, out refinedTotal))
        {
            return;
        }

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

    /// <summary>The ratio along the column, the row again and the diagonal.</summary>
    private static bool TryCrossCheck(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, float rowCenterX, int y, int total, bool referenceWalk, out float centerX, out float centerY, out int refinedTotal)
    {
        centerX = rowCenterX;
        refinedTotal = 0;
        centerY = CrossCheck(luminance, width, height, threshold, grey, (int)rowCenterX, y, vertical: true, total, referenceWalk, out _, default);
        if (float.IsNaN(centerY))
            return false;

        centerX = CrossCheck(luminance, width, height, threshold, grey, (int)rowCenterX, (int)centerY, vertical: false, total, referenceWalk, out refinedTotal, default);
        if (float.IsNaN(centerX))
            return false;

        return CrossCheckDiagonal(luminance, width, height, threshold, grey, (int)centerX, (int)centerY, referenceWalk);
    }

    /// <summary>
    /// The runs a crisp finder leaves under about 1.6 px/module, where each module is 1 or 2 px wide: four single modules and a centre of 3 to 5.
    /// The centre first: on fine noise, where this is asked of every window, few runs are that long.
    /// </summary>
    internal static bool IsSmallCrispFinderRuns(int r0, int r1, int r2, int r3, int r4)
        => (uint)(r2 - 3) <= 2u && (uint)(r0 - 1) <= 1u && (uint)(r1 - 1) <= 1u && (uint)(r3 - 1) <= 1u && (uint)(r4 - 1) <= 1u;

    /// <summary>
    /// A small crisp finder's runs that the row above or below repeats pixel for pixel.
    /// A crisp finder's centre band is three rows or more of the same runs; noise does not repeat, and the comparison is what keeps it from reaching the cross-checks.
    /// </summary>
    private static bool IsRepeatedNearFinderRatio(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int r0, int r1, int r2, int r3, int r4, int endX, int y)
    {
        if (!IsSmallCrispFinderRuns(r0, r1, r2, r3, r4))
            return false;

        var start = endX - (r0 + r1 + r2 + r3 + r4);
        return RowRepeats(luminance, width, threshold, start, endX, y, y - 1) || (y + 1 < height && RowRepeats(luminance, width, threshold, start, endX, y, y + 1));

        static bool RowRepeats(ReadOnlySpan<byte> luminance, int width, byte threshold, int start, int end, int y, int otherY)
        {
            if (otherY < 0)
                return false;
            var row = luminance.Slice(y * width, width);
            var other = luminance.Slice(otherY * width, width);
            for (var x = start; x < end; x++)
            {
                if (row[x] < threshold != other[x] < threshold)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// The cross-check for a pattern too small for ratios: near misses on the row and the column, then all 49 modules read through the edges those two lines measured.
    /// </summary>
    /// <remarks>
    /// At 1 to 1.5 px/module a crisp render draws each module 1 or 2 px wide, so a run is up to a whole module off and the half-module tolerance means nothing. The edges themselves are exact, and the pattern they frame either is a finder, module for module, or is not: a stronger test than three ratios, and independent of how wide each module came out.
    /// </remarks>
    private static bool TryCrossCheckWholePattern(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, ReadOnlySpan<int> rowRuns, int endX, int y, bool referenceWalk, out float centerX, out float centerY, out int refinedTotal)
    {
        centerX = centerY = 0f;
        refinedTotal = rowRuns[0] + rowRuns[1] + rowRuns[2] + rowRuns[3] + rowRuns[4];
        if (!IsSmallCrispFinderRuns(rowRuns[0], rowRuns[1], rowRuns[2], rowRuns[3], rowRuns[4]))
            return false;

        var rowCenterX = endX - rowRuns[4] - rowRuns[3] - rowRuns[2] / 2f;
        Span<int> columnRuns = stackalloc int[5];
        centerY = CrossCheck(luminance, width, height, threshold, default, (int)rowCenterX, y, vertical: true, refinedTotal, referenceWalk, out _, columnRuns);
        if (float.IsNaN(centerY))
            return false;

        // The row through the column's centre, so both lines cross the centre square
        Span<int> centerRowRuns = stackalloc int[5];
        centerX = CrossCheck(luminance, width, height, threshold, default, (int)rowCenterX, (int)centerY, vertical: false, refinedTotal, referenceWalk, out refinedTotal, centerRowRuns);
        if (float.IsNaN(centerX))
            return false;

        var left = (int)(centerX + centerRowRuns[2] / 2f) - centerRowRuns[2] - centerRowRuns[1] - centerRowRuns[0];
        var top = (int)(centerY + columnRuns[2] / 2f) - columnRuns[2] - columnRuns[1] - columnRuns[0];
        Span<int> xs = stackalloc int[7];
        Span<int> ys = stackalloc int[7];
        ModuleSamples(centerRowRuns, left, xs);
        ModuleSamples(columnRuns, top, ys);
        for (var row = 0; row < 7; row++)
        {
            for (var column = 0; column < 7; column++)
            {
                var ring = Math.Min(Math.Min(row, column), Math.Min(6 - row, 6 - column));
                if (IsDark(luminance, width, xs[column], ys[row], threshold) != (ring != 1))
                    return false;
            }
        }
        return true;

        // One pixel inside each of the seven modules a line crosses: the four single-module runs, and the centre run in thirds
        static void ModuleSamples(ReadOnlySpan<int> runs, int start, Span<int> samples)
        {
            var edge1 = start + runs[0];
            var edge2 = edge1 + runs[1];
            var edge5 = edge2 + runs[2];
            var edge6 = edge5 + runs[3];
            samples[0] = start + runs[0] / 2;
            samples[1] = edge1 + runs[1] / 2;
            samples[2] = edge2 + runs[2] / 6;
            samples[3] = edge2 + runs[2] / 2;
            samples[4] = edge2 + runs[2] * 5 / 6;
            samples[5] = edge5 + runs[3] / 2;
            samples[6] = edge6 + runs[4] / 2;
        }
    }

    /// <summary>
    /// Walks outwards from a supposed center along one axis and re-validates the 1:1:3:1:1 ratio.
    /// Returns the refined center coordinate on that axis, or NaN.
    /// With <paramref name="nearMissRuns"/> the line only has to read as a small crisp finder's, and its runs are handed back for the whole-pattern check.
    /// </summary>
    internal static float CrossCheck(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, int centerX, int centerY, bool vertical, int expectedTotal, bool referenceWalk, out int total, Span<int> nearMissRuns)
    {
        total = 0;

        // The walk gives up where the verdict below is already certain to refuse, in either mode
        Span<int> runs = stackalloc int[5];
        int end;
        var measured = referenceWalk
            ? MeasureAxisRunsReference(luminance, width, height, threshold, centerX, centerY, vertical, expectedTotal, runs, out end)
            : MeasureRunsBounded(luminance, width, height, threshold, centerX, centerY, vertical ? 0 : 1, vertical ? 1 : 0, expectedTotal, runs, out end);
        if (!measured)
            return float.NaN;

        var i = (vertical ? centerY : centerX) + end;
        total = runs[0] + runs[1] + runs[2] + runs[3] + runs[4];

        // Reject when the cross section is wildly different from the row hit
        if (5 * Math.Abs(total - expectedTotal) >= 2 * expectedTotal)
            return float.NaN;

        if (!nearMissRuns.IsEmpty)
        {
            if (!IsSmallCrispFinderRuns(runs[0], runs[1], runs[2], runs[3], runs[4]))
                return float.NaN;
            runs.CopyTo(nearMissRuns);
        }
        else if (!IsFinderRatio(runs) && !IsFinderRatioByCoverage(luminance, width, height, grey, runs, vertical ? centerX : i, vertical ? i : centerY, vertical ? 0 : 1, vertical ? 1 : 0))
        {
            return float.NaN;
        }

        return i - runs[4] - runs[3] - runs[2] / 2f;
    }

    /// <summary>
    /// Validates the 1:1:3:1:1 ratio along the top-left → bottom-right diagonal, killing false positives that pass both axis checks (e.g. dense data areas).
    /// </summary>
    /// <remarks>
    /// This one takes the second look too, because it is also what reads a real finder's diagonal once the edges are grey, but it is the weakest place to take it: it is reached only after both axes have accepted, so re-measuring can only turn a refusal into an acceptance, and a 45° walk crosses module corners, where a pixel's darkness is not the position of a single edge.
    /// Below 2.25 px/module that is enough to admit a cross whose axes read 1:1:3:1:1 and whose diagonal does not. Measuring whole pixels here instead removes that class only below 2.05, where this is the route it comes in by, and costs real finders their decode at 2 px/module, so the repair is the corner model rather than the second look.
    /// </remarks>
    private static bool CrossCheckDiagonal(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, int centerX, int centerY, bool referenceWalk)
    {
        Span<int> runs = stackalloc int[5];
        int i;
        var measured = referenceWalk
            ? MeasureDiagonalRunsReference(luminance, width, height, threshold, centerX, centerY, runs, out i)
            : MeasureRuns(luminance, width, height, threshold, centerX, centerY, 1, 1, NoRunCap, runs, out i);
        if (!measured)
            return false;

        return IsFinderRatio(runs) || IsFinderRatioByCoverage(luminance, width, height, grey, runs, centerX + i, centerY + i, 1, 1);
    }

    private static bool IsDark(ReadOnlySpan<byte> luminance, int width, int x, int y, byte threshold)
        => luminance[y * width + x] < threshold;

    /// <summary>
    /// Scores within this of the best are a tie, broken toward the triple confirmed on more rows.
    /// Wide enough for keystone and pixel noise, where a false candidate at the fourth corner of the square scored within 0.01 of the real triple (the four corners of a square make four right isosceles triangles), and far below the 0.33 a false candidate off the corners scores.
    /// </summary>
    private const float SelectionTieTolerance = 0.05f;

    /// <summary>
    /// Picks the three candidates that best form a finder triple: closest to a right isosceles triangle with the most consistent module size, preferring repeatedly confirmed ones on a tie.
    /// </summary>
    /// <remarks>
    /// Module size alone cannot decide: a render at a whole number of pixels per module measures every candidate, false ones included, at exactly the same size.
    /// </remarks>
    internal static bool TrySelectBestThree(Span<FinderPattern> candidates, Span<FinderPattern> patterns)
        => TrySelectBestThree(candidates, patterns, out _);

    /// <summary>
    /// <paramref name="unconfirmedLeftOut"/> is set when candidates seen on one row were dropped and the triple selected without them scores past <see cref="DoubtfulTripleScore"/>.
    /// Under a row stride the counts are undercounts, so that is when the dropped ones deserve their rows.
    /// </summary>
    internal static bool TrySelectBestThree(Span<FinderPattern> candidates, Span<FinderPattern> patterns, out bool unconfirmedLeftOut)
    {
        unconfirmedLeftOut = false;

        // Confirmed candidates (seen in multiple rows) are far more trustworthy
        var confirmed = 0;
        for (var i = 0; i < candidates.Length; i++)
        {
            if (candidates[i].Count >= 2)
                confirmed++;
        }

        // Compact to the confirmed subset when it is large enough to choose from
        var compacted = confirmed >= 3 && confirmed < candidates.Length;
        if (compacted)
        {
            var w = 0;
            for (var i = 0; i < candidates.Length; i++)
            {
                if (candidates[i].Count >= 2)
                    candidates[w++] = candidates[i];
            }
            candidates = candidates.Slice(0, w);
        }

        if (!TrySelectScored(candidates, patterns))
            return false;

        unconfirmedLeftOut = compacted && TripleScore(patterns) > DoubtfulTripleScore;
        return true;
    }

    /// <summary>
    /// Past this score a triple chosen with unconfirmed candidates left out is worth a full sweep.
    /// A flat symbol's triple scores up to 0.11 and one holding a false candidate 0.33 and up; a keystoned real triple scores higher still, and then pays only the sweep.
    /// </summary>
    private const float DoubtfulTripleScore = 0.25f;

    /// <summary>The selection score of one triple: distance from a right isosceles triangle plus relative module-size spread.</summary>
    private static float TripleScore(ReadOnlySpan<FinderPattern> triple)
    {
        var smallest = Math.Min(triple[0].ModuleSize, Math.Min(triple[1].ModuleSize, triple[2].ModuleSize));
        var largest = Math.Max(triple[0].ModuleSize, Math.Max(triple[1].ModuleSize, triple[2].ModuleSize));
        var skew = RightIsoscelesSkew(DistanceSquared(triple[0], triple[1]), DistanceSquared(triple[0], triple[2]), DistanceSquared(triple[1], triple[2]));
        return float.IsNaN(skew) || !(smallest > 0f) ? float.MaxValue : skew + (largest - smallest) / smallest;
    }

    private static bool TrySelectScored(Span<FinderPattern> candidates, Span<FinderPattern> patterns)
    {
        if (candidates.Length < 3)
            return false;

        if (candidates.Length == 3)
        {
            candidates.CopyTo(patterns);
            return true;
        }

        // More than 3: score every triple. Sorted by module size, the size term only grows along j and k, so it bounds the loops without rejecting anything.
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

        // Pass 1 finds the best score; pass 2 takes the most confirmed triple within the tolerance of it.
        // One pass with a running tie rule could chain small differences and drift past the tolerance.
        int bestI = 0, bestJ = 1, bestK = 2;
        var bestScore = float.MaxValue;
        for (var pass = 0; pass < 2; pass++)
        {
            var bound = pass == 0 ? float.MaxValue : bestScore + SelectionTieTolerance;
            var bestCount = 0;
            var bestTieScore = float.MaxValue;
            for (var i = 0; i < candidates.Length - 2; i++)
            {
                ref readonly var a = ref candidates[i];
                for (var j = i + 1; j < candidates.Length - 1; j++)
                {
                    if ((candidates[j].ModuleSize - a.ModuleSize) / a.ModuleSize > Math.Min(bound, bestScore + SelectionTieTolerance))
                        break;

                    ref readonly var b = ref candidates[j];
                    var ab = DistanceSquared(a, b);
                    for (var k = j + 1; k < candidates.Length; k++)
                    {
                        ref readonly var c = ref candidates[k];
                        var sizeSpread = (c.ModuleSize - a.ModuleSize) / a.ModuleSize;
                        if (sizeSpread > Math.Min(bound, bestScore + SelectionTieTolerance))
                            break;

                        var skew = RightIsoscelesSkew(ab, DistanceSquared(a, c), DistanceSquared(b, c));
                        if (float.IsNaN(skew))
                            continue;

                        var score = skew + sizeSpread;
                        if (pass == 0)
                        {
                            if (score < bestScore)
                            {
                                bestScore = score;
                                bestI = i;
                                bestJ = j;
                                bestK = k;
                            }
                            continue;
                        }

                        var count = a.Count + b.Count + c.Count;
                        if (score <= bound && (count > bestCount || (count == bestCount && score < bestTieScore)))
                        {
                            bestCount = count;
                            bestTieScore = score;
                            bestI = i;
                            bestJ = j;
                            bestK = k;
                        }
                    }
                }
            }
        }

        patterns[0] = candidates[bestI];
        patterns[1] = candidates[bestJ];
        patterns[2] = candidates[bestK];
        return true;
    }

    /// <summary>
    /// How far three points are from a right isosceles triangle, from their squared side lengths: 0 when the two short sides are equal and meet at a right angle, unchanged by rotation, mirroring and scale.
    /// NaN for coincident points.
    /// </summary>
    private static float RightIsoscelesSkew(float d0, float d1, float d2)
    {
        // Order so that shortest <= middle <= longest
        var longest = Math.Max(d0, Math.Max(d1, d2));
        var shortest = Math.Min(d0, Math.Min(d1, d2));
        var middle = d0 + d1 + d2 - longest - shortest;
        if (!(longest > 0f))
            return float.NaN;

        return (Math.Abs(longest - 2f * shortest) + Math.Abs(longest - 2f * middle)) / longest;
    }

    private static float DistanceSquared(in FinderPattern a, in FinderPattern b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
