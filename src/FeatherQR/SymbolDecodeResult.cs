namespace FeatherQR;

/// <summary>
/// One symbol of any symbology read from an image: which symbology, its text and where it sits, and that symbology's own result.
/// </summary>
/// <remarks>
/// Only the property of the symbology read holds a result. The other two are default, and their <c>Success</c> is <c>false</c>.
/// </remarks>
public readonly record struct SymbolDecodeResult
{
    internal SymbolDecodeResult(in QRCodeDecodeResult symbol)
    {
        Symbology = Symbology.StandardQR;
        StandardQR = symbol;
    }

    internal SymbolDecodeResult(in MicroQRCodeDecodeResult symbol)
    {
        Symbology = Symbology.MicroQR;
        MicroQR = symbol;
    }

    internal SymbolDecodeResult(in RmQRCodeDecodeResult symbol)
    {
        Symbology = Symbology.RmQR;
        RmQR = symbol;
    }

    /// <summary>The symbology of the symbol read.</summary>
    public Symbology Symbology { get; }

    /// <summary>The decoded text. <c>null</c> in a default value, which no read returns.</summary>
    public string Text => Symbology switch
    {
        Symbology.StandardQR => StandardQR.Text,
        Symbology.MicroQR => MicroQR.Text,
        _ => RmQR.Text,
    };

    /// <summary>Where the symbol sits in the image, as its symbology's result gives it.</summary>
    public SymbolCorners Corners => Symbology switch
    {
        Symbology.StandardQR => StandardQR.Info.Corners,
        Symbology.MicroQR => MicroQR.Info.Corners,
        _ => RmQR.Info.Corners,
    };

    /// <summary>Whether a symbol was read. <c>false</c> for a default value.</summary>
    public bool Success => Symbology switch
    {
        Symbology.StandardQR => StandardQR.Success,
        Symbology.MicroQR => MicroQR.Success,
        _ => RmQR.Success,
    };

    /// <summary>The Standard QR code read when <see cref="Symbology"/> is <see cref="Symbology.StandardQR"/>, and a default result otherwise.</summary>
    public QRCodeDecodeResult StandardQR { get; }

    /// <summary>The Micro QR code read when <see cref="Symbology"/> is <see cref="Symbology.MicroQR"/>, and a default result otherwise.</summary>
    public MicroQRCodeDecodeResult MicroQR { get; }

    /// <summary>The rMQR code read when <see cref="Symbology"/> is <see cref="Symbology.RmQR"/>, and a default result otherwise.</summary>
    public RmQRCodeDecodeResult RmQR { get; }
}
