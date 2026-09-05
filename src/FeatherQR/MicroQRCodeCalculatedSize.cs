namespace FeatherQR;

/// <summary>
/// Result of <see cref="MicroQRCodeGenerator.TryGetRequiredBufferSize"/>: buffer size,
/// matrix side length and selected version for a pending Micro QR encode.
/// </summary>
public readonly struct MicroQRCodeCalculatedSize
{
    internal MicroQRCodeCalculatedSize(int bufferSize, int qrSize, MicroQRVersion version)
    {
        BufferSize = bufferSize;
        Size = qrSize;
        Version = version;
    }

    /// <summary>Required destination buffer size in bytes (one byte per module, quiet zone included).</summary>
    public int BufferSize { get; }

    /// <summary>Matrix side length in modules, quiet zone included.</summary>
    public int Size { get; }

    /// <summary>The Micro QR version that will be produced.</summary>
    public MicroQRVersion Version { get; }
}
