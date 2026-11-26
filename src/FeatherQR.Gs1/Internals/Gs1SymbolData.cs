using System.Diagnostics;
using System.Globalization;

namespace FeatherQR.Gs1.Internals;

/// <summary>
/// What the generator entry points share: the caller's text, (AI) text or an element string, turned into the checked
/// element string a GS1 symbol carries or into the Digital Link URI of its data, with every error placed in the
/// text the caller passed. The symbol itself is the core generators' work.
/// </summary>
internal static class Gs1SymbolData
{
    /// <summary>
    /// The length of each stack buffer, in characters. The element buffer serves (AI) text up to this length, and
    /// the URI buffer a <see cref="UriBufferLength"/> up to it.
    /// </summary>
    public const int StackLimit = 512;

    /// <summary>The most characters a Standard QR symbol holds: digits, in version 40 at level L.</summary>
    public const int StandardQrCapacity = 7089;

    /// <summary>The most characters an rMQR symbol holds: digits, in R17x139 at level M.</summary>
    public const int RmQrCapacity = 361;

    /// <summary>
    /// Whether the text is (AI) text. It starts with an opening parenthesis and an element string starts with an
    /// AI's digit, so the first character tells the two apart.
    /// </summary>
    public static bool IsAiText(ReadOnlySpan<char> text) => text.Length > 0 && text[0] == '(';

    /// <summary>
    /// Whether the stack buffer serves as the element buffer. An element string is checked where it is and never
    /// copied, so only (AI) text longer than the buffer needs a pooled one.
    /// </summary>
    public static bool StackBufferServes(ReadOnlySpan<char> text) => text.Length <= StackLimit || !IsAiText(text);

    /// <summary>
    /// Whether the text is longer than a symbol of the given capacity can hold, whatever it holds. An element
    /// string goes into the symbol as it is. (AI) text sheds at most half its length on the way: two parentheses
    /// for an AI of two digits or more, and one backslash for each escaped character. A size form answers from
    /// this before the checks, so text of any length that no symbol can hold costs neither the checks nor, for
    /// (AI) text, a pooled buffer as long as the text.
    /// </summary>
    public static bool IsPastCapacity(ReadOnlySpan<char> text, int capacity) => text.Length > (IsAiText(text) ? 2L * capacity : capacity);

    /// <summary>
    /// The length of the URI buffer for a text under a stem. It is the bound <see cref="Gs1ElementString"/> documents
    /// for <c>TryToDigitalLink</c>, the stem plus three times the element string plus two, with the text's length
    /// standing in for the element string's, which is never longer. The bound caps at one character past the
    /// symbol's capacity, since no symbol holds a URI that fills that buffer.
    /// </summary>
    /// <remarks>
    /// The text's length does not bound the URI's from below: an AI repeated with the same value is one AI in the URI.
    /// So the cap, not the text, keeps the buffer small, and the bound is computed in <see cref="long"/>, which three
    /// times any text's length stays inside.
    /// </remarks>
    public static int UriBufferLength(int textLength, int stemLength, int capacity) => (int)Math.Min(stemLength + 3L * textLength + 2, capacity + 1L);

    /// <summary>Whether <see cref="TryGetDigitalLink"/> failed because the URI, valid in itself, is longer than any symbol holds.</summary>
    public static bool IsPastEverySymbol(in Gs1Error error) => error.Kind == Gs1ErrorKind.DestinationTooSmall;

    /// <summary>
    /// The error for a URI longer than the most a symbol holds. It fits no version whatever the options allow, so like
    /// the core's error for content too long for every version it names no parameter.
    /// </summary>
    public static ArgumentException UriPastEverySymbol(string symbol, int capacity)
        => new($"The Digital Link URI is longer than the {capacity.ToString("N0", CultureInfo.InvariantCulture)} characters {symbol} holds at most. Shorten the data or the stem.");

    /// <summary>
    /// The element string of the text after every check. (AI) text is built into <paramref name="buffer"/>,
    /// which must be at least as long as the text. An element string is checked and returned as it is, redundant
    /// separators included, since a caller who wrote it chose them and a reader tolerates them.
    /// </summary>
    public static bool TryGetElementString(ReadOnlySpan<char> text, int year, Span<char> buffer, out ReadOnlySpan<char> elementString, out Gs1Error error)
    {
        if (IsAiText(text))
        {
            Debug.Assert(buffer.Length >= text.Length, "the element string is built in the buffer, and is never longer than its text");
            var built = Gs1Hri.TryFromHri(text, year, buffer, out var length, out error);
            elementString = buffer.Slice(0, length);
            return built;
        }

        var valid = Gs1Validator.TryValidate(text, year, out error);
        elementString = valid ? text : default;
        return valid;
    }

    /// <summary>
    /// The Digital Link URI of the text's data under a stem already validated, written into
    /// <paramref name="uriBuffer"/>, which holds <see cref="UriBufferLength"/> characters. The data is checked
    /// as <see cref="TryGetElementString"/> checks it, then against what a URI can carry. A URI that is valid but longer
    /// than the buffer fails with <see cref="Gs1ErrorKind.DestinationTooSmall"/>, which <see cref="IsPastEverySymbol"/>
    /// tells apart: no symbol holds it.
    /// </summary>
    public static bool TryGetDigitalLink(ReadOnlySpan<char> text, int year, ReadOnlySpan<char> stem, Span<char> elementBuffer, Span<char> uriBuffer, out ReadOnlySpan<char> uri, out Gs1Error error)
    {
        uri = default;
        if (!TryGetElementString(text, year, elementBuffer, out var elementString, out error))
            return false;

        if (!Gs1DigitalLink.TryBuild(elementString, year, stem, uriBuffer, out var length, out error))
        {
            // The builder checks the data before it writes, so a short buffer is the last thing it reports.
            if (IsPastEverySymbol(error))
                return false;
            // The builder names a place in the element string. For (AI) text that is a string the caller never saw.
            if (IsAiText(text))
                error = error.Kind == Gs1ErrorKind.MissingPrimaryKey ? new Gs1Error(error.Kind, 0, text.Length) : Gs1Hri.MapElement(text, error);
            return false;
        }

        uri = uriBuffer.Slice(0, length);
        return true;
    }
}
