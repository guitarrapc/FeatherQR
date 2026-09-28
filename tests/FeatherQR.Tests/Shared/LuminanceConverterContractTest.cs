using TUnit.Assertions.Enums;
using FeatherQR.Internals.ImageDecoders;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The luminance seam between the core and the rendering package:
/// <see cref="LuminanceConverter.Convert"/>, internal and reached through
/// <c>InternalsVisibleTo</c>. Tier parity between the kernels is
/// <see cref="LuminanceConverterParityTest"/>; this file pins what an adapter sees: the
/// BT.601 weighting and white compositing, the four layouts, row padding, argument
/// validation, and that the SkiaSharp adapter produces the same bytes as a direct call
/// with the pixmap span.
/// </summary>
public class LuminanceConverterContractTest
{
    private static readonly PixelLayout[] ColorLayouts = [PixelLayout.Rgba8888, PixelLayout.Bgra8888, PixelLayout.Rgb888x];

    // The enum is internal, so it cannot be a public test parameter; layout-generic tests loop instead.
    private static readonly PixelLayout[] AllLayouts = [PixelLayout.Gray8, PixelLayout.Rgba8888, PixelLayout.Bgra8888, PixelLayout.Rgb888x];

    private static int BytesPerPixel(PixelLayout layout) => layout == PixelLayout.Gray8 ? 1 : 4;

    /// <summary>The documented formula, written once here so a kernel change that drifts from it fails.</summary>
    private static byte ExpectedLuma(int r, int g, int b, int a, bool premultiplied)
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
        return (byte)((77 * r + 150 * g + 29 * b) >> 8);
    }

    /// <summary>Writes one pixel in the given layout; alpha is dropped for Rgb888x, whose fourth byte is padding.</summary>
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

    [Test]
    [Arguments(255, 255, 255, 255)] // white
    [Arguments(0, 0, 0, 255)]       // black
    [Arguments(255, 0, 0, 255)]     // red: 77 * 255 >> 8
    [Arguments(0, 255, 0, 255)]     // green: 150 * 255 >> 8
    [Arguments(0, 0, 255, 255)]     // blue: 29 * 255 >> 8
    [Arguments(0, 0, 0, 0)]         // fully transparent black composites to white
    [Arguments(0, 0, 0, 128)]       // half transparent black
    [Arguments(200, 30, 90, 77)]    // arbitrary partial alpha
    public async Task Convert_OpaqueAndStraightAlpha_MatchesDocumentedFormula(int r, int g, int b, int a)
    {
        foreach (var layout in ColorLayouts)
        {
            var pixels = new byte[4];
            WritePixel(pixels, layout, (byte)r, (byte)g, (byte)b, (byte)a);
            var luminance = new byte[1];

            LuminanceConverter.Convert(pixels, 1, 1, 4, layout, premultipliedAlpha: false, luminance);

            // Rgb888x has no alpha, so its pixel is opaque whatever the fourth byte says.
            var expected = ExpectedLuma(r, g, b, layout == PixelLayout.Rgb888x ? 255 : a, premultiplied: false);
            await Assert.That(luminance[0]).IsEqualTo(expected).Because($"{layout} rgba({r},{g},{b},{a})");
        }
    }

    [Test]
    [Arguments(0, 0, 0, 128)]
    [Arguments(100, 15, 45, 128)] // premultiplied channels are at most alpha
    [Arguments(0, 0, 0, 0)]
    public async Task Convert_PremultipliedAlpha_AddsTheMissingWhite(int r, int g, int b, int a)
    {
        foreach (var layout in new[] { PixelLayout.Rgba8888, PixelLayout.Bgra8888 })
        {
            var pixels = new byte[4];
            WritePixel(pixels, layout, (byte)r, (byte)g, (byte)b, (byte)a);
            var luminance = new byte[1];

            LuminanceConverter.Convert(pixels, 1, 1, 4, layout, premultipliedAlpha: true, luminance);

            await Assert.That(luminance[0]).IsEqualTo(ExpectedLuma(r, g, b, a, premultiplied: true)).Because($"{layout}");
        }
    }

    /// <summary>The flag is documented as ignored where there is no alpha channel.</summary>
    [Test]
    public async Task Convert_PremultipliedFlag_IsIgnoredForLayoutsWithoutAlpha()
    {
        byte[] rgbx = [200, 100, 50, 7];
        byte[] gray = [123];
        var straight = new byte[1];
        var premultiplied = new byte[1];

        LuminanceConverter.Convert(rgbx, 1, 1, 4, PixelLayout.Rgb888x, false, straight);
        LuminanceConverter.Convert(rgbx, 1, 1, 4, PixelLayout.Rgb888x, true, premultiplied);
        await Assert.That(premultiplied[0]).IsEqualTo(straight[0]);

        LuminanceConverter.Convert(gray, 1, 1, 1, PixelLayout.Gray8, false, straight);
        LuminanceConverter.Convert(gray, 1, 1, 1, PixelLayout.Gray8, true, premultiplied);
        await Assert.That(premultiplied[0]).IsEqualTo((byte)123);
        await Assert.That(straight[0]).IsEqualTo((byte)123);
    }

    /// <summary>
    /// The same image in every 32-bit layout converts to the same bytes; the layout only
    /// says where the channels are.
    /// </summary>
    [Test]
    public async Task Convert_SameImageInEveryColorLayout_ProducesIdenticalLuminance()
    {
        const int width = 37, height = 5; // straddles the 32-pixel AVX2 block and the 16-pixel NEON block
        var rng = new Random(20260904);
        var rgb = new byte[width * height * 3];
        rng.NextBytes(rgb);

        byte[]? reference = null;
        foreach (var layout in ColorLayouts)
        {
            var rowBytes = width * 4 + 12; // padded rows
            var pixels = new byte[rowBytes * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 3;
                    WritePixel(pixels.AsSpan(y * rowBytes + x * 4, 4), layout, rgb[i], rgb[i + 1], rgb[i + 2], 255);
                }

            var luminance = new byte[width * height];
            LuminanceConverter.Convert(pixels, width, height, rowBytes, layout, false, luminance);

            if (reference is null)
                reference = luminance;
            else
                await Assert.That(luminance).IsEquivalentTo(reference, CollectionOrdering.Matching).Because($"{layout} differs from {ColorLayouts[0]}");
        }

        // and against the formula, pixel by pixel
        for (var i = 0; i < width * height; i++)
            await Assert.That(reference![i]).IsEqualTo(ExpectedLuma(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2], 255, false));
    }

    [Test]
    public async Task Convert_Gray8_CopiesRowsAndDropsPadding()
    {
        const int width = 3, height = 2, rowBytes = 5;
        byte[] pixels = [1, 2, 3, 0xEE, 0xEE, 4, 5, 6, 0xEE, 0xEE];
        var luminance = new byte[width * height];

        LuminanceConverter.Convert(pixels, width, height, rowBytes, PixelLayout.Gray8, false, luminance);

        await Assert.That(luminance).IsEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6 }, CollectionOrdering.Matching);
    }

    /// <summary>The last row needs only its pixels, not a full stride, as the doc promises.</summary>
    [Test]
    public async Task Convert_LastRowMayBeShorterThanTheStride()
    {
        foreach (var layout in AllLayouts)
        {
            var bpp = BytesPerPixel(layout);
            const int width = 4, height = 3;
            var rowBytes = width * bpp + 3;
            var pixels = new byte[(height - 1) * rowBytes + width * bpp];
            var luminance = new byte[width * height];

            LuminanceConverter.Convert(pixels, width, height, rowBytes, layout, false, luminance);

            // All-zero bytes are black for Gray8 and Rgb888x, and fully transparent (so white) where there is alpha.
            var expected = layout is PixelLayout.Rgba8888 or PixelLayout.Bgra8888 ? (byte)255 : (byte)0;
            await Assert.That(luminance.All(b => b == expected)).IsTrue().Because($"{layout}: {string.Join(",", luminance)}");
        }
    }

    [Test]
    public async Task Convert_ZeroWidthOrHeight_IsANoOp()
    {
        foreach (var layout in AllLayouts)
        {
            var luminance = new byte[] { 0xAB };

            LuminanceConverter.Convert([], 0, 5, 0, layout, false, luminance);
            LuminanceConverter.Convert([], 5, 0, 0, layout, false, luminance);
            LuminanceConverter.Convert([], 0, 0, 0, layout, false, []);

            await Assert.That(luminance[0]).IsEqualTo((byte)0xAB).Because($"{layout}");
        }
    }

    [Test]
    public async Task Convert_UndefinedLayout_Throws()
    {
        await Assert.That(() => LuminanceConverter.Convert(new byte[4], 1, 1, 4, (PixelLayout)42, false, new byte[1]))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(-1, 1, 4)]
    [Arguments(1, -1, 4)]
    [Arguments(2, 1, 4)]  // stride shorter than one row of two pixels
    [Arguments(1, 1, -4)]
    public async Task Convert_NegativeDimensionOrShortStride_Throws(int width, int height, int rowBytes)
    {
        await Assert.That(() => LuminanceConverter.Convert(new byte[64], width, height, rowBytes, PixelLayout.Rgba8888, false, new byte[16]))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Convert_ShortLuminanceBuffer_Throws()
    {
        foreach (var layout in AllLayouts)
        {
            var bpp = BytesPerPixel(layout);
            await Assert.That(() => LuminanceConverter.Convert(new byte[3 * 2 * bpp], 3, 2, 3 * bpp, layout, false, new byte[5]))
                .Throws<ArgumentException>().Because($"{layout}");
        }
    }

    [Test]
    public async Task Convert_ShortPixelBuffer_Throws()
    {
        foreach (var layout in AllLayouts)
        {
            var bpp = BytesPerPixel(layout);
            await Assert.That(() => LuminanceConverter.Convert(new byte[3 * 2 * bpp - 1], 3, 2, 3 * bpp, layout, false, new byte[6]))
                .Throws<ArgumentException>().Because($"{layout}");
        }
    }

    /// <summary>
    /// The first-party adapter is nothing but a layout lookup over the public seam: for
    /// every color type the core converts directly, feeding the pixmap span to
    /// <see cref="LuminanceConverter.Convert"/> gives the bytes the adapter gives.
    /// </summary>
    [Test]
    [Arguments(SKColorType.Gray8, SKAlphaType.Opaque)]
    [Arguments(SKColorType.Rgba8888, SKAlphaType.Unpremul)]
    [Arguments(SKColorType.Rgba8888, SKAlphaType.Premul)]
    [Arguments(SKColorType.Bgra8888, SKAlphaType.Unpremul)]
    [Arguments(SKColorType.Bgra8888, SKAlphaType.Premul)]
    [Arguments(SKColorType.Rgb888x, SKAlphaType.Opaque)]
    public async Task SkiaAdapter_MatchesDirectCallWithThePixmapSpan(SKColorType colorType, SKAlphaType alphaType)
    {
        const int width = 41, height = 7;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, colorType, alphaType));
        var rng = new Random(20260904);
        var raw = bitmap.GetPixelSpan();
        var noise = new byte[raw.Length];
        rng.NextBytes(noise);
        noise.AsSpan().CopyTo(bitmap.GetPixelSpan()); // premultiplied invariants do not matter for a byte-parity check

        var viaAdapter = new byte[width * height];
        BitmapLuminanceConverter.Convert(bitmap, viaAdapter);

        await Assert.That(BitmapLuminanceConverter.TryGetLayout(colorType, out var layout)).IsTrue();
        var direct = new byte[width * height];
        using var pixmap = bitmap.PeekPixels();
        LuminanceConverter.Convert(pixmap.GetPixelSpan(), width, height, pixmap.RowBytes, layout, alphaType == SKAlphaType.Premul, direct);

        await Assert.That(viaAdapter).IsEquivalentTo(direct, CollectionOrdering.Matching);
    }

    /// <summary>A color type outside the four is redrawn once; the result is still luminance, not garbage.</summary>
    [Test]
    public async Task SkiaAdapter_UnsupportedColorType_IsRedrawnAndConverted()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(8, 2, SKColorType.Rgb565, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawRect(new SKRect(0, 1, 8, 2), new SKPaint { Color = SKColors.Black });
        }

        var luminance = new byte[16];
        BitmapLuminanceConverter.Convert(bitmap, luminance);

        await Assert.That(luminance.Take(8).All(v => v == 255)).IsTrue().Because("top row is white");
        await Assert.That(luminance.Skip(8).All(v => v == 0)).IsTrue().Because("bottom row is black");
    }
}
