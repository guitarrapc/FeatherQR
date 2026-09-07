using System.Runtime.CompilerServices;

namespace FeatherQR.Internals;

/// <summary>
/// Character-class predicates and alphanumeric encoding values shared by all QR symbologies.
/// The Numeric and Alphanumeric alphabets are defined identically by ISO/IEC 18004 (Standard QR, Micro QR) and ISO/IEC 23941 (rMQR).
/// </summary>
internal static class CharacterSets
{
    /// <summary>
    /// Checks if a character is numeric (0-9).
    /// </summary>
    /// <param name="c">Character to check.</param>
    /// <returns>True if the character is a digit (0-9).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNumeric(char c) => c >= '0' && c <= '9';

    /// <summary>
    /// ASCII lookup table for alphanumeric character validation and encoding.
    /// Index: ASCII code (0-127) Value: Encoding value (0-44) or -1 if not alphanumeric Based on ISO/IEC 18004 Section 7.4.3.
    /// </summary>
    private static ReadOnlySpan<sbyte> alphanumericLookup => [
        // 0-31: Control characters (invalid)
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, // 0-9
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, // 10-19
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, // 20-29
        -1, -1,                                 // 30-31

        // 32-47: Space and special characters
        36,    // 32: ' '
        -1,    // 33: '!'
        -1,    // 34: '"'
        -1,    // 35: '#'
        37,    // 36: '$'
        38,    // 37: '%'
        -1,    // 38: '&'
        -1,    // 39: '''
        -1,    // 40: '('
        -1,    // 41: ')'
        39,    // 42: '*'
        40,    // 43: '+'
        -1,    // 44: ','
        41,    // 45: '-'
        42,    // 46: '.'
        43,    // 47: '/'

        // 48-57: Digits 0-9
        0,     // 48: '0'
        1,     // 49: '1'
        2,     // 50: '2'
        3,     // 51: '3'
        4,     // 52: '4'
        5,     // 53: '5'
        6,     // 54: '6'
        7,     // 55: '7'
        8,     // 56: '8'
        9,     // 57: '9'

        // 58-64: Colon and others
        44,    // 58: ':'
        -1,    // 59: ';'
        -1,    // 60: '<'
        -1,    // 61: '='
        -1,    // 62: '>'
        -1,    // 63: '?'
        -1,    // 64: '@'

        // 65-90: Letters A-Z
        10,    // 65: 'A'
        11,    // 66: 'B'
        12,    // 67: 'C'
        13,    // 68: 'D'
        14,    // 69: 'E'
        15,    // 70: 'F'
        16,    // 71: 'G'
        17,    // 72: 'H'
        18,    // 73: 'I'
        19,    // 74: 'J'
        20,    // 75: 'K'
        21,    // 76: 'L'
        22,    // 77: 'M'
        23,    // 78: 'N'
        24,    // 79: 'O'
        25,    // 80: 'P'
        26,    // 81: 'Q'
        27,    // 82: 'R'
        28,    // 83: 'S'
        29,    // 84: 'T'
        30,    // 85: 'U'
        31,    // 86: 'V'
        32,    // 87: 'W'
        33,    // 88: 'X'
        34,    // 89: 'Y'
        35,    // 90: 'Z'

        // 91-127: Invalid
        -1,    // 91: '['
        -1,    // 92: '\'
        -1,    // 93: ']'
        -1,    // 94: '^'
        -1,    // 95: '_'
        -1,    // 96: '`'
        -1,    // 97: 'a'
        -1,    // 98: 'b'
        -1,    // 99: 'c'
        -1,    // 100: 'd'
        -1,    // 101: 'e'
        -1,    // 102: 'f'
        -1,    // 103: 'g'
        -1,    // 104: 'h'
        -1,    // 105: 'i'
        -1,    // 106: 'j'
        -1,    // 107: 'k'
        -1,    // 108: 'l'
        -1,    // 109: 'm'
        -1,    // 110: 'n'
        -1,    // 111: 'o'
        -1,    // 112: 'p'
        -1,    // 113: 'q'
        -1,    // 114: 'r'
        -1,    // 115: 's'
        -1,    // 116: 't'
        -1,    // 117: 'u'
        -1,    // 118: 'v'
        -1,    // 119: 'w'
        -1,    // 120: 'x'
        -1,    // 121: 'y'
        -1,    // 122: 'z'
        -1,    // 123: '{'
        -1,    // 124: '|'
        -1,    // 125: '}'
        -1,    // 126: '~'
        -1     // 127: DEL
    ];

    /// <summary>
    /// Checks if a character is valid in alphanumeric mode.
    /// Valid characters: 0-9, A-Z, space, $%*+-./:
    /// </summary>
    /// <param name="c">Character to check.</param>
    /// <returns>True if valid alphanumeric character.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAlphanumeric(char c)
    {
        return c < alphanumericLookup.Length && alphanumericLookup[c] >= 0;
    }

    /// <summary>
    /// Validates if text can be encoded in ISO-8859-1.
    /// ISO-8859-1 supports U+0000 to U+00FF only.
    /// </summary>
    /// <param name="text">The string to check for ISO-8859-1-only characters.</param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidISO88591(string text) => IsValidISO88591(text.AsSpan());

    /// <summary>
    /// Validates if text can be encoded in ISO-8859-1.
    /// ISO-8859-1 supports U+0000 to U+00FF only.
    /// </summary>
    /// <param name="textSpan">The char span to check for ISO-8859-1-only characters.</param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidISO88591(ReadOnlySpan<char> textSpan)
    {
        if (textSpan.Length == 0) return true;

        // TODO: Can be simd

        foreach (char c in textSpan)
        {
            if (c > 0xFF) return false;
        }
        return true;
    }

    /// <summary>
    /// Gets the encoding value for an alphanumeric character.
    /// </summary>
    /// <param name="c">Alphanumeric character.</param>
    /// <returns>Encoding value (0-44).</returns>
    /// <exception cref="ArgumentException">If character is not alphanumeric.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetAlphanumericValue(char c)
    {
        if (c >= alphanumericLookup.Length)
            throw new ArgumentException($"Character '{c}' is not alphanumeric.", nameof(c));

        var value = alphanumericLookup[c];
        if (value < 0)
            throw new ArgumentException($"Character '{c}' is not alphanumeric.", nameof(c));

        return value;
    }

    /// <summary>
    /// Tries to get the encoding value for an alphanumeric character.
    /// </summary>
    /// <param name="c">Character to encode.</param>
    /// <param name="value">Encoding value (0-44) if successful.</param>
    /// <returns>True if character is alphanumeric.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetAlphanumericValue(char c, out int value)
    {
        if (c < alphanumericLookup.Length)
        {
            var lookup = alphanumericLookup[c];
            if (lookup >= 0)
            {
                value = lookup;
                return true;
            }
        }
        value = -1;
        return false;
    }
}
