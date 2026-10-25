namespace FeatherQR;

/// <summary>
/// One rMQR code read from an image: its text, and what the decode found.
/// </summary>
/// <remarks>
/// A read that can fail, such as one from a stream, returns a result in every case: <see cref="Success"/> is <c>false</c>, <see cref="Text"/> is empty and <see cref="Info"/> carries the reason.
/// </remarks>
public readonly record struct RmQRCodeDecodeResult
{
    internal RmQRCodeDecodeResult(string text, RmQRCodeDecodeInfo info)
    {
        Text = text;
        Info = info;
    }

    /// <summary>The decoded text, or an empty string when decoding failed.</summary>
    public string Text { get; }

    /// <summary>What the attempt found: status, version, level and corrections, and on success where the symbol sits in the image.</summary>
    public RmQRCodeDecodeInfo Info { get; }

    /// <summary>
    /// Whether an rMQR code was read. <c>false</c> for a failed read, and for a default value.
    /// </summary>
    // A default value's status is 0, which is Success, so its missing text is what tells it apart
    public bool Success => Text is not null && Info.Status == DecodeStatus.Success;
}
