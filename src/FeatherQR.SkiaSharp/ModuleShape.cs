using SkiaSharp;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// How the individual modules of a symbol are drawn.
/// </summary>
public abstract class ModuleShape
{
    /// <summary>
    /// Whether the shape needs antialiasing.
    /// Return <see langword="true"/> for curves, <see langword="false"/> for straight edges.
    /// </summary>
    public abstract bool RequiresAntialiasing { get; }

    /// <summary>
    /// Draws one module.
    /// </summary>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="rect">Where to draw it.</param>
    /// <param name="paint">The paint to draw with.</param>
    public abstract void Draw(SKCanvas canvas, SKRect rect, SKPaint paint);
}

/// <summary>
/// Draws modules as squares, the standard look.
/// </summary>
public sealed class RectangleModuleShape : ModuleShape
{
    /// <summary>
    /// A ready-made instance to use as-is.
    /// </summary>
    public static readonly RectangleModuleShape Default = new();

    /// <summary>
    /// Off, so neighbouring modules do not show a gray seam between them.
    /// </summary>
    public override bool RequiresAntialiasing => false;

    // Enforce singleton pattern
    private RectangleModuleShape() { }

    /// <inheritdoc/>
    public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint)
    {
        canvas.DrawRect(rect, paint);
    }
}

/// <summary>
/// Draws modules as circles.
/// </summary>
public sealed class CircleModuleShape : ModuleShape
{
    /// <summary>
    /// A ready-made instance to use as-is.
    /// </summary>
    public static readonly CircleModuleShape Default = new();

    /// <summary>
    /// On, so the curves do not come out jagged.
    /// </summary>
    public override bool RequiresAntialiasing => true;

    // Enforce singleton pattern
    private CircleModuleShape() { }

    /// <inheritdoc/>
    public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint)
    {
        var radius = Math.Min(rect.Width, rect.Height) / 2;
        var centerX = rect.MidX;
        var centerY = rect.MidY;
        canvas.DrawCircle(centerX, centerY, radius, paint);
    }
}

/// <summary>
/// Draws modules as rounded squares.
/// </summary>
public sealed class RoundedRectangleModuleShape : ModuleShape
{
    /// <summary>
    /// A ready-made instance to use as-is.
    /// </summary>
    public static readonly RoundedRectangleModuleShape Default = new(0.3f);

    // Gets the corner radius as a percentage of the smaller dimension (width or height).
    private readonly float _cornerRadiusPercent;

    /// <summary>
    /// On, so the curves do not come out jagged.
    /// </summary>
    public override bool RequiresAntialiasing => true;

    /// <summary>
    /// Creates the shape with a corner radius of your own.
    /// </summary>
    /// <param name="cornerRadiusPercent">The radius as a fraction of the module size, 0.0 to 1.0.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the radius is outside 0.0 to 1.0.</exception>
    public RoundedRectangleModuleShape(float cornerRadiusPercent = 0.3f)
    {
        if (cornerRadiusPercent < 0 || cornerRadiusPercent > 1)
            throw new ArgumentOutOfRangeException(nameof(cornerRadiusPercent), "Corner radius percent must be between 0 and 1.");

        _cornerRadiusPercent = cornerRadiusPercent;
    }

    /// <inheritdoc/>
    public override void Draw(SKCanvas canvas, SKRect rect, SKPaint paint)
    {
        var radius = Math.Min(rect.Width, rect.Height) * _cornerRadiusPercent;
        canvas.DrawRoundRect(rect, radius, radius, paint);
    }
}
