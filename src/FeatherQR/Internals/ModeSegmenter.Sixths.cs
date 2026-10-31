#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

namespace FeatherQR.Internals;

internal static partial class ModeSegmenter
{
    /// <summary>The sixths of each ASCII character at its cheapest mode: a digit at the Numeric rate, the rest of the alphanumeric alphabet at the Alphanumeric rate, every other character one byte.</summary>
    private static ReadOnlySpan<byte> asciiSixths => [
        48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48,
        33, 48, 48, 48, 33, 33, 48, 48, 48, 48, 33, 33, 48, 33, 33, 33, // space $ % * + - . /
        20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 33, 48, 48, 48, 48, 48, // 0-9 :
        48, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 48, 48, 48, 48, 48, // A-Z
        48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48,
    ];

    /// <summary>
    /// The sixths sum of pricing each character at the cheapest rate any mode could give it: one pass over the text, no cost table.
    /// Rounded up and topped with the cheapest possible header by the caller, it is a lower bound on any plan at any version, so it may only reject.
    /// </summary>
    /// <remarks>
    /// A character's price is 48 sixths a byte less 28 for a digit and 15 for the rest of the alphanumeric alphabet, so the vector tiers price a block by counting each class with lane compares.
    /// Under UTF-8 a lane adds a byte above 0x7F and another above 0x7FF. A block holding a surrogate half is priced one character at a time, since that price depends on its neighbour.
    /// The 16-bit lanes are summed into an integer before they can wrap, since a Structured Append set holds up to 113,424 characters and a longer input is priced whole before it is refused.
    /// </remarks>
    public static int CheapestSixths(ReadOnlySpan<char> text, EciMode charset)
    {
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && text.Length >= Vector256<ushort>.Count)
            return CheapestSixthsAvx2(text, charset);
        if (Vector128.IsHardwareAccelerated && text.Length >= Vector128<ushort>.Count)
            return CheapestSixthsVector128(text, charset);
#endif
        return CheapestSixthsScalar(text, charset);
    }

    /// <summary>The bound by one table read a character. Under a one-byte charset a character above 0x7F is one byte, so only UTF-8 looks at the character's value.</summary>
    internal static int CheapestSixthsScalar(ReadOnlySpan<char> text, EciMode charset)
    {
        if (charset == EciMode.Utf8)
            return Utf8Sixths(text, 0, text.Length);

        var sixths = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            sixths += c < 0x80 ? asciiSixths[c] : SixthsPerByte;
        }
        return sixths;
    }

    /// <summary>
    /// The UTF-8 price of the characters from <paramref name="start"/> to <paramref name="end"/>. A surrogate's partner may lie just outside, so the whole text is read for it, as <see cref="ByteCost"/> does.
    /// </summary>
    private static int Utf8Sixths(ReadOnlySpan<char> text, int start, int end)
    {
        var sixths = 0;
        for (var i = start; i < end; i++)
        {
            var c = text[i];
            if (c < 0x80)
                sixths += asciiSixths[c];
            else if (c < 0x800)
                sixths += 2 * SixthsPerByte;
            else if (char.IsHighSurrogate(c))
                sixths += (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 4 : 3) * SixthsPerByte;
            else if (char.IsLowSurrogate(c))
                sixths += (i > 0 && char.IsHighSurrogate(text[i - 1]) ? 0 : 3) * SixthsPerByte;
            else
                sixths += 3 * SixthsPerByte;
        }
        return sixths;
    }

}
