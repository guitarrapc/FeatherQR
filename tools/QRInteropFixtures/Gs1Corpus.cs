using System.Text;

namespace QRInteropFixtures;

/// <summary>
/// The GS1 data corpus: the standards' own examples, the equivalence classes of the GS1 layer's inputs, one
/// valid element string per AI of the pinned dictionary, and one failing value per linter the
/// dictionary names. Every case goes through the oracles unjudged: the fixture records their verdicts,
/// and the tests under Gs1/ compare this library against them. The hand-written cases are fixed
/// literals, and the synthesized ones follow from the committed dictionary, so a regeneration with
/// the same dictionary and oracles is byte-identical.
/// </summary>
public static class Gs1Corpus
{
    private const string Gs = "\u001D";

    // GS1's own example keys, so the check digits pass every validator.
    private const string Gtin = "09520123456788";              // Digital Link URI Syntax 1.7.0 examples
    private const string Sscc = "195201234567891232";          // Digital Link URI Syntax 1.7.0 examples
    private const string Gln = "9520123456788";                // Digital Link URI Syntax 1.7.0 examples
    private const string Giai = "9520614141234567";            // Digital Link URI Syntax 1.7.0 examples
    private const string Gmn = "1987654Ad4X4bL5ttr2310c2K";    // a GMN example from GS1 tooling, its check character pair confirmed by the Syntax Engine
    private const string Gsrn = "795260646688514634";          // Syntax Engine dl.c generation vector
    private const string Gdti = "9526064000028000001";         // Syntax Engine dl.c generation vector
    private const string Photograph = "0109504000059101" + "2112345678p901" + Gs + "101234567p" + Gs + "17141120" + "8200http://www.gs1.org/demo/";

    public static IEnumerable<(string Group, string Source, Gs1CaseInput[] Inputs)> Build(Gs1SyntaxDictionary dictionary, IReadOnlyDictionary<string, Gs1LinterVector[]> linterVectors)
    {
        yield return ("genspecs", "GS1 General Specifications, Release 26.0", GenSpecs().ToArray());
        yield return ("digital-link-standard", "GS1 Digital Link Standard: URI Syntax, Release 1.7.0", DigitalLinkStandard().ToArray());
        yield return ("equivalence-classes", "qrcode-test-fixtures.md equivalence classes", EquivalenceClasses(dictionary).ToArray());
        yield return ("per-ai", $"one valid element string per AI of Syntax Dictionary {dictionary.Release}", PerAi(dictionary).ToArray());
        yield return ("linter-mutations", $"one failing value per linter named by Syntax Dictionary {dictionary.Release}", LinterMutations(dictionary, linterVectors).ToArray());
    }

    private static Gs1CaseInput Case(string id, string group, string source, Gs1InputKind kind, string input, string? note = null)
        => new(id, "corpus/" + group, source, kind, input, note);

    private static IEnumerable<Gs1CaseInput> GenSpecs()
    {
        const string group = "genspecs";
        const string gs = "GS1 General Specifications 26.0";
        yield return Case("genspecs-figure-7-10", group, gs + " Figure 7-10 (7.8.6.1)", Gs1InputKind.Bracketed, "(01)95012345678903(3102)000400", "GTIN with net weight 4.00 kg in one symbol, no separator after the predefined-length element");
        yield return Case("genspecs-figure-7-12", group, gs + " Figure 7-12 (7.8.6.2)", Gs1InputKind.Bracketed, "(8005)000365(10)123456", "price per unit of measure and batch, a separator required after (8005). Both AIs require a key the figure omits");
        yield return Case("genspecs-figure-7-13", group, gs + " Figure 7-13 (7.8.6.3, also 5.5.2.3.1)", Gs1InputKind.Bracketed, "(01)90614141000015(3202)000150");
        yield return Case("genspecs-figure-7-14", group, gs + " Figure 7-14 (7.8.6.3)", Gs1InputKind.Bracketed, "(15)021231", "best before date alone, the GTIN in another symbol: the pairing rule is per item, not per symbol");
        yield return Case("genspecs-section-7-8-7-a", group, gs + " 7.8.7", Gs1InputKind.Bracketed, "(3943)1020", "implied decimal point: 1.020");
        yield return Case("genspecs-section-7-8-7-b", group, gs + " 7.8.7", Gs1InputKind.Bracketed, "(3929)300123456789", "implied decimal point: 300.123456789");
        yield return Case("genspecs-section-7-8-7-c", group, gs + " 7.8.7", Gs1InputKind.Bracketed, "(3923)3000200", "implied decimal point: 3000.200");
        yield return Case("genspecs-section-5-7-4-3-scan", group, gs + " 5.7.4.3", Gs1InputKind.ScanData, "]Q30110012345678902", "GS1 QR Code transmission with the symbology identifier");
        yield return Case("genspecs-section-5-7-4-3-element", group, gs + " 5.7.4.3", Gs1InputKind.ElementString, "0110012345678902");
        yield return Case("genspecs-section-2-1-transmitted", group, gs + " 1.4.2 (transmitted element string example)", Gs1InputKind.ElementString, "0109521234500001" + "10ABC123" + Gs + "11250504");
        yield return Case("genspecs-section-2-1-bracketed", group, gs + " 1.4.2 (bracketed example)", Gs1InputKind.Bracketed, "(01)09521234500001(10)ABC123(11)250504");
        yield return Case("genspecs-section-2-6-epc", group, gs + " 1.3 (SGTIN-96 example)", Gs1InputKind.Bracketed, "(01)09506000134352(21)123456789");
        yield return Case("genspecs-figure-4-14-1", group, gs + " 4.14.1 (healthcare HRI figure)", Gs1InputKind.Bracketed, "(01)09524000059109");
        yield return Case("genspecs-section-5-11-2-a", group, gs + " 5.11.2 (symbol structure figure)", Gs1InputKind.Bracketed, "(01)13112345678906(17)010615(10)A123456");
        yield return Case("genspecs-section-5-11-2-b", group, gs + " 5.11.2 (symbol structure figure)", Gs1InputKind.Bracketed, "(01)03812345678908(10)ABCD123456(410)3898765432108");
        yield return Case("genspecs-section-5-11-4-1", group, gs + " 5.11.4.1 (default transmission mode example)", Gs1InputKind.Bracketed, "(01)10012345678902(10)ABC123");
        yield return Case("genspecs-section-5-11-8-a", group, gs + " 5.11.8 (sample Composite symbols)", Gs1InputKind.Bracketed, "(01)03612345678904(11)990102");
        yield return Case("genspecs-section-5-11-8-b", group, gs + " 5.11.8 (sample Composite symbols)", Gs1InputKind.Bracketed, "(01)03412345678900(17)010200");
        yield return Case("genspecs-section-5-11-8-c", group, gs + " 5.11.8 (sample Composite symbols)", Gs1InputKind.Bracketed, "(01)93712345678904(3103)001234");
        yield return Case("genspecs-figure-4-15-1-2-photograph", group, gs + " Figure 4.15.1-2, the real-image corpus symbol qrcode-2/gs1-figure-4.15.1-2", Gs1InputKind.ElementString, Photograph);
        yield return Case("genspecs-figure-4-15-1-2-bracketed", group, gs + " Figure 4.15.1-2", Gs1InputKind.Bracketed, "(01)09504000059101(21)12345678p901(10)1234567p(17)141120(8200)http://www.gs1.org/demo/");
    }

    private static IEnumerable<Gs1CaseInput> DigitalLinkStandard()
    {
        const string group = "digital-link-standard";
        const string dl = "GS1 Digital Link URI Syntax 1.7.0";
        var examples = new (string Id, string Uri, string? Note)[]
        {
            ("gtin", "https://id.gs1.org/01/09520123456788", null),
            ("gtin-brand-stem", "https://brand.example.com/01/09520123456788", "any stem"),
            ("gtin-stem-with-path", "https://brand.example.com/some-extra/pathinfo/01/09520123456788", "a stem with a path"),
            ("gtin-cpv", "https://id.gs1.org/01/09520123456788/22/2A", null),
            ("gtin-cpv-brand-stem", "https://brand.example.com/01/09520123456788/22/2A", null),
            ("gtin-lot", "https://id.gs1.org/01/09520123456788/10/ABC123", null),
            ("gtin-lot-brand-stem", "https://brand.example.com/01/09520123456788/10/ABC123", null),
            ("gtin-serial", "https://id.gs1.org/01/09520123456788/21/12345", null),
            ("gtin-serial-brand-stem", "https://brand.example.com/01/09520123456788/21/12345", null),
            ("gtin-lot-serial-expiry", "https://id.gs1.org/01/09520123456788/10/ABC1/21/12345?17=180426", null),
            ("gtin-lot-serial-expiry-example-stem", "https://example.com/01/09520123456788/10/ABC1/21/12345?17=180426", null),
            ("gtin-net-weight", "https://id.gs1.org/01/09520123456788?3103=000195", null),
            ("gtin-net-weight-example-stem", "https://example.com/01/09520123456788?3103=000195", null),
            ("gtin-three-attributes-unsorted", "https://example.com/01/09520123456788?3103=000195&3922=0299&17=201225", "query keys not in canonical order"),
            ("gtin-three-attributes-unsorted-canonical-stem", "https://id.gs1.org/01/09520123456788?3103=000195&3922=0299&17=201225", null),
            ("gtin-three-attributes-canonical", "https://id.gs1.org/01/09520123456788?17=201225&3103=000195&3922=0299", "the canonical form (4.12)"),
            ("sscc", "https://id.gs1.org/00/195201234567891232", null),
            ("sscc-example-stem", "https://example.com/00/195201234567891232", null),
            ("sscc-content-count-lot", "https://id.gs1.org/00/195201234567891232?02=09520123456788&37=25&10=ABC123", "batch/lot as a query attribute beside SSCC and CONTENT"),
            ("sscc-content-count-lot-example-stem", "https://example.com/00/195201234567891232?02=09520123456788&37=25&10=ABC123", null),
            ("gln", "https://id.gs1.org/414/9520123456788", null),
            ("gln-extension-percent-encoded", "https://id.gs1.org/414/9520123456788/254/32a%2Fb", "a percent-encoded reserved character in a qualifier"),
            ("gln-example-stem", "https://example.com/414/9520123456788", null),
            ("gln-extension-percent-encoded-example-stem", "https://example.com/414/9520123456788/254/32a%2Fb", null),
            ("giai-with-gtin-attribute", "https://example.com/8004/9520614141234567?01=09520123456788", "a primary key as a data attribute of another"),
            ("gtin-with-giai-attribute", "https://example.com/01/09520123456788?8004=9520614141234567", null),
            ("gtin-cpv-linktype", "http://example.com/01/09520123456788/22/2A?linkType=gs1:traceability", "http and the reserved linkType key (4.10.1)"),
        };
        foreach (var (id, uri, note) in examples)
            yield return Case("dl-" + id, group, dl + " examples", Gs1InputKind.DigitalLink, uri, note);
    }

    private static IEnumerable<Gs1CaseInput> EquivalenceClasses(Gs1SyntaxDictionary dictionary)
    {
        const string group = "equivalence-classes";
        const string source = "qrcode-test-fixtures.md equivalence classes";
        var cases = new List<Gs1CaseInput>();
        void B(string id, string input, string? note = null) => cases.Add(Case("eq-" + id, group, source, Gs1InputKind.Bracketed, input, note));
        void E(string id, string input, string? note = null) => cases.Add(Case("eq-" + id, group, source, Gs1InputKind.ElementString, input, note));
        void D(string id, string input, string? note = null) => cases.Add(Case("eq-" + id, group, source, Gs1InputKind.DigitalLink, input, note));
        void S(string id, string input, string? note = null) => cases.Add(Case("eq-" + id, group, source, Gs1InputKind.ScanData, input, note));

        var gtin = "(01)" + Gtin;
        foreach (var unknown in new[] { "89", "4000" })
        {
            if (dictionary.TryGet(unknown, out _))
                throw new InvalidOperationException($"AI {unknown} is assigned in dictionary {dictionary.Release}; the unknown-AI cases need another.");
        }

        // AI lengths and shapes
        B("ai-2-digit", gtin + "(17)251231");
        B("ai-3-digit", "(253)" + Gdti + "(10)ABC", "GDTI with its optional serial component and a batch, where (10) requires a key (01) that is absent");
        B("ai-3-digit-serial-component", "(253)" + Gdti, "GDTI with the optional X..17 serial component present");
        B("ai-4-digit", gtin + "(8200)http://example.com/", null);
        B("ai-range-decimal-digit", gtin + "(3103)000195", "3103: three decimal places");
        B("ai-range-decimal-digit-variable", gtin + "(30)5(3923)3000200", "392n needs (30) or 31nn-36nn");
        B("ai-predefined-fixed", gtin + "(17)251231(10)ABC", "no separator after (01) and (17)");
        B("ai-fixed-outside-predefined", gtin + "(8005)000365(10)ABC", "(8005) is N6 but not in Table 7-6, so a separator follows it");
        B("ai-variable", gtin + "(10)ABC(21)XYZ");
        B("ai-optional-component-absent", gtin + "(7007)251231");
        B("ai-optional-component-present", gtin + "(7007)251231261231");
        B("ai-multi-component-8008-minimum", gtin + "(8008)25123123");
        B("ai-multi-component-8008-minutes", gtin + "(8008)2512312359");
        B("ai-multi-component-8008-seconds", gtin + "(8008)251231235959");
        B("ai-multi-component-8008-odd-length", gtin + "(8008)251231235", "9 digits: a partial component");
        B("ai-multi-component-7040", "(417)" + Gln + "(7040)1AB-");

        // Separators, on the element string as a symbol carries it
        E("sep-after-variable-not-last", "01" + Gtin + "10ABC" + Gs + "17251231");
        E("sep-after-fixed-outside-predefined", "01" + Gtin + "8005000365" + Gs + "10ABC");
        E("sep-none-after-predefined", "01" + Gtin + "17251231" + "10ABC");
        E("sep-none-after-last-variable", "01" + Gtin + "10ABC");
        E("sep-redundant-after-predefined", "01" + Gtin + Gs + "17251231", "7.8.6.3: a processing routine shall tolerate a single separator after any element");
        E("sep-trailing", "01" + Gtin + "10ABC" + Gs, "a trailing separator after the last element");
        E("sep-two-in-a-row", "01" + Gtin + "10ABC" + Gs + Gs + "17251231", "an empty field");
        E("sep-missing-after-variable", "01" + Gtin + "10ABC" + "17251231", "the missing separator makes the batch ABC17251231: no error, a different meaning");
        E("sep-missing-makes-too-long", "01" + Gtin + "10ABCDEFGHIJKLMN" + "17251231", "the missing separator makes the batch 22 characters, past X..20");
        E("sep-leading", Gs + "01" + Gtin, "a separator before the first element");

        // Character sets
        B("cset82-boundary", gtin + "(10)!\"%&'*+,-./:;<=>?_Az", "CSET 82 characters that need no escape");
        B("cset82-escaped-parenthesis", gtin + "(10)A\\(1)B", "a literal parenthesis escaped as \\( in bracketed input");
        E("cset82-parenthesis-element-string", "01" + Gtin + "10A(1)B");
        B("cset82-space", gtin + "(10)A B", "space is outside CSET 82");
        B("cset82-hash", gtin + "(10)A#B", "# is outside CSET 82");
        B("cset82-non-ascii", gtin + "(10)é", "a non-ASCII character");
        E("cset82-non-ascii-element-string", "01" + Gtin + "10é");
        B("cset82-control-character", gtin + "(10)A\tB", "a control character");
        B("cset39-boundary", "(8010)0628165987ABC-#/", "CSET 39: digits, upper case, #, - and /");
        B("cset39-lower-case", "(8010)0628165987abc", "lower case is outside CSET 39");
        B("cset64-boundary", gtin + "(21)ABC(8030)QUJDREVGRw==", "base64url with = padding");
        B("cset64-no-padding", gtin + "(21)ABC(8030)QUJDREVGRw", "base64url without padding");
        B("cset64-invalid-character", gtin + "(21)ABC(8030)ABC:123");
        B("cset64-invalid-padding", gtin + "(21)ABC(8030)123=");
        B("numeric-with-letter", "(01)0952012345678A");
        B("numeric-with-space", "(01)0952012345678 ");

        // Lengths
        B("length-fixed-short", "(01)0952012345678");
        B("length-fixed-long", "(01)095201234567881");
        B("length-variable-max", gtin + "(10)ABCDEFGHIJKLMNOPQRST", "20 characters");
        B("length-variable-over", gtin + "(10)ABCDEFGHIJKLMNOPQRSTU", "21 characters");
        B("length-variable-empty", gtin + "(10)", "an empty value");
        B("length-variable-empty-not-last", gtin + "(10)(17)251231");

        // Check digits and check character pairs
        B("csum-gtin-valid", gtin);
        B("csum-gtin-invalid", "(01)09520123456789");
        B("csum-sscc-valid", "(00)" + Sscc);
        B("csum-sscc-invalid", "(00)195201234567891233");
        B("csum-grai", "(8003)0" + Gln + "ABC", "GRAI: the check digit is over the N13 after the leading 0");
        B("csumalpha-gmn-valid", "(8013)" + Gmn);
        B("csumalpha-gmn-invalid", "(8013)1987654Ad4X4bL5ttr2310c2L");

        // Dates
        B("date-day-00-allowed", gtin + "(17)251200", "yymmd0: day 00 means end of month");
        B("date-day-00-refused", gtin + "(7006)251200", "yymmdd on (7006): a real day is required");
        B("date-leap-day-2024", gtin + "(17)240229");
        B("date-leap-day-2025", gtin + "(17)250229");
        B("date-leap-day-2000", gtin + "(17)000229", "year 00 reads as 2000 while 2000 is inside the -49/+50 window, until 2050");
        B("date-month-00", gtin + "(17)250031");
        B("date-month-13", gtin + "(17)251331");
        B("date-day-32", gtin + "(17)251232");
        B("date-yyyymmdd", "(8018)" + Gsrn + "(7250)20251231");
        B("date-yyyymmdd-invalid", "(8018)" + Gsrn + "(7250)20251301");

        // Pairings
        B("req-missing", "(10)ABC", "(10) requires 01, 02, 03, 8006 or 8026");
        B("req-satisfied", gtin + "(10)ABC");
        B("req-satisfied-by-alternative", "(8006)" + Gtin + "0102(10)ABC", "(10) satisfied by (8006)");
        B("req-compound-missing-both", "(8040)123456789012345", "(8040) requires 01 with 21");
        B("req-compound-missing-one", gtin + "(8040)123456789012345");
        B("req-compound-satisfied", gtin + "(21)ABC(8040)123456789012345");
        B("ex-serial-and-tpx", gtin + "(21)ABC(235)XYZ", "(21) excludes (235)");
        B("ex-gtin-and-content", gtin + "(02)" + Gtin + "(37)5", "(02) excludes (01)");
        B("ex-pattern", gtin + "(3103)000195(3104)000195", "310n excludes 310n");
        B("repeat-equal", gtin + "(10)ABC(10)ABC");
        B("repeat-unequal", gtin + "(10)ABC(10)DEF");

        // Unknown AIs
        B("unknown-ai-2-digit", gtin + "(89)ABC");
        B("unknown-ai-4-digit", gtin + "(4000)ABC");
        E("unknown-ai-element-string-last", "01" + Gtin + "4000ABC");
        E("unknown-ai-element-string-separated", "01" + Gtin + "4000ABC" + Gs + "10DEF", "an unknown AI read as variable length up to the separator");

        // (AI) text forms
        B("text-unbalanced-open", "(01" + Gtin);
        B("text-unbalanced-close", "01)" + Gtin);
        B("text-non-digit-ai", "(0A)" + Gtin);
        B("text-space-after-ai", "(01) " + Gtin);
        B("text-leading-space", " " + gtin);
        B("text-trailing-space", gtin + " ");
        B("text-square-brackets", "[01]" + Gtin, "libzint's bracket convention");
        B("text-element-string-given-as-bracketed", "01" + Gtin + "10ABC", "no brackets at all");
        B("text-empty", "");
        E("element-string-empty", "");
        E("element-string-fnc1-caret", "^01" + Gtin, "the Syntax Engine's ^ is not a separator in an element string");
        B("text-ai-only", "(01)");
        B("text-ai-with-leading-zero", "(010)" + Gtin.Substring(1));

        // Digital Link
        D("dl-key-00", "https://id.gs1.org/00/" + Sscc);
        D("dl-key-01", "https://id.gs1.org/01/" + Gtin);
        D("dl-key-253", "https://id.gs1.org/253/" + Gdti);
        D("dl-key-255", "https://id.gs1.org/255/" + Gln + "1234");
        D("dl-key-401", "https://id.gs1.org/401/9521234ABC");
        D("dl-key-402", "https://id.gs1.org/402/95212345678901234");
        D("dl-key-414", "https://id.gs1.org/414/" + Gln);
        D("dl-key-415", "https://id.gs1.org/415/" + Gln + "/8020/INV123");
        D("dl-key-417", "https://id.gs1.org/417/" + Gln);
        D("dl-key-8003", "https://id.gs1.org/8003/0" + Gln + "ABC");
        D("dl-key-8004", "https://id.gs1.org/8004/" + Giai);
        D("dl-key-8006", "https://id.gs1.org/8006/" + Gtin + "0102/21/ABC");
        D("dl-key-8010", "https://id.gs1.org/8010/0628165987ABC/8011/123");
        D("dl-key-8013", "https://id.gs1.org/8013/" + Gmn);
        D("dl-key-8017", "https://id.gs1.org/8017/" + Gsrn + "/8019/1");
        D("dl-key-8018", "https://id.gs1.org/8018/" + Gsrn);
        D("dl-qualifiers-in-order", "https://id.gs1.org/01/" + Gtin + "/22/2A/10/ABC/21/123");
        D("dl-qualifiers-out-of-order", "https://id.gs1.org/01/" + Gtin + "/10/ABC/22/2A");
        D("dl-qualifier-alternative-235", "https://id.gs1.org/01/" + Gtin + "/235/XYZ");
        D("dl-qualifiers-mixed-alternatives", "https://id.gs1.org/01/" + Gtin + "/22/2A/235/XYZ");
        D("dl-query-attributes", "https://id.gs1.org/01/" + Gtin + "?17=251231&3103=000195");
        D("dl-query-8200", "https://id.gs1.org/01/" + Gtin + "?8200=http%3A%2F%2Fexample.com", "(8200) cannot be a Digital Link attribute");
        D("dl-query-03", "https://id.gs1.org/01/" + Gtin + "?03=" + Gtin, "(03) is not permitted in a Digital Link URI");
        D("dl-query-8014", "https://id.gs1.org/01/" + Gtin + "?8014=" + Gmn, "(8014) is not permitted in a Digital Link URI");
        D("dl-query-8040", "https://id.gs1.org/01/" + Gtin + "/21/ABC?8040=123456789012345", "a data attribute URI Syntax 1.7.0 added for GenSpecs 26.0, not flagged ? in dictionary 2026-01-27");
        D("dl-percent-encoded-reserved", "https://id.gs1.org/01/" + Gtin + "/10/A%2FB");
        D("dl-percent-encoded-unreserved", "https://id.gs1.org/01/" + Gtin + "/10/%41%42");
        D("dl-percent-malformed", "https://id.gs1.org/01/" + Gtin + "/10/A%2");
        D("dl-stem-with-path", "https://brand.example.com/some-extra/pathinfo/01/" + Gtin);
        D("dl-http", "http://example.com/01/" + Gtin);
        D("dl-uppercase-scheme", "HTTPS://id.gs1.org/01/" + Gtin);
        D("dl-uppercase-host", "https://ID.GS1.ORG/01/" + Gtin);
        D("dl-trailing-slash", "https://id.gs1.org/01/" + Gtin + "/");
        D("dl-no-scheme", "id.gs1.org/01/" + Gtin);
        D("dl-unknown-numeric-key", "https://id.gs1.org/01/" + Gtin + "?4000=ABC");
        D("dl-extension-key", "https://id.gs1.org/01/" + Gtin + "?foo=bar");
        D("dl-extension-linktype", "https://id.gs1.org/01/" + Gtin + "?linkType=gs1:pip");
        D("dl-repeated-key", "https://id.gs1.org/01/" + Gtin + "?17=251231&17=261231", "a repeated key, which the engine refuses even with equal values");
        D("dl-compressed", "https://id.gs1.org/DBHKVAdpQgqrAg", "a compressed URI (legacy compression, frozen at 1.1.4)");
        D("dl-no-primary-key", "https://id.gs1.org/10/ABC");
        D("dl-qualifier-without-key", "https://id.gs1.org/21/ABC");
        D("dl-gtin-short", "https://id.gs1.org/01/9520123456788", "a 13-digit GTIN: removed in 1.3.0 (4.1)");
        D("dl-gtin-bad-check-digit", "https://id.gs1.org/01/09520123456789");
        D("dl-fragment", "https://id.gs1.org/01/" + Gtin + "#section");
        D("dl-empty-query-value", "https://id.gs1.org/01/" + Gtin + "?17=");
        D("dl-pairing-ex", "https://id.gs1.org/01/" + Gtin + "/21/ABC?235=XYZ", "(21) excludes (235)");
        B("dl-build-two-primary-keys", gtin + "(00)" + Sscc, "an element string with two primary keys: which one leads the URI");
        B("dl-build-no-primary-key", "(10)ABC", "no primary key, and (10) requires one");
        B("dl-build-key-and-qualifiers", gtin + "(21)123(10)ABC(22)2A", "qualifiers given out of path order");
        B("dl-build-attribute-only-ai", gtin + "(8200)http://example.com/x", "(8200) cannot go into a URI");
        B("dl-build-reserved-characters", gtin + "(10)A/B?C#D%E&F=G+H", "every reserved character of CSET 82 in a path element");
        B("dl-build-qualifier-as-attribute", "(00)" + Sscc + "(02)" + Gtin + "(37)5(10)ABC", "(10) is an attribute of SSCC, a qualifier of GTIN");

        // Scan data
        S("scan-q3", "]Q301" + Gtin + "10ABC" + Gs + "17251231");
        S("scan-q3-trailing-separator", "]Q301" + Gtin + "10ABC" + Gs);
        S("scan-q1-plain", "]Q1Hello");
        S("scan-q1-digital-link", "]Q1https://id.gs1.org/01/" + Gtin);
        S("scan-q1-element-string", "]Q101" + Gtin, "GS1 data without FNC1 is plain text");
        S("scan-q4-eci", "]Q401" + Gtin, "first position under the ECI protocol: not a GS1 transmission");
        S("scan-q5-second-position", "]Q53701" + Gtin, "second position with indicator 37");
        S("scan-d2-datamatrix", "]d201" + Gtin);
        S("scan-no-identifier", "01" + Gtin);

        // Input length
        B("length-one-element", gtin);
        B("length-long", gtin + "(10)ABCDEFGHIJKLMNOPQRST(21)ABCDEFGHIJKLMNOPQRST(240)ABCDEFGHIJKLMNOPQRSTUVWXYZ0123(241)ABCDEFGHIJKLMNOPQRSTUVWXYZ0123(250)ABCDEFGHIJKLMNOPQRSTUVWXYZ0123(251)ABCDEFGHIJKLMNOPQRSTUVWXYZ0123(4300)ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789(4301)ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789(8200)http://example.com/ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", "more than most symbols hold");

        return cases;
    }

    /// <summary>
    /// One case per AI: a value for each of its components, with the requisite AIs the dictionary demands (the
    /// first alternative of each <c>req</c> group, applied until closed), bracketed. The value table maps each
    /// linter to a value that satisfies it. The oracles say whether the result is valid.
    /// </summary>
    private static IEnumerable<Gs1CaseInput> PerAi(Gs1SyntaxDictionary dictionary)
    {
        foreach (var ai in dictionary.Ais)
            yield return Case("ai-" + ai, "per-ai", $"Syntax Dictionary {dictionary.Release}, AI {ai} ({(dictionary.TryGet(ai, out var e) ? e.Title : "?")})", Gs1InputKind.Bracketed, ElementStringFor(dictionary, ai, null));
    }

    private static IEnumerable<Gs1CaseInput> LinterMutations(Gs1SyntaxDictionary dictionary, IReadOnlyDictionary<string, Gs1LinterVector[]> linterVectors)
    {
        foreach (var linter in dictionary.Linters)
        {
            // The first AI whose component names the linter, and that component.
            var (ai, componentIndex) = dictionary.Entries
                .SelectMany(e => e.Components.Select((c, i) => (Entry: e, Component: c, Index: i)))
                .Where(x => x.Component.Linters.Contains(linter))
                .Select(x => (x.Entry.FirstAi, x.Index))
                .First();
            dictionary.TryGet(ai, out var entry);
            var component = entry.Components[componentIndex];
            var failing = FailingValue(linter, component, linterVectors.TryGetValue(linter, out var vectors) ? vectors : []);
            var input = ElementStringFor(dictionary, ai, (componentIndex, failing));
            yield return Case($"linter-{linter}-{ai}", "linter-mutations", $"Syntax Dictionary {dictionary.Release} linter {linter} on AI {ai}", Gs1InputKind.Bracketed, input, $"component {componentIndex} replaced by \"{failing}\"");
        }
    }

    /// <summary>The bracketed element string for <paramref name="ai"/> with its requisites, one component optionally replaced.</summary>
    private static string ElementStringFor(Gs1SyntaxDictionary dictionary, string ai, (int Index, string Value)? replace)
    {
        var ais = new List<string> { ai };
        var queue = new Queue<string>();
        queue.Enqueue(ai);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!dictionary.TryGet(current, out var entry))
                continue;
            foreach (var req in entry.Req)
            {
                var alternatives = req.Split(',');
                // An alternative already satisfied needs no addition.
                if (alternatives.Any(alt => alt.Split('+').All(member => ais.Any(a => Matches(a, member)))))
                    continue;
                foreach (var member in alternatives[0].Split('+'))
                {
                    var concrete = member.Replace('n', '0');
                    if (!ais.Any(a => Matches(a, member)))
                    {
                        ais.Add(concrete);
                        queue.Enqueue(concrete);
                    }
                }
            }
        }

        // Keys and other requisites first, the AI itself last, in dictionary order among the requisites.
        var ordered = ais.Skip(1).OrderBy(a => a, StringComparer.Ordinal).Append(ai);
        var text = new StringBuilder();
        foreach (var a in ordered)
        {
            dictionary.TryGet(a, out var entry);
            text.Append('(').Append(a).Append(')');
            for (var i = 0; i < entry.Components.Length; i++)
                text.Append(a == ai && replace is { } r && r.Index == i ? r.Value : ValueFor(entry.Components[i]));
        }
        return text.ToString();
    }

    private static bool Matches(string ai, string pattern)
        => ai.Length == pattern.Length && ai.Zip(pattern).All(p => p.Second == 'n' ? char.IsDigit(p.First) : p.First == p.Second);

    /// <summary>A value that satisfies the component's type, length and linters.</summary>
    public static string ValueFor(Gs1Component component)
    {
        var length = component.IsVariable ? Math.Min(component.Max, Math.Max(component.Min, 6)) : component.Max;
        string? value = null;
        foreach (var linter in component.Linters)
        {
            var candidate = linter switch
            {
                "yymmd0" or "yymmdd" => "251231",
                "yyyymmdd" => "20251231",
                "hh" => "23",
                "mi" or "ss" => "59",
                "hhmi" => "2359",
                "iso3166" or "iso3166999" => "276",
                "iso3166alpha2" => "DE",
                "iso4217" => "978",
                "iso5218" => "1",
                "latitude" => "0900000000",
                "longitude" => "1800000000",
                "yesno" => "1",
                "hyphen" => "-",
                "importeridx" => "-",
                "packagetype" => "1A",
                "mediatype" => "01",
                "posinseqslash" => "1/3",
                "winding" => "0",
                "zero" => "0",
                "nozeroprefix" => "123456",
                "iban" => "DE91100000000123456789",
                "couponcode" => "012345612345611110123",
                "couponposoffer" => "001234561234560123456",
                "pieceoftotal" => "0102",
                "csumalpha" => Gmn,
                "pcenc" => "ABC",
                _ => null,
            };
            if (candidate is not null)
            {
                value = candidate;
                break;
            }
        }

        value ??= component.Type switch
        {
            'N' => Cycle("1234567890", length),
            'X' => Cycle("952112345ABCDEFG", length),
            'Y' => Cycle("952112345ABCDEFG", length),
            'Z' => Cycle("QUJDREVG", Math.Max(4, length - length % 4)),
            _ => throw new InvalidOperationException($"Unknown component type {component.Type}."),
        };
        if (component.Linters.Contains("csum"))
            value = value.Substring(0, value.Length - 1) + Mod10CheckDigit(value.Substring(0, value.Length - 1));
        return value;
    }

    /// <summary>
    /// A value the linter refuses: the first of its unit-test failures that fits the component's length and
    /// character set (so the refusal is the linter's, not the length or charset check's), else a crafted one.
    /// </summary>
    private static string FailingValue(string linter, Gs1Component component, Gs1LinterVector[] vectors)
    {
        var fits = vectors
            .Where(v => !v.Pass)
            .Where(v => v.Value.Length >= component.Min && v.Value.Length <= component.Max)
            .Where(v => v.Value.All(c => InCharset(component.Type, c)))
            .Select(v => v.Value)
            .FirstOrDefault();
        if (fits is not null)
            return fits;

        var valid = ValueFor(component);
        return linter switch
        {
            "csum" => valid.Substring(0, valid.Length - 1) + ((valid[^1] - '0' + 1) % 10),
            "csumalpha" => valid.Substring(0, valid.Length - 1) + (valid[^1] == 'K' ? 'L' : 'K'),
            "gcppos1" => "1A" + valid.Substring(2),
            "gcppos2" => valid.Substring(0, 2) + "A" + valid.Substring(3),
            "hasnondigit" => Cycle("1234567890", valid.Length),
            "zero" => "1",
            "nonzero" => new string('0', valid.Length),
            "nozeroprefix" => "0" + valid.Substring(1),
            _ => throw new InvalidOperationException($"No failing value for linter {linter} on {component.Type}{component.Min}..{component.Max}."),
        };
    }

    private static bool InCharset(char type, char c) => type switch
    {
        'N' => c >= '0' && c <= '9',
        'X' => c < 128 && (char.IsAsciiLetterOrDigit(c) || "!\"%&'()*+,-./:;<=>?_".Contains(c)),
        'Y' => c < 128 && (char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c) || "#-/".Contains(c)),
        'Z' => c < 128 && (char.IsAsciiLetterOrDigit(c) || "-_=".Contains(c)),
        _ => false,
    };

    private static string Cycle(string alphabet, int length)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            sb.Append(alphabet[i % alphabet.Length]);
        return sb.ToString();
    }

    /// <summary>GS1 mod-10 check digit (General Specifications 7.9.1): weights 3 and 1 from the right.</summary>
    public static char Mod10CheckDigit(string digits)
    {
        var sum = 0;
        var weight = 3;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            sum += (digits[i] - '0') * weight;
            weight = 4 - weight;
        }
        return (char)('0' + (10 - sum % 10) % 10);
    }
}
