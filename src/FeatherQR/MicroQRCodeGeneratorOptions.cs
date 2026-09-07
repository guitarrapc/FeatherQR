namespace FeatherQR;

/// <summary>
/// Optional settings for <see cref="MicroQRCodeGenerator"/>.
/// <c>default</c> is the complete default configuration (automatic version, 2-module quiet zone), so set only what you need: <c>new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M3, QuietZoneSize = 0 }</c>.
/// </summary>
/// <remarks>
/// The smallest option set of the three symbologies: Micro QR has no ECI, and no fit strategy because M1-M4 are totally ordered by capacity.
/// </remarks>
public readonly record struct MicroQRCodeGeneratorOptions
{
    // Offset from the specified default, for the reasons on QRCodeGeneratorOptions.QuietZoneSize.
    private readonly int _quietZoneSizeOffset;

    private readonly int? _maskPattern;

    /// <summary>
    /// Builds an option set without an object initializer, for consumers whose language version predates C# 9.
    /// </summary>
    /// <remarks>
    /// Prefer the object initializer (<c>new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 }</c>): it names only what it sets and does not depend on this parameter order. Pass constructor arguments by name: <paramref name="quietZoneSize"/> and <paramref name="maskPattern"/> are adjacent integers, so a positional call can transpose them and still compile.
    /// The reasoning is recorded once on <see cref="QRCodeGeneratorOptions(EciMode, bool, QRVersionRange, int, int?, bool, QRSegmentation)"/>.
    /// </remarks>
    /// <param name="version">See <see cref="Version"/>.</param>
    /// <param name="quietZoneSize">See <see cref="QuietZoneSize"/>.</param>
    /// <param name="maskPattern">See <see cref="MaskPattern"/>.</param>
    /// <param name="segmentation">See <see cref="Segmentation"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maskPattern"/> is not 0-3 or <c>null</c>.</exception>
    public MicroQRCodeGeneratorOptions(
        MicroQRVersionRange version = default,
        int quietZoneSize = MicroQRCodeGenerator.DefaultQuietZone,
        int? maskPattern = null,
        MicroQRSegmentation segmentation = MicroQRSegmentation.Single)
        : this()
    {
        Version = version;
        QuietZoneSize = quietZoneSize;
        MaskPattern = maskPattern;
        Segmentation = segmentation;
    }

    /// <summary>The default configuration, identical to <c>default</c>.</summary>
    public static MicroQRCodeGeneratorOptions Default => default;

    /// <summary>
    /// The versions the generator may choose from.
    /// Defaults to <see cref="MicroQRVersionRange.Any"/>; a <see cref="MicroQRVersion"/> or its nullable converts implicitly, so a <c>null</c> means automatic.
    /// </summary>
    public MicroQRVersionRange Version { get; init; }

    /// <summary>
    /// Pin one of the four Micro QR data mask patterns (0-3, ISO/IEC 18004 Table 10) instead of the automatic edge-score selection.
    /// <c>null</c> (the default) selects the highest-scoring pattern.
    /// Any pattern yields a valid, decodable symbol; the automatic choice merely optimizes scan reliability.
    /// </summary>
    /// <remarks>
    /// For reproducing a symbol produced elsewhere byte-for-byte (the pattern another encoder chose is reported by <see cref="MicroQRCodeDecodeInfo.MaskPattern"/>), and for exercising a decoder against all four patterns.
    /// Micro QR numbers its patterns 0-3; they are not the Standard QR patterns of the same index.
    /// Like <see cref="Version"/>, an invalid value is an argument error and is rejected here rather than when a generator reads it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not 0-3 or <c>null</c>.</exception>
    public int? MaskPattern
    {
        get => _maskPattern;
        init
        {
            if (value is < 0 or > 3)
                throw new ArgumentOutOfRangeException(nameof(MaskPattern), $"Mask pattern must be 0-3, or null for automatic selection, but was {value}");
            _maskPattern = value;
        }
    }

    /// <summary>
    /// Quiet zone width in modules.
    /// Defaults to 2, the ISO/IEC 18004 value for Micro QR; 0 is valid.
    /// </summary>
    public int QuietZoneSize
    {
        get => MicroQRCodeGenerator.DefaultQuietZone + _quietZoneSizeOffset;
        init => _quietZoneSizeOffset = value - MicroQRCodeGenerator.DefaultQuietZone;
    }

    /// <summary>
    /// How the content is split into encoding-mode segments (see <see cref="MicroQRSegmentation"/>).
    /// Defaults to <see cref="MicroQRSegmentation.Single"/>.
    /// <see cref="MicroQRSegmentation.Optimal"/> never selects a larger version, and emits the identical bit stream when a split would not shrink the symbol.
    /// Size a destination buffer with the same value you encode with.
    /// </summary>
    public MicroQRSegmentation Segmentation { get; init; }
}
