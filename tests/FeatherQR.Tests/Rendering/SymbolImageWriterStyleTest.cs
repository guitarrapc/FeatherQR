using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Xml.Linq;
using SkiaSharp;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// Module and finder pattern styles from the core: the options, what each format writes for them, that the PNG and the pixel buffer
/// agree, that a finder pattern never takes the module style, and that an unstyled image is the one-bit image it was.
/// </summary>
public class SymbolImageWriterStyleTest
{
    private static readonly SymbolColor Navy = new(0x1E, 0x3A, 0x8A);
    private static readonly SymbolColor Ivory = new(0xFF, 0xFF, 0xF0);
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    // ---- the options ----------------------------------------------------------------------

    [Test]
    public async Task Defaults_AreSquaresAtFullSize_WithSquareFinders()
    {
        var options = default(SymbolImageOptions);
        await Assert.That(options.ModuleStyle).IsEqualTo(ModuleStyle.Square);
        await Assert.That(options.ModuleScalePercent).IsEqualTo(100);
        await Assert.That(options.FinderPatternStyle).IsEqualTo(FinderPatternStyle.Square);
        await Assert.That(options.CornerRadiusPercent).IsEqualTo(30);

        var explicitDefaults = new SymbolImageOptions { ModuleStyle = ModuleStyle.Square, ModuleScalePercent = 100, FinderPatternStyle = FinderPatternStyle.Square, CornerRadiusPercent = 30 };
        await Assert.That(explicitDefaults).IsEqualTo(options);
    }

    [Test]
    public async Task OutOfRangeValues_Throw()
    {
        await Assert.That(() => new SymbolImageOptions { ModuleScalePercent = 0 }).Throws<ArgumentOutOfRangeException>();
        // Half the cell is the floor, as the image builders take it.
        await Assert.That(() => new SymbolImageOptions { ModuleScalePercent = 49 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(new SymbolImageOptions { ModuleScalePercent = 50 }.ModuleScalePercent).IsEqualTo(50);
        await Assert.That(() => new SymbolImageOptions { ModuleScalePercent = 101 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SymbolImageOptions { CornerRadiusPercent = -1 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SymbolImageOptions { CornerRadiusPercent = 101 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SymbolImageOptions { ModuleStyle = (ModuleStyle)3 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SymbolImageOptions { ModuleStyle = (ModuleStyle)(-1) }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SymbolImageOptions { FinderPatternStyle = (FinderPatternStyle)4 }).Throws<ArgumentOutOfRangeException>();
        // The media type goes into an SVG attribute as it is, so it is refused where it could break one.
        await Assert.That(() => new SymbolImageOptions { IconMediaType = "image/png\"" }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SymbolImageOptions { IconMediaType = "image/svg xml" }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SymbolImageOptions { IconMediaType = "image/<svg>" }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(new SymbolImageOptions { IconMediaType = "image/svg+xml" }.IconMediaType).IsEqualTo("image/svg+xml");
    }

    /// <summary>The explicit defaults are the one-bit image, byte for byte, so nothing written today changes.</summary>
    [Test]
    public async Task UnstyledOptions_WriteTheOneBitImage()
    {
        var qr = QRCodeGenerator.Create("unstyled", QREccLevel.M);
        var options = new SymbolImageOptions { ModuleStyle = ModuleStyle.Square, ModuleScalePercent = 100, FinderPatternStyle = FinderPatternStyle.Square, CornerRadiusPercent = 45 };

        await Assert.That(SymbolImageWriter.GetPngBytes(qr, options)).IsEquivalentTo(SymbolImageWriter.GetPngBytes(qr), CollectionOrdering.Matching);
        await Assert.That(SymbolImageWriter.GetSvgString(qr, options)).IsEqualTo(SymbolImageWriter.GetSvgString(qr));
        await Assert.That(SymbolImageWriter.GetPngBytes(qr)[24]).IsEqualTo((byte)1).Because("bit depth");
    }

    // ---- every format ---------------------------------------------------------------------

    public static IEnumerable<(ModuleStyle Style, int Scale, FinderPatternStyle Finder)> Styles() =>
    [
        (ModuleStyle.Circle, 100, FinderPatternStyle.Square),
        (ModuleStyle.RoundedSquare, 92, FinderPatternStyle.Square),
        (ModuleStyle.Square, 75, FinderPatternStyle.Square),
        (ModuleStyle.Square, 100, FinderPatternStyle.Circle),
        (ModuleStyle.Circle, 85, FinderPatternStyle.RoundedSquare),
        (ModuleStyle.RoundedSquare, 90, FinderPatternStyle.RoundedSquareCircle),
    ];

    [Test]
    [MethodDataSource(nameof(Styles))]
    public async Task StyledOutput_DiffersFromPlain_InEveryFormat(ModuleStyle style, int scale, FinderPatternStyle finder)
    {
        var qr = QRCodeGenerator.Create("styled", QREccLevel.M);
        var styled = new SymbolImageOptions { ModuleStyle = style, ModuleScalePercent = scale, FinderPatternStyle = finder };

        await Assert.That(SymbolImageWriter.GetPngBytes(qr, styled)).IsNotEquivalentTo(SymbolImageWriter.GetPngBytes(qr));
        await Assert.That(SymbolImageWriter.GetSvgString(qr, styled)).IsNotEqualTo(SymbolImageWriter.GetSvgString(qr));
        await Assert.That(Pixels(qr, styled, PixelLayout.Gray8)).IsNotEquivalentTo(Pixels(qr, default, PixelLayout.Gray8));
    }

    /// <summary>
    /// The PNG decodes to the pixel buffer's bytes, read without premultiplying so the palette comes back as written, for an opaque light
    /// colour, a light colour with an alpha, and a dark colour with an alpha over each.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Styles))]
    public async Task Png_AndPixels_Agree(ModuleStyle style, int scale, FinderPatternStyle finder)
    {
        var qr = QRCodeGenerator.Create("agree", QREccLevel.M);
        var rm = RmQRCodeGenerator.Create("agree", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R13x77 });
        foreach (var dark in new[] { Navy, new SymbolColor(0x1E, 0x3A, 0x8A, 128) })
        {
            foreach (var light in new[] { Ivory, new SymbolColor(0xFF, 0xFF, 0xF0, 51) })
            {
                var options = new SymbolImageOptions { ModulePixelSize = 10, ModuleStyle = style, ModuleScalePercent = scale, FinderPatternStyle = finder, DarkColor = dark, LightColor = light };

                await Assert.That(DecodePng(SymbolImageWriter.GetPngBytes(qr, options))).IsEquivalentTo(Pixels(qr, options, PixelLayout.Bgra8888), CollectionOrdering.Matching);
                await Assert.That(DecodePng(SymbolImageWriter.GetPngBytes(rm, options))).IsEquivalentTo(Pixels(rm, options, PixelLayout.Bgra8888), CollectionOrdering.Matching);
            }
        }
    }

    /// <summary>A dark colour with an alpha over an opaque light colour blends to opaque levels, so the PNG carries no transparency.</summary>
    [Test]
    public async Task DarkAlpha_OverAnOpaqueLight_NeedsNoTransparency()
    {
        var qr = QRCodeGenerator.Create("alpha", QREccLevel.M);
        var png = SymbolImageWriter.GetPngBytes(qr, new SymbolImageOptions { ModuleStyle = ModuleStyle.Circle, DarkColor = new SymbolColor(0, 0, 0, 128), LightColor = Ivory });
        await Assert.That(ChunkTypes(png)).IsEquivalentTo(new[] { "IHDR", "PLTE", "IDAT", "IEND" }, CollectionOrdering.Matching);
    }

    /// <summary>Where the canvas is taller than the symbol, the rows above and below it are the light colour in a styled image too.</summary>
    [Test]
    public async Task Styled_AtASetSizeWithAPad_LeavesThePadLight()
    {
        var rm = RmQRCodeGenerator.Create("pad", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 });
        var options = new SymbolImageOptions { Width = 300, Height = 300, ModuleStyle = ModuleStyle.Circle, FinderPatternStyle = FinderPatternStyle.Circle };
        var gray = Pixels(rm, options, PixelLayout.Gray8);
        // The plain image is written without the styled rows, so its light rows say where the pad and the quiet zone are.
        var plain = Pixels(rm, new SymbolImageOptions { Width = 300, Height = 300 }, PixelLayout.Gray8);
        var lightRows = 0;
        for (var y = 0; y < 300; y++)
        {
            var light = gray.AsSpan(y * 300, 300).IndexOfAnyExcept((byte)255) < 0;
            var plainLight = plain.AsSpan(y * 300, 300).IndexOfAnyExcept((byte)255) < 0;
            await Assert.That(light).IsEqualTo(plainLight).Because($"row {y}");
            if (light)
                lightRows++;
        }
        await Assert.That(lightRows).IsGreaterThan(250).Because("the symbol is 21 modules of the 300 rows");
    }

    /// <summary>
    /// A corner radius past half the side rounds the square into a circle, as the writers clamp it, so the shape stays symmetric and at
    /// 100 percent equals the circle style.
    /// </summary>
    [Test]
    [Arguments(60)]
    [Arguments(75)]
    [Arguments(100)]
    public async Task CornerRadius_PastHalfTheSide_StaysACircle(int radius)
    {
        var qr = QRCodeGenerator.Create("corners", QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
        var rounded = new SymbolImageOptions { ModulePixelSize = 32, ModuleStyle = ModuleStyle.RoundedSquare, CornerRadiusPercent = radius, FinderPatternStyle = FinderPatternStyle.RoundedSquare };
        var gray = Pixels(qr, rounded, PixelLayout.Gray8);
        var width = qr.Size * 32;
        var (row, column) = FirstDarkModuleOutsideTheFinders(qr);

        var cell = Block(gray, width, column * 32, row * 32, 32, 32);
        await Assert.That(cell).IsEquivalentTo(MirroredLeftRight(cell, 32), CollectionOrdering.Matching).Because("a rounded square is symmetric");
        await Assert.That(cell).IsEquivalentTo(MirroredTopBottom(cell, 32), CollectionOrdering.Matching);
        var finder = Block(gray, width, 0, 0, 7 * 32, 7 * 32);
        await Assert.That(finder).IsEquivalentTo(MirroredLeftRight(finder, 7 * 32), CollectionOrdering.Matching).Because("a rounded finder pattern is symmetric");
        await Assert.That(finder).IsEquivalentTo(MirroredTopBottom(finder, 7 * 32), CollectionOrdering.Matching);

        if (radius == 100)
        {
            var circle = Pixels(qr, rounded with { ModuleStyle = ModuleStyle.Circle, FinderPatternStyle = FinderPatternStyle.Square }, PixelLayout.Gray8);
            await Assert.That(Block(circle, width, column * 32, row * 32, 32, 32)).IsEquivalentTo(cell, CollectionOrdering.Matching).Because("a square rounded by half its side is a circle");
        }
    }

    /// <summary>
    /// A cell or a finder box past the stamps' size is sampled row by row instead of kept, and the rows come out as the kept stamp's would.
    /// A version 1 symbol at 2,100 pixels a module has cells of 4.4 million pixels and a finder box of 216 million, both past the cap,
    /// which the test holds so that a raised cap cannot turn it into a test of the kept path.
    /// </summary>
    [Test]
    public async Task HugeCells_AreSampledRowByRow_AsTheKeptStampWouldBe()
    {
        var qr = QRCodeGenerator.Create("huge", QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
        var options = new SymbolImageOptions { ModulePixelSize = 2100, ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 90, FinderPatternStyle = FinderPatternStyle.RoundedSquareCircle };
        var cellPixels = (long)options.ModulePixelSize * options.ModulePixelSize;
        await Assert.That(cellPixels).IsGreaterThan(FeatherQR.Internals.ImageWriters.StyledRaster.MaxKeptStampPixels).Because("a cell is past the cap");
        await Assert.That(cellPixels * 49).IsGreaterThan(FeatherQR.Internals.ImageWriters.StyledRaster.MaxKeptStampPixels).Because("a finder box is past the cap");
        var view = new FeatherQR.Internals.StandardQrMatrixView(qr);
        var layout = FeatherQR.Internals.ImageWriters.ImageLayout.Create(in view, in options);
        var icon = FeatherQR.Internals.ImageWriters.IconArea.Get(in view, in options);
        var raster = FeatherQR.Internals.ImageWriters.StyledRaster.Take();
        try
        {
            raster.Prepare(in view, in layout, in options, in icon, 16);
            var (row, column) = FirstDarkModuleOutsideTheFinders(qr);
            const int subRow = 1234;
            var levels = new byte[qr.Size * 2100];
            raster.ComposeRow(in view, row * 2100 + subRow, levels);

            var stamp = new byte[2100 * 2100];
            FeatherQR.Internals.ImageWriters.Stamp.Module(stamp, 2100, 2100, in options, 16);
            await Assert.That(levels.AsSpan(column * 2100, 2100).ToArray()).IsEquivalentTo(stamp.AsSpan(subRow * 2100, 2100).ToArray(), CollectionOrdering.Matching);

            var finderRow = new byte[7 * 2100];
            FeatherQR.Internals.ImageWriters.Stamp.FinderRow(finderRow, 7 * 2100, 7 * 2100, subRow, in options, 16);
            raster.ComposeRow(in view, subRow, levels);
            await Assert.That(levels.AsSpan(0, 7 * 2100).ToArray()).IsEquivalentTo(finderRow, CollectionOrdering.Matching);
        }
        finally
        {
            FeatherQR.Internals.ImageWriters.StyledRaster.Return(raster);
        }
    }

    /// <summary>
    /// The styled PNG's lookup keys on 8 bytes, the longest key: a key of one module's bytes, 4 at this module size, left this symbol's
    /// file past the bound.
    /// </summary>
    [Test]
    public async Task Png_Styled_KeepsTheMeasuredSize()
    {
        var qr = QRCodeGenerator.Create(new string('a', 150), QREccLevel.M, new QRCodeGeneratorOptions { Version = new QRVersionRange(10, 10) });
        var styled = new SymbolImageOptions { ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 92, FinderPatternStyle = FinderPatternStyle.Circle };

        var png = SymbolImageWriter.GetPngBytes(qr, styled);

        await Assert.That(png.Length).IsLessThanOrEqualTo(9_200);
    }

    [Test]
    public async Task Png_IsFourBits_GreyscaleForBlackOnWhite_AndAPaletteOtherwise()
    {
        var qr = QRCodeGenerator.Create("depth", QREccLevel.M);
        var styled = new SymbolImageOptions { ModuleStyle = ModuleStyle.Circle };

        var grey = SymbolImageWriter.GetPngBytes(qr, styled);
        await Assert.That(grey[24]).IsEqualTo((byte)4).Because("bit depth");
        await Assert.That(grey[25]).IsEqualTo((byte)0).Because("colour type greyscale");
        await Assert.That(ChunkTypes(grey)).IsEquivalentTo(new[] { "IHDR", "IDAT", "IEND" }, CollectionOrdering.Matching);

        var coloured = SymbolImageWriter.GetPngBytes(qr, styled with { DarkColor = Navy, LightColor = Ivory });
        await Assert.That(coloured[24]).IsEqualTo((byte)4);
        await Assert.That(coloured[25]).IsEqualTo((byte)3).Because("colour type indexed");
        await Assert.That(ChunkTypes(coloured)).IsEquivalentTo(new[] { "IHDR", "PLTE", "IDAT", "IEND" }, CollectionOrdering.Matching);
        await Assert.That(ChunkLength(coloured, "PLTE")).IsEqualTo(48).Because("sixteen levels of three bytes");

        var translucent = SymbolImageWriter.GetPngBytes(qr, styled with { LightColor = new SymbolColor(255, 255, 255, 0) });
        await Assert.That(ChunkTypes(translucent)).IsEquivalentTo(new[] { "IHDR", "PLTE", "tRNS", "IDAT", "IEND" }, CollectionOrdering.Matching);
        await Assert.That(ChunkLength(translucent, "tRNS")).IsEqualTo(16);
    }

    /// <summary>Every pixel is one of the levels between the two colours, the light colour at the first and the dark at the last.</summary>
    [Test]
    public async Task Pixels_AreLevelsBetweenTheTwoColors()
    {
        var qr = QRCodeGenerator.Create("levels", QREccLevel.M);
        var options = new SymbolImageOptions { ModulePixelSize = 16, ModuleStyle = ModuleStyle.Circle, DarkColor = Navy, LightColor = Ivory };
        var pixels = Pixels(qr, options, PixelLayout.Rgba8888);
        var distinct = Enumerable.Range(0, pixels.Length / 4).Select(p => BinaryPrimitives.ReadUInt32LittleEndian(pixels.AsSpan(p * 4))).Distinct().ToArray();

        await Assert.That(distinct.Length).IsLessThanOrEqualTo(16);
        await Assert.That(distinct.Length).IsGreaterThan(2).Because("a circle's edge has levels between the colours");
        await Assert.That(distinct).Contains(BinaryPrimitives.ReadUInt32LittleEndian(new byte[] { 0x1E, 0x3A, 0x8A, 255 }));
        await Assert.That(distinct).Contains(BinaryPrimitives.ReadUInt32LittleEndian(new byte[] { 0xFF, 0xFF, 0xF0, 255 }));
    }

    /// <summary>A module's centre is the dark colour and its cell's corner the light one for a circle, and for a square scaled down.</summary>
    [Test]
    [Arguments(ModuleStyle.Circle, 100)]
    [Arguments(ModuleStyle.RoundedSquare, 100)]
    [Arguments(ModuleStyle.Square, 50)]
    public async Task ModuleCentre_IsDark_AndItsCellsCorner_IsLight(ModuleStyle style, int scale)
    {
        var qr = QRCodeGenerator.Create("centre", QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
        var options = new SymbolImageOptions { ModulePixelSize = 16, ModuleStyle = style, ModuleScalePercent = scale };
        var gray = Pixels(qr, options, PixelLayout.Gray8);
        var width = qr.Size * 16;

        // The scan starts at row 8, below the top finder patterns, and stops 8 columns short of the right edge, so nothing it returns is in a finder box.
        var (row, column) = FirstDarkModuleOutsideTheFinders(qr);
        var cellX = (4 + column) * 16;
        var cellY = (4 + row) * 16;
        await Assert.That(gray[(cellY + 8) * width + cellX + 8]).IsEqualTo((byte)0).Because("the centre");
        await Assert.That(gray[cellY * width + cellX]).IsEqualTo((byte)255).Because("the corner");
        if (style == ModuleStyle.Square)
            await Assert.That(gray[(cellY + 8) * width + cellX + 2]).IsEqualTo((byte)255).Because("a square at half size leaves a quarter of the cell on each side");
    }

    // ---- the finder patterns ----------------------------------------------------------------

    /// <summary>
    /// With circle modules at half size, the finder patterns are the plain image's pixel for pixel, on all three symbologies: a module style
    /// that reached a finder pattern would split the run a reader scans for.
    /// </summary>
    [Test]
    [Arguments("qr")]
    [Arguments("microqr")]
    [Arguments("rmqr")]
    public async Task FinderPatterns_NeverTakeTheModuleStyle(string symbology)
    {
        var data = Symbol(symbology);
        var styled = new SymbolImageOptions { ModulePixelSize = 12, ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 50 };
        var plain = new SymbolImageOptions { ModulePixelSize = 12 };
        var (columns, rows, quietZone, finders) = Geometry(data);
        var width = columns * 12;

        var styledGray = Pixels(data, styled, PixelLayout.Gray8);
        var plainGray = Pixels(data, plain, PixelLayout.Gray8);
        await Assert.That(styledGray).IsNotEquivalentTo(plainGray).Because("the data modules are styled");

        foreach (var (finderRow, finderColumn) in finders)
        {
            for (var y = 0; y < 7 * 12; y++)
            {
                var at = ((quietZone + finderRow) * 12 + y) * width + (quietZone + finderColumn) * 12;
                await Assert.That(styledGray.AsSpan(at, 7 * 12).SequenceEqual(plainGray.AsSpan(at, 7 * 12))).IsTrue()
                    .Because($"{symbology}: finder at ({finderRow}, {finderColumn}), pixel row {y}");
            }
        }
    }

    /// <summary>A finder pattern style changes the finder patterns and nothing else, and keeps the pattern a reader scans for: dark edges and centre, a light ring.</summary>
    [Test]
    [Arguments("qr", FinderPatternStyle.Circle)]
    [Arguments("qr", FinderPatternStyle.RoundedSquare)]
    [Arguments("qr", FinderPatternStyle.RoundedSquareCircle)]
    [Arguments("microqr", FinderPatternStyle.Circle)]
    [Arguments("rmqr", FinderPatternStyle.RoundedSquareCircle)]
    public async Task FinderPatternStyle_ChangesTheFinderPatternsAlone(string symbology, FinderPatternStyle finder)
    {
        var data = Symbol(symbology);
        var styled = new SymbolImageOptions { ModulePixelSize = 24, FinderPatternStyle = finder };
        var plain = new SymbolImageOptions { ModulePixelSize = 24 };
        var (columns, rows, quietZone, finders) = Geometry(data);
        var width = columns * 24;
        var styledGray = Pixels(data, styled, PixelLayout.Gray8);
        var plainGray = Pixels(data, plain, PixelLayout.Gray8);

        var differ = 0;
        for (var y = 0; y < rows * 24; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (styledGray[y * width + x] == plainGray[y * width + x])
                    continue;
                var inside = finders.Any(f => x >= (quietZone + f.Column) * 24 && x < (quietZone + f.Column + 7) * 24 && y >= (quietZone + f.Row) * 24 && y < (quietZone + f.Row + 7) * 24);
                await Assert.That(inside).IsTrue().Because($"{symbology} {finder}: pixel ({x}, {y}) differs outside every finder pattern");
                differ++;
            }
        }
        await Assert.That(differ).IsGreaterThan(0).Because("the style reaches the finder patterns");

        foreach (var (finderRow, finderColumn) in finders)
        {
            byte At(double column, double row) => styledGray[(int)(((quietZone + finderRow) * 24) + row * 24) * width + (int)((quietZone + finderColumn) * 24 + column * 24)];
            foreach (var (column, row) in new[] { (3.5, 3.5), (3.5, 0.5), (0.5, 3.5), (6.5, 3.5), (3.5, 6.5) })
                await Assert.That(At(column, row)).IsLessThan((byte)64).Because($"{symbology} {finder}: module ({column}, {row}) of the finder is dark");
            foreach (var (column, row) in new[] { (1.5, 3.5), (3.5, 1.5), (5.5, 3.5), (3.5, 5.5) })
                await Assert.That(At(column, row)).IsGreaterThan((byte)191).Because($"{symbology} {finder}: module ({column}, {row}) is the light ring");
        }
    }

    // ---- the SVG ----------------------------------------------------------------------------

    [Test]
    public async Task Svg_StyledModules_UseOneShape_AndTheSquareFinders_StayRuns()
    {
        var qr = QRCodeGenerator.Create("svg", QREccLevel.M);
        var options = new SymbolImageOptions { ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 92, DarkColor = Navy };
        var root = XDocument.Parse(SymbolImageWriter.GetSvgString(qr, options)).Root!;
        var (columns, rows, quietZone, finders) = Geometry(qr);

        await Assert.That(root.Attribute("shape-rendering")).IsNull().Because("a curve is not drawn crisp");
        var group = root.Element(Svg + "g")!;
        await Assert.That(group.Attribute("fill")?.Value).IsEqualTo("#1e3a8a");

        var shape = group.Element(Svg + "defs")!.Element(Svg + "circle")!;
        await Assert.That(shape.Attribute("id")?.Value).IsEqualTo("m");
        await Assert.That(shape.Attribute("r")?.Value).IsEqualTo("0.46");

        var useElements = group.Elements(Svg + "use").ToArray();
        var uses = useElements.Select(u => (Row: int.Parse(u.Attribute("y")!.Value) - quietZone, Column: int.Parse(u.Attribute("x")!.Value) - quietZone)).ToHashSet();
        await Assert.That(useElements.All(x => x.Attribute("href")?.Value == "#m")).IsTrue();
        await Assert.That(uses.Count).IsEqualTo(useElements.Length).Because("one use a module, none twice");
        await Assert.That(uses).IsEquivalentTo(DarkModules(qr, finder: false), CollectionOrdering.Any).Because("one use at each dark module outside the finder patterns");

        var runs = PathModules(group.Element(Svg + "path")!.Attribute("d")!.Value, quietZone);
        await Assert.That(runs).IsEquivalentTo(DarkModules(qr, finder: true), CollectionOrdering.Any).Because("the finder patterns are the stroked runs of the plain document");
    }

    [Test]
    public async Task Svg_SquareModulesWithAStyledFinder_KeepTheRunPath_WithoutTheFinderPatterns()
    {
        var qr = QRCodeGenerator.Create("svg", QREccLevel.M);
        var options = new SymbolImageOptions { FinderPatternStyle = FinderPatternStyle.RoundedSquare, CornerRadiusPercent = 20 };
        var root = XDocument.Parse(SymbolImageWriter.GetSvgString(qr, options)).Root!;
        var (columns, rows, quietZone, finders) = Geometry(qr);
        var group = root.Element(Svg + "g")!;

        await Assert.That(group.Elements(Svg + "use").Count()).IsEqualTo(0);
        var paths = group.Elements(Svg + "path").ToArray();
        var runs = paths.Single(p => p.Attribute("fill-rule") is null);
        await Assert.That(PathModules(runs.Attribute("d")!.Value, quietZone)).IsEquivalentTo(DarkModules(qr, finder: false), CollectionOrdering.Any);
        await Assert.That(paths.Count(p => p.Attribute("fill-rule")?.Value == "evenodd")).IsEqualTo(3).Because("one even-odd path a finder pattern");
        // The outer square's corner radius: 20 percent of its 7 modules.
        await Assert.That(paths.First(p => p.Attribute("fill-rule") is not null).Attribute("d")!.Value).Contains("a1.4 1.4 0 0 1 1.4 1.4");
    }

    /// <summary>
    /// The masked runs on every symbology and with an icon area: the styled document's run path is the plain document's runs less the
    /// finder box and the icon area. Each case holds that there was something to clear.
    /// </summary>
    [Test]
    [Arguments("qr", 0)]
    [Arguments("qr", 5)]
    [Arguments("microqr", 0)]
    [Arguments("rmqr", 0)]
    public async Task Svg_SquareModulesWithAStyledFinder_MaskTheFinderBoxesAndTheIconArea(string symbology, int iconSize)
    {
        var data = Symbol(symbology);
        var (_, _, quietZone, finders) = Geometry(data);
        var options = new SymbolImageOptions { FinderPatternStyle = FinderPatternStyle.Circle, IconSizeModules = iconSize, MaxCoreOccupancyPercent = 50 };
        var plain = PathModules(RunPath(data, default).Attribute("d")!.Value, quietZone);
        var masked = PathModules(RunPath(data, options).Attribute("d")!.Value, quietZone);

        var expected = plain.Where(m => !finders.Any(f => (uint)(m.Row - f.Row) < 7 && (uint)(m.Column - f.Column) < 7)).ToHashSet();
        await Assert.That(expected.Count).IsLessThan(plain.Count).Because("the finder box holds dark modules to clear");
        if (iconSize != 0)
        {
            var view = new FeatherQR.Internals.StandardQrMatrixView((QRCodeData)data);
            var icon = FeatherQR.Internals.ImageWriters.IconArea.Get(in view, in options);
            await Assert.That(expected.Count(m => icon.Covers(m.Row, m.Column))).IsGreaterThan(0).Because("the icon area holds dark modules to clear");
            expected.RemoveWhere(m => icon.Covers(m.Row, m.Column));
        }
        await Assert.That(masked).IsEquivalentTo(expected, CollectionOrdering.Any);
    }

    private static XElement RunPath(object data, SymbolImageOptions options)
    {
        var svg = data switch
        {
            QRCodeData qr => SymbolImageWriter.GetSvgString(qr, options),
            MicroQRCodeData micro => SymbolImageWriter.GetSvgString(micro, options),
            RmQRCodeData rm => SymbolImageWriter.GetSvgString(rm, options),
            _ => throw new ArgumentException(nameof(data)),
        };
        return XDocument.Parse(svg).Root!.Descendants(Svg + "path").Single(p => p.Attribute("fill-rule") is null);
    }

    [Test]
    public async Task Svg_FinderStyles_WriteTheirElements()
    {
        var qr = QRCodeGenerator.Create("svg", QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 });
        var group = XDocument.Parse(SymbolImageWriter.GetSvgString(qr, new SymbolImageOptions { FinderPatternStyle = FinderPatternStyle.Circle, DarkColor = Navy })).Root!.Element(Svg + "g")!;
        var circles = group.Elements(Svg + "circle").ToArray();
        await Assert.That(circles.Length).IsEqualTo(6).Because("a stroked ring and a disc a finder pattern");
        var ring = circles[0];
        await Assert.That(ring.Attribute("r")?.Value).IsEqualTo("3");
        await Assert.That(ring.Attribute("fill")?.Value).IsEqualTo("none");
        await Assert.That(ring.Attribute("stroke")?.Value).IsEqualTo("#1e3a8a");
        await Assert.That(ring.Attribute("cx")?.Value).IsEqualTo("7.5").Because("the top-left finder's centre, quiet zone included");
        await Assert.That(circles[1].Attribute("r")?.Value).IsEqualTo("1.5");

        group = XDocument.Parse(SymbolImageWriter.GetSvgString(qr, new SymbolImageOptions { FinderPatternStyle = FinderPatternStyle.RoundedSquareCircle })).Root!.Element(Svg + "g")!;
        await Assert.That(group.Elements(Svg + "path").Count(p => p.Attribute("fill-rule")?.Value == "evenodd")).IsEqualTo(3);
        await Assert.That(group.Elements(Svg + "circle").Count()).IsEqualTo(3);
    }

    [Test]
    public async Task Svg_KeepsCrispEdges_ForSquares_AndDropsIt_ForCurves()
    {
        var qr = QRCodeGenerator.Create("crisp", QREccLevel.M);
        await Assert.That(XDocument.Parse(SymbolImageWriter.GetSvgString(qr, new SymbolImageOptions { ModuleScalePercent = 75 })).Root!.Attribute("shape-rendering")?.Value).IsEqualTo("crispEdges");
        await Assert.That(XDocument.Parse(SymbolImageWriter.GetSvgString(qr, new SymbolImageOptions { ModuleStyle = ModuleStyle.RoundedSquare })).Root!.Attribute("shape-rendering")).IsNull();
        await Assert.That(XDocument.Parse(SymbolImageWriter.GetSvgString(qr, new SymbolImageOptions { FinderPatternStyle = FinderPatternStyle.Circle })).Root!.Attribute("shape-rendering")).IsNull();
    }

    [Test]
    public async Task Svg_ScaledSquare_IsAnInsetRect_AndARoundedOne_HasARadius()
    {
        var qr = QRCodeGenerator.Create("svg", QREccLevel.M);
        var square = XDocument.Parse(SymbolImageWriter.GetSvgString(qr, new SymbolImageOptions { ModuleScalePercent = 80 })).Root!.Descendants(Svg + "rect").Single(r => r.Attribute("id")?.Value == "m");
        await Assert.That(square.Attribute("x")?.Value).IsEqualTo("0.1");
        await Assert.That(square.Attribute("width")?.Value).IsEqualTo("0.8");
        await Assert.That(square.Attribute("rx")).IsNull();

        // Below the clamp the radius follows the option: 30 percent of the 0.9 side.
        var rounded = XDocument.Parse(SymbolImageWriter.GetSvgString(qr, new SymbolImageOptions { ModuleStyle = ModuleStyle.RoundedSquare, ModuleScalePercent = 90, CornerRadiusPercent = 30 })).Root!.Descendants(Svg + "rect").Single(r => r.Attribute("id")?.Value == "m");
        await Assert.That(rounded.Attribute("rx")?.Value).IsEqualTo("0.27");

        var half = XDocument.Parse(SymbolImageWriter.GetSvgString(qr, new SymbolImageOptions { ModuleStyle = ModuleStyle.RoundedSquare, ModuleScalePercent = 90, CornerRadiusPercent = 50 })).Root!.Descendants(Svg + "rect").Single(r => r.Attribute("id")?.Value == "m");
        await Assert.That(half.Attribute("rx")?.Value).IsEqualTo("0.45");

        // Past half the side the writer stops at the circle, as the raster does, instead of leaving a renderer to clamp the radius.
        var past = XDocument.Parse(SymbolImageWriter.GetSvgString(qr, new SymbolImageOptions { ModuleStyle = ModuleStyle.RoundedSquare, ModuleScalePercent = 90, CornerRadiusPercent = 80 })).Root!.Descendants(Svg + "rect").Single(r => r.Attribute("id")?.Value == "m");
        await Assert.That(past.Attribute("rx")?.Value).IsEqualTo("0.45");
    }

    [Test]
    public async Task WriteSvg_AndWritePng_Styled_WriteTheStringAndTheArray()
    {
        var micro = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L);
        var options = new SymbolImageOptions { ModuleStyle = ModuleStyle.Circle, FinderPatternStyle = FinderPatternStyle.Circle, DarkColor = Navy, LightColor = Ivory };
        var writer = new ArrayBufferWriter<byte>();

        var svgLength = SymbolImageWriter.WriteSvg(micro, writer, options);
        await Assert.That(writer.WrittenSpan.ToArray()).IsEquivalentTo(System.Text.Encoding.UTF8.GetBytes(SymbolImageWriter.GetSvgString(micro, options)), CollectionOrdering.Matching);
        await Assert.That(svgLength).IsEqualTo(writer.WrittenCount);

        writer.Clear();
        var pngLength = SymbolImageWriter.WritePng(micro, writer, options);
        await Assert.That(writer.WrittenSpan.ToArray()).IsEquivalentTo(SymbolImageWriter.GetPngBytes(micro, options), CollectionOrdering.Matching);
        await Assert.That(pngLength).IsEqualTo(writer.WrittenCount);
    }

    /// <summary>At a set size the cells come in two sizes, each with a stamp of its own, and the PNG and the pixels still agree.</summary>
    [Test]
    public async Task AtASetSize_StyledPngAndPixels_Agree()
    {
        var qr = QRCodeGenerator.Create("set size", QREccLevel.M, new QRCodeGeneratorOptions { Version = 5 });
        var options = new SymbolImageOptions { Width = 500, Height = 400, ModuleStyle = ModuleStyle.RoundedSquare, ModuleScalePercent = 88, FinderPatternStyle = FinderPatternStyle.Circle, DarkColor = Navy, LightColor = Ivory };

        var png = SymbolImageWriter.GetPngBytes(qr, options);
        await Assert.That(DecodePng(png)).IsEquivalentTo(Pixels(qr, options, PixelLayout.Bgra8888), CollectionOrdering.Matching);
        await Assert.That((BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)))).IsEqualTo((500, 400));
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static object Symbol(string symbology) => symbology switch
    {
        "microqr" => MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { QuietZoneSize = 2 }),
        "rmqr" => RmQRCodeGenerator.Create("finder", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x59, QuietZoneSize = 2 }),
        _ => QRCodeGenerator.Create("finder", QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 }),
    };

    private static (int Columns, int Rows, int QuietZone, (int Row, int Column)[] Finders) Geometry(object data) => data switch
    {
        QRCodeData qr => (qr.Size, qr.Size, (qr.Size - qr.GetCoreSize()) / 2, [(0, 0), (0, qr.GetCoreSize() - 7), (qr.GetCoreSize() - 7, 0)]),
        MicroQRCodeData micro => (micro.Size, micro.Size, (micro.Size - micro.GetCoreSize()) / 2, [(0, 0)]),
        RmQRCodeData rm => (rm.Width, rm.Height, (rm.Width - rm.GetCoreWidth()) / 2, [(0, 0)]),
        _ => throw new ArgumentException(nameof(data)),
    };

    private static byte[] Pixels(object data, SymbolImageOptions options, PixelLayout layout)
    {
        byte[] buffer;
        bool written;
        switch (data)
        {
            case QRCodeData qr:
                buffer = new byte[SymbolImageWriter.GetPixelBufferSize(qr, layout, options)];
                written = SymbolImageWriter.TryWritePixels(qr, buffer, layout, out _, out _, options);
                break;
            case MicroQRCodeData micro:
                buffer = new byte[SymbolImageWriter.GetPixelBufferSize(micro, layout, options)];
                written = SymbolImageWriter.TryWritePixels(micro, buffer, layout, out _, out _, options);
                break;
            case RmQRCodeData rm:
                buffer = new byte[SymbolImageWriter.GetPixelBufferSize(rm, layout, options)];
                written = SymbolImageWriter.TryWritePixels(rm, buffer, layout, out _, out _, options);
                break;
            default:
                throw new ArgumentException(nameof(data));
        }
        if (!written)
            throw new InvalidOperationException("the buffer was sized by the writer");
        return buffer;
    }

    private static byte[] DecodePng(byte[] png)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(png));
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = SKBitmap.Decode(codec, info);
        return bitmap.GetPixelSpan().ToArray();
    }

    private static byte[] Block(byte[] gray, int width, int left, int top, int blockWidth, int blockHeight)
    {
        var block = new byte[blockWidth * blockHeight];
        for (var y = 0; y < blockHeight; y++)
            gray.AsSpan((top + y) * width + left, blockWidth).CopyTo(block.AsSpan(y * blockWidth));
        return block;
    }

    private static byte[] MirroredLeftRight(byte[] block, int width)
    {
        var mirrored = (byte[])block.Clone();
        for (var y = 0; y < block.Length / width; y++)
            Array.Reverse(mirrored, y * width, width);
        return mirrored;
    }

    private static byte[] MirroredTopBottom(byte[] block, int width)
    {
        var height = block.Length / width;
        var mirrored = new byte[block.Length];
        for (var y = 0; y < height; y++)
            block.AsSpan(y * width, width).CopyTo(mirrored.AsSpan((height - 1 - y) * width));
        return mirrored;
    }

    private static (int Row, int Column) FirstDarkModuleOutsideTheFinders(QRCodeData qr)
    {
        var core = qr.GetCoreSize();
        for (var row = 8; row < core; row++)
            for (var column = 8; column < core - 8; column++)
                if (qr.GetCoreModule(row, column) && !qr.IsFinderPattern(row, column))
                    return (row, column);
        throw new InvalidOperationException("no dark module outside the finder patterns");
    }

    private static HashSet<(int Row, int Column)> DarkModules(QRCodeData qr, bool finder)
    {
        var core = qr.GetCoreSize();
        var modules = new HashSet<(int, int)>();
        for (var row = 0; row < core; row++)
            for (var column = 0; column < core; column++)
                if (qr.GetCoreModule(row, column) && qr.IsFinderPattern(row, column) == finder)
                    modules.Add((row, column));
        return modules;
    }

    /// <summary>The modules a stroked run path covers: an absolute move with a half, then relative moves, each followed by a horizontal run.</summary>
    private static HashSet<(int Row, int Column)> PathModules(string d, int quietZone)
    {
        var modules = new HashSet<(int, int)>();
        var tokens = System.Text.RegularExpressions.Regex.Matches(d, @"[Mmh]|-?\d+(\.\d+)?").Select(m => m.Value).ToArray();
        var x = 0;
        var y = 0;
        var i = 0;
        while (i < tokens.Length)
        {
            var command = tokens[i++];
            if (command == "M")
            {
                x = int.Parse(tokens[i++], CultureInfo.InvariantCulture);
                y = (int)double.Parse(tokens[i++], CultureInfo.InvariantCulture);
            }
            else if (command == "m")
            {
                x += int.Parse(tokens[i++], CultureInfo.InvariantCulture);
                y += int.Parse(tokens[i++], CultureInfo.InvariantCulture);
            }
            else
            {
                var length = int.Parse(tokens[i++], CultureInfo.InvariantCulture);
                for (var k = 0; k < length; k++)
                    modules.Add((y - quietZone, x + k - quietZone));
                x += length;
            }
        }
        return modules;
    }

    private static string[] ChunkTypes(byte[] png)
    {
        var types = new List<string>();
        var position = 8;
        while (position + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position));
            types.Add(System.Text.Encoding.ASCII.GetString(png, position + 4, 4));
            position += 12 + length;
        }
        return types.ToArray();
    }

    private static int ChunkLength(byte[] png, string type)
    {
        var position = 8;
        while (position + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position));
            if (System.Text.Encoding.ASCII.GetString(png, position + 4, 4) == type)
                return length;
            position += 12 + length;
        }
        throw new InvalidOperationException($"no {type} chunk");
    }
}
