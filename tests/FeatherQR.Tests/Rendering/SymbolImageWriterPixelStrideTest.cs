namespace FeatherQR.Tests;

/// <summary>
/// The pixel writer with a row stride: rows start that many bytes apart and hold the packed overload's bytes, the bytes between a row's
/// last pixel and the next row keep what they held, the last row may end at its last pixel, and a symbol goes into a region of a larger
/// buffer in place.
/// </summary>
public class SymbolImageWriterPixelStrideTest
{
    private const byte Sentinel = 0xCD;
    private static readonly PixelLayout[] Layouts = [PixelLayout.Gray8, PixelLayout.Rgba8888, PixelLayout.Bgra8888, PixelLayout.Rgb888x, PixelLayout.Rgb24, PixelLayout.Bgr24];
    private static readonly SymbolImageOptions Shaped = new() { ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 90, FinderPatternStyle = FinderPatternStyle.RoundedSquareCircle };
    private static readonly SymbolColor Navy = new(0x1E, 0x3A, 0x8A, 200);
    private static readonly SymbolColor Ivory = new(0xFF, 0xF8, 0xE7, 51);

    public static IEnumerable<(string Symbol, string Options)> SymbolsAndOptions()
    {
        foreach (var symbol in new[] { "QR3", "M4", "R17x139" })
        {
            foreach (var options in new[] { "default", "module 1", "colours", "styled", "fraction padded", "styled fraction" })
                yield return (symbol, options);
        }
        yield return ("QR5 H", "icon");
        yield return ("QR5 H", "icon styled");
    }

    // ---- rows a stride apart --------------------------------------------------------------

    /// <summary>
    /// A stride of one row is the packed overload. A wider one moves each row and writes nothing between them. The paddings of 1, 5 and 7
    /// bytes are not whole pixels at four bytes or at three, so a writer that fills whole pixels past a row's end is caught.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(SymbolsAndOptions))]
    public async Task Stride_MovesTheRows_AndKeepsTheBytesBetweenThem(string symbol, string optionsName)
    {
        var data = Symbol(symbol);
        var options = Options(optionsName);
        foreach (var layout in Layouts)
        {
            var bytesPerPixel = BytesPerPixel(layout);
            var packed = Packed(data, layout, options, out var width, out var height);
            var rowLength = width * bytesPerPixel;
            foreach (var padding in new[] { 0, 1, 5, 7, 64 })
            {
                var stride = rowLength + padding;
                // The last row ends at its last pixel, and the bytes after it belong to the caller.
                var destination = new byte[(height - 1) * stride + rowLength + 9];
                // Each byte differs from its neighbours and from the row above, so padding copied from another row shows.
                for (var i = 0; i < destination.Length; i++)
                    destination[i] = (byte)(i * 31 + i / 251);
                var before = destination.ToArray();

                var (written, reportedWidth, reportedHeight) = TryWritePixels(data, destination, stride, layout, options);

                await Assert.That(written).IsTrue();
                await Assert.That((reportedWidth, reportedHeight)).IsEqualTo((width, height));
                var wrongRows = 0;
                var touched = 0;
                for (var y = 0; y < height; y++)
                {
                    if (!destination.AsSpan(y * stride, rowLength).SequenceEqual(packed.AsSpan(y * rowLength, rowLength)))
                        wrongRows++;
                    var from = y * stride + rowLength;
                    var length = y < height - 1 ? padding : destination.Length - from;
                    touched += destination.AsSpan(from, length).SequenceEqual(before.AsSpan(from, length)) ? 0 : 1;
                }
                await Assert.That(wrongRows).IsEqualTo(0).Because($"{layout}, {padding} bytes of padding");
                await Assert.That(touched).IsEqualTo(0).Because($"{layout}, {padding} bytes of padding: the bytes between rows and after the last are the caller's");
            }
        }
    }

    // ---- the destination's length ---------------------------------------------------------

    /// <summary>
    /// The destination needs every row but the last at the stride and the last at its pixels. That length is taken, and one byte short
    /// is refused with nothing written and no size reported.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(SymbolsAndOptions))]
    public async Task LastRow_MayEndAtItsLastPixel_AndOneByteShortIsRefused(string symbol, string optionsName)
    {
        var data = Symbol(symbol);
        var options = Options(optionsName);
        foreach (var layout in Layouts)
        {
            var bytesPerPixel = BytesPerPixel(layout);
            var packed = Packed(data, layout, options, out var width, out var height);
            var rowLength = width * bytesPerPixel;
            foreach (var padding in new[] { 0, 3, 64 })
            {
                var stride = rowLength + padding;
                var needed = (height - 1) * stride + rowLength;

                var exact = new byte[needed];
                var (written, _, _) = TryWritePixels(data, exact, stride, layout, options);
                await Assert.That(written).IsTrue().Because($"{layout}, {padding} bytes of padding");
                await Assert.That(exact.AsSpan((height - 1) * stride).SequenceEqual(packed.AsSpan((height - 1) * rowLength))).IsTrue();

                var shortByOne = new byte[needed - 1];
                shortByOne.AsSpan().Fill(Sentinel);
                var (refused, reportedWidth, reportedHeight) = TryWritePixels(data, shortByOne, stride, layout, options);
                await Assert.That(refused).IsFalse().Because($"{layout}, {padding} bytes of padding");
                await Assert.That((reportedWidth, reportedHeight)).IsEqualTo((0, 0));
                await Assert.That(shortByOne.AsSpan().Count(Sentinel)).IsEqualTo(shortByOne.Length);
            }
        }
    }

    /// <summary>
    /// A stride the destination could never hold is a destination too short, not a bad argument: version 1 at 8 pixels is 232 rows,
    /// and 231 strides of <see cref="int.MaxValue"/> pass an int.
    /// </summary>
    [Test]
    public async Task StrideTheDestinationCannotHold_ReturnsFalse()
    {
        var qr = QRCodeGenerator.Create("1", QREccLevel.L, new QRCodeGeneratorOptions { Version = 1 });
        var destination = new byte[232 * 232 * 4];
        destination.AsSpan().Fill(Sentinel);

        await Assert.That(SymbolImageWriter.TryWritePixels(qr, destination, int.MaxValue, PixelLayout.Rgba8888, out var width, out var height)).IsFalse();
        await Assert.That((width, height)).IsEqualTo((0, 0));
        await Assert.That(destination.AsSpan().Count(Sentinel)).IsEqualTo(destination.Length);
    }

    // ---- the stride as an argument --------------------------------------------------------

    /// <summary>
    /// A stride below one row cannot hold the image, so it throws whatever the destination, as the converter's stride does.
    /// One byte under a row is the boundary, and exactly a row is taken.
    /// </summary>
    [Test]
    public async Task StrideBelowOneRow_Throws()
    {
        var qr = (QRCodeData)Symbol("QR3");
        var micro = (MicroQRCodeData)Symbol("M4");
        var rm = (RmQRCodeData)Symbol("R17x139");
        var buffer = new byte[1 << 22];

        foreach (var layout in Layouts)
        {
            var bytesPerPixel = BytesPerPixel(layout);
            foreach (var stride in new[] { 296 * bytesPerPixel - 1, 1, 0, -1, int.MinValue })
            {
                var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.TryWritePixels(qr, buffer, stride, layout, out _, out _));
                await Assert.That(thrown.ParamName).IsEqualTo("rowBytes").Because($"{layout}, stride {stride}");
                // The argument is wrong whatever the destination holds.
                thrown = Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.TryWritePixels(qr, Span<byte>.Empty, stride, layout, out _, out _));
                await Assert.That(thrown.ParamName).IsEqualTo("rowBytes").Because($"{layout}, stride {stride}, an empty destination");
            }
            await Assert.That(SymbolImageWriter.TryWritePixels(qr, buffer, 296 * bytesPerPixel, layout, out _, out _)).IsTrue();

            await Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.TryWritePixels(micro, buffer, 168 * bytesPerPixel - 1, layout, out _, out _)).ParamName).IsEqualTo("rowBytes");
            await Assert.That(SymbolImageWriter.TryWritePixels(micro, buffer, 168 * bytesPerPixel, layout, out _, out _)).IsTrue();
            await Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.TryWritePixels(rm, buffer, 1144 * bytesPerPixel - 1, layout, out _, out _)).ParamName).IsEqualTo("rowBytes");
            await Assert.That(SymbolImageWriter.TryWritePixels(rm, buffer, 1144 * bytesPerPixel, layout, out _, out _)).IsTrue();
        }
    }

    /// <summary>The row's length follows the options, so the same stride is taken at one module size and refused at the next.</summary>
    [Test]
    public async Task StrideBelowOneRow_FollowsTheOptions()
    {
        var micro = (MicroQRCodeData)Symbol("M4");
        var buffer = new byte[1 << 20];
        var stride = 21 * 3 * 4;

        await Assert.That(SymbolImageWriter.TryWritePixels(micro, buffer, stride, PixelLayout.Rgba8888, out _, out _, new SymbolImageOptions { ModulePixelSize = 3 })).IsTrue();
        await Assert.That(() => SymbolImageWriter.TryWritePixels(micro, buffer, stride, PixelLayout.Rgba8888, out _, out _, new SymbolImageOptions { ModulePixelSize = 4 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SymbolImageWriter.TryWritePixels(micro, buffer, stride, PixelLayout.Rgba8888, out _, out _, new SymbolImageOptions { Width = 64, Height = 40 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(SymbolImageWriter.TryWritePixels(micro, buffer, stride, PixelLayout.Rgba8888, out _, out _, new SymbolImageOptions { Width = 63, Height = 40 })).IsTrue();
    }

    [Test]
    public async Task NullData_UndefinedLayout_AndRefusedOptions_Throw()
    {
        var qr = (QRCodeData)Symbol("QR3");
        var micro = (MicroQRCodeData)Symbol("M4");
        var rm = (RmQRCodeData)Symbol("R17x139");
        var buffer = new byte[1 << 22];

        await Assert.That(() => SymbolImageWriter.TryWritePixels((QRCodeData)null!, buffer, 4096, PixelLayout.Gray8, out _, out _)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.TryWritePixels((MicroQRCodeData)null!, buffer, 4096, PixelLayout.Gray8, out _, out _)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.TryWritePixels((RmQRCodeData)null!, buffer, 4096, PixelLayout.Gray8, out _, out _)).Throws<ArgumentNullException>();

        foreach (var layout in new[] { (PixelLayout)(-1), (PixelLayout)99 })
        {
            // The layout is named even with a stride that is wrong too, since a row's length is not known without it.
            await Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.TryWritePixels(qr, buffer, -1, layout, out _, out _)).ParamName).IsEqualTo("layout");
            await Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.TryWritePixels(micro, buffer, 4096, layout, out _, out _)).ParamName).IsEqualTo("layout");
            await Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.TryWritePixels(rm, buffer, 4096, layout, out _, out _)).ParamName).IsEqualTo("layout");
        }

        var underOnePixel = new SymbolImageOptions { Width = 20, Height = 20 };
        await Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.TryWritePixels(qr, buffer, 4096, PixelLayout.Gray8, out _, out _, underOnePixel)).ParamName).IsEqualTo("options");
        var iconOnMicro = new SymbolImageOptions { IconSizeModules = 1, IconBorderModules = 0 };
        await Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => SymbolImageWriter.TryWritePixels(micro, buffer, 4096, PixelLayout.Gray8, out _, out _, iconOnMicro)).ParamName).IsEqualTo("options");
    }

    // ---- a region of a larger buffer ------------------------------------------------------

    /// <summary>
    /// A symbol written at a position inside a larger image, the stride that image's row: the region holds the packed overload's bytes
    /// and every byte outside it is the one the image held. The image's bytes differ from their neighbours, so a moved byte shows.
    /// The positions put the region at the image's first pixel, in its middle, against its right edge, and in its last rows,
    /// where the destination ends with the symbol's last pixel.
    /// </summary>
    [Test]
    [Arguments("M4", "default", 0, 0)]
    [Arguments("M4", "colours", 13, 7)]
    [Arguments("M4", "styled", 232, 100)]
    [Arguments("M4", "default", 232, 232)]
    [Arguments("M4", "fraction padded", 0, 150)]
    [Arguments("R17x139", "module 1", 100, 379)]
    [Arguments("QR3", "styled fraction", 0, 0)]
    public async Task Region_HoldsTheSymbol_AndEveryByteOutsideIsKept(string symbol, string optionsName, int left, int top)
    {
        var data = Symbol(symbol);
        var options = optionsName switch
        {
            // The canvas is 400 pixels a side, so the set sizes here are ones that fit inside it.
            "fraction padded" => new SymbolImageOptions { Width = 400, Height = 250 },
            "styled fraction" => Shaped with { Width = 400, Height = 400 },
            _ => Options(optionsName),
        };
        const int canvasWidth = 400, canvasHeight = 400;
        foreach (var layout in Layouts)
        {
            var bytesPerPixel = BytesPerPixel(layout);
            var packed = Packed(data, layout, options, out var width, out var height);
            await Assert.That(left + width <= canvasWidth && top + height <= canvasHeight).IsTrue().Because("the region lies inside the canvas");
            var stride = canvasWidth * bytesPerPixel;
            var canvas = new byte[stride * canvasHeight];
            for (var i = 0; i < canvas.Length; i++)
                canvas[i] = (byte)(i * 31 + i / 251);
            var before = canvas.ToArray();

            var (written, reportedWidth, reportedHeight) = TryWritePixels(data, canvas.AsSpan((top * canvasWidth + left) * bytesPerPixel), stride, layout, options);

            await Assert.That(written).IsTrue();
            await Assert.That((reportedWidth, reportedHeight)).IsEqualTo((width, height));
            var wrongInside = 0;
            var changedOutside = 0;
            for (var y = 0; y < canvasHeight; y++)
            {
                var row = canvas.AsSpan(y * stride, stride);
                var kept = before.AsSpan(y * stride, stride);
                if (y < top || y >= top + height)
                {
                    changedOutside += row.SequenceEqual(kept) ? 0 : 1;
                    continue;
                }
                var from = left * bytesPerPixel;
                var to = from + width * bytesPerPixel;
                changedOutside += row.Slice(0, from).SequenceEqual(kept.Slice(0, from)) ? 0 : 1;
                changedOutside += row.Slice(to).SequenceEqual(kept.Slice(to)) ? 0 : 1;
                wrongInside += row.Slice(from, to - from).SequenceEqual(packed.AsSpan((y - top) * width * bytesPerPixel, width * bytesPerPixel)) ? 0 : 1;
            }
            await Assert.That(wrongInside).IsEqualTo(0).Because($"{layout}");
            await Assert.That(changedOutside).IsEqualTo(0).Because($"{layout}");
        }
    }

    /// <summary>The region that does not fit below its position is refused and the image keeps every byte.</summary>
    [Test]
    public async Task Region_PastTheBuffersEnd_IsRefused()
    {
        var micro = (MicroQRCodeData)Symbol("M4");
        const int canvasWidth = 400, canvasHeight = 400;
        var canvas = new byte[canvasWidth * canvasHeight * 4];
        canvas.AsSpan().Fill(Sentinel);

        // 168 rows from row 233 end on row 400, one past the last.
        var at = (233 * canvasWidth + 232) * 4;
        await Assert.That(SymbolImageWriter.TryWritePixels(micro, canvas.AsSpan(at), canvasWidth * 4, PixelLayout.Bgra8888, out var width, out var height)).IsFalse();
        await Assert.That((width, height)).IsEqualTo((0, 0));
        await Assert.That(canvas.AsSpan().Count(Sentinel)).IsEqualTo(canvas.Length);
    }

    // ---- the calls by symbology -----------------------------------------------------------

    private static int BytesPerPixel(PixelLayout layout) => layout switch
    {
        PixelLayout.Gray8 => 1,
        PixelLayout.Rgb24 or PixelLayout.Bgr24 => 3,
        _ => 4,
    };

    private static object Symbol(string name) => name switch
    {
        "QR3" => QRCodeGenerator.Create("row stride", QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 }),
        "QR5 H" => QRCodeGenerator.Create("row stride", QREccLevel.H, new QRCodeGeneratorOptions { Version = 5 }),
        "M4" => MicroQRCodeGenerator.Create("HELLO123", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M4 }),
        "R17x139" => RmQRCodeGenerator.Create("HELLO WORLD 1234567890", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 }),
        _ => throw new ArgumentException(name),
    };

    private static SymbolImageOptions Options(string name) => name switch
    {
        "default" => default,
        "module 1" => new SymbolImageOptions { ModulePixelSize = 1 },
        "colours" => new SymbolImageOptions { ModulePixelSize = 3, DarkColor = Navy, LightColor = Ivory },
        "styled" => Shaped with { DarkColor = Navy, LightColor = Ivory },
        // Neither side is whole pixels for 37, 21 or 143 modules, and the symbol is padded on one axis.
        "fraction padded" => new SymbolImageOptions { Width = 500, Height = 333, DarkColor = Navy },
        "styled fraction" => Shaped with { Width = 500, Height = 333 },
        "icon" => new SymbolImageOptions { IconSizeModules = 7, ModulePixelSize = 5 },
        "icon styled" => Shaped with { IconSizeModules = 7, Width = 333 },
        _ => throw new ArgumentException(name),
    };

    /// <summary>The packed overload's bytes, which the stride overload's rows are held to.</summary>
    private static byte[] Packed(object data, PixelLayout layout, SymbolImageOptions options, out int width, out int height)
    {
        int w, h;
        byte[] pixels;
        bool written;
        switch (data)
        {
            case QRCodeData qr:
                pixels = new byte[SymbolImageWriter.GetPixelBufferSize(qr, layout, options)];
                written = SymbolImageWriter.TryWritePixels(qr, pixels, layout, out w, out h, options);
                break;
            case MicroQRCodeData micro:
                pixels = new byte[SymbolImageWriter.GetPixelBufferSize(micro, layout, options)];
                written = SymbolImageWriter.TryWritePixels(micro, pixels, layout, out w, out h, options);
                break;
            case RmQRCodeData rm:
                pixels = new byte[SymbolImageWriter.GetPixelBufferSize(rm, layout, options)];
                written = SymbolImageWriter.TryWritePixels(rm, pixels, layout, out w, out h, options);
                break;
            default: throw new ArgumentException(nameof(data));
        }
        if (!written)
            throw new InvalidOperationException("the buffer was sized by the writer");
        (width, height) = (w, h);
        return pixels;
    }

    private static (bool Written, int Width, int Height) TryWritePixels(object data, Span<byte> destination, int rowBytes, PixelLayout layout, SymbolImageOptions options)
    {
        bool written;
        int w, h;
        switch (data)
        {
            case QRCodeData qr: written = SymbolImageWriter.TryWritePixels(qr, destination, rowBytes, layout, out w, out h, options); break;
            case MicroQRCodeData micro: written = SymbolImageWriter.TryWritePixels(micro, destination, rowBytes, layout, out w, out h, options); break;
            case RmQRCodeData rm: written = SymbolImageWriter.TryWritePixels(rm, destination, rowBytes, layout, out w, out h, options); break;
            default: throw new ArgumentException(nameof(data));
        }
        return (written, w, h);
    }
}
