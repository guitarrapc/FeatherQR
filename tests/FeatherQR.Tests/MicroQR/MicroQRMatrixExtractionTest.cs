using FeatherQR.Internals;
using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.MicroQR;

namespace FeatherQR.Tests;

/// <summary>
/// Full-pipeline structural verification: reads the generated matrix BACK —
/// format info by exhaustive match, unmasking, inverse zigzag extraction, and
/// checks that the recovered codewords equal the bit-stream encoder's output and
/// that recomputed Reed-Solomon ECC matches the placed ECC. Until the Micro QR
/// decoder (Phase 3) and external fixtures exist, this is the guard that the
/// placement/mask/format stages are mutually consistent rather than each stage
/// being its own oracle.
/// </summary>
public class MicroQRMatrixExtractionTest
{
    public static IEnumerable<(string text, MicroQREccLevel ecc, MicroQRVersion version)> AllVersionEccCombinations()
    {
        yield return ("12345", MicroQREccLevel.ErrorDetectionOnly, MicroQRVersion.M1);
        yield return ("0123456789", MicroQREccLevel.L, MicroQRVersion.M2);
        yield return ("12345678", MicroQREccLevel.M, MicroQRVersion.M2);
        yield return ("HELLO WORLD 14", MicroQREccLevel.L, MicroQRVersion.M3);
        yield return ("byte hi", MicroQREccLevel.M, MicroQRVersion.M3);
        yield return ("HELLO WORLD PLUS 21ST", MicroQREccLevel.L, MicroQRVersion.M4);
        yield return ("bytes m4 mode", MicroQREccLevel.M, MicroQRVersion.M4); // 13 bytes = M4-M byte capacity
        yield return ("bytes!!!!", MicroQREccLevel.Q, MicroQRVersion.M4);
    }

    [Test]
    [MethodDataSource(nameof(AllVersionEccCombinations))]
    public async Task ExtractedCodewords_MatchEncoderOutput_AndEccRecomputes(string text, MicroQREccLevel ecc, MicroQRVersion expectedVersion)
    {
        var data = MicroQRCodeGenerator.CreateMicroQRCode(text, ecc, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
        await Assert.That(data.Version).IsEqualTo(expectedVersion);
        var size = data.Size;

        // 1. Recover (symbol number, mask) from the placed format info by
        // exhaustive match over all 32 valid patterns.
        ushort placed = 0;
        for (var col = 1; col <= 8; col++)
        {
            placed = (ushort)((placed << 1) | (data[8, col] ? 1 : 0));
        }
        for (var row = 7; row >= 1; row--)
        {
            placed = (ushort)((placed << 1) | (data[row, 8] ? 1 : 0));
        }

        var mask = -1;
        for (var candidate = 0; candidate < 4; candidate++)
        {
            if (MicroQRConstants.GetFormatBits(expectedVersion, ecc, candidate) == placed)
            {
                mask = candidate;
                break;
            }
        }
        await Assert.That(mask).IsBetween(0, 3);

        // 2. Unmask + inverse zigzag: collect the transmission bit stream.
        var dataBitCount = MicroQRConstants.GetDataBitCapacity(expectedVersion, ecc);
        var eccCount = MicroQRConstants.GetEccCodewordCount(expectedVersion, ecc);
        var totalBits = dataBitCount + eccCount * 8;
        var stream = new List<bool>(totalBits);
        var upward = true;
        for (var right = size - 1; right >= 2; right -= 2)
        {
            for (var step = 0; step < size; step++)
            {
                var row = upward ? size - 1 - step : step;
                for (var side = 0; side < 2; side++)
                {
                    var col = right - side;
                    if (row == 0 || col == 0 || (row <= 8 && col <= 8))
                        continue;
                    var module = data[row, col] ^ MaskBit(mask, row, col);
                    stream.Add(module);
                }
            }
            upward = !upward;
        }
        await Assert.That(stream.Count).IsEqualTo(totalBits);

        // 3. Rebuild codewords: data section (half codeword high nibble for
        // M1/M3), then ECC section.
        var dataCodewordCount = MicroQRConstants.GetDataCodewordCount(expectedVersion, ecc);
        var extractedData = new byte[dataCodewordCount];
        for (var i = 0; i < dataBitCount; i++)
        {
            if (stream[i])
            {
                extractedData[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }
        var extractedEcc = new byte[eccCount];
        for (var i = 0; i < eccCount * 8; i++)
        {
            if (stream[dataBitCount + i])
            {
                extractedEcc[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        // 4. Data codewords must equal the bit-stream encoder's direct output.
        var expectedData = new byte[16];
        var analysis = TextAnalyzer.Analyze(text.AsSpan(), EciMode.Default);
        var written = MicroQRBinaryEncoder.EncodeDataCodewords(text.AsSpan(), expectedVersion, ecc, analysis.EncodingMode, expectedData);
        await Assert.That(extractedData).IsEquivalentTo(expectedData.AsSpan(0, written).ToArray());

        // 5. Recomputed Reed-Solomon ECC over the extracted data must equal the
        // placed ECC codewords.
        var recomputedEcc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(extractedData, recomputedEcc, eccCount);
        await Assert.That(extractedEcc).IsEquivalentTo(recomputedEcc);
    }

    private static bool MaskBit(int mask, int row, int col) => mask switch
    {
        0 => row % 2 == 0,
        1 => (row / 2 + col / 3) % 2 == 0,
        2 => (row * col % 2 + row * col % 3) % 2 == 0,
        _ => ((row + col) % 2 + row * col % 3) % 2 == 0,
    };
}
