using TUnit.Assertions.Enums;
using FeatherQR.Internals;
using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Tests;

/// <summary>
/// The edges of the block entry, <see cref="EccBinaryEncoder.CalculateECCBlocks"/>, that no QR layout reaches: ECC counts past what the
/// multi-block kernels hold, and the argument checks of the path with a group of two or more blocks.
/// </summary>
public class EccBinaryEncoderBlocksUnitTest
{
    // Groups of two or more blocks, so the multi-block dispatch decides, beside a lone block and an empty group.
    public static IEnumerable<(int EccCount, int Blocks1, int Length1, int Blocks2, int Length2)> AboveKernelLimit()
    {
        foreach (var eccCount in new[] { 33, 40, 64, 255 })
        {
            yield return (eccCount, 2, 5, 0, 0);
            yield return (eccCount, 2, 5, 2, 6);
            yield return (eccCount, 4, 9, 1, 10);
            yield return (eccCount, 3, 4, 3, 5);
        }
    }

    [Test]
    [MethodDataSource(nameof(AboveKernelLimit))]
    public async Task CalculateECCBlocks_AboveTheMultiBlockKernelsEccCount_MatchesPerBlock(int eccCount, int blocks1, int length1, int blocks2, int length2)
    {
        // The multi-block kernels hold at most 32 ECC codewords; above that every block goes to CalculateECC.
        var data = new byte[blocks1 * length1 + blocks2 * length2];
        new Random(eccCount * 31 + blocks1 * 7 + blocks2).NextBytes(data);
        var expected = new byte[(blocks1 + blocks2) * eccCount];
        for (var b = 0; b < blocks1 + blocks2; b++)
        {
            var offset = b < blocks1 ? b * length1 : blocks1 * length1 + (b - blocks1) * length2;
            var length = b < blocks1 ? length1 : length2;
            EccBinaryEncoder.CalculateECC(data.AsSpan(offset, length), expected.AsSpan(b * eccCount, eccCount), eccCount);
        }

        var actual = new byte[expected.Length];
        EccBinaryEncoder.CalculateECCBlocks(data, actual, Layout(eccCount, blocks1, length1, blocks2, length2));
        await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task CalculateECCBlocks_ArgumentsOutOfRange_ThrowFromTheGroupChecks()
    {
        // Every layout has a group of two or more blocks and no lone block, so CalculateECC and Span.Slice throw nothing first.
        var eccZero = await Assert.That(() => EccBinaryEncoder.CalculateECCBlocks(new byte[8], new byte[16], Layout(0, 2, 4, 0, 0))).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(eccZero!.ParamName).IsEqualTo("eccInfo");

        // Buffers large enough for 256 codewords a block, so only the ECC count is out of range.
        var ecc256 = await Assert.That(() => EccBinaryEncoder.CalculateECCBlocks(new byte[8], new byte[2 * 256], Layout(256, 2, 4, 0, 0))).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(ecc256!.ParamName).IsEqualTo("eccInfo");

        var shortData = await Assert.That(() => EccBinaryEncoder.CalculateECCBlocks(new byte[2 * 4 + 2 * 5 - 1], new byte[4 * 7], Layout(7, 2, 4, 2, 5))).ThrowsExactly<ArgumentException>();
        await Assert.That(shortData!.ParamName).IsEqualTo("data");

        var shortEcc = await Assert.That(() => EccBinaryEncoder.CalculateECCBlocks(new byte[2 * 4 + 2 * 5], new byte[4 * 7 - 1], Layout(7, 2, 4, 2, 5))).ThrowsExactly<ArgumentException>();
        await Assert.That(shortEcc!.ParamName).IsEqualTo("ecc");
    }

    // A block structure no symbol has; the entry reads the ECC count and the two groups only.
    private static ECCInfo Layout(int eccCount, int blocks1, int length1, int blocks2, int length2)
        => new(0, QREccLevel.L, blocks1 * length1 + blocks2 * length2, eccCount, blocks1, length1, blocks2, length2);
}
