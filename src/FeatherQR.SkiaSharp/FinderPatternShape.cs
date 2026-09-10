using SkiaSharp;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// How the finder patterns, the large squares in the corners, are drawn: three of them on Standard QR, one on Micro QR and rMQR.
/// </summary>
public abstract class FinderPatternShape
{
    /// <summary>
    /// Gets whether this shape requires antialiasing for smooth rendering.
    /// Curved shapes such as circles and rounded rectangles should return <see langword="true"/>; straight-edged shapes such as rectangles can return <see langword="false"/>.
    /// </summary>
    public virtual bool RequiresAntialiasing => false;

    /// <summary>
    /// Draws a finder pattern.
    /// </summary>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="rect">Where to draw it: the 7 by 7 module area.</param>
    /// <param name="paint">The paint to draw with.</param>
    public abstract void Draw(SKCanvas canvas, SKRect rect, SKPaint paint);

    /// <summary>
    /// Draws a finder pattern, coloring its light modules too.
    /// </summary>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="rect">Where to draw it: the 7 by 7 module area.</param>
    /// <param name="paint">The paint for dark modules.</param>
    /// <param name="backgroundColor">The color for light modules.</param>
    public virtual void Draw(SKCanvas canvas, SKRect rect, SKPaint paint, SKColor backgroundColor)
    {
        Draw(canvas, rect, paint);
    }

    /// <summary>
    /// Draws a finder pattern using the paint the renderer already holds for light modules.
    /// </summary>
    /// <remarks>
    /// The default implementation preserves compatibility with custom finder shapes that override the color-based overload.
    /// Built-in shapes override this overload so the renderer can reuse its background paint.
    /// The renderer may set the paint's blend mode to <see cref="SKBlendMode.Clear"/> while drawing on an isolated layer so transparent and translucent light modules reveal the background rendered beneath the finder pattern.
    /// Implementations should therefore draw with this paint directly instead of copying only its color.
    /// The renderer owns <paramref name="backgroundPaint"/> and may reuse it across calls; implementations must not modify or dispose it.
    /// Custom shapes should override this overload when they need to reuse the renderer's configured background paint or when they must support non-opaque backgrounds (blend modes).
    /// Existing custom shapes may continue to override <see cref="Draw(SKCanvas, SKRect, SKPaint, SKColor)"/>, but that overload cannot use the renderer's blend mode.
    /// </remarks>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="rect">Where to draw it: the 7 by 7 module area.</param>
    /// <param name="paint">The paint for dark modules.</param>
    /// <param name="backgroundPaint">The renderer-owned paint to use for drawing light modules. Do not modify or dispose it.</param>
    public virtual void Draw(SKCanvas canvas, SKRect rect, SKPaint paint, SKPaint backgroundPaint)
    {
        Draw(canvas, rect, paint, backgroundPaint.Color);
    }
}

/// <summary>
/// The standard finder pattern: three nested squares.
/// </summary>
public sealed class RectangleFinderPatternShape : FinderPatternShape
{
    /// <summary>
    /// A ready-made instance to use as-is.
    /// </summary>
    public static readonly RectangleFinderPatternShape Default = new();

    // Enforce singleton pattern
    private RectangleFinderPatternShape() { }

    /// <summary>
    /// Off: straight edges render cleanly without it.
    /// </summary>
    public override bool RequiresAntialiasing => false;

    /// <inheritdoc/>
    public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint)
    {
        Draw(canvas, rect, paint, SKColors.White);
    }

    /// <inheritdoc/>
    public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint, SKColor backgroundColor)
    {
        using var backgroundPaint = new SKPaint { Color = backgroundColor, Style = SKPaintStyle.Fill, IsAntialias = paint.IsAntialias };
        Draw(canvas, rect, paint, backgroundPaint);
    }

    /// <inheritdoc/>
    public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint, SKPaint backgroundPaint)
    {
        // Per axis: a canvas that is not square gives the symbol non-square cells, and the rings
        // have to land on the same grid as the modules around them.
        var moduleWidth = rect.Width / 7f;
        var moduleHeight = rect.Height / 7f;

        // Draw outer ring (7×7)
        canvas.DrawRect(rect, paint);

        // Draw ring (5×5)
        var innerRect = SKRect.Create(
            rect.Left + moduleWidth,
            rect.Top + moduleHeight,
            moduleWidth * 5,
            moduleHeight * 5);
        canvas.DrawRect(innerRect, backgroundPaint);

        // Draw black center (3×3)
        var centerRect = SKRect.Create(
            rect.Left + moduleWidth * 2,
            rect.Top + moduleHeight * 2,
            moduleWidth * 3,
            moduleHeight * 3);
        canvas.DrawRect(centerRect, paint);
    }
}

/// <summary>
/// Three nested ovals, circles on a square area.
/// </summary>
public sealed class CircleFinderPatternShape : FinderPatternShape
{
    /// <summary>
    /// A ready-made instance to use as-is.
    /// </summary>
    public static readonly CircleFinderPatternShape Default = new();

    // Enforce singleton pattern
    private CircleFinderPatternShape() { }

    /// <summary>
    /// On, so the curves do not come out jagged.
    /// </summary>
    public override bool RequiresAntialiasing => true;

    /// <inheritdoc/>
    public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint)
    {
        Draw(canvas, rect, paint, SKColors.White);
    }

    /// <inheritdoc/>
    public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint, SKColor backgroundColor)
    {
        using var backgroundPaint = new SKPaint { Color = backgroundColor, Style = SKPaintStyle.Fill, IsAntialias = paint.IsAntialias };
        Draw(canvas, rect, paint, backgroundPaint);
    }

    /// <inheritdoc/>
    public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint, SKPaint backgroundPaint)
    {
        // Ovals inscribed in the ring rects rather than circles: on a square area they are the
        // same circles as before, and on a non-square one they keep following the module grid.
        var moduleWidth = rect.Width / 7f;
        var moduleHeight = rect.Height / 7f;

        // Draw outer ring (7x7)
        canvas.DrawOval(rect, paint);

        // Draw ring (5x5)
        canvas.DrawOval(SKRect.Create(rect.Left + moduleWidth, rect.Top + moduleHeight, moduleWidth * 5, moduleHeight * 5), backgroundPaint);

        // Draw black center (3x3)
        canvas.DrawOval(SKRect.Create(rect.Left + moduleWidth * 2, rect.Top + moduleHeight * 2, moduleWidth * 3, moduleHeight * 3), paint);
    }
}

/// <summary>
/// Three nested rounded rectangles.
/// </summary>
public sealed class RoundedRectangleFinderPatternShape : FinderPatternShape
{
    /// <summary>
    /// A ready-made instance to use as-is.
    /// </summary>
    public static readonly RoundedRectangleFinderPatternShape Default = new();

    private readonly float _cornerRadiusPercent;

    /// <summary>
    /// Creates the shape with a corner radius of your own.
    /// </summary>
    /// <param name="cornerRadiusPercent">The corner radius as a fraction of the whole pattern, which is seven modules across, 0.0 to 1.0.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the radius is outside 0.0 to 1.0.</exception>
    public RoundedRectangleFinderPatternShape(float cornerRadiusPercent = 0.2f)
    {
        if (cornerRadiusPercent < 0 || cornerRadiusPercent > 1)
            throw new ArgumentOutOfRangeException(nameof(cornerRadiusPercent), "Corner radius percent must be between 0 and 1.");
        _cornerRadiusPercent = cornerRadiusPercent;
    }

    /// <summary>
    /// On, so the curves do not come out jagged.
    /// </summary>
    public override bool RequiresAntialiasing => true;

    /// <inheritdoc/>
    public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint)
    {
        Draw(canvas, rect, paint, SKColors.White);
    }

    /// <inheritdoc/>
    public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint, SKColor backgroundColor)
    {
        using var backgroundPaint = new SKPaint { Color = backgroundColor, Style = SKPaintStyle.Fill, IsAntialias = paint.IsAntialias };
        Draw(canvas, rect, paint, backgroundPaint);
    }

    /// <inheritdoc/>
    public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint, SKPaint backgroundPaint)
    {
        // Per axis, so the rings follow the module grid when the cells are not square.
        var moduleWidth = rect.Width / 7f;
        var moduleHeight = rect.Height / 7f;
        var radius = Math.Min(rect.Width, rect.Height) * _cornerRadiusPercent;

        // Draw outer rounded rectangle (7×7)
        canvas.DrawRoundRect(rect, radius, radius, paint);

        // Draw ring (5×5)
        var innerRect = SKRect.Create(
            rect.Left + moduleWidth,
            rect.Top + moduleHeight,
            moduleWidth * 5,
            moduleHeight * 5);
        canvas.DrawRoundRect(innerRect, radius * 0.8f, radius * 0.8f, backgroundPaint);

        // Draw black center (3×3)
        var centerRect = SKRect.Create(
            rect.Left + moduleWidth * 2,
            rect.Top + moduleHeight * 2,
            moduleWidth * 3,
            moduleHeight * 3);
        canvas.DrawRoundRect(centerRect, radius * 0.6f, radius * 0.6f, paint);
    }
}

/// <summary>
/// Two nested rounded rectangles around an oval, a circle on a square area.
/// </summary>
public sealed class RoundedRectangleCircleFinderPatternShape : FinderPatternShape
{
    /// <summary>
    /// A ready-made instance to use as-is.
    /// </summary>
    public static readonly RoundedRectangleCircleFinderPatternShape Default = new();

    private readonly float _cornerRadiusPercent;

    /// <summary>
    /// Creates the shape with a corner radius of your own.
    /// </summary>
    /// <param name="cornerRadiusPercent">The corner radius as a fraction of the whole pattern, which is seven modules across, 0.0 to 1.0.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the radius is outside 0.0 to 1.0.</exception>
    public RoundedRectangleCircleFinderPatternShape(float cornerRadiusPercent = 0.3f)
    {
        if (cornerRadiusPercent < 0 || cornerRadiusPercent > 1)
            throw new ArgumentOutOfRangeException(nameof(cornerRadiusPercent), "Corner radius percent must be between 0 and 1.");
        _cornerRadiusPercent = cornerRadiusPercent;
    }

    /// <summary>
    /// On, so the curves do not come out jagged.
    /// </summary>
    public override bool RequiresAntialiasing => true;

    /// <inheritdoc/>
    public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint)
    {
        Draw(canvas, rect, paint, SKColors.White);
    }

    /// <inheritdoc/>
    public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint, SKColor backgroundColor)
    {
        using var backgroundPaint = new SKPaint { Color = backgroundColor, Style = SKPaintStyle.Fill, IsAntialias = paint.IsAntialias };
        Draw(canvas, rect, paint, backgroundPaint);
    }

    /// <inheritdoc/>
    public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint, SKPaint backgroundPaint)
    {
        // Per axis, so the rings follow the module grid when the cells are not square.
        var moduleWidth = rect.Width / 7f;
        var moduleHeight = rect.Height / 7f;

        // Corner radius for rounded rectangle
        var cornerRadius = Math.Min(rect.Width, rect.Height) * _cornerRadiusPercent;

        // Draw outer rounded rectangle (7×7)
        canvas.DrawRoundRect(rect, cornerRadius, cornerRadius, paint);

        // Draw ring (5×5)
        var innerRect = SKRect.Create(
            rect.Left + moduleWidth,
            rect.Top + moduleHeight,
            moduleWidth * 5,
            moduleHeight * 5);
        canvas.DrawRoundRect(innerRect, cornerRadius * 0.7f, cornerRadius * 0.7f, backgroundPaint);

        // Draw black center (3×3), an oval so it stays on the grid; a circle on a square area.
        canvas.DrawOval(SKRect.Create(rect.Left + moduleWidth * 2, rect.Top + moduleHeight * 2, moduleWidth * 3, moduleHeight * 3), paint);
    }
}
