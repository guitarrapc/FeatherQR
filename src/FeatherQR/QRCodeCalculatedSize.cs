namespace FeatherQR;

/// <summary>
/// Result of <see cref="QRCodeGenerator.TryGetRequiredBufferSize"/>: buffer size, matrix side length and selected version for a pending QR code encode.
/// </summary>
public readonly record struct QRCodeCalculatedSize
{
    internal QRCodeCalculatedSize(int bufferSize, int size, int version)
    {
        BufferSize = bufferSize;
        Size = size;
        Version = version;
    }

    /// <summary>Required destination buffer size in bytes (one byte per module, quiet zone included).</summary>
    public int BufferSize { get; }

    /// <summary>Matrix side length in modules, quiet zone included.</summary>
    public int Size { get; }

    /// <summary>The QR code version (1-40) that will be produced.</summary>
    public int Version { get; }
}
