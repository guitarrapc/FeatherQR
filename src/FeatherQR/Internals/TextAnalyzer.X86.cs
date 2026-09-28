#if NET5_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR.Internals;

internal static partial class TextAnalyzer
{
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

        var dataLength = CalculateLength(text, encoding, actualEciMode, hasNonIso88591);

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

        var dataLength = CalculateLength(text, encoding, actualEciMode, hasNonIso88591);

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
}
#endif
