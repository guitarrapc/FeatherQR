using SkiaSharp;
using FeatherQR.Internals;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Draws a QR, Micro QR or rMQR code onto a SkiaSharp canvas, with full control over colors, shapes, gradients and icon overlays.
/// Use it when the image builders do not give you the control you need.
/// </summary>
/// <remarks>
/// The symbol is fitted into the area at one uniform module scale and centered, whatever the area's aspect ratio, using the same fit as the image builders (given an explicit canvas size, they also round the offset to whole pixels): modules stay square, because a symbol whose modules are not square stops being findable well before it stops being drawn. The background covers the whole area.
/// To pre-distort a symbol for an output device whose dots are not square, scale the canvas and draw into a square area.
/// An inverted area is refused rather than read as a mirror; for a mirrored symbol (a transfer print, a sticker read through glass), scale the canvas by -1 about the area.
/// </remarks>
public static class SymbolRenderer
{
    /// <summary>
    /// Draws a QR code into an area of the canvas.
    /// </summary>
    /// <remarks>
    /// A non-square area gets the symbol at a uniform module scale, centered, with the background over the whole area. <see cref="GetFinderPatternRect"/> and <see cref="GetIconRects"/> fit the same way, so pass them the same area.
    /// With the default rectangle shape at <paramref name="moduleSizePercent"/> 1.0, horizontal runs of dark modules are drawn as single merged rectangles (fewer native draw calls).
    /// Merged and per-module rendering are pixel-identical under axis-preserving canvas transforms (translation/scale); under rotation, shared-edge rounding may differ at sub-pixel level.
    /// Any custom module shape or a module size below 1.0 falls back to per-module drawing.
    /// </remarks>
    /// <param name="canvas">The canvas to render the QR code on.</param>
    /// <param name="area">Where to draw it.</param>
    /// <param name="data">The QR code to draw.</param>
    /// <param name="codeColor">The dark modules. This parameter is required; pass <see langword="null"/> for black.</param>
    /// <param name="backgroundColor">Behind the QR code. This parameter is required; pass <see langword="null"/> for white.</param>
    /// <param name="iconData">An icon to draw over the center. None when omitted.</param>
    /// <param name="moduleShape">The shape to draw modules as. Squares when omitted. It styles the data modules only; the finder patterns are never drawn with gaps, or the symbol stops being detectable.</param>
    /// <param name="moduleSizePercent">How much of its cell a data module fills, 0.0 to 1.0. The default 1.0 leaves no gaps.</param>
    /// <param name="gradientOptions">A gradient to paint the modules with. Solid color when omitted.</param>
    /// <param name="finderPatternShape">The shape to draw the finder patterns as. Plain squares when omitted. On a background that is not fully opaque, any shape here and any styled module set draw the finder patterns by cutting their light rings out of it, which SVG output cannot express: the finder patterns are dropped from the document and the symbol stops scanning.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="canvas"/> or <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="area"/> is inverted (a negative width or height) or has a coordinate or size that is not finite. An area no symbol fits in, a zero width or height or an aspect ratio so extreme the centered square rounds away, keeps its background and draws nothing else.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="moduleSizePercent"/> is outside 0.0 to 1.0, an <paramref name="iconData"/> size, border or occupancy limit is out of range, or <paramref name="gradientOptions"/> has a direction that is not defined. Icon settings are checked whatever the area's size, so a render that draws nothing still refuses an icon it could not draw.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the icon does not fit the QR code. Checked whatever the area's size, like the icon's other settings.</exception>
    public static void Render(
        SKCanvas canvas,
        SKRect area,
        QRCodeData data,
        SKColor? codeColor,
        SKColor? backgroundColor,
        IconData? iconData = null,
        ModuleShape? moduleShape = null,
        float moduleSizePercent = 1.0f,
        GradientOptions? gradientOptions = null,
        FinderPatternShape? finderPatternShape = null)
    {
        ValidateRenderArguments(canvas, data, area, moduleSizePercent, gradientOptions, nameof(data));
        if (iconData is not null)
            ValidateIcon(data, iconData);

        var bgColor = backgroundColor ?? SKColors.White;
        var fgColor = codeColor ?? SKColors.Black;
        var shape = moduleShape ?? RectangleModuleShape.Default;

        // Draw the background over the whole area at once
        using var lightPaint = new SKPaint() { Color = bgColor, Style = SKPaintStyle.Fill };
        canvas.DrawRect(area, lightPaint);

        // Fit: uniform module scale, symbol centered in the area.
        var symbolArea = GetSquareArea(area);

        // No symbol fits, so the area keeps its background and nothing else is drawn, overlays sized in pixels included.
        if (IsEmpty(symbolArea))
            return;

        // Create paint with gradient or solid color
        // disable antialiasing as it causes gray border around each module.
        using var darkPaint = new SKPaint() { Style = SKPaintStyle.Fill, IsAntialias = shape.RequiresAntialiasing };

        // Apply gradient if specified. The shader wrapper must be disposed here;
        // disposing the paint alone leaves the SKShader to the finalizer.
        using var gradientShader = CreateGradientShader(symbolArea, gradientOptions);
        if (gradientShader is not null)
        {
            darkPaint.Shader = gradientShader;
        }
        else
        {
            darkPaint.Color = fgColor;
        }

        // Draw regular modules (exclude finder patterns when a shape draws them).
        // Full-cell rectangles with antialiasing off touch exactly, so horizontal runs
        // of dark modules collapse into single rects, far fewer native draw calls.
        var finderShape = ResolveFinderShape(shape, moduleSizePercent, finderPatternShape);
        var skipFinderPatterns = finderShape is not null;
        if (shape is RectangleModuleShape && moduleSizePercent == 1.0f)
        {
            DrawModuleRuns(canvas, new StandardQrMatrixView(data), symbolArea, darkPaint, skipFinderPatterns);
        }
        else
        {
            DrawModules(canvas, new StandardQrMatrixView(data), symbolArea, darkPaint, shape, moduleSizePercent, skipFinderPatterns);
        }

        // Draw finder patterns
        if (finderShape is not null)
        {
            // Finder shapes draw the outer dark area before drawing the light inner
            // ring. A non-opaque light color with SrcOver cannot restore the
            // background after that dark area has been drawn. In that case, draw
            // each finder on a temporary layer and clear its light modules to reveal
            // the already-rendered QR background underneath.
            var requiresBackgroundRestore = bgColor.Alpha < byte.MaxValue;
            if (requiresBackgroundRestore)
            {
                lightPaint.BlendMode = SKBlendMode.Clear;
            }

            // Curved finder shapes require antialiasing independently from module shapes.
            // Apply the same setting to both paints so their shared edges are rasterized
            // consistently.
            if (finderShape.RequiresAntialiasing)
            {
                darkPaint.IsAntialias = true;
                lightPaint.IsAntialias = true;
            }

            // total 3 finder patterns
            for (var i = 0; i < 3; i++)
            {
                var finderRect = GetFinderPatternRect(data, i, symbolArea);
                if (!requiresBackgroundRestore)
                {
                    finderShape.Draw(canvas, finderRect, darkPaint, lightPaint);
                    continue;
                }

                var saveCount = canvas.SaveLayer(finderRect, null);
                try
                {
                    finderShape.Draw(canvas, finderRect, darkPaint, lightPaint);
                }
                finally
                {
                    canvas.RestoreToCount(saveCount);
                }
            }
        }

        // Draw the icon if provided
        if (iconData?.Icon is not null)
        {
            var (iconRect, borderRect) = GetIconRects(data, symbolArea, iconData);
            iconData.Icon.Draw(canvas, iconRect, borderRect, bgColor);
        }
    }

    /// <summary>
    /// Draws a Micro QR code into an area of the canvas.
    /// </summary>
    /// <remarks>
    /// A non-square area gets the symbol at a uniform module scale, centered, with the background over the whole area.
    /// Micro QR has one finder pattern, at the top left, and no error-correction headroom for overlays, so the Standard QR icon option is intentionally not available.
    /// See <see cref="Render(SKCanvas, SKRect, QRCodeData, SKColor?, SKColor?, IconData?, ModuleShape?, float, GradientOptions?, FinderPatternShape?)"/> for the module-run merge behavior shared with Standard QR.
    /// </remarks>
    /// <param name="canvas">The canvas to render the Micro QR code on.</param>
    /// <param name="area">Where to draw it.</param>
    /// <param name="data">The Micro QR code to draw.</param>
    /// <param name="codeColor">The dark modules. This parameter is required; pass <see langword="null"/> for black.</param>
    /// <param name="backgroundColor">Behind the Micro QR code. This parameter is required; pass <see langword="null"/> for white.</param>
    /// <param name="moduleShape">The shape to draw modules as. Squares when omitted. It styles the data modules only; the finder pattern is never drawn with gaps, or the symbol stops being detectable.</param>
    /// <param name="moduleSizePercent">How much of its cell a data module fills, 0.0 to 1.0. The default 1.0 leaves no gaps.</param>
    /// <param name="gradientOptions">A gradient to paint the modules with. Solid color when omitted.</param>
    /// <param name="finderPatternShape">The shape to draw the finder pattern as. A plain square when omitted. On a background that is not fully opaque, any shape here and any styled module set draw the finder pattern by cutting its light rings out of it, which SVG output cannot express: the finder pattern is dropped from the document and the symbol stops scanning.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="canvas"/> or <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="area"/> is inverted (a negative width or height) or has a coordinate or size that is not finite. An area no symbol fits in, a zero width or height or an aspect ratio so extreme the centered square rounds away, keeps its background and draws nothing else.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="moduleSizePercent"/> is outside 0.0 to 1.0, or <paramref name="gradientOptions"/> has a direction that is not defined.</exception>
    public static void Render(
        SKCanvas canvas,
        SKRect area,
        MicroQRCodeData data,
        SKColor? codeColor,
        SKColor? backgroundColor,
        ModuleShape? moduleShape = null,
        float moduleSizePercent = 1.0f,
        GradientOptions? gradientOptions = null,
        FinderPatternShape? finderPatternShape = null)
    {
        ValidateRenderArguments(canvas, data, area, moduleSizePercent, gradientOptions, nameof(data));

        var bgColor = backgroundColor ?? SKColors.White;
        var fgColor = codeColor ?? SKColors.Black;
        var shape = moduleShape ?? RectangleModuleShape.Default;

        // Draw the background over the whole area at once
        using var lightPaint = new SKPaint() { Color = bgColor, Style = SKPaintStyle.Fill };
        canvas.DrawRect(area, lightPaint);

        // Fit: uniform module scale, symbol centered in the area.
        var symbolArea = GetSquareArea(area);

        // No symbol fits, so the area keeps its background and nothing else is drawn, overlays sized in pixels included.
        if (IsEmpty(symbolArea))
            return;

        // disable antialiasing as it causes gray border around each module.
        using var darkPaint = new SKPaint() { Style = SKPaintStyle.Fill, IsAntialias = shape.RequiresAntialiasing };

        // Apply gradient if specified. The shader wrapper must be disposed here;
        // disposing the paint alone leaves the SKShader to the finalizer.
        using var gradientShader = CreateGradientShader(symbolArea, gradientOptions);
        if (gradientShader is not null)
        {
            darkPaint.Shader = gradientShader;
        }
        else
        {
            darkPaint.Color = fgColor;
        }

        var finderShape = ResolveFinderShape(shape, moduleSizePercent, finderPatternShape);
        var view = new MicroQRMatrixView(data);
        if (shape is RectangleModuleShape && moduleSizePercent == 1.0f)
        {
            DrawModuleRuns(canvas, view, symbolArea, darkPaint, finderShape is not null);
        }
        else
        {
            DrawModules(canvas, view, symbolArea, darkPaint, shape, moduleSizePercent, finderShape is not null);
        }

        if (finderShape is not null)
            DrawSingleFinder(canvas, view, symbolArea, finderShape, darkPaint, lightPaint, bgColor);
    }

    /// <summary>
    /// Draws a rectangular rMQR code into an area of the canvas, at a uniform module scale and centered, never stretched.
    /// </summary>
    /// <remarks>
    /// The whole area gets the background color, not just the code itself. rMQR has no error correction headroom to spare, so there is no icon option.
    /// Modules are merged as for Standard QR.
    /// </remarks>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="area">Where to draw it.</param>
    /// <param name="data">The rMQR code to draw.</param>
    /// <param name="codeColor">The dark modules. This parameter is required; pass <see langword="null"/> for black.</param>
    /// <param name="backgroundColor">Behind the rMQR code. This parameter is required; pass <see langword="null"/> for white.</param>
    /// <param name="moduleShape">The shape to draw modules as. Squares when omitted. It styles the data modules only; the finder pattern is never drawn with gaps, or the symbol stops being detectable.</param>
    /// <param name="moduleSizePercent">How much of its cell a data module fills, 0.0 to 1.0. The default 1.0 leaves no gaps.</param>
    /// <param name="gradientOptions">A gradient to paint the modules with. Solid color when omitted.</param>
    /// <param name="finderPatternShape">The shape to draw the finder pattern as. A plain square when omitted. On a background that is not fully opaque, any shape here and any styled module set draw the finder pattern by cutting its light rings out of it, which SVG output cannot express: the finder pattern is dropped from the document and the symbol stops scanning.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="canvas"/> or <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="area"/> is inverted (a negative width or height) or has a coordinate or size that is not finite. An area no symbol fits in, a zero width or height or an aspect ratio so extreme the centered rectangle rounds away, keeps its background and draws nothing else.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="moduleSizePercent"/> is outside 0.0 to 1.0, or <paramref name="gradientOptions"/> has a direction that is not defined.</exception>
    public static void Render(
        SKCanvas canvas,
        SKRect area,
        RmQRCodeData data,
        SKColor? codeColor,
        SKColor? backgroundColor,
        ModuleShape? moduleShape = null,
        float moduleSizePercent = 1.0f,
        GradientOptions? gradientOptions = null,
        FinderPatternShape? finderPatternShape = null)
    {
        ValidateRenderArguments(canvas, data, area, moduleSizePercent, gradientOptions, nameof(data));

        var bgColor = backgroundColor ?? SKColors.White;
        var fgColor = codeColor ?? SKColors.Black;
        var shape = moduleShape ?? RectangleModuleShape.Default;

        // Draw the background over the whole area at once
        using var lightPaint = new SKPaint() { Color = bgColor, Style = SKPaintStyle.Fill };
        canvas.DrawRect(area, lightPaint);

        // Letterbox: uniform module scale, symbol centered in the area.
        var symbolArea = GetLetterboxedArea(area, data.Width, data.Height);

        // No symbol fits, so the area keeps its background and nothing else is drawn, overlays sized in pixels included.
        if (IsEmpty(symbolArea))
            return;

        // disable antialiasing as it causes gray border around each module.
        using var darkPaint = new SKPaint() { Style = SKPaintStyle.Fill, IsAntialias = shape.RequiresAntialiasing };

        using var gradientShader = CreateGradientShader(symbolArea, gradientOptions);
        if (gradientShader is not null)
        {
            darkPaint.Shader = gradientShader;
        }
        else
        {
            darkPaint.Color = fgColor;
        }

        var finderShape = ResolveFinderShape(shape, moduleSizePercent, finderPatternShape);
        var view = new RmQRMatrixView(data);
        if (shape is RectangleModuleShape && moduleSizePercent == 1.0f)
        {
            DrawModuleRuns(canvas, view, symbolArea, darkPaint, finderShape is not null);
        }
        else
        {
            DrawModules(canvas, view, symbolArea, darkPaint, shape, moduleSizePercent, finderShape is not null);
        }

        if (finderShape is not null)
            DrawSingleFinder(canvas, view, symbolArea, finderShape, darkPaint, lightPaint, bgColor);
    }

    /// <summary>
    /// The largest rectangle with the matrix aspect ratio that fits inside <paramref name="area"/>, centered (uniform module scale, no stretch).
    /// </summary>
    internal static SKRect GetLetterboxedArea(SKRect area, int matrixWidth, int matrixHeight)
    {
        var scale = Math.Min(area.Width / matrixWidth, area.Height / matrixHeight);
        var width = matrixWidth * scale;
        var height = matrixHeight * scale;
        var left = area.Left + (area.Width - width) / 2;
        var top = area.Top + (area.Height - height) / 2;
        return SKRect.Create(left, top, width, height);
    }

    /// <summary>
    /// The largest square that fits inside <paramref name="area"/>, centered: the letterbox for a square symbology.
    /// </summary>
    /// <remarks>
    /// An area already square to within float rounding comes back exactly as given, so a square area draws what it did before the fit existed, and fitting twice is the same as fitting once. The public geometry helpers depend on the second: they fit whatever area they are handed, and the renderer hands them one it already fitted.
    /// Expects an area that passed <see cref="ValidateArea"/>.
    /// </remarks>
    internal static SKRect GetSquareArea(SKRect area)
    {
        var width = area.Width;
        var height = area.Height;

        // SKRect.Create(x, y, s, s) stores right and bottom as rounded sums, so a square at fractional
        // coordinates can come out a rounding step from square; shrinking it by that step moves module edges.
        // Scaled per axis before the sum, which would overflow near the top of the float range.
        var slack = MaxAbs(area.Left, area.Right) * SquareTolerance + MaxAbs(area.Top, area.Bottom) * SquareTolerance;
        if (Math.Abs(width - height) <= slack)
            return area;

        var side = Math.Min(width, height);
        return SKRect.Create(area.Left + (width - side) / 2, area.Top + (height - side) / 2, side, side);
    }

    /// <summary>
    /// Refuses an area with a negative or non-finite size. A zero size is accepted; the renderer keeps its background and draws nothing else.
    /// </summary>
    /// <remarks>
    /// An inverted area reads two ways, a mirror or the same bounds written backwards, and for an asymmetric symbol the two are different pictures, so neither is guessed. A mirror is asked for with the canvas.
    /// </remarks>
    internal static void ValidateArea(SKRect area, string paramName)
    {
        // Finite edges can still be further apart than a float holds, and a finite width or height implies finite edges.
        if (!IsFinite(area.Width) || !IsFinite(area.Height))
            throw new ArgumentException("The area must have finite coordinates and size.", paramName);
        if (area.Width < 0 || area.Height < 0)
            throw new ArgumentException("The area must not be inverted: its width and height must be zero or more. To mirror the symbol, scale the canvas by -1 about the area instead.", paramName);
    }

    /// <summary>
    /// Refuses what every <c>Render</c> overload takes, so the canvas extensions can refuse it before they clear the canvas.
    /// </summary>
    internal static void ValidateRenderArguments(SKCanvas canvas, object? data, SKRect area, float moduleSizePercent, GradientOptions? gradientOptions, string dataParamName)
    {
        if (canvas is null)
            throw new ArgumentNullException(nameof(canvas));
        if (data is null)
            throw new ArgumentNullException(dataParamName);
        ValidateArea(area, "area");
        // Written as a negated range so that NaN, which is neither below 0 nor above 1, is refused as well.
        if (moduleSizePercent is not (>= 0f and <= 1.0f))
            throw new ArgumentOutOfRangeException(nameof(moduleSizePercent), "Module size percent must be between 0.0 and 1.0.");
        // Nothing upstream checks Direction: the constructor validates only the colours and their stops.
        if (gradientOptions is not null && !IsDefinedDirection(gradientOptions.Direction))
            throw new ArgumentOutOfRangeException(nameof(gradientOptions), $"Direction {gradientOptions.Direction} is not a valid gradient direction.");
    }

    private static bool IsDefinedDirection(GradientDirection direction) => direction
        is GradientDirection.None
        or GradientDirection.LeftToRight
        or GradientDirection.RightToLeft
        or GradientDirection.TopToBottom
        or GradientDirection.BottomToTop
        or GradientDirection.TopLeftToBottomRight
        or GradientDirection.TopRightToBottomLeft
        or GradientDirection.BottomLeftToTopRight
        or GradientDirection.BottomRightToTopLeft;

    /// <summary>
    /// Refuses icon settings that do not fit the QR code, whatever size the area has.
    /// </summary>
    /// <remarks>
    /// Separate from the rectangles it guards, because a render that draws nothing still refuses an icon it could never draw: a collapsed panel would otherwise hide the mistake until it opens.
    /// </remarks>
    internal static void ValidateIcon(QRCodeData data, IconData iconData)
    {
        if (iconData.IconSizeModules is not null)
        {
            var iconBorderModules = iconData.IconBorderModules ?? 1;
            if (iconData.IconSizeModules.Value < 1)
                throw new ArgumentOutOfRangeException(nameof(iconData), "Icon size modules must be at least 1.");
            if (iconBorderModules < 0)
                throw new ArgumentOutOfRangeException(nameof(iconData), "Icon border modules must be 0 or greater.");
            if (iconData.MaxCoreOccupancyPercent is < 1 or > 100)
                throw new ArgumentOutOfRangeException(nameof(iconData), "Max core occupancy percent must be between 1 and 100.");

            // Widened: a module count near int.MaxValue overflowed the sum, and the negative total passed both limits below.
            var totalModules = (long)iconData.IconSizeModules.Value + ((long)iconBorderModules * 2);
            var coreSize = data.GetCoreSize();
            var maxByCore = coreSize * iconData.MaxCoreOccupancyPercent / 100;

            if (totalModules > data.Size)
            {
                throw new InvalidOperationException(
                    $"Icon occupies {totalModules} modules, which exceeds QR matrix size {data.Size}.");
            }

            if (totalModules > maxByCore)
            {
                throw new InvalidOperationException(
                    $"Icon occupies {totalModules} modules, but max allowed is {maxByCore} " +
                    $"(coreSize={coreSize}, maxCoreOccupancyPercent={iconData.MaxCoreOccupancyPercent}).");
            }
        }
        else
        {
            if (iconData.IconSizePercent is < 1 or > 100)
                throw new ArgumentOutOfRangeException(nameof(iconData), "Icon size percent must be between 1 and 100.");
            if (iconData.IconBorderWidth < 0)
                throw new ArgumentOutOfRangeException(nameof(iconData), "Icon border width must be 0 or greater.");
        }
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    /// <summary>A fitted symbol with no size. A zero area gives one, and so does an area long enough that centering the symbol swallows its shorter side.</summary>
    private static bool IsEmpty(SKRect symbolArea) => symbolArea.Width <= 0 || symbolArea.Height <= 0;

    private static float MaxAbs(float a, float b) => Math.Max(Math.Abs(a), Math.Abs(b));

    /// <summary>
    /// Slack for "already square", per axis, as a share of that axis's largest coordinate: two float steps there, since a float step at m is at most m / 2^23.
    /// Two steps is what a square costs to store, not a margin: a square whose edges are each scaled, which is what a canvas scale gives, comes back two steps from square.
    /// It follows that far from the origin, where a step is a large share of the area, a rectangle that is not square can pass as one; no symbol renders there either, since the same step quantises every module edge.
    /// </summary>
    private const float SquareTolerance = 1f / (1 << 22);

    /// <summary>
    /// Works out where an icon and its border land inside a QR code.
    /// </summary>
    /// <remarks>
    /// When <see cref="IconData.IconSizeModules"/> is set, sizing is module-based and percent/pixel values are ignored.
    /// A non-square area is fitted exactly as <c>Render</c> fits it, so pass the area you drew into. An area no symbol fits in, where <c>Render</c> draws nothing, gives empty rectangles at the fitted symbol's centre.
    /// Module-based icons are validated against QR size and core occupancy at render time.
    /// Icon rectangles are snapped to the module grid; even module sizes cannot be geometrically centered on an odd QR matrix.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> or <paramref name="iconData"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="area"/> is inverted (a negative width or height) or has a coordinate or size that is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the icon size, border or occupancy limit is out of range.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the icon does not fit the QR code. Checked whatever the area's size, like the icon's other settings.</exception>
    public static (SKRect iconRect, SKRect borderRect) GetIconRects(QRCodeData data, SKRect area, IconData iconData)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (iconData is null)
            throw new ArgumentNullException(nameof(iconData));
        ValidateArea(area, nameof(area));
        ValidateIcon(data, iconData);

        area = GetSquareArea(area);
        var centerX = area.Left + area.Width / 2;
        var centerY = area.Top + area.Height / 2;

        // The render draws nothing here, so neither the icon nor a border sized in pixels has anywhere to go.
        if (IsEmpty(area))
            return (new SKRect(centerX, centerY, centerX, centerY), new SKRect(centerX, centerY, centerX, centerY));

        if (iconData.IconSizeModules is not null)
        {
            var iconSizeModules = iconData.IconSizeModules.Value;
            var iconBorderModules = iconData.IconBorderModules ?? 1;
            var size = data.Size;

            var cellWidth = area.Width / size;
            var cellHeight = area.Height / size;
            var iconWidth = iconSizeModules * cellWidth;
            var iconHeight = iconSizeModules * cellHeight;
            var borderWidth = iconBorderModules * cellWidth;
            var borderHeight = iconBorderModules * cellHeight;

            // Snap to module grid. QR matrix size is always odd, so an even icon size
            // cannot be geometrically centered without cutting modules; prefer module edges.
            var iconOriginModule = (size - iconSizeModules) / 2;
            var iconLeft = area.Left + iconOriginModule * cellWidth;
            var iconTop = area.Top + iconOriginModule * cellHeight;
            var iconRect = SKRect.Create(iconLeft, iconTop, iconWidth, iconHeight);

            var borderRect = iconBorderModules > 0
                ? SKRect.Create(
                    iconLeft - borderWidth,
                    iconTop - borderHeight,
                    iconWidth + borderWidth * 2,
                    iconHeight + borderHeight * 2)
                : iconRect;

            return (iconRect, borderRect);
        }
        else
        {
            var iconSize = iconData.IconSizePercent / 100f;
            var iconWidth = area.Width * iconSize;
            var iconHeight = area.Height * iconSize;

            var iconRect = SKRect.Create(
                centerX - iconWidth / 2,
                centerY - iconHeight / 2,
                iconWidth,
                iconHeight);

            var borderWidth = iconData.IconBorderWidth;
            var borderRect = borderWidth > 0
                ? SKRect.Create(
                    centerX - iconWidth / 2 - borderWidth,
                    centerY - iconHeight / 2 - borderWidth,
                    iconWidth + (borderWidth * 2f),
                    iconHeight + (borderWidth * 2f))
                : iconRect;

            return (iconRect, borderRect);
        }
    }

    /// <summary>
    /// Where one of the three finder patterns lands inside a rendered QR code, so you can draw over or around it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The finder patterns are the large squares typically located at three corners of a QR code.
    /// This method calculates their positions based on the QR code's size and quiet zone, ensuring accurate placement within the specified rendering area.
    /// </para>
    /// <para>
    /// Pass the area you gave <c>Render</c>: a non-square one is fitted the same way, so the answer is where the pattern was drawn.
    /// For a <see cref="QRCodeImageBuilder"/> output, the whole image works when only the canvas size was set, to within half a pixel, because the builder rounds its centering offset down to whole pixels where this fit does not.
    /// With a module pixel size pinned inside a larger canvas the symbol is smaller than the fit, so pass its content rectangle instead: <c>matrix size × module pixel size</c> on each side, offset by half the leftover, rounded down.
    /// </para>
    /// </remarks>
    /// <param name="data">The QR code the finder patterns belong to.</param>
    /// <param name="patternIndex">Which pattern: 0 top-left, 1 top-right, 2 bottom-left.</param>
    /// <param name="renderArea">The area the QR code was drawn into.</param>
    /// <returns>An SKRect representing the position and size of the specified finder pattern within the rendering area. An area no symbol fits in, where <c>Render</c> draws nothing, gives an empty rectangle at the fitted symbol's top left corner.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="patternIndex"/> is not 0, 1 or 2.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="renderArea"/> is inverted (a negative width or height) or has a coordinate or size that is not finite.</exception>
    public static SKRect GetFinderPatternRect(QRCodeData data, int patternIndex, SKRect renderArea)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (patternIndex is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(patternIndex), "Pattern index must be 0 (top-left), 1 (top-right), or 2 (bottom-left).");
        ValidateArea(renderArea, nameof(renderArea));

        renderArea = GetSquareArea(renderArea);

        // No symbol was drawn, so no pattern was either; a cell of one side's size would give a rect with the other side's.
        if (IsEmpty(renderArea))
            return new SKRect(renderArea.Left, renderArea.Top, renderArea.Left, renderArea.Top);

        var size = data.Size;
        var coreSize = data.GetCoreSize();
        var cellWidth = renderArea.Width / size;
        var cellHeight = renderArea.Height / size;
        var finderWidth = cellWidth * 7;
        var finderHeight = cellHeight * 7;

        var quietZoneOffset = size - coreSize;

        return patternIndex switch
        {
            0 => SKRect.Create(
                renderArea.Left + ((float)quietZoneOffset / 2) * cellWidth,
                renderArea.Top + ((float)quietZoneOffset / 2) * cellHeight,
                finderWidth,
                finderHeight),
            1 => SKRect.Create(
                renderArea.Left + (coreSize - 7 + (float)quietZoneOffset / 2) * cellWidth,
                renderArea.Top + ((float)quietZoneOffset / 2) * cellHeight,
                finderWidth,
                finderHeight),
            2 => SKRect.Create(
                renderArea.Left + ((float)quietZoneOffset / 2) * cellWidth,
                renderArea.Top + (coreSize - 7 + (float)quietZoneOffset / 2) * cellHeight,
                finderWidth,
                finderHeight),
            _ => throw new ArgumentOutOfRangeException(nameof(patternIndex), "Pattern index must be 0 (top-left), 1 (top-right), or 2 (bottom-left)."),
        };
    }

    /// <summary>
    /// Which shape draws the finder patterns, or <c>null</c> to leave them to the module loop.
    /// </summary>
    /// <remarks>
    /// The module shape and size style the data; a finder is located by its 1:1:3:1:1 run, which
    /// gaps between modules erase, so it never takes them. Full-size rectangles already draw the
    /// pattern solid and stay in the run-merge path; anything else gets an explicit shape.
    /// </remarks>
    private static FinderPatternShape? ResolveFinderShape(ModuleShape shape, float moduleSizePercent, FinderPatternShape? requested)
    {
        if (requested is not null)
            return requested;

        return shape is RectangleModuleShape && moduleSizePercent == 1.0f
            ? null
            : RectangleFinderPatternShape.Default;
    }

    /// <summary>
    /// Draws the single top-left finder pattern that Micro QR and rMQR share, in the same
    /// transparent-background-safe way as the Standard QR path above.
    /// </summary>
    private static void DrawSingleFinder<TView>(SKCanvas canvas, TView data, SKRect area, FinderPatternShape finderShape, SKPaint darkPaint, SKPaint lightPaint, SKColor bgColor)
        where TView : struct, IModuleMatrixView
    {
        var cellWidth = area.Width / data.Width;
        var cellHeight = area.Height / data.Height;
        var quietZoneX = (data.Width - data.CoreWidth) / 2f;
        var quietZoneY = (data.Height - data.CoreHeight) / 2f;
        var finderRect = SKRect.Create(
            area.Left + quietZoneX * cellWidth,
            area.Top + quietZoneY * cellHeight,
            cellWidth * 7,
            cellHeight * 7);

        var requiresBackgroundRestore = bgColor.Alpha < byte.MaxValue;
        if (requiresBackgroundRestore)
            lightPaint.BlendMode = SKBlendMode.Clear;

        if (finderShape.RequiresAntialiasing)
        {
            darkPaint.IsAntialias = true;
            lightPaint.IsAntialias = true;
        }

        if (!requiresBackgroundRestore)
        {
            finderShape.Draw(canvas, finderRect, darkPaint, lightPaint);
            return;
        }

        var saveCount = canvas.SaveLayer(finderRect, null);
        try
        {
            finderShape.Draw(canvas, finderRect, darkPaint, lightPaint);
        }
        finally
        {
            canvas.RestoreToCount(saveCount);
        }
    }

    /// <summary>
    /// Draws dark modules as merged horizontal runs of full-cell rectangles.
    /// Only valid for <see cref="RectangleModuleShape"/> at 100% module size, where adjacent modules share edges, antialiasing is always off (<see cref="RectangleModuleShape.RequiresAntialiasing"/> is false), and merging is pixel-identical to per-module drawing.
    /// The parity holds under axis-preserving canvas transforms (translation/scale); rotated canvases may rasterize shared edges hairline-differently at sub-pixel level, inherent to non-axis-aligned rasterization, which affects per-module drawing between adjacent modules just the same.
    /// </summary>
    private static void DrawModuleRuns<TView>(SKCanvas canvas, TView data, SKRect area, SKPaint paint, bool skipFinderPatterns)
        where TView : struct, IModuleMatrixView
    {
        var cellWidth = area.Width / data.Width;
        var cellHeight = area.Height / data.Height;
        var quietZone = (data.Width - data.CoreWidth) / 2;

        // The quiet zone is always light, so only the core area is scanned. Run
        // detection is shared with the public GetModuleRectangles surface, so what
        // this path draws is exactly the geometry that API reports.
        var runs = new ModuleRunEnumerator<TView>(data, skipFinderPatterns);
        while (runs.MoveNext())
        {
            var top = area.Top + (runs.RunRow + quietZone) * cellHeight;
            var left = area.Left + (runs.RunStart + quietZone) * cellWidth;
            var right = area.Left + (runs.RunStart + runs.RunLength + quietZone) * cellWidth;
            canvas.DrawRect(new SKRect(left, top, right, top + cellHeight), paint);
        }
    }

    /// <summary>
    /// Draws dark modules one by one through the module shape.
    /// Used for custom shapes and for module sizes below 100% (gaps between modules).
    /// </summary>
    private static void DrawModules<TView>(SKCanvas canvas, TView data, SKRect area, SKPaint paint, ModuleShape shape, float moduleSizePercent, bool skipFinderPatterns)
        where TView : struct, IModuleMatrixView
    {
        var coreWidth = data.CoreWidth;
        var coreHeight = data.CoreHeight;
        var cellWidth = area.Width / data.Width;
        var cellHeight = area.Height / data.Height;
        var quietZone = (data.Width - coreWidth) / 2;

        // Calculate module size with gaps
        var moduleWidth = cellWidth * moduleSizePercent;
        var moduleHeight = cellHeight * moduleSizePercent;
        var xOffset = (cellWidth - moduleWidth) / 2;
        var yOffset = (cellHeight - moduleHeight) / 2;

        // The quiet zone is always light, so only the core area is scanned.
        for (var coreRow = 0; coreRow < coreHeight; coreRow++)
        {
            var y = area.Top + (coreRow + quietZone) * cellHeight + yOffset;
            for (var coreCol = 0; coreCol < coreWidth; coreCol++)
            {
                if (skipFinderPatterns && data.IsFinderPattern(coreRow, coreCol))
                    continue;

                if (data.GetCoreModule(coreRow, coreCol))
                {
                    var x = area.Left + (coreCol + quietZone) * cellWidth + xOffset;
                    var rect = SKRect.Create(x, y, moduleWidth, moduleHeight);
                    shape.Draw(canvas, rect, paint);
                }
            }
        }
    }

    private static SKShader? CreateGradientShader(SKRect area, GradientOptions? gradientOptions)
    {
        if (gradientOptions is null || gradientOptions.Direction == GradientDirection.None)
            return null;

        var (start, end) = GetLinearGradientPoints(area, gradientOptions.Direction);
        return SKShader.CreateLinearGradient(start, end, gradientOptions.ColorArray, gradientOptions.ColorPositionArray, SKShaderTileMode.Clamp);
    }

    private static (SKPoint start, SKPoint end) GetLinearGradientPoints(SKRect area, GradientDirection direction)
    {
        return direction switch
        {
            GradientDirection.LeftToRight => (new SKPoint(area.Left, area.MidY), new SKPoint(area.Right, area.MidY)),
            GradientDirection.RightToLeft => (new SKPoint(area.Right, area.MidY), new SKPoint(area.Left, area.MidY)),
            GradientDirection.TopToBottom => (new SKPoint(area.MidX, area.Top), new SKPoint(area.MidX, area.Bottom)),
            GradientDirection.BottomToTop => (new SKPoint(area.MidX, area.Bottom), new SKPoint(area.MidX, area.Top)),
            GradientDirection.TopLeftToBottomRight => (new SKPoint(area.Left, area.Top), new SKPoint(area.Right, area.Bottom)),
            GradientDirection.TopRightToBottomLeft => (new SKPoint(area.Right, area.Top), new SKPoint(area.Left, area.Bottom)),
            GradientDirection.BottomLeftToTopRight => (new SKPoint(area.Left, area.Bottom), new SKPoint(area.Right, area.Top)),
            GradientDirection.BottomRightToTopLeft => (new SKPoint(area.Right, area.Bottom), new SKPoint(area.Left, area.Top)),
            _ => throw new ArgumentOutOfRangeException(nameof(direction), $"Direction {direction} is not a valid linear gradient direction."),
        };
    }
}
