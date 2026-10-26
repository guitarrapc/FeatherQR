namespace FeatherQR.SkiaSharp;

/// <summary>
/// The most a stream read of the image decoders takes: the bytes it reads from the stream, and the pixels the image's header may declare.
/// </summary>
/// <remarks>
/// <para>
/// A read past either limit throws <see cref="InvalidDataException"/> before any pixel is allocated, and the message names the limit to raise.
/// The pixel limit bounds the image, not the stream: an image cut short after its header decodes too, so a stream of a few dozen bytes can declare an image at the limit.
/// An interlaced PNG of 16 bits a channel took 192 MiB to decode at the default limit and 3,078 MiB at 16,384 × 16,384, so a caller raises the limits only as far as the images it expects.
/// </para>
/// <para>
/// The defaults hold an image of 4,032 × 3,024 pixels, a 12-megapixel photograph.
/// </para>
/// <para>
/// The limits apply to the stream reads alone. A bitmap or luminance the caller decodes itself is memory already allocated.
/// </para>
/// </remarks>
public sealed record class ImageStreamReadLimits
{
    private const int DefaultMaxBytes = 64 * 1024 * 1024;
    private const long DefaultMaxPixels = 4_096L * 4_096;

    // The read holds the bytes in one array, and no array is longer
    private const int LongestArray = 0x7FFFFFC7;

    private readonly int _maxBytes;
    private readonly long _maxPixels;

    /// <summary>
    /// Creates the default limits, which an object initializer then sets.
    /// </summary>
    /// <remarks>
    /// Declared beside the constructor below so that <c>new ImageStreamReadLimits { MaxPixels = 4096 * 4096 }</c> binds to it. Bound to the optional parameters, the call would hold the defaults it was compiled with when a later version changes them.
    /// </remarks>
    public ImageStreamReadLimits()
    {
        _maxBytes = DefaultMaxBytes;
        _maxPixels = DefaultMaxPixels;
    }

    /// <summary>
    /// Creates limits, each left at its default when omitted.
    /// </summary>
    /// <remarks>
    /// The same as the object initializer (<c>new ImageStreamReadLimits { MaxPixels = 4096 * 4096 }</c>), for consumers whose language version predates C# 9, where <c>init</c> accessors are unassignable.
    /// </remarks>
    /// <param name="maxBytes">See <see cref="MaxBytes"/>.</param>
    /// <param name="maxPixels">See <see cref="MaxPixels"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a value is not positive, or the byte limit is over 2,147,483,591.</exception>
    public ImageStreamReadLimits(int maxBytes = DefaultMaxBytes, long maxPixels = DefaultMaxPixels)
    {
        _maxBytes = Bytes(maxBytes, nameof(maxBytes));
        _maxPixels = Pixels(maxPixels, nameof(maxPixels));
    }

    /// <summary>
    /// The limits the stream reads take when none are given: 64 MiB, and 16,777,216 pixels (4,096 × 4,096).
    /// </summary>
    public static readonly ImageStreamReadLimits Default = new();

    /// <summary>
    /// The most bytes a read takes from the stream, from 1 to 2,147,483,591, the length of the longest array. 67,108,864 (64 MiB) by default.
    /// </summary>
    /// <remarks>
    /// A stream that cannot say its length is read into a buffer that doubles as it fills, so the read holds more than this while it reads, about twice it at the default limit and three times it at a limit of 268,435,457 bytes.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not positive or is over 2,147,483,591.</exception>
    public int MaxBytes
    {
        get => _maxBytes;
        init => _maxBytes = Bytes(value, nameof(MaxBytes));
    }

    /// <summary>
    /// The most pixels the image's header may declare. 16,777,216 (4,096 × 4,096) by default.
    /// </summary>
    /// <remarks>
    /// A bitmap holds at most 2,147,483,647 bytes, 536,870,911 pixels of four bytes, so an image past that is refused whatever the limit.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not positive.</exception>
    public long MaxPixels
    {
        get => _maxPixels;
        init => _maxPixels = Pixels(value, nameof(MaxPixels));
    }

    private static int Bytes(int value, string name)
        => value is > 0 and <= LongestArray ? value : throw new ArgumentOutOfRangeException(name, "The byte limit must be from 1 to 2,147,483,591.");

    private static long Pixels(long value, string name)
        => value > 0 ? value : throw new ArgumentOutOfRangeException(name, "The pixel limit must be positive.");
}
