using FeatherQR.SkiaSharp;
using SkiaSharp;
namespace FeatherQR.Tests;

/// <summary>
/// Decodes the committed Structured Append corpus (Fixtures/StandardQrStructuredAppend/):
/// two external encoder lineages with matrix and PNG, plus third-party captures that are
/// image-only. Every symbol must decode to its own text and report the header it carries;
/// the set-level test pins the reassembly rules a caller relies on. Regenerate with
/// <c>dotnet run --project tools/QRInteropFixtures -- regenerate-structured-append</c>.
/// </summary>
public class StructuredAppendFixtureTest
{
    private const string Symbology = "StandardQrStructuredAppend";

    public static IEnumerable<string> FixtureIds() => FixtureLoader.EnumerateFixtureIds(Symbology);

    public static IEnumerable<string> MatrixFixtureIds() => FixtureIds().Where(id => File.Exists(FixtureLoader.Load(Symbology, id).MatrixPath));

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

    public static IEnumerable<string> SetIds() => FixtureIds()
        .Select(id => FixtureLoader.Load(Symbology, id).Manifest)
        .Select(m => m.Generator + "/" + m.StructuredAppend!.SetId)
        .Distinct();

    [Test]
    public async Task FixtureCorpus_IsNotEmpty()
    {
        await Assert.That(FixtureIds().Count()).IsGreaterThan(40);
        await Assert.That(FixtureIds().Any(id => !File.Exists(FixtureLoader.Load(Symbology, id).MatrixPath))).IsTrue().Because("the third-party image-only captures must be present");
    }

    [Test]
    [MethodDataSource(nameof(MatrixFixtureIds))]
    public async Task Decode_MatrixFixture_ReportsHeaderAndOwnText(string fixtureId)
    {
        var fixture = FixtureLoader.Load(Symbology, fixtureId);
        var manifest = fixture.Manifest;
        var (modules, size) = FixtureLoader.ReadMatrix(fixture.MatrixPath);

        var success = QRCodeDecoder.TryDecode(modules, size, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{fixtureId}: {info.Status}");
        await Assert.That(text).IsEqualTo(manifest.PayloadText);
        await Assert.That(info.Version).IsEqualTo(manifest.Version);
        await Assert.That(info.EccLevel).IsEqualTo(Enum.Parse<QREccLevel>(manifest.ErrorCorrectionLevel));
        await Assert.That(info.MaskPattern).IsEqualTo(manifest.MaskPattern);
        await Assert.That(info.ErrorsCorrected).IsEqualTo(0);
        await AssertHeader(info, manifest);
    }

    [Test]
    [MethodDataSource(nameof(KnownImagePathDefectIds))]
    public async Task KnownImagePathDefect_StillReproduces(string fixtureId)
    {
        var fixture = FixtureLoader.Load(Symbology, fixtureId);
        var (modules, size) = FixtureLoader.ReadMatrix(fixture.MatrixPath);
        using var bitmap = SKBitmap.Decode(fixture.PngPath);

        await Assert.That(QRCodeDecoder.TryDecode(modules, size, out _, out _)).IsTrue();
        var success = QRCodeDecoder.TryDecode(bitmap, out _, out var info);

        await Assert.That(success).IsFalse().Because("the defect is fixed: remove the entry from KnownImagePathDefects");
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DataUncorrectable);
    }

    [Test]
    [MethodDataSource(nameof(ImageFixtureIds))]
    public async Task Decode_ImageFixture_ReportsHeaderAndOwnText(string fixtureId)
    {
        var fixture = FixtureLoader.Load(Symbology, fixtureId);
        var manifest = fixture.Manifest;
        using var bitmap = SKBitmap.Decode(fixture.PngPath);

        var success = QRCodeDecoder.TryDecode(bitmap, out var text, out var info);

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
    /// The four reassembly rules, on the manifests themselves: one Count and one Parity
    /// per set, indices 0..Count-1 each present once. A corpus that violated them could
    /// not be reassembled by anyone, so this is a guard on the fixtures, not the decoder.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(SetIds))]
    public async Task Set_HasOneCountOneParityAndEveryIndexOnce(string setId)
    {
        var members = FixtureIds()
            .Select(id => FixtureLoader.Load(Symbology, id).Manifest)
            .Where(m => m.Generator + "/" + m.StructuredAppend!.SetId == setId)
            .Select(m => m.StructuredAppend!)
            .ToList();

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
}
