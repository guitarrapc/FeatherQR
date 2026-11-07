namespace FeatherQR.Tests;

/// <summary>
/// The raw pixel buffer: its size, each layout's byte order, rows packed with no padding, straight alpha, and a Gray8 that equals
/// the luminance the decoders read from the RGBA buffer.
/// </summary>
public class SymbolImageWriterPixelsTest
{
    private static readonly PixelLayout[] Layouts = [PixelLayout.Gray8, PixelLayout.Rgba8888, PixelLayout.Bgra8888, PixelLayout.Rgb888x];

    // ---- every symbol -------------------------------------------------------------------

    [Test]
    [MethodDataSource(typeof(SymbolImageWriterSvgTest), nameof(SymbolImageWriterSvgTest.StandardQrVersionsAndLevels))]
    public async Task StandardQr_MatchesTheMatrix_EveryVersionAndLevel(int version, QREccLevel level)
    {
        var data = QRCodeGenerator.Create("1", level, new QRCodeGeneratorOptions { Version = version });
        var options = new SymbolImageOptions { ModulePixelSize = 2 };
        await AssertPixels(SymbolImageWriterPngTest.Grid.Of(data), options, PixelLayout.Rgba8888,
            (destination, layout) => (SymbolImageWriter.TryWritePixels(data, destination, layout, out var w, out var h, options), w, h),
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
                (destination, l) => (SymbolImageWriter.TryWritePixels(data, destination, l, out var w, out var h), w, h),
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
            (destination, layout) => (SymbolImageWriter.TryWritePixels(data, destination, layout, out var w, out var h, options), w, h),
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
                    (destination, l) => (SymbolImageWriter.TryWritePixels(qr, destination, l, out var w, out var h, options), w, h),
                    l => SymbolImageWriter.GetPixelBufferSize(qr, l, options));
            }
        }
    }

    // ---- Gray8 is the decoders' luminance ----------------------------------------------

    /// <summary>
    /// Gray8 is what the decoders compute from the RGBA buffer: each alpha below 255 composited over white, then BT.601.
    /// The decoders read Rgb888x without its fourth byte, and the same Gray8 must be their reading of that buffer too.
    /// </summary>
    [Test]
    public async Task Gray8_IsTheLuminanceTheDecodersReadFromRgbaAndRgb888x()
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
            SymbolImageWriter.TryWritePixels(micro, gray, PixelLayout.Gray8, out var width, out var height, options);
            foreach (var layout in new[] { PixelLayout.Rgba8888, PixelLayout.Rgb888x })
            {
                var color = new byte[SymbolImageWriter.GetPixelBufferSize(micro, layout, options)];
                SymbolImageWriter.TryWritePixels(micro, color, layout, out _, out _, options);
                var expected = new byte[width * height];
                LuminanceConverter.Convert(color, width, height, width * 4, layout, premultipliedAlpha: false, expected);
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
            (destination, l) => (SymbolImageWriter.TryWritePixels(qr, destination, l, out var w, out var h, options), w, h),
            l => SymbolImageWriter.GetPixelBufferSize(qr, l, options));
        await Assert.That(Expected(options.LightColor, PixelLayout.Rgb888x)).IsEquivalentTo(new byte[] { 255, 255, 255, 255 });
    }

    [Test]
    public async Task Gray8_ReadsBackThroughTheDecoders()
    {
        const string text = "https://github.com/guitarrapc/FeatherQR";
        var qr = QRCodeGenerator.Create(text, QREccLevel.M);
        var gray = new byte[SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Gray8)];
        await Assert.That(SymbolImageWriter.TryWritePixels(qr, gray, PixelLayout.Gray8, out var width, out var height)).IsTrue();
        await Assert.That(QRCodeDecoder.TryDecodeImage(gray, width, height, out var read, out _)).IsTrue();
        await Assert.That(read).IsEqualTo(text);

        var micro = MicroQRCodeGenerator.Create("FeatherQR", MicroQREccLevel.L);
        gray = new byte[SymbolImageWriter.GetPixelBufferSize(micro, PixelLayout.Gray8)];
        SymbolImageWriter.TryWritePixels(micro, gray, PixelLayout.Gray8, out width, out height);
        await Assert.That(MicroQRCodeDecoder.TryDecodeImage(gray, width, height, out read, out _)).IsTrue();
        await Assert.That(read).IsEqualTo("FeatherQR");

        var rm = RmQRCodeGenerator.Create(text, RmQREccLevel.M);
        gray = new byte[SymbolImageWriter.GetPixelBufferSize(rm, PixelLayout.Gray8)];
        SymbolImageWriter.TryWritePixels(rm, gray, PixelLayout.Gray8, out width, out height);
        await Assert.That(RmQRCodeDecoder.TryDecodeImage(gray, width, height, out read, out _)).IsTrue();
        await Assert.That(read).IsEqualTo(text);
    }

    // ---- sizes and arguments ------------------------------------------------------------

    [Test]
    public async Task ShortDestination_ReturnsFalse_WritesNothing_AndReportsNoSize()
    {
        var qr = QRCodeGenerator.Create("short", QREccLevel.M);
        var size = SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Rgba8888);
        var destination = new byte[size - 1];
        destination.AsSpan().Fill(0xCD);

        await Assert.That(SymbolImageWriter.TryWritePixels(qr, destination, PixelLayout.Rgba8888, out var width, out var height)).IsFalse();
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
        foreach (var layout in new[] { (PixelLayout)(-1), (PixelLayout)4 })
        {
            await Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.GetPixelBufferSize(qr, layout)).ParamName).IsEqualTo("layout");
            await Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.TryWritePixels(qr, buffer, layout, out _, out _)).ParamName).IsEqualTo("layout");
            await Assert.That(() => SymbolImageWriter.GetPixelBufferSize(micro, layout)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => SymbolImageWriter.TryWritePixels(micro, buffer, layout, out _, out _)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => SymbolImageWriter.GetPixelBufferSize(rm, layout)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => SymbolImageWriter.TryWritePixels(rm, buffer, layout, out _, out _)).Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task NullData_Throws()
    {
        var buffer = new byte[16];
        await Assert.That(() => SymbolImageWriter.GetPixelBufferSize((QRCodeData)null!, PixelLayout.Gray8)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.GetPixelBufferSize((MicroQRCodeData)null!, PixelLayout.Gray8)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.GetPixelBufferSize((RmQRCodeData)null!, PixelLayout.Gray8)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.TryWritePixels((QRCodeData)null!, buffer, PixelLayout.Gray8, out _, out _)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.TryWritePixels((MicroQRCodeData)null!, buffer, PixelLayout.Gray8, out _, out _)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.TryWritePixels((RmQRCodeData)null!, buffer, PixelLayout.Gray8, out _, out _)).Throws<ArgumentNullException>();
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
        var written = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.TryWritePixels(qr, Span<byte>.Empty, PixelLayout.Rgba8888, out _, out _, new SymbolImageOptions { ModulePixelSize = 126 }));
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
            var written = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.TryWritePixels(qr, Span<byte>.Empty, layout, out _, out _, options));
            await Assert.That(written.ParamName).IsEqualTo("options").Because($"{layout}");
        }
    }

    // ---- the reader ---------------------------------------------------------------------

    private delegate (bool Written, int Width, int Height) Write(Span<byte> destination, PixelLayout layout);

    private static async Task AssertPixels(SymbolImageWriterPngTest.Grid grid, SymbolImageOptions options, PixelLayout layout, Write write, Func<PixelLayout, int> size)
    {
        var pixels = options.ModulePixelSize;
        var bytesPerPixel = layout == PixelLayout.Gray8 ? 1 : 4;
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

    /// <summary>The bytes of one pixel, written out here from each layout's definition.</summary>
    private static byte[] Expected(SymbolColor c, PixelLayout layout) => layout switch
    {
        PixelLayout.Rgba8888 => [c.R, c.G, c.B, c.A],
        PixelLayout.Bgra8888 => [c.B, c.G, c.R, c.A],
        PixelLayout.Rgb888x => [OverWhite(c.R, c.A), OverWhite(c.G, c.A), OverWhite(c.B, c.A), 255],
        _ => [Luminance(c)],
    };

    /// <summary>A channel composited over white, which the layouts with no alpha hold.</summary>
    private static byte OverWhite(byte channel, byte alpha) => (byte)((channel * alpha + 255 * (255 - alpha)) / 255);

    private static byte Luminance(SymbolColor c) => (byte)((77 * OverWhite(c.R, c.A) + 150 * OverWhite(c.G, c.A) + 29 * OverWhite(c.B, c.A)) >> 8);
}
