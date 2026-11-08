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
    private static readonly PixelLayout[] ColorLayouts = [PixelLayout.Rgba8888, PixelLayout.Bgra8888, PixelLayout.Rgb888x];
    private static readonly PixelLayout[] AlphaLayouts = [PixelLayout.Rgba8888, PixelLayout.Bgra8888];
    private static readonly PixelLayout[] AllLayouts = [PixelLayout.Gray8, PixelLayout.Rgba8888, PixelLayout.Bgra8888, PixelLayout.Rgb888x];

    private static int BytesPerPixel(PixelLayout layout) => layout == PixelLayout.Gray8 ? 1 : 4;

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
            default:
                throw new ArgumentOutOfRangeException(nameof(layout));
        }
    }

    private static byte ConvertOne(PixelLayout layout, byte r, byte g, byte b, byte a, bool premultiplied = false)
    {
        var pixels = new byte[4];
        WritePixel(pixels, layout, r, g, b, a);
        var luminance = new byte[1];
        LuminanceConverter.Convert(pixels, 1, 1, 4, layout, premultiplied, luminance);
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

            LuminanceConverter.Convert(new byte[width * height * 4], width, height, width * 4, layout, false, luminance);

            var expected = layout == PixelLayout.Rgb888x ? (byte)0 : (byte)255;
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
        await Assert.That(ConvertOne(PixelLayout.Rgb888x, 200, 100, 50, 7, premultiplied: true))
            .IsEqualTo(ConvertOne(PixelLayout.Rgb888x, 200, 100, 50, 7, premultiplied: false));

        byte[] gray = [123];
        var luminance = new byte[1];
        LuminanceConverter.Convert(gray, 1, 1, 1, PixelLayout.Gray8, true, luminance);
        await Assert.That(luminance[0]).IsEqualTo((byte)123);
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
                        WritePixel(pixels.AsSpan(y * rowBytes + x * bpp, 4), layout, value, value, value, 255);
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
        await Assert.That(() => LuminanceConverter.Convert(new byte[4], 1, 1, 4, (PixelLayout)42, false, new byte[1]))
            .Throws<ArgumentOutOfRangeException>();
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
