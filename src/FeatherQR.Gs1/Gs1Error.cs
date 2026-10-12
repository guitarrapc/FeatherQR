namespace FeatherQR.Gs1;

/// <summary>
/// Why a conversion or check refused its input: the <see cref="Kind"/>, and where in the input
/// (<see cref="Offset"/> and <see cref="Length"/>, both 0 when the kind names no place). The <c>default</c>
/// is <see cref="Gs1ErrorKind.None"/>. <see cref="ToString"/> is the message the generator entry points put
/// in their <c>Gs1FormatException</c>.
/// </summary>
public readonly record struct Gs1Error
{
    /// <summary>Creates an error. <paramref name="offset"/> and <paramref name="length"/> are positions in the input the error is about, 0 where the kind names no place.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> or <paramref name="length"/> is negative.</exception>
    public Gs1Error(Gs1ErrorKind kind, int offset, int length)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset must not be negative.");
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "The length must not be negative.");
        Kind = kind;
        Offset = offset;
        Length = length;
    }

    /// <summary>What was wrong.</summary>
    public Gs1ErrorKind Kind { get; }

    /// <summary>Where it starts in the input, in characters. 0 when the kind names no place.</summary>
    public int Offset { get; }

    /// <summary>How many characters of the input it covers. 0 when the kind names no place or a missing thing.</summary>
    public int Length { get; }

    /// <summary>The kind, the place and a sentence, such as <c>InvalidCheckDigit at 15 (1 character): the check digit is wrong.</c></summary>
    public override string ToString()
    {
        if (Kind == Gs1ErrorKind.None)
            return "No error.";
        var description = Describe(Kind);
        if (Kind == Gs1ErrorKind.DestinationTooSmall)
            return $"{Kind}: {description}.";
        return $"{Kind} at {Offset} ({Length} {(Length == 1 ? "character" : "characters")}): {description}.";
    }

    private static string Describe(Gs1ErrorKind kind) => kind switch
    {
        Gs1ErrorKind.DestinationTooSmall => "the destination is too small",
        Gs1ErrorKind.InvalidText => "the text has no element where one must be",
        Gs1ErrorKind.NotAUri => "the text is not an http or https URI with a path",
        Gs1ErrorKind.CompressedUri => "the URI is compressed, which is not read",
        Gs1ErrorKind.InvalidPercentEncoding => "a percent escape is malformed",
        Gs1ErrorKind.UnexpectedSeparator => "a separator where no value can end",
        Gs1ErrorKind.UnknownAi => "the AI is not known",
        Gs1ErrorKind.AiNotAllowedInUri => "the AI cannot be carried in a Digital Link URI",
        Gs1ErrorKind.MissingPrimaryKey => "no primary key AI for the URI path",
        Gs1ErrorKind.InvalidKeyQualifierSequence => "the key qualifiers are not in an allowed order",
        Gs1ErrorKind.EmptyValue => "the value is empty",
        Gs1ErrorKind.ValueTooShort => "the value is shorter than its AI requires",
        Gs1ErrorKind.ValueTooLong => "the value is longer than its AI allows",
        Gs1ErrorKind.InvalidCharacter => "a character outside the component's character set",
        Gs1ErrorKind.InvalidCheckDigit => "the check digit is wrong",
        Gs1ErrorKind.InvalidDate => "the date or time does not exist",
        Gs1ErrorKind.InvalidValue => "the value fails a rule of its AI",
        Gs1ErrorKind.MissingRequiredAi => "a required AI is missing",
        Gs1ErrorKind.ExcludedAiPair => "two AIs that cannot appear together",
        Gs1ErrorKind.RepeatedAi => "the same AI twice, with different values in an element string or at all in a Digital Link URI",
        _ => "an error",
    };
}
