using System.Diagnostics;

namespace FeatherQR.Gs1.Internals;

/// <summary>
/// What the generator entry points share: the caller's text, (AI) text or an element string, turned into the checked
/// element string a GS1 symbol carries or into the Digital Link URI of its data, with every error placed in the
/// text the caller passed. The symbol itself is the core generators' work.
/// </summary>
internal static class Gs1SymbolData
{
    /// <summary>The longest text a caller's stack buffer is taken for, in characters.</summary>
    public const int StackLimit = 512;

    /// <summary>
    /// Whether the text is (AI) text. It starts with an opening parenthesis and an element string starts with an
    /// AI's digit, so the first character tells the two apart.
    /// </summary>
    public static bool IsAiText(ReadOnlySpan<char> text) => text.Length > 0 && text[0] == '(';

    /// <summary>
    /// A length no Digital Link URI of a text exceeds: the stem, each character of the element string as a
    /// percent escape of three, a question mark or a slash for the rest. The element string is never longer
    /// than its (AI) text.
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
