namespace FeatherQR;

/// <summary>
/// Result of <see cref="RmQRCodeGenerator.TryGetRequiredBufferSize"/>: the byte count of the byte-per-module matrix, its dimensions (quiet zone included) and the version that will be used.
/// </summary>
public readonly record struct RmQRCodeCalculatedSize
{
    internal RmQRCodeCalculatedSize(int bufferSize, int width, int height, RmQRVersion version)
    {
        BufferSize = bufferSize;
        Width = width;
        Height = height;
        Version = version;
    }

    /// <summary>Required destination size in bytes (<see cref="Width"/> × <see cref="Height"/>).</summary>
    public int BufferSize { get; }

    /// <summary>Matrix width in modules, quiet zone included.</summary>
    public int Width { get; }

    /// <summary>Matrix height in modules, quiet zone included.</summary>
    public int Height { get; }

    /// <summary>The rMQR version that will be generated (requested or automatically selected).</summary>
    public RmQRVersion Version { get; }
}
