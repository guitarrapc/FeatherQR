using System.Buffers;
using System.Buffers.Binary;
using System.Xml.Linq;
using FeatherQR.SkiaSharp;
using SkiaSharp;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// An image of a set size. The symbol fills the canvas as the rendering package's <c>WithSize</c> fills it, so the two write the same pixels:
/// one uniform scale, the symbol centred on whole pixels, and each pixel belonging to the module its centre falls in.
/// The test computes that rule on its own from the fit and holds the pixel buffer and the PNG to it, and the SVG to the canvas's size.
/// </summary>
public class SymbolImageWriterSizeTest
{
    private static readonly SymbolColor Navy = new(0x1E, 0x3A, 0x8A);
    private static readonly SymbolColor Ivory = new(0xFF, 0xFF, 0xF0);

    public static IEnumerable<(string Symbol, int Width, int Height)> SymbolsAndCanvases()
    {
        foreach (var symbol in new[] { "QR3", "QR40", "M4", "R17x139" })
        {
            yield return (symbol, 512, 512);
            yield return (symbol, 300, 300);
            yield return (symbol, 777, 777);
            yield return (symbol, 600, 400);
            yield return (symbol, 200, 200);
        }
        yield return ("QR3", 1024, 160);
        yield return ("M4", 1024, 160);
        yield return ("R17x139", 1024, 160);
        yield return ("QR3", 64, 64);
        yield return ("M4", 64, 64);
        // Whole pixels with a pad: every module the same size, the symbol off the canvas's edge, on both axes and all three symbologies.
        yield return ("QR3", 364, 296);
        yield return ("QR3", 296, 364);
        yield return ("M4", 200, 168);
        yield return ("R17x139", 286, 60);
        // A single light row below the symbol and none above it, at whole pixels and at a fraction.
        yield return ("QR3", 296, 297);
        yield return ("R17x139", 1000, 148);
    }

    // ---- the renderer's pixels ----------------------------------------------------------

    [Test]
    [MethodDataSource(nameof(SymbolsAndCanvases))]
    public async Task SetSize_WritesTheRenderersPixels(string symbol, int width, int height)
    {
        var data = Symbol(symbol);
        var options = new SymbolImageOptions { Width = width, Height = height };
        var rule = RulePixels(data, width, height, new SymbolColor(0, 0, 0), new SymbolColor(255, 255, 255));

        await Assert.That(RendererPixels(data, width, height)).IsEquivalentTo(rule, CollectionOrdering.Matching)
            .Because("the pixel-centre rule is what the renderer draws");

        var pixels = new byte[PixelBufferSize(data, PixelLayout.Bgra8888, options)];
        var (written, w, h) = TryWritePixels(data, pixels, PixelLayout.Bgra8888, options);
        await Assert.That(written).IsTrue();
        await Assert.That((w, h)).IsEqualTo((width, height));
        await Assert.That(pixels).IsEquivalentTo(rule, CollectionOrdering.Matching);

        var png = GetPngBytes(data, options);
        await Assert.That(PngSize(png)).IsEqualTo((width, height));
        await Assert.That(png[24]).IsEqualTo((byte)1).Because("every module is whole pixels, so the PNG stays one bit a pixel");
        await Assert.That(DecodePng(png)).IsEquivalentTo(rule, CollectionOrdering.Matching);

        var root = XDocument.Parse(GetSvgString(data, options)).Root!;
        await Assert.That(root.Attribute("width")?.Value).IsEqualTo(width.ToString());
        await Assert.That(root.Attribute("height")?.Value).IsEqualTo(height.ToString());
    }

    /// <summary>The symbol at a set size still reads, down to modules of one and two pixels.</summary>
    [Test]
    [Arguments(512)]
    [Arguments(300)]
    public async Task SetSize_Decodes(int side)
    {
        var qr = QRCodeGenerator.Create(new string('a', 2000), QREccLevel.M, new QRCodeGeneratorOptions { Version = 40 });
        var options = new SymbolImageOptions { Width = side, Height = side };
        var gray = new byte[SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Gray8, options)];
        await Assert.That(SymbolImageWriter.TryWritePixels(qr, gray, PixelLayout.Gray8, out var w, out var h, options)).IsTrue();

        await Assert.That(QRCodeDecoder.TryDecodeImage(gray, w, h, out string text, out _)).IsTrue();
        await Assert.That(text).IsEqualTo(new string('a', 2000));
    }

    // ---- the options ----------------------------------------------------------------------

    [Test]
    public async Task NoSize_IsTheDefault_AndTheSymbolsOwnSize()
    {
        await Assert.That(new SymbolImageOptions { Width = 0, Height = 0 }).IsEqualTo(default(SymbolImageOptions));
        await Assert.That(default(SymbolImageOptions).Width).IsEqualTo(0);
        await Assert.That(default(SymbolImageOptions).Height).IsEqualTo(0);

        var qr = QRCodeGenerator.Create("own size", QREccLevel.M);
        await Assert.That(SymbolImageWriter.GetPngBytes(qr, new SymbolImageOptions { Width = 0, Height = 0 }))
            .IsEquivalentTo(SymbolImageWriter.GetPngBytes(qr), CollectionOrdering.Matching);
    }

    [Test]
    public async Task NegativeSize_Throws()
    {
        await Assert.That(() => new SymbolImageOptions { Width = -1 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SymbolImageOptions { Height = -1 }).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>With a size set, the module size is the fit's, so <c>ModulePixelSize</c> changes nothing.</summary>
    [Test]
    public async Task SetSize_IgnoresModulePixelSize()
    {
        var rm = RmQRCodeGenerator.Create("ignored", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 });
        var fit = new SymbolImageOptions { Width = 600, Height = 400 };
        var pinned = new SymbolImageOptions { Width = 600, Height = 400, ModulePixelSize = 3 };

        await Assert.That(SymbolImageWriter.GetPngBytes(rm, pinned)).IsEquivalentTo(SymbolImageWriter.GetPngBytes(rm, fit), CollectionOrdering.Matching);
        await Assert.That(SymbolImageWriter.GetSvgString(rm, pinned)).IsEqualTo(SymbolImageWriter.GetSvgString(rm, fit));
        await Assert.That(SymbolImageWriter.GetPixelBufferSize(rm, PixelLayout.Gray8, pinned)).IsEqualTo(SymbolImageWriter.GetPixelBufferSize(rm, PixelLayout.Gray8, fit));
    }

    /// <summary>
    /// One side at 0 follows the symbol's aspect at the scale the other side gives, rounded up to whole pixels, so the symbol fits inside
    /// and the image is the one both sides would give: rounded down, the same two sides would bind on the followed one at a smaller scale.
    /// </summary>
    [Test]
    public async Task OneSideAtZero_FollowsTheAspect()
    {
        var qr = QRCodeGenerator.Create("aspect", QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 });
        var rm = RmQRCodeGenerator.Create("aspect", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 });
        await Assert.That((rm.Width, rm.Height)).IsEqualTo((143, 21));

        await Assert.That(Size(qr, new SymbolImageOptions { Width = 300 })).IsEqualTo((300, 300));
        await Assert.That(Size(qr, new SymbolImageOptions { Height = 300 })).IsEqualTo((300, 300));
        // 21 × 1430 / 143 is exactly 210, and 143 × 210 / 21 exactly 1430.
        await Assert.That(Size(rm, new SymbolImageOptions { Width = 1430 })).IsEqualTo((1430, 210));
        await Assert.That(Size(rm, new SymbolImageOptions { Height = 210 })).IsEqualTo((1430, 210));
        // 21 × 1000 / 143 is 146.85, so the symbol needs 147 rows.
        await Assert.That(Size(rm, new SymbolImageOptions { Width = 1000 })).IsEqualTo((1000, 147));
        // 143 × 100 / 21 is 680.95.
        await Assert.That(Size(rm, new SymbolImageOptions { Height = 100 })).IsEqualTo((681, 100));

        // The image with one side given is the one with both, so its pixels follow the same rule.
        var options = new SymbolImageOptions { Width = 1000 };
        var pixels = new byte[SymbolImageWriter.GetPixelBufferSize(rm, PixelLayout.Rgba8888, options)];
        await Assert.That(SymbolImageWriter.TryWritePixels(rm, pixels, PixelLayout.Rgba8888, out _, out _, options)).IsTrue();
        await Assert.That(pixels).IsEquivalentTo(RulePixels(rm, 1000, 147, new SymbolColor(0, 0, 0), new SymbolColor(255, 255, 255), PixelLayout.Rgba8888), CollectionOrdering.Matching);

        // 7 × 419 / 139 is 21.1 rows, so the followed side is 22: at 21 the same canvas with both sides set would bind on the height at
        // 3 pixels a module with a column of pad, a different image.
        var narrow = RmQRCodeGenerator.Create("aspect", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x139, QuietZoneSize = 0 });
        await Assert.That(Size(narrow, new SymbolImageOptions { Width = 419 })).IsEqualTo((419, 22));
        foreach (var layout in new[] { PixelLayout.Gray8, PixelLayout.Rgba8888 })
        {
            var oneSide = new byte[SymbolImageWriter.GetPixelBufferSize(narrow, layout, new SymbolImageOptions { Width = 419 })];
            await Assert.That(SymbolImageWriter.TryWritePixels(narrow, oneSide, layout, out _, out _, new SymbolImageOptions { Width = 419 })).IsTrue();
            var bothSides = new byte[SymbolImageWriter.GetPixelBufferSize(narrow, layout, new SymbolImageOptions { Width = 419, Height = 22 })];
            await Assert.That(SymbolImageWriter.TryWritePixels(narrow, bothSides, layout, out _, out _, new SymbolImageOptions { Width = 419, Height = 22 })).IsTrue();
            await Assert.That(oneSide).IsEquivalentTo(bothSides, CollectionOrdering.Matching);
        }
        await Assert.That(SymbolImageWriter.GetSvgString(narrow, new SymbolImageOptions { Width = 419 })).IsEqualTo(SymbolImageWriter.GetSvgString(narrow, new SymbolImageOptions { Width = 419, Height = 22 }));
    }

    /// <summary>A canvas that gives a module under one pixel is refused by every call, since modules then vanish and nothing reads the result.</summary>
    [Test]
    [Arguments(100, 100)]
    [Arguments(1024, 160)]
    [Arguments(184, 0)]
    [Arguments(0, 184)]
    public async Task ModuleUnderOnePixel_Throws(int width, int height)
    {
        var qr = QRCodeGenerator.Create("too small", QREccLevel.M, new QRCodeGeneratorOptions { Version = 40 });
        await Assert.That(qr.Size).IsEqualTo(185);
        var options = new SymbolImageOptions { Width = width, Height = height };
        var writer = new ArrayBufferWriter<byte>();

        await Assert.That(() => SymbolImageWriter.GetSvgString(qr, options)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.WriteSvg(qr, writer, options)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.GetPngBytes(qr, options)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.WritePng(qr, writer, options)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Gray8, options)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.TryWritePixels(qr, new byte[1 << 20], PixelLayout.Gray8, out _, out _, options)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(writer.WrittenCount).IsEqualTo(0);

        // One pixel a module is the floor, and 185 pixels give exactly that.
        var floor = new SymbolImageOptions { Width = 185, Height = 185 };
        await Assert.That(Size(qr, floor)).IsEqualTo((185, 185));
        await Assert.That(SymbolImageWriter.GetPngBytes(qr, floor)).IsEquivalentTo(SymbolImageWriter.GetPngBytes(qr, new SymbolImageOptions { ModulePixelSize = 1 }), CollectionOrdering.Matching);
    }

    // ---- the pad and the colours ----------------------------------------------------------

    /// <summary>
    /// Where the canvas and the symbol differ in aspect, the rest is the light colour with its alpha, in every format.
    /// The PNG is read back without premultiplying, so the palette's bytes come back as written.
    /// </summary>
    [Test]
    public async Task Pad_IsTheLightColor_WithItsAlpha()
    {
        var rm = RmQRCodeGenerator.Create("pad", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 });
        var light = new SymbolColor(0xFF, 0xFF, 0xF0, 51);
        var options = new SymbolImageOptions { Width = 300, Height = 300, DarkColor = Navy, LightColor = light };
        var rule = RulePixels(rm, 300, 300, Navy, light);
        // 300 × 143 - 21 × 300 over 2 × 143 is 127.97, so the symbol starts on row 127.
        var top = 127;

        var pixels = new byte[SymbolImageWriter.GetPixelBufferSize(rm, PixelLayout.Bgra8888, options)];
        await Assert.That(SymbolImageWriter.TryWritePixels(rm, pixels, PixelLayout.Bgra8888, out _, out _, options)).IsTrue();
        await Assert.That(pixels).IsEquivalentTo(rule, CollectionOrdering.Matching);
        var padAbove = Enumerable.Range(0, top * 300).Select(p => pixels.AsSpan(p * 4, 4).ToArray());
        await Assert.That(padAbove.All(pixel => pixel.SequenceEqual(new byte[] { 0xF0, 0xFF, 0xFF, 51 }))).IsTrue().Because("the pad above is the light colour with its alpha");

        var png = SymbolImageWriter.GetPngBytes(rm, options);
        await Assert.That(DecodePng(png, unpremultiplied: true)).IsEquivalentTo(rule, CollectionOrdering.Matching);

        var root = XDocument.Parse(SymbolImageWriter.GetSvgString(rm, options)).Root!;
        XNamespace ns = "http://www.w3.org/2000/svg";
        var rect = root.Element(ns + "rect")!;
        await Assert.That(rect.Attribute("fill")?.Value).IsEqualTo("#fffff0");
        await Assert.That(rect.Attribute("fill-opacity")?.Value).IsEqualTo("0.2");
    }

    // ---- the SVG ----------------------------------------------------------------------------

    /// <summary>
    /// The SVG keeps its path in modules and lets the renderer scale it: the root's size is the canvas, and the view box and the
    /// background grow by the pad in module units, to three decimals.
    /// </summary>
    [Test]
    public async Task Svg_HasTheCanvasSize_AndTheViewBoxGrownByThePad()
    {
        var rm = RmQRCodeGenerator.Create("svg", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 });
        var fitted = XDocument.Parse(SymbolImageWriter.GetSvgString(rm, new SymbolImageOptions { Width = 300, Height = 300 })).Root!;
        var own = XDocument.Parse(SymbolImageWriter.GetSvgString(rm)).Root!;
        XNamespace ns = "http://www.w3.org/2000/svg";

        await Assert.That(fitted.Attribute("width")?.Value).IsEqualTo("300");
        await Assert.That(fitted.Attribute("height")?.Value).IsEqualTo("300");
        await Assert.That(fitted.Attribute("shape-rendering")?.Value).IsEqualTo("crispEdges");

        // The symbol is 143 modules over 300 pixels, so the pad of 127 pixels above it is 127 × 143 / 300 = 60.537 modules.
        var viewBox = fitted.Attribute("viewBox")!.Value.Split(' ').Select(double.Parse).ToArray();
        await Assert.That(viewBox[0]).IsEqualTo(0);
        await Assert.That(Math.Abs(viewBox[1] - (-127 * 143.0 / 300))).IsLessThan(0.0006);
        await Assert.That(viewBox[2]).IsEqualTo(143);
        await Assert.That(viewBox[3]).IsEqualTo(143);

        var rect = fitted.Element(ns + "rect")!;
        await Assert.That(rect.Attribute("x")?.Value).IsEqualTo(fitted.Attribute("viewBox")!.Value.Split(' ')[0]);
        await Assert.That(rect.Attribute("y")?.Value).IsEqualTo(fitted.Attribute("viewBox")!.Value.Split(' ')[1]);
        await Assert.That(rect.Attribute("width")?.Value).IsEqualTo("143");
        await Assert.That(rect.Attribute("height")?.Value).IsEqualTo("143");

        await Assert.That(fitted.Element(ns + "path")!.ToString()).IsEqualTo(own.Element(ns + "path")!.ToString())
            .Because("the path is in modules and does not change with the canvas");
    }

    /// <summary>At whole pixels with a pad the view box is in modules too: a pad of 34 pixels at 8 a module is 4.25 modules, and 364 pixels are 45.5.</summary>
    [Test]
    public async Task Svg_WholePixelsWithAPad_HasTheViewBoxInModules()
    {
        var root = XDocument.Parse(GetSvgString(Symbol("QR3"), new SymbolImageOptions { Width = 364, Height = 296 })).Root!;

        var viewBox = root.Attribute("viewBox")!.Value.Split(' ').Select(double.Parse).ToArray();
        await Assert.That(viewBox).IsEquivalentTo(new[] { -4.25, 0, 45.5, 37 }, CollectionOrdering.Matching);
        await Assert.That(root.Attribute("width")?.Value).IsEqualTo("364");
        await Assert.That(root.Attribute("height")?.Value).IsEqualTo("296");
    }

    /// <summary>A square symbol in a square canvas has no pad, and the view box is the symbol's own.</summary>
    [Test]
    public async Task Svg_WithoutAPad_KeepsTheViewBoxInModules()
    {
        var qr = QRCodeGenerator.Create("svg", QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 });
        var root = XDocument.Parse(SymbolImageWriter.GetSvgString(qr, new SymbolImageOptions { Width = 512, Height = 512 })).Root!;
        XNamespace ns = "http://www.w3.org/2000/svg";

        await Assert.That(root.Attribute("viewBox")?.Value).IsEqualTo("0 0 37 37");
        await Assert.That(root.Attribute("width")?.Value).IsEqualTo("512");
        var rect = root.Element(ns + "rect")!;
        await Assert.That(rect.Attribute("width")?.Value).IsEqualTo("37");
        await Assert.That(rect.Attribute("height")?.Value).IsEqualTo("37");
    }

    [Test]
    public async Task WriteSvg_AndWritePng_AtASetSize_WriteTheStringAndTheArray()
    {
        var micro = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L);
        var options = new SymbolImageOptions { Width = 300, Height = 200, DarkColor = Navy, LightColor = Ivory };
        var writer = new ArrayBufferWriter<byte>();

        var svgLength = SymbolImageWriter.WriteSvg(micro, writer, options);
        await Assert.That(writer.WrittenSpan.ToArray()).IsEquivalentTo(System.Text.Encoding.UTF8.GetBytes(SymbolImageWriter.GetSvgString(micro, options)), CollectionOrdering.Matching);
        await Assert.That(svgLength).IsEqualTo(writer.WrittenCount);

        writer.Clear();
        var pngLength = SymbolImageWriter.WritePng(micro, writer, options);
        await Assert.That(writer.WrittenSpan.ToArray()).IsEquivalentTo(SymbolImageWriter.GetPngBytes(micro, options), CollectionOrdering.Matching);
        await Assert.That(pngLength).IsEqualTo(writer.WrittenCount);
    }

    // ---- the rule, computed here ----------------------------------------------------------

    /// <summary>
    /// The pixels the fit gives: one scale, the smaller the two sides allow, the symbol centred on whole pixels as the rendering package
    /// centres it, and each pixel the module its centre falls in. Outside the symbol the light colour.
    /// </summary>
    private static byte[] RulePixels(object data, int width, int height, SymbolColor dark, SymbolColor light, PixelLayout layout = PixelLayout.Bgra8888)
    {
        var (columns, rows, isDark) = Modules(data);
        var scale = Math.Min((double)width / columns, (double)height / rows);
        var left = Math.Max(0, Math.Floor((width - columns * scale) / 2 + 1e-6));
        var top = Math.Max(0, Math.Floor((height - rows * scale) / 2 + 1e-6));
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var column = (int)Math.Floor((x + 0.5 - left) / scale);
                var row = (int)Math.Floor((y + 0.5 - top) / scale);
                var inside = x + 0.5 >= left && y + 0.5 >= top && column < columns && row < rows;
                var color = inside && isDark(row, column) ? dark : light;
                var at = (y * width + x) * 4;
                if (layout == PixelLayout.Bgra8888)
                {
                    pixels[at] = color.B;
                    pixels[at + 1] = color.G;
                    pixels[at + 2] = color.R;
                }
                else
                {
                    pixels[at] = color.R;
                    pixels[at + 1] = color.G;
                    pixels[at + 2] = color.B;
                }
                pixels[at + 3] = color.A;
            }
        }
        return pixels;
    }

    private static byte[] RendererPixels(object data, int width, int height)
    {
        using var bitmap = data switch
        {
            QRCodeData qr => new QRCodeImageBuilder(qr).WithSize(width, height).ToBitmap(),
            MicroQRCodeData micro => new MicroQRCodeImageBuilder(micro).WithSize(width, height).ToBitmap(),
            RmQRCodeData rm => new RmQRCodeImageBuilder(rm).WithSize(width, height).ToBitmap(),
            _ => throw new ArgumentException(nameof(data)),
        };
        return Bgra(bitmap);
    }

    private static byte[] DecodePng(byte[] png, bool unpremultiplied = false)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(png));
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, unpremultiplied ? SKAlphaType.Unpremul : SKAlphaType.Premul);
        using var bitmap = SKBitmap.Decode(codec, info);
        return Bgra(bitmap);
    }

    private static byte[] Bgra(SKBitmap bitmap)
    {
        using var converted = bitmap.ColorType == SKColorType.Bgra8888 ? null : bitmap.Copy(SKColorType.Bgra8888);
        return (converted ?? bitmap).GetPixelSpan().ToArray();
    }

    private static (int Width, int Height) PngSize(byte[] png)
        => (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));

    private static (int Width, int Height) Size(object data, SymbolImageOptions options)
    {
        var pixels = new byte[PixelBufferSize(data, PixelLayout.Gray8, options)];
        var (written, w, h) = TryWritePixels(data, pixels, PixelLayout.Gray8, options);
        if (!written)
            throw new InvalidOperationException("the buffer was sized by the writer");
        var png = GetPngBytes(data, options);
        if (PngSize(png) != (w, h))
            throw new InvalidOperationException($"the PNG is {PngSize(png)} and the pixels {(w, h)}");
        return (w, h);
    }

    private static object Symbol(string name) => name switch
    {
        "QR3" => QRCodeGenerator.Create("set size", QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 }),
        "QR40" => QRCodeGenerator.Create(new string('a', 2000), QREccLevel.M, new QRCodeGeneratorOptions { Version = 40 }),
        "M4" => MicroQRCodeGenerator.Create("HELLO123", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M4 }),
        "R17x139" => RmQRCodeGenerator.Create("HELLO WORLD 1234567890", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 }),
        _ => throw new ArgumentException(name),
    };

    private static (int Columns, int Rows, Func<int, int, bool> IsDark) Modules(object data) => data switch
    {
        QRCodeData qr => (qr.Size, qr.Size, (r, c) => qr[r, c]),
        MicroQRCodeData micro => (micro.Size, micro.Size, (r, c) => micro[r, c]),
        RmQRCodeData rm => (rm.Width, rm.Height, (r, c) => rm[r, c]),
        _ => throw new ArgumentException(nameof(data)),
    };

    private static int PixelBufferSize(object data, PixelLayout layout, SymbolImageOptions options) => data switch
    {
        QRCodeData qr => SymbolImageWriter.GetPixelBufferSize(qr, layout, options),
        MicroQRCodeData micro => SymbolImageWriter.GetPixelBufferSize(micro, layout, options),
        RmQRCodeData rm => SymbolImageWriter.GetPixelBufferSize(rm, layout, options),
        _ => throw new ArgumentException(nameof(data)),
    };

    private static (bool Written, int Width, int Height) TryWritePixels(object data, byte[] destination, PixelLayout layout, SymbolImageOptions options)
    {
        bool written;
        int w, h;
        switch (data)
        {
            case QRCodeData qr: written = SymbolImageWriter.TryWritePixels(qr, destination, layout, out w, out h, options); break;
            case MicroQRCodeData micro: written = SymbolImageWriter.TryWritePixels(micro, destination, layout, out w, out h, options); break;
            case RmQRCodeData rm: written = SymbolImageWriter.TryWritePixels(rm, destination, layout, out w, out h, options); break;
            default: throw new ArgumentException(nameof(data));
        }
        return (written, w, h);
    }

    private static byte[] GetPngBytes(object data, SymbolImageOptions options) => data switch
    {
        QRCodeData qr => SymbolImageWriter.GetPngBytes(qr, options),
        MicroQRCodeData micro => SymbolImageWriter.GetPngBytes(micro, options),
        RmQRCodeData rm => SymbolImageWriter.GetPngBytes(rm, options),
        _ => throw new ArgumentException(nameof(data)),
    };

    private static string GetSvgString(object data, SymbolImageOptions options) => data switch
    {
        QRCodeData qr => SymbolImageWriter.GetSvgString(qr, options),
        MicroQRCodeData micro => SymbolImageWriter.GetSvgString(micro, options),
        RmQRCodeData rm => SymbolImageWriter.GetSvgString(rm, options),
        _ => throw new ArgumentException(nameof(data)),
    };
}
