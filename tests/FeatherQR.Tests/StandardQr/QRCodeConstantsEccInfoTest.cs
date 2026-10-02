using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="QRCodeConstants.GetEccInfo"/> is an index into a table built in <c>[version - 1][L, M, Q, H]</c> order. These hold the
/// index to that order: a lookup returns its own version and level, its codewords fill the version's data modules exactly,
/// and a version outside the table fails as an argument error rather than as an index out of range.
/// </summary>
public class QRCodeConstantsEccInfoTest
{
    [Test]
    public async Task EveryVersionAndLevel_IsItsOwnEntry()
    {
        for (var version = 1; version <= 40; version++)
        {
            // The placement layout counts the modules data can take, independently of the ECC table: every level of a
            // version fills them with the same number of codewords, only the split between data and ECC changes.
            var codewords = ModulePlacer.GetLayout(version).FreeModules / 8;
            foreach (var level in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
            {
                var info = QRCodeConstants.GetEccInfo(version, level);
                await Assert.That(info.Version).IsEqualTo(version);
                await Assert.That(info.ErrorCorrectionLevel).IsEqualTo(level);
                var blocks = info.BlocksInGroup1 + info.BlocksInGroup2;
                await Assert.That(info.BlocksInGroup1 * info.CodewordsInGroup1 + info.BlocksInGroup2 * info.CodewordsInGroup2).IsEqualTo(info.TotalDataCodewords);
                await Assert.That(info.TotalDataCodewords + blocks * info.ECCPerBlock).IsEqualTo(codewords).Because($"version {version}-{level}");
            }
        }
    }

    [Test]
    [Arguments(1, QREccLevel.L, 19)]
    [Arguments(1, QREccLevel.H, 9)]
    [Arguments(19, QREccLevel.M, 627)]
    [Arguments(39, QREccLevel.H, 1222)]
    [Arguments(40, QREccLevel.L, 2956)]
    [Arguments(40, QREccLevel.H, 1276)]
    public async Task DataCodewords_MatchTheStandard(int version, QREccLevel level, int dataCodewords)
    {
        // ISO/IEC 18004 Table 9 anchors, including the two symbols the benchmarks named one version high.
        await Assert.That(QRCodeConstants.GetEccInfo(version, level).TotalDataCodewords).IsEqualTo(dataCodewords);
    }

    [Test]
    [Arguments(0)]
    [Arguments(41)]
    [Arguments(-1)]
    [Arguments(int.MinValue)]
    public async Task VersionOutsideTheTable_ThrowsArgumentException(int version)
    {
        await Assert.That(() => QRCodeConstants.GetEccInfo(version, QREccLevel.M)).ThrowsExactly<ArgumentException>();
    }
}
