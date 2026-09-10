using SkiaSharp;
using System.Buffers;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Turns text into a Micro QR image, as PNG, JPEG, WebP or SVG.
/// </summary>
/// <remarks>
/// The same shape as <see cref="QRCodeImageBuilder"/>, with Micro QR versions and levels and the 2-module quiet zone the specification asks for.
/// Icons are not offered here: Micro QR has no error correction headroom to spare. Its one finder pattern can be styled with <see cref="SymbolImageBuilderBase{TSelf}.WithFinderPatternShape"/>.
/// </remarks>
/// <seealso cref="MicroQRCodeGenerator"/>
/// <seealso cref="SymbolRenderer"/>
/// <seealso cref="QRCodeImageBuilder"/>
public sealed class MicroQRCodeImageBuilder : SymbolImageBuilderBase<MicroQRCodeImageBuilder>
{
    private readonly string? _content;
    private readonly MicroQRCodeData? _data;
    private MicroQREccLevel _eccLevel = MicroQREccLevel.M;
    private MicroQRVersionRange _versionRange;
    private int? _maskPattern;
    private MicroQRSegmentation _segmentation = MicroQRSegmentation.Single;

    /// <summary>
    /// Starts a builder that will encode <paramref name="content"/> when you ask for an image.
    /// Error correction, version and every other option keep their defaults until you set them.
    /// </summary>
    /// <param name="content">The text to encode. Micro QR holds very little, so keep it short.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="content"/> is empty or only whitespace.</exception>
    public MicroQRCodeImageBuilder(string content) : base(defaultQuietZoneSize: 2)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Content cannot be empty", nameof(content));

        _content = content;
    }

    /// <summary>
    /// Starts a builder that draws a Micro QR code you have already generated.
    /// The Micro QR code is drawn exactly as given, so only the appearance options apply and the encoding options throw <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <param name="microQrCodeData">The Micro QR code to draw.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="microQrCodeData"/> is null.</exception>
    public MicroQRCodeImageBuilder(MicroQRCodeData microQrCodeData) : base(defaultQuietZoneSize: 2)
    {
        if (microQrCodeData is null)
            throw new ArgumentNullException(nameof(microQrCodeData));

        _data = microQrCodeData;
    }

    // static methods for quick generation

    /// <summary>
    /// Encodes the content and returns a PNG image.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image side length in pixels.</param>
    public static byte[] GetPngBytes(string content, MicroQREccLevel eccLevel = MicroQREccLevel.M, int size = 512)
    {
        return GetImageBytes(content, SKEncodedImageFormat.Png, eccLevel, size, 100);
    }

    /// <summary>
    /// Renders the Micro QR code and returns a PNG image.
    /// </summary>
    /// <param name="microQrCodeData">The Micro QR code to draw.</param>
    /// <param name="size">Image side length in pixels.</param>
    public static byte[] GetPngBytes(MicroQRCodeData microQrCodeData, int size = 512)
    {
        return GetImageBytes(microQrCodeData, SKEncodedImageFormat.Png, size, 100);
    }

    /// <summary>
    /// Encodes the content and returns an image in the format you choose.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="format">The format to encode as.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image side length in pixels.</param>
    /// <param name="quality">Quality from 0 to 100, for formats that are lossy.</param>
    public static byte[] GetImageBytes(string content, SKEncodedImageFormat format, MicroQREccLevel eccLevel = MicroQREccLevel.M, int size = 512, int quality = 100)
    {
        return new MicroQRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .WithFormat(format, quality)
            .ToByteArray();
    }

    /// <summary>
    /// Renders the Micro QR code and returns an image in the format you choose.
    /// </summary>
    /// <param name="microQrCodeData">The Micro QR code to draw.</param>
    /// <param name="format">The format to encode as.</param>
    /// <param name="size">Image side length in pixels.</param>
    /// <param name="quality">Quality from 0 to 100, for formats that are lossy.</param>
    public static byte[] GetImageBytes(MicroQRCodeData microQrCodeData, SKEncodedImageFormat format, int size = 512, int quality = 100)
    {
        return new MicroQRCodeImageBuilder(microQrCodeData)
            .WithSize(size, size)
            .WithFormat(format, quality)
            .ToByteArray();
    }

    /// <summary>
    /// Encodes the content and writes a PNG to a stream.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="output">Where to write. Left open afterwards.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image side length in pixels.</param>
    public static void SavePng(string content, Stream output, MicroQREccLevel eccLevel = MicroQREccLevel.M, int size = 512)
    {
        new MicroQRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .SaveTo(output);
    }

    /// <summary>
    /// Renders the Micro QR code and writes a PNG to a stream.
    /// </summary>
    /// <param name="microQrCodeData">The Micro QR code to draw.</param>
    /// <param name="output">Where to write. Left open afterwards.</param>
    /// <param name="size">Image side length in pixels.</param>
    public static void SavePng(MicroQRCodeData microQrCodeData, Stream output, int size = 512)
    {
        new MicroQRCodeImageBuilder(microQrCodeData)
            .WithSize(size, size)
            .SaveTo(output);
    }

    /// <summary>
    /// Encodes the content and returns an SVG document as UTF-8 bytes.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Viewport side length in SVG units.</param>
    public static byte[] GetSvgBytes(string content, MicroQREccLevel eccLevel = MicroQREccLevel.M, int size = 512)
    {
        using var stream = new MemoryStream();
        new MicroQRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .SaveToSvg(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Renders the Micro QR code and returns an SVG document as UTF-8 bytes.
    /// </summary>
    /// <param name="microQrCodeData">The Micro QR code to draw.</param>
    /// <param name="size">Viewport side length in SVG units.</param>
    public static byte[] GetSvgBytes(MicroQRCodeData microQrCodeData, int size = 512)
    {
        using var stream = new MemoryStream();
        new MicroQRCodeImageBuilder(microQrCodeData)
            .WithSize(size, size)
            .SaveToSvg(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Encodes the content and writes an SVG document to a stream.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="output">Where to write. Left open afterwards.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Viewport side length in SVG units.</param>
    public static void SaveSvg(string content, Stream output, MicroQREccLevel eccLevel = MicroQREccLevel.M, int size = 512)
    {
        new MicroQRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .SaveToSvg(output);
    }

    /// <summary>
    /// Renders the Micro QR code and writes an SVG document to a stream.
    /// </summary>
    /// <param name="microQrCodeData">The Micro QR code to draw.</param>
    /// <param name="output">Where to write. Left open afterwards.</param>
    /// <param name="size">Viewport side length in SVG units.</param>
    public static void SaveSvg(MicroQRCodeData microQrCodeData, Stream output, int size = 512)
    {
        new MicroQRCodeImageBuilder(microQrCodeData)
            .WithSize(size, size)
            .SaveToSvg(output);
    }

    /// <summary>
    /// Encodes the content and returns an SVG document as a string.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Viewport side length in SVG units.</param>
    public static string GetSvgString(string content, MicroQREccLevel eccLevel = MicroQREccLevel.M, int size = 512)
    {
        return new MicroQRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .ToSvgString();
    }

    /// <summary>
    /// Renders the Micro QR code and returns an SVG document as a string.
    /// </summary>
    /// <param name="microQrCodeData">The Micro QR code to draw.</param>
    /// <param name="size">Viewport side length in SVG units.</param>
    public static string GetSvgString(MicroQRCodeData microQrCodeData, int size = 512)
    {
        return new MicroQRCodeImageBuilder(microQrCodeData)
            .WithSize(size, size)
            .ToSvgString();
    }

    /// <summary>
    /// Encodes the content and writes an SVG document to a buffer writer.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="writer">Where to write.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Viewport side length in SVG units.</param>
    public static void WriteSvg(string content, IBufferWriter<byte> writer, MicroQREccLevel eccLevel = MicroQREccLevel.M, int size = 512)
    {
        new MicroQRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .SaveToSvg(writer);
    }

    /// <summary>
    /// Renders the Micro QR code and writes an SVG document to a buffer writer.
    /// </summary>
    /// <param name="microQrCodeData">The Micro QR code to draw.</param>
    /// <param name="writer">Where to write.</param>
    /// <param name="size">Viewport side length in SVG units.</param>
    public static void WriteSvg(MicroQRCodeData microQrCodeData, IBufferWriter<byte> writer, int size = 512)
    {
        new MicroQRCodeImageBuilder(microQrCodeData)
            .WithSize(size, size)
            .SaveToSvg(writer);
    }

    /// <summary>
    /// Encodes the content and writes a PNG to a buffer writer.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="writer">Where to write.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image side length in pixels.</param>
    public static void WritePng(string content, IBufferWriter<byte> writer, MicroQREccLevel eccLevel = MicroQREccLevel.M, int size = 512)
    {
        WriteImage(content, writer, SKEncodedImageFormat.Png, eccLevel, size, quality: 100);
    }

    /// <summary>
    /// Renders the Micro QR code and writes a PNG to a buffer writer.
    /// </summary>
    /// <param name="microQrCodeData">The Micro QR code to draw.</param>
    /// <param name="writer">Where to write.</param>
    /// <param name="size">Image side length in pixels.</param>
    public static void WritePng(MicroQRCodeData microQrCodeData, IBufferWriter<byte> writer, int size = 512)
    {
        WriteImage(microQrCodeData, writer, SKEncodedImageFormat.Png, size, quality: 100);
    }

    /// <summary>
    /// Encodes the content and writes an image in the format you choose to a buffer writer.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="writer">Where to write.</param>
    /// <param name="format">The format to encode as.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image side length in pixels.</param>
    /// <param name="quality">Quality from 0 to 100, for formats that are lossy.</param>
    public static void WriteImage(string content, IBufferWriter<byte> writer, SKEncodedImageFormat format, MicroQREccLevel eccLevel = MicroQREccLevel.M, int size = 512, int quality = 100)
    {
        new MicroQRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .WithFormat(format, quality)
            .SaveTo(writer);
    }

    /// <summary>
    /// Renders the Micro QR code and writes an image in the format you choose to a buffer writer.
    /// </summary>
    /// <param name="microQrCodeData">The Micro QR code to draw.</param>
    /// <param name="writer">Where to write.</param>
    /// <param name="format">The format to encode as.</param>
    /// <param name="size">Image side length in pixels.</param>
    /// <param name="quality">Quality from 0 to 100, for formats that are lossy.</param>
    public static void WriteImage(MicroQRCodeData microQrCodeData, IBufferWriter<byte> writer, SKEncodedImageFormat format, int size = 512, int quality = 100)
    {
        new MicroQRCodeImageBuilder(microQrCodeData)
            .WithSize(size, size)
            .WithFormat(format, quality)
            .SaveTo(writer);
    }

    // Micro QR-specific builder methods

    /// <summary>
    /// Sets how much damage the Micro QR code should survive.
    /// </summary>
    /// <remarks>
    /// What is available depends on the version: M1 takes <see cref="MicroQREccLevel.ErrorDetectionOnly"/> alone, M2 and M3 take L and M, and M4 adds Q.
    /// A combination that does not exist throws when the Micro QR code is generated.
    /// </remarks>
    /// <param name="eccLevel">The level to encode at.</param>
    /// <exception cref="InvalidOperationException">Thrown when the builder was given a ready-made Micro QR code.</exception>
    public MicroQRCodeImageBuilder WithErrorCorrection(MicroQREccLevel eccLevel)
    {
        if (_data is not null)
            throw new InvalidOperationException("WithErrorCorrection cannot be used when MicroQRCodeData is provided directly.");

        _eccLevel = eccLevel;
        return this;
    }

    /// <summary>
    /// Pins the version instead of letting the content choose it.
    /// </summary>
    /// <remarks>
    /// The pinned case of <see cref="WithVersion(MicroQRVersionRange)"/>.
    /// </remarks>
    /// <param name="version">The version, M1 to M4. Left alone, the smallest that holds the content wins.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the version is not M1-M4.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the builder was given a ready-made Micro QR code.</exception>
    public MicroQRCodeImageBuilder WithVersion(MicroQRVersion version)
    {
        if (_data is not null)
            throw new InvalidOperationException("WithVersion cannot be used when MicroQRCodeData is provided directly.");
        if ((uint)((int)version - 1) > 3)
            throw new ArgumentOutOfRangeException(nameof(version), $"Invalid Micro QR version: {version}");

        _versionRange = MicroQRVersionRange.Exactly(version);
        return this;
    }

    /// <summary>
    /// Narrows the versions the generator may choose from, for a Micro QR code that has to reach or stay under a physical size.
    /// </summary>
    /// <param name="versionRange">The versions to choose from; the smallest that holds the content wins.</param>
    /// <exception cref="InvalidOperationException">Thrown when the builder was given a ready-made Micro QR code.</exception>
    public MicroQRCodeImageBuilder WithVersion(MicroQRVersionRange versionRange)
    {
        if (_data is not null)
            throw new InvalidOperationException("WithVersion cannot be used when MicroQRCodeData is provided directly.");

        _versionRange = versionRange;
        return this;
    }

    /// <summary>
    /// Pin one of the four Micro QR data mask patterns (0-3) instead of the automatic edge-score selection; see <see cref="MicroQRCodeGeneratorOptions.MaskPattern"/>.
    /// </summary>
    /// <param name="maskPattern">Mask pattern (0-3), or null for automatic selection.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maskPattern"/> is not 0-3 or null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the builder was given a ready-made Micro QR code.</exception>
    public MicroQRCodeImageBuilder WithMaskPattern(int? maskPattern)
    {
        if (_data is not null)
            throw new InvalidOperationException("WithMaskPattern cannot be used when MicroQRCodeData is provided directly.");
        if (maskPattern is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(maskPattern), $"Mask pattern must be 0-3, or null for automatic selection, but was {maskPattern}");

        _maskPattern = maskPattern;
        return this;
    }

    /// <summary>
    /// Split the content into mixed-mode segments when that lowers the version (see <see cref="MicroQRSegmentation"/>).
    /// Defaults to <see cref="MicroQRSegmentation.Single"/>.
    /// Never selects a larger version, and produces the identical Micro QR code when a split would not shrink it.
    /// </summary>
    /// <param name="segmentation">Segmentation strategy.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="segmentation"/> is not a defined value.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the builder was given a ready-made Micro QR code.</exception>
    public MicroQRCodeImageBuilder WithSegmentation(MicroQRSegmentation segmentation)
    {
        if (_data is not null)
            throw new InvalidOperationException("WithSegmentation cannot be used when MicroQRCodeData is provided directly.");
        if (segmentation is not (MicroQRSegmentation.Single or MicroQRSegmentation.Optimal))
            throw new ArgumentOutOfRangeException(nameof(segmentation), $"Invalid segmentation: {segmentation}");

        _segmentation = segmentation;
        return this;
    }

    // symbology hooks

    private protected override object ResolveSymbol(out int matrixWidth, out int matrixHeight)
    {
        var data = _data ?? MicroQRCodeGenerator.Create(_content.AsSpan(), _eccLevel, new MicroQRCodeGeneratorOptions { Version = _versionRange, QuietZoneSize = _quietZoneSize, MaskPattern = _maskPattern, Segmentation = _segmentation });
        matrixWidth = matrixHeight = data.Size;
        return data;
    }

    private protected override void RenderSymbol(SKCanvas canvas, object symbol, SKRect contentRect)
    {
        SymbolRenderer.Render(canvas, contentRect, (MicroQRCodeData)symbol, _codeColor, _backgroundColor, _moduleShape, _moduleSizePercent, _gradientOptions, _finderPatternShape);
    }

    /// <summary>Only a custom finder shape needs antialiasing here; Micro QR has no icon overlays.</summary>
    private protected override bool UseCrispEdgesCore() => _finderPatternShape?.RequiresAntialiasing != true;
}
