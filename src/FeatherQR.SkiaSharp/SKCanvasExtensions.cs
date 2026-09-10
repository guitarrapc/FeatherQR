using SkiaSharp;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Draws a QR, Micro QR or rMQR code onto an <see cref="SKCanvas"/> you already have, instead of producing an image file.
/// </summary>
/// <remarks>
/// <para>
/// Each method clears the whole canvas before drawing, so anything already on it is lost.
/// To place a code inside a larger drawing, wrap the call in <see cref="SKCanvas.Save"/> and <see cref="SKCanvas.ClipRect(SKRect, SKClipOperation, bool)"/>, or call <see cref="SymbolRenderer"/> directly, which draws only the code.
/// </para>
/// <para>
/// The area is taken literally, so a rectangle that is not square gives the square symbologies modules that are not square, and readers stop finding the symbol well before it stops being drawn: a finder pattern is located by its 1:1:3:1:1 run along a line, and that ratio survives on one axis only. Measured, failures start around 1.25:1 and nothing survives past 1.8:1, so there is no safe ratio to aim at.
/// Pass a square area unless you are deliberately compensating for an output device whose pixels are not square; the image builders fit the symbol for you. rMQR fits its own rectangle into the area, since its aspect ratio comes from the version rather than the caller.
/// </para>
/// </remarks>
public static class SKCanvasExtensions
{
    /// <summary>
    /// Draws a QR code filling an area of this size, with the default colors.
    /// </summary>
    /// <remarks>The area is taken literally, so a non-square one gives the symbol non-square modules and readers stop finding it. Pass a square area, or use <see cref="QRCodeImageBuilder"/>, which fits the symbol for you.</remarks>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="data">The QR code to draw.</param>
    /// <param name="width">Width of the area to draw into.</param>
    /// <param name="height">Height of the area to draw into.</param>
    /// <param name="clearColor">Clears the whole canvas before drawing. Transparent when omitted, unlike the image builders, whose padding falls back to the background color.</param>
    /// <param name="codeColor">The dark modules. Black when omitted.</param>
    /// <param name="backgroundColor">Behind the QR code. White when omitted.</param>
    /// <param name="iconData">An icon to draw over the center. None when omitted.</param>
    /// <param name="moduleShape">The shape to draw modules as. Squares when omitted.</param>
    /// <param name="moduleSizePercent">How much of its cell a module fills, 0.0 to 1.0. The default 1.0 leaves no gaps.</param>
    /// <param name="gradientOptions">A gradient to paint the modules with. Solid color when omitted.</param>
    /// <param name="finderPatternShape">The shape to draw the finder patterns as. Plain squares when omitted.</param>
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
        var area = SKRect.Create(0, 0, width, height);
        canvas.Render(data, area, clearColor, codeColor, backgroundColor, iconData, moduleShape, moduleSizePercent, gradientOptions, finderPatternShape);
    }

    /// <summary>
    /// Draws a QR code into an area of the canvas.
    /// </summary>
    /// <remarks>The area is taken literally, so a non-square one gives the symbol non-square modules and readers stop finding it. Pass a square area, or use <see cref="QRCodeImageBuilder"/>, which fits the symbol for you.</remarks>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="data">The QR code to draw.</param>
    /// <param name="area">Where to draw it.</param>
    /// <param name="clearColor">Clears the whole canvas before drawing. Transparent when omitted, unlike the image builders, whose padding falls back to the background color.</param>
    /// <param name="codeColor">The dark modules. Black when omitted.</param>
    /// <param name="backgroundColor">Behind the QR code. White when omitted.</param>
    /// <param name="iconData">An icon to draw over the center. None when omitted.</param>
    /// <param name="moduleShape">The shape to draw modules as. Squares when omitted.</param>
    /// <param name="moduleSizePercent">How much of its cell a module fills, 0.0 to 1.0. The default 1.0 leaves no gaps.</param>
    /// <param name="gradientOptions">A gradient to paint the modules with. Solid color when omitted.</param>
    /// <param name="finderPatternShape">The shape to draw the finder patterns as. Plain squares when omitted.</param>
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
        canvas.Clear(clearColor ?? SKColors.Transparent);
        SymbolRenderer.Render(canvas, area, data, codeColor, backgroundColor, iconData, moduleShape, moduleSizePercent, gradientOptions, finderPatternShape);
    }

    /// <summary>
    /// Draws a Micro QR code filling an area of this size, with the default colors.
    /// </summary>
    /// <remarks>
    /// The area is taken literally, so a non-square one gives the symbol non-square modules and readers stop finding it. Pass a square area, or use <see cref="MicroQRCodeImageBuilder"/>, which fits the symbol for you.
    /// Micro QR does not offer the Standard QR icon overlay (no error-correction headroom for overlays); its one finder pattern takes a shape like any other symbology.
    /// </remarks>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="data">The Micro QR code to draw.</param>
    /// <param name="width">Width of the area to draw into.</param>
    /// <param name="height">Height of the area to draw into.</param>
    /// <param name="clearColor">Clears the whole canvas before drawing. Transparent when omitted, unlike the image builders, whose padding falls back to the background color.</param>
    /// <param name="codeColor">The dark modules. Black when omitted.</param>
    /// <param name="backgroundColor">Behind the Micro QR code. White when omitted.</param>
    /// <param name="moduleShape">The shape to draw modules as. Squares when omitted.</param>
    /// <param name="moduleSizePercent">How much of its cell a module fills, 0.0 to 1.0. The default 1.0 leaves no gaps.</param>
    /// <param name="gradientOptions">A gradient to paint the modules with. Solid color when omitted.</param>
    /// <param name="finderPatternShape">The shape to draw the finder pattern as. A plain square when omitted.</param>
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
        var area = SKRect.Create(0, 0, width, height);
        canvas.Render(data, area, clearColor, codeColor, backgroundColor, moduleShape, moduleSizePercent, gradientOptions, finderPatternShape);
    }

    /// <summary>
    /// Draws a Micro QR code into an area of the canvas.
    /// </summary>
    /// <remarks>
    /// The area is taken literally, so a non-square one gives the symbol non-square modules and readers stop finding it. Pass a square area, or use <see cref="MicroQRCodeImageBuilder"/>, which fits the symbol for you.
    /// Micro QR does not offer the Standard QR icon overlay (no error-correction headroom for overlays); its one finder pattern takes a shape like any other symbology.
    /// </remarks>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="data">The Micro QR code to draw.</param>
    /// <param name="area">Where to draw it.</param>
    /// <param name="clearColor">Clears the whole canvas before drawing. Transparent when omitted, unlike the image builders, whose padding falls back to the background color.</param>
    /// <param name="codeColor">The dark modules. Black when omitted.</param>
    /// <param name="backgroundColor">Behind the Micro QR code. White when omitted.</param>
    /// <param name="moduleShape">The shape to draw modules as. Squares when omitted.</param>
    /// <param name="moduleSizePercent">How much of its cell a module fills, 0.0 to 1.0. The default 1.0 leaves no gaps.</param>
    /// <param name="gradientOptions">A gradient to paint the modules with. Solid color when omitted.</param>
    /// <param name="finderPatternShape">The shape to draw the finder pattern as. A plain square when omitted.</param>
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
    /// <param name="clearColor">Clears the whole canvas before drawing. Transparent when omitted, unlike the image builders, whose padding falls back to the background color.</param>
    /// <param name="codeColor">The dark modules. Black when omitted.</param>
    /// <param name="backgroundColor">Behind the rMQR code. White when omitted.</param>
    /// <param name="moduleShape">The shape to draw modules as. Squares when omitted.</param>
    /// <param name="moduleSizePercent">How much of its cell a module fills, 0.0 to 1.0. The default 1.0 leaves no gaps.</param>
    /// <param name="gradientOptions">A gradient to paint the modules with. Solid color when omitted.</param>
    /// <param name="finderPatternShape">The shape to draw the finder pattern as. A plain square when omitted.</param>
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
    /// <param name="clearColor">Clears the whole canvas before drawing. Transparent when omitted, unlike the image builders, whose padding falls back to the background color.</param>
    /// <param name="codeColor">The dark modules. Black when omitted.</param>
    /// <param name="backgroundColor">Behind the rMQR code. White when omitted.</param>
    /// <param name="moduleShape">The shape to draw modules as. Squares when omitted.</param>
    /// <param name="moduleSizePercent">How much of its cell a module fills, 0.0 to 1.0. The default 1.0 leaves no gaps.</param>
    /// <param name="gradientOptions">A gradient to paint the modules with. Solid color when omitted.</param>
    /// <param name="finderPatternShape">The shape to draw the finder pattern as. A plain square when omitted.</param>
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
        canvas.Clear(clearColor ?? SKColors.Transparent);
        SymbolRenderer.Render(canvas, area, data, codeColor, backgroundColor, moduleShape, moduleSizePercent, gradientOptions, finderPatternShape);
    }
}
