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
/// file and updated once a year. Reading members accept an AI newer than the table as a variable-length
/// value, since the tables that give an AI's length from its first two digits and the predefined lengths are
/// frozen by the General Specifications (7.8.2, 7.8.3).
/// </para>
/// <para>
/// A two-digit year resolves to the century that puts it within -49 to +50 years of the current year
/// (General Specifications 7.12), so whether 29 February of a two-digit year exists depends on when the
/// check runs. Years 00 to 76 read as 2000 to 2076 until 2027, and 00 reads as 2000 until 2050.
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
    /// Gs1ElementString.EnumerateElements(text))</c>. Runs what splitting needs and no more: a known AI of fixed
    /// length takes its length and any other value runs to the next separator, an unknown AI whose first two
    /// digits give its length is read, a redundant or trailing separator is skipped, and the enumeration stops
    /// at the first element it cannot split. <see cref="TryValidate"/> says why.
    /// </summary>
    /// <param name="elementString">The element string, separators as U+001D and no leading FNC1.</param>
    public static Gs1ElementEnumerator EnumerateElements(ReadOnlySpan<char> elementString) => new(elementString);
}
