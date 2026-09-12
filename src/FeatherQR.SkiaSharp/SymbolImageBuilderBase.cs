using SkiaSharp;
using FeatherQR.SkiaSharp.Internals;
using System.Buffers;
using System.Text;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Shared implementation for the symbology-specific QR image builders (<see cref="QRCodeImageBuilder"/>, <see cref="MicroQRCodeImageBuilder"/>, <see cref="RmQRCodeImageBuilder"/>): the fluent options every symbology supports, canvas layout, and the complete raster/SVG output surface.
/// Symbology-specific concerns, such as error correction and version types and icon overlays, live on the derived builders.
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
    private protected SKColor _codeColor = SKColors.Black;
    private protected SKColor _backgroundColor = SKColors.White;
    // Nullable because "no clear color" is a state of its own, not a color: the padding follows the background.
    private protected SKColor? _clearColor;
    private protected ModuleShape _moduleShape = RectangleModuleShape.Default;
    private protected float _moduleSizePercent = 1.0f;
    private protected FinderPatternShape? _finderPatternShape;
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
    /// On its own, the symbol is fitted into the canvas with one uniform module scale and centered, whatever the canvas aspect ratio: modules stay square, because a symbol whose modules are not square stops being findable well before it stops being drawn. The module size follows from the canvas, so it can be fractional and shifts when the version changes.
    /// Combined with <see cref="WithModulePixelSize(int)"/> this is the canvas alone and the modules decide the content size.
    /// Either way the content is centered and the leftover canvas is padded, in the clear color when one is set and the background color otherwise.
    /// A canvas smaller than the content on either side is an error, but only when the module size is pinned; on its own this method has no size it cannot fit.
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
    /// Combined with <see cref="WithSize(int, int)"/> the content is centered on that canvas and the rest padded, in the clear color when one is set and the background color otherwise.
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
    /// Sets the two colors a symbol always has, the modules and what is behind them.
    /// </summary>
    /// <remarks>
    /// Both are required, so this call sets exactly the two colors it names and leaves the clear color alone.
    /// To change one color on its own use <see cref="WithCodeColor(SKColor)"/> or <see cref="WithBackgroundColor(SKColor)"/>.
    /// </remarks>
    /// <param name="codeColor">The dark modules.</param>
    /// <param name="backgroundColor">Behind the symbol, quiet zone included.</param>
    public TSelf WithColors(SKColor codeColor, SKColor backgroundColor)
    {
        _codeColor = codeColor;
        _backgroundColor = backgroundColor;
        return (TSelf)this;
    }

    /// <summary>
    /// Sets the color of the dark modules, black by default.
    /// </summary>
    /// <param name="codeColor">The color to draw the dark modules in.</param>
    public TSelf WithCodeColor(SKColor codeColor)
    {
        _codeColor = codeColor;
        return (TSelf)this;
    }

    /// <summary>
    /// Sets the color behind the symbol, quiet zone included. White by default.
    /// </summary>
    /// <param name="backgroundColor">The color to fill behind the symbol.</param>
    public TSelf WithBackgroundColor(SKColor backgroundColor)
    {
        _backgroundColor = backgroundColor;
        return (TSelf)this;
    }

    /// <summary>
    /// Sets the padding around the symbol when the canvas is larger than the content, which is also the color the canvas is cleared with before the symbol is drawn.
    /// </summary>
    /// <remarks>
    /// Without a clear color the padding follows the background, so the image does not come out part symbol and part transparent.
    /// Pass <see cref="SKColors.Transparent"/> for transparent surroundings.
    /// </remarks>
    /// <param name="clearColor">The color to clear the canvas with, or <see langword="null"/> for no clear color of its own, which leaves the padding following the background.</param>
    public TSelf WithClearColor(SKColor? clearColor)
    {
        _clearColor = clearColor;
        return (TSelf)this;
    }

    /// <summary>
    /// Draws the modules as a shape other than plain squares.
    /// </summary>
    /// <remarks>
    /// Styles the data modules only. The finder patterns keep their solid shape whatever is asked for here, because a decoder locates the symbol by scanning for their 1:1:3:1:1 run of dark and light, and gaps between modules erase it: a symbol whose finders are drawn as separated shapes is not read by anything, this library or a phone. Use <c>WithFinderPatternShape</c> to style them in a way that keeps them detectable.
    /// Every custom shape costs scan margin, and below 0.8 the symbol may stop scanning reliably, so test what you ship.
    /// </remarks>
    /// <param name="moduleShape">The shape to draw. <see cref="RectangleModuleShape.Default"/> for the plain squares a builder starts with.</param>
    /// <param name="sizePercent">How much of its cell a data module fills, 0.5 to 1.0. The default 1.0 leaves no gaps.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="moduleShape"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the size is outside 0.5 to 1.0.</exception>
    public TSelf WithModuleShape(ModuleShape moduleShape, float sizePercent = 1.0f)
    {
        if (moduleShape is null)
            throw new ArgumentNullException(nameof(moduleShape));
        if (sizePercent is < 0.5f or > 1.0f)
            throw new ArgumentOutOfRangeException(nameof(sizePercent), "Module size percent must be between 0.5 and 1.0.");

        _moduleShape = moduleShape;
        _moduleSizePercent = sizePercent;
        return (TSelf)this;
    }

    /// <summary>
    /// Draws the finder patterns as a shape of their own.
    /// </summary>
    /// <remarks>
    /// The built-in shapes reshape the concentric rings without breaking them, so they stay detectable; what a decoder needs is that the rings are continuous, not that they are square. Not calling this leaves the plain square the standards define, which is always the safest to scan, and it is also what a styled symbol gets: a shaped module never reaches a finder pattern.
    /// </remarks>
    /// <param name="finderPatternShape">The shape to draw. <see cref="RectangleFinderPatternShape.Default"/> for the plain squares a builder starts with; it renders identically, only through the finder pass rather than with the modules around it.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="finderPatternShape"/> is <see langword="null"/>.</exception>
    public TSelf WithFinderPatternShape(FinderPatternShape finderPatternShape)
    {
        if (finderPatternShape is null)
            throw new ArgumentNullException(nameof(finderPatternShape));

        _finderPatternShape = finderPatternShape;
        return (TSelf)this;
    }

    /// <summary>
    /// Paints the modules with a gradient instead of one solid color.
    /// </summary>
    /// <param name="gradientOptions">The gradient to paint, or <see langword="null"/> for no gradient, which paints the modules in the code color.</param>
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
    /// <exception cref="InvalidOperationException">Thrown when the render cannot be laid out: <see cref="WithModulePixelSize(int)"/> pins a module size the canvas given to <see cref="WithSize(int, int)"/> cannot hold, or overflows the image dimensions, or an icon is asked to occupy more modules than the symbol can spare.</exception>
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
    /// <exception cref="InvalidOperationException">Thrown when the render cannot be laid out: <see cref="WithModulePixelSize(int)"/> pins a module size the canvas given to <see cref="WithSize(int, int)"/> cannot hold, or overflows the image dimensions, or an icon is asked to occupy more modules than the symbol can spare.</exception>
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
    /// <exception cref="InvalidOperationException">Thrown when the render cannot be laid out: <see cref="WithModulePixelSize(int)"/> pins a module size the canvas given to <see cref="WithSize(int, int)"/> cannot hold, or overflows the image dimensions, or an icon is asked to occupy more modules than the symbol can spare.</exception>
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
    /// <exception cref="InvalidOperationException">Thrown when the render cannot be laid out: <see cref="WithModulePixelSize(int)"/> pins a module size the canvas given to <see cref="WithSize(int, int)"/> cannot hold, or overflows the image dimensions, or an icon is asked to occupy more modules than the symbol can spare.</exception>
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
    /// <exception cref="InvalidOperationException">Thrown when the render cannot be laid out: <see cref="WithModulePixelSize(int)"/> pins a module size the canvas given to <see cref="WithSize(int, int)"/> cannot hold, or overflows the image dimensions, or an icon is asked to occupy more modules than the symbol can spare.</exception>
    public string ToSvgString()
    {
        using var stream = new MemoryStream();
        SaveToSvg(stream);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <summary>
    /// Renders the symbol and returns the encoded image bytes.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the render cannot be laid out: <see cref="WithModulePixelSize(int)"/> pins a module size the canvas given to <see cref="WithSize(int, int)"/> cannot hold, or overflows the image dimensions, or an icon is asked to occupy more modules than the symbol can spare.</exception>
    public byte[] ToByteArray()
    {
        using var image = GenerateImage();
        using var data = image.Encode(_format, _quality);
        return data.ToArray();
    }

    /// <summary>
    /// Renders the symbol as an <see cref="SKImage"/>, which the caller disposes.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the render cannot be laid out: <see cref="WithModulePixelSize(int)"/> pins a module size the canvas given to <see cref="WithSize(int, int)"/> cannot hold, or overflows the image dimensions, or an icon is asked to occupy more modules than the symbol can spare.</exception>
    public SKImage ToImage()
    {
        return GenerateImage();
    }

    /// <summary>
    /// Renders the symbol as an <see cref="SKBitmap"/>, which the caller disposes.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the render cannot be laid out: <see cref="WithModulePixelSize(int)"/> pins a module size the canvas given to <see cref="WithSize(int, int)"/> cannot hold, or overflows the image dimensions, or an icon is asked to occupy more modules than the symbol can spare.</exception>
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
        var (info, contentRect) = QRImageLayout.CreateLayout(matrixWidth, matrixHeight, _explicitSize, _modulePixelSize, GetDefaultCanvasSize(matrixWidth, matrixHeight));

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
        return _moduleShape is RectangleModuleShape
            && _moduleSizePercent == 1.0f
            && UseCrispEdgesCore();
    }

    /// <summary>
    /// Generate the SKImage from the resolved symbol.
    /// </summary>
    private SKImage GenerateImage()
    {
        var symbol = ResolveSymbol(out var matrixWidth, out var matrixHeight);

        var (info, contentRect) = QRImageLayout.CreateLayout(matrixWidth, matrixHeight, _explicitSize, _modulePixelSize, GetDefaultCanvasSize(matrixWidth, matrixHeight));

        var contentCoversCanvas = QRImageLayout.ContentCoversCanvas(contentRect, info);
        var backgroundIsOpaque = _backgroundColor.Alpha == byte.MaxValue;
        var padIsOpaque = PadColor().Alpha == byte.MaxValue;

        // When the base layer (background fill, or the padded canvas) is opaque everywhere, anything drawn over it stays opaque, so the whole image is opaque no matter what modules/icons/gradients are painted on top.
        // An opaque surface lets encoders skip the alpha channel and the unpremul pass, so PNG output becomes RGB and encodes measurably faster.
        var isOpaque = contentCoversCanvas
            ? backgroundIsOpaque || padIsOpaque
            : padIsOpaque;
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
        var contentCoversCanvas = QRImageLayout.ContentCoversCanvas(contentRect, info);
        var backgroundIsOpaque = _backgroundColor.Alpha == byte.MaxValue;

        if (_clearColor is SKColor clearColor)
        {
            // A clear color is a canvas color, so it goes under the symbol too and a translucent background blends over it.
            // Skipped when it cannot remain visible: a fresh canvas is already transparent, and an opaque background covering it overwrites the clear.
            if (clearColor.Alpha != 0 && !(contentCoversCanvas && backgroundIsOpaque))
            {
                canvas.Clear(clearColor);
            }
        }
        else if (!contentCoversCanvas)
        {
            // The pad inherits the background. Painting it under the symbol too would coat a translucent background twice.
            FillAround(canvas, contentRect, info, _backgroundColor);
        }

        RenderSymbol(canvas, symbol, contentRect);
    }

    /// <summary>
    /// The pad the canvas carries where the symbol does not reach.</summary>
    private SKColor PadColor() => _clearColor ?? _backgroundColor;

    /// <summary>
    /// Fills the canvas outside <paramref name="contentRect"/>, in up to four bands. They meet the content on the coordinates the renderer fills from, with antialiasing off, so the shared edges tile without a seam.
    /// </summary>
    private static void FillAround(SKCanvas canvas, SKRect contentRect, SKImageInfo info, SKColor color)
    {
        if (color.Alpha == 0)
            return;

        using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = false };
        if (contentRect.Top > 0)
            canvas.DrawRect(SKRect.Create(0, 0, info.Width, contentRect.Top), paint);
        if (contentRect.Bottom < info.Height)
            canvas.DrawRect(SKRect.Create(0, contentRect.Bottom, info.Width, info.Height - contentRect.Bottom), paint);
        if (contentRect.Left > 0)
            canvas.DrawRect(SKRect.Create(0, contentRect.Top, contentRect.Left, contentRect.Height), paint);
        if (contentRect.Right < info.Width)
            canvas.DrawRect(SKRect.Create(contentRect.Right, contentRect.Top, info.Width - contentRect.Right, contentRect.Height), paint);
    }
}
