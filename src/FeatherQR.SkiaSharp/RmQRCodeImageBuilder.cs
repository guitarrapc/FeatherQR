using SkiaSharp;
using FeatherQR.SkiaSharp.Internals;
using System.Buffers;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Turns text into a rectangular rMQR image, as PNG, JPEG, WebP or SVG.
/// </summary>
/// <remarks>
/// The same shape as <see cref="QRCodeImageBuilder"/>, with rMQR versions, levels and fit strategies and the 2-module quiet zone the specification asks for.
/// Because the rMQR code is rectangular, sizing has three modes: <see cref="SymbolImageBuilderBase{TSelf}.WithModulePixelSize"/> gives the matrix at an exact scale, <see cref="SymbolImageBuilderBase{TSelf}.WithSize"/> fits it into an exact canvas without stretching, and <see cref="WithWidth"/> takes a width and lets the height follow the rMQR code.
/// Icons and custom finder shapes are not offered here: rMQR has one finder pattern and no error correction headroom to spare.
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
    /// Starts a builder that draws an rMQR code you have already generated.
    /// The rMQR code is drawn exactly as given, so only the appearance options apply and the encoding options throw <see cref="InvalidOperationException"/>.
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
    /// Encodes the content and returns a PNG image.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image width in pixels (height follows the rMQR code aspect ratio). Default is 512.</param>
    public static byte[] GetPngBytes(string content, RmQREccLevel eccLevel = RmQREccLevel.M, int size = DefaultWidth)
    {
        return GetImageBytes(content, SKEncodedImageFormat.Png, eccLevel, size, 100);
    }

    /// <summary>
    /// Renders the rMQR code and returns a PNG image.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code to draw.</param>
    /// <param name="size">Image width in pixels (height follows the rMQR code aspect ratio). Default is 512.</param>
    public static byte[] GetPngBytes(RmQRCodeData rmQrCodeData, int size = DefaultWidth)
    {
        return GetImageBytes(rmQrCodeData, SKEncodedImageFormat.Png, size, 100);
    }

    /// <summary>
    /// Encodes the content and returns an image in the format you choose.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="format">The format to encode as.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image width in pixels (height follows the rMQR code aspect ratio). Default is 512.</param>
    /// <param name="quality">Quality from 0 to 100, for formats that are lossy.</param>
    public static byte[] GetImageBytes(string content, SKEncodedImageFormat format, RmQREccLevel eccLevel = RmQREccLevel.M, int size = DefaultWidth, int quality = 100)
    {
        return new RmQRCodeImageBuilder(content)
            .WithWidth(size)
            .WithErrorCorrection(eccLevel)
            .WithFormat(format, quality)
            .ToByteArray();
    }

    /// <summary>
    /// Renders the rMQR code and returns an image in the format you choose.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code to draw.</param>
    /// <param name="format">The format to encode as.</param>
    /// <param name="size">Image width in pixels (height follows the rMQR code aspect ratio). Default is 512.</param>
    /// <param name="quality">Quality from 0 to 100, for formats that are lossy.</param>
    public static byte[] GetImageBytes(RmQRCodeData rmQrCodeData, SKEncodedImageFormat format, int size = DefaultWidth, int quality = 100)
    {
        return new RmQRCodeImageBuilder(rmQrCodeData)
            .WithWidth(size)
            .WithFormat(format, quality)
            .ToByteArray();
    }

    /// <summary>
    /// Encodes the content and writes a PNG to a stream.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="output">Output stream.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image width in pixels (height follows the rMQR code aspect ratio). Default is 512.</param>
    public static void SavePng(string content, Stream output, RmQREccLevel eccLevel = RmQREccLevel.M, int size = DefaultWidth)
    {
        new RmQRCodeImageBuilder(content)
            .WithWidth(size)
            .WithErrorCorrection(eccLevel)
            .SaveTo(output);
    }

    /// <summary>
    /// Renders the rMQR code and writes a PNG to a stream.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code to draw.</param>
    /// <param name="output">Output stream.</param>
    /// <param name="size">Image width in pixels (height follows the rMQR code aspect ratio). Default is 512.</param>
    public static void SavePng(RmQRCodeData rmQrCodeData, Stream output, int size = DefaultWidth)
    {
        new RmQRCodeImageBuilder(rmQrCodeData)
            .WithWidth(size)
            .SaveTo(output);
    }

    /// <summary>
    /// Encodes the content and returns an SVG document as UTF-8 bytes.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image width in pixels (height follows the rMQR code aspect ratio). Default is 512.</param>
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
    /// Renders the rMQR code and returns an SVG document as UTF-8 bytes.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code to draw.</param>
    /// <param name="size">Image width in pixels (height follows the rMQR code aspect ratio). Default is 512.</param>
    public static byte[] GetSvgBytes(RmQRCodeData rmQrCodeData, int size = DefaultWidth)
    {
        using var stream = new MemoryStream();
        new RmQRCodeImageBuilder(rmQrCodeData)
            .WithWidth(size)
            .SaveToSvg(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Encodes the content and writes an SVG document to a stream.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="output">Output stream.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image width in pixels (height follows the rMQR code aspect ratio). Default is 512.</param>
    public static void SaveSvg(string content, Stream output, RmQREccLevel eccLevel = RmQREccLevel.M, int size = DefaultWidth)
    {
        new RmQRCodeImageBuilder(content)
            .WithWidth(size)
            .WithErrorCorrection(eccLevel)
            .SaveToSvg(output);
    }

    /// <summary>
    /// Renders the rMQR code and writes an SVG document to a stream.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code to draw.</param>
    /// <param name="output">Output stream.</param>
    /// <param name="size">Image width in pixels (height follows the rMQR code aspect ratio). Default is 512.</param>
    public static void SaveSvg(RmQRCodeData rmQrCodeData, Stream output, int size = DefaultWidth)
    {
        new RmQRCodeImageBuilder(rmQrCodeData)
            .WithWidth(size)
            .SaveToSvg(output);
    }

    /// <summary>
    /// Encodes the content and returns an SVG document as a string.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image width in pixels (height follows the rMQR code aspect ratio). Default is 512.</param>
    public static string GetSvgString(string content, RmQREccLevel eccLevel = RmQREccLevel.M, int size = DefaultWidth)
    {
        return new RmQRCodeImageBuilder(content)
            .WithWidth(size)
            .WithErrorCorrection(eccLevel)
            .ToSvgString();
    }

    /// <summary>
    /// Renders the rMQR code and returns an SVG document as a string.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code to draw.</param>
    /// <param name="size">Image width in pixels (height follows the rMQR code aspect ratio). Default is 512.</param>
    public static string GetSvgString(RmQRCodeData rmQrCodeData, int size = DefaultWidth)
    {
        return new RmQRCodeImageBuilder(rmQrCodeData)
            .WithWidth(size)
            .ToSvgString();
    }

    /// <summary>
    /// Encodes the content and writes an SVG document to a buffer writer.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="writer">Destination buffer writer.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image width in pixels (height follows the rMQR code aspect ratio). Default is 512.</param>
    public static void WriteSvg(string content, IBufferWriter<byte> writer, RmQREccLevel eccLevel = RmQREccLevel.M, int size = DefaultWidth)
    {
        new RmQRCodeImageBuilder(content)
            .WithWidth(size)
            .WithErrorCorrection(eccLevel)
            .SaveToSvg(writer);
    }

    /// <summary>
    /// Renders the rMQR code and writes an SVG document to a buffer writer.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code to draw.</param>
    /// <param name="writer">Destination buffer writer.</param>
    /// <param name="size">Image width in pixels (height follows the rMQR code aspect ratio). Default is 512.</param>
    public static void WriteSvg(RmQRCodeData rmQrCodeData, IBufferWriter<byte> writer, int size = DefaultWidth)
    {
        new RmQRCodeImageBuilder(rmQrCodeData)
            .WithWidth(size)
            .SaveToSvg(writer);
    }

    /// <summary>
    /// Encodes the content and writes a PNG to a buffer writer.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="writer">Destination buffer writer.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image width in pixels (height follows the rMQR code aspect ratio). Default is 512.</param>
    public static void WritePng(string content, IBufferWriter<byte> writer, RmQREccLevel eccLevel = RmQREccLevel.M, int size = DefaultWidth)
    {
        WriteImage(content, writer, SKEncodedImageFormat.Png, eccLevel, size, quality: 100);
    }

    /// <summary>
    /// Renders the rMQR code and writes a PNG to a buffer writer.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code to draw.</param>
    /// <param name="writer">Destination buffer writer.</param>
    /// <param name="size">Image width in pixels (height follows the rMQR code aspect ratio). Default is 512.</param>
    public static void WritePng(RmQRCodeData rmQrCodeData, IBufferWriter<byte> writer, int size = DefaultWidth)
    {
        WriteImage(rmQrCodeData, writer, SKEncodedImageFormat.Png, size, quality: 100);
    }

    /// <summary>
    /// Encodes the content and writes an image in the format you choose to a buffer writer.
    /// </summary>
    /// <param name="content">The text or URL to encode.</param>
    /// <param name="writer">Destination buffer writer.</param>
    /// <param name="format">The format to encode as.</param>
    /// <param name="eccLevel">Error correction level. Default is M.</param>
    /// <param name="size">Image width in pixels (height follows the rMQR code aspect ratio). Default is 512.</param>
    /// <param name="quality">Quality from 0 to 100, for formats that are lossy.</param>
    public static void WriteImage(string content, IBufferWriter<byte> writer, SKEncodedImageFormat format, RmQREccLevel eccLevel = RmQREccLevel.M, int size = DefaultWidth, int quality = 100)
    {
        new RmQRCodeImageBuilder(content)
            .WithWidth(size)
            .WithErrorCorrection(eccLevel)
            .WithFormat(format, quality)
            .SaveTo(writer);
    }

    /// <summary>
    /// Renders the rMQR code and writes an image in the format you choose to a buffer writer.
    /// </summary>
    /// <param name="rmQrCodeData">The rMQR code to draw.</param>
    /// <param name="writer">Destination buffer writer.</param>
    /// <param name="format">The format to encode as.</param>
    /// <param name="size">Image width in pixels (height follows the rMQR code aspect ratio). Default is 512.</param>
    /// <param name="quality">Quality from 0 to 100, for formats that are lossy.</param>
    public static void WriteImage(RmQRCodeData rmQrCodeData, IBufferWriter<byte> writer, SKEncodedImageFormat format, int size = DefaultWidth, int quality = 100)
    {
        new RmQRCodeImageBuilder(rmQrCodeData)
            .WithWidth(size)
            .WithFormat(format, quality)
            .SaveTo(writer);
    }

    // rMQR-specific builder methods

    /// <summary>
    /// Sets how much damage the rMQR code should survive, M or H.
    /// </summary>
    /// <param name="eccLevel">The level to encode at.</param>
    /// <exception cref="InvalidOperationException">Thrown when the builder was given a ready-made rMQR code.</exception>
    public RmQRCodeImageBuilder WithErrorCorrection(RmQREccLevel eccLevel)
    {
        if (_data is not null)
            throw new InvalidOperationException("WithErrorCorrection cannot be used when RmQRCodeData is provided directly.");

        _eccLevel = eccLevel;
        return this;
    }

    /// <summary>Sets the character encoding to declare in the rMQR code.</summary>
    /// <param name="eciMode">The encoding to declare. The default picks it from the content.</param>
    public RmQRCodeImageBuilder WithEciMode(EciMode eciMode)
    {
        if (_data is not null)
            throw new InvalidOperationException("WithEciMode cannot be used when RmQRCodeData is provided directly.");

        _eciMode = eciMode;
        return this;
    }

    /// <summary>
    /// Pins the version instead of letting the content choose it.
    /// </summary>
    /// <param name="version">The version to encode at. Left alone, <see cref="WithFitStrategy"/> chooses, optionally within <see cref="WithHeight"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the version is not an rMQR version.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the builder was given a ready-made rMQR code.</exception>
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
    /// Configure how the version is chosen among those that hold the content (default <see cref="RmQRFitStrategy.MinimizeArea"/>, fewest modules; note it may prefer a taller, narrower rMQR code, use <see cref="RmQRFitStrategy.MinimizeHeight"/> or <see cref="WithHeight"/> for the flattest fit).
    /// </summary>
    /// <param name="fitStrategy">Fit strategy.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the icon size, border or occupancy limit is out of range.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the icon does not fit the rMQR code.</exception>
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
    /// Restrict automatic version selection to one rMQR code height (fixed height, automatic width).
    /// Must agree with <see cref="WithVersion"/> when both are used.
    /// </summary>
    /// <param name="height">rMQR code height in modules.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the icon size, border or occupancy limit is out of range.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the icon does not fit the rMQR code.</exception>
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
    /// Split the content into mixed-mode segments when that lowers the module count (see <see cref="RmQRSegmentation"/>).
    /// Defaults to <see cref="RmQRSegmentation.Single"/>.
    /// Fewer modules is not the same as a smaller image: a flatter, wider rMQR code can render onto a larger grid.
    /// </summary>
    /// <param name="segmentation">Segmentation strategy.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the icon size, border or occupancy limit is out of range.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the icon does not fit the rMQR code.</exception>
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
    /// Configure the image width in pixels; the height follows the rMQR code aspect ratio (rounded to whole pixels), the background covers the whole image and the rMQR code is drawn at a uniform module scale inside it.
    /// This is the static helpers' sizing rule and the default (512) when no size is configured.
    /// <see cref="SymbolImageBuilderBase{TSelf}.WithSize"/> (letterbox into an exact canvas) or <see cref="SymbolImageBuilderBase{TSelf}.WithModulePixelSize"/> (exact matrix) take precedence when also called.
    /// </summary>
    /// <param name="width">Image width in pixels (must be positive).</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is out of range.</exception>
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
        SymbolRenderer.Render(canvas, contentRect, (RmQRCodeData)symbol, _codeColor, _backgroundColor, _moduleShape, _moduleSizePercent, _gradientOptions, _finderPatternShape);
    }

    /// <summary>rMQR has no finder styling or icon overlays, no extra antialiasing conditions.</summary>
    private protected override bool UseCrispEdgesCore() => _finderPatternShape is null;

    /// <summary>Rectangular rMQR codes are letterboxed into an explicit canvas, never stretched.</summary>
    private protected override bool PreserveAspectRatio => true;

    /// <summary>Default canvas: the configured (or 512) width, height from the rMQR code aspect ratio.</summary>
    private protected override Vector2Slim GetDefaultCanvasSize(int matrixWidth, int matrixHeight)
    {
        var width = _widthOnly ?? DefaultWidth;
        var height = Math.Max(1, (int)Math.Round((double)width * matrixHeight / matrixWidth));
        return new Vector2Slim(width, height);
    }
}
