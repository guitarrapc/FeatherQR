using TUnit.Assertions.Enums;
using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;
namespace FeatherQR.Tests;

/// <summary>
/// This library's Kanji output against the Kanji symbols other encoders wrote (kanji-encoding-plan.md, phase 6.6).
/// </summary>
/// <remarks>
/// <para>
/// A single Kanji segment at a fixed version and level has one valid stream, so another encoder's symbol of the same text is an oracle for the whole of this library's: the stream, the error correction, the placement and the format.
/// The mask is the one thing an encoder chooses, so Standard QR and Micro QR pin it to the one the fixture's manifest records (ZXing.Net reports its own; for qrtool's Micro QR symbols the zxing-cpp gate read it); rMQR has one mask.
/// </para>
/// <para>
/// qrtool 0.13.2 never writes the last h − 10 modules of an rMQR placement walk (column 1, rows 8 to h − 3) on symbols 11 or more modules high, a defect recorded in qrcode-test-fixtures.md; those modules are left out of the comparison and every other module must match.
/// A fixture whose text has a character without an encoder cell (the seven cells CP932 reads differently) is not Kanji output of this library and is left out, by name.
/// </para>
/// </remarks>
public class KanjiEncoderOracleTest
{
    /// <summary>The Kanji fixture whose text this library does not write in Kanji mode: the seven divergent cells (K3).</summary>
    private const string DivergentCells = "qrtool/r15x59-m-kanji-jisx0208-divergent";

    private static bool HasEncoderCells(string text)
        => text.All(c => c < 0x80 || ShiftJisKanjiReverseTable.Lookup(c) >= 0);

    public static IEnumerable<string> StandardQrIds() => KanjiFixtureTest.StandardQrIds();
    public static IEnumerable<string> MicroQrIds() => KanjiFixtureTest.MicroQrIds();
    public static IEnumerable<string> RmQrIds() => KanjiFixtureTest.RmQrIds().Where(id => id != DivergentCells);

    [Test]
    public async Task OnlyTheDivergentCellsFixture_IsLeftOut()
    {
        var left = KanjiFixtureTest.StandardQrIds().Select(id => ("StandardQr", id))
            .Concat(KanjiFixtureTest.MicroQrIds().Select(id => ("MicroQR", id)))
            .Concat(KanjiFixtureTest.RmQrIds().Select(id => ("RmQr", id)))
            .Where(f => !HasEncoderCells(FixtureLoader.Load(f.Item1, f.Item2).Manifest.PayloadText))
            .Select(f => f.Item2)
            .ToArray();
        await Assert.That(left).IsEquivalentTo(new[] { DivergentCells });
    }

    // ---- Coverage: every count-indicator width of every symbology -------------------

    /// <summary>Standard QR's Kanji count indicator is 8, 10 or 12 bits by version band (1-9, 10-26, 27-40); each band has a fixture.</summary>
    [Test]
    public async Task StandardQr_KanjiFixtures_CoverEveryCountWidth()
    {
        var widths = StandardQrIds()
            .Select(id => EncodingMode.Kanji.GetCountIndicatorLength(FixtureLoader.Load("StandardQr", id).Manifest.Version))
            .Distinct()
            .Order()
            .ToArray();
        await Assert.That(widths).IsEquivalentTo(new[] { 8, 10, 12 }, CollectionOrdering.Matching);
    }

    /// <summary>Micro QR's Kanji count indicator is 3 bits at M3 and 4 at M4; each has a fixture.</summary>
    [Test]
    public async Task MicroQr_KanjiFixtures_CoverEveryCountWidth()
    {
        var versions = MicroQrIds().Select(id => FixtureLoader.Load("MicroQR", id).Manifest.Version).Distinct().Order().ToArray();
        await Assert.That(versions).IsEquivalentTo(new[] { 3, 4 }, CollectionOrdering.Matching);
    }

    /// <summary>
    /// rMQR's Kanji count indicator is 2 to 7 bits by version (ISO/IEC 23941 Table 3). Each width has a fixture this library's symbol is compared with, so no width rests on the table's transcription alone.
    /// </summary>
    [Test]
    public async Task RmQr_KanjiFixtures_CoverEveryCountWidth()
    {
        var widths = RmQrIds()
            .Select(id =>
            {
                var manifest = FixtureLoader.Load("RmQr", id).Manifest;
                RmQRConstants.TryGetVersion(manifest.Height, manifest.Width, out var version);
                return RmQRConstants.GetKanjiCountIndicatorLength(version);
            })
            .Distinct()
            .Order()
            .ToArray();
        await Assert.That(widths).IsEquivalentTo(new[] { 2, 3, 4, 5, 6, 7 }, CollectionOrdering.Matching);
    }

    // ---- Capacity at the widths the fixtures fill --------------------------------------

    /// <summary>
    /// The fixtures filled to a version's Kanji capacity hold the other encoder's count at that version, and this library holds that many and not one more.
    /// Standard QR's band edge is one character past what version 26 holds.
    /// </summary>
    [Test]
    public async Task KanjiFixture_AtCapacity_IsTheLastCountThisLibraryHolds()
    {
        const string Next = "日";
        var filled = 0;
        foreach (var id in RmQrIds().Where(id => id.EndsWith("-max", StringComparison.Ordinal)))
        {
            var manifest = FixtureLoader.Load("RmQr", id).Manifest;
            RmQRConstants.TryGetVersion(manifest.Height, manifest.Width, out var version);
            var ecc = Enum.Parse<RmQREccLevel>(manifest.ErrorCorrectionLevel);
            var options = new RmQRCodeGeneratorOptions { AllowKanji = true, Version = version };
            await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(manifest.PayloadText, ecc, out _, options)).IsTrue().Because(id);
            await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(manifest.PayloadText + Next, ecc, out _, options)).IsFalse().Because(id);
            filled++;
        }
        foreach (var id in StandardQrIds().Where(id => id.EndsWith("-max-l", StringComparison.Ordinal) || id.Contains("-edge-", StringComparison.Ordinal)))
        {
            var manifest = FixtureLoader.Load("StandardQr", id).Manifest;
            var ecc = Enum.Parse<QREccLevel>(manifest.ErrorCorrectionLevel);
            var options = new QRCodeGeneratorOptions { AllowKanji = true, Version = QRVersionRange.Exactly(manifest.Version) };
            await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(manifest.PayloadText, ecc, out _, options)).IsTrue().Because(id);
            if (id.Contains("-edge-", StringComparison.Ordinal))
            {
                var below = new QRCodeGeneratorOptions { AllowKanji = true, Version = QRVersionRange.Exactly(manifest.Version - 1) };
                await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(manifest.PayloadText, ecc, out _, below)).IsFalse().Because(id);
                await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(manifest.PayloadText[..^1], ecc, out _, below)).IsTrue().Because(id);
            }
            else
            {
                await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(manifest.PayloadText + Next, ecc, out _, options)).IsFalse().Because(id);
            }
            filled++;
        }
        await Assert.That(filled).IsEqualTo(6);
    }

    // ---- The symbols -----------------------------------------------------------------

    [Test]
    [MethodDataSource(nameof(StandardQrIds))]
    public async Task StandardQr_KanjiFixture_IsThisLibrarysSymbol(string fixtureId)
    {
        var fixture = FixtureLoader.Load("StandardQr", fixtureId);
        var manifest = fixture.Manifest;
        var (oracle, size) = FixtureLoader.ReadMatrix(fixture.MatrixPath);
        var ecc = Enum.Parse<QREccLevel>(manifest.ErrorCorrectionLevel);

        var data = QRCodeGenerator.Create(manifest.PayloadText, ecc, new QRCodeGeneratorOptions { AllowKanji = true, Version = QRVersionRange.Exactly(manifest.Version), MaskPattern = manifest.MaskPattern, QuietZoneSize = 0 });

        await Assert.That(data.Size).IsEqualTo(size).Because(fixtureId);
        await Assert.That(Differences(size, size, (row, col) => data[row, col], oracle, skip: null)).IsEmpty().Because($"{fixtureId}: {manifest.Generator} {manifest.Version}-{manifest.ErrorCorrectionLevel} mask {manifest.MaskPattern}");
    }

    [Test]
    [MethodDataSource(nameof(MicroQrIds))]
    public async Task MicroQr_KanjiFixture_IsThisLibrarysSymbol(string fixtureId)
    {
        var fixture = FixtureLoader.Load("MicroQR", fixtureId);
        var manifest = fixture.Manifest;
        var (oracle, size) = FixtureLoader.ReadMatrix(fixture.MatrixPath);
        var ecc = Enum.Parse<MicroQREccLevel>(manifest.ErrorCorrectionLevel);

        var data = MicroQRCodeGenerator.Create(manifest.PayloadText, ecc, new MicroQRCodeGeneratorOptions { AllowKanji = true, Version = (MicroQRVersion)manifest.Version, MaskPattern = manifest.MaskPattern, QuietZoneSize = 0 });

        await Assert.That(data.Size).IsEqualTo(size).Because(fixtureId);
        await Assert.That(Differences(size, size, (row, col) => data[row, col], oracle, skip: null)).IsEmpty().Because($"{fixtureId}: {manifest.Generator} M{manifest.Version}-{manifest.ErrorCorrectionLevel} mask {manifest.MaskPattern}");
    }

    [Test]
    [MethodDataSource(nameof(RmQrIds))]
    public async Task RmQr_KanjiFixture_IsThisLibrarysSymbol(string fixtureId)
    {
        var fixture = FixtureLoader.Load("RmQr", fixtureId);
        var manifest = fixture.Manifest;
        var (oracle, width, height) = FixtureLoader.ReadRectangularMatrix(fixture.MatrixPath);
        RmQRConstants.TryGetVersion(height, width, out var version);
        var ecc = Enum.Parse<RmQREccLevel>(manifest.ErrorCorrectionLevel);

        var data = RmQRCodeGenerator.Create(manifest.PayloadText, ecc, new RmQRCodeGeneratorOptions { AllowKanji = true, Version = version, QuietZoneSize = 0 });

        // qrtool's tail defect: the last h - 10 modules of the walk, column 1, rows 8 to h - 3.
        Func<int, int, bool>? skip = manifest.Generator == "qrtool" && height >= 11 ? (row, col) => col == 1 && row >= 8 && row <= height - 3 : null;
        await Assert.That(Differences(width, height, (row, col) => data[row, col], oracle, skip)).IsEmpty().Because($"{fixtureId}: {manifest.Generator} {manifest.VersionName}-{manifest.ErrorCorrectionLevel}");
    }

    private static List<string> Differences(int width, int height, Func<int, int, bool> ours, byte[] oracle, Func<int, int, bool>? skip)
    {
        var differences = new List<string>();
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                if (skip?.Invoke(row, col) == true)
                    continue;
                if (ours(row, col) != (oracle[row * width + col] != 0))
                    differences.Add($"({row},{col})");
            }
        }
        return differences;
    }
}
