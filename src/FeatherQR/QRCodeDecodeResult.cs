namespace FeatherQR;

/// <summary>
/// One QR code read from an image: its text, and what the decode found.
/// </summary>
/// <remarks>
/// A read that can fail, such as one from a stream, returns a result in every case: <see cref="Success"/> is <c>false</c>, <see cref="Text"/> is empty and <see cref="Info"/> carries the reason.
/// The reads for several symbols return only the symbols they read.
/// </remarks>
public readonly record struct QRCodeDecodeResult
{
    internal QRCodeDecodeResult(string text, QRCodeDecodeInfo info)
    {
        Text = text;
        Info = info;
    }

    /// <summary>The decoded text, or an empty string when decoding failed. <c>null</c> in a default value, which no read returns.</summary>
    public string Text { get; }

    /// <summary>What the attempt found: status, version, level, mask and corrections, and on success where the symbol sits in the image.</summary>
    public QRCodeDecodeInfo Info { get; }

    /// <summary>
    /// Whether a QR code was read. <c>false</c> for a failed read, and for a default value.
    /// </summary>
    // A default value's status is 0, which is Success, so its missing text is what tells it apart
    public bool Success => Text is not null && Info.Status == DecodeStatus.Success;
}
