#if NET5_0_OR_GREATER
#define SIMD_SUPPORTED
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.Wasm;
#endif
using System.Runtime.CompilerServices;
using System.Text;

namespace FeatherQR.Internals;

internal readonly record struct TextAnalysisResult(EncodingMode EncodingMode, EciMode EciMode, int DataLength);

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

        // Portable 128-bit path for WebAssembly (16 chars at once, 8-char remainder blocks)
        if (PackedSimd.IsSupported && text.Length >= 8)
        {
            return AnalyzeVector128(text, requestedEciMode);
        }
#endif
#endif

        // Scalar fallback for .NET Standard or short text
        return AnalyzeScalar(text, requestedEciMode);
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
