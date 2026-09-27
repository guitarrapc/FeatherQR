using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// Symbols whose dark rings are printed or read thicker or thinner than their modules: a finder the ratio refuses on some line is found once that line's like edges read it (<c>FinderPatternFinder.IsFinderRatioByLikeEdges</c>).
/// </summary>
public class SpreadRingDecodeTest
{
    private const string Content = "FQR SPREAD 0123";

    /// <summary>
    /// Every dark edge moved by a fifth or a seventh of a module, turned; each render fails on the commit before and reads under nudges of ±0.05 px/module, ±0.5° and ±0.01 of a module.
    /// </summary>
    [Test]
    [Arguments(1, 3f, 23f, -0.2f, false)]
    [Arguments(3, 5f, 23f, 0.2f, false)]
    [Arguments(5, 5f, 23f, -0.2f, false)]
    [Arguments(5, 5f, 23f, 0.2f, true)]
    [Arguments(10, 3f, 11f, 0.15f, false)]
    [Arguments(10, 3f, 23f, -0.15f, true)]
    public async Task SpreadSymbol_Decodes(int version, float pixelsPerModule, float degrees, float spread, bool binarized)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version) });
        var (luminance, width, height) = SpreadRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule, degrees, spread, binarized);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(Content);
    }

    /// <summary>The like edges find a finder and read nothing: a spread symbol with its data destroyed does not decode.</summary>
    [Test]
    [Arguments(5f, 23f, 0.2f, true)]
    [Arguments(5f, 23f, -0.2f, false)]
    public async Task SpreadSymbolWithItsDataDestroyed_DoesNotDecode(float pixelsPerModule, float degrees, float spread, bool binarized)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(5) });
        // Every module right of column 8 and below row 8 of the symbol, clear of the finders and both format copies
        bool IsDark(int row, int column) => row >= 13 && column >= 13 && row < qr.Size - 4 && column < qr.Size - 4 ? !qr[row, column] : qr[row, column];
        var (luminance, width, height) = SpreadRenderer.Render(IsDark, qr.Size, qr.Size, pixelsPerModule, degrees, spread, binarized);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsFalse().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(string.Empty);
    }

    /// <summary>Photographs and captures from the real-image fixtures (Apache-2.0) whose finders a line's own ratio refused: rings blurred thin, printed thick, a screen's glow read inverted.</summary>
    public static IEnumerable<(string, string, int)> RealImages()
    {
        yield return ("qrcode-3", "30.webp", 0);
        yield return ("qrcode-3", "30.webp", 1);
        yield return ("qrcode-3", "30.webp", 2);
        yield return ("qrcode-4", "21.webp", 1);
        yield return ("qrcode-4", "21.webp", 3);
        yield return ("qrcode-2", "qr-inv-1.webp", 0);
        yield return ("qrcode-2", "qr-inv-1.webp", 2);
        yield return ("qrcode-2", "#1132.webp", 2);
        yield return ("qrcode-1", "17.webp", 2);
    }

    [Test]
    [MethodDataSource(nameof(RealImages))]
    public async Task RealImageWithSpreadRings_Decodes(string set, string file, int quarterTurns)
    {
        var path = Path.Combine(FixtureLoader.FixtureRoot, "RealImages", "zxing-cpp-samples", set, file);
        var expected = File.ReadAllText(Path.ChangeExtension(path, ".txt"));
        using var bitmap = SKBitmap.Decode(path);
        var luminance = new byte[bitmap.Width * bitmap.Height];
        BitmapLuminanceConverter.Convert(bitmap, luminance);
        var (turned, width, height) = NearestNeighbourRenderer.Turn(luminance, bitmap.Width, bitmap.Height, quarterTurns, mirror: false);

        var success = QRCodeDecoder.TryDecodeImage(turned, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{set}/{file} turned {quarterTurns * 90}°: {info.Status}");
        await Assert.That(text).IsEqualTo(expected);
    }
}
