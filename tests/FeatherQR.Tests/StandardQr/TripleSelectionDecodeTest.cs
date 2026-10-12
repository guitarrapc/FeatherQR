using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// Two scenes where every finder is a candidate and the triple chosen is another shape: many symbols side by side, where the best-looking triples span symbols, and a tall image whose stride pass sees each finder on a row or none and selects what it saw.
/// </summary>
public class TripleSelectionDecodeTest
{
    /// <summary>
    /// A grid of symbols of one size under keystone, as a photograph of many symbols is: the three finders meeting where three symbols touch form a right isosceles triangle smaller than any symbol's own, which keystone skews past the selection's tie band, so the selection spans symbols from the full sweep's list and from the strided scan's.
    /// No shape tells such a triple from a symbol's own. Its timing patterns do, and the image reads through the triples gated by them.
    /// </summary>
    [Test]
    [Arguments(3, 3, 0.12f)]
    [Arguments(4, 2, 0.15f)]
    public async Task GridOfSymbols_ReadsThoughTheSelectionSpansSymbols(int across, int down, float keystone)
    {
        var (luminance, width, height, size) = RenderGrid(across, down, keystone, i => $"FQR LOTS {i}");
        var truth = SupersampledGeometry.GridToPixel(across * size, down * size, 4f, 0f, keystone);

        // The premise: every drawn finder is a candidate of the full sweep, under the list's capacity
        var drawn = new List<(int Symbol, float X, float Y)>();
        for (var s = 0; s < across * down; s++)
        {
            var left = s % across * size;
            var top = s / across * size;
            foreach (var (u, v) in new[] { (5.5f, 5.5f), (size - 5.5f, 5.5f), (5.5f, size - 5.5f) })
            {
                truth.Transform(left + u, top + v, out var x, out var y);
                drawn.Add((s, x, y));
            }
        }
        await Assert.That(drawn.Count).IsLessThanOrEqualTo(FinderPatternFinder.MaxFinderCandidates);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var count = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey);
        foreach (var (symbol, x, y) in drawn)
            await Assert.That(Nearest(candidates.AsSpan(0, count), x, y)).IsLessThan(6f).Because($"the finder at ({x}, {y}) of symbol {symbol} is a candidate");

        // The premise: both selections span symbols
        var selected = new FinderPattern[3];
        await Assert.That(FinderPatternFinder.TrySelectBestThree(candidates.AsSpan(0, count).ToArray(), selected)).IsTrue();
        await Assert.That(OneSymbol(drawn, selected)).IsFalse().Because("the full sweep's selection is one symbol's");
        await Assert.That(FinderPatternFinder.TryFind(luminance, width, height, threshold, selected, grey)).IsTrue();
        await Assert.That(OneSymbol(drawn, selected)).IsFalse().Because("the strided scan's selection is one symbol's");

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).Matches("^FQR LOTS [0-9]+$");
    }

    /// <summary>
    /// Four symbols of which one is whole and the others show one or two finders, as glare or a fold leaves them: six to eight finders, two symbols' worth, with the three that meet where the symbols touch among them.
    /// The selection and its alternatives take triples that span symbols, and the image reads through the triples gated by their timing patterns, as a fuller grid does.
    /// </summary>
    [Test]
    [Arguments(6, 0.12f)]
    [Arguments(7, 0.12f)]
    [Arguments(8, 0.12f)]
    [Arguments(8, 0.08f)]
    public async Task PartialGridOfSymbols_ReadsThoughTheSelectionSpansSymbols(int finders, float keystone)
    {
        // Symbol 0 whole, the three finders at the crossing of the four symbols, then the top-left ones of symbols 1 and 2
        (int Symbol, int Finder)[] kept = [(0, 0), (0, 1), (0, 2), (1, 2), (2, 1), (3, 0), (1, 0), (2, 0)];
        var shown = kept.AsSpan(0, finders).ToArray();
        var (luminance, width, height, size) = RenderGrid(2, 2, keystone, i => $"FQR PART {i}", (symbol, finder) => shown.Contains((symbol, finder)));
        var truth = SupersampledGeometry.GridToPixel(2 * size, 2 * size, 4f, 0f, keystone);

        // The premise: the strided scan's list holds the shown finders and no more, each confirmed, and its selection spans symbols
        var drawn = new List<(int Symbol, float X, float Y)>();
        foreach (var (symbol, finder) in shown)
        {
            var (u, v) = finder switch { 0 => (5.5f, 5.5f), 1 => (size - 5.5f, 5.5f), _ => (5.5f, size - 5.5f) };
            truth.Transform(symbol % 2 * size + u, symbol / 2 * size + v, out var x, out var y);
            drawn.Add((symbol, x, y));
        }
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var selected = new FinderPattern[3];
        var list = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        await Assert.That(FinderPatternFinder.TryFind(luminance, width, height, threshold, selected, grey, list, out var count, out _)).IsTrue();
        await Assert.That(list.AsSpan(0, count).ToArray().Count(static c => c.Count >= 2)).IsEqualTo(finders);
        foreach (var (symbol, x, y) in drawn)
            await Assert.That(Nearest(list.AsSpan(0, count), x, y)).IsLessThan(6f).Because($"the finder at ({x}, {y}) of symbol {symbol} is a candidate");
        await Assert.That(OneSymbol(drawn, selected)).IsFalse().Because("the selection is one symbol's");

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo("FQR PART 0");
    }

    /// <summary>
    /// A symbol at 4 px/module in an image 4,000 px tall, whose stride of 16 rows crosses the centre band of two finders once and the third's not at all, beside a lone finder pattern the stride does cross: the strided scan selects the two and the lone one, unscored since they are three, and returns without the rows between.
    /// The triple fails, and the symbol reads once the skipped rows are scanned: the stride cannot have confirmed a finder whose band is shorter than two strides. The image holds only black and white, so the global passes are all that run.
    /// </summary>
    [Test]
    public async Task UndersampledStrideList_ReadsAfterTheSkippedRowsAreScanned()
    {
        var (luminance, width, height, drawn) = RenderTall(destroyData: false);

        // The premise: two-level, the strided scan selects a triple that is not the symbol's and leaves rows unscanned, and the full sweep holds the symbol's three finders
        await Assert.That(luminance.All(static l => l is 0 or 255)).IsTrue();
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var selected = new FinderPattern[3];
        var list = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        await Assert.That(FinderPatternFinder.TryFind(luminance, width, height, threshold, selected, grey, list, out var kept, out var everyRowScanned)).IsTrue();
        await Assert.That(everyRowScanned).IsFalse();
        await Assert.That(selected.Count(p => drawn.Any(d => Math.Abs(d.X - p.X) < 6f && Math.Abs(d.Y - p.Y) < 6f))).IsLessThan(3).Because($"the strided scan's triple of {kept} candidates is not the symbol's");
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var count = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey);
        foreach (var (x, y) in drawn)
            await Assert.That(Nearest(candidates.AsSpan(0, count), x, y)).IsLessThan(6f);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(TallContent);
    }

    /// <summary>A grid of version 1 symbols with 2-module quiet zones, each with its own text, supersampled at 4 px/module under keystone. The size of one symbol's grid is returned.</summary>
    internal static (byte[] Luminance, int Width, int Height, int Size) RenderGrid(int across, int down, float keystone, Func<int, string> content, Func<int, int, bool>? showsFinder = null)
    {
        var symbols = Enumerable.Range(0, across * down).Select(i => QRCodeGenerator.Create(content(i), QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1), QuietZoneSize = 2 })).ToArray();
        var size = symbols[0].Size;
        bool IsDark(int row, int column)
        {
            var symbol = row / size * across + column / size;
            int r = row % size, c = column % size;
            // A finder not shown is left light, its seven modules square: top-left 0, top-right 1, bottom-left 2
            if (showsFinder is not null && (r < 9 || r >= size - 9) && (c < 9 || c >= size - 9) && !(r >= size - 9 && c >= size - 9)
                && !showsFinder(symbol, r >= size - 9 ? 2 : c >= size - 9 ? 1 : 0))
            {
                return false;
            }
            return symbols[symbol][r, c];
        }
        var (luminance, width, height) = SupersampledRenderer.Render(IsDark, across * size, down * size, 4f, 0f, keystone);
        return (luminance, width, height, size);
    }

    /// <summary>The negative the sweep must not admit: the same scene with the symbol's data destroyed reads nothing.</summary>
    [Test]
    public async Task UndersampledStrideList_DestroyedData_ReadsNothing()
    {
        var (luminance, width, height, _) = RenderTall(destroyData: true);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out _, out var info);

        await Assert.That(success).IsFalse().Because(info.Status.ToString());
    }

    internal const string TallContent = "FQR STRIDE 1";

    /// <summary>
    /// A 600 by 4,000 px white image: the version 1 symbol with its 4-module quiet zone at 4 px/module with its top-left module at (100, 100), so the stride rows (every 16th) cross the top finders' centre bands at row 128 and the bottom-left one's (rows 180 to 191) not at all, and a lone finder pattern centred at (130, 70), crossed at row 64, a right angle from the top-left finder.
    /// </summary>
    internal static (byte[] Luminance, int Width, int Height, (float X, float Y)[] Drawn) RenderTall(bool destroyData)
    {
        const int width = 600, height = 4000, pixels = 4, left = 100, top = 100;
        var qr = QRCodeGenerator.Create(TallContent, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1), QuietZoneSize = 4 });
        var size = qr.Size;
        var random = new Random(1234);
        var luminance = new byte[width * height];
        Array.Fill(luminance, (byte)255);
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                var dark = qr[row, column];
                // Version 1's data modules lie past the finders, separators, timing patterns and format information
                if (destroyData && row >= 13 && column >= 13)
                    dark = random.Next(2) == 0;
                if (dark)
                    FillModule(luminance, width, left + column * pixels, top + row * pixels, pixels);
            }
        }

        // The lone finder: 7 by 7 modules, dark ring, light ring, dark centre, centred at (130, 70)
        for (var row = 0; row < 7; row++)
        {
            for (var column = 0; column < 7; column++)
            {
                var ring = Math.Max(Math.Abs(row - 3), Math.Abs(column - 3));
                if (ring != 2)
                    FillModule(luminance, width, 130 - 14 + column * pixels, 70 - 14 + row * pixels, pixels);
            }
        }

        var centre = 4 + 3.5f;
        (float X, float Y)[] drawn = [(left + pixels * centre, top + pixels * centre), (left + pixels * (size - centre), top + pixels * centre), (left + pixels * centre, top + pixels * (size - centre))];
        return (luminance, width, height, drawn);

        static void FillModule(byte[] luminance, int width, int x, int y, int pixels)
        {
            for (var dy = 0; dy < pixels; dy++)
                Array.Fill(luminance, (byte)0, (y + dy) * width + x, pixels);
        }
    }

    private static bool OneSymbol(List<(int Symbol, float X, float Y)> drawn, FinderPattern[] triple)
    {
        var symbols = new HashSet<int>();
        foreach (var p in triple)
        {
            var owner = drawn.Where(d => Math.Abs(d.X - p.X) < 6f && Math.Abs(d.Y - p.Y) < 6f).Select(static d => (int?)d.Symbol).FirstOrDefault();
            if (owner is null)
                return false;
            symbols.Add(owner.Value);
        }
        return symbols.Count == 1;
    }

    private static float Nearest(ReadOnlySpan<FinderPattern> candidates, float x, float y)
    {
        var best = float.MaxValue;
        foreach (var candidate in candidates)
            best = Math.Min(best, MathF.Sqrt((candidate.X - x) * (candidate.X - x) + (candidate.Y - y) * (candidate.Y - y)));
        return best;
    }
}
