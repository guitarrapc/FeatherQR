using FeatherQR.SkiaSharp;
using SkiaSharp;
namespace FeatherQR.Tests;

/// <summary>
/// The Kanji-mode guarantee, stated once and explicitly: Japanese symbols produced
/// by other libraries decode here, in every symbology, through both the matrix and
/// the image entry point.
/// </summary>
/// <remarks>
/// <para>
/// The per-symbology fixture tests already sweep the whole corpus, so these cases
/// are covered there too. This class exists because that coverage is implicit: if a
/// corpus regeneration ever dropped the Kanji cases, the sweep would still pass with
/// nothing to say. The count assertions below fail instead.
/// </para>
/// <para>
/// These fixtures are the Kanji symbols other encoders wrote; this library's own Kanji
/// output is checked where it is written (the *BinaryEncoderKanjiTest classes and
/// KanjiEligibilityTest). Generators: ZXing.Net (Standard QR,
/// mode self-reported by its encoder) and qrtool (Micro QR M3/M4, rMQR). libzint
/// cannot produce Kanji, it emits Byte mode with ECI 20 for the same input.
/// </para>
/// </remarks>
public class KanjiFixtureTest
{
    private const string KanjiMode = "Kanji";

    private static IEnumerable<string> KanjiFixtureIds(string symbology)
        => FixtureLoader.EnumerateFixtureIds(symbology)
            .Where(id => FixtureLoader.Load(symbology, id).Manifest.Mode == KanjiMode);

    public static IEnumerable<string> StandardQrIds() => KanjiFixtureIds("StandardQr");
    public static IEnumerable<string> MicroQrIds() => KanjiFixtureIds("MicroQR");
    public static IEnumerable<string> RmQrIds() => KanjiFixtureIds("RmQr");

    /// <summary>
    /// The corpus must keep carrying Kanji symbols for every symbology. Without this,
    /// a regeneration that silently lost them would leave the decode path untested.
    /// </summary>
    [Test]
    public async Task Corpus_CarriesKanjiFixturesForEverySymbology()
    {
        await Assert.That(StandardQrIds().Count()).IsGreaterThanOrEqualTo(5).Because("Standard QR Kanji fixtures (zxing-net)");
        await Assert.That(MicroQrIds().Count()).IsGreaterThanOrEqualTo(5).Because("Micro QR Kanji fixtures (qrtool, M3/M4)");
        await Assert.That(RmQrIds().Count()).IsGreaterThanOrEqualTo(4).Because("rMQR Kanji fixtures (qrtool)");
    }

    [Test]
    [MethodDataSource(nameof(StandardQrIds))]
    public async Task StandardQr_KanjiFixture_DecodesFromMatrixAndImage(string fixtureId)
    {
        var fixture = FixtureLoader.Load("StandardQr", fixtureId);
        var manifest = fixture.Manifest;
        var (modules, size) = FixtureLoader.ReadMatrix(fixture.MatrixPath);

        await Assert.That(QRCodeDecoder.TryDecode(modules, size, out var fromMatrix, out var info)).IsTrue();
        await Assert.That(fromMatrix).IsEqualTo(manifest.PayloadText);
        await Assert.That(info.ErrorsCorrected).IsEqualTo(0);

        using var bitmap = SKBitmap.Decode(fixture.PngPath);
        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out var fromImage, out _)).IsTrue();
        await Assert.That(fromImage).IsEqualTo(manifest.PayloadText);
    }

    [Test]
    [MethodDataSource(nameof(MicroQrIds))]
    public async Task MicroQr_KanjiFixture_DecodesFromMatrixAndImage(string fixtureId)
    {
        var fixture = FixtureLoader.Load("MicroQR", fixtureId);
        var manifest = fixture.Manifest;
        var (modules, size) = FixtureLoader.ReadMatrix(fixture.MatrixPath);

        // Kanji mode exists only in M3 and M4; narrower mode indicators cannot express it.
        await Assert.That(manifest.Version).IsGreaterThanOrEqualTo(3);

        await Assert.That(MicroQRCodeDecoder.TryDecode(modules, size, out var fromMatrix, out var info)).IsTrue();
        await Assert.That(fromMatrix).IsEqualTo(manifest.PayloadText);
        await Assert.That(info.ErrorsCorrected).IsEqualTo(0);

        using var bitmap = SKBitmap.Decode(fixture.PngPath);
        await Assert.That(MicroQRCodeDecoder.TryDecode(bitmap, out var fromImage, out _)).IsTrue();
        await Assert.That(fromImage).IsEqualTo(manifest.PayloadText);
    }

    [Test]
    [MethodDataSource(nameof(RmQrIds))]
    public async Task RmQr_KanjiFixture_DecodesFromMatrixAndImage(string fixtureId)
    {
        var fixture = FixtureLoader.Load("RmQr", fixtureId);
        var manifest = fixture.Manifest;
        var (modules, width, height) = FixtureLoader.ReadRectangularMatrix(fixture.MatrixPath);

        await Assert.That(RmQRCodeDecoder.TryDecode(modules, width, height, out var fromMatrix, out _)).IsTrue();
        await Assert.That(fromMatrix).IsEqualTo(manifest.PayloadText);

        using var bitmap = SKBitmap.Decode(fixture.PngPath);
        await Assert.That(RmQRCodeDecoder.TryDecode(bitmap, out var fromImage, out _)).IsTrue();
        await Assert.That(fromImage).IsEqualTo(manifest.PayloadText);
    }

    /// <summary>
    /// The mapping decision, pinned to an external symbol. This fixture holds the seven
    /// Shift_JIS cells where JIS X 0208 and CP932 disagree, with the bytes fixed by the
    /// corpus, so a table rebuilt from CP932 decodes it to the wrong seven characters
    /// and this test says so.
    /// </summary>
    [Test]
    public async Task RmQr_DivergentCellFixture_DecodesWithJisX0208Readings()
    {
        const string Id = "qrtool/r15x59-m-kanji-jisx0208-divergent";
        var fixture = FixtureLoader.Load("RmQr", Id);
        var (modules, width, height) = FixtureLoader.ReadRectangularMatrix(fixture.MatrixPath);

        await Assert.That(RmQRCodeDecoder.TryDecode(modules, width, height, out var text, out _)).IsTrue();

        // JIS X 0208 readings, not the CP932 ones (＼～∥－￠￡￢).
        await Assert.That(text).IsEqualTo("\\〜‖−¢£¬");
        await Assert.That(text).IsEqualTo(fixture.Manifest.PayloadText);
        await Assert.That(text).IsNotEqualTo("＼～∥－￠￡￢");
    }
}
