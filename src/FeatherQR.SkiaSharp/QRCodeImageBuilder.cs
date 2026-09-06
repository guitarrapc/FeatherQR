using SkiaSharp;
using System.Buffers;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Turns text into a Standard QR image, as PNG, JPEG, WebP or SVG.
/// </summary>
/// <remarks>
/// The static methods cover the common cases in one line, such as <see cref="GetPngBytes(string, QREccLevel, int)"/>.
/// For anything else, construct the builder and chain the options, from sizing and colors down to <see cref="WithIcon(IconData?)"/> and <see cref="WithFinderPatternShape(FinderPatternShape?)"/>.
/// </remarks>
/// <seealso cref="QRCodeGenerator"/>
/// <seealso cref="SymbolRenderer"/>
public sealed class QRCodeImageBuilder : SymbolImageBuilderBase<QRCodeImageBuilder>
{
    private readonly string? _content;
    private readonly QRCodeData? _qrCodeData;
    private QREccLevel _eccLevel = QREccLevel.M;
    private bool _boostEccLevel;
    private EciMode _eciMode = EciMode.Default;
    private QRVersionRange _versionRange;
    private int? _maskPattern;
    private QRSegmentation _segmentation = QRSegmentation.Single;

    // rendering (Standard QR-only options; Micro QR has a single finder pattern
    // and no ECC headroom for overlays)
    private FinderPatternShape? _finderPatternShape;
    private IconData? _iconData;

    /// <summary>
    /// Starts a builder that will encode <paramref name="content"/> when you ask for an image.
    /// Error correction, version and every other option keep their defaults until you set them.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="content"/> is empty or only whitespace.</exception>
    public QRCodeImageBuilder(string content) : base(defaultQuietZoneSize: 4)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Content cannot be empty", nameof(content));

        _content = content;
    }

    /// <summary>
    /// Starts a builder that draws a QR code you have already generated.
    /// The QR code is used exactly as given, so only the appearance options apply.
    /// Most encoding options throw <see cref="InvalidOperationException"/> on a builder created this way; <see cref="WithErrorCorrection"/> and <see cref="WithEciMode"/> are accepted and then ignored, because they shipped that way in 1.1.1.
    /// </summary>
    /// <param name="qrCodeData">The QR code to draw.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="qrCodeData"/> is null.</exception>
    public QRCodeImageBuilder(QRCodeData qrCodeData) : base(defaultQuietZoneSize: 4)
    {
        if (qrCodeData is null)
            throw new ArgumentNullException(nameof(qrCodeData));

        _qrCodeData = qrCodeData;
    }

    // static methods for quick generation

    /// <summary>
    /// Encodes the content and returns a PNG image.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="eccLevel">How much damage the QR code should survive.</param>
    /// <param name="size">Image side length in pixels.</param>
    public static byte[] GetPngBytes(string content, QREccLevel eccLevel = QREccLevel.M, int size = 512)
    {
        return GetImageBytes(content, SKEncodedImageFormat.Png, eccLevel, size, 100);
    }

    /// <summary>
    /// Renders the QR code and returns a PNG image.
    /// </summary>
    /// <param name="qrCodeData">The QR code to draw.</param>
    /// <param name="size">Image side length in pixels.</param>
    public static byte[] GetPngBytes(QRCodeData qrCodeData, int size = 512)
    {
        return GetImageBytes(qrCodeData, SKEncodedImageFormat.Png, size, 100);
    }

    /// <summary>
    /// Encodes the content and returns an image in the format you choose.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="format">The format to encode as.</param>
    /// <param name="eccLevel">How much damage the QR code should survive.</param>
    /// <param name="size">Image side length in pixels.</param>
    /// <param name="quality">Quality from 0 to 100, for formats that are lossy.</param>
    public static byte[] GetImageBytes(string content, SKEncodedImageFormat format, QREccLevel eccLevel = QREccLevel.M, int size = 512, int quality = 100)
    {
        return new QRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .WithFormat(format, quality)
            .ToByteArray();
    }

    /// <summary>
    /// Renders the QR code and returns an image in the format you choose.
    /// </summary>
    /// <param name="qrCodeData">The QR code to draw.</param>
    /// <param name="format">The format to encode as.</param>
    /// <param name="size">Image side length in pixels.</param>
    /// <param name="quality">Quality from 0 to 100, for formats that are lossy.</param>
    public static byte[] GetImageBytes(QRCodeData qrCodeData, SKEncodedImageFormat format, int size = 512, int quality = 100)
    {
        return new QRCodeImageBuilder(qrCodeData)
            .WithSize(size, size)
            .WithFormat(format, quality)
            .ToByteArray();
    }

    /// <summary>
    /// Encodes the content and writes a PNG to a stream.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="output">Where to write. Left open afterwards.</param>
    /// <param name="eccLevel">How much damage the QR code should survive.</param>
    /// <param name="size">Image side length in pixels.</param>
    public static void SavePng(string content, Stream output, QREccLevel eccLevel = QREccLevel.M, int size = 512)
    {
        new QRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .SaveTo(output);
    }

    /// <summary>
    /// Renders the QR code and writes a PNG to a stream.
    /// </summary>
    /// <param name="qrCodeData">The QR code to draw.</param>
    /// <param name="output">Where to write. Left open afterwards.</param>
    /// <param name="size">Image side length in pixels.</param>
    public static void SavePng(QRCodeData qrCodeData, Stream output, int size = 512)
    {
        new QRCodeImageBuilder(qrCodeData)
            .WithSize(size, size)
            .SaveTo(output);
    }

    /// <summary>
    /// Encodes the content and returns an SVG document as UTF-8 bytes.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="eccLevel">How much damage the QR code should survive.</param>
    /// <param name="size">Viewport side length in SVG units.</param>
    public static byte[] GetSvgBytes(string content, QREccLevel eccLevel = QREccLevel.M, int size = 512)
    {
        using var stream = new MemoryStream();
        new QRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .SaveToSvg(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Renders the QR code and returns an SVG document as UTF-8 bytes.
    /// </summary>
    /// <param name="qrCodeData">The QR code to draw.</param>
    /// <param name="size">Viewport side length in SVG units.</param>
    public static byte[] GetSvgBytes(QRCodeData qrCodeData, int size = 512)
    {
        using var stream = new MemoryStream();
        new QRCodeImageBuilder(qrCodeData)
            .WithSize(size, size)
            .SaveToSvg(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Encodes the content and writes an SVG document to a stream.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="output">Where to write. Left open afterwards.</param>
    /// <param name="eccLevel">How much damage the QR code should survive.</param>
    /// <param name="size">Viewport side length in SVG units.</param>
    public static void SaveSvg(string content, Stream output, QREccLevel eccLevel = QREccLevel.M, int size = 512)
    {
        new QRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .SaveToSvg(output);
    }

    /// <summary>
    /// Renders the QR code and writes an SVG document to a stream.
    /// </summary>
    /// <param name="qrCodeData">The QR code to draw.</param>
    /// <param name="output">Where to write. Left open afterwards.</param>
    /// <param name="size">Viewport side length in SVG units.</param>
    public static void SaveSvg(QRCodeData qrCodeData, Stream output, int size = 512)
    {
        new QRCodeImageBuilder(qrCodeData)
            .WithSize(size, size)
            .SaveToSvg(output);
    }

    /// <summary>
    /// Encodes the content and returns an SVG document as a string.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="eccLevel">How much damage the QR code should survive.</param>
    /// <param name="size">Viewport side length in SVG units.</param>
    public static string GetSvgString(string content, QREccLevel eccLevel = QREccLevel.M, int size = 512)
    {
        return new QRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .ToSvgString();
    }

    /// <summary>
    /// Renders the QR code and returns an SVG document as a string.
    /// </summary>
    /// <param name="qrCodeData">The QR code to draw.</param>
    /// <param name="size">Viewport side length in SVG units.</param>
    public static string GetSvgString(QRCodeData qrCodeData, int size = 512)
    {
        return new QRCodeImageBuilder(qrCodeData)
            .WithSize(size, size)
            .ToSvgString();
    }

    /// <summary>
    /// Encodes the content and writes an SVG document to a buffer writer.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="writer">Where to write.</param>
    /// <param name="eccLevel">How much damage the QR code should survive.</param>
    /// <param name="size">Viewport side length in SVG units.</param>
    public static void WriteSvg(string content, IBufferWriter<byte> writer, QREccLevel eccLevel = QREccLevel.M, int size = 512)
    {
        new QRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .SaveToSvg(writer);
    }

    /// <summary>
    /// Renders the QR code and writes an SVG document to a buffer writer.
    /// </summary>
    /// <param name="qrCodeData">The QR code to draw.</param>
    /// <param name="writer">Where to write.</param>
    /// <param name="size">Viewport side length in SVG units.</param>
    public static void WriteSvg(QRCodeData qrCodeData, IBufferWriter<byte> writer, int size = 512)
    {
        new QRCodeImageBuilder(qrCodeData)
            .WithSize(size, size)
            .SaveToSvg(writer);
    }

    /// <summary>
    /// Encodes the content and writes a PNG to a buffer writer.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="writer">Where to write.</param>
    /// <param name="eccLevel">How much damage the QR code should survive.</param>
    /// <param name="size">Image side length in pixels.</param>
    public static void WritePng(string content, IBufferWriter<byte> writer, QREccLevel eccLevel = QREccLevel.M, int size = 512)
    {
        WriteImage(content, writer, SKEncodedImageFormat.Png, eccLevel, size, quality: 100);
    }

    /// <summary>
    /// Renders the QR code and writes a PNG to a buffer writer.
    /// </summary>
    /// <param name="qrCodeData">The QR code to draw.</param>
    /// <param name="writer">Where to write.</param>
    /// <param name="size">Image side length in pixels.</param>
    public static void WritePng(QRCodeData qrCodeData, IBufferWriter<byte> writer, int size = 512)
    {
        WriteImage(qrCodeData, writer, SKEncodedImageFormat.Png, size, quality: 100);
    }

    /// <summary>
    /// Encodes the content and writes an image in the format you choose to a buffer writer.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="writer">Where to write.</param>
    /// <param name="format">The format to encode as.</param>
    /// <param name="eccLevel">How much damage the QR code should survive.</param>
    /// <param name="size">Image side length in pixels.</param>
    /// <param name="quality">Quality from 0 to 100, for formats that are lossy.</param>
    public static void WriteImage(string content, IBufferWriter<byte> writer, SKEncodedImageFormat format, QREccLevel eccLevel = QREccLevel.M, int size = 512, int quality = 100)
    {
        new QRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .WithFormat(format, quality)
            .SaveTo(writer);
    }

    /// <summary>
    /// Renders the QR code and writes an image in the format you choose to a buffer writer.
    /// </summary>
    /// <param name="qrCodeData">The QR code to draw.</param>
    /// <param name="writer">Where to write.</param>
    /// <param name="format">The format to encode as.</param>
    /// <param name="size">Image side length in pixels.</param>
    /// <param name="quality">Quality from 0 to 100, for formats that are lossy.</param>
    public static void WriteImage(QRCodeData qrCodeData, IBufferWriter<byte> writer, SKEncodedImageFormat format, int size = 512, int quality = 100)
    {
        new QRCodeImageBuilder(qrCodeData)
            .WithSize(size, size)
            .WithFormat(format, quality)
            .SaveTo(writer);
    }

    // Standard QR-specific builder methods

    /// <summary>
    /// Sets how much damage the QR code should survive: L recovers 7% of it, M 15%, Q 25% and H 30%.
    /// </summary>
    /// <param name="eccLevel">The level to encode at. Reach for H when an icon or a custom module shape covers part of the QR code.</param>
    public QRCodeImageBuilder WithErrorCorrection(QREccLevel eccLevel)
    {
        _eccLevel = eccLevel;
        return this;
    }

    /// <summary>
    /// Raise the error correction level above the one configured with <see cref="WithErrorCorrection(QREccLevel)"/> when the chosen version's capacity allows it, without changing the version or the QR code size.
    /// Recommended together with <see cref="WithIcon(IconData?)"/>: the spare capacity absorbs the modules the icon covers.
    /// </summary>
    /// <param name="boostEccLevel">Whether to boost; see <see cref="QRCodeGeneratorOptions.BoostEccLevel"/>.</param>
    /// <exception cref="InvalidOperationException">Thrown when the builder was given a ready-made QR code.</exception>
    public QRCodeImageBuilder WithErrorCorrectionBoost(bool boostEccLevel = true)
    {
        if (_qrCodeData is not null)
            throw new InvalidOperationException("WithErrorCorrectionBoost cannot be used when QRCodeData is provided directly.");

        _boostEccLevel = boostEccLevel;
        return this;
    }

    /// <summary>
    /// Sets the character encoding to declare in the QR code.
    /// </summary>
    /// <param name="eciMode">The encoding to declare. The default picks it from the content.</param>
    public QRCodeImageBuilder WithEciMode(EciMode eciMode)
    {
        _eciMode = eciMode;
        return this;
    }

    /// <summary>
    /// Pins the version instead of letting the content choose it.
    /// </summary>
    /// <remarks>
    /// The pinned case of <see cref="WithVersion(QRVersionRange)"/>.
    /// </remarks>
    /// <param name="version">The version, 1 to 40, or -1 to keep it automatic.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the version is not 1-40 or -1.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the builder was given a ready-made QR code.</exception>
    public QRCodeImageBuilder WithVersion(int version)
    {
        if (_qrCodeData is not null)
            throw new InvalidOperationException("WithVersion cannot be used when QRCodeData is provided directly.");
        if (version is not -1 and (< 1 or > 40))
            throw new ArgumentOutOfRangeException(nameof(version), "Version must be between 1 and 40, or -1 for automatic selection.");

        _versionRange = version == -1 ? QRVersionRange.Any : QRVersionRange.Exactly(version);
        return this;
    }

    /// <summary>
    /// Narrows the versions the generator may choose from, for a QR code that has to reach or stay under a physical size.
    /// </summary>
    /// <param name="versionRange">The versions to choose from; the smallest that holds the content wins.</param>
    /// <exception cref="InvalidOperationException">Thrown when the builder was given a ready-made QR code.</exception>
    public QRCodeImageBuilder WithVersion(QRVersionRange versionRange)
    {
        if (_qrCodeData is not null)
            throw new InvalidOperationException("WithVersion cannot be used when QRCodeData is provided directly.");

        _versionRange = versionRange;
        return this;
    }

    /// <summary>
    /// Pin one of the eight data mask patterns (0-7) instead of the automatic penalty-scored selection; see <see cref="QRCodeGeneratorOptions.MaskPattern"/>.
    /// </summary>
    /// <param name="maskPattern">Mask pattern (0-7), or null for automatic selection.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maskPattern"/> is not 0-7 or null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the builder was given a ready-made QR code.</exception>
    public QRCodeImageBuilder WithMaskPattern(int? maskPattern)
    {
        if (_qrCodeData is not null)
            throw new InvalidOperationException("WithMaskPattern cannot be used when QRCodeData is provided directly.");
        if (maskPattern is < 0 or > 7)
            throw new ArgumentOutOfRangeException(nameof(maskPattern), $"Mask pattern must be 0-7, or null for automatic selection, but was {maskPattern}");

        _maskPattern = maskPattern;
        return this;
    }

    /// <summary>
    /// Split the content into mixed-mode segments when that lowers the version (see <see cref="QRSegmentation"/>).
    /// Defaults to <see cref="QRSegmentation.Single"/>.
    /// Never selects a larger version, and produces the identical QR code when a split would not shrink it.
    /// </summary>
    /// <param name="segmentation">Segmentation strategy.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="segmentation"/> is not a defined value.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the builder was given a ready-made QR code.</exception>
    public QRCodeImageBuilder WithSegmentation(QRSegmentation segmentation)
    {
        if (_qrCodeData is not null)
            throw new InvalidOperationException("WithSegmentation cannot be used when QRCodeData is provided directly.");
        if (segmentation is not (QRSegmentation.Single or QRSegmentation.Optimal))
            throw new ArgumentOutOfRangeException(nameof(segmentation), $"Invalid segmentation: {segmentation}");

        _segmentation = segmentation;
        return this;
    }

    /// <summary>
    /// Draws an icon over the center of the QR code.
    /// </summary>
    /// <remarks>
    /// The icon covers modules, so pair it with a high error correction level or <see cref="WithErrorCorrectionBoost(bool)"/>, and scan what you ship.
    /// </remarks>
    /// <param name="iconData">The icon to draw. No icon when omitted.</param>
    public QRCodeImageBuilder WithIcon(IconData? iconData)
    {
        _iconData = iconData;
        return this;
    }

    /// <summary>
    /// Draws the three finder patterns as a shape of their own.
    /// </summary>
    /// <param name="finderPatternShape">The shape to draw. When omitted the finders follow the module shape.</param>
    public QRCodeImageBuilder WithFinderPatternShape(FinderPatternShape? finderPatternShape)
    {
        _finderPatternShape = finderPatternShape;
        return this;
    }

    // symbology hooks

    private protected override object ResolveSymbol(out int matrixWidth, out int matrixHeight)
    {
        var qrCodeData = _qrCodeData ?? QRCodeGenerator.Create(_content.AsSpan(), _eccLevel, new QRCodeGeneratorOptions
        {
            EciMode = _eciMode,
            Version = _versionRange,
            QuietZoneSize = _quietZoneSize,
            BoostEccLevel = _boostEccLevel,
            MaskPattern = _maskPattern,
            Segmentation = _segmentation,
        });
        matrixWidth = matrixHeight = qrCodeData.Size;
        return qrCodeData;
    }

    private protected override void RenderSymbol(SKCanvas canvas, object symbol, SKRect contentRect)
    {
        SymbolRenderer.Render(canvas, contentRect, (QRCodeData)symbol, _codeColor, _backgroundColor, _iconData, _moduleShape, _moduleSizePercent, _gradientOptions, _finderPatternShape);
    }

    /// <summary>
    /// Custom finder shapes require antialiasing; built-in icon shapes only draw rectangles, bitmaps, and text, none of which degrade under crispEdges.
    /// </summary>
    private protected override bool UseCrispEdgesCore()
    {
        return _finderPatternShape is null
            && (_iconData?.Icon is null or ImageIconShape or ImageTextIconShape);
    }
}
