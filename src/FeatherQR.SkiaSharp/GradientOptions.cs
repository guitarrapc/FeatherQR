using SkiaSharp;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Gradient configuration for symbol rendering.
/// </summary>
/// <remarks>
/// <para>
/// Configures a linear gradient applied across the whole symbol, flowing in one of the
/// directions of <see cref="GradientDirection"/>.
/// </para>
/// <para>
/// For a simple two-colour gradient pass two colours; for more, add colours and
/// optionally the stops they sit at.
/// </para>
/// <para>
/// An instance is immutable through its API: the constructor copies the arrays it is
/// given and no member hands one back, so ordinary code cannot repaint a shared instance
/// such as <see cref="Default"/> for everyone else. The colours are reachable through
/// <see cref="System.Runtime.InteropServices.MemoryMarshal"/>, which takes a writable
/// reference out of any span; that is a deliberate escape hatch of the runtime, not a
/// supported way to edit these options.
/// </para>
/// <para>
/// A <c>with</c> expression varies the direction of an existing gradient —
/// <c>GradientOptions.Default with { Direction = GradientDirection.BottomToTop }</c> —
/// and the copy shares the colour array with the original, which is safe for the same
/// reason: neither instance exposes it.
/// </para>
/// <para>
/// <see cref="Direction"/> is the only <c>init</c> member, which is a rule rather than an
/// omission: <see cref="Colors"/> and <see cref="ColorPositions"/> have to agree in
/// length, and <c>with</c> sets one member at a time, so allowing
/// <c>with { Colors = … }</c> would let a caller build a gradient whose stops no longer
/// match its colours and only find out when it is drawn. The pair is therefore set
/// together, through the constructor, where the mismatch is refused immediately.
/// </para>
/// </remarks>
public sealed record class GradientOptions
{
    private readonly SKColor[] _colors;
    private readonly float[] _colorPositions;

    /// <summary>
    /// A ready-made gradient: dark orange to firebrick, running from the top-left corner
    /// to the bottom-right. Use it when you want a gradient without choosing colors.
    /// </summary>
    public static readonly GradientOptions Default = new([SKColors.DarkOrange, SKColors.Firebrick], GradientDirection.TopLeftToBottomRight);

    /// <summary>
    /// Initializes a new instance of <see cref="GradientOptions"/>. Both spans are copied,
    /// so the caller keeps ownership of whatever it passed.
    /// </summary>
    /// <remarks>
    /// The parameters take the same shapes <see cref="Colors"/> and
    /// <see cref="ColorPositions"/> hand back, so an existing gradient can be rebuilt with
    /// one change and nothing else translated:
    /// <code>
    /// var recolored = new GradientOptions([SKColors.Red, SKColors.Blue], options.Direction, options.ColorPositions);
    /// </code>
    /// An array converts implicitly, so <c>new GradientOptions(myColors)</c> still works.
    /// </remarks>
    /// <param name="colors">Gradient colors. At least 2 are required.</param>
    /// <param name="direction">Gradient direction.</param>
    /// <param name="colorPositions">Color stop positions (0.0 to 1.0); leave it empty to distribute the colors evenly.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="colors"/> has fewer than 2 elements, or when <paramref name="colorPositions"/> is non-empty and of a different length.</exception>
    public GradientOptions(ReadOnlySpan<SKColor> colors, GradientDirection direction = GradientDirection.LeftToRight, ReadOnlySpan<float> colorPositions = default)
    {
        if (colors.Length < 2)
            throw new ArgumentException("At least 2 colors are required for gradient", nameof(colors));
        if (!colorPositions.IsEmpty && colorPositions.Length != colors.Length)
            throw new ArgumentException("Color positions length must match colors length", nameof(colorPositions));

        _colors = colors.ToArray();
        _colorPositions = colorPositions.IsEmpty ? [] : colorPositions.ToArray();
        Direction = direction;
    }

    /// <summary>
    /// Gradient colors, in flow order.
    /// </summary>
    /// <remarks>
    /// At least 2 colors. The gradient flows from the first color to the last in the
    /// direction given by <see cref="Direction"/>.
    /// </remarks>
    public ReadOnlySpan<SKColor> Colors => _colors;

    /// <summary>
    /// The gradient direction.
    /// </summary>
    /// <remarks>
    /// Determines the start and end points of the gradient across the symbol area. This is
    /// the member a <c>with</c> expression varies; the colours are chosen at construction.
    /// </remarks>
    public GradientDirection Direction { get; init; }

    /// <summary>
    /// Color stops (0.0 to 1.0), or empty when the colors are evenly distributed.
    /// </summary>
    /// <remarks>
    /// When non-empty the length matches <see cref="Colors"/>, and the values ascend from
    /// 0.0 (start) to 1.0 (end): <c>[0.0f, 0.3f, 1.0f]</c> for three colors.
    /// </remarks>
    public ReadOnlySpan<float> ColorPositions => _colorPositions;

    // No WithColors / WithColorPositions wither here, deliberately. One was written and
    // dropped: the only thing it bought over `new GradientOptions(colors, o.Direction,
    // o.ColorPositions)` was carrying those two arguments for the caller, since a colour
    // count the stops cannot match throws either way. Nothing in this repository — the
    // Playground, the samples, the tests — ever recolours an existing gradient; they all
    // build one from scratch. The need came from explaining what replaced
    // `with { Colors = … }` in the migration guide, and that guide now shows the
    // constructor, which reads in one line. Add the wither when someone asks for it, with
    // a use case attached.
    //
    // The Skia shader factory takes arrays, and these are the copies nobody else holds.
    internal SKColor[] ColorArray => _colors;
    internal float[]? ColorPositionArray => _colorPositions.Length == 0 ? null : _colorPositions;

    /// <inheritdoc/>
    public bool Equals(GradientOptions? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Direction == other.Direction
            && _colors.AsSpan().SequenceEqual(other._colors)
            && _colorPositions.AsSpan().SequenceEqual(other._colorPositions);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // Arrays hash by reference, so the elements are folded in by hand; two gradients
        // that compare equal have to hash equal. FNV-1a, because System.HashCode is not
        // available on every target framework here.
        unchecked
        {
            var hash = (int)2166136261;
            hash = (hash ^ (int)Direction) * 16777619;
            foreach (var color in _colors)
                hash = (hash ^ (int)(uint)color) * 16777619;
            foreach (var position in _colorPositions)
                hash = (hash ^ position.GetHashCode()) * 16777619;
            return hash;
        }
    }

    /// <summary>
    /// The record printer, written by hand: the generated one would print a
    /// <see cref="ReadOnlySpan{T}"/> member as its type name, and would have to compile
    /// against a ref struct on every target framework.
    /// </summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(_colors.Length).Append(" colors, Direction = ").Append(Direction);
        if (_colorPositions.Length != 0)
            builder.Append(", ").Append(_colorPositions.Length).Append(" stops");
        return true;
    }
}

/// <summary>
/// Defines the direction of gradient flow for symbol rendering.
/// </summary>
/// <remarks>
/// <para>
/// Linear gradients flow in a straight line across the entire symbol area.
/// The direction determines the start and end points of the gradient.
/// </para>
/// <para>
/// <b>Common Use Cases:</b>
/// </para>
/// <list type="bullet">
/// <item><description><see cref="LeftToRight"/> or <see cref="TopToBottom"/>: Simple horizontal/vertical gradients</description></item>
/// <item><description><see cref="TopLeftToBottomRight"/>: Diagonal gradients for dynamic appearance</description></item>
/// <item><description><see cref="None"/>: Use when solid color is desired (disables gradient)</description></item>
/// </list>
/// </remarks>
public enum GradientDirection
{
    /// <summary>
    /// No gradient. Use solid color specified in symbol rendering options.
    /// However, if solid is required, it's better to omit gradient options entirely.
    /// </summary>
    None = 0,
    /// <summary>
    /// Gradient flows from left edge to right edge horizontally.
    /// </summary>
    LeftToRight,
    /// <summary>
    /// Gradient flows from right edge to left edge horizontally.
    /// </summary>
    RightToLeft,
    /// <summary>
    /// Gradient flows from top edge to bottom edge vertically.
    /// </summary>
    TopToBottom,
    /// <summary>
    /// Gradient flows from bottom edge to top edge vertically.
    /// </summary>
    BottomToTop,
    /// <summary>
    /// Gradient flows diagonally from top-left corner to bottom-right corner.
    /// </summary>
    TopLeftToBottomRight,
    /// <summary>
    /// Gradient flows diagonally from top-right corner to bottom-left corner.
    /// </summary>
    TopRightToBottomLeft,
    /// <summary>
    /// Gradient flows diagonally from bottom-left corner to top-right corner.
    /// </summary>
    BottomLeftToTopRight,
    /// <summary>
    /// Gradient flows diagonally from bottom-right corner to top-left corner.
    /// </summary>
    BottomRightToTopLeft,
}
