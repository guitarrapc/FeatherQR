using System.Text;

namespace QRInteropFixtures;

/// <summary>
/// The rMQR fixture corpus. Two lineages get different case lists on purpose:
///
/// zint-libzint (ASCII-only through the ZXingCpp wrapper) carries the systematic
/// sweep: every version × mode with a one-character payload (the leading data
/// codewords then pin the count-indicator width of that version/mode from the bit
/// stream, and every version appears at both ECC levels), plus capacity-boundary
/// payloads per height (padding-free streams).
///
/// qrtool (Rust qrcode2 crate) carries one capacity-boundary case per version
/// with rotating mode / ECC, plus the UTF-8 / Japanese byte-mode cases (no ECI;
/// readers detect UTF-8 heuristically or expose raw bytes).
///
/// Payloads are deterministic (fixed literals or fixed cyclic patterns) so
/// regeneration is byte-reproducible for a given generator version.
/// </summary>
public static class RmQRCorpus
{
    private const string NumericAlphabet = "0123456789";
    private const string AlphanumericAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 $%*+-./:";
    private const string ByteAlphabet = "the quick brown fox jumps over the lazy dog?! ";

    public static readonly RmQRFixtureCaseDefinition[] ZintCases = BuildZintCases();
    public static readonly RmQRFixtureCaseDefinition[] QrtoolCases = BuildQrtoolCases();

    private static RmQRFixtureCaseDefinition[] BuildZintCases()
    {
        var cases = new List<RmQRFixtureCaseDefinition>();

        // Systematic single-character sweep: pins count-indicator widths (N/A/B per
        // version) and format information (both ECC levels per version).
        foreach (var v in RmQRVersionTable.Entries)
        {
            var byteEcc = v.Index % 2 == 0 ? "M" : "H";
            cases.Add(new($"{Slug(v)}-m-numeric-1", "1", "M", v.Height, v.Width, "Numeric"));
            cases.Add(new($"{Slug(v)}-h-alphanumeric-1", "A", "H", v.Height, v.Width, "Alphanumeric"));
            cases.Add(new($"{Slug(v)}-{byteEcc.ToLowerInvariant()}-byte-1", "a", byteEcc, v.Height, v.Width, "Byte"));
        }

        // Capacity boundaries per height: numeric max at M on the widest width,
        // byte max at H on the narrowest width (padding-free, multi-block for the
        // large sizes).
        foreach (var height in new[] { 7, 9, 11, 13, 15, 17 })
        {
            var ofHeight = RmQRVersionTable.Entries.Where(e => e.Height == height).ToArray();
            var widest = ofHeight[^1];
            var narrowest = ofHeight[0];
            cases.Add(new($"{Slug(widest)}-m-numeric-max", Cyclic(NumericAlphabet, widest.NumericM), "M", widest.Height, widest.Width, "Numeric"));
            cases.Add(new($"{Slug(narrowest)}-h-byte-max", Cyclic(ByteAlphabet, narrowest.ByteH), "H", narrowest.Height, narrowest.Width, "Byte"));
        }

        return [.. cases];
    }

    private static RmQRFixtureCaseDefinition[] BuildQrtoolCases()
    {
        var cases = new List<RmQRFixtureCaseDefinition>();

        // One capacity-boundary case per version, mode and ECC rotating so every
        // mode × ECC pair appears across the sweep.
        foreach (var v in RmQRVersionTable.Entries)
        {
            var mode = (v.Index % 3) switch { 0 => "Numeric", 1 => "Alphanumeric", _ => "Byte" };
            var ecc = (v.Index / 3) % 2 == 0 ? "M" : "H";
            var alphabet = mode switch { "Numeric" => NumericAlphabet, "Alphanumeric" => AlphanumericAlphabet, _ => ByteAlphabet };
            cases.Add(new($"{Slug(v)}-{ecc.ToLowerInvariant()}-{mode.ToLowerInvariant()}-max", Cyclic(alphabet, v.Capacity(ecc, mode)), ecc, v.Height, v.Width, mode));
        }

        // UTF-8 byte payloads (no ECI): smallest byte capacities and a long text.
        cases.Add(new("r7x43-m-utf8-japanese", "こ", "M", 7, 43, "Byte", Utf8: true));                    // 3 bytes of 5
        cases.Add(new("r11x27-h-utf8-japanese", "あ", "H", 11, 27, "Byte", Utf8: true));                  // 3 bytes of 4
        cases.Add(new("r13x59-m-utf8-japanese", "こんにちは世界", "M", 13, 59, "Byte", Utf8: true));      // 21 bytes of 36
        cases.Add(new("r17x139-h-utf8-mixed", "rMQR 矩形コード ✓ naïve café", "H", 17, 139, "Byte", Utf8: true));

        // Kanji mode (ISO/IEC 23941 7.4.5, mode indicator 100): read by this library's decoder and
        // compared with its encoder module for module (KanjiEncoderOracleTest). qrtool takes
        // the payload as raw Shift_JIS bytes; libzint cannot produce Kanji at all.
        cases.Add(new("r11x43-m-kanji", "日本語漢字", "M", 11, 43, "Kanji"));
        cases.Add(new("r13x59-h-kanji", "漢字試験", "H", 13, 59, "Kanji"));
        cases.Add(new("r17x139-m-kanji-long", Cyclic("日本語漢字符号化試験用文字列", 60), "M", 17, 139, "Kanji"));

        // The Kanji count widths the cases above leave to derivation (2, 3 and 6; ISO/IEC 23941
        // Table 3), each filled to the version's Kanji capacity: the data bits less the 3-bit
        // mode indicator and the count, in 13-bit characters. All four are under 11 modules
        // high, clear of qrtool's tail defect.
        cases.Add(new("r7x43-m-kanji-max", "亜漾熙", "M", 7, 43, "Kanji"));                       // 2 bits: 3 of (48 - 5) / 13
        cases.Add(new("r7x59-m-kanji-max", "　滌漾熙ΩЖ", "M", 7, 59, "Kanji"));                  // 3 bits: 6 of (96 - 6) / 13
        cases.Add(new("r9x43-h-kanji-max", "─┼ヶ", "H", 9, 43, "Kanji"));                       // 3 bits: 3 of (56 - 6) / 13
        cases.Add(new("r9x139-m-kanji-max", Cyclic(StandardQrCorpus.KanjiCellCycle, 38), "M", 9, 139, "Kanji")); // 6 bits: 38 of (504 - 9) / 13

        // The seven cells where JIS X 0208 and CP932 disagree, with the Shift_JIS bytes
        // pinned because .NET cannot encode the JIS X 0208 readings (U+301C is not in
        // CP932). This is the fixture that fails if the table is ever rebuilt from CP932.
        cases.Add(new(
            "r15x59-m-kanji-jisx0208-divergent",
            "\\〜‖−¢£¬",
            "M", 15, 59, "Kanji",
            ShiftJisHex: "815F81608161817C8191819281CA"));

        return [.. cases];
    }

    private static string Slug(RmQRVersionTable.Entry v) => $"r{v.Height}x{v.Width}";

    private static string Cyclic(string alphabet, int length)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            sb.Append(alphabet[i % alphabet.Length]);
        return sb.ToString();
    }
}
