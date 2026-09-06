using SkiaSharp;
using FeatherQR.SkiaSharp.Internals;
using System.Buffers;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// High-level builder for creating rMQR code images with fluent configuration and static methods.
/// </summary>
/// <remarks>
/// <para>
/// This builder mirrors <see cref="QRCodeImageBuilder"/> / <see cref="MicroQRCodeImageBuilder"/>
/// for the rMQR symbology (ISO/IEC 23941, R7x43-R17x139). Version, error correction
/// and fit use the rMQR-typed <see cref="RmQRVersion"/> / <see cref="RmQREccLevel"/> /
/// <see cref="RmQRFitStrategy"/> / <see cref="RmQRHeight"/>, and the default quiet zone
/// is the 2 modules the specification requires.
/// </para>
/// <para>
/// rMQR symbols are rectangular. With <see cref="SymbolImageBuilderBase{TSelf}.WithModulePixelSize"/>
/// the image is exactly the matrix at that scale; with <see cref="SymbolImageBuilderBase{TSelf}.WithSize"/>
/// the symbol is fitted into the canvas with a uniform module scale and centered
/// (letterbox, never stretched); with <see cref="WithWidth"/> (the static helpers'
/// <c>size</c>, and the 512-pixel default when nothing is configured) the image is
/// that wide, the height follows the symbol aspect ratio rounded to whole pixels,
/// the background covers the whole image and the symbol is drawn at a uniform
/// module scale inside it (the height rounding can leave a few pixels of
/// background at the sides on the widest versions; there is no clear-colour pad,
/// so an opaque background gives an opaque image).
/// </para>
/// <para>
/// rMQR has a single finder pattern and no error-correction headroom for overlays,
/// so the Standard QR styling options that depend on those (icon overlays and
/// custom finder pattern shapes) are intentionally not offered.
/// </para>
/// </remarks>
/// <seealso cref="RmQRCodeGenerator"/>
/// <seealso cref="SymbolRenderer"/>
public sealed class RmQRCodeImageBuilder : SymbolImageBuilderBase<RmQRCodeImageBuilder>
{
    private const int DefaultWidth = 512;

    private readonly string? _content;
    private readonly RmQRCodeData? _data;
    private RmQREccLevel _eccLevel = RmQREccLevel.M;
    private EciMode _eciMode = EciMode.Default;
    private RmQRVersion? _requestedVersion;
    private RmQRFitStrategy _fitStrategy = RmQRFitStrategy.MinimizeArea;
    private RmQRHeight? _height;
    private RmQRSegmentation _segmentation = RmQRSegmentation.Single;
    private int? _widthOnly;

    /// <summary>
    /// Starts a builder that will encode <paramref name="content"/> when you ask for an image.
    /// Error correction, version and fit keep their defaults until you set them.
    /// </summary>
    /// <param name="content">The text to encode.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="content"/> is empty or only whitespace.</exception>
    public RmQRCodeImageBuilder(string content) : base(defaultQuietZoneSize: default(RmQRCodeGeneratorOptions).QuietZoneSize)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Content cannot be empty", nameof(content));

        _content = content;
    }

    /// <summary>
    /// Starts a builder that draws an rMQR code you have already generated. The symbol is
    /// used exactly as given, so only the appearance options apply. Every encoding option
    /// throws <see cref="InvalidOperationException"/> on a builder created this way.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code to draw.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="rmQrCodeData"/> is null.</exception>
    public RmQRCodeImageBuilder(RmQRCodeData rmQrCodeData) : base(defaultQuietZoneSize: default(RmQRCodeGeneratorOptions).QuietZoneSize)
    {
        if (rmQrCodeData is null)
            throw new ArgumentNullException(nameof(rmQrCodeData));

        _data = rmQrCodeData;
    }

    // static methods for quick generation

    /// <summary>
    /// Generate an rMQR code as PNG byte array with default settings.
    /// </summary>
    /// <param name="content">The content to encode.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image width in pixels (height follows the symbol aspect ratio). Default is 512.</param>
    /// <returns>PNG encoded byte array.</returns>
    public static byte[] GetPngBytes(string content, RmQREccLevel eccLevel = RmQREccLevel.M, int size = DefaultWidth)
    {
        return GetImageBytes(content, SKEncodedImageFormat.Png, eccLevel, size, 100);
    }

    /// <summary>
    /// Generate an rMQR code as PNG byte array with default settings.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code data to render.</param>
    /// <param name="size">Image width in pixels (height follows the symbol aspect ratio). Default is 512.</param>
    /// <returns>PNG encoded byte array.</returns>
    public static byte[] GetPngBytes(RmQRCodeData rmQrCodeData, int size = DefaultWidth)
    {
        return GetImageBytes(rmQrCodeData, SKEncodedImageFormat.Png, size, 100);
    }

    /// <summary>
    /// Generate an rMQR code as image byte array with specified format.
    /// </summary>
    /// <param name="content">The content to encode.</param>
    /// <param name="format">Image format (PNG, JPEG, WEBP, etc.).</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image width in pixels (height follows the symbol aspect ratio). Default is 512.</param>
    /// <param name="quality">Encoding quality (0-100). Default is 100.</param>
    /// <returns>Encoded byte array.</returns>
    public static byte[] GetImageBytes(string content, SKEncodedImageFormat format, RmQREccLevel eccLevel = RmQREccLevel.M, int size = DefaultWidth, int quality = 100)
    {
        return new RmQRCodeImageBuilder(content)
            .WithWidth(size)
            .WithErrorCorrection(eccLevel)
            .WithFormat(format, quality)
            .ToByteArray();
    }

    /// <summary>
    /// Generate an rMQR code as image byte array with specified format.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code data to render.</param>
    /// <param name="format">Image format (PNG, JPEG, WEBP, etc.).</param>
    /// <param name="size">Image width in pixels (height follows the symbol aspect ratio). Default is 512.</param>
    /// <param name="quality">Encoding quality (0-100). Default is 100.</param>
    /// <returns>Encoded byte array.</returns>
    public static byte[] GetImageBytes(RmQRCodeData rmQrCodeData, SKEncodedImageFormat format, int size = DefaultWidth, int quality = 100)
    {
        return new RmQRCodeImageBuilder(rmQrCodeData)
            .WithWidth(size)
            .WithFormat(format, quality)
            .ToByteArray();
    }

    /// <summary>
    /// Generate an rMQR code and save as PNG to stream.
    /// </summary>
    /// <param name="content">The content to encode.</param>
    /// <param name="output">Output stream.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image width in pixels (height follows the symbol aspect ratio). Default is 512.</param>
    public static void SavePng(string content, Stream output, RmQREccLevel eccLevel = RmQREccLevel.M, int size = DefaultWidth)
    {
        new RmQRCodeImageBuilder(content)
            .WithWidth(size)
            .WithErrorCorrection(eccLevel)
            .SaveTo(output);
    }

    /// <summary>
    /// Generate an rMQR code and save as PNG to stream.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code data to render.</param>
    /// <param name="output">Output stream.</param>
    /// <param name="size">Image width in pixels (height follows the symbol aspect ratio). Default is 512.</param>
    public static void SavePng(RmQRCodeData rmQrCodeData, Stream output, int size = DefaultWidth)
    {
        new RmQRCodeImageBuilder(rmQrCodeData)
            .WithWidth(size)
            .SaveTo(output);
    }

    /// <summary>
    /// Generate an rMQR code as SVG byte array.
    /// </summary>
    /// <param name="content">The content to encode.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image width in pixels (height follows the symbol aspect ratio). Default is 512.</param>
    /// <returns>SVG document as UTF-8 bytes.</returns>
    public static byte[] GetSvgBytes(string content, RmQREccLevel eccLevel = RmQREccLevel.M, int size = DefaultWidth)
    {
        using var stream = new MemoryStream();
        new RmQRCodeImageBuilder(content)
            .WithWidth(size)
            .WithErrorCorrection(eccLevel)
            .SaveToSvg(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Generate an rMQR code as SVG byte array.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code data to render.</param>
    /// <param name="size">Image width in pixels (height follows the symbol aspect ratio). Default is 512.</param>
    /// <returns>SVG document as UTF-8 bytes.</returns>
    public static byte[] GetSvgBytes(RmQRCodeData rmQrCodeData, int size = DefaultWidth)
    {
        using var stream = new MemoryStream();
        new RmQRCodeImageBuilder(rmQrCodeData)
            .WithWidth(size)
            .SaveToSvg(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Generate an rMQR code and save as SVG to stream.
    /// </summary>
    /// <param name="content">The content to encode.</param>
    /// <param name="output">Output stream.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image width in pixels (height follows the symbol aspect ratio). Default is 512.</param>
    public static void SaveSvg(string content, Stream output, RmQREccLevel eccLevel = RmQREccLevel.M, int size = DefaultWidth)
    {
        new RmQRCodeImageBuilder(content)
            .WithWidth(size)
            .WithErrorCorrection(eccLevel)
            .SaveToSvg(output);
    }

    /// <summary>
    /// Generate an rMQR code and save as SVG to stream.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code data to render.</param>
    /// <param name="output">Output stream.</param>
    /// <param name="size">Image width in pixels (height follows the symbol aspect ratio). Default is 512.</param>
    public static void SaveSvg(RmQRCodeData rmQrCodeData, Stream output, int size = DefaultWidth)
    {
        new RmQRCodeImageBuilder(rmQrCodeData)
            .WithWidth(size)
            .SaveToSvg(output);
    }

    /// <summary>
    /// Generate an rMQR code as SVG string.
    /// </summary>
    /// <param name="content">The content to encode.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image width in pixels (height follows the symbol aspect ratio). Default is 512.</param>
    /// <returns>SVG document.</returns>
    public static string GetSvgString(string content, RmQREccLevel eccLevel = RmQREccLevel.M, int size = DefaultWidth)
    {
        return new RmQRCodeImageBuilder(content)
            .WithWidth(size)
            .WithErrorCorrection(eccLevel)
            .ToSvgString();
    }

    /// <summary>
    /// Generate an rMQR code as SVG string.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code data to render.</param>
    /// <param name="size">Image width in pixels (height follows the symbol aspect ratio). Default is 512.</param>
    /// <returns>SVG document.</returns>
    public static string GetSvgString(RmQRCodeData rmQrCodeData, int size = DefaultWidth)
    {
        return new RmQRCodeImageBuilder(rmQrCodeData)
            .WithWidth(size)
            .ToSvgString();
    }

    /// <summary>
    /// Generate an rMQR code and write the SVG document to a buffer writer.
    /// </summary>
    /// <param name="content">The content to encode.</param>
    /// <param name="writer">Destination buffer writer.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image width in pixels (height follows the symbol aspect ratio). Default is 512.</param>
    public static void WriteSvg(string content, IBufferWriter<byte> writer, RmQREccLevel eccLevel = RmQREccLevel.M, int size = DefaultWidth)
    {
        new RmQRCodeImageBuilder(content)
            .WithWidth(size)
            .WithErrorCorrection(eccLevel)
            .SaveToSvg(writer);
    }

    /// <summary>
    /// Generate an rMQR code and write the SVG document to a buffer writer.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code data to render.</param>
    /// <param name="writer">Destination buffer writer.</param>
    /// <param name="size">Image width in pixels (height follows the symbol aspect ratio). Default is 512.</param>
    public static void WriteSvg(RmQRCodeData rmQrCodeData, IBufferWriter<byte> writer, int size = DefaultWidth)
    {
        new RmQRCodeImageBuilder(rmQrCodeData)
            .WithWidth(size)
            .SaveToSvg(writer);
    }

    /// <summary>
    /// Generate an rMQR code and write PNG bytes to a buffer writer.
    /// </summary>
    /// <param name="content">The content to encode.</param>
    /// <param name="writer">Destination buffer writer.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image width in pixels (height follows the symbol aspect ratio). Default is 512.</param>
    public static void WritePng(string content, IBufferWriter<byte> writer, RmQREccLevel eccLevel = RmQREccLevel.M, int size = DefaultWidth)
    {
        WriteImage(content, writer, SKEncodedImageFormat.Png, eccLevel, size, quality: 100);
    }

    /// <summary>
    /// Generate an rMQR code and write PNG bytes to a buffer writer.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code data to render.</param>
    /// <param name="writer">Destination buffer writer.</param>
    /// <param name="size">Image width in pixels (height follows the symbol aspect ratio). Default is 512.</param>
    public static void WritePng(RmQRCodeData rmQrCodeData, IBufferWriter<byte> writer, int size = DefaultWidth)
    {
        WriteImage(rmQrCodeData, writer, SKEncodedImageFormat.Png, size, quality: 100);
    }

    /// <summary>
    /// Generate an rMQR code and write encoded image bytes to a buffer writer.
    /// </summary>
    /// <param name="content">The content to encode.</param>
    /// <param name="writer">Destination buffer writer.</param>
    /// <param name="format">Image format (PNG, JPEG, WEBP, etc.).</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image width in pixels (height follows the symbol aspect ratio). Default is 512.</param>
    /// <param name="quality">Encoding quality (0-100). Default is 100.</param>
    public static void WriteImage(string content, IBufferWriter<byte> writer, SKEncodedImageFormat format, RmQREccLevel eccLevel = RmQREccLevel.M, int size = DefaultWidth, int quality = 100)
    {
        new RmQRCodeImageBuilder(content)
            .WithWidth(size)
            .WithErrorCorrection(eccLevel)
            .WithFormat(format, quality)
            .SaveTo(writer);
    }

    /// <summary>
    /// Generate an rMQR code and write encoded image bytes to a buffer writer.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code data to render.</param>
    /// <param name="writer">Destination buffer writer.</param>
    /// <param name="format">Image format (PNG, JPEG, WEBP, etc.).</param>
    /// <param name="size">Image width in pixels (height follows the symbol aspect ratio). Default is 512.</param>
    /// <param name="quality">Encoding quality (0-100). Default is 100.</param>
    public static void WriteImage(RmQRCodeData rmQrCodeData, IBufferWriter<byte> writer, SKEncodedImageFormat format, int size = DefaultWidth, int quality = 100)
    {
        new RmQRCodeImageBuilder(rmQrCodeData)
            .WithWidth(size)
            .WithFormat(format, quality)
            .SaveTo(writer);
    }

    // rMQR-specific builder methods

    /// <summary>
    /// Configure the error correction level (M or H).
    /// </summary>
    /// <param name="eccLevel">Error correction level.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="InvalidOperationException"></exception>
    public RmQRCodeImageBuilder WithErrorCorrection(RmQREccLevel eccLevel)
    {
        if (_data is not null)
            throw new InvalidOperationException("WithErrorCorrection cannot be used when RmQRCodeData is provided directly.");

        _eccLevel = eccLevel;
        return this;
    }

    /// <summary>Configure the ECI character encoding declaration.</summary>
    /// <param name="eciMode">Default auto-detects ASCII, ISO-8859-1 and UTF-8.</param>
    /// <returns>This builder instance for method chaining.</returns>
    public RmQRCodeImageBuilder WithEciMode(EciMode eciMode)
    {
        if (_data is not null)
            throw new InvalidOperationException("WithEciMode cannot be used when RmQRCodeData is provided directly.");

        _eciMode = eciMode;
        return this;
    }

    /// <summary>
    /// Configure the exact rMQR version to generate.
    /// </summary>
    /// <param name="version">Version to use (R7x43-R17x139). When not called, the version is chosen by <see cref="WithFitStrategy"/> (default: fewest modules), optionally within <see cref="WithHeight"/>.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public RmQRCodeImageBuilder WithVersion(RmQRVersion version)
    {
        if (_data is not null)
            throw new InvalidOperationException("WithVersion cannot be used when RmQRCodeData is provided directly.");
        if (!Enum.IsDefined(typeof(RmQRVersion), version))
            throw new ArgumentOutOfRangeException(nameof(version), $"Invalid rMQR version: {version}");

        _requestedVersion = version;
        return this;
    }

    /// <summary>
    /// Configure how the version is chosen among those that hold the content
    /// (default <see cref="RmQRFitStrategy.MinimizeArea"/>, fewest modules; note it may
    /// prefer a taller, narrower symbol, use <see cref="RmQRFitStrategy.MinimizeHeight"/>
    /// or <see cref="WithHeight"/> for the flattest fit).
    /// </summary>
    /// <param name="fitStrategy">Fit strategy.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public RmQRCodeImageBuilder WithFitStrategy(RmQRFitStrategy fitStrategy)
    {
        if (_data is not null)
            throw new InvalidOperationException("WithFitStrategy cannot be used when RmQRCodeData is provided directly.");
        if (fitStrategy is < RmQRFitStrategy.MinimizeArea or > RmQRFitStrategy.MinimizeHeight)
            throw new ArgumentOutOfRangeException(nameof(fitStrategy), $"Invalid rMQR fit strategy: {fitStrategy}");

        _fitStrategy = fitStrategy;
        return this;
    }

    /// <summary>
    /// Restrict automatic version selection to one symbol height (fixed height,
    /// automatic width). Must agree with <see cref="WithVersion"/> when both are used.
    /// </summary>
    /// <param name="height">Symbol height in modules.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public RmQRCodeImageBuilder WithHeight(RmQRHeight height)
    {
        if (_data is not null)
            throw new InvalidOperationException("WithHeight cannot be used when RmQRCodeData is provided directly.");
        if (height is not (RmQRHeight.H7 or RmQRHeight.H9 or RmQRHeight.H11 or RmQRHeight.H13 or RmQRHeight.H15 or RmQRHeight.H17))
            throw new ArgumentOutOfRangeException(nameof(height), $"Invalid rMQR height: {height}");

        _height = height;
        return this;
    }

    /// <summary>
    /// Split the content into mixed-mode segments when that lowers the module count
    /// (see <see cref="RmQRSegmentation"/>). Defaults to
    /// <see cref="RmQRSegmentation.Single"/>. Fewer modules is not the same as a
    /// smaller image: a flatter, wider symbol can render onto a larger grid.
    /// </summary>
    /// <param name="segmentation">Segmentation strategy.</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public RmQRCodeImageBuilder WithSegmentation(RmQRSegmentation segmentation)
    {
        if (_data is not null)
            throw new InvalidOperationException("WithSegmentation cannot be used when RmQRCodeData is provided directly.");
        if (segmentation is not (RmQRSegmentation.Single or RmQRSegmentation.Optimal))
            throw new ArgumentOutOfRangeException(nameof(segmentation), $"Invalid rMQR segmentation: {segmentation}");

        _segmentation = segmentation;
        return this;
    }

    /// <summary>
    /// Configure the image width in pixels; the height follows the symbol aspect
    /// ratio (rounded to whole pixels), the background covers the whole image and
    /// the symbol is drawn at a uniform module scale inside it. This is the static
    /// helpers' sizing rule and the default (512) when no size is configured.
    /// <see cref="SymbolImageBuilderBase{TSelf}.WithSize"/> (letterbox into an exact
    /// canvas) or <see cref="SymbolImageBuilderBase{TSelf}.WithModulePixelSize"/>
    /// (exact matrix) take precedence when also called.
    /// </summary>
    /// <param name="width">Image width in pixels (must be positive).</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public RmQRCodeImageBuilder WithWidth(int width)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");

        _widthOnly = width;
        return this;
    }

    // symbology hooks

    private protected override object ResolveSymbol(out int matrixWidth, out int matrixHeight)
    {
        var data = _data ?? RmQRCodeGenerator.Create(_content.AsSpan(), _eccLevel, new RmQRCodeGeneratorOptions
        {
            EciMode = _eciMode,
            Version = _requestedVersion,
            FitStrategy = _fitStrategy,
            Height = _height,
            QuietZoneSize = _quietZoneSize,
            Segmentation = _segmentation,
        });
        matrixWidth = data.Width;
        matrixHeight = data.Height;
        return data;
    }

    private protected override void RenderSymbol(SKCanvas canvas, object symbol, SKRect contentRect)
    {
        SymbolRenderer.Render(canvas, contentRect, (RmQRCodeData)symbol, _codeColor, _backgroundColor, _moduleShape, _moduleSizePercent, _gradientOptions);
    }

    /// <summary>rMQR has no finder styling or icon overlays, no extra antialiasing conditions.</summary>
    private protected override bool UseCrispEdgesCore() => true;

    /// <summary>Rectangular symbols are letterboxed into an explicit canvas, never stretched.</summary>
    private protected override bool PreserveAspectRatio => true;

    /// <summary>Default canvas: the configured (or 512) width, height from the symbol aspect ratio.</summary>
    private protected override Vector2Slim GetDefaultCanvasSize(int matrixWidth, int matrixHeight)
    {
        var width = _widthOnly ?? DefaultWidth;
        var height = Math.Max(1, (int)Math.Round((double)width * matrixHeight / matrixWidth));
        return new Vector2Slim(width, height);
    }
}
