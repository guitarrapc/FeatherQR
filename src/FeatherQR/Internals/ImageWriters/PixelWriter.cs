using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// A raw pixel buffer for a module matrix, its rows packed with no padding or a stride apart.
/// </summary>
/// <remarks>
/// <para>
/// A dark pixel is the dark colour painted over the light one, as the SVG paints its path over its background.
/// <see cref="PixelLayout.Rgba8888"/> and <see cref="PixelLayout.Bgra8888"/> carry each colour straight, alpha not premultiplied.
/// <see cref="PixelLayout.Gray8"/>, <see cref="PixelLayout.Rgb888x"/>, <see cref="PixelLayout.Bgr888x"/>, <see cref="PixelLayout.Rgb24"/> and
/// <see cref="PixelLayout.Bgr24"/> have no alpha, so they hold each colour composited over white, as the decoders read a transparent pixel,
/// and the fourth byte of Rgb888x and Bgr888x is 255.
/// Gray8 then weighs the channels 77, 150 and 29 over 256 (BT.601). So a Gray8 buffer equals the decoders' reading of the others.
/// </para>
/// <para>
/// The first pixel row of each module row is written from the runs of the stored module bits, each run over the pixels <see cref="ImageLayout"/>
/// gives its modules, and the module row's other pixel rows are copies of it.
/// </para>
/// </remarks>
internal static class PixelWriter
{
    public static int BufferSize<TView>(in TView view, PixelLayout layout, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        var bytesPerPixel = BytesPerPixel(layout);
        var image = ImageLayout.Create(in view, in options);
        // Refused here too, so a caller sizing a buffer learns of an icon that does not fit before writing.
        IconArea.Get(in view, in options);
        return BufferSize(in image, bytesPerPixel, in options);
    }

    private static int BufferSize(in ImageLayout image, int bytesPerPixel, in SymbolImageOptions options)
    {
        // Each side fits an int, so the pixel count fits a long, but four bytes a pixel can wrap it.
        var pixelCount = (long)image.Width * image.Height;
        ImageLimits.ThrowIfLongerThanAnArray(pixelCount, image.Width, image.Height);
        var length = pixelCount * bytesPerPixel;
        ImageLimits.ThrowIfLongerThanAnArray(length, image.Width, image.Height);
        return (int)length;
    }

    /// <summary>Rows packed: each starts where the one before ends.</summary>
    public static bool TryWrite<TView>(in TView view, Span<byte> destination, PixelLayout layout, out int width, out int height, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        var bytesPerPixel = BytesPerPixel(layout);
        var image = ImageLayout.Create(in view, in options);
        var icon = IconArea.Get(in view, in options);
        var length = BufferSize(in image, bytesPerPixel, in options);
        if (destination.Length < length)
        {
            width = 0;
            height = 0;
            return false;
        }

        width = image.Width;
        height = image.Height;
        Write(in view, destination, in image, in icon, layout, bytesPerPixel, width * bytesPerPixel, in options);
        return true;
    }

    /// <summary>Rows <paramref name="rowBytes"/> apart. The bytes between a row's last pixel and the next row are not written.</summary>
    public static bool TryWrite<TView>(in TView view, Span<byte> destination, int rowBytes, PixelLayout layout, out int width, out int height, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        var bytesPerPixel = BytesPerPixel(layout);
        var image = ImageLayout.Create(in view, in options);
        var icon = IconArea.Get(in view, in options);
        // An image the packed overload refuses is refused here, so the two take the same options.
        BufferSize(in image, bytesPerPixel, in options);
        var rowLength = image.Width * bytesPerPixel;
        if (rowBytes < rowLength)
            ThrowRowBytesBelowOneRow(rowBytes, image.Width, bytesPerPixel);
        // The last row ends at its last pixel, so a region that reaches the end of a larger buffer fits.
        if (destination.Length < (long)(image.Height - 1) * rowBytes + rowLength)
        {
            width = 0;
            height = 0;
            return false;
        }

        width = image.Width;
        height = image.Height;
        Write(in view, destination, in image, in icon, layout, bytesPerPixel, rowBytes, in options);
        return true;
    }

    // The message is built here, so the entry holds none of the code that formats it.
    private static void ThrowRowBytesBelowOneRow(int rowBytes, int width, int bytesPerPixel)
        => throw new ArgumentOutOfRangeException(nameof(rowBytes), rowBytes, $"Row stride must cover a row of {width} pixels at {bytesPerPixel} bytes each.");

    /// <summary>The image into a destination already checked to hold it, its rows <paramref name="rowBytes"/> apart.</summary>
    /// <remarks>
    /// Kept a method of its own. Inlined into a caller it is compiled on that caller's profile, which is taken before these loops
    /// have run enough to count, and the plain image is then written far slower.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Write<TView>(in TView view, Span<byte> destination, in ImageLayout image, in IconArea icon, PixelLayout layout, int bytesPerPixel, int rowBytes, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        if (options.IsStyled)
        {
            WriteStyled(in view, destination, in image, in icon, layout, bytesPerPixel, rowBytes, in options);
            return;
        }
        var rowLength = image.Width * bytesPerPixel;
        var dark = Pixel(options.DarkColor.Over(options.LightColor), layout);
        var light = Pixel(options.LightColor, layout);
        var quietZone = (view.Width - view.CoreWidth) / 2;
        var coreWidth = view.CoreWidth;
        var core = view.CoreBits;
        // At whole pixels a run's bytes follow from its length, so the layout is not asked where each run ends.
        var runUnit = image.Denominator == 1 ? (int)image.Numerator * bytesPerPixel : 0;

        for (var group = 0; group < image.Groups; group++)
        {
            var top = image.GroupStart(group);
            var first = destination.Slice((int)((long)top * rowBytes), rowLength);
            var coreRow = image.GroupRow(group) - quietZone;
            if ((uint)coreRow >= (uint)view.CoreHeight)
            {
                Fill(first, light, bytesPerPixel);
            }
            else
            {
                var at = image.ColumnStart(quietZone) * bytesPerPixel;
                Fill(first.Slice(0, at), light, bytesPerPixel);
                var start = (long)coreRow * coreWidth;
                var column = 0;
                var isDark = false;
                while (column < coreWidth)
                {
                    var run = PackedRuns.Length(core, start + column, coreWidth - column, isDark);
                    column += run;
                    var end = runUnit != 0 ? at + run * runUnit : image.ColumnStart(quietZone + column) * bytesPerPixel;
                    Fill(first.Slice(at, end - at), isDark ? dark : light, bytesPerPixel);
                    at = end;
                    isDark = !isDark;
                }
                Fill(first.Slice(at), light, bytesPerPixel);
                if (icon.CoversRow(coreRow))
                {
                    var from = image.ColumnStart(quietZone + icon.Column) * bytesPerPixel;
                    Fill(first.Slice(from, image.ColumnStart(quietZone + icon.Column + icon.Size) * bytesPerPixel - from), light, bytesPerPixel);
                }
            }

            var rows = image.GroupHeight(group);
            for (var repeat = 1; repeat < rows; repeat++)
                first.CopyTo(destination.Slice((int)((long)(top + repeat) * rowBytes), rowLength));
        }
    }

    /// <summary>
    /// A styled image row by row: each row's levels, then each level's pixel from a table, so the pixels are the colours the PNG's palette holds.
    /// </summary>
    private static void WriteStyled<TView>(in TView view, Span<byte> destination, in ImageLayout image, in IconArea icon, PixelLayout layout, int bytesPerPixel, int rowBytes, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        const int levels = 1 << PngEncoder.StyledBitDepth;
        var raster = StyledRaster.Take();
        var levelRow = ArrayPool<byte>.Shared.Rent(image.Width);
        try
        {
            raster.Prepare(in view, in image, in options, in icon, levels);
            Span<uint> pixels = stackalloc uint[levels];
            for (var k = 0; k < levels; k++)
                pixels[k] = Pixel(StyledRaster.LevelColor(options.DarkColor, options.LightColor, k, levels), layout);
            var row = levelRow.AsSpan(0, image.Width);
            for (var y = 0; y < image.Height; y++)
            {
                var line = destination.Slice((int)((long)y * rowBytes), image.Width * bytesPerPixel);
                if (raster.IsLightRow(y))
                {
                    Fill(line, pixels[0], bytesPerPixel);
                    continue;
                }
                raster.ComposeRow(in view, y, row);
                if (bytesPerPixel == 1)
                {
                    for (var x = 0; x < row.Length; x++)
                        line[x] = (byte)pixels[row[x]];
                }
                else if (bytesPerPixel == 3)
                {
                    // Each pixel is stored as a whole word, and the next pixel overwrites its fourth byte.
                    // The last pixel's fourth byte would land past the row, so that one is stored byte by byte.
                    var last = row.Length - 1;
                    for (var x = 0; x < last; x++)
                        Unsafe.WriteUnaligned(ref line[x * 3], pixels[row[x]]);
                    FillThreeBytes(line.Slice(last * 3), pixels[row[last]]);
                }
                else
                {
                    var words = MemoryMarshal.Cast<byte, uint>(line);
                    for (var x = 0; x < row.Length; x++)
                        words[x] = pixels[row[x]];
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(levelRow);
            StyledRaster.Return(raster);
        }
    }

    private static int BytesPerPixel(PixelLayout layout) => layout switch
    {
        PixelLayout.Gray8 => 1,
        PixelLayout.Rgba8888 or PixelLayout.Bgra8888 or PixelLayout.Rgb888x or PixelLayout.Bgr888x => 4,
        PixelLayout.Rgb24 or PixelLayout.Bgr24 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(layout), $"Pixel layout {(int)layout} is not one of the seven defined."),
    };

    /// <summary>
    /// One pixel's bytes as they lie in memory, read as a native word. Gray8's one byte is the low byte, and a three-byte pixel
    /// is the word's first three bytes in memory.
    /// </summary>
    private static uint Pixel(SymbolColor color, PixelLayout layout) => layout switch
    {
        PixelLayout.Gray8 => Luminance(OverWhite(color)),
        PixelLayout.Rgba8888 => InMemoryOrder(color.R, color.G, color.B, color.A),
        PixelLayout.Bgra8888 => InMemoryOrder(color.B, color.G, color.R, color.A),
        // A three-byte layout takes the word of its four-byte twin, and stores its first three bytes.
        PixelLayout.Bgr888x or PixelLayout.Bgr24 => Bgr888x(OverWhite(color)),
        _ => Rgb888x(OverWhite(color)),
    };

    private static uint Rgb888x(SymbolColor color) => InMemoryOrder(color.R, color.G, color.B, 255);

    private static uint Bgr888x(SymbolColor color) => InMemoryOrder(color.B, color.G, color.R, 255);

    // The word is stored as a native uint, so a big-endian machine needs it reversed to keep the first byte first.
    private static uint InMemoryOrder(byte first, byte second, byte third, byte fourth)
    {
        var littleEndian = (uint)(first | second << 8 | third << 16 | fourth << 24);
        return BitConverter.IsLittleEndian ? littleEndian : BinaryPrimitives.ReverseEndianness(littleEndian);
    }

    private static byte Luminance(SymbolColor color) => (byte)((77 * color.R + 150 * color.G + 29 * color.B) >> 8);

    // A layout with no alpha shows the colour as it looks over white, so a transparent colour is white and not its bare channels.
    private static SymbolColor OverWhite(SymbolColor color)
    {
        int a = color.A;
        if (a == 255)
            return color;
        return new SymbolColor(
            (byte)((color.R * a + 255 * (255 - a)) / 255),
            (byte)((color.G * a + 255 * (255 - a)) / 255),
            (byte)((color.B * a + 255 * (255 - a)) / 255));
    }

    private static void Fill(Span<byte> span, uint pixel, int bytesPerPixel)
    {
        if (bytesPerPixel == 1)
            span.Fill((byte)pixel);
        else if (bytesPerPixel == 3)
            FillThreeBytes(span, pixel);
        else
            MemoryMarshal.Cast<byte, uint>(span).Fill(pixel);
    }

    /// <summary>
    /// A run of three-byte pixels, which no fill takes as a value: a short run as overlapping words, and a longer one
    /// from its first pixels by copies.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FillThreeBytes(Span<byte> span, uint pixel)
    {
        var count = span.Length / 3;
        if (count == 0)
            return;
        // Up to eight pixels are stored as whole words, each overwriting the fourth byte of the one before.
        // The run's last pixel is never one of them, since its fourth byte would land past the run.
        var words = Math.Min(count - 1, 8);
        for (var i = 0; i < words; i++)
            Unsafe.WriteUnaligned(ref span[i * 3], pixel);
        if (words == count - 1)
        {
            // The word holds the pixel in memory order, so its first byte is the low one only on a little-endian machine.
            if (!BitConverter.IsLittleEndian)
                pixel = BinaryPrimitives.ReverseEndianness(pixel);
            var last = span.Slice(words * 3, 3);
            last[0] = (byte)pixel;
            last[1] = (byte)(pixel >> 8);
            last[2] = (byte)(pixel >> 16);
            return;
        }
        // A longer run: each copy doubles what is written.
        for (var written = words * 3; written < span.Length;)
        {
            var length = Math.Min(written, span.Length - written);
            span.Slice(0, length).CopyTo(span.Slice(written));
            written += length;
        }
    }
}
