namespace FeatherQR;

/// <summary>
/// The shape <see cref="SymbolImageWriter"/> draws each dark module as. Finder patterns keep their own shape, <see cref="FinderPatternStyle"/>.
/// </summary>
/// <remarks>
/// A module is read by sampling its centre, so a shape that keeps the centre dark reads as a square does.
/// <see cref="SymbolImageOptions.ModuleScalePercent"/> shrinks the shape inside its cell, and <see cref="SymbolImageOptions.CornerRadiusPercent"/> rounds the square.
/// </remarks>
public enum ModuleStyle
{
    /// <summary>A square filling its cell, the standard look.</summary>
    Square = 0,

    /// <summary>A circle inscribed in the cell.</summary>
    Circle = 1,

    /// <summary>A square with rounded corners.</summary>
    RoundedSquare = 2,
}
