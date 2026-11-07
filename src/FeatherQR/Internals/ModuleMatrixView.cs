namespace FeatherQR.Internals;

/// <summary>
/// Uniform read view over a symbol matrix (core modules + virtual quiet zone), letting the rendering loops in <c>SymbolRenderer</c> (FeatherQR.SkiaSharp) and the core's image writers serve every symbology through struct specialization (no virtual dispatch).
/// Dimensions are width × height so rectangular symbols (rMQR) share the loops. Square symbologies report the same value for both axes.
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

    /// <summary>
    /// The core modules packed MSB first, row after row with no padding between rows: module (r, c) is bit r × <see cref="CoreWidth"/> + c.
    /// </summary>
    ReadOnlySpan<byte> CoreBits { get; }

    /// <summary>
    /// Whether the core module belongs to a finder pattern, which a renderer must draw as one
    /// continuous pattern rather than as styled modules: the finder is what a decoder scans for,
    /// by its 1:1:3:1:1 run of dark and light, and gaps between modules erase that run.
    /// </summary>
    bool IsFinderPattern(int coreRow, int coreCol);

    /// <summary>How many finder patterns the symbol has: three on Standard QR, one on Micro QR and rMQR.</summary>
    int FinderPatternCount { get; }

    /// <summary>The core row and column of a finder pattern's top-left module, each pattern seven modules square.</summary>
    void GetFinderPatternOrigin(int index, out int coreRow, out int coreCol);

    /// <summary>Whether an icon area can be left at the centre, which Standard QR's error correction and square core allow.</summary>
    bool SupportsIcon { get; }
}

internal readonly struct StandardQrMatrixView(QRCodeData data) : IModuleMatrixView
{
    public int Width => data.Size;
    public int Height => data.Size;
    public int CoreWidth => data.GetCoreSize();
    public int CoreHeight => data.GetCoreSize();
    public bool GetCoreModule(int coreRow, int coreCol) => data.GetCoreModule(coreRow, coreCol);
    public ReadOnlySpan<byte> CoreBits => data.CoreBits;
    public bool IsFinderPattern(int coreRow, int coreCol) => data.IsFinderPattern(coreRow, coreCol);
    public int FinderPatternCount => 3;
    public bool SupportsIcon => true;

    // Top-left, top-right, bottom-left.
    public void GetFinderPatternOrigin(int index, out int coreRow, out int coreCol)
    {
        var far = data.GetCoreSize() - 7;
        coreRow = index == 2 ? far : 0;
        coreCol = index == 1 ? far : 0;
    }
}

internal readonly struct MicroQRMatrixView(MicroQRCodeData data) : IModuleMatrixView
{
    public int Width => data.Size;
    public int Height => data.Size;
    public int CoreWidth => data.GetCoreSize();
    public int CoreHeight => data.GetCoreSize();
    public bool GetCoreModule(int coreRow, int coreCol) => data.GetCoreModule(coreRow, coreCol);
    public ReadOnlySpan<byte> CoreBits => data.CoreBits;
    // One finder, top-left, as in every Micro QR version.
    public bool IsFinderPattern(int coreRow, int coreCol) => coreRow < 7 && coreCol < 7;
    public int FinderPatternCount => 1;
    public bool SupportsIcon => false;

    public void GetFinderPatternOrigin(int index, out int coreRow, out int coreCol)
    {
        coreRow = 0;
        coreCol = 0;
    }
}

internal readonly struct RmQRMatrixView(RmQRCodeData data) : IModuleMatrixView
{
    public int Width => data.Width;
    public int Height => data.Height;
    public int CoreWidth => data.GetCoreWidth();
    public int CoreHeight => data.GetCoreHeight();
    public bool GetCoreModule(int coreRow, int coreCol) => data.GetCoreModule(coreRow, coreCol);
    public ReadOnlySpan<byte> CoreBits => data.CoreBits;
    // One finder, top-left. The sub-finder and corner patterns are not reported: they are located
    // from the finder and the format information, not by the run ratio, so they survive styling.
    public bool IsFinderPattern(int coreRow, int coreCol) => coreRow < 7 && coreCol < 7;
    public int FinderPatternCount => 1;
    public bool SupportsIcon => false;

    public void GetFinderPatternOrigin(int index, out int coreRow, out int coreCol)
    {
        coreRow = 0;
        coreCol = 0;
    }
}
