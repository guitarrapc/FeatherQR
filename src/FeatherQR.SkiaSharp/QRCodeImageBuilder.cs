using SkiaSharp;
using System.Buffers;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// High-level builder for creating QR code images with fluent configuration and static methods.
/// </summary>
/// <remarks>
/// <para>
/// This builder provides both simple static methods for quick QR code generation
/// and a fluent API for advanced customization.
/// </para>
/// <para>
/// <b>Quick Generation (Static Methods):</b><br/>
/// Use static methods like <see cref="GetPngBytes(string, QREccLevel, int)"/> for one-liner QR code creation with default settings.
/// </para>
/// <para>
/// <b>Advanced Configuration (Fluent API):</b><br/>
/// Chain the shared options (<see cref="SymbolImageBuilderBase{TSelf}.WithSize(int, int)"/>,
/// <see cref="SymbolImageBuilderBase{TSelf}.WithModulePixelSize(int)"/>,
/// <see cref="SymbolImageBuilderBase{TSelf}.WithColors(SKColor?, SKColor?, SKColor?)"/>,
/// <see cref="SymbolImageBuilderBase{TSelf}.WithModuleShape(ModuleShape?, float)"/>,
/// <see cref="SymbolImageBuilderBase{TSelf}.WithGradient(GradientOptions?)"/>)
/// with the Standard QR-specific options (<see cref="WithErrorCorrection(QREccLevel)"/>,
/// <see cref="WithVersion(int)"/>, <see cref="WithIcon(IconData?)"/>,
/// <see cref="WithFinderPatternShape(FinderPatternShape?)"/>) to customize appearance.
/// </para>
/// </remarks>
/// <seealso cref="QRCodeGenerator"/>
/// <seealso cref="SymbolRenderer"/>
/// <seealso cref="MicroQRCodeImageBuilder"/>
public class QRCodeImageBuilder : SymbolImageBuilderBase<QRCodeImageBuilder>
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
    /// Starts a builder that draws a QR code you have already generated. The symbol is used
    /// exactly as given, so only the appearance options apply. Most encoding options throw
    /// <see cref="InvalidOperationException"/> on a builder created this way;
    /// <see cref="WithErrorCorrection"/> and <see cref="WithEciMode"/> are accepted and then
    /// ignored, because they shipped that way in 1.1.1.
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
    /// Generate a QR code as PNG byte array with default settings.
    /// </summary>
    /// <param name="content">The content to encode.</param>
    /// <param name="eccLevel">Error correction level. Default is M (15%).</param>
    /// <param name="size">Image size in pixels. Default is 512x512.</param>
    /// <returns>PNG encoded byte array.</returns>
    public static byte[] GetPngBytes(string content, QREccLevel eccLevel = QREccLevel.M, int size = 512)
    {
        return GetImageBytes(content, SKEncodedImageFormat.Png, eccLevel, size, 100);
    }

    /// <summary>
    /// Generate a QR code as PNG byte array with default settings.
    /// </summary>
    /// <param name="qrCodeData">The QR code data to render.</param>
    /// <param name="size">Image size in pixels. Default is 512x512.</param>
    /// <returns>PNG encoded byte array.</returns>
    public static byte[] GetPngBytes(QRCodeData qrCodeData, int size = 512)
    {
        return GetImageBytes(qrCodeData, SKEncodedImageFormat.Png, size, 100);
    }

    /// <summary>
    /// Generate a QR code as image byte array with specified format.
    /// </summary>
    /// <param name="content">The content to encode.</param>
    /// <param name="format">Image format (PNG, JPEG, WEBP, etc.).</param>
    /// <param name="eccLevel">Error correction level. Default is M (15%).</param>
    /// <param name="size">Image size in pixels. Default is 512x512.</param>
    /// <param name="quality">Encoding quality (0-100). Default is 100.</param>
    /// <returns>Encoded byte array.</returns>
    public static byte[] GetImageBytes(string content, SKEncodedImageFormat format, QREccLevel eccLevel = QREccLevel.M, int size = 512, int quality = 100)
    {
        return new QRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .WithFormat(format, quality)
            .ToByteArray();
    }

    /// <summary>
    /// Generate a QR code as image byte array with specified format.
    /// </summary>
    /// <param name="qrCodeData">The QR code data to render.</param>
    /// <param name="format">Image format (PNG, JPEG, WEBP, etc.).</param>
    /// <param name="size">Image size in pixels. Default is 512x512.</param>
    /// <param name="quality">Encoding quality (0-100). Default is 100.</param>
    /// <returns>Encoded byte array.</returns>
    public static byte[] GetImageBytes(QRCodeData qrCodeData, SKEncodedImageFormat format, int size = 512, int quality = 100)
    {
        return new QRCodeImageBuilder(qrCodeData)
            .WithSize(size, size)
            .WithFormat(format, quality)
            .ToByteArray();
    }

    /// <summary>
    /// Generate a QR code and save to stream with default PNG settings.
    /// </summary>
    /// <param name="content">The content to encode.</param>
    /// <param name="output">The output stream.</param>
    /// <param name="eccLevel">Error correction level. Default is M (15%).</param>
    /// <param name="size">Image size in pixels. Default is 512x512.</param>
    public static void SavePng(string content, Stream output, QREccLevel eccLevel = QREccLevel.M, int size = 512)
    {
        new QRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .SaveTo(output);
    }

    /// <summary>
    /// Generate a QR code and save to stream with default PNG settings.
    /// </summary>
    /// <param name="qrCodeData">The QR code data to render.</param>
    /// <param name="output">The output stream.</param>
    /// <param name="size">Image size in pixels. Default is 512x512.</param>
    public static void SavePng(QRCodeData qrCodeData, Stream output, int size = 512)
    {
        new QRCodeImageBuilder(qrCodeData)
            .WithSize(size, size)
            .SaveTo(output);
    }

    /// <summary>
    /// Generate a QR code as SVG (UTF-8 encoded) byte array with default settings.
    /// </summary>
    /// <param name="content">The content to encode.</param>
    /// <param name="eccLevel">Error correction level. Default is M (15%).</param>
    /// <param name="size">SVG viewport size in units. Default is 512x512.</param>
    /// <returns>UTF-8 encoded SVG document.</returns>
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
    /// Generate a QR code as SVG (UTF-8 encoded) byte array with default settings.
    /// </summary>
    /// <param name="qrCodeData">The QR code data to render.</param>
    /// <param name="size">SVG viewport size in units. Default is 512x512.</param>
    /// <returns>UTF-8 encoded SVG document.</returns>
    public static byte[] GetSvgBytes(QRCodeData qrCodeData, int size = 512)
    {
        using var stream = new MemoryStream();
        new QRCodeImageBuilder(qrCodeData)
            .WithSize(size, size)
            .SaveToSvg(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Generate a QR code and save as SVG to stream with default settings.
    /// </summary>
    /// <param name="content">The content to encode.</param>
    /// <param name="output">The output stream.</param>
    /// <param name="eccLevel">Error correction level. Default is M (15%).</param>
    /// <param name="size">SVG viewport size in units. Default is 512x512.</param>
    public static void SaveSvg(string content, Stream output, QREccLevel eccLevel = QREccLevel.M, int size = 512)
    {
        new QRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .SaveToSvg(output);
    }

    /// <summary>
    /// Generate a QR code and save as SVG to stream with default settings.
    /// </summary>
    /// <param name="qrCodeData">The QR code data to render.</param>
    /// <param name="output">The output stream.</param>
    /// <param name="size">SVG viewport size in units. Default is 512x512.</param>
    public static void SaveSvg(QRCodeData qrCodeData, Stream output, int size = 512)
    {
        new QRCodeImageBuilder(qrCodeData)
            .WithSize(size, size)
            .SaveToSvg(output);
    }

    /// <summary>
    /// Generate a QR code as SVG document string with default settings.
    /// </summary>
    /// <param name="content">The content to encode.</param>
    /// <param name="eccLevel">Error correction level. Default is M (15%).</param>
    /// <param name="size">SVG viewport size in units. Default is 512x512.</param>
    /// <returns>SVG document.</returns>
    public static string GetSvgString(string content, QREccLevel eccLevel = QREccLevel.M, int size = 512)
    {
        return new QRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .ToSvgString();
    }

    /// <summary>
    /// Generate a QR code as SVG document string with default settings.
    /// </summary>
    /// <param name="qrCodeData">The QR code data to render.</param>
    /// <param name="size">SVG viewport size in units. Default is 512x512.</param>
    /// <returns>SVG document.</returns>
    public static string GetSvgString(QRCodeData qrCodeData, int size = 512)
    {
        return new QRCodeImageBuilder(qrCodeData)
            .WithSize(size, size)
            .ToSvgString();
    }

    /// <summary>
    /// Generate a QR code and write as SVG (UTF-8 encoded) to an IBufferWriter with default settings.
    /// </summary>
    /// <param name="content">The content to encode.</param>
    /// <param name="writer">The buffer writer to write to.</param>
    /// <param name="eccLevel">Error correction level. Default is M (15%).</param>
    /// <param name="size">SVG viewport size in units. Default is 512x512.</param>
    public static void WriteSvg(string content, IBufferWriter<byte> writer, QREccLevel eccLevel = QREccLevel.M, int size = 512)
    {
        new QRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .SaveToSvg(writer);
    }

    /// <summary>
    /// Generate a QR code and write as SVG (UTF-8 encoded) to an IBufferWriter with default settings.
    /// </summary>
    /// <param name="qrCodeData">The QR code data to render.</param>
    /// <param name="writer">The buffer writer to write to.</param>
    /// <param name="size">SVG viewport size in units. Default is 512x512.</param>
    public static void WriteSvg(QRCodeData qrCodeData, IBufferWriter<byte> writer, int size = 512)
    {
        new QRCodeImageBuilder(qrCodeData)
            .WithSize(size, size)
            .SaveToSvg(writer);
    }

    /// <summary>
    /// Generate a QR code and write to an IBufferWriter with default PNG settings.
    /// </summary>
    /// <param name="content">The content to encode.</param>
    /// <param name="writer">The buffer writer to write to.</param>
    /// <param name="eccLevel">Error correction level. Default is M (15%).</param>
    /// <param name="size">Image size in pixels. Default is 512x512.</param>
    public static void WritePng(string content, IBufferWriter<byte> writer, QREccLevel eccLevel = QREccLevel.M, int size = 512)
    {
        WriteImage(content, writer, SKEncodedImageFormat.Png, eccLevel, size, quality: 100);
    }

    /// <summary>
    /// Generate a QR code and write to an IBufferWriter with default PNG settings.
    /// </summary>
    /// <param name="qrCodeData">The QR code data to render.</param>
    /// <param name="writer">The buffer writer to write to.</param>
    /// <param name="size">Image size in pixels. Default is 512x512.</param>
    public static void WritePng(QRCodeData qrCodeData, IBufferWriter<byte> writer, int size = 512)
    {
        WriteImage(qrCodeData, writer, SKEncodedImageFormat.Png, size, quality: 100);
    }

    /// <summary>
    /// Generate a QR code and write to an IBufferWriter with specified format.
    /// </summary>
    /// <param name="content">The content to encode.</param>
    /// <param name="writer">The buffer writer to write to.</param>
    /// <param name="format">Image format (PNG, JPEG, WEBP, etc.).</param>
    /// <param name="eccLevel">Error correction level. Default is M (15%).</param>
    /// <param name="size">Image size in pixels. Default is 512x512.</param>
    /// <param name="quality">Encoding quality (0-100). Default is 100.</param>
    public static void WriteImage(string content, IBufferWriter<byte> writer, SKEncodedImageFormat format, QREccLevel eccLevel = QREccLevel.M, int size = 512, int quality = 100)
    {
        new QRCodeImageBuilder(content)
            .WithSize(size, size)
            .WithErrorCorrection(eccLevel)
            .WithFormat(format, quality)
            .SaveTo(writer);
    }

    /// <summary>
    /// Generate a QR code and write to an IBufferWriter with specified format.
    /// </summary>
    /// <param name="qrCodeData">The QR code data to render.</param>
    /// <param name="writer">The buffer writer to write to.</param>
    /// <param name="format">Image format (PNG, JPEG, WEBP, etc.).</param>
    /// <param name="size">Image size in pixels. Default is 512x512.</param>
    /// <param name="quality">Encoding quality (0-100). Default is 100.</param>
    public static void WriteImage(QRCodeData qrCodeData, IBufferWriter<byte> writer, SKEncodedImageFormat format, int size = 512, int quality = 100)
    {
        new QRCodeImageBuilder(qrCodeData)
            .WithSize(size, size)
            .WithFormat(format, quality)
            .SaveTo(writer);
    }

    // Standard QR-specific builder methods

    /// <summary>
    /// Configure the error correction level for the QR code.
    /// </summary>
    /// <param name="eccLevel">Error correction level (L=7%, M=15%, Q=25%, H=30%). Level H is recommended when using icons or custom module shapes.</param>
    /// <returns>This builder instance for method chaining.</returns>
    public QRCodeImageBuilder WithErrorCorrection(QREccLevel eccLevel)
    {
        _eccLevel = eccLevel;
        return this;
    }

    /// <summary>
    /// Raise the error correction level above the one configured with
    /// <see cref="WithErrorCorrection(QREccLevel)"/> when the chosen version's capacity
    /// allows it, without changing the version or the symbol size. Recommended together
    /// with <see cref="WithIcon(IconData?)"/>: the spare capacity absorbs the modules
    /// the icon covers.
    /// </summary>
    /// <param name="boostEccLevel">Whether to boost; see <see cref="QRCodeGeneratorOptions.BoostEccLevel"/>.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the builder was given a pre-built <see cref="QRCodeData"/>.</exception>
    public QRCodeImageBuilder WithErrorCorrectionBoost(bool boostEccLevel = true)
    {
        if (_qrCodeData is not null)
            throw new InvalidOperationException("WithErrorCorrectionBoost cannot be used when QRCodeData is provided directly.");

        _boostEccLevel = boostEccLevel;
        return this;
    }

    /// <summary>
    /// Configure the ECI (Extended Channel Interpretation) mode for character encoding.
    /// </summary>
    /// <param name="eciMode">The ECI mode to use.</param>
    /// <returns>This builder instance for method chaining.</returns>
    public QRCodeImageBuilder WithEciMode(EciMode eciMode)
    {
        _eciMode = eciMode;
        return this;
    }

    /// <summary>
    /// Configure the QR code version to generate.
    /// </summary>
    /// <param name="version">Version to use (1-40), or -1 for automatic selection based on content length.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    /// <remarks>The pinned case of <see cref="WithVersion(QRVersionRange)"/>; -1 is <see cref="QRVersionRange.Any"/>.</remarks>
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
    /// Configure the versions the generator may choose from, rather than a single one.
    /// </summary>
    /// <param name="versionRange">The permitted versions; the smallest one that holds the content is used.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the builder was given a pre-built <see cref="QRCodeData"/>.</exception>
    /// <remarks>For a symbol that must reach, or not exceed, a physical size. An <c>int?</c> converts implicitly, so an optional version needs no branch.</remarks>
    public QRCodeImageBuilder WithVersion(QRVersionRange versionRange)
    {
        if (_qrCodeData is not null)
            throw new InvalidOperationException("WithVersion cannot be used when QRCodeData is provided directly.");

        _versionRange = versionRange;
        return this;
    }

    /// <summary>
    /// Pin one of the eight data mask patterns (0-7) instead of the automatic
    /// penalty-scored selection; see <see cref="QRCodeGeneratorOptions.MaskPattern"/>.
    /// </summary>
    /// <param name="maskPattern">Mask pattern (0-7), or null for automatic selection.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maskPattern"/> is not 0-7 or null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the builder was given a pre-built <see cref="QRCodeData"/>.</exception>
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
    /// Split the content into mixed-mode segments when that lowers the version
    /// (see <see cref="QRSegmentation"/>). Defaults to
    /// <see cref="QRSegmentation.Single"/>. Never selects a larger version, and
    /// produces the identical symbol when a split would not shrink it.
    /// </summary>
    /// <param name="segmentation">Segmentation strategy.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="segmentation"/> is not a defined value.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the builder was given a pre-built <see cref="QRCodeData"/>.</exception>
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
    /// Configure an icon to overlay on the center of the QR code.
    /// </summary>
    /// <param name="iconData">Icon configuration. If null, no icon is displayed.</param>
    /// <returns>This builder instance for method chaining.</returns>
    public QRCodeImageBuilder WithIcon(IconData? iconData)
    {
        _iconData = iconData;
        return this;
    }

    /// <summary>
    /// Configure the shape of the finder patterns.
    /// </summary>
    /// <param name="finderPatternShape">Shape to use for finder patterns. If null, uses standard pattern or same as module shape.</param>
    /// <returns>This builder instance for method chaining.</returns>
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
    /// Custom finder shapes require antialiasing; built-in icon shapes only draw
    /// rectangles, bitmaps, and text, none of which degrade under crispEdges.
    /// </summary>
    private protected override bool UseCrispEdgesCore()
    {
        return _finderPatternShape is null
            && (_iconData?.Icon is null or ImageIconShape or ImageTextIconShape);
    }
}
