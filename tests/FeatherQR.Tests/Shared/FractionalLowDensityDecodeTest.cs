using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// Between 1 and 1.5 px/module a crisp render draws each module 1 or 2 px wide. Every module is still a whole pixel or more, so the symbol is all there, but no run is within half a module of its neighbours' and a grid extrapolated from the finders is off by more than the eighth of a pixel a sample has to spare.
/// </summary>
public class FractionalLowDensityDecodeTest
{
    private const string Content = "FQR 2.0";

    public static IEnumerable<(int, int)> StandardQRSizes()
    {
        foreach (var version in new[] { 1, 3, 7, 10 })
        {
            var modules = 17 + 4 * version + 8;
            for (var sizePx = modules + 1; sizePx < modules * 3 / 2; sizePx++)
                yield return (version, sizePx);
        }
    }

    [Test]
    [MethodDataSource(nameof(StandardQRSizes))]
    public async Task StandardQR_BuilderRender_Decodes(int version, int sizePx)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = version });
        using var bitmap = new QRCodeImageBuilder(qr).WithSize(sizePx, sizePx).ToBitmap();

        var success = QRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"v{version} at {sizePx} px ({sizePx / (float)qr.Size:F2} px/module): {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
        await Assert.That(info.Version).IsEqualTo(version);
        await DrawnCorners.AssertMatch(bitmap, info.Corners, qr.Size - 8, 0.75f);
    }

    public static IEnumerable<(MicroQRVersion, int)> MicroQRSizes()
    {
        foreach (var version in new[] { MicroQRVersion.M2, MicroQRVersion.M3, MicroQRVersion.M4 })
        {
            var modules = 9 + 2 * (int)version + 4;
            for (var sizePx = modules + 1; sizePx < modules * 3 / 2; sizePx++)
                yield return (version, sizePx);
        }
    }

    [Test]
    [MethodDataSource(nameof(MicroQRSizes))]
    public async Task MicroQR_BuilderRender_Decodes(MicroQRVersion version, int sizePx)
    {
        var qr = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = version });
        using var bitmap = new MicroQRCodeImageBuilder(qr).WithSize(sizePx, sizePx).ToBitmap();

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version} at {sizePx} px ({sizePx / (float)qr.Size:F2} px/module): {info.Status}");
        await Assert.That(text).IsEqualTo("12345");
        await DrawnCorners.AssertMatch(bitmap, info.Corners, qr.Size - 4, 0.75f);
    }

    public static IEnumerable<(RmQRVersion, int)> RmQRSizes()
    {
        foreach (var version in new[] { RmQRVersion.R7x43, RmQRVersion.R11x77, RmQRVersion.R13x99, RmQRVersion.R17x139 })
        {
            var qr = RmQRCodeGenerator.Create("12345", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });
            for (var widthPx = qr.Width + 1; widthPx < qr.Width * 3 / 2; widthPx += 3)
                yield return (version, widthPx);
        }
    }

    [Test]
    [MethodDataSource(nameof(RmQRSizes))]
    public async Task RmQR_BuilderRender_Decodes(RmQRVersion version, int widthPx)
    {
        var qr = RmQRCodeGenerator.Create("12345", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });
        var heightPx = (int)MathF.Round(widthPx * (float)qr.Height / qr.Width);
        using var bitmap = new RmQRCodeImageBuilder(qr).WithSize(widthPx, heightPx).ToBitmap();

        var success = RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version} at {widthPx} px ({widthPx / (float)qr.Width:F2} px/module): {info.Status}");
        await Assert.That(text).IsEqualTo("12345");
        await DrawnCorners.AssertMatch(bitmap, info.Corners, qr.Width - 4, 0.75f);
    }
}
