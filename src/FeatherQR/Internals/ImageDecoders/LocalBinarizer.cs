#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
#endif

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// Regional binarization for a symbol lit unevenly: each pixel is read against its neighbourhood's level instead of one global threshold.
/// </summary>
/// <remarks>
/// An 8 × 8 block's black point is its mean, or for a flat block (range at most <see cref="MinDynamicRange"/>) half its minimum, raised to the 1:2:1 mean of its top, left and top-left neighbours when its minimum is below that mean (off the first block row and column); each pixel is read against the mean black point of the 5 × 5 blocks around its block.
/// </remarks>
internal static class LocalBinarizer
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

#if NET8_0_OR_GREATER
    /// <summary>Two blocks a step, 16 pixels wide: lane-wise min, max and widened sums over the eight rows, then each half folded to one value. Returns the first block left to the scalar loop.</summary>
    private static int BlockStatsVector128(ReadOnlySpan<byte> luminance, int width, byte flip, int top, int alignedColumns, Span<int> packed)
    {
        if (!Vector128.IsHardwareAccelerated)
            return 0;
        ref var origin = ref MemoryMarshal.GetReference(luminance);
        var flipLanes = Vector128.Create(flip);
        var blockX = 0;
        for (; blockX + 2 <= alignedColumns; blockX += 2)
        {
            var offset = (nuint)(top * width + blockX * BlockSize);
            var first = Vector128.LoadUnsafe(ref origin, offset) ^ flipLanes;
            var min = first;
            var max = first;
            var sumLow = Vector128.WidenLower(first);
            var sumHigh = Vector128.WidenUpper(first);
            for (var y = 1; y < BlockSize; y++)
            {
                var row = Vector128.LoadUnsafe(ref origin, offset + (nuint)(y * width)) ^ flipLanes;
                min = Vector128.Min(min, row);
                max = Vector128.Max(max, row);
                sumLow += Vector128.WidenLower(row);
                sumHigh += Vector128.WidenUpper(row);
            }
            // Each 8-byte half folded onto its lowest byte: the shifts stay inside a 64-bit lane
            var minFold = min.AsUInt64();
            var maxFold = max.AsUInt64();
            for (var shift = 32; shift >= 8; shift >>= 1)
            {
                minFold = Vector128.Min(minFold.AsByte(), Vector128.ShiftRightLogical(minFold, shift).AsByte()).AsUInt64();
                maxFold = Vector128.Max(maxFold.AsByte(), Vector128.ShiftRightLogical(maxFold, shift).AsByte()).AsUInt64();
            }
            packed[blockX] = Pack((int)(minFold.GetElement(0) & 0xFF), (int)(maxFold.GetElement(0) & 0xFF), Vector128.Sum(sumLow));
            packed[blockX + 1] = Pack((int)(minFold.GetElement(1) & 0xFF), (int)(maxFold.GetElement(1) & 0xFF), Vector128.Sum(sumHigh));
        }
        return blockX;
    }

    /// <summary>Two blocks a step: each half compared with its own block's threshold. Returns the first block left to the scalar loop.</summary>
    private static int WriteBlocksVector128(ReadOnlySpan<byte> luminance, Span<byte> binarized, int width, byte flip, int top, int alignedColumns, ReadOnlySpan<int> thresholds)
    {
        if (!Vector128.IsHardwareAccelerated)
            return 0;
        ref var source = ref MemoryMarshal.GetReference(luminance);
        ref var target = ref MemoryMarshal.GetReference(binarized);
        var flipLanes = Vector128.Create(flip);
        var blockX = 0;
        for (; blockX + 2 <= alignedColumns; blockX += 2)
        {
            // Thresholds are means of bytes, so they fit a byte; dark is luminance <= threshold
            var threshold = Vector128.Create(Vector64.Create((byte)thresholds[blockX]), Vector64.Create((byte)thresholds[blockX + 1]));
            var offset = (nuint)(top * width + blockX * BlockSize);
            for (var y = 0; y < BlockSize; y++, offset += (nuint)width)
            {
                var pixels = Vector128.LoadUnsafe(ref source, offset) ^ flipLanes;
                Vector128.OnesComplement(Vector128.Equals(Vector128.Min(pixels, threshold), pixels)).StoreUnsafe(ref target, offset);
            }
        }
        return blockX;
    }

    /// <summary>Dark pixels, and whether any lands in the other class than the global threshold puts it, 16 pixels a step. Returns the first pixel left to the scalar loop.</summary>
    private static int CountVector128(ReadOnlySpan<byte> luminance, ReadOnlySpan<byte> binarized, byte flip, byte globalThreshold, ref int dark, ref bool differs)
    {
        if (!Vector128.IsHardwareAccelerated)
            return 0;
        ref var source = ref MemoryMarshal.GetReference(luminance);
        ref var target = ref MemoryMarshal.GetReference(binarized);
        // luminance < g is min(luminance, g - 1) == luminance, with nothing dark at g = 0
        var globalMinus1 = Vector128.Create((byte)(globalThreshold == 0 ? 0 : globalThreshold - 1));
        var globalNone = globalThreshold == 0 ? Vector128<byte>.AllBitsSet : Vector128<byte>.Zero;
        var flipLanes = Vector128.Create(flip);
        var mismatch = Vector128<byte>.Zero;
        var i = 0;
        var last = binarized.Length - Vector128<byte>.Count;
        while (i <= last)
        {
            // Byte counters hold up to 255 steps before they are widened out
            var counts = Vector128<byte>.Zero;
            var stop = Math.Min(last, i + 254 * Vector128<byte>.Count);
            for (; i <= stop; i += Vector128<byte>.Count)
            {
                var pixels = Vector128.LoadUnsafe(ref source, (nuint)i) ^ flipLanes;
                var isDark = Vector128.Equals(Vector128.LoadUnsafe(ref target, (nuint)i), Vector128<byte>.Zero);
                var globalDark = Vector128.AndNot(Vector128.Equals(Vector128.Min(pixels, globalMinus1), pixels), globalNone);
                mismatch |= isDark ^ globalDark;
                counts -= isDark; // all bits set is -1
            }
            dark += (int)(Vector128.Sum(Vector128.WidenLower(counts)) + Vector128.Sum(Vector128.WidenUpper(counts)));
        }
        differs |= mismatch != Vector128<byte>.Zero;
        return i;
    }
#endif
}
