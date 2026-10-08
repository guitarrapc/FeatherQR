using TUnit.Assertions.Enums;
using FeatherQR.Internals;
using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// Holds the block entry, <see cref="EccBinaryEncoder.CalculateECCBlocks"/>, and the kernels that advance several blocks'
/// remainders in one loop to a naive per-block polynomial division: every Standard QR and rMQR block layout, and each
/// multi-block kernel called directly, since the dispatch hides a lower tier behind a higher one.
/// </summary>
public class EccBinaryEncoderBlocksParityTest
{
    [Test]
    public async Task CalculateECCBlocks_MatchesNaivePerBlock_EveryStandardQRLayout()
    {
        for (var version = 1; version <= 40; version++)
        {
            foreach (var level in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
            {
                await AssertLayout(QRCodeConstants.GetEccInfo(version, level));
            }
        }
    }

    [Test]
    public async Task CalculateECCBlocks_MatchesNaivePerBlock_EveryRmQRLayout()
    {
        for (var v = 1; v <= RmQRConstants.VersionCount; v++)
        {
            foreach (var level in new[] { RmQREccLevel.M, RmQREccLevel.H })
            {
                await AssertLayout(RmQRConstants.GetEccInfo((RmQRVersion)v, level));
            }
        }
    }

    // Block counts that reach every quad, pair and single remainder (1 to 9), on lengths under one 4-byte step, between steps,
    // and on version 40-L's longer block (119 codewords, against QR's longest, 123), at ECC counts on both sides of the 16-byte register split,
    // and at 31 and 32, which no QR layout has but the dispatch sends to these kernels.
    public static IEnumerable<(int Blocks, int Length, int EccCount)> GroupShapes()
    {
        foreach (var blocks in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 })
        {
            foreach (var (length, eccCount) in new[] { (1, 7), (3, 10), (6, 16), (15, 16), (16, 17), (27, 22), (119, 30), (5, 31), (34, 32) })
                yield return (blocks, length, eccCount);
        }
    }

    [Test]
    [MethodDataSource(nameof(GroupShapes))]
    public async Task Ssse3GroupKernel_MatchesNaivePerBlock(int blocks, int length, int eccCount)
    {
        if (!System.Runtime.Intrinsics.X86.Ssse3.IsSupported)
        {
            Skip.Test("SSSE3 not supported on this machine");
            return;
        }

        foreach (var data in Inputs(blocks * length))
        {
            var actual = new byte[blocks * eccCount];
            EccBinaryEncoder.CalculateEccSsse3Group(data, actual, eccCount, blocks, length);
            await Assert.That(actual).IsEquivalentTo(NaivePerBlock(data, blocks, length, eccCount), CollectionOrdering.Matching);
        }
    }

#if NET10_0_OR_GREATER
    [Test]
    [MethodDataSource(nameof(GroupShapes))]
    public async Task GfniGroupKernel_MatchesNaivePerBlock(int blocks, int length, int eccCount)
    {
        if (!System.Runtime.Intrinsics.X86.Gfni.IsSupported)
        {
            Skip.Test("GFNI not supported on this machine");
            return;
        }

        foreach (var data in Inputs(blocks * length))
        {
            var actual = new byte[blocks * eccCount];
            EccBinaryEncoder.CalculateEccGfniGroup(data, actual, eccCount, blocks, length);
            await Assert.That(actual).IsEquivalentTo(NaivePerBlock(data, blocks, length, eccCount), CollectionOrdering.Matching);
        }
    }
#endif

    private static async Task AssertLayout(ECCInfo info)
    {
        var (eccCount, blocks1, length1, blocks2, length2) = (info.ECCPerBlock, info.BlocksInGroup1, info.CodewordsInGroup1, info.BlocksInGroup2, info.CodewordsInGroup2);
        var dataLength = blocks1 * length1 + blocks2 * length2;
        foreach (var data in Inputs(dataLength))
        {
            var expected = new byte[(blocks1 + blocks2) * eccCount];
            NaivePerBlock(data.AsSpan(0, blocks1 * length1), blocks1, length1, eccCount).CopyTo(expected, 0);
            NaivePerBlock(data.AsSpan(blocks1 * length1), blocks2, length2, eccCount).CopyTo(expected, blocks1 * eccCount);

            var actual = new byte[expected.Length];
            EccBinaryEncoder.CalculateECCBlocks(data, actual, info);
            await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
    }

    private static IEnumerable<byte[]> Inputs(int length)
    {
        for (var seed = 0; seed < 2; seed++)
        {
            var data = new byte[length];
            new Random(seed * 7919 + length).NextBytes(data);
            yield return data;
        }
        yield return new byte[length];
        var ones = new byte[length];
        ones.AsSpan().Fill(0xFF);
        yield return ones;
    }

    private static byte[] NaivePerBlock(ReadOnlySpan<byte> data, int blocks, int length, int eccCount)
    {
        var ecc = new byte[blocks * eccCount];
        for (var b = 0; b < blocks; b++)
            NaiveECC(data.Slice(b * length, length), ecc.AsSpan(b * eccCount, eccCount), eccCount);
        return ecc;
    }

    /// <summary>ISO/IEC 18004 Section 8.5 polynomial division, independent of the production kernels.</summary>
    private static void NaiveECC(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
    {
        var generator = new byte[eccCount + 1];
        generator[0] = 1;
        var temp = new byte[generator.Length];
        for (var i = 0; i < eccCount; i++)
        {
            Array.Clear(temp);
            for (var j = 0; j <= i; j++)
            {
                temp[j] ^= generator[j];
                temp[j + 1] ^= GaloisField.Multiply(generator[j], GaloisField.Exp[i]);
            }
            temp.CopyTo(generator.AsSpan());
        }

        var message = new byte[data.Length + eccCount];
        data.CopyTo(message);
        for (var i = 0; i < data.Length; i++)
        {
            var coefficient = message[i];
            for (var j = 0; j < eccCount; j++)
                message[i + j + 1] ^= GaloisField.Multiply(generator[j + 1], coefficient);
        }
        message.AsSpan(data.Length, eccCount).CopyTo(ecc);
    }
}
