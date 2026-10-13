using System.Security.Cryptography;

namespace FeatherQR.Tests;

/// <summary>
/// Pins the GS1 data corpus (Fixtures/Gs1/) that the GS1 layer (FeatherQR.Gs1) is built against: the
/// pinned dictionary release is the one the provenance names, every oracle verdict comes from the release the
/// fixture spec pins, the corpus holds the shapes its equivalence classes need, the per-AI and per-linter cases
/// cover the committed dictionary, and the imported Syntax Engine vectors agree with their source except where
/// a known cause (listed here and in the fixture spec) explains the difference. These tests read the fixtures
/// only, and the tests under Gs1/ hold the library to them. Regenerate with <c>dotnet run --project tools/QRInteropFixtures -- regenerate-gs1</c>.
/// </summary>
public class Gs1FixtureTest
{
    private const string DictionaryRelease = "2026-01-27";
    private const string DictionarySha256 = "6461ece7c03420fb27a3f18163ef9a08694aa4bb2491dea25f63bcdf22451a99";

    private static readonly Lazy<IReadOnlyList<Gs1FixtureCase>> Cases = new(() => Gs1FixtureLoader.EnumerateCases().ToList());

    public static IEnumerable<string> FileIds() => Gs1FixtureLoader.EnumerateFileIds();

    [Test]
    public async Task PinnedDictionary_IsTheRecordedRelease()
    {
        var bytes = await File.ReadAllBytesAsync(Gs1FixtureLoader.DictionaryPath);
        await Assert.That(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()).IsEqualTo(DictionarySha256);

        var lines = (await File.ReadAllLinesAsync(Gs1FixtureLoader.DictionaryPath)).Select(l => l.TrimEnd('\r')).ToArray();
        await Assert.That(lines).Contains("# Release: " + DictionaryRelease);
        var (ais, linters) = ParseDictionary(lines);
        await Assert.That(ais.Count).IsEqualTo(541);
        await Assert.That(linters.Count).IsEqualTo(34);
        // Every linter the dictionary names has an imported vector file, and the list linters' sources sit beside the dictionary.
        var vectorFiles = Gs1FixtureLoader.EnumerateLinters().ToHashSet(StringComparer.Ordinal);
        await Assert.That(linters.Where(l => !vectorFiles.Contains(l))).IsEmpty();
        foreach (var listLinter in new[] { "iso3166", "iso3166alpha2", "iso3166999", "iso4217", "iso5218", "mediatype", "packagetype" })
            await Assert.That(File.Exists(Path.Combine(Gs1FixtureLoader.Root, "syntax-dictionary", $"lint_{listLinter}.c"))).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(FileIds))]
    public async Task File_RecordsThePinnedOracles(string fileId)
    {
        var file = Gs1FixtureLoader.LoadFile(fileId);
        await Assert.That(file.Oracles.SyntaxEngine.Version).StartsWith("1.4.1");
        await Assert.That(file.Oracles.SyntaxEngine.Dictionary).StartsWith(DictionaryRelease);
        await Assert.That(file.Oracles.CodeGlyphX.Version).StartsWith("2.1.0");
        await Assert.That(file.Oracles.CodeGlyphX.Dictionary).IsEqualTo(DictionaryRelease);
        await Assert.That(file.Cases.Length).IsGreaterThan(0);
        await Assert.That(file.Cases.Select(c => c.Id).Distinct().Count()).IsEqualTo(file.Cases.Length);
        foreach (var c in file.Cases)
        {
            await Assert.That(c.Group).IsEqualTo(fileId);
            // A verdict is one or the other: an error with no data, or data with no error.
            await Assert.That(c.SyntaxEngine.Valid).IsEqualTo(c.SyntaxEngine.Error is null);
            if (c.SyntaxEngine.Valid && c.SyntaxEngine.Bracketed is not null)
                await Assert.That(c.SyntaxEngine.ElementString).IsNotNull();
            if (c.InputKind == Gs1InputKind.Bracketed)
                await Assert.That(c.Libzint).IsNotNull();
            if (c.Reader is not null)
                await Assert.That(c.Reader.SymbologyIdentifier).IsEqualTo("]Q3");
        }
    }

    /// <summary>The corpus shapes the equivalence classes name, each present under its id, and the file sizes a regeneration could silently shrink.</summary>
    [Test]
    public async Task Corpus_HasEveryShapeTheLayerNeeds()
    {
        var byId = Cases.Value.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var counts = Cases.Value.GroupBy(c => c.Group).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        await Assert.That(counts["corpus/genspecs"]).IsEqualTo(21);
        await Assert.That(counts["corpus/digital-link-standard"]).IsEqualTo(27);
        await Assert.That(counts["corpus/equivalence-classes"]).IsEqualTo(156);
        await Assert.That(counts["corpus/per-ai"]).IsEqualTo(541);
        await Assert.That(counts["corpus/linter-mutations"]).IsEqualTo(34);
        await Assert.That(counts["syntax-engine/parse-ai-data"]).IsEqualTo(27);
        await Assert.That(counts["syntax-engine/process-ai-data"]).IsEqualTo(77);
        await Assert.That(counts["syntax-engine/validate-ais"]).IsEqualTo(77);
        await Assert.That(counts["syntax-engine/linters"]).IsEqualTo(99);
        await Assert.That(counts["syntax-engine/parse-dl-uri"]).IsEqualTo(113);
        await Assert.That(counts["syntax-engine/generate-dl-uri"]).IsEqualTo(22);
        await Assert.That(counts["syntax-engine/scan-data"]).IsEqualTo(54);

        foreach (var kind in Enum.GetValues<Gs1InputKind>())
            await Assert.That(Cases.Value.Any(c => c.InputKind == kind)).IsTrue();

        string[] required =
        [
            // AIs and shapes
            "eq-ai-2-digit", "eq-ai-3-digit", "eq-ai-4-digit", "eq-ai-range-decimal-digit", "eq-ai-predefined-fixed", "eq-ai-fixed-outside-predefined", "eq-ai-variable",
            "eq-ai-optional-component-absent", "eq-ai-optional-component-present", "eq-ai-multi-component-8008-minimum", "eq-ai-multi-component-8008-seconds",
            // separators
            "eq-sep-after-variable-not-last", "eq-sep-after-fixed-outside-predefined", "eq-sep-none-after-predefined", "eq-sep-none-after-last-variable",
            "eq-sep-redundant-after-predefined", "eq-sep-trailing", "eq-sep-two-in-a-row", "eq-sep-missing-after-variable",
            // character sets
            "eq-cset82-boundary", "eq-cset82-escaped-parenthesis", "eq-cset82-space", "eq-cset82-non-ascii", "eq-cset39-boundary", "eq-cset39-lower-case",
            "eq-cset64-boundary", "eq-cset64-invalid-character", "eq-cset64-invalid-padding", "eq-numeric-with-letter",
            // check digits, dates, pairings, unknown AIs, text forms
            "eq-csum-gtin-valid", "eq-csum-gtin-invalid", "eq-csumalpha-gmn-valid", "eq-csumalpha-gmn-invalid",
            "eq-date-day-00-allowed", "eq-date-day-00-refused", "eq-date-leap-day-2024", "eq-date-leap-day-2025", "eq-date-month-00", "eq-date-month-13",
            "eq-req-missing", "eq-req-satisfied", "eq-req-compound-missing-one", "eq-req-compound-satisfied", "eq-ex-serial-and-tpx", "eq-repeat-equal", "eq-repeat-unequal",
            "eq-unknown-ai-2-digit", "eq-unknown-ai-4-digit", "eq-unknown-ai-element-string-separated",
            "eq-text-unbalanced-open", "eq-text-non-digit-ai", "eq-text-space-after-ai", "eq-text-empty", "eq-element-string-empty",
            // Digital Link
            "eq-dl-key-00", "eq-dl-key-01", "eq-dl-key-253", "eq-dl-key-255", "eq-dl-key-401", "eq-dl-key-402", "eq-dl-key-414", "eq-dl-key-415", "eq-dl-key-417",
            "eq-dl-key-8003", "eq-dl-key-8004", "eq-dl-key-8006", "eq-dl-key-8010", "eq-dl-key-8013", "eq-dl-key-8017", "eq-dl-key-8018",
            "eq-dl-qualifiers-in-order", "eq-dl-qualifiers-out-of-order", "eq-dl-query-attributes", "eq-dl-query-8200", "eq-dl-percent-encoded-reserved",
            "eq-dl-stem-with-path", "eq-dl-http", "eq-dl-trailing-slash", "eq-dl-unknown-numeric-key", "eq-dl-extension-key", "eq-dl-repeated-key", "eq-dl-compressed",
            "eq-dl-build-two-primary-keys", "eq-dl-uppercase-scheme",
            // the standards' examples and the photograph
            "genspecs-figure-7-13", "genspecs-section-5-7-4-3-scan", "genspecs-figure-4-15-1-2-photograph", "dl-gtin-lot-serial-expiry", "dl-gtin-three-attributes-canonical",
        ];
        await Assert.That(required.Where(id => !byId.ContainsKey(id))).IsEmpty();

        // The two readings of the standard's examples and the photograph the real-image corpus holds.
        await Assert.That(byId["genspecs-figure-4-15-1-2-photograph"].SyntaxEngine.Hri).IsEquivalentTo(["(01) 09504000059101", "(21) 12345678p901", "(10) 1234567p", "(17) 141120", "(8200) http://www.gs1.org/demo/"]);
        await Assert.That(byId["genspecs-figure-4-15-1-2-photograph"].Reader!.Hri).IsEqualTo("(01)09504000059101(21)12345678p901(10)1234567p(17)141120(8200)http://www.gs1.org/demo/");
        await Assert.That(byId["genspecs-section-5-7-4-3-scan"].SyntaxEngine.ElementString).IsEqualTo("0110012345678902");
        // The missing-separator trap the release decision rests on (FNC1 encode ships with the GS1 builders): valid data with another meaning.
        await Assert.That(byId["eq-sep-missing-after-variable"].SyntaxEngine.Valid).IsTrue();
        await Assert.That(byId["eq-sep-missing-after-variable"].SyntaxEngine.Bracketed).IsEqualTo("(01)09520123456788(10)ABC17251231");
        // Separator tolerance on read (7.8.6.3), where the oracles differ.
        await Assert.That(byId["eq-sep-redundant-after-predefined"].SyntaxEngine.Valid).IsTrue();
        await Assert.That(byId["eq-sep-redundant-after-predefined"].CodeGlyphX!.Valid).IsFalse();
    }

    /// <summary>One element string per AI of the committed dictionary that the Syntax Engine accepts, and one value per named linter that it refuses on that linter's ground.</summary>
    [Test]
    public async Task PerAiAndPerLinterCases_CoverTheCommittedDictionary()
    {
        var lines = (await File.ReadAllLinesAsync(Gs1FixtureLoader.DictionaryPath)).Select(l => l.TrimEnd('\r')).ToArray();
        var (ais, linters) = ParseDictionary(lines);

        var perAi = Cases.Value.Where(c => c.Group == "corpus/per-ai").ToDictionary(c => c.Id.Substring("ai-".Length), StringComparer.Ordinal);
        await Assert.That(ais.Where(ai => !perAi.ContainsKey(ai))).IsEmpty();
        await Assert.That(perAi.Values.Where(c => !c.SyntaxEngine.Valid).Select(c => c.Id)).IsEmpty();
        // CodeGlyphX shares the release, so it agrees on every AI's valid value.
        await Assert.That(perAi.Values.Where(c => c.CodeGlyphX is not { Valid: true }).Select(c => c.Id)).IsEmpty();
        // The reader's (AI) text of libzint's symbol equals the engine's HRI for every AI but the TPX cases (AI 235, libzint writes no separator after it).
        var hriMismatch = perAi.Values.Where(c => c.Reader is not null && c.Reader.Hri != string.Concat(c.SyntaxEngine.Hri!.Select(l => l.Replace(") ", ")")))).Select(c => c.Id).ToList();
        await Assert.That(hriMismatch).IsEmpty();

        var mutations = Cases.Value.Where(c => c.Group == "corpus/linter-mutations").ToList();
        foreach (var linter in linters)
        {
            var mutation = mutations.SingleOrDefault(c => c.Id.StartsWith($"linter-{linter}-", StringComparison.Ordinal));
            await Assert.That(mutation).IsNotNull();
            await Assert.That(mutation!.SyntaxEngine.Valid).IsFalse();
        }
    }

    /// <summary>The 5,656 linter unit-test vectors of the dictionary's reference linters, as imported.</summary>
    [Test]
    public async Task LinterVectors_HaveThePinnedCounts()
    {
        var files = Gs1FixtureLoader.EnumerateLinters().Select(Gs1FixtureLoader.LoadLinter).ToList();
        await Assert.That(files.Count).IsEqualTo(39);
        await Assert.That(files.Sum(f => f.Cases.Length)).IsEqualTo(5656);
        var byName = files.ToDictionary(f => f.Linter, StringComparer.Ordinal);
        await Assert.That(byName["iso3166"].Cases.Length).IsEqualTo(1009);
        await Assert.That(byName["iso4217"].Cases.Length).IsEqualTo(1008);
        await Assert.That(byName["iso3166alpha2"].Cases.Length).IsEqualTo(682);
        await Assert.That(byName["packagetype"].Cases.Length).IsEqualTo(442);
        await Assert.That(byName["couponcode"].Cases.Length).IsEqualTo(389);
        await Assert.That(byName["yymmd0"].Cases.Count(c => c.Pass)).IsGreaterThan(0);
        await Assert.That(byName["yymmd0"].Cases.Count(c => !c.Pass && c.Error is not null && c.Markup is not null)).IsGreaterThan(0);
        // The ISO 3166 numeric list as the linter enumerates it: 249 assigned codes of 1000.
        await Assert.That(byName["iso3166"].Cases.Count(c => c.Pass && c.Value.Length == 3)).IsEqualTo(249);
    }

    /// <summary>
    /// Every vector imported from the Syntax Engine's unit tests gets the verdict its source states from the
    /// pinned binary, except where one of these causes applies: the source tests a layer below the console app's
    /// full validation (no requisite check), a test flag the console app does not set (unknown AIs, zero-suppressed
    /// GTINs, convenience alphas), or input the app takes as plain data where the parser refuses it.
    /// </summary>
    [Test]
    public async Task SyntaxEngineVectors_MatchTheirSourceOrAKnownCause()
    {
        var unexplained = new List<string>();
        foreach (var c in Cases.Value.Where(c => c.Expected is not null))
        {
            var generates = c.Group == "syntax-engine/generate-dl-uri";
            var binaryAccepts = generates ? c.SyntaxEngine.DigitalLink is not null : c.SyntaxEngine.Valid;
            if (binaryAccepts == c.Expected!.Valid)
                continue;
            var reason = c.SyntaxEngine.Error ?? c.SyntaxEngine.DigitalLinkError ?? "";
            var known =
                reason.StartsWith("Required AIs for AI", StringComparison.Ordinal)              // parse-level tests, no requisite check
                || reason.Contains("AI: 89", StringComparison.Ordinal) || reason.Contains("AI (89)", StringComparison.Ordinal) // permitUnknownAIs
                || reason == "AI (01) value is too short"                                           // permitZeroSuppressedGTINinDLuris
                || reason == "No GS1 DL keys found in path info"                                    // permitConvenienceAlphas
                || (!c.Expected.Valid && c.SyntaxEngine.Valid && c.SyntaxEngine.Bracketed is null); // not a URI: the app takes it as plain data
            if (!known)
                unexplained.Add($"{c.Id}: source {(c.Expected.Valid ? "accepts" : "refuses")}, binary {(binaryAccepts ? "accepts" : "refuses")} ({reason})");
        }
        await Assert.That(unexplained).IsEmpty();

        // Where both agree the vector is accepted and the source states the output, the binary's output is the source's
        // (element strings compared as AI sets, since the source sorts them; a URI generated on another stem is not compared).
        var outputMismatch = new List<string>();
        foreach (var c in Cases.Value.Where(c => c.Expected is { Valid: true, Output: not null }))
        {
            var expected = c.Expected!.Output!;
            if (c.Group == "syntax-engine/generate-dl-uri")
            {
                if (c.SyntaxEngine.DigitalLink is not null && expected.StartsWith("https://id.gs1.org/", StringComparison.Ordinal) && c.SyntaxEngine.DigitalLink != expected)
                    outputMismatch.Add(c.Id);
                continue;
            }
            if (!c.SyntaxEngine.Valid)
                continue;
            var caret = c.SyntaxEngine.ElementString is null ? null : "^" + c.SyntaxEngine.ElementString.Replace('\u001D', '^');
            var matches = expected == caret || expected == c.SyntaxEngine.DataString
                || (caret is not null && expected.StartsWith("^", StringComparison.Ordinal) && Sorted(caret) == Sorted(expected));
            if (!matches)
                outputMismatch.Add($"{c.Id}: source \"{expected}\", binary \"{caret ?? c.SyntaxEngine.DataString}\"");
        }
        await Assert.That(outputMismatch).IsEmpty();
    }

    private static string Sorted(string caret) => string.Join("^", caret.TrimStart('^').Split('^').OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>The AIs (ranges expanded) and the linter names of the dictionary file, read from its entry lines.</summary>
    private static (HashSet<string> Ais, HashSet<string> Linters) ParseDictionary(string[] lines)
    {
        var ais = new HashSet<string>(StringComparer.Ordinal);
        var linters = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            var body = line.Split('#')[0];
            var tokens = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var range = tokens[0].Split('-');
            var first = int.Parse(range[0]);
            var last = int.Parse(range[^1]);
            for (var ai = first; ai <= last; ai++)
                ais.Add(ai.ToString().PadLeft(range[0].Length, '0'));
            foreach (var token in tokens.Skip(1))
            {
                if (token.StartsWith("req=", StringComparison.Ordinal) || token.StartsWith("ex=", StringComparison.Ordinal) || token.StartsWith("dlpkey", StringComparison.Ordinal))
                    continue;
                foreach (var name in token.Split(',').Skip(1))
                    linters.Add(name);
            }
        }
        return (ais, linters);
    }
}
