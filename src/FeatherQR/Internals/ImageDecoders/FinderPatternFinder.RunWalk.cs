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
    /// Measures the five runs through (<paramref name="centerX"/>, <paramref name="centerY"/>) along (<paramref name="stepX"/>, <paramref name="stepY"/>), which is (0, 1), (1, 0), (1, 1) or (1, −1).
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
    /// One walk for the four lines: they differ only in the distance between two pixels of the line and in how many pixels lie before and after the centre, so both are worked out once and the loops test a count, not coordinates.
    /// The pixels read and their order are the reference's. The offset moves as an integer and is turned into a reference only after the count has said the pixel exists, so no reference is ever formed outside the image.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool MeasureRuns(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int centerX, int centerY, int stepX, int stepY, int cap, Span<int> runs, out int end)
    {
        Debug.Assert((stepX == 0 && stepY == 1) || (stepX == 1 && (stepY == 0 || stepY == 1 || stepY == -1)), "The line is a row, a column or a diagonal.");

        // The only checks of the walk: everything after this indexes unchecked
        if ((uint)centerX >= (uint)width || (uint)centerY >= (uint)height || (long)width * height > luminance.Length)
            ThrowCentreOutsideImage(centerX, centerY, width, height, luminance.Length);

        end = 0;
        var before = stepX == 0 ? centerY : stepY == 0 ? centerX : stepY > 0 ? Math.Min(centerX, centerY) : Math.Min(centerX, height - 1 - centerY);
        var after = stepX == 0 ? height - 1 - centerY : stepY == 0 ? width - 1 - centerX : stepY > 0 ? Math.Min(width - 1 - centerX, height - 1 - centerY) : Math.Min(width - 1 - centerX, centerY);
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

    /// <summary>
    /// Whether an axis cross-check's run total is close enough to the total of the row it checks.
    /// </summary>
    /// <remarks>
    /// Each line has passed its own 1:1:3:1:1 by then, so this is only how the two lines may differ in scale, and that depends on which line it is.
    /// The row again (<paramref name="acrossAxes"/> false) is the row scan's own line through a refined centre: the same cross section, within 40 %.
    /// The column is the other axis, and a symbol in perspective draws a finder longer along one axis than the other: at keystone k the finder at the wide edge is 1/(1 − k) times taller than wide in the symbol's own axes, and turning it only brings the two totals closer, so a column is at most that factor from the row, 2 at 50 %.
    /// The window is 5/12 to 12/5, that factor and a fifth on top for whole-pixel runs; zxing-cpp allows 5 between any two of its four lines and walks up to 4 times the row, with no early stop.
    /// </remarks>
    internal static bool IsTotalInWindow(int total, int expectedTotal, bool acrossAxes)
        => acrossAxes
            ? 12 * total > 5 * expectedTotal && 5 * total < 12 * expectedTotal
            : 5 * Math.Abs(total - expectedTotal) < 2 * expectedTotal;

    /// <summary>
    /// What the runs of a cross section an axis cross-check could still accept are bounded by, given the total it expects.
    /// </summary>
    /// <remarks>
    /// Every bound is a consequence of the cross-check's own accept conditions and of nothing else: the total inside the window of <see cref="IsTotalInWindow"/>, and either the strict ratio (each side run within half a module of one module, the centre within a module and a half of three) or the near-miss one (each run within ten sevenths of a pixel), which is also the only door to the grey second look, or, in the mode that hands its runs back, the small crisp runs, which turn out to lie inside the same bounds.
    /// The strict ratio puts every side run below the centre run, and the near-miss one ties it to a third of the centre, so the bound on a side run is known as soon as the centre has been measured.
    /// A walk that stops at these bounds can therefore only stop where the verdict would have been a refusal. <c>FinderRunBoundsTest</c> holds each bound against the ratio checks themselves.
    /// </remarks>
    internal readonly struct AxisRunBounds
    {
        /// <summary>The smallest total either ratio check accepts.</summary>
        private const int MinTotal = 7;

        private readonly int _totalHigh;

        /// <summary>Shortest centre run of an acceptable cross section.</summary>
        public readonly int CentreLow;

        /// <summary>Longest centre run of an acceptable cross section; below <see cref="CentreLow"/> when nothing is acceptable.</summary>
        public readonly int CentreHigh;

        private AxisRunBounds(int totalHigh, int centreLow, int centreHigh)
        {
            _totalHigh = totalHigh;
            CentreLow = centreLow;
            CentreHigh = centreHigh;
        }

        public static AxisRunBounds From(int expectedTotal, bool acrossAxes)
        {
            // The smallest and the largest total inside the window: 5·|total − expected| < 2·expected along the row, 5·expected < 12·total and 5·total < 12·expected across the axes
            var totalLow = Math.Max((acrossAxes ? 5 * expectedTotal / 12 : 3 * expectedTotal / 5) + 1, MinTotal);
            var totalHigh = acrossAxes ? (12 * expectedTotal - 1) / 5 : (7 * expectedTotal - 1) / 5;
            if (totalHigh < totalLow)
                return new AxisRunBounds(totalHigh, 1, 0);

            // Strict: 3·total < 14·centre < 9·total. Near miss: |3·total − 7·centre| <= 10.
            var centreLow = Math.Min(3 * totalLow / 14 + 1, (3 * totalLow - 10 + 6) / 7);
            // The near-miss form of the upper bound, (3·total + 10) / 7, is below the strict one from a total of 7 up
            var centreHigh = (9 * totalHigh - 1) / 14;
            return new AxisRunBounds(totalHigh, centreLow, centreHigh);
        }

        /// <summary>Longest side run beside a centre run of this length. Strict: 14·side &lt; 3·total &lt; 14·centre. Near miss: 21·side &lt;= 3·total + 30 &lt;= 7·centre + 40.</summary>
        public int SideCap(int centre)
            => Math.Max(Math.Min(centre - 1, (3 * _totalHigh - 1) / 14), Math.Min((7 * centre + 40) / 21, (_totalHigh + 10) / 7));

        /// <summary>Largest total around a centre run of this length: 3·total &lt; 14·centre. The near-miss form, (7·centre + 10) / 3, is the larger one only below a centre of 2, which is never acceptable.</summary>
        public int TotalCap(int centre)
            => Math.Min(_totalHigh, (14 * centre - 1) / 3);
    }

    /// <summary>
    /// <see cref="MeasureRuns"/> for an axis cross-check, given up the moment no cross section the cross-check could accept is left.
    /// </summary>
    /// <returns>
    /// True with the runs and the end <see cref="MeasureRuns"/> gives under a cap of <paramref name="expectedTotal"/>; false when the cross-check is certain to refuse, with nothing written.
    /// </returns>
    /// <remarks>
    /// The centre run is measured first, in both directions, because every other bound follows from it; the pixels read are the ones <see cref="MeasureRuns"/> reads, in another order, and fewer of them.
    /// Each side run is then allowed what is left of two budgets: the longest side run this centre admits, and the largest total it admits less what has been measured and one pixel for each side run still to come, since an empty side run is refused too.
    /// Both are at most the expected total, so a walk that finishes never stopped a run where the reference's cap would not have.
    /// On a large symbol a refused cross-check reads 0.55 to 0.60 of the pixels it used to.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool MeasureRunsBounded(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int centerX, int centerY, int stepX, int stepY, int expectedTotal, Span<int> runs, out int end)
    {
        Debug.Assert((stepX | stepY) == 1 && (stepX & stepY) == 0 && (stepX & ~1) == 0 && (stepY & ~1) == 0, "The line is a row or a column.");

        if ((uint)centerX >= (uint)width || (uint)centerY >= (uint)height || (long)width * height > luminance.Length)
            ThrowCentreOutsideImage(centerX, centerY, width, height, luminance.Length);

        end = 0;
        var before = stepX == 0 ? centerY : centerX;
        var after = stepX == 0 ? height - 1 - centerY : width - 1 - centerX;
        var step = (nint)stepY * width + stepX;
        var centre = (nint)centerY * width + centerX;
        ref var origin = ref MemoryMarshal.GetReference(luminance);

        // The centre run, back from the centre pixel and forward from the one after it
        var r2 = 0;
        var back = centre;
        var leftBack = before + 1;
        while (leftBack > 0 && Unsafe.Add(ref origin, back) < threshold)
        {
            r2++;
            leftBack--;
            back -= step;
        }
        if (leftBack == 0)
            return false;

        var forward = centre + step;
        var leftForward = after;
        while (leftForward > 0 && Unsafe.Add(ref origin, forward) < threshold)
        {
            r2++;
            leftForward--;
            forward += step;
        }
        if (leftForward == 0)
            return false;

        // A column is checked against the row, the other axis; a row against the row
        var bounds = AxisRunBounds.From(expectedTotal, acrossAxes: stepX == 0);
        if (r2 < bounds.CentreLow || r2 > bounds.CentreHigh)
            return false;

        var sideCap = bounds.SideCap(r2);
        var budget = bounds.TotalCap(r2) - r2;

        // Light then dark, back; light then dark, forward. A run about to pass its cap ends the walk.
        int r0 = 0, r1 = 0, r3 = 0, r4 = 0;
        var cap = Math.Min(sideCap, budget - 3);
        while (leftBack > 0 && Unsafe.Add(ref origin, back) >= threshold)
        {
            if (r1 >= cap)
                return false;
            r1++;
            leftBack--;
            back -= step;
        }

        cap = Math.Min(sideCap, budget - r1 - 2);
        while (leftBack > 0 && Unsafe.Add(ref origin, back) < threshold)
        {
            if (r0 >= cap)
                return false;
            r0++;
            leftBack--;
            back -= step;
        }

        cap = Math.Min(sideCap, budget - r1 - r0 - 1);
        while (leftForward > 0 && Unsafe.Add(ref origin, forward) >= threshold)
        {
            if (r3 >= cap)
                return false;
            r3++;
            leftForward--;
            forward += step;
        }

        cap = Math.Min(sideCap, budget - r1 - r0 - r3);
        while (leftForward > 0 && Unsafe.Add(ref origin, forward) < threshold)
        {
            if (r4 >= cap)
                return false;
            r4++;
            leftForward--;
            forward += step;
        }

        runs[4] = r4;
        runs[3] = r3;
        runs[2] = r2;
        runs[1] = r1;
        runs[0] = r0;
        end = after - leftForward + 1;
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

    /// <summary>
    /// The bottom-left to top-right walk, written as <see cref="MeasureDiagonalRunsReference"/> is with the rows running the other way, as the reference <see cref="MeasureRuns"/> is held to along (1, −1). Its side runs have no cap.
    /// </summary>
    internal static bool MeasureRisingDiagonalRunsReference(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, int centerX, int centerY, Span<int> runsOut, out int end)
    {
        end = 0;
        Span<int> runs = stackalloc int[5];

        var i = 0;
        while (centerX - i >= 0 && centerY + i < height && IsDark(luminance, width, centerX - i, centerY + i, threshold))
        {
            runs[2]++;
            i++;
        }
        if (centerX - i < 0 || centerY + i >= height)
            return false;

        while (centerX - i >= 0 && centerY + i < height && !IsDark(luminance, width, centerX - i, centerY + i, threshold))
        {
            runs[1]++;
            i++;
        }
        while (centerX - i >= 0 && centerY + i < height && IsDark(luminance, width, centerX - i, centerY + i, threshold))
        {
            runs[0]++;
            i++;
        }

        i = 1;
        while (centerX + i < width && centerY - i >= 0 && IsDark(luminance, width, centerX + i, centerY - i, threshold))
        {
            runs[2]++;
            i++;
        }
        if (centerX + i >= width || centerY - i < 0)
            return false;

        while (centerX + i < width && centerY - i >= 0 && !IsDark(luminance, width, centerX + i, centerY - i, threshold))
        {
            runs[3]++;
            i++;
        }
        while (centerX + i < width && centerY - i >= 0 && IsDark(luminance, width, centerX + i, centerY - i, threshold))
        {
            runs[4]++;
            i++;
        }

        runs.CopyTo(runsOut);
        end = i;
        return true;
    }
}
