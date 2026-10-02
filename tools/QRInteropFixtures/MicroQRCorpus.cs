namespace QRInteropFixtures;

/// <summary>
/// The Micro QR fixture corpus: every version × legal ECC combination, every
/// supported mode, capacity-boundary payloads (padding-free) plus short payloads
/// (terminator + pad codeword paths), and a UTF-8 byte-mode case. All payloads
/// are fixed literals so regeneration is byte-reproducible for a given generator
/// version.
/// </summary>
public static class MicroQRCorpus
{
    public static readonly MicroQRFixtureCaseDefinition[] Cases =
    [
        // M1: numeric only, error detection only
        new("m1-numeric-max", "12345", "ErrorDetectionOnly", 1, "Numeric"),
        new("m1-numeric-short", "1", "ErrorDetectionOnly", 1, "Numeric"),

        // M2: L/M, numeric + alphanumeric
        new("m2-l-numeric-max", "0123456789", "L", 2, "Numeric"),
        new("m2-m-numeric-max", "12345678", "M", 2, "Numeric"),
        new("m2-l-alphanumeric-max", "ABCDEF", "L", 2, "Alphanumeric"),
        new("m2-m-alphanumeric-max", "HELLO", "M", 2, "Alphanumeric"),

        // M3: L/M, adds byte mode (capacities end on a half codeword)
        new("m3-l-numeric-max", "12345678901234567890123", "L", 3, "Numeric"),
        new("m3-m-numeric-max", "123456789012345678", "M", 3, "Numeric"),
        new("m3-l-alphanumeric-max", "HELLO WORLD 14", "L", 3, "Alphanumeric"),
        new("m3-l-byte-max", "bytes m3l", "L", 3, "Byte"),
        new("m3-m-byte-max", "byte hi", "M", 3, "Byte"),

        // M4: L/M/Q
        new("m4-l-numeric-max", "99999999999999999999999999999999999", "L", 4, "Numeric"),
        new("m4-l-alphanumeric-max", "HELLO WORLD PLUS 21ST", "L", 4, "Alphanumeric"),
        new("m4-m-alphanumeric-max", "HELLO MICRO QR 18C", "M", 4, "Alphanumeric"),
        new("m4-l-byte-max", "fifteen bytes!!", "L", 4, "Byte"),
        new("m4-m-byte-short", "m4 pads", "M", 4, "Byte"),
        new("m4-q-byte-max", "bytes!!!!", "Q", 4, "Byte"),

        // UTF-8 byte payload (Micro QR has no ECI; readers detect UTF-8
        // heuristically). libzint rejects non-ASCII input through the ZXingCpp
        // wrapper, so this case comes from the qrtool lineage only.
        new("m4-l-utf8-japanese", "こんにちは", "L", 4, "Byte", Utf8: true),

        // Kanji mode (ISO/IEC 18004 8.4.5): M3 and M4 only, the versions whose mode
        // indicator is wide enough to express it. Read by this library's decoder and compared with
        // its encoder (KanjiEncoderOracleTest), and
        // qrtool is the only lineage here that can emit it (libzint rejects non-ASCII
        // input through the ZXingCpp wrapper, and M1/M2 have no Kanji mode at all).
        new("m3-l-kanji", "日本語", "L", 3, "Kanji"),
        new("m3-m-kanji", "漢字", "M", 3, "Kanji"),
        new("m4-l-kanji", "日本語試験", "L", 4, "Kanji"),
        new("m4-m-kanji", "日本語符", "M", 4, "Kanji"),
        new("m4-q-kanji", "漢字", "Q", 4, "Kanji"),
    ];
}
