using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FeatherQR.Internals.RmQR;

/// <summary>
/// The data codeword streams under FNC1 (ISO/IEC 23941 7.4.8, specs/rmqr-encoder.md FNC1): the headers in the
/// standard's order (the 11-bit ECI prefix when a charset is declared, the FNC1 indicator <c>101</c> or <c>110</c>
/// with its 8-bit application indicator, then the data), and the <c>%</c> rule applied in reverse in an
/// Alphanumeric run (the separator U+001D written as <c>%</c>, a literal <c>%</c> as <c>%%</c>). Written as its own
/// methods, reusing the run writers, so the streams without FNC1 keep their code.
/// </summary>
internal static partial class RmQRBinaryEncoder
{
    private const int Fnc1FirstIndicator = 0b101;
    private const int Fnc1SecondIndicator = 0b110;
    private const int Fnc1ApplicationIndicatorBits = 8;
    private const int PercentValue = 38;
    private const char FieldSeparator = '\u001D';

    /// <summary>
    /// <see cref="EncodeDataCodewords"/> under FNC1: the ECI prefix, the indicator, then the single run of the analysis's mode.
    /// The caller guarantees the content and the headers fit (see <see cref="RmQRVersionSelector.SelectFnc1"/>).
    /// </summary>
    public static int EncodeDataCodewordsFnc1(ReadOnlySpan<char> text, RmQRVersion version, RmQREccLevel eccLevel, in TextAnalysisResult analysis, Fnc1Mode fnc1, Span<byte> destination)
    {
        var codewordCount = RmQRConstants.GetDataCodewordCount(version, eccLevel);
        var capacityBits = codewordCount * 8;
        var mode = analysis.EncodingMode;
        if (destination.Length < codewordCount)
            throw new ArgumentException($"Destination too small: {codewordCount} data codewords required, got {destination.Length} bytes.", nameof(destination));
        if (analysis.EciMode is not (EciMode.Default or EciMode.Iso8859_1 or EciMode.Utf8))
            throw new ArgumentOutOfRangeException(nameof(analysis), $"Unsupported charset {analysis.EciMode} for rMQR.");
        Debug.Assert(RmQRVersionSelector.GetRequiredBits(version, mode, analysis.DataLength, analysis.EciMode) + fnc1.RmQrHeaderBits <= capacityBits, "content must fit (version selection guarantees this)");

        ref var dest = ref MemoryMarshal.GetReference(destination);
        ulong acc = 0;
        var accBits = 0;
        var bytePos = 0;

        if (analysis.EciMode != EciMode.Default)
            WriteEciHeader(ref dest, ref acc, ref accBits, ref bytePos, analysis.EciMode);
        WriteFnc1Header(ref dest, ref acc, ref accBits, ref bytePos, fnc1);

        switch (mode)
        {
            case EncodingMode.Numeric:
                {
                    var countBits = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric);
                    Append(ref dest, ref acc, ref accBits, ref bytePos, (0b001 << countBits) | analysis.DataLength, RmQRConstants.ModeIndicatorLength + countBits);
                    WriteNumeric(ref dest, ref acc, ref accBits, ref bytePos, text, vectorized: true);
                    break;
                }
            case EncodingMode.Alphanumeric:
                {
                    var countBits = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric);
                    Append(ref dest, ref acc, ref accBits, ref bytePos, (0b010 << countBits) | analysis.DataLength, RmQRConstants.ModeIndicatorLength + countBits);
                    WriteAlphanumericFnc1(ref dest, ref acc, ref accBits, ref bytePos, text);
                    break;
                }
            case EncodingMode.Byte:
                {
                    var countBits = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte);
                    Append(ref dest, ref acc, ref accBits, ref bytePos, (0b011 << countBits) | analysis.DataLength, RmQRConstants.ModeIndicatorLength + countBits);
                    if (analysis.EciMode == EciMode.Utf8)
                        WriteUtf8(ref dest, ref acc, ref accBits, ref bytePos, text, analysis.DataLength);
                    else
                        WriteLatin1(ref dest, ref acc, ref accBits, ref bytePos, text, vectorized: true);
                    break;
                }
            case EncodingMode.Kanji:
                {
                    if (analysis.EciMode != EciMode.Default)
                        KanjiCells.ThrowKanjiUnderEci(analysis.EciMode);
                    var countBits = RmQRConstants.GetKanjiCountIndicatorLength(version);
                    Append(ref dest, ref acc, ref accBits, ref bytePos, (RmQRConstants.KanjiModeIndicatorValue << countBits) | analysis.DataLength, RmQRConstants.ModeIndicatorLength + countBits);
                    WriteKanji(ref dest, ref acc, ref accBits, ref bytePos, text);
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(analysis), $"Encoding mode {mode} is not supported by rMQR.");
        }

        Finish(ref dest, acc, accBits, bytePos, codewordCount, capacityBits);
        return codewordCount;
    }

    /// <summary>
    /// <see cref="EncodeDataCodewordsSegmented"/> under FNC1: the ECI prefix, the indicator, then each planned run, an
    /// Alphanumeric run through the <c>%</c> rule with the unit count the plan priced (its characters plus its literal
    /// percents). A plan under FNC1 holds no Kanji run.
    /// </summary>
    public static int EncodeDataCodewordsSegmentedFnc1(ReadOnlySpan<char> text, RmQRVersion version, RmQREccLevel eccLevel, EciMode charset, Fnc1Mode fnc1, ReadOnlySpan<ModeSegment> segments, Span<byte> destination)
    {
        var codewordCount = RmQRConstants.GetDataCodewordCount(version, eccLevel);
        var capacityBits = codewordCount * 8;
        if (destination.Length < codewordCount)
            throw new ArgumentException($"Destination too small: {codewordCount} data codewords required, got {destination.Length} bytes.", nameof(destination));
        if (segments.Length == 0)
            throw new ArgumentException("A segmented rMQR stream needs at least one segment.", nameof(segments));
        if (charset is not (EciMode.Default or EciMode.Iso8859_1 or EciMode.Utf8))
            throw new ArgumentOutOfRangeException(nameof(charset), $"Unsupported charset {charset} for rMQR.");

        var headerBits = (charset == EciMode.Default ? 0 : EciHeaderBits) + fnc1.RmQrHeaderBits;
        var plannedBits = RmQRSegmentPlanner.MeasurePlan(version, segments) + headerBits;
        if (plannedBits > capacityBits)
            throw new ArgumentException($"Segment plan does not fit rMQR {version} at ECC level {eccLevel}: {plannedBits} bits required, {capacityBits} available.", nameof(segments));

        ref var dest = ref MemoryMarshal.GetReference(destination);
        ulong acc = 0;
        var accBits = 0;
        var bytePos = 0;

        if (charset != EciMode.Default)
            WriteEciHeader(ref dest, ref acc, ref accBits, ref bytePos, charset);
        WriteFnc1Header(ref dest, ref acc, ref accBits, ref bytePos, fnc1);

        Span<byte> utf8 = stackalloc byte[StackByteBudget];
        var expectedStart = 0;

        var numericCountBits = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric);
        var alnumCountBits = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric);
        var byteCountBits = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte);

        foreach (var segment in segments)
        {
            if (segment.Start != expectedStart || segment.Length == 0 || segment.Start + segment.Length > text.Length)
                throw new ArgumentException("Segment plan must cover the content in order with non-empty runs.", nameof(segments));
            expectedStart = segment.Start + segment.Length;

            var chars = text.Slice(segment.Start, segment.Length);
            switch (segment.ModeIndex)
            {
                case 0:
                    if (segment.UnitCount != segment.Length)
                        throw new ArgumentException($"Segment plan gives a {segment.Length}-character run a unit count of {segment.UnitCount}; they must agree in Numeric mode.", nameof(segments));
                    Debug.Assert(segment.UnitCount < (1 << numericCountBits), "run length must fit the character count indicator");
                    Append(ref dest, ref acc, ref accBits, ref bytePos, (0b001 << numericCountBits) | segment.UnitCount, RmQRConstants.ModeIndicatorLength + numericCountBits);
                    WriteNumeric(ref dest, ref acc, ref accBits, ref bytePos, chars, vectorized: true);
                    break;
                case 1:
                    if (segment.UnitCount != ModeSegmenter.AlphanumericUnitsFnc1(chars))
                        throw new ArgumentException($"Segment plan gives a {segment.Length}-character run a unit count of {segment.UnitCount}; under FNC1 an Alphanumeric run counts its literal percents twice.", nameof(segments));
                    Debug.Assert(segment.UnitCount < (1 << alnumCountBits), "run length must fit the character count indicator");
                    Append(ref dest, ref acc, ref accBits, ref bytePos, (0b010 << alnumCountBits) | segment.UnitCount, RmQRConstants.ModeIndicatorLength + alnumCountBits);
                    WriteAlphanumericFnc1(ref dest, ref acc, ref accBits, ref bytePos, chars);
                    break;
                case 2:
                    if (charset != EciMode.Utf8 && segment.UnitCount != segment.Length)
                        throw new ArgumentException($"Segment plan gives a {segment.Length}-character run a unit count of {segment.UnitCount}; they must agree outside UTF-8 Byte mode.", nameof(segments));
                    Debug.Assert(segment.UnitCount < (1 << byteCountBits), "run length must fit the character count indicator");
                    Append(ref dest, ref acc, ref accBits, ref bytePos, (0b011 << byteCountBits) | segment.UnitCount, RmQRConstants.ModeIndicatorLength + byteCountBits);
                    if (charset == EciMode.Utf8)
                        WriteUtf8Segment(ref dest, ref acc, ref accBits, ref bytePos, chars, segment.UnitCount, utf8);
                    else
                        WriteLatin1(ref dest, ref acc, ref accBits, ref bytePos, chars, vectorized: true);
                    break;
                default:
                    throw new ArgumentException("A plan under FNC1 holds Numeric, Alphanumeric and Byte runs only.", nameof(segments));
            }
        }

        if (expectedStart != text.Length)
            throw new ArgumentException("Segment plan must cover the content in order with non-empty runs.", nameof(segments));

        Finish(ref dest, acc, accBits, bytePos, codewordCount, capacityBits);
        return codewordCount;
    }

    /// <summary>The FNC1 indicator: <c>101</c> in first position, <c>110</c> and the 8-bit application indicator in second, nothing for none.</summary>
    private static void WriteFnc1Header(ref byte dest, ref ulong acc, ref int accBits, ref int bytePos, Fnc1Mode fnc1)
    {
        if (fnc1.IsFirstPosition)
            Append(ref dest, ref acc, ref accBits, ref bytePos, Fnc1FirstIndicator, RmQRConstants.ModeIndicatorLength);
        else if (fnc1.IsSecondPosition)
            Append(ref dest, ref acc, ref accBits, ref bytePos, (Fnc1SecondIndicator << Fnc1ApplicationIndicatorBits) | fnc1.Codeword, RmQRConstants.ModeIndicatorLength + Fnc1ApplicationIndicatorBits);
    }

    /// <summary>
    /// The Alphanumeric payload under FNC1 (7.4.8): a separator is written as <c>%</c> and a literal <c>%</c> as
    /// <c>%%</c>, and the values pair into 11 bits as in any Alphanumeric run. The caller keeps a separator pair out
    /// of the run (a reader would take the <c>%%</c> first). Scalar: the vectorized writer never sees a separator.
    /// </summary>
    private static void WriteAlphanumericFnc1(ref byte dest, ref ulong acc, ref int accBits, ref int bytePos, ReadOnlySpan<char> chars)
    {
        var pending = -1;
        foreach (var c in chars)
        {
            if (c == FieldSeparator)
            {
                PutAlphanumeric(ref dest, ref acc, ref accBits, ref bytePos, ref pending, PercentValue);
            }
            else if (c == '%')
            {
                PutAlphanumeric(ref dest, ref acc, ref accBits, ref bytePos, ref pending, PercentValue);
                PutAlphanumeric(ref dest, ref acc, ref accBits, ref bytePos, ref pending, PercentValue);
            }
            else
            {
                PutAlphanumeric(ref dest, ref acc, ref accBits, ref bytePos, ref pending, AlnumValues[c & 0x7F]);
            }
        }
        if (pending >= 0)
            Append(ref dest, ref acc, ref accBits, ref bytePos, pending, 6);
    }

    private static void PutAlphanumeric(ref byte dest, ref ulong acc, ref int accBits, ref int bytePos, ref int pending, int value)
    {
        if (pending < 0)
        {
            pending = value;
            return;
        }
        Append(ref dest, ref acc, ref accBits, ref bytePos, pending * 45 + value, 11);
        pending = -1;
    }

    /// <summary>The UTF-8 payload of a single Byte run, transcoded into the stack budget the analysis's byte count fits, or a rented buffer past it.</summary>
    private static void WriteUtf8(ref byte dest, ref ulong acc, ref int accBits, ref int bytePos, ReadOnlySpan<char> text, int byteCount)
    {
        if (byteCount <= StackByteBudget)
        {
            Span<byte> utf8 = stackalloc byte[StackByteBudget];
            var length = CharacterSets.GetUtf8Bytes(text, utf8);
            WriteBytes(ref dest, ref acc, ref accBits, ref bytePos, utf8.Slice(0, length));
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(text.Length * 3);
        try
        {
            var length = CharacterSets.GetUtf8Bytes(text, rented);
            WriteBytes(ref dest, ref acc, ref accBits, ref bytePos, rented.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
