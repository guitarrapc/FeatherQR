using FeatherQR.Gs1.Internals;

namespace FeatherQR.Gs1;

public static partial class Gs1ElementString
{
    /// <summary>
    /// Builds a GS1 Digital Link URI (URI Syntax 1.7.0, uncompressed) from an element string: the primary key and
    /// its key qualifiers in the path, every other AI as a query parameter in the lexical order of the AIs, each AI
    /// once, reserved characters percent-encoded. With no stem the canonical <c>https://id.gs1.org</c>, so the
    /// result is the canonical form of section 4.12. Runs every check <see cref="TryValidate"/> runs, plus what a
    /// URI can carry: a primary key must be present and every AI outside the path must be a data attribute.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first element whose AI is a primary key leads the path. Where the key has alternative qualifier
    /// sequences, the one that matches the most elements is used, preferring a sequence that leaves only data
    /// attributes for the query. The URI is never longer than the stem plus three times the element string plus two.
    /// </para>
    /// <para>
    /// A stem that is not an <c>http</c> or <c>https</c> URI prefix with a host and no query or fragment throws
    /// <see cref="ArgumentException"/>, as a malformed option does elsewhere. One trailing slash on it is dropped.
    /// </para>
    /// </remarks>
    /// <param name="elementString">The element string, separators as U+001D and no leading FNC1.</param>
    /// <param name="uri">The URI. Empty when it failed.</param>
    /// <param name="error">Why it failed: the kind and the place in <paramref name="elementString"/>. <c>default</c> when it passed.</param>
    /// <param name="stem">The scheme, host and path prefix before the GS1 part, or <c>null</c> for <c>https://id.gs1.org</c>.</param>
    /// <returns><c>true</c> when the data passed every check and a URI can carry it.</returns>
    /// <exception cref="ArgumentException"><paramref name="stem"/> is not an http or https URI prefix.</exception>
    public static bool TryToDigitalLink(ReadOnlySpan<char> elementString, out string uri, out Gs1Error error, string? stem = null)
        => Gs1DigitalLink.TryToDigitalLink(elementString, DateTime.UtcNow.Year, stem, out uri, out error);

    /// <summary>
    /// Builds a GS1 Digital Link URI into <paramref name="destination"/> without allocating.
    /// <see cref="TryGetToDigitalLinkLength"/> gives the length to reserve. See <see cref="TryToDigitalLink(ReadOnlySpan{char}, out string, out Gs1Error, string?)"/>.
    /// </summary>
    /// <param name="elementString">The element string, separators as U+001D and no leading FNC1.</param>
    /// <param name="destination">Where the URI is written.</param>
    /// <param name="charsWritten">The length written. 0 when it failed.</param>
    /// <param name="error">Why it failed: <see cref="Gs1ErrorKind.DestinationTooSmall"/> when the URI does not fit, else the kind and the place in <paramref name="elementString"/>. <c>default</c> when it passed.</param>
    /// <param name="stem">The scheme, host and path prefix before the GS1 part, or <c>null</c> for <c>https://id.gs1.org</c>.</param>
    /// <returns><c>true</c> when the URI was written.</returns>
    /// <exception cref="ArgumentException"><paramref name="stem"/> is not an http or https URI prefix.</exception>
    public static bool TryToDigitalLink(ReadOnlySpan<char> elementString, Span<char> destination, out int charsWritten, out Gs1Error error, string? stem = null)
        => Gs1DigitalLink.TryToDigitalLink(elementString, DateTime.UtcNow.Year, stem, destination, out charsWritten, out error);

    /// <summary>The length of the URI <see cref="TryToDigitalLink(ReadOnlySpan{char}, out string, out Gs1Error, string?)"/> would build for this element string and stem, after the same checks.</summary>
    /// <param name="elementString">The element string, separators as U+001D and no leading FNC1.</param>
    /// <param name="length">The length of the URI. 0 when it failed.</param>
    /// <param name="error">Why it failed: the kind and the place in <paramref name="elementString"/>. <c>default</c> when it passed.</param>
    /// <param name="stem">The scheme, host and path prefix before the GS1 part, or <c>null</c> for <c>https://id.gs1.org</c>.</param>
    /// <returns><c>true</c> when the data passed every check and a URI can carry it.</returns>
    /// <exception cref="ArgumentException"><paramref name="stem"/> is not an http or https URI prefix.</exception>
    public static bool TryGetToDigitalLinkLength(ReadOnlySpan<char> elementString, out int length, out Gs1Error error, string? stem = null)
        => Gs1DigitalLink.TryGetToDigitalLinkLength(elementString, DateTime.UtcNow.Year, stem, out length, out error);

    /// <summary>
    /// Reads the GS1 data out of a Digital Link URI with any stem: the primary key and its qualifiers from the end
    /// of the path, then the numeric query keys in URI order, percent-decoded. Runs what reading needs, the URI's
    /// structure: the scheme and host, the key qualifier order, that a numeric query key is an AI the table knows
    /// and a data attribute not already in the path, and that a value decodes. The values' own checks are
    /// <see cref="TryValidate"/>'s.
    /// </summary>
    /// <remarks>
    /// A query key that is not numeric (<c>linkType</c>, an extension) is ignored, an AI that appears twice in the
    /// URI is refused even with equal values, as the GS1 Syntax Engine refuses it, and a fragment is ignored. A
    /// compressed URI is refused with its own kind. The element string is never longer than the URI.
    /// </remarks>
    /// <param name="uri">The URI.</param>
    /// <param name="elementString">The element string, separators as U+001D and no leading FNC1. Empty when it failed.</param>
    /// <param name="error">Why it failed: the kind and the place in <paramref name="uri"/>. <c>default</c> when it passed.</param>
    /// <returns><c>true</c> when the URI is a Digital Link URI whose structure reads.</returns>
    public static bool TryFromDigitalLink(ReadOnlySpan<char> uri, out string elementString, out Gs1Error error)
        => Gs1DigitalLink.TryFromDigitalLink(uri, out elementString, out error);

    /// <summary>
    /// Reads the GS1 data out of a Digital Link URI into <paramref name="destination"/> without allocating.
    /// <see cref="TryGetFromDigitalLinkLength"/> gives the length to reserve. See <see cref="TryFromDigitalLink(ReadOnlySpan{char}, out string, out Gs1Error)"/>.
    /// </summary>
    /// <param name="uri">The URI.</param>
    /// <param name="destination">Where the element string is written.</param>
    /// <param name="charsWritten">The length written. 0 when it failed.</param>
    /// <param name="error">Why it failed: <see cref="Gs1ErrorKind.DestinationTooSmall"/> when the element string does not fit, else the kind and the place in <paramref name="uri"/>. <c>default</c> when it passed.</param>
    /// <returns><c>true</c> when the element string was written.</returns>
    public static bool TryFromDigitalLink(ReadOnlySpan<char> uri, Span<char> destination, out int charsWritten, out Gs1Error error)
        => Gs1DigitalLink.TryFromDigitalLink(uri, destination, out charsWritten, out error);

    /// <summary>The length of the element string <see cref="TryFromDigitalLink(ReadOnlySpan{char}, out string, out Gs1Error)"/> would read from this URI, after the same reading.</summary>
    /// <param name="uri">The URI.</param>
    /// <param name="length">The length of the element string. 0 when it failed.</param>
    /// <param name="error">Why it failed: the kind and the place in <paramref name="uri"/>. <c>default</c> when it passed.</param>
    /// <returns><c>true</c> when the URI is a Digital Link URI whose structure reads.</returns>
    public static bool TryGetFromDigitalLinkLength(ReadOnlySpan<char> uri, out int length, out Gs1Error error)
        => Gs1DigitalLink.TryGetFromDigitalLinkLength(uri, out length, out error);

    /// <summary>
    /// Reads what a decoder returned as GS1 data: with FNC1 in first position the text is an element string and
    /// comes back as (AI) text, as <see cref="TryToHri(ReadOnlySpan{char}, out string, out Gs1Error)"/> gives it,
    /// otherwise the text is read as a Digital Link URI, as <see cref="TryFromDigitalLink(ReadOnlySpan{char}, out string, out Gs1Error)"/>
    /// reads it, and its element string comes back as (AI) text. <c>false</c> when it is neither. The decode-side
    /// mirror of <c>CreateGs1</c>, independent of which decode entry point read the symbol.
    /// </summary>
    /// <param name="text">The decoded text.</param>
    /// <param name="fnc1">The symbol's FNC1 mode, from the decode info.</param>
    /// <param name="hri">The (AI) text. Empty when it failed.</param>
    /// <param name="error">Why it failed: the kind and the place in <paramref name="text"/>. <c>default</c> when it passed.</param>
    /// <returns><c>true</c> when the text read as GS1 data under the given carrier.</returns>
    public static bool TryRead(ReadOnlySpan<char> text, Fnc1Mode fnc1, out string hri, out Gs1Error error)
        => Gs1Reader.TryRead(text, fnc1, out hri, out error);

    /// <summary>
    /// Reads what a decoder returned as GS1 data into <paramref name="destination"/> without allocating.
    /// <see cref="TryGetReadLength"/> gives the length to reserve, and the text is never longer than twice the input.
    /// See <see cref="TryRead(ReadOnlySpan{char}, Fnc1Mode, out string, out Gs1Error)"/>.
    /// </summary>
    /// <param name="text">The decoded text.</param>
    /// <param name="fnc1">The symbol's FNC1 mode, from the decode info.</param>
    /// <param name="destination">Where the (AI) text is written.</param>
    /// <param name="charsWritten">The length written. 0 when it failed.</param>
    /// <param name="error">Why it failed: <see cref="Gs1ErrorKind.DestinationTooSmall"/> when the text does not fit, else the kind and the place in <paramref name="text"/>. <c>default</c> when it passed.</param>
    /// <returns><c>true</c> when the (AI) text was written.</returns>
    public static bool TryRead(ReadOnlySpan<char> text, Fnc1Mode fnc1, Span<char> destination, out int charsWritten, out Gs1Error error)
        => Gs1Reader.TryRead(text, fnc1, destination, out charsWritten, out error);

    /// <summary>The length of the (AI) text <see cref="TryRead(ReadOnlySpan{char}, Fnc1Mode, out string, out Gs1Error)"/> would give, after the same reading.</summary>
    /// <param name="text">The decoded text.</param>
    /// <param name="fnc1">The symbol's FNC1 mode, from the decode info.</param>
    /// <param name="length">The length of the (AI) text. 0 when it failed.</param>
    /// <param name="error">Why it failed: the kind and the place in <paramref name="text"/>. <c>default</c> when it passed.</param>
    /// <returns><c>true</c> when the text read as GS1 data under the given carrier.</returns>
    public static bool TryGetReadLength(ReadOnlySpan<char> text, Fnc1Mode fnc1, out int length, out Gs1Error error)
        => Gs1Reader.TryGetReadLength(text, fnc1, out length, out error);
}
