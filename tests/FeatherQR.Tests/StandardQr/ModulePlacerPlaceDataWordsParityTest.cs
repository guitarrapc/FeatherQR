using FeatherQR.Internals.StandardQR;
using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// Verifies that the accumulator-based PlaceDataWords (sequential 64-bit stream
/// reader + column-pair fast path + ref access) produces a byte-identical
/// matrix to a naive per-module reference implementation of ISO/IEC 18004
/// Section 7.7.3 (symbol character placement).
/// The reference walks the zigzag one module at a time and extracts each data
/// bit with an indexed byte load, deliberately independent from the production
/// bit tricks.
/// </summary>
public class ModulePlacerPlaceDataWordsParityTest
{
    // Versions covering all structural cases:
    // 1 (no alignment patterns), 2/5/6 (alignment, no version info),
    // 7/10 (version info), 11/12/13, 20/40 (large matrices, multiple
    // alignment rows).
    public static IEnumerable<int> Versions => [1, 2, 5, 6, 7, 10, 11, 12, 13, 20, 40];

    [Test]
    [MethodDataSource(nameof(Versions))]
    public async Task PlaceDataWords_MatchesPerModuleReference(int version)
    {
        var (pristine, blockedMask, size, freeModules) = BuildFixture(version);

        // Stream lengths covering every consumption path:
        // floor(capacity)  = real-world shape (0-7 remainder modules left empty)
        // ceil(capacity)   = stream outlives the free modules
        // 5 bytes          = stream exhausts early (early-return path)
        // 0 bytes          = nothing placed
        int[] dataLengths = [freeModules / 8, (freeModules + 7) / 8, 5, 0];

        foreach (var dataLength in dataLengths)
        {
            for (var seed = 0; seed < 3; seed++)
            {
                var data = new byte[dataLength];
                new Random(seed).NextBytes(data);

                var expected = (byte[])pristine.Clone();
                ReferencePlaceDataWords(expected, size, data, blockedMask);

                var actual = (byte[])pristine.Clone();
                ModulePlacer.PlaceDataWords(actual, size, data, blockedMask);

                await Assert.That(actual).IsEquivalentTo(expected);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Versions))]
    public async Task PlaceDataWords_AllZeroAndAllOneData_MatchesReference(int version)
    {
        // Degenerate streams exercise the fast path with constant accumulator
        // contents (every 2-bit consume identical).
        var (pristine, blockedMask, size, freeModules) = BuildFixture(version);

        foreach (var fill in new byte[] { 0x00, 0xFF })
        {
            var data = new byte[freeModules / 8];
            data.AsSpan().Fill(fill);

            var expected = (byte[])pristine.Clone();
            ReferencePlaceDataWords(expected, size, data, blockedMask);

            var actual = (byte[])pristine.Clone();
            ModulePlacer.PlaceDataWords(actual, size, data, blockedMask);

            await Assert.That(actual).IsEquivalentTo(expected);
        }
    }

    [Test]
    public void PlaceDataWords_UndersizedBuffer_Throws()
    {
        var (_, blockedMask, size, _) = BuildFixture(1);
        var data = new byte[8];

        Assert.Throws<ArgumentException>(() =>
        {
            var small = new byte[size * size - 1];
            ModulePlacer.PlaceDataWords(small, size, data, blockedMask);
        });
    }

    [Test]
    public void PlaceDataWords_UndersizedBlockedMask_Throws()
    {
        var (pristine, blockedMask, size, _) = BuildFixture(1);
        var data = new byte[8];

        Assert.Throws<ArgumentException>(() =>
        {
            var smallMask = blockedMask.AsSpan(0, blockedMask.Length - 1);
            ModulePlacer.PlaceDataWords(pristine, size, data, smallMask);
        });
    }

    [Test]
    public void PlaceDataWords_TooSmallSize_Throws()
    {
        var buffer = new byte[36];
        var mask = new byte[5];
        var data = new byte[4];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ModulePlacer.PlaceDataWords(buffer, 6, data, mask));
    }

    [Test]
    [Arguments(46341)]      // smallest size whose square overflows int
    [Arguments(65536)]      // size*size wraps to exactly 0 in int
    [Arguments(int.MaxValue)]
    public void PlaceDataWords_SizeSquareOverflowsInt_Throws(int size)
    {
        // size*size computed in int would wrap (to a small or negative value)
        // and slip past the length validation, letting the unchecked writes run
        // out of bounds. The validation must reject these instead.
        var buffer = new byte[64];
        var mask = new byte[64];
        var data = new byte[8];

        Assert.Throws<ArgumentException>(() =>
            ModulePlacer.PlaceDataWords(buffer, size, data, mask));
    }

    /// <summary>
    /// Builds the matrix state right before data placement the same way
    /// QRCodeGenerator.WriteQRMatrix does: all function patterns placed via
    /// ModulePlacer and the blocked bitmask built.
    /// </summary>
    private static (byte[] Buffer, byte[] BlockedMask, int Size, int FreeModules) BuildFixture(int version)
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

        return (buffer, blockedMask, size, freeModules);
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
    // Naive per-module reference
    // ---------------------------------

    /// <summary>Plain ISO/IEC 18004 Section 7.7.3 zigzag placement, one module and one indexed bit load at a time.</summary>
    private static void ReferencePlaceDataWords(byte[] buffer, int size, byte[] interleavedData, byte[] blockedMask)
    {
        var bitPos = 0;
        var totalBits = interleavedData.Length * 8;
        var up = true;

        for (var x = size - 1; x >= 0; x -= 2)
        {
            if (x == 6)
                x--;

            for (var yMod = 1; yMod <= size; yMod++)
            {
                var y = up ? size - yMod : yMod - 1;

                for (var xOffset = 0; xOffset < 2; xOffset++)
                {
                    var bitIndex = y * size + (x - xOffset);

                    if ((blockedMask[bitIndex >> 3] & (1 << (bitIndex & 7))) == 0 && bitPos < totalBits)
                    {
                        var bit = (interleavedData[bitPos >> 3] & (1 << (7 - (bitPos & 7)))) != 0;
                        buffer[bitIndex] = bit ? (byte)1 : (byte)0;
                        bitPos++;
                    }
                }
            }

            up = !up;
        }
    }
}
