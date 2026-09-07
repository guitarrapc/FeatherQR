namespace FeatherQR;

/// <summary>
/// The Micro QR versions a generator may choose from: the smallest one in the range that holds the content is used.
/// The Micro QR counterpart of <see cref="QRVersionRange"/>.
/// </summary>
/// <remarks>
/// M1-M4 differ in the modes and ECC levels they offer, not only in capacity, so a range can leave nothing usable for two reasons.
/// No version offering the requested ECC level is a contradiction and throws; none carrying the mode the text needs is a poor fit and returns <c>false</c>, since the text is what picks the mode.
/// </remarks>
public readonly record struct MicroQRVersionRange
{
    /// <summary>The lowest Micro QR version, M1.</summary>
    public const MicroQRVersion MinVersion = MicroQRVersion.M1;

    /// <summary>The highest Micro QR version, M4.</summary>
    public const MicroQRVersion MaxVersion = MicroQRVersion.M4;

    // Normalised as in QRVersionRange: 0 means "at the natural limit", so default(T),
    // Any and Between(M1, M4) are one canonical value. MicroQRVersion starts at 1.
    private readonly byte _min;
    private readonly byte _max;

    /// <summary>An inclusive range from <paramref name="min"/> to <paramref name="max"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound is not M1-M4, or <paramref name="min"/> exceeds <paramref name="max"/>.</exception>
    public MicroQRVersionRange(MicroQRVersion min, MicroQRVersion max)
    {
        ValidateBound(min, nameof(min));
        ValidateBound(max, nameof(max));
        if (min > max)
            throw new ArgumentOutOfRangeException(nameof(min), $"Version range minimum {min} must not exceed maximum {max}.");

        _min = (byte)(min == MinVersion ? 0 : (int)min);
        _max = (byte)(max == MaxVersion ? 0 : (int)max);
    }

    /// <summary>The lowest permitted version (M1 when unbounded below).</summary>
    public MicroQRVersion Min => _min == 0 ? MinVersion : (MicroQRVersion)_min;

    /// <summary>The highest permitted version (M4 when unbounded above).</summary>
    public MicroQRVersion Max => _max == 0 ? MaxVersion : (MicroQRVersion)_max;

    /// <summary>Whether this range constrains nothing (the default).</summary>
    public bool IsAny => _min == 0 && _max == 0;

    /// <summary>Whether this range pins a single version.</summary>
    public bool IsExact => Min == Max;

    /// <summary>Every version, M1 to M4. Identical to <c>default</c>.</summary>
    public static MicroQRVersionRange Any => default;

    /// <inheritdoc cref="MicroQRVersionRange(MicroQRVersion, MicroQRVersion)"/>
    /// <summary>Exactly <paramref name="version"/>, with no automatic selection.</summary>
    public static MicroQRVersionRange Exactly(MicroQRVersion version) => new(version, version);

    /// <inheritdoc cref="MicroQRVersionRange(MicroQRVersion, MicroQRVersion)"/>
    /// <summary><paramref name="version"/> or larger.</summary>
    public static MicroQRVersionRange AtLeast(MicroQRVersion version) => new(version, MaxVersion);

    /// <inheritdoc cref="MicroQRVersionRange(MicroQRVersion, MicroQRVersion)"/>
    /// <summary><paramref name="version"/> or smaller.</summary>
    public static MicroQRVersionRange AtMost(MicroQRVersion version) => new(MinVersion, version);

    /// <inheritdoc cref="MicroQRVersionRange(MicroQRVersion, MicroQRVersion)"/>
    /// <summary>An inclusive range from <paramref name="min"/> to <paramref name="max"/>.</summary>
    public static MicroQRVersionRange Between(MicroQRVersion min, MicroQRVersion max) => new(min, max);

    /// <inheritdoc cref="MicroQRVersionRange(MicroQRVersion, MicroQRVersion)"/>
    /// <summary>A single version, as <see cref="Exactly"/>.</summary>
    public static implicit operator MicroQRVersionRange(MicroQRVersion version) => Exactly(version);

    /// <summary>A single version, or <see cref="Any"/> when there is none, so an optional version needs no branch.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> has a value that is not M1-M4.</exception>
    public static implicit operator MicroQRVersionRange(MicroQRVersion? version) => version.HasValue ? Exactly(version.GetValueOrDefault()) : Any;

    /// <summary>Whether <paramref name="version"/> falls inside this range.</summary>
    public bool Contains(MicroQRVersion version) => version >= Min && version <= Max;

    /// <inheritdoc/>
    public override string ToString() => IsExact ? Min.ToString() : $"{Min}-{Max}";

    private static void ValidateBound(MicroQRVersion version, string paramName)
    {
        if (version < MinVersion || version > MaxVersion)
            throw new ArgumentOutOfRangeException(paramName, $"Micro QR version must be {MinVersion}-{MaxVersion}, but was {version}");
    }
}
