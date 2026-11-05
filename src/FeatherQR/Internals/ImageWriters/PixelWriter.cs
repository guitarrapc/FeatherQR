using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// A raw pixel buffer for a module matrix, rows packed with no padding.
/// </summary>
/// <remarks>
/// <para>
/// A dark pixel is the dark colour painted over the light one, as the SVG paints its path over its background.
/// <see cref="PixelLayout.Rgba8888"/> and <see cref="PixelLayout.Bgra8888"/> carry each colour straight, alpha not premultiplied.
/// <see cref="PixelLayout.Gray8"/> and <see cref="PixelLayout.Rgb888x"/> have no alpha, so they hold each colour composited over white,
/// as the decoders read a transparent pixel, and Rgb888x's fourth byte is 255. Gray8 then weighs the channels 77, 150 and 29 over 256 (BT.601).
/// So a Gray8 buffer equals the decoders' reading of the Rgba8888 and Rgb888x ones.
/// </para>
/// <para>
/// The first pixel row of each module row is written from the runs of the stored module bits, and the module's other rows are copies of it.
/// </para>
/// </remarks>
internal static class PixelWriter
{
    public static int BufferSize<TView>(in TView view, PixelLayout layout, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        var bytesPerPixel = BytesPerPixel(layout);
        ImageSize.ThrowIfTooLarge(in view, in options);
        long pixels = options.ModulePixelSize;
        // Each side fits an int, so the pixel count fits a long, but four bytes a pixel can wrap it.
        var pixelCount = view.Width * pixels * view.Height * pixels;
        ImageSize.ThrowIfLongerThanAnArray(pixelCount, in options);
        var length = pixelCount * bytesPerPixel;
        ImageSize.ThrowIfLongerThanAnArray(length, in options);
        return (int)length;
    }

    public static bool TryWrite<TView>(in TView view, Span<byte> destination, PixelLayout layout, out int width, out int height, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        var length = BufferSize(in view, layout, in options);
        if (destination.Length < length)
        {
            width = 0;
            height = 0;
            return false;
        }

        var pixels = options.ModulePixelSize;
        var bytesPerPixel = BytesPerPixel(layout);
        width = view.Width * pixels;
        height = view.Height * pixels;
        var rowBytes = width * bytesPerPixel;
        var dark = Pixel(options.DarkColor.Over(options.LightColor), layout);
        var light = Pixel(options.LightColor, layout);
        var quietZone = (view.Width - view.CoreWidth) / 2;
        var coreWidth = view.CoreWidth;
        var core = view.CoreBits;

        for (var m = 0; m < view.Height; m++)
        {
            var first = destination.Slice(m * pixels * rowBytes, rowBytes);
            var coreRow = m - quietZone;
            if ((uint)coreRow >= (uint)view.CoreHeight)
            {
                Fill(first, light, bytesPerPixel);
            }
            else
            {
                var edge = quietZone * pixels * bytesPerPixel;
                Fill(first.Slice(0, edge), light, bytesPerPixel);
                Fill(first.Slice(rowBytes - edge), light, bytesPerPixel);
                var at = edge;
                var start = (long)coreRow * coreWidth;
                var column = 0;
                var isDark = false;
                while (column < coreWidth)
                {
                    var run = PackedRuns.Length(core, start + column, coreWidth - column, isDark);
                    var bytes = run * pixels * bytesPerPixel;
                    Fill(first.Slice(at, bytes), isDark ? dark : light, bytesPerPixel);
                    at += bytes;
                    column += run;
                    isDark = !isDark;
                }
            }

            for (var repeat = 1; repeat < pixels; repeat++)
                first.CopyTo(destination.Slice((m * pixels + repeat) * rowBytes, rowBytes));
        }
        return true;
    }

    private static int BytesPerPixel(PixelLayout layout) => layout switch
    {
        PixelLayout.Gray8 => 1,
        PixelLayout.Rgba8888 or PixelLayout.Bgra8888 or PixelLayout.Rgb888x => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(layout), $"Pixel layout {(int)layout} is not one of the four defined."),
    };

    /// <summary>One pixel's bytes as they lie in memory, read as a native word. Gray8's one byte is the low byte.</summary>
    private static uint Pixel(SymbolColor color, PixelLayout layout) => layout switch
    {
        PixelLayout.Gray8 => Luminance(OverWhite(color)),
        PixelLayout.Rgba8888 => InMemoryOrder(color.R, color.G, color.B, color.A),
        PixelLayout.Bgra8888 => InMemoryOrder(color.B, color.G, color.R, color.A),
        _ => Rgb888x(OverWhite(color)),
    };

    private static uint Rgb888x(SymbolColor color) => InMemoryOrder(color.R, color.G, color.B, 255);

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
        else
            MemoryMarshal.Cast<byte, uint>(span).Fill(pixel);
    }
}
