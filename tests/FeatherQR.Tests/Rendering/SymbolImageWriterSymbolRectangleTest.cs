namespace FeatherQR.Tests;

/// <summary>
/// Where the symbol lands in its image, quiet zone excluded: the dark pixels' bounds on every symbology, with and without a set size,
/// the values worked out by hand, the corners the decoders report, and the module size the remarks give.
/// </summary>
public class SymbolImageWriterSymbolRectangleTest
{
    public static IEnumerable<(string Symbol, string Options)> SymbolsAndOptions()
    {
        foreach (var symbol in new[] { "QR3", "QR40", "QR5 q0", "QR2 q1", "M1", "M4", "M3 q0", "R7x43", "R17x139", "R11x77 q0" })
        {
            foreach (var options in new[] { "default", "module 1", "module 3", "fraction", "fraction wider", "fraction taller", "width only", "height only", "whole pixels padded", "fill", "fill wider", "fill width only" })
                yield return (symbol, options);
        }
    }

    /// <summary>
    /// The rectangle is the bounds of the dark pixels the writer draws. That holds because the core's first and last rows and columns each
    /// hold a dark module, which the test checks first: the finder patterns on Standard QR, the finder pattern and the timing patterns, which end
    /// dark on an odd side, on Micro QR, and the finder and sub-finder patterns on rMQR. The writer's pixels are held to the rendering package's,
    /// so this cannot pass with a query and a writer wrong together.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(SymbolsAndOptions))]
    public async Task SymbolRectangle_IsTheDarkPixelsBounds(string symbolName, string optionsName)
    {
        var symbol = Symbol(symbolName);
        var options = Options(optionsName, symbol);
        await Assert.That(CoreEdgesHoldDarkModules(symbol)).IsTrue();

        var (width, height) = SizeOf(symbol.Data, options);
        var pixels = new byte[width * height];
        await Assert.That(TryWriteGray8(symbol.Data, pixels, options)).IsTrue();
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (pixels[y * width + x] >= 128)
                    continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x + 1);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y + 1);
            }
        }

        var rectangle = RectangleOf(symbol.Data, options);
        await Assert.That((rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height)).IsEqualTo((left, top, right - left, bottom - top));
    }

    /// <summary>Without a set size the rectangle starts after the quiet zone's modules and is the core's modules, each the module size.</summary>
    [Test]
    public async Task SymbolRectangle_WithoutASize_IsTheCoreTimesTheModuleSize()
    {
        var qr = (QRCodeData)Symbol("QR3").Data;
        var micro = (MicroQRCodeData)Symbol("M4").Data;
        var rm = (RmQRCodeData)Symbol("R17x139").Data;
        var bare = (QRCodeData)Symbol("QR5 q0").Data;

        await Assert.That(Tuple(SymbolImageWriter.GetSymbolRectangle(qr))).IsEqualTo((32, 32, 232, 232));
        await Assert.That(Tuple(SymbolImageWriter.GetSymbolRectangle(micro))).IsEqualTo((16, 16, 136, 136));
        await Assert.That(Tuple(SymbolImageWriter.GetSymbolRectangle(rm))).IsEqualTo((16, 16, 1112, 136));
        await Assert.That(Tuple(SymbolImageWriter.GetSymbolRectangle(rm, new SymbolImageOptions { ModulePixelSize = 3 }))).IsEqualTo((6, 6, 417, 51));
        // With no quiet zone the symbol is the whole image.
        await Assert.That(Tuple(SymbolImageWriter.GetSymbolRectangle(bare))).IsEqualTo((0, 0, 296, 296));
    }

    /// <summary>
    /// At a set size, worked out by hand: each module is the largest whole number of pixels both sides allow, and the spare pixels are
    /// halved, rounded down on the left and above.
    /// </summary>
    [Test]
    public async Task SymbolRectangle_AtASetSize_IsWhereTheWholePixelModulesPutIt()
    {
        var qr = (QRCodeData)Symbol("QR3").Data;
        var micro = (MicroQRCodeData)Symbol("M4").Data;
        var rm = (RmQRCodeData)Symbol("R17x139").Data;

        // 400 pixels hold 37 modules of 10, so 370 pixels centred leave 115 columns on the left and 15 rows above.
        await Assert.That(Tuple(SymbolImageWriter.GetSymbolRectangle(qr, new SymbolImageOptions { Width = 600, Height = 400 }))).IsEqualTo((155, 55, 290, 290));
        // 300 pixels hold 21 modules of 14, so 294 pixels leave 6 columns and 7 rows, 3 of each before the symbol.
        await Assert.That(Tuple(SymbolImageWriter.GetSymbolRectangle(micro, new SymbolImageOptions { Width = 300, Height = 301 }))).IsEqualTo((31, 31, 238, 238));
        // 1,000 pixels hold 143 modules of 6, and the height follows the fit at 147 rows, so 858 by 126 pixels leave 71 columns and 10 rows before.
        await Assert.That(Tuple(SymbolImageWriter.GetSymbolRectangle(rm, new SymbolImageOptions { Width = 1000 }))).IsEqualTo((83, 22, 834, 102));
    }

    /// <summary>
    /// With <c>FillCanvas</c>, worked out by hand: module k starts at the first pixel whose centre is at or past k times the module's size,
    /// after the pixels that centre the symbol.
    /// </summary>
    [Test]
    public async Task SymbolRectangle_FillingTheCanvas_IsWhereTheFitPutsIt()
    {
        var qr = (QRCodeData)Symbol("QR3").Data;
        var micro = (MicroQRCodeData)Symbol("M4").Data;
        var rm = (RmQRCodeData)Symbol("R17x139").Data;

        // 37 modules over 400 pixels is 10.81 a module, centred 100 pixels in. Modules 4 and 33 start 43 and 357 pixels into the symbol.
        await Assert.That(Tuple(SymbolImageWriter.GetSymbolRectangle(qr, new SymbolImageOptions { Width = 600, Height = 400, FillCanvas = true }))).IsEqualTo((143, 43, 314, 314));
        // 21 modules over 300 pixels is 14.29 a module. The one spare row goes below, since half a pixel of centring rounds down.
        await Assert.That(Tuple(SymbolImageWriter.GetSymbolRectangle(micro, new SymbolImageOptions { Width = 300, Height = 301, FillCanvas = true }))).IsEqualTo((29, 29, 242, 242));
        // 143 modules over 1,000 pixels is 6.99 a module. Modules 2 and 141 start 14 and 986 pixels in, rows 2 and 19 at 14 and 133.
        await Assert.That(Tuple(SymbolImageWriter.GetSymbolRectangle(rm, new SymbolImageOptions { Width = 1000, FillCanvas = true }))).IsEqualTo((14, 14, 972, 119));
    }

    /// <summary>Styles, colours and an icon area change what is drawn, not where, so the rectangle is the plain image's.</summary>
    [Test]
    public async Task SymbolRectangle_DoesNotMoveWithStyleColoursOrIcon()
    {
        var qr = QRCodeGenerator.Create("symbol rectangle", QREccLevel.H, new QRCodeGeneratorOptions { Version = 5 });
        var sized = new SymbolImageOptions { Width = 500, Height = 400 };
        var plain = SymbolImageWriter.GetSymbolRectangle(qr, sized);

        await Assert.That(SymbolImageWriter.GetSymbolRectangle(qr, sized with { ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 80, FinderPatternStyle = FinderPatternStyle.Circle })).IsEqualTo(plain);
        await Assert.That(SymbolImageWriter.GetSymbolRectangle(qr, sized with { DarkColor = new SymbolColor(0, 0, 128), LightColor = new SymbolColor(255, 255, 240, 0) })).IsEqualTo(plain);
        await Assert.That(SymbolImageWriter.GetSymbolRectangle(qr, sized with { IconSizeModules = 7 })).IsEqualTo(plain);
    }

    /// <summary>
    /// The decoders report a symbol's corners with the quiet zone excluded too, so the image read back gives the rectangle's corners,
    /// within the half a module their documentation holds them to at 8 pixels a module or more.
    /// </summary>
    [Test]
    public async Task SymbolRectangle_IsWhereTheDecodersFindTheSymbol()
    {
        var cases = new (SymbolCase Symbol, SymbolImageOptions Options)[]
        {
            (Symbol("QR3"), new SymbolImageOptions { Width = 600, Height = 400 }),
            (Symbol("M4"), new SymbolImageOptions { Width = 300, Height = 301 }),
            (Symbol("R17x139"), new SymbolImageOptions { Width = 1200 }),
        };
        foreach (var (symbol, options) in cases)
        {
            var (width, height) = SizeOf(symbol.Data, options);
            var pixels = new byte[width * height];
            await Assert.That(TryWriteGray8(symbol.Data, pixels, options)).IsTrue();
            var corners = symbol.Data switch
            {
                QRCodeData => QRCodeDecoder.TryDecodeImage(pixels, width, height, out string _, out var info) ? info.Corners : default,
                MicroQRCodeData => MicroQRCodeDecoder.TryDecodeImage(pixels, width, height, out string _, out var info) ? info.Corners : default,
                _ => RmQRCodeDecoder.TryDecodeImage(pixels, width, height, out string _, out var info) ? info.Corners : default,
            };
            await Assert.That(corners.IsEmpty).IsFalse();

            var (x, y, w, h) = RectangleOf(symbol.Data, options);
            var module = Math.Min(width / symbol.Columns, height / symbol.Rows);
            await Assert.That(module).IsGreaterThanOrEqualTo(8);
            var expected = new[] { (x, y), (x + w, y), (x + w, y + h), (x, y + h) };
            var reported = new[] { corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft };
            for (var i = 0; i < 4; i++)
            {
                await Assert.That(Math.Abs((double)reported[i].X - expected[i].Item1)).IsLessThanOrEqualTo(module / 2.0);
                await Assert.That(Math.Abs((double)reported[i].Y - expected[i].Item2)).IsLessThanOrEqualTo(module / 2.0);
            }
        }
    }

    /// <summary>
    /// The remarks give a module's size as the image's width over the modules across, quiet zone included, or its height over the modules
    /// down where that is smaller: every module is that size rounded down, or with <c>FillCanvas</c> rounded down or up. Core row 6 is a
    /// finder's bottom row, the separator, the timing pattern and the separator and finder on the right, so across the rectangle every run
    /// but the first and last is one module. The run count checks that the pixel row taken is in that module row.
    /// </summary>
    [Test]
    [Arguments(0, 0, false)]
    [Arguments(600, 400, false)]
    [Arguments(400, 600, false)]
    [Arguments(777, 0, false)]
    [Arguments(0, 333, false)]
    [Arguments(600, 400, true)]
    [Arguments(400, 600, true)]
    [Arguments(777, 0, true)]
    [Arguments(0, 333, true)]
    public async Task ModuleSize_FromTheImageSize_IsEveryModuleRounded(int canvasWidth, int canvasHeight, bool fillCanvas)
    {
        var symbol = Symbol("QR40");
        var qr = (QRCodeData)symbol.Data;
        var options = new SymbolImageOptions { Width = canvasWidth, Height = canvasHeight, FillCanvas = fillCanvas };
        var (width, height) = SymbolImageWriter.GetImageSize(qr, options);
        var module = Math.Min((double)width / qr.Size, (double)height / qr.Size);
        var pitch = fillCanvas ? module : Math.Floor(module);

        var pixels = new byte[width * height];
        await Assert.That(TryWriteGray8(qr, pixels, options)).IsTrue();
        var (x, y, w, _) = SymbolImageWriter.GetSymbolRectangle(qr, options);
        var core = qr.Size - 2 * symbol.QuietZone;
        var row = (y + (int)(6.5 * pitch)) * width;
        var runs = new List<int>();
        var start = x;
        for (var px = x + 1; px <= x + w; px++)
        {
            if (px == x + w || (pixels[row + px] < 128) != (pixels[row + px - 1] < 128))
            {
                runs.Add(px - start);
                start = px;
            }
        }

        await Assert.That(runs.Count).IsEqualTo(core - 14 + 2);
        foreach (var run in runs.Skip(1).SkipLast(1))
        {
            var rounded = fillCanvas ? run == (int)Math.Floor(module) || run == (int)Math.Ceiling(module) : run == (int)pitch;
            await Assert.That(rounded).IsTrue().Because($"a run of {run} at {module:F3} pixels a module");
        }
        if (fillCanvas)
            await Assert.That(Math.Abs((double)w / core - module)).IsLessThan(1.0 / core);
        else
            await Assert.That(w).IsEqualTo((int)pitch * core);
    }

    // ---- what every image call refuses, refused here too ---------------------------------

    [Test]
    public async Task NullData_Throws()
    {
        await Assert.That(() => SymbolImageWriter.GetSymbolRectangle((QRCodeData)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.GetSymbolRectangle((MicroQRCodeData)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.GetSymbolRectangle((RmQRCodeData)null!)).Throws<ArgumentNullException>();
    }

    /// <summary>Version 40 is 185 modules a side, so 184 pixels give a module under one pixel and 185 give one.</summary>
    [Test]
    public async Task ModuleUnderOnePixel_Throws_AndOnePixelIsPlaced()
    {
        var qr = (QRCodeData)Symbol("QR40").Data;
        var micro = (MicroQRCodeData)Symbol("M4").Data;
        var rm = (RmQRCodeData)Symbol("R17x139").Data;

        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.GetSymbolRectangle(qr, new SymbolImageOptions { Width = 184, Height = 184 }));
        await Assert.That(refused.ParamName).IsEqualTo("options");
        await Assert.That(() => SymbolImageWriter.GetSymbolRectangle(micro, new SymbolImageOptions { Width = 20 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.GetSymbolRectangle(rm, new SymbolImageOptions { Width = 1024, Height = 20 })).Throws<ArgumentOutOfRangeException>();

        await Assert.That(Tuple(SymbolImageWriter.GetSymbolRectangle(qr, new SymbolImageOptions { Width = 185, Height = 185 }))).IsEqualTo((4, 4, 177, 177));
    }

    /// <summary>An icon that the writers refuse is refused here too, as the image's size refuses it.</summary>
    [Test]
    public async Task AnIconTheWritersRefuse_Throws()
    {
        var v1 = QRCodeGenerator.Create("fit", QREccLevel.H, new QRCodeGeneratorOptions { Version = 1 });
        var small = new SymbolImageOptions { IconSizeModules = 1, IconBorderModules = 0 };

        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.GetSymbolRectangle(v1, new SymbolImageOptions { IconSizeModules = 7 }));
        await Assert.That(refused.ParamName).IsEqualTo("options");
        await Assert.That(() => SymbolImageWriter.GetSymbolRectangle((MicroQRCodeData)Symbol("M4").Data, small)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.GetSymbolRectangle((RmQRCodeData)Symbol("R17x139").Data, small)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.GetSymbolRectangle(v1, new SymbolImageOptions { IconImage = new byte[8] })).Throws<ArgumentOutOfRangeException>()
            .Because("an icon image needs an icon area");
    }

    // ---- the symbols ----------------------------------------------------------------------

    public sealed record SymbolCase(object Data, int Columns, int Rows, int QuietZone);

    private static SymbolCase Symbol(string name) => name switch
    {
        "QR3" => Qr(3, 4),
        "QR40" => Qr(40, 4),
        "QR5 q0" => Qr(5, 0),
        "QR2 q1" => Qr(2, 1),
        "M1" => Micro(MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly, 2),
        "M4" => Micro(MicroQRVersion.M4, MicroQREccLevel.L, 2),
        "M3 q0" => Micro(MicroQRVersion.M3, MicroQREccLevel.L, 0),
        "R7x43" => Rm(RmQRVersion.R7x43, 2),
        "R17x139" => Rm(RmQRVersion.R17x139, 2),
        "R11x77 q0" => Rm(RmQRVersion.R11x77, 0),
        _ => throw new ArgumentException(name),
    };

    private static SymbolCase Qr(int version, int quietZone)
    {
        var qr = QRCodeGenerator.Create("12345", QREccLevel.M, new QRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });
        return new SymbolCase(qr, qr.Size, qr.Size, quietZone);
    }

    private static SymbolCase Micro(MicroQRVersion version, MicroQREccLevel level, int quietZone)
    {
        var micro = MicroQRCodeGenerator.Create("12345", level, new MicroQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });
        return new SymbolCase(micro, micro.Size, micro.Size, quietZone);
    }

    private static SymbolCase Rm(RmQRVersion version, int quietZone)
    {
        var rm = RmQRCodeGenerator.Create("12345", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });
        return new SymbolCase(rm, rm.Width, rm.Height, quietZone);
    }

    private static SymbolImageOptions Options(string name, SymbolCase symbol) => name switch
    {
        "default" => default,
        "module 1" => new SymbolImageOptions { ModulePixelSize = 1 },
        "module 3" => new SymbolImageOptions { ModulePixelSize = 3 },
        "fraction" => new SymbolImageOptions { Width = 777, Height = 777 },
        "fraction wider" => new SymbolImageOptions { Width = 1000, Height = 777 },
        "fraction taller" => new SymbolImageOptions { Width = 777, Height = 1000 },
        "width only" => new SymbolImageOptions { Width = 1000 },
        "height only" => new SymbolImageOptions { Height = 500 },
        // 5 pixels a module with 9 spare columns, so the symbol starts 4 pixels in.
        "whole pixels padded" => new SymbolImageOptions { Width = 5 * symbol.Columns + 9, Height = 5 * symbol.Rows },
        "fill" => new SymbolImageOptions { Width = 777, Height = 777, FillCanvas = true },
        "fill wider" => new SymbolImageOptions { Width = 1000, Height = 777, FillCanvas = true },
        "fill width only" => new SymbolImageOptions { Width = 1000, FillCanvas = true },
        _ => throw new ArgumentException(name),
    };

    private static bool CoreEdgesHoldDarkModules(SymbolCase symbol)
    {
        bool Dark(int row, int column) => symbol.Data switch
        {
            QRCodeData qr => qr[row, column],
            MicroQRCodeData micro => micro[row, column],
            RmQRCodeData rm => rm[row, column],
            _ => throw new ArgumentException(nameof(symbol)),
        };
        int q = symbol.QuietZone, lastRow = symbol.Rows - q - 1, lastColumn = symbol.Columns - q - 1;
        bool top = false, bottom = false, left = false, right = false;
        for (var column = q; column <= lastColumn; column++)
        {
            top |= Dark(q, column);
            bottom |= Dark(lastRow, column);
        }
        for (var row = q; row <= lastRow; row++)
        {
            left |= Dark(row, q);
            right |= Dark(row, lastColumn);
        }
        return top && bottom && left && right;
    }

    private static (int X, int Y, int Width, int Height) Tuple(ImageRect rectangle) => (rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

    private static ImageRect RectangleOf(object data, SymbolImageOptions options) => data switch
    {
        QRCodeData qr => SymbolImageWriter.GetSymbolRectangle(qr, options),
        MicroQRCodeData micro => SymbolImageWriter.GetSymbolRectangle(micro, options),
        RmQRCodeData rm => SymbolImageWriter.GetSymbolRectangle(rm, options),
        _ => throw new ArgumentException(nameof(data)),
    };

    private static ImageSize SizeOf(object data, SymbolImageOptions options) => data switch
    {
        QRCodeData qr => SymbolImageWriter.GetImageSize(qr, options),
        MicroQRCodeData micro => SymbolImageWriter.GetImageSize(micro, options),
        RmQRCodeData rm => SymbolImageWriter.GetImageSize(rm, options),
        _ => throw new ArgumentException(nameof(data)),
    };

    private static bool TryWriteGray8(object data, byte[] destination, SymbolImageOptions options) => data switch
    {
        QRCodeData qr => SymbolImageWriter.TryWritePixels(qr, destination, PixelLayout.Gray8, out _, out _, options),
        MicroQRCodeData micro => SymbolImageWriter.TryWritePixels(micro, destination, PixelLayout.Gray8, out _, out _, options),
        RmQRCodeData rm => SymbolImageWriter.TryWritePixels(rm, destination, PixelLayout.Gray8, out _, out _, options),
        _ => throw new ArgumentException(nameof(data)),
    };
}
