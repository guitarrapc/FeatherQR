namespace FeatherQR.Gs1;

/// <summary>
/// Thrown by the generator entry points (<c>CreateGs1</c>, <c>CreateGs1DigitalLink</c>) when the data is not valid
/// GS1 data, or is data a Digital Link URI cannot carry. Nothing on <see cref="Gs1ElementString"/> throws for its input.
/// </summary>
/// <remarks>
/// A <see cref="FormatException"/>, as <see cref="UriFormatException"/> is, so an existing
/// <c>catch (FormatException)</c> still catches it. <see cref="Error"/> gives the kind and the place in the text
/// the caller passed, and <see cref="Exception.Message"/> is that error's <see cref="Gs1Error.ToString"/>.
/// Data that is valid and too long for the symbol is not this exception: <c>Create</c>'s own is thrown for it.
/// The type is not serializable, so on .NET Framework it does not cross an application domain boundary.
/// </remarks>
public sealed class Gs1FormatException : FormatException
{
    /// <summary>Creates the exception for an error of the data.</summary>
    /// <param name="error">What was wrong and where. Its <see cref="Gs1Error.ToString"/> becomes the message.</param>
    public Gs1FormatException(Gs1Error error)
        : base(error.ToString())
    {
        Error = error;
    }

    /// <summary>What was wrong, and where in the text the caller passed.</summary>
    public Gs1Error Error { get; }
}
