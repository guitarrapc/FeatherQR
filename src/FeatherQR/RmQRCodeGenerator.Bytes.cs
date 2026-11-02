using System.Buffers;
#if NET5_0_OR_GREATER
using System.Text;
#endif
using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;

namespace FeatherQR;

public static partial class RmQRCodeGenerator
{
    // Bytes take the text pipeline widened one to a char, U+0000 to U+00FF, under a charset of their own (EciModeExtensions):
    // every path writes such a char back as its byte, and the charset carries the caller's assignment or none.

    /// <summary>The most bytes one symbol holds: digits, a byte each in Numeric mode, 361 of them at R17x139-M. They are widened on the stack, and a longer payload is refused unwidened.</summary>
    private const int MaxSymbolBytes = 361;

    /// <summary>
    /// Encodes bytes into an rMQR code, exactly as given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bytes are the bytes in the symbol. Runs of digits may be written in Numeric mode and runs of uppercase letters in Alphanumeric mode, which carry the same ASCII bytes in fewer bits, the way text takes those modes.
    /// <paramref name="eciAssignment"/> declares how the bytes are interpreted, for example 899 for binary data, 20 for Shift_JIS or 26 for UTF-8, and is written as an ECI header of 11 to 27 bits. With none, as by default, no header is written.
    /// The assignment a bytes call of <see cref="RmQRCodeDecoder"/> reports passes straight back, so the bytes it read are written again under the declaration they were read under.
    /// Kanji mode and FNC1 are not part of the bytes and are not written again. Kanji pairs take 16 bits each in Byte mode, where Kanji mode takes 13, so they can need a larger symbol.
    /// </para>
    /// <para>
    /// <see cref="RmQRCodeGeneratorOptions.Version"/>, <see cref="RmQRCodeGeneratorOptions.FitStrategy"/>, <see cref="RmQRCodeGeneratorOptions.Height"/>, <see cref="RmQRCodeGeneratorOptions.QuietZoneSize"/> and <see cref="RmQRCodeGeneratorOptions.Segmentation"/> apply as they do to text.
    /// The options that mean something only for text are refused: an <see cref="RmQRCodeGeneratorOptions.EciMode"/> other than <see cref="EciMode.Default"/>, <see cref="RmQRCodeGeneratorOptions.AllowKanji"/> and an FNC1 mode.
    /// </para>
    /// </remarks>
    /// <param name="data">The bytes to encode. An array converts implicitly. An empty payload needs a typed argument, such as <c>ReadOnlySpan&lt;byte&gt;.Empty</c>, since <c>[]</c>, <c>default</c> and <c>null</c> fit the text overload too.</param>
    /// <param name="eccLevel">How much damage the rMQR code should survive, M or H.</param>
    /// <param name="options">Version, fit, quiet zone and segmentation settings. Omit for the defaults.</param>
    /// <param name="eciAssignment">The ECI assignment that describes the bytes, from 0 to 999999, or <c>null</c> for no ECI header.</param>
    /// <returns>The module matrix.</returns>
    /// <exception cref="ArgumentException">Thrown when the content does not fit, when the options contradict each other, or when they carry an <see cref="RmQRCodeGeneratorOptions.EciMode"/> other than <see cref="EciMode.Default"/> or another setting only text uses.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an out-of-range quiet zone, an undefined level, version, fit strategy, height or segmentation, or an assignment outside 0 to 999999.</exception>
    public static RmQRCodeData Create(ReadOnlySpan<byte> data, RmQREccLevel eccLevel, in RmQRCodeGeneratorOptions options = default, int? eciAssignment = null)
    {
        var charset = ValidateBytes(in options, eciAssignment);
        if (data.Length > MaxSymbolBytes)
            ThrowBytesDoNotFit(data.Length, charset, eccLevel, in options);

        Span<char> buffer = stackalloc char[MaxSymbolBytes];
        var widened = Widen(data, buffer);
        var analysis = AnalyzeBytes(widened, charset);
        if (options.Segmentation != RmQRSegmentation.Single)
            return CreateOptimal(widened, eccLevel, in analysis, options.Version, options.FitStrategy, options.Height, options.QuietZoneSize, fnc1: default);

        var config = PrepareConfiguration(in analysis, eccLevel, options.Version, options.FitStrategy, options.Height);
        var result = new RmQRCodeData(config.Version, options.QuietZoneSize);
        var coreWidth = result.GetCoreWidth();
        var coreLength = coreWidth * result.GetCoreHeight();
        var rented = ArrayPool<byte>.Shared.Rent(coreLength);
        try
        {
            var core = rented.AsSpan(0, coreLength);
            WriteCoreModules(widened, in config, core, coreWidth);
            result.SetCoreData(core);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary>
    /// Encodes bytes into the buffer you provide, exactly as given, without allocating.
    /// </summary>
    /// <remarks>
    /// What is written, and what the options and <paramref name="eciAssignment"/> do, is described on <see cref="Create(ReadOnlySpan{byte}, RmQREccLevel, in RmQRCodeGeneratorOptions, int?)"/>.
    /// The matrix is laid out as the text overload <see cref="Create(ReadOnlySpan{char}, RmQREccLevel, Span{byte}, in RmQRCodeGeneratorOptions)"/> lays it out.
    /// </remarks>
    /// <param name="data">The bytes to encode. An array converts implicitly.</param>
    /// <param name="eccLevel">How much damage the rMQR code should survive, M or H.</param>
    /// <param name="destination">Where to write the matrix. Needs <see cref="RmQRCodeCalculatedSize.BufferSize"/> bytes, as reported by <see cref="TryGetRequiredBufferSize(ReadOnlySpan{byte}, RmQREccLevel, out RmQRCodeCalculatedSize, in RmQRCodeGeneratorOptions, int?)"/>.</param>
    /// <param name="options">Version, fit, quiet zone and segmentation settings. Size <paramref name="destination"/> with the same options.</param>
    /// <param name="eciAssignment">The ECI assignment that describes the bytes, from 0 to 999999, or <c>null</c> for no ECI header. Size <paramref name="destination"/> with the same assignment.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is too small, when the content does not fit, when the options contradict each other, or when they carry an <see cref="RmQRCodeGeneratorOptions.EciMode"/> other than <see cref="EciMode.Default"/> or another setting only text uses.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an out-of-range quiet zone, an undefined level, version, fit strategy, height or segmentation, or an assignment outside 0 to 999999.</exception>
    public static int Create(ReadOnlySpan<byte> data, RmQREccLevel eccLevel, Span<byte> destination, in RmQRCodeGeneratorOptions options = default, int? eciAssignment = null)
    {
        var charset = ValidateBytes(in options, eciAssignment);
        if (data.Length > MaxSymbolBytes)
            ThrowBytesDoNotFit(data.Length, charset, eccLevel, in options);

        Span<char> buffer = stackalloc char[MaxSymbolBytes];
        var widened = Widen(data, buffer);
        var analysis = AnalyzeBytes(widened, charset);
        var quietZoneSize = options.QuietZoneSize;
        if (options.Segmentation != RmQRSegmentation.Single)
            return CreateOptimalTo(widened, eccLevel, in analysis, destination, options.Version, options.FitStrategy, options.Height, quietZoneSize, fnc1: default);

        var config = PrepareConfiguration(in analysis, eccLevel, options.Version, options.FitStrategy, options.Height);
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
            WriteCoreModules(widened, in config, target, coreWidth);
            return requiredSize;
        }

        QuietZoneWindow.ClearMargins(target, coreWidth, coreHeight, quietZoneSize);
        WriteCoreModules(widened, in config, target.Slice(quietZoneSize * totalWidth + quietZoneSize), totalWidth);
        return requiredSize;
    }

    /// <summary>
    /// Reports how large the rMQR code of these bytes will be, or <c>false</c> when they do not fit.
    /// </summary>
    /// <remarks>
    /// <c>false</c> means that and only that. Bad arguments still throw. Pass the options and the assignment you will encode with, since segmentation and the assignment's header can pick different versions.
    /// </remarks>
    /// <param name="data">The bytes to size for. An array converts implicitly.</param>
    /// <param name="eccLevel">How much damage the rMQR code should survive, M or H.</param>
    /// <param name="size">The size on success, <c>default</c> when the content does not fit.</param>
    /// <param name="options">Version, fit, quiet zone and segmentation settings.</param>
    /// <param name="eciAssignment">The ECI assignment that describes the bytes, from 0 to 999999, or <c>null</c> for no ECI header.</param>
    /// <returns><c>true</c> when the content fits.</returns>
    /// <exception cref="ArgumentException">Thrown when <see cref="RmQRCodeGeneratorOptions.Version"/> and <see cref="RmQRCodeGeneratorOptions.Height"/> disagree, or when the options carry an <see cref="RmQRCodeGeneratorOptions.EciMode"/> other than <see cref="EciMode.Default"/> or another setting only text uses.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an out-of-range quiet zone, an undefined level, version, fit strategy, height or segmentation, or an assignment outside 0 to 999999.</exception>
    public static bool TryGetRequiredBufferSize(ReadOnlySpan<byte> data, RmQREccLevel eccLevel, out RmQRCodeCalculatedSize size, in RmQRCodeGeneratorOptions options = default, int? eciAssignment = null)
    {
        size = default;
        var charset = ValidateBytes(in options, eciAssignment);

        bool fits;
        RmQRVersion version;
        if (data.Length > MaxSymbolBytes)
        {
            fits = RmQRVersionSelector.TrySelect(EncodingMode.Byte, data.Length, charset, eccLevel, options.Version, options.FitStrategy, options.Height, out version);
        }
        else
        {
            Span<char> buffer = stackalloc char[MaxSymbolBytes];
            var widened = Widen(data, buffer);
            var analysis = AnalyzeBytes(widened, charset);
            fits = options.Segmentation != RmQRSegmentation.Single
                ? TryPlanOptimalVersion(widened, eccLevel, in analysis, options.Version, options.FitStrategy, options.Height, fnc1: default, out version)
                : RmQRVersionSelector.TrySelect(analysis.EncodingMode, analysis.DataLength, analysis.EciMode, eccLevel, options.Version, options.FitStrategy, options.Height, out version);
        }

        if (!fits)
            return false;

        var totalWidth = RmQRConstants.GetWidth(version) + options.QuietZoneSize * 2;
        var totalHeight = RmQRConstants.GetHeight(version) + options.QuietZoneSize * 2;
        size = new RmQRCodeCalculatedSize(totalWidth * totalHeight, totalWidth, totalHeight, version);
        return true;
    }

    /// <summary>
    /// The bytes calls' checks in the text calls' order, the text-only options and the assignment where the charset is checked, and the charset the bytes are written under.
    /// The level, version, fit and height are the selector's to check, as on the text route.
    /// </summary>
    private static EciMode ValidateBytes(in RmQRCodeGeneratorOptions options, int? eciAssignment)
    {
        ValidateQuietZone(options.QuietZoneSize);
        if (options.Segmentation != RmQRSegmentation.Single)
            ValidateOptimalSegmentation(options.Segmentation);
        if (options.EciMode != EciMode.Default)
            throw new ArgumentException("EciMode chooses the charset text is converted into. Bytes are written as they are: pass the ECI assignment that describes them as eciAssignment.", nameof(options));
        if (options.AllowKanji)
            throw new ArgumentException("AllowKanji lets text take Kanji mode. Bytes are written in Numeric, Alphanumeric and Byte mode.", nameof(options));
        if (options.Fnc1.IsActive)
            throw new ArgumentException("FNC1 marks GS1 and industry data, which is written as text.", nameof(options));
        EciModeExtensions.ValidateAssignment(eciAssignment);
        return EciModeExtensions.BytesCharset(eciAssignment);
    }

    /// <summary>The selector's "content is too long" error for a payload longer than any symbol holds, after the checks of the level, version, fit and height it makes first.</summary>
    private static void ThrowBytesDoNotFit(int length, EciMode charset, RmQREccLevel eccLevel, in RmQRCodeGeneratorOptions options)
    {
        _ = PrepareConfiguration(new TextAnalysisResult(EncodingMode.Byte, charset, length), eccLevel, options.Version, options.FitStrategy, options.Height);
        throw new InvalidOperationException($"No rMQR version holds {length} bytes, so the selector refuses them first.");
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

    /// <summary>The analysis of widened bytes: the mode their characters allow, as text gets, under the charset of the bytes.</summary>
    private static TextAnalysisResult AnalyzeBytes(ReadOnlySpan<char> widened, EciMode charset)
        => TextAnalyzer.Analyze(widened, EciMode.Default) with { EciMode = charset };
}
