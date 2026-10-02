namespace FeatherQR.Tests;

/// <summary>
/// One of the decoders that read around a single finder, Micro QR or rMQR, through its public image entry point, so that a
/// rule both of them follow is tested on both by one test (<see cref="DestinationContractTest"/>).
/// </summary>
public sealed class SingleFinderDecoder
{
    public static readonly SingleFinderDecoder MicroQR = new("Micro QR", MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4), static (luminance, width, height, destination) =>
    {
        var ok = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, destination, out var written, out var info);
        return new SingleFinderRead(ok, written, info.Status, info.Version.ToString(), info.Corners);
    });

    public static readonly SingleFinderDecoder RmQR = new("rMQR", RmQRCodeDecoder.GetMaxDecodedLength(RmQRVersion.R17x139), static (luminance, width, height, destination) =>
    {
        var ok = RmQRCodeDecoder.TryDecodeImage(luminance, width, height, destination, out var written, out var info);
        return new SingleFinderRead(ok, written, info.Status, info.Version.ToString(), info.Corners);
    });

    private readonly Func<byte[], int, int, char[], SingleFinderRead> _decode;

    private SingleFinderDecoder(string name, int maxDecodedLength, Func<byte[], int, int, char[], SingleFinderRead> decode)
    {
        Name = name;
        MaxDecodedLength = maxDecodedLength;
        _decode = decode;
    }

    public string Name { get; }

    /// <summary>The longest text any symbol of the symbology holds: a destination every symbol fits.</summary>
    public int MaxDecodedLength { get; }

    public SingleFinderRead Decode(byte[] luminance, int width, int height, char[] destination) => _decode(luminance, width, height, destination);

    public override string ToString() => Name;
}

/// <summary>What an image decode returned: the result, the characters written, and the parts of the diagnostics both symbologies have.</summary>
public readonly record struct SingleFinderRead(bool Ok, int Written, DecodeStatus Status, string Version, SymbolCorners Corners);
