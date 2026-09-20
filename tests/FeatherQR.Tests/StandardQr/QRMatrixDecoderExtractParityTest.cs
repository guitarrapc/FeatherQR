using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The run walk (<see cref="QRMatrixDecoder.ExtractCodewords"/>: the encoder's
/// <see cref="ModulePlacer.PlacementLayout.Ops"/> read backwards, mask from the
/// periodic run mask table) against the per-module walk
/// (<see cref="QRMatrixDecoder.ExtractCodewordsReference"/>), byte for byte, for every
/// version 1..40 and every mask pattern.
/// </summary>
/// <remarks>
/// The reference reads neither <c>Ops</c> nor <c>Index</c>: it walks the zigzag over the
/// blocked-module bitmask and evaluates the mask predicate per module. Once the decoder
/// reads the same placement tables the encoder writes through, a round trip can no
/// longer catch a wrong table, so this test is what holds the tables to an independent
/// walk.
/// </remarks>
public class QRMatrixDecoderExtractParityTest
{
    public static IEnumerable<int> AllVersions() => Enumerable.Range(1, 40);

    // 0 light; 1..3 all dark written as 1 / 0xFF / 2 (the contract is "non-zero = dark",
    // so a kernel that bit-tests instead of comparing against zero fails on 2); the rest
    // pseudo-random, dark modules carrying arbitrary non-zero bytes.
    private const int GridShapes = 7;

    private static byte[] Grid(int length, int shape, int seed)
    {
        var grid = new byte[length];
        switch (shape)
        {
            case 0:
                break;
            case 1:
                grid.AsSpan().Fill(1);
                break;
            case 2:
                grid.AsSpan().Fill(0xFF);
                break;
            case 3:
                grid.AsSpan().Fill(2);
                break;
            default:
                var state = (uint)(seed * 31 + shape) * 2654435761u + 7u;
                for (var i = 0; i < length; i++)
                {
                    state = state * 1664525u + 1013904223u;
                    var dark = (state >> 16 & 1) != 0;
                    grid[i] = dark ? (byte)(1 + (state >> 20) % 255) : (byte)0;
                }
                break;
        }
        return grid;
    }

    private static byte[] Reference(byte[] grid, int version, int mask, int length)
    {
        var layout = ModulePlacer.GetLayout(version);
        var expected = new byte[length];
        QRMatrixDecoder.ExtractCodewordsReference(grid, layout.Size, layout.BlockedMask, mask, expected);
        return expected;
    }

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task ExtractCodewords_MatchesReferenceWalk_AllMasksAndGrids(int version)
    {
        var layout = ModulePlacer.GetLayout(version);
        var total = layout.FreeModules / 8;

        for (var shape = 0; shape < GridShapes; shape++)
        {
            var grid = Grid(layout.Size * layout.Size, shape, version);
            for (var mask = 0; mask < 8; mask++)
            {
                var expected = Reference(grid, version, mask, total);
                // dirty on purpose: the walk must write every byte, not OR into a cleared one
                var actual = new byte[total];
                actual.AsSpan().Fill(0xA5);
                QRMatrixDecoder.ExtractCodewords(grid, layout, mask, actual);

                await Assert.That(actual.AsSpan().SequenceEqual(expected)).IsTrue()
                    .Because($"v{version} mask {mask} grid shape {shape}: first difference at byte {FirstDifference(actual, expected)}");
            }
        }
    }

    /// <summary>
    /// The error correction level does not move the end of the stream (data plus ECC is one
    /// total per version), so the cut is driven here: an output shorter than the version's
    /// total ends the stream inside a run, between the two modules of a row, or inside a
    /// scatter range, depending on the length. The walk stores through unchecked
    /// references, so a byte past the span is memory corruption, not an exception.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task ShortOutput_IsThePrefix_AndWritesNothingPast(int version)
    {
        var layout = ModulePlacer.GetLayout(version);
        var total = layout.FreeModules / 8;
        var grid = Grid(layout.Size * layout.Size, 4, version + 41);

        var lengths = new List<int> { 0, 1, 2, 7, total / 3, total / 2 };
        for (var d = 0; d <= 20 && d <= total; d++)
            lengths.Add(total - d);

        foreach (var length in lengths)
        {
            var mask = (length + version) % 8;
            var expected = Reference(grid, version, mask, length);

            var backing = new byte[total + 8];
            backing.AsSpan().Fill(0xA5);
            QRMatrixDecoder.ExtractCodewords(grid, layout, mask, backing.AsSpan(0, length));

            await Assert.That(backing.AsSpan(0, length).SequenceEqual(expected)).IsTrue()
                .Because($"v{version} mask {mask}: a {length}-byte output must be the stream's prefix");
            await Assert.That(backing.AsSpan(length).IndexOfAnyExcept((byte)0xA5)).IsEqualTo(-1)
                .Because($"v{version} mask {mask}: wrote past a {length}-byte output");
        }
    }

    /// <summary>
    /// Versions with remainder bits have free modules the codewords do not cover. An output
    /// with room for them receives them, as the reference walk's does, and nothing after.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task LongOutput_ReceivesTheRemainderBits_AndKeepsItsTail(int version)
    {
        var layout = ModulePlacer.GetLayout(version);
        var total = layout.FreeModules / 8;
        var grid = Grid(layout.Size * layout.Size, 5, version + 97);

        for (var mask = 0; mask < 8; mask++)
        {
            var expected = Reference(grid, version, mask, total + 3);
            var actual = new byte[total + 3];
            QRMatrixDecoder.ExtractCodewords(grid, layout, mask, actual);

            await Assert.That(actual.AsSpan().SequenceEqual(expected)).IsTrue()
                .Because($"v{version} mask {mask}: {layout.FreeModules % 8} remainder bit(s), first difference at byte {FirstDifference(actual, expected)}");
        }
    }

    /// <summary>
    /// The table is indexed by phase, the symbol by coordinate. Checked at real coordinates,
    /// so the claim under test is the periodicity itself: 12 rows, 6 columns, both directions.
    /// </summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    public async Task RunMaskTable_MatchesThePredicate_AtRealCoordinates(int pattern)
    {
        var mismatches = new List<string>();
        for (var up = 0; up < 2; up++)
        {
            var step = up == 1 ? -1 : 1;
            for (var y = 3; y < 177 - 3; y++)
            {
                for (var x = 1; x < 177; x++)
                {
                    var expected = 0;
                    for (var k = 0; k < 4; k++)
                    {
                        expected = expected << 1 | (QRMatrixDecoder.GetMaskBit(pattern, y + step * k, x) ? 1 : 0);
                        expected = expected << 1 | (QRMatrixDecoder.GetMaskBit(pattern, y + step * k, x - 1) ? 1 : 0);
                    }
                    if (QRMatrixDecoder.GetRunMask(pattern, up == 1, x, y) != expected)
                        mismatches.Add($"up={up} y={y} x={x}");
                }
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task ExtractCodewords_RejectsWhatItCannotIndex()
    {
        var layout = ModulePlacer.GetLayout(1);
        var grid = new byte[layout.Size * layout.Size];
        var output = new byte[26];

        await Assert.That(() => QRMatrixDecoder.ExtractCodewords(grid, layout, 8, output)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRMatrixDecoder.ExtractCodewords(grid, layout, -1, output)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRMatrixDecoder.ExtractCodewords(grid.AsSpan(0, grid.Length - 1), layout, 0, output)).Throws<ArgumentException>();
    }

#if !DEBUG
    /// <summary>
    /// The walk keeps no table per mask pattern or per (version, mask): once a version's layout
    /// exists, a mask it has never seen costs nothing. A cached mask stream, the faster form this
    /// one was chosen over, fails here.
    /// </summary>
    [Test]
    public async Task ExtractCodewords_NewMaskAtAWarmVersion_AllocatesNothing()
    {
        var layout = ModulePlacer.GetLayout(40);
        var grid = Grid(layout.Size * layout.Size, 4, 40);
        var output = new byte[layout.FreeModules / 8];
        QRMatrixDecoder.ExtractCodewords(grid, layout, 0, output);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var mask = 1; mask < 8; mask++)
            QRMatrixDecoder.ExtractCodewords(grid, layout, mask, output);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0);
    }
#endif

    private static int FirstDifference(byte[] actual, byte[] expected)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            if (actual[i] != expected[i])
                return i;
        }
        return -1;
    }
}
