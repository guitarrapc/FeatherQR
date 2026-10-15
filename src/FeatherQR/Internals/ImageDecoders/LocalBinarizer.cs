using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// Regional binarization for a symbol lit unevenly: each pixel is read against its neighbourhood's level instead of one global threshold.
/// </summary>
/// <remarks>
/// <para>
/// An 8 × 8 block's black point is its mean, or for a flat block (range at most <see cref="MinDynamicRange"/>) half its minimum, raised to the 1:2:1 mean of its top, left and top-left neighbours when its minimum is below that mean (off the first block row and column). Each pixel is read against the mean black point of the 5 × 5 blocks around its block.
/// </para>
/// <para>
/// The pixels are read twice. The first pass takes each block's sum, minimum and maximum, which hold for both polarities: the negative's are 255 less the positive's, exchanged. The second writes a row at a time against one threshold a pixel, and counts as it writes.
/// A caller that binarizes both polarities of one image keeps the statistics between the two calls, so the second reads the pixels once.
/// </para>
/// </remarks>
internal static partial class LocalBinarizer
{
    internal const int BlockSize = 8;

    /// <summary>A block's range at or below this is read as flat.</summary>
    internal const int MinDynamicRange = 24;

    /// <summary>Blocks each side of a block that its threshold averages over.</summary>
    private const int Radius = 2;

    private const int Neighbourhood = (2 * Radius + 1) * (2 * Radius + 1);

    /// <summary>Luminance a pixel is written with when dark and when light. The histogram of the output holds these two bins only.</summary>
    internal const byte Dark = 0;
    internal const byte Light = 255;

    // A block's statistics in one int: its sum in the low 14 bits (64 pixels of 255 are 16,320), then its minimum, then its maximum
    private const int SumMask = (1 << MinShift) - 1;
    private const int MinShift = 14;
    private const int MaxShift = 22;
    private const int BlockPixels = BlockSize * BlockSize;

    /// <summary>The form a binarization runs in.</summary>
    internal enum Kernel
    {
        /// <summary>The widest vectors the build accelerates, or the scalar form.</summary>
        Auto,

        /// <summary>The scalar form, whatever the hardware, and the reference the vector forms are held to.</summary>
        Scalar,

        /// <summary>Two blocks and sixteen pixels a step.</summary>
        Vector128,

        /// <summary>Four blocks and 32 pixels a step.</summary>
        Vector256,
    }

    /// <summary>The number of ints <see cref="TryBinarize(ReadOnlySpan{byte}, int, int, bool, byte, Span{byte}, Span{int}, out int)"/> needs as scratch, or 0 when the image is under 5 blocks a side, where there is no neighbourhood to average.</summary>
    internal static int ScratchLength(int width, int height)
    {
        var columns = (width + BlockSize - 1) / BlockSize;
        var rows = (height + BlockSize - 1) / BlockSize;
        if (columns < 2 * Radius + 1 || rows < 2 * Radius + 1)
            return 0;
        // Statistics and black points a block, column sums and thresholds a block column, and one row of thresholds a pixel
        return 2 * columns * rows + 2 * columns + (width + sizeof(int) - 1) / sizeof(int);
    }

    /// <summary>
    /// Writes the regional binarization of <paramref name="luminance"/> as <see cref="Dark"/> and <see cref="Light"/> pixels.
    /// </summary>
    /// <param name="luminance">Grayscale pixels, row-major.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="negative">Binarize the negative: each pixel is read as 255 minus its value, and the negative is never written out.</param>
    /// <param name="globalThreshold">The global threshold the decoder already failed on in that polarity (dark: luminance &lt; threshold).</param>
    /// <param name="binarized">Receives one byte a pixel. May not alias <paramref name="luminance"/>.</param>
    /// <param name="scratch"><see cref="ScratchLength"/> ints.</param>
    /// <param name="darkCount">Pixels written <see cref="Dark"/>.</param>
    /// <returns>
    /// <see langword="false"/> when the image is under five blocks a side, or no pixel leaves the class the global threshold put it in.
    /// </returns>
    internal static bool TryBinarize(ReadOnlySpan<byte> luminance, int width, int height, bool negative, byte globalThreshold, Span<byte> binarized, Span<int> scratch, out int darkCount)
    {
        var measured = false;
        return TryBinarizeCore(luminance, width, height, negative, globalThreshold, binarized, scratch, Kernel.Auto, ref measured, out darkCount);
    }

    /// <summary>
    /// <see cref="TryBinarize(ReadOnlySpan{byte}, int, int, bool, byte, Span{byte}, Span{int}, out int)"/> for a caller that binarizes one image more than once, in both polarities.
    /// </summary>
    /// <remarks>
    /// <paramref name="measured"/> is whether <paramref name="scratch"/> holds the block statistics of this image already. A call that finds it false reads them from the pixels and sets it.
    /// The caller starts it false and passes the same scratch, untouched, with the same image each time.
    /// </remarks>
    internal static bool TryBinarize(ReadOnlySpan<byte> luminance, int width, int height, bool negative, byte globalThreshold, Span<byte> binarized, Span<int> scratch, ref bool measured, out int darkCount)
        => TryBinarizeCore(luminance, width, height, negative, globalThreshold, binarized, scratch, Kernel.Auto, ref measured, out darkCount);

    /// <summary><see cref="TryBinarize(ReadOnlySpan{byte}, int, int, bool, byte, Span{byte}, Span{int}, ref bool, out int)"/> in one form, for the tests that hold the forms to each other.</summary>
    internal static bool TryBinarizeWith(Kernel kernel, ReadOnlySpan<byte> luminance, int width, int height, bool negative, byte globalThreshold, Span<byte> binarized, Span<int> scratch, ref bool measured, out int darkCount)
        => TryBinarizeCore(luminance, width, height, negative, globalThreshold, binarized, scratch, kernel, ref measured, out darkCount);

    /// <summary>The scalar form of <see cref="TryBinarize(ReadOnlySpan{byte}, int, int, bool, byte, Span{byte}, Span{int}, out int)"/>, whatever the hardware, and the reference its vector forms are held to.</summary>
    internal static bool TryBinarizeScalar(ReadOnlySpan<byte> luminance, int width, int height, bool negative, byte globalThreshold, Span<byte> binarized, Span<int> scratch, out int darkCount)
    {
        var measured = false;
        return TryBinarizeCore(luminance, width, height, negative, globalThreshold, binarized, scratch, Kernel.Scalar, ref measured, out darkCount);
    }

    private static bool TryBinarizeCore(ReadOnlySpan<byte> luminance, int width, int height, bool negative, byte globalThreshold, Span<byte> binarized, Span<int> scratch, Kernel kernel, ref bool measured, out int darkCount)
    {
        darkCount = 0;
        if (ScratchLength(width, height) == 0)
            return false;

        var columns = (width + BlockSize - 1) / BlockSize;
        var rows = (height + BlockSize - 1) / BlockSize;
        var blocks = columns * rows;
        var pixelCount = width * height;
        luminance = luminance.Slice(0, pixelCount);
        binarized = binarized.Slice(0, pixelCount);
        var statistics = scratch.Slice(0, blocks);
        var blackPoints = scratch.Slice(blocks, blocks);
        var columnSums = scratch.Slice(2 * blocks, columns);
        var thresholds = scratch.Slice(2 * blocks + columns, columns);
        var thresholdRow = MemoryMarshal.AsBytes(scratch.Slice(2 * blocks + 2 * columns)).Slice(0, width);

#if NET8_0_OR_GREATER
        if (kernel == Kernel.Auto)
            kernel = Vector256.IsHardwareAccelerated ? Kernel.Vector256 : Vector128.IsHardwareAccelerated ? Kernel.Vector128 : Kernel.Scalar;
#else
        kernel = Kernel.Scalar;
#endif

        if (!measured)
        {
            for (var blockY = 0; blockY < rows; blockY++)
            {
                var top = Math.Min(blockY * BlockSize, height - BlockSize);
                var packed = statistics.Slice(blockY * columns, columns);
#if NET8_0_OR_GREATER
                if (kernel == Kernel.Vector256)
                    MeasureBlocksVector256(luminance, width, top, packed);
                else if (kernel == Kernel.Vector128)
                    MeasureBlocksVector128(luminance, width, top, packed);
                else
#endif
                    MeasureBlocksScalar(luminance, width, top, packed);
            }
            measured = true;
        }

        ComputeBlackPoints(statistics, blackPoints, columns, rows, negative);

        // The last block row and column start where their eight pixels end at the image's edge, over the ones before them.
        // A pixel under two blocks takes the later one's threshold, so each row is written once: the rows of a block row that the last one does not cover.
        var flip = negative ? (byte)0xFF : (byte)0;
        var light = 0;
        var differs = false;
        var thresholdsOf = -1;
        for (var blockY = 0; blockY < rows; blockY++)
        {
            var top = Math.Min(blockY * BlockSize, height - BlockSize);
            var bottom = blockY == rows - 1 ? height : Math.Min(top + BlockSize, height - BlockSize);

            var centreY = Math.Min(Math.Max(blockY, Radius), rows - Radius - 1);
            if (centreY != thresholdsOf)
            {
                thresholdsOf = centreY;
#if NET8_0_OR_GREATER
                if (kernel == Kernel.Vector256)
                    ComputeThresholdsVector256(blackPoints, columns, centreY, columnSums, thresholds);
                else
#endif
                    ComputeThresholdsScalar(blackPoints, columns, centreY, columnSums, thresholds);
                SpreadThresholds(thresholds, width, thresholdRow);
            }

#if NET8_0_OR_GREATER
            if (kernel == Kernel.Vector256)
                WriteRowsVector256(luminance, binarized, width, top, bottom, flip, globalThreshold, thresholdRow, ref light, ref differs);
            else if (kernel == Kernel.Vector128)
                WriteRowsVector128(luminance, binarized, width, top, bottom, flip, globalThreshold, thresholdRow, ref light, ref differs);
            else
#endif
                WriteRowsScalar(luminance, binarized, width, top, bottom, flip, globalThreshold, thresholdRow, ref light, ref differs);
        }

        darkCount = pixelCount - light;
        return differs;
    }

    /// <summary>One block row's statistics, each block's sum, minimum and maximum of the pixels as they are stored, in one int.</summary>
    private static void MeasureBlocksScalar(ReadOnlySpan<byte> luminance, int width, int top, Span<int> packed)
    {
        for (var blockX = 0; blockX < packed.Length; blockX++)
            packed[blockX] = MeasureBlockScalar(luminance, width, top, Math.Min(blockX * BlockSize, width - BlockSize));
    }

    private static int MeasureBlockScalar(ReadOnlySpan<byte> luminance, int width, int top, int left)
    {
        int sum = 0, min = 255, max = 0;
        for (var y = 0; y < BlockSize; y++)
        {
            var row = luminance.Slice((top + y) * width + left, BlockSize);
            for (var x = 0; x < row.Length; x++)
            {
                int pixel = row[x];
                sum += pixel;
                min = Math.Min(min, pixel);
                max = Math.Max(max, pixel);
            }
        }
        return sum | min << MinShift | max << MaxShift;
    }

    /// <summary>
    /// Turns the statistics into the black points of one polarity. A flat block reads its top, left and top-left neighbours, which are final by then, so the blocks are taken in order.
    /// </summary>
    private static void ComputeBlackPoints(ReadOnlySpan<int> statistics, Span<int> blackPoints, int columns, int rows, bool negative)
    {
        for (var blockY = 0; blockY < rows; blockY++)
        {
            var packed = statistics.Slice(blockY * columns, columns);
            var current = blackPoints.Slice(blockY * columns, columns);
            // The first block row and column have no neighbours to read
            var above = blockY == 0 ? default : blackPoints.Slice((blockY - 1) * columns, columns);
            var left = 0;
            for (var blockX = 0; blockX < current.Length; blockX++)
            {
                left = BlackPoint(packed[blockX], negative, above, blockX, left);
                current[blockX] = left;
            }
        }
    }

    /// <summary>One block's black point from its statistics. <paramref name="above"/> is the block row above, empty for the first, and <paramref name="left"/> the black point of the block before it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BlackPoint(int block, bool negative, ReadOnlySpan<int> above, int blockX, int left)
    {
        var sum = block & SumMask;
        var min = (block >> MinShift) & 0xFF;
        var max = (block >> MaxShift) & 0xFF;
        if (negative)
        {
            var positiveMin = min;
            min = 255 - max;
            max = 255 - positiveMin;
            sum = BlockPixels * 255 - sum;
        }

        // Nothing here is negative, so the divisions are shifts
        var blackPoint = sum >> 6;
        if (max - min <= MinDynamicRange)
        {
            blackPoint = min >> 1;
            if ((uint)(blockX - 1) < (uint)above.Length)
            {
                var neighbours = (above[blockX] + 2 * left + above[blockX - 1]) >> 2;
                if (min < neighbours)
                    blackPoint = neighbours;
            }
        }
        return blackPoint;
    }

    /// <summary>
    /// One block row's thresholds: the mean of the 25 black points around each block, the window held inside the image at the edges.
    /// The five rows are summed a column first, so a block adds five sums where it would add 25 black points.
    /// </summary>
    private static void ComputeThresholdsScalar(ReadOnlySpan<int> blackPoints, int columns, int centreY, Span<int> columnSums, Span<int> thresholds)
    {
        var rows = blackPoints.Slice((centreY - Radius) * columns, (2 * Radius + 1) * columns);
        for (var blockX = 0; blockX < columnSums.Length; blockX++)
            columnSums[blockX] = rows[blockX] + rows[columns + blockX] + rows[2 * columns + blockX] + rows[3 * columns + blockX] + rows[4 * columns + blockX];

        for (var blockX = Radius; blockX < columnSums.Length - Radius; blockX++)
            thresholds[blockX] = (columnSums[blockX - 2] + columnSums[blockX - 1] + columnSums[blockX] + columnSums[blockX + 1] + columnSums[blockX + 2]) / Neighbourhood;
        SpreadEdgeThresholds(thresholds);
    }

    /// <summary>The two block columns at each edge take the threshold of the nearest block whose window fits.</summary>
    private static void SpreadEdgeThresholds(Span<int> thresholds)
    {
        thresholds[0] = thresholds[1] = thresholds[Radius];
        thresholds[thresholds.Length - 1] = thresholds[thresholds.Length - 2] = thresholds[thresholds.Length - Radius - 1];
    }

    /// <summary>One threshold a pixel of a row, from one a block: each block's over its eight pixels, the last block's written last.</summary>
    private static void SpreadThresholds(ReadOnlySpan<int> thresholds, int width, Span<byte> thresholdRow)
    {
        // Thresholds are means of bytes, so they fit a byte
        var aligned = width / BlockSize;
        var whole = MemoryMarshal.Cast<byte, ulong>(thresholdRow.Slice(0, aligned * BlockSize));
        var own = thresholds.Slice(0, whole.Length);
        for (var blockX = 0; blockX < own.Length; blockX++)
            whole[blockX] = (ulong)own[blockX] * 0x0101010101010101UL;
        if (aligned != thresholds.Length)
            BinaryPrimitives.WriteUInt64LittleEndian(thresholdRow.Slice(width - BlockSize, BlockSize), (ulong)thresholds[aligned] * 0x0101010101010101UL);
    }

    /// <summary>
    /// Writes the rows from <paramref name="top"/> to <paramref name="bottom"/>, each pixel against its threshold, and counts the light ones and whether any lands in the other class than the global threshold puts it.
    /// </summary>
    private static void WriteRowsScalar(ReadOnlySpan<byte> luminance, Span<byte> binarized, int width, int top, int bottom, byte flip, byte globalThreshold, ReadOnlySpan<byte> thresholdRow, ref int light, ref bool differs)
    {
        var lightCount = 0;
        var anyDiffers = false;
        for (var y = top; y < bottom; y++)
        {
            var source = luminance.Slice(y * width, thresholdRow.Length);
            var target = binarized.Slice(y * width, thresholdRow.Length);
            for (var x = 0; x < thresholdRow.Length; x++)
            {
                var pixel = source[x] ^ flip;
                var isLight = pixel > thresholdRow[x];
                target[x] = isLight ? Light : Dark;
                lightCount += isLight ? 1 : 0;
                anyDiffers |= isLight == pixel < globalThreshold;
            }
        }
        light += lightCount;
        differs |= anyDiffers;
    }
}
