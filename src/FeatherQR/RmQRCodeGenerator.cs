using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;

namespace FeatherQR;

/// <summary>
/// Encodes text into a rectangular rMQR code (ISO/IEC 23941), versions R7x43 to R17x139.
/// </summary>
/// <remarks>
/// Pick a version outright, or let <see cref="RmQRFitStrategy"/> choose among those that hold the content, optionally within one height.
/// The default <see cref="RmQRFitStrategy.MinimizeArea"/> takes the fewest modules, which can mean a taller and narrower code: 12 digits at level M give R11x27 (297 modules) rather than R7x43 (301).
/// Use <see cref="RmQRFitStrategy.MinimizeHeight"/> or a fixed <see cref="RmQRHeight"/> when you want the flattest code instead.
/// Writes Numeric, Alphanumeric and Byte mode, the last with ECI assignment 3 for ISO-8859-1 or 26 for UTF-8.
/// Kanji mode is never written, so Japanese text goes out as UTF-8; <see cref="RmQRCodeDecoder"/> does read Kanji that other encoders produce.
/// The quiet zone defaults to the 2 modules ISO/IEC 23941 asks for.
/// </remarks>
public static class RmQRCodeGenerator
{
    // -----------------------------------------------------
    // rMQR Data Structure
    // -----------------------------------------------------
    //
    // 1. Header
    // ┌─────────────────┬───────────────┬────────────────┐
    // │ ECI (0 or 11b)  │ Mode (3b)     │ Count (per ver)│
    // └─────────────────┴───────────────┴────────────────┘
    //   The ECI header is 11 bits, not Standard QR's 12: the rMQR mode
    //   indicator is 3 bits wide, so it is 3 + 8 designator bits.
    // 2. Data
    // ┌──────────────────────────────────────────────────┐
    // │ Encoded data (variable length)                   │
    // └──────────────────────────────────────────────────┘
    // 3. Padding
    // ┌──────┬────────┬──────────────────────────────────┐
    // │ Term │ Align  │ Pad bytes (0xEC, 0x11...)        │
    // │ (3b) │ (0-7b) │ (until dataCapacityBits reached) │
    // └──────┴────────┴──────────────────────────────────┘
    // 4. Final message (what actually reaches the matrix)
    // ┌────────────────────────────────┬─────────────────┐
    // │ Interleaved data + ECC blocks  │ Remainder bits  │
    // └────────────────────────────────┴─────────────────┘

    private const int MaxDataCodewords = 152;   // R17x139-M
    private const int MaxFinalMessageBytes = 233; // R17x139: 232 codewords + remainder byte

    /// <summary>
    /// Encodes text into an rMQR code.
    /// </summary>
    /// <param name="textSpan">The text to encode. A <see cref="string"/> converts implicitly.</param>
    /// <param name="eccLevel">How much damage the rMQR code should survive, M or H.</param>
    /// <param name="options">Version, fit, ECI, quiet zone and segmentation settings. Omit for the defaults.</param>
    /// <returns>The module matrix.</returns>
    /// <exception cref="ArgumentException">Thrown when the content does not fit, or when the options contradict each other.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined option value.</exception>
    public static RmQRCodeData Create(ReadOnlySpan<char> textSpan, RmQREccLevel eccLevel, in RmQRCodeGeneratorOptions options = default)
    {
        // rMQR generation process:
        // ------------------------------------------------
        // 1. Validate input parameters (quiet zone size, version/height agreement)
        // 2. Prepare configuration:
        //    - Analyze text to determine the encoding mode and the ECI to declare
        //    - Select the version: a pinned RmQRVersion, or the best fit among those
        //      that hold the content by RmQRFitStrategy, optionally within one RmQRHeight
        // 3. Encode data codewords:
        //    - Write the ECI header (11 bits) when a charset is declared
        //    - Write mode indicator (3 bits) and character count indicator
        //    - Write the data bits
        //    - Add padding (terminator, byte alignment, 0xEC/0x11 pattern)
        // 4. Assemble the final message:
        //    - Reed-Solomon per block, then interleave data and ECC codewords
        //    - Append the version's remainder bits
        // 5. Place the symbol:
        //    - Place fixed patterns (finder 7x7 top-left, sub-finder 5x5 bottom-right,
        //      timing on all four edges, corner patterns, vertical timing columns with
        //      their 3x3 alignment patterns)
        //    - Place the final message in the two-column zigzag, walking column pairs
        //      leftward from the right edge
        //    - Apply THE mask: rMQR defines one fixed pattern, dark where
        //      ((row / 2) + (col / 3)) is even, so there is nothing to score or select
        //    - Place both format information copies (finder side and sub-finder side)
        // 6. Return RmQRCodeData (quiet zone handled by RmQRCodeData class)

        var quietZoneSize = options.QuietZoneSize;
        ValidateQuietZone(quietZoneSize);
        // One compare on the default path; validation of the value itself lives in the
        // cold method so Single costs a predicted not-taken branch and nothing else.
        if (options.Segmentation != RmQRSegmentation.Single)
            return CreateOptimal(textSpan, eccLevel, options.EciMode, options.Version, options.FitStrategy, options.Height, quietZoneSize, options.Segmentation);

        var config = PrepareConfiguration(textSpan, eccLevel, options.EciMode, options.Version, options.FitStrategy, options.Height);
        var result = new RmQRCodeData(config.Version, quietZoneSize);
        var coreWidth = result.GetCoreWidth();
        var coreHeight = result.GetCoreHeight();
        var coreLength = coreWidth * coreHeight;

        // Core matrix up to 17 × 139 = 2,363 bytes: rented, not stack (same policy as
        // the Standard QR generator), returned in finally, never escapes.
        var rented = ArrayPool<byte>.Shared.Rent(coreLength);
        try
        {
            var core = rented.AsSpan(0, coreLength);
            WriteCoreModules(textSpan, in config, core, coreWidth);
            result.SetCoreData(core);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary>
    /// Encodes text into the buffer you provide, without allocating.
    /// </summary>
    /// <remarks>
    /// One byte per module, 0 light and 1 dark, row-major over the full width with the quiet zone included, so the module at (row, col) is <c>destination[row * width + col]</c>.
    /// </remarks>
    /// <param name="textSpan">The text to encode. A <see cref="string"/> converts implicitly.</param>
    /// <param name="eccLevel">How much damage the rMQR code should survive, M or H.</param>
    /// <param name="destination">Where to write the matrix. Needs <see cref="RmQRCodeCalculatedSize.BufferSize"/> bytes, as reported by <see cref="TryGetRequiredBufferSize"/>.</param>
    /// <param name="options">Version, fit, ECI, quiet zone and segmentation settings. Size <paramref name="destination"/> with the same options.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is too small, when the content does not fit, or when the options contradict each other.</exception>
    public static int Create(ReadOnlySpan<char> textSpan, RmQREccLevel eccLevel, Span<byte> destination, in RmQRCodeGeneratorOptions options = default)
    {
        var quietZoneSize = options.QuietZoneSize;
        ValidateQuietZone(quietZoneSize);
        if (options.Segmentation != RmQRSegmentation.Single)
            return CreateOptimalTo(textSpan, eccLevel, options.EciMode, destination, options.Version, options.FitStrategy, options.Height, quietZoneSize, options.Segmentation);

        var config = PrepareConfiguration(textSpan, eccLevel, options.EciMode, options.Version, options.FitStrategy, options.Height);
        var coreWidth = RmQRConstants.GetWidth(config.Version);
        var coreHeight = RmQRConstants.GetHeight(config.Version);
        var totalWidth = coreWidth + quietZoneSize * 2;
        var totalHeight = coreHeight + quietZoneSize * 2;
        var requiredSize = totalWidth * totalHeight;
        if (destination.Length < requiredSize)
            throw new ArgumentException($"Destination buffer too small: {requiredSize} bytes required (version {config.Version}, {totalWidth}x{totalHeight} modules), got {destination.Length} bytes. Use {nameof(TryGetRequiredBufferSize)} to calculate the required size.", nameof(destination));

        var target = destination.Slice(0, requiredSize);
        if (quietZoneSize == 0)
        {
            // The placer writes every core module, no clear needed.
            WriteCoreModules(textSpan, in config, target, coreWidth);
            return requiredSize;
        }

        // Quiet zone: light rows above and below, light margins on every core row; the
        // placer writes the core straight into the strided window in between (no
        // intermediate core buffer, no row copies).
        var margin = quietZoneSize * totalWidth;
        target.Slice(0, margin + quietZoneSize).Clear();                          // top rows + first row's left margin
        for (var row = 1; row < coreHeight; row++)
        {
            // right margin of row - 1 and left margin of row are contiguous
            target.Slice(margin + row * totalWidth - quietZoneSize, 2 * quietZoneSize).Clear();
        }
        target.Slice(margin + coreHeight * totalWidth - quietZoneSize).Clear();     // last row's right margin + bottom rows
        WriteCoreModules(textSpan, in config, target.Slice(margin + quietZoneSize), totalWidth);

        return requiredSize;
    }

    /// <summary>
    /// Reports how large the rMQR code will be, or <c>false</c> when the content does not fit.
    /// </summary>
    /// <param name="text">The text to encode.</param>
    /// <param name="eccLevel">How much damage the rMQR code should survive, M or H.</param>
    /// <param name="size">The size on success, <c>default</c> when the content does not fit.</param>
    /// <param name="options">Version, fit, ECI, quiet zone and segmentation settings.</param>
    /// <returns><c>true</c> when the content fits.</returns>
    /// <remarks>
    /// <para>
    /// <c>false</c> means that and only that; bad arguments still throw. rMQR holds 5 to 150 bytes, so not fitting is an ordinary answer here, which is why sizing is only offered in this form.
    /// </para>
    /// <para>
    /// Pass the options you will encode with, since segmentation and ECI can pick different versions and a buffer sized for one can be too small for the other.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <see cref="RmQRCodeGeneratorOptions.Version"/> and <see cref="RmQRCodeGeneratorOptions.Height"/> disagree, or when <see cref="EciMode.Iso8859_1"/> is declared over content that is not Latin-1.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an out-of-range quiet zone, or an undefined level, version, fit strategy or segmentation.</exception>
    public static bool TryGetRequiredBufferSize(ReadOnlySpan<char> text, RmQREccLevel eccLevel, out RmQRCodeCalculatedSize size, in RmQRCodeGeneratorOptions options = default)
    {
        size = default;
        var quietZoneSize = options.QuietZoneSize;
        ValidateQuietZone(quietZoneSize);

        bool fits;
        RmQRVersion version;
        if (options.Segmentation != RmQRSegmentation.Single)
        {
            fits = TryPlanOptimalVersion(text, eccLevel, options.EciMode, options.Version, options.FitStrategy, options.Height, options.Segmentation, out version);
        }
        else
        {
            ValidateEci(text, options.EciMode);
            var analysis = TextAnalyzer.Analyze(text, options.EciMode);
            fits = RmQRVersionSelector.TrySelect(analysis.EncodingMode, analysis.DataLength, analysis.EciMode, eccLevel, options.Version, options.FitStrategy, options.Height, out version);
        }

        if (!fits)
            return false;

        var totalWidth = RmQRConstants.GetWidth(version) + quietZoneSize * 2;
        var totalHeight = RmQRConstants.GetHeight(version) + quietZoneSize * 2;
        size = new RmQRCodeCalculatedSize(totalWidth * totalHeight, totalWidth, totalHeight, version);
        return true;
    }

    /// <summary>
    /// Mixed-mode fit without the "content is too long" throw.
    /// Mirrors <see cref="PrepareConfigurationOptimal"/>, fallback included, so the version reported here is the version an encode would use.
    /// </summary>
    private static bool TryPlanOptimalVersion(ReadOnlySpan<char> textSpan, RmQREccLevel eccLevel, EciMode eciMode, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height, RmQRSegmentation segmentation, out RmQRVersion version)
    {
        ValidateOptimalEntry(textSpan, eciMode, segmentation);

        var analysis = TextAnalyzer.Analyze(textSpan, eciMode);
        if (!RmQRSegmentPlanner.TrySelectVersion(textSpan, in analysis, eccLevel, requestedVersion, fitStrategy, height, out version, out var useSegments))
            return false;

        if (!useSegments)
            return true;

        Span<ModeSegment> plan = stackalloc ModeSegment[RmQRSegmentPlanner.MaxSegments];
        if (RmQRSegmentPlanner.TryBuildPlan(textSpan, analysis.EciMode, version, eccLevel, plan, out _))
            return true;

        return RmQRVersionSelector.TrySelect(analysis.EncodingMode, analysis.DataLength, analysis.EciMode, eccLevel, requestedVersion, fitStrategy, height, out version);
    }

    private static void ValidateQuietZone(int quietZoneSize)
    {
        // (139 + 2·qz) × (17 + 2·qz) must stay far below int.MaxValue; 10000 modules of
        // quiet zone is already absurd, so a simple hard cap keeps the math safe.
        if (quietZoneSize < 0 || quietZoneSize > 10_000)
            throw new ArgumentOutOfRangeException(nameof(quietZoneSize), $"Quiet zone size must be 0-10000, got {quietZoneSize}");
    }

    /// <summary>
    /// Everything the mixed-mode entry points must reject, gathered off the default path so <see cref="RmQRSegmentation.Single"/> pays only one compare.
    /// </summary>
    private static void ValidateOptimalEntry(ReadOnlySpan<char> textSpan, EciMode eciMode, RmQRSegmentation segmentation)
    {
        if (segmentation != RmQRSegmentation.Optimal)
            throw new ArgumentOutOfRangeException(nameof(segmentation), $"Invalid rMQR segmentation: {segmentation}");
        ValidateEci(textSpan, eciMode);
    }

    private static void ValidateEci(ReadOnlySpan<char> textSpan, EciMode eciMode)
    {
        if (eciMode is not (EciMode.Default or EciMode.Iso8859_1 or EciMode.Utf8))
            throw new ArgumentOutOfRangeException(nameof(eciMode), $"Unsupported ECI mode for rMQR: {eciMode}");
        if (eciMode == EciMode.Iso8859_1 && !CharacterSets.IsValidISO88591(textSpan))
            throw new ArgumentException("The content contains characters that cannot be represented by ISO-8859-1. Use EciMode.Utf8 or EciMode.Default.", nameof(eciMode));
    }

    /// <summary>Analyzes the text under the requested ECI and selects / validates the version.</summary>
    private static RmQRConfiguration PrepareConfiguration(ReadOnlySpan<char> textSpan, RmQREccLevel eccLevel, EciMode eciMode, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height)
    {
        ValidateEci(textSpan, eciMode);

        // Default resolves to no ECI for ASCII, assignment 3 for Latin-1 beyond
        // ASCII, and assignment 26 for Unicode. DataLength is the encoded byte count.
        var analysis = TextAnalyzer.Analyze(textSpan, eciMode);
        var version = analysis.EciMode == EciMode.Default
            ? RmQRVersionSelector.Select(analysis.EncodingMode, analysis.DataLength, eccLevel, requestedVersion, fitStrategy, height)
            : RmQRVersionSelector.Select(analysis.EncodingMode, analysis.DataLength, analysis.EciMode, eccLevel, requestedVersion, fitStrategy, height);
        return new RmQRConfiguration(version, eccLevel, analysis);
    }

    /// <summary>
    /// Runs encode → ECC + interleave → placement into a byte-per-module core window (width × height, rows <paramref name="stride"/> bytes apart, every core module written; stride == width for a packed core).
    /// Allocation-free: fixed stack budgets.
    /// </summary>
    private static void WriteCoreModules(ReadOnlySpan<char> textSpan, in RmQRConfiguration config, Span<byte> core, int stride)
    {
        // Dispatch on the *resolved* ECI, not the requested one: an explicit
        // Iso8859_1 / Utf8 is carried through analysis verbatim and never collapses to
        // Default, so the no-ECI writer is reached only when no ECI is to be emitted.
        if (config.Analysis.EciMode == EciMode.Default)
            WriteCoreModulesWithoutEci(textSpan, in config, core, stride);
        else
            WriteCoreModulesWithEci(textSpan, in config, core, stride);
    }

    private static void WriteCoreModulesWithoutEci(ReadOnlySpan<char> textSpan, in RmQRConfiguration config, Span<byte> core, int stride)
    {
        Span<byte> dataCodewords = stackalloc byte[MaxDataCodewords];
        var analysis = config.Analysis;
        Debug.Assert(analysis.EciMode == EciMode.Default);
        var dataCount = RmQRBinaryEncoder.EncodeDataCodewordsWithoutEci(textSpan, config.Version, config.EccLevel, in analysis, dataCodewords);

        Span<byte> finalMessage = stackalloc byte[MaxFinalMessageBytes];
        finalMessage = finalMessage.Slice(0, RmQRCodewordEncoder.GetFinalMessageSize(config.Version));
        RmQRCodewordEncoder.AssembleFinalMessage(dataCodewords.Slice(0, dataCount), config.Version, config.EccLevel, finalMessage);

        RmQRModulePlacer.PlaceSymbol(core, stride, config.Version, config.EccLevel, finalMessage);
    }

    private static void WriteCoreModulesWithEci(ReadOnlySpan<char> textSpan, in RmQRConfiguration config, Span<byte> core, int stride)
    {
        Span<byte> dataCodewords = stackalloc byte[MaxDataCodewords];
        var analysis = config.Analysis;
        var dataCount = RmQRBinaryEncoder.EncodeDataCodewords(textSpan, config.Version, config.EccLevel, in analysis, dataCodewords);

        Span<byte> finalMessage = stackalloc byte[MaxFinalMessageBytes];
        finalMessage = finalMessage.Slice(0, RmQRCodewordEncoder.GetFinalMessageSize(config.Version));
        RmQRCodewordEncoder.AssembleFinalMessage(dataCodewords.Slice(0, dataCount), config.Version, config.EccLevel, finalMessage);

        RmQRModulePlacer.PlaceSymbol(core, stride, config.Version, config.EccLevel, finalMessage);
    }

    // ---------------------------------------------------------------
    // Mixed-mode segmentation (RmQRSegmentation.Optimal).
    //
    // Kept in its own non-inlined methods so the single-mode entry points above keep
    // their frame and codegen. The plan buffer lives here rather than in the planner
    // because a plan is a caller-lent Span<ModeSegment> that never escapes.
    // ---------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RmQRCodeData CreateOptimal(ReadOnlySpan<char> textSpan, RmQREccLevel eccLevel, EciMode eciMode, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height, int quietZoneSize, RmQRSegmentation segmentation)
    {
        ValidateOptimalEntry(textSpan, eciMode, segmentation);
        Span<ModeSegment> plan = stackalloc ModeSegment[RmQRSegmentPlanner.MaxSegments];
        var config = PrepareConfigurationOptimal(textSpan, eccLevel, eciMode, requestedVersion, fitStrategy, height, plan, out var segmentCount);
        var segments = plan.Slice(0, segmentCount);

        var result = new RmQRCodeData(config.Version, quietZoneSize);
        var coreWidth = result.GetCoreWidth();
        var coreLength = coreWidth * result.GetCoreHeight();
        var rented = ArrayPool<byte>.Shared.Rent(coreLength);
        try
        {
            var core = rented.AsSpan(0, coreLength);
            WriteCoreModulesPlanned(textSpan, in config, segments, core, coreWidth);
            result.SetCoreData(core);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CreateOptimalTo(ReadOnlySpan<char> textSpan, RmQREccLevel eccLevel, EciMode eciMode, Span<byte> destination, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height, int quietZoneSize, RmQRSegmentation segmentation)
    {
        ValidateOptimalEntry(textSpan, eciMode, segmentation);
        Span<ModeSegment> plan = stackalloc ModeSegment[RmQRSegmentPlanner.MaxSegments];
        var config = PrepareConfigurationOptimal(textSpan, eccLevel, eciMode, requestedVersion, fitStrategy, height, plan, out var segmentCount);
        var segments = plan.Slice(0, segmentCount);

        var coreWidth = RmQRConstants.GetWidth(config.Version);
        var coreHeight = RmQRConstants.GetHeight(config.Version);
        var totalWidth = coreWidth + quietZoneSize * 2;
        var totalHeight = coreHeight + quietZoneSize * 2;
        var requiredSize = totalWidth * totalHeight;
        if (destination.Length < requiredSize)
            throw new ArgumentException($"Destination buffer too small: {requiredSize} bytes required (version {config.Version}, {totalWidth}x{totalHeight} modules), got {destination.Length} bytes. Use {nameof(TryGetRequiredBufferSize)} to calculate the required size.", nameof(destination));

        var target = destination.Slice(0, requiredSize);
        if (quietZoneSize == 0)
        {
            WriteCoreModulesPlanned(textSpan, in config, segments, target, coreWidth);
            return requiredSize;
        }

        var margin = quietZoneSize * totalWidth;
        target.Slice(0, margin + quietZoneSize).Clear();
        for (var row = 1; row < coreHeight; row++)
            target.Slice(margin + row * totalWidth - quietZoneSize, 2 * quietZoneSize).Clear();
        target.Slice(margin + coreHeight * totalWidth - quietZoneSize).Clear();
        WriteCoreModulesPlanned(textSpan, in config, segments, target.Slice(margin + quietZoneSize), totalWidth);
        return requiredSize;
    }

    /// <summary>
    /// Analyzes the content, fits a version under mixed-mode segmentation, and writes the plan.
    /// A zero <paramref name="segmentCount"/> means the single-mode stream is what gets emitted, which is the case whenever mixing would not shrink the rMQR code.
    /// </summary>
    private static RmQRConfiguration PrepareConfigurationOptimal(ReadOnlySpan<char> textSpan, RmQREccLevel eccLevel, EciMode eciMode, RmQRVersion? requestedVersion, RmQRFitStrategy fitStrategy, RmQRHeight? height, Span<ModeSegment> plan, out int segmentCount)
    {
        var analysis = TextAnalyzer.Analyze(textSpan, eciMode);
        var version = RmQRSegmentPlanner.SelectVersion(textSpan, in analysis, eccLevel, requestedVersion, fitStrategy, height, out var useSegments);
        segmentCount = 0;

        if (useSegments && !RmQRSegmentPlanner.TryBuildPlan(textSpan, analysis.EciMode, version, eccLevel, plan, out segmentCount))
        {
            // The plan for this version could not be built: it needed more runs
            // than the buffer holds, the decoder would misread it, the exact
            // re-cost disagreed with the dynamic program, or — for a requested
            // version, which the scan accepts unpriced — the content is unplannable
            // or the stream simply does not fit it. Fall back to the single-mode
            // fit, which throws the ordinary "content is too long" error when there
            // is no such fit — the honest outcome, because with the plan gone
            // nothing else can be emitted.
            segmentCount = 0;
            version = RmQRSegmentPlanner.SelectSingle(in analysis, eccLevel, requestedVersion, fitStrategy, height);
        }

        return new RmQRConfiguration(version, eccLevel, analysis);
    }

    private static void WriteCoreModulesPlanned(ReadOnlySpan<char> textSpan, in RmQRConfiguration config, ReadOnlySpan<ModeSegment> segments, Span<byte> core, int stride)
    {
        if (segments.Length == 0)
        {
            WriteCoreModules(textSpan, in config, core, stride);
            return;
        }

        Span<byte> dataCodewords = stackalloc byte[MaxDataCodewords];
        var dataCount = RmQRBinaryEncoder.EncodeDataCodewordsSegmented(textSpan, config.Version, config.EccLevel, config.Analysis.EciMode, segments, dataCodewords);

        Span<byte> finalMessage = stackalloc byte[MaxFinalMessageBytes];
        finalMessage = finalMessage.Slice(0, RmQRCodewordEncoder.GetFinalMessageSize(config.Version));
        RmQRCodewordEncoder.AssembleFinalMessage(dataCodewords.Slice(0, dataCount), config.Version, config.EccLevel, finalMessage);

        RmQRModulePlacer.PlaceSymbol(core, stride, config.Version, config.EccLevel, finalMessage);
    }

    private readonly record struct RmQRConfiguration(RmQRVersion Version, RmQREccLevel EccLevel, TextAnalysisResult Analysis);
}
