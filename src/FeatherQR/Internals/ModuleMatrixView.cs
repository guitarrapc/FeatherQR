namespace FeatherQR.Internals;

/// <summary>
/// Uniform read view over a symbol matrix (core modules + virtual quiet zone),
/// letting the rendering loops in <c>SymbolRenderer</c> (FeatherQR.SkiaSharp) serve every
/// symbology through struct specialization (no virtual dispatch). Dimensions are
/// width × height so rectangular symbols (rMQR) share the loops; square symbologies
/// report the same value for both axes.
/// </summary>
internal interface IModuleMatrixView
{
    /// <summary>Matrix width in modules, including the quiet zone.</summary>
    int Width { get; }

    /// <summary>Matrix height in modules, including the quiet zone.</summary>
    int Height { get; }

    /// <summary>Core matrix width (quiet zone excluded).</summary>
    int CoreWidth { get; }

    /// <summary>Core matrix height (quiet zone excluded).</summary>
    int CoreHeight { get; }

    /// <summary>Reads a core module (caller guarantees bounds).</summary>
    bool GetCoreModule(int coreRow, int coreCol);

    /// <summary>Whether the core module belongs to a finder pattern.</summary>
    bool IsFinderPattern(int coreRow, int coreCol);
}

internal readonly struct StandardQrMatrixView(QRCodeData data) : IModuleMatrixView
{
    public int Width => data.Size;
    public int Height => data.Size;
    public int CoreWidth => data.GetCoreSize();
    public int CoreHeight => data.GetCoreSize();
    public bool GetCoreModule(int coreRow, int coreCol) => data.GetCoreModule(coreRow, coreCol);
    public bool IsFinderPattern(int coreRow, int coreCol) => data.IsFinderPattern(coreRow, coreCol);
}

internal readonly struct MicroQRMatrixView(MicroQRCodeData data) : IModuleMatrixView
{
    public int Width => data.Size;
    public int Height => data.Size;
    public int CoreWidth => data.GetCoreSize();
    public int CoreHeight => data.GetCoreSize();
    public bool GetCoreModule(int coreRow, int coreCol) => data.GetCoreModule(coreRow, coreCol);
    // Micro QR rendering never styles finder patterns separately, so the draw
    // loops are always called with finder skipping disabled.
    public bool IsFinderPattern(int coreRow, int coreCol) => false;
}

internal readonly struct RmQRMatrixView(RmQRCodeData data) : IModuleMatrixView
{
    public int Width => data.Width;
    public int Height => data.Height;
    public int CoreWidth => data.GetCoreWidth();
    public int CoreHeight => data.GetCoreHeight();
    public bool GetCoreModule(int coreRow, int coreCol) => data.GetCoreModule(coreRow, coreCol);
    // rMQR rendering never styles finder patterns separately (one finder, no ECC headroom).
    public bool IsFinderPattern(int coreRow, int coreCol) => false;
}
