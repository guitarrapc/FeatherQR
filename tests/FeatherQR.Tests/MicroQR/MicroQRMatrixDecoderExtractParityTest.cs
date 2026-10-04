using FeatherQR.Internals.MicroQR;

namespace FeatherQR.Tests;

/// <summary>
/// The codeword extraction the decoder runs (<see cref="MicroQRMatrixDecoder.ExtractCodewords"/>: the placement table read in order, eight modules to a byte)
/// against a per-module walk of the two-column zigzag, byte for byte, for every version and level, every mask pattern, and both module views.
/// </summary>
/// <remarks>
/// The reference here reads no placement table: it walks the zigzag over <see cref="MicroQRModulePlacer.IsFunctionModule"/> and evaluates
/// <see cref="MicroQRModulePlacer.GetMaskBit"/> per module, setting one bit at a time. The grids are not symbols, so the dark modules
/// follow no codeword structure, and the data and ECC runs are checked apart from Reed-Solomon. M1 and M3 end their data on a half codeword.
/// </remarks>
public class MicroQRMatrixDecoderExtractParityTest
{
    public static IEnumerable<(MicroQRVersion Version, MicroQREccLevel Level)> AllVersionLevelCombinations()
    {
        yield return (MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly);
        yield return (MicroQRVersion.M2, MicroQREccLevel.L);
        yield return (MicroQRVersion.M2, MicroQREccLevel.M);
        yield return (MicroQRVersion.M3, MicroQREccLevel.L);
        yield return (MicroQRVersion.M3, MicroQREccLevel.M);
        yield return (MicroQRVersion.M4, MicroQREccLevel.L);
        yield return (MicroQRVersion.M4, MicroQREccLevel.M);
        yield return (MicroQRVersion.M4, MicroQREccLevel.Q);
    }

    // 0 light; 1..3 all dark written as 1 / 0xFF / 2 (the contract is "non-zero = dark"); the rest pseudo-random,
    // dark modules carrying arbitrary non-zero bytes.
    private const int GridShapes = 8;

    [Test]
    [MethodDataSource(nameof(AllVersionLevelCombinations))]
    public async Task ExtractCodewords_MatchesReferenceWalk_AllMasksGridsAndViews(MicroQRVersion version, MicroQREccLevel level)
    {
        var size = 9 + 2 * (int)version;
        var dataBitCount = MicroQRConstants.GetDataBitCapacity(version, level);
        var dataCodewords = MicroQRConstants.GetDataCodewordCount(version, level);
        var total = dataCodewords + MicroQRConstants.GetEccCodewordCount(version, level);

        for (var shape = 0; shape < GridShapes; shape++)
        {
            var grid = Grid(size, shape, (int)version * 8 + (int)level);
            var transposed = Transpose(grid, size);
            for (var mask = 0; mask < 4; mask++)
            {
                var expected = Reference(grid, size, mask, dataBitCount, dataCodewords, total);

                var actual = new byte[total];
                MicroQRMatrixDecoder.ExtractCodewords(grid, new MatrixModules(size), size, mask, dataBitCount, dataCodewords, actual);
                await Assert.That(actual.AsSpan().SequenceEqual(expected)).IsTrue()
                    .Because($"{version} {level} mask {mask} grid shape {shape}: first difference at byte {FirstDifference(actual, expected)}");

                // The transposed view over the transposed grid reads the same modules.
                var viaView = new byte[total];
                MicroQRMatrixDecoder.ExtractCodewords(transposed, new TransposedModules<MatrixModules>(new MatrixModules(size)), size, mask, dataBitCount, dataCodewords, viaView);
                await Assert.That(viaView.AsSpan().SequenceEqual(expected)).IsTrue()
                    .Because($"{version} {level} mask {mask} grid shape {shape}, transposed view: first difference at byte {FirstDifference(viaView, expected)}");
            }
        }
    }

    /// <summary>The zigzag walked module by module: column pairs from the right, up then down, data bits from byte 0 and ECC bits from byte <paramref name="dataCodewords"/>.</summary>
    private static byte[] Reference(byte[] grid, int size, int mask, int dataBitCount, int dataCodewords, int total)
    {
        var block = new byte[total];
        var totalBits = dataBitCount + (total - dataCodewords) * 8;
        var bitIndex = 0;
        var upward = true;
        for (var right = size - 1; right >= 2; right -= 2)
        {
            for (var step = 0; step < size; step++)
            {
                var row = upward ? size - 1 - step : step;
                for (var col = right; col >= right - 1; col--)
                {
                    if (MicroQRModulePlacer.IsFunctionModule(row, col))
                        continue;
                    if (bitIndex == totalBits)
                        return block;

                    if ((grid[row * size + col] != 0) != MicroQRModulePlacer.GetMaskBit(mask, row, col))
                    {
                        var position = bitIndex < dataBitCount ? bitIndex : dataCodewords * 8 + bitIndex - dataBitCount;
                        block[position / 8] |= (byte)(0x80 >> position % 8);
                    }
                    bitIndex++;
                }
            }
            upward = !upward;
        }
        return block;
    }

    private static byte[] Grid(int size, int shape, int seed)
    {
        var grid = new byte[size * size];
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
                for (var i = 0; i < grid.Length; i++)
                {
                    state = state * 1664525u + 1013904223u;
                    var dark = (state >> 16 & 1) != 0;
                    grid[i] = dark ? (byte)(1 + (state >> 20) % 255) : (byte)0;
                }
                break;
        }
        return grid;
    }

    private static byte[] Transpose(byte[] grid, int size)
    {
        var transposed = new byte[grid.Length];
        for (var row = 0; row < size; row++)
        {
            for (var col = 0; col < size; col++)
                transposed[col * size + row] = grid[row * size + col];
        }
        return transposed;
    }

    private static int FirstDifference(byte[] actual, byte[] expected)
    {
        for (var i = 0; i < actual.Length; i++)
        {
            if (actual[i] != expected[i])
                return i;
        }
        return -1;
    }
}
