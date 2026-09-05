using FeatherQR.Internals;
using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// rMQR final message (ISO/IEC 23941 7.5-7.6): per-block Reed-Solomon ECC over the
/// data codewords, then block interleaving (data round-robin, ECC round-robin,
/// zero remainder bits). Verified structurally against naive deinterleaving and the
/// shared RS kernel, and against every comparable committed external symbol: the full
/// interleaved stream walked out of a libzint / qrtool symbol must equal our final
/// message for the same payload, which pins the RS generator polynomials and ECC
/// counts per block against two independent encoders. The four legacy qrtool UTF-8
/// symbols omit ECI and remain decoder fixtures, but are not encoder-bitstream oracles.
/// </summary>
public class RmQRCodewordEncoderUnitTest
{
    public static IEnumerable<(RmQRVersion version, RmQREccLevel ecc)> AllVersionEcc()
    {
        foreach (var v in Enum.GetValues<RmQRVersion>())
        {
            yield return (v, RmQREccLevel.M);
            yield return (v, RmQREccLevel.H);
        }
    }

    private static byte[] SyntheticData(int length, int seed)
    {
        var data = new byte[length];
        var state = (uint)seed * 2654435761u + 99u;
        for (var i = 0; i < length; i++)
        {
            state = state * 1664525u + 1013904223u;
            data[i] = (byte)(state >> 24);
        }
        return data;
    }

    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task FinalMessageSize_IsTotalCodewordsPlusRemainderByte(RmQRVersion version, RmQREccLevel ecc)
    {
        var expected = RmQRConstants.GetTotalCodewordCount(version) + (RmQRConstants.GetRemainderBitCount(version) > 0 ? 1 : 0);
        await Assert.That(RmQRCodewordEncoder.GetFinalMessageSize(version)).IsEqualTo(expected);
        await Assert.That(RmQRCodewordEncoder.GetFinalMessageSize(version)).IsEqualTo((8 * RmQRConstants.GetTotalCodewordCount(version) + RmQRConstants.GetRemainderBitCount(version) + 7) / 8);
        // ECC scratch never exceeds the documented budget (R17x139-H: 6 × 26 = 156).
        await Assert.That(RmQRConstants.GetEccCodewordCount(version, ecc)).IsLessThanOrEqualTo(RmQRCodewordEncoder.MaxEccCodewords);
    }

    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task AssembleFinalMessage_DataDeinterleaves_EccMatchesPerBlockRs_TailIsZero(RmQRVersion version, RmQREccLevel ecc)
    {
        var info = RmQRConstants.GetEccInfo(version, ecc);
        var blocks = info.BlocksInGroup1 + info.BlocksInGroup2;
        var data = SyntheticData(info.TotalDataCodewords, (int)version * 3 + (int)ecc);

        var output = new byte[RmQRCodewordEncoder.GetFinalMessageSize(version)];
        output.AsSpan().Fill(0xAA); // dirty buffer: the remainder tail must still come out zero
        RmQRCodewordEncoder.AssembleFinalMessage(data, version, ecc, output);

        // Data part deinterleaves back to the input (naive reference).
        var back = RmQRNaiveReference.DeinterleaveData(output, blocks, info.BlocksInGroup1, info.CodewordsInGroup1);
        await Assert.That(back).IsEquivalentTo(data);

        // ECC part: block b, codeword e sits at data + e * blocks + b, and equals the
        // shared RS kernel over that block's data.
        var dataOffset = 0;
        for (var b = 0; b < blocks; b++)
        {
            var length = b < info.BlocksInGroup1 ? info.CodewordsInGroup1 : info.CodewordsInGroup2;
            var expectedEcc = new byte[info.ECCPerBlock];
            EccBinaryEncoder.CalculateECC(data.AsSpan(dataOffset, length), expectedEcc, info.ECCPerBlock);
            for (var e = 0; e < info.ECCPerBlock; e++)
            {
                if (output[info.TotalDataCodewords + e * blocks + b] != expectedEcc[e])
                    Assert.Fail($"{version}-{ecc}: ECC block {b} codeword {e} mismatch");
            }
            dataOffset += length;
        }

        // Remainder byte (if any) is zero.
        var totalCodewords = RmQRConstants.GetTotalCodewordCount(version);
        if (output.Length > totalCodewords)
            await Assert.That(output[totalCodewords]).IsEqualTo((byte)0);
    }

    [Test]
    public async Task AssembleFinalMessage_RejectsUndersizedBuffers()
    {
        var info = RmQRConstants.GetEccInfo(RmQRVersion.R7x43, RmQREccLevel.M);
        var data = new byte[info.TotalDataCodewords];
        await Assert.That(() => RmQRCodewordEncoder.AssembleFinalMessage(data.AsSpan(0, data.Length - 1).ToArray(), RmQRVersion.R7x43, RmQREccLevel.M, new byte[13])).Throws<ArgumentException>();
        await Assert.That(() => RmQRCodewordEncoder.AssembleFinalMessage(data, RmQRVersion.R7x43, RmQREccLevel.M, new byte[12])).Throws<ArgumentException>();
    }

    public static IEnumerable<string> FixtureIdsWithoutEci() => RmQRBinaryEncoderUnitTest.FixtureIdsWithoutEci();

    [Test]
    [MethodDataSource(nameof(FixtureIdsWithoutEci))]
    public async Task FinalMessage_MatchesEveryComparableExternalOracleSymbol(string fixtureId)
    {
        var fixture = FixtureLoader.Load("RmQr", fixtureId);
        var manifest = fixture.Manifest;
        var (modules, width, height) = FixtureLoader.ReadRectangularMatrix(fixture.MatrixPath);
        RmQRConstants.TryGetVersion(height, width, out var version);
        var ecc = Enum.Parse<RmQREccLevel>(manifest.ErrorCorrectionLevel);

        var utf8 = manifest.PayloadText.Any(c => c > 0xFF);
        var analysis = manifest.Mode switch
        {
            "Numeric" => new TextAnalysisResult(EncodingMode.Numeric, EciMode.Default, manifest.PayloadText.Length),
            "Alphanumeric" => new TextAnalysisResult(EncodingMode.Alphanumeric, EciMode.Default, manifest.PayloadText.Length),
            _ => new TextAnalysisResult(EncodingMode.Byte, utf8 ? EciMode.Utf8 : EciMode.Default, utf8 ? System.Text.Encoding.UTF8.GetByteCount(manifest.PayloadText) : manifest.PayloadText.Length),
        };
        var data = new byte[RmQRConstants.GetDataCodewordCount(version, ecc)];
        RmQRBinaryEncoder.EncodeDataCodewords(manifest.PayloadText, version, ecc, in analysis, data);

        var ours = new byte[RmQRCodewordEncoder.GetFinalMessageSize(version)];
        RmQRCodewordEncoder.AssembleFinalMessage(data, version, ecc, ours);

        var oracle = RmQRNaiveReference.ExtractInterleavedStream(modules, height, width, out var bitCount);
        await Assert.That(oracle.Length).IsEqualTo(ours.Length);

        // Known qrtool 0.13.2 (qrcode2 crate) defect, recorded in specs/qrcode-test-fixtures.md:
        // it never writes the last h - 10 modules of the walk (column 1, rows 8..h-3), so on
        // versions 11 modules high or taller the final ECC codeword loses its lowest
        // (h - 10 - remainderBits) bits (zxing-cpp corrects it as one codeword error). Every
        // other byte, including the remainder tail, must still match; libzint symbols match exactly.
        var totalCodewords = RmQRConstants.GetTotalCodewordCount(version);
        var allowedLowBits = manifest.Generator == "qrtool" ? Math.Max(0, height - 10 - RmQRConstants.GetRemainderBitCount(version)) : 0;
        for (var i = 0; i < ours.Length; i++)
        {
            var diff = ours[i] ^ oracle[i];
            if (i == totalCodewords - 1)
                diff >>= allowedLowBits;
            if (diff != 0)
                Assert.Fail($"{fixtureId}: {manifest.Generator} {manifest.VersionName}-{manifest.ErrorCorrectionLevel} \"{manifest.PayloadText}\" ({bitCount} bits): byte {i} ours {ours[i]:X2} oracle {oracle[i]:X2}");
        }
    }
}
