namespace FeatherQR.Gs1;

/// <summary>The kinds of <see cref="Gs1Error"/>: why a conversion or check refused its input. Later releases may add members at the end.</summary>
public enum Gs1ErrorKind
{
    /// <summary>No error. The <c>default</c> of <see cref="Gs1Error"/>.</summary>
    None = 0,

    /// <summary>The caller's destination span is shorter than the result. Nothing was written.</summary>
    DestinationTooSmall = 1,

    /// <summary>The text has no element where one must be: an empty input, or (AI) text that is malformed, with an unbalanced or empty parenthesis, a non-digit AI, or a stray character outside the elements.</summary>
    InvalidText = 2,

    /// <summary>The input is not an http or https URI with a path.</summary>
    NotAUri = 3,

    /// <summary>The URI is a compressed GS1 Digital Link URI, which this library does not read.</summary>
    CompressedUri = 4,

    /// <summary>A percent escape in the URI is not <c>%</c> and two hexadecimal digits, does not decode to UTF-8, or decodes to a control character.</summary>
    InvalidPercentEncoding = 5,

    /// <summary>A separator (U+001D) where no value can end: at the start of the element string, or right after another separator.</summary>
    UnexpectedSeparator = 6,

    /// <summary>An AI the table does not know, where building refuses it, or digits that no AI of the General Specifications' length table can start.</summary>
    UnknownAi = 7,

    /// <summary>An AI a GS1 Digital Link URI cannot carry: (03), (8014), (8200), or one the table does not flag as a data attribute.</summary>
    AiNotAllowedInUri = 8,

    /// <summary>No primary key AI for the path of a GS1 Digital Link URI.</summary>
    MissingPrimaryKey = 9,

    /// <summary>The key qualifiers after the primary key are not in one of the orders the table allows, or a qualifier of that key is in the query instead of the path.</summary>
    InvalidKeyQualifierSequence = 10,

    /// <summary>An element with no value.</summary>
    EmptyValue = 11,

    /// <summary>A value shorter than its AI's format requires.</summary>
    ValueTooShort = 12,

    /// <summary>A value longer than its AI's format allows.</summary>
    ValueTooLong = 13,

    /// <summary>A character outside the character set of its component (digits, CSET 82, CSET 39 or CSET 64 with its padding rule).</summary>
    InvalidCharacter = 14,

    /// <summary>A wrong check digit or check character pair.</summary>
    InvalidCheckDigit = 15,

    /// <summary>A date or time component that no calendar or clock holds: month 13, day 31 of a short month, 29 February of a common year, hour 24.</summary>
    InvalidDate = 16,

    /// <summary>A value that another rule of its AI refuses: an ISO 3166 or 4217 code not in the list, a yes/no flag, a zero or non-zero rule, a coordinate out of range, an IBAN, a coupon field, a position in a sequence.</summary>
    InvalidValue = 17,

    /// <summary>An AI the table says this AI requires on the same item is missing.</summary>
    MissingRequiredAi = 18,

    /// <summary>Two AIs the table says cannot appear together.</summary>
    ExcludedAiPair = 19,

    /// <summary>The same AI twice with different values in an element string, or twice at all in a Digital Link URI.</summary>
    RepeatedAi = 20,
}
