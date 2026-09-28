#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace FeatherQR.Internals;

internal static partial class TextAnalyzer
{
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

        var dataLength = CalculateLength(text, encoding, actualEciMode, hasNonIso88591);

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
}
#endif
