using System.Buffers;
using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The entry that returns every symbol of an image whatever its symbology (<see cref="MixedImageDecoder"/>): each drawn symbol once, as its own symbology and with its own corners, and nothing that is not drawn.
/// </summary>
public class MixedSymbolsDecodeTest
{
    /// <summary>A symbol drawn on a test image: its symbology, its text, its module-area corners in the drawn order, and its module size there.</summary>
    private sealed record Drawn(Symbology Symbology, string Text, (float X, float Y)[] Corners, float ModulePixels);

    /// <summary>A result, in the parts every symbology has.</summary>
    private sealed record Read(Symbology Symbology, string Text, SymbolCorners Corners);

    /// <summary>What a result's corner may be off by on these renders, in modules, as each symbology's own tests allow.</summary>
    private static float CornerTolerance(Symbology symbology) => symbology == Symbology.RmQR ? 1.5f : 1f;

    /// <summary>The quiet zone each symbology is drawn with on a sheet, in modules.</summary>
    private static int QuietZone(Symbology symbology) => symbology == Symbology.StandardQR ? 4 : 2;

    private static Read[] Decode(byte[] luminance, int width, int height, int room = 256)
    {
        var symbols = new SymbolDecodeResult[room];
        var count = MixedImageDecoder.DecodeSymbols(luminance, width, height, symbols);
        return [.. symbols.AsSpan(0, count).ToArray().Select(ToRead)];
    }

    private static Read[] DecodeGrowing(byte[] luminance, int width, int height, CancellationToken cancellationToken = default)
        => [.. MixedImageDecoder.DecodeSymbols(luminance, width, height, cancellationToken).Select(ToRead)];

    private static Read ToRead(SymbolDecodeResult symbol) => new(symbol.Symbology, symbol.Text, symbol.Corners);

    /// <summary>What the every-symbol entry of one symbology reads of the image.</summary>
    private static Read[] OwnEntry(Symbology symbology, byte[] luminance, int width, int height) => symbology switch
    {
        Symbology.StandardQR => [.. QRImageDecoder.DecodeSymbols(luminance, width, height).Select(static s => new Read(Symbology.StandardQR, s.Text, s.Info.Corners))],
        Symbology.MicroQR => [.. MicroQRImageDecoder.DecodeSymbols(luminance, width, height).Select(static s => new Read(Symbology.MicroQR, s.Text, s.Info.Corners))],
        _ => [.. RmQRImageDecoder.DecodeSymbols(luminance, width, height).Select(static s => new Read(Symbology.RmQR, s.Text, s.Info.Corners))],
    };

    /// <summary>Every drawn symbol is a result once, as its symbology, with its text and its corners in the drawn order, and there is no other result.</summary>
    private static async Task AssertReadsEach(Read[] read, IReadOnlyList<Drawn> drawn)
    {
        foreach (var symbol in drawn)
        {
            var matches = read.Where(r => r.Text == symbol.Text).ToArray();
            await Assert.That(matches.Length).IsEqualTo(1).Because($"{symbol.Symbology} {symbol.Text} is read once");
            await Assert.That(matches[0].Symbology).IsEqualTo(symbol.Symbology);
            var corners = matches[0].Corners;
            ImagePoint[] actual = [corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft];
            for (var i = 0; i < 4; i++)
            {
                var distance = MathF.Sqrt((actual[i].X - symbol.Corners[i].X) * (actual[i].X - symbol.Corners[i].X) + (actual[i].Y - symbol.Corners[i].Y) * (actual[i].Y - symbol.Corners[i].Y));
                await Assert.That(distance).IsLessThanOrEqualTo(CornerTolerance(symbol.Symbology) * symbol.ModulePixels)
                    .Because($"{symbol.Text} corner {i}: got ({actual[i].X:F1}, {actual[i].Y:F1}), drawn at ({symbol.Corners[i].X:F1}, {symbol.Corners[i].Y:F1})");
            }
        }
        await Assert.That(read.Length).IsEqualTo(drawn.Count).Because($"read {string.Join(", ", read.Select(static r => $"{r.Symbology} \"{r.Text}\""))}");
    }

    /// <summary>The results start with what the Standard QR entry reads of the image, in its order.</summary>
    private static async Task AssertStandardQRFirst(Read[] read, byte[] luminance, int width, int height)
    {
        var standard = OwnEntry(Symbology.StandardQR, luminance, width, height);
        await Assert.That(read.Take(standard.Length)).IsEquivalentTo(standard, CollectionOrdering.Matching);
    }

    /// <summary>A symbol of the symbology holding <paramref name="text"/>, without its quiet zone: each takes the smallest version that holds the text.</summary>
    private static (bool[] Modules, int Columns, int Rows) Symbol(Symbology symbology, string text)
    {
        switch (symbology)
        {
            case Symbology.StandardQR:
                var qr = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
                return (Modules(qr.Size, qr.Size, (r, c) => qr[r, c]), qr.Size, qr.Size);
            case Symbology.MicroQR:
                var micro = MicroQRCodeGenerator.Create(text, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
                return (Modules(micro.Size, micro.Size, (r, c) => micro[r, c]), micro.Size, micro.Size);
            default:
                var rmqr = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = 0 });
                return (Modules(rmqr.Width, rmqr.Height, (r, c) => rmqr[r, c]), rmqr.Width, rmqr.Height);
        }

        static bool[] Modules(int width, int height, Func<int, int, bool> isDark)
        {
            var modules = new bool[width * height];
            for (var r = 0; r < height; r++)
                for (var c = 0; c < width; c++)
                    modules[r * width + c] = isDark(r, c);
            return modules;
        }
    }

    /// <summary>Texts of several versions: Standard QR's are versions 1 to 3, Micro QR's M2 to M4, rMQR's three widths.</summary>
    private static string Text(Symbology symbology, int index) => symbology switch
    {
        Symbology.StandardQR => $"FQR MIXED QR {index}" + new string('Q', index % 3 * 12),
        Symbology.MicroQR => (index % 3) switch { 0 => $"MQ{index}", 1 => $"{index}0123456789", _ => $"FQR MICRO {index}" },
        _ => $"FQR RMQR {index}" + new string('A', index % 3 * 10),
    };

    public enum Mix { StandardAndMicro, StandardAndRmQR, MicroAndRmQR, AllThree }

    /// <summary><paramref name="count"/> symbols, the symbologies of <paramref name="mix"/> in turn.</summary>
    private static List<(Symbology Symbology, string Text)> Symbols(Mix mix, int count)
    {
        Symbology[] symbologies = mix switch
        {
            Mix.StandardAndMicro => [Symbology.StandardQR, Symbology.MicroQR],
            Mix.StandardAndRmQR => [Symbology.StandardQR, Symbology.RmQR],
            Mix.MicroAndRmQR => [Symbology.MicroQR, Symbology.RmQR],
            _ => [Symbology.StandardQR, Symbology.MicroQR, Symbology.RmQR],
        };
        return [.. Enumerable.Range(0, count).Select(i => (symbologies[i % symbologies.Length], Text(symbologies[i % symbologies.Length], i)))];
    }

    /// <summary>
    /// A sheet of the symbols, <paramref name="across"/> a row, each in a cell of its own size plus its symbology's quiet zone all round, each row as tall as its tallest cell, as a module grid with where each symbol lies in it.
    /// </summary>
    private static (Func<int, int, bool> IsDark, int Columns, int Rows, List<(Symbology Symbology, string Text, int Left, int Top, int Columns, int Rows)> Placed) Sheet(IReadOnlyList<(Symbology Symbology, string Text)> symbols, int across)
    {
        var placed = new List<(Symbology Symbology, string Text, int Left, int Top, int Columns, int Rows)>();
        var modules = new List<bool[]>();
        int top = 0, columns = 0;
        for (var first = 0; first < symbols.Count; first += across)
        {
            int left = 0, tallest = 0;
            for (var i = first; i < Math.Min(first + across, symbols.Count); i++)
            {
                var (symbology, text) = symbols[i];
                var (m, c, r) = Symbol(symbology, text);
                var quiet = QuietZone(symbology);
                placed.Add((symbology, text, left + quiet, top + quiet, c, r));
                modules.Add(m);
                left += c + 2 * quiet;
                tallest = Math.Max(tallest, r + 2 * quiet);
            }
            columns = Math.Max(columns, left);
            top += tallest;
        }

        // Which symbol each module belongs to, so that a supersampled render looks each sample up once
        var owner = new int[top * columns];
        Array.Fill(owner, -1);
        for (var i = 0; i < placed.Count; i++)
        {
            var (_, _, l, t, c, r) = placed[i];
            for (var row = 0; row < r; row++)
                owner.AsSpan((t + row) * columns + l, c).Fill(i);
        }
        var width = columns;
        return ((row, column) =>
        {
            var i = owner[row * width + column];
            return i >= 0 && modules[i][(row - placed[i].Top) * placed[i].Columns + column - placed[i].Left];
        }, columns, top, placed);
    }

    /// <summary>The sheet drawn by <see cref="SupersampledRenderer"/>, turned and under keystone, with where each symbol was drawn.</summary>
    private static (byte[] Luminance, int Width, int Height, List<Drawn> Drawn) RenderSheet(IReadOnlyList<(Symbology Symbology, string Text)> symbols, int across, float pixelsPerModule, float degrees, float keystone)
    {
        var (isDark, columns, rows, placed) = Sheet(symbols, across);
        var (luminance, width, height) = SupersampledRenderer.Render(isDark, columns, rows, pixelsPerModule, degrees, keystone);
        var truth = SupersampledGeometry.GridToPixel(columns, rows, pixelsPerModule, degrees, keystone);
        var drawn = new List<Drawn>();
        foreach (var (symbology, text, left, top, c, r) in placed)
        {
            var corners = new (float X, float Y)[4];
            truth.Transform(left, top, out corners[0].X, out corners[0].Y);
            truth.Transform(left + c, top, out corners[1].X, out corners[1].Y);
            truth.Transform(left + c, top + r, out corners[2].X, out corners[2].Y);
            truth.Transform(left, top + r, out corners[3].X, out corners[3].Y);
            drawn.Add(new Drawn(symbology, text, corners, pixelsPerModule));
        }
        return (luminance, width, height, drawn);
    }

    /// <summary>
    /// Sheets of each pair of symbologies and of all three, turned and under keystone: each symbol is one result, as its own symbology and with its own corners, through both forms alike, and the Standard QR entry's reads come first.
    /// </summary>
    [Test]
    [Arguments(Mix.StandardAndMicro, 2, 1, 0f, 0f)]
    [Arguments(Mix.StandardAndMicro, 2, 2, 30f, 0f)]
    [Arguments(Mix.StandardAndMicro, 3, 3, 0f, 0.04f)]
    [Arguments(Mix.StandardAndRmQR, 2, 1, 0f, 0f)]
    [Arguments(Mix.StandardAndRmQR, 2, 2, 75f, 0f)]
    [Arguments(Mix.StandardAndRmQR, 3, 3, 10f, 0.03f)]
    [Arguments(Mix.MicroAndRmQR, 2, 1, 0f, 0f)]
    [Arguments(Mix.MicroAndRmQR, 2, 2, 100f, 0f)]
    [Arguments(Mix.MicroAndRmQR, 3, 3, 0f, 0.04f)]
    [Arguments(Mix.AllThree, 3, 1, 0f, 0f)]
    [Arguments(Mix.AllThree, 3, 2, 45f, 0f)]
    [Arguments(Mix.AllThree, 3, 3, 190f, 0.03f)]
    public async Task SheetOfSymbologies_EachIsReadOnce_AsItsSymbology(Mix mix, int across, int down, float degrees, float keystone)
    {
        var (luminance, width, height, drawn) = RenderSheet(Symbols(mix, across * down), across, 4f, degrees, keystone);

        var read = Decode(luminance, width, height);

        await AssertReadsEach(read, drawn);
        await Assert.That(DecodeGrowing(luminance, width, height)).IsEquivalentTo(read, CollectionOrdering.Matching);
        await AssertStandardQRFirst(read, luminance, width, height);
    }

    /// <summary>A sheet of one symbology is what that symbology's entry reads of it, to the order and the corners.</summary>
    [Test]
    [Arguments(Symbology.StandardQR)]
    [Arguments(Symbology.MicroQR)]
    [Arguments(Symbology.RmQR)]
    public async Task SheetOfOneSymbology_IsThatSymbologysRead(Symbology symbology)
    {
        var symbols = Enumerable.Range(0, 9).Select(i => (symbology, Text(symbology, i))).ToList();
        var (luminance, width, height, drawn) = RenderSheet(symbols, 3, 4f, 20f, 0f);
        var own = OwnEntry(symbology, luminance, width, height);
        // The premise: the symbology's entry reads every symbol
        await Assert.That(own.Length).IsEqualTo(drawn.Count);

        await Assert.That(Decode(luminance, width, height)).IsEquivalentTo(own, CollectionOrdering.Matching);
        await Assert.That(DecodeGrowing(luminance, width, height)).IsEquivalentTo(own, CollectionOrdering.Matching);
    }

    /// <summary>An image of one symbol, named for the test's display.</summary>
    public sealed record OneSymbolImage(Symbology Symbology, string Name, byte[] Luminance, int Width, int Height)
    {
        public override string ToString() => $"{Symbology} {Name}";
    }

    /// <summary>One symbol and its quiet zone as a module grid, light on dark when <paramref name="reversed"/>.</summary>
    private static (Func<int, int, bool> IsDark, int Columns, int Rows) Tile(Symbology symbology, string text, bool reversed = false)
    {
        var (modules, columns, rows) = Symbol(symbology, text);
        var quietZone = QuietZone(symbology);
        return ((row, column) =>
        {
            int r = row - quietZone, c = column - quietZone;
            var dark = r >= 0 && c >= 0 && r < rows && c < columns && modules[r * columns + c];
            return dark != reversed;
        }, columns + 2 * quietZone, rows + 2 * quietZone);
    }

    /// <summary>
    /// Images of one symbol each: Standard QR renders of each path its passes read by, and the Micro QR and rMQR images of their own entries' test, the committed real images among them.
    /// </summary>
    public static IEnumerable<Func<OneSymbolImage>> OneSymbolImages()
    {
        var text = Text(Symbology.StandardQR, 4);
        var tile = Tile(Symbology.StandardQR, text);
        yield return () => Image("crisp 4 px", NearestNeighbourRenderer.Render(tile.IsDark, tile.Columns, tile.Rows, 4f, 0f, 0f));
        yield return () => Image("crisp 1.3 px", NearestNeighbourRenderer.Render(tile.IsDark, tile.Columns, tile.Rows, 1.3f, 0.75f, 0.75f));
        yield return () => Image("turned 37°", SupersampledRenderer.Render(tile.IsDark, tile.Columns, tile.Rows, 4f, 37f));
        yield return () => Image("keystone 8 %", SupersampledRenderer.Render(tile.IsDark, tile.Columns, tile.Rows, 5f, 200f, 0.08f));
        yield return () =>
        {
            var (l, w, h) = NearestNeighbourRenderer.Render(tile.IsDark, tile.Columns, tile.Rows, 3f, 0f, 0f);
            return Image("mirrored", NearestNeighbourRenderer.Turn(l, w, h, 1, mirror: true));
        };
        yield return () => Image("reversed", NearestNeighbourRenderer.Render((r, c) => !tile.IsDark(r, c), tile.Columns, tile.Rows, 3f, 0f, 0f));
        yield return () => Image("shadowed", UnevenLightingRenderer.Render(tile.IsDark, tile.Columns, tile.Rows, 4, UnevenLight.Shadow, 0f, 0.55f));
        yield return () =>
        {
            var (modules, columns, rows) = Symbol(Symbology.StandardQR, text);
            var photo = CameraRenderer.Render((r, c) => modules[r * columns + c], columns, rows, 4, new CameraRenderer.Shot(4f, 25f, 30f, 10f, BlurSigma: 0.8f, NoiseSigma: 4f, SceneScale: 2f, Seed: 7));
            return new OneSymbolImage(Symbology.StandardQR, "photograph in a scene", photo.Luminance, photo.Width, photo.Height);
        };

        foreach (var image in SingleFinderSymbolsDecodeTest.OneSymbolImages())
        {
            yield return () =>
            {
                var one = image();
                return new OneSymbolImage(one.Symbology == SingleFinderSymbolsDecodeTest.Symbology.MicroQR ? Symbology.MicroQR : Symbology.RmQR, one.Name, one.Luminance, one.Width, one.Height);
            };
        }

        static OneSymbolImage Image(string name, (byte[] Luminance, int Width, int Height) image) => new(Symbology.StandardQR, name, image.Luminance, image.Width, image.Height);
    }

    /// <summary>
    /// An image of one symbol is what its symbology's entry reads of it and nothing more. The other two decoders search every finder of it, and a result of theirs there would be a false read.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(OneSymbolImages))]
    public async Task OneSymbolImage_IsItsSymbologysRead_Alone(OneSymbolImage image)
    {
        var own = OwnEntry(image.Symbology, image.Luminance, image.Width, image.Height);
        await Assert.That(own.Length).IsLessThanOrEqualTo(1);

        var read = Decode(image.Luminance, image.Width, image.Height);

        await Assert.That(read).IsEquivalentTo(own, CollectionOrdering.Matching).Because($"read {string.Join(", ", read.Select(static r => $"{r.Symbology} \"{r.Text}\""))}");
    }

    /// <summary>A symbol of <paramref name="symbology"/> whose data is scrambled past correction, its finder, timing patterns and format information as drawn.</summary>
    private static (Func<int, int, bool> IsDark, int Columns, int Rows) Unreadable(Symbology symbology, string text)
    {
        var (modules, columns, rows) = Symbol(symbology, text);
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var data = symbology == Symbology.RmQR
                    ? r >= 1 && r < rows - 1 && c >= 12 && c < columns - 8
                    : r >= 9 && c >= 9;
                if (data && (r * 7 + c * 13) % 5 < 2)
                    modules[r * columns + c] = !modules[r * columns + c];
            }
        }
        return ((r, c) => modules[r * columns + c], columns, rows);
    }

    /// <summary>
    /// A symbol whose data does not read, alone and in a turned grid of 16, gives no result. Its finders are candidates of the other two decoders, whose searches must not read them as their own.
    /// </summary>
    [Test]
    [Arguments(Symbology.StandardQR, 1)]
    [Arguments(Symbology.StandardQR, 4)]
    [Arguments(Symbology.MicroQR, 4)]
    [Arguments(Symbology.RmQR, 4)]
    public async Task SymbolsWhoseDataDoesNotRead_GiveNoResult(Symbology symbology, int side)
    {
        var cells = Enumerable.Range(0, side * side).Select(i => Unreadable(symbology, Text(symbology, i))).ToArray();
        var quiet = QuietZone(symbology);
        int cellColumns = cells.Max(static c => c.Columns) + 2 * quiet, cellRows = cells.Max(static c => c.Rows) + 2 * quiet;
        var (luminance, width, height) = SupersampledRenderer.Render((row, column) =>
        {
            var (isDark, columns, rows) = cells[row / cellRows * side + column / cellColumns];
            int r = row % cellRows - quiet, c = column % cellColumns - quiet;
            return r >= 0 && c >= 0 && r < rows && c < columns && isDark(r, c);
        }, side * cellColumns, side * cellRows, side == 1 ? 4f : 3f, 10f);
        // The premises: the symbology's own entry reads nothing, and the single-finder decoders have the finders to search
        await Assert.That(OwnEntry(symbology, luminance, width, height)).IsEmpty();
        await Assert.That(StridedCandidates(luminance, width, height)).IsGreaterThanOrEqualTo(symbology == Symbology.StandardQR ? 3 * side * side : side * side);

        await Assert.That(Decode(luminance, width, height)).IsEmpty();
        await Assert.That(DecodeGrowing(luminance, width, height)).IsEmpty();

        static int StridedCandidates(byte[] luminance, int width, int height)
        {
            var histogram = new int[Binarizer.HistogramBins];
            Binarizer.FillHistogram(luminance, histogram);
            var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);
            var candidates = new FinderPattern[4096];
            return FinderPatternFinder.FindCandidatesStrided(luminance, width, height, threshold, candidates, grey);
        }
    }

    /// <summary>
    /// A Micro QR symbol drawn inside the data of a Standard QR symbol that its error correction reads through: the Micro QR finder is a candidate inside a read symbol, so it is not decoded, and the Standard QR code is the only result.
    /// </summary>
    [Test]
    public async Task CandidateInsideAStandardQRRead_IsNotDecoded()
    {
        var qr = QRCodeGenerator.Create("FQR MIXED OUTER SYMBOL", QREccLevel.H, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(10), QuietZoneSize = 0 });
        var (micro, microSize, _) = Symbol(Symbology.MicroQR, "MQ IN");
        // Clear of the alignment patterns at 28 and 50, with a quiet zone of two all round
        const int At = 31;
        var inner = microSize + 4;
        var tile = ((Func<int, int, bool>)((row, column) =>
        {
            int r = row - 4, c = column - 4;
            if (r < 0 || c < 0 || r >= qr.Size || c >= qr.Size)
                return false;
            if (r >= At && c >= At && r < At + inner && c < At + inner)
            {
                int mr = r - At - 2, mc = c - At - 2;
                return mr >= 0 && mc >= 0 && mr < microSize && mc < microSize && micro[mr * microSize + mc];
            }
            return qr[r, c];
        }), qr.Size + 8, qr.Size + 8);
        var (luminance, width, height) = NearestNeighbourRenderer.Render(tile.Item1, tile.Item2, tile.Item3, 4f, 0f, 0f);
        // The premises: each entry alone reads its own symbol
        await Assert.That(OwnEntry(Symbology.StandardQR, luminance, width, height).Select(static r => r.Text)).IsEquivalentTo(["FQR MIXED OUTER SYMBOL"], CollectionOrdering.Matching);
        await Assert.That(OwnEntry(Symbology.MicroQR, luminance, width, height).Select(static r => r.Text)).IsEquivalentTo(["MQ IN"], CollectionOrdering.Matching);

        await Assert.That(Decode(luminance, width, height).Select(static r => r.Text)).IsEquivalentTo(["FQR MIXED OUTER SYMBOL"], CollectionOrdering.Matching);
        await Assert.That(DecodeGrowing(luminance, width, height).Select(static r => r.Text)).IsEquivalentTo(["FQR MIXED OUTER SYMBOL"], CollectionOrdering.Matching);
    }

    /// <summary>A room of any size holds the first results the room of all of them holds, the Standard QR reads first.</summary>
    [Test]
    public async Task Room_HoldsTheFirstResults()
    {
        var (luminance, width, height, drawn) = RenderSheet(Symbols(Mix.AllThree, 6), 3, 4f, 0f, 0f);
        var all = Decode(luminance, width, height);
        await Assert.That(all.Length).IsEqualTo(drawn.Count);

        for (var room = 1; room < all.Length; room++)
            await Assert.That(Decode(luminance, width, height, room)).IsEquivalentTo(all.Take(room), CollectionOrdering.Matching).Because($"room {room}");
        await Assert.That(MixedImageDecoder.DecodeSymbols(luminance, width, height, Span<SymbolDecodeResult>.Empty)).IsEqualTo(0);
    }

    /// <summary>Paper and noise give no result through either form.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NoSymbol_GivesNoResult(bool noise)
    {
        var luminance = new byte[240 * 240];
        if (noise)
            new Random(20261029).NextBytes(luminance);
        else
            Array.Fill(luminance, (byte)255);

        await Assert.That(Decode(luminance, 240, 240)).IsEmpty();
        await Assert.That(DecodeGrowing(luminance, 240, 240)).IsEmpty();
    }

    /// <summary>A token canceled before the call stops it, an image with no symbol included.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task CanceledToken_Throws(bool withSymbols)
    {
        var (luminance, width, height) = withSymbols ? RenderGrid(Mix.AllThree, 3, 1) : (new byte[160 * 160], 160, 160);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.That(() => DecodeGrowing(luminance, width, height, cancellation.Token)).Throws<OperationCanceledException>();
    }

    /// <summary>A sheet of symbols drawn upright at 4 px a module, for a test that only counts what it reads.</summary>
    internal static (byte[] Luminance, int Width, int Height) RenderGrid(Mix mix, int across, int down)
        => RenderGrid(mix, across, down, 0f);

    internal static (byte[] Luminance, int Width, int Height) RenderGrid(Mix mix, int across, int down, float degrees)
    {
        var (luminance, width, height, _) = RenderSheet(Symbols(mix, across * down), across, 4f, degrees, 0f);
        return (luminance, width, height);
    }

    private const string HiddenCode = "X$T4:JZSFVPVCKXJP3VA";

    /// <summary>
    /// Three rMQR symbols of one height above a QR code, one a row, each in its quiet zone, drawn crisp and lit by a shadow from below, with where each was drawn.
    /// With <paramref name="blankRmQR"/> the rMQR symbols are left out and the light falls as it does with them.
    /// </summary>
    private static (byte[] Luminance, int Width, int Height, List<Drawn> Drawn) RenderShadowedColumn(float pixelsPerModule, bool blankRmQR)
    {
        var cells = new List<(Symbology Symbology, string Text, int Columns, int Rows, Func<int, int, bool> IsDark)>();
        foreach (var (text, version) in new[] { ("FQR RMQR 1", RmQRVersion.R13x27), ("FQR RMQR 2 ABCDEFGHIJ", RmQRVersion.R13x43), ("FQR RMQR 3 ABCDEFGHIJKLMNOPQRST", RmQRVersion.R13x59) })
        {
            var rmqr = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = 0, Version = version });
            cells.Add((Symbology.RmQR, text, rmqr.Width, rmqr.Height, (r, c) => rmqr[r, c]));
        }
        var qr = QRCodeGenerator.Create(HiddenCode, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
        cells.Add((Symbology.StandardQR, HiddenCode, qr.Size, qr.Size, (r, c) => qr[r, c]));

        var placed = new List<(int Left, int Top)>();
        int columns = 0, top = 0;
        foreach (var cell in cells)
        {
            var quiet = QuietZone(cell.Symbology);
            placed.Add((quiet, top + quiet));
            top += cell.Rows + 2 * quiet;
            columns = Math.Max(columns, cell.Columns + 2 * quiet);
        }
        var (luminance, width, height) = NearestNeighbourRenderer.Render((row, column) =>
        {
            for (var i = 0; i < cells.Count; i++)
            {
                var (left, t) = placed[i];
                if (row >= t && column >= left && row < t + cells[i].Rows && column < left + cells[i].Columns)
                    return (cells[i].Symbology == Symbology.StandardQR || !blankRmQR) && cells[i].IsDark(row - t, column - left);
            }
            return false;
        }, columns, top, pixelsPerModule, 0f, 0f);
        UnevenLightingRenderer.Light(luminance, width, height, pixelsPerModule, UnevenLight.Shadow, 185f, 0.7f);

        var drawn = cells.Select((cell, i) =>
        {
            var (left, t) = placed[i];
            (float X, float Y)[] corners = [(left, t), (left + cell.Columns, t), (left + cell.Columns, t + cell.Rows), (left, t + cell.Rows)];
            return new Drawn(cell.Symbology, cell.Text, [.. corners.Select(p => (p.X * pixelsPerModule, p.Y * pixelsPerModule))], pixelsPerModule);
        }).ToList();
        return (luminance, width, height, drawn);
    }

    /// <summary>
    /// A QR code below three rMQR symbols in a shadow: the rMQR finders take the Standard QR stages' triple selection, and Standard QR alone does not read the code.
    /// Once the rMQR symbols are read, Standard QR reads again with them taken as read, and the code is the last result.
    /// </summary>
    [Test]
    [Arguments(1.9f)]
    [Arguments(2f)]
    [Arguments(2.1f)]
    public async Task CodeHiddenByRmQRFinders_IsReadByStandardQRsSecondRead(float pixelsPerModule)
    {
        var (luminance, width, height, drawn) = RenderShadowedColumn(pixelsPerModule, blankRmQR: false);
        // The premises: Standard QR alone misses the code, and a second read with its own reads taken as read misses it too, so only the rMQR reads taken as read can find it.
        // With the rMQR symbols left out it reads, so what hides it is their finders
        var standard = QRImageDecoder.DecodeSymbols(luminance, width, height);
        await Assert.That(standard.Select(static s => s.Text)).DoesNotContain(HiddenCode);
        var room = new QRCodeDecodeResult[standard.Length + 8];
        standard.CopyTo(room, 0);
        var again = QRImageDecoder.DecodeSymbolsBeside(luminance, width, height, Histogram(luminance), ref room, standard.Length, CancellationToken.None);
        await Assert.That(room.Take(again).Skip(standard.Length).Select(static s => s.Text)).DoesNotContain(HiddenCode);
        var (blanked, blankedWidth, blankedHeight, _) = RenderShadowedColumn(pixelsPerModule, blankRmQR: true);
        await Assert.That(QRImageDecoder.DecodeSymbols(blanked, blankedWidth, blankedHeight).Select(static s => s.Text)).Contains(HiddenCode);

        var read = Decode(luminance, width, height);

        await AssertReadsEach(read, drawn);
        await Assert.That(read[^1].Text).IsEqualTo(HiddenCode);
        await Assert.That(DecodeGrowing(luminance, width, height)).IsEquivalentTo(read, CollectionOrdering.Matching);
    }

    private static QRCodeDecodeResult Seed(SymbolCorners corners)
        => new("", new QRCodeDecodeInfo(DecodeStatus.Success, 1, QREccLevel.M, 0, 0).WithCorners(corners));

    private static SymbolCorners Corners(Drawn drawn) => new(
        new ImagePoint(drawn.Corners[0].X, drawn.Corners[0].Y), new ImagePoint(drawn.Corners[1].X, drawn.Corners[1].Y),
        new ImagePoint(drawn.Corners[2].X, drawn.Corners[2].Y), new ImagePoint(drawn.Corners[3].X, drawn.Corners[3].Y));

    /// <summary>
    /// The Standard QR read beside symbols already read: a seeded symbol keeps its place and is not read again, a QR code inside a seeded outline is not read, and the others follow the seeds, through both forms.
    /// </summary>
    [Test]
    public async Task StandardQRBeside_SeededSymbols_AreTakenAsRead()
    {
        var symbols = Enumerable.Range(0, 3).Select(i => (Symbology.StandardQR, Text(Symbology.StandardQR, i))).ToList();
        var (luminance, width, height, drawn) = RenderSheet(symbols, 3, 4f, 0f, 0f);
        var read = QRImageDecoder.DecodeSymbols(luminance, width, height);
        await Assert.That(read.Length).IsEqualTo(3);
        var first = read.Single(r => r.Text == drawn[0].Text);
        // The second code's outline as another symbology's read would give it
        var other = Seed(Corners(drawn[1]));

        var room = new QRCodeDecodeResult[8];
        room[0] = first;
        room[1] = other;
        var count = QRImageDecoder.DecodeSymbolsBeside(luminance, width, height, Histogram(luminance), room, seeded: 2);

        await Assert.That(count).IsEqualTo(3);
        await Assert.That(room[0]).IsEqualTo(first);
        await Assert.That(room[1]).IsEqualTo(other);
        await Assert.That(room[2]).IsEqualTo(read.Single(r => r.Text == drawn[2].Text));

        // The growing form's room is rented, as the array form rents it
        var growing = ArrayPool<QRCodeDecodeResult>.Shared.Rent(2);
        growing[0] = first;
        growing[1] = other;
        var grownCount = QRImageDecoder.DecodeSymbolsBeside(luminance, width, height, Histogram(luminance), ref growing, seeded: 2, CancellationToken.None);
        await Assert.That(grownCount).IsEqualTo(3);
        await Assert.That(growing.Take(3)).IsEquivalentTo(room.Take(3), CollectionOrdering.Matching);
        ArrayPool<QRCodeDecodeResult>.Shared.Return(growing, clearArray: true);
    }

    /// <summary>The growing form's room, rented and full, is replaced by a larger rental: eighteen Standard QR codes through a room of sixteen are all read, as the array form reads them.</summary>
    [Test]
    public async Task StandardQRBeside_RoomFull_GrowsFromThePool()
    {
        var symbols = Enumerable.Range(0, 18).Select(i => (Symbology.StandardQR, Text(Symbology.StandardQR, i))).ToList();
        var (luminance, width, height, _) = RenderSheet(symbols, 6, 4f, 0f, 0f);
        var entry = QRImageDecoder.DecodeSymbols(luminance, width, height);
        await Assert.That(entry.Length).IsEqualTo(18);

        var growing = ArrayPool<QRCodeDecodeResult>.Shared.Rent(16);
        // The premise: the room holds sixteen, so the seventeenth read grows it
        await Assert.That(growing.Length).IsEqualTo(16);
        var count = QRImageDecoder.DecodeSymbolsBeside(luminance, width, height, Histogram(luminance), ref growing, seeded: 0, CancellationToken.None);

        await Assert.That(count).IsEqualTo(18);
        await Assert.That(growing.Take(count)).IsEquivalentTo(entry, CollectionOrdering.Matching);
        ArrayPool<QRCodeDecodeResult>.Shared.Return(growing, clearArray: true);
    }

    private static int[] Histogram(byte[] luminance)
    {
        var histogram = new int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(luminance, histogram);
        return histogram;
    }

    /// <summary>
    /// The Standard QR read beside nothing seeded, given the image's own histogram, reads what the entry for every symbol reads, through both forms: that read fills the same histogram from the pixels.
    /// </summary>
    [Test]
    [Arguments(Mix.AllThree, 0f, 0f)]
    [Arguments(Mix.StandardAndRmQR, 30f, 0.04f)]
    public async Task StandardQRBeside_GivenTheImagesHistogram_ReadsAsTheEntry(Mix mix, float degrees, float keystone)
    {
        var (luminance, width, height, _) = RenderSheet(Symbols(mix, 6), 3, 4f, degrees, keystone);
        var entry = QRImageDecoder.DecodeSymbols(luminance, width, height);
        // The premise: the sheet holds Standard QR codes the entry reads
        await Assert.That(entry.Length).IsGreaterThan(0);

        var room = new QRCodeDecodeResult[16];
        var count = QRImageDecoder.DecodeSymbolsBeside(luminance, width, height, Histogram(luminance), room, seeded: 0);
        var growing = ArrayPool<QRCodeDecodeResult>.Shared.Rent(1);
        var grownCount = QRImageDecoder.DecodeSymbolsBeside(luminance, width, height, Histogram(luminance), ref growing, seeded: 0, CancellationToken.None);

        await Assert.That(room.Take(count)).IsEquivalentTo(entry, CollectionOrdering.Matching);
        await Assert.That(growing.Take(grownCount)).IsEquivalentTo(entry, CollectionOrdering.Matching);
        ArrayPool<QRCodeDecodeResult>.Shared.Return(growing, clearArray: true);
    }

    /// <summary>
    /// The histogram given is the one the read's thresholds come from. A sheet drawn between greys 100 and 150 reads with its own histogram.
    /// Given that of a black and white image, its threshold leaves no pixel dark, and an image of two levels takes neither the regional pass nor the reduced scales, so nothing reads.
    /// </summary>
    [Test]
    public async Task StandardQRBeside_GivenAnotherHistogram_ReadsByIt()
    {
        var (rendered, width, height, _) = RenderSheet(Symbols(Mix.StandardAndMicro, 4), 2, 4f, 0f, 0f);
        var luminance = rendered.Select(static p => (byte)(100 + p * 50 / 255)).ToArray();
        var blackAndWhite = new int[Binarizer.HistogramBins];
        blackAndWhite[0] = luminance.Length / 2;
        blackAndWhite[255] = luminance.Length - luminance.Length / 2;
        var room = new QRCodeDecodeResult[16];
        // The premise: with the image's own histogram the read finds the sheet's codes
        await Assert.That(QRImageDecoder.DecodeSymbolsBeside(luminance, width, height, Histogram(luminance), room, seeded: 0)).IsGreaterThan(0);

        await Assert.That(QRImageDecoder.DecodeSymbolsBeside(luminance, width, height, blackAndWhite, room, seeded: 0)).IsEqualTo(0);
    }
}
