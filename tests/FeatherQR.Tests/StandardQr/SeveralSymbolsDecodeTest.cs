using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The entry that returns every symbol of an image in one call (<see cref="QRImageDecoder.DecodeSymbols"/>): each drawn symbol once, with its own corners, whichever pass reads it, and nothing that is not drawn.
/// Its first result is what the one-symbol call reads of the same image.
/// </summary>
public class SeveralSymbolsDecodeTest
{
    /// <summary>What a result's corner may be off by, in modules of its symbol.</summary>
    private const float CornerToleranceModules = 1f;

    private sealed record Drawn(string Text, (float X, float Y)[] Corners, float ModulePixels);

    private static DecodedSymbol[] Read(byte[] luminance, int width, int height, int room = 128)
    {
        var symbols = new DecodedSymbol[room];
        var count = QRImageDecoder.DecodeSymbols(luminance, width, height, symbols);
        return symbols.AsSpan(0, count).ToArray();
    }

    /// <summary>Every drawn symbol is a result once, with its text and its corners in the drawn order, and there is no other result.</summary>
    private static async Task AssertReadsEach(DecodedSymbol[] read, IReadOnlyList<Drawn> drawn)
    {
        foreach (var symbol in drawn)
        {
            var matches = read.Where(r => r.Text == symbol.Text).ToArray();
            await Assert.That(matches.Length).IsEqualTo(1).Because($"{symbol.Text} is read once");
            var corners = matches[0].Info.Corners;
            ImagePoint[] actual = [corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft];
            for (var i = 0; i < 4; i++)
            {
                var distance = MathF.Sqrt((actual[i].X - symbol.Corners[i].X) * (actual[i].X - symbol.Corners[i].X) + (actual[i].Y - symbol.Corners[i].Y) * (actual[i].Y - symbol.Corners[i].Y));
                await Assert.That(distance).IsLessThanOrEqualTo(CornerToleranceModules * symbol.ModulePixels)
                    .Because($"{symbol.Text} corner {i}: got ({actual[i].X:F1}, {actual[i].Y:F1}), drawn at ({symbol.Corners[i].X:F1}, {symbol.Corners[i].Y:F1})");
            }
            await Assert.That(matches[0].Info.Status).IsEqualTo(DecodeStatus.Success);
        }
        await Assert.That(read.Length).IsEqualTo(drawn.Count).Because($"read {string.Join(", ", read.Select(static r => r.Text))}");
    }

    /// <summary>The first result is the one-symbol call's read of the same image, to the last field.</summary>
    private static async Task AssertFirstIsTheOneSymbolRead(DecodedSymbol[] read, byte[] luminance, int width, int height)
    {
        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
        await Assert.That(success).IsEqualTo(read.Length > 0);
        if (!success)
            return;
        await Assert.That(read[0].Text).IsEqualTo(text);
        await Assert.That(read[0].Info).IsEqualTo(info);
    }

    /// <summary>The symbols of <see cref="TripleSelectionDecodeTest.RenderGrid"/> with where each was drawn: version 1, a 2-module quiet zone, 4 px a module.</summary>
    private static (byte[] Luminance, int Width, int Height, List<Drawn> Drawn) Grid(int across, int down, float keystone, Func<int, int, bool>? showsFinder = null)
    {
        var (luminance, width, height, size) = TripleSelectionDecodeTest.RenderGrid(across, down, keystone, static i => $"FQR GRID {i}", showsFinder);
        var truth = SupersampledGeometry.GridToPixel(across * size, down * size, 4f, 0f, keystone);
        var drawn = new List<Drawn>();
        for (var s = 0; s < across * down; s++)
        {
            float left = s % across * size + 2, top = s / across * size + 2, side = size - 4;
            var corners = new (float X, float Y)[4];
            truth.Transform(left, top, out corners[0].X, out corners[0].Y);
            truth.Transform(left + side, top, out corners[1].X, out corners[1].Y);
            truth.Transform(left + side, top + side, out corners[2].X, out corners[2].Y);
            truth.Transform(left, top + side, out corners[3].X, out corners[3].Y);
            drawn.Add(new Drawn($"FQR GRID {s}", corners, 4f));
        }
        return (luminance, width, height, drawn);
    }

    /// <summary>
    /// Two, three and four symbols and a grid of them, flat and under keystone, where the three finders that meet at a corner of three symbols make a better triple than any symbol's own.
    /// </summary>
    [Test]
    [Arguments(2, 1, 0f)]
    [Arguments(3, 1, 0f)]
    [Arguments(2, 2, 0f)]
    [Arguments(2, 2, 0.1f)]
    [Arguments(3, 3, 0.12f)]
    [Arguments(4, 2, 0.15f)]
    public async Task GridOfSymbols_EachIsReadOnce_WithItsCorners(int across, int down, float keystone)
    {
        var (luminance, width, height, drawn) = Grid(across, down, keystone);

        var read = Read(luminance, width, height);

        await AssertReadsEach(read, drawn);
        await AssertFirstIsTheOneSymbolRead(read, luminance, width, height);
    }

    /// <summary>
    /// More symbols than one candidate list holds the finders of: 16, 60 and 100 symbols are 48, 180 and 300 finders against a list of <see cref="FinderPatternFinder.MaxFinderCandidates"/>.
    /// </summary>
    [Test]
    [Arguments(4, 4)]
    [Arguments(6, 10)]
    [Arguments(10, 10)]
    public async Task GridPastOneCandidateList_EachIsReadOnce_WithItsCorners(int across, int down)
    {
        var (luminance, width, height, drawn) = Grid(across, down, 0f);
        await Assert.That(3 * drawn.Count).IsGreaterThan(FinderPatternFinder.MaxFinderCandidates);

        var read = Read(luminance, width, height);

        await AssertReadsEach(read, drawn);
        await AssertFirstIsTheOneSymbolRead(read, luminance, width, height);
    }

    /// <summary>
    /// A grid whose top three rows of four do not read, their data drawn at random: 36 finders that pass every check a finder passes, more than the stages hold at once, scanned before every symbol that reads.
    /// The one-symbol call reads nothing of it. Here the twenty symbols below are read, since a head of the list the stages give up on leaves the list.
    /// </summary>
    [Test]
    public async Task SymbolsThatDoNotRead_DoNotHideTheOnesScannedAfterThem()
    {
        const int Across = 4, Down = 8, Unreadable = 12;
        var symbols = Enumerable.Range(0, Across * Down).Select(static i => Symbol($"FQR GRID {i}", 1, quietZone: 2)).ToArray();
        var size = symbols[0].Size;
        var random = new Random(20261014);
        var noise = new bool[Unreadable * size * size];
        for (var i = 0; i < noise.Length; i++)
            noise[i] = random.Next(2) == 0;
        bool IsDark(int row, int column)
        {
            var symbol = row / size * Across + column / size;
            int r = row % size, c = column % size;
            // Version 1's data lies past the finders, the timing patterns and the format information: modules 9 to 20 of each axis, behind a 2-module quiet zone
            if (symbol < Unreadable && r >= 11 && c >= 11 && r < size - 2 && c < size - 2)
                return noise[(symbol * size + r) * size + c];
            return symbols[symbol][r, c];
        }
        var (luminance, width, height) = SupersampledRenderer.Render(IsDark, Across * size, Down * size, 4f, 0f, 0f);
        var truth = SupersampledGeometry.GridToPixel(Across * size, Down * size, 4f, 0f, 0f);
        var drawn = new List<Drawn>();
        for (var s = Unreadable; s < Across * Down; s++)
        {
            float left = s % Across * size + 2, top = s / Across * size + 2, side = size - 4;
            var corners = new (float X, float Y)[4];
            truth.Transform(left, top, out corners[0].X, out corners[0].Y);
            truth.Transform(left + side, top, out corners[1].X, out corners[1].Y);
            truth.Transform(left + side, top + side, out corners[2].X, out corners[2].Y);
            truth.Transform(left, top + side, out corners[3].X, out corners[3].Y);
            drawn.Add(new Drawn($"FQR GRID {s}", corners, 4f));
        }
        var findersThatDoNotRead = 3 * Unreadable;
        await Assert.That(findersThatDoNotRead).IsGreaterThan(FinderPatternFinder.MaxFinderCandidates);
        await Assert.That(QRCodeDecoder.TryDecodeImage(luminance, width, height, out _, out _)).IsFalse().Because("a premise: the one-symbol call does not get past the symbols that do not read");

        var read = Read(luminance, width, height);

        await AssertReadsEach(read, drawn);
    }

    /// <summary>A white canvas with tiles pasted side by side, each tile's drawn corners moved with it.</summary>
    private static (byte[] Luminance, int Width, int Height, List<Drawn> Drawn) Compose(params (byte[] Luminance, int Width, int Height, Drawn Drawn)[] tiles)
    {
        const int Gap = 24;
        var width = tiles.Sum(static t => t.Width) + Gap * (tiles.Length + 1);
        var height = tiles.Max(static t => t.Height) + 2 * Gap;
        var luminance = new byte[width * height];
        Array.Fill(luminance, (byte)255);
        var drawn = new List<Drawn>();
        var x = Gap;
        foreach (var tile in tiles)
        {
            for (var row = 0; row < tile.Height; row++)
                tile.Luminance.AsSpan(row * tile.Width, tile.Width).CopyTo(luminance.AsSpan((Gap + row) * width + x, tile.Width));
            drawn.Add(tile.Drawn with { Corners = [.. tile.Drawn.Corners.Select(c => (c.X + x, c.Y + Gap))] });
            x += tile.Width + Gap;
        }
        return (luminance, width, height, drawn);
    }

    private static QRCodeData Symbol(string text, int version, int quietZone = 0)
        => QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = quietZone });

    /// <summary>A symbol drawn alone by <see cref="SupersampledRenderer"/>, turned and under keystone.</summary>
    private static (byte[] Luminance, int Width, int Height, Drawn Drawn) Tile(string text, int version, float pixelsPerModule, float degrees, float keystone)
    {
        var qr = Symbol(text, version);
        var (luminance, side) = SupersampledRenderer.Render(qr, pixelsPerModule, degrees, keystone);
        var truth = SupersampledGeometry.GridToPixel(qr.Size, qr.Size, pixelsPerModule, degrees, keystone);
        var corners = new (float X, float Y)[4];
        truth.Transform(0, 0, out corners[0].X, out corners[0].Y);
        truth.Transform(qr.Size, 0, out corners[1].X, out corners[1].Y);
        truth.Transform(qr.Size, qr.Size, out corners[2].X, out corners[2].Y);
        truth.Transform(0, qr.Size, out corners[3].X, out corners[3].Y);
        return (luminance, side, side, new Drawn(text, corners, pixelsPerModule));
    }

    /// <summary>A tile light on dark: the symbol's negative, on a black ground.</summary>
    private static (byte[] Luminance, int Width, int Height, Drawn Drawn) Negative((byte[] Luminance, int Width, int Height, Drawn Drawn) tile)
        => ([.. tile.Luminance.Select(static l => (byte)(255 - l))], tile.Width, tile.Height, tile.Drawn);

    /// <summary>A symbol under a one-pixel texture (<see cref="FineTextureRenderer"/>), which reads only once the image is halved.</summary>
    private static (byte[] Luminance, int Width, int Height, Drawn Drawn) TexturedTile(string text, int version, int pixelsPerModule)
    {
        const int QuietZone = 4;
        var qr = Symbol(text, version, QuietZone);
        var (luminance, width, height) = FineTextureRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule);
        float near = QuietZone * pixelsPerModule, far = (qr.Size - QuietZone) * pixelsPerModule;
        return (luminance, width, height, new Drawn(text, [(near, near), (far, near), (far, far), (near, far)], pixelsPerModule));
    }

    /// <summary>Symbols of four versions and sizes in one image, one turned and one under keystone.</summary>
    [Test]
    public async Task SymbolsOfDifferentVersionsSizesAndTurns_EachIsReadOnce_WithItsCorners()
    {
        var (luminance, width, height, drawn) = Compose(
            Tile("FQR SEVERAL A", 1, 6f, 0f, 0f),
            Tile("FQR SEVERAL B, a longer text for version five", 5, 4f, 17f, 0f),
            Tile("FQR SEVERAL C", 2, 5f, 0f, 0.1f),
            Tile("FQR SEVERAL D on a version ten symbol, which holds a great deal more than the others do", 10, 3f, 90f, 0f));

        var read = Read(luminance, width, height);

        await AssertReadsEach(read, drawn);
        await AssertFirstIsTheOneSymbolRead(read, luminance, width, height);
    }

    /// <summary>
    /// A symbol read at reduced scale whose finders, in the pixels of the image halved and of the image quartered, lie inside the outline of a symbol read at full size.
    /// A reduced reading's candidates are in its own pixels, and they are held to the outlines in the image's.
    /// </summary>
    [Test]
    public async Task SymbolReadAtReducedScale_WhoseReducedPositionLiesInsideAnother_IsRead()
    {
        // A version 10 symbol at 8 px a module from (0, 0), its 2-module quiet zone included, and the textured one right of it, 40 px down
        var large = Symbol("FQR REDUCED BESIDE A LARGE SYMBOL", 10, quietZone: 2);
        var (largeLuminance, largeWidth, largeHeight) = NearestNeighbourRenderer.Render((row, column) => large[row, column], large.Size, large.Size, 8f, 0f, 0f);
        var textured = TexturedTile("FQR REDUCED TEXTURED", 3, 8);
        const int Left = 500, Top = 40;
        int width = Left + textured.Width, height = Math.Max(largeHeight, Top + textured.Height);
        var luminance = new byte[width * height];
        Array.Fill(luminance, (byte)255);
        for (var row = 0; row < largeHeight; row++)
            largeLuminance.AsSpan(row * largeWidth, largeWidth).CopyTo(luminance.AsSpan(row * width, largeWidth));
        for (var row = 0; row < textured.Height; row++)
            textured.Luminance.AsSpan(row * textured.Width, textured.Width).CopyTo(luminance.AsSpan((Top + row) * width + Left, textured.Width));
        float near = 2 * 8f, far = (large.Size - 2) * 8f;
        var drawn = new List<Drawn>
        {
            new("FQR REDUCED BESIDE A LARGE SYMBOL", [(near, near), (far, near), (far, far), (near, far)], 8f),
            textured.Drawn with { Corners = [.. textured.Drawn.Corners.Select(static c => (c.X + Left, c.Y + Top))] },
        };

        // The premise: each finder centre of the textured symbol, three and a half modules inside its corners, lies inside the large symbol's outline at half and at a quarter of its position
        var (topLeft, topRight, bottomLeft) = (drawn[1].Corners[0], drawn[1].Corners[1], drawn[1].Corners[3]);
        foreach (var (x, y) in new[] { (topLeft.X + 28f, topLeft.Y + 28f), (topRight.X - 28f, topRight.Y + 28f), (bottomLeft.X + 28f, bottomLeft.Y - 28f) })
        {
            foreach (var scale in new[] { 2f, 4f })
                await Assert.That(x / scale > near && x / scale < far && y / scale > near && y / scale < far).IsTrue().Because($"({x}, {y}) at 1/{scale}");
        }

        var read = Read(luminance, width, height);

        await AssertReadsEach(read, drawn);
    }

    /// <summary>The global positive pass alone, the first the decoder runs.</summary>
    private static DecodeStatus PositivePass(byte[] luminance, int width, int height)
    {
        Span<int> histogram = stackalloc int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(luminance, histogram);
        return QRImageDecoder.DecodeLuminanceCore(luminance, histogram, width, height, new char[QRCodeDecoder.GetMaxDecodedLength(40)], out _, out _);
    }

    /// <summary>
    /// Three symbols no one pass reads: one dark on light, one light on dark, which the negative reads, and one under a texture, which the image halved reads.
    /// A read does not end the passes, and the symbol the first pass read is not read again by the ones after it.
    /// </summary>
    [Test]
    public async Task SymbolsReadByDifferentPasses_EachIsReadOnce_WithItsCorners()
    {
        var plain = Tile("FQR PASS PLAIN", 2, 6f, 0f, 0f);
        var negative = Negative(Tile("FQR PASS NEGATIVE", 2, 6f, 0f, 0f));
        var textured = TexturedTile("FQR PASS TEXTURED", 3, 8);

        // The premises: alone on the canvas, the negative and the textured symbol read, and not in the first pass
        foreach (var later in new[] { negative, textured })
        {
            var alone = Compose(later);
            await Assert.That(PositivePass(alone.Luminance, alone.Width, alone.Height)).IsNotEqualTo(DecodeStatus.Success).Because($"{later.Drawn.Text} is not read by the first pass");
            await Assert.That(QRCodeDecoder.TryDecodeImage(alone.Luminance, alone.Width, alone.Height, out var aloneText, out _)).IsTrue();
            await Assert.That(aloneText).IsEqualTo(later.Drawn.Text);
        }
        var (luminance, width, height, drawn) = Compose(plain, negative, textured);
        await Assert.That(PositivePass(luminance, width, height)).IsEqualTo(DecodeStatus.Success);

        var read = Read(luminance, width, height);

        await AssertReadsEach(read, drawn);
        await AssertFirstIsTheOneSymbolRead(read, luminance, width, height);
        await Assert.That(read[0].Text).IsEqualTo(plain.Drawn.Text);
    }

    /// <summary>
    /// An image of one symbol gives one result, the one-symbol call's: read in the first pass, in the negative, under a texture at half scale, through grey edges, a turn and a keystone, and from version 1 to 40.
    /// The passes after the one that reads it find its finders again and do not read it again.
    /// </summary>
    [Test]
    [Arguments(1, 4f, 0f, 0f)]
    [Arguments(2, 3f, 30f, 0f)]
    [Arguments(6, 4f, 0f, 0.2f)]
    [Arguments(7, 3f, 45f, 0.1f)]
    [Arguments(14, 3f, 17f, 0f)]
    [Arguments(25, 4f, 23f, 0.15f)]
    [Arguments(40, 3f, 0f, 0f)]
    public async Task OneSymbol_IsOneResult_TheOneSymbolRead(int version, float pixelsPerModule, float degrees, float keystone)
    {
        var tile = Tile($"FQR ONE v{version}", version, pixelsPerModule, degrees, keystone);
        foreach (var (luminance, width, height, drawn) in new[] { Compose(tile), Compose(Negative(tile)) })
        {
            var read = Read(luminance, width, height);

            await AssertReadsEach(read, drawn);
            await AssertFirstIsTheOneSymbolRead(read, luminance, width, height);
        }
    }

    /// <summary>
    /// One symbol that the one-symbol search reads through each stage after its first triple, drawn as the test that pins that stage draws it:
    /// the skipped rows (<see cref="TripleSelectionDecodeTest"/>), the triples of a crowded list, here one whole symbol among three that show a finder each, an alternative triple (<see cref="KeystoneFinderDecodeTest"/>), a finder inferred from two (<see cref="InferredFinderDecodeTest"/>) and the regional pass (<see cref="UnevenLightingDecodeTest"/>).
    /// </summary>
    public static IEnumerable<Func<(string Name, byte[] Luminance, int Width, int Height)>> LaterStageScenes()
    {
        yield return () =>
        {
            var (luminance, width, height, _) = TripleSelectionDecodeTest.RenderTall(destroyData: false);
            return ("skipped rows", luminance, width, height);
        };
        yield return () =>
        {
            (int Symbol, int Finder)[] shown = [(0, 0), (0, 1), (0, 2), (1, 2), (2, 1), (3, 0)];
            var (luminance, width, height, _) = TripleSelectionDecodeTest.RenderGrid(2, 2, 0.12f, static i => $"FQR PART {i}", (symbol, finder) => shown.Contains((symbol, finder)));
            return ("crowded list", luminance, width, height);
        };
        foreach (var (version, pixels, degrees, keystone) in new[] { (40, 4.11f, 268.9f, 0.444f), (35, 4f, 250.6f, 0.469f), (38, 3.45f, 93.3f, 0.466f) })
        {
            yield return () =>
            {
                var qr = QRCodeGenerator.Create("FQR KEYSTONE 0123", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version) });
                var (luminance, width, height) = SupersampledRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixels, degrees, keystone, 0f);
                return ($"alternative triple v{version}", luminance, width, height);
            };
        }
        foreach (var (blanked, degrees, keystone) in new[] { (InferredFinderDecodeTest.Finder.TopLeft, 0f, 0f), (InferredFinderDecodeTest.Finder.TopRight, 335f, 0f), (InferredFinderDecodeTest.Finder.BottomLeft, 200f, 0.18f) })
        {
            yield return () =>
            {
                var (luminance, width, height, _, _) = InferredFinderDecodeTest.Render(blanked, degrees, keystone);
                return ($"inferred {blanked}", luminance, width, height);
            };
        }
        yield return () =>
        {
            var qr = Symbol("FQR ONE SHADOWED", 3, quietZone: 4);
            var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, 4, UnevenLight.Shadow, 180f, 0.55f);
            return ("regional pass", luminance, width, height);
        };
    }

    [Test]
    [MethodDataSource(nameof(LaterStageScenes))]
    public async Task OneSymbolReadByALaterStage_IsOneResult_TheOneSymbolRead(string name, byte[] luminance, int width, int height)
    {
        var read = Read(luminance, width, height);

        await Assert.That(read.Length).IsEqualTo(1).Because(name);
        await AssertFirstIsTheOneSymbolRead(read, luminance, width, height);
    }

    /// <summary>
    /// Two symbols on one sheet with a shadow across the middle of the left one, which only the regional pass reads, and the right one in full light: the regional pass gives its symbol after the first pass gave the other.
    /// </summary>
    [Test]
    public async Task SymbolOnlyTheRegionalPassReads_BesideAPlainOne_BothAreRead()
    {
        const int QuietZone = 4, Pixels = 4;
        var shadowed = Symbol("FQR PASS SHADOWED", 3, QuietZone);
        var plain = Symbol("FQR PASS PLAIN", 3, QuietZone);
        var size = shadowed.Size;
        // Three places in a row, the first empty, so that the shadow's edge, through the middle of the sheet, crosses the symbol in the second
        bool IsDark(int row, int column) => (column / size) switch { 1 => shadowed[row, column - size], 2 => plain[row, column - 2 * size], _ => false };
        var (luminance, width, height) = UnevenLightingRenderer.Render(IsDark, 3 * size, size, Pixels, UnevenLight.Shadow, 180f, 0.55f);
        Drawn At(string text, int place)
        {
            float left = (place * size + QuietZone) * Pixels, right = ((place + 1) * size - QuietZone) * Pixels, top = QuietZone * Pixels, bottom = (size - QuietZone) * Pixels;
            return new Drawn(text, [(left, top), (right, top), (right, bottom), (left, bottom)], Pixels);
        }

        // The premises: with the plain symbol painted out, neither global pass reads the sheet, and the one-symbol call does
        var alone = (byte[])luminance.Clone();
        for (var y = 0; y < height; y++)
            alone.AsSpan(y * width + 2 * size * Pixels, size * Pixels).Fill(alone[y * width + width - 1]);
        await Assert.That(PositivePass(alone, width, height)).IsNotEqualTo(DecodeStatus.Success);
        await Assert.That(PositivePass([.. alone.Select(static l => (byte)(255 - l))], width, height)).IsNotEqualTo(DecodeStatus.Success);
        await Assert.That(QRCodeDecoder.TryDecodeImage(alone, width, height, out var aloneText, out _)).IsTrue();
        await Assert.That(aloneText).IsEqualTo("FQR PASS SHADOWED");

        var read = Read(luminance, width, height);

        await AssertReadsEach(read, [At("FQR PASS SHADOWED", 1), At("FQR PASS PLAIN", 2)]);
        await AssertFirstIsTheOneSymbolRead(read, luminance, width, height);
        await Assert.That(read[0].Text).IsEqualTo("FQR PASS PLAIN");
    }

    [Test]
    public async Task OneSymbolUnderATexture_IsOneResult_TheOneSymbolRead()
    {
        var (luminance, width, height, drawn) = Compose(TexturedTile("FQR ONE TEXTURED", 3, 8));

        var read = Read(luminance, width, height);

        await AssertReadsEach(read, drawn);
        await AssertFirstIsTheOneSymbolRead(read, luminance, width, height);
    }

    /// <summary>The results stop at the room the caller gives: none with no room, the one-symbol read with room for one, and no more than the room for any.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(8)]
    [Arguments(9)]
    public async Task RoomForFewerThanTheImageHolds_GivesAsManyAsFit(int room)
    {
        var (luminance, width, height, drawn) = Grid(3, 3, 0f);

        var read = Read(luminance, width, height, room);

        await Assert.That(read.Length).IsEqualTo(room);
        await Assert.That(read.Select(static r => r.Text).Distinct().Count()).IsEqualTo(room);
        foreach (var result in read)
            await Assert.That(drawn.Any(d => d.Text == result.Text)).IsTrue();
        if (room > 0)
            await AssertFirstIsTheOneSymbolRead(read, luminance, width, height);
    }

    /// <summary>
    /// The three finders that meet where three symbols touch, each symbol showing only that one: a right isosceles triangle as well shaped as a symbol's own, and nothing to read.
    /// </summary>
    [Test]
    [Arguments(0f)]
    [Arguments(0.12f)]
    public async Task FindersOfThreeNeighbours_AreNoSymbol(float keystone)
    {
        (int Symbol, int Finder)[] shown = [(1, 2), (2, 1), (3, 0)];
        var (luminance, width, height, _) = Grid(2, 2, keystone, (symbol, finder) => shown.Contains((symbol, finder)));

        // The premise: the three are candidates, and the selection takes them
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var selected = new FinderPattern[3];
        var list = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        await Assert.That(FinderPatternFinder.TryFind(luminance, width, height, threshold, selected, grey, list, out var count, out _)).IsTrue();
        await Assert.That(count).IsEqualTo(3);

        var read = Read(luminance, width, height);

        await Assert.That(read.Length).IsEqualTo(0).Because($"read {string.Join(", ", read.Select(static r => r.Text))}");
    }

    /// <summary>
    /// Two symbols side by side, the left showing only the finder beside the right one and the right missing its top-right finder: the three finders shown are a triple across the two symbols, and no symbol is made of it.
    /// The right symbol holds two finders of its own, which a decoder may read it from. Then the result is that symbol's, with its corners, the one at the missing finder within two modules.
    /// </summary>
    [Test]
    public async Task FindersOfTwoNeighbours_AreNotOneSymbol()
    {
        (int Symbol, int Finder)[] shown = [(0, 1), (1, 0), (1, 2)];
        var (luminance, width, height, drawn) = Grid(2, 1, 0f, (symbol, finder) => shown.Contains((symbol, finder)));

        // The premise: the three are the candidates, and the selection takes them
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var selected = new FinderPattern[3];
        var list = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        await Assert.That(FinderPatternFinder.TryFind(luminance, width, height, threshold, selected, grey, list, out var count, out _)).IsTrue();
        await Assert.That(count).IsEqualTo(3);

        var read = Read(luminance, width, height);

        await Assert.That(read.Length).IsLessThanOrEqualTo(1).Because($"read {string.Join(", ", read.Select(static r => r.Text))}");
        foreach (var result in read)
        {
            await Assert.That(result.Text).IsEqualTo(drawn[1].Text);
            var corners = result.Info.Corners;
            ImagePoint[] actual = [corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft];
            for (var i = 0; i < 4; i++)
            {
                var distance = MathF.Sqrt((actual[i].X - drawn[1].Corners[i].X) * (actual[i].X - drawn[1].Corners[i].X) + (actual[i].Y - drawn[1].Corners[i].Y) * (actual[i].Y - drawn[1].Corners[i].Y));
                await Assert.That(distance).IsLessThanOrEqualTo(2f * drawn[1].ModulePixels).Because($"corner {i}: got ({actual[i].X:F1}, {actual[i].Y:F1}), drawn at ({drawn[1].Corners[i].X:F1}, {drawn[1].Corners[i].Y:F1})");
            }
        }
        await AssertFirstIsTheOneSymbolRead(read, luminance, width, height);
    }

    /// <summary>
    /// A symbol whose content does not decode, a Kanji cell outside the table, beside one that reads: the one-symbol call stops at the verdict, and here the symbol that reads is the result.
    /// </summary>
    [Test]
    public async Task SymbolWithAContentVerdict_IsLeftOut_AndTheOthersAreRead()
    {
        const int Unmapped = 0x8740;
        var modules = KanjiUnmappedCharacterEndToEndTest.BuildSymbol(Unmapped);
        const int Size = KanjiUnmappedCharacterEndToEndTest.Size;
        var (verdictLuminance, verdictSide, _) = SupersampledRenderer.Render((row, column) => modules[row * Size + column] != 0, Size, Size, 6f, 0f, 0f);
        var readable = Tile("FQR BESIDE A VERDICT", 1, 6f, 0f, 0f);
        var (luminance, width, height, drawn) = Compose((verdictLuminance, verdictSide, verdictSide, new Drawn("", [], 6f)), readable);

        // The premise: the one-symbol call reports the verdict
        await Assert.That(QRCodeDecoder.TryDecodeImage(luminance, width, height, out _, out var info)).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.UnmappedCharacter);

        var read = Read(luminance, width, height);

        await AssertReadsEach(read, [drawn[1]]);
    }

    /// <summary>
    /// The committed real images, as given and turned a quarter: on each the first result is the one-symbol call's read, and an image that call reads nothing of gives no result.
    /// </summary>
    [Test]
    public async Task RealImages_FirstResultIsTheOneSymbolRead()
    {
        var root = Path.Combine(FixtureLoader.FixtureRoot, "RealImages", "zxing-cpp-samples");
        var images = 0;
        var differing = new List<string>();
        foreach (var path in Directory.EnumerateDirectories(root, "qrcode-*").SelectMany(Directory.EnumerateFiles).Where(static p => Path.GetExtension(p) is ".png" or ".jpg" or ".webp").Order(StringComparer.Ordinal))
        {
            using var bitmap = SKBitmap.Decode(path);
            var upright = new byte[bitmap.Width * bitmap.Height];
            BitmapLuminanceConverter.Convert(bitmap, upright);
            for (var turns = 0; turns < 2; turns++)
            {
                var (luminance, width, height) = NearestNeighbourRenderer.Turn(upright, bitmap.Width, bitmap.Height, turns, mirror: false);
                var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
                var read = Read(luminance, width, height);
                images++;
                // A verdict ends the one-symbol call where this entry goes on, so only a read is held to it
                var same = success ? read.Length > 0 && read[0].Text == text && read[0].Info == info : read.Length == 0 || AttemptStatus.IsContentVerdict(info.Status);
                if (!same)
                    differing.Add($"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)} turned {turns}: {info.Status}, {read.Length} results");
            }
        }

        await Assert.That(images).IsGreaterThan(200);
        await Assert.That(differing).IsEmpty().Because(string.Join("; ", differing));
    }

    private static DecodedSymbol At(float left, float top, float side)
        => new("", new QRCodeDecodeInfo(DecodeStatus.Success, 1, QREccLevel.M, 0, 0).WithCorners(new SymbolCorners(new ImagePoint(left, top), new ImagePoint(left + side, top), new ImagePoint(left + side, top + side), new ImagePoint(left, top + side))));

    /// <summary>
    /// What makes a read a symbol already held: the same symbol read a little off is, and a symbol beside it, one half its size inside it and one twice its size around it are not.
    /// </summary>
    [Test]
    [Arguments(100f, 100f, 84f, true)]
    [Arguments(104f, 96f, 88f, true)]
    [Arguments(120f, 100f, 84f, true)]
    [Arguments(124f, 100f, 84f, false)]
    [Arguments(200f, 100f, 84f, false)]
    [Arguments(121f, 121f, 42f, false)]
    [Arguments(58f, 58f, 168f, false)]
    public async Task ReadAlready_IsTheSameSymbolAndNoOther(float left, float top, float side, bool expected)
    {
        DecodedSymbol[] held = [At(300f, 300f, 84f), At(100f, 100f, 84f)];

        await Assert.That(QRImageDecoder.IsReadAlready(held, At(left, top, side).Info.Corners)).IsEqualTo(expected);
    }

    /// <summary>One corner far off is another symbol, whichever corner it is.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ReadAlready_NeedsEveryCornerNear(int moved)
    {
        DecodedSymbol[] held = [At(100f, 100f, 84f)];
        ImagePoint[] corners = [new(100f, 100f), new(184f, 100f), new(184f, 184f), new(100f, 184f)];
        corners[moved] = new ImagePoint(corners[moved].X + 30f, corners[moved].Y + 30f);

        await Assert.That(QRImageDecoder.IsReadAlready(held, new SymbolCorners(corners[0], corners[1], corners[2], corners[3]))).IsFalse();
    }

    private static FinderPattern Candidate(float x, float y) => new() { X = x, Y = y, ModuleSize = 4f, Count = 3 };

    /// <summary>
    /// The passes are run again only when that can read more: a read took candidates a pass had given up on out of its list and a triple's worth is left, or the passes gave up on more than were kept.
    /// Not when nothing was given up on, when every candidate given up on was the read symbol's, when no read touched them, or when two are left.
    /// </summary>
    [Test]
    public async Task AnotherRound_OnlyWhenAReadChangedAListThatStillHoldsATriple()
    {
        DecodedSymbol[] read = [At(100f, 100f, 84f)];
        FinderPattern[] inside = [Candidate(114f, 114f), Candidate(170f, 114f), Candidate(114f, 170f)];
        FinderPattern[] outside = [Candidate(314f, 114f), Candidate(370f, 114f), Candidate(314f, 170f)];

        await Assert.That(QRImageDecoder.IsWorthAnotherRound([], leftOut: false, read)).IsFalse();
        await Assert.That(QRImageDecoder.IsWorthAnotherRound([.. inside], leftOut: false, read)).IsFalse();
        await Assert.That(QRImageDecoder.IsWorthAnotherRound([.. outside], leftOut: false, read)).IsFalse();
        await Assert.That(QRImageDecoder.IsWorthAnotherRound([.. inside, .. outside], leftOut: false, read)).IsTrue();
        await Assert.That(QRImageDecoder.IsWorthAnotherRound([.. outside, .. inside], leftOut: false, read)).IsTrue();
        await Assert.That(QRImageDecoder.IsWorthAnotherRound([.. inside, outside[0], outside[1]], leftOut: false, read)).IsFalse();
        await Assert.That(QRImageDecoder.IsWorthAnotherRound([], leftOut: true, read)).IsTrue();
    }

    /// <summary>Nothing to read gives no result: an image with no symbol, and one whose size does not hold together.</summary>
    [Test]
    public async Task NoSymbol_GivesNoResult()
    {
        var random = new Random(20261014);
        var noise = new byte[300 * 300];
        random.NextBytes(noise);
        var white = new byte[200 * 200];
        Array.Fill(white, (byte)255);

        await Assert.That(Read(noise, 300, 300).Length).IsEqualTo(0);
        await Assert.That(Read(white, 200, 200).Length).IsEqualTo(0);
        await Assert.That(Read(white, 300, 300).Length).IsEqualTo(0).Because("the buffer is shorter than the size given");
        await Assert.That(Read(white, 0, 200).Length).IsEqualTo(0);
    }
}
