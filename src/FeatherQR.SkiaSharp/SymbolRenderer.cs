using SkiaSharp;
using FeatherQR.Internals;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Provides low-level rendering capabilities for QR codes to SkiaSharp canvases.
/// Offers fine-grained control over appearance, including colors, shapes, gradients, and icon overlays.
/// </summary>
public static class QRCodeRenderer
{
    /// <summary>
    /// Render the specified data into the given area of the target canvas.
    /// </summary>
    /// <remarks>
    /// With the default rectangle shape at <paramref name="moduleSizePercent"/> 1.0,
    /// horizontal runs of dark modules are drawn as single merged rectangles
    /// (fewer native draw calls). Merged and per-module rendering are
    /// pixel-identical under axis-preserving canvas transforms (translation/scale);
    /// under rotation, shared-edge rounding may differ at sub-pixel level.
    /// Any custom module shape or a module size below 1.0 falls back to
    /// per-module drawing.
    /// </remarks>
    /// <param name="canvas">The canvas to render the QR code on.</param>
    /// <param name="area">The rectangular area where the QR code will be rendered.</param>
    /// <param name="data">The QR code data to render.</param>
    /// <param name="codeColor">The color of the QR code modules. If null, black is used.</param>
    /// <param name="backgroundColor">The background color. If null, white is used.</param>
    /// <param name="iconData">Optional icon data to overlay on the center of the QR code.</param>
    /// <param name="moduleShape">The shape to use for drawing modules. If null, rectangles are used.</param>
    /// <param name="moduleSizePercent">The size of each module as a percentage of the cell size (0.0 to 1.0). Default is 1.0 (no gap).</param>
    /// <param name="gradientOptions">Optional gradient options for the QR code modules.</param>
    /// <param name="finderPatternShape">The shape to use for drawing finder patterns. If null, finder patterns are drawn using the same shape as regular modules. Set to a custom shape to differentiate finder patterns from data modules.</param>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
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
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (moduleSizePercent is < 0f or > 1.0f)
            throw new ArgumentOutOfRangeException(nameof(moduleSizePercent), "Module size percent must be between 0.0 and 1.0.");

        var bgColor = backgroundColor ?? SKColors.White;
        var fgColor = codeColor ?? SKColors.Black;
        var shape = moduleShape ?? RectangleModuleShape.Default;

        // Draw the background at once
        using var lightPaint = new SKPaint() { Color = bgColor, Style = SKPaintStyle.Fill };
        canvas.DrawRect(area, lightPaint);

        // Create paint with gradient or solid color
        // disable antialiasing as it causes gray border around each module.
        using var darkPaint = new SKPaint() { Style = SKPaintStyle.Fill, IsAntialias = shape.RequiresAntialiasing };

        // Apply gradient if specified. The shader wrapper must be disposed here;
        // disposing the paint alone leaves the SKShader to the finalizer.
        using var gradientShader = CreateGradientShader(area, gradientOptions);
        if (gradientShader is not null)
        {
            darkPaint.Shader = gradientShader;
        }
        else
        {
            darkPaint.Color = fgColor;
        }

        // Draw regular modules (exclude finder patterns when a custom shape draws them).
        // Full-cell rectangles with antialiasing off touch exactly, so horizontal runs
        // of dark modules collapse into single rects, far fewer native draw calls.
        var skipFinderPatterns = finderPatternShape is not null;
        if (shape is RectangleModuleShape && moduleSizePercent == 1.0f)
        {
            DrawModuleRuns(canvas, new StandardQrMatrixView(data), area, darkPaint, skipFinderPatterns);
        }
        else
        {
            DrawModules(canvas, new StandardQrMatrixView(data), area, darkPaint, shape, moduleSizePercent, skipFinderPatterns);
        }

        // Draw finder patterns
        if (finderPatternShape is not null)
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
            if (finderPatternShape.RequiresAntialiasing)
            {
                darkPaint.IsAntialias = true;
                lightPaint.IsAntialias = true;
            }

            // total 3 finder patterns
            for (var i = 0; i < 3; i++)
            {
                var finderRect = GetFinderPatternRect(data, i, area);
                if (!requiresBackgroundRestore)
                {
                    finderPatternShape.Draw(canvas, finderRect, darkPaint, lightPaint);
                    continue;
                }

                var saveCount = canvas.SaveLayer(finderRect, null);
                try
                {
                    finderPatternShape.Draw(canvas, finderRect, darkPaint, lightPaint);
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
            var (iconRect, borderRect) = GetIconRects(data, area, iconData);
            iconData.Icon.Draw(canvas, iconRect, borderRect, bgColor);
        }
    }

    /// <summary>
    /// Render the specified Micro QR data into the given area of the target canvas.
    /// </summary>
    /// <remarks>
    /// Micro QR has a single finder pattern and no error-correction headroom for
    /// overlays, so the Standard QR options for icons and custom finder pattern
    /// shapes are intentionally not available. See <see cref="Render(SKCanvas, SKRect, QRCodeData, SKColor?, SKColor?, IconData?, ModuleShape?, float, GradientOptions?, FinderPatternShape?)"/>
    /// for the module-run merge behavior shared with Standard QR.
    /// </remarks>
    /// <param name="canvas">The canvas to render the Micro QR code on.</param>
    /// <param name="area">The rectangular area where the Micro QR code will be rendered.</param>
    /// <param name="data">The Micro QR code data to render.</param>
    /// <param name="codeColor">The color of the modules. If null, black is used.</param>
    /// <param name="backgroundColor">The background color. If null, white is used.</param>
    /// <param name="moduleShape">The shape to use for drawing modules. If null, rectangles are used.</param>
    /// <param name="moduleSizePercent">The size of each module as a percentage of the cell size (0.0 to 1.0). Default is 1.0 (no gap).</param>
    /// <param name="gradientOptions">Optional gradient options for the modules.</param>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public static void Render(
        SKCanvas canvas,
        SKRect area,
        MicroQRCodeData data,
        SKColor? codeColor,
        SKColor? backgroundColor,
        ModuleShape? moduleShape = null,
        float moduleSizePercent = 1.0f,
        GradientOptions? gradientOptions = null)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (moduleSizePercent is < 0f or > 1.0f)
            throw new ArgumentOutOfRangeException(nameof(moduleSizePercent), "Module size percent must be between 0.0 and 1.0.");

        var bgColor = backgroundColor ?? SKColors.White;
        var fgColor = codeColor ?? SKColors.Black;
        var shape = moduleShape ?? RectangleModuleShape.Default;

        // Draw the background at once
        using var lightPaint = new SKPaint() { Color = bgColor, Style = SKPaintStyle.Fill };
        canvas.DrawRect(area, lightPaint);

        // disable antialiasing as it causes gray border around each module.
        using var darkPaint = new SKPaint() { Style = SKPaintStyle.Fill, IsAntialias = shape.RequiresAntialiasing };

        // Apply gradient if specified. The shader wrapper must be disposed here;
        // disposing the paint alone leaves the SKShader to the finalizer.
        using var gradientShader = CreateGradientShader(area, gradientOptions);
        if (gradientShader is not null)
        {
            darkPaint.Shader = gradientShader;
        }
        else
        {
            darkPaint.Color = fgColor;
        }

        if (shape is RectangleModuleShape && moduleSizePercent == 1.0f)
        {
            DrawModuleRuns(canvas, new MicroQRMatrixView(data), area, darkPaint, skipFinderPatterns: false);
        }
        else
        {
            DrawModules(canvas, new MicroQRMatrixView(data), area, darkPaint, shape, moduleSizePercent, skipFinderPatterns: false);
        }
    }

    /// <summary>
    /// Renders an rMQR code onto the canvas. The rectangular symbol (quiet zone
    /// included) is drawn with a uniform module scale and centered in
    /// <paramref name="area"/> (letterbox); the whole area receives the background.
    /// </summary>
    /// <remarks>
    /// rMQR has one finder pattern and no error-correction headroom for overlays, so
    /// there are no icon or finder-shape options. Module runs are merged as for the
    /// other symbologies (see <see cref="Render(SKCanvas, SKRect, QRCodeData, SKColor?, SKColor?, IconData?, ModuleShape?, float, GradientOptions?, FinderPatternShape?)"/>).
    /// </remarks>
    /// <param name="canvas">The canvas to render the rMQR code on.</param>
    /// <param name="area">The rectangular area where the rMQR code will be rendered.</param>
    /// <param name="data">The rMQR code data to render.</param>
    /// <param name="codeColor">The color of the modules. If null, black is used.</param>
    /// <param name="backgroundColor">The background color. If null, white is used.</param>
    /// <param name="moduleShape">The shape to use for drawing modules. If null, rectangles are used.</param>
    /// <param name="moduleSizePercent">The size of each module as a percentage of the cell size (0.0 to 1.0). Default is 1.0 (no gap).</param>
    /// <param name="gradientOptions">Optional gradient options for the modules.</param>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public static void Render(
        SKCanvas canvas,
        SKRect area,
        RmQRCodeData data,
        SKColor? codeColor,
        SKColor? backgroundColor,
        ModuleShape? moduleShape = null,
        float moduleSizePercent = 1.0f,
        GradientOptions? gradientOptions = null)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (moduleSizePercent is < 0f or > 1.0f)
            throw new ArgumentOutOfRangeException(nameof(moduleSizePercent), "Module size percent must be between 0.0 and 1.0.");

        var bgColor = backgroundColor ?? SKColors.White;
        var fgColor = codeColor ?? SKColors.Black;
        var shape = moduleShape ?? RectangleModuleShape.Default;

        // Draw the background over the whole area at once
        using var lightPaint = new SKPaint() { Color = bgColor, Style = SKPaintStyle.Fill };
        canvas.DrawRect(area, lightPaint);

        // Letterbox: uniform module scale, symbol centered in the area.
        var symbolArea = GetLetterboxedArea(area, data.Width, data.Height);

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

        if (shape is RectangleModuleShape && moduleSizePercent == 1.0f)
        {
            DrawModuleRuns(canvas, new RmQRMatrixView(data), symbolArea, darkPaint, skipFinderPatterns: false);
        }
        else
        {
            DrawModules(canvas, new RmQRMatrixView(data), symbolArea, darkPaint, shape, moduleSizePercent, skipFinderPatterns: false);
        }
    }

    /// <summary>
    /// The largest rectangle with the matrix aspect ratio that fits inside
    /// <paramref name="area"/>, centered (uniform module scale, no stretch).
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
    /// Calculates icon and border rectangles for the given QR code area.
    /// </summary>
    /// <remarks>
    /// When <see cref="IconData.IconSizeModules"/> is set, sizing is module-based and percent/pixel values are ignored.
    /// Module-based icons are validated against QR size and core occupancy at render time.
    /// Icon rectangles are snapped to the module grid; even module sizes cannot be geometrically centered on an odd QR matrix.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public static (SKRect iconRect, SKRect borderRect) GetIconRects(QRCodeData data, SKRect area, IconData iconData)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (iconData is null)
            throw new ArgumentNullException(nameof(iconData));

        var centerX = area.Left + area.Width / 2;
        var centerY = area.Top + area.Height / 2;

        if (iconData.IconSizeModules is not null)
        {
            var iconSizeModules = iconData.IconSizeModules.Value;
            var iconBorderModules = iconData.IconBorderModules ?? 1;
            var maxCoreOccupancyPercent = iconData.MaxCoreOccupancyPercent;

            if (iconSizeModules < 1)
                throw new ArgumentOutOfRangeException(nameof(iconData), "Icon size modules must be at least 1.");
            if (iconBorderModules < 0)
                throw new ArgumentOutOfRangeException(nameof(iconData), "Icon border modules must be 0 or greater.");
            if (maxCoreOccupancyPercent is < 1 or > 100)
                throw new ArgumentOutOfRangeException(nameof(iconData), "Max core occupancy percent must be between 1 and 100.");

            var totalModules = iconSizeModules + (iconBorderModules * 2);
            var size = data.Size;
            var coreSize = data.GetCoreSize();
            var maxByCore = coreSize * maxCoreOccupancyPercent / 100;

            if (totalModules > size)
            {
                throw new InvalidOperationException(
                    $"Icon occupies {totalModules} modules, which exceeds QR matrix size {size}.");
            }

            if (totalModules > maxByCore)
            {
                throw new InvalidOperationException(
                    $"Icon occupies {totalModules} modules, but max allowed is {maxByCore} " +
                    $"(coreSize={coreSize}, maxCoreOccupancyPercent={maxCoreOccupancyPercent}).");
            }

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
            if (iconData.IconSizePercent is < 1 or > 100)
                throw new ArgumentOutOfRangeException(nameof(iconData), "Icon size percent must be between 1 and 100.");
            if (iconData.IconBorderWidth < 0)
                throw new ArgumentOutOfRangeException(nameof(iconData), "Icon border width must be 0 or greater.");

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
    /// Gets the rectangle area for a specified finder pattern in the rendered QR code area.
    /// </summary>
    /// <remarks>
    /// The finder patterns are the large squares typically located at three corners of a QR code.
    /// This method calculates their positions based on the QR code's size and quiet zone, ensuring accurate placement
    /// within the specified rendering area.
    /// </remarks>
    /// <param name="data">The QR code data containing module and layout information. Cannot be null.</param>
    /// <param name="patternIndex">Finder pattern index (0=top-left, 1=top-right, 2=bottom-left).</param>
    /// <param name="renderArea">The area within which the QR code is rendered. The finder pattern rectangle is calculated relative to this area.</param>
    /// <returns>An SKRect representing the position and size of the specified finder pattern within the rendering area.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="data"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="patternIndex"/> is less than 0 or greater than 2.</exception>
    public static SKRect GetFinderPatternRect(QRCodeData data, int patternIndex, SKRect renderArea)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (patternIndex is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(patternIndex), "Pattern index must be 0 (top-left), 1 (top-right), or 2 (bottom-left).");

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
    /// Draws dark modules as merged horizontal runs of full-cell rectangles.
    /// Only valid for <see cref="RectangleModuleShape"/> at 100% module size, where
    /// adjacent modules share edges, antialiasing is always off
    /// (<see cref="RectangleModuleShape.RequiresAntialiasing"/> is false), and
    /// merging is pixel-identical to per-module drawing. The parity holds under
    /// axis-preserving canvas transforms (translation/scale); rotated canvases may
    /// rasterize shared edges hairline-differently at sub-pixel level, inherent to
    /// non-axis-aligned rasterization, which affects per-module drawing between
    /// adjacent modules just the same.
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
        return SKShader.CreateLinearGradient(start, end, gradientOptions.Colors, gradientOptions.ColorPositions, SKShaderTileMode.Clamp);
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
