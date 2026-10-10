using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics.Arm;
#endif

namespace FeatherQR.Internals.StandardQR;

/// <summary>Character boundaries used by the single-mode Structured Append cost model.</summary>
internal static partial class StructuredAppendScanner
{
    /// <summary>The leading Numeric and Alphanumeric lengths; digits belong to both alphabets.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ModeBoundaries(ReadOnlySpan<char> text, out int digits, out int alnum)
    {
#if NET8_0_OR_GREATER
        if (AdvSimd.Arm64.IsSupported && text.Length >= 16)
        {
            ModeBoundariesAdvSimd(text, out digits, out alnum);
            return;
        }
#endif
        ModeBoundariesScalar(text, out digits, out alnum);
    }

    internal static void ModeBoundariesScalar(ReadOnlySpan<char> text, out int digits, out int alnum)
    {
        var d = 0;
        while (d < text.Length && (uint)(text[d] - '0') <= 9)
            d++;
        var a = d;
        while (a < text.Length && CharacterSets.IsAlphanumeric(text[a]))
            a++;
        digits = d;
        alnum = a;
    }

    /// <summary>Longest UTF-8 prefix within the byte budget, without cutting a surrogate pair.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Utf8PrefixLength(ReadOnlySpan<char> text, int bytes)
    {
#if NET8_0_OR_GREATER
        if (AdvSimd.Arm64.IsSupported && text.Length >= 8 && bytes >= 8)
            return Utf8PrefixLengthAdvSimd(text, bytes);
#endif
        return Utf8PrefixLengthScalar(text, bytes);
    }

    internal static int Utf8PrefixLengthScalar(ReadOnlySpan<char> text, int bytes)
    {
        var used = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            int cost, step = 1;
            if (c < 0x80)
                cost = 1;
            else if (c < 0x800)
                cost = 2;
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                (cost, step) = (4, 2);
            else
                cost = 3; // A BMP character or a lone surrogate written as U+FFFD.
            if (used + cost > bytes)
                break;
            used += cost;
            i += step;
        }
        return i;
    }
}
