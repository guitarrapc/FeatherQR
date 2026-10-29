namespace FeatherQR;

/// <summary>The symbology of a symbol read from an image.</summary>
internal enum SymbologyKind : byte
{
    StandardQR,
    MicroQR,
    RmQR,
}

/// <summary>A symbol of any symbology read from an image: which symbology, and that symbology's result, the other two default.</summary>
internal readonly struct MixedSymbol
{
    public MixedSymbol(in QRCodeDecodeResult symbol)
    {
        Symbology = SymbologyKind.StandardQR;
        StandardQR = symbol;
    }

    public MixedSymbol(in MicroQRCodeDecodeResult symbol)
    {
        Symbology = SymbologyKind.MicroQR;
        MicroQR = symbol;
    }

    public MixedSymbol(in RmQRCodeDecodeResult symbol)
    {
        Symbology = SymbologyKind.RmQR;
        RmQR = symbol;
    }

    public SymbologyKind Symbology { get; }

    public QRCodeDecodeResult StandardQR { get; }

    public MicroQRCodeDecodeResult MicroQR { get; }

    public RmQRCodeDecodeResult RmQR { get; }

    /// <summary>The decoded text. <c>null</c> in a default value, which no read returns.</summary>
    public string Text => Symbology switch
    {
        SymbologyKind.StandardQR => StandardQR.Text,
        SymbologyKind.MicroQR => MicroQR.Text,
        _ => RmQR.Text,
    };

    /// <summary>Where the symbol lies in the image.</summary>
    public SymbolCorners Corners => Symbology switch
    {
        SymbologyKind.StandardQR => StandardQR.Info.Corners,
        SymbologyKind.MicroQR => MicroQR.Info.Corners,
        _ => RmQR.Info.Corners,
    };
}
