using FeatherQR.Internals.RmQR;

namespace FeatherQR;

/// <summary>
/// Optional settings for <see cref="RmQRCodeGenerator"/>.
/// <c>default</c> is the complete default configuration and is what an omitted argument sends, so the shortest correct call is <c>Create(text, eccLevel)</c>.
/// </summary>
/// <remarks>
/// rMQR specific rather than shared: <see cref="Version"/> is a different type in each symbology, <see cref="QuietZoneSize"/> has a different specified default, and <see cref="FitStrategy"/>, <see cref="Height"/> and <see cref="Segmentation"/> have no meaning outside rMQR.
/// There is no version range here because rMQR's 32 versions are not totally ordered; fit is constrained by strategy and height instead.
/// </remarks>
public readonly record struct RmQRCodeGeneratorOptions
{
    // Offset from the specified default, so default(T) carries the ISO/IEC 23941 value of 2
    // rather than 0 (a legitimate caller choice). An offset rather than a value+1 sentinel
    // keeps the canonical form unique, so equality does not report two identical option sets
    // as different.
    private readonly int _quietZoneSizeOffset;

    /// <summary>
    /// Builds an option set without an object initializer, for consumers whose language version predates C# 9.
    /// </summary>
    /// <remarks>
    /// Prefer the object initializer (<c>new RmQRCodeGeneratorOptions { QuietZoneSize = 0 }</c>): it names only what it sets and does not depend on this parameter order, and pass constructor arguments by name.
    /// The reasoning is recorded once on <see cref="QRCodeGeneratorOptions(EciMode, bool, QRVersionRange, int, int?, bool, QRSegmentation)"/>.
    /// </remarks>
    /// <param name="eciMode">See <see cref="EciMode"/>.</param>
    /// <param name="version">See <see cref="Version"/>.</param>
    /// <param name="fitStrategy">See <see cref="FitStrategy"/>.</param>
    /// <param name="height">See <see cref="Height"/>.</param>
    /// <param name="quietZoneSize">See <see cref="QuietZoneSize"/>.</param>
    /// <param name="segmentation">See <see cref="Segmentation"/>.</param>
    public RmQRCodeGeneratorOptions(
        EciMode eciMode = EciMode.Default,
        RmQRVersion? version = null,
        RmQRFitStrategy fitStrategy = RmQRFitStrategy.MinimizeArea,
        RmQRHeight? height = null,
        int quietZoneSize = RmQRConstants.QuietZoneModules,
        RmQRSegmentation segmentation = RmQRSegmentation.Single)
        : this()
    {
        EciMode = eciMode;
        Version = version;
        FitStrategy = fitStrategy;
        Height = height;
        QuietZoneSize = quietZoneSize;
        Segmentation = segmentation;
    }

    /// <summary>The default configuration, identical to <c>default</c>.</summary>
    public static RmQRCodeGeneratorOptions Default => default;

    /// <summary>
    /// Character encoding declaration.
    /// The default auto-detects ASCII (no ECI), ISO-8859-1 (assignment 3) or UTF-8 (assignment 26) from the content.
    /// </summary>
    /// <remarks>
    /// Only <see cref="EciMode.Default"/>, <see cref="EciMode.Iso8859_1"/> and <see cref="EciMode.Utf8"/> are accepted.
    /// Declaring Latin-1 over content it cannot represent throws rather than silently re-encoding.
    /// </remarks>
    public EciMode EciMode { get; init; }

    /// <summary>
    /// A specific version, or <c>null</c> (the default) to fit one automatically by <see cref="FitStrategy"/> and <see cref="Height"/>.
    /// </summary>
    public RmQRVersion? Version { get; init; }

    /// <summary>
    /// How to choose among the versions that hold the content.
    /// Defaults to <see cref="RmQRFitStrategy.MinimizeArea"/>, the choice both reference encoders make.
    /// </summary>
    public RmQRFitStrategy FitStrategy { get; init; }

    /// <summary>
    /// Restrict automatic fitting to one symbol height, or <c>null</c> (the default) to consider every height.
    /// Must agree with <see cref="Version"/> when both are set.
    /// </summary>
    public RmQRHeight? Height { get; init; }

    /// <summary>
    /// Quiet zone width in modules.
    /// Defaults to 2, the ISO/IEC 23941 value; 0 is valid.
    /// </summary>
    public int QuietZoneSize
    {
        get => RmQRConstants.QuietZoneModules + _quietZoneSizeOffset;
        init => _quietZoneSizeOffset = value - RmQRConstants.QuietZoneModules;
    }

    /// <summary>
    /// Whether to split the content into mixed-mode segments.
    /// Defaults to <see cref="RmQRSegmentation.Single"/>.
    /// Size a destination buffer with the same value you encode with: the two modes can select different versions.
    /// </summary>
    public RmQRSegmentation Segmentation { get; init; }
}
