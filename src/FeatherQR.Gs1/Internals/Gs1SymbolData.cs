using System.Diagnostics;

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
    /// the URI buffer a <see cref="DigitalLinkBound"/> up to it.
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
    /// A length no Digital Link URI of a text exceeds under a stem, the bound <see cref="Gs1ElementString"/>
    /// documents for <c>TryToDigitalLink</c>: the stem plus three times the element string plus two. The text's
    /// length stands in for the element string's, which is never longer.
    /// </summary>
    public static int DigitalLinkBound(int textLength, int stemLength) => stemLength + 3 * textLength + 2;

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
    /// <paramref name="uriBuffer"/>, which must hold <see cref="DigitalLinkBound"/> characters. The data is checked
    /// as <see cref="TryGetElementString"/> checks it, then against what a URI can carry.
    /// </summary>
    public static bool TryGetDigitalLink(ReadOnlySpan<char> text, int year, ReadOnlySpan<char> stem, Span<char> elementBuffer, Span<char> uriBuffer, out ReadOnlySpan<char> uri, out Gs1Error error)
    {
        uri = default;
        if (!TryGetElementString(text, year, elementBuffer, out var elementString, out error))
            return false;

        if (!Gs1DigitalLink.TryBuild(elementString, year, stem, uriBuffer, out var length, out error))
        {
            Debug.Assert(error.Kind != Gs1ErrorKind.DestinationTooSmall, "the bound holds every URI of the text");
            // The builder names a place in the element string. For (AI) text that is a string the caller never saw.
            if (IsAiText(text))
                error = error.Kind == Gs1ErrorKind.MissingPrimaryKey ? new Gs1Error(error.Kind, 0, text.Length) : Gs1Hri.MapElement(text, error);
            return false;
        }

        uri = uriBuffer.Slice(0, length);
        return true;
    }
}
