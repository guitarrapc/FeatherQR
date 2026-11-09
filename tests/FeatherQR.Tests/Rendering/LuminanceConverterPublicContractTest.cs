using System.Reflection;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The public shape and contract of <see cref="LuminanceConverter.Convert"/>, the one call a consumer makes
/// to turn browser-canvas or OS pixels into the luminance <c>TryDecodeImage</c> reads.
/// The shape is held by reflection: the type is public in the <c>FeatherQR</c> namespace and the conversion is
/// its only public member. The contract is held through the call: BT.601 weights, alpha composited against
/// white, premultiplied alpha, a stride wider than the row, a packed destination, a zero-size no-op, and the
/// documented exception for each argument error, including dimensions whose product does not fit an int.
/// Tier parity is <see cref="LuminanceConverterParityTest"/>, and the first-party adapter is
/// <see cref="LuminanceConverterContractTest"/>.
/// </summary>
public class LuminanceConverterPublicContractTest
{
    private static readonly PixelLayout[] ColorLayouts = [PixelLayout.Rgba8888, PixelLayout.Bgra8888, PixelLayout.Rgb888x, PixelLayout.Rgb24, PixelLayout.Bgr24];
    private static readonly PixelLayout[] AlphaLayouts = [PixelLayout.Rgba8888, PixelLayout.Bgra8888];
    private static readonly PixelLayout[] AllLayouts = [PixelLayout.Gray8, PixelLayout.Rgba8888, PixelLayout.Bgra8888, PixelLayout.Rgb888x, PixelLayout.Rgb24, PixelLayout.Bgr24];
    private static readonly PixelLayout[] NoAlphaLayouts = [PixelLayout.Rgb888x, PixelLayout.Rgb24, PixelLayout.Bgr24];

    private static int BytesPerPixel(PixelLayout layout) => layout switch
    {
        PixelLayout.Gray8 => 1,
        PixelLayout.Rgb24 or PixelLayout.Bgr24 => 3,
        _ => 4,
    };

    /// <summary>The documented formula: composite against white below alpha 255, then the integer part of (77 R + 150 G + 29 B) / 256.</summary>
    private static byte DocumentedLuma(int r, int g, int b, int a, bool premultiplied)
    {
        if (a != 255)
        {
            if (premultiplied)
            {
                r += 255 - a;
                g += 255 - a;
                b += 255 - a;
            }
            else
            {
                r = (r * a + 255 * (255 - a)) / 255;
                g = (g * a + 255 * (255 - a)) / 255;
                b = (b * a + 255 * (255 - a)) / 255;
            }
        }
        return (byte)((77 * r + 150 * g + 29 * b) / 256);
    }

    private static void WritePixel(Span<byte> pixel, PixelLayout layout, byte r, byte g, byte b, byte a)
    {
        switch (layout)
        {
            case PixelLayout.Rgba8888:
                pixel[0] = r; pixel[1] = g; pixel[2] = b; pixel[3] = a;
                break;
            case PixelLayout.Bgra8888:
                pixel[0] = b; pixel[1] = g; pixel[2] = r; pixel[3] = a;
                break;
            case PixelLayout.Rgb888x:
                pixel[0] = r; pixel[1] = g; pixel[2] = b; pixel[3] = 0x5A;
                break;
            case PixelLayout.Rgb24:
                pixel[0] = r; pixel[1] = g; pixel[2] = b;
                break;
            case PixelLayout.Bgr24:
                pixel[0] = b; pixel[1] = g; pixel[2] = r;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(layout));
        }
    }

    private static byte ConvertOne(PixelLayout layout, byte r, byte g, byte b, byte a, bool premultiplied = false)
    {
        var pixels = new byte[BytesPerPixel(layout)];
        WritePixel(pixels, layout, r, g, b, a);
        var luminance = new byte[1];
        LuminanceConverter.Convert(pixels, 1, 1, pixels.Length, layout, premultiplied, luminance);
        return luminance[0];
    }

    // ---- shape ------------------------------------------------------------------------

    /// <summary>
    /// The type's documentation is the contract alone. The class is partial over its kernel files, and a doc comment on any of those
    /// parts would be merged into the public type's documentation and shown on the API page.
    /// </summary>
    [Test]
    public async Task TypeDocumentation_IsOneSummary_AboutTheContract()
    {
        var xml = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FeatherQR.xml"));
        var member = xml.Descendants("member").Single(m => m.Attribute("name")?.Value == "T:FeatherQR.LuminanceConverter");
        var summaries = member.Elements("summary").ToArray();

        await Assert.That(summaries.Length).IsEqualTo(1);
        await Assert.That(summaries[0].Value.Trim()).StartsWith("Converts");
    }

    /// <summary>
    /// A zero width or height converts nothing and touches neither buffer, in every layout, as the documentation says. The destination
    /// starts at a value no conversion writes for a zero source, so a write of any kind would show.
    /// </summary>
    [Test]
    public async Task ZeroWidthOrHeight_TouchesNothing_InEveryLayout()
    {
        foreach (var layout in AllLayouts)
        {
            LuminanceConverter.Convert(ReadOnlySpan<byte>.Empty, 0, 3, 100, layout, false, Span<byte>.Empty);
            LuminanceConverter.Convert(ReadOnlySpan<byte>.Empty, 5, 0, 100, layout, false, Span<byte>.Empty);
            byte[] untouched = [0xAB, 0xAB, 0xAB, 0xAB];
            LuminanceConverter.Convert(new byte[8], 0, 2, 4, layout, false, untouched);
            await Assert.That(untouched).IsEquivalentTo(new byte[] { 0xAB, 0xAB, 0xAB, 0xAB }, CollectionOrdering.Matching).Because($"{layout}");
        }
    }

    /// <summary>A negative stride is refused whether or not there is a row to read, as the exception tag says.</summary>
    [Test]
    public async Task NegativeRowBytes_Throws_AtAZeroSizeToo()
    {
        foreach (var layout in AllLayouts)
        {
            foreach (var (width, height) in new[] { (0, 3), (5, 0), (0, 0) })
            {
                var thrown = await Assert.That(() => LuminanceConverter.Convert(ReadOnlySpan<byte>.Empty, width, height, -1, layout, false, Span<byte>.Empty))
                    .Throws<ArgumentOutOfRangeException>().Because($"{layout} at {width} by {height}");
                await Assert.That(thrown!.ParamName).IsEqualTo("rowBytes");
            }
        }
    }

    [Test]
    public async Task Type_IsAPublicStaticClassInTheRootNamespace()
    {
        var type = typeof(LuminanceConverter);

        await Assert.That(type.IsPublic).IsTrue();
        await Assert.That(type.Namespace).IsEqualTo("FeatherQR");
        await Assert.That(type.IsAbstract && type.IsSealed).IsTrue().Because("a static class");
    }

    [Test]
    public async Task Convert_IsTheOnlyPublicMember()
    {
        var members = typeof(LuminanceConverter)
            .GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToArray();

        await Assert.That(members).IsEquivalentTo(new[] { "Convert" }, CollectionOrdering.Matching);

        var convert = typeof(LuminanceConverter).GetMethod("Convert", BindingFlags.Public | BindingFlags.Static);
        await Assert.That(convert).IsNotNull();
        var parameters = convert!.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}").ToArray();
        await Assert.That(parameters).IsEquivalentTo(
            new[] { "ReadOnlySpan`1 pixels", "Int32 width", "Int32 height", "Int32 rowBytes", "PixelLayout layout", "Boolean premultipliedAlpha", "Span`1 luminance" },
            CollectionOrdering.Matching);
    }

    // ---- what the bytes mean ----------------------------------------------------------

    /// <summary>Pure channels at full alpha. The integer division truncates, so pure red is 76, not 77.</summary>
    [Test]
    public async Task PureChannels_WeighByBt601()
    {
        foreach (var layout in ColorLayouts)
        {
            await Assert.That(ConvertOne(layout, 255, 0, 0, 255)).IsEqualTo((byte)76).Because($"{layout} red");
            await Assert.That(ConvertOne(layout, 0, 255, 0, 255)).IsEqualTo((byte)149).Because($"{layout} green");
            await Assert.That(ConvertOne(layout, 0, 0, 255, 255)).IsEqualTo((byte)28).Because($"{layout} blue");
            await Assert.That(ConvertOne(layout, 255, 255, 255, 255)).IsEqualTo((byte)255).Because($"{layout} white");
            await Assert.That(ConvertOne(layout, 0, 0, 0, 255)).IsEqualTo((byte)0).Because($"{layout} black");
        }
    }

    [Test]
    public async Task Gray8_PassesTheBytesThrough()
    {
        byte[] pixels = [0, 1, 127, 128, 254, 255];
        var luminance = new byte[6];

        LuminanceConverter.Convert(pixels, 6, 1, 6, PixelLayout.Gray8, false, luminance);

        await Assert.That(luminance).IsEquivalentTo(pixels, CollectionOrdering.Matching);
    }

    /// <summary>An all-zero RGBA buffer is fully transparent, so it is white. Without alpha the same bytes are black.</summary>
    [Test]
    public async Task AllZeroBuffer_IsWhiteWhereThereIsAlpha()
    {
        const int width = 20, height = 3;
        foreach (var layout in ColorLayouts)
        {
            var luminance = new byte[width * height];

            var bpp = BytesPerPixel(layout);
            LuminanceConverter.Convert(new byte[width * height * bpp], width, height, width * bpp, layout, false, luminance);

            var expected = NoAlphaLayouts.Contains(layout) ? (byte)0 : (byte)255;
            await Assert.That(luminance.All(v => v == expected)).IsTrue().Because($"{layout}: {string.Join(",", luminance)}");
        }
    }

    [Test]
    public async Task PartialAlpha_CompositesAgainstWhite()
    {
        foreach (var layout in AlphaLayouts)
        {
            // Half-transparent black over white is mid grey, and the BT.601 weights sum to 256, so it stays 127.
            await Assert.That(ConvertOne(layout, 0, 0, 0, 128)).IsEqualTo((byte)127).Because($"{layout} half black");
            await Assert.That(ConvertOne(layout, 200, 30, 90, 77)).IsEqualTo(DocumentedLuma(200, 30, 90, 77, premultiplied: false)).Because($"{layout} partial");
        }

        // Rgb888x has no alpha, so its fourth byte changes nothing.
        await Assert.That(ConvertOne(PixelLayout.Rgb888x, 0, 0, 0, 0)).IsEqualTo((byte)0);
    }

    /// <summary>The same bytes read as premultiplied and as straight alpha give different luminance, so the flag is not decoration.</summary>
    [Test]
    public async Task PremultipliedAlpha_AddsTheMissingWhite()
    {
        foreach (var layout in AlphaLayouts)
        {
            var premultiplied = ConvertOne(layout, 100, 15, 45, 128, premultiplied: true);
            var straight = ConvertOne(layout, 100, 15, 45, 128, premultiplied: false);

            await Assert.That(premultiplied).IsEqualTo(DocumentedLuma(100, 15, 45, 128, premultiplied: true)).Because($"{layout}");
            await Assert.That(straight).IsEqualTo(DocumentedLuma(100, 15, 45, 128, premultiplied: false)).Because($"{layout}");
            await Assert.That(premultiplied).IsNotEqualTo(straight).Because($"{layout}");
        }
    }

    [Test]
    public async Task PremultipliedFlag_IsIgnoredWithoutAlpha()
    {
        foreach (var layout in NoAlphaLayouts)
        {
            await Assert.That(ConvertOne(layout, 200, 100, 50, 7, premultiplied: true))
                .IsEqualTo(ConvertOne(layout, 200, 100, 50, 7, premultiplied: false)).Because($"{layout}");
        }

        byte[] gray = [123];
        var luminance = new byte[1];
        LuminanceConverter.Convert(gray, 1, 1, 1, PixelLayout.Gray8, true, luminance);
        await Assert.That(luminance[0]).IsEqualTo((byte)123);
    }

    // ---- three bytes a pixel -----------------------------------------------------------

    /// <summary>The three bytes in memory order: red is the first byte of Rgb24 and the third of Bgr24.</summary>
    [Test]
    public async Task Rgb24AndBgr24_ReadTheirBytesInMemoryOrder()
    {
        var luminance = new byte[1];

        LuminanceConverter.Convert([255, 0, 0], 1, 1, 3, PixelLayout.Rgb24, false, luminance);
        await Assert.That(luminance[0]).IsEqualTo((byte)76).Because("Rgb24, the first byte is red");
        LuminanceConverter.Convert([0, 0, 255], 1, 1, 3, PixelLayout.Rgb24, false, luminance);
        await Assert.That(luminance[0]).IsEqualTo((byte)28).Because("Rgb24, the third byte is blue");

        LuminanceConverter.Convert([255, 0, 0], 1, 1, 3, PixelLayout.Bgr24, false, luminance);
        await Assert.That(luminance[0]).IsEqualTo((byte)28).Because("Bgr24, the first byte is blue");
        LuminanceConverter.Convert([0, 0, 255], 1, 1, 3, PixelLayout.Bgr24, false, luminance);
        await Assert.That(luminance[0]).IsEqualTo((byte)76).Because("Bgr24, the third byte is red");
    }

    /// <summary>
    /// A 24-bit buffer gives the luminance of the same colours held as Rgb888x, and the documented formula, on random colours.
    /// The widths go one pixel at a time past two blocks of the widest vector tier, and the rows are packed, padded by a byte count
    /// that is not a whole pixel, and padded to four bytes. The padding holds 0xEE, which no row of the source may take a colour from.
    /// </summary>
    [Test]
    public async Task Rgb24AndBgr24_ReadTheColoursRgb888xReads()
    {
        var random = new Random(24);
        const int height = 3;
        for (var width = 1; width <= 140; width++)
        {
            foreach (var padding in new[] { 0, 1, 5, (4 - width * 3 % 4) % 4 })
            {
                var colours = new byte[width * height * 3];
                random.NextBytes(colours);
                var four = new byte[width * height * 4];
                var expected = new byte[width * height];
                for (var pixel = 0; pixel < width * height; pixel++)
                {
                    WritePixel(four.AsSpan(pixel * 4, 4), PixelLayout.Rgb888x, colours[pixel * 3], colours[pixel * 3 + 1], colours[pixel * 3 + 2], 255);
                    expected[pixel] = DocumentedLuma(colours[pixel * 3], colours[pixel * 3 + 1], colours[pixel * 3 + 2], 255, premultiplied: false);
                }
                var fromFour = new byte[width * height];
                LuminanceConverter.Convert(four, width, height, width * 4, PixelLayout.Rgb888x, false, fromFour);
                await Assert.That(fromFour.AsSpan().SequenceEqual(expected)).IsTrue().Because("the four-byte reading is the documented formula");

                foreach (var layout in new[] { PixelLayout.Rgb24, PixelLayout.Bgr24 })
                {
                    var rowBytes = width * 3 + padding;
                    // The last row ends at its last pixel.
                    var pixels = new byte[(height - 1) * rowBytes + width * 3];
                    pixels.AsSpan().Fill(0xEE);
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var colour = colours.AsSpan((y * width + x) * 3, 3);
                            WritePixel(pixels.AsSpan(y * rowBytes + x * 3, 3), layout, colour[0], colour[1], colour[2], 255);
                        }
                    }
                    // A byte either side of the destination, which the conversion must leave.
                    var luminance = new byte[width * height + 2];
                    luminance[0] = 0xA5;
                    luminance[^1] = 0x5A;

                    LuminanceConverter.Convert(pixels, width, height, rowBytes, layout, false, luminance.AsSpan(1, width * height));

                    await Assert.That(luminance.AsSpan(1, width * height).SequenceEqual(expected)).IsTrue().Because($"{layout}, {width} pixels wide, {padding} bytes of padding");
                    await Assert.That((luminance[0], luminance[^1])).IsEqualTo(((byte)0xA5, (byte)0x5A)).Because($"{layout}, {width} pixels wide");
                }
            }
        }
    }

    /// <summary>
    /// A source that ends where readable memory ends and a destination that ends where writable memory ends: a conversion that loads
    /// or stores a whole vector past the last pixel faults here, where over an array it would touch the next object unseen.
    /// </summary>
    [Test]
    public async Task Rgb24AndBgr24_ReadAndWriteNothingPastTheLastPixel()
    {
        if (!PageEndMemory.IsSupported)
            return;
        using var sourcePage = new PageEndMemory();
        using var destinationPage = new PageEndMemory();
        var random = new Random(3);
        var converted = 0;
        foreach (var layout in new[] { PixelLayout.Rgb24, PixelLayout.Bgr24 })
        {
            for (var width = 1; width <= 140; width++)
            {
                foreach (var (height, padding) in new[] { (1, 0), (2, 0), (3, 5) })
                {
                    var rowBytes = width * 3 + padding;
                    var managed = new byte[(height - 1) * rowBytes + width * 3];
                    random.NextBytes(managed);
                    var expected = new byte[width * height];
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var p = y * rowBytes + x * 3;
                            expected[y * width + x] = layout == PixelLayout.Rgb24
                                ? DocumentedLuma(managed[p], managed[p + 1], managed[p + 2], 255, false)
                                : DocumentedLuma(managed[p + 2], managed[p + 1], managed[p], 255, false);
                        }
                    }

                    var matches = ConvertAtPageEnds(sourcePage, destinationPage, managed, width, height, rowBytes, layout, expected);
                    await Assert.That(matches).IsTrue().Because($"{layout}, {width} by {height}, {padding} bytes of padding");
                    converted++;
                }
            }
        }
        await Assert.That(converted).IsEqualTo(2 * 140 * 3);

        static bool ConvertAtPageEnds(PageEndMemory sourcePage, PageEndMemory destinationPage, byte[] managed, int width, int height, int rowBytes, PixelLayout layout, byte[] expected)
        {
            ReadOnlySpan<byte> source = sourcePage.AtPageEnd(managed);
            var destination = destinationPage.AtPageEnd(width * height);
            LuminanceConverter.Convert(source, width, height, rowBytes, layout, false, destination);
            return destination.SequenceEqual(expected);
        }
    }

    [Test]
    public async Task Rgb24AndBgr24_AStrideUnderThreeBytesAPixel_Throws()
    {
        foreach (var layout in new[] { PixelLayout.Rgb24, PixelLayout.Bgr24 })
        {
            // Five pixels are fifteen bytes. Fourteen is refused, and so is the 5 that would do for Gray8.
            foreach (var rowBytes in new[] { 14, 5, 0 })
            {
                var thrown = await Assert.That(() => LuminanceConverter.Convert(new byte[64], 5, 2, rowBytes, layout, false, new byte[10]))
                    .Throws<ArgumentOutOfRangeException>().Because($"{layout}, stride {rowBytes}");
                await Assert.That(thrown!.ParamName).IsEqualTo("rowBytes");
            }
            LuminanceConverter.Convert(new byte[30], 5, 2, 15, layout, false, new byte[10]);
            // Two rows of 16 with the second ending at its fifteenth byte need 31 bytes.
            LuminanceConverter.Convert(new byte[31], 5, 2, 16, layout, false, new byte[10]);
            await Assert.That(() => LuminanceConverter.Convert(new byte[30], 5, 2, 16, layout, false, new byte[10])).ThrowsExactly<ArgumentException>().Because($"{layout}");
        }
    }

    // ---- rows -------------------------------------------------------------------------

    /// <summary>The stride may exceed the row. The padding is never read, and the destination has none.</summary>
    [Test]
    public async Task StrideWiderThanTheRow_SkipsThePaddingAndPacksTheDestination()
    {
        const int width = 3, height = 2;
        foreach (var layout in AllLayouts)
        {
            var bpp = BytesPerPixel(layout);
            var rowBytes = width * bpp + 5;
            var pixels = new byte[rowBytes * height];
            pixels.AsSpan().Fill(0xEE);
            var expected = new byte[width * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var value = (byte)(40 * (y * width + x) + 10);
                    if (layout == PixelLayout.Gray8)
                        pixels[y * rowBytes + x] = value;
                    else
                        WritePixel(pixels.AsSpan(y * rowBytes + x * bpp, bpp), layout, value, value, value, 255);
                    expected[y * width + x] = value;
                }

            var luminance = new byte[width * height];
            LuminanceConverter.Convert(pixels, width, height, rowBytes, layout, false, luminance);

            await Assert.That(luminance).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"{layout}");
        }
    }

    [Test]
    public async Task Gray8_DropsThePadding()
    {
        byte[] pixels = [1, 2, 3, 0xEE, 0xEE, 4, 5, 6, 0xEE, 0xEE];
        var luminance = new byte[6];

        LuminanceConverter.Convert(pixels, 3, 2, 5, PixelLayout.Gray8, false, luminance);

        await Assert.That(luminance).IsEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6 }, CollectionOrdering.Matching);
    }

    /// <summary>The last row needs its pixels only, not a whole stride.</summary>
    [Test]
    public async Task LastRow_MayEndAfterItsPixels()
    {
        foreach (var layout in AllLayouts)
        {
            var bpp = BytesPerPixel(layout);
            const int width = 4, height = 3;
            var rowBytes = width * bpp + 3;
            var pixels = new byte[(height - 1) * rowBytes + width * bpp];
            var luminance = new byte[width * height];

            LuminanceConverter.Convert(pixels, width, height, rowBytes, layout, false, luminance);

            var expected = layout is PixelLayout.Rgba8888 or PixelLayout.Bgra8888 ? (byte)255 : (byte)0;
            await Assert.That(luminance.All(v => v == expected)).IsTrue().Because($"{layout}");
        }
    }

    [Test]
    public async Task ZeroWidthOrHeight_TouchesNeitherBuffer()
    {
        foreach (var layout in AllLayouts)
        {
            byte[] luminance = [0xAB, 0xCD];

            LuminanceConverter.Convert([], 0, 5, 0, layout, false, luminance);
            LuminanceConverter.Convert([], 5, 0, 0, layout, false, luminance);
            LuminanceConverter.Convert([], 0, 0, 0, layout, false, []);

            await Assert.That(luminance).IsEquivalentTo(new byte[] { 0xAB, 0xCD }, CollectionOrdering.Matching).Because($"{layout}");
        }
    }

    // ---- argument errors --------------------------------------------------------------

    [Test]
    public async Task UndefinedLayout_ThrowsArgumentOutOfRange()
    {
        foreach (var layout in new[] { (PixelLayout)42, (PixelLayout)6, (PixelLayout)(-1) })
        {
            var thrown = await Assert.That(() => LuminanceConverter.Convert(new byte[4], 1, 1, 4, layout, false, new byte[1]))
                .Throws<ArgumentOutOfRangeException>().Because($"{(int)layout}");
            await Assert.That(thrown!.ParamName).IsEqualTo("layout");
        }
    }

    [Test]
    [Arguments(-1, 1, 4)]
    [Arguments(1, -1, 4)]
    [Arguments(2, 1, 4)]
    [Arguments(1, 1, -4)]
    public async Task NegativeDimensionOrShortStride_ThrowsArgumentOutOfRange(int width, int height, int rowBytes)
    {
        await Assert.That(() => LuminanceConverter.Convert(new byte[64], width, height, rowBytes, PixelLayout.Rgba8888, false, new byte[16]))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ShortSource_ThrowsArgument()
    {
        foreach (var layout in AllLayouts)
        {
            var bpp = BytesPerPixel(layout);
            await Assert.That(() => LuminanceConverter.Convert(new byte[3 * 2 * bpp - 1], 3, 2, 3 * bpp, layout, false, new byte[6]))
                .Throws<ArgumentException>().Because($"{layout}");
        }
    }

    [Test]
    public async Task ShortDestination_ThrowsArgument()
    {
        foreach (var layout in AllLayouts)
        {
            var bpp = BytesPerPixel(layout);
            await Assert.That(() => LuminanceConverter.Convert(new byte[3 * 2 * bpp], 3, 2, 3 * bpp, layout, false, new byte[5]))
                .Throws<ArgumentException>().Because($"{layout}");
        }
    }

    /// <summary>
    /// A width and height whose product does not fit an int. Multiplied as ints the product wraps to a small
    /// count that a short buffer would satisfy, and the kernels walk by reference without bounds checks.
    /// </summary>
    [Test]
    [Arguments(65536, 65536)]
    [Arguments(int.MaxValue, 2)]
    [Arguments(2, int.MaxValue)]
    public async Task DimensionsPastInt_ThrowArgumentRatherThanWrap(int width, int height)
    {
        foreach (var layout in AllLayouts)
        {
            var bpp = BytesPerPixel(layout);
            if ((long)width * bpp > int.MaxValue)
                continue; // no int stride covers such a row, so that case is the stride check's
            await Assert.That(() => LuminanceConverter.Convert(new byte[16], width, height, width * bpp, layout, false, new byte[16]))
                .ThrowsExactly<ArgumentException>().Because($"{layout} {width}x{height}");
        }
    }
}
