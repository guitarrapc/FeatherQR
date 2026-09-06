using SkiaSharp;
using FeatherQR.SkiaSharp.Internals;
using System.Buffers;
using System.Text;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Shared implementation for the symbology-specific QR image builders (<see cref="QRCodeImageBuilder"/>, <see cref="MicroQRCodeImageBuilder"/>): the fluent options every symbology supports, canvas layout, and the complete raster/SVG output surface.
/// Symbology-specific concerns, error correction and version types, icon overlays, finder pattern styling, live on the derived builders.
/// </summary>
/// <remarks>
/// <para>
/// The self-referential type parameter keeps fluent chains typed to the concrete builder, so shared and symbology-specific options mix freely without casts: <c>new MicroQRCodeImageBuilder("...").WithSize(256, 256).WithVersion(MicroQRVersion.M4)</c>.
/// </para>
/// <para>
/// Deriving from this class outside the library is not supported: the abstract hooks that connect a symbology's data model to the shared output pipeline are <c>private protected</c>.
/// </para>
/// </remarks>
/// <typeparam name="TSelf">The concrete builder type (self-referential).</typeparam>
public abstract class SymbolImageBuilderBase<TSelf> where TSelf : SymbolImageBuilderBase<TSelf>
{
    private Vector2Slim? _explicitSize;
    private SKEncodedImageFormat _format = SKEncodedImageFormat.Png;
    private int _quality = 100;
    private int? _modulePixelSize;

    private protected int _quietZoneSize;
    private protected SKColor? _codeColor;
    private protected SKColor? _backgroundColor;
    private protected SKColor? _clearColor;
    private protected ModuleShape? _moduleShape;
    private protected float _moduleSizePercent = 1.0f;
    private protected GradientOptions? _gradientOptions;

    private protected SymbolImageBuilderBase(int defaultQuietZoneSize)
    {
        _quietZoneSize = defaultQuietZoneSize;
    }

    // ─── Symbology hooks ───

    /// <summary>
    /// Resolves the symbol to render (encoding the configured content when the builder was not given pre-built data) and reports its matrix side length including the quiet zone (width and height; equal for square symbologies).
    /// Called exactly once per output operation.
    /// </summary>
    private protected abstract object ResolveSymbol(out int matrixWidth, out int matrixHeight);

    /// <summary>
    /// Whether an explicit canvas size fits the symbol with a uniform module scale (letterbox) instead of filling the canvas.
    /// Square symbologies keep the historical fill behavior; rectangular symbologies must never be stretched.
    /// </summary>
    private protected virtual bool PreserveAspectRatio => false;

    /// <summary>
    /// Canvas size used when neither <see cref="WithSize"/> nor <see cref="WithModulePixelSize"/> was called (512 × 512 for square symbologies).
    /// </summary>
    private protected virtual Vector2Slim GetDefaultCanvasSize(int matrixWidth, int matrixHeight) => new(512, 512);

    /// <summary>Draws the resolved symbol into the content rectangle.</summary>
    private protected abstract void RenderSymbol(SKCanvas canvas, object symbol, SKRect contentRect);

    /// <summary>
    /// Symbology-specific part of the crispEdges decision (e.g.
    /// Standard QR must keep antialiasing for custom finder shapes and drawn icon overlays).
    /// </summary>
    private protected abstract bool UseCrispEdgesCore();

    // ─── Fluent options shared by every symbology ───

    /// <summary>
    /// Sets the output image size in pixels.
    /// </summary>
    /// <remarks>
    /// On its own, the symbol fills the canvas: a square symbology divides the canvas by the matrix size, which can be fractional and shifts when the version changes, while rMQR is fitted with one uniform module scale and the leftover is padded.
    /// Combined with <see cref="WithModulePixelSize(int)"/> this is the canvas alone, the modules decide the content size, and the content is centered with the rest padded in the clear color.
    /// A canvas smaller than the content on either side is an error.
    /// </remarks>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when either side is not positive.</exception>
    public TSelf WithSize(int width, int height)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive");

        _explicitSize = new Vector2Slim(width, height);
        return (TSelf)this;
    }

    /// <summary>
    /// Gives every module an exact pixel size, so the symbol comes out crisp at any version.
    /// </summary>
    /// <remarks>
    /// On its own the image is exactly as large as the symbol needs.
    /// Combined with <see cref="WithSize(int, int)"/> the content is centered on that canvas and the rest padded in the clear color.
    /// </remarks>
    /// <param name="modulePixelSize">Pixels per module.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the size is not positive.</exception>
    public TSelf WithModulePixelSize(int modulePixelSize)
    {
        if (modulePixelSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(modulePixelSize), "Module pixel size must be positive");

        _modulePixelSize = modulePixelSize;
        return (TSelf)this;
    }

    /// <summary>
    /// Sets the encoded image format and quality.
    /// PNG at 100 by default.
    /// </summary>
    /// <param name="format">The format to encode as.</param>
    /// <param name="quality">Quality from 0 to 100, for formats that are lossy.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the quality is outside 0 to 100.</exception>
    public TSelf WithFormat(SKEncodedImageFormat format, int quality = 100)
    {
        if (quality is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(quality), "Quality must be between 0 and 100");

        _format = format;
        _quality = quality;
        return (TSelf)this;
    }

    /// <summary>
    /// Sets the width of the light border around the symbol.
    /// </summary>
    /// <remarks>
    /// Defaults to what the specification asks for: 4 modules for Standard QR, 2 for Micro QR and rMQR.
    /// Ignored when the builder was given a ready-made symbol, which carries its own quiet zone.
    /// </remarks>
    /// <param name="size">Width in modules, 0 to 10.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the width is outside 0 to 10.</exception>
    public TSelf WithQuietZone(int size)
    {
        if (size is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(size), "Quiet zone size must be between 0 and 10");
        _quietZoneSize = size;
        return (TSelf)this;
    }

    /// <summary>
    /// Sets the colors of the image.
    /// </summary>
    /// <param name="codeColor">The dark modules. Black when omitted.</param>
    /// <param name="backgroundColor">Behind the symbol, quiet zone included. White when omitted.</param>
    /// <param name="clearColor">The padding around the symbol when the canvas is larger than the content. Transparent when omitted.</param>
    public TSelf WithColors(SKColor? codeColor = null, SKColor? backgroundColor = null, SKColor? clearColor = null)
    {
        _codeColor = codeColor;
        _backgroundColor = backgroundColor;
        _clearColor = clearColor;
        return (TSelf)this;
    }

    /// <summary>
    /// Draws the modules as a shape other than plain squares.
    /// </summary>
    /// <remarks>
    /// Every custom shape costs scan margin, and below 0.8 the symbol may stop scanning reliably, so test what you ship.
    /// On Standard QR the shape reaches the finder patterns too, unless <c>WithFinderPatternShape</c> gives them one of their own.
    /// </remarks>
    /// <param name="moduleShape">The shape to draw. Squares when omitted.</param>
    /// <param name="sizePercent">How much of its cell a module fills, 0.5 to 1.0. The default 1.0 leaves no gaps.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the size is outside 0.5 to 1.0.</exception>
    public TSelf WithModuleShape(ModuleShape? moduleShape, float sizePercent = 1.0f)
    {
        if (sizePercent is < 0.5f or > 1.0f)
            throw new ArgumentOutOfRangeException(nameof(sizePercent), "Module size percent must be between 0.5 and 1.0.");

        _moduleShape = moduleShape;
        _moduleSizePercent = sizePercent;
        return (TSelf)this;
    }

    /// <summary>
    /// Paints the modules with a gradient instead of one solid color.
    /// </summary>
    /// <param name="gradientOptions">The gradient to paint. Solid color when omitted.</param>
    public TSelf WithGradient(GradientOptions? gradientOptions)
    {
        _gradientOptions = gradientOptions;
        return (TSelf)this;
    }

    // ─── Output surface ───

    /// <summary>
    /// Renders the symbol and writes the encoded image to a stream.
    /// </summary>
    /// <param name="output">Where to write. Left open afterwards.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="output"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="output"/> is not writable.</exception>
    public void SaveTo(Stream output)
    {
        if (output is null)
            throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite)
            throw new ArgumentException("Output stream must be writable", nameof(output));

        using var image = GenerateImage();
        using var data = image.Encode(_format, _quality);

        // write to stream
        data.SaveTo(output);
    }

    /// <summary>
    /// Renders the symbol and writes the encoded image to a buffer writer, skipping the intermediate buffering the stream overload does.
    /// </summary>
    /// <param name="writer">Where to write.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="writer"/> is <c>null</c>.</exception>
    public void SaveTo(IBufferWriter<byte> writer)
    {
        if (writer is null)
            throw new ArgumentNullException(nameof(writer));

        using var image = GenerateImage();
        using var data = image.Encode(_format, _quality);

        // Write in writer-provided segments; a single GetSpan for the whole payload
        // would force segmented writers (e.g. PipeWriter) into one contiguous buffer.
        writer.Write(data.AsSpan());
    }

    /// <summary>
    /// Renders the symbol as an SVG document and writes it to a stream.
    /// </summary>
    /// <remarks>
    /// The symbol is drawn as vector shapes, so it scales without losing quality, and every builder option applies.
    /// The root element carries a <c>viewBox</c>, so the document resizes when embedded at another size.
    /// Plain square modules get <c>shape-rendering="crispEdges"</c> to avoid antialiasing seams; custom shapes keep antialiasing for smooth curves.
    /// <see cref="WithFormat(SKEncodedImageFormat, int)"/> does not apply, since SVG is not a raster format; the size options set the viewport instead.
    /// </remarks>
    /// <param name="output">Where to write. Left open afterwards.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="output"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="output"/> is not writable.</exception>
    public void SaveToSvg(Stream output)
    {
        if (output is null)
            throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite)
            throw new ArgumentException("Output stream must be writable", nameof(output));

        RenderSvg(output);
    }

    /// <summary>
    /// Renders the symbol as an SVG document and writes it to a buffer writer.
    /// </summary>
    /// <remarks>
    /// Renders exactly as <see cref="SaveToSvg(Stream)"/>.
    /// The document is written in the writer own segments, so a segmented writer such as a pipe never has to hold it contiguously.
    /// </remarks>
    /// <param name="writer">Where to write.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="writer"/> is <c>null</c>.</exception>
    public void SaveToSvg(IBufferWriter<byte> writer)
    {
        if (writer is null)
            throw new ArgumentNullException(nameof(writer));

        using var stream = new BufferWriterStream(writer);
        RenderSvg(stream);
    }

    /// <summary>
    /// Renders the symbol as an SVG document and returns it as a string.
    /// </summary>
    /// <remarks>
    /// Renders exactly as <see cref="SaveToSvg(Stream)"/>.
    /// </remarks>
    public string ToSvgString()
    {
        using var stream = new MemoryStream();
        SaveToSvg(stream);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <summary>
    /// Renders the symbol and returns the encoded image bytes.
    /// </summary>
    public byte[] ToByteArray()
    {
        using var image = GenerateImage();
        using var data = image.Encode(_format, _quality);
        return data.ToArray();
    }

    /// <summary>
    /// Renders the symbol as an <see cref="SKImage"/>, which the caller disposes.
    /// </summary>
    public SKImage ToImage()
    {
        return GenerateImage();
    }

    /// <summary>
    /// Renders the symbol as an <see cref="SKBitmap"/>, which the caller disposes.
    /// </summary>
    public SKBitmap ToBitmap()
    {
        using var image = GenerateImage();
        return SKBitmap.FromImage(image);
    }

    // ─── Shared pipeline ───

    /// <summary>
    /// Renders the SVG document to the output stream, injecting root element attributes (<c>viewBox</c>, optional <c>shape-rendering</c>) while streaming.
    /// </summary>
    /// <remarks>
    /// <see cref="SKSvgCanvas"/> writes <c>width</c>/<c>height</c> on the root element but no <c>viewBox</c>.
    /// Without a viewBox, an SVG embedded at a different size (img element, CSS) keeps its content at the original coordinates instead of scaling, the main reason to use SVG in the first place.
    /// <see cref="SvgRootAttributeInjectorStream"/> inserts the attributes right after the <c>&lt;svg </c> marker while the canvas streams to the output, so the document is never buffered as a whole; if the marker is not found (unexpected upstream format change), the document passes through unpatched.
    /// </remarks>
    private void RenderSvg(Stream output)
    {
        var symbol = ResolveSymbol(out var matrixWidth, out var matrixHeight);
        var (info, contentRect) = QRImageLayout.CreateLayout(matrixWidth, matrixHeight, _explicitSize, _modulePixelSize, PreserveAspectRatio, GetDefaultCanvasSize(matrixWidth, matrixHeight));

        var viewBox = $"viewBox=\"0 0 {info.Width} {info.Height}\" ";
        var rootAttributes = UseCrispEdges() ? viewBox + "shape-rendering=\"crispEdges\" " : viewBox;

        using var injector = new SvgRootAttributeInjectorStream(output, rootAttributes);
        // SKSvgCanvas flushes the SVG document when the canvas is disposed, so dispose
        // it before the injector (which then flushes any pending header bytes).
        // The output stream itself stays open.
        using (var canvas = SKSvgCanvas.Create(SKRect.Create(0, 0, info.Width, info.Height), injector))
        {
            RenderContent(canvas, symbol, info, contentRect);
        }
    }

    /// <summary>
    /// Antialiasing between adjacent vector shapes produces visible hairline seams when the SVG is scaled to non-integer sizes.
    /// For plain rectangular modules crispEdges removes the seams; custom shapes keep antialiasing for smooth curves.
    /// The symbology hook adds conditions the shared options cannot see (custom finder shapes, drawn icon overlays).
    /// </summary>
    private bool UseCrispEdges()
    {
        return (_moduleShape is null || _moduleShape is RectangleModuleShape)
            && _moduleSizePercent == 1.0f
            && UseCrispEdgesCore();
    }

    /// <summary>
    /// Generate the SKImage from the resolved symbol.
    /// </summary>
    private SKImage GenerateImage()
    {
        var symbol = ResolveSymbol(out var matrixWidth, out var matrixHeight);

        var (info, contentRect) = QRImageLayout.CreateLayout(matrixWidth, matrixHeight, _explicitSize, _modulePixelSize, PreserveAspectRatio, GetDefaultCanvasSize(matrixWidth, matrixHeight));

        var clearColor = _clearColor ?? SKColors.Transparent;
        var contentCoversCanvas = QRImageLayout.ContentCoversCanvas(contentRect, info);
        var backgroundIsOpaque = (_backgroundColor ?? SKColors.White).Alpha == byte.MaxValue;
        var clearIsOpaque = clearColor.Alpha == byte.MaxValue;

        // When the base layer (background fill, or the cleared canvas) is opaque
        // everywhere, anything drawn over it stays opaque, so the whole image is
        // opaque no matter what modules/icons/gradients are painted on top.
        // An opaque surface lets encoders skip the alpha channel and the unpremul
        // pass, PNG output becomes RGB: smaller and faster to encode.
        var isOpaque = contentCoversCanvas
            ? backgroundIsOpaque || clearIsOpaque
            : clearIsOpaque;
        if (isOpaque)
        {
            info = info.WithAlphaType(SKAlphaType.Opaque);
        }

        using var surface = SKSurface.Create(info);
        RenderContent(surface.Canvas, symbol, info, contentRect);

        return surface.Snapshot();
    }

    /// <summary>
    /// Draws the configured symbol onto the canvas.
    /// Shared by the raster (<see cref="GenerateImage"/>) and SVG (<see cref="SaveToSvg(Stream)"/>) paths.
    /// </summary>
    private void RenderContent(SKCanvas canvas, object symbol, SKImageInfo info, SKRect contentRect)
    {
        var clearColor = _clearColor ?? SKColors.Transparent;
        var contentCoversCanvas = QRImageLayout.ContentCoversCanvas(contentRect, info);
        var backgroundIsOpaque = (_backgroundColor ?? SKColors.White).Alpha == byte.MaxValue;

        // Clear the canvas with clearColor, then draw into contentRect; extra
        // canvas area (pad) keeps clearColor. The clear is skipped when it cannot
        // remain visible: a fresh canvas is already fully transparent, and an
        // opaque background covering the whole canvas overwrites it anyway.
        if (clearColor.Alpha != 0 && !(contentCoversCanvas && backgroundIsOpaque))
        {
            canvas.Clear(clearColor);
        }

        RenderSymbol(canvas, symbol, contentRect);
    }
}
