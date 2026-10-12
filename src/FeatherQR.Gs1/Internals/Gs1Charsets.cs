namespace FeatherQR.Gs1.Internals;

/// <summary>
/// The character sets of the General Specifications 7.11: CSET 82 (Table 7-20), CSET 39 (Table 7-21),
/// CSET 64 (Table 7-22, base64url with <c>=</c> padding) and digits. CSET 82 is also the alphabet of the
/// check character pair (Table 7-18), in this order.
/// </summary>
internal static class Gs1Charsets
{
    /// <summary>CSET 82 in the order of Table 7-18, so a character's index is its reference value.</summary>
    public const string Cset82 = "!\"%&'()*+,-./0123456789:;<=>?ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz";

    /// <summary>The reference value of a CSET 82 character (Table 7-18), or -1 outside the set.</summary>
    public static int Cset82Value(char c)
    {
        if (c < '!' || c > 'z')
            return -1;
        return Cset82.IndexOf(c);
    }

    public static bool IsCset82(char c) => Cset82Value(c) >= 0;

    /// <summary>CSET 39: digits, upper-case letters, <c>#</c>, <c>-</c> and <c>/</c>.</summary>
    public static bool IsCset39(char c) => (uint)(c - '0') <= 9 || (uint)(c - 'A') <= 25 || c == '#' || c == '-' || c == '/';

    /// <summary>A CSET 64 data character: base64url without the padding.</summary>
    public static bool IsCset64(char c) => (uint)(c - '0') <= 9 || (uint)(c - 'A') <= 25 || (uint)(c - 'a') <= 25 || c == '-' || c == '_';

    public static bool IsDigit(char c) => (uint)(c - '0') <= 9;

    /// <summary>
    /// The index of the first character outside the component's set, or -1 when every character is in it.
    /// For CSET 64 the padding is part of the rule: at most two <c>=</c>, only at the end, and only where they
    /// complete a group of three, so <paramref name="length"/> is the run of padding at fault, else 1.
    /// </summary>
    public static int FirstInvalid(Gs1ComponentType type, ReadOnlySpan<char> value, out int length)
    {
        length = 1;
        switch (type)
        {
            case Gs1ComponentType.Numeric:
                for (var i = 0; i < value.Length; i++)
                {
                    if (!IsDigit(value[i]))
                        return i;
                }
                return -1;
            case Gs1ComponentType.Cset82:
                for (var i = 0; i < value.Length; i++)
                {
                    if (!IsCset82(value[i]))
                        return i;
                }
                return -1;
            case Gs1ComponentType.Cset39:
                for (var i = 0; i < value.Length; i++)
                {
                    if (!IsCset39(value[i]))
                        return i;
                }
                return -1;
            default:
                return FirstInvalidCset64(value, out length);
        }
    }

    private static int FirstInvalidCset64(ReadOnlySpan<char> value, out int length)
    {
        length = 1;
        var padding = 0;
        while (padding < value.Length && value[value.Length - 1 - padding] == '=')
            padding++;
        var data = value.Length - padding;
        for (var i = 0; i < data; i++)
        {
            if (!IsCset64(value[i]))
                return i;
        }
        // Padding completes a group of three characters, so one or two pads bring the length to a multiple of three.
        if (padding > 2 || (padding > 0 && value.Length % 3 != 0))
        {
            length = padding;
            return data;
        }
        return -1;
    }
}
