#if NET5_0_OR_GREATER
#define SIMD_SUPPORTED
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
#endif
using System.Runtime.CompilerServices;
using System.Text;

namespace FeatherQR.Internals;

/// <summary>The single mode a text fits, the charset it is written in, and its length in that mode's units.</summary>
/// <param name="EncodingMode">The narrowest single mode that holds the whole text.</param>
/// <param name="EciMode">The charset the single-mode stream declares, or <see cref="EciMode.Default"/> for none.</param>
/// <param name="DataLength">The text's length in <paramref name="EncodingMode"/>'s units: characters, or encoded bytes for Byte mode.</param>
/// <param name="KanjiPlannable">
/// The text is Kanji-eligible and holds ASCII, so a mixed-mode plan can write its other characters as Kanji runs beside runs of its ASCII, with no ECI header.
/// Set only for a caller that plans Kanji (<see cref="TextAnalyzer.Analyze(ReadOnlySpan{char}, EciMode, bool, bool)"/>); the rest of the analysis is then the UTF-8 one, which is what the single-mode stream writes.
/// </param>
internal readonly record struct TextAnalysisResult(EncodingMode EncodingMode, EciMode EciMode, int DataLength, bool KanjiPlannable = false);

/// <summary>
/// Text analyzer for automatic encoding and ECI mode detection in single pass.
/// </summary>
internal static partial class TextAnalyzer
{
    /// <summary>
    /// Analyzes the input text to determine the most efficient encoding mode (Numeric, Alphanumeric, Byte). If SIMD is supported, uses SIMD instructions for faster analysis.
    /// </summary>
    /// <param name="text"></param>
    /// <param name="requestedEciMode"></param>
    /// <returns></returns>
    public static TextAnalysisResult Analyze(ReadOnlySpan<char> text, EciMode requestedEciMode)
    {
        // ISO/IEC 18004 does not define behavior for empty data.
        // However from other practical libraries, when data was empty EncodingMode should use Byte mode.
        // Empty data means no actual data to encode, so the difference in only in mode indicator bits (4 bits for Numeric/Alphanumeric vs 4+4 bits for Byte).
        //
        // Why not Numeric or Alphanumeric?
        // - If we use Numeric mode, empty data == no number => ❌ contradiction
        // - If we use Alphanumeric mode, empty data == no character => ❌ contradiction
        // - If we use Byte mode, empty data == 0 length byte => ✔️ valid
        if (text.IsEmpty)
        {
            var actualEciMode = requestedEciMode == EciMode.Default ? EciMode.Default : requestedEciMode;
            return new TextAnalysisResult(EncodingMode.Byte, actualEciMode, 0);
        }

#if SIMD_SUPPORTED
        // SIMD path for x86/x64 AVX2 support (16 chars at once)
        if (Avx2.IsSupported)
        {
            return AnalyzeAvx2(text, requestedEciMode);
        }

        // SIMD path for x86/x64 SSE2 support (8 chars at once)
        if (Sse2.IsSupported && text.Length >= 8)
        {
            return AnalyzeSse2(text, requestedEciMode);
        }

#if NET8_0_OR_GREATER
        // SIMD path for ARM64 NEON support (16 chars at once, 8-char remainder blocks)
        if (AdvSimd.Arm64.IsSupported && text.Length >= 8)
        {
            return AnalyzeAdvSimd(text, requestedEciMode);
        }
#endif
#endif

        // Scalar fallback for .NET Standard or short text
        return AnalyzeScalar(text, requestedEciMode);
    }

    /// <summary>
    /// <see cref="Analyze(ReadOnlySpan{char}, EciMode)"/>, then Kanji mode for a text that Kanji mode holds on its own: the charset was left to the library, the library chose UTF-8, and every character has an encoder cell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every such character costs 13 bits in Kanji mode against 16 or 24 in UTF-8, and the stream carries no ECI header, so the result never needs a larger version than UTF-8 would.
    /// A text with ASCII in it stays UTF-8 here, because one Kanji segment cannot hold ASCII; mixing the two is a plan's business.
    /// </para>
    /// <para>
    /// The pass runs only once the analysis has resolved UTF-8, and stops at the first character without a cell, so ASCII and Latin-1 text pays one comparison for it.
    /// <paramref name="allowKanji"/> is false where UTF-8 was asked for in effect (a byte order mark) or where the caller's path does not write Kanji yet.
    /// </para>
    /// <para>
    /// <paramref name="planKanji"/> is for a mixed-mode path: the pass then reads past ASCII too, and a text whose characters are all ASCII or have a cell comes back as its UTF-8 analysis marked <see cref="TextAnalysisResult.KanjiPlannable"/>.
    /// A single-mode path leaves it off and keeps stopping at the first ASCII character, since one Kanji segment cannot hold one.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TextAnalysisResult Analyze(ReadOnlySpan<char> text, EciMode requestedEciMode, bool allowKanji, bool planKanji = false)
    {
        var analysis = Analyze(text, requestedEciMode);
        return allowKanji && analysis.EciMode == EciMode.Utf8 && requestedEciMode == EciMode.Default
            ? ResolveKanji(text, in analysis, planKanji)
            : analysis;
    }

    /// <summary>
    /// The Kanji analysis of a UTF-8 text when every character has an encoder cell; with <paramref name="planKanji"/>, the UTF-8 analysis marked plannable when every character is ASCII or has one; otherwise the UTF-8 analysis unchanged.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TextAnalysisResult ResolveKanji(ReadOnlySpan<char> text, in TextAnalysisResult utf8, bool planKanji)
    {
        var ascii = false;
        foreach (var c in text)
        {
            // No ASCII character has a cell (U+005C's is one of the seven never written), so ASCII skips the lookup.
            if (c < 0x80)
            {
                if (!planKanji)
                    return utf8;
                ascii = true;
            }
            else if (ShiftJisKanjiReverseTable.Lookup(c) < 0)
            {
                return utf8;
            }
        }

        return ascii
            ? utf8 with { KanjiPlannable = true }
            : new TextAnalysisResult(EncodingMode.Kanji, EciMode.Default, text.Length);
    }

    /// <summary>
    /// Scalar fallback analysis (process 1 char at once)
    /// </summary>
    /// <param name="text">The text to inspect.</param>
    /// <param name="requestedEciMode">The character encoding the caller asked for, or Default to choose one from the text.</param>
    /// <returns>The narrowest encoding mode the text fits, its length in that mode's units, and the ECI mode to declare.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TextAnalysisResult AnalyzeScalar(ReadOnlySpan<char> text, EciMode requestedEciMode)
    {
        var hasNonNumeric = false;
        var hasNonAlphanumeric = false;
        var hasNonAscii = false;
        var hasNonIso88591 = false;

        foreach (var c in text)
        {
            if (!hasNonAscii && c > 127)
                hasNonAscii = true;

            if (!hasNonIso88591 && c > 255)
                hasNonIso88591 = true;

            if (!hasNonNumeric && !CharacterSets.IsNumeric(c))
                hasNonNumeric = true;

            if (!hasNonAlphanumeric && !CharacterSets.IsAlphanumeric(c))
                hasNonAlphanumeric = true;

            // Early exit if all types are found
            if (hasNonNumeric && hasNonAlphanumeric && hasNonIso88591)
                break;
        }

        var encoding = DetermineEncoding(hasNonNumeric, hasNonAlphanumeric);

        // If there are user constraints (e.g. requestedVersion), calculate actual data length.
        var actualEciMode = requestedEciMode == EciMode.Default
            ? DetermineEciMode(hasNonAscii, hasNonIso88591)
            : requestedEciMode;

        var dataLength = CalculateLength(text, encoding, actualEciMode, hasNonIso88591);

        return new TextAnalysisResult(encoding, actualEciMode, dataLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EncodingMode DetermineEncoding(bool hasNonNumeric, bool hasNonAlphanumeric)
    {
        if (!hasNonNumeric)
            return EncodingMode.Numeric;
        if (!hasNonAlphanumeric)
            return EncodingMode.Alphanumeric;
        return EncodingMode.Byte;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EciMode DetermineEciMode(bool hasNonAscii, bool hasNonIso88591)
    {
        if (!hasNonAscii)
            return EciMode.Default;
        if (!hasNonIso88591)
            return EciMode.Iso8859_1;
        return EciMode.Utf8;
    }

    private static int CalculateLength(ReadOnlySpan<char> text, EncodingMode encoding, EciMode eciMode, bool hasNonIso88591)
    {
        return encoding switch
        {
            EncodingMode.Numeric => text.Length,
            EncodingMode.Alphanumeric => text.Length,
            EncodingMode.Byte => CalculateByteCount(text, eciMode, hasNonIso88591),
            _ => text.Length
        };
    }

    /// <summary>
    /// Bytes a Byte-mode segment carries, which is what its character count indicator declares.
    /// </summary>
    /// <remarks>
    /// Only UTF-8 has to be counted. Latin-1 is one byte per <c>char</c>, out-of-range ones included, since the writer narrows each and the encoder replaces each, so the length is the count and no pass over the text is needed; the classification pass has already answered which of the two the charset resolves to, so the rescan that answer used to cost is gone with it.
    /// Pinned against <see cref="Encoding"/> and against the bytes the writer emits, per charset and content class, by <c>TextAnalyzerByteCountTest</c>.
    /// </remarks>
    private static int CalculateByteCount(ReadOnlySpan<char> text, EciMode eciMode, bool hasNonIso88591)
    {
        switch (eciMode)
        {
            case EciMode.Default:
                // Auto-detection declares Default only for ASCII, so the flag is the Latin-1 answer.
                return hasNonIso88591 ? ModeSegmenter.ByteUnitCount(text, EciMode.Utf8) : text.Length;
            case EciMode.Iso8859_1:
                return text.Length;
            case EciMode.Utf8:
                return ModeSegmenter.ByteUnitCount(text, EciMode.Utf8);
            default:
                throw new ArgumentOutOfRangeException(nameof(eciMode), "Unsupported ECI mode for Byte encoding");
        }
    }
}
