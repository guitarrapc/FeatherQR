using FeatherQR.SkiaSharp;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// Decodes the committed FNC1 corpus (Fixtures/StandardQrFnc1/ from three writer lineages, Fixtures/RmQrFnc1/
/// from two) with this library's decoders: every symbol must decode to the text the zxing-cpp gate
/// recorded, with the separator as U+001D and nothing else, and report the mode and application indicator the
/// gate recorded on <see cref="QRCodeDecodeInfo.Fnc1"/> or <see cref="RmQRCodeDecodeInfo.Fnc1"/>. The corpus
/// tests pin the shapes a regeneration could silently drop, and the photograph test reads the GS1 symbol of the
/// real-image corpus at the four right angles.
/// Regenerate with <c>dotnet run --project tools/QRInteropFixtures -- regenerate-fnc1</c>.
/// </summary>
public class Fnc1FixtureTest
{
    private const string StandardQr = "StandardQrFnc1";
    private const string RmQr = "RmQrFnc1";

    private static readonly Lazy<IReadOnlyDictionary<string, FixtureManifest>> StandardQrManifests = new(() =>
        FixtureLoader.EnumerateFixtureIds(StandardQr).ToDictionary(id => id, id => FixtureLoader.Load(StandardQr, id).Manifest));

    private static readonly Lazy<IReadOnlyDictionary<string, FixtureManifest>> RmQrManifests = new(() =>
        FixtureLoader.EnumerateFixtureIds(RmQr).ToDictionary(id => id, id => FixtureLoader.Load(RmQr, id).Manifest));

    public static IEnumerable<string> StandardQrIds() => StandardQrManifests.Value.Keys.OrderBy(id => id, StringComparer.Ordinal);

    public static IEnumerable<string> RmQrIds() => RmQrManifests.Value.Keys.OrderBy(id => id, StringComparer.Ordinal);

    public static IEnumerable<int> QuarterTurns() => [0, 1, 2, 3];

    /// <summary>
    /// The shapes the contract needs: three lineages, both positions, each form of the application indicator
    /// (00, a two-digit number, 99, an upper-case and a lower-case letter), FNC1 beside an ECI header from a writer
    /// of each header order, and the two separator pairs that read back other than written, each recorded with
    /// the writer's input beside the reader's text.
    /// </summary>
    [Test]
    public async Task Corpus_HasEveryShapeTheContractNeeds()
    {
        var manifests = StandardQrManifests.Value;
        var all = manifests.Values.ToList();

        await Assert.That(all.Count).IsGreaterThanOrEqualTo(20);
        await Assert.That(all.Select(m => m.Generator).Distinct().OrderBy(g => g, StringComparer.Ordinal)).IsEquivalentTo(["codeglyphx", "zint-libzint", "zxing-net"]);
        await Assert.That(all.All(m => m.Fnc1 is not null)).IsTrue().Because("every fixture of this corpus carries an FNC1 mode");
        await Assert.That(all.Count(m => m.Fnc1!.Position == "First")).IsGreaterThanOrEqualTo(14);
        await Assert.That(all.Count(m => m.Fnc1!.Position == "Second")).IsGreaterThanOrEqualTo(6);
        await Assert.That(all.Where(m => m.Fnc1!.Position == "Second").Select(m => m.Fnc1!.ApplicationIndicator!.Value).OrderBy(i => i)).IsEquivalentTo([0, 37, 37, 99, 165, 222]);
        await Assert.That(all.Where(m => m.Fnc1!.Position == "First").All(m => m.Fnc1!.ApplicationIndicator is null)).IsTrue();
        await Assert.That(all.All(m => m.Fnc1!.SymbologyIdentifier == (m.Fnc1.Position == "First" ? "]Q3" : "]Q5"))).IsTrue();
        await Assert.That(all.Any(m => m.PayloadText.Contains('\u001D'))).IsTrue().Because("the separator is carried as U+001D");

        // An ECI header on each side of FNC1: ZXing.Net writes the standard's order, the other two write FNC1 first.
        // Neither the writers nor the gate report the order, so it is read from the matrix.
        await Assert.That(StandardQrShape("zxing-net/gs1-eci-then-fnc1-utf8-m").Headers).IsEquivalentTo(["ECI:26", "FNC1First"], CollectionOrdering.Matching);
        await Assert.That(StandardQrShape("codeglyphx/gs1-fnc1-then-eci-utf8-m").Headers).IsEquivalentTo(["FNC1First", "ECI:26"], CollectionOrdering.Matching);
        await Assert.That(StandardQrShape("zint-libzint/gs1-fnc1-then-eci-utf8-m").Headers).IsEquivalentTo(["FNC1First", "ECI:26"], CollectionOrdering.Matching);

        // Each data mode under FNC1, as the symbols hold it: a Numeric-only stream, an Alphanumeric segment with
        // the separator as %, and a Byte segment with the separator as 0x1D.
        var shapes = StandardQrIds().Select(StandardQrShape).ToList();
        await Assert.That(shapes.Any(s => s.Headers.Contains("FNC1First") && s.DataModes.Count > 0 && s.DataModes.All(m => m == "Numeric"))).IsTrue().Because("a GS1 symbol of digits only");
        await Assert.That(shapes.Any(s => s.AlphanumericHasPercent)).IsTrue().Because("an Alphanumeric segment with %");
        await Assert.That(shapes.Any(s => s.ByteHasGs)).IsTrue().Because("a Byte segment with 0x1D");
        await Assert.That(StandardQrShape("codeglyphx/gs1-percent-literal-doubled-h").AlphanumericHasPercent).IsTrue();

        // The % rule's edges, each a symbol whose reading is not the writer's input.
        foreach (var (id, expected) in new[]
        {
            ("zxing-net/gs1-alphanumeric-lone-percent-reads-as-gs-l", "123\u001D"),
            ("codeglyphx/gs1-gs-then-percent-reads-percent-then-gs-m", "10AB%\u001DCD"),
            ("codeglyphx/gs1-gs-gs-reads-one-percent-m", "10AB%CD"),
            ("codeglyphx/aim-37-gs-then-percent-reads-percent-then-gs-m", "AB%\u001DCD"),
        })
        {
            await Assert.That(manifests[id].PayloadText).IsEqualTo(expected).Because(id);
            await Assert.That(manifests[id].Fnc1!.WriterInput).IsNotNull().Because($"{id} records the writer's input beside the reader's text");
            await Assert.That(manifests[id].Fnc1!.WriterInput).IsNotEqualTo(expected).Because(id);
        }
        await Assert.That(manifests["codeglyphx/gs1-percent-literal-doubled-h"].PayloadText).Contains("AB%CD");
        await Assert.That(manifests["codeglyphx/gs1-percent-literal-doubled-h"].Fnc1!.WriterInput).IsNull().Because("a doubled literal % reads back as written");
    }

    /// <summary>
    /// The stream read from the matrix with the reference extraction, not with the decoder under test, carries the
    /// headers the manifest claims: one FNC1 indicator in the recorded position with the recorded application
    /// indicator, an ECI 26 header exactly where the manifest says UTF-8, and no Structured Append header.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(StandardQrIds))]
    public async Task StandardQrStream_HeadersAgreeWithTheManifest(string fixtureId)
    {
        var manifest = StandardQrManifests.Value[fixtureId];
        var shape = StandardQrShape(fixtureId);

        await AssertHeaders(shape, manifest);
    }

    [Test]
    [MethodDataSource(nameof(StandardQrIds))]
    public async Task Decode_StandardQrMatrixFixture_ReportsTheModeAndTheText(string fixtureId)
    {
        var fixture = FixtureLoader.Load(StandardQr, fixtureId);
        var manifest = fixture.Manifest;
        var (modules, size) = FixtureLoader.ReadMatrix(fixture.MatrixPath);

        var success = QRCodeDecoder.TryDecode(modules, size, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{fixtureId}: {info.Status}");
        await Assert.That(text).IsEqualTo(manifest.PayloadText);
        await Assert.That(info.Version).IsEqualTo(manifest.Version);
        await Assert.That(info.EccLevel).IsEqualTo(Enum.Parse<QREccLevel>(manifest.ErrorCorrectionLevel));
        await Assert.That(info.MaskPattern).IsEqualTo(manifest.MaskPattern);
        await Assert.That(info.ErrorsCorrected).IsEqualTo(0);
        await AssertFnc1(info.Fnc1, manifest.Fnc1!);
    }

    /// <summary>
    /// Negative control for the <c>ErrorsCorrected == 0</c> assertion above: one flipped data module must decode
    /// with exactly one corrected codeword, and the mode must survive the correction. The bottom-right corner
    /// module carries the first bit of codeword 0 in every Standard QR version.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(StandardQrIds))]
    public async Task Decode_StandardQrMatrixFixtureWithOneFlippedDataModule_ReportsOneCorrectedCodeword(string fixtureId)
    {
        var fixture = FixtureLoader.Load(StandardQr, fixtureId);
        var manifest = fixture.Manifest;
        var (modules, size) = FixtureLoader.ReadMatrix(fixture.MatrixPath);

        modules[(size - 1) * size + (size - 1)] ^= 1;

        var success = QRCodeDecoder.TryDecode(modules, size, out var text, out var info);

        await Assert.That(success).IsTrue().Because(fixtureId);
        await Assert.That(text).IsEqualTo(manifest.PayloadText);
        await Assert.That(info.ErrorsCorrected).IsEqualTo(1).Because(fixtureId);
        await AssertFnc1(info.Fnc1, manifest.Fnc1!);
    }

    /// <summary>
    /// The mode is reported only for a successful decode. A destination too small for the text fails after the
    /// indicator has been read, and the result must not carry it.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(StandardQrIds))]
    public async Task Decode_StandardQrMatrixFixtureIntoTooSmallDestination_LeavesTheModeDefault(string fixtureId)
    {
        var fixture = FixtureLoader.Load(StandardQr, fixtureId);
        var (modules, size) = FixtureLoader.ReadMatrix(fixture.MatrixPath);
        var destination = new char[1];

        var success = QRCodeDecoder.TryDecode(modules, size, destination, out var charsWritten, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(charsWritten).IsEqualTo(0);
        await Assert.That(info.Fnc1).IsEqualTo(default(Fnc1Mode));
    }

    [Test]
    [MethodDataSource(nameof(StandardQrIds))]
    public async Task Decode_StandardQrPngFixture_ReportsTheModeAndTheText(string fixtureId)
    {
        var fixture = FixtureLoader.Load(StandardQr, fixtureId);
        var manifest = fixture.Manifest;
        using var bitmap = SKBitmap.Decode(fixture.PngPath);
        await Assert.That(bitmap).IsNotNull();

        var success = QRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{fixtureId}: {info.Status}");
        await Assert.That(text).IsEqualTo(manifest.PayloadText);
        await Assert.That(info.Version).IsEqualTo(manifest.Version);
        await Assert.That(info.EccLevel).IsEqualTo(Enum.Parse<QREccLevel>(manifest.ErrorCorrectionLevel));
        await AssertFnc1(info.Fnc1, manifest.Fnc1!);
    }

    /// <summary>
    /// The one GS1 symbol of the real-image corpus (GS1 General Specifications figure 4.15.1-2, from zxing-cpp's
    /// samples), which the image decode sweep counted in its content column while FNC1 was unsupported. Its text
    /// file holds the element string with raw separators and no identifier, the form this decoder returns.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(QuarterTurns))]
    public async Task RealImage_Gs1Figure_ReadsAsGs1AtEveryRightAngle(int quarterTurns)
    {
        var path = Path.Combine(FixtureLoader.FixtureRoot, "RealImages", "zxing-cpp-samples", "qrcode-2", "gs1-figure-4.15.1-2.png");
        var expected = File.ReadAllText(Path.ChangeExtension(path, ".txt"));
        await Assert.That(expected).Contains("\u001D").Because("the expected text carries the separators raw");
        using var bitmap = SKBitmap.Decode(path);
        var luminance = new byte[bitmap.Width * bitmap.Height];
        BitmapLuminanceConverter.Convert(bitmap, luminance);
        var (turned, width, height) = NearestNeighbourRenderer.Turn(luminance, bitmap.Width, bitmap.Height, quarterTurns, mirror: false);

        var success = QRCodeDecoder.TryDecodeImage(turned, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"turned {quarterTurns * 90}°: {info.Status}");
        await Assert.That(text).IsEqualTo(expected);
        await Assert.That(info.Fnc1.IsFirstPosition).IsTrue();
        await Assert.That(info.Fnc1.ApplicationIndicator).IsNull();
    }

#if !DEBUG
    /// <summary>
    /// The allocation-free matrix overload stays allocation-free under FNC1: the mode is a value on the result,
    /// and the % rule writes into the caller's destination. Release only, as the other allocation tests are.
    /// </summary>
    [Test]
    [Arguments("zint-libzint/gs1-alphanumeric-m")]
    [Arguments("codeglyphx/aim-37-iso-example-m")]
    public async Task Decode_StandardQrMatrixFixture_IsAllocationFree(string fixtureId)
    {
        var fixture = FixtureLoader.Load(StandardQr, fixtureId);
        var (modules, size) = FixtureLoader.ReadMatrix(fixture.MatrixPath);
        var destination = new char[QRCodeDecoder.GetMaxDecodedLength(fixture.Manifest.Version)];

        for (var i = 0; i < 3; i++)
            QRCodeDecoder.TryDecode(modules, size, destination, out _, out _);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 16; i++)
            QRCodeDecoder.TryDecode(modules, size, destination, out _, out _);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0);
    }
#endif

    // rMQR: both writers of the corpus that produce rMQR FNC1 symbols write first position only, and neither writes second.

    /// <summary>Two lineages, first position only (no writer of the corpus produces rMQR second position), an ECI case, the separator carried, and a bracketed input recorded.</summary>
    [Test]
    public async Task RmQrCorpus_HasEveryShapeTheContractNeeds()
    {
        var all = RmQrManifests.Value.Values.ToList();

        await Assert.That(all.Count).IsGreaterThanOrEqualTo(7);
        await Assert.That(all.Select(m => m.Generator).Distinct().OrderBy(g => g, StringComparer.Ordinal)).IsEquivalentTo(["codeglyphx", "zint-libzint"]);
        await Assert.That(all.All(m => m.SymbolType == "rMQR" && m.Fnc1 is not null && m.Fnc1.Position == "First" && m.Fnc1.SymbologyIdentifier == "]Q3")).IsTrue();
        await Assert.That(all.Any(m => m.PayloadText.Contains('\u001D'))).IsTrue();
        await Assert.That(all.All(m => m.Fnc1!.WriterInput is not null && m.Fnc1.WriterInput.StartsWith('('))).IsTrue().Because("both rMQR writers take bracketed AI syntax");

        // FNC1 before the ECI header (libzint's order), read from the matrix, and the separator in both forms.
        await Assert.That(RmQrShape("zint-libzint/r13x99-m-gs1-fnc1-then-eci-utf8").Headers).IsEquivalentTo(["FNC1First", "ECI:26"], CollectionOrdering.Matching);
        var shapes = RmQrIds().Select(RmQrShape).ToList();
        await Assert.That(shapes.Any(s => s.AlphanumericHasPercent)).IsTrue().Because("an Alphanumeric segment with %");
        await Assert.That(shapes.Any(s => s.ByteHasGs)).IsTrue().Because("a Byte segment with 0x1D");
    }

    [Test]
    [MethodDataSource(nameof(RmQrIds))]
    public async Task RmQrStream_HeadersAgreeWithTheManifest(string fixtureId)
    {
        var manifest = RmQrManifests.Value[fixtureId];
        var shape = RmQrShape(fixtureId);

        await AssertHeaders(shape, manifest);
    }

    [Test]
    [MethodDataSource(nameof(RmQrIds))]
    public async Task Decode_RmQrMatrixFixture_ReportsTheModeAndTheText(string fixtureId)
    {
        var fixture = FixtureLoader.Load(RmQr, fixtureId);
        var manifest = fixture.Manifest;
        var (modules, width, height) = FixtureLoader.ReadRectangularMatrix(fixture.MatrixPath);

        var success = RmQRCodeDecoder.TryDecode(modules, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{fixtureId}: {info.Status}");
        await Assert.That(text).IsEqualTo(manifest.PayloadText);
        await Assert.That((int)info.Version).IsEqualTo(manifest.Version);
        await Assert.That(info.EccLevel).IsEqualTo(Enum.Parse<RmQREccLevel>(manifest.ErrorCorrectionLevel));
        await Assert.That(info.ErrorsCorrected).IsEqualTo(0);
        await AssertFnc1(info.Fnc1, manifest.Fnc1!);
    }

    /// <summary>The flipped-module control, on the first codeword module of the rMQR placement walk.</summary>
    [Test]
    [MethodDataSource(nameof(RmQrIds))]
    public async Task Decode_RmQrMatrixFixtureWithOneFlippedDataModule_ReportsOneCorrectedCodeword(string fixtureId)
    {
        var fixture = FixtureLoader.Load(RmQr, fixtureId);
        var manifest = fixture.Manifest;
        var (modules, width, height) = FixtureLoader.ReadRectangularMatrix(fixture.MatrixPath);

        var (row, col) = RmQrFixtureTest.FirstDataModule(height, width);
        modules[row * width + col] ^= 1;

        var success = RmQRCodeDecoder.TryDecode(modules, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because(fixtureId);
        await Assert.That(text).IsEqualTo(manifest.PayloadText);
        await Assert.That(info.ErrorsCorrected).IsEqualTo(1).Because(fixtureId);
        await AssertFnc1(info.Fnc1, manifest.Fnc1!);
    }

    [Test]
    [MethodDataSource(nameof(RmQrIds))]
    public async Task Decode_RmQrMatrixFixtureIntoTooSmallDestination_LeavesTheModeDefault(string fixtureId)
    {
        var fixture = FixtureLoader.Load(RmQr, fixtureId);
        var (modules, width, height) = FixtureLoader.ReadRectangularMatrix(fixture.MatrixPath);
        var destination = new char[1];

        var success = RmQRCodeDecoder.TryDecode(modules, width, height, destination, out var charsWritten, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(charsWritten).IsEqualTo(0);
        await Assert.That(info.Fnc1).IsEqualTo(default(Fnc1Mode));
    }

    [Test]
    [MethodDataSource(nameof(RmQrIds))]
    public async Task Decode_RmQrPngFixture_ReportsTheModeAndTheText(string fixtureId)
    {
        var fixture = FixtureLoader.Load(RmQr, fixtureId);
        var manifest = fixture.Manifest;
        using var bitmap = SKBitmap.Decode(fixture.PngPath);
        await Assert.That(bitmap).IsNotNull();

        var success = RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{fixtureId}: {info.Status}");
        await Assert.That(text).IsEqualTo(manifest.PayloadText);
        await Assert.That((int)info.Version).IsEqualTo(manifest.Version);
        await Assert.That(info.EccLevel).IsEqualTo(Enum.Parse<RmQREccLevel>(manifest.ErrorCorrectionLevel));
        await AssertFnc1(info.Fnc1, manifest.Fnc1!);
    }

#if !DEBUG
    [Test]
    [Arguments("zint-libzint/r17x77-h-gs1-mixed-case-serial")]
    public async Task Decode_RmQrMatrixFixture_IsAllocationFree(string fixtureId)
    {
        var fixture = FixtureLoader.Load(RmQr, fixtureId);
        var (modules, width, height) = FixtureLoader.ReadRectangularMatrix(fixture.MatrixPath);
        var destination = new char[RmQRCodeDecoder.GetMaxDecodedLength((RmQRVersion)fixture.Manifest.Version)];

        for (var i = 0; i < 3; i++)
            RmQRCodeDecoder.TryDecode(modules, width, height, destination, out _, out _);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 16; i++)
            RmQRCodeDecoder.TryDecode(modules, width, height, destination, out _, out _);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0);
    }
#endif

    private static Fnc1StreamShape StandardQrShape(string fixtureId)
    {
        var fixture = FixtureLoader.Load(StandardQr, fixtureId);
        var (modules, size) = FixtureLoader.ReadMatrix(fixture.MatrixPath);
        return Fnc1StreamShape.OfStandardQr(modules, size, fixture.Manifest.Version, Enum.Parse<QREccLevel>(fixture.Manifest.ErrorCorrectionLevel), fixture.Manifest.MaskPattern);
    }

    private static Fnc1StreamShape RmQrShape(string fixtureId)
    {
        var fixture = FixtureLoader.Load(RmQr, fixtureId);
        var (modules, width, height) = FixtureLoader.ReadRectangularMatrix(fixture.MatrixPath);
        return Fnc1StreamShape.OfRmQr(modules, width, height, (RmQRVersion)fixture.Manifest.Version, Enum.Parse<RmQREccLevel>(fixture.Manifest.ErrorCorrectionLevel));
    }

    private static async Task AssertHeaders(Fnc1StreamShape shape, FixtureManifest manifest)
    {
        var fnc1 = manifest.Fnc1!;
        var expectedFnc1 = fnc1.Position == "First" ? "FNC1First" : $"FNC1Second:{fnc1.ApplicationIndicator}";
        await Assert.That(shape.Headers.Count(h => h.StartsWith("FNC1", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(shape.Headers).Contains(expectedFnc1);
        await Assert.That(shape.Headers.Count(h => h.StartsWith("ECI:", StringComparison.Ordinal))).IsEqualTo(manifest.EciCharset is null ? 0 : 1);
        if (manifest.EciCharset is not null)
            await Assert.That(shape.Headers).Contains("ECI:26").Because($"the manifest says {manifest.EciCharset}");
        await Assert.That(shape.Headers).DoesNotContain("SA");
        await Assert.That(shape.DataModes.Count).IsGreaterThan(0);
    }

    private static async Task AssertFnc1(Fnc1Mode actual, Fnc1Manifest expected)
    {
        await Assert.That(actual.IsFirstPosition).IsEqualTo(expected.Position == "First");
        await Assert.That(actual.IsSecondPosition).IsEqualTo(expected.Position == "Second");
        await Assert.That(actual.ApplicationIndicator).IsEqualTo(expected.ApplicationIndicator);
    }
}
