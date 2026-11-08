namespace FeatherQR.Tests;

/// <summary>
/// The styles both packages' decodability tests render, the rendering package through its builders and the core through
/// <see cref="SymbolImageWriter"/>. One list, so a style that reads through one writer and not the other is a failing test.
/// </summary>
public static class StyledSymbolCases
{
    public enum Style
    {
        Rectangle,
        Circle,
        RoundedRectangle,
    }

    /// <summary>Module styles and scales. 98 percent exercises a small gap and 75 the widest gap tested.</summary>
    public static IEnumerable<(Style Style, int ScalePercent)> ModuleStyles() =>
    [
        (Style.Rectangle, 100),
        (Style.Rectangle, 98),
        (Style.Rectangle, 92),
        (Style.Rectangle, 75),
        (Style.Circle, 100),
        (Style.Circle, 92),
        (Style.RoundedRectangle, 92),
    ];

    /// <summary>Finder pattern styles, each rendered with circle modules at 85 percent.</summary>
    public static IEnumerable<string> FinderStyles() => ["rectangle", "circle", "rounded", "roundedCircle"];

    public static ModuleStyle ModuleStyleOf(Style style) => style switch
    {
        Style.Circle => ModuleStyle.Circle,
        Style.RoundedRectangle => ModuleStyle.RoundedSquare,
        _ => ModuleStyle.Square,
    };

    public static FinderPatternStyle FinderStyleOf(string finder) => finder switch
    {
        "circle" => FinderPatternStyle.Circle,
        "rounded" => FinderPatternStyle.RoundedSquare,
        "roundedCircle" => FinderPatternStyle.RoundedSquareCircle,
        _ => FinderPatternStyle.Square,
    };
}
