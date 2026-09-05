using SkiaSharp;
using FeatherQR.SkiaSharp.Internals;
using System.Buffers;
using System.Text;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Shared implementation for the symbology-specific QR image builders
/// (<see cref="QRCodeImageBuilder"/>, <see cref="MicroQRCodeImageBuilder"/>):
/// the fluent options every symbology supports, canvas layout, and the complete
/// raster/SVG output surface. Symbology-specific concerns, error correction and
/// version types, icon overlays, finder pattern styling, live on the derived
/// builders.
/// </summary>
/// <remarks>
/// <para>
/// The self-referential type parameter keeps fluent chains typed to the concrete
/// builder, so shared and symbology-specific options mix freely without casts:
/// <c>new MicroQRCodeImageBuilder("...").WithSize(256, 256).WithVersion(MicroQRVersion.M4)</c>.
/// </para>
/// <para>
/// Deriving from this class outside the library is not supported: the abstract
/// hooks that connect a symbology's data model to the shared output pipeline are
/// <c>private protected</c>.
/// </para>
/// </remarks>
/// <typeparam name="TSelf">The concrete builder type (self-referential).</typeparam>
public abstract class QRCodeImageBuilderBase<TSelf> where TSelf : QRCodeImageBuilderBase<TSelf>
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

    private protected QRCodeImageBuilderBase(int defaultQuietZoneSize)
    {
        _quietZoneSize = defaultQuietZoneSize;
    }

    // ─── Symbology hooks ───

    /// <summary>
    /// Resolves the symbol to render (encoding the configured content when the
    /// builder was not given pre-built data) and reports its matrix side length
    /// including the quiet zone (width and height; equal for square symbologies).
    /// Called exactly once per output operation.
    /// </summary>
    private protected abstract object ResolveSymbol(out int matrixWidth, out int matrixHeight);

    /// <summary>
    /// Whether an explicit canvas size fits the symbol with a uniform module scale
    /// (letterbox) instead of filling the canvas. Square symbologies keep the
    /// historical fill behavior; rectangular symbologies must never be stretched.
    /// </summary>
    private protected virtual bool PreserveAspectRatio => false;

    /// <summary>
    /// Canvas size used when neither <see cref="WithSize"/> nor
    /// <see cref="WithModulePixelSize"/> was called (512 × 512 for square symbologies).
    /// </summary>
    private protected virtual Vector2Slim GetDefaultCanvasSize(int matrixWidth, int matrixHeight) => new(512, 512);

    /// <summary>Draws the resolved symbol into the content rectangle.</summary>
    private protected abstract void RenderSymbol(SKCanvas canvas, object symbol, SKRect contentRect);

    /// <summary>
    /// Symbology-specific part of the crispEdges decision (e.g. Standard QR must
    /// keep antialiasing for custom finder shapes and drawn icon overlays).
    /// </summary>
    private protected abstract bool UseCrispEdgesCore();

    // ─── Fluent options shared by every symbology ───

    /// <summary>
    /// Configure the output image size in absolute pixels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used alone, this sets an exact canvas size. For the square symbologies the module
    /// pixel size then becomes <c>imageSize / matrixSize</c>, which may be fractional and can
    /// change when the version changes; the rectangular rMQR builder fits the symbol into
    /// the canvas with a uniform module scale instead (letterbox, the leftover pad keeps
    /// <c>clearColor</c>).
    /// </para>
    /// <para>
    /// Used with <see cref="WithModulePixelSize(int)"/>, this sets the canvas size while module pixels
    /// define the content size (<c>matrixSize * modulePixelSize</c>).
    /// The canvas must be at least as large as the content on both sides; extra space is padded and
    /// the content is centered. Padding uses <c>clearColor</c> from <see cref="WithColors"/>.
    /// </para>
    /// </remarks>
    /// <param name="width">Width in pixels (must be positive).</param>
    /// <param name="height">Height in pixels (must be positive).</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
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
    /// Configure content size from pixels-per-module.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sets each module to an exact integer pixel size. Content side length is
    /// <c>matrixSize * modulePixelSize</c>.
    /// </para>
    /// <para>
    /// Used alone, the output image matches the content size.
    /// Used with <see cref="WithSize(int, int)"/>, the content is centered on the larger canvas
    /// and padded with <c>clearColor</c>. If the canvas is smaller than the content, rendering throws.
    /// </para>
    /// </remarks>
    /// <param name="modulePixelSize">Pixel size per module (must be positive).</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public TSelf WithModulePixelSize(int modulePixelSize)
    {
        if (modulePixelSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(modulePixelSize), "Module pixel size must be positive");

        _modulePixelSize = modulePixelSize;
        return (TSelf)this;
    }

    /// <summary>
    /// Configure the output image format and quality.
    /// </summary>
    /// <param name="format">The image format to use.</param>
    /// <param name="quality">The image quality (0-100).</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public TSelf WithFormat(SKEncodedImageFormat format, int quality = 100)
    {
        if (quality is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(quality), "Quality must be between 0 and 100");

        _format = format;
        _quality = quality;
        return (TSelf)this;
    }

    /// <summary>
    /// Configure the quiet zone size (light border) around the symbol.
    /// </summary>
    /// <remarks>
    /// When not called, the builder uses its symbology's specification default:
    /// 4 modules for Standard QR, 2 for Micro QR and rMQR. Ignored when the builder was
    /// given pre-built symbol data (the data already carries its quiet zone).
    /// </remarks>
    /// <param name="size">Quiet zone size in modules (0-10).</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public TSelf WithQuietZone(int size)
    {
        if (size is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(size), "Quiet zone size must be between 0 and 10");
        _quietZoneSize = size;
        return (TSelf)this;
    }

    /// <summary>
    /// Configure the colors used in the image.
    /// </summary>
    /// <param name="codeColor">Color of modules. If null, uses black.</param>
    /// <param name="backgroundColor">Background color. If null, uses white.</param>
    /// <param name="clearColor">Canvas clear color. If null, uses transparent.</param>
    /// <returns>This builder instance for method chaining.</returns>
    public TSelf WithColors(SKColor? codeColor = null, SKColor? backgroundColor = null, SKColor? clearColor = null)
    {
        _codeColor = codeColor;
        _backgroundColor = backgroundColor;
        _clearColor = clearColor;
        return (TSelf)this;
    }

    /// <summary>
    /// Configure the shape of the modules.
    /// </summary>
    /// <remarks>
    /// Note: Custom module shapes reduce scan margin; sizes below 0.8 may affect readability.
    /// On Standard QR they also affect finder patterns unless a custom finder pattern shape is
    /// explicitly set via its <c>WithFinderPatternShape</c> option.
    /// </remarks>
    /// <param name="moduleShape">Shape to use for modules. If null, uses rectangles.</param>
    /// <param name="sizePercent">Module size as a percentage of cell size (0.5-1.0). Default is 1.0 (no gaps).</param>
    /// <returns>This builder instance for method chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public TSelf WithModuleShape(ModuleShape? moduleShape, float sizePercent = 1.0f)
    {
        if (sizePercent is < 0.5f or > 1.0f)
            throw new ArgumentOutOfRangeException(nameof(sizePercent), "Module size percent must be between 0.5 and 1.0.");

        _moduleShape = moduleShape;
        _moduleSizePercent = sizePercent;
        return (TSelf)this;
    }

    /// <summary>
    /// Configure gradient options for the modules.
    /// </summary>
    /// <param name="gradientOptions">Gradient configuration. If null, uses solid color.</param>
    /// <returns>This builder instance for method chaining.</returns>
    public TSelf WithGradient(GradientOptions? gradientOptions)
    {
        _gradientOptions = gradientOptions;
        return (TSelf)this;
    }

    // ─── Output surface ───

    /// <summary>
    /// Generate the symbol image and save to stream.
    /// </summary>
    /// <param name="output">The output stream (must be writable).</param>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="ArgumentException"></exception>
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
    /// Generate the symbol image and write to an IBufferWriter.
    /// This is more efficient than SaveTo(Stream) as it avoids intermediate buffering.
    /// </summary>
    /// <param name="writer">The buffer writer</param>
    /// <exception cref="ArgumentNullException"></exception>
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
    /// Generate the symbol and save as SVG document to stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The symbol is drawn as vector shapes via <see cref="SKSvgCanvas"/>, so the output scales
    /// without quality loss. All builder options apply.
    /// </para>
    /// <para>
    /// The root element carries a <c>viewBox</c>, so the document scales its content when
    /// embedded at a different size (img element, CSS). For plain rectangular modules,
    /// <c>shape-rendering="crispEdges"</c> is added to avoid antialiasing seams between modules;
    /// custom shapes keep antialiasing for smooth curves.
    /// </para>
    /// <para>
    /// <see cref="WithFormat(SKEncodedImageFormat, int)"/> is ignored, SVG is a vector format,
    /// not an <see cref="SKEncodedImageFormat"/>. Size options (<see cref="WithSize(int, int)"/>,
    /// <see cref="WithModulePixelSize(int)"/>, or the symbology's default canvas) define the SVG viewport in units.
    /// The stream is left open after writing.
    /// </para>
    /// </remarks>
    /// <param name="output">The output stream (must be writable).</param>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="ArgumentException"></exception>
    public void SaveToSvg(Stream output)
    {
        if (output is null)
            throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite)
            throw new ArgumentException("Output stream must be writable", nameof(output));

        RenderSvg(output);
    }

    /// <summary>
    /// Generate the symbol and write as SVG document to an IBufferWriter.
    /// </summary>
    /// <remarks>
    /// See <see cref="SaveToSvg(Stream)"/> for rendering behavior. Data is written in
    /// writer-provided segments, so segmented writers (e.g. PipeWriter) work without
    /// a single contiguous buffer for the whole document.
    /// </remarks>
    /// <param name="writer">The buffer writer to write to.</param>
    /// <exception cref="ArgumentNullException"></exception>
    public void SaveToSvg(IBufferWriter<byte> writer)
    {
        if (writer is null)
            throw new ArgumentNullException(nameof(writer));

        using var stream = new BufferWriterStream(writer);
        RenderSvg(stream);
    }

    /// <summary>
    /// Generate the symbol and return as SVG document string.
    /// </summary>
    /// <remarks>
    /// See <see cref="SaveToSvg(Stream)"/> for rendering behavior.
    /// </remarks>
    /// <returns>SVG document.</returns>
    public string ToSvgString()
    {
        using var stream = new MemoryStream();
        SaveToSvg(stream);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <summary>
    /// Generate the symbol image and return as byte array.
    /// </summary>
    /// <returns>Encoded image as byte array.</returns>
    public byte[] ToByteArray()
    {
        using var image = GenerateImage();
        using var data = image.Encode(_format, _quality);
        return data.ToArray();
    }

    /// <summary>
    /// Generate the symbol image and return as SKImage.
    /// </summary>
    /// <returns>SKImage instance (caller must dispose).</returns>
    public SKImage ToImage()
    {
        return GenerateImage();
    }

    /// <summary>
    /// Generate the symbol image and return as SKBitmap.
    /// </summary>
    /// <returns>SKBitmap instance (caller must dispose).</returns>
    public SKBitmap ToBitmap()
    {
        using var image = GenerateImage();
        return SKBitmap.FromImage(image);
    }

    // ─── Shared pipeline ───

    /// <summary>
    /// Renders the SVG document to the output stream, injecting root element attributes
    /// (<c>viewBox</c>, optional <c>shape-rendering</c>) while streaming.
    /// </summary>
    /// <remarks>
    /// <see cref="SKSvgCanvas"/> writes <c>width</c>/<c>height</c> on the root element but no
    /// <c>viewBox</c>. Without a viewBox, an SVG embedded at a different size (img element, CSS)
    /// keeps its content at the original coordinates instead of scaling, the main reason to use
    /// SVG in the first place. <see cref="SvgRootAttributeInjectorStream"/> inserts the attributes
    /// right after the <c>&lt;svg </c> marker while the canvas streams to the output, so the
    /// document is never buffered as a whole; if the marker is not found (unexpected upstream
    /// format change), the document passes through unpatched.
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
    /// Antialiasing between adjacent vector shapes produces visible hairline seams when the SVG
    /// is scaled to non-integer sizes. For plain rectangular modules crispEdges removes the seams;
    /// custom shapes keep antialiasing for smooth curves. The symbology hook adds conditions the
    /// shared options cannot see (custom finder shapes, drawn icon overlays).
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
    /// Draws the configured symbol onto the canvas. Shared by the raster
    /// (<see cref="GenerateImage"/>) and SVG (<see cref="SaveToSvg(Stream)"/>) paths.
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
