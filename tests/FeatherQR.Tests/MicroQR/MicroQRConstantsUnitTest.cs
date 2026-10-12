using FeatherQR.Internals.MicroQR;

namespace FeatherQR.Tests;

public class MicroQRConstantsUnitTest
{
    // The 32 Micro QR format information patterns (ISO/IEC 18004: BCH(15,5) with
    // generator 0x537, XOR mask 0x4445), indexed by (symbolNumber << 2 | mask).
    // Values are independently derivable from the ISO definition. Index 0
    // (data 00000 -> 0x0000 ^ 0x4445) and index 1 (data 00001 -> 0x0537 ^ 0x4445)
    // are verified by hand in the naive reference test below.
    private static readonly ushort[] expectedFormatInfos =
    [
        0x4445, 0x4172, 0x4E2B, 0x4B1C, 0x55AE, 0x5099, 0x5FC0, 0x5AF7,
        0x6793, 0x62A4, 0x6DFD, 0x68CA, 0x7678, 0x734F, 0x7C16, 0x7921,
        0x06DE, 0x03E9, 0x0CB0, 0x0987, 0x1735, 0x1202, 0x1D5B, 0x186C,
        0x2508, 0x203F, 0x2F66, 0x2A51, 0x34E3, 0x31D4, 0x3E8D, 0x3BBA,
    ];

    // Symbol number encoding (ISO/IEC 18004): version + ECC level -> 3-bit value.
    private static readonly (MicroQRVersion Version, MicroQREccLevel Ecc, int SymbolNumber)[] symbolNumbers =
    [
        (MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly, 0),
        (MicroQRVersion.M2, MicroQREccLevel.L, 1),
        (MicroQRVersion.M2, MicroQREccLevel.M, 2),
        (MicroQRVersion.M3, MicroQREccLevel.L, 3),
        (MicroQRVersion.M3, MicroQREccLevel.M, 4),
        (MicroQRVersion.M4, MicroQREccLevel.L, 5),
        (MicroQRVersion.M4, MicroQREccLevel.M, 6),
        (MicroQRVersion.M4, MicroQREccLevel.Q, 7),
    ];

    public static IEnumerable<(MicroQRVersion version, MicroQREccLevel ecc, int mask, ushort expected)> FormatInfoCases()
    {
        foreach (var (version, ecc, symbolNumber) in symbolNumbers)
        {
            for (var mask = 0; mask < 4; mask++)
            {
                yield return (version, ecc, mask, expectedFormatInfos[(symbolNumber << 2) | mask]);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(FormatInfoCases))]
    public async Task GetFormatBits_MatchesIsoTable(MicroQRVersion version, MicroQREccLevel ecc, int mask, ushort expected)
    {
        var actual = MicroQRConstants.GetFormatBits(version, ecc, mask);
        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    public async Task FormatInfoTable_MatchesNaiveBchReference()
    {
        // Independent naive BCH(15,5) reference: remainder of data*x^10 mod 0x537,
        // then XOR 0x4445 (ISO/IEC 18004 Micro QR format specification).
        for (var data = 0; data < 32; data++)
        {
            var value = data << 10;
            for (var bit = 14; bit >= 10; bit--)
            {
                if ((value & (1 << bit)) != 0)
                {
                    value ^= 0x537 << (bit - 10);
                }
            }
            var expected = (ushort)(((data << 10) | value) ^ 0x4445);
            await Assert.That(expectedFormatInfos[data]).IsEqualTo(expected);
        }
    }

    [Test]
    [Arguments(MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly, 20, 3, 2)]
    [Arguments(MicroQRVersion.M2, MicroQREccLevel.L, 40, 5, 5)]
    [Arguments(MicroQRVersion.M2, MicroQREccLevel.M, 32, 4, 6)]
    [Arguments(MicroQRVersion.M3, MicroQREccLevel.L, 84, 11, 6)]
    [Arguments(MicroQRVersion.M3, MicroQREccLevel.M, 68, 9, 8)]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.L, 128, 16, 8)]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.M, 112, 14, 10)]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.Q, 80, 10, 14)]
    public async Task CodewordTables_MatchIso(MicroQRVersion version, MicroQREccLevel ecc, int dataBits, int dataCodewords, int eccCodewords)
    {
        await Assert.That(MicroQRConstants.GetDataBitCapacity(version, ecc)).IsEqualTo(dataBits);
        await Assert.That(MicroQRConstants.GetDataCodewordCount(version, ecc)).IsEqualTo(dataCodewords);
        await Assert.That(MicroQRConstants.GetEccCodewordCount(version, ecc)).IsEqualTo(eccCodewords);
    }

    [Test]
    [Arguments(MicroQRVersion.M1, 11)]
    [Arguments(MicroQRVersion.M2, 13)]
    [Arguments(MicroQRVersion.M3, 15)]
    [Arguments(MicroQRVersion.M4, 17)]
    public async Task SizeFromVersion_IsoSizes(MicroQRVersion version, int expectedSize)
    {
        await Assert.That(MicroQRConstants.SizeFromVersion(version)).IsEqualTo(expectedSize);
    }

    [Test]
    [Arguments(MicroQRVersion.M1, MicroQREccLevel.L, false)]
    [Arguments(MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly, true)]
    [Arguments(MicroQRVersion.M2, MicroQREccLevel.ErrorDetectionOnly, false)]
    [Arguments(MicroQRVersion.M2, MicroQREccLevel.L, true)]
    [Arguments(MicroQRVersion.M2, MicroQREccLevel.M, true)]
    [Arguments(MicroQRVersion.M2, MicroQREccLevel.Q, false)]
    [Arguments(MicroQRVersion.M3, MicroQREccLevel.Q, false)]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.Q, true)]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.ErrorDetectionOnly, false)]
    public async Task IsValidCombination_VersionEccLegality(MicroQRVersion version, MicroQREccLevel ecc, bool expected)
    {
        await Assert.That(MicroQRConstants.IsValidCombination(version, ecc)).IsEqualTo(expected);
    }
}
