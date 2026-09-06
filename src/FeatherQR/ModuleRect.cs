namespace FeatherQR;

/// <summary>
/// An axis-aligned rectangle of dark modules, in module coordinates.
/// Produced by the <c>GetModuleRectangles</c> family on <see cref="QRCodeData"/>, <see cref="MicroQRCodeData"/>, and <see cref="RmQRCodeData"/>.
/// </summary>
/// <param name="X">Column of the left edge (0-based, including the quiet zone if present).</param>
/// <param name="Y">Row of the top edge (0-based, including the quiet zone if present).</param>
/// <param name="Width">Width in modules (always positive).</param>
/// <param name="Height">Height in modules (always positive).</param>
/// <remarks>
/// <para>
/// Coordinates use the same space as the matrix indexer: one unit is one module, origin at the top-left corner of the symbol including its quiet zone, X growing right and Y growing down.
/// The module at column <c>c</c>, row <c>r</c> read via <c>data[r, c]</c> corresponds to the unit rectangle at <c>X = c</c>, <c>Y = r</c>.
/// Renderers map to pixels by multiplying all four values by the pixel size of one module.
/// </para>
/// <para>
/// No scale or pixel size is baked in, so the same value works for SVG path data (viewBox in module units), draw calls, and any other coordinate transform the consumer applies.
/// </para>
/// </remarks>
public readonly record struct ModuleRect(int X, int Y, int Width, int Height);
