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

        var result = EccBinaryDecoder.TryCorrect(codeword, 10, out var errorsCorrected);

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

        var result = EccBinaryDecoder.TryCorrect(codeword, 10, out var errorsCorrected);

        await Assert.That(result).IsTrue();
        await Assert.That(errorsCorrected).IsEquivalentTo(errorCount);
        await Assert.That(codeword).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task TryCorrect_ErrorsBeyondCapacity_Fails()
    {
        // Arrange - 10 ECC codewords, capacity is 5, so inject 6 errors
        byte[] data = [64, 86, 134, 86, 242, 7, 118, 134];
        var codeword = BuildCodeword(data, 10);

        var random = new Random(42);
        CorruptDistinctPositions(codeword, 6, random);

        var result = EccBinaryDecoder.TryCorrect(codeword, 10, out _);

        await Assert.That(result).IsFalse();
    }

    [Test]
    [Arguments(7)]   // ECC level L (version 1)
    [Arguments(16)]  // M
    [Arguments(22)]  // Q
    [Arguments(30)]  // H (max per QR spec)
    public async Task TryCorrect_EveryQrEccCount_RoundTrips(int eccCount)
    {
        var data = new byte[20];
        var random = new Random(1234);
        random.NextBytes(data);
        var codeword = BuildCodeword(data, eccCount);
        var expected = (byte[])codeword.Clone();

        var capacity = eccCount / 2;
        CorruptDistinctPositions(codeword, capacity, random);

        var result = EccBinaryDecoder.TryCorrect(codeword, eccCount, out var errorsCorrected);

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

            var result = EccBinaryDecoder.TryCorrect(codeword, eccCount, out var errorsCorrected);

            await Assert.That(result).IsTrue().Because($"round {round}: dataLength={dataLength}, eccCount={eccCount}, errors={errors}");
            await Assert.That(errorsCorrected).IsEquivalentTo(errors);
            await Assert.That(codeword).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
    }

    public static IEnumerable<int> ShortEccCounts() => Enumerable.Range(1, 14);

    /// <summary>
    /// The decoder against the definition of bounded-distance decoding: a block within ⌊eccCount/2⌋ of a codeword corrects to it, and any other block fails.
    /// </summary>
    /// <remarks>
    /// Two data codewords make 65,536 codewords, few enough to find the nearest by search, so the check needs no second decoder.
    /// Blocks far from every codeword are where the error locator grows longest, and Micro QR's ECC counts are all below 15.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(ShortEccCounts))]
    public async Task TryCorrect_TwoDataCodewords_IsTheNearestCodewordWithinCapacity(int eccCount)
    {
        const int DataCount = 2;
        var length = DataCount + eccCount;
        var codewords = new byte[65536 * length];
        for (var d = 0; d < 65536; d++)
        {
            var codeword = codewords.AsSpan(d * length, length);
            codeword[0] = (byte)(d >> 8);
            codeword[1] = (byte)d;
            EccBinaryEncoder.CalculateECC(codeword.Slice(0, DataCount), codeword.Slice(DataCount), eccCount);
        }

        var capacity = eccCount / 2;
        var random = new Random(eccCount);
        var corrected = 0;
        var failed = 0;
        var first = "";
        var mismatches = 0;
        for (var trial = 0; trial < 60; trial++)
        {
            // Errors from none to two past capacity, and every fifth block uniform noise
            var block = new byte[length];
            if (trial % 5 == 4)
            {
                random.NextBytes(block);
            }
            else
            {
                codewords.AsSpan(random.Next(65536) * length, length).CopyTo(block);
                CorruptDistinctPositions(block, Math.Min(length, trial % (capacity + 3)), random);
            }

            var nearest = -1;
            var nearestDistance = int.MaxValue;
            for (var d = 0; d < 65536; d++)
            {
                var codeword = codewords.AsSpan(d * length, length);
                var distance = 0;
                for (var i = 0; i < length && distance < nearestDistance; i++)
                {
                    if (codeword[i] != block[i])
                        distance++;
                }
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = d;
                }
            }

            var received = (byte[])block.Clone();
            var ok = EccBinaryDecoder.TryCorrect(block, eccCount, out var errorsCorrected);
            var want = nearestDistance <= capacity;
            var same = ok == want && (!ok || (errorsCorrected == nearestDistance && block.AsSpan().SequenceEqual(codewords.AsSpan(nearest * length, length))));
            if (!same && mismatches++ == 0)
                first = $"trial {trial}: block {Convert.ToHexString(received)}, nearest at {nearestDistance}, decoder {ok} with {errorsCorrected}";
            if (want)
                corrected++;
            else
                failed++;
        }

        await Assert.That(mismatches).IsEqualTo(0).Because(first);
        await Assert.That(corrected).IsGreaterThanOrEqualTo(15).Because("blocks within capacity have to be corrected, or only failures are compared");
        await Assert.That(failed).IsGreaterThanOrEqualTo(15).Because("blocks past capacity have to fail, or only corrections are compared");
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
