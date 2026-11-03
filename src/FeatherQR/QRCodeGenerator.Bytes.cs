using System.Buffers;
#if NET5_0_OR_GREATER
using System.Text;
#endif
using System.Runtime.InteropServices;
using FeatherQR.Internals;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR;

public static partial class QRCodeGenerator
{
    // Bytes take the text pipeline widened one to a char, U+0000 to U+00FF, under a charset of their own (EciModeExtensions):
    // every path writes such a char back as its byte, and the charset carries the caller's assignment or none.

    /// <summary>The most bytes one symbol holds: digits, a byte each in Numeric mode, 7,089 of them at 40-L. A longer payload is refused before it is widened.</summary>
    private const int MaxSymbolBytes = 7089;

    /// <summary>Payloads up to this many bytes are widened on the stack, longer ones into a rented buffer.</summary>
    private const int StackWidenedBytes = 256;

    /// <summary>
    /// Encodes bytes into a QR code, exactly as given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bytes are the bytes in the symbol. Runs of digits may be written in Numeric mode and runs of uppercase letters in Alphanumeric mode, which carry the same ASCII bytes in fewer bits, the way text takes those modes.
    /// <paramref name="eciAssignment"/> declares how the bytes are interpreted, for example 899 for binary data, 20 for Shift_JIS or 26 for UTF-8, and is written as an ECI header of 12 to 28 bits. With none, as by default, no header is written, which is what a binary payload usually carries.
    /// The assignment a bytes call of <see cref="QRCodeDecoder"/> reports passes straight back, so the bytes it read are written again under the declaration they were read under.
    /// Kanji mode, FNC1 and a Structured Append header are not part of the bytes and are not written again. Kanji pairs take 16 bits each in Byte mode, where Kanji mode takes 13, so they can need a larger symbol.
    /// </para>
    /// <para>
    /// <see cref="QRCodeGeneratorOptions.Version"/>, <see cref="QRCodeGeneratorOptions.QuietZoneSize"/>, <see cref="QRCodeGeneratorOptions.MaskPattern"/>, <see cref="QRCodeGeneratorOptions.BoostEccLevel"/> and <see cref="QRCodeGeneratorOptions.Segmentation"/> apply as they do to text.
    /// The options that mean something only for text are refused: an <see cref="QRCodeGeneratorOptions.EciMode"/> other than <see cref="EciMode.Default"/>, <see cref="QRCodeGeneratorOptions.Utf8Bom"/>, <see cref="QRCodeGeneratorOptions.AllowKanji"/> and an FNC1 mode.
    /// </para>
    /// </remarks>
    /// <param name="data">The bytes to encode. An array converts implicitly. An empty payload needs a typed argument, such as <c>ReadOnlySpan&lt;byte&gt;.Empty</c>, since <c>[]</c>, <c>default</c> and <c>null</c> fit the text overload too.</param>
    /// <param name="eccLevel">How much damage the QR code should survive: L recovers 7% of it, M 15%, Q 25% and H 30%.</param>
    /// <param name="options">Version, quiet zone, mask, boost and segmentation settings. Omit for the defaults.</param>
    /// <param name="eciAssignment">The ECI assignment that describes the bytes, from 0 to 999999, or <c>null</c> for no ECI header.</param>
    /// <returns>The module matrix.</returns>
    /// <exception cref="ArgumentException">Thrown when the content does not fit a narrowed <see cref="QRCodeGeneratorOptions.Version"/>, or when the options carry an <see cref="QRCodeGeneratorOptions.EciMode"/> other than <see cref="EciMode.Default"/> or another setting only text uses.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined level or segmentation, a quiet zone outside 0 to 10,000, or an assignment outside 0 to 999999.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the content does not fit version 40 and <see cref="QRCodeGeneratorOptions.Version"/> spans every version, as it does by default.</exception>
    public static QRCodeData Create(ReadOnlySpan<byte> data, QREccLevel eccLevel, in QRCodeGeneratorOptions options = default, int? eciAssignment = null)
    {
        var charset = ValidateBytes(eccLevel, in options, eciAssignment);
        if (data.Length > MaxSymbolBytes)
            ThrowBytesDoNotFit(ref MemoryMarshal.GetReference(data), data.Length, charset, eccLevel, in options);

        char[]? rented = null;
        Span<char> buffer = data.Length <= StackWidenedBytes ? stackalloc char[StackWidenedBytes] : (rented = ArrayPool<char>.Shared.Rent(data.Length));
        try
        {
            var widened = Widen(data, buffer);
            var analysis = AnalyzeBytes(widened, charset);
            return CreateFromBytesAnalysis(widened, eccLevel, in analysis, in options);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Encodes bytes into the buffer you provide, exactly as given.
    /// </summary>
    /// <remarks>
    /// What is written, and what the options and <paramref name="eciAssignment"/> do, is described on <see cref="Create(ReadOnlySpan{byte}, QREccLevel, in QRCodeGeneratorOptions, int?)"/>.
    /// The matrix is laid out as the text overload <see cref="Create(ReadOnlySpan{char}, QREccLevel, Span{byte}, in QRCodeGeneratorOptions)"/> lays it out.
    /// </remarks>
    /// <param name="data">The bytes to encode. An array converts implicitly.</param>
    /// <param name="eccLevel">How much damage the QR code should survive: L recovers 7% of it, M 15%, Q 25% and H 30%.</param>
    /// <param name="destination">Where to write the matrix. Needs <see cref="QRCodeCalculatedSize.BufferSize"/> bytes, as reported by <see cref="TryGetRequiredBufferSize(ReadOnlySpan{byte}, QREccLevel, out QRCodeCalculatedSize, in QRCodeGeneratorOptions, int?)"/>.</param>
    /// <param name="options">Version, quiet zone, mask, boost and segmentation settings. Size <paramref name="destination"/> with the same options.</param>
    /// <param name="eciAssignment">The ECI assignment that describes the bytes, from 0 to 999999, or <c>null</c> for no ECI header. Size <paramref name="destination"/> with the same assignment.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is too small, when the content does not fit a narrowed <see cref="QRCodeGeneratorOptions.Version"/>, or when the options carry an <see cref="QRCodeGeneratorOptions.EciMode"/> other than <see cref="EciMode.Default"/> or another setting only text uses.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined level or segmentation, a quiet zone outside 0 to 10,000, or an assignment outside 0 to 999999.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the content does not fit version 40 and <see cref="QRCodeGeneratorOptions.Version"/> spans every version, as it does by default.</exception>
    public static int Create(ReadOnlySpan<byte> data, QREccLevel eccLevel, Span<byte> destination, in QRCodeGeneratorOptions options = default, int? eciAssignment = null)
    {
        var charset = ValidateBytes(eccLevel, in options, eciAssignment);
        if (data.Length > MaxSymbolBytes)
            ThrowBytesDoNotFit(ref MemoryMarshal.GetReference(data), data.Length, charset, eccLevel, in options);

        char[]? rented = null;
        Span<char> buffer = data.Length <= StackWidenedBytes ? stackalloc char[StackWidenedBytes] : (rented = ArrayPool<char>.Shared.Rent(data.Length));
        try
        {
            var widened = Widen(data, buffer);
            var analysis = AnalyzeBytes(widened, charset);
            if (options.Segmentation != QRSegmentation.Single)
                return CreateOptimalTo(widened, eccLevel, destination, in analysis, in options);

            var (version, level) = ResolveVersionAndEcc(in analysis, eccLevel, in options);
            return CreateCore(widened, PrepareConfiguration(in analysis, level, utf8BOM: false, version), destination, options.QuietZoneSize, options.MaskPattern ?? AutomaticMask);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Reports how large the QR code of these bytes will be, or <c>false</c> when they do not fit.
    /// </summary>
    /// <remarks>
    /// <c>false</c> means that and only that. Bad arguments still throw. Pass the options and the assignment you will encode with, since segmentation and the assignment's header can pick different versions.
    /// </remarks>
    /// <param name="data">The bytes to size for. An array converts implicitly.</param>
    /// <param name="eccLevel">How much damage the QR code should survive.</param>
    /// <param name="size">The size on success, <c>default</c> when the content does not fit.</param>
    /// <param name="options">Version, quiet zone, mask, boost and segmentation settings.</param>
    /// <param name="eciAssignment">The ECI assignment that describes the bytes, from 0 to 999999, or <c>null</c> for no ECI header.</param>
    /// <returns><c>true</c> when the content fits.</returns>
    /// <exception cref="ArgumentException">Thrown when the options carry an <see cref="QRCodeGeneratorOptions.EciMode"/> other than <see cref="EciMode.Default"/> or another setting only text uses.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined level or segmentation, a quiet zone outside 0 to 10,000, or an assignment outside 0 to 999999.</exception>
    public static bool TryGetRequiredBufferSize(ReadOnlySpan<byte> data, QREccLevel eccLevel, out QRCodeCalculatedSize size, in QRCodeGeneratorOptions options = default, int? eciAssignment = null)
    {
        size = default;
        var charset = ValidateBytes(eccLevel, in options, eciAssignment);
        if (data.Length > MaxSymbolBytes)
            return false;

        char[]? rented = null;
        Span<char> buffer = data.Length <= StackWidenedBytes ? stackalloc char[StackWidenedBytes] : (rented = ArrayPool<char>.Shared.Rent(data.Length));
        try
        {
            var widened = Widen(data, buffer);
            var analysis = AnalyzeBytes(widened, charset);
            if (!TryResolveVersion(widened, eccLevel, in analysis, in options, options.Segmentation != QRSegmentation.Single, out var version))
                return false;

            var (totalSize, bufferSize) = CalculateMatrixSize(QRCodeData.SizeFromVersion(version), options.QuietZoneSize);
            size = new QRCodeCalculatedSize(bufferSize, totalSize, version);
            return true;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Encodes bytes as a Structured Append set: up to sixteen QR codes whose parts, joined in index order, are the bytes.
    /// </summary>
    /// <remarks>
    /// The split, the shared version, the balance between symbols and the boost follow <see cref="CreateStructuredAppend(ReadOnlySpan{char}, QREccLevel, in QRCodeGeneratorOptions)"/>.
    /// Every symbol declares <paramref name="eciAssignment"/>, or nothing, and carries the XOR of all the bytes as its parity.
    /// Splits fall between any two bytes, so a multi-byte character can straddle two symbols, and the bytes calls of <see cref="QRCodeDecoder"/> join it back exactly.
    /// A payload one symbol holds returns that one symbol, as <see cref="Create(ReadOnlySpan{byte}, QREccLevel, in QRCodeGeneratorOptions, int?)"/> would write it, with no Structured Append header.
    /// </remarks>
    /// <param name="data">The bytes to encode. An array converts implicitly.</param>
    /// <param name="eccLevel">How much damage each QR code should survive, the same level for every symbol of the set.</param>
    /// <param name="options">Version, quiet zone, mask, boost and segmentation settings. The version range bounds the size of every symbol.</param>
    /// <param name="eciAssignment">The ECI assignment that describes the bytes, from 0 to 999999, or <c>null</c> for no ECI header.</param>
    /// <returns>The symbols in set order: one when the bytes fit a single symbol, otherwise two to sixteen.</returns>
    /// <exception cref="ArgumentException">Thrown when the bytes do not fit sixteen symbols of the largest version in the range, or when the options carry an <see cref="QRCodeGeneratorOptions.EciMode"/> other than <see cref="EciMode.Default"/> or another setting only text uses.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined level or segmentation, a quiet zone outside 0 to 10,000, or an assignment outside 0 to 999999.</exception>
    public static QRCodeData[] CreateStructuredAppend(ReadOnlySpan<byte> data, QREccLevel eccLevel, in QRCodeGeneratorOptions options = default, int? eciAssignment = null)
    {
        var charset = ValidateBytes(eccLevel, in options, eciAssignment);
        if (data.Length > StructuredAppendPlanner.MaxSymbols * MaxSymbolBytes)
        {
            throw new ArgumentException($"Content does not fit {StructuredAppendPlanner.MaxSymbols} Structured Append symbols: {data.Length} bytes is more than sixteen symbols hold in any mode.", nameof(options));
        }

        char[]? rented = null;
        Span<char> buffer = data.Length <= StackWidenedBytes ? stackalloc char[StackWidenedBytes] : (rented = ArrayPool<char>.Shared.Rent(data.Length));
        try
        {
            var widened = Widen(data, buffer);
            var wholeText = AnalyzeBytes(widened, charset);
            return CreateStructuredAppend(widened, eccLevel, in wholeText, in options, planTogether: true, bytes: true);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>The symbol of widened bytes from their analysis: the plan under <see cref="QRSegmentation.Optimal"/>, otherwise the single-mode stream at the version and level the options resolve.</summary>
    private static QRCodeData CreateFromBytesAnalysis(ReadOnlySpan<char> widened, QREccLevel eccLevel, in TextAnalysisResult analysis, in QRCodeGeneratorOptions options)
    {
        if (options.Segmentation != QRSegmentation.Single)
            return CreateOptimal(widened, eccLevel, in analysis, in options);

        var (version, level) = ResolveVersionAndEcc(in analysis, eccLevel, in options);
        return CreateCore(widened, PrepareConfiguration(in analysis, level, utf8BOM: false, version), options.QuietZoneSize, options.MaskPattern ?? AutomaticMask);
    }

    /// <summary>
    /// The bytes calls' checks in the text calls' order, the text-only options and the assignment where the charset is checked, and the charset the bytes are written under.
    /// </summary>
    private static EciMode ValidateBytes(QREccLevel eccLevel, in QRCodeGeneratorOptions options, int? eciAssignment)
    {
        ValidateQuietZoneSize(options.QuietZoneSize);
        if (options.Segmentation != QRSegmentation.Single)
            ValidateOptimalEntry(options.Segmentation);
        if (options.EciMode != EciMode.Default)
            throw new ArgumentException("EciMode chooses the charset text is converted into. Bytes are written as they are: pass the ECI assignment that describes them as eciAssignment.", nameof(options));
        if (options.Utf8Bom)
            throw new ArgumentException("Utf8Bom heads UTF-8 text with a byte order mark. Bytes are written as they are, so a mark that belongs to them is part of them.", nameof(options));
        if (options.AllowKanji)
            throw new ArgumentException("AllowKanji lets text take Kanji mode. Bytes are written in Numeric, Alphanumeric and Byte mode.", nameof(options));
        if (options.Fnc1.IsActive)
            throw new ArgumentException("FNC1 marks GS1 and industry data, which is written as text.", nameof(options));
        EciModeExtensions.ValidateAssignment(eciAssignment);
        ValidateEccLevel(eccLevel);
        return EciModeExtensions.BytesCharset(eciAssignment);
    }

    /// <summary>The failure of a payload longer than any symbol holds, in the mode its bytes take, which <see cref="ThrowDoesNotFit"/> describes as the text calls do.</summary>
    private static void ThrowBytesDoNotFit(ref byte first, int length, EciMode charset, QREccLevel eccLevel, in QRCodeGeneratorOptions options)
        => ThrowDoesNotFit(new TextAnalysisResult(TextAnalyzer.ModeOfBytes(ref first, length), charset, length), eccLevel, in options);

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

    /// <summary>The analysis of widened bytes. The mode is the one text of the same characters takes, and the charset is the bytes' own.</summary>
    private static TextAnalysisResult AnalyzeBytes(ReadOnlySpan<char> widened, EciMode charset)
        => TextAnalyzer.Analyze(widened, EciMode.Default) with { EciMode = charset };
}
