namespace FeatherQR.Internals.RmQr;

/// <summary>
/// rMQR version fit (ISO/IEC 23941 capacities, design record
/// specs/rmqr-encoder.md): exact requested version, or the best of the versions
/// that hold the content according to an <see cref="RmQRFitStrategy"/>, optionally
/// restricted to one <see cref="RmQRHeight"/>. Also owns the capacity arithmetic
/// (required bits per mode, its inverse for error messages).
/// </summary>
internal static class RmQRVersionSelector
{
    // rMQR ECI prefix for the assignments exposed by EciMode: 3-bit mode 111
    // followed by the one-byte assignment designator (3 or 26).
    private const int EciHeaderBits = RmQRConstants.ModeIndicatorLength + 8;

    /// <summary>
    /// Total bit count for header (3-bit mode + count indicator) plus data. The
    /// count indicator range never binds below the bit capacity for any
    /// version/mode (verified by RmQRConstantsUnitTest), so no range check is needed.
    /// </summary>
    /// <remarks>
    /// Priced in <see cref="long"/>: Byte mode costs <c>8 × dataLength</c>, which wraps
    /// <see cref="int"/> for a span past ~268M units and would read as a fit. Widening
    /// keeps the mode switch on every path, which a length fast-path in
    /// <see cref="Fits(RmQRVersion, RmQREccLevel, EncodingMode, int)"/> would skip for
    /// exactly the lengths that need it most.
    /// </remarks>
    public static long GetRequiredBits(RmQRVersion version, EncodingMode mode, int dataLength)
    {
        long headerBits = RmQRConstants.ModeIndicatorLength + RmQRConstants.GetCountIndicatorLength(version, mode);
        var dataBits = mode switch
        {
            EncodingMode.Numeric => dataLength / 3 * 10L + (dataLength % 3) switch { 2 => 7, 1 => 4, _ => 0 },
            EncodingMode.Alphanumeric => dataLength / 2 * 11L + dataLength % 2 * 6,
            EncodingMode.Byte => dataLength * 8L,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Encoding mode {mode} is not supported by rMQR."),
        };
        return headerBits + dataBits;
    }

    /// <inheritdoc cref="GetRequiredBits(RmQRVersion, EncodingMode, int)"/>
    /// <summary>
    /// Total bit count including the optional rMQR ECI prefix. The supported
    /// ISO-8859-1 and UTF-8 assignments both use the one-byte designator form.
    /// </summary>
    public static long GetRequiredBits(RmQRVersion version, EncodingMode mode, int dataLength, EciMode eciMode)
    {
        long headerBits = GetEciHeaderBits(eciMode) + RmQRConstants.ModeIndicatorLength + RmQRConstants.GetCountIndicatorLength(version, mode);
        var dataBits = mode switch
        {
            EncodingMode.Numeric => dataLength / 3 * 10L + (dataLength % 3) switch { 2 => 7, 1 => 4, _ => 0 },
            EncodingMode.Alphanumeric => dataLength / 2 * 11L + dataLength % 2 * 6,
            EncodingMode.Byte => dataLength * 8L,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Encoding mode {mode} is not supported by rMQR."),
        };
        return headerBits + dataBits;
    }

    /// <summary>Whether <paramref name="dataLength"/> units of <paramref name="mode"/> fit the version at the ECC level.</summary>
    public static bool Fits(RmQRVersion version, RmQREccLevel eccLevel, EncodingMode mode, int dataLength)
        => GetRequiredBits(version, mode, dataLength) <= 8 * RmQRConstants.GetDataCodewordCount(version, eccLevel);

    /// <summary>Whether the data and optional ECI prefix fit the version at the ECC level.</summary>
    public static bool Fits(RmQRVersion version, RmQREccLevel eccLevel, EncodingMode mode, int dataLength, EciMode eciMode)
        => GetRequiredBits(version, mode, dataLength, eciMode) <= 8 * RmQRConstants.GetDataCodewordCount(version, eccLevel);

    /// <summary>
    /// Largest data length (digits / characters / bytes) that fits a version × ECC × mode,
    /// the inverse of <see cref="GetRequiredBits(RmQRVersion, EncodingMode, int)"/> against
    /// the data bit capacity.
    /// </summary>
    public static int GetMaxDataLength(RmQRVersion version, RmQREccLevel eccLevel, EncodingMode mode)
    {
        var headerBits = RmQRConstants.ModeIndicatorLength + RmQRConstants.GetCountIndicatorLength(version, mode);
        var dataBits = 8 * RmQRConstants.GetDataCodewordCount(version, eccLevel) - headerBits;
        if (dataBits <= 0)
            return 0;

        switch (mode)
        {
            case EncodingMode.Numeric:
                {
                    var groups = dataBits / 10;
                    var remainder = dataBits - groups * 10;
                    return groups * 3 + (remainder >= 7 ? 2 : remainder >= 4 ? 1 : 0);
                }
            case EncodingMode.Alphanumeric:
                {
                    var pairs = dataBits / 11;
                    var remainder = dataBits - pairs * 11;
                    return pairs * 2 + (remainder >= 6 ? 1 : 0);
                }
            default:
                return dataBits / 8;
        }
    }

    /// <summary>Largest data length after accounting for the optional ECI prefix.</summary>
    public static int GetMaxDataLength(RmQRVersion version, RmQREccLevel eccLevel, EncodingMode mode, EciMode eciMode)
    {
        var headerBits = GetEciHeaderBits(eciMode) + RmQRConstants.ModeIndicatorLength + RmQRConstants.GetCountIndicatorLength(version, mode);
        var dataBits = 8 * RmQRConstants.GetDataCodewordCount(version, eccLevel) - headerBits;
        if (dataBits <= 0)
            return 0;

        switch (mode)
        {
            case EncodingMode.Numeric:
                {
                    // 10 bits per 3-digit group; a 2-digit tail costs 7 bits, 1 digit costs 4
                    var groups = dataBits / 10;
                    var remainder = dataBits - groups * 10;
                    return groups * 3 + (remainder >= 7 ? 2 : remainder >= 4 ? 1 : 0);
                }
            case EncodingMode.Alphanumeric:
                {
                    // 11 bits per character pair; a single tail character costs 6 bits
                    var pairs = dataBits / 11;
                    var remainder = dataBits - pairs * 11;
                    return pairs * 2 + (remainder >= 6 ? 1 : 0);
                }
            default:
                return dataBits / 8;
        }
    }

    /// <summary>
    /// Fit-strategy comparator: true when <paramref name="candidate"/> should replace
    /// <paramref name="incumbent"/>. MinimizeArea: fewer modules, ties → smaller
    /// height; MinimizeWidth: smaller width, ties → smaller height; MinimizeHeight:
    /// smaller height, ties → smaller width. Equal versions never replace each other.
    /// </summary>
    public static bool IsBetter(RmQRVersion candidate, RmQRVersion incumbent, RmQRFitStrategy strategy)
    {
        var ch = RmQRConstants.GetHeight(candidate);
        var cw = RmQRConstants.GetWidth(candidate);
        var ih = RmQRConstants.GetHeight(incumbent);
        var iw = RmQRConstants.GetWidth(incumbent);

        switch (strategy)
        {
            case RmQRFitStrategy.MinimizeArea:
                {
                    var ca = ch * cw;
                    var ia = ih * iw;
                    return ca != ia ? ca < ia : ch < ih;
                }
            case RmQRFitStrategy.MinimizeWidth:
                return cw != iw ? cw < iw : ch < ih;
            case RmQRFitStrategy.MinimizeHeight:
                return ch != ih ? ch < ih : cw < iw;
            default:
                throw new ArgumentOutOfRangeException(nameof(strategy), $"Invalid rMQR fit strategy: {strategy}");
        }
    }

    /// <summary>
    /// Selects the version for the analyzed content. Validates every enum argument;
    /// throws <see cref="ArgumentException"/> with an actionable message (actual
    /// length, applicable maximum in mode units, remedy) when the content does not fit.
    /// </summary>
    public static RmQRVersion Select(EncodingMode mode, int dataLength, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height)
    {
        // Deliberately keep the pre-ECI hot path separate. Forwarding this overload
        // through the larger ECI selector measurably changes end-to-end JIT codegen.
        if (!RmQRConstants.IsValidEccLevel(eccLevel))
            throw new ArgumentOutOfRangeException(nameof(eccLevel), $"Invalid rMQR ECC level: {eccLevel}");
        if (fitStrategy is < RmQRFitStrategy.MinimizeArea or > RmQRFitStrategy.MinimizeHeight)
            throw new ArgumentOutOfRangeException(nameof(fitStrategy), $"Invalid rMQR fit strategy: {fitStrategy}");
        if (height is { } h && h is not (RmQRHeight.H7 or RmQRHeight.H9 or RmQRHeight.H11 or RmQRHeight.H13 or RmQRHeight.H15 or RmQRHeight.H17))
            throw new ArgumentOutOfRangeException(nameof(height), $"Invalid rMQR height: {height}");

        if (requestedVersion is { } version)
        {
            if (!RmQRConstants.IsValidVersion(version))
                throw new ArgumentOutOfRangeException(nameof(requestedVersion), $"Invalid rMQR version: {version}");
            if (height is { } requiredHeight && RmQRConstants.GetHeight(version) != (int)requiredHeight)
                throw new ArgumentException($"Requested rMQR version {version} is {RmQRConstants.GetHeight(version)} modules high, but height {requiredHeight} was requested. Specify one or the other, or make them agree.", nameof(height));
            if (!Fits(version, eccLevel, mode, dataLength))
                throw NotFittingError(mode, dataLength, EciMode.Default, eccLevel, requestedVersion, height);

            return version;
        }

        var capacities = FitCapacities[((RmQRConstants.GetModeIndex(mode) * 2 + (int)eccLevel) * 2) * 3 + (int)fitStrategy];
        var order = FitOrders[(int)fitStrategy];
        var heightMask = height is { } fitHeight ? FitHeightMasks[(int)fitStrategy][((int)fitHeight - 7) / 2] : uint.MaxValue;
        for (var j = 0; j < capacities.Length; j++)
        {
            if (capacities[j] >= dataLength && (heightMask & (1u << j)) != 0)
                return (RmQRVersion)order[j];
        }

        throw NotFittingError(mode, dataLength, EciMode.Default, eccLevel, requestedVersion, height);
    }

    /// <inheritdoc cref="Select(EncodingMode, int, RmQREccLevel, RmQRVersion?, RmQRFitStrategy, RmQRHeight?)"/>
    /// <summary>Selects a version while accounting for an optional ECI prefix.</summary>
    public static RmQRVersion Select(EncodingMode mode, int dataLength, EciMode eciMode, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height)
    {
        if (TrySelect(mode, dataLength, eciMode, eccLevel, requestedVersion, fitStrategy, height, out var version))
            return version;

        throw NotFittingError(mode, dataLength, eciMode, eccLevel, requestedVersion, height);
    }

    /// <summary>
    /// The actionable "content is too long" error, built off the success path: the
    /// applicable maximum in mode units, and which constraint produced it.
    /// </summary>
    private static ArgumentException NotFittingError(EncodingMode mode, int dataLength, EciMode eciMode, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRHeight? height)
    {
        if (requestedVersion is { } version)
        {
            return new ArgumentException(
                $"Content is too long for rMQR {version} at ECC level {eccLevel}: {FormatDataLength(dataLength, mode)} in {mode} mode, " +
                $"but the maximum is {FormatDataLength(GetMaxDataLength(version, eccLevel, mode, eciMode), mode)}. " +
                "Shorten the content, lower the ECC level, choose a larger version, or use Standard QR (QRCodeGenerator) for longer content.",
                nameof(requestedVersion));
        }

        // Most capacious candidate for the message; failure path only, so the success
        // path never pays for this scan.
        var largest = (RmQRVersion)0;
        var largestMax = -1;
        for (var i = 1; i <= RmQRConstants.VersionCount; i++)
        {
            var candidate = (RmQRVersion)i;
            if (height is { } wanted && RmQRConstants.GetHeight(candidate) != (int)wanted)
                continue;

            var candidateMax = GetMaxDataLength(candidate, eccLevel, mode, eciMode);
            if (candidateMax > largestMax)
            {
                largestMax = candidateMax;
                largest = candidate;
            }
        }

        var scope = height is { } hh ? $"rMQR height {hh}" : "rMQR";
        return new ArgumentException(
            $"Content is too long for {scope}: {FormatDataLength(dataLength, mode)} in {mode} mode, " +
            $"but ECC level {eccLevel} fits at most {FormatDataLength(largestMax, mode)} ({largest}). " +
            (height is null
                ? "Shorten the content, lower the ECC level, or use Standard QR (QRCodeGenerator) for longer content."
                : "Shorten the content, lower the ECC level, allow a taller symbol, or use Standard QR (QRCodeGenerator) for longer content."));
    }

    // ---------------------------------------------------------------
    // Auto-fit tables (built once at type init from IsBetter / GetMaxDataLength):
    //   FitOrders[strategy][rank]                       = version (1..32), best first
    //   FitCapacities[((mode*2+ecc)*2+eci)*3+strategy]  = that version's max data length
    //   FitHeightMasks[strategy][(height-7)/2]          = bit `rank` set when the version has that height
    // 3 × 32 B + 36 × 64 B + 3 × 24 B ≈ 2.5 KB of table data. Declaration order matters:
    // static field initializers run textually, and the two lower tables index FitOrders.
    // ---------------------------------------------------------------
    private static readonly byte[][] FitOrders = BuildFitOrders();
    private static readonly ushort[][] FitCapacities = BuildFitCapacities();
    private static readonly uint[][] FitHeightMasks = BuildFitHeightMasks();

    private static byte[][] BuildFitOrders()
    {
        var orders = new byte[3][];
        for (var s = 0; s < 3; s++)
        {
            // insertion sort under the strict IsBetter comparator: it is a total order
            // (equal area and height means the same version), so the ranking is unique
            var sorted = new List<byte>(RmQRConstants.VersionCount);
            for (var v = 1; v <= RmQRConstants.VersionCount; v++)
            {
                var pos = 0;
                while (pos < sorted.Count && !IsBetter((RmQRVersion)v, (RmQRVersion)sorted[pos], (RmQRFitStrategy)s)) pos++;
                sorted.Insert(pos, (byte)v);
            }
            orders[s] = sorted.ToArray();
        }
        return orders;
    }

    private static ushort[][] BuildFitCapacities()
    {
        var tables = new ushort[RmQRConstants.ModeCount * 2 * 2 * 3][];
        foreach (var mode in new[] { EncodingMode.Numeric, EncodingMode.Alphanumeric, EncodingMode.Byte })
            for (var e = 0; e < 2; e++)
                for (var eci = 0; eci < 2; eci++)
                    for (var s = 0; s < 3; s++)
                    {
                        var t = new ushort[RmQRConstants.VersionCount];
                        var eciMode = eci == 0 ? EciMode.Default : EciMode.Utf8;
                        for (var rank = 0; rank < RmQRConstants.VersionCount; rank++)
                            t[rank] = (ushort)GetMaxDataLength((RmQRVersion)FitOrders[s][rank], (RmQREccLevel)e, mode, eciMode);
                        tables[((RmQRConstants.GetModeIndex(mode) * 2 + e) * 2 + eci) * 3 + s] = t; // same index function as Select
                    }
        return tables;
    }

    private static uint[][] BuildFitHeightMasks()
    {
        var masks = new uint[3][];
        for (var s = 0; s < 3; s++)
        {
            masks[s] = new uint[6];
            for (var rank = 0; rank < RmQRConstants.VersionCount; rank++)
                masks[s][(RmQRConstants.GetHeight((RmQRVersion)FitOrders[s][rank]) - 7) / 2] |= 1u << rank;
        }
        return masks;
    }

    /// <summary>
    /// Non-throwing automatic fit: the same table scan the <c>Select</c> overloads run,
    /// reporting "nothing fits" instead of throwing. <see cref="RmQRSegmentPlanner"/>
    /// needs the answer without the exception, because content that overflows every
    /// version in one mode can still fit once the modes are mixed. Arguments must
    /// already be validated (see <see cref="ValidateFitArguments"/>).
    /// </summary>
    public static bool TrySelectAutoFit(EncodingMode mode, int dataLength, EciMode eciMode, RmQREccLevel eccLevel, RmQRFitStrategy fitStrategy, RmQRHeight? height, out RmQRVersion version)
    {
        var eciIndex = eciMode == EciMode.Default ? 0 : 1;
        var capacities = FitCapacities[((RmQRConstants.GetModeIndex(mode) * 2 + (int)eccLevel) * 2 + eciIndex) * 3 + (int)fitStrategy];
        var order = FitOrders[(int)fitStrategy];
        var heightMask = GetFitHeightMask(fitStrategy, height);
        for (var j = 0; j < capacities.Length; j++)
        {
            if (capacities[j] >= dataLength && (heightMask & (1u << j)) != 0)
            {
                version = (RmQRVersion)order[j];
                return true;
            }
        }

        version = default;
        return false;
    }

    /// <summary>
    /// <c>Select</c> without the capacity throw: same argument validation, but a content
    /// that does not fit returns false with <paramref name="version"/> at <c>default</c>.
    /// </summary>
    public static bool TrySelect(EncodingMode mode, int dataLength, EciMode eciMode, RmQREccLevel eccLevel, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height, out RmQRVersion version)
    {
        ValidateFitArguments(eccLevel, fitStrategy, height, requestedVersion, eciMode);

        if (requestedVersion is { } requested)
        {
            if (!Fits(requested, eccLevel, mode, dataLength, eciMode))
            {
                version = default;
                return false;
            }

            version = requested;
            return true;
        }

        return TrySelectAutoFit(mode, dataLength, eciMode, eccLevel, fitStrategy, height, out version);
    }

    /// <summary>
    /// The auto-fit scan order for a strategy: version numbers (1-32), best first.
    /// Exposed for <see cref="RmQRSegmentPlanner"/>, which walks the same ranking but
    /// with a per-version bit cost that no table can precompute.
    /// </summary>
    public static ReadOnlySpan<byte> GetFitOrder(RmQRFitStrategy fitStrategy) => FitOrders[(int)fitStrategy];

    /// <summary>
    /// Rank mask for a height constraint over <see cref="GetFitOrder"/>: bit
    /// <c>rank</c> is set when the version at that rank has the requested height.
    /// All bits are set when the height is unconstrained.
    /// </summary>
    public static uint GetFitHeightMask(RmQRFitStrategy fitStrategy, RmQRHeight? height)
        => height is { } h ? FitHeightMasks[(int)fitStrategy][((int)h - 7) / 2] : uint.MaxValue;

    /// <summary>
    /// The argument validation both <c>Select</c> overloads perform before they look
    /// at capacity, in the same order, so a caller that needs to try something else
    /// before letting <c>Select</c> throw still reports argument errors identically.
    /// </summary>
    public static void ValidateFitArguments(RmQREccLevel eccLevel, RmQRFitStrategy fitStrategy, RmQRHeight? height, RmQRVersion? requestedVersion, EciMode eciMode)
    {
        _ = GetEciHeaderBits(eciMode);
        if (!RmQRConstants.IsValidEccLevel(eccLevel))
            throw new ArgumentOutOfRangeException(nameof(eccLevel), $"Invalid rMQR ECC level: {eccLevel}");
        if (fitStrategy is < RmQRFitStrategy.MinimizeArea or > RmQRFitStrategy.MinimizeHeight)
            throw new ArgumentOutOfRangeException(nameof(fitStrategy), $"Invalid rMQR fit strategy: {fitStrategy}");
        if (height is { } h && h is not (RmQRHeight.H7 or RmQRHeight.H9 or RmQRHeight.H11 or RmQRHeight.H13 or RmQRHeight.H15 or RmQRHeight.H17))
            throw new ArgumentOutOfRangeException(nameof(height), $"Invalid rMQR height: {height}");
        if (requestedVersion is { } version)
        {
            if (!RmQRConstants.IsValidVersion(version))
                throw new ArgumentOutOfRangeException(nameof(requestedVersion), $"Invalid rMQR version: {version}");
            if (height is { } requiredHeight && RmQRConstants.GetHeight(version) != (int)requiredHeight)
                throw new ArgumentException($"Requested rMQR version {version} is {RmQRConstants.GetHeight(version)} modules high, but height {requiredHeight} was requested. Specify one or the other, or make them agree.", nameof(height));
        }
    }

    /// <summary>Human unit per mode: Numeric counts digits, Alphanumeric characters, Byte encoded bytes.</summary>
    private static string FormatDataLength(int dataLength, EncodingMode mode) => mode switch
    {
        EncodingMode.Numeric => $"{dataLength} digits",
        EncodingMode.Alphanumeric => $"{dataLength} characters",
        _ => $"{dataLength} bytes",
    };

    private static int GetEciHeaderBits(EciMode eciMode) => eciMode switch
    {
        EciMode.Default => 0,
        EciMode.Iso8859_1 or EciMode.Utf8 => EciHeaderBits,
        _ => throw new ArgumentOutOfRangeException(nameof(eciMode), $"Unsupported ECI mode for rMQR: {eciMode}"),
    };
}
