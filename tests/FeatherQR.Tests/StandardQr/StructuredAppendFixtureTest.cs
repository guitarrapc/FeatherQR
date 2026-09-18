using FeatherQR.SkiaSharp;
using SkiaSharp;
namespace FeatherQR.Tests;

/// <summary>
/// Decodes the committed Structured Append corpus (Fixtures/StandardQrStructuredAppend/):
/// two external encoder lineages with matrix and PNG, plus third-party captures that are
/// image-only. Every symbol must decode to its own text and report the header it carries;
/// the set-level test pins the reassembly rules a caller relies on, and the corpus test
/// pins the shapes the contract needs so a regeneration cannot drop them silently.
/// Regenerate with <c>dotnet run --project tools/QRInteropFixtures -- regenerate-structured-append</c>.
/// </summary>
public class StructuredAppendFixtureTest
{
    private const string Symbology = "StandardQrStructuredAppend";

    // Every manifest is parsed once per process; data sources and set-level tests read from here.
    private static readonly Lazy<IReadOnlyDictionary<string, FixtureManifest>> Manifests = new(() =>
        FixtureLoader.EnumerateFixtureIds(Symbology).ToDictionary(id => id, id => FixtureLoader.Load(Symbology, id).Manifest));

    public static IEnumerable<string> FixtureIds() => Manifests.Value.Keys.OrderBy(id => id, StringComparer.Ordinal);

    public static IEnumerable<string> MatrixFixtureIds() => FixtureIds().Where(id => File.Exists(MatrixPath(id)));

    public static IEnumerable<string> SetIds() => Manifests.Value.Values.Select(SetKey).Distinct().OrderBy(k => k, StringComparer.Ordinal);

    /// <summary>
    /// Symbols this library's image path cannot read today, for a reason that is not
    /// Structured Append: the finder locator picks a false candidate three modules inside
    /// the real top-right finder of this one symbol at 5 px/module and up (follow-up F10
    /// in the 2.0.0 plan). The matrix path reads it. Listed here rather than skipped so
    /// that <see cref="KnownImagePathDefect_StillReproduces"/> fails the day it is fixed.
    /// </summary>
    private static readonly string[] KnownImagePathDefects = ["codeglyphx/sixteen-symbols-max-v2-l-2of16"];

    public static IEnumerable<string> ImageFixtureIds() => FixtureIds().Where(id => !KnownImagePathDefects.Contains(id));

    public static IEnumerable<string> KnownImagePathDefectIds() => KnownImagePathDefects;

    /// <summary>
    /// The shapes the contract needs, each of which a regeneration could lose without any
    /// per-symbol test noticing: both encoder lineages and the captures, the smallest and
    /// the largest set, a Kanji-mode set, and the two texts that carry a different parity
    /// in each lineage because the lineages write them in different charsets.
    /// </summary>
    [Test]
    public async Task Corpus_HasEveryShapeTheContractNeeds()
    {
        var manifests = Manifests.Value.Values.ToList();

        await Assert.That(manifests.Count).IsGreaterThan(40);
        await Assert.That(MatrixFixtureIds().Count()).IsGreaterThan(40).Because("the matrix path must keep finding its files; an empty data source would pass every matrix test vacuously");
        await Assert.That(manifests.Select(m => m.Generator).Distinct()).Contains("qrcodegenerator");
        await Assert.That(manifests.Select(m => m.Generator).Distinct()).Contains("codeglyphx");
        await Assert.That(manifests.Select(m => m.Generator).Distinct()).Contains("zxing-cpp-samples");
        await Assert.That(FixtureIds().Any(id => !File.Exists(MatrixPath(id)))).IsTrue().Because("the third-party captures are image-only");
        await Assert.That(manifests.Select(m => m.StructuredAppend!.Count).Distinct()).Contains(2);
        await Assert.That(manifests.Select(m => m.StructuredAppend!.Count).Distinct()).Contains(16);
        await Assert.That(manifests.Any(m => m.Mode == "Kanji")).IsTrue();

        foreach (var setId in new[] { "byte-latin1-diacritics-v3-m", "byte-utf8-japanese-v5-m" })
        {
            var parities = manifests.Where(m => m.StructuredAppend!.SetId == setId).GroupBy(m => m.Generator).Select(g => g.First().StructuredAppend!.Parity).ToList();
            await Assert.That(parities.Count).IsEqualTo(2).Because($"{setId} must exist in both encoder lineages");
            await Assert.That(parities[0]).IsNotEqualTo(parities[1]).Because($"{setId} is carried in a different charset by each lineage");
        }
    }

    [Test]
    [MethodDataSource(nameof(MatrixFixtureIds))]
    public async Task Decode_MatrixFixture_ReportsHeaderAndOwnText(string fixtureId)
    {
        var manifest = Manifests.Value[fixtureId];
        var (modules, size) = FixtureLoader.ReadMatrix(MatrixPath(fixtureId));

        var success = QRCodeDecoder.TryDecode(modules, size, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{fixtureId}: {info.Status}");
        await Assert.That(text).IsEqualTo(manifest.PayloadText);
        await Assert.That(info.Version).IsEqualTo(manifest.Version);
        await Assert.That(info.EccLevel).IsEqualTo(Enum.Parse<QREccLevel>(manifest.ErrorCorrectionLevel));
        await Assert.That(info.MaskPattern).IsEqualTo(manifest.MaskPattern);
        await Assert.That(info.ErrorsCorrected).IsEqualTo(0);
        await AssertHeader(info, manifest);
    }

    /// <summary>
    /// Negative control for the <c>ErrorsCorrected == 0</c> assertion above: one flipped data
    /// module must decode with exactly one corrected codeword, and the header must survive
    /// the correction. The bottom-right corner module is a data module on every version.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(MatrixFixtureIds))]
    public async Task Decode_MatrixFixtureWithOneFlippedDataModule_ReportsOneCorrectedCodeword(string fixtureId)
    {
        var manifest = Manifests.Value[fixtureId];
        var (modules, size) = FixtureLoader.ReadMatrix(MatrixPath(fixtureId));

        modules[(size - 1) * size + (size - 1)] ^= 1;

        var success = QRCodeDecoder.TryDecode(modules, size, out var text, out var info);

        await Assert.That(success).IsTrue().Because(fixtureId);
        await Assert.That(text).IsEqualTo(manifest.PayloadText);
        await Assert.That(info.ErrorsCorrected).IsEqualTo(1).Because(fixtureId);
        await AssertHeader(info, manifest);
    }

    /// <summary>
    /// The header is reported only for a successful decode. A destination too small for
    /// the text fails after the header has been read, and the result must not carry it.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(MatrixFixtureIds))]
    public async Task Decode_MatrixFixtureIntoTooSmallDestination_LeavesTheHeaderEmpty(string fixtureId)
    {
        var (modules, size) = FixtureLoader.ReadMatrix(MatrixPath(fixtureId));
        var destination = new char[1];

        var success = QRCodeDecoder.TryDecode(modules, size, destination, out _, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(info.StructuredAppend.IsEmpty).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(KnownImagePathDefectIds))]
    public async Task KnownImagePathDefect_StillReproduces(string fixtureId)
    {
        var (modules, size) = FixtureLoader.ReadMatrix(MatrixPath(fixtureId));
        using var bitmap = SKBitmap.Decode(PngPath(fixtureId));

        await Assert.That(QRCodeDecoder.TryDecode(modules, size, out _, out _)).IsTrue();
        var success = QRCodeImageDecoder.TryDecode(bitmap, out _, out var info);

        await Assert.That(success).IsFalse().Because("the defect is fixed: remove the entry from KnownImagePathDefects");
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DataUncorrectable).Because("the failure mode changed: re-measure F10");
    }

    [Test]
    [MethodDataSource(nameof(ImageFixtureIds))]
    public async Task Decode_ImageFixture_ReportsHeaderAndOwnText(string fixtureId)
    {
        var manifest = Manifests.Value[fixtureId];
        using var bitmap = SKBitmap.Decode(PngPath(fixtureId));

        var success = QRCodeImageDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{fixtureId}: {info.Status}");
        await Assert.That(text).IsEqualTo(manifest.PayloadText);
        await Assert.That(info.Version).IsEqualTo(manifest.Version);
        await Assert.That(info.EccLevel).IsEqualTo(Enum.Parse<QREccLevel>(manifest.ErrorCorrectionLevel));
        if (manifest.MaskPattern >= 0)
        {
            await Assert.That(info.MaskPattern).IsEqualTo(manifest.MaskPattern);
        }
        await AssertHeader(info, manifest);
    }

    /// <summary>
    /// The reassembly rules, on the manifests themselves: one Count and one Parity per set,
    /// indices 0..Count-1 each present once. A corpus that violated them could not be
    /// reassembled by anyone, so this is a guard on the fixtures, not the decoder.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(SetIds))]
    public async Task Set_HasOneCountOneParityAndEveryIndexOnce(string setKey)
    {
        var members = Manifests.Value.Values.Where(m => SetKey(m) == setKey).Select(m => m.StructuredAppend!).ToList();

        var count = members[0].Count;
        await Assert.That(members.Count).IsEqualTo(count);
        await Assert.That(members.Select(m => m.Count).Distinct().Count()).IsEqualTo(1);
        await Assert.That(members.Select(m => m.Parity).Distinct().Count()).IsEqualTo(1);
        await Assert.That(members.Select(m => m.Index).OrderBy(i => i)).IsEquivalentTo(Enumerable.Range(0, count));
        await Assert.That(count).IsBetween(2, 16);
    }

    private static async Task AssertHeader(QRCodeDecodeInfo info, FixtureManifest manifest)
    {
        var expected = manifest.StructuredAppend!;
        await Assert.That(info.StructuredAppend.IsEmpty).IsFalse();
        await Assert.That(info.StructuredAppend.Index).IsEqualTo(expected.Index);
        await Assert.That(info.StructuredAppend.Count).IsEqualTo(expected.Count);
        await Assert.That((int)info.StructuredAppend.Parity).IsEqualTo(expected.Parity);
    }

    private static string SetKey(FixtureManifest m) => m.Generator + "/" + m.StructuredAppend!.SetId;

    private static string MatrixPath(string fixtureId) => Path.Combine(FixtureLoader.FixtureRoot, Symbology, fixtureId.Replace('/', Path.DirectorySeparatorChar) + ".matrix.txt");

    private static string PngPath(string fixtureId) => Path.Combine(FixtureLoader.FixtureRoot, Symbology, fixtureId.Replace('/', Path.DirectorySeparatorChar) + ".png");
}
