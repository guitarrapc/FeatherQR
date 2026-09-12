using SkiaSharp;
using FeatherQR.SkiaSharp;
using System.Buffers;

namespace FeatherQR.Tests;

public class QRCodeImageBuilderTest
{
    private const string TestContent = "https://github.com/guitarrapc/FeatherQR";

    #region Constructor Tests

    [Test]
    public async Task Constructor_ValidContent_Success()
    {
        var builder = new QRCodeImageBuilder(TestContent);
        await Assert.That(builder).IsNotNull();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Constructor_InvalidContent_ThrowsArgumentException(string? content)
    {
        Assert.Throws<ArgumentException>(() => new QRCodeImageBuilder(content!));
    }

    #endregion

    #region Static Method Tests - GetPngBytes

    [Test]
    public async Task GetPngBytes_DefaultParameters_ReturnsValidPngBytes()
    {
        var bytes = QRCodeImageBuilder.GetPngBytes(TestContent);

        await Assert.That(bytes).IsNotNull();
        await Assert.That(bytes).IsNotEmpty();
        // PNG signature: 89 50 4E 47 0D 0A 1A 0A
        await Assert.That(bytes[0]).IsEqualTo((byte)0x89);
        await Assert.That(bytes[1]).IsEqualTo((byte)0x50);
        await Assert.That(bytes[2]).IsEqualTo((byte)0x4E);
        await Assert.That(bytes[3]).IsEqualTo((byte)0x47);
    }

    [Test]
    [Arguments(QREccLevel.L)]
    [Arguments(QREccLevel.M)]
    [Arguments(QREccLevel.Q)]
    [Arguments(QREccLevel.H)]
    public async Task GetPngBytes_DifferentEccLevels_ReturnsValidPngBytes(QREccLevel eccLevel)
    {
        var bytes = QRCodeImageBuilder.GetPngBytes(TestContent, eccLevel);

        await Assert.That(bytes).IsNotNull();
        await Assert.That(bytes).IsNotEmpty();
    }

    [Test]
    [Arguments(128)]
    [Arguments(256)]
    [Arguments(512)]
    [Arguments(1024)]
    public async Task GetPngBytes_DifferentSizes_ReturnsValidPngBytes(int size)
    {
        var bytes = QRCodeImageBuilder.GetPngBytes(TestContent, size: size);

        await Assert.That(bytes).IsNotNull();
        await Assert.That(bytes).IsNotEmpty();
    }

    #endregion

    #region Static Method Tests - GetImageBytes

    [Test]
    [Arguments(SKEncodedImageFormat.Png)]
    [Arguments(SKEncodedImageFormat.Jpeg)]
    [Arguments(SKEncodedImageFormat.Webp)]
    public async Task GetImageBytes_DifferentFormats_ReturnsValidBytes(SKEncodedImageFormat format)
    {
        var bytes = QRCodeImageBuilder.GetImageBytes(TestContent, format);

        await Assert.That(bytes).IsNotNull();
        await Assert.That(bytes).IsNotEmpty();
    }

    [Test]
    [Arguments(50)]
    [Arguments(75)]
    [Arguments(100)]
    public async Task GetImageBytes_DifferentQuality_ReturnsValidBytes(int quality)
    {
        var bytes = QRCodeImageBuilder.GetImageBytes(TestContent, SKEncodedImageFormat.Jpeg, quality: quality);

        await Assert.That(bytes).IsNotNull();
        await Assert.That(bytes).IsNotEmpty();
    }

    #endregion

    #region Static Method Tests - SavePng

    [Test]
    public async Task SavePng_ValidStream_WritesData()
    {
        using var stream = new MemoryStream();
        QRCodeImageBuilder.SavePng(TestContent, stream);

        await Assert.That(stream.Length > 0).IsTrue();
        stream.Position = 0;
        var bytes = stream.ToArray();
        // Verify PNG signature
        await Assert.That(bytes[0]).IsEqualTo((byte)0x89);
        await Assert.That(bytes[1]).IsEqualTo((byte)0x50);
    }

    #endregion

    #region Static Method Tests - WritePng

    [Test]
    public async Task WritePng_ValidBufferWriter_WritesData()
    {
        var writer = new ArrayBufferWriter<byte>();
        QRCodeImageBuilder.WritePng(TestContent, writer);

        var writtenBytes = writer.WrittenSpan.ToArray();
        await Assert.That(writtenBytes.Length > 0).IsTrue();
        // Verify PNG signature
        await Assert.That(writtenBytes[0]).IsEqualTo((byte)0x89);
        await Assert.That(writtenBytes[1]).IsEqualTo((byte)0x50);
    }

    #endregion

    #region Static Method Tests - WriteImage

    [Test]
    [Arguments(SKEncodedImageFormat.Png)]
    [Arguments(SKEncodedImageFormat.Jpeg)]
    [Arguments(SKEncodedImageFormat.Webp)]
    public async Task WriteImage_DifferentFormats_WritesData(SKEncodedImageFormat format)
    {
        var writer = new ArrayBufferWriter<byte>();
        QRCodeImageBuilder.WriteImage(TestContent, writer, format);

        var writtenBytes = writer.WrittenSpan.ToArray();
        await Assert.That(writtenBytes.Length > 0).IsTrue();
    }

    #endregion

    #region Fluent API Tests - WithSize

    [Test]
    public async Task WithSize_ValidSize_ReturnsBuilder()
    {
        var builder = new QRCodeImageBuilder(TestContent);
        var result = builder.WithSize(256, 256);

        await Assert.That(result).IsSameReferenceAs(builder);
    }

    [Test]
    [Arguments(0, 100)]
    [Arguments(-1, 100)]
    [Arguments(100, 0)]
    [Arguments(100, -1)]
    public async Task WithSize_InvalidSize_ThrowsArgumentOutOfRangeException(int width, int height)
    {
        var builder = new QRCodeImageBuilder(TestContent);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithSize(width, height));
    }

    [Test]
    public async Task WithSize_CustomSize_GeneratesCorrectSize()
    {
        var builder = new QRCodeImageBuilder(TestContent);
        var bitmap = builder.WithSize(300, 400).ToBitmap();

        using (bitmap)
        {
            await Assert.That(bitmap.Width).IsEqualTo(300);
            await Assert.That(bitmap.Height).IsEqualTo(400);
        }
    }

    #endregion

    #region Fluent API Tests - WithModulePixelSize

    [Test]
    public async Task WithModulePixelSize_ValidSize_ReturnsBuilder()
    {
        var builder = new QRCodeImageBuilder(TestContent);
        var result = builder.WithModulePixelSize(10);

        await Assert.That(result).IsSameReferenceAs(builder);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task WithModulePixelSize_InvalidSize_ThrowsArgumentOutOfRangeException(int modulePixelSize)
    {
        var builder = new QRCodeImageBuilder(TestContent);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithModulePixelSize(modulePixelSize));
    }

    [Test]
    public async Task WithModulePixelSize_CustomSize_GeneratesCorrectSize()
    {
        const int modulePixelSize = 10;
        var qrCodeData = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var expectedSide = qrCodeData.Size * modulePixelSize;

        using var bitmap = new QRCodeImageBuilder(TestContent)
            .WithModulePixelSize(modulePixelSize)
            .ToBitmap();

        await Assert.That(bitmap.Width).IsEqualTo(expectedSide);
        await Assert.That(bitmap.Height).IsEqualTo(expectedSide);
    }

    [Test]
    public async Task WithModulePixelSize_QRCodeDataBuilder_GeneratesCorrectSize()
    {
        const int modulePixelSize = 8;
        var qrCodeData = QRCodeGenerator.Create(TestContent, QREccLevel.H, new QRCodeGeneratorOptions { Version = 10 });
        var expectedSide = qrCodeData.Size * modulePixelSize;

        using var bitmap = new QRCodeImageBuilder(qrCodeData)
            .WithModulePixelSize(modulePixelSize)
            .ToBitmap();

        await Assert.That(bitmap.Width).IsEqualTo(expectedSide);
        await Assert.That(bitmap.Height).IsEqualTo(expectedSide);
    }

    [Test]
    public async Task WithModulePixelSize_AndLargerCanvas_PadsAndCentersContent()
    {
        const int modulePixelSize = 6;
        var qrCodeData = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var contentSide = qrCodeData.Size * modulePixelSize;
        const int canvasWidth = 400;
        const int canvasHeight = 500;
        await Assert.That(canvasWidth >= contentSide).IsTrue();
        await Assert.That(canvasHeight >= contentSide).IsTrue();

        using var bitmap = new QRCodeImageBuilder(TestContent)
            .WithModulePixelSize(modulePixelSize)
            .WithSize(canvasWidth, canvasHeight)
            .WithColors(codeColor: SKColors.Black, backgroundColor: SKColors.White).WithClearColor(SKColors.Transparent)
            .ToBitmap();

        await Assert.That(bitmap.Width).IsEqualTo(canvasWidth);
        await Assert.That(bitmap.Height).IsEqualTo(canvasHeight);

        var expectedLeft = (canvasWidth - contentSide) / 2;
        var expectedTop = (canvasHeight - contentSide) / 2;

        // Padding outside content should keep clearColor (transparent).
        await Assert.That(bitmap.GetPixel(0, 0).Alpha).IsEqualTo((byte)0);
        await Assert.That(bitmap.GetPixel(canvasWidth - 1, canvasHeight - 1).Alpha).IsEqualTo((byte)0);

        // Content area corners should be QR background (quiet zone).
        await Assert.That(bitmap.GetPixel(expectedLeft, expectedTop)).IsEqualTo(SKColors.White);
        await Assert.That(bitmap.GetPixel(expectedLeft + contentSide - 1, expectedTop + contentSide - 1)).IsEqualTo(SKColors.White);
    }

    [Test]
    public async Task WithModulePixelSize_AndTooSmallCanvas_ThrowsInvalidOperationException()
    {
        const int modulePixelSize = 10;
        var qrCodeData = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var contentSide = qrCodeData.Size * modulePixelSize;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new QRCodeImageBuilder(TestContent)
                .WithModulePixelSize(modulePixelSize)
                .WithSize(contentSide - 1, contentSide)
                .ToBitmap());

        await Assert.That(ex.Message).Contains("smaller than QR content size");
    }

    [Test]
    public async Task WithModulePixelSize_AndExactCanvas_MatchesContentSize()
    {
        const int modulePixelSize = 8;
        var qrCodeData = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var contentSide = qrCodeData.Size * modulePixelSize;

        using var bitmap = new QRCodeImageBuilder(TestContent)
            .WithModulePixelSize(modulePixelSize)
            .WithSize(contentSide, contentSide)
            .ToBitmap();

        await Assert.That(bitmap.Width).IsEqualTo(contentSide);
        await Assert.That(bitmap.Height).IsEqualTo(contentSide);
    }

    [Test]
    public async Task WithModulePixelSize_AndOddPadding_UsesIntegerOrigin()
    {
        const int modulePixelSize = 6;
        var qrCodeData = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var contentSide = qrCodeData.Size * modulePixelSize;
        var canvasSide = contentSide + 1; // odd padding: 0 left/top, 1 right/bottom with integer division

        using var bitmap = new QRCodeImageBuilder(TestContent)
            .WithModulePixelSize(modulePixelSize)
            .WithSize(canvasSide, canvasSide)
            .WithColors(codeColor: SKColors.Black, backgroundColor: SKColors.White).WithClearColor(SKColors.Transparent)
            .ToBitmap();

        var expectedLeft = (canvasSide - contentSide) / 2;
        var expectedTop = (canvasSide - contentSide) / 2;
        await Assert.That(expectedLeft).IsEqualTo(0);
        await Assert.That(expectedTop).IsEqualTo(0);

        // Content starts on an integer pixel and keeps QR background in quiet zone.
        await Assert.That(bitmap.GetPixel(expectedLeft, expectedTop)).IsEqualTo(SKColors.White);
        // The extra 1px pad is on the far edges.
        await Assert.That(bitmap.GetPixel(canvasSide - 1, 0).Alpha).IsEqualTo((byte)0);
        await Assert.That(bitmap.GetPixel(0, canvasSide - 1).Alpha).IsEqualTo((byte)0);
    }

    #endregion

    #region Fluent API Tests - WithFormat

    [Test]
    public async Task WithFormat_ValidFormat_ReturnsBuilder()
    {
        var builder = new QRCodeImageBuilder(TestContent);
        var result = builder.WithFormat(SKEncodedImageFormat.Jpeg, 80);

        await Assert.That(result).IsSameReferenceAs(builder);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(101)]
    public async Task WithFormat_InvalidQuality_ThrowsArgumentOutOfRangeException(int quality)
    {
        var builder = new QRCodeImageBuilder(TestContent);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithFormat(SKEncodedImageFormat.Png, quality));
    }

    #endregion

    #region Fluent API Tests - WithErrorCorrection

    [Test]
    [Arguments(QREccLevel.L)]
    [Arguments(QREccLevel.M)]
    [Arguments(QREccLevel.Q)]
    [Arguments(QREccLevel.H)]
    public async Task WithErrorCorrection_AllLevels_ReturnsBuilder(QREccLevel eccLevel)
    {
        var builder = new QRCodeImageBuilder(TestContent);
        var result = builder.WithErrorCorrection(eccLevel);

        await Assert.That(result).IsSameReferenceAs(builder);
    }

    #endregion

    #region Fluent API Tests - WithEciMode

    [Test]
    [Arguments(EciMode.Default)]
    [Arguments(EciMode.Iso8859_1)]
    [Arguments(EciMode.Utf8)]
    public async Task WithEciMode_ValidModes_ReturnsBuilder(EciMode eciMode)
    {
        var builder = new QRCodeImageBuilder(TestContent);
        var result = builder.WithEciMode(eciMode);

        await Assert.That(result).IsSameReferenceAs(builder);
    }

    #endregion

    #region Fluent API Tests - WithVersion

    [Test]
    [Arguments(-1)]
    [Arguments(1)]
    [Arguments(10)]
    [Arguments(40)]
    public async Task WithVersion_ValidVersions_ReturnsBuilder(int version)
    {
        var builder = new QRCodeImageBuilder(TestContent);
        var result = builder.WithVersion(version);

        await Assert.That(result).IsSameReferenceAs(builder);
    }

    [Test]
    [Arguments(-2)]
    [Arguments(0)]
    [Arguments(41)]
    public async Task WithVersion_InvalidVersion_ThrowsArgumentOutOfRangeException(int version)
    {
        var builder = new QRCodeImageBuilder(TestContent);

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithVersion(version));
    }

    [Test]
    public async Task WithVersion_QRCodeDataBuilder_ThrowsInvalidOperationException()
    {
        var qrCodeData = QRCodeGenerator.Create(TestContent, QREccLevel.H);
        var builder = new QRCodeImageBuilder(qrCodeData);

        Assert.Throws<InvalidOperationException>(() => builder.WithVersion(10));
    }

    [Test]
    public async Task WithVersion_FixedVersion_MatchesQRCodeDataRendering()
    {
        const int fixedVersion = 10;
        using var expectedBitmap = new QRCodeImageBuilder(QRCodeGenerator.Create(TestContent, QREccLevel.H, new QRCodeGeneratorOptions { Version = fixedVersion }))
            .WithSize(256, 256)
            .ToBitmap();

        using var actualBitmap = new QRCodeImageBuilder(TestContent)
            .WithErrorCorrection(QREccLevel.H)
            .WithVersion(fixedVersion)
            .WithSize(256, 256)
            .ToBitmap();

        await Assert.That(BitmapsAreEqual(expectedBitmap, actualBitmap)).IsTrue();
    }

    #endregion

    #region Fluent API Tests - WithQuietZone

    [Test]
    [Arguments(0)]
    [Arguments(4)]
    [Arguments(10)]
    public async Task WithQuietZone_ValidSize_ReturnsBuilder(int size)
    {
        var builder = new QRCodeImageBuilder(TestContent);
        var result = builder.WithQuietZone(size);

        await Assert.That(result).IsSameReferenceAs(builder);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(11)]
    public async Task WithQuietZone_InvalidSize_ThrowsArgumentOutOfRangeException(int size)
    {
        var builder = new QRCodeImageBuilder(TestContent);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithQuietZone(size));
    }

    #endregion

    #region Fluent API Tests - WithColors

    [Test]
    public async Task WithColors_ValidColors_ReturnsBuilder()
    {
        var builder = new QRCodeImageBuilder(TestContent);
        var result = builder.WithColors(SKColors.Blue, SKColors.Yellow).WithClearColor(SKColors.Transparent);

        await Assert.That(result).IsSameReferenceAs(builder);
    }

    [Test]
    public async Task WithClearColor_NullClearColor_ReturnsBuilder()
    {
        var builder = new QRCodeImageBuilder(TestContent);
        var result = builder.WithClearColor(null);

        await Assert.That(result).IsSameReferenceAs(builder);
    }

    #endregion

    #region Fluent API Tests - WithModuleShape

    [Test]
    [Arguments(0.5f)]
    [Arguments(0.8f)]
    [Arguments(1.0f)]
    public async Task WithModuleShape_ValidSizePercent_ReturnsBuilder(float sizePercent)
    {
        var builder = new QRCodeImageBuilder(TestContent);
        var result = builder.WithModuleShape(CircleModuleShape.Default, sizePercent);

        await Assert.That(result).IsSameReferenceAs(builder);
    }

    [Test]
    [Arguments(0.4f)]
    [Arguments(1.1f)]
    public async Task WithModuleShape_InvalidSizePercent_ThrowsArgumentOutOfRangeException(float sizePercent)
    {
        var builder = new QRCodeImageBuilder(TestContent);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithModuleShape(CircleModuleShape.Default, sizePercent));
    }

    #endregion

    #region Fluent API Tests - WithGradient

    [Test]
    public async Task WithGradient_ValidOptions_ReturnsBuilder()
    {
        var gradientOptions = new GradientOptions([SKColors.Red, SKColors.Blue], GradientDirection.TopToBottom);
        var builder = new QRCodeImageBuilder(TestContent);
        var result = builder.WithGradient(gradientOptions);

        await Assert.That(result).IsSameReferenceAs(builder);
    }

    [Test]
    public async Task WithGradient_Null_ReturnsBuilder()
    {
        var builder = new QRCodeImageBuilder(TestContent);
        var result = builder.WithGradient(null);

        await Assert.That(result).IsSameReferenceAs(builder);
    }

    #endregion

    #region Fluent API Tests - WithIcon

    [Test]
    public async Task WithIcon_Null_ReturnsBuilder()
    {
        var builder = new QRCodeImageBuilder(TestContent);
        var result = builder.WithIcon(null);

        await Assert.That(result).IsSameReferenceAs(builder);
    }

    #endregion

    #region Output Method Tests - SaveTo Stream

    [Test]
    public async Task SaveTo_Stream_ValidStream_WritesData()
    {
        var builder = new QRCodeImageBuilder(TestContent);
        using var stream = new MemoryStream();

        builder.SaveTo(stream);

        await Assert.That(stream.Length > 0).IsTrue();
    }

    [Test]
    public async Task SaveTo_Stream_NullStream_ThrowsArgumentNullException()
    {
        var builder = new QRCodeImageBuilder(TestContent);
        Assert.Throws<ArgumentNullException>(() => builder.SaveTo((Stream)null!));
    }

    [Test]
    public async Task SaveTo_Stream_NonWritableStream_ThrowsArgumentException()
    {
        var builder = new QRCodeImageBuilder(TestContent);
        using var stream = new MemoryStream(new byte[100], writable: false);

        Assert.Throws<ArgumentException>(() => builder.SaveTo(stream));
    }

    #endregion

    #region Output Method Tests - SaveTo IBufferWriter

    [Test]
    public async Task SaveTo_BufferWriter_ValidWriter_WritesData()
    {
        var builder = new QRCodeImageBuilder(TestContent);
        var writer = new ArrayBufferWriter<byte>();

        builder.SaveTo(writer);

        await Assert.That(writer.WrittenCount > 0).IsTrue();
    }

    [Test]
    public async Task SaveTo_BufferWriter_NullWriter_ThrowsArgumentNullException()
    {
        var builder = new QRCodeImageBuilder(TestContent);
        Assert.Throws<ArgumentNullException>(() => builder.SaveTo((IBufferWriter<byte>)null!));
    }

    #endregion

    #region Output Method Tests - ToByteArray

    [Test]
    public async Task ToByteArray_ReturnsValidBytes()
    {
        var builder = new QRCodeImageBuilder(TestContent);
        var bytes = builder.ToByteArray();

        await Assert.That(bytes).IsNotNull();
        await Assert.That(bytes).IsNotEmpty();
    }

    #endregion

    #region Output Method Tests - ToImage

    [Test]
    public async Task ToImage_ReturnsValidSKImage()
    {
        var builder = new QRCodeImageBuilder(TestContent);
        using var image = builder.ToImage();

        await Assert.That(image).IsNotNull();
        await Assert.That(image.Width).IsEqualTo(512);
        await Assert.That(image.Height).IsEqualTo(512);
    }

    #endregion

    #region Output Method Tests - ToBitmap

    [Test]
    public async Task ToBitmap_ReturnsValidSKBitmap()
    {
        var builder = new QRCodeImageBuilder(TestContent);
        using var bitmap = builder.ToBitmap();

        await Assert.That(bitmap).IsNotNull();
        await Assert.That(bitmap.Width).IsEqualTo(512);
        await Assert.That(bitmap.Height).IsEqualTo(512);
    }

    #endregion

    #region Integration Tests

    [Test]
    public async Task FluentAPI_ChainMultipleMethods_GeneratesValidQRCode()
    {
        var builder = new QRCodeImageBuilder(TestContent)
            .WithSize(400, 400)
            .WithErrorCorrection(QREccLevel.H)
            .WithQuietZone(2)
            .WithColors(SKColors.DarkBlue, SKColors.LightYellow)
            .WithFormat(SKEncodedImageFormat.Png, 95);

        var bytes = builder.ToByteArray();

        await Assert.That(bytes).IsNotNull();
        await Assert.That(bytes).IsNotEmpty();
    }

    [Test]
    public async Task FluentAPI_WithAllCustomizations_GeneratesValidQRCode()
    {
        var gradientOptions = new GradientOptions([SKColors.Purple, SKColors.Pink], GradientDirection.TopLeftToBottomRight);
        var builder = new QRCodeImageBuilder(TestContent)
            .WithSize(600, 600)
            .WithErrorCorrection(QREccLevel.H)
            .WithEciMode(EciMode.Utf8)
            .WithQuietZone(3)
            .WithColors(SKColors.Navy, SKColors.Beige).WithClearColor(SKColors.Transparent)
            .WithModuleShape(CircleModuleShape.Default, 0.9f)
            .WithGradient(gradientOptions)
            .WithFormat(SKEncodedImageFormat.Png, 100);

        using var bitmap = builder.ToBitmap();

        await Assert.That(bitmap).IsNotNull();
        await Assert.That(bitmap.Width).IsEqualTo(600);
        await Assert.That(bitmap.Height).IsEqualTo(600);
    }

    #endregion

    private static bool BitmapsAreEqual(SKBitmap left, SKBitmap right)
    {
        if (left.Width != right.Width || left.Height != right.Height)
            return false;

        for (var y = 0; y < left.Height; y++)
        {
            for (var x = 0; x < left.Width; x++)
            {
                if (left.GetPixel(x, y) != right.GetPixel(x, y))
                    return false;
            }
        }

        return true;
    }
}
