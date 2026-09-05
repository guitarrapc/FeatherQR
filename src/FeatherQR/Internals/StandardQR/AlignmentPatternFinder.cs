#if NET8_0_OR_GREATER
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
#endif

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Locates the bottom-right alignment pattern (5×5: dark ring, light ring, dark
/// center) near its predicted position, providing the fourth correspondence point
/// for perspective sampling.
/// </summary>
/// <remarks>
/// The scan matches the light-dark-light run triple through the pattern center
/// (inner ring, center module, inner ring, one module each). Unlike the outer dark
/// ring, those three runs are fully owned by the pattern: adjacent dark data
/// modules can merge with the border ring and stretch its runs, but never touch the
/// inner ones. Candidates are cross-checked vertically with the same signature.
/// The search stays inside a small window around the prediction, 1-module runs
/// are everywhere in QR data, so an unconstrained search would drown in false
/// positives. Version 1 symbols have no alignment pattern and callers fall back to
/// the parallelogram corner estimate.
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
    /// <returns>True when a cross-checked alignment pattern was found in the window.</returns>
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

        // Scan rows outward from the middle of the window: the pattern is most
        // likely at the prediction, so the first cross-checked hit wins.
        // Row stride: the pattern's center dark run is ~1 module tall, so scanning
        // every ⌊moduleSize/2⌋-th row cannot miss it, and the vertical cross-check
        // recenters exactly regardless of which row inside the run was hit
        // (measured 4x on the not-found sweep; see the AlignmentFind findings log).
        var step = Math.Max(1, (int)(moduleSize / 2f));
        var midY = (minY + maxY) / 2;
        for (var offset = 0; midY + offset <= maxY || midY - offset >= minY; offset += step)
        {
            if (midY + offset <= maxY
                && TryScanRow(luminance, width, height, threshold, midY + offset, minX, maxX, moduleSize, axisX, axisY, forceScalar, ref centerX, ref centerY))
            {
                return true;
            }
            if (offset != 0 && midY - offset >= minY
                && TryScanRow(luminance, width, height, threshold, midY - offset, minX, maxX, moduleSize, axisX, axisY, forceScalar, ref centerX, ref centerY))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryScanRow(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int y, int minX, int maxX, float moduleSize, (float X, float Y) axisX, (float X, float Y) axisY, bool forceScalar, ref float centerX, ref float centerY)
    {
#if NET8_0_OR_GREATER
        // SIMD path: classify pixels into a dark bitmask with vector compares
        // (32 per AVX2 compare, 64 per NEON fold, 16 per 128-bit compare), then
        // walk RUNS via tzcnt instead of pixels (measured 6.4x on the x64
        // not-found sweep). Vector256 acceleration implies Vector128, so one
        // gate covers x64, ARM64 and WASM SIMD.
        if (!forceScalar && Vector128.IsHardwareAccelerated && maxX - minX + 1 >= 16)
        {
            return TryScanRowMask(luminance, width, height, threshold, y, minX, maxX, moduleSize, axisX, axisY, ref centerX, ref centerY);
        }
#endif
        _ = forceScalar;
        return TryScanRowScalar(luminance, width, height, threshold, y, minX, maxX, moduleSize, axisX, axisY, ref centerX, ref centerY);
    }

    private static bool TryScanRowScalar(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int y, int minX, int maxX, float moduleSize, (float X, float Y) axisX, (float X, float Y) axisY, ref float centerX, ref float centerY)
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
            if (IsAlignmentRatio(runs, moduleSize))
            {
                // Candidate center = middle of the dark run
                var candidateX = x - runs[2] - runs[1] / 2f;
                if (TryCrossCheck(luminance, width, height, threshold, candidateX, y, moduleSize, axisX, axisY, out centerX, out centerY))
                    return true;
            }

            // Slide: keep [dark, light] as the new [?, light]... the window must
            // start with a light run, so the completed dark+light become runs 1-2
            // shifted down by one color pair.
            runs[0] = runs[2];
            runs[1] = 1; // the incoming dark run
            runs[2] = 0;
            runIndex = 1;
        }

        return false;
    }

#if NET8_0_OR_GREATER
    /// <summary>Per-byte bit weights [1,2,4,...,128] repeated: dark byte i contributes bit (i mod 8) of its half.</summary>
    private static readonly Vector128<byte> NeonBitWeights = Vector128.Create(
        (byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);

    /// <summary>
    /// Mask-based row scan: vector compares (32 px AVX2, 64 px NEON fold, 16 px
    /// otherwise) produce a dark bitmask; runs are walked via trailing-zero
    /// counts, evaluating the same (light, dark, light) triple at every
    /// light→dark transition as the scalar walk.
    /// </summary>
    private static bool TryScanRowMask(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int y, int minX, int maxX, float moduleSize, (float X, float Y) axisX, (float X, float Y) axisY, ref float centerX, ref float centerY)
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
                && IsAlignmentRatio(previousGap, previousDarkLength, gap, moduleSize))
            {
                var x = minX + darkStart;
                var candidateX = x - gap - previousDarkLength / 2f;
                if (TryCrossCheck(luminance, width, height, threshold, candidateX, y, moduleSize, axisX, axisY, out centerX, out centerY))
                    return true;
            }

            previousGap = gap;
            previousDarkLength = darkEnd - darkStart;
            lightStart = darkEnd;
            pos = darkEnd;
        }

        return false;
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

    private static bool IsAlignmentRatio(int lightBefore, int dark, int lightAfter, float moduleSize)
    {
        var maxVariance = moduleSize / 2f;
        return Math.Abs(lightBefore - moduleSize) < maxVariance
            && Math.Abs(dark - moduleSize) < maxVariance
            && Math.Abs(lightAfter - moduleSize) < maxVariance;
    }

    /// <summary>
    /// Each of the three runs (light, dark, light) must be within 50% of one module.
    /// </summary>
    private static bool IsAlignmentRatio(ReadOnlySpan<int> runs, float moduleSize)
    {
        var maxVariance = moduleSize / 2f;
        return Math.Abs(runs[0] - moduleSize) < maxVariance
            && Math.Abs(runs[1] - moduleSize) < maxVariance
            && Math.Abs(runs[2] - moduleSize) < maxVariance;
    }

    /// <summary>
    /// Confirms the light-dark-light signature vertically through the candidate
    /// center and refines the center's y coordinate.
    /// </summary>
    private static bool TryCrossCheck(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float candidateX, int candidateY, float moduleSize, (float X, float Y) axisX, (float X, float Y) axisY, out float centerX, out float centerY)
    {
        centerX = 0;
        centerY = 0;

        var x = (int)(candidateX + 0.5f);
        if (x < 0 || x >= width)
            return false;

        var limit = (int)(moduleSize * 2f) + 1;

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

        // The vertical dark run must also be ~1 module, and the light rings ~1 module
        var vertical = up + down;
        var maxVariance = moduleSize; // pixel quantization + mild perspective tolerance
        if (Math.Abs(vertical - moduleSize) >= maxVariance)
            return false;
        if (Math.Abs(Math.Min(lightUp, lightDown) - moduleSize) >= maxVariance)
            return false;

        var refinedY = candidateY + (down - up) / 2f + 0.5f;

        // Border-ring check: the light-dark-light core signature also matches any
        // isolated dark data module (light on all four sides), extremely common in
        // data areas, and a false positive here shears the whole sampling transform.
        // Only the real pattern has its 5×5 dark border: all eight ring samples at
        // ±2 modules from the center must be dark.
        if (!IsRingDark(luminance, width, height, threshold, candidateX, refinedY, axisX, axisY))
            return false;

        centerX = candidateX;
        centerY = refinedY;
        return true;
    }

    private static bool IsRingDark(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float centerX, float centerY, (float X, float Y) axisX, (float X, float Y) axisY)
    {
        // Ring samples follow the GRID axes (from the finder geometry), not the
        // image axes: under rotation, image-axis offsets at ±2 modules land outside
        // the rotated 5×5 ring and reject the true pattern.
        Span<float> steps = stackalloc float[3] { -2f, 0f, 2f };

        foreach (var stepY in steps)
        {
            foreach (var stepX in steps)
            {
                if (stepX == 0f && stepY == 0f)
                    continue; // center already validated

                var x = (int)(centerX + stepX * axisX.X + stepY * axisY.X + 0.5f);
                var y = (int)(centerY + stepX * axisX.Y + stepY * axisY.Y + 0.5f);
                if (x < 0 || x >= width || y < 0 || y >= height)
                    return false;
                if (luminance[y * width + x] >= threshold)
                    return false;
            }
        }

        return true;
    }
}
