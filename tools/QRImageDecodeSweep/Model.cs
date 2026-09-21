namespace QRImageDecodeSweep;

/// <summary>A module matrix without quiet zone, row-major, true = dark.</summary>
internal sealed record Symbol(bool[] Modules, int Width, int Height, string VersionName)
{
    public bool this[int row, int column] => Modules[row * Width + column];
}

/// <summary>A grayscale image, one byte a pixel, with the density it was drawn at.</summary>
internal sealed record Rendered(byte[] Luminance, int Width, int Height, float PixelsPerModule);

/// <summary>One payload at one version and level; every encoder gets the same one.</summary>
internal sealed record CaseDefinition(string Symbology, int CaseId, string Text, string Mode, string Ecc, int Version, string VersionName, int RmqrHeight, int RmqrWidth);

/// <summary>An encoder library: its matrix, and its own image writer where it has one.</summary>
internal sealed record Encoder(string Name, Func<CaseDefinition, Symbol?> Encode, Func<CaseDefinition, Symbol, Random, Rendered?>? NativeRender = null);

/// <summary>A way of turning a matrix into an image.</summary>
internal sealed record Kind(string Name, Func<Symbol, CaseDefinition, Encoder, Random, Rendered?> Render);

/// <summary>What the three readers made of one image. <see cref="Key"/> names the image and is stable between runs and trees; <see cref="Note"/> says how a misread differs and is not part of the result file.</summary>
internal sealed record ResultRow(string[] Key, string Status, bool FeatherQr, bool Misread, bool ZXingCpp, bool ZXingNet, string? Note = null);

internal static class Symbologies
{
    public const string StandardQr = "qr";
    public const string MicroQr = "micro";
    public const string RmQr = "rmqr";

    public static readonly string[] All = [StandardQr, MicroQr, RmQr];

    public static string VersionName(string symbology, int width, int height) => symbology switch
    {
        StandardQr => "v" + (width - 17) / 4,
        MicroQr => "M" + (width - 9) / 2,
        _ => $"R{height}x{width}",
    };

    public static int QuietZone(string symbology) => symbology == StandardQr ? 4 : 2;

    /// <summary>The baseline's case counts: ten per Standard QR version, a hundred per Micro QR version, twenty per rMQR version.</summary>
    public static int DefaultCases(string symbology) => symbology switch
    {
        StandardQr => 400,
        MicroQr => 400,
        _ => 640,
    };
}
