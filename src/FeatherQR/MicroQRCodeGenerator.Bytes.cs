#if NET5_0_OR_GREATER
using System.Text;
#endif
using FeatherQR.Internals;
using FeatherQR.Internals.MicroQR;

namespace FeatherQR;

public static partial class MicroQRCodeGenerator
{
    // Bytes take the text pipeline widened one to a char, U+0000 to U+00FF, analyzed under the default charset, so every writer narrows such a char back to its byte.
    // The text route's rewrite into UTF-8 of text the decoder would read otherwise is left out: there is no ECI to declare anything, and the bytes are what the caller gave.
    // The bytes have a route of their own past the analysis. Sharing the text's would put a call on the text's path, and bytes never take a Kanji plan.

    /// <summary>
    /// Encodes bytes into a Micro QR code, exactly as given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bytes are the bytes in the symbol. Runs of digits may be written in Numeric mode and runs of uppercase letters in Alphanumeric mode, which carry the same ASCII bytes in fewer bits, the way text takes those modes.
    /// Micro QR has no ECI, so nothing says how the bytes are interpreted. A reader that returns text guesses from the bytes, and the bytes calls of <see cref="MicroQRCodeDecoder"/> return them as written.
    /// Kanji mode is not part of the bytes and is not written again. Kanji pairs take 16 bits each in Byte mode, where Kanji mode takes 13, so they can need a larger symbol.
    /// </para>
    /// <para>
    /// <see cref="MicroQRCodeGeneratorOptions.Version"/>, <see cref="MicroQRCodeGeneratorOptions.QuietZoneSize"/>, <see cref="MicroQRCodeGeneratorOptions.MaskPattern"/> and <see cref="MicroQRCodeGeneratorOptions.Segmentation"/> apply as they do to text.
    /// <see cref="MicroQRCodeGeneratorOptions.AllowKanji"/>, which means something only for text, is refused.
    /// </para>
    /// </remarks>
    /// <param name="data">The bytes to encode. An array converts implicitly. An empty payload needs a typed argument, such as <c>ReadOnlySpan&lt;byte&gt;.Empty</c>, since <c>[]</c>, <c>default</c> and <c>null</c> fit the text overload too.</param>
    /// <param name="eccLevel">How much damage the Micro QR code can survive. M1 accepts <see cref="MicroQREccLevel.ErrorDetectionOnly"/> alone, and Q is available on M4 alone.</param>
    /// <param name="options">Version, quiet zone, mask and segmentation settings. Omit for the defaults.</param>
    /// <returns>The module matrix.</returns>
    /// <exception cref="ArgumentException">Thrown when the content does not fit, when the version, level and mode cannot be combined, or when the options carry <see cref="MicroQRCodeGeneratorOptions.AllowKanji"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined level or segmentation, or a quiet zone outside 0 to 10,000.</exception>
    public static MicroQRCodeData Create(ReadOnlySpan<byte> data, MicroQREccLevel eccLevel, in MicroQRCodeGeneratorOptions options = default)
    {
        ValidateBytes(in options);
        if (data.Length > MicroQRSegmentPlanner.MaxPlannableChars)
            throw BytesDoNotFit(data.Length, eccLevel, options.Version);

        Span<char> buffer = stackalloc char[MicroQRSegmentPlanner.MaxPlannableChars];
        var widened = Widen(data, buffer);
        var analysis = AnalyzeBytes(widened);
        Span<ModeSegment> plan = stackalloc ModeSegment[MicroQRSegmentPlanner.MaxPlannableChars];
        if (!TryPlanBytes(widened, eccLevel, in analysis, in options, plan, out var version, out var segmentCount))
            throw NotFittingError(analysis.EncodingMode, analysis.DataLength, eccLevel, options.Version);

        var size = MicroQRConstants.SizeFromVersion(version);
        // The placer writes every module, so the core needs no clear.
        Span<byte> core = stackalloc byte[MaxCoreSize * MaxCoreSize];
        core = core.Slice(0, size * size);
        WriteBytesCore(widened, eccLevel, in analysis, version, plan.Slice(0, segmentCount), core, size, size, options.MaskPattern ?? AutomaticMask);

        var result = new MicroQRCodeData(version, options.QuietZoneSize);
        result.SetCoreData(core);
        return result;
    }

    /// <summary>
    /// Encodes bytes into the buffer you provide, exactly as given, without allocating.
    /// </summary>
    /// <remarks>
    /// What is written, and what the options do, is described on <see cref="Create(ReadOnlySpan{byte}, MicroQREccLevel, in MicroQRCodeGeneratorOptions)"/>.
    /// The matrix is laid out as the text overload <see cref="Create(ReadOnlySpan{char}, MicroQREccLevel, Span{byte}, in MicroQRCodeGeneratorOptions)"/> lays it out.
    /// </remarks>
    /// <param name="data">The bytes to encode. An array converts implicitly.</param>
    /// <param name="eccLevel">How much damage the Micro QR code should survive. M1 takes <see cref="MicroQREccLevel.ErrorDetectionOnly"/> alone, and Q needs M4.</param>
    /// <param name="destination">Where to write the matrix. Needs <see cref="MicroQRCodeCalculatedSize.BufferSize"/> bytes, as reported by <see cref="TryGetRequiredBufferSize(ReadOnlySpan{byte}, MicroQREccLevel, out MicroQRCodeCalculatedSize, in MicroQRCodeGeneratorOptions)"/>.</param>
    /// <param name="options">Version, quiet zone, mask and segmentation settings. Size <paramref name="destination"/> with the same options.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is too small, when the content does not fit, when the version, level and mode cannot be combined, or when the options carry <see cref="MicroQRCodeGeneratorOptions.AllowKanji"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined level or segmentation, or a quiet zone outside 0 to 10,000.</exception>
    public static int Create(ReadOnlySpan<byte> data, MicroQREccLevel eccLevel, Span<byte> destination, in MicroQRCodeGeneratorOptions options = default)
    {
        ValidateBytes(in options);
        if (data.Length > MicroQRSegmentPlanner.MaxPlannableChars)
            throw BytesDoNotFit(data.Length, eccLevel, options.Version);

        Span<char> buffer = stackalloc char[MicroQRSegmentPlanner.MaxPlannableChars];
        var widened = Widen(data, buffer);
        var analysis = AnalyzeBytes(widened);
        Span<ModeSegment> plan = stackalloc ModeSegment[MicroQRSegmentPlanner.MaxPlannableChars];
        if (!TryPlanBytes(widened, eccLevel, in analysis, in options, plan, out var version, out var segmentCount))
            throw NotFittingError(analysis.EncodingMode, analysis.DataLength, eccLevel, options.Version);

        var quietZoneSize = options.QuietZoneSize;
        var size = MicroQRConstants.SizeFromVersion(version);
        var totalSize = size + quietZoneSize * 2;
        var requiredSize = totalSize * totalSize;
        if (destination.Length < requiredSize)
            throw new ArgumentException($"Destination buffer too small: {requiredSize} bytes required (version {version}, {totalSize}x{totalSize} modules), got {destination.Length} bytes. Use {nameof(TryGetRequiredBufferSize)} to calculate the required size.", nameof(destination));

        // As the text's span form: the margins cleared, then the core straight into the window
        var target = destination.Slice(0, requiredSize);
        if (quietZoneSize > 0)
            QuietZoneWindow.ClearMargins(target, size, size, quietZoneSize);
        WriteBytesCore(widened, eccLevel, in analysis, version, plan.Slice(0, segmentCount), target.Slice(quietZoneSize * totalSize + quietZoneSize), size, totalSize, options.MaskPattern ?? AutomaticMask);
        return requiredSize;
    }

    /// <summary>
    /// Reports how large the Micro QR code of these bytes will be, or <c>false</c> when they do not fit.
    /// </summary>
    /// <remarks>
    /// <c>false</c> means that and only that. Bad arguments still throw. Pass the options you will encode with, since segmentation can pick a different version.
    /// </remarks>
    /// <param name="data">The bytes to size for. An array converts implicitly.</param>
    /// <param name="eccLevel">How much damage the Micro QR code should survive.</param>
    /// <param name="size">The size on success, <c>default</c> when the content does not fit.</param>
    /// <param name="options">Version, quiet zone, mask and segmentation settings.</param>
    /// <returns><c>true</c> when the content fits.</returns>
    /// <exception cref="ArgumentException">Thrown when no version in the range offers <paramref name="eccLevel"/> at all, which no content could satisfy, or when the options carry <see cref="MicroQRCodeGeneratorOptions.AllowKanji"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined level or segmentation, or a quiet zone outside 0 to 10,000.</exception>
    public static bool TryGetRequiredBufferSize(ReadOnlySpan<byte> data, MicroQREccLevel eccLevel, out MicroQRCodeCalculatedSize size, in MicroQRCodeGeneratorOptions options = default)
    {
        size = default;
        ValidateBytes(in options);
        if (data.Length > MicroQRSegmentPlanner.MaxPlannableChars)
        {
            // No version holds it, and the selector still checks the level against the range first
            _ = TrySelectVersionInRange(new TextAnalysisResult(EncodingMode.Byte, EciMode.Default, data.Length), eccLevel, options.Version, out _);
            return false;
        }

        Span<char> buffer = stackalloc char[MicroQRSegmentPlanner.MaxPlannableChars];
        var widened = Widen(data, buffer);
        var analysis = AnalyzeBytes(widened);
        Span<ModeSegment> plan = stackalloc ModeSegment[MicroQRSegmentPlanner.MaxPlannableChars];
        if (!TryPlanBytes(widened, eccLevel, in analysis, in options, plan, out var version, out _))
            return false;

        var totalSize = MicroQRConstants.SizeFromVersion(version) + options.QuietZoneSize * 2;
        size = new MicroQRCodeCalculatedSize(totalSize * totalSize, totalSize, version);
        return true;
    }

    /// <summary>The bytes calls' checks in the text calls' order, with the text-only option after the segmentation. The level and the version range are the selector's to check, as on the text route.</summary>
    private static void ValidateBytes(in MicroQRCodeGeneratorOptions options)
    {
        ValidateQuietZone(options.QuietZoneSize);
        if (options.Segmentation != MicroQRSegmentation.Single)
            ValidateOptimalEntry(options.Segmentation);
        if (options.AllowKanji)
            throw new ArgumentException("AllowKanji lets text take Kanji mode. Bytes are written in Numeric, Alphanumeric and Byte mode.", nameof(options));
    }

    /// <summary>
    /// The version the bytes take, and the plan written there, as the text's routes decide them. No segment means the single-mode stream.
    /// Under <see cref="MicroQRSegmentation.Optimal"/> a plan the version scan chose but that cannot be built falls back to the single-mode fit, as on the text route.
    /// </summary>
    /// <remarks>The planner's check of Latin-1 runs the decoder would read as another text runs under <see cref="EciMode.Iso8859_1"/> alone, so it never refuses bytes.</remarks>
    private static bool TryPlanBytes(ReadOnlySpan<char> widened, MicroQREccLevel eccLevel, in TextAnalysisResult analysis, in MicroQRCodeGeneratorOptions options, Span<ModeSegment> plan, out MicroQRVersion version, out int segmentCount)
    {
        segmentCount = 0;
        if (options.Segmentation == MicroQRSegmentation.Single)
            return TrySelectVersionInRange(in analysis, eccLevel, options.Version, out version);

        if (!MicroQRSegmentPlanner.TrySelectVersion(widened, in analysis, eccLevel, options.Version, out version, out var useSegments, out _))
            return false;
        if (!useSegments || MicroQRSegmentPlanner.TryBuildPlan(widened, analysis.EciMode, version, eccLevel, plan, out segmentCount))
            return true;

        segmentCount = 0;
        return TrySelectVersionInRange(in analysis, eccLevel, options.Version, out version);
    }

    /// <summary>Writes the single-mode stream when <paramref name="plan"/> is empty, and the planned runs otherwise.</summary>
    private static void WriteBytesCore(ReadOnlySpan<char> widened, MicroQREccLevel eccLevel, in TextAnalysisResult analysis, MicroQRVersion version, ReadOnlySpan<ModeSegment> plan, Span<byte> core, int size, int stride, int maskPattern)
    {
        if (plan.IsEmpty)
            WriteCoreModules(widened, PrepareConfiguration(in analysis, eccLevel, version), core, size, stride, maskPattern);
        else
            WriteCoreModulesPlanned(widened, version, eccLevel, analysis.EciMode, plan, core, size, stride, maskPattern);
    }

    /// <summary>The selector's "content is too long" error for a payload longer than any symbol holds, after the checks of the level and the range it makes first.</summary>
    private static ArgumentException BytesDoNotFit(int length, MicroQREccLevel eccLevel, MicroQRVersionRange range)
    {
        var analysis = new TextAnalysisResult(EncodingMode.Byte, EciMode.Default, length);
        _ = TrySelectVersionInRange(in analysis, eccLevel, range, out _);
        return NotFittingError(EncodingMode.Byte, length, eccLevel, range);
    }

    /// <summary>The bytes one to a char, U+0000 to U+00FF, which every writer turns back into the same bytes.</summary>
    private static ReadOnlySpan<char> Widen(ReadOnlySpan<byte> data, Span<char> buffer)
    {
        var widened = buffer.Slice(0, data.Length);
#if NET5_0_OR_GREATER
        Encoding.Latin1.GetChars(data, widened);
#else
        for (var i = 0; i < data.Length; i++)
            widened[i] = (char)data[i];
#endif
        return widened;
    }

    /// <summary>The analysis of widened bytes: the mode their characters allow, as text gets, under the default charset, which narrows every char back to its byte.</summary>
    private static TextAnalysisResult AnalyzeBytes(ReadOnlySpan<char> widened)
        => TextAnalyzer.Analyze(widened, EciMode.Default) with { EciMode = EciMode.Default };
}
