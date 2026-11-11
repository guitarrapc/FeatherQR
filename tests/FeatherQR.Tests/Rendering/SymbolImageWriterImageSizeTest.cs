using System.Buffers.Binary;
using System.Xml.Linq;

namespace FeatherQR.Tests;

/// <summary>
/// The image's size asked before anything is written: the size the pixel writer reports, the PNG's header holds and the SVG's root names,
/// for every symbology, plain and styled, at a module size, at a set size that is not whole pixels, and with an icon area.
/// </summary>
public class SymbolImageWriterImageSizeTest
{
    private static readonly SymbolImageOptions Shaped = new() { ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 90, FinderPatternStyle = FinderPatternStyle.RoundedSquareCircle };

    public static IEnumerable<(string Symbol, string Options)> SymbolsAndOptions()
    {
        foreach (var symbol in new[] { "QR3", "QR40", "M4", "R17x139" })
        {
            foreach (var options in new[] { "default", "module 1", "module 3", "styled", "fraction", "fraction padded", "width only", "height only", "styled fraction", "whole padded", "styled whole padded" })
                yield return (symbol, options);
        }
        yield return ("QR5 H", "icon");
        yield return ("QR5 H", "icon fraction");
        yield return ("QR5 H", "icon styled");
    }

    [Test]
    [MethodDataSource(nameof(SymbolsAndOptions))]
    public async Task ImageSize_IsThePixelWritersThePngsAndTheSvgs(string symbol, string optionsName)
    {
        var data = Symbol(symbol);
        var options = Options(optionsName);

        var size = SizeOf(data, options);

        var pixels = new byte[PixelBufferSize(data, options)];
        var (written, width, height) = TryWritePixels(data, pixels, options);
        await Assert.That(written).IsTrue();
        await Assert.That(size).IsEqualTo((width, height));
        await Assert.That(pixels.Length).IsEqualTo(size.Width * size.Height).Because("Gray8 is one byte a pixel");

        var png = GetPngBytes(data, options);
        await Assert.That(size).IsEqualTo((BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20))));

        var root = XDocument.Parse(GetSvgString(data, options)).Root!;
        await Assert.That(root.Attribute("width")?.Value).IsEqualTo(size.Width.ToString());
        await Assert.That(root.Attribute("height")?.Value).IsEqualTo(size.Height.ToString());
    }

    /// <summary>The sizes written out, so the test above cannot agree with a writer and a query that are wrong together.</summary>
    [Test]
    public async Task ImageSize_IsTheModulesTimesTheModuleSize_OrTheCanvas()
    {
        var qr = (QRCodeData)Symbol("QR3");
        var micro = (MicroQRCodeData)Symbol("M4");
        var rm = (RmQRCodeData)Symbol("R17x139");
        await Assert.That((qr.Size, micro.Size, rm.Width, rm.Height)).IsEqualTo((37, 21, 143, 21));

        await Assert.That(Pair(SymbolImageWriter.GetImageSize(qr))).IsEqualTo((296, 296));
        await Assert.That(Pair(SymbolImageWriter.GetImageSize(micro))).IsEqualTo((168, 168));
        await Assert.That(Pair(SymbolImageWriter.GetImageSize(rm))).IsEqualTo((1144, 168));
        await Assert.That(Pair(SymbolImageWriter.GetImageSize(rm, new SymbolImageOptions { ModulePixelSize = 3 }))).IsEqualTo((429, 63));

        await Assert.That(Pair(SymbolImageWriter.GetImageSize(qr, new SymbolImageOptions { Width = 600, Height = 400 }))).IsEqualTo((600, 400));
        await Assert.That(Pair(SymbolImageWriter.GetImageSize(micro, new SymbolImageOptions { Width = 300, Height = 301 }))).IsEqualTo((300, 301));
        // 21 × 1000 / 143 is 146.85, so the side that follows is 147.
        await Assert.That(Pair(SymbolImageWriter.GetImageSize(rm, new SymbolImageOptions { Width = 1000 }))).IsEqualTo((1000, 147));
        await Assert.That(Pair(SymbolImageWriter.GetImageSize(rm, new SymbolImageOptions { Height = 100 }))).IsEqualTo((681, 100));

    }

    // ---- the value returned ---------------------------------------------------------------

    /// <summary>
    /// The size is an <see cref="ImageSize"/>: its two sides by name, and split in the order width, height. The rMQR code is 1,144 by 168
    /// pixels, so a split in the other order shows.
    /// </summary>
    [Test]
    public async Task ImageSize_NamesItsSides_AndSplitsAsWidthThenHeight()
    {
        var rm = (RmQRCodeData)Symbol("R17x139");

        ImageSize size = SymbolImageWriter.GetImageSize(rm);
        var (width, height) = size;

        await Assert.That(size.Width).IsEqualTo(1144);
        await Assert.That(size.Height).IsEqualTo(168);
        await Assert.That(width).IsEqualTo(1144);
        await Assert.That(height).IsEqualTo(168);
    }

    /// <summary>Two sizes compare by their sides and print them, and <c>default</c> is no pixels at all.</summary>
    [Test]
    public async Task ImageSize_ComparesAndPrintsByValue()
    {
        var qr = (QRCodeData)Symbol("QR3");
        var rm = (RmQRCodeData)Symbol("R17x139");

        var size = SymbolImageWriter.GetImageSize(rm);

        await Assert.That(size == SymbolImageWriter.GetImageSize(rm)).IsTrue();
        await Assert.That(size).IsEqualTo(SymbolImageWriter.GetImageSize(rm, new SymbolImageOptions { ModulePixelSize = 8 }));
        await Assert.That(size != SymbolImageWriter.GetImageSize(qr)).IsTrue();
        // The same two numbers the other way round are another size.
        await Assert.That(size != SymbolImageWriter.GetImageSize(rm, new SymbolImageOptions { Width = 168, Height = 1144 })).IsTrue();
        await Assert.That(Pair(SymbolImageWriter.GetImageSize(rm, new SymbolImageOptions { Width = 168, Height = 1144 }))).IsEqualTo((168, 1144));
        await Assert.That(size.ToString()).IsEqualTo("ImageSize { Width = 1144, Height = 168 }");
        await Assert.That((default(ImageSize).Width, default(ImageSize).Height)).IsEqualTo((0, 0));
    }

    /// <summary>
    /// The size does not depend on a layout, so an image too long for a pixel buffer still has one: the SVG is written at it.
    /// Version 40 at 126 pixels a module is 23,310 pixels a side, which four bytes a pixel cannot hold in an array.
    /// </summary>
    [Test]
    public async Task ImageSize_PastThePixelBuffersLimit_IsStillTheSvgs()
    {
        var qr = QRCodeGenerator.Create("1", QREccLevel.L, new QRCodeGeneratorOptions { Version = 40 });
        var options = new SymbolImageOptions { ModulePixelSize = 126 };
        await Assert.That(() => SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Rgba8888, options)).Throws<ArgumentOutOfRangeException>();

        await Assert.That(Pair(SymbolImageWriter.GetImageSize(qr, options))).IsEqualTo((23_310, 23_310));
        var root = XDocument.Parse(SymbolImageWriter.GetSvgString(qr, options)).Root!;
        await Assert.That(root.Attribute("width")?.Value).IsEqualTo("23310");
    }

    // ---- what every image call refuses, refused here too ---------------------------------

    [Test]
    public async Task NullData_Throws()
    {
        await Assert.That(() => SymbolImageWriter.GetImageSize((QRCodeData)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.GetImageSize((MicroQRCodeData)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.GetImageSize((RmQRCodeData)null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task ModuleUnderOnePixel_Throws_AndOnePixelIsSized()
    {
        var qr = QRCodeGenerator.Create("too small", QREccLevel.M, new QRCodeGeneratorOptions { Version = 40 });
        var micro = (MicroQRCodeData)Symbol("M4");
        var rm = (RmQRCodeData)Symbol("R17x139");

        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.GetImageSize(qr, new SymbolImageOptions { Width = 184, Height = 184 }));
        await Assert.That(refused.ParamName).IsEqualTo("options");
        await Assert.That(() => SymbolImageWriter.GetImageSize(qr, new SymbolImageOptions { Width = 184 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.GetImageSize(micro, new SymbolImageOptions { Width = 20, Height = 20 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.GetImageSize(rm, new SymbolImageOptions { Width = 1024, Height = 20 })).Throws<ArgumentOutOfRangeException>();

        await Assert.That(Pair(SymbolImageWriter.GetImageSize(qr, new SymbolImageOptions { Width = 185, Height = 185 }))).IsEqualTo((185, 185));
        await Assert.That(Pair(SymbolImageWriter.GetImageSize(micro, new SymbolImageOptions { Width = 21, Height = 21 }))).IsEqualTo((21, 21));
        await Assert.That(Pair(SymbolImageWriter.GetImageSize(rm, new SymbolImageOptions { Width = 143, Height = 21 }))).IsEqualTo((143, 21));
    }

    /// <summary>Version 40 is 185 modules a side, so 11,608,019 pixels a module keep a side within an int and 11,608,020 do not.</summary>
    [Test]
    public async Task SidePastAnInt_Throws_AndUpToItIsSized()
    {
        var qr = QRCodeGenerator.Create("1", QREccLevel.L, new QRCodeGeneratorOptions { Version = 40 });

        await Assert.That(Pair(SymbolImageWriter.GetImageSize(qr, new SymbolImageOptions { ModulePixelSize = 11_608_019 }))).IsEqualTo((2_147_483_515, 2_147_483_515));
        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.GetImageSize(qr, new SymbolImageOptions { ModulePixelSize = 11_608_020 }));
        await Assert.That(refused.ParamName).IsEqualTo("options");
    }

    /// <summary>An icon that the writers refuse is refused when the size is asked, so the caller learns before making an image of that size.</summary>
    [Test]
    public async Task AnIconTheWritersRefuse_Throws()
    {
        var v1 = QRCodeGenerator.Create("fit", QREccLevel.H, new QRCodeGeneratorOptions { Version = 1 });
        var micro = (MicroQRCodeData)Symbol("M4");
        var rm = (RmQRCodeData)Symbol("R17x139");
        var small = new SymbolImageOptions { IconSizeModules = 1, IconBorderModules = 0 };

        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.GetImageSize(v1, new SymbolImageOptions { IconSizeModules = 7 }));
        await Assert.That(refused.ParamName).IsEqualTo("options");
        await Assert.That(() => SymbolImageWriter.GetImageSize(micro, small)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.GetImageSize(rm, small)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.GetImageSize(v1, new SymbolImageOptions { IconImage = new byte[8] })).Throws<ArgumentOutOfRangeException>()
            .Because("an icon image needs an icon area");

        // The same icon on a symbol it fits.
        await Assert.That(Pair(SymbolImageWriter.GetImageSize((QRCodeData)Symbol("QR5 H"), new SymbolImageOptions { IconSizeModules = 7 }))).IsEqualTo((360, 360));
    }

    // ---- the calls by symbology -----------------------------------------------------------

    private static object Symbol(string name) => name switch
    {
        "QR3" => QRCodeGenerator.Create("image size", QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 }),
        "QR5 H" => QRCodeGenerator.Create("image size", QREccLevel.H, new QRCodeGeneratorOptions { Version = 5 }),
        "QR40" => QRCodeGenerator.Create(new string('a', 2000), QREccLevel.M, new QRCodeGeneratorOptions { Version = 40 }),
        "M4" => MicroQRCodeGenerator.Create("HELLO123", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M4 }),
        "R17x139" => RmQRCodeGenerator.Create("HELLO WORLD 1234567890", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 }),
        _ => throw new ArgumentException(name),
    };

    private static SymbolImageOptions Options(string name) => name switch
    {
        "default" => default,
        "module 1" => new SymbolImageOptions { ModulePixelSize = 1 },
        "module 3" => new SymbolImageOptions { ModulePixelSize = 3 },
        "styled" => Shaped,
        // 778 is whole pixels for none of the four symbols: 37, 185, 21 and 143 modules across.
        "fraction" => new SymbolImageOptions { Width = 778, Height = 778, FillCanvas = true },
        "fraction padded" => new SymbolImageOptions { Width = 1000, Height = 778, FillCanvas = true },
        "whole padded" => new SymbolImageOptions { Width = 1000, Height = 778 },
        "width only" => new SymbolImageOptions { Width = 1000 },
        "height only" => new SymbolImageOptions { Height = 500 },
        "styled fraction" => Shaped with { Width = 1000, Height = 778, FillCanvas = true },
        "styled whole padded" => Shaped with { Width = 1000, Height = 778 },
        "icon" => new SymbolImageOptions { IconSizeModules = 7 },
        "icon fraction" => new SymbolImageOptions { IconSizeModules = 7, Width = 500, Height = 400, FillCanvas = true },
        "icon styled" => Shaped with { IconSizeModules = 7, Width = 500 },
        _ => throw new ArgumentException(name),
    };

    private static (int Width, int Height) SizeOf(object data, SymbolImageOptions options) => Pair(data switch
    {
        QRCodeData qr => SymbolImageWriter.GetImageSize(qr, options),
        MicroQRCodeData micro => SymbolImageWriter.GetImageSize(micro, options),
        RmQRCodeData rm => SymbolImageWriter.GetImageSize(rm, options),
        _ => throw new ArgumentException(nameof(data)),
    });

    private static (int Width, int Height) Pair(ImageSize size) => (size.Width, size.Height);

    private static int PixelBufferSize(object data, SymbolImageOptions options) => data switch
    {
        QRCodeData qr => SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Gray8, options),
        MicroQRCodeData micro => SymbolImageWriter.GetPixelBufferSize(micro, PixelLayout.Gray8, options),
        RmQRCodeData rm => SymbolImageWriter.GetPixelBufferSize(rm, PixelLayout.Gray8, options),
        _ => throw new ArgumentException(nameof(data)),
    };

    private static (bool Written, int Width, int Height) TryWritePixels(object data, byte[] destination, SymbolImageOptions options)
    {
        bool written;
        int w, h;
        switch (data)
        {
            case QRCodeData qr: written = SymbolImageWriter.TryWritePixels(qr, destination, PixelLayout.Gray8, out w, out h, options); break;
            case MicroQRCodeData micro: written = SymbolImageWriter.TryWritePixels(micro, destination, PixelLayout.Gray8, out w, out h, options); break;
            case RmQRCodeData rm: written = SymbolImageWriter.TryWritePixels(rm, destination, PixelLayout.Gray8, out w, out h, options); break;
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
