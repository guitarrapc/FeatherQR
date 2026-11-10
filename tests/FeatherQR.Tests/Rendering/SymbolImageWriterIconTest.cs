using System.Buffers;
using System.Xml.Linq;
using FeatherQR.SkiaSharp;
using SkiaSharp;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The icon area of a Standard QR code: a centred square of modules with a border, cleared to the light colour in every format, its
/// rectangle in pixels reported for the caller to draw into, and an image the caller gives embedded in the SVG.
/// </summary>
public class SymbolImageWriterIconTest
{
    private static readonly SymbolColor Navy = new(0x1E, 0x3A, 0x8A);
    private static readonly SymbolColor Ivory = new(0xFF, 0xFF, 0xF0);
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    // ---- the options ----------------------------------------------------------------------

    [Test]
    public async Task Defaults_HaveNoIcon_OneModuleOfBorder_AndThirtyPercentOfTheCore()
    {
        var options = default(SymbolImageOptions);
        await Assert.That(options.IconSizeModules).IsEqualTo(0);
        await Assert.That(options.IconBorderModules).IsEqualTo(1);
        await Assert.That(options.MaxCoreOccupancyPercent).IsEqualTo(30);
        await Assert.That(options.IconImage.IsEmpty).IsTrue();
        await Assert.That(options.IconMediaType).IsNull();
        await Assert.That(new SymbolImageOptions { IconSizeModules = 0, IconBorderModules = 1, MaxCoreOccupancyPercent = 30 }).IsEqualTo(options);
    }

    [Test]
    public async Task OutOfRangeValues_Throw()
    {
        await Assert.That(() => new SymbolImageOptions { IconSizeModules = -1 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SymbolImageOptions { IconBorderModules = -1 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SymbolImageOptions { MaxCoreOccupancyPercent = 0 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SymbolImageOptions { MaxCoreOccupancyPercent = 101 }).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// An icon image whose base64 would make the document longer than an array is refused naming the options. The memory reports its
    /// length without holding the bytes, so the refusal has to come from the length alone, before anything is rented.
    /// </summary>
    [Test]
    public async Task IconImage_LongerThanAnArrayCanHold_IsRefused()
    {
        var qr = QRCodeGenerator.Create("https://github.com/guitarrapc/FeatherQR", QREccLevel.H);
        var options = new SymbolImageOptions { IconSizeModules = 7, IconImage = new LengthOnlyMemory(1_700_000_000).Memory };
        var thrown = await Assert.That(() => SymbolImageWriter.GetSvgString(qr, options)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(thrown!.ParamName).IsEqualTo("options");
        await Assert.That(() => SymbolImageWriter.WriteSvg(qr, new ArrayBufferWriter<byte>(), options)).Throws<ArgumentOutOfRangeException>();
    }

    private sealed class LengthOnlyMemory(int length) : MemoryManager<byte>
    {
        public override Memory<byte> Memory => CreateMemory(length);

        public override Span<byte> GetSpan() => throw new InvalidOperationException("The bytes are never read before the refusal.");

        public override MemoryHandle Pin(int elementIndex = 0) => throw new InvalidOperationException("The bytes are never read before the refusal.");

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }

    /// <summary>An icon that does not fit is refused by every call, as the rendering package refuses it, and Micro QR and rMQR take none.</summary>
    [Test]
    public async Task AnIconThatDoesNotFit_OrOnMicroQrOrRmQr_Throws()
    {
        var v1 = QRCodeGenerator.Create("fit", QREccLevel.H, new QRCodeGeneratorOptions { Version = 1 });
        // 7 modules and a border of 1 are 9 of the 21, 43 percent, over the 30 allowed.
        var tooLarge = new SymbolImageOptions { IconSizeModules = 7 };
        var writer = new ArrayBufferWriter<byte>();
        // The symbol is 29 modules across with its quiet zone, so the message names the 21 the limit is taken from.
        var occupancy = await Assert.That(() => SymbolImageWriter.GetPngBytes(v1, tooLarge)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(occupancy!.Message).Contains("30 percent of the symbol's 21 modules without its quiet zone allows 6");
        await Assert.That(() => SymbolImageWriter.WritePng(v1, writer, tooLarge)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.GetSvgString(v1, tooLarge)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.WriteSvg(v1, writer, tooLarge)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.GetPixelBufferSize(v1, PixelLayout.Gray8, tooLarge)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.TryWritePixels(v1, new byte[1 << 16], PixelLayout.Gray8, out _, out _, tooLarge)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.GetIconRectangle(v1, tooLarge)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(writer.WrittenCount).IsEqualTo(0);

        // Allowed up to half the core, 9 of 21 is under that, but its first row is the finder patterns' last, which nothing could read.
        await Assert.That(() => SymbolImageWriter.GetPngBytes(v1, tooLarge with { MaxCoreOccupancyPercent = 50 })).Throws<ArgumentOutOfRangeException>();
        // Without the border the 7 modules start on row 7, just past the finder patterns.
        await Assert.That(SymbolImageWriter.GetPngBytes(v1, tooLarge with { MaxCoreOccupancyPercent = 50, IconBorderModules = 0 }).Length).IsGreaterThan(0);
        // Larger than the core itself.
        var larger = await Assert.That(() => SymbolImageWriter.GetPngBytes(v1, new SymbolImageOptions { IconSizeModules = 21, MaxCoreOccupancyPercent = 100 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(larger!.Message).Contains("more than the symbol's 21 modules without its quiet zone");
        await Assert.That(() => SymbolImageWriter.GetIconRectangle(v1, default)).Throws<ArgumentOutOfRangeException>().Because("no icon is set");

        var micro = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L);
        var rm = RmQRCodeGenerator.Create("fit", RmQREccLevel.M);
        var small = new SymbolImageOptions { IconSizeModules = 1, IconBorderModules = 0 };
        await Assert.That(() => SymbolImageWriter.GetPngBytes(micro, small)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.GetSvgString(rm, small)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.GetPixelBufferSize(micro, PixelLayout.Gray8, small)).Throws<ArgumentOutOfRangeException>();
    }

    // ---- the cleared area -----------------------------------------------------------------

    /// <summary>
    /// The icon and its border are the light colour in every format, snapped to the module grid as the rendering package snaps them,
    /// and nothing outside the area changes. The rectangle reported is the rendering package's for the same modules.
    /// </summary>
    [Test]
    [Arguments(7, 1, 10)]
    [Arguments(8, 0, 10)]
    [Arguments(5, 2, 3)]
    public async Task IconArea_IsLight_InEveryFormat_AndTheRectangle_IsTheRenderers(int iconModules, int borderModules, int pixels)
    {
        var qr = QRCodeGenerator.Create("icon area", QREccLevel.H, new QRCodeGeneratorOptions { Version = 5, QuietZoneSize = 4 });
        var core = qr.GetCoreSize();
        var options = new SymbolImageOptions { ModulePixelSize = pixels, IconSizeModules = iconModules, IconBorderModules = borderModules, DarkColor = Navy, LightColor = Ivory };
        var plain = options with { IconSizeModules = 0 };
        var origin = (core - iconModules) / 2;
        var (clearRow, clearColumn, clearSize) = (origin - borderModules, origin - borderModules, iconModules + 2 * borderModules);

        var pixelsWithIcon = Pixels(qr, options, PixelLayout.Bgra8888);
        var pixelsPlain = Pixels(qr, plain, PixelLayout.Bgra8888);
        var width = qr.Size * pixels;
        for (var y = 0; y < qr.Size * pixels; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var row = y / pixels - 4;
                var column = x / pixels - 4;
                var inside = row >= clearRow && row < clearRow + clearSize && column >= clearColumn && column < clearColumn + clearSize;
                var at = (y * width + x) * 4;
                var pixel = pixelsWithIcon.AsSpan(at, 4).ToArray();
                if (inside)
                    await Assert.That(pixel).IsEquivalentTo(new byte[] { 0xF0, 0xFF, 0xFF, 255 }, CollectionOrdering.Matching).Because($"({x}, {y}) is in the icon area");
                else
                    await Assert.That(pixel).IsEquivalentTo(pixelsPlain.AsSpan(at, 4).ToArray(), CollectionOrdering.Matching).Because($"({x}, {y}) is outside the icon area");
            }
        }

        await Assert.That(DecodePng(SymbolImageWriter.GetPngBytes(qr, options))).IsEquivalentTo(pixelsWithIcon, CollectionOrdering.Matching);
        await Assert.That(SymbolImageWriter.GetPngBytes(qr, options)[24]).IsEqualTo((byte)1).Because("the icon area keeps the PNG at one bit");

        var runs = PathModules(XDocument.Parse(SymbolImageWriter.GetSvgString(qr, options)).Root!.Descendants(Svg + "path").Single().Attribute("d")!.Value, 4);
        var expected = new HashSet<(int, int)>();
        for (var row = 0; row < core; row++)
            for (var column = 0; column < core; column++)
                if (qr.GetCoreModule(row, column) && !(row >= clearRow && row < clearRow + clearSize && column >= clearColumn && column < clearColumn + clearSize))
                    expected.Add((row, column));
        await Assert.That(runs).IsEquivalentTo(expected, CollectionOrdering.Any);

        ImageRect rectangle = SymbolImageWriter.GetIconRectangle(qr, options);
        await Assert.That((rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height)).IsEqualTo(((4 + origin) * pixels, (4 + origin) * pixels, iconModules * pixels, iconModules * pixels));
        using var bitmap = new SKBitmap(4, 4);
        var (iconRect, _) = SymbolRenderer.GetIconRects(qr, SKRect.Create(0, 0, width, width), new IconData(new ImageIconShape(bitmap), iconSizeModules: iconModules, iconBorderModules: borderModules));
        await Assert.That((iconRect.Left, iconRect.Top, iconRect.Width, iconRect.Height)).IsEqualTo(((float)rectangle.X, (float)rectangle.Y, (float)rectangle.Width, (float)rectangle.Height));
    }

    /// <summary>
    /// The rectangle is an <see cref="ImageRect"/>: its members by name, split in the order x, y, width, height. On a canvas wider than
    /// tall the symbol is padded on its left, so the left edge differs from the top one and a split that exchanged them shows.
    /// </summary>
    [Test]
    public async Task IconRectangle_NamesItsMembers_AndSplitsAsXYWidthHeight()
    {
        var qr = QRCodeGenerator.Create("https://github.com/guitarrapc/FeatherQR", QREccLevel.H);
        var options = new SymbolImageOptions { Width = 500, Height = 400, IconSizeModules = 7 };

        var rectangle = SymbolImageWriter.GetIconRectangle(qr, options);
        var (x, y, width, height) = rectangle;

        await Assert.That(rectangle.X).IsNotEqualTo(rectangle.Y);
        await Assert.That((x, y, width, height)).IsEqualTo((rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height));
        await Assert.That(rectangle == SymbolImageWriter.GetIconRectangle(qr, options)).IsTrue();
        await Assert.That(rectangle != SymbolImageWriter.GetIconRectangle(qr, options with { IconSizeModules = 5 })).IsTrue();
        await Assert.That(rectangle.ToString()).IsEqualTo($"ImageRect {{ X = {rectangle.X}, Y = {rectangle.Y}, Width = {rectangle.Width}, Height = {rectangle.Height} }}");
    }

    /// <summary>
    /// The split's order, on a rectangle whose four members all differ. An icon is square, so no rectangle the writer returns can
    /// tell width from height.
    /// </summary>
    [Test]
    public async Task ImageRect_SplitsItsFourMembersInOrder()
    {
        var (x, y, width, height) = new ImageRect(1, 2, 3, 4);

        await Assert.That((x, y, width, height)).IsEqualTo((1, 2, 3, 4));
        await Assert.That(new ImageRect(1, 2, 3, 4).ToString()).IsEqualTo("ImageRect { X = 1, Y = 2, Width = 3, Height = 4 }");
        await Assert.That(new ImageRect(1, 2, 3, 4) != new ImageRect(1, 2, 4, 3)).IsTrue();
    }

    /// <summary>
    /// At a set size and with a style, the area is still the light colour and the rectangle lands on the cells the layout gives: 45 modules
    /// over 400 pixels are 8 each, or 8.9 when filling, where the 7 cells are 8 or 9 pixels.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IconArea_AtASetSize_AndStyled_IsLight_AndTheRectangle_IsOnTheGrid(bool fillCanvas)
    {
        var qr = QRCodeGenerator.Create("icon area", QREccLevel.H, new QRCodeGeneratorOptions { Version = 5 });
        var options = new SymbolImageOptions { Width = 500, Height = 400, FillCanvas = fillCanvas, IconSizeModules = 7, ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 90, DarkColor = Navy, LightColor = Ivory };
        var pixels = Pixels(qr, options, PixelLayout.Rgba8888);
        var (x, y, width, height) = SymbolImageWriter.GetIconRectangle(qr, options);

        if (fillCanvas)
            await Assert.That(width).IsBetween(7 * 8, 7 * 9);
        else
            await Assert.That(width).IsEqualTo(7 * 8);
        for (var py = y; py < y + height; py++)
            for (var px = x; px < x + width; px++)
                await Assert.That(pixels.AsSpan((py * 500 + px) * 4, 4).ToArray()).IsEquivalentTo(new byte[] { 0xFF, 0xFF, 0xF0, 255 }, CollectionOrdering.Matching).Because($"({px}, {py})");
        await Assert.That(DecodePng(SymbolImageWriter.GetPngBytes(qr, options))).IsEquivalentTo(Pixels(qr, options, PixelLayout.Bgra8888), CollectionOrdering.Matching);
    }

    [Test]
    public async Task WithAnIconArea_TheSymbolStillDecodes()
    {
        foreach (var level in new[] { QREccLevel.H, QREccLevel.Q })
        {
            var qr = QRCodeGenerator.Create("https://github.com/guitarrapc/FeatherQR", level, new QRCodeGeneratorOptions { Version = 7 });
            var options = new SymbolImageOptions { ModulePixelSize = 8, IconSizeModules = 9 };
            var gray = Pixels(qr, options, PixelLayout.Gray8);
            await Assert.That(QRCodeDecoder.TryDecodeImage(gray, qr.Size * 8, qr.Size * 8, out string text, out var info)).IsTrue().Because($"{level}: {info.Status}");
            await Assert.That(text).IsEqualTo("https://github.com/guitarrapc/FeatherQR");
        }
    }

    // ---- the SVG image --------------------------------------------------------------------

    [Test]
    public async Task Svg_EmbedsTheIconImage_AsADataUri_OverTheIconRectangle()
    {
        var qr = QRCodeGenerator.Create("icon image", QREccLevel.H, new QRCodeGeneratorOptions { Version = 5, QuietZoneSize = 4 });
        var icon = SymbolImageWriter.GetPngBytes(MicroQRCodeGenerator.Create("1", MicroQREccLevel.L));
        var options = new SymbolImageOptions { IconSizeModules = 7, IconImage = icon };
        var origin = (qr.GetCoreSize() - 7) / 2 + 4;

        var root = XDocument.Parse(SymbolImageWriter.GetSvgString(qr, options)).Root!;
        var image = root.Descendants(Svg + "image").Single();
        await Assert.That(image.Attribute("x")?.Value).IsEqualTo(origin.ToString());
        await Assert.That(image.Attribute("y")?.Value).IsEqualTo(origin.ToString());
        await Assert.That(image.Attribute("width")?.Value).IsEqualTo("7");
        await Assert.That(image.Attribute("height")?.Value).IsEqualTo("7");
        await Assert.That(image.Attribute("preserveAspectRatio")?.Value).IsEqualTo("none");
        var href = image.Attribute("href")!.Value;
        await Assert.That(href.StartsWith("data:image/png;base64,", StringComparison.Ordinal)).IsTrue();
        await Assert.That(Convert.FromBase64String(href.Substring("data:image/png;base64,".Length))).IsEquivalentTo(icon, CollectionOrdering.Matching);
        await Assert.That(root.Elements().Last().Name.LocalName).IsEqualTo("image").Because("the image is drawn over the modules");

        var svg = XDocument.Parse(SymbolImageWriter.GetSvgString(qr, options with { IconMediaType = "image/svg+xml" })).Root!.Descendants(Svg + "image").Single();
        await Assert.That(svg.Attribute("href")!.Value.StartsWith("data:image/svg+xml;base64,", StringComparison.Ordinal)).IsTrue();

        // Without an image the area is only cleared.
        await Assert.That(XDocument.Parse(SymbolImageWriter.GetSvgString(qr, options with { IconImage = default })).Root!.Descendants(Svg + "image").Count()).IsEqualTo(0);
        // An image with no area to put it in is refused.
        await Assert.That(() => SymbolImageWriter.GetSvgString(qr, new SymbolImageOptions { IconImage = icon })).Throws<ArgumentOutOfRangeException>();
        // A fully transparent dark colour draws no modules, and the image still goes in.
        var transparentDark = XDocument.Parse(SymbolImageWriter.GetSvgString(qr, options with { DarkColor = new SymbolColor(0, 0, 0, 0) })).Root!;
        await Assert.That(transparentDark.Descendants(Svg + "path").Count()).IsEqualTo(0);
        await Assert.That(transparentDark.Descendants(Svg + "image").Count()).IsEqualTo(1);
    }

    [Test]
    public async Task WriteSvg_AndWritePng_WithAnIcon_WriteTheStringAndTheArray()
    {
        var qr = QRCodeGenerator.Create("icon", QREccLevel.H, new QRCodeGeneratorOptions { Version = 5 });
        var icon = SymbolImageWriter.GetPngBytes(MicroQRCodeGenerator.Create("1", MicroQREccLevel.L));
        var options = new SymbolImageOptions { IconSizeModules = 7, IconImage = icon, ModuleStyle = ModuleStyle.RoundedSquare };
        var writer = new ArrayBufferWriter<byte>();

        var svgLength = SymbolImageWriter.WriteSvg(qr, writer, options);
        await Assert.That(writer.WrittenSpan.ToArray()).IsEquivalentTo(System.Text.Encoding.UTF8.GetBytes(SymbolImageWriter.GetSvgString(qr, options)), CollectionOrdering.Matching);
        await Assert.That(svgLength).IsEqualTo(writer.WrittenCount);

        writer.Clear();
        var pngLength = SymbolImageWriter.WritePng(qr, writer, options);
        await Assert.That(writer.WrittenSpan.ToArray()).IsEquivalentTo(SymbolImageWriter.GetPngBytes(qr, options), CollectionOrdering.Matching);
        await Assert.That(pngLength).IsEqualTo(writer.WrittenCount);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static byte[] Pixels(QRCodeData qr, SymbolImageOptions options, PixelLayout layout)
    {
        var buffer = new byte[SymbolImageWriter.GetPixelBufferSize(qr, layout, options)];
        if (!SymbolImageWriter.TryWritePixels(qr, buffer, layout, out _, out _, options))
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
                x = int.Parse(tokens[i++], System.Globalization.CultureInfo.InvariantCulture);
                y = (int)double.Parse(tokens[i++], System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (command == "m")
            {
                x += int.Parse(tokens[i++], System.Globalization.CultureInfo.InvariantCulture);
                y += int.Parse(tokens[i++], System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                var length = int.Parse(tokens[i++], System.Globalization.CultureInfo.InvariantCulture);
                for (var k = 0; k < length; k++)
                    modules.Add((y - quietZone, x + k - quietZone));
                x += length;
            }
        }
        return modules;
    }
}
