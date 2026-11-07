namespace FeatherQR;

/// <summary>
/// The shape <see cref="SymbolImageWriter"/> draws the finder patterns as, the large squares in the corners: three on Standard QR, one on Micro QR and rMQR.
/// </summary>
/// <remarks>
/// A reader locates a symbol by the finder pattern's 1:1:3:1:1 run of dark and light, so every style keeps the rings continuous and never takes
/// the module style or scale. The light ring is left as the light colour.
/// </remarks>
public enum FinderPatternStyle
{
    /// <summary>Three nested squares, the standard look.</summary>
    Square = 0,

    /// <summary>Three nested circles.</summary>
    Circle = 1,

    /// <summary>Three nested squares with rounded corners, each rounded by <see cref="SymbolImageOptions.CornerRadiusPercent"/> of its side.</summary>
    RoundedSquare = 2,

    /// <summary>Two nested rounded squares around a circle.</summary>
    RoundedSquareCircle = 3,
}
