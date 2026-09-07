using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// rMQR data-codeword stream (ISO/IEC 23941 7.4): golden vectors read from
/// external oracle symbols, naive bit-string references for the terminator /
/// alignment / padding paths, and the encoder-side oracle test: for EVERY
/// committed corpus symbol (libzint + qrtool), our data codewords for the same
/// payload / version / ECC / mode must equal the codewords deinterleaved out of the
/// external symbol (single-segment streams are deterministic, so two conformant
/// encoders must agree byte for byte).
/// </summary>
public class RmQRBinaryEncoderUnitTest
{
    private static byte[] Encode(string text, RmQRVersion version, RmQREccLevel ecc, EncodingMode mode, EciMode eci, int dataLength)
    {
        var destination = new byte[RmQRConstants.GetDataCodewordCount(version, ecc)];
        var analysis = new TextAnalysisResult(mode, eci, dataLength);
        var written = RmQRBinaryEncoder.EncodeDataCodewords(text, version, ecc, in analysis, destination);
        return destination.AsSpan(0, written).ToArray();
    }

    private static TextAnalysisResult Analysis(string text, string mode)
    {
        var utf8 = text.Any(c => c > 0xFF);
        return mode switch
        {
            "Numeric" => new TextAnalysisResult(EncodingMode.Numeric, EciMode.Default, text.Length),
            "Alphanumeric" => new TextAnalysisResult(EncodingMode.Alphanumeric, EciMode.Default, text.Length),
            _ => new TextAnalysisResult(EncodingMode.Byte, utf8 ? EciMode.Utf8 : EciMode.Default, utf8 ? System.Text.Encoding.UTF8.GetByteCount(text) : text.Length),
        };
    }

    [Test]
    public async Task Encode_R7x43M_Numeric1_MatchesOracleGolden()
    {
        // Read from the qrtool R7x43-M "1" symbol during pre-verification:
        // 001 (numeric) + 0001 (count, 4 bits) + 0001 ('1') + 000 (terminator) + align → 22 20, then EC 11 EC 11.
        var actual = Encode("1", RmQRVersion.R7x43, RmQREccLevel.M, EncodingMode.Numeric, EciMode.Default, 1);
        await Assert.That(actual).IsEquivalentTo(new byte[] { 0x22, 0x20, 0xEC, 0x11, 0xEC, 0x11 });
    }

    [Test]
    public async Task Encode_ReturnsExactlyTheDataCodewordCount()
    {
        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            foreach (var ecc in new[] { RmQREccLevel.M, RmQREccLevel.H })
            {
                var actual = Encode("7", version, ecc, EncodingMode.Numeric, EciMode.Default, 1);
                await Assert.That(actual.Length).IsEqualTo(RmQRConstants.GetDataCodewordCount(version, ecc));
            }
        }
    }

    [Test]
    public async Task Encode_ExactCapacity_ShortensTerminatorToZero()
    {
        // R7x43-H byte: 3 data codewords = 24 bits; mode 3 + count 3 + 2 bytes 16 = 22 bits → 2-bit terminator, no pad.
        var actual = Encode("ab", RmQRVersion.R7x43, RmQREccLevel.H, EncodingMode.Byte, EciMode.Default, 2);
        await Assert.That(actual).IsEquivalentTo(RmQRNaiveReference.NaiveDataCodewords("ab", 3, 0b011, 3, "Byte", utf8: false));
        // 011 010 01100001 01100010 00 → 0x69 0x85 0x88
        await Assert.That(actual).IsEquivalentTo(new byte[] { 0x69, 0x85, 0x88 });

        // R7x43-M numeric 12 digits: 3 + 4 + 40 = 47 bits of 48 → 1-bit terminator.
        var digits = Encode("012345678901", RmQRVersion.R7x43, RmQREccLevel.M, EncodingMode.Numeric, EciMode.Default, 12);
        await Assert.That(digits).IsEquivalentTo(RmQRNaiveReference.NaiveDataCodewords("012345678901", 6, 0b001, 4, "Numeric", utf8: false));
    }

    [Test]
    public async Task Encode_EmptyText_IsByteModeZeroCountThenPadding()
    {
        // 011 + count(3 bits) 000 + terminator 000 → 0x00 (9 bits → aligned to 16 → 00 00) then pads.
        var actual = Encode("", RmQRVersion.R7x43, RmQREccLevel.M, EncodingMode.Byte, EciMode.Default, 0);
        await Assert.That(actual).IsEquivalentTo(new byte[] { 0x60, 0x00, 0xEC, 0x11, 0xEC, 0x11 });
    }

    [Test]
    public async Task Encode_Utf8_WritesEci26BeforeByteSegment()
    {
        // ECI mode 111 + assignment 26 + Byte mode 011 + count 3 + "こ" = E3 81 93
        // + terminator/alignment: 111 00011010 011 011 E3 81 93 000 0000.
        var actual = Encode("こ", RmQRVersion.R7x43, RmQREccLevel.M, EncodingMode.Byte, EciMode.Utf8, 3);
        await Assert.That(actual).IsEquivalentTo(new byte[] { 0xE3, 0x4D, 0xF1, 0xC0, 0xC9, 0x80 });
    }

    [Test]
    public async Task Encode_Latin1_WritesEci3AndNarrowsWithoutTransliteration()
    {
        // ECI mode 111 + assignment 3 + Byte mode 011 + count 1 + E9 + terminator/alignment.
        var actual = Encode("é", RmQRVersion.R7x43, RmQREccLevel.M, EncodingMode.Byte, EciMode.Iso8859_1, 1);
        await Assert.That(actual).IsEquivalentTo(new byte[] { 0xE0, 0x6C, 0xF4, 0x80, 0xEC, 0x11 });
    }

    public static IEnumerable<string> FixtureIdsWithoutEci()
    {
        foreach (var fixtureId in FixtureLoader.EnumerateFixtureIds("RmQr"))
        {
            var manifest = FixtureLoader.Load("RmQr", fixtureId).Manifest;
            if (TextAnalyzer.Analyze(manifest.PayloadText, EciMode.Default).EciMode == EciMode.Default)
                yield return fixtureId;
        }
    }

    [Test]
    [MethodDataSource(nameof(FixtureIdsWithoutEci))]
    public async Task Encode_MatchesDataCodewords_OfEveryComparableExternalOracleSymbol(string fixtureId)
    {
        var fixture = FixtureLoader.Load("RmQr", fixtureId);
        var manifest = fixture.Manifest;
        var (modules, width, height) = FixtureLoader.ReadRectangularMatrix(fixture.MatrixPath);
        RmQRConstants.TryGetVersion(height, width, out var version);
        var ecc = Enum.Parse<RmQREccLevel>(manifest.ErrorCorrectionLevel);
        var info = RmQRConstants.GetEccInfo(version, ecc);

        var stream = RmQRNaiveReference.ExtractInterleavedStream(modules, height, width, out _);
        var oracleData = RmQRNaiveReference.DeinterleaveData(stream, info.BlocksInGroup1 + info.BlocksInGroup2, info.BlocksInGroup1, info.CodewordsInGroup1);

        var analysis = Analysis(manifest.PayloadText, manifest.Mode);
        var destination = new byte[info.TotalDataCodewords];
        var written = RmQRBinaryEncoder.EncodeDataCodewords(manifest.PayloadText, version, ecc, in analysis, destination);

        await Assert.That(written).IsEqualTo(oracleData.Length);
        await Assert.That(destination).IsEquivalentTo(oracleData).Because($"{fixtureId}: {manifest.Generator} {manifest.VersionName}-{manifest.ErrorCorrectionLevel} {manifest.Mode} \"{manifest.PayloadText}\"");
    }
}
