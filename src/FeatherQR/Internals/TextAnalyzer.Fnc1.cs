namespace FeatherQR.Internals;

internal static partial class TextAnalyzer
{
    private const char FieldSeparator = '\u001D';

    /// <summary>
    /// The single-mode analysis under FNC1 (ISO/IEC 18004 6.4.8.1). The separator U+001D is an Alphanumeric
    /// character, written <c>%</c>, and a literal <c>%</c> costs two, written <c>%%</c>. A separator followed by
    /// a separator or a literal <c>%</c> keeps the text out of Alphanumeric mode, since a reader takes <c>%%</c>
    /// first within a segment (specs/standardqr-encoder.md, FNC1). Everything else is the analysis without FNC1:
    /// the ECI follows the content or the request, and with <paramref name="allowKanji"/> a text JIS X 0208 holds
    /// entirely takes Kanji mode. It is scalar and kept apart from the SIMD analysis, so the route without FNC1 keeps
    /// its instructions.
    /// </summary>
    /// <remarks>The result plans no Kanji segments: <see cref="QRSegmentation.Optimal"/> under FNC1 mixes Numeric, Alphanumeric and Byte runs only.</remarks>
    public static TextAnalysisResult AnalyzeFnc1(ReadOnlySpan<char> text, EciMode requestedEciMode, bool allowKanji)
    {
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
