using SkiaSharp;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// How the finder patterns, the large squares in the corners, are drawn: three of them on Standard QR, one on Micro QR and rMQR.
/// </summary>
/// <remarks>
/// A shape draws the dark modules only. The light ring between them is left undrawn, so whatever the renderer put beneath the finder pattern, the background at any alpha, shows through it: nothing is painted over and nothing is erased, which is what lets raster and SVG output carry the same drawing.
/// The ring has to survive as a continuous light run: a reader locates the symbol by scanning for the finder pattern's 1:1:3:1:1 run of dark and light, so a shape that fills it, a plain <see cref="SKCanvas.DrawRect(SKRect, SKPaint)"/> over the whole area included, renders a symbol nothing can scan.
/// </remarks>
public abstract class FinderPatternShape
{
    /// <summary>
    /// Whether the shape needs antialiasing. It decides the finder pattern's antialiasing on its own, so <see langword="false"/> turns antialiasing off even where the module shape asked for it.
    /// Return <see langword="true"/> for curves, <see langword="false"/> for straight edges.
    /// </summary>
    public abstract bool RequiresAntialiasing { get; }

    /// <summary>
    /// Draws the dark modules of a finder pattern. The light ring is left undrawn.
    /// </summary>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="rect">Where to draw it: the 7 by 7 module area. It may not be square, so size the rings per axis.</param>
    /// <param name="paint">The paint for dark modules. It may carry a gradient shader, so draw with it rather than copying its <see cref="SKPaint.Color"/>. The renderer owns it and may reuse it across calls: do not dispose it, and restore any property you change before returning.</param>
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

        // The bands below share edges, and antialiased edges do not composite back to opaque: a
        // caller's antialiased paint would draw a grey seam across the ring. The renderer already
        // asks this shape for straight edges, so hold the paint to it and put it back.
        var wasAntialias = paint.IsAntialias;
        paint.IsAntialias = false;

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

        paint.IsAntialias = wasAntialias;
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

        // The center (3x3), drawn on its own either way: SKSvgCanvas writes an oval as one
        // <ellipse> and a path as thousands of flattened quadratics, so keeping it out of the
        // path costs nothing and saves 5 KB of document per finder.
        var centerRect = SKRect.Create(rect.Left + moduleWidth * 2, rect.Top + moduleHeight * 2, moduleWidth * 3, moduleHeight * 3);

        // A circle's offset curve is another circle, so on a square area the ring between the 5x5
        // and 7x7 ovals is exactly a one-module stroke of the 6x6 one, and that is an <ellipse>
        // too. Not so when the cells are not square: one stroke width cannot be one module on both
        // axes, and the ring would spill past the short edges, so those fall back to the path.
        // The tolerance is for the fit's own rounding, which leaves two of a Standard QR's three
        // finders a few float ulps off square at most canvas sizes; a ring a ten-thousandth of a
        // module out is not a ring anyone can see, and a real aspect difference is orders larger.
        if (Math.Abs(moduleWidth - moduleHeight) <= 1e-4f * Math.Max(moduleWidth, moduleHeight))
        {
            var style = paint.Style;
            var strokeWidth = paint.StrokeWidth;
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = moduleWidth;
            canvas.DrawOval(SKRect.Create(rect.Left + moduleWidth * 0.5f, rect.Top + moduleHeight * 0.5f, moduleWidth * 6, moduleHeight * 6), paint);
            paint.Style = style;
            paint.StrokeWidth = strokeWidth;

            canvas.DrawOval(centerRect, paint);
            return;
        }

        // One even-odd path: the outer oval (7x7) with the ring oval (5x5) as a hole.
        using var builder = new SKPathBuilder { FillType = SKPathFillType.EvenOdd };
        builder.AddOval(rect);
        builder.AddOval(SKRect.Create(rect.Left + moduleWidth, rect.Top + moduleHeight, moduleWidth * 5, moduleHeight * 5));
        using var path = builder.Detach();
        canvas.DrawPath(path, paint);
        canvas.DrawOval(centerRect, paint);
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
        using var builder = new SKPathBuilder { FillType = SKPathFillType.EvenOdd };
        builder.AddRoundRect(rect, radius, radius);
        builder.AddRoundRect(SKRect.Create(rect.Left + moduleWidth, rect.Top + moduleHeight, moduleWidth * 5, moduleHeight * 5), radius * 0.8f, radius * 0.8f);
        builder.AddRoundRect(SKRect.Create(rect.Left + moduleWidth * 2, rect.Top + moduleHeight * 2, moduleWidth * 3, moduleHeight * 3), radius * 0.6f, radius * 0.6f);
        using var path = builder.Detach();
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

        // One even-odd path: the outer rounded rectangle (7×7) with the ring (5×5) as a hole.
        using var builder = new SKPathBuilder { FillType = SKPathFillType.EvenOdd };
        builder.AddRoundRect(rect, cornerRadius, cornerRadius);
        builder.AddRoundRect(SKRect.Create(rect.Left + moduleWidth, rect.Top + moduleHeight, moduleWidth * 5, moduleHeight * 5), cornerRadius * 0.7f, cornerRadius * 0.7f);
        using var path = builder.Detach();
        canvas.DrawPath(path, paint);

        // The center (3×3), an oval so it stays on the grid; a circle on a square area. Drawn on
        // its own rather than as a third contour: it sits strictly inside the hole, so the picture
        // is the same, and SKSvgCanvas writes it as one <ellipse> instead of flattening it into
        // the path's quadratics, which is 5 KB of document per finder.
        canvas.DrawOval(SKRect.Create(rect.Left + moduleWidth * 2, rect.Top + moduleHeight * 2, moduleWidth * 3, moduleHeight * 3), paint);
    }
}
