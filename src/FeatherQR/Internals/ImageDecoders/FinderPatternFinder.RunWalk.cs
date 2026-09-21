using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// The run measurement of the cross-checks: from a supposed centre outwards along one line, the five runs of a 1:1:3:1:1 cross section and where the walk ended.
/// The verdict on the runs stays with the cross-checks; what is here is only the walk, which is where a refused cross-check spends its time, and 97 to 98 % of cross-checks on a large symbol are refusals.
/// </summary>
internal static partial class FinderPatternFinder
{
    /// <summary>Cap for a walk whose side runs are not capped: the diagonal's.</summary>
    internal const int NoRunCap = int.MaxValue;

    /// <summary>
    /// Measures the five runs through (<paramref name="centerX"/>, <paramref name="centerY"/>) along (<paramref name="stepX"/>, <paramref name="stepY"/>), which is (0, 1), (1, 0) or (1, 1).
    /// </summary>
    /// <param name="luminance">Grayscale pixels, row-major, width × height bytes.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="threshold">A pixel is dark when luminance &lt; threshold.</param>
    /// <param name="centerX">Centre column, inside the image.</param>
    /// <param name="centerY">Centre row, inside the image.</param>
    /// <param name="stepX">Column step of the line.</param>
    /// <param name="stepY">Row step of the line.</param>
    /// <param name="cap">A side run stops growing once it is longer than this.</param>
    /// <param name="runs">Receives the five runs, first to last along the line. All five are written on success.</param>
    /// <param name="end">Steps from the centre to the first pixel after the last run.</param>
    /// <returns>False when the centre run reaches the image border on either side, which no cross-check accepts.</returns>
    /// <remarks>
    /// One walk for the three lines: they differ only in the distance between two pixels of the line and in how many pixels lie before and after the centre, so both are worked out once and the loops test a count, not coordinates.
    /// The pixels read and their order are the reference's. The offset moves as an integer and is turned into a reference only after the count has said the pixel exists, so no reference is ever formed outside the image.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool MeasureRuns(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int centerX, int centerY, int stepX, int stepY, int cap, Span<int> runs, out int end)
    {
        Debug.Assert((stepX | stepY) == 1 && (stepX & ~1) == 0 && (stepY & ~1) == 0, "The line is a row, a column or the falling diagonal.");

        // The only checks of the walk: everything after this indexes unchecked
        if ((uint)centerX >= (uint)width || (uint)centerY >= (uint)height || (long)width * height > luminance.Length)
            ThrowCentreOutsideImage(centerX, centerY, width, height, luminance.Length);

        end = 0;
        var before = stepX == 0 ? centerY : stepY == 0 ? centerX : Math.Min(centerX, centerY);
        var after = stepX == 0 ? height - 1 - centerY : stepY == 0 ? width - 1 - centerX : Math.Min(width - 1 - centerX, height - 1 - centerY);
        var step = (nint)stepY * width + stepX;
        var centre = (nint)centerY * width + centerX;
        ref var origin = ref MemoryMarshal.GetReference(luminance);

        int r0 = 0, r1 = 0, r2 = 0, r3 = 0, r4 = 0;

        // Back from the centre, the centre pixel included: the dark centre run, then a light and a dark run
        var at = centre;
        var left = before + 1;
        while (left > 0 && Unsafe.Add(ref origin, at) < threshold)
        {
            r2++;
            left--;
            at -= step;
        }
        if (left == 0)
            return false;

        while (left > 0 && r1 <= cap && Unsafe.Add(ref origin, at) >= threshold)
        {
            r1++;
            left--;
            at -= step;
        }
        while (left > 0 && r0 <= cap && Unsafe.Add(ref origin, at) < threshold)
        {
            r0++;
            left--;
            at -= step;
        }

        // Forward from the pixel after the centre
        at = centre + step;
        left = after;
        while (left > 0 && Unsafe.Add(ref origin, at) < threshold)
        {
            r2++;
            left--;
            at += step;
        }
        if (left == 0)
            return false;

        while (left > 0 && r3 <= cap && Unsafe.Add(ref origin, at) >= threshold)
        {
            r3++;
            left--;
            at += step;
        }
        while (left > 0 && r4 <= cap && Unsafe.Add(ref origin, at) < threshold)
        {
            r4++;
            left--;
            at += step;
        }

        runs[4] = r4;
        runs[3] = r3;
        runs[2] = r2;
        runs[1] = r1;
        runs[0] = r0;
        end = after - left + 1;
        return true;
    }

    private static void ThrowCentreOutsideImage(int centerX, int centerY, int width, int height, int length)
        => throw new ArgumentOutOfRangeException(nameof(centerX), $"Centre ({centerX}, {centerY}) is outside the {width} x {height} image, or the image is larger than its {length} pixel buffer");

    /// <summary>
    /// The axis walk as it was inside the cross-check, kept as the reference <see cref="MeasureRuns"/> is held to: one loop for both axes, the axis chosen per pixel, every pixel a checked index.
    /// Off the decode path except through <see cref="TryFindScalar"/>.
    /// </summary>
    internal static bool MeasureAxisRunsReference(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int centerX, int centerY, bool vertical, int expectedTotal, Span<int> runsOut, out int end)
    {
        end = 0;
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
            return false;

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
            return false;

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

        runs.CopyTo(runsOut);
        end = i - center;
        return true;
    }

    /// <summary>
    /// The top-left to bottom-right walk as it was inside the diagonal cross-check, kept as the reference <see cref="MeasureRuns"/> is held to. Its side runs have no cap.
    /// </summary>
    internal static bool MeasureDiagonalRunsReference(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int centerX, int centerY, Span<int> runsOut, out int end)
    {
        end = 0;
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

        runs.CopyTo(runsOut);
        end = i;
        return true;
    }
}
