namespace QRInteropFixtures;

/// <summary>
/// The FNC1 fixture corpus: three writer lineages, Standard QR and rMQR, both FNC1 modes. The
/// cases cover each data mode under FNC1 (Numeric, Alphanumeric with the separator as <c>%</c>,
/// Byte with the separator as 0x1D), a literal <c>%</c> written <c>%%</c>, the two separator
/// pairs no Alphanumeric segment can hold (GS GS and GS <c>%</c>, which read back as <c>%</c>
/// and <c>%</c> GS), FNC1 beside an ECI header in both orders the writers produce, and each
/// form of the application indicator (00, 99, an upper-case and a lower-case letter).
/// </summary>
/// <remarks>
/// The GS1 element strings are GS1's own examples (the GTIN, serial, lot and expiry of zxing-cpp's
/// rMQR GS1 unit test, from the GS1 General Specifications), so their check digits pass every
/// writer's validator. Everything is a fixed literal, so regeneration is reproducible for a given
/// writer version.
/// </remarks>
public static class Fnc1Corpus
{
    private const string Gs = "\u001D";
    private const string Gtin = "(01)09524000059109";
    private const string GtinElement = "0109524000059109";
    private const string ByteElementString = GtinElement + "101234567p" + Gs + "17231120";
    private const string MixedCaseSerialElementString = GtinElement + "2112345678p901" + Gs + "101234567p" + Gs + "17231120";
    private const string Utf8ElementString = "10é" + Gs + "17231120";

    /// <summary>ISO/IEC 18004:2006 6.4.8.2's example data for FNC1 in second position, application indicator 37. The standard prints the last character as <c>&lt;CR&gt;</c>.</summary>
    public const string IsoSecondPositionExample = "AA1234BBB112text text text text\r";

    private const string RmQR = Fnc1CaseDefinition.RmQR;
    private const Fnc1Position Second = Fnc1Position.Second;

    /// <summary>
    /// libzint takes bracketed AI syntax, validates it (character set, AI lengths, check digits) and writes
    /// the element string itself, with a separator after each variable-length field that another follows.
    /// It writes first position only, in Standard QR and rMQR, and puts FNC1 before an ECI header.
    /// </summary>
    public static readonly Fnc1CaseDefinition[] ZintCases =
    [
        new("gs1-numeric-only-l", Gtin + "(17)231120", "L", ExpectedText: GtinElement + "17231120"),
        new("gs1-alphanumeric-m", Gtin + "(10)ABC123(17)231120", "M", ExpectedText: GtinElement + "10ABC123" + Gs + "17231120"),
        new("gs1-mixed-case-serial-q", Gtin + "(21)12345678p901(10)1234567p(17)231120", "Q", ExpectedText: MixedCaseSerialElementString),
        new("gs1-percent-literal-h", Gtin + "(10)AB%CD(17)231120", "H", ExpectedText: GtinElement + "10AB%CD" + Gs + "17231120"),
        new("gs1-fnc1-then-eci-utf8-m", Gtin + "(10)ABC", "M", ExpectedText: GtinElement + "10ABC", Utf8: true),

        new("r7x43-m-gs1-date-only", "(17)231120", "M", RmQR, ExpectedText: "17231120", RmQRVersion: 1),
        new("r11x59-m-gs1-numeric", Gtin + "(17)231120", "M", RmQR, ExpectedText: GtinElement + "17231120", RmQRVersion: 13),
        new("r17x77-h-gs1-mixed-case-serial", Gtin + "(21)12345678p901(10)1234567p(17)231120", "H", RmQR, ExpectedText: MixedCaseSerialElementString, RmQRVersion: 30),
        new("r17x139-h-gs1-percent-literal", Gtin + "(10)AB%CD(17)231120", "H", RmQR, ExpectedText: GtinElement + "10AB%CD" + Gs + "17231120", RmQRVersion: 32),
        new("r13x99-m-gs1-fnc1-then-eci-utf8", Gtin + "(10)ABC", "M", RmQR, ExpectedText: GtinElement + "10ABC", Utf8: true, RmQRVersion: 21),
    ];

    /// <summary>
    /// ZXing.Net's <c>GS1_FORMAT</c> hint takes the element string as is and writes first position in Standard
    /// QR only, with the ECI header before FNC1 as the standard orders them. A separator keeps a text out of
    /// Alphanumeric mode (0x1D is not in its table), so its Alphanumeric segments hold no separator, and it
    /// does not double a literal <c>%</c>: the lone <c>%</c> it writes is the separator by the standard, and
    /// the fixture says so.
    /// </summary>
    public static readonly Fnc1CaseDefinition[] ZXingNetCases =
    [
        new("gs1-numeric-m", GtinElement + "17231120", "M", ExpectedText: GtinElement + "17231120"),
        new("gs1-byte-element-string-m", ByteElementString, "M", ExpectedText: ByteElementString),
        new("gs1-alphanumeric-lone-percent-reads-as-gs-l", "123%", "L", ExpectedText: "123" + Gs),
        new("gs1-eci-then-fnc1-utf8-m", Utf8ElementString, "M", ExpectedText: Utf8ElementString, Utf8: true),
    ];

    /// <summary>
    /// CodeGlyphX writes both positions in Standard QR (first through <c>EncodeGs1</c>, second through its
    /// options) and first position in rMQR through a GS1 validator that takes bracketed syntax. It puts FNC1
    /// before an ECI header. It doubles a literal <c>%</c> but writes the two separator pairs into one
    /// Alphanumeric segment, where a reader takes <c>%%</c> first: GS GS reads as <c>%</c>, and GS <c>%</c>
    /// reads as <c>%</c> GS. Those two fixtures carry the standard's reading of the symbol as written.
    /// </summary>
    public static readonly Fnc1CaseDefinition[] CodeGlyphXCases =
    [
        new("gs1-byte-element-string-m", ByteElementString, "M", ExpectedText: ByteElementString),
        new("gs1-alphanumeric-gs-as-percent-q", GtinElement + "10ABC123" + Gs + "17231120", "Q", ExpectedText: GtinElement + "10ABC123" + Gs + "17231120"),
        new("gs1-percent-literal-doubled-h", GtinElement + "10AB%CD", "H", ExpectedText: GtinElement + "10AB%CD"),
        new("gs1-gs-then-percent-reads-percent-then-gs-m", "10AB" + Gs + "%CD", "M", ExpectedText: "10AB%" + Gs + "CD"),
        new("gs1-gs-gs-reads-one-percent-m", "10AB" + Gs + Gs + "CD", "M", ExpectedText: "10AB%CD"),
        new("gs1-fnc1-then-eci-utf8-m", Utf8ElementString, "M", ExpectedText: Utf8ElementString, Utf8: true),

        new("aim-00-alphanumeric-m", "AB12", "M", Position: Second, ApplicationIndicator: 0, ExpectedText: "AB12"),
        new("aim-37-iso-example-m", IsoSecondPositionExample, "M", Position: Second, ApplicationIndicator: 37, ExpectedText: IsoSecondPositionExample),
        new("aim-99-numeric-l", "0123456789", "L", Position: Second, ApplicationIndicator: 99, ExpectedText: "0123456789"),
        new("aim-upper-a-byte-q", "hello world", "Q", Position: Second, ApplicationIndicator: 'A' + 100, ExpectedText: "hello world"),
        new("aim-lower-z-alphanumeric-h", "ABC", "H", Position: Second, ApplicationIndicator: 'z' + 100, ExpectedText: "ABC"),
        new("aim-37-gs-then-percent-reads-percent-then-gs-m", "AB" + Gs + "%CD", "M", Position: Second, ApplicationIndicator: 37, ExpectedText: "AB%" + Gs + "CD"),

        new("r11x59-m-gs1-numeric", Gtin + "(17)231120", "M", RmQR, ExpectedText: GtinElement + "17231120", RmQRVersion: 13),
        new("r13x139-m-gs1-mixed-case-serial", Gtin + "(21)12345678p901(10)1234567p(17)231120", "M", RmQR, ExpectedText: MixedCaseSerialElementString, RmQRVersion: 22),
    ];
}
