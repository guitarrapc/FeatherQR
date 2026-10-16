using System.Text;

namespace FeatherQR;

/// <summary>
/// The FNC1 mode a Standard QR or rMQR symbol carries (ISO/IEC 18004:2006 6.4.8, ISO/IEC 23941 7.4.8): written through <see cref="QRCodeGeneratorOptions.Fnc1"/> and <see cref="RmQRCodeGeneratorOptions.Fnc1"/>, read on <see cref="QRCodeDecodeInfo.Fnc1"/> and <see cref="RmQRCodeDecodeInfo.Fnc1"/>.
/// </summary>
/// <remarks>
/// <para>
/// FNC1 in first position (<see cref="FirstPosition"/>) marks GS1 data: an element string of application identifiers and their values, with the group separator (GS, U+001D) after each variable-length value that another follows.
/// FNC1 in second position (<see cref="SecondPosition(int)"/>, <see cref="SecondPosition(char)"/>) marks data in an industry format registered with AIM, named by an 8-bit application indicator (<see cref="ApplicationIndicator"/>): 00 to 99 as its value, or one letter A-Z or a-z as its ASCII value plus 100.
/// <see cref="None"/>, the default, writes no indicator.
/// </para>
/// <para>
/// The mode changes how the text reads. In an Alphanumeric segment a lone <c>%</c> is the separator and comes back as U+001D, and <c>%%</c> is one literal <c>%</c>, read left to right within the segment. A Byte segment carries the separator as the byte 0x1D.
/// The encoder takes the text with U+001D as the separator and writes it under that rule, so the text given to the generator is the text the decoder returns. The decoded text is the data alone, the element string in first position. It holds neither a symbology identifier nor the application indicator.
/// </para>
/// <para>
/// A reader transmits such a symbol as the AIM symbology identifier, then in second position the application indicator as two digits or one letter, then the data.
/// The identifier is <c>]Q3</c> for first position and <c>]Q5</c> for second, one higher (<c>]Q4</c>, <c>]Q6</c>) when the reader transmits under the ECI protocol (ISO/IEC 18004:2006 Annex F), and <c>]Q1</c> or <c>]Q2</c> without FNC1.
/// The indicator <c>n</c> is written <c>n &lt; 100 ? n.ToString("D2") : ((char)(n - 100)).ToString()</c>.
/// </para>
/// <para>
/// A symbol without FNC1 reports <c>default</c>, on which <see cref="IsFirstPosition"/> and <see cref="IsSecondPosition"/> are both <c>false</c> and <see cref="ApplicationIndicator"/> is <c>null</c>, as it is in first position.
/// A decoded mode is the value the generator takes, so a symbol can be written again from its decode info. Micro QR has no FNC1 mode.
/// </para>
/// </remarks>
public readonly record struct Fnc1Mode
{
    private const byte NonePosition = 0;
    private const byte First = 1;
    private const byte Second = 2;

    private readonly byte _position;
    private readonly byte _applicationIndicator;

    private Fnc1Mode(byte position, byte applicationIndicator)
    {
        _position = position;
        _applicationIndicator = applicationIndicator;
    }

    /// <summary>No FNC1, the default: the symbol's text is read as it is.</summary>
    public static Fnc1Mode None => default;

    /// <summary>FNC1 in first position: GS1 data, an element string with U+001D as the separator.</summary>
    public static Fnc1Mode FirstPosition => new(First, 0);

    /// <summary>FNC1 in second position with a two-digit application indicator, 00 to 99. A letter indicator is given to <see cref="SecondPosition(char)"/>.</summary>
    /// <param name="number">The indicator, 0 to 99.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="number"/> is outside 0 to 99.</exception>
    public static Fnc1Mode SecondPosition(int number)
    {
        if ((uint)number > 99)
            throw new ArgumentOutOfRangeException(nameof(number), $"The application indicator must be 00 to 99, but was {number}. A letter is given as a char.");
        return new(Second, (byte)number);
    }

    /// <summary>FNC1 in second position with a one-letter application indicator, A to Z or a to z, carried as its ASCII value plus 100.</summary>
    /// <param name="letter">The indicator, A to Z or a to z.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="letter"/> is not an ASCII letter.</exception>
    public static Fnc1Mode SecondPosition(char letter)
    {
        if (!((uint)(letter - 'A') <= 25 || (uint)(letter - 'a') <= 25))
            throw new ArgumentOutOfRangeException(nameof(letter), $"The application indicator must be a letter A-Z or a-z, but was '{letter}'. A number is given as an int.");
        return new(Second, (byte)(letter + 100));
    }

    /// <summary>Second position from the indicator as on the wire, a value <see cref="IsValidApplicationIndicator"/> accepts: the decoders' entry.</summary>
    internal static Fnc1Mode FromCodeword(int applicationIndicator) => new(Second, (byte)applicationIndicator);

    /// <summary>The ranges of ISO/IEC 18004:2006 6.4.8.2: 00-99 as a number, 165-190 for A-Z and 197-222 for a-z.</summary>
    internal static bool IsValidApplicationIndicator(int codeword) => codeword is (>= 0 and <= 99) or (>= 165 and <= 190) or (>= 197 and <= 222);

    /// <summary>Whether either indicator is set, so the <c>%</c> rule applies to the Alphanumeric segments.</summary>
    internal bool IsActive => _position != NonePosition;

    /// <summary>The application indicator as on the wire, 0 outside second position.</summary>
    internal int Codeword => _applicationIndicator;

    /// <summary>The bits the mode adds to a Standard QR stream: the 4-bit indicator, and in second position the 8-bit application indicator.</summary>
    internal int StandardQrHeaderBits => _position == First ? 4 : _position == Second ? 12 : 0;

    /// <summary>The bits the mode adds to an rMQR stream: the 3-bit indicator, and in second position the 8-bit application indicator.</summary>
    internal int RmQrHeaderBits => _position == First ? 3 : _position == Second ? 11 : 0;

    /// <summary>The mode as ten bits, for an option struct that keeps it beside another value in one field.</summary>
    internal int RouteBits => _position | (_applicationIndicator << 2);

    internal static Fnc1Mode FromRouteBits(int bits) => new((byte)(bits & 3), (byte)(bits >> 2));

    /// <summary><c>true</c> when the symbol carries FNC1 in first position: its text is a GS1 element string.</summary>
    public bool IsFirstPosition => _position == First;

    /// <summary><c>true</c> when the symbol carries FNC1 in second position: its text follows the industry format <see cref="ApplicationIndicator"/> names.</summary>
    public bool IsSecondPosition => _position == Second;

    /// <summary>
    /// The application indicator as on the wire in second position (0-99 as its value, or a letter's ASCII value plus 100), and <c>null</c> in first position and without FNC1.
    /// <c>00</c> is the value 0, so a missing indicator is <c>null</c> rather than a number.
    /// </summary>
    public int? ApplicationIndicator => _position == Second ? _applicationIndicator : null;

    // The letter goes beside a letter's codeword, since 165 alone does not say A.
#pragma warning disable IDE0051 // the generated ToString calls it, but the analyzer does not see that
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("IsFirstPosition = ").Append(IsFirstPosition)
            .Append(", IsSecondPosition = ").Append(IsSecondPosition)
            .Append(", ApplicationIndicator = ");
        if (_position == Second)
        {
            builder.Append(_applicationIndicator);
            if (_applicationIndicator >= 100)
                builder.Append(" (").Append((char)(_applicationIndicator - 100)).Append(')');
        }
        return true;
    }
#pragma warning restore IDE0051
}
