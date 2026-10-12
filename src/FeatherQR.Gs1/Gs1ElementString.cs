using FeatherQR.Gs1.Internals;

namespace FeatherQR.Gs1;

/// <summary>
/// Conversions between a GS1 element string (AI values with the group separator U+001D after each
/// variable-length value another follows), its (AI) text and a GS1 Digital Link URI, and the checks the GS1
/// General Specifications define on the data. Every member takes a <see cref="ReadOnlySpan{T}"/> of
/// <see cref="char"/>, so a <see cref="string"/> converts without an overload, and none throws for its input.
/// </summary>
/// <remarks>
/// <para>
/// The checks follow the GS1 Barcode Syntax Dictionary release named by the table in this package (the
/// AI formats, the linters, the requisite and exclusive pairings), which is generated from the dictionary
/// file and updated once a year. Reading members accept an AI newer than the table, since the tables that give
/// an AI's length from its first two digits and the predefined lengths are frozen by the General Specifications
/// (7.8.2, 7.8.3): its value takes the predefined length when its first two digits have one and runs to the
/// next separator otherwise.
/// </para>
/// <para>
/// A two-digit year resolves to the century that puts it within -49 to +50 years of the current year
/// (General Specifications 7.12), so whether 29 February of a two-digit year exists depends on when the
/// check runs. In 2026, 00 to 76 read as 2000 to 2076 and 77 to 99 as 1977 to 1999. The window moves up by
/// one each year, and from 2050 the year 00 reads as 2100.
/// </para>
/// </remarks>
public static partial class Gs1ElementString
{
    /// <summary>
    /// Runs every check on an element string: AI lengths and character sets, check digits and check character
    /// pairs, dates and the other linters, requisite and exclusive pairings, repeated AIs. <c>true</c> with
    /// <c>default</c> error when it passes.
    /// </summary>
    /// <remarks>
    /// A separator after a predefined-length element and a trailing separator are tolerated, as the General
    /// Specifications ask of a processing routine (7.8.6.3). A separator at the start or two in a row are refused.
    /// The pairing rules are checked within this element string alone, although the General Specifications
    /// define them over every carrier on the item, so data split across symbols can be refused here.
    /// </remarks>
    /// <param name="elementString">The element string, separators as U+001D and no leading FNC1.</param>
    /// <param name="error">Why it failed: the kind and the place in <paramref name="elementString"/>. <c>default</c> when it passed.</param>
    /// <returns><c>true</c> when every check passed.</returns>
    public static bool TryValidate(ReadOnlySpan<char> elementString, out Gs1Error error)
        => Gs1Validator.TryValidate(elementString, DateTime.UtcNow.Year, out error);

    /// <summary>
    /// Splits an element string into its elements without allocating: <c>foreach (var element in
    /// Gs1ElementString.EnumerateElements(text))</c>. Runs what splitting needs and no more: an AI of predefined
    /// length (Table 7-6, known to the table or not) takes its length and any other value runs to the next
    /// separator, an unknown AI whose first two digits give its length is read, a redundant or trailing separator
    /// is skipped, and the enumeration stops at the first element it cannot split. <see cref="TryValidate"/> says why.
    /// </summary>
    /// <param name="elementString">The element string, separators as U+001D and no leading FNC1.</param>
    public static Gs1ElementEnumerator EnumerateElements(ReadOnlySpan<char> elementString) => new(elementString);

    /// <summary>
    /// Formats an element string as its (AI) text, <c>(01)09520123456788(10)ABC</c>: each AI in parentheses and
    /// its value after it, a literal <c>(</c> in a value written <c>\(</c> and a <c>\</c> written <c>\\</c>. Runs what splitting needs and no more
    /// (an unknown AI whose first two digits give its length is read, a redundant or trailing separator is
    /// skipped), so a string <see cref="TryValidate"/> would refuse can still format.
    /// </summary>
    /// <remarks>The text is at most twice as long as the element string.</remarks>
    /// <param name="elementString">The element string, separators as U+001D and no leading FNC1.</param>
    /// <param name="hri">The (AI) text. Empty when it failed.</param>
    /// <param name="error">Why it failed: the kind and the place in <paramref name="elementString"/>. <c>default</c> when it passed.</param>
    /// <returns><c>true</c> when every element could be split.</returns>
    public static bool TryToHri(ReadOnlySpan<char> elementString, out string hri, out Gs1Error error)
        => Gs1Hri.TryToHri(elementString, out hri, out error);

    /// <summary>
    /// Formats an element string as its (AI) text into <paramref name="destination"/> without allocating.
    /// <see cref="TryGetToHriLength"/> gives the length to reserve. See <see cref="TryToHri(ReadOnlySpan{char}, out string, out Gs1Error)"/>.
    /// </summary>
    /// <param name="elementString">The element string, separators as U+001D and no leading FNC1.</param>
    /// <param name="destination">Where the text is written.</param>
    /// <param name="charsWritten">The length written. 0 when it failed.</param>
    /// <param name="error">Why it failed: <see cref="Gs1ErrorKind.DestinationTooSmall"/> when the text does not fit, else the kind and the place in <paramref name="elementString"/>. <c>default</c> when it passed.</param>
    /// <returns><c>true</c> when the text was written.</returns>
    public static bool TryToHri(ReadOnlySpan<char> elementString, Span<char> destination, out int charsWritten, out Gs1Error error)
        => Gs1Hri.TryToHri(elementString, destination, out charsWritten, out error);

    /// <summary>The length of the (AI) text <see cref="TryToHri(ReadOnlySpan{char}, out string, out Gs1Error)"/> would produce, after the same checks.</summary>
    /// <param name="elementString">The element string, separators as U+001D and no leading FNC1.</param>
    /// <param name="length">The length of the text. 0 when it failed.</param>
    /// <param name="error">Why it failed: the kind and the place in <paramref name="elementString"/>. <c>default</c> when it passed.</param>
    /// <returns><c>true</c> when every element could be split.</returns>
    public static bool TryGetToHriLength(ReadOnlySpan<char> elementString, out int length, out Gs1Error error)
        => Gs1Hri.TryGetToHriLength(elementString, out length, out error);

    /// <summary>
    /// Builds the element string of (AI) text, <c>(01)09520123456788(10)ABC</c>: each AI in parentheses and its
    /// value after it, up to the next unescaped <c>(</c>, with a literal <c>(</c> in a value written <c>\(</c> and a
    /// <c>\</c> written <c>\\</c> (a <c>\</c> before any other character is itself). Runs every
    /// check <see cref="TryValidate"/> runs, and puts the separator after every value that needs one.
    /// </summary>
    /// <remarks>
    /// The text has nothing between its pairs and no blank after a closing parenthesis, so a space in a value
    /// is refused as an invalid character. The element string is never longer than the text.
    /// </remarks>
    /// <param name="hri">The (AI) text.</param>
    /// <param name="elementString">The element string, separators as U+001D and no leading FNC1. Empty when it failed.</param>
    /// <param name="error">Why it failed: the kind and the place in <paramref name="hri"/>, escapes included. <c>default</c> when it passed.</param>
    /// <returns><c>true</c> when the text is pairs that pass every check.</returns>
    public static bool TryFromHri(ReadOnlySpan<char> hri, out string elementString, out Gs1Error error)
        => Gs1Hri.TryFromHri(hri, DateTime.UtcNow.Year, out elementString, out error);

    /// <summary>
    /// Builds the element string of (AI) text into <paramref name="destination"/> without allocating.
    /// <see cref="TryGetFromHriLength"/> gives the length to reserve. See <see cref="TryFromHri(ReadOnlySpan{char}, out string, out Gs1Error)"/>.
    /// </summary>
    /// <param name="hri">The (AI) text.</param>
    /// <param name="destination">Where the element string is written.</param>
    /// <param name="charsWritten">The length written. 0 when it failed.</param>
    /// <param name="error">Why it failed: <see cref="Gs1ErrorKind.DestinationTooSmall"/> when the element string does not fit, else the kind and the place in <paramref name="hri"/>. <c>default</c> when it passed.</param>
    /// <returns><c>true</c> when the element string was written.</returns>
    public static bool TryFromHri(ReadOnlySpan<char> hri, Span<char> destination, out int charsWritten, out Gs1Error error)
        => Gs1Hri.TryFromHri(hri, DateTime.UtcNow.Year, destination, out charsWritten, out error);

    /// <summary>The length of the element string <see cref="TryFromHri(ReadOnlySpan{char}, out string, out Gs1Error)"/> would build, after the same checks.</summary>
    /// <param name="hri">The (AI) text.</param>
    /// <param name="length">The length of the element string. 0 when it failed.</param>
    /// <param name="error">Why it failed: the kind and the place in <paramref name="hri"/>. <c>default</c> when it passed.</param>
    /// <returns><c>true</c> when the text is pairs that pass every check.</returns>
    public static bool TryGetFromHriLength(ReadOnlySpan<char> hri, out int length, out Gs1Error error)
        => Gs1Hri.TryGetFromHriLength(hri, DateTime.UtcNow.Year, out length, out error);
}
