using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// The entries that return every Micro QR or rMQR symbol of an image in one call (<see cref="MicroQRImageDecoder.DecodeSymbols(ReadOnlySpan{byte}, int, int, Span{MicroQRCodeDecodeResult}, CancellationToken)"/>, <see cref="RmQRImageDecoder.DecodeSymbols(ReadOnlySpan{byte}, int, int, Span{RmQRCodeDecodeResult}, CancellationToken)"/>):
/// each drawn symbol once, with its own corners, whichever pass reads it, and nothing that is not drawn.
/// </summary>
public class SingleFinderSymbolsDecodeTest
{
    public enum Symbology { MicroQR, RmQR }

    /// <summary>A symbol drawn on a test image: its text, its module-area corners in the drawn order, and its module size there.</summary>
    private sealed record Drawn(string Text, (float X, float Y)[] Corners, float ModulePixels);

    /// <summary>A result, in the parts both symbologies have.</summary>
    private sealed record Read(string Text, DecodeStatus Status, string Version, SymbolCorners Corners);

    private const int QuietZone = 2;

    /// <summary>What a result's corner may be off by on these renders, in modules. rMQR's is wider since its far corners drift under keystone.</summary>
    private static float CornerTolerance(Symbology symbology) => symbology == Symbology.MicroQR ? 1f : 1.5f;

    private static Read[] DecodeSymbols(Symbology symbology, byte[] luminance, int width, int height, int room = 256, CancellationToken cancellationToken = default)
    {
        if (symbology == Symbology.MicroQR)
        {
            var symbols = new MicroQRCodeDecodeResult[room];
            var count = MicroQRImageDecoder.DecodeSymbols(luminance, width, height, symbols, cancellationToken);
            return [.. symbols.AsSpan(0, count).ToArray().Select(static s => new Read(s.Text, s.Info.Status, s.Info.Version.ToString(), s.Info.Corners))];
        }
        else
        {
            var symbols = new RmQRCodeDecodeResult[room];
            var count = RmQRImageDecoder.DecodeSymbols(luminance, width, height, symbols, cancellationToken);
            return [.. symbols.AsSpan(0, count).ToArray().Select(static s => new Read(s.Text, s.Info.Status, s.Info.Version.ToString(), s.Info.Corners))];
        }
    }

    private static Read[] DecodeSymbolsGrowing(Symbology symbology, byte[] luminance, int width, int height, CancellationToken cancellationToken = default) => symbology == Symbology.MicroQR
        ? [.. MicroQRImageDecoder.DecodeSymbols(luminance, width, height, cancellationToken).Select(static s => new Read(s.Text, s.Info.Status, s.Info.Version.ToString(), s.Info.Corners))]
        : [.. RmQRImageDecoder.DecodeSymbols(luminance, width, height, cancellationToken).Select(static s => new Read(s.Text, s.Info.Status, s.Info.Version.ToString(), s.Info.Corners))];

    /// <summary>The one-symbol call's read, or null.</summary>
    private static Read? DecodeOne(Symbology symbology, byte[] luminance, int width, int height)
    {
        if (symbology == Symbology.MicroQR)
            return MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info) ? new Read(text, info.Status, info.Version.ToString(), info.Corners) : null;
        return RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out var rmqrText, out var rmqrInfo) ? new Read(rmqrText, rmqrInfo.Status, rmqrInfo.Version.ToString(), rmqrInfo.Corners) : null;
    }

    /// <summary>Every drawn symbol is a result once, with its text and its corners in the drawn order, and there is no other result.</summary>
    private static async Task AssertReadsEach(Symbology symbology, Read[] read, IReadOnlyList<Drawn> drawn)
    {
        foreach (var symbol in drawn)
        {
            var matches = read.Where(r => r.Text == symbol.Text).ToArray();
            await Assert.That(matches.Length).IsEqualTo(1).Because($"{symbol.Text} is read once");
            var corners = matches[0].Corners;
            ImagePoint[] actual = [corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft];
            for (var i = 0; i < 4; i++)
            {
                var distance = MathF.Sqrt((actual[i].X - symbol.Corners[i].X) * (actual[i].X - symbol.Corners[i].X) + (actual[i].Y - symbol.Corners[i].Y) * (actual[i].Y - symbol.Corners[i].Y));
                await Assert.That(distance).IsLessThanOrEqualTo(CornerTolerance(symbology) * symbol.ModulePixels)
                    .Because($"{symbol.Text} corner {i}: got ({actual[i].X:F1}, {actual[i].Y:F1}), drawn at ({symbol.Corners[i].X:F1}, {symbol.Corners[i].Y:F1})");
            }
            await Assert.That(matches[0].Status).IsEqualTo(DecodeStatus.Success);
        }
        await Assert.That(read.Length).IsEqualTo(drawn.Count).Because($"read {string.Join(", ", read.Select(static r => r.Text))}");
    }

    /// <summary>The first result is the one-symbol call's read of the same image, when that call reads one.</summary>
    private static async Task AssertFirstIsTheOneSymbolRead(Symbology symbology, Read[] read, byte[] luminance, int width, int height)
    {
        var one = DecodeOne(symbology, luminance, width, height);
        if (one is null)
            return;
        await Assert.That(read.Length).IsGreaterThan(0);
        await Assert.That(read[0]).IsEqualTo(one);
    }

    /// <summary>
    /// A symbol of the symbology holding <paramref name="text"/>, without its quiet zone. Micro QR takes the smallest version that holds the text, rMQR the narrowest of <paramref name="height"/> modules.
    /// </summary>
    private static (bool[] Modules, int Columns, int Rows) Symbol(Symbology symbology, string text, int height = 11)
    {
        if (symbology == Symbology.MicroQR)
        {
            var micro = MicroQRCodeGenerator.Create(text, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
            return (Modules(micro.Size, micro.Size, (r, c) => micro[r, c]), micro.Size, micro.Size);
        }
        var version = Enum.GetValues<RmQRVersion>().First(v => RmQRConstants.GetHeight(v) == height && RmQRCodeGenerator.TryGetRequiredBufferSize(text, RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { QuietZoneSize = 0, Version = v }));
        var rmqr = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = 0, Version = version });
        return (Modules(rmqr.Width, rmqr.Height, (r, c) => rmqr[r, c]), rmqr.Width, rmqr.Height);

        static bool[] Modules(int width, int height, Func<int, int, bool> isDark)
        {
            var modules = new bool[width * height];
            for (var r = 0; r < height; r++)
                for (var c = 0; c < width; c++)
                    modules[r * width + c] = isDark(r, c);
            return modules;
        }
    }

    /// <summary>Texts of several versions: Micro QR's are M2 to M4, rMQR's three widths of one height.</summary>
    private static string Text(Symbology symbology, int index) => symbology == Symbology.MicroQR
        ? (index % 3) switch { 0 => $"FQR{index}", 1 => $"{index}0123456789", _ => $"FQR MICRO {index}" }
        : $"FQR RMQR {index}" + new string('A', index % 3 * 10);

    /// <summary>
    /// A sheet of <paramref name="across"/> × <paramref name="down"/> symbols, each in a cell of its own size plus the quiet zone all round, each row as tall as its tallest cell, as a module grid with where each symbol lies in it.
    /// </summary>
    private static (Func<int, int, bool> IsDark, int Columns, int Rows, List<(string Text, int Left, int Top, int Columns, int Rows)> Placed) Sheet(Symbology symbology, int across, int down, Func<int, string>? text = null)
    {
        text ??= i => Text(symbology, i);
        var placed = new List<(string Text, int Left, int Top, int Columns, int Rows)>();
        var symbols = new List<bool[]>();
        int top = 0, columns = 0;
        for (var row = 0; row < down; row++)
        {
            int left = 0, tallest = 0;
            for (var i = row * across; i < (row + 1) * across; i++)
            {
                var (modules, c, r) = Symbol(symbology, text(i));
                placed.Add((text(i), left + QuietZone, top + QuietZone, c, r));
                symbols.Add(modules);
                left += c + 2 * QuietZone;
                tallest = Math.Max(tallest, r + 2 * QuietZone);
            }
            columns = Math.Max(columns, left);
            top += tallest;
        }
        return ((row, column) =>
        {
            for (var i = 0; i < placed.Count; i++)
            {
                var (_, l, t, c, r) = placed[i];
                if (row >= t && column >= l && row < t + r && column < l + c)
                    return symbols[i][(row - t) * c + column - l];
            }
            return false;
        }, columns, top, placed);
    }

    /// <summary>The sheet drawn by <see cref="SupersampledRenderer"/>, turned and under keystone, with where each symbol was drawn.</summary>
    private static (byte[] Luminance, int Width, int Height, List<Drawn> Drawn) RenderSheet(Symbology symbology, int across, int down, float pixelsPerModule, float degrees, float keystone)
    {
        var (isDark, columns, rows, placed) = Sheet(symbology, across, down);
        var (luminance, width, height) = SupersampledRenderer.Render(isDark, columns, rows, pixelsPerModule, degrees, keystone);
        var truth = SupersampledGeometry.GridToPixel(columns, rows, pixelsPerModule, degrees, keystone);
        var drawn = new List<Drawn>();
        foreach (var (text, left, top, c, r) in placed)
        {
            var corners = new (float X, float Y)[4];
            truth.Transform(left, top, out corners[0].X, out corners[0].Y);
            truth.Transform(left + c, top, out corners[1].X, out corners[1].Y);
            truth.Transform(left + c, top + r, out corners[2].X, out corners[2].Y);
            truth.Transform(left, top + r, out corners[3].X, out corners[3].Y);
            drawn.Add(new Drawn(text, corners, pixelsPerModule));
        }
        return (luminance, width, height, drawn);
    }

    /// <summary>
    /// Two, three and four symbols and grids of them, turned and under keystone. A grid of 25 holds more finders than the one-symbol scan decodes.
    /// </summary>
    [Test]
    [Arguments(Symbology.MicroQR, 2, 1, 0f, 0f)]
    [Arguments(Symbology.MicroQR, 3, 1, 30f, 0f)]
    [Arguments(Symbology.MicroQR, 2, 2, 0f, 0.04f)]
    [Arguments(Symbology.MicroQR, 3, 3, 75f, 0f)]
    [Arguments(Symbology.MicroQR, 5, 5, 10f, 0.03f)]
    [Arguments(Symbology.RmQR, 2, 1, 0f, 0f)]
    [Arguments(Symbology.RmQR, 1, 3, 30f, 0f)]
    [Arguments(Symbology.RmQR, 2, 2, 0f, 0.04f)]
    [Arguments(Symbology.RmQR, 3, 3, 100f, 0f)]
    [Arguments(Symbology.RmQR, 3, 6, 190f, 0.03f)]
    public async Task GridOfSymbols_EachIsReadOnce_WithItsCorners(Symbology symbology, int across, int down, float degrees, float keystone)
    {
        var (luminance, width, height, drawn) = RenderSheet(symbology, across, down, 4f, degrees, keystone);

        var read = DecodeSymbols(symbology, luminance, width, height);

        await AssertReadsEach(symbology, read, drawn);
        await AssertFirstIsTheOneSymbolRead(symbology, read, luminance, width, height);
    }

    /// <summary>A grid of 60 through the array form, which starts with room for eight and grows: every symbol is read once.</summary>
    [Test]
    [Arguments(Symbology.MicroQR)]
    [Arguments(Symbology.RmQR)]
    public async Task ArrayForm_GridOfSixty_EachIsReadOnce(Symbology symbology)
    {
        var (luminance, width, height, drawn) = RenderSheet(symbology, symbology == Symbology.MicroQR ? 6 : 3, symbology == Symbology.MicroQR ? 10 : 20, 3f, 0f, 0f);

        var read = DecodeSymbolsGrowing(symbology, luminance, width, height);

        await AssertReadsEach(symbology, read, drawn);
    }

    /// <summary>Two images side by side on paper, top-aligned.</summary>
    private static (byte[] Luminance, int Width, int Height) Beside((byte[] Luminance, int Width, int Height) left, (byte[] Luminance, int Width, int Height) right, byte paper = 255)
    {
        var width = left.Width + right.Width;
        var height = Math.Max(left.Height, right.Height);
        var luminance = new byte[width * height];
        Array.Fill(luminance, paper);
        for (var y = 0; y < left.Height; y++)
            Array.Copy(left.Luminance, y * left.Width, luminance, y * width, left.Width);
        for (var y = 0; y < right.Height; y++)
            Array.Copy(right.Luminance, y * right.Width, luminance, y * width + left.Width, right.Width);
        return (luminance, width, height);
    }

    /// <summary>One symbol and its quiet zone as a module grid, light on dark when <paramref name="reversed"/>.</summary>
    private static (Func<int, int, bool> IsDark, int Columns, int Rows) Tile(Symbology symbology, string text, bool reversed = false, int quietZone = 4)
    {
        var (modules, columns, rows) = Symbol(symbology, text);
        return ((row, column) =>
        {
            int r = row - quietZone, c = column - quietZone;
            var dark = r >= 0 && c >= 0 && r < rows && c < columns && modules[r * columns + c];
            return dark != reversed;
        }, columns + 2 * quietZone, rows + 2 * quietZone);
    }

    /// <summary>A dark-on-light symbol beside a light-on-dark one: the global pass reads the first and the negative pass the second.</summary>
    [Test]
    [Arguments(Symbology.MicroQR)]
    [Arguments(Symbology.RmQR)]
    public async Task SymbolsOfBothPolarities_BothAreRead(Symbology symbology)
    {
        var plain = Tile(symbology, Text(symbology, 1));
        var reversed = Tile(symbology, Text(symbology, 2), reversed: true);
        var image = Beside(NearestNeighbourRenderer.Render(plain.IsDark, plain.Columns, plain.Rows, 4f, 0f, 0f), NearestNeighbourRenderer.Render(reversed.IsDark, reversed.Columns, reversed.Rows, 4f, 0f, 0f));
        // The premise: the one-symbol call reads the first and stops
        await Assert.That(DecodeOne(symbology, image.Luminance, image.Width, image.Height)?.Text).IsEqualTo(Text(symbology, 1));

        var read = DecodeSymbols(symbology, image.Luminance, image.Width, image.Height);

        await Assert.That(read.Select(static r => r.Text)).IsEquivalentTo([Text(symbology, 1), Text(symbology, 2)], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>A plain symbol beside one in a shadow deep enough that only the regional binarization reads it: both are read.</summary>
    [Test]
    [Arguments(Symbology.MicroQR)]
    [Arguments(Symbology.RmQR)]
    public async Task SymbolOnlyTheRegionalPassReads_BesideAPlainOne_BothAreRead(Symbology symbology)
    {
        var plain = Tile(symbology, Text(symbology, 1), quietZone: 2);
        var shadowed = Tile(symbology, Text(symbology, 2), quietZone: 2);
        var shadowedImage = UnevenLightingRenderer.Render(shadowed.IsDark, shadowed.Columns, shadowed.Rows, 4, UnevenLight.Shadow, 0f, 0.55f);
        var image = Beside(UnevenLightingRenderer.Render(plain.IsDark, plain.Columns, plain.Rows, 4, UnevenLight.Shadow, 0f, 0f), shadowedImage, UnevenLightingRenderer.LightReflectance);
        // The premises: the shadowed one alone is read by no global pass, and the one-symbol call reads the plain one
        await Assert.That(GlobalPassesRead(symbology, shadowedImage.Luminance, shadowedImage.Width, shadowedImage.Height)).IsFalse();
        await Assert.That(DecodeOne(symbology, shadowedImage.Luminance, shadowedImage.Width, shadowedImage.Height)?.Text).IsEqualTo(Text(symbology, 2));
        await Assert.That(DecodeOne(symbology, image.Luminance, image.Width, image.Height)?.Text).IsEqualTo(Text(symbology, 1));

        var read = DecodeSymbols(symbology, image.Luminance, image.Width, image.Height);

        await Assert.That(read.Select(static r => r.Text)).IsEquivalentTo([Text(symbology, 1), Text(symbology, 2)], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>Whether the global threshold of either polarity, or of the negative alone, reads a symbol of the image.</summary>
    private static bool GlobalPassesRead(Symbology symbology, byte[] luminance, int width, int height, bool negativeOnly = false)
    {
        var inverted = luminance.Select(static p => (byte)(255 - p)).ToArray();
        foreach (var image in negativeOnly ? new[] { inverted } : new[] { luminance, inverted })
        {
            var histogram = new int[Binarizer.HistogramBins];
            Binarizer.FillHistogram(image, histogram);
            var destination = new char[512];
            var status = symbology == Symbology.MicroQR
                ? MicroQRImageDecoder.DecodeLuminanceCore(image, histogram, width, height, destination, out _, out _)
                : RmQRImageDecoder.DecodeLuminanceCore(image, histogram, width, height, destination, out _, out _);
            if (status == DecodeStatus.Success)
                return true;
        }
        return false;
    }

    /// <summary>
    /// A symbol at 4 px a module beside one at 1.3 px whose finder only the full sweep finds: the one-symbol call reads the first and stops, and the sweep that runs after a read here finds the second.
    /// </summary>
    [Test]
    [Arguments(Symbology.MicroQR)]
    [Arguments(Symbology.RmQR)]
    public async Task SymbolOnlyTheSweepFinds_BesideARead_IsRead(Symbology symbology)
    {
        var large = Tile(symbology, Text(symbology, 1));
        var small = Tile(symbology, Text(symbology, 2));
        var left = NearestNeighbourRenderer.Render(large.IsDark, large.Columns, large.Rows, 4f, 0f, 0f);
        var right = NearestNeighbourRenderer.Render(small.IsDark, small.Columns, small.Rows, 1.30f, 0.75f, 0.75f);
        var image = Beside(left, right);
        // The premises: the strided scan finds no candidate on the small symbol, the small one alone reads, and the one-symbol call reads the large one.
        // The image is black and white, so no regional pass runs. A regional pass reads nothing by its strided scan, and its own sweep would find the small symbol even for a call that skips the sweep after a read.
        await Assert.That(StridedCandidatesRightOf(image.Luminance, image.Width, image.Height, left.Width)).IsEqualTo(0);
        var histogram = new int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(image.Luminance, histogram);
        await Assert.That(RegionalRetry.HoldsOnlyExtremes(histogram)).IsTrue();
        await Assert.That(DecodeOne(symbology, right.Luminance, right.Width, right.Height)?.Text).IsEqualTo(Text(symbology, 2));
        await Assert.That(DecodeOne(symbology, image.Luminance, image.Width, image.Height)?.Text).IsEqualTo(Text(symbology, 1));

        var read = DecodeSymbols(symbology, image.Luminance, image.Width, image.Height);

        await Assert.That(read.Select(static r => r.Text)).IsEquivalentTo([Text(symbology, 1), Text(symbology, 2)], TUnit.Assertions.Enums.CollectionOrdering.Matching);

        static int StridedCandidatesRightOf(byte[] luminance, int width, int height, int x)
        {
            var histogram = new int[Binarizer.HistogramBins];
            Binarizer.FillHistogram(luminance, histogram);
            var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);
            var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
            var found = FinderPatternFinder.FindCandidates(luminance, width, height, threshold, candidates, grey);
            return candidates.Take(found).Count(c => c.X >= x);
        }
    }

    /// <summary>
    /// A symbol anti-aliased at about 1.16 px a module, which only the midpoint pass reads: the global threshold finds no finder on it, and neither global polarity nor either regional binarization reads it.
    /// Beside a plain symbol the midpoint pass does not run, as in the one-symbol call, so it is read alone.
    /// </summary>
    [Test]
    [Arguments(Symbology.MicroQR, 1.1696374f, 0.26704776f, 0.45265362f)]
    [Arguments(Symbology.RmQR, 1.1457224f, 0.99830526f, 0.71906835f)]
    public async Task SymbolOnlyTheMidpointPassReads_IsRead(Symbology symbology, float pixelsPerModule, float offsetX, float offsetY)
    {
        var (text, luminance, width, height) = MidpointSymbol(symbology, pixelsPerModule, offsetX, offsetY);
        // The premises: no finder at the global threshold, no read by the passes before the midpoint, and the one-symbol call reads it
        var histogram = new int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(luminance, histogram);
        var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        await Assert.That(FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey)).IsEqualTo(0);
        await Assert.That(GlobalPassesRead(symbology, luminance, width, height)).IsFalse();
        await Assert.That(RegionalPassesRead(symbology, luminance, width, height)).IsFalse();
        await Assert.That(DecodeOne(symbology, luminance, width, height)?.Text).IsEqualTo(text);

        var read = DecodeSymbols(symbology, luminance, width, height);

        await Assert.That(read.Select(static r => r.Text)).IsEquivalentTo([text], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>The symbol of <see cref="SymbolOnlyTheMidpointPassReads_IsRead"/>, anti-aliased with its two-module quiet zone.</summary>
    private static (string Text, byte[] Luminance, int Width, int Height) MidpointSymbol(Symbology symbology, float pixelsPerModule, float offsetX, float offsetY)
    {
        var text = symbology == Symbology.MicroQR ? "FQR MICRO 2" : "FQR RMQR 1";
        Func<int, int, bool> symbol;
        int columns, rows;
        if (symbology == Symbology.MicroQR)
        {
            var micro = MicroQRCodeGenerator.Create(text, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
            symbol = (r, c) => micro[r, c];
            columns = rows = micro.Size;
        }
        else
        {
            var rmqr = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = 0, Version = RmQRVersion.R11x43 });
            symbol = (r, c) => rmqr[r, c];
            columns = rmqr.Width;
            rows = rmqr.Height;
        }
        var (luminance, width, height) = AntiAliasedRenderer.Render((r, c) => r >= QuietZone && c >= QuietZone && r < rows + QuietZone && c < columns + QuietZone && symbol(r - QuietZone, c - QuietZone), columns + 2 * QuietZone, rows + 2 * QuietZone, pixelsPerModule, offsetX, offsetY);
        return (text, luminance, width, height);
    }

    /// <summary>
    /// A plain symbol beside a reversed copy of an anti-aliased symbol that only the midpoint pass reads: the negative midpoint pass reads the second, and it runs after the positive pass has read the first.
    /// </summary>
    [Test]
    [Arguments(Symbology.MicroQR, 1.13f, 0.59004194f, 0.19798319f, (byte)255)]
    [Arguments(Symbology.RmQR, 1.09f, 0.80488366f, 0.33534238f, (byte)255)]
    [Arguments(Symbology.RmQR, 1.1f, 0.7842325f, 0.22932193f, (byte)0)]
    public async Task ReversedSymbolOnlyTheNegativeMidpointPassReads_BesideAPlainOne_BothAreRead(Symbology symbology, float pixelsPerModule, float offsetX, float offsetY, byte paper)
    {
        var (text, luminance, width, height) = MidpointSymbol(symbology, pixelsPerModule, offsetX, offsetY);
        var reversed = (Luminance: luminance.Select(static p => (byte)(255 - p)).ToArray(), Width: width, Height: height);
        var plainText = symbology == Symbology.MicroQR ? "10123456789" : "FQR RMQR 7AAAAAAAAAA";
        Func<int, int, bool> plainSymbol;
        int columns, rows;
        if (symbology == Symbology.MicroQR)
        {
            var micro = MicroQRCodeGenerator.Create(plainText, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
            plainSymbol = (r, c) => micro[r, c];
            columns = rows = micro.Size;
        }
        else
        {
            var rmqr = RmQRCodeGenerator.Create(plainText, RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = 0, Version = RmQRVersion.R11x59 });
            plainSymbol = (r, c) => rmqr[r, c];
            columns = rmqr.Width;
            rows = rmqr.Height;
        }
        const int PlainZone = 4;
        var plain = NearestNeighbourRenderer.Render((r, c) => r >= PlainZone && c >= PlainZone && r < rows + PlainZone && c < columns + PlainZone && plainSymbol(r - PlainZone, c - PlainZone), columns + 2 * PlainZone, rows + 2 * PlainZone, 4f, 0f, 0f);
        var image = Beside(plain, reversed, paper);
        // The premises: the inverted image has no finder at its global threshold, so the negative midpoint pass runs, the reversed symbol alone is read by no global or regional pass,
        // the negative regional binarization of the whole image reads nothing, and the one-symbol call reads the plain one
        await Assert.That(FullSweepCandidates(image.Luminance.Select(static p => (byte)(255 - p)).ToArray(), image.Width, image.Height)).IsEqualTo(0);
        await Assert.That(GlobalPassesRead(symbology, reversed.Luminance, width, height)).IsFalse();
        await Assert.That(RegionalPassesRead(symbology, reversed.Luminance, width, height)).IsFalse();
        await Assert.That(RegionalPassesRead(symbology, image.Luminance, image.Width, image.Height, negativeOnly: true)).IsFalse();
        await Assert.That(DecodeOne(symbology, image.Luminance, image.Width, image.Height)?.Text).IsEqualTo(plainText);

        var read = DecodeSymbols(symbology, image.Luminance, image.Width, image.Height);

        await Assert.That(read.Select(static r => r.Text)).IsEquivalentTo([plainText, text], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>
    /// A buffer longer than the image is read as its first width × height bytes. The symbol is rMQR's midpoint one, whose read depends on the thresholds.
    /// The bytes past the image are black, four times as many as the image's, so read with it they would move its global threshold.
    /// </summary>
    [Test]
    public async Task BufferLongerThanTheImage_IsReadAsTheImage()
    {
        var (text, luminance, width, height) = MidpointSymbol(Symbology.RmQR, 1.1457224f, 0.99830526f, 0.71906835f);
        var longer = new byte[5 * luminance.Length];
        luminance.CopyTo(longer, 0);
        // The premise: the whole buffer has another global threshold than the image
        await Assert.That(GlobalThreshold(longer)).IsNotEqualTo(GlobalThreshold(luminance));

        var read = DecodeSymbols(Symbology.RmQR, longer, width, height);

        await Assert.That(read.Select(static r => r.Text)).IsEquivalentTo([text], TUnit.Assertions.Enums.CollectionOrdering.Matching);

        static byte GlobalThreshold(byte[] luminance)
        {
            var histogram = new int[Binarizer.HistogramBins];
            Binarizer.FillHistogram(luminance, histogram);
            return Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out _);
        }
    }

    /// <summary>How many finder candidates the full sweep finds at the image's global threshold.</summary>
    private static int FullSweepCandidates(byte[] luminance, int width, int height)
    {
        var histogram = new int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(luminance, histogram);
        var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        return FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey);
    }

    /// <summary>A plain symbol beside a reversed one in a shadow deep enough that only the negative regional binarization reads it: both are read.</summary>
    [Test]
    [Arguments(Symbology.MicroQR)]
    [Arguments(Symbology.RmQR)]
    public async Task ReversedSymbolOnlyTheNegativeRegionalPassReads_BesideAPlainOne_BothAreRead(Symbology symbology)
    {
        var plain = Tile(symbology, Text(symbology, 1), quietZone: 2);
        var shadowed = Tile(symbology, Text(symbology, 2), quietZone: 2);
        var shadowedImage = UnevenLightingRenderer.Render(shadowed.IsDark, shadowed.Columns, shadowed.Rows, 4, UnevenLight.Shadow, 0f, 0.55f);
        var reversed = (Luminance: shadowedImage.Luminance.Select(static p => (byte)(255 - p)).ToArray(), shadowedImage.Width, shadowedImage.Height);
        var image = Beside(UnevenLightingRenderer.Render(plain.IsDark, plain.Columns, plain.Rows, 4, UnevenLight.Shadow, 0f, 0f), reversed, UnevenLightingRenderer.LightReflectance);
        // The premises: no global pass reads the reversed one alone, and no negative global pass reads it beside the plain one. The one-symbol call reads it alone, so a regional pass does.
        // The inverted image has finders at its global threshold, so no negative midpoint pass runs, and the one-symbol call reads the plain one.
        await Assert.That(GlobalPassesRead(symbology, reversed.Luminance, reversed.Width, reversed.Height)).IsFalse();
        await Assert.That(GlobalPassesRead(symbology, image.Luminance, image.Width, image.Height, negativeOnly: true)).IsFalse();
        await Assert.That(DecodeOne(symbology, reversed.Luminance, reversed.Width, reversed.Height)?.Text).IsEqualTo(Text(symbology, 2));
        await Assert.That(FullSweepCandidates(image.Luminance.Select(static p => (byte)(255 - p)).ToArray(), image.Width, image.Height)).IsGreaterThan(0);
        await Assert.That(DecodeOne(symbology, image.Luminance, image.Width, image.Height)?.Text).IsEqualTo(Text(symbology, 1));

        var read = DecodeSymbols(symbology, image.Luminance, image.Width, image.Height);

        await Assert.That(read.Select(static r => r.Text)).IsEquivalentTo([Text(symbology, 1), Text(symbology, 2)], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    public enum Foreign { StandardQR, MicroQR, RmQR }

    /// <summary>
    /// A grid of 16 symbols of another symbology, turned: no result. Every finder of the grid is a candidate the call decodes, where the one-symbol call decodes eight.
    /// </summary>
    [Test]
    [Arguments(Symbology.MicroQR, Foreign.StandardQR)]
    [Arguments(Symbology.MicroQR, Foreign.RmQR)]
    [Arguments(Symbology.RmQR, Foreign.StandardQR)]
    [Arguments(Symbology.RmQR, Foreign.MicroQR)]
    public async Task GridOfAnotherSymbology_GivesNoResult(Symbology symbology, Foreign foreign)
    {
        var cells = new List<(Func<int, int, bool> IsDark, int Columns, int Rows)>();
        for (var i = 0; i < 16; i++)
        {
            var text = $"FOREIGN {i}";
            switch (foreign)
            {
                case Foreign.StandardQR:
                    var qr = QRCodeGenerator.Create(text + new string('X', i * 3), QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
                    cells.Add(((r, c) => qr[r, c], qr.Size, qr.Size));
                    break;
                case Foreign.MicroQR:
                    var micro = MicroQRCodeGenerator.Create(text, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
                    cells.Add(((r, c) => micro[r, c], micro.Size, micro.Size));
                    break;
                default:
                    var rmqr = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = 0 });
                    cells.Add(((r, c) => rmqr[r, c], rmqr.Width, rmqr.Height));
                    break;
            }
        }
        int cellColumns = cells.Max(static c => c.Columns) + 2 * QuietZone, cellRows = cells.Max(static c => c.Rows) + 2 * QuietZone;
        var (luminance, width, height) = SupersampledRenderer.Render((row, column) =>
        {
            var (isDark, columns, rows) = cells[row / cellRows * 4 + column / cellColumns];
            int r = row % cellRows - QuietZone, c = column % cellColumns - QuietZone;
            return r >= 0 && c >= 0 && r < rows && c < columns && isDark(r, c);
        }, 4 * cellColumns, 4 * cellRows, 3f, 10f);
        // The premise: the strided scan finds more candidates than the one-symbol call decodes
        await Assert.That(StridedCandidates(luminance, width, height)).IsGreaterThan(CandidateScan.MaxCandidatesToTry);

        await Assert.That(DecodeSymbols(symbology, luminance, width, height)).IsEmpty();

        static int StridedCandidates(byte[] luminance, int width, int height)
        {
            var histogram = new int[Binarizer.HistogramBins];
            Binarizer.FillHistogram(luminance, histogram);
            var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var grey);
            var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
            return FinderPatternFinder.FindCandidates(luminance, width, height, threshold, candidates, grey);
        }
    }

    /// <summary>Whether the regional binarization of either polarity, or of the negative alone, reads a symbol of the image.</summary>
    private static bool RegionalPassesRead(Symbology symbology, byte[] luminance, int width, int height, bool negativeOnly = false)
    {
        var positive = new int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(luminance, positive);
        var positiveThreshold = Binarizer.ComputeOtsuThresholdFromHistogram(positive, out _);
        Binarizer.InvertHistogram(positive);
        var negativeThreshold = Binarizer.ComputeOtsuThresholdFromHistogram(positive, out _);
        var scratch = new int[LocalBinarizer.ScratchLength(width, height)];
        var binarized = new byte[width * height];
        foreach (var negative in negativeOnly ? new[] { true } : new[] { false, true })
        {
            if (!LocalBinarizer.TryBinarize(luminance, width, height, negative, negative ? negativeThreshold : positiveThreshold, binarized, scratch, out var dark))
                continue;
            var histogram = new int[Binarizer.HistogramBins];
            histogram[LocalBinarizer.Dark] = dark;
            histogram[LocalBinarizer.Light] = binarized.Length - dark;
            var destination = new char[512];
            var status = symbology == Symbology.MicroQR
                ? MicroQRImageDecoder.DecodeLuminanceCore(binarized, histogram, width, height, destination, out _, out _)
                : RmQRImageDecoder.DecodeLuminanceCore(binarized, histogram, width, height, destination, out _, out _);
            if (status == DecodeStatus.Success)
                return true;
        }
        return false;
    }

    /// <summary>An image of one symbol, named for the test's display.</summary>
    public sealed record OneSymbolImage(Symbology Symbology, string Name, byte[] Luminance, int Width, int Height)
    {
        public override string ToString() => $"{Symbology} {Name}";
    }

    /// <summary>The images of one symbol each: renders of every path the passes read by, and the committed real images, upright and turned a quarter.</summary>
    public static IEnumerable<Func<OneSymbolImage>> OneSymbolImages()
    {
        foreach (var symbology in new[] { Symbology.MicroQR, Symbology.RmQR })
        {
            var text = Text(symbology, 5);
            var tile = Tile(symbology, text, quietZone: 2);
            yield return () => Image(symbology, "crisp 4 px", NearestNeighbourRenderer.Render(tile.IsDark, tile.Columns, tile.Rows, 4f, 0f, 0f));
            yield return () => Image(symbology, "crisp 1.3 px", NearestNeighbourRenderer.Render(tile.IsDark, tile.Columns, tile.Rows, 1.3f, 0.75f, 0.75f));
            yield return () => Image(symbology, "turned 37°", SupersampledRenderer.Render(tile.IsDark, tile.Columns, tile.Rows, 4f, 37f));
            yield return () => Image(symbology, "keystone 8 %", SupersampledRenderer.Render(tile.IsDark, tile.Columns, tile.Rows, 5f, 200f, 0.08f));
            yield return () =>
            {
                var (l, w, h) = NearestNeighbourRenderer.Render(tile.IsDark, tile.Columns, tile.Rows, 3f, 0f, 0f);
                return Image(symbology, "mirrored", NearestNeighbourRenderer.Turn(l, w, h, 1, mirror: true));
            };
            yield return () => Image(symbology, "reversed", NearestNeighbourRenderer.Render((r, c) => !tile.IsDark(r, c), tile.Columns, tile.Rows, 3f, 0f, 0f));
            yield return () => Image(symbology, "shadowed", UnevenLightingRenderer.Render(tile.IsDark, tile.Columns, tile.Rows, 4, UnevenLight.Shadow, 0f, 0.55f));
            yield return () =>
            {
                var (modules, columns, rows) = Symbol(symbology, text);
                var photo = CameraRenderer.Render((r, c) => modules[r * columns + c], columns, rows, 2, new CameraRenderer.Shot(5f, 25f, 30f, 10f, BlurSigma: 0.8f, NoiseSigma: 4f, SceneScale: 2f, Seed: 7));
                return new OneSymbolImage(symbology, "photograph in a scene", photo.Luminance, photo.Width, photo.Height);
            };
        }

        var root = Path.Combine(FixtureLoader.FixtureRoot, "RealImages", "zxing-cpp-samples");
        foreach (var (set, symbology) in new[] { ("microqrcode-1", Symbology.MicroQR), ("rmqrcode-1", Symbology.RmQR) })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, set)).Where(static p => Path.GetExtension(p) is ".png" or ".jpg" or ".webp").Order(StringComparer.Ordinal))
            {
                for (var turns = 0; turns < 2; turns++)
                {
                    var quarter = turns;
                    yield return () =>
                    {
                        using var bitmap = global::SkiaSharp.SKBitmap.Decode(path);
                        var upright = DecodeImageSymbolsTest.Luminance(bitmap);
                        return Image(symbology, $"{set}/{Path.GetFileName(path)} turned {quarter}", NearestNeighbourRenderer.Turn(upright, bitmap.Width, bitmap.Height, quarter, mirror: false));
                    };
                }
            }
        }

        static OneSymbolImage Image(Symbology symbology, string name, (byte[] Luminance, int Width, int Height) image) => new(symbology, name, image.Luminance, image.Width, image.Height);
    }

    /// <summary>
    /// An image of one symbol is one result, the one-symbol call's read to the last field, whichever pass reads it: a second result there would be a false read.
    /// An image that call does not read gives none.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(OneSymbolImages))]
    public async Task OneSymbolImage_IsOneResult_TheOneSymbolRead(OneSymbolImage image)
    {
        var one = DecodeOne(image.Symbology, image.Luminance, image.Width, image.Height);

        var read = DecodeSymbols(image.Symbology, image.Luminance, image.Width, image.Height);

        if (one is null)
        {
            await Assert.That(read.Length).IsEqualTo(0);
            return;
        }
        await Assert.That(read.Length).IsEqualTo(1).Because($"read {string.Join(", ", read.Select(static r => $"\"{r.Text}\""))}");
        await Assert.That(read[0]).IsEqualTo(one);
    }

    /// <summary>A grid of symbols drawn upright at 4 px a module, for a test that only counts what it reads.</summary>
    internal static (byte[] Luminance, int Width, int Height) RenderGrid(Symbology symbology, int across, int down)
    {
        var (luminance, width, height, _) = RenderSheet(symbology, across, down, 4f, 0f, 0f);
        return (luminance, width, height);
    }

    /// <summary>A symbol whose content gives a verdict, a Kanji cell outside the table, beside one that reads, in either order: the one that reads is the only result.</summary>
    [Test]
    [Arguments(Symbology.MicroQR, true)]
    [Arguments(Symbology.MicroQR, false)]
    [Arguments(Symbology.RmQR, true)]
    [Arguments(Symbology.RmQR, false)]
    public async Task SymbolWithAContentVerdict_IsLeftOut_AndTheOtherIsRead(Symbology symbology, bool verdictFirst)
    {
        var (unmapped, columns, rows) = symbology == Symbology.MicroQR ? ContentVerdictImageDecodeTest.BuildMicroQR(ContentVerdictImageDecodeTest.Unmapped) : ContentVerdictImageDecodeTest.BuildRmQR(ContentVerdictImageDecodeTest.Unmapped);
        var (mapped, _, _) = symbology == Symbology.MicroQR ? ContentVerdictImageDecodeTest.BuildMicroQR(ContentVerdictImageDecodeTest.Mapped) : ContentVerdictImageDecodeTest.BuildRmQR(ContentVerdictImageDecodeTest.Mapped);
        const int Zone = 4;
        (byte[] Luminance, int Width, int Height) Render(byte[] modules) => NearestNeighbourRenderer.Render((row, column) =>
        {
            int r = row - Zone, c = column - Zone;
            return r >= 0 && c >= 0 && r < rows && c < columns && modules[r * columns + c] != 0;
        }, columns + 2 * Zone, rows + 2 * Zone, 4f, 0f, 0f);
        var image = verdictFirst ? Beside(Render(unmapped), Render(mapped)) : Beside(Render(mapped), Render(unmapped));
        // The premise: alone, the one with the verdict is not read
        var alone = Render(unmapped);
        await Assert.That(DecodeOne(symbology, alone.Luminance, alone.Width, alone.Height)).IsNull();

        var read = DecodeSymbols(symbology, image.Luminance, image.Width, image.Height);

        await Assert.That(read.Select(static r => r.Text)).IsEquivalentTo(["亜"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>Room for the four symbols of the image, or for fewer, is filled with the first symbols the read with ample room gives, in its order.</summary>
    [Test]
    [Arguments(Symbology.MicroQR, 1)]
    [Arguments(Symbology.MicroQR, 3)]
    [Arguments(Symbology.MicroQR, 4)]
    [Arguments(Symbology.RmQR, 1)]
    [Arguments(Symbology.RmQR, 3)]
    [Arguments(Symbology.RmQR, 4)]
    public async Task Room_IsFilledWithTheFirstFound(Symbology symbology, int room)
    {
        var (luminance, width, height) = RenderGrid(symbology, 2, 2);
        var all = DecodeSymbols(symbology, luminance, width, height);
        await Assert.That(all.Length).IsEqualTo(4);

        var read = DecodeSymbols(symbology, luminance, width, height, room);

        await Assert.That(read).IsEquivalentTo(all.Take(room), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>The longest text of digits the symbology holds, beside a short one: the text buffer every read writes into holds it.</summary>
    [Test]
    [Arguments(Symbology.MicroQR)]
    [Arguments(Symbology.RmQR)]
    public async Task LongestText_IsRead(Symbology symbology)
    {
        string longest;
        (bool[] Modules, int Columns, int Rows) symbol;
        if (symbology == Symbology.MicroQR)
        {
            longest = Longest(text => MicroQRCodeGenerator.TryGetRequiredBufferSize(text, MicroQREccLevel.L, out _, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 }));
            var micro = MicroQRCodeGenerator.Create(longest, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
            symbol = (Enumerable.Range(0, micro.Size * micro.Size).Select(i => micro[i / micro.Size, i % micro.Size]).ToArray(), micro.Size, micro.Size);
        }
        else
        {
            longest = Longest(text => RmQRCodeGenerator.TryGetRequiredBufferSize(text, RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { QuietZoneSize = 0, Version = RmQRVersion.R17x139 }));
            var rmqr = RmQRCodeGenerator.Create(longest, RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = 0, Version = RmQRVersion.R17x139 });
            symbol = (Enumerable.Range(0, rmqr.Width * rmqr.Height).Select(i => rmqr[i / rmqr.Width, i % rmqr.Width]).ToArray(), rmqr.Width, rmqr.Height);
        }

        static string Longest(Func<string, bool> fits)
        {
            var length = 1;
            while (fits(new string('7', length + 1)))
                length++;
            return new string('7', length);
        }
        const int Zone = 4;
        await Assert.That(longest.Length).IsGreaterThan(symbology == Symbology.MicroQR ? 30 : 300);
        var big = NearestNeighbourRenderer.Render((row, column) =>
        {
            int r = row - Zone, c = column - Zone;
            return r >= 0 && c >= 0 && r < symbol.Rows && c < symbol.Columns && symbol.Modules[r * symbol.Columns + c];
        }, symbol.Columns + 2 * Zone, symbol.Rows + 2 * Zone, 3f, 0f, 0f);
        var shortTile = Tile(symbology, Text(symbology, 1));
        var image = Beside(NearestNeighbourRenderer.Render(shortTile.IsDark, shortTile.Columns, shortTile.Rows, 3f, 0f, 0f), big);

        var read = DecodeSymbols(symbology, image.Luminance, image.Width, image.Height);

        // A set by contract: which symbol's finder ranks first is the scan's business
        await Assert.That(read.Select(static r => r.Text)).IsEquivalentTo([Text(symbology, 1), longest], TUnit.Assertions.Enums.CollectionOrdering.Any);
    }

    /// <summary>An image with no symbol of the symbology.</summary>
    public enum NoSymbolImage { Blank, Noise, StandardQRSymbol, MicroQRSymbol, RmQRSymbol }

    private static (byte[] Luminance, int Width, int Height) Render(NoSymbolImage image)
    {
        switch (image)
        {
            case NoSymbolImage.Blank:
                {
                    var luminance = new byte[160 * 160];
                    Array.Fill(luminance, (byte)255);
                    return (luminance, 160, 160);
                }
            case NoSymbolImage.Noise:
                {
                    var luminance = new byte[240 * 240];
                    new Random(20261027).NextBytes(luminance);
                    return (luminance, 240, 240);
                }
            case NoSymbolImage.StandardQRSymbol:
                {
                    var qr = QRCodeGenerator.Create("FQR 2.0 SEVERAL", QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
                    return SupersampledRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, 4f, 10f);
                }
            case NoSymbolImage.MicroQRSymbol:
                {
                    var qr = MicroQRCodeGenerator.Create("MICRO QR M4 TEST", MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
                    return SupersampledRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, 5f, 10f);
                }
            default:
                {
                    var qr = RmQRCodeGenerator.Create("RMQR IMAGE 123", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R13x99, QuietZoneSize = 0 });
                    return SupersampledRenderer.Render((row, column) => qr[row, column], qr.Width, qr.Height, 4f, 10f);
                }
        }
    }

    /// <summary>Images with no symbol of the symbology: paper, noise, and the symbols of the other two. Each gives no result.</summary>
    [Test]
    [Arguments(Symbology.MicroQR, NoSymbolImage.Blank)]
    [Arguments(Symbology.MicroQR, NoSymbolImage.Noise)]
    [Arguments(Symbology.MicroQR, NoSymbolImage.StandardQRSymbol)]
    [Arguments(Symbology.MicroQR, NoSymbolImage.RmQRSymbol)]
    [Arguments(Symbology.RmQR, NoSymbolImage.Blank)]
    [Arguments(Symbology.RmQR, NoSymbolImage.Noise)]
    [Arguments(Symbology.RmQR, NoSymbolImage.StandardQRSymbol)]
    [Arguments(Symbology.RmQR, NoSymbolImage.MicroQRSymbol)]
    public async Task NoSymbol_GivesNoResult(Symbology symbology, NoSymbolImage image)
    {
        var (luminance, width, height) = Render(image);

        await Assert.That(DecodeSymbols(symbology, luminance, width, height)).IsEmpty();
        await Assert.That(DecodeSymbolsGrowing(symbology, luminance, width, height)).IsEmpty();
    }

    /// <summary>A token canceled before the call stops it at its first check, an image with no symbol included, through both forms.</summary>
    [Test]
    [Arguments(Symbology.MicroQR, true)]
    [Arguments(Symbology.MicroQR, false)]
    [Arguments(Symbology.RmQR, true)]
    [Arguments(Symbology.RmQR, false)]
    public async Task CanceledToken_Throws(Symbology symbology, bool withSymbols)
    {
        var (luminance, width, height) = withSymbols ? RenderGrid(symbology, 2, 1) : Render(NoSymbolImage.Blank);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.That(() => DecodeSymbols(symbology, luminance, width, height, cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(() => DecodeSymbolsGrowing(symbology, luminance, width, height, cancellation.Token)).Throws<OperationCanceledException>();
    }

    /// <summary>A token canceled once the first symbol is read stops the search before the rest: the check runs between symbols.</summary>
    [Test]
    [Arguments(Symbology.MicroQR)]
    [Arguments(Symbology.RmQR)]
    public async Task CanceledOnceTheFirstSymbolIsRead_StopsBeforeTheRest(Symbology symbology)
    {
        var (luminance, width, height) = RenderGrid(symbology, 4, 6);
        // The cancel comes from another thread, which a busy machine can leave waiting until the last symbol is read.
        // Such a try proves nothing, so tries go on until one cancels in time, and a search that never stops between symbols runs out of time
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var tries = 0;
        while (true)
        {
            tries++;
            var (caught, written) = CancelOnceTheFirstSymbolIsRead(symbology, luminance, width, height);
            await Assert.That(caught is null or OperationCanceledException).IsTrue();
            if (caught is OperationCanceledException && written > 0 && written < 24)
                break;
            if (clock.Elapsed > TimeSpan.FromSeconds(30))
                Assert.Fail($"None of {tries} tries stopped between the symbols of the grid.");
        }
    }

    private static (Exception? Caught, int Written) CancelOnceTheFirstSymbolIsRead(Symbology symbology, byte[] luminance, int width, int height)
    {
        var micro = new MicroQRCodeDecodeResult[64];
        var rmqr = new RmQRCodeDecodeResult[64];
        using var cancellation = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var done = false;
        // The span form writes each symbol into the caller's room as it reads it, so a first entry is the moment to cancel
        var watcher = new Thread(() =>
        {
            started.Set();
            while (micro[0].Text is null && rmqr[0].Text is null && !Volatile.Read(ref done))
                Thread.SpinWait(20);
            cancellation.Cancel();
        });
        watcher.Start();
        started.Wait();

        Exception? caught = null;
        try
        {
            if (symbology == Symbology.MicroQR)
                MicroQRImageDecoder.DecodeSymbols(luminance, width, height, micro, cancellation.Token);
            else
                RmQRImageDecoder.DecodeSymbols(luminance, width, height, rmqr, cancellation.Token);
        }
        catch (Exception ex)
        {
            caught = ex;
        }
        Volatile.Write(ref done, true);
        watcher.Join();
        return (caught, symbology == Symbology.MicroQR ? micro.Count(static s => s.Text is not null) : rmqr.Count(static s => s.Text is not null));
    }
}
