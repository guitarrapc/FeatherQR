using FeatherQR;
using FeatherQR.SkiaSharp;

namespace BlazorWasm;

/// <summary>
/// QR generation options bound to the page controls. Mirrors the option set of the
/// FeatherQR.Playground <c>QrRequest</c>, using library enums directly since
/// no JS interop boundary is involved.
/// </summary>
public sealed class QrOptions
{
    public string Content { get; set; } = "https://github.com/guitarrapc/FeatherQR";
    /// <summary>Symbology: Standard QR (versions 1-40), Micro QR (M1-M4) or rMQR (R7x43-R17x139).</summary>
    public SymbologyKind Symbology { get; set; } = SymbologyKind.QrCode;
    /// <summary>Error correction level. H is recommended when a logo overlays the code.</summary>
    public QREccLevel Ecc { get; set; } = QREccLevel.H;
    /// <summary>Micro QR error correction level (M1 supports error detection only).</summary>
    public MicroQREccLevel MicroEcc { get; set; } = MicroQREccLevel.M;
    /// <summary>rMQR error correction level (M or H).</summary>
    public RmQREccLevel RmEcc { get; set; } = RmQREccLevel.M;
    /// <summary>rMQR automatic fit: fewest modules (default, matches other encoders), narrowest or shortest symbol.</summary>
    public RmQRFitStrategy RmFitStrategy { get; set; } = RmQRFitStrategy.MinimizeArea;
    /// <summary>rMQR fixed symbol height (modules) for automatic width selection, or null for any height.</summary>
    public RmQRHeight? RmHeight { get; set; }
    /// <summary>Exported image size in pixels (square). The on-screen preview scales to fit.</summary>
    public int Size { get; set; } = 512;
    /// <summary>Quiet zone in modules (0-10). The specification default is 4 (Micro QR: 2).</summary>
    public int QuietZone { get; set; } = 4;
    /// <summary>QR version 1-40 (Micro QR: 1-4 for M1-M4; rMQR: 1-32 for R7x43-R17x139), or -1 for automatic selection.</summary>
    public int Version { get; set; } = -1;
    public ModuleShapeKind ModuleShape { get; set; } = ModuleShapeKind.Circle;
    /// <summary>Module size as a fraction of the cell (0.5-1.0). Below 1.0 leaves gaps between modules.</summary>
    public float ModuleSizePercent { get; set; } = 1.0f;
    /// <summary>Corner radius fraction for rounded modules (0.0-1.0).</summary>
    public float ModuleCornerRadius { get; set; } = 0.3f;
    public FinderShapeKind FinderShape { get; set; } = FinderShapeKind.RoundedCircle;
    /// <summary>Module color as #RRGGBB. Ignored while the gradient is enabled.</summary>
    public string Foreground { get; set; } = "#000000";
    /// <summary>Background color as #RRGGBB. Ignored while <see cref="TransparentBackground"/> is set.</summary>
    public string Background { get; set; } = "#ffffff";
    public bool TransparentBackground { get; set; }
    public bool GradientEnabled { get; set; } = true;
    public string GradientStart { get; set; } = "#fa7e1e";
    public string GradientEnd { get; set; } = "#962fbf";
    public GradientDirection GradientDirection { get; set; } = GradientDirection.TopLeftToBottomRight;
    public LogoMode LogoMode { get; set; } = LogoMode.BuiltIn;
    /// <summary>Logo size as a percentage of the QR side length (1-40).</summary>
    public int LogoSizePercent { get; set; } = 18;
    /// <summary>Border padding around the logo in pixels (0-24).</summary>
    public int LogoBorderWidth { get; set; } = 6;
}

/// <summary>Symbology choices exposed by the page.</summary>
public enum SymbologyKind
{
    QrCode,
    MicroQR,
    RmQR,
}

/// <summary>Module shape choices exposed by the page.</summary>
public enum ModuleShapeKind
{
    Rectangle,
    Circle,
    Rounded,
}

/// <summary>Finder pattern choices exposed by the page. Auto is the plain square the standards define.</summary>
public enum FinderShapeKind
{
    Auto,
    Rectangle,
    Circle,
    Rounded,
    RoundedCircle,
}

public enum LogoMode
{
    None,
    BuiltIn,
    Custom,
}
