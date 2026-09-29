#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals;

internal static partial class TextAnalyzer
{
    /// <summary>
    /// Portable 128-bit analysis, 16 chars a step, with 8-char remainder blocks (WebAssembly's tier).
    /// A block holding a char above U+00FF settles every flag at once, since such a char is neither numeric, alphanumeric nor Latin-1,
    /// so the rest of the text cannot change the result. Otherwise every char fits a byte, so the block narrows to one byte vector
    /// exactly and is classified there.
    /// </summary>
    /// <param name="text">The text to inspect.</param>
    /// <param name="requestedEciMode">The character encoding the caller asked for, or Default to choose one from the text.</param>
    /// <returns>The narrowest encoding mode the text fits, its length in that mode's units, and the ECI mode to declare.</returns>
    internal static TextAnalysisResult AnalyzeVector128(ReadOnlySpan<char> text, EciMode requestedEciMode)
    {
        var hasNonNumeric = false;
        var hasNonAlphanumeric = false;
        var hasNonAscii = false;
        var hasNonIso88591 = false;

        var i = 0;
        var length = text.Length;
        ref var src = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(text));

        for (; i <= length - 16; i += 16)
        {
            if (ClassifyBlockVector128(Vector128.LoadUnsafe(ref src, (nuint)i), Vector128.LoadUnsafe(ref src, (nuint)(i + 8)),
                ref hasNonNumeric, ref hasNonAlphanumeric, ref hasNonAscii, ref hasNonIso88591))
                break;
        }

        // Every flag only ever turns on, so classifying a char twice is harmless, which allows the overlapped last block
        if (!hasNonIso88591)
        {
            // 8-15 remaining chars: one 8-wide block, taken as both halves
            if (i <= length - 8)
            {
                var block = Vector128.LoadUnsafe(ref src, (nuint)i);
                ClassifyBlockVector128(block, block, ref hasNonNumeric, ref hasNonAlphanumeric, ref hasNonAscii, ref hasNonIso88591);
                i += 8;
            }

            // 4-7 remaining chars: the 8 that end at the last char. Below 4 the scalar loop is cheaper than classifying 5-7 again
            if (!hasNonIso88591 && length - i >= 4 && length >= 8)
            {
                var block = Vector128.LoadUnsafe(ref src, (nuint)(length - 8));
                ClassifyBlockVector128(block, block, ref hasNonNumeric, ref hasNonAlphanumeric, ref hasNonAscii, ref hasNonIso88591);
                i = length;
            }

            for (; i < length && !hasNonIso88591; i++)
            {
                var c = text[i];

                if (!hasNonAscii && c > 127)
                    hasNonAscii = true;

                if (c > 255)
                    hasNonIso88591 = true;

                if (!hasNonNumeric && !CharacterSets.IsNumeric(c))
                    hasNonNumeric = true;

                if (!hasNonAlphanumeric && !CharacterSets.IsAlphanumeric(c))
                    hasNonAlphanumeric = true;
            }
        }

        var encoding = DetermineEncoding(hasNonNumeric, hasNonAlphanumeric);

        var actualEciMode = requestedEciMode == EciMode.Default
            ? DetermineEciMode(hasNonAscii, hasNonIso88591)
            : requestedEciMode;

        var dataLength = CalculateLength(text, encoding, actualEciMode, hasNonIso88591);

        return new TextAnalysisResult(encoding, actualEciMode, dataLength);
    }

    /// <summary>Classifies 16 chars; true when one above U+00FF settled every flag.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ClassifyBlockVector128(Vector128<ushort> lo, Vector128<ushort> hi,
        ref bool hasNonNumeric, ref bool hasNonAlphanumeric, ref bool hasNonAscii, ref bool hasNonIso88591)
    {
        var any = lo | hi;
        if ((any & Vector128.Create((ushort)0xFF00)) != Vector128<ushort>.Zero)
        {
            hasNonNumeric = true;
            hasNonAlphanumeric = true;
            hasNonAscii = true;
            hasNonIso88591 = true;
            return true;
        }

        if (!hasNonAscii && (any & Vector128.Create((ushort)0xFF80)) != Vector128<ushort>.Zero)
            hasNonAscii = true;

        if (hasNonAlphanumeric)
            return false;

        // Exact: no char is above U+00FF
        var bytes = Vector128.Narrow(lo, hi);

        // (c - '0') wraps unsigned, so any non-digit is above 9
        if (!hasNonNumeric && Vector128.GreaterThanAny(bytes - Vector128.Create((byte)'0'), Vector128.Create((byte)9)))
            hasNonNumeric = true;

        if (hasNonNumeric && !IsAllAlphanumericVector128(bytes))
            hasNonAlphanumeric = true;

        return false;
    }

    // The alphanumeric alphabet (ISO/IEC 18004 Section 7.4.3) as four unsigned ranges and a space: '-' to ':' is one run of
    // code points ("-./0123456789:"), then 'A'-'Z', '$%' and '*+'.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAllAlphanumericVector128(Vector128<byte> chars)
    {
        var valid = Vector128.LessThanOrEqual(chars - Vector128.Create((byte)'-'), Vector128.Create((byte)13))
            | Vector128.LessThanOrEqual(chars - Vector128.Create((byte)'A'), Vector128.Create((byte)25))
            | Vector128.LessThanOrEqual(chars - Vector128.Create((byte)'$'), Vector128.Create((byte)1))
            | Vector128.LessThanOrEqual(chars - Vector128.Create((byte)'*'), Vector128.Create((byte)1))
            | Vector128.Equals(chars, Vector128.Create((byte)' '));
        return valid == Vector128<byte>.AllBitsSet;
    }
}
#endif
