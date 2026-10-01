using SkiaSharp;
using FeatherQR.Internals.MicroQR;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>The Micro QR images of <see cref="DestinationContractTest"/>.</summary>
internal static class MicroQRDestinationRenders
{
    /// <summary>A symbol drawn by the renderer at <paramref name="pixelsPerModule"/>, turned about the centre of a canvas half as wide again.</summary>
    public static (byte[] Luminance, int Width, int Height) Render(string content, MicroQRVersion version, MicroQREccLevel eccLevel, int pixelsPerModule, float degrees)
    {
        var data = MicroQRCodeGenerator.Create(content, eccLevel, new MicroQRCodeGeneratorOptions { Version = version });
        var symbolPx = data.Size * pixelsPerModule;
        var canvasPx = (int)(symbolPx * 1.5f) + 16;
        using var bitmap = new SKBitmap(new SKImageInfo(canvasPx, canvasPx, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.Translate(canvasPx / 2f, canvasPx / 2f);
            canvas.RotateDegrees(degrees);
            canvas.Translate(-symbolPx / 2f, -symbolPx / 2f);
            SymbolRenderer.Render(canvas, SKRect.Create(0, 0, symbolPx, symbolPx), data, SKColors.Black, SKColors.White);
        }
        return (Luminance(bitmap), canvasPx, canvasPx);
    }

    /// <summary>
    /// The big symbol at 8 px/module, whose finder is confirmed on more rows, and the small one at 5, one above the other; the
    /// small one turned about its centre by <paramref name="smallDegrees"/>, in a cell wide enough for any turn.
    /// </summary>
    public static (byte[] Luminance, int Width, int Height) RenderTwo(string bigContent, MicroQRVersion bigVersion, MicroQREccLevel bigLevel, string smallContent, MicroQRVersion smallVersion, MicroQREccLevel smallLevel, bool bigAbove, float smallDegrees = 0f)
    {
        var big = MicroQRCodeGenerator.Create(bigContent, bigLevel, new MicroQRCodeGeneratorOptions { Version = bigVersion });
        var small = MicroQRCodeGenerator.Create(smallContent, smallLevel, new MicroQRCodeGeneratorOptions { Version = smallVersion });
        var bigPx = big.Size * 8;
        var smallPx = small.Size * 5;
        var smallCell = (int)Math.Ceiling(smallPx * 1.5f);
        const int margin = 16;
        var width = Math.Max(bigPx, smallCell) + 2 * margin;
        var height = bigPx + smallCell + 3 * margin;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            var (bigTop, smallTop) = bigAbove ? (margin, 2 * margin + bigPx) : (2 * margin + smallCell, margin);
            SymbolRenderer.Render(canvas, SKRect.Create(margin, bigTop, bigPx, bigPx), big, SKColors.Black, SKColors.White);
            canvas.Save();
            canvas.Translate(margin + smallCell / 2f, smallTop + smallCell / 2f);
            canvas.RotateDegrees(smallDegrees);
            SymbolRenderer.Render(canvas, SKRect.Create(-smallPx / 2f, -smallPx / 2f, smallPx, smallPx), small, SKColors.Black, SKColors.White);
            canvas.Restore();
        }
        return (Luminance(bitmap), width, height);
    }

    /// <summary>
    /// A symbol turned about the image centre, each pixel the mean of 2 × 2 point samples of the modules (dark 20, light 235), then
    /// uniform noise of up to <paramref name="noise"/> drawn from <paramref name="seed"/>, in a square image half as wide again as
    /// the symbol and its quiet zone; transposed, a mirrored capture, as a front camera takes one.
    /// </summary>
    public static (byte[] Luminance, int Width, int Height) RenderTurnedSupersampled(MicroQRVersion version, string content, MicroQREccLevel eccLevel, double pixelsPerModule, double degrees, bool mirrored = false, double noise = 0, int seed = 0)
    {
        var data = MicroQRCodeGenerator.Create(content, eccLevel, new MicroQRCodeGeneratorOptions { Version = version });
        var random = new Random(seed);
        var side = (int)(data.Size * pixelsPerModule * 1.5) + 16;
        var luminance = new byte[side * side];
        var cos = Math.Cos(-degrees * Math.PI / 180);
        var sin = Math.Sin(-degrees * Math.PI / 180);
        var centre = side / 2.0;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var sum = 0.0;
                for (var sy = 0; sy < 2; sy++)
                {
                    for (var sx = 0; sx < 2; sx++)
                    {
                        var px = x + 0.25 + 0.5 * sx - centre;
                        var py = y + 0.25 + 0.5 * sy - centre;
                        var column = (int)Math.Floor((px * cos - py * sin) / pixelsPerModule + data.Size / 2.0);
                        var row = (int)Math.Floor((px * sin + py * cos) / pixelsPerModule + data.Size / 2.0);
                        sum += column >= 0 && row >= 0 && column < data.Size && row < data.Size && data[row, column] ? 20 : 235;
                    }
                }
                var value = noise == 0 ? sum / 4 : sum / 4 + (random.NextDouble() * 2 - 1) * noise;
                luminance[mirrored ? x * side + y : y * side + x] = (byte)Math.Clamp(Math.Round(value), 0, 255);
            }
        }
        return (luminance, side, side);
    }

    /// <summary>An upright symbol, each pixel the module under its centre, quiet zone included.</summary>
    public static (byte[] Luminance, int Width, int Height) RenderNearest(MicroQRVersion version, string content, MicroQREccLevel eccLevel, double pixelsPerModule)
    {
        var data = MicroQRCodeGenerator.Create(content, eccLevel, new MicroQRCodeGeneratorOptions { Version = version });
        var side = (int)Math.Ceiling(data.Size * pixelsPerModule);
        var luminance = new byte[side * side];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var column = Math.Min(data.Size - 1, (int)((x + 0.5) / pixelsPerModule));
                var row = Math.Min(data.Size - 1, (int)((y + 0.5) / pixelsPerModule));
                luminance[y * side + x] = data[row, column] ? (byte)0 : (byte)255;
            }
        }
        return (luminance, side, side);
    }

    /// <summary>
    /// An M4-L symbol carrying <paramref name="text"/> to a thresholded grid and <paramref name="coverageText"/> to one read by
    /// coverage (<see cref="TwoTextRenderer"/>), at 6 px/module, and the texts of those two grids.
    /// </summary>
    public static (byte[] Luminance, int Width, int Height, string Thresholded, string ReadByCoverage) RenderTwoTexts(string text, string coverageText, double degrees)
    {
        const int quietZone = 2;
        var options = new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M4, QuietZoneSize = quietZone, MaskPattern = 0 };
        var first = MicroQRCodeGenerator.Create(text, MicroQREccLevel.L, options);
        var second = MicroQRCodeGenerator.Create(coverageText, MicroQREccLevel.L, options);
        var renderer = new TwoTextRenderer(first.Size, first.Size, pixelsPerModule: 6, offset: 0.25f, degrees);
        var luminance = renderer.Render((row, column) => first[row, column], (row, column) => second[row, column]);
        var (thresholded, readByCoverage) = renderer.SampleGrids(luminance, quietZone);
        var size = first.Size - 2 * quietZone;
        return (luminance, renderer.Width, renderer.Height, DecodeGrid(thresholded, size), DecodeGrid(readByCoverage, size));
    }

    private static string DecodeGrid(byte[] grid, int size)
    {
        var destination = new char[MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4)];
        var status = MicroQRMatrixDecoder.DecodeMatrix(grid, size, destination, out var written, out _);
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
