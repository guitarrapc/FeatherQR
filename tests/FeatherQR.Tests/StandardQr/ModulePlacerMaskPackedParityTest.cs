using FeatherQR.Internals.StandardQR;
using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// Verifies that the bit-packed MaskCode (single-word and triple-word paths)
/// selects the same mask pattern and produces a byte-identical matrix as a
/// naive byte-per-module reference implementation of ISO/IEC 18004
/// Section 7.8 (data masking) + Section 8.8.2 (penalty scoring).
/// The reference uses the plain textbook mask formulas and scoring loops,
/// deliberately independent from the production bit tricks.
/// </summary>
public class ModulePlacerMaskPackedParityTest
{
    // Versions covering all structural cases:
    // 1 (no alignment patterns), 2/5/6 (alignment, no version info),
    // 7/10 (version info), 11/12 (61 -> 65 modules: single-word/triple-word
    // boundary), 20/40 (large matrices, multiple alignment rows).
    public static IEnumerable<int> Versions => [1, 2, 5, 6, 7, 10, 11, 12, 13, 20, 40];

    [Test]
    [MethodDataSource(nameof(Versions))]
    public async Task MaskCode_MatchesByteDomainReference(int version)
    {
        QREccLevel[] eccLevels = [QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H];

        foreach (var eccLevel in eccLevels)
        {
            for (var seed = 0; seed < 3; seed++)
            {
                var (buffer, blockedMask, size) = BuildFixture(version, seed);

                var expectedBuffer = (byte[])buffer.Clone();
                var expectedBest = ReferenceMaskCode(expectedBuffer, size, version, blockedMask, eccLevel);

                var actualBuffer = (byte[])buffer.Clone();
                var actualBest = ModulePlacer.MaskCode(actualBuffer, size, version, blockedMask, eccLevel);

                await Assert.That(actualBest).IsEquivalentTo(expectedBest);
                await Assert.That(actualBuffer).IsEquivalentTo(expectedBuffer);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Versions))]
    public async Task MaskCode_AllZeroAndAllOneData_MatchesReference(int version)
    {
        // Degenerate fills maximize penalty-rule hits (long runs, uniform 2x2
        // blocks, extreme balance) and exercise every scoring branch.
        foreach (var fill in new byte[] { 0, 1 })
        {
            var (buffer, blockedMask, size) = BuildFixture(version, seed: 0);
            for (var i = 0; i < buffer.Length; i++)
            {
                if ((blockedMask[i >> 3] & (1 << (i & 7))) == 0)
                {
                    buffer[i] = fill;
                }
            }

            var expectedBuffer = (byte[])buffer.Clone();
            var expectedBest = ReferenceMaskCode(expectedBuffer, size, version, blockedMask, QREccLevel.M);

            var actualBuffer = (byte[])buffer.Clone();
            var actualBest = ModulePlacer.MaskCode(actualBuffer, size, version, blockedMask, QREccLevel.M);

            await Assert.That(actualBest).IsEquivalentTo(expectedBest);
            await Assert.That(actualBuffer).IsEquivalentTo(expectedBuffer);
        }
    }

    /// <summary>
    /// Builds realistic MaskCode inputs the same way QRCodeGenerator.WriteQRMatrix
    /// does: all function patterns placed via ModulePlacer, blocked bitmask built,
    /// and data modules filled through the real zigzag placement with seeded
    /// random codewords.
    /// </summary>
    private static (byte[] Buffer, byte[] BlockedMask, int Size) BuildFixture(int version, int seed)
    {
        var size = 21 + (version - 1) * 4;
        var buffer = new byte[size * size];

        var blockedModules = new Rectangle[128];
        var blockedCount = 0;
        var alignmentPatternLocations = GetAlignmentPatternPositions(version);

        ModulePlacer.PlaceFinderPatterns(buffer, size, blockedModules, ref blockedCount);
        ModulePlacer.ReserveSeparatorAreas(size, blockedModules, ref blockedCount);
        ModulePlacer.PlaceAlignmentPatterns(buffer, size, alignmentPatternLocations, blockedModules, ref blockedCount);
        ModulePlacer.PlaceTimingPatterns(buffer, size, blockedModules, ref blockedCount);
        ModulePlacer.PlaceDarkModule(buffer, size, version, blockedModules, ref blockedCount);
        ModulePlacer.ReserveVersionAreas(size, version, blockedModules, ref blockedCount);

        var blockedMask = new byte[(size * size + 7) / 8];
        QRCodeGenerator.BuildBlockedMask(blockedMask, size, blockedModules.AsSpan(0, blockedCount));

        var freeModules = 0;
        for (var i = 0; i < size * size; i++)
        {
            if ((blockedMask[i >> 3] & (1 << (i & 7))) == 0) freeModules++;
        }
        var data = new byte[(freeModules + 7) / 8];
        new Random(seed).NextBytes(data);
        ModulePlacer.PlaceDataWords(buffer, size, data, blockedMask);

        return (buffer, blockedMask, size);
    }

    private static List<Point> GetAlignmentPatternPositions(int version)
    {
        var table = QRCodeConstants.AlignmentPatternTable;
        for (var i = 0; i < table.Count; i++)
        {
            if (table[i].Version == version)
                return table[i].PatternPositions;
        }
        throw new InvalidOperationException($"Alignment pattern positions not found for version {version}");
    }

    // ---------------------------------
    // Naive byte-per-module reference
    // ---------------------------------

    private static int ReferenceMaskCode(byte[] buffer, int size, int version, byte[] blockedMask, QREccLevel eccLevel)
    {
        var temp = new byte[size * size];
        var bestPatternIndex = 0;
        var bestScore = int.MaxValue;

        for (var patternIndex = 0; patternIndex < 8; patternIndex++)
        {
            Array.Copy(buffer, temp, temp.Length);
            ApplyMask(temp, patternIndex, size, blockedMask);
            ModulePlacer.PlaceFormat(temp, size, QRCodeConstants.GetFormatBits(eccLevel, patternIndex));
            if (version >= 7)
            {
                ModulePlacer.PlaceVersion(temp, size, QRCodeConstants.GetVersionBits(version));
            }

            var score = ReferenceScore(temp, size);
            if (score < bestScore)
            {
                bestPatternIndex = patternIndex;
                bestScore = score;
            }
        }

        ApplyMask(buffer, bestPatternIndex, size, blockedMask);
        return bestPatternIndex;
    }

    private static void ApplyMask(byte[] buffer, int patternIndex, int size, byte[] blockedMask)
    {
        for (var row = 0; row < size; row++)
        {
            for (var col = 0; col < size; col++)
            {
                var idx = row * size + col;
                if ((blockedMask[idx >> 3] & (1 << (idx & 7))) != 0) continue;
                if (MaskHit(patternIndex, row, col))
                {
                    buffer[idx] ^= 1;
                }
            }
        }
    }

    /// <summary>Plain ISO/IEC 18004 Section 7.8.2 mask condition formulas.</summary>
    private static bool MaskHit(int patternIndex, int row, int col) => patternIndex switch
    {
        0 => (row + col) % 2 == 0,
        1 => row % 2 == 0,
        2 => col % 3 == 0,
        3 => (row + col) % 3 == 0,
        4 => (row / 2 + col / 3) % 2 == 0,
        5 => (row * col) % 2 + (row * col) % 3 == 0,
        6 => ((row * col) % 2 + (row * col) % 3) % 2 == 0,
        7 => (((row + col) % 2) + ((row * col) % 3)) % 2 == 0,
        _ => throw new ArgumentOutOfRangeException(nameof(patternIndex)),
    };

    /// <summary>Plain ISO/IEC 18004 Section 8.8.2 penalty scoring (two-pass, byte per module).</summary>
    private static int ReferenceScore(byte[] buffer, int size)
    {
        const uint PATTERN_FORWARD = 0b_0000_1011101;
        const uint PATTERN_BACKWARD = 0b_1011101_0000;
        const uint MASK_11BIT = 0b_111_1111_1111;

        var score1 = 0;
        var score2 = 0;
        var score3 = 0;
        var blackModules = 0;

        for (var y = 0; y < size; y++)
        {
            var rowModCount = 0;
            var rowLastValue = buffer[y * size] != 0;
            uint rowBits = 0;

            for (var x = 0; x < size; x++)
            {
                var current = buffer[y * size + x] != 0;

                if (current) blackModules++;

                if (current == rowLastValue)
                {
                    rowModCount++;
                    if (rowModCount >= 5)
                    {
                        score1 += rowModCount == 5 ? 3 : 1;
                    }
                }
                else
                {
                    rowModCount = 1;
                    rowLastValue = current;
                }

                if (x < size - 1 && y < size - 1)
                {
                    var right = buffer[y * size + x + 1] != 0;
                    var bottom = buffer[(y + 1) * size + x] != 0;
                    var diag = buffer[(y + 1) * size + x + 1] != 0;
                    if (current == right && current == bottom && current == diag)
                    {
                        score2 += 3;
                    }
                }

                rowBits = ((rowBits << 1) | (current ? 1u : 0u)) & MASK_11BIT;
                if (x >= 10 && (rowBits == PATTERN_FORWARD || rowBits == PATTERN_BACKWARD))
                {
                    score3 += 40;
                }
            }
        }

        for (var x = 0; x < size; x++)
        {
            var colModCount = 0;
            var colLastValue = buffer[x] != 0;
            uint colBits = 0;

            for (var y = 0; y < size; y++)
            {
                var current = buffer[y * size + x] != 0;

                if (current == colLastValue)
                {
                    colModCount++;
                    if (colModCount >= 5)
                    {
                        score1 += colModCount == 5 ? 3 : 1;
                    }
                }
                else
                {
                    colModCount = 1;
                    colLastValue = current;
                }

                colBits = ((colBits << 1) | (current ? 1u : 0u)) & MASK_11BIT;
                if (y >= 10 && (colBits == PATTERN_FORWARD || colBits == PATTERN_BACKWARD))
                {
                    score3 += 40;
                }
            }
        }

        var percent = (blackModules / (double)(size * size)) * 100;
        var prevMultipleOf5 = Math.Abs((int)Math.Floor(percent / 5) * 5 - 50) / 5;
        var nextMultipleOf5 = Math.Abs((int)Math.Ceiling(percent / 5) * 5 - 50) / 5;
        var score4 = Math.Min(prevMultipleOf5, nextMultipleOf5) * 10;

        return score1 + score2 + score3 + score4;
    }
}
