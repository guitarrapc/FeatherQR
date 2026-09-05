using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// Full-pipeline extraction (encoder-side consistency guard, independent of any
/// decoder): place a final message with <see cref="RmQRModulePlacer"/>, then walk
/// the symbol back with the naive reference (inverse zigzag + unmask), deinterleave,
/// and require the original data codewords, per-block ECC that recomputes, zero
/// remainder bits and intact function patterns, for every version × ECC and for
/// all-zero / all-one / pseudo-random data.
/// </summary>
public class RmQRMatrixExtractionTest
{
    public static IEnumerable<(RmQRVersion version, RmQREccLevel ecc, string payload)> Cases()
    {
        foreach (var v in Enum.GetValues<RmQRVersion>())
        {
            foreach (var ecc in new[] { RmQREccLevel.M, RmQREccLevel.H })
            {
                yield return (v, ecc, "zero");
                yield return (v, ecc, "ones");
                yield return (v, ecc, "random1");
                yield return (v, ecc, "random2");
            }
        }
    }

    private static byte[] Data(RmQRVersion version, RmQREccLevel ecc, string payload)
    {
        var data = new byte[RmQRConstants.GetDataCodewordCount(version, ecc)];
        switch (payload)
        {
            case "zero": break;
            case "ones": data.AsSpan().Fill(0xFF); break;
            default:
                var state = (uint)((int)version * 7 + (int)ecc + payload.Length) * 2654435761u;
                for (var i = 0; i < data.Length; i++)
                {
                    state = state * 1664525u + 1013904223u;
                    data[i] = (byte)(state >> 24);
                }
                break;
        }
        return data;
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task PlaceThenExtract_RoundTripsDataAndEcc(RmQRVersion version, RmQREccLevel ecc, string payload)
    {
        var height = RmQRConstants.GetHeight(version);
        var width = RmQRConstants.GetWidth(version);
        var data = Data(version, ecc, payload);
        var message = new byte[RmQRCodewordEncoder.GetFinalMessageSize(version)];
        RmQRCodewordEncoder.AssembleFinalMessage(data, version, ecc, message);

        var core = new byte[width * height];
        RmQRModulePlacer.PlaceSymbol(core, version, ecc, message);

        // Independent walk: same interleaved stream, bit for bit, incl. zero remainder.
        var extracted = RmQRNaiveReference.ExtractInterleavedStream(core, height, width, out var bitCount);
        await Assert.That(bitCount).IsEqualTo(8 * RmQRConstants.GetTotalCodewordCount(version) + RmQRConstants.GetRemainderBitCount(version));
        await Assert.That(extracted).IsEquivalentTo(message);

        // Deinterleave and recompute ECC per block.
        var info = RmQRConstants.GetEccInfo(version, ecc);
        var blocks = info.BlocksInGroup1 + info.BlocksInGroup2;
        var back = RmQRNaiveReference.DeinterleaveData(extracted, blocks, info.BlocksInGroup1, info.CodewordsInGroup1);
        await Assert.That(back).IsEquivalentTo(data);
        var dataOffset = 0;
        for (var b = 0; b < blocks; b++)
        {
            var length = b < info.BlocksInGroup1 ? info.CodewordsInGroup1 : info.CodewordsInGroup2;
            var expectedEcc = new byte[info.ECCPerBlock];
            EccBinaryEncoder.CalculateECC(data.AsSpan(dataOffset, length), expectedEcc, info.ECCPerBlock);
            for (var e = 0; e < info.ECCPerBlock; e++)
                if (extracted[info.TotalDataCodewords + e * blocks + b] != expectedEcc[e])
                    Assert.Fail($"{version}-{ecc} {payload}: ECC block {b} codeword {e}");
            dataOffset += length;
        }

        // Function patterns are payload-independent: identical to an all-zero placement
        // on every function module, and the format copies decode to (version, ecc).
        var reference = new byte[width * height];
        RmQRModulePlacer.PlaceSymbol(reference, version, ecc, new byte[message.Length]);
        for (var row = 0; row < height; row++)
            for (var col = 0; col < width; col++)
                if (RmQRModulePlacer.IsFunctionModule(version, row, col) && core[row * width + col] != reference[row * width + col])
                    Assert.Fail($"{version}-{ecc} {payload}: function module ({row},{col}) depends on the payload");
        var (finderSide, subFinderSide) = RmQRNaiveReference.ReadFormatRegions(core, height, width);
        await Assert.That(finderSide).IsEqualTo(RmQRConstants.GetFormatBits(version, ecc, false));
        await Assert.That(subFinderSide).IsEqualTo(RmQRConstants.GetFormatBits(version, ecc, true));
    }
}
