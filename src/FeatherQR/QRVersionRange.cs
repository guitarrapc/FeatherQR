namespace FeatherQR;

/// <summary>
/// The Standard QR versions a generator may choose from: the smallest one in the range
/// that holds the content is used. A fixed version is <see cref="Exactly"/>, the
/// degenerate case, rather than a separate setting.
/// </summary>
/// <remarks>
/// Both bounds are inclusive, unlike <see cref="System.Range"/> whose end is exclusive.
/// That is why <c>1..40</c> is not usable here: it would read as 1 to 40 and mean 1 to 39.
/// </remarks>
public readonly record struct QRCodeVersionRange
{
    /// <summary>The lowest version defined by ISO/IEC 18004.</summary>
    public const int MinVersion = 1;

    /// <summary>The highest version defined by ISO/IEC 18004.</summary>
    public const int MaxVersion = 40;

    // Bounds stored normalised, 0 meaning "at the natural limit", so the canonical form is
    // unique: default(T), Any and Between(1, 40) must not compare unequal. Version 0 does
    // not exist, so 0 is free as a sentinel.
    private readonly byte _min;
    private readonly byte _max;

    /// <summary>An inclusive range from <paramref name="min"/> to <paramref name="max"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound is outside 1-40, or <paramref name="min"/> exceeds <paramref name="max"/>.</exception>
    public QRCodeVersionRange(int min, int max)
    {
        ValidateBound(min, nameof(min));
        ValidateBound(max, nameof(max));
        if (min > max)
            throw new ArgumentOutOfRangeException(nameof(min), $"Version range minimum {min} must not exceed maximum {max}.");

        _min = (byte)(min == MinVersion ? 0 : min);
        _max = (byte)(max == MaxVersion ? 0 : max);
    }

    /// <summary>The lowest permitted version (1 when unbounded below).</summary>
    public int Min => _min == 0 ? MinVersion : _min;

    /// <summary>The highest permitted version (40 when unbounded above).</summary>
    public int Max => _max == 0 ? MaxVersion : _max;

    /// <summary>Whether this range constrains nothing (the default).</summary>
    public bool IsAny => _min == 0 && _max == 0;

    /// <summary>Whether this range pins a single version.</summary>
    public bool IsExact => Min == Max;

    /// <summary>Every version, 1 to 40. Identical to <c>default</c>.</summary>
    public static QRCodeVersionRange Any => default;

    /// <inheritdoc cref="QRCodeVersionRange(int, int)"/>
    /// <summary>Exactly <paramref name="version"/>, with no automatic selection.</summary>
    public static QRCodeVersionRange Exactly(int version) => new(version, version);

    /// <inheritdoc cref="QRCodeVersionRange(int, int)"/>
    /// <summary><paramref name="version"/> or larger.</summary>
    public static QRCodeVersionRange AtLeast(int version) => new(version, MaxVersion);

    /// <inheritdoc cref="QRCodeVersionRange(int, int)"/>
    /// <summary><paramref name="version"/> or smaller.</summary>
    public static QRCodeVersionRange AtMost(int version) => new(MinVersion, version);

    /// <inheritdoc cref="QRCodeVersionRange(int, int)"/>
    /// <summary>An inclusive range from <paramref name="min"/> to <paramref name="max"/>.</summary>
    public static QRCodeVersionRange Between(int min, int max) => new(min, max);

    /// <inheritdoc cref="QRCodeVersionRange(int, int)"/>
    /// <summary>A single version, as <see cref="Exactly"/>. Lets an option set read <c>Version = 15</c>.</summary>
    public static implicit operator QRCodeVersionRange(int version) => Exactly(version);

    /// <summary>A single version, or <see cref="Any"/> when there is none.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> has a value outside 1-40.</exception>
    /// <remarks>
    /// Lets a caller whose version is optional pass it through without branching. -1 is not
    /// accepted as a second spelling of <see cref="Any"/>: a mistyped or defaulted value
    /// must fail rather than silently produce an automatically sized symbol.
    /// </remarks>
    public static implicit operator QRCodeVersionRange(int? version) => version.HasValue ? Exactly(version.GetValueOrDefault()) : Any;

    /// <summary>Whether <paramref name="version"/> falls inside this range.</summary>
    public bool Contains(int version) => version >= Min && version <= Max;

    /// <inheritdoc/>
    public override string ToString() => IsExact ? $"v{Min}" : $"v{Min}-v{Max}";

    private static void ValidateBound(int version, string paramName)
    {
        if (version < MinVersion || version > MaxVersion)
            throw new ArgumentOutOfRangeException(paramName, $"Version must be {MinVersion}-{MaxVersion}, but was {version}");
    }
}
