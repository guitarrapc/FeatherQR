namespace FeatherQR;

/// <summary>
/// The FNC1 mode a Standard QR or rMQR symbol carries (ISO/IEC 18004 6.4.8, ISO/IEC 23941 7.4.8), reported on <see cref="QRCodeDecodeInfo.Fnc1"/> and <see cref="RmQRCodeDecodeInfo.Fnc1"/>.
/// </summary>
/// <remarks>
/// <para>
/// FNC1 in first position (<see cref="IsFirstPosition"/>) marks GS1 data: an element string of application identifiers and their values, with the group separator (GS, U+001D) after each variable-length value that another follows.
/// FNC1 in second position (<see cref="IsSecondPosition"/>) marks data in an industry format registered with AIM, named by an 8-bit application indicator (<see cref="ApplicationIndicator"/>): 00 to 99 as its value, or one letter A-Z or a-z as its ASCII value plus 100.
/// </para>
/// <para>
/// The mode changes how the text reads. In an Alphanumeric segment a lone <c>%</c> is the separator and comes back as U+001D, and <c>%%</c> is one literal <c>%</c>, read left to right within the segment. A Byte segment carries the separator as the byte 0x1D.
/// The decoded text is the element string alone. It holds neither a symbology identifier nor the application indicator.
/// </para>
/// <para>
/// A reader transmits such a symbol as the AIM symbology identifier, then in second position the application indicator as two digits or one letter, then the data.
/// The identifier is <c>]Q3</c> for first position and <c>]Q5</c> for second, or <c>]Q4</c> and <c>]Q6</c> when the symbol also carries an ECI header, and <c>]Q1</c> or <c>]Q2</c> without FNC1.
/// The indicator <c>n</c> is written <c>n &lt; 100 ? n.ToString("D2") : ((char)(n - 100)).ToString()</c>.
/// </para>
/// <para>
/// A symbol without FNC1 reports <c>default</c>, on which <see cref="IsFirstPosition"/> and <see cref="IsSecondPosition"/> are both <c>false</c> and <see cref="ApplicationIndicator"/> is <c>null</c>, as it is in first position.
/// Only the decoders produce a value today. Micro QR has no FNC1 mode.
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

    /// <summary>FNC1 in first position: GS1 data.</summary>
    internal static Fnc1Mode FirstPosition => new(First, 0);

    /// <summary>FNC1 in second position with the application indicator as on the wire, a value <see cref="IsValidApplicationIndicator"/> accepts.</summary>
    internal static Fnc1Mode SecondPosition(int applicationIndicator) => new(Second, (byte)applicationIndicator);

    /// <summary>The ranges of ISO/IEC 18004 6.4.8.2: 00-99 as a number, 165-190 for A-Z and 197-222 for a-z.</summary>
    internal static bool IsValidApplicationIndicator(int codeword) => codeword is (>= 0 and <= 99) or (>= 165 and <= 190) or (>= 197 and <= 222);

    /// <summary>Whether either indicator was read, so the <c>%</c> rule applies to the Alphanumeric segments.</summary>
    internal bool IsActive => _position != NonePosition;

    /// <summary><c>true</c> when the symbol carries FNC1 in first position: its text is a GS1 element string.</summary>
    public bool IsFirstPosition => _position == First;

    /// <summary><c>true</c> when the symbol carries FNC1 in second position: its text follows the industry format <see cref="ApplicationIndicator"/> names.</summary>
    public bool IsSecondPosition => _position == Second;

    /// <summary>
    /// The application indicator as on the wire in second position (0-99 as its value, or a letter's ASCII value plus 100), and <c>null</c> in first position and without FNC1.
    /// <c>00</c> is the value 0, so a missing indicator is <c>null</c> rather than a number.
    /// </summary>
    public int? ApplicationIndicator => _position == Second ? _applicationIndicator : null;
}
