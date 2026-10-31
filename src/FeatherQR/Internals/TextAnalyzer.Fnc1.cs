namespace FeatherQR.Internals;

internal static partial class TextAnalyzer
{
    private const char FieldSeparator = '\u001D';

    /// <summary>
    /// Analyzes the single-mode encoding and charset under FNC1 (ISO/IEC 18004 6.4.8.1).
    /// </summary>
    /// <remarks>
    /// With <paramref name="allowKanji"/>, text entirely representable in JIS X 0208 can use Kanji mode.
    /// </remarks>
    /// <remarks>The result plans no Kanji segments: <see cref="QRSegmentation.Optimal"/> under FNC1 mixes Numeric, Alphanumeric and Byte runs only.</remarks>
    public static TextAnalysisResult AnalyzeFnc1(ReadOnlySpan<char> text, EciMode requestedEciMode, bool allowKanji)
    {
        // U+001D contributes one Alphanumeric character (%) and a literal % contributes two (%%).
        // A separator followed by another separator or % rules out Alphanumeric: the reader consumes %% first.
        // This scalar route is separate so ordinary text analysis retains its SIMD path.
        if (text.IsEmpty)
            return new TextAnalysisResult(EncodingMode.Byte, requestedEciMode, 0);

        var hasNonNumeric = false;
        var hasNonAlphanumeric = false;
        var hasNonAscii = false;
        var hasNonIso88591 = false;
        var percents = 0;
        var previousWasSeparator = false;
        foreach (var c in text)
        {
            if (c > 127)
                hasNonAscii = true;
            if (c > 255)
                hasNonIso88591 = true;
            if (!CharacterSets.IsNumeric(c))
                hasNonNumeric = true;

            if (c == FieldSeparator)
            {
                if (previousWasSeparator)
                    hasNonAlphanumeric = true;
                previousWasSeparator = true;
                continue;
            }
            if (c == '%')
            {
                percents++;
                if (previousWasSeparator)
                    hasNonAlphanumeric = true;
            }
            else if (!CharacterSets.IsAlphanumeric(c))
            {
                hasNonAlphanumeric = true;
            }
            previousWasSeparator = false;
        }

        var encoding = DetermineEncoding(hasNonNumeric, hasNonAlphanumeric);
        var eciMode = requestedEciMode == EciMode.Default ? DetermineEciMode(hasNonAscii, hasNonIso88591) : requestedEciMode;

        if (allowKanji && encoding == EncodingMode.Byte && eciMode == EciMode.Utf8 && requestedEciMode == EciMode.Default && IsAllKanjiCells(text))
            return new TextAnalysisResult(EncodingMode.Kanji, EciMode.Default, text.Length);

        var dataLength = encoding == EncodingMode.Alphanumeric
            ? text.Length + percents
            : CalculateLength(text, encoding, eciMode, hasNonIso88591);
        return new TextAnalysisResult(encoding, eciMode, dataLength);
    }

    private static bool IsAllKanjiCells(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (c < 0x80 || !ShiftJisKanjiReverseTable.HasCell(c))
                return false;
        }
        return true;
    }
}
