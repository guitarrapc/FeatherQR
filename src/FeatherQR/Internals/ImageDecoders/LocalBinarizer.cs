namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// Regional binarization for a symbol lit unevenly: each pixel is read against its neighbourhood's level instead of one global threshold.
/// </summary>
/// <remarks>
/// An 8 × 8 block's black point is its mean, or for a flat block (range at most <see cref="MinDynamicRange"/>) half its minimum, raised to the 1:2:1 mean of its top, left and top-left neighbours when its minimum is below that mean (off the first block row and column); each pixel is read against the mean black point of the 5 × 5 blocks around its block.
/// </remarks>
internal static partial class LocalBinarizer
{
    internal const int BlockSize = 8;

    /// <summary>A block's range at or below this is read as flat.</summary>
    internal const int MinDynamicRange = 24;

    /// <summary>Blocks each side of a block that its threshold averages over.</summary>
    private const int Radius = 2;

    private const int Neighbourhood = (2 * Radius + 1) * (2 * Radius + 1);

    /// <summary>Luminance a pixel is written with when dark and when light; the histogram of the output holds these two bins only.</summary>
    internal const byte Dark = 0;
    internal const byte Light = 255;

    /// <summary>The number of ints <see cref="TryBinarize"/> needs as scratch; 0 when the image is under 5 blocks a side, where there is no neighbourhood to average.</summary>
    internal static int ScratchLength(int width, int height)
    {
        var columns = (width + BlockSize - 1) / BlockSize;
        var rows = (height + BlockSize - 1) / BlockSize;
        // Black points, then one threshold per block of a row
        return columns < 2 * Radius + 1 || rows < 2 * Radius + 1 ? 0 : columns * rows + columns;
    }

    /// <summary>
    /// Writes the regional binarization of <paramref name="luminance"/> as <see cref="Dark"/> and <see cref="Light"/> pixels.
    /// </summary>
    /// <param name="luminance">Grayscale pixels, row-major.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="negative">Binarize the negative: each pixel is read as 255 minus its value, and the negative is never written out.</param>
    /// <param name="globalThreshold">The global threshold the decoder already failed on in that polarity (dark: luminance &lt; threshold).</param>
    /// <param name="binarized">Receives one byte a pixel; may not alias <paramref name="luminance"/>.</param>
    /// <param name="scratch"><see cref="ScratchLength"/> ints.</param>
    /// <param name="darkCount">Pixels written <see cref="Dark"/>.</param>
    /// <returns>
    /// <see langword="false"/> when the image is under five blocks a side, or no pixel leaves the class the global threshold put it in.
    /// </returns>
    internal static bool TryBinarize(ReadOnlySpan<byte> luminance, int width, int height, bool negative, byte globalThreshold, Span<byte> binarized, Span<int> scratch, out int darkCount)
        => TryBinarizeCore(luminance, width, height, negative ? (byte)0xFF : (byte)0, globalThreshold, binarized, scratch, vector: true, out darkCount);

    /// <summary>The scalar form of <see cref="TryBinarize"/>, whatever the hardware; the reference its vector form is held to.</summary>
    internal static bool TryBinarizeScalar(ReadOnlySpan<byte> luminance, int width, int height, bool negative, byte globalThreshold, Span<byte> binarized, Span<int> scratch, out int darkCount)
        => TryBinarizeCore(luminance, width, height, negative ? (byte)0xFF : (byte)0, globalThreshold, binarized, scratch, vector: false, out darkCount);

    // flip is XORed into every pixel as it is loaded: 0, or 0xFF for the negative (255 - v is v ^ 0xFF)
    private static bool TryBinarizeCore(ReadOnlySpan<byte> luminance, int width, int height, byte flip, byte globalThreshold, Span<byte> binarized, Span<int> scratch, bool vector, out int darkCount)
    {
        darkCount = 0;
        var length = ScratchLength(width, height);
        if (length == 0)
            return false;

        var columns = (width + BlockSize - 1) / BlockSize;
        var rows = (height + BlockSize - 1) / BlockSize;
        var pixelCount = width * height;
        luminance = luminance.Slice(0, pixelCount);
        binarized = binarized.Slice(0, pixelCount);
        var blackPoints = scratch.Slice(0, columns * rows);
        var thresholds = scratch.Slice(columns * rows, columns);

        // Blocks that start on their own 8-pixel column; the last one starts earlier when the width is not a multiple of 8
        var alignedColumns = width % BlockSize == 0 ? columns : columns - 1;

        for (var blockY = 0; blockY < rows; blockY++)
        {
            var top = Math.Min(blockY * BlockSize, height - BlockSize);
            var packed = blackPoints.Slice(blockY * columns, columns);
            var blockX = 0;
#if NET8_0_OR_GREATER
            if (vector)
                blockX = BlockStatsVector128(luminance, width, flip, top, alignedColumns, packed);
#endif
            for (; blockX < columns; blockX++)
            {
                var left = Math.Min(blockX * BlockSize, width - BlockSize);
                int sum = 0, min = 255, max = 0;
                for (var y = 0; y < BlockSize; y++)
                {
                    var row = luminance.Slice((top + y) * width + left, BlockSize);
                    for (var x = 0; x < BlockSize; x++)
                    {
                        var pixel = row[x] ^ flip;
                        sum += pixel;
                        min = Math.Min(min, pixel);
                        max = Math.Max(max, pixel);
                    }
                }
                packed[blockX] = Pack(min, max, sum);
            }
            ApplyFlatRule(blackPoints, columns, blockY);
        }

        for (var blockY = 0; blockY < rows; blockY++)
        {
            var top = Math.Min(blockY * BlockSize, height - BlockSize);
            var centreY = Math.Min(Math.Max(blockY, Radius), rows - Radius - 1);
            for (var blockX = 0; blockX < columns; blockX++)
            {
                var centreX = Math.Min(Math.Max(blockX, Radius), columns - Radius - 1);
                var sum = 0;
                for (var dy = -Radius; dy <= Radius; dy++)
                {
                    var neighbourRow = blackPoints.Slice((centreY + dy) * columns + centreX - Radius, 2 * Radius + 1);
                    for (var dx = 0; dx < neighbourRow.Length; dx++)
                        sum += neighbourRow[dx];
                }
                thresholds[blockX] = sum / Neighbourhood;
            }

            var blockStart = 0;
#if NET8_0_OR_GREATER
            if (vector)
                blockStart = WriteBlocksVector128(luminance, binarized, width, flip, top, alignedColumns, thresholds);
#endif
            for (var blockX = blockStart; blockX < columns; blockX++)
            {
                var left = Math.Min(blockX * BlockSize, width - BlockSize);
                var threshold = thresholds[blockX];
                for (var y = 0; y < BlockSize; y++)
                {
                    var offset = (top + y) * width + left;
                    var source = luminance.Slice(offset, BlockSize);
                    var target = binarized.Slice(offset, BlockSize);
                    for (var x = 0; x < BlockSize; x++)
                        target[x] = (source[x] ^ flip) <= threshold ? Dark : Light;
                }
            }
        }

        // Counted once every block is written: the overlapped last row and column are written twice
        var start = 0;
        var dark = 0;
        var differs = false;
#if NET8_0_OR_GREATER
        if (vector)
            start = CountVector128(luminance, binarized, flip, globalThreshold, ref dark, ref differs);
#endif
        for (var i = start; i < pixelCount; i++)
        {
            var isDark = binarized[i] == Dark;
            dark += isDark ? 1 : 0;
            differs |= isDark != (luminance[i] ^ flip) < globalThreshold;
        }
        darkCount = dark;
        return differs;
    }

    /// <summary>A block's statistics until <see cref="ApplyFlatRule"/> reads them: range, minimum and mean in one int.</summary>
    private static int Pack(int min, int max, int sum) => (max - min) << 16 | min << 8 | sum / (BlockSize * BlockSize);

    /// <summary>Turns a block row's packed statistics into black points; a flat block reads its top, left and top-left neighbours, which are final by then.</summary>
    private static void ApplyFlatRule(Span<int> blackPoints, int columns, int blockY)
    {
        for (var blockX = 0; blockX < columns; blockX++)
        {
            var index = blockY * columns + blockX;
            var packed = blackPoints[index];
            var range = packed >> 16;
            var min = (packed >> 8) & 0xFF;
            var blackPoint = packed & 0xFF;
            if (range <= MinDynamicRange)
            {
                blackPoint = min / 2;
                if (blockY > 0 && blockX > 0)
                {
                    var neighbours = (blackPoints[index - columns] + 2 * blackPoints[index - 1] + blackPoints[index - columns - 1]) / 4;
                    if (min < neighbours)
                        blackPoint = neighbours;
                }
            }
            blackPoints[index] = blackPoint;
        }
    }
}
