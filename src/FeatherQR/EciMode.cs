namespace FeatherQR;

/// <summary>
/// The character encoding declared in the symbol, as an ECI assignment.
/// </summary>
public enum EciMode
{
    // ECI header makes QR mask pattern difference, due to bit length difference.
    //
    // # Data structure:
    // When ASCII text "AB" was passed... 12 bits difference for ECI header.
    // Byte mode at version 1-9, where the count indicator is 8 bits (it widens to 16 at version 10).
    //
    // EciMode.Default
    // ┌──────────┬────────────────┬──────────┐
    // │ Mode(4b) │ Count(8b)      │ Data     │
    // │ 0100     │ 00000010       │ ...      │
    // └──────────┴────────────────┴──────────┘
    // 4 + 8 + (2 * 8) = 28 bits
    //
    // EciMode.Iso8859_1
    // ┌──────────┬────────────┬──────────┬────────────────┬──────────┐
    // │ ECI(4b)  │ Value(8b)  │ Mode(4b) │ Count(8b)      │ Data     │
    // │ 0111     │ 00000011   │ 0100     │ 00000010       │ ...      │
    // └──────────┴────────────┴──────────┴────────────────┴──────────┘
    // 4 + 8 + 4 + 8 + (2 * 8) = 40 bits
    //
    // # Effects:
    // 1. Padding
    // public void WritePadding(int targetBitCount)
    // {
    //     var remaining = targetBitCount - _writer.BitPosition; // <- - different due to ECI header
    //     ....
    // }
    //
    // 2. ECC
    // Data difference -> Reed Solomon ECC calculation difference -> Data after interleaving is different.
    //
    // 3. Mask pattern
    // Data difference -> Optimal mask pattern may be different -> Final QR code pattern is different.

    /// <summary>
    /// Picks the encoding from the content, which is what you want unless a reader demands otherwise.
    /// </summary>
    /// <remarks>
    /// Pure ASCII is sent with no ECI header at all, which is both smallest and most widely readable.
    /// Anything ISO-8859-1 can represent becomes <see cref="Iso8859_1"/>, and the rest becomes <see cref="Utf8"/>. With the generator's <c>AllowKanji</c> option, text whose every character is in JIS X 0208 (Japanese, and the Greek, Cyrillic and symbols that table holds) is written in Kanji mode instead, with no ECI header, at 13 bits a character, and under Optimal segmentation text JIS X 0208 holds apart from its ASCII characters can be written as Kanji runs beside runs of that ASCII, again with no ECI header, where that is the smaller symbol.
    /// Seven JIS X 0208 characters that Windows code page 932 reads differently, among them the wave dash 〜 and the minus sign −, keep a text in UTF-8, since a reader using that code page would show other characters.
    /// The header is the whole cost, 12 bits in Standard QR and 11 in rMQR, and it is what can push the content into a larger version.
    /// Two characters in Byte mode at version 1-9, where the count indicator is 8 bits wide:
    /// <code>
    /// "HE"    no header       4 + 8 + 16       = 28 bits
    /// "Ca"    ECI 3           12 + 4 + 8 + 16  = 40 bits
    /// "🎉"    ECI 26          12 + 4 + 8 + 32  = 56 bits
    /// </code>
    /// </remarks>
    Default = 0,
    /// <summary>
    /// ISO-8859-1, covering ASCII plus Western European letters such as À, Ç, Ñ, é and ü.
    /// </summary>
    /// <remarks>
    /// The ECI header costs 12 bits in Standard QR and 11 in rMQR.
    /// Emoji, CJK and Cyrillic cannot be encoded. Use <see cref="Utf8"/> for those.
    /// When it is asked for over text with any character above U+00FF, Standard QR and rMQR refuse it with an <see cref="System.ArgumentException"/>, from sizing as well as from the encode.
    /// </remarks>
    Iso8859_1 = 3,
    /// <summary>
    /// UTF-8, which covers every Unicode character: emoji, CJK, Cyrillic, Arabic, Hebrew and the rest.
    /// </summary>
    /// <remarks>
    /// The ECI header costs 12 bits in Standard QR and 11 in rMQR, and each character takes 1 to 4 bytes.
    /// For Western European text <see cref="Iso8859_1"/> is denser, and for Japanese text the Kanji mode <see cref="Default"/> writes with the generator's <c>AllowKanji</c> option is. Asking for UTF-8 keeps such text in UTF-8 whatever that option says.
    /// </remarks>
    Utf8 = 26
}

/// <summary>
/// Helpers for <see cref="EciMode"/>, and the charsets bytes are written under.
/// </summary>
/// <remarks>
/// Inside the encoders an <see cref="EciMode"/> says both how the content becomes bytes and which header precedes them.
/// Bytes input is written as it is, the way ISO-8859-1 text is, under the assignment the caller names or under none.
/// None is <see cref="EciMode.Default"/>. A named assignment is a value no caller can pass: <see cref="DeclaredBytesFlag"/> with the number in the bits below it.
/// The encoders compare a charset only with <see cref="EciMode.Default"/>, for the header, and with <see cref="EciMode.Utf8"/>, to transcode, so such a value is written with its header and its bytes as they are.
/// </remarks>
internal static class EciModeExtensions
{
    // ECI header. rMQR's mode indicator is 3 bits, 111.
    // ┌───────────────┬─────────────────────────────────────────────────┐
    // │ ECI indicator │ Designator                                      │
    // │ 0111 (4 bits) │ 0xxxxxxx                       0 to 127         │
    // │               │ 10xxxxxx xxxxxxxx              128 to 16383     │
    // │               │ 110xxxxx xxxxxxxx xxxxxxxx     16384 to 999999  │
    // └───────────────┴─────────────────────────────────────────────────┘

    /// <summary>The largest assignment number a designator carries.</summary>
    public const int MaxAssignment = 999_999;

    /// <summary>Marks the charset of bytes declared under an assignment. It is above every assignment number, so the number fits below it.</summary>
    private const int DeclaredBytesFlag = 1 << 20;

    /// <summary>The charset of bytes input: none, or the assignment, which <see cref="ValidateAssignment"/> has checked.</summary>
    public static EciMode BytesCharset(int? assignment)
        => assignment is { } number ? (EciMode)(DeclaredBytesFlag | number) : EciMode.Default;

    /// <summary>Whether the charset is bytes declared under an assignment.</summary>
    public static bool IsDeclaredBytes(this EciMode eciMode) => ((int)eciMode & DeclaredBytesFlag) != 0;

    /// <summary>The assignment the header carries. The two text charsets' values are their assignments.</summary>
    public static int Assignment(this EciMode eciMode) => (int)eciMode & (DeclaredBytesFlag - 1);

    /// <summary>The designator's length: one codeword up to 127, two up to 16383, three above.</summary>
    public static int DesignatorBits(int assignment) => assignment < 128 ? 8 : assignment < 16384 ? 16 : 24;

    /// <summary>The designator as written, its leading 0, 10 or 110 included.</summary>
    public static int Designator(int assignment)
        => assignment < 128 ? assignment : assignment < 16384 ? (0b10 << 14) | assignment : (0b110 << 21) | assignment;

    /// <summary>The Standard QR ECI header in bits: none for <see cref="EciMode.Default"/>, otherwise the 4-bit indicator and the designator.</summary>
    internal static int GetStandardQrHeaderBits(this EciMode eciMode)
        => eciMode == EciMode.Default ? 0 : 4 + DesignatorBits(eciMode.Assignment());

    /// <summary>The rMQR ECI header in bits: none for <see cref="EciMode.Default"/>, otherwise the 3-bit indicator and the designator.</summary>
    internal static int GetRmQrHeaderBits(this EciMode eciMode)
        => eciMode == EciMode.Default ? 0 : 3 + DesignatorBits(eciMode.Assignment());

    /// <summary>The charset as an error message names it: the member's name, or the number of a declared assignment.</summary>
    public static string Describe(this EciMode eciMode)
        => eciMode.IsDeclaredBytes() ? eciMode.Assignment().ToString(System.Globalization.CultureInfo.InvariantCulture) : eciMode.ToString();

    /// <summary>Throws for an assignment no designator carries.</summary>
    public static void ValidateAssignment(int? assignment)
    {
        if (assignment is { } number && (uint)number > MaxAssignment)
            throw new ArgumentOutOfRangeException("eciAssignment", number, $"An ECI assignment runs from 0 to {MaxAssignment}.");
    }
}
