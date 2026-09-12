using SkiaSharp;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// How the finder patterns, the large squares in the corners, are drawn: three of them on Standard QR, one on Micro QR and rMQR.
/// </summary>
/// <remarks>
/// A shape draws the dark modules only. The light ring between them is left undrawn, so whatever the renderer put beneath the finder pattern, the background at any alpha, shows through it: nothing is painted over and nothing is erased, which is what lets raster and SVG output carry the same drawing.
/// </remarks>
public abstract class FinderPatternShape
{
    /// <summary>
    /// Gets whether this shape requires antialiasing for smooth rendering.
    /// Curved shapes such as circles and rounded rectangles should return <see langword="true"/>; straight-edged shapes such as rectangles can return <see langword="false"/>.
    /// </summary>
    public virtual bool RequiresAntialiasing => false;

    /// <summary>
    /// Draws the dark modules of a finder pattern. The light ring is left undrawn.
    /// </summary>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="rect">Where to draw it: the 7 by 7 module area.</param>
    /// <param name="paint">The paint for dark modules. The renderer owns it and may reuse it across calls; do not modify or dispose it.</param>
    public abstract void Draw(SKCanvas canvas, SKRect rect, SKPaint paint);
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
        // Per axis: every renderer path hands this a square, rMQR's letterbox included, since one scale
        // sizes both axes there. A caller drawing into a non-square rect still needs the rings on the
        // same grid as the modules around them.
        var moduleWidth = rect.Width / 7f;
        var moduleHeight = rect.Height / 7f;

        // The 5×5 hole. Its edges are what the outer ring's bands meet, so the ring lands on the same
        // pixels whether drawn as bands or, as it once was, as a light square over a dark one.
        var innerRect = SKRect.Create(
            rect.Left + moduleWidth,
            rect.Top + moduleHeight,
            moduleWidth * 5,
            moduleHeight * 5);

        // Outer ring (7×7) as four bands around the hole, rectangles so the SVG stays rectangles:
        // top and bottom full width, the two sides between them.
        canvas.DrawRect(new SKRect(rect.Left, rect.Top, rect.Right, innerRect.Top), paint);
        canvas.DrawRect(new SKRect(rect.Left, innerRect.Bottom, rect.Right, rect.Bottom), paint);
        canvas.DrawRect(new SKRect(rect.Left, innerRect.Top, innerRect.Left, innerRect.Bottom), paint);
        canvas.DrawRect(new SKRect(innerRect.Right, innerRect.Top, rect.Right, innerRect.Bottom), paint);

        // Center (3×3)
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
        // Ovals inscribed in the ring rects rather than circles: on a square area they are the
        // same circles as before, and on a non-square one they keep following the module grid.
        var moduleWidth = rect.Width / 7f;
        var moduleHeight = rect.Height / 7f;

        // One even-odd path: the outer oval (7x7) with the ring oval (5x5) as a hole, and the
        // center oval (3x3) filled again inside it.
        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        path.AddOval(rect);
        path.AddOval(SKRect.Create(rect.Left + moduleWidth, rect.Top + moduleHeight, moduleWidth * 5, moduleHeight * 5));
        path.AddOval(SKRect.Create(rect.Left + moduleWidth * 2, rect.Top + moduleHeight * 2, moduleWidth * 3, moduleHeight * 3));
        canvas.DrawPath(path, paint);
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
        // Per axis, so the rings follow the module grid when the cells are not square.
        var moduleWidth = rect.Width / 7f;
        var moduleHeight = rect.Height / 7f;
        var radius = Math.Min(rect.Width, rect.Height) * _cornerRadiusPercent;

        // One even-odd path: the outer rounded rectangle (7×7) with the ring (5×5) as a hole, and
        // the center (3×3) filled again inside it.
        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        path.AddRoundRect(rect, radius, radius);
        path.AddRoundRect(SKRect.Create(rect.Left + moduleWidth, rect.Top + moduleHeight, moduleWidth * 5, moduleHeight * 5), radius * 0.8f, radius * 0.8f);
        path.AddRoundRect(SKRect.Create(rect.Left + moduleWidth * 2, rect.Top + moduleHeight * 2, moduleWidth * 3, moduleHeight * 3), radius * 0.6f, radius * 0.6f);
        canvas.DrawPath(path, paint);
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
        // Per axis, so the rings follow the module grid when the cells are not square.
        var moduleWidth = rect.Width / 7f;
        var moduleHeight = rect.Height / 7f;

        // Corner radius for rounded rectangle
        var cornerRadius = Math.Min(rect.Width, rect.Height) * _cornerRadiusPercent;

        // One even-odd path: the outer rounded rectangle (7×7) with the ring (5×5) as a hole, and
        // the center (3×3) filled again inside it, an oval so it stays on the grid; a circle on a square area.
        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        path.AddRoundRect(rect, cornerRadius, cornerRadius);
        path.AddRoundRect(SKRect.Create(rect.Left + moduleWidth, rect.Top + moduleHeight, moduleWidth * 5, moduleHeight * 5), cornerRadius * 0.7f, cornerRadius * 0.7f);
        path.AddOval(SKRect.Create(rect.Left + moduleWidth * 2, rect.Top + moduleHeight * 2, moduleWidth * 3, moduleHeight * 3));
        canvas.DrawPath(path, paint);
    }
}
