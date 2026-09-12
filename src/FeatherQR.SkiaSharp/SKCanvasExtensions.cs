using SkiaSharp;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Draws a QR, Micro QR or rMQR code onto an <see cref="SKCanvas"/> you already have, instead of producing an image file.
/// </summary>
/// <remarks>
/// <para>
/// Each method clears the whole canvas before drawing, so anything already on it is lost. Every argument is checked before that clear, so a refused call leaves the canvas as it was.
/// To place a code inside a larger drawing, wrap the call in <see cref="SKCanvas.Save"/> and <see cref="SKCanvas.ClipRect(SKRect, SKClipOperation, bool)"/>, or call <see cref="SymbolRenderer"/> directly, which draws only the code.
/// </para>
/// <para>
/// Every symbology is fitted into the area at one uniform module scale and centered, using the same fit as the image builders (given an explicit canvas size, they also round the offset to whole pixels): a finder pattern is located by its 1:1:3:1:1 run along a line, and that ratio survives on one axis only once the modules stop being square. The whole area gets the background color.
/// To pre-distort a symbol for an output device whose dots are not square, scale the canvas and draw into a square area.
/// An inverted area is refused rather than read as a mirror; for a mirrored symbol, scale the canvas by -1 about the area.
/// </para>
/// </remarks>
public static class SKCanvasExtensions
{
    /// <summary>
    /// Draws a QR code into an area of this size, with the default colors.
    /// </summary>
    /// <remarks>The QR code is drawn at a uniform module scale and centered, never stretched, and the whole area gets the background color.</remarks>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="data">The QR code to draw.</param>
    /// <param name="width">Width of the area to draw into.</param>
    /// <param name="height">Height of the area to draw into.</param>
    /// <param name="clearColor">Clears the whole canvas before drawing, transparent when omitted. The background is then painted over the whole area, bands beside the symbol included, so the clear color shows outside the area and, inside it, only through a background that is not opaque; an image builder instead pads those bands with its clear color when one is set.</param>
    /// <param name="codeColor">The dark modules. Black when omitted.</param>
    /// <param name="backgroundColor">Behind the QR code. White when omitted.</param>
    /// <param name="iconData">An icon to draw over the center. None when omitted.</param>
    /// <param name="moduleShape">The shape to draw modules as. Squares when omitted.</param>
    /// <param name="moduleSizePercent">How much of its cell a module fills, 0.0 to 1.0. The default 1.0 leaves no gaps.</param>
    /// <param name="gradientOptions">A gradient to paint the modules with. Solid color when omitted.</param>
    /// <param name="finderPatternShape">The shape to draw the finder patterns as. Plain squares when omitted. A shape draws the dark modules only and leaves the light rings undrawn, so the background shows through them at any alpha, in raster and SVG output alike.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="canvas"/> or <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="width"/> or <paramref name="height"/> is negative, <paramref name="moduleSizePercent"/> is outside 0.0 to 1.0, an <paramref name="iconData"/> size, border or occupancy limit is out of range, or <paramref name="gradientOptions"/> has a direction that is not defined. An area no symbol fits in, a zero width or height or an aspect ratio so extreme the centered square rounds away, is accepted: the canvas is still cleared, the area keeps its background, and no code is drawn.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the icon does not fit the QR code. Checked whatever the area's size, like the icon's other settings.</exception>
    public static void Render(
        this SKCanvas canvas,
        QRCodeData data,
        int width,
        int height,
        SKColor? clearColor = null,
        SKColor? codeColor = null,
        SKColor? backgroundColor = null,
        IconData? iconData = null,
        ModuleShape? moduleShape = null,
        float moduleSizePercent = 1.0f,
        GradientOptions? gradientOptions = null,
        FinderPatternShape? finderPatternShape = null)
    {
        // The canvas and the symbol are reported before the size, as they are on the area overloads.
        if (canvas is null)
            throw new ArgumentNullException(nameof(canvas));
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (width < 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be zero or more.");
        if (height < 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be zero or more.");

        var area = SKRect.Create(0, 0, width, height);
        canvas.Render(data, area, clearColor, codeColor, backgroundColor, iconData, moduleShape, moduleSizePercent, gradientOptions, finderPatternShape);
    }

    /// <summary>
    /// Draws a QR code into an area of the canvas.
    /// </summary>
    /// <remarks>The QR code is drawn at a uniform module scale and centered, never stretched, and the whole area gets the background color.</remarks>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="data">The QR code to draw.</param>
    /// <param name="area">Where to draw it.</param>
    /// <param name="clearColor">Clears the whole canvas before drawing, transparent when omitted. The background is then painted over the whole area, bands beside the symbol included, so the clear color shows outside the area and, inside it, only through a background that is not opaque; an image builder instead pads those bands with its clear color when one is set.</param>
    /// <param name="codeColor">The dark modules. Black when omitted.</param>
    /// <param name="backgroundColor">Behind the QR code. White when omitted.</param>
    /// <param name="iconData">An icon to draw over the center. None when omitted.</param>
    /// <param name="moduleShape">The shape to draw modules as. Squares when omitted.</param>
    /// <param name="moduleSizePercent">How much of its cell a module fills, 0.0 to 1.0. The default 1.0 leaves no gaps.</param>
    /// <param name="gradientOptions">A gradient to paint the modules with. Solid color when omitted.</param>
    /// <param name="finderPatternShape">The shape to draw the finder patterns as. Plain squares when omitted. A shape draws the dark modules only and leaves the light rings undrawn, so the background shows through them at any alpha, in raster and SVG output alike.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="canvas"/> or <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="area"/> is inverted (a negative width or height) or has a coordinate or size that is not finite. An area no symbol fits in, a zero width or height or an aspect ratio so extreme the centered square rounds away, is accepted: the canvas is still cleared, the area keeps its background, and no code is drawn.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="moduleSizePercent"/> is outside 0.0 to 1.0, an <paramref name="iconData"/> size, border or occupancy limit is out of range, or <paramref name="gradientOptions"/> has a direction that is not defined.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the icon does not fit the QR code. Checked whatever the area's size, like the icon's other settings.</exception>
    public static void Render(
        this SKCanvas canvas,
        QRCodeData data,
        SKRect area,
        SKColor? clearColor = null,
        SKColor? codeColor = null,
        SKColor? backgroundColor = null,
        IconData? iconData = null,
        ModuleShape? moduleShape = null,
        float moduleSizePercent = 1.0f,
        GradientOptions? gradientOptions = null,
        FinderPatternShape? finderPatternShape = null)
    {
        // Every argument is refused before the clear, so a refused call leaves the canvas as it was.
        SymbolRenderer.ValidateRenderArguments(canvas, data, area, moduleSizePercent, gradientOptions, nameof(data));
        if (iconData is not null)
            SymbolRenderer.ValidateIcon(data, iconData);
        canvas.Clear(clearColor ?? SKColors.Transparent);
        SymbolRenderer.Render(canvas, area, data, codeColor, backgroundColor, iconData, moduleShape, moduleSizePercent, gradientOptions, finderPatternShape);
    }

    /// <summary>
    /// Draws a Micro QR code into an area of this size, with the default colors.
    /// </summary>
    /// <remarks>
    /// The Micro QR code is drawn at a uniform module scale and centered, never stretched, and the whole area gets the background color.
    /// Micro QR does not offer the Standard QR icon overlay (no error-correction headroom for overlays); its one finder pattern takes a shape like any other symbology.
    /// </remarks>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="data">The Micro QR code to draw.</param>
    /// <param name="width">Width of the area to draw into.</param>
    /// <param name="height">Height of the area to draw into.</param>
    /// <param name="clearColor">Clears the whole canvas before drawing, transparent when omitted. The background is then painted over the whole area, bands beside the symbol included, so the clear color shows outside the area and, inside it, only through a background that is not opaque; an image builder instead pads those bands with its clear color when one is set.</param>
    /// <param name="codeColor">The dark modules. Black when omitted.</param>
    /// <param name="backgroundColor">Behind the Micro QR code. White when omitted.</param>
    /// <param name="moduleShape">The shape to draw modules as. Squares when omitted.</param>
    /// <param name="moduleSizePercent">How much of its cell a module fills, 0.0 to 1.0. The default 1.0 leaves no gaps.</param>
    /// <param name="gradientOptions">A gradient to paint the modules with. Solid color when omitted.</param>
    /// <param name="finderPatternShape">The shape to draw the finder pattern as. A plain square when omitted. A shape draws the dark modules only and leaves the light ring undrawn, so the background shows through it at any alpha, in raster and SVG output alike.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="canvas"/> or <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="width"/> or <paramref name="height"/> is negative, or <paramref name="moduleSizePercent"/> is outside 0.0 to 1.0, or <paramref name="gradientOptions"/> has a direction that is not defined. An area no symbol fits in, a zero width or height or an aspect ratio so extreme the centered square rounds away, is accepted: the canvas is still cleared, the area keeps its background, and no code is drawn.</exception>
    public static void Render(
        this SKCanvas canvas,
        MicroQRCodeData data,
        int width,
        int height,
        SKColor? clearColor = null,
        SKColor? codeColor = null,
        SKColor? backgroundColor = null,
        ModuleShape? moduleShape = null,
        float moduleSizePercent = 1.0f,
        GradientOptions? gradientOptions = null,
        FinderPatternShape? finderPatternShape = null)
    {
        // The canvas and the symbol are reported before the size, as they are on the area overloads.
        if (canvas is null)
            throw new ArgumentNullException(nameof(canvas));
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (width < 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be zero or more.");
        if (height < 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be zero or more.");

        var area = SKRect.Create(0, 0, width, height);
        canvas.Render(data, area, clearColor, codeColor, backgroundColor, moduleShape, moduleSizePercent, gradientOptions, finderPatternShape);
    }

    /// <summary>
    /// Draws a Micro QR code into an area of the canvas.
    /// </summary>
    /// <remarks>
    /// The Micro QR code is drawn at a uniform module scale and centered, never stretched, and the whole area gets the background color.
    /// Micro QR does not offer the Standard QR icon overlay (no error-correction headroom for overlays); its one finder pattern takes a shape like any other symbology.
    /// </remarks>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="data">The Micro QR code to draw.</param>
    /// <param name="area">Where to draw it.</param>
    /// <param name="clearColor">Clears the whole canvas before drawing, transparent when omitted. The background is then painted over the whole area, bands beside the symbol included, so the clear color shows outside the area and, inside it, only through a background that is not opaque; an image builder instead pads those bands with its clear color when one is set.</param>
    /// <param name="codeColor">The dark modules. Black when omitted.</param>
    /// <param name="backgroundColor">Behind the Micro QR code. White when omitted.</param>
    /// <param name="moduleShape">The shape to draw modules as. Squares when omitted.</param>
    /// <param name="moduleSizePercent">How much of its cell a module fills, 0.0 to 1.0. The default 1.0 leaves no gaps.</param>
    /// <param name="gradientOptions">A gradient to paint the modules with. Solid color when omitted.</param>
    /// <param name="finderPatternShape">The shape to draw the finder pattern as. A plain square when omitted. A shape draws the dark modules only and leaves the light ring undrawn, so the background shows through it at any alpha, in raster and SVG output alike.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="canvas"/> or <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="area"/> is inverted (a negative width or height) or has a coordinate or size that is not finite. An area no symbol fits in, a zero width or height or an aspect ratio so extreme the centered square rounds away, is accepted: the canvas is still cleared, the area keeps its background, and no code is drawn.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="moduleSizePercent"/> is outside 0.0 to 1.0, or <paramref name="gradientOptions"/> has a direction that is not defined.</exception>
    public static void Render(
        this SKCanvas canvas,
        MicroQRCodeData data,
        SKRect area,
        SKColor? clearColor = null,
        SKColor? codeColor = null,
        SKColor? backgroundColor = null,
        ModuleShape? moduleShape = null,
        float moduleSizePercent = 1.0f,
        GradientOptions? gradientOptions = null,
        FinderPatternShape? finderPatternShape = null)
    {
        // Every argument is refused before the clear, so a refused call leaves the canvas as it was.
        SymbolRenderer.ValidateRenderArguments(canvas, data, area, moduleSizePercent, gradientOptions, nameof(data));
        canvas.Clear(clearColor ?? SKColors.Transparent);
        SymbolRenderer.Render(canvas, area, data, codeColor, backgroundColor, moduleShape, moduleSizePercent, gradientOptions, finderPatternShape);
    }

    /// <summary>
    /// Draws a rMQR code into an area of this size, with the default colors.
    /// </summary>
    /// <remarks>
    /// The rMQR code is drawn at a uniform module scale and centered, never stretched, and the whole area gets the background color. rMQR has no error correction headroom to spare, so there is no icon option; its one finder pattern takes a shape like any other symbology.
    /// </remarks>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="data">The rMQR code to draw.</param>
    /// <param name="width">Width of the area to draw into.</param>
    /// <param name="height">Height of the area to draw into.</param>
    /// <param name="clearColor">Clears the whole canvas before drawing, transparent when omitted. The background is then painted over the whole area, bands beside the symbol included, so the clear color shows outside the area and, inside it, only through a background that is not opaque; an image builder instead pads those bands with its clear color when one is set.</param>
    /// <param name="codeColor">The dark modules. Black when omitted.</param>
    /// <param name="backgroundColor">Behind the rMQR code. White when omitted.</param>
    /// <param name="moduleShape">The shape to draw modules as. Squares when omitted.</param>
    /// <param name="moduleSizePercent">How much of its cell a module fills, 0.0 to 1.0. The default 1.0 leaves no gaps.</param>
    /// <param name="gradientOptions">A gradient to paint the modules with. Solid color when omitted.</param>
    /// <param name="finderPatternShape">The shape to draw the finder pattern as. A plain square when omitted. A shape draws the dark modules only and leaves the light ring undrawn, so the background shows through it at any alpha, in raster and SVG output alike.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="canvas"/> or <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="width"/> or <paramref name="height"/> is negative, or <paramref name="moduleSizePercent"/> is outside 0.0 to 1.0, or <paramref name="gradientOptions"/> has a direction that is not defined. An area no symbol fits in, a zero width or height or an aspect ratio so extreme the centered rectangle rounds away, is accepted: the canvas is still cleared, the area keeps its background, and no code is drawn.</exception>
    public static void Render(
        this SKCanvas canvas,
        RmQRCodeData data,
        int width,
        int height,
        SKColor? clearColor = null,
        SKColor? codeColor = null,
        SKColor? backgroundColor = null,
        ModuleShape? moduleShape = null,
        float moduleSizePercent = 1.0f,
        GradientOptions? gradientOptions = null,
        FinderPatternShape? finderPatternShape = null)
    {
        // The canvas and the symbol are reported before the size, as they are on the area overloads.
        if (canvas is null)
            throw new ArgumentNullException(nameof(canvas));
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (width < 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be zero or more.");
        if (height < 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be zero or more.");

        var area = SKRect.Create(0, 0, width, height);
        canvas.Render(data, area, clearColor, codeColor, backgroundColor, moduleShape, moduleSizePercent, gradientOptions, finderPatternShape);
    }

    /// <summary>
    /// Draws a rMQR code into an area of the canvas.
    /// </summary>
    /// <remarks>
    /// The rMQR code is drawn at a uniform module scale and centered, never stretched, and the whole area gets the background color. rMQR has no error correction headroom to spare, so there is no icon option; its one finder pattern takes a shape like any other symbology.
    /// </remarks>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="data">The rMQR code to draw.</param>
    /// <param name="area">Where to draw it.</param>
    /// <param name="clearColor">Clears the whole canvas before drawing, transparent when omitted. The background is then painted over the whole area, bands beside the symbol included, so the clear color shows outside the area and, inside it, only through a background that is not opaque; an image builder instead pads those bands with its clear color when one is set.</param>
    /// <param name="codeColor">The dark modules. Black when omitted.</param>
    /// <param name="backgroundColor">Behind the rMQR code. White when omitted.</param>
    /// <param name="moduleShape">The shape to draw modules as. Squares when omitted.</param>
    /// <param name="moduleSizePercent">How much of its cell a module fills, 0.0 to 1.0. The default 1.0 leaves no gaps.</param>
    /// <param name="gradientOptions">A gradient to paint the modules with. Solid color when omitted.</param>
    /// <param name="finderPatternShape">The shape to draw the finder pattern as. A plain square when omitted. A shape draws the dark modules only and leaves the light ring undrawn, so the background shows through it at any alpha, in raster and SVG output alike.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="canvas"/> or <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="area"/> is inverted (a negative width or height) or has a coordinate or size that is not finite. An area no symbol fits in, a zero width or height or an aspect ratio so extreme the centered rectangle rounds away, is accepted: the canvas is still cleared, the area keeps its background, and no code is drawn.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="moduleSizePercent"/> is outside 0.0 to 1.0, or <paramref name="gradientOptions"/> has a direction that is not defined.</exception>
    public static void Render(
        this SKCanvas canvas,
        RmQRCodeData data,
        SKRect area,
        SKColor? clearColor = null,
        SKColor? codeColor = null,
        SKColor? backgroundColor = null,
        ModuleShape? moduleShape = null,
        float moduleSizePercent = 1.0f,
        GradientOptions? gradientOptions = null,
        FinderPatternShape? finderPatternShape = null)
    {
        // Every argument is refused before the clear, so a refused call leaves the canvas as it was.
        SymbolRenderer.ValidateRenderArguments(canvas, data, area, moduleSizePercent, gradientOptions, nameof(data));
        canvas.Clear(clearColor ?? SKColors.Transparent);
        SymbolRenderer.Render(canvas, area, data, codeColor, backgroundColor, moduleShape, moduleSizePercent, gradientOptions, finderPatternShape);
    }
}
