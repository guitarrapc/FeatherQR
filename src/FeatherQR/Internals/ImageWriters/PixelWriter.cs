using System.Buffers.Binary;

namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// A raw pixel buffer for a module matrix, rows packed with no padding.
/// </summary>
/// <remarks>
/// <para>
/// The colours go in straight: alpha is not premultiplied, and <see cref="PixelLayout.Rgb888x"/>'s fourth byte is 255.
/// <see cref="PixelLayout.Gray8"/> holds each colour's luminance the way the decoders compute it from an RGBA buffer: an alpha below 255
/// composited over white, then BT.601 weights of 77, 150 and 29 over 256. So a Gray8 buffer and the decoders' reading of the RGBA one agree.
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
        var length = view.Width * pixels * view.Height * pixels * bytesPerPixel;
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
        var dark = Pixel(options.DarkColor, layout);
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

    /// <summary>One pixel's bytes in their order in memory, the first in the low byte.</summary>
    private static uint Pixel(SymbolColor color, PixelLayout layout) => layout switch
    {
        PixelLayout.Gray8 => Luminance(color),
        PixelLayout.Rgba8888 => (uint)(color.R | color.G << 8 | color.B << 16 | color.A << 24),
        PixelLayout.Bgra8888 => (uint)(color.B | color.G << 8 | color.R << 16 | color.A << 24),
        _ => (uint)(color.R | color.G << 8 | color.B << 16 | 255 << 24),
    };

    private static byte Luminance(SymbolColor color)
    {
        int r = color.R, g = color.G, b = color.B, a = color.A;
        if (a != 255)
        {
            r = (r * a + 255 * (255 - a)) / 255;
            g = (g * a + 255 * (255 - a)) / 255;
            b = (b * a + 255 * (255 - a)) / 255;
        }
        return (byte)((77 * r + 150 * g + 29 * b) >> 8);
    }

    private static void Fill(Span<byte> span, uint pixel, int bytesPerPixel)
    {
        if (bytesPerPixel == 1)
        {
            span.Fill((byte)pixel);
            return;
        }
        for (var i = 0; i < span.Length; i += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(i), pixel);
    }
}
