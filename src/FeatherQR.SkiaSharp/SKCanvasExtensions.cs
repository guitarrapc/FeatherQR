using SkiaSharp;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Extension methods that render a QR, Micro QR or rMQR symbol onto an
/// <see cref="SKCanvas"/> you already have, instead of producing an image file.
/// </summary>
/// <remarks>
/// Each method clears the whole canvas before drawing, so anything already on it is
/// lost. To place a symbol inside a larger drawing, wrap the call in
/// <see cref="SKCanvas.Save"/> and <see cref="SKCanvas.ClipRect(SKRect, SKClipOperation, bool)"/>,
/// or call <see cref="QRCodeRenderer"/> directly, which draws only the symbol.
/// </remarks>
public static class QRCodeExtensions
{
    /// <summary>
    /// Renders a QR code on the canvas with default colors.
    /// </summary>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="data">The QR code data.</param>
    /// <param name="width">The width of the rendering area.</param>
    /// <param name="height">The height of the rendering area.</param>
    /// <param name="clearColor">The color used to clear the canvas. Defaults to Transparent.</param>
    /// <param name="codeColor">The color of the QR code modules. Defaults to Black.</param>
    /// <param name="backgroundColor">The background color of the QR Code. Defaults to White.</param>
    /// <param name="iconData">Optional icon data to overlay on the center of the QR code.</param>
    /// <param name="moduleShape">The shape to use for drawing modules. If null, rectangles are used.</param>
    /// <param name="moduleSizePercent">The size of each module as a percentage of the cell size (0.0 to 1.0). Default is 1.0 (no gap).</param>
    /// <param name="gradientOptions">Optional gradient options for the QR code modules.</param>
    /// <param name="finderPatternShape">The shape to use for drawing finder patterns. If null, uses same shape as modules.</param>
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
    /// Renders a QR code on the canvas with custom colors.
    /// </summary>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="data">The QR code data.</param>
    /// <param name="area">The rectangular area where the QR code will be rendered.</param>
    /// <param name="clearColor">The color used to clear the canvas. Defaults to Transparent.</param>
    /// <param name="codeColor">The color of the QR code modules. Defaults to Black.</param>
    /// <param name="backgroundColor">The background color of the QR Code. Defaults to White.</param>
    /// <param name="iconData">Optional icon data to overlay on the center of the QR code.</param>
    /// <param name="moduleShape">The shape to use for drawing modules. If null, rectangles are used.</param>
    /// <param name="moduleSizePercent">The size of each module as a percentage of the cell size (0.0 to 1.0). Default is 1.0 (no gap).</param>
    /// <param name="gradientOptions">Optional gradient options for the QR code modules.</param>
    /// <param name="finderPatternShape">The shape to use for drawing finder patterns. If null, uses same shape as modules.</param>
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
        QRCodeRenderer.Render(canvas, area, data, codeColor, backgroundColor, iconData, moduleShape, moduleSizePercent, gradientOptions, finderPatternShape);
    }

    /// <summary>
    /// Renders a Micro QR code on the canvas with default colors.
    /// </summary>
    /// <remarks>
    /// Micro QR does not offer the Standard QR icon overlay or custom finder pattern
    /// shape options (single finder pattern, no error-correction headroom for overlays).
    /// </remarks>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="data">The Micro QR code data.</param>
    /// <param name="width">The width of the rendering area.</param>
    /// <param name="height">The height of the rendering area.</param>
    /// <param name="clearColor">The color used to clear the canvas. Defaults to Transparent.</param>
    /// <param name="codeColor">The color of the modules. Defaults to Black.</param>
    /// <param name="backgroundColor">The background color of the symbol. Defaults to White.</param>
    /// <param name="moduleShape">The shape to use for drawing modules. If null, rectangles are used.</param>
    /// <param name="moduleSizePercent">The size of each module as a percentage of the cell size (0.0 to 1.0). Default is 1.0 (no gap).</param>
    /// <param name="gradientOptions">Optional gradient options for the modules.</param>
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
        GradientOptions? gradientOptions = null)
    {
        var area = SKRect.Create(0, 0, width, height);
        canvas.Render(data, area, clearColor, codeColor, backgroundColor, moduleShape, moduleSizePercent, gradientOptions);
    }

    /// <summary>
    /// Renders a Micro QR code on the canvas with custom colors.
    /// </summary>
    /// <remarks>
    /// Micro QR does not offer the Standard QR icon overlay or custom finder pattern
    /// shape options (single finder pattern, no error-correction headroom for overlays).
    /// </remarks>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="data">The Micro QR code data.</param>
    /// <param name="area">The rectangular area where the Micro QR code will be rendered.</param>
    /// <param name="clearColor">The color used to clear the canvas. Defaults to Transparent.</param>
    /// <param name="codeColor">The color of the modules. Defaults to Black.</param>
    /// <param name="backgroundColor">The background color of the symbol. Defaults to White.</param>
    /// <param name="moduleShape">The shape to use for drawing modules. If null, rectangles are used.</param>
    /// <param name="moduleSizePercent">The size of each module as a percentage of the cell size (0.0 to 1.0). Default is 1.0 (no gap).</param>
    /// <param name="gradientOptions">Optional gradient options for the modules.</param>
    public static void Render(
        this SKCanvas canvas,
        MicroQRCodeData data,
        SKRect area,
        SKColor? clearColor = null,
        SKColor? codeColor = null,
        SKColor? backgroundColor = null,
        ModuleShape? moduleShape = null,
        float moduleSizePercent = 1.0f,
        GradientOptions? gradientOptions = null)
    {
        canvas.Clear(clearColor ?? SKColors.Transparent);
        QRCodeRenderer.Render(canvas, area, data, codeColor, backgroundColor, moduleShape, moduleSizePercent, gradientOptions);
    }

    /// <summary>
    /// Renders an rMQR code on the canvas with default colors.
    /// </summary>
    /// <remarks>
    /// The rectangular symbol (quiet zone included) is drawn with a uniform module
    /// scale and centered in the area (letterbox); the whole area receives the
    /// background color. rMQR offers no icon overlay or finder styling options.
    /// </remarks>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="data">The rMQR code data.</param>
    /// <param name="width">The width of the rendering area.</param>
    /// <param name="height">The height of the rendering area.</param>
    /// <param name="clearColor">The color used to clear the canvas. Defaults to Transparent.</param>
    /// <param name="codeColor">The color of the modules. Defaults to Black.</param>
    /// <param name="backgroundColor">The background color of the symbol. Defaults to White.</param>
    /// <param name="moduleShape">The shape to use for drawing modules. If null, rectangles are used.</param>
    /// <param name="moduleSizePercent">The size of each module as a percentage of the cell size (0.0 to 1.0). Default is 1.0 (no gap).</param>
    /// <param name="gradientOptions">Optional gradient options for the modules.</param>
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
        GradientOptions? gradientOptions = null)
    {
        var area = SKRect.Create(0, 0, width, height);
        canvas.Render(data, area, clearColor, codeColor, backgroundColor, moduleShape, moduleSizePercent, gradientOptions);
    }

    /// <summary>
    /// Renders an rMQR code on the canvas with custom colors.
    /// </summary>
    /// <remarks>
    /// The rectangular symbol (quiet zone included) is drawn with a uniform module
    /// scale and centered in the area (letterbox); the whole area receives the
    /// background color. rMQR offers no icon overlay or finder styling options.
    /// </remarks>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="data">The rMQR code data.</param>
    /// <param name="area">The rectangular area where the rMQR code will be rendered.</param>
    /// <param name="clearColor">The color used to clear the canvas. Defaults to Transparent.</param>
    /// <param name="codeColor">The color of the modules. Defaults to Black.</param>
    /// <param name="backgroundColor">The background color of the symbol. Defaults to White.</param>
    /// <param name="moduleShape">The shape to use for drawing modules. If null, rectangles are used.</param>
    /// <param name="moduleSizePercent">The size of each module as a percentage of the cell size (0.0 to 1.0). Default is 1.0 (no gap).</param>
    /// <param name="gradientOptions">Optional gradient options for the modules.</param>
    public static void Render(
        this SKCanvas canvas,
        RmQRCodeData data,
        SKRect area,
        SKColor? clearColor = null,
        SKColor? codeColor = null,
        SKColor? backgroundColor = null,
        ModuleShape? moduleShape = null,
        float moduleSizePercent = 1.0f,
        GradientOptions? gradientOptions = null)
    {
        canvas.Clear(clearColor ?? SKColors.Transparent);
        QRCodeRenderer.Render(canvas, area, data, codeColor, backgroundColor, moduleShape, moduleSizePercent, gradientOptions);
    }
}
