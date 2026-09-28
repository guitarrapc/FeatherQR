using TUnit.Assertions.Enums;
using FeatherQR.Internals.BinaryDecoders;
using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Tests;

public class EccBinaryDecoderUnitTest
{
    [Test]
    public async Task TryCorrect_CleanBlock_ReportsNoErrors()
    {
        // Arrange - ISO/IEC 18004 Annex I example
        var codeword = BuildCodeword([64, 86, 134, 86], 10);

        // Act
        var result = EccBinaryDecoder.TryCorrect(codeword, 10, out var errorsCorrected);

        // Assert
        await Assert.That(result).IsTrue();
        await Assert.That(errorsCorrected).IsEquivalentTo(0);
        await Assert.That(codeword.AsSpan(0, 4).ToArray()).IsEquivalentTo(new byte[] { 64, 86, 134, 86 }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    public async Task TryCorrect_ErrorsWithinCapacity_Corrects(int errorCount)
    {
        // Arrange - 10 ECC codewords correct up to 5 errors
        byte[] data = [64, 86, 134, 86, 242, 7, 118, 134];
        var codeword = BuildCodeword(data, 10);
        var expected = (byte[])codeword.Clone();

        // Corrupt distinct positions deterministically
        var random = new Random(42);
        CorruptDistinctPositions(codeword, errorCount, random);

        // Act
        var result = EccBinaryDecoder.TryCorrect(codeword, 10, out var errorsCorrected);

        // Assert
        await Assert.That(result).IsTrue();
        await Assert.That(errorsCorrected).IsEquivalentTo(errorCount);
        await Assert.That(codeword).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task TryCorrect_ErrorsBeyondCapacity_Fails()
    {
        // Arrange - 10 ECC codewords, capacity is 5; inject 6 errors
        byte[] data = [64, 86, 134, 86, 242, 7, 118, 134];
        var codeword = BuildCodeword(data, 10);

        var random = new Random(42);
        CorruptDistinctPositions(codeword, 6, random);

        // Act
        var result = EccBinaryDecoder.TryCorrect(codeword, 10, out _);

        // Assert - must fail, never silently miscorrect to the original
        await Assert.That(result).IsFalse();
    }

    [Test]
    [Arguments(7)]   // ECC level L (version 1)
    [Arguments(16)]  // M
    [Arguments(22)]  // Q
    [Arguments(30)]  // H (max per QR spec)
    public async Task TryCorrect_EveryQrEccCount_RoundTrips(int eccCount)
    {
        // Arrange
        var data = new byte[20];
        var random = new Random(1234);
        random.NextBytes(data);
        var codeword = BuildCodeword(data, eccCount);
        var expected = (byte[])codeword.Clone();

        var capacity = eccCount / 2;
        CorruptDistinctPositions(codeword, capacity, random);

        // Act
        var result = EccBinaryDecoder.TryCorrect(codeword, eccCount, out var errorsCorrected);

        // Assert
        await Assert.That(result).IsTrue();
        await Assert.That(errorsCorrected).IsEquivalentTo(capacity);
        await Assert.That(codeword).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task TryCorrect_RandomizedManyRounds_AlwaysRecoversWithinCapacity()
    {
        // Arrange - fixed seed for reproducibility
        var random = new Random(20260711);

        for (var round = 0; round < 500; round++)
        {
            var dataLength = random.Next(4, 100);
            var eccCount = random.Next(2, 31);
            var data = new byte[dataLength];
            random.NextBytes(data);

            var codeword = BuildCodeword(data, eccCount);
            var expected = (byte[])codeword.Clone();

            var errors = random.Next(0, eccCount / 2 + 1);
            CorruptDistinctPositions(codeword, errors, random);

            // Act
            var result = EccBinaryDecoder.TryCorrect(codeword, eccCount, out var errorsCorrected);

            // Assert
            await Assert.That(result).IsTrue().Because($"round {round}: dataLength={dataLength}, eccCount={eccCount}, errors={errors}");
            await Assert.That(errorsCorrected).IsEquivalentTo(errors);
            await Assert.That(codeword).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
    }

    [Test]
    public void TryCorrect_InvalidArguments_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EccBinaryDecoder.TryCorrect(new byte[10], 0, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => EccBinaryDecoder.TryCorrect(new byte[40], 31, out _));
        Assert.Throws<ArgumentException>(() => EccBinaryDecoder.TryCorrect(new byte[10], 10, out _)); // no data codewords
    }

    private static byte[] BuildCodeword(byte[] data, int eccCount)
    {
        var codeword = new byte[data.Length + eccCount];
        data.CopyTo(codeword, 0);
        EccBinaryEncoder.CalculateECC(data, codeword.AsSpan(data.Length), eccCount);
        return codeword;
    }

    private static void CorruptDistinctPositions(byte[] codeword, int errorCount, Random random)
    {
        var positions = new HashSet<int>();
        while (positions.Count < errorCount)
        {
            positions.Add(random.Next(codeword.Length));
        }
        foreach (var position in positions)
        {
            byte garbage;
            do
            {
                garbage = (byte)random.Next(256);
            } while (garbage == codeword[position]);
            codeword[position] = garbage;
        }
    }
}
