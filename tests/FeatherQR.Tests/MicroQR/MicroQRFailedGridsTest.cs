using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.MicroQR;

namespace FeatherQR.Tests;

/// <summary>
/// What lets the grid searches skip a grid they decoded before: a grid whose modules alone decided its failure fails the same wherever it was sampled, and the record of such grids answers for the same modules only.
/// </summary>
public class MicroQRFailedGridsTest
{
    private static readonly int[] Sizes = [11, 13, 15, 17];

    public static IEnumerable<int> Seeds() => Enumerable.Range(0, 8);

    /// <summary>
    /// The record against a list of the last grids remembered: a grid is seen when one of them has its size and its modules.
    /// </summary>
    /// <remarks>
    /// Grids come from a small pool so the same one returns, each time over different bytes past its last module, which the record must not read.
    /// Some pool grids share their modules with a grid of another size, so the size has to count too, and some differ from another only in their last module.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(Seeds))]
    public async Task Seen_IsTheLastRememberedGridsOnly(int seed)
    {
        var random = new Random(seed);
        var pool = new List<(byte[] Modules, int Size)>();
        for (var i = 0; i < 90; i++)
        {
            var size = Sizes[random.Next(Sizes.Length)];
            var modules = new byte[size * size];
            for (var p = 0; p < modules.Length; p++)
                modules[p] = (byte)random.Next(2);
            pool.Add((modules, size));
            // The same leading modules read at the next size up
            if (size < 17 && i % 10 == 0)
            {
                var larger = new byte[(size + 2) * (size + 2)];
                modules.CopyTo(larger, 0);
                pool.Add((larger, size + 2));
            }
            // The same grid but for its last module, the one past the last whole eight at every size
            if (i % 10 == 5)
            {
                var other = (byte[])modules.Clone();
                other[^1] ^= 1;
                pool.Add((other, size));
            }
        }

        var keys = new ulong[(MicroQRFailedGrids.Capacity + 1) * MicroQRFailedGrids.Words];
        var prints = new ulong[MicroQRFailedGrids.Capacity];
        var record = new MicroQRFailedGrids(keys, prints);
        var remembered = new List<(byte[] Modules, int Size)>();
        var buffer = new byte[17 * 17];
        var seen = 0;
        var evicted = 0;
        var first = "";
        var mismatches = 0;
        for (var step = 0; step < 3000; step++)
        {
            var (modules, size) = pool[random.Next(pool.Count)];
            random.NextBytes(buffer);
            for (var p = 0; p < buffer.Length; p++)
                buffer[p] &= 1;
            modules.CopyTo(buffer, 0);

            var want = remembered.Any(r => r.Size == size && r.Modules.AsSpan(0, size * size).SequenceEqual(modules.AsSpan(0, size * size)));
            var got = record.Seen(buffer, size);
            if (got != want && mismatches++ == 0)
                first = $"step {step}: size {size}, seen {got}";
            if (want)
                seen++;
            if (!got && random.Next(4) != 0)
            {
                record.Remember();
                remembered.Add((modules, size));
                if (remembered.Count > MicroQRFailedGrids.Capacity)
                {
                    remembered.RemoveAt(0);
                    evicted++;
                }
            }
        }

        await Assert.That(mismatches).IsEqualTo(0).Because(first);
        await Assert.That(seen).IsGreaterThanOrEqualTo(500).Because("grids have to return, or only absences are compared");
        await Assert.That(evicted).IsGreaterThanOrEqualTo(500).Because("the record has to fill and drop its oldest, or no eviction is compared");
    }

    /// <summary>
    /// A sampled grid's decode, at the quiet zone of a clean image and of a dark one: where the modules alone decided the result, it is the same, and they decide it the same way at both.
    /// </summary>
    /// <remarks>
    /// The quiet zone is the one input of a sampled decode besides the modules, so it stands for the place a grid was sampled at.
    /// The count of results the quiet zone changed shows the comparison can fail: those are grids Reed-Solomon corrected, which the modules do not decide.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(Seeds))]
    public async Task DecidedByModules_IsTheSameResultAtAnyQuietZone(int seed)
    {
        var random = new Random(200 + seed);
        var decided = 0;
        var changedByQuietZone = 0;
        var first = "";
        var mismatches = 0;
        for (var i = 0; i < 400; i++)
        {
            var (matrix, size) = (i % 4) switch
            {
                0 => SymbolMatrix(random, damage: 0),
                1 => SymbolMatrix(random, damage: random.Next(1, 8)),
                2 => FormattedNoise(random),
                _ => Noise(random),
            };
            for (var transposed = 0; transposed < 2; transposed++)
            {
                var clean = Decode(matrix, size, transposed == 1, quietZoneDark: 0, out var cleanDecided);
                // Every module of the quiet-zone line dark: column size and row size, 2 · size + 1 modules
                var dark = Decode(matrix, size, transposed == 1, quietZoneDark: 2 * size + 1, out var darkDecided);
                var same = cleanDecided == darkDecided && (!cleanDecided || clean == dark);
                if (!same && mismatches++ == 0)
                    first = $"case {i} size {size} transposed {transposed}: {clean} ({cleanDecided}) against {dark} ({darkDecided})";
                if (cleanDecided)
                    decided++;
                else if (clean != dark)
                    changedByQuietZone++;
            }
        }

        await Assert.That(mismatches).IsEqualTo(0).Because(first);
        await Assert.That(decided).IsGreaterThanOrEqualTo(300).Because("failures the modules decide have to occur, or nothing is compared");
        await Assert.That(changedByQuietZone).IsGreaterThan(0).Because("the quiet zone has to change some result, or a flag set everywhere would pass");
    }

    /// <summary>
    /// A grid read both ways counts as decided by its modules only when both reads were: one read can fail on its modules where the other corrected and fell short on the quiet zone.
    /// </summary>
    /// <remarks>
    /// An M1 symbol transposed fails its format word as sampled and passes Reed-Solomon transposed. M1 corrects nothing, so its read needs the quiet zone's evidence, and against a dark one it fails there: only one of its reads is decided by the modules.
    /// Read as drawn, the same symbol is decided the other way only, so a flag taken from either way alone fails one of the two.
    /// Noise under a valid format word, transposed, fails both ways on its modules.
    /// </remarks>
    [Test]
    public async Task BothWays_IsDecidedByModulesOnlyWhenBothReadsAre()
    {
        var random = new Random(7);
        var straightOnly = 0;
        var mirroredOnly = 0;
        var bothWays = 0;
        var first = "";
        var mismatches = 0;
        for (var i = 0; i < 4000 && (straightOnly < 20 || mirroredOnly < 20 || bothWays < 20); i++)
        {
            var (matrix, size) = i % 2 == 0 ? M1Matrix(random) : FormattedNoise(random);
            if (i % 4 != 2)
                Transpose(matrix, size);
            var quietZoneDark = 2 * size + 1;
            var straight = Decode(matrix, size, transposed: false, quietZoneDark, out var straightDecided);
            var mirrored = Decode(matrix, size, transposed: true, quietZoneDark, out var mirroredDecided);
            // A read ends the grid, whatever the other way would do
            if (AttemptStatus.IsTerminal(straight.Status) || AttemptStatus.IsTerminal(mirrored.Status))
                continue;

            var got = BothWaysDecided(matrix, size);
            if (got != (straightDecided && mirroredDecided) && mismatches++ == 0)
                first = $"case {i} size {size}: {got} where the ways were {straightDecided} and {mirroredDecided}";
            if (straightDecided && !mirroredDecided)
                straightOnly++;
            else if (mirroredDecided && !straightDecided)
                mirroredOnly++;
            else if (straightDecided)
                bothWays++;
        }

        await Assert.That(mismatches).IsEqualTo(0).Because(first);
        await Assert.That(straightOnly).IsGreaterThanOrEqualTo(20).Because("grids decided as sampled only have to occur, or a flag from the read as sampled alone would pass");
        await Assert.That(mirroredOnly).IsGreaterThanOrEqualTo(20).Because("grids decided transposed only have to occur, or a flag from the transposed read alone would pass");
        await Assert.That(bothWays).IsGreaterThanOrEqualTo(20);
    }

    /// <summary>
    /// A grid that failed on its quiet zone is not kept, so a later grid with its modules still reads: a symbol whose quiet zone a dark band crosses at the finder's scale reads through a nudged grid past the band.
    /// </summary>
    /// <remarks>
    /// Each scene is an M1 symbol turned and drawn crisp, with a dark band from a hair past a far side to about a module out, where the grids at and near the finder's scale sample the quiet zone.
    /// M1 corrects nothing and its format word reads, so only the quiet zone fails such a grid, and a nudge that keeps the symbol's modules fails the same until one samples past the band.
    /// So a search that kept a grid the quiet zone failed, in the base grid, the scale search or the perspective search, would skip the grids that read.
    /// </remarks>
    [Test]
    [Arguments("07658", 13.373, 302.213, 0, 0.002, 1.068)]
    [Arguments("28850", 11.804, 50.308, 1, 0.011, 1.071)]
    [Arguments("56192", 9.629, 155.426, 1, 0.009, 1.041)]
    [Arguments("49070", 9.230, 55.011, 1, 0.003, 1.042)]
    [Arguments("36597", 7.677, 137.811, 1, 0.016, 1.112)]
    public async Task GridFailedByItsQuietZone_IsNotKept(string text, double pixelsPerModule, double degrees, int sides, double bandFrom, double bandTo)
    {
        var (banded, width, height, grid) = Banded(text, pixelsPerModule, degrees, sides, bandFrom, bandTo);
        var (clear, _, _, _) = Banded(text, pixelsPerModule, degrees, sides, bandFrom, bandFrom);

        // The premise: the grid drawn at the finder's scale reads without the band, and with it fails on the quiet zone alone
        await Assert.That(DecodeExactGrid(clear, width, height, grid, out _)).IsEqualTo(DecodeStatus.Success);
        await Assert.That(DecodeExactGrid(banded, width, height, grid, out var decided)).IsNotEqualTo(DecodeStatus.Success);
        await Assert.That(decided).IsFalse();

        await Assert.That(MicroQRCodeDecoder.TryDecodeImage(banded, width, height, out var read, out _)).IsTrue();
        await Assert.That(read).IsEqualTo(text);
    }

    /// <summary>
    /// An M1 symbol turned by <paramref name="degrees"/> about the image centre, crisp, and a dark band from <paramref name="bandFrom"/> to <paramref name="bandTo"/> modules past its bottom side (sides 0), its right side (1) or both (2).
    /// </summary>
    private static (byte[] Luminance, int Width, int Height, MicroQRImageDecoder.AffineGrid Grid) Banded(string text, double pixelsPerModule, double degrees, int sides, double bandFrom, double bandTo)
    {
        var symbol = MicroQRCodeGenerator.Create(text, MicroQREccLevel.ErrorDetectionOnly, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
        var size = symbol.Size;
        var side = (int)Math.Ceiling((size + 14) * pixelsPerModule * 1.45);
        var luminance = new byte[side * side];
        var theta = degrees * (Math.PI / 180);
        double cx = side / 2.0, cy = side / 2.0, cos = Math.Cos(theta), sin = Math.Sin(theta);
        for (var py = 0; py < side; py++)
        {
            for (var px = 0; px < side; px++)
            {
                double dx = px + 0.5 - cx, dy = py + 0.5 - cy;
                var mx = (dx * cos + dy * sin) / pixelsPerModule + size / 2.0;
                var my = (-dx * sin + dy * cos) / pixelsPerModule + size / 2.0;
                var dark = false;
                if (mx >= 0 && my >= 0 && mx < size && my < size)
                    dark = symbol[(int)my, (int)mx];
                else if (sides != 1 && my >= size + bandFrom && my < size + bandTo && mx >= -1 && mx < size + 1)
                    dark = true;
                else if (sides != 0 && mx >= size + bandFrom && mx < size + bandTo && my >= -1 && my < size + 1)
                    dark = true;
                luminance[py * side + px] = dark ? (byte)0 : (byte)255;
            }
        }
        var half = size / 2.0;
        var grid = new MicroQRImageDecoder.AffineGrid(
            (float)(cx + (-half * cos + half * sin) * pixelsPerModule), (float)(cy + (-half * sin - half * cos) * pixelsPerModule),
            (float)(cos * pixelsPerModule), (float)(sin * pixelsPerModule), (float)(-sin * pixelsPerModule), (float)(cos * pixelsPerModule));
        return (luminance, side, side, grid);
    }

    /// <summary>The M1 grid of <paramref name="grid"/> sampled at its module centres and read both ways, with the quiet zone read from the image.</summary>
    private static DecodeStatus DecodeExactGrid(byte[] luminance, int width, int height, MicroQRImageDecoder.AffineGrid grid, out bool decidedByModules)
    {
        const int Size = 11;
        var modules = new byte[Size * Size];
        for (var row = 0; row < Size; row++)
        {
            for (var col = 0; col < Size; col++)
            {
                grid.Map(col + 0.5f, row + 0.5f, out var x, out var y);
                modules[row * Size + col] = luminance[(int)y * width + (int)x] < 128 ? (byte)1 : (byte)0;
            }
        }
        var both = new MicroQRImageDecoder.BothWays<MicroQRImageDecoder.AffineGrid>(grid, Size);
        var result = new SearchResult<MicroQRCodeDecodeInfo>(ReportRule.Furthest, default);
        var status = both.Decode(modules, new ImageView(luminance, width, height, 128, default), new char[64], out _, out _, ref result);
        decidedByModules = both.DecidedByModules;
        return status;
    }

    /// <summary>The image path's grid read both ways, over an image dark everywhere, so its quiet zone counts every module dark as the matrix decode above was told.</summary>
    private static bool BothWaysDecided(byte[] matrix, int size)
    {
        var luminance = new byte[40 * 40];
        var image = new ImageView(luminance, 40, 40, 128, default);
        var grid = new MicroQRImageDecoder.BothWays<MicroQRImageDecoder.AffineGrid>(new MicroQRImageDecoder.AffineGrid(2f, 2f, 1f, 0f, 0f, 1f), size);
        var result = new SearchResult<MicroQRCodeDecodeInfo>(ReportRule.Furthest, default);
        grid.Decode(matrix, image, new char[64], out _, out _, ref result);
        return grid.DecidedByModules;
    }

    private static void Transpose(byte[] modules, int size)
    {
        for (var y = 0; y < size; y++)
        {
            for (var x = y + 1; x < size; x++)
                (modules[y * size + x], modules[x * size + y]) = (modules[x * size + y], modules[y * size + x]);
        }
    }

    private readonly record struct Outcome(DecodeStatus Status, MicroQRCodeDecodeInfo Info, int CharsWritten);

    private static Outcome Decode(byte[] matrix, int size, bool transposed, int quietZoneDark, out bool decidedByModules)
    {
        var destination = new char[64];
        var quietZone = new CountedQuietZone(quietZoneDark);
        var status = transposed
            ? MicroQRMatrixDecoder.DecodeSampledGrid(matrix, new TransposedModules<MatrixModules>(new MatrixModules(size)), size, default, 0, 0, 0, quietZone, destination, out var charsWritten, out var info, out decidedByModules)
            : MicroQRMatrixDecoder.DecodeSampledGrid(matrix, new MatrixModules(size), size, default, 0, 0, 0, quietZone, destination, out charsWritten, out info, out decidedByModules);
        return new Outcome(status, info, charsWritten);
    }

    private static readonly (string Text, MicroQREccLevel Level)[] Payloads =
    [
        ("12345", MicroQREccLevel.ErrorDetectionOnly),
        ("0123456789", MicroQREccLevel.L),
        ("12345678", MicroQREccLevel.M),
        ("HELLO WORLD 14", MicroQREccLevel.L),
        ("byte hi", MicroQREccLevel.M),
        ("HELLO WORLD PLUS 21ST", MicroQREccLevel.L),
        ("bytes m4 mode", MicroQREccLevel.M),
        ("bytes!!!!", MicroQREccLevel.Q),
    ];

    private static (byte[] Matrix, int Size) M1Matrix(Random random)
    {
        var data = MicroQRCodeGenerator.Create(random.Next(100000).ToString("D5"), MicroQREccLevel.ErrorDetectionOnly, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
        var matrix = new byte[data.Size * data.Size];
        for (var row = 0; row < data.Size; row++)
        {
            for (var col = 0; col < data.Size; col++)
                matrix[row * data.Size + col] = data[row, col] ? (byte)1 : (byte)0;
        }
        return (matrix, data.Size);
    }

    private static (byte[] Matrix, int Size) SymbolMatrix(Random random, int damage)
    {
        var (text, level) = Payloads[random.Next(Payloads.Length)];
        var data = MicroQRCodeGenerator.Create(text, level, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
        var size = data.Size;
        var matrix = new byte[size * size];
        for (var row = 0; row < size; row++)
        {
            for (var col = 0; col < size; col++)
                matrix[row * size + col] = data[row, col] ? (byte)1 : (byte)0;
        }
        // Damage away from the format word, so the grid reaches error correction
        for (var d = 0; d < damage; d++)
        {
            var row = random.Next(9, size);
            var col = random.Next(1, size);
            matrix[row * size + col] ^= 1;
        }
        return (matrix, size);
    }

    /// <summary>Random modules under a valid format word, so the codewords reach error correction and fail there.</summary>
    private static (byte[] Matrix, int Size) FormattedNoise(Random random)
    {
        var (matrix, size) = Noise(random);
        var version = MicroQRConstants.VersionFromSize(size);
        var level = version switch
        {
            MicroQRVersion.M1 => MicroQREccLevel.ErrorDetectionOnly,
            MicroQRVersion.M4 => (MicroQREccLevel)random.Next(1, 4),
            _ => (MicroQREccLevel)random.Next(1, 3),
        };
        var format = MicroQRConstants.GetFormatBits(version, level, random.Next(4));
        var bit = 14;
        for (var col = 1; col <= 8; col++, bit--)
            matrix[8 * size + col] = (byte)(format >> bit & 1);
        for (var row = 7; row >= 1; row--, bit--)
            matrix[row * size + 8] = (byte)(format >> bit & 1);
        return (matrix, size);
    }

    private static (byte[] Matrix, int Size) Noise(Random random)
    {
        var size = Sizes[random.Next(Sizes.Length)];
        var matrix = new byte[size * size];
        for (var p = 0; p < matrix.Length; p++)
            matrix[p] = (byte)random.Next(2);
        return (matrix, size);
    }
}
