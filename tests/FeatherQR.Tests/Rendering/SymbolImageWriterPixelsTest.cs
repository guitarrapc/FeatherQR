namespace FeatherQR.Tests;

/// <summary>
/// The raw pixel buffer: its size, each layout's byte order, rows packed with no padding, straight and premultiplied alpha, and a Gray8 that equals
/// the luminance the decoders read from the RGBA buffer.
/// </summary>
public class SymbolImageWriterPixelsTest
{
    private static readonly PixelLayout[] Layouts = [PixelLayout.Gray8, PixelLayout.Rgba8888, PixelLayout.Bgra8888, PixelLayout.Rgb888x, PixelLayout.Bgr888x, PixelLayout.Rgb24, PixelLayout.Bgr24];

    // ---- every symbol -------------------------------------------------------------------

    [Test]
    [MethodDataSource(typeof(SymbolImageWriterSvgTest), nameof(SymbolImageWriterSvgTest.StandardQrVersionsAndLevels))]
    public async Task StandardQr_MatchesTheMatrix_EveryVersionAndLevel(int version, QREccLevel level)
    {
        var data = QRCodeGenerator.Create("1", level, new QRCodeGeneratorOptions { Version = version });
        var options = new SymbolImageOptions { ModulePixelSize = 2 };
        await AssertPixels(SymbolImageWriterPngTest.Grid.Of(data), options, PixelLayout.Rgba8888,
            (destination, layout) => (SymbolImageWriter.TryWritePixels(data, destination, layout, premultipliedAlpha: false, out var w, out var h, options), w, h),
            layout => SymbolImageWriter.GetPixelBufferSize(data, layout, options));
    }

    [Test]
    [MethodDataSource(typeof(SymbolImageWriterSvgTest), nameof(SymbolImageWriterSvgTest.MicroQrVersionsAndLevels))]
    public async Task MicroQr_MatchesTheMatrix_EveryVersionAndLevel(MicroQRVersion version, MicroQREccLevel level)
    {
        var data = MicroQRCodeGenerator.Create("1", level, new MicroQRCodeGeneratorOptions { Version = version });
        foreach (var layout in Layouts)
        {
            await AssertPixels(SymbolImageWriterPngTest.Grid.Of(data), SymbolImageOptions.Default, layout,
                (destination, l) => (SymbolImageWriter.TryWritePixels(data, destination, l, premultipliedAlpha: false, out var w, out var h), w, h),
                l => SymbolImageWriter.GetPixelBufferSize(data, l));
        }
    }

    [Test]
    [MethodDataSource(typeof(SymbolImageWriterSvgTest), nameof(SymbolImageWriterSvgTest.RmQrVersionsAndLevels))]
    public async Task RmQr_MatchesTheMatrix_EveryVersionAndLevel(RmQRVersion version, RmQREccLevel level)
    {
        var data = RmQRCodeGenerator.Create("1", level, new RmQRCodeGeneratorOptions { Version = version });
        var options = new SymbolImageOptions { ModulePixelSize = 3 };
        await AssertPixels(SymbolImageWriterPngTest.Grid.Of(data), options, PixelLayout.Bgra8888,
            (destination, layout) => (SymbolImageWriter.TryWritePixels(data, destination, layout, premultipliedAlpha: false, out var w, out var h, options), w, h),
            layout => SymbolImageWriter.GetPixelBufferSize(data, layout, options));
    }

    /// <summary>Every layout, at one pixel a module where no row is repeated, at 3 and at the default 8, with quiet zones of 0 and 4, colours, and alphas below 255.</summary>
    [Test]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(8)]
    public async Task EveryLayout_MatchesTheMatrix_WithColorsAndAlpha(int pixels)
    {
        foreach (var quietZone in new[] { 0, 4 })
        {
            var qr = QRCodeGenerator.Create("pixels", QREccLevel.Q, new QRCodeGeneratorOptions { QuietZoneSize = quietZone });
            var options = new SymbolImageOptions { ModulePixelSize = pixels, DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A, 200), LightColor = new SymbolColor(0xFF, 0xF8, 0xE7, 51) };
            foreach (var layout in Layouts)
            {
                await AssertPixels(SymbolImageWriterPngTest.Grid.Of(qr), options, layout,
                    (destination, l) => (SymbolImageWriter.TryWritePixels(qr, destination, l, premultipliedAlpha: false, out var w, out var h, options), w, h),
                    l => SymbolImageWriter.GetPixelBufferSize(qr, l, options));
            }
        }
    }

    // ---- Gray8 is the decoders' luminance ----------------------------------------------

    /// <summary>
    /// Gray8 is what the decoders compute from the RGBA buffer: each alpha below 255 composited over white, then BT.601.
    /// The layouts with no alpha hold the same colours composited over white, so the same Gray8 must be the decoders' reading of each.
    /// </summary>
    [Test]
    public async Task Gray8_IsTheLuminanceTheDecodersReadFromRgbaAndTheLayoutsWithNoAlpha()
    {
        var rng = new Random(8);
        var micro = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L);
        for (var trial = 0; trial < 64; trial++)
        {
            var options = new SymbolImageOptions
            {
                ModulePixelSize = 2,
                DarkColor = new SymbolColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)),
                LightColor = new SymbolColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)),
            };
            var gray = new byte[SymbolImageWriter.GetPixelBufferSize(micro, PixelLayout.Gray8, options)];
            SymbolImageWriter.TryWritePixels(micro, gray, PixelLayout.Gray8, premultipliedAlpha: false, out var width, out var height, options);
            foreach (var layout in new[] { PixelLayout.Rgba8888, PixelLayout.Rgb888x, PixelLayout.Bgr888x, PixelLayout.Rgb24, PixelLayout.Bgr24 })
            {
                var color = new byte[SymbolImageWriter.GetPixelBufferSize(micro, layout, options)];
                SymbolImageWriter.TryWritePixels(micro, color, layout, premultipliedAlpha: false, out _, out _, options);
                var expected = new byte[width * height];
                LuminanceConverter.Convert(color, width, height, width * BytesPerPixel(layout), layout, premultipliedAlpha: false, expected);
                await Assert.That(gray.AsSpan().SequenceEqual(expected)).IsTrue().Because($"{layout}: {options.DarkColor} on {options.LightColor}");
            }
        }
    }

    /// <summary>
    /// A layout with no alpha shows a transparent colour as white. So transparent black, which is <c>default</c>, must not turn the light modules black.
    /// The last line pins the expected pixel to white, so the test cannot agree with a writer that copies the channels straight.
    /// </summary>
    [Test]
    public async Task Rgb888x_ShowsATransparentLightColorAsWhite()
    {
        var qr = QRCodeGenerator.Create("transparent", QREccLevel.M);
        var options = new SymbolImageOptions { LightColor = default };
        await AssertPixels(SymbolImageWriterPngTest.Grid.Of(qr), options, PixelLayout.Rgb888x,
            (destination, l) => (SymbolImageWriter.TryWritePixels(qr, destination, l, premultipliedAlpha: false, out var w, out var h, options), w, h),
            l => SymbolImageWriter.GetPixelBufferSize(qr, l, options));
        await Assert.That(Expected(options.LightColor, PixelLayout.Rgb888x)).IsEquivalentTo(new byte[] { 255, 255, 255, 255 });
    }

    // ---- three bytes a pixel --------------------------------------------------------------

    public static IEnumerable<(string Symbol, string Options)> ThreeByteCases()
    {
        foreach (var symbol in new[] { "QR3", "M4", "R17x139" })
        {
            foreach (var options in new[] { "default", "module 1", "colours", "styled", "fraction padded", "styled fraction", "whole padded", "styled whole padded" })
                yield return (symbol, options);
        }
        yield return ("QR5 H", "icon");
        yield return ("QR5 H", "icon styled");
        yield return ("QR5 H", "icon styled fill");
    }

    /// <summary>
    /// Rgb24 is Rgb888x with every fourth byte dropped, and Bgr24 the same with red and blue exchanged, pixel for pixel: plain, styled,
    /// at a size that is not whole pixels, and with an icon area. The colours have alphas below 255, which both hold composited over white.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ThreeByteCases))]
    public async Task Rgb24AndBgr24_AreRgb888xWithoutItsFourthByte(string symbol, string optionsName)
    {
        var shaped = new SymbolImageOptions { ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 90, FinderPatternStyle = FinderPatternStyle.RoundedSquareCircle };
        var navy = new SymbolColor(0x1E, 0x3A, 0x8A, 200);
        var ivory = new SymbolColor(0xFF, 0xF8, 0xE7, 51);
        var options = optionsName switch
        {
            "default" => default,
            "module 1" => new SymbolImageOptions { ModulePixelSize = 1 },
            "colours" => new SymbolImageOptions { ModulePixelSize = 3, DarkColor = navy, LightColor = ivory },
            "styled" => shaped with { DarkColor = navy, LightColor = ivory },
            "fraction padded" => new SymbolImageOptions { Width = 500, Height = 334, DarkColor = navy, FillCanvas = true },
            "styled fraction" => shaped with { Width = 500, Height = 334, DarkColor = navy, LightColor = ivory, FillCanvas = true },
            "whole padded" => new SymbolImageOptions { Width = 500, Height = 334, DarkColor = navy },
            "styled whole padded" => shaped with { Width = 500, Height = 334, DarkColor = navy, LightColor = ivory },
            "icon" => new SymbolImageOptions { IconSizeModules = 7, ModulePixelSize = 5, DarkColor = navy },
            "icon styled" => shaped with { IconSizeModules = 7, Width = 333, LightColor = ivory },
            "icon styled fill" => shaped with { IconSizeModules = 7, Width = 333, LightColor = ivory, FillCanvas = true },
            _ => throw new ArgumentException(optionsName),
        };
        var data = PixelSymbol(symbol);

        var four = WritePacked(data, PixelLayout.Rgb888x, premultipliedAlpha: false, options, out var width, out var height);
        var rgb = WritePacked(data, PixelLayout.Rgb24, premultipliedAlpha: false, options, out var rgbWidth, out var rgbHeight);
        var bgr = WritePacked(data, PixelLayout.Bgr24, premultipliedAlpha: false, options, out var bgrWidth, out var bgrHeight);

        await Assert.That((rgbWidth, rgbHeight)).IsEqualTo((width, height));
        await Assert.That((bgrWidth, bgrHeight)).IsEqualTo((width, height));
        await Assert.That(rgb.Length).IsEqualTo(width * height * 3);
        await Assert.That(bgr.Length).IsEqualTo(width * height * 3);
        int wrongRgb = 0, wrongBgr = 0;
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var (r, g, b) = (four[pixel * 4], four[pixel * 4 + 1], four[pixel * 4 + 2]);
            if (rgb[pixel * 3] != r || rgb[pixel * 3 + 1] != g || rgb[pixel * 3 + 2] != b)
                wrongRgb++;
            if (bgr[pixel * 3] != b || bgr[pixel * 3 + 1] != g || bgr[pixel * 3 + 2] != r)
                wrongBgr++;
        }
        await Assert.That(wrongRgb).IsEqualTo(0);
        await Assert.That(wrongBgr).IsEqualTo(0);
        // Navy differs in red and blue, so a Bgr24 that kept Rgb24's order would have passed nothing above. This pins that the two differ.
        if (optionsName != "default" && optionsName != "module 1")
            await Assert.That(rgb.AsSpan().SequenceEqual(bgr)).IsFalse();
    }

    private static byte[] WritePacked(object data, PixelLayout layout, bool premultipliedAlpha, SymbolImageOptions options, out int width, out int height)
    {
        int w, h;
        byte[] pixels;
        bool written;
        switch (data)
        {
            case QRCodeData qr:
                pixels = new byte[SymbolImageWriter.GetPixelBufferSize(qr, layout, options)];
                written = SymbolImageWriter.TryWritePixels(qr, pixels, layout, premultipliedAlpha, out w, out h, options);
                break;
            case MicroQRCodeData micro:
                pixels = new byte[SymbolImageWriter.GetPixelBufferSize(micro, layout, options)];
                written = SymbolImageWriter.TryWritePixels(micro, pixels, layout, premultipliedAlpha, out w, out h, options);
                break;
            case RmQRCodeData rm:
                pixels = new byte[SymbolImageWriter.GetPixelBufferSize(rm, layout, options)];
                written = SymbolImageWriter.TryWritePixels(rm, pixels, layout, premultipliedAlpha, out w, out h, options);
                break;
            default: throw new ArgumentException(nameof(data));
        }
        if (!written)
            throw new InvalidOperationException("the buffer was sized by the writer");
        (width, height) = (w, h);
        return pixels;
    }

    /// <summary>A 24-bit buffer reads back through the converter and the decoders, in both channel orders and with padded rows.</summary>
    [Test]
    public async Task Rgb24AndBgr24_ReadBackThroughTheConverterAndTheDecoders()
    {
        const string text = "https://github.com/guitarrapc/FeatherQR";
        var qr = QRCodeGenerator.Create(text, QREccLevel.M);
        var options = new SymbolImageOptions { ModulePixelSize = 5, DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A), LightColor = new SymbolColor(0xFF, 0xF8, 0xE7) };
        var (width, height) = SymbolImageWriter.GetImageSize(qr, options);
        foreach (var layout in new[] { PixelLayout.Rgb24, PixelLayout.Bgr24 })
        {
            // 185 pixels are 555 bytes, so a stride that is a multiple of four pads each row by one.
            var stride = (width * 3 + 3) & ~3;
            var pixels = new byte[stride * height];
            await Assert.That(SymbolImageWriter.TryWritePixels(qr, pixels, stride, layout, premultipliedAlpha: false, out _, out _, options)).IsTrue();
            var luminance = new byte[width * height];
            LuminanceConverter.Convert(pixels, width, height, stride, layout, premultipliedAlpha: false, luminance);

            await Assert.That(QRCodeDecoder.TryDecodeImage(luminance, width, height, out string read, out _)).IsTrue().Because($"{layout}");
            await Assert.That(read).IsEqualTo(text);
            // Navy's luminance: (77 × 0x1E + 150 × 0x3A + 29 × 0x8A) / 256 = 58, and read with red and blue exchanged it would be 78.
            await Assert.That(luminance.Min()).IsEqualTo((byte)58).Because($"{layout}");
        }
    }

    [Test]
    public async Task Gray8_ReadsBackThroughTheDecoders()
    {
        const string text = "https://github.com/guitarrapc/FeatherQR";
        var qr = QRCodeGenerator.Create(text, QREccLevel.M);
        var gray = new byte[SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Gray8)];
        await Assert.That(SymbolImageWriter.TryWritePixels(qr, gray, PixelLayout.Gray8, premultipliedAlpha: false, out var width, out var height)).IsTrue();
        await Assert.That(QRCodeDecoder.TryDecodeImage(gray, width, height, out var read, out _)).IsTrue();
        await Assert.That(read).IsEqualTo(text);

        var micro = MicroQRCodeGenerator.Create("FeatherQR", MicroQREccLevel.L);
        gray = new byte[SymbolImageWriter.GetPixelBufferSize(micro, PixelLayout.Gray8)];
        SymbolImageWriter.TryWritePixels(micro, gray, PixelLayout.Gray8, premultipliedAlpha: false, out width, out height);
        await Assert.That(MicroQRCodeDecoder.TryDecodeImage(gray, width, height, out read, out _)).IsTrue();
        await Assert.That(read).IsEqualTo("FeatherQR");

        var rm = RmQRCodeGenerator.Create(text, RmQREccLevel.M);
        gray = new byte[SymbolImageWriter.GetPixelBufferSize(rm, PixelLayout.Gray8)];
        SymbolImageWriter.TryWritePixels(rm, gray, PixelLayout.Gray8, premultipliedAlpha: false, out width, out height);
        await Assert.That(RmQRCodeDecoder.TryDecodeImage(gray, width, height, out read, out _)).IsTrue();
        await Assert.That(read).IsEqualTo(text);
    }

    // ---- premultiplied alpha --------------------------------------------------------------

    public static IEnumerable<(string Symbol, string Options)> PremultipliedCases()
    {
        foreach (var symbol in new[] { "QR3", "M4", "R17x139" })
        {
            foreach (var options in new[] { "translucent", "transparent white", "styled", "styled fraction" })
                yield return (symbol, options);
        }
        yield return ("QR5 H", "icon");
        yield return ("QR5 H", "icon styled");
    }

    /// <summary>
    /// With premultiplied alpha, each channel of Rgba8888 and Bgra8888 is the straight channel times alpha over 255, rounded to the nearest,
    /// and alpha is the same. This holds for every pixel, plain and styled, at a size that is not whole pixels and with an icon area.
    /// Transparent white is the case a writer that copies the channels gets most wrong: straight it is 255, 255, 255, 0, and premultiplied it is all zero.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(PremultipliedCases))]
    public async Task Premultiplied_IsEachStraightChannelTimesAlpha(string symbol, string optionsName)
    {
        var shaped = new SymbolImageOptions { ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 90, FinderPatternStyle = FinderPatternStyle.RoundedSquareCircle };
        var navy = new SymbolColor(0x1E, 0x3A, 0x8A, 200);
        var ivory = new SymbolColor(0xFF, 0xF8, 0xE7, 51);
        var options = optionsName switch
        {
            "translucent" => new SymbolImageOptions { ModulePixelSize = 3, DarkColor = navy, LightColor = ivory },
            "transparent white" => new SymbolImageOptions { ModulePixelSize = 2, LightColor = new SymbolColor(255, 255, 255, 0) },
            "styled" => shaped with { DarkColor = navy, LightColor = ivory },
            "styled fraction" => shaped with { Width = 500, Height = 334, DarkColor = navy, LightColor = ivory, FillCanvas = true },
            "icon" => new SymbolImageOptions { ModulePixelSize = 3, IconSizeModules = 7, DarkColor = navy, LightColor = ivory },
            "icon styled" => shaped with { IconSizeModules = 7, Width = 333, DarkColor = navy, LightColor = ivory },
            _ => throw new ArgumentException(optionsName),
        };
        var data = PixelSymbol(symbol);

        foreach (var layout in new[] { PixelLayout.Rgba8888, PixelLayout.Bgra8888 })
        {
            var straight = WritePacked(data, layout, premultipliedAlpha: false, options, out var width, out var height);
            var premultiplied = WritePacked(data, layout, premultipliedAlpha: true, options, out var premultipliedWidth, out var premultipliedHeight);
            await Assert.That((premultipliedWidth, premultipliedHeight)).IsEqualTo((width, height));

            int wrong = 0, translucent = 0;
            for (var pixel = 0; pixel < width * height; pixel++)
            {
                var alpha = straight[pixel * 4 + 3];
                if (alpha < 255)
                    translucent++;
                for (var channel = 0; channel < 3; channel++)
                {
                    if (premultiplied[pixel * 4 + channel] != Premultiply(straight[pixel * 4 + channel], alpha))
                        wrong++;
                }
                if (premultiplied[pixel * 4 + 3] != alpha)
                    wrong++;
            }
            await Assert.That(wrong).IsEqualTo(0).Because($"{layout}");
            // Some pixel is translucent, so the loop above compared a channel that premultiplying changes.
            await Assert.That(translucent).IsGreaterThan(0).Because($"{layout}");
        }
        if (optionsName == "transparent white")
        {
            var premultiplied = WritePacked(data, PixelLayout.Rgba8888, premultipliedAlpha: true, options, out _, out _);
            await Assert.That(premultiplied.AsSpan(0, 4).SequenceEqual(new byte[4])).IsTrue();
        }
    }

    /// <summary>
    /// Opaque colours are the same either way, and so is every layout with no alpha, which holds each colour composited over white whatever the flag says.
    /// </summary>
    [Test]
    public async Task Premultiplied_ChangesNothing_ForOpaqueColorsOrLayoutsWithNoAlpha()
    {
        var qr = QRCodeGenerator.Create("opaque", QREccLevel.M);
        var opaque = new SymbolImageOptions { ModulePixelSize = 2, DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A), LightColor = new SymbolColor(0xFF, 0xF8, 0xE7) };
        var translucent = opaque with { DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A, 200), LightColor = new SymbolColor(0xFF, 0xF8, 0xE7, 51) };
        foreach (var layout in Layouts)
        {
            foreach (var options in new[] { opaque, translucent })
            {
                var hasAlpha = layout is PixelLayout.Rgba8888 or PixelLayout.Bgra8888;
                if (hasAlpha && options.LightColor.A < 255)
                    continue;
                var straight = WritePacked(qr, layout, premultipliedAlpha: false, options, out _, out _);
                var premultiplied = WritePacked(qr, layout, premultipliedAlpha: true, options, out _, out _);
                await Assert.That(premultiplied.AsSpan().SequenceEqual(straight)).IsTrue().Because($"{layout}, light alpha {options.LightColor.A}");
            }
        }
    }

    /// <summary>
    /// Each symbology's stride overload takes the flag too: its rows are those of the packed premultiplied buffer, and the padding is left as it was.
    /// </summary>
    [Test]
    [Arguments("QR3")]
    [Arguments("M4")]
    [Arguments("R17x139")]
    public async Task Premultiplied_AtAStride_IsThePackedRows(string symbol)
    {
        var data = PixelSymbol(symbol);
        var options = new SymbolImageOptions { ModulePixelSize = 3, DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A, 200), LightColor = new SymbolColor(0xFF, 0xF8, 0xE7, 51) };
        var packed = WritePacked(data, PixelLayout.Bgra8888, premultipliedAlpha: true, options, out var width, out var height);
        var stride = width * 4 + 12;
        var strided = new byte[stride * height];
        strided.AsSpan().Fill(0xCD);
        var written = data switch
        {
            QRCodeData qr => SymbolImageWriter.TryWritePixels(qr, strided, stride, PixelLayout.Bgra8888, premultipliedAlpha: true, out _, out _, options),
            MicroQRCodeData micro => SymbolImageWriter.TryWritePixels(micro, strided, stride, PixelLayout.Bgra8888, premultipliedAlpha: true, out _, out _, options),
            RmQRCodeData rm => SymbolImageWriter.TryWritePixels(rm, strided, stride, PixelLayout.Bgra8888, premultipliedAlpha: true, out _, out _, options),
            _ => throw new ArgumentException(symbol),
        };
        await Assert.That(written).IsTrue();
        int wrong = 0, padding = 0;
        for (var y = 0; y < height; y++)
        {
            if (!strided.AsSpan(y * stride, width * 4).SequenceEqual(packed.AsSpan(y * width * 4, width * 4)))
                wrong++;
            if (strided.AsSpan(y * stride + width * 4, 12).ToArray().Any(b => b != 0xCD))
                padding++;
        }
        await Assert.That(wrong).IsEqualTo(0);
        await Assert.That(padding).IsEqualTo(0);
    }

    /// <summary>
    /// A premultiplied buffer reads back through the converter told so, and decodes. Its luminance is the Gray8 buffer's to within one level,
    /// which is the rounding of the premultiplied channels. Read as straight alpha, the same buffer comes out darker.
    /// </summary>
    [Test]
    public async Task Premultiplied_ReadsBackThroughTheConverterAndTheDecoders()
    {
        const string text = "https://github.com/guitarrapc/FeatherQR";
        var qr = QRCodeGenerator.Create(text, QREccLevel.M);
        var options = new SymbolImageOptions { ModulePixelSize = 4, DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A, 230), LightColor = new SymbolColor(0xFF, 0xF8, 0xE7, 120) };
        var gray = WritePacked(qr, PixelLayout.Gray8, premultipliedAlpha: false, options, out var width, out var height);
        foreach (var layout in new[] { PixelLayout.Rgba8888, PixelLayout.Bgra8888 })
        {
            var pixels = WritePacked(qr, layout, premultipliedAlpha: true, options, out _, out _);
            var luminance = new byte[width * height];
            LuminanceConverter.Convert(pixels, width, height, width * 4, layout, premultipliedAlpha: true, luminance);
            var farthest = 0;
            for (var i = 0; i < luminance.Length; i++)
                farthest = Math.Max(farthest, Math.Abs(luminance[i] - gray[i]));
            await Assert.That(farthest).IsLessThanOrEqualTo(1).Because($"{layout}");
            await Assert.That(QRCodeDecoder.TryDecodeImage(luminance, width, height, out string read, out _)).IsTrue().Because($"{layout}");
            await Assert.That(read).IsEqualTo(text);

            LuminanceConverter.Convert(pixels, width, height, width * 4, layout, premultipliedAlpha: false, luminance);
            await Assert.That(luminance.Max()).IsLessThan(gray.Max()).Because($"{layout} read as straight");
        }
    }

    /// <summary>The channel times alpha over 255, rounded to the nearest. 255 is odd, so there is no tie.</summary>
    private static byte Premultiply(byte channel, byte alpha) => (byte)((channel * alpha + 127) / 255);

    private static object PixelSymbol(string symbol) => symbol switch
    {
        "QR3" => QRCodeGenerator.Create("three bytes", QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 }),
        "QR5 H" => QRCodeGenerator.Create("three bytes", QREccLevel.H, new QRCodeGeneratorOptions { Version = 5 }),
        "M4" => MicroQRCodeGenerator.Create("HELLO123", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M4 }),
        "R17x139" => RmQRCodeGenerator.Create("HELLO WORLD 1234567890", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 }),
        _ => throw new ArgumentException(symbol),
    };

    // ---- sizes and arguments ------------------------------------------------------------

    [Test]
    public async Task ShortDestination_ReturnsFalse_WritesNothing_AndReportsNoSize()
    {
        var qr = QRCodeGenerator.Create("short", QREccLevel.M);
        var size = SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Rgba8888);
        var destination = new byte[size - 1];
        destination.AsSpan().Fill(0xCD);

        await Assert.That(SymbolImageWriter.TryWritePixels(qr, destination, PixelLayout.Rgba8888, premultipliedAlpha: false, out var width, out var height)).IsFalse();
        await Assert.That(width).IsEqualTo(0);
        await Assert.That(height).IsEqualTo(0);
        await Assert.That(destination.All(b => b == 0xCD)).IsTrue();
    }

    [Test]
    public async Task UndefinedLayout_Throws()
    {
        var qr = QRCodeGenerator.Create("layout", QREccLevel.M);
        var micro = MicroQRCodeGenerator.Create("1", MicroQREccLevel.ErrorDetectionOnly);
        var rm = RmQRCodeGenerator.Create("1", RmQREccLevel.M);
        var buffer = new byte[1 << 20];
        foreach (var layout in new[] { (PixelLayout)(-1), (PixelLayout)7 })
        {
            await Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.GetPixelBufferSize(qr, layout)).ParamName).IsEqualTo("layout");
            await Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.TryWritePixels(qr, buffer, layout, premultipliedAlpha: false, out _, out _)).ParamName).IsEqualTo("layout");
            await Assert.That(() => SymbolImageWriter.GetPixelBufferSize(micro, layout)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => SymbolImageWriter.TryWritePixels(micro, buffer, layout, premultipliedAlpha: false, out _, out _)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => SymbolImageWriter.GetPixelBufferSize(rm, layout)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => SymbolImageWriter.TryWritePixels(rm, buffer, layout, premultipliedAlpha: false, out _, out _)).Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task NullData_Throws()
    {
        var buffer = new byte[16];
        await Assert.That(() => SymbolImageWriter.GetPixelBufferSize((QRCodeData)null!, PixelLayout.Gray8)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.GetPixelBufferSize((MicroQRCodeData)null!, PixelLayout.Gray8)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.GetPixelBufferSize((RmQRCodeData)null!, PixelLayout.Gray8)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.TryWritePixels((QRCodeData)null!, buffer, PixelLayout.Gray8, premultipliedAlpha: false, out _, out _)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.TryWritePixels((MicroQRCodeData)null!, buffer, PixelLayout.Gray8, premultipliedAlpha: false, out _, out _)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.TryWritePixels((RmQRCodeData)null!, buffer, PixelLayout.Gray8, premultipliedAlpha: false, out _, out _)).Throws<ArgumentNullException>();
    }

    /// <summary>
    /// The buffer has to fit an array. Version 40 with the default quiet zone at four bytes a pixel fits up to 125 pixels a module
    /// (2,139,062,500 bytes) and not at 126 (2,173,424,400). The size is checked before the destination, so an empty span is enough.
    /// </summary>
    [Test]
    public async Task BufferPastTheLargestArray_Throws_AndUpToItIsSized()
    {
        var qr = QRCodeGenerator.Create("1", QREccLevel.L, new QRCodeGeneratorOptions { Version = 40 });

        await Assert.That(SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Rgba8888, new SymbolImageOptions { ModulePixelSize = 125 })).IsEqualTo(2_139_062_500);
        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Rgba8888, new SymbolImageOptions { ModulePixelSize = 126 }));
        await Assert.That(refused.ParamName).IsEqualTo("options");
        var written = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.TryWritePixels(qr, Span<byte>.Empty, PixelLayout.Rgba8888, premultipliedAlpha: false, out _, out _, new SymbolImageOptions { ModulePixelSize = 126 }));
        await Assert.That(written.ParamName).IsEqualTo("options");
        // One byte a pixel goes further.
        await Assert.That(SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Gray8, new SymbolImageOptions { ModulePixelSize = 250 })).IsEqualTo(185 * 250 * 185 * 250);
    }

    /// <summary>
    /// At version 40 each side stays within an int up to 11,608,019 pixels a module, but from 8,208,110 four bytes a pixel pass a long and wrap.
    /// The wrapped count cast to an int is positive at 8,300,000 and negative at 10,000,000, and both must still be refused.
    /// </summary>
    [Test]
    [Arguments(8_300_000)]
    [Arguments(10_000_000)]
    public async Task BufferPastALong_Throws(int pixels)
    {
        var qr = QRCodeGenerator.Create("1", QREccLevel.L, new QRCodeGeneratorOptions { Version = 40 });
        var options = new SymbolImageOptions { ModulePixelSize = pixels };
        foreach (var layout in new[] { PixelLayout.Rgba8888, PixelLayout.Bgra8888, PixelLayout.Rgb888x })
        {
            var sized = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.GetPixelBufferSize(qr, layout, options));
            await Assert.That(sized.ParamName).IsEqualTo("options").Because($"{layout}");
            var written = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.TryWritePixels(qr, Span<byte>.Empty, layout, premultipliedAlpha: false, out _, out _, options));
            await Assert.That(written.ParamName).IsEqualTo("options").Because($"{layout}");
        }
    }

    // ---- the reader ---------------------------------------------------------------------

    private delegate (bool Written, int Width, int Height) Write(Span<byte> destination, PixelLayout layout);

    private static async Task AssertPixels(SymbolImageWriterPngTest.Grid grid, SymbolImageOptions options, PixelLayout layout, Write write, Func<PixelLayout, int> size)
    {
        var pixels = options.ModulePixelSize;
        var bytesPerPixel = BytesPerPixel(layout);
        var width = grid.Width * pixels;
        var height = grid.Height * pixels;
        await Assert.That(size(layout)).IsEqualTo(width * height * bytesPerPixel);

        // Room past the end, which must be left as it was.
        var buffer = new byte[width * height * bytesPerPixel + 7];
        buffer.AsSpan().Fill(0xCD);
        var (written, reportedWidth, reportedHeight) = write(buffer, layout);
        await Assert.That(written).IsTrue();
        await Assert.That(reportedWidth).IsEqualTo(width);
        await Assert.That(reportedHeight).IsEqualTo(height);
        await Assert.That(buffer.AsSpan(width * height * bytesPerPixel).ToArray().All(b => b == 0xCD)).IsTrue();

        // The dark colour is painted over the light one, as the SVG paints its path over its background.
        var dark = Expected(SymbolImageWriterPngTest.Over(options.DarkColor, options.LightColor), layout);
        var light = Expected(options.LightColor, layout);
        var wrong = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var expected = grid[y / pixels, x / pixels] ? dark : light;
                if (!buffer.AsSpan((y * width + x) * bytesPerPixel, bytesPerPixel).SequenceEqual(expected))
                    wrong++;
            }
        }
        await Assert.That(wrong).IsEqualTo(0).Because($"{layout} at {pixels} px");
    }

    private static int BytesPerPixel(PixelLayout layout) => layout switch
    {
        PixelLayout.Gray8 => 1,
        PixelLayout.Rgb24 or PixelLayout.Bgr24 => 3,
        _ => 4,
    };

    /// <summary>The bytes of one pixel, written out here from each layout's definition.</summary>
    private static byte[] Expected(SymbolColor c, PixelLayout layout) => layout switch
    {
        PixelLayout.Rgba8888 => [c.R, c.G, c.B, c.A],
        PixelLayout.Bgra8888 => [c.B, c.G, c.R, c.A],
        PixelLayout.Rgb888x => [OverWhite(c.R, c.A), OverWhite(c.G, c.A), OverWhite(c.B, c.A), 255],
        PixelLayout.Bgr888x => [OverWhite(c.B, c.A), OverWhite(c.G, c.A), OverWhite(c.R, c.A), 255],
        PixelLayout.Rgb24 => [OverWhite(c.R, c.A), OverWhite(c.G, c.A), OverWhite(c.B, c.A)],
        PixelLayout.Bgr24 => [OverWhite(c.B, c.A), OverWhite(c.G, c.A), OverWhite(c.R, c.A)],
        _ => [Luminance(c)],
    };

    /// <summary>A channel composited over white, which the layouts with no alpha hold.</summary>
    private static byte OverWhite(byte channel, byte alpha) => (byte)((channel * alpha + 255 * (255 - alpha)) / 255);

    private static byte Luminance(SymbolColor c) => (byte)((77 * OverWhite(c.R, c.A) + 150 * OverWhite(c.G, c.A) + 29 * OverWhite(c.B, c.A)) >> 8);
}
