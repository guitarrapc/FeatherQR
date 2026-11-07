using FeatherQR.SkiaSharp;
using SkiaSharp;
using Style = FeatherQR.Tests.StyledSymbolCases.Style;

namespace FeatherQR.Tests;

/// <summary>
/// A styled image from the core still reads, over the styles the rendering package's <see cref="StyledSymbolDecodabilityTest"/> renders.
/// The finder patterns are drawn apart from the styled modules, since a gap in them breaks the run a reader scans for.
/// </summary>
public class StyledSymbolImageWriterDecodabilityTest
{
    private const string Content = "https://github.com/guitarrapc/FeatherQR";
    private const string ShortContent = "https://githu";

    [Test]
    [MethodDataSource(typeof(StyledSymbolCases), nameof(StyledSymbolCases.ModuleStyles))]
    public async Task StandardQR_Styled_StillDecodes(Style style, int scalePercent)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
        var options = new SymbolImageOptions { ModulePixelSize = 24, ModuleStyle = StyledSymbolCases.ModuleStyleOf(style), ModuleScalePercent = scalePercent };

        await AssertDecodes(qr, options, Content);
        await AssertDecodes(qr, options with { Width = 512, Height = 512 }, Content);
    }

    [Test]
    [MethodDataSource(typeof(StyledSymbolCases), nameof(StyledSymbolCases.ModuleStyles))]
    public async Task MicroQR_Styled_StillDecodes(Style style, int scalePercent)
    {
        var micro = MicroQRCodeGenerator.Create(ShortContent, MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { QuietZoneSize = 2 });
        var options = new SymbolImageOptions { ModulePixelSize = 24, ModuleStyle = StyledSymbolCases.ModuleStyleOf(style), ModuleScalePercent = scalePercent };

        await AssertDecodes(micro, options, ShortContent);
        await AssertDecodes(micro, options with { Width = 504, Height = 504 }, ShortContent);
    }

    /// <summary>The sub-finder and the corner patterns take the module style, as they do in the rendering package, and the symbol still reads.</summary>
    [Test]
    [MethodDataSource(typeof(StyledSymbolCases), nameof(StyledSymbolCases.ModuleStyles))]
    public async Task RmQR_Styled_StillDecodes(Style style, int scalePercent)
    {
        var rm = RmQRCodeGenerator.Create(ShortContent, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x59, QuietZoneSize = 2 });
        var options = new SymbolImageOptions { ModulePixelSize = 10, ModuleStyle = StyledSymbolCases.ModuleStyleOf(style), ModuleScalePercent = scalePercent };

        await AssertDecodes(rm, options, ShortContent);
        await AssertDecodes(rm, options with { Width = 630, Height = 160 }, ShortContent);
    }

    [Test]
    [MethodDataSource(typeof(StyledSymbolCases), nameof(StyledSymbolCases.FinderStyles))]
    public async Task StandardQR_DecorativeFinder_StillDecodes(string finder)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
        var options = new SymbolImageOptions { ModulePixelSize = 24, ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 85, FinderPatternStyle = StyledSymbolCases.FinderStyleOf(finder) };

        await AssertDecodes(qr, options, Content);
        await AssertDecodes(qr, options with { Width = 900, Height = 540 }, Content);
    }

    [Test]
    [MethodDataSource(typeof(StyledSymbolCases), nameof(StyledSymbolCases.FinderStyles))]
    public async Task MicroQR_DecorativeFinder_StillDecodes(string finder)
    {
        var micro = MicroQRCodeGenerator.Create(ShortContent, MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { QuietZoneSize = 2 });
        var options = new SymbolImageOptions { ModulePixelSize = 24, ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 85, FinderPatternStyle = StyledSymbolCases.FinderStyleOf(finder) };

        await AssertDecodes(micro, options, ShortContent);
        await AssertDecodes(micro, options with { Width = 504, Height = 504 }, ShortContent);
    }

    [Test]
    [MethodDataSource(typeof(StyledSymbolCases), nameof(StyledSymbolCases.FinderStyles))]
    public async Task RmQR_DecorativeFinder_StillDecodes(string finder)
    {
        var rm = RmQRCodeGenerator.Create(ShortContent, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x59, QuietZoneSize = 2 });
        var options = new SymbolImageOptions { ModulePixelSize = 10, ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 85, FinderPatternStyle = StyledSymbolCases.FinderStyleOf(finder) };

        await AssertDecodes(rm, options, ShortContent);
        await AssertDecodes(rm, options with { Width = 630, Height = 160 }, ShortContent);
    }

    /// <summary>The PNG, read as a file by the rendering package's decoder, which decodes it through SkiaSharp.</summary>
    [Test]
    [Arguments(ModuleStyle.Circle, 92, FinderPatternStyle.Circle)]
    [Arguments(ModuleStyle.RoundedSquare, 90, FinderPatternStyle.RoundedSquare)]
    [Arguments(ModuleStyle.Square, 75, FinderPatternStyle.RoundedSquareCircle)]
    public async Task StyledPng_DecodesAsAFile(ModuleStyle style, int scalePercent, FinderPatternStyle finder)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M);
        var options = new SymbolImageOptions { ModulePixelSize = 12, ModuleStyle = style, ModuleScalePercent = scalePercent, FinderPatternStyle = finder, DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A), LightColor = new SymbolColor(0xFF, 0xFF, 0xF0) };
        using var bitmap = SKBitmap.Decode(SymbolImageWriter.GetPngBytes(qr, options));

        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out string text, out var info)).IsTrue().Because($"status={info.Status}");
        await Assert.That(text).IsEqualTo(Content);
    }

    private static async Task AssertDecodes(object data, SymbolImageOptions options, string content)
    {
        byte[] gray;
        bool ok;
        int w, h;
        string text;
        DecodeStatus status;
        switch (data)
        {
            case QRCodeData qr:
                gray = new byte[SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Gray8, options)];
                await Assert.That(SymbolImageWriter.TryWritePixels(qr, gray, PixelLayout.Gray8, out w, out h, options)).IsTrue();
                ok = QRCodeDecoder.TryDecodeImage(gray, w, h, out text, out var qrInfo);
                status = qrInfo.Status;
                break;
            case MicroQRCodeData micro:
                gray = new byte[SymbolImageWriter.GetPixelBufferSize(micro, PixelLayout.Gray8, options)];
                await Assert.That(SymbolImageWriter.TryWritePixels(micro, gray, PixelLayout.Gray8, out w, out h, options)).IsTrue();
                ok = MicroQRCodeDecoder.TryDecodeImage(gray, w, h, out text, out var microInfo);
                status = microInfo.Status;
                break;
            case RmQRCodeData rm:
                gray = new byte[SymbolImageWriter.GetPixelBufferSize(rm, PixelLayout.Gray8, options)];
                await Assert.That(SymbolImageWriter.TryWritePixels(rm, gray, PixelLayout.Gray8, out w, out h, options)).IsTrue();
                ok = RmQRCodeDecoder.TryDecodeImage(gray, w, h, out text, out var rmInfo);
                status = rmInfo.Status;
                break;
            default:
                throw new ArgumentException(nameof(data));
        }
        await Assert.That(ok).IsTrue().Because($"{options.ModuleStyle} at {options.ModuleScalePercent} % with {options.FinderPatternStyle} finders, {w}x{h}: status={status}");
        await Assert.That(text).IsEqualTo(content);
    }
}
