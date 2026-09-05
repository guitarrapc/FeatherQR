using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// Unit tests for format information decoding: exact match for all 32 valid
/// patterns, BCH(15,5) correction up to 3 bit errors, copy selection, and
/// rejection beyond correction distance.
/// </summary>
public class FormatInformationDecoderUnitTest
{
    [Test]
    public async Task AllValidPatterns_DecodeExactly()
    {
        for (var level = 0; level < 4; level++)
        {
            for (var mask = 0; mask < 8; mask++)
            {
                var bits = QRCodeConstants.GetFormatBits((QREccLevel)level, mask);

                await Assert.That(FormatInformationDecoder.TryDecode(bits, bits, out var eccLevel, out var maskPattern)).IsTrue();
                await Assert.That(eccLevel).IsEquivalentTo((QREccLevel)level);
                await Assert.That(maskPattern).IsEquivalentTo(mask);
            }
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BitErrorsWithinBchCapacity_AreCorrected(int errorBits)
    {
        // Flip N distinct bits in every valid pattern; BCH(15,5) has minimum
        // distance 7, so up to 3 errors must decode to the original.
        for (var level = 0; level < 4; level++)
        {
            for (var mask = 0; mask < 8; mask++)
            {
                var bits = QRCodeConstants.GetFormatBits((QREccLevel)level, mask);
                var corrupted = bits;
                for (var b = 0; b < errorBits; b++)
                {
                    corrupted ^= (ushort)(1 << (b * 5 % 15)); // distinct positions
                }

                await Assert.That(FormatInformationDecoder.TryDecode(corrupted, corrupted, out var eccLevel, out var maskPattern)).IsTrue().Because($"level={(QREccLevel)level}, mask={mask}, errors={errorBits}");
                await Assert.That(eccLevel).IsEquivalentTo((QREccLevel)level);
                await Assert.That(maskPattern).IsEquivalentTo(mask);
            }
        }
    }

    [Test]
    public async Task CorruptedFirstCopy_IntactSecondCopy_DecodesViaSecond()
    {
        var bits = QRCodeConstants.GetFormatBits(QREccLevel.Q, 5);
        var destroyed = FindPatternFarFromAllFormats();

        await Assert.That(FormatInformationDecoder.TryDecode(destroyed, bits, out var eccLevel, out var maskPattern)).IsTrue();
        await Assert.That(eccLevel).IsEquivalentTo(QREccLevel.Q);
        await Assert.That(maskPattern).IsEquivalentTo(5);
    }

    [Test]
    public async Task BothCopiesBeyondCorrectionDistance_ReturnsFalse()
    {
        var destroyed = FindPatternFarFromAllFormats();

        await Assert.That(FormatInformationDecoder.TryDecode(destroyed, destroyed, out _, out _)).IsFalse();
    }

    /// <summary>
    /// Finds a 15-bit pattern with Hamming distance &gt; 3 from every valid masked
    /// format pattern (flipping arbitrary bits is not enough: the complement of a
    /// codeword can be close to another codeword).
    /// </summary>
    private static ushort FindPatternFarFromAllFormats()
    {
        for (var pattern = 0; pattern < 0x8000; pattern++)
        {
            var minDistance = int.MaxValue;
            for (var level = 0; level < 4; level++)
            {
                for (var mask = 0; mask < 8; mask++)
                {
                    var candidate = QRCodeConstants.GetFormatBits((QREccLevel)level, mask);
                    var distance = CountBits((ushort)(pattern ^ candidate));
                    if (distance < minDistance)
                        minDistance = distance;
                }
            }
            if (minDistance > 3)
                return (ushort)pattern;
        }
        throw new InvalidOperationException("no pattern found beyond correction distance");
    }

    private static int CountBits(ushort v)
    {
        var count = 0;
        while (v != 0)
        {
            count += v & 1;
            v >>= 1;
        }
        return count;
    }
}
