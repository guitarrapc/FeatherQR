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

internal readonly record struct TextAnalysisResult(EncodingMode EncodingMode, EciMode EciMode, int DataLength);

/// <summary>
/// Text analyzer for automatic encoding and ECI mode detection in single pass.
/// </summary>
internal static class TextAnalyzer
{
    private static readonly Encoding Iso88591Encoding = Encoding.GetEncoding("ISO-8859-1");

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

#if SIMD_SUPPORTED

    /// <summary>
    /// AVX2 optimized analysis (process 16 chars at once)
    /// </summary>
    /// <param name="text">The text to inspect.</param>
    /// <param name="requestedEciMode">The character encoding the caller asked for, or Default to choose one from the text.</param>
    /// <returns>The narrowest encoding mode the text fits, its length in that mode's units, and the ECI mode to declare.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TextAnalysisResult AnalyzeAvx2(ReadOnlySpan<char> text, EciMode requestedEciMode)
    {
        var hasNonNumeric = false;
        var hasNonAlphanumeric = false;
        var hasNonAscii = false;
        var hasNonIso88591 = false;

        var i = 0;
        var length = text.Length;

        // AVX2 processing 16 chars at once
        for (; i <= length - 16; i += 16)
        {
            // load 16 chars as Vector256<ushort>
            var chars = Vector256.Create(
                text[i], text[i + 1], text[i + 2], text[i + 3],
                text[i + 4], text[i + 5], text[i + 6], text[i + 7],
                text[i + 8], text[i + 9], text[i + 10], text[i + 11],
                text[i + 12], text[i + 13], text[i + 14], text[i + 15]
            );

            var charsInt16 = chars.AsInt16();

            // ASCII check (0-127). Both thresholds must be tested UNSIGNED: a UTF-16
            // code unit at or above U+8000 (surrogates, U+FFFD, most of the CJK
            // compatibility area) is negative as an Int16, so a signed
            // CompareGreaterThan reports it as in-range and the text would be declared
            // Latin-1 encodable. c > 127 <=> any bit from bit 7 up is set, and vptest
            // answers that directly without a compare.
            if (!hasNonAscii)
            {
                var aboveAscii = Vector256.Create(unchecked((short)0xFF80));
                if (!Avx2.TestZ(charsInt16, aboveAscii))
                {
                    hasNonAscii = true;
                }
            }

            // ISO-8859-1 check (0-255): same reasoning, c > 255 <=> any high-byte bit set.
            if (!hasNonIso88591 && hasNonAscii)
            {
                var aboveIso = Vector256.Create(unchecked((short)0xFF00));
                if (!Avx2.TestZ(charsInt16, aboveIso))
                {
                    hasNonIso88591 = true;
                }
            }

            // Numeric check (0-9)
            if (!hasNonNumeric)
            {
                var char0 = Vector256.Create((short)'0');
                var char9 = Vector256.Create((short)'9');

                var lessThan0 = Avx2.CompareGreaterThan(char0, charsInt16);
                var greaterThan9 = Avx2.CompareGreaterThan(charsInt16, char9);
                var nonNumericMask = Avx2.Or(lessThan0, greaterThan9);

                if (!Avx2.TestZ(nonNumericMask, nonNumericMask))
                {
                    hasNonNumeric = true;
                }
            }

            // Alphanumeric check (require scalar processing due to complexity)
            if (!hasNonAlphanumeric && hasNonNumeric)
            {
                if (!IsAllAlphanumericAvx2(charsInt16))
                {
                    hasNonAlphanumeric = true;
                }
            }

            // Early exit if all types are found
            if (hasNonNumeric && hasNonAlphanumeric && hasNonIso88591)
                break;
        }

        // Process remaining chars with scalar fallback
        for (; i < length; i++)
        {
            var c = text[i];

            if (!hasNonAscii && c > 127)
                hasNonAscii = true;

            if (!hasNonIso88591 && c > 255)
                hasNonIso88591 = true;

            if ((!hasNonNumeric && !CharacterSets.IsNumeric(c)))
                hasNonNumeric = true;

            if (!hasNonAlphanumeric && !CharacterSets.IsAlphanumeric(c))
                hasNonAlphanumeric = true;

            if (hasNonNumeric && hasNonAlphanumeric && hasNonIso88591)
                break;
        }

        var encoding = DetermineEncoding(hasNonNumeric, hasNonAlphanumeric);

        // If there are user constraints (e.g. requestedVersion), calculate actual data length.
        var actualEciMode = requestedEciMode == EciMode.Default
            ? DetermineEciMode(hasNonAscii, hasNonIso88591)
            : requestedEciMode;

        var dataLength = CalculateLength(text, encoding, actualEciMode);

        return new TextAnalysisResult(encoding, actualEciMode, dataLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAllAlphanumericAvx2(Vector256<short> chars)
    {
        // Digits: 0-9
        var char0 = Vector256.Create((short)'0');
        var char9 = Vector256.Create((short)'9');
        var lessThan0 = Avx2.CompareGreaterThan(char0, chars);
        var greaterThan9 = Avx2.CompareGreaterThan(chars, char9);
        var notInDigitRange = Avx2.Or(lessThan0, greaterThan9);
        var isDigit = Avx2.AndNot(notInDigitRange, Vector256.Create((short)-1));

        // Uppercase letters: A-Z
        var charA = Vector256.Create((short)'A');
        var charZ = Vector256.Create((short)'Z');
        var lessThanA = Avx2.CompareGreaterThan(charA, chars);
        var greaterThanZ = Avx2.CompareGreaterThan(chars, charZ);
        var notInUpperRange = Avx2.Or(lessThanA, greaterThanZ);
        var isUpper = Avx2.AndNot(notInUpperRange, Vector256.Create((short)-1));

        // Special characters: space, $, %, *, +, -, ., /, :
        var space = Avx2.CompareEqual(chars, Vector256.Create((short)' ')); // 0x20
        var dollar = Avx2.CompareEqual(chars, Vector256.Create((short)'$')); // 0x24
        var percent = Avx2.CompareEqual(chars, Vector256.Create((short)'%')); // 0x25
        var asterisk = Avx2.CompareEqual(chars, Vector256.Create((short)'*')); // 0x2A
        var plus = Avx2.CompareEqual(chars, Vector256.Create((short)'+')); // 0x2B
        var minus = Avx2.CompareEqual(chars, Vector256.Create((short)'-')); // 0x2D
        var period = Avx2.CompareEqual(chars, Vector256.Create((short)'.')); // 0x2E
        var slash = Avx2.CompareEqual(chars, Vector256.Create((short)'/')); // 0x2F
        var colon = Avx2.CompareEqual(chars, Vector256.Create((short)':')); // 0x3A

        var isSpecial = Avx2.Or(
            Avx2.Or(Avx2.Or(space, dollar), Avx2.Or(percent, asterisk)),
            Avx2.Or(Avx2.Or(plus, minus), Avx2.Or(Avx2.Or(period, slash), colon))
        );

        // combine all checks
        var isValid = Avx2.Or(Avx2.Or(isDigit, isUpper), isSpecial);

        // Check all bits are flagged.
        var allOnes = Vector256.Create((short)-1);
        var mask = Avx2.CompareEqual(isValid, allOnes);

        // All 32bytes must be 0xFF, so MoveMask must return 0xFFFFFFFF
        return Avx2.MoveMask(mask.AsByte()) == -1; // 0xFFFFFFFF
    }

    /// <summary>
    /// SSE2 optimized analysis (process 8 chars at once)
    /// </summary>
    /// <param name="text">The text to inspect.</param>
    /// <param name="requestedEciMode">The character encoding the caller asked for, or Default to choose one from the text.</param>
    /// <returns>The narrowest encoding mode the text fits, its length in that mode's units, and the ECI mode to declare.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TextAnalysisResult AnalyzeSse2(ReadOnlySpan<char> text, EciMode requestedEciMode)
    {
        var hasNonNumeric = false;
        var hasNonAlphanumeric = false;
        var hasNonAscii = false;
        var hasNonIso88591 = false;

        var i = 0;
        var length = text.Length;

        // SSE2 processing 8 chars at once
        for (; i <= length - 8; i += 8)
        {
            // load 8 chars as Vector128<ushort>
            var chars = Vector128.Create(
                text[i], text[i + 1], text[i + 2], text[i + 3],
                text[i + 4], text[i + 5], text[i + 6], text[i + 7]
            );

            var charsInt16 = chars.AsInt16();

            // ASCII check (0-127). Both thresholds must be tested UNSIGNED: a UTF-16
            // code unit at or above U+8000 (surrogates, U+FFFD, most of the CJK
            // compatibility area) is negative as an Int16, so a signed
            // CompareGreaterThan reports it as in-range and the text would be declared
            // Latin-1 encodable. c > 127 <=> any bit from bit 7 up is set; SSE2 has no
            // PTEST, so the test is AND against zero (MoveMask cannot see bit 8-15 of a
            // lane, which is exactly where the ISO-8859-1 answer lives).
            if (!hasNonAscii)
            {
                var aboveAscii = Sse2.And(charsInt16, Vector128.Create(unchecked((short)0xFF80)));
                if (Sse2.MoveMask(Sse2.CompareEqual(aboveAscii, Vector128<short>.Zero).AsByte()) != 0xFFFF)
                {
                    hasNonAscii = true;
                }
            }

            // ISO-8859-1 check (0-255): same reasoning, c > 255 <=> any high-byte bit set.
            if (!hasNonIso88591 && hasNonAscii)
            {
                var aboveIso = Sse2.And(charsInt16, Vector128.Create(unchecked((short)0xFF00)));
                if (Sse2.MoveMask(Sse2.CompareEqual(aboveIso, Vector128<short>.Zero).AsByte()) != 0xFFFF)
                {
                    hasNonIso88591 = true;
                }
            }

            // Numeric check (0-9)
            if (!hasNonNumeric)
            {
                var char0 = Vector128.Create((short)'0');
                var char9 = Vector128.Create((short)'9');

                var lessThan0 = Sse2.CompareLessThan(charsInt16, char0);
                var greaterThan9 = Sse2.CompareGreaterThan(charsInt16, char9);
                var nonNumericMask = Sse2.Or(lessThan0, greaterThan9);

                if (Sse2.MoveMask(nonNumericMask.AsByte()) != 0)
                {
                    hasNonNumeric = true;
                }
            }

            // Alphanumeric check (require scalar processing due to complexity)
            if (!hasNonAlphanumeric && hasNonNumeric)
            {
                if (!IsAllAlphanumericSse2(charsInt16))
                {
                    hasNonAlphanumeric = true;
                }
            }

            // Early exit if all types are found
            if (hasNonNumeric && hasNonAlphanumeric && hasNonIso88591)
                break;
        }

        // Process remaining chars with scalar fallback
        for (; i < length; i++)
        {
            var c = text[i];

            if (!hasNonAscii && c > 127)
                hasNonAscii = true;

            if (!hasNonIso88591 && c > 255)
                hasNonIso88591 = true;

            if ((!hasNonNumeric && !CharacterSets.IsNumeric(c)))
                hasNonNumeric = true;

            if (!hasNonAlphanumeric && !CharacterSets.IsAlphanumeric(c))
                hasNonAlphanumeric = true;

            if (hasNonNumeric && hasNonAlphanumeric && hasNonIso88591)
                break;
        }

        var encoding = DetermineEncoding(hasNonNumeric, hasNonAlphanumeric);

        // If there are user constraints (e.g. requestedVersion), calculate actual data length.
        var actualEciMode = requestedEciMode == EciMode.Default
            ? DetermineEciMode(hasNonAscii, hasNonIso88591)
            : requestedEciMode;

        var dataLength = CalculateLength(text, encoding, actualEciMode);

        return new TextAnalysisResult(encoding, actualEciMode, dataLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAllAlphanumericSse2(Vector128<short> chars)
    {
        // Digits: 0-9
        var char0 = Vector128.Create((short)'0');
        var char9 = Vector128.Create((short)'9');
        var lessThan0 = Sse2.CompareGreaterThan(char0, chars);
        var greaterThan9 = Sse2.CompareGreaterThan(chars, char9);
        var notInDigitRange = Sse2.Or(lessThan0, greaterThan9);
        var isDigit = Sse2.AndNot(notInDigitRange, Vector128.Create((short)-1));

        // Uppercase letters: A-Z
        var charA = Vector128.Create((short)'A');
        var charZ = Vector128.Create((short)'Z');
        var lessThanA = Sse2.CompareGreaterThan(charA, chars);
        var greaterThanZ = Sse2.CompareGreaterThan(chars, charZ);
        var notInUpperRange = Sse2.Or(lessThanA, greaterThanZ);
        var isUpper = Sse2.AndNot(notInUpperRange, Vector128.Create((short)-1));

        // Special characters: space, $, %, *, +, -, ., /, :
        var space = Sse2.CompareEqual(chars, Vector128.Create((short)' ')); // 0x20
        var dollar = Sse2.CompareEqual(chars, Vector128.Create((short)'$')); // 0x24
        var percent = Sse2.CompareEqual(chars, Vector128.Create((short)'%')); // 0x25
        var asterisk = Sse2.CompareEqual(chars, Vector128.Create((short)'*')); // 0x2A
        var plus = Sse2.CompareEqual(chars, Vector128.Create((short)'+')); // 0x2B
        var minus = Sse2.CompareEqual(chars, Vector128.Create((short)'-')); // 0x2D
        var period = Sse2.CompareEqual(chars, Vector128.Create((short)'.')); // 0x2E
        var slash = Sse2.CompareEqual(chars, Vector128.Create((short)'/')); // 0x2F
        var colon = Sse2.CompareEqual(chars, Vector128.Create((short)':')); // 0x3A

        var isSpecial = Sse2.Or(
            Sse2.Or(Sse2.Or(space, dollar), Sse2.Or(percent, asterisk)),
            Sse2.Or(Sse2.Or(plus, minus), Sse2.Or(Sse2.Or(period, slash), colon))
        );

        // combine all checks
        var isValid = Sse2.Or(Sse2.Or(isDigit, isUpper), isSpecial);

        // Check all bits are flagged.
        var allOnes = Vector128.Create((short)-1);
        var allValid = Sse2.CompareEqual(isValid, allOnes);

        // All 16bytes must be 0xFF, so MoveMask must return 0xFFFF
        return Sse2.MoveMask(allValid.AsByte()) == 0xFFFF;
    }

#if NET8_0_OR_GREATER
    /// <summary>
    /// ARM64 NEON optimized analysis (16 chars per step, vector remainder blocks).
    /// Two ushort loads saturate-narrow (UQXTN/UQXTN2) into one byte vector per step: chars above 0xFF clamp to 0xFF, which classifies identically (non-numeric, non-alphanumeric), so byte-domain checks stay exact.
    /// ASCII/ISO detection keeps 16-bit precision via one UMAXV of the raw block, whose max answers both the &gt;127 and &gt;255 thresholds at once.
    /// </summary>
    /// <param name="text">The text to inspect.</param>
    /// <param name="requestedEciMode">The character encoding the caller asked for, or Default to choose one from the text.</param>
    /// <returns>The narrowest encoding mode the text fits, its length in that mode's units, and the ECI mode to declare.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TextAnalysisResult AnalyzeAdvSimd(ReadOnlySpan<char> text, EciMode requestedEciMode)
    {
        var hasNonNumeric = false;
        var hasNonAlphanumeric = false;
        var hasNonAscii = false;
        var hasNonIso88591 = false;

        var i = 0;
        var length = text.Length;
        ref var src = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(text));

        var char0Byte = Vector128.Create((byte)'0');
        var char0Wide = Vector128.Create((ushort)'0');

        // NEON processing 16 chars at once
        for (; i <= length - 16; i += 16)
        {
            var lo = Vector128.LoadUnsafe(ref src, (nuint)i);
            var hi = Vector128.LoadUnsafe(ref src, (nuint)(i + 8));

            // ASCII (0-127) and ISO-8859-1 (0-255) checks share one reduction
            if (!hasNonIso88591)
            {
                var blockMax = AdvSimd.Arm64.MaxAcross(AdvSimd.Max(lo, hi)).ToScalar();
                if (blockMax > 127) hasNonAscii = true;
                if (blockMax > 255) hasNonIso88591 = true;
            }

            var bytes = AdvSimd.ExtractNarrowingSaturateUpper(AdvSimd.ExtractNarrowingSaturateLower(lo), hi);

            // Numeric check (0-9): (c - '0') wraps unsigned, so any non-digit
            // pushes the block max above 9, one SUB + one UMAXV
            if (!hasNonNumeric)
            {
                if (AdvSimd.Arm64.MaxAcross(AdvSimd.Subtract(bytes, char0Byte)).ToScalar() > 9)
                    hasNonNumeric = true;
            }

            // Alphanumeric check
            if (!hasNonAlphanumeric && hasNonNumeric)
            {
                if (!IsAllAlphanumericAdvSimd(bytes, char0Byte))
                    hasNonAlphanumeric = true;
            }

            // Early exit if all types are found
            if (hasNonNumeric && hasNonAlphanumeric && hasNonIso88591)
                break;
        }

        // Every flag is a monotonic OR, so once all are set the remainder
        // cannot change the result, and re-classifying a char twice is
        // harmless, which allows the overlapped final block below.
        if (!(hasNonNumeric && hasNonAlphanumeric && hasNonIso88591))
        {
            // 8-15 remaining chars: one 8-wide block
            if (i <= length - 8)
            {
                AnalyzeBlockAdvSimd(Vector128.LoadUnsafe(ref src, (nuint)i), char0Wide,
                    ref hasNonNumeric, ref hasNonAlphanumeric, ref hasNonAscii, ref hasNonIso88591);
                i += 8;
            }

            // 4-7 remaining chars: one 8-wide block loaded to END at the last
            // char (re-checks up to 4 already-classified chars). Below 4
            // remaining, the scalar loop is cheaper than re-checking 5-7.
            if (length - i >= 4 && length >= 8)
            {
                AnalyzeBlockAdvSimd(Vector128.LoadUnsafe(ref src, (nuint)(length - 8)), char0Wide,
                    ref hasNonNumeric, ref hasNonAlphanumeric, ref hasNonAscii, ref hasNonIso88591);
                i = length;
            }

            // Process remaining chars with scalar fallback
            for (; i < length; i++)
            {
                var c = text[i];

                if (!hasNonAscii && c > 127)
                    hasNonAscii = true;

                if (!hasNonIso88591 && c > 255)
                    hasNonIso88591 = true;

                if (!hasNonNumeric && !CharacterSets.IsNumeric(c))
                    hasNonNumeric = true;

                if (!hasNonAlphanumeric && !CharacterSets.IsAlphanumeric(c))
                    hasNonAlphanumeric = true;

                if (hasNonNumeric && hasNonAlphanumeric && hasNonIso88591)
                    break;
            }
        }

        var encoding = DetermineEncoding(hasNonNumeric, hasNonAlphanumeric);

        // If there are user constraints (e.g. requestedVersion), calculate actual data length.
        var actualEciMode = requestedEciMode == EciMode.Default
            ? DetermineEciMode(hasNonAscii, hasNonIso88591)
            : requestedEciMode;

        var dataLength = CalculateLength(text, encoding, actualEciMode);

        return new TextAnalysisResult(encoding, actualEciMode, dataLength);
    }

    /// <summary>
    /// One 8-wide (ushort-domain) classification step for remainder blocks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AnalyzeBlockAdvSimd(Vector128<ushort> chars, Vector128<ushort> char0,
        ref bool hasNonNumeric, ref bool hasNonAlphanumeric, ref bool hasNonAscii, ref bool hasNonIso88591)
    {
        if (!hasNonIso88591)
        {
            var blockMax = AdvSimd.Arm64.MaxAcross(chars).ToScalar();
            if (blockMax > 127) hasNonAscii = true;
            if (blockMax > 255) hasNonIso88591 = true;
        }

        if (!hasNonNumeric)
        {
            if (AdvSimd.Arm64.MaxAcross(AdvSimd.Subtract(chars, char0)).ToScalar() > 9)
                hasNonNumeric = true;
        }

        if (!hasNonAlphanumeric && hasNonNumeric)
        {
            if (!IsAllAlphanumericAdvSimd(chars, char0))
                hasNonAlphanumeric = true;
        }
    }

    // The alphanumeric alphabet (ISO/IEC 18004 Section 7.4.3) as five unsigned
    // ranges/equalities, '$%', '*+' and '-./' are contiguous code points:
    //   digits  '0'-'9'  : (c - 0x30) <= 9
    //   upper   'A'-'Z'  : (c - 0x41) <= 25
    //   '$','%'          : (c - 0x24) <= 1
    //   '*','+'          : (c - 0x2A) <= 1
    //   '-','.','/'      : (c - 0x2D) <= 2
    //   ' '              : == 0x20
    //   ':'              : == 0x3A

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAllAlphanumericAdvSimd(Vector128<byte> chars, Vector128<byte> char0)
    {
        var isDigit = AdvSimd.CompareLessThanOrEqual(AdvSimd.Subtract(chars, char0), Vector128.Create((byte)9));
        var isUpper = AdvSimd.CompareLessThanOrEqual(AdvSimd.Subtract(chars, Vector128.Create((byte)'A')), Vector128.Create((byte)25));
        var isDollarPercent = AdvSimd.CompareLessThanOrEqual(AdvSimd.Subtract(chars, Vector128.Create((byte)'$')), Vector128.Create((byte)1));
        var isAsteriskPlus = AdvSimd.CompareLessThanOrEqual(AdvSimd.Subtract(chars, Vector128.Create((byte)'*')), Vector128.Create((byte)1));
        var isMinusPeriodSlash = AdvSimd.CompareLessThanOrEqual(AdvSimd.Subtract(chars, Vector128.Create((byte)'-')), Vector128.Create((byte)2));
        var isSpace = AdvSimd.CompareEqual(chars, Vector128.Create((byte)' '));
        var isColon = AdvSimd.CompareEqual(chars, Vector128.Create((byte)':'));

        var isValid = AdvSimd.Or(
            AdvSimd.Or(AdvSimd.Or(isDigit, isUpper), AdvSimd.Or(isDollarPercent, isAsteriskPlus)),
            AdvSimd.Or(isMinusPeriodSlash, AdvSimd.Or(isSpace, isColon)));

        // all lanes valid <=> min lane is 0xFF (UMINV)
        return AdvSimd.Arm64.MinAcross(isValid).ToScalar() == 0xFF;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAllAlphanumericAdvSimd(Vector128<ushort> chars, Vector128<ushort> char0)
    {
        var isDigit = AdvSimd.CompareLessThanOrEqual(AdvSimd.Subtract(chars, char0), Vector128.Create((ushort)9));
        var isUpper = AdvSimd.CompareLessThanOrEqual(AdvSimd.Subtract(chars, Vector128.Create((ushort)'A')), Vector128.Create((ushort)25));
        var isDollarPercent = AdvSimd.CompareLessThanOrEqual(AdvSimd.Subtract(chars, Vector128.Create((ushort)'$')), Vector128.Create((ushort)1));
        var isAsteriskPlus = AdvSimd.CompareLessThanOrEqual(AdvSimd.Subtract(chars, Vector128.Create((ushort)'*')), Vector128.Create((ushort)1));
        var isMinusPeriodSlash = AdvSimd.CompareLessThanOrEqual(AdvSimd.Subtract(chars, Vector128.Create((ushort)'-')), Vector128.Create((ushort)2));
        var isSpace = AdvSimd.CompareEqual(chars, Vector128.Create((ushort)' '));
        var isColon = AdvSimd.CompareEqual(chars, Vector128.Create((ushort)':'));

        var isValid = AdvSimd.Or(
            AdvSimd.Or(AdvSimd.Or(isDigit, isUpper), AdvSimd.Or(isDollarPercent, isAsteriskPlus)),
            AdvSimd.Or(isMinusPeriodSlash, AdvSimd.Or(isSpace, isColon)));

        // all lanes valid <=> min lane is 0xFFFF (UMINV)
        return AdvSimd.Arm64.MinAcross(isValid).ToScalar() == 0xFFFF;
    }
#endif
#endif

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

        var dataLength = CalculateLength(text, encoding, actualEciMode);

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

    private static int CalculateLength(ReadOnlySpan<char> text, EncodingMode encoding, EciMode eciMode)
    {
        return encoding switch
        {
            EncodingMode.Numeric => text.Length,
            EncodingMode.Alphanumeric => text.Length,
            EncodingMode.Byte => CalculateByteCount(text, eciMode),
            _ => text.Length
        };
    }

    private static int CalculateByteCount(ReadOnlySpan<char> text, EciMode eciMode)
    {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        ReadOnlySpan<char> input = text;
#else
        string input = text.ToString();
#endif
        // ISO-8859-x encoding based on ECI mode
        return eciMode switch
        {
            EciMode.Default => CharacterSets.IsValidISO88591(input)
                ? Iso88591Encoding.GetByteCount(input)
                : Encoding.UTF8.GetByteCount(input),
            EciMode.Iso8859_1 => Iso88591Encoding.GetByteCount(input),
            EciMode.Utf8 => Encoding.UTF8.GetByteCount(input),
            _ => throw new ArgumentOutOfRangeException(nameof(eciMode), "Unsupported ECI mode for Byte encoding"),
        };
    }
}
