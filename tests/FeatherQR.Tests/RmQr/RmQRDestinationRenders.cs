using SkiaSharp;
using FeatherQR.Internals.RmQR;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>The rMQR images of <see cref="DestinationContractTest"/>.</summary>
internal static class RmQRDestinationRenders
{
    /// <summary>A symbol drawn by the image builder at <paramref name="pixelsPerModule"/>, upright.</summary>
    public static (byte[] Luminance, int Width, int Height) Render(string content, RmQRVersion version, int pixelsPerModule)
    {
        using var bitmap = RmQRCodeDecoderImageTest.RenderBitmap(RmQRCodeDecoderImageTest.Create(content, RmQREccLevel.M, version), pixelsPerModule);
        return (Luminance(bitmap), bitmap.Width, bitmap.Height);
    }

    /// <summary>A symbol at 8 px/module turned about the centre of a canvas as wide as its diagonal.</summary>
    public static (byte[] Luminance, int Width, int Height) RenderRotated(string content, RmQRVersion version, float degrees)
    {
        using var bitmap = RmQRCodeDecoderImageTest.RenderRotated(content, version, RmQREccLevel.M, degrees);
        return (Luminance(bitmap), bitmap.Width, bitmap.Height);
    }

    /// <summary>Each pixel the module under its centre, quiet zone included.</summary>
    public static (byte[] Luminance, int Width, int Height) RenderNearest(RmQRCodeData data, float pixelsPerModule)
    {
        var width = (int)Math.Ceiling(data.Width * pixelsPerModule);
        var height = (int)Math.Ceiling(data.Height * pixelsPerModule);
        var luminance = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var column = Math.Min(data.Width - 1, (int)((x + 0.5) / pixelsPerModule));
                var row = Math.Min(data.Height - 1, (int)((y + 0.5) / pixelsPerModule));
                luminance[y * width + x] = data[row, column] ? (byte)0 : (byte)255;
            }
        }
        return (luminance, width, height);
    }

    /// <summary>The image flipped left to right: a mirrored capture.</summary>
    public static (byte[] Luminance, int Width, int Height) FlipHorizontally((byte[] Luminance, int Width, int Height) image)
    {
        var (luminance, width, height) = image;
        var flipped = new byte[luminance.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                flipped[y * width + width - 1 - x] = luminance[y * width + x];
        }
        return (flipped, width, height);
    }

    /// <summary>The big symbol at 6 px/module, whose finder is confirmed on more rows, and the small one at 4, one above the other in either order.</summary>
    public static (byte[] Luminance, int Width, int Height) RenderStacked(RmQRCodeData big, RmQRCodeData small, bool bigAbove)
    {
        using var bigBitmap = RmQRCodeDecoderImageTest.RenderBitmap(big, modulePixelSize: 6);
        using var smallBitmap = RmQRCodeDecoderImageTest.RenderBitmap(small, modulePixelSize: 4);
        var width = Math.Max(bigBitmap.Width, smallBitmap.Width) + 24;
        var height = bigBitmap.Height + smallBitmap.Height + 36;
        using var canvas = new SKBitmap(width, height);
        using (var surface = new SKCanvas(canvas))
        {
            surface.Clear(SKColors.White);
            var (top, bottom) = bigAbove ? (bigBitmap, smallBitmap) : (smallBitmap, bigBitmap);
            surface.DrawBitmap(top, 12, 12, SKSamplingOptions.Default);
            surface.DrawBitmap(bottom, 12, 12 + top.Height + 12, SKSamplingOptions.Default);
        }
        return (Luminance(canvas), width, height);
    }

    /// <summary>
    /// <paramref name="upright"/> at 6 px/module, whose finder is confirmed on more rows, above <paramref name="turned"/>
    /// at 4 px/module, turned about its centre.
    /// </summary>
    public static (byte[] Luminance, int Width, int Height) RenderUprightAndTurned(RmQRCodeData upright, RmQRCodeData turned, float degrees)
    {
        const int margin = 24;
        var uprightWidth = upright.Width * 6;
        var uprightHeight = upright.Height * 6;
        var turnedWidth = turned.Width * 4;
        var turnedHeight = turned.Height * 4;
        var turnedSide = (int)Math.Ceiling(Math.Sqrt(turnedWidth * turnedWidth + turnedHeight * turnedHeight));
        var width = Math.Max(uprightWidth, turnedSide) + 2 * margin;
        var height = uprightHeight + turnedSide + 3 * margin;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            SymbolRenderer.Render(canvas, SKRect.Create(margin, margin, uprightWidth, uprightHeight), upright, SKColors.Black, SKColors.White);
            canvas.Translate(margin + turnedSide / 2f, uprightHeight + 2 * margin + turnedSide / 2f);
            canvas.RotateDegrees(degrees);
            canvas.Translate(-turnedWidth / 2f, -turnedHeight / 2f);
            SymbolRenderer.Render(canvas, SKRect.Create(0, 0, turnedWidth, turnedHeight), turned, SKColors.Black, SKColors.White);
        }
        return (Luminance(bitmap), width, height);
    }

    /// <summary>
    /// A symbol carrying <paramref name="text"/> to a thresholded grid and <paramref name="coverageText"/> to one read by coverage
    /// (<see cref="TwoTextRenderer"/>), upright, and the texts of those two grids.
    /// </summary>
    public static (byte[] Luminance, int Width, int Height, string Thresholded, string ReadByCoverage) RenderTwoTexts(RmQRVersion version, RmQREccLevel eccLevel, string text, string coverageText, int pixelsPerModule, float offset)
    {
        const int quietZone = 2;
        var first = RmQRCodeGenerator.Create(text, eccLevel, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });
        var second = RmQRCodeGenerator.Create(coverageText, eccLevel, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });
        var renderer = new TwoTextRenderer(first.Width, first.Height, pixelsPerModule, offset, degrees: 0);
        var luminance = renderer.Render((row, column) => first[row, column], (row, column) => second[row, column]);
        var (thresholded, readByCoverage) = renderer.SampleGrids(luminance, quietZone);
        var (columns, rows) = (first.Width - 2 * quietZone, first.Height - 2 * quietZone);
        return (luminance, renderer.Width, renderer.Height, DecodeGrid(thresholded, columns, rows), DecodeGrid(readByCoverage, columns, rows));
    }

    private static string DecodeGrid(byte[] grid, int columns, int rows)
    {
        var destination = new char[RmQRCodeDecoder.GetMaxDecodedLength(RmQRVersion.R17x139)];
        var status = RmQRMatrixDecoder.DecodeMatrix(grid, columns, rows, destination, out var written, out _);
        return status == DecodeStatus.Success ? new string(destination, 0, written) : status.ToString();
    }

    private static byte[] Luminance(SKBitmap bitmap)
    {
        var luminance = new byte[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
                luminance[y * bitmap.Width + x] = bitmap.GetPixel(x, y).Red;
        }
        return luminance;
    }
}
