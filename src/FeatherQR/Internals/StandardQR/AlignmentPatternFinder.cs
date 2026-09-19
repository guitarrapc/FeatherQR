#if NET8_0_OR_GREATER
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
#endif

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Locates the bottom-right alignment pattern (5×5: dark ring, light ring, dark center) near its predicted position, providing the fourth correspondence point for perspective sampling.
/// </summary>
/// <remarks>
/// The scan matches the light-dark-light run triple through the pattern center (inner ring, center module, inner ring, one module each).
/// Unlike the outer dark ring, those three runs are fully owned by the pattern: adjacent dark data modules can merge with the border ring and stretch its runs, but never touch the inner ones.
/// Candidates are cross-checked vertically with the same signature.
/// The search stays inside a small window around the prediction, 1-module runs are everywhere in QR data, so an unconstrained search would drown in false positives.
/// Version 1 symbols have no alignment pattern and callers fall back to the parallelogram corner estimate.
/// </remarks>
internal static class AlignmentPatternFinder
{
    /// <summary>
    /// Searches a window around the expected position for the alignment pattern.
    /// </summary>
    /// <param name="luminance">Grayscale pixels, row-major.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="threshold">Binarization threshold (dark = below).</param>
    /// <param name="expectedX">Predicted center x in pixels.</param>
    /// <param name="expectedY">Predicted center y in pixels.</param>
    /// <param name="moduleSize">Estimated module size in pixels.</param>
    /// <param name="axisX">Per-module pixel vector along the grid x axis (from the finder geometry).</param>
    /// <param name="axisY">Per-module pixel vector along the grid y axis.</param>
    /// <param name="allowanceModules">Search half-window in modules around the prediction.</param>
    /// <param name="centerX">Found center x.</param>
    /// <param name="centerY">Found center y.</param>
    /// <returns>True when a cross-checked alignment pattern was found in the window; the one nearest the prediction is returned.</returns>
    public static bool TryFind(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float expectedX, float expectedY, float moduleSize, (float X, float Y) axisX, (float X, float Y) axisY, float allowanceModules, out float centerX, out float centerY)
        => TryFindCore(luminance, width, height, threshold, expectedX, expectedY, moduleSize, axisX, axisY, allowanceModules, forceScalar: false, out centerX, out centerY);

    /// <summary>Scalar-scan entry for kernel parity tests; behavior-identical to <see cref="TryFind"/>.</summary>
    internal static bool TryFindScalar(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float expectedX, float expectedY, float moduleSize, (float X, float Y) axisX, (float X, float Y) axisY, float allowanceModules, out float centerX, out float centerY)
        => TryFindCore(luminance, width, height, threshold, expectedX, expectedY, moduleSize, axisX, axisY, allowanceModules, forceScalar: true, out centerX, out centerY);

    private static bool TryFindCore(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float expectedX, float expectedY, float moduleSize, (float X, float Y) axisX, (float X, float Y) axisY, float allowanceModules, bool forceScalar, out float centerX, out float centerY)
    {
        centerX = 0;
        centerY = 0;

        var allowance = allowanceModules * moduleSize;
        var minX = Math.Max(0, (int)(expectedX - allowance));
        var maxX = Math.Min(width - 1, (int)(expectedX + allowance));
        var minY = Math.Max(0, (int)(expectedY - allowance));
        var maxY = Math.Min(height - 1, (int)(expectedY + allowance));
        if (maxX - minX < 3 * moduleSize || maxY - minY < 3 * moduleSize)
            return false;

        // Scan rows outward from the middle of the window and keep the cross-checked
        // hit nearest the prediction: data can pass every check too, and the first
        // hit in scan order was measured to be a false one while the real pattern
        // sat on the prediction. A row farther than the best hit plus the cross
        // check's recentering (under a module and a pixel) cannot beat it.
        // Row stride: the pattern's center dark run is ~1 module tall, so scanning
        // every half-run-th row cannot miss it, and the vertical cross-check
        // recenters exactly regardless of which row inside the run was hit
        // (measured 4x on the not-found sweep; see the AlignmentFind findings log).
        var expected = ExpectedRuns.FromAxes(axisX, axisY, moduleSize);
        var best = new NearestHit(expectedX, expectedY);
        var step = Math.Max(1, (int)(expected.Column / 2f));
        var midY = (minY + maxY) / 2;
        for (var offset = 0; midY + offset <= maxY || midY - offset >= minY; offset += step)
        {
            if (best.Found && offset - Math.Abs(midY - expectedY) - expected.Column - 1f > best.Distance)
                break;

            if (midY + offset <= maxY)
                ScanRow(luminance, width, height, threshold, midY + offset, minX, maxX, expected, axisX, axisY, forceScalar, ref best);
            if (offset != 0 && midY - offset >= minY)
                ScanRow(luminance, width, height, threshold, midY - offset, minX, maxX, expected, axisX, axisY, forceScalar, ref best);
        }

        if (!best.Found)
            return false;

        // Each row hit centers x on its own row, and a row off the pattern's middle
        // cuts the center module short, so the nearest of them leans toward the
        // prediction. Re-center x on the dark run of the refined center row.
        centerX = RecenterX(luminance, width, height, threshold, best.X, best.Y, expected.Row);
        centerY = best.Y;
        return true;
    }

    private static float RecenterX(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float x, float y, float rowModule)
    {
        var row = (int)y;
        var column = (int)x;
        if (row < 0 || row >= height || column < 0 || column >= width || luminance[row * width + column] >= threshold)
            return x;

        var left = column;
        while (left > 0 && luminance[row * width + left - 1] < threshold)
            left--;
        var right = column;
        while (right < width - 1 && luminance[row * width + right + 1] < threshold)
            right++;

        // A run longer than two modules has merged with something: keep the row hit
        return right - left + 1 < 2f * rowModule ? (left + right + 1) / 2f : x;
    }

    /// <summary>
    /// Pixel length of one module crossed along an image row and along an image column through the pattern's center.
    /// A rotated square is crossed obliquely, up to √2 modules long at 45°, so the length comes from the grid axes rather than the module size.
    /// </summary>
    private readonly struct ExpectedRuns(float row, float column)
    {
        public float Row { get; } = row;
        public float Column { get; } = column;

        public static ExpectedRuns FromAxes((float X, float Y) axisX, (float X, float Y) axisY, float moduleSize)
        {
            // Grid step per pixel along the image x and y axes: columns of the
            // inverse of [axisX axisY]. A line through a unit cell's center stays
            // inside it for 1 / max(|gx|, |gy|) pixels.
            var determinant = axisX.X * axisY.Y - axisY.X * axisX.Y;
            if (Math.Abs(determinant) < 1e-6f)
                return new ExpectedRuns(moduleSize, moduleSize);

            var rowGx = axisY.Y / determinant;
            var rowGy = -axisX.Y / determinant;
            var columnGx = -axisY.X / determinant;
            var columnGy = axisX.X / determinant;
            return new ExpectedRuns(
                1f / Math.Max(Math.Abs(rowGx), Math.Abs(rowGy)),
                1f / Math.Max(Math.Abs(columnGx), Math.Abs(columnGy)));
        }
    }

    /// <summary>The cross-checked hit nearest the predicted center so far.</summary>
    private struct NearestHit(float expectedX, float expectedY)
    {
        public bool Found;
        public float X;
        public float Y;
        private float _distanceSquared = float.MaxValue;

        public readonly float Distance => (float)Math.Sqrt(_distanceSquared);

        public void Offer(float x, float y)
        {
            var dx = x - expectedX;
            var dy = y - expectedY;
            var distanceSquared = dx * dx + dy * dy;
            if (distanceSquared < _distanceSquared)
            {
                _distanceSquared = distanceSquared;
                X = x;
                Y = y;
                Found = true;
            }
        }
    }

    private static void ScanRow(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int y, int minX, int maxX, ExpectedRuns expected, (float X, float Y) axisX, (float X, float Y) axisY, bool forceScalar, ref NearestHit best)
    {
#if NET8_0_OR_GREATER
        // SIMD path: classify pixels into a dark bitmask with vector compares
        // (32 per AVX2 compare, 64 per NEON fold, 16 per 128-bit compare), then
        // walk RUNS via tzcnt instead of pixels (measured 6.4x on the x64
        // not-found sweep). Vector256 acceleration implies Vector128, so one
        // gate covers x64, ARM64 and WASM SIMD.
        if (!forceScalar && Vector128.IsHardwareAccelerated && maxX - minX + 1 >= 16)
        {
            ScanRowMask(luminance, width, height, threshold, y, minX, maxX, expected, axisX, axisY, ref best);
            return;
        }
#endif
        _ = forceScalar;
        ScanRowScalar(luminance, width, height, threshold, y, minX, maxX, expected, axisX, axisY, ref best);
    }

    private static void ScanRowScalar(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int y, int minX, int maxX, ExpectedRuns expected, (float X, float Y) axisX, (float X, float Y) axisY, ref NearestHit best)
    {
        // Track the last three completed runs as [light, dark, light]; a window is
        // evaluated whenever a light run completes (light → dark transition).
        Span<int> runs = stackalloc int[3];
        runs.Clear();
        var runIndex = -1; // -1: waiting for the first light run
        var currentDark = false;

        for (var x = minX; x <= maxX + 1; x++)
        {
            // One virtual pixel past the window terminates a trailing light run
            var dark = x <= maxX && luminance[y * width + x] < threshold;

            if (runIndex == -1)
            {
                if (!dark)
                {
                    runIndex = 0;
                    runs[0] = 1;
                    currentDark = false;
                }
                continue;
            }

            if (dark == currentDark)
            {
                runs[runIndex]++;
                continue;
            }

            currentDark = dark;
            if (runIndex < 2)
            {
                runIndex++;
                runs[runIndex] = 1;
                continue;
            }

            // A window [light, dark, light] just completed (transition to dark)
            if (IsAlignmentRatio(runs, expected.Row))
            {
                // Candidate center = middle of the dark run
                var candidateX = x - runs[2] - runs[1] / 2f;
                if (TryCrossCheck(luminance, width, height, threshold, candidateX, y, expected.Column, axisX, axisY, out var centerX, out var centerY))
                    best.Offer(centerX, centerY);
            }

            // Slide: keep [dark, light] as the new [?, light]... the window must
            // start with a light run, so the completed dark+light become runs 1-2
            // shifted down by one color pair.
            runs[0] = runs[2];
            runs[1] = 1; // the incoming dark run
            runs[2] = 0;
            runIndex = 1;
        }
    }

#if NET8_0_OR_GREATER
    /// <summary>Per-byte bit weights [1,2,4,...,128] repeated: dark byte i contributes bit (i mod 8) of its half.</summary>
    private static readonly Vector128<byte> NeonBitWeights = Vector128.Create(
        (byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);

    /// <summary>
    /// Mask-based row scan: vector compares (32 px AVX2, 64 px NEON fold, 16 px otherwise) produce a dark bitmask; runs are walked via trailing-zero counts, evaluating the same (light, dark, light) triple at every light→dark transition as the scalar walk.
    /// </summary>
    private static void ScanRowMask(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int y, int minX, int maxX, ExpectedRuns expected, (float X, float Y) axisX, (float X, float Y) axisY, ref NearestHit best)
    {
        var length = maxX - minX + 1;
        Span<ulong> mask = stackalloc ulong[((length + 63) >> 6) + 1];
        mask.Clear();

        // Build the dark bitmask (bit i = pixel minX+i is dark);
        // threshold == 0 means nothing is dark, so the compare loops can skip.
        var row = luminance.Slice(y * width + minX, length);
        var i = 0;
        if (threshold > 0)
        {
            ref var rowRef = ref MemoryMarshal.GetReference(row);
            if (Vector256.IsHardwareAccelerated && length >= 32)
            {
                // x64 has no unsigned byte compare: unsigned v < t ⟺ min(v, t-1) == v
                var thresholdMinus1 = Vector256.Create((byte)(threshold - 1));
                for (; i + 32 <= length; i += 32)
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
                    // word instead: 4 compares select per-byte bit weights, then a
                    // chain of pairwise adds reduces them (simdjson bulk-movemask shape).
                    for (; i + 64 <= length; i += 64)
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
                for (; i + 16 <= length; i += 16)
                {
                    var dark = Vector128.LessThan(Vector128.LoadUnsafe(ref rowRef, (nuint)i), thr);
                    mask[i >> 6] |= (ulong)dark.ExtractMostSignificantBits() << (i & 63);
                }
            }
        }
        for (; i < length; i++)
        {
            if (row[i] < threshold)
                mask[i >> 6] |= 1ul << (i & 63);
        }

        // Walk dark runs; a leading dark run is skipped (the scalar walk waits for
        // the first light pixel before opening a window).
        var pos = NextBit(mask, 0, length, set: false);
        var lightStart = pos;
        var previousDarkLength = 0;
        var previousGap = 0;

        while (true)
        {
            var darkStart = NextBit(mask, pos, length, set: true);
            if (darkStart >= length)
                break;
            var darkEnd = NextBit(mask, darkStart, length, set: false);

            var gap = darkStart - lightStart; // light run before this dark run
            if (previousDarkLength > 0
                && IsAlignmentRatio(previousGap, previousDarkLength, gap, expected.Row))
            {
                var x = minX + darkStart;
                var candidateX = x - gap - previousDarkLength / 2f;
                if (TryCrossCheck(luminance, width, height, threshold, candidateX, y, expected.Column, axisX, axisY, out var centerX, out var centerY))
                    best.Offer(centerX, centerY);
            }

            previousGap = gap;
            previousDarkLength = darkEnd - darkStart;
            lightStart = darkEnd;
            pos = darkEnd;
        }
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
    /// Each light run plus the dark run must be within 50% of two modules, and no run longer than two.
    /// </summary>
    /// <remarks>
    /// A light and a dark run together span two edges of the same polarity, which grey edge pixels on one side of the threshold shift together, so the pair keeps its length where each run on its own gains or loses a pixel: at 3 px/module and 45° a light run measured 2 px against an expected 4.2.
    /// </remarks>
    private static bool IsAlignmentRatio(int lightBefore, int dark, int lightAfter, float run)
    {
        var pair = 2f * run;
        return Math.Abs(lightBefore + dark - pair) < run
            && Math.Abs(dark + lightAfter - pair) < run
            && lightBefore < pair && dark < pair && lightAfter < pair;
    }

    private static bool IsAlignmentRatio(ReadOnlySpan<int> runs, float run)
        => IsAlignmentRatio(runs[0], runs[1], runs[2], run);

    /// <summary>
    /// Confirms the light-dark-light signature vertically through the candidate center and refines the center's y coordinate.
    /// </summary>
    private static bool TryCrossCheck(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float candidateX, int candidateY, float columnModule, (float X, float Y) axisX, (float X, float Y) axisY, out float centerX, out float centerY)
    {
        centerX = 0;
        centerY = 0;

        var x = (int)candidateX;
        if (x < 0 || x >= width)
            return false;

        var limit = (int)(columnModule * 2f) + 1;

        // Middle dark run, walking up then down from the candidate row
        var up = 0;
        var y = candidateY;
        while (y >= 0 && up <= limit && luminance[y * width + x] < threshold)
        {
            up++;
            y--;
        }
        if (y < 0 || up == 0 || up > limit)
            return false;
        var lightUp = 0;
        while (y >= 0 && lightUp <= limit && luminance[y * width + x] >= threshold)
        {
            lightUp++;
            y--;
        }
        if (lightUp == 0)
            return false;

        var down = 0;
        y = candidateY + 1;
        while (y < height && down <= limit && luminance[y * width + x] < threshold)
        {
            down++;
            y++;
        }
        if (y >= height || down > limit)
            return false;
        var lightDown = 0;
        while (y < height && lightDown <= limit && luminance[y * width + x] >= threshold)
        {
            lightDown++;
            y++;
        }
        if (lightDown == 0)
            return false;

        // Vertically too, each light ring plus the dark center must span ~2 modules;
        // one module of tolerance covers pixel quantization and mild perspective.
        var vertical = up + down;
        var pair = 2f * columnModule;
        if (Math.Abs(lightUp + vertical - pair) >= columnModule || Math.Abs(vertical + lightDown - pair) >= columnModule)
            return false;

        var refinedY = candidateY + (down - up) / 2f + 1f;

        // Ring check: the light-dark-light core signature also matches any
        // isolated dark data module (light on all four sides), extremely common in
        // data areas, and a false positive here shears the whole sampling transform.
        // Only the real pattern has both rings around its center: the eight samples
        // at ±1 module light and the eight at ±2 modules dark.
        if (!IsRingPattern(luminance, width, height, threshold, candidateX, refinedY, axisX, axisY))
            return false;

        centerX = candidateX;
        centerY = refinedY;
        return true;
    }

    private static bool IsRingPattern(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float centerX, float centerY, (float X, float Y) axisX, (float X, float Y) axisY)
        => IsRing(luminance, width, height, threshold, centerX, centerY, axisX, axisY, 1f, dark: false)
            && IsRing(luminance, width, height, threshold, centerX, centerY, axisX, axisY, 2f, dark: true);

    private static bool IsRing(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float centerX, float centerY, (float X, float Y) axisX, (float X, float Y) axisY, float distance, bool dark)
    {
        // Ring samples follow the GRID axes (from the finder geometry), not the
        // image axes: under rotation, image-axis offsets land outside the rotated
        // rings and reject the true pattern.
        for (var stepY = -1; stepY <= 1; stepY++)
        {
            for (var stepX = -1; stepX <= 1; stepX++)
            {
                if (stepX == 0 && stepY == 0)
                    continue; // center already validated

                var x = (int)(centerX + distance * (stepX * axisX.X + stepY * axisY.X));
                var y = (int)(centerY + distance * (stepX * axisX.Y + stepY * axisY.Y));
                if (x < 0 || x >= width || y < 0 || y >= height)
                    return false;
                if (luminance[y * width + x] < threshold != dark)
                    return false;
            }
        }

        return true;
    }
}
