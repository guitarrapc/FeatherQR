using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// Pins the rMQR tables against symbols produced by two independent external
/// encoders (the committed Fixtures/RmQr corpus: libzint and qrtool), before any
/// rMQR placer or decoder of our own exists. Reading is done with the naive
/// helpers in <see cref="RmQRNaiveReference"/>:
/// dimensions ↔ version, both format-information copies ↔ format words (this pins
/// the BCH generator, both XOR masks, the version index order and the ECC bit),
/// and for the single-character libzint cases the leading data codewords ↔ mode
/// indicator value and count-indicator width (this pins the count table AND
/// proves the mask, the zigzag start, the function-module map and the block
/// interleaving, since a mismatch in any of them scrambles the leading bits).
/// </summary>
public class RmQRConstantsOracleTest
{
    public static IEnumerable<string> FixtureIds() => FixtureLoader.EnumerateFixtureIds("RmQr");

    public static IEnumerable<string> SingleCharacterFixtureIds() =>
        FixtureIds().Where(id => FixtureLoader.Load("RmQr", id).Manifest.PayloadText.Length == 1 && !id.Contains("utf8", StringComparison.Ordinal));

    [Test]
    public async Task Corpus_HasSingleCharacterCases_ForEveryVersionAndMode()
    {
        var ids = SingleCharacterFixtureIds().ToArray();
        await Assert.That(ids.Length).IsGreaterThanOrEqualTo(96);
    }

    [Test]
    [MethodDataSource(nameof(FixtureIds))]
    public async Task Dimensions_MapToVersion_AndBothFormatCopiesMatchTheTable(string fixtureId)
    {
        var fixture = FixtureLoader.Load("RmQr", fixtureId);
        var manifest = fixture.Manifest;
        var (modules, width, height) = FixtureLoader.ReadRectangularMatrix(fixture.MatrixPath);

        await Assert.That(RmQRConstants.TryGetVersion(height, width, out var version)).IsTrue();
        await Assert.That((int)version).IsEqualTo(manifest.Version);
        await Assert.That(version.ToString()).IsEqualTo(manifest.VersionName);
        var ecc = Enum.Parse<RmQREccLevel>(manifest.ErrorCorrectionLevel);

        var (finderSide, subFinderSide) = RmQRNaiveReference.ReadFormatRegions(modules, height, width);
        await Assert.That(finderSide).IsEqualTo(RmQRConstants.GetFormatBits(version, ecc, subFinderSide: false))
            .Because($"finder-side format copy of {fixtureId}");
        await Assert.That(subFinderSide).IsEqualTo(RmQRConstants.GetFormatBits(version, ecc, subFinderSide: true))
            .Because($"sub-finder-side format copy of {fixtureId}");

        // Every non-function module is a data/ECC/remainder bit: the naive walk over
        // the symbol must yield exactly 8 × total codewords + remainder bits.
        RmQRNaiveReference.ExtractInterleavedStream(modules, height, width, out var bitCount);
        await Assert.That(bitCount).IsEqualTo(8 * RmQRConstants.GetTotalCodewordCount(version) + RmQRConstants.GetRemainderBitCount(version));
    }

    [Test]
    [MethodDataSource(nameof(SingleCharacterFixtureIds))]
    public async Task SingleCharacterSymbols_LeadingCodewords_PinModeAndCountIndicatorWidth(string fixtureId)
    {
        var fixture = FixtureLoader.Load("RmQr", fixtureId);
        var manifest = fixture.Manifest;
        var (modules, width, height) = FixtureLoader.ReadRectangularMatrix(fixture.MatrixPath);
        RmQRConstants.TryGetVersion(height, width, out var version);
        var ecc = Enum.Parse<RmQREccLevel>(manifest.ErrorCorrectionLevel);
        var mode = Enum.Parse<EncodingMode>(manifest.Mode);

        var stream = RmQRNaiveReference.ExtractInterleavedStream(modules, height, width, out _);
        var info = RmQRConstants.GetEccInfo(version, ecc);
        var block0 = RmQRNaiveReference.DeinterleaveFirstBlock(stream, info.BlocksInGroup1 + info.BlocksInGroup2, info.BlocksInGroup1, info.CodewordsInGroup1);
        await Assert.That(block0.Length).IsGreaterThanOrEqualTo(2);

        // First 16 data bits: mode(3) + count(cci, value 1 => leading zeros then '1') + payload…
        var leading = (block0[0] << 8) | block0[1];
        var modeValue = leading >> 13;
        await Assert.That(modeValue).IsEqualTo(RmQRConstants.GetModeIndicatorValue(mode)).Because($"mode indicator of {fixtureId}");

        var afterMode = (leading << 3) & 0xFFFF; // 13 bits remain in the low 16, MSB-aligned
        var countWidth = 0;
        for (var bit = 15; bit >= 0; bit--)
        {
            countWidth++;
            if ((afterMode & (1 << bit)) != 0)
                break;
        }
        await Assert.That(countWidth).IsEqualTo(RmQRConstants.GetCountIndicatorLength(version, mode)).Because($"count indicator width of {fixtureId} ({version} {mode})");

        // The payload character follows: numeric '1' = 0001 (4 bits), alphanumeric 'A' = 001010 (6 bits), byte 'a' = 0x61.
        var payloadStart = RmQRConstants.ModeIndicatorLength + countWidth;
        int Bits(int start, int count) => (int)(((uint)((block0[0] << 24) | (block0[1] << 16) | (block0.Length > 2 ? block0[2] << 8 : 0) | (block0.Length > 3 ? block0[3] : 0)) << start) >> (32 - count));
        var expected = manifest.PayloadText switch { "1" => (0b0001, 4), "A" => (0b001010, 6), "a" => (0x61, 8), _ => throw new InvalidOperationException(manifest.PayloadText) };
        await Assert.That(Bits(payloadStart, expected.Item2)).IsEqualTo(expected.Item1).Because($"payload bits of {fixtureId}");
    }
}
