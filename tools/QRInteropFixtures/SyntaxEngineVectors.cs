namespace QRInteropFixtures;

/// <summary>An input the corpus or an imported vector hands to the oracles, before any verdict.</summary>
public sealed record Gs1CaseInput(string Id, string Group, string Source, Gs1InputKind InputKind, string Input, string? Note = null, Gs1ExpectedVerdict? Expected = null);

/// <summary>
/// Imports the Syntax Engine's own unit-test vectors from its pinned source (<c>src/c-lib/ai.c</c>,
/// <c>dl.c</c>, <c>scandata.c</c>): bracketed parsing, unbracketed processing, the pairing and repeat
/// checks, Digital Link parsing and generation, URI escaping and scan data. Each vector keeps what its
/// source asserts (accepted or not, and the expected output in the source's <c>^</c> notation), and the
/// generator adds the pinned binary's verdict beside it, so the two can be compared.
/// </summary>
public static class SyntaxEngineVectors
{
    public const string SourceName = "GS1 Barcode Syntax Engine " + SyntaxEngineOracle.Version + " unit tests";

    /// <summary>The vector groups: file name under <c>Gs1/syntax-engine/</c>, the source file, the test macro, and the inputs.</summary>
    public static IEnumerable<(string File, string Source, Gs1CaseInput[] Inputs)> Read(string engineSourceDir)
    {
        var cLib = Path.Combine(engineSourceDir, "src", "c-lib");
        var ai = CSource.StripComments(File.ReadAllText(Path.Combine(cLib, "ai.c")));
        var dl = CSource.StripComments(File.ReadAllText(Path.Combine(cLib, "dl.c")));
        var scan = CSource.StripComments(File.ReadAllText(Path.Combine(cLib, "scandata.c")));

        yield return ("parse-ai-data", "ai.c test_ai_parseAIdata", Cases("parse-ai-data", "ai.c test_parseAIdata", CSource.Calls(ai, "test_parseAIdata"),
            a => (Gs1InputKind.Bracketed, CSource.Literal(a[1]), new Gs1ExpectedVerdict(a[0] == "true", CSource.Literal(a[2])))));

        // The source's unbracketed form has ^ as FNC1 in first position and as the separator. Vectors without the
        // leading ^ test the C parser's refusal of data that has no FNC1, which has no element-string form: the
        // console app takes such data as plain text. They are not imported.
        yield return ("process-ai-data", "ai.c test_ai_processAIdata", Cases("process-ai-data", "ai.c test_processAIdata", CSource.Calls(ai, "test_processAIdata").Where(c => CSource.IsLiteral(c.Args[1]) && CSource.Literal(c.Args[1]).StartsWith("^", StringComparison.Ordinal)),
            a => (Gs1InputKind.ElementString, CSource.Literal(a[1]).Substring(1).Replace('^', '\u001D'), new Gs1ExpectedVerdict(a[0] == "true", null))));

        yield return ("validate-ais", "ai.c test_ai_validateAIs", Cases("validate-ais", "ai.c test_validateAIs", CSource.Calls(ai, "test_validateAIs"),
            a => (Gs1InputKind.Bracketed, CSource.Literal(a[2]), new Gs1ExpectedVerdict(a[0] == "true", null)), a => a[1]));

        yield return ("linters", "ai.c test_ai_linters", LinterTable(ai));

        yield return ("parse-dl-uri", "dl.c test_dl_parseDLuri", Cases("parse-dl-uri", "dl.c test_parseDLuri", CSource.Calls(dl, "test_parseDLuri"),
            a => (Gs1InputKind.DigitalLink, CSource.Literal(a[1]), new Gs1ExpectedVerdict(a[0] == "true", a[0] == "true" ? CSource.Literal(a[2]) : null))));

        yield return ("generate-dl-uri", "dl.c test_dl_generateDLuri", Cases("generate-dl-uri", "dl.c test_testGenerateDLuri", CSource.Calls(dl, "test_testGenerateDLuri"),
            a => (Gs1InputKind.Bracketed, CSource.Literal(a[2]), new Gs1ExpectedVerdict(a[0] == "true", a[0] == "true" ? CSource.Literal(a[3]) : null)),
            a => a[1] == "NULL" ? "stem: canonical" : $"stem: {CSource.Literal(a[1])}"));

        yield return ("scan-data", "scandata.c test_scandata_processScanData", Cases("scan-data", "scandata.c test_testProcessScanData", CSource.Calls(scan, "test_testProcessScanData"),
            a => (Gs1InputKind.ScanData, CSource.Literal(a[1]), new Gs1ExpectedVerdict(a[0] == "true", a[0] == "true" ? CSource.Literal(a[3]) : null)),
            a => $"symbology: {a[2]}"));
    }

    private static Gs1CaseInput[] Cases(string group, string source, IEnumerable<(string[] Args, string Comment)> calls, Func<string[], (Gs1InputKind Kind, string Input, Gs1ExpectedVerdict Expected)> map, Func<string[], string>? extraNote = null)
    {
        var list = new List<Gs1CaseInput>();
        var index = 0;
        foreach (var (args, comment) in calls)
        {
            Gs1InputKind kind;
            string input;
            Gs1ExpectedVerdict expected;
            try
            {
                (kind, input, expected) = map(args);
            }
            catch (InvalidDataException)
            {
                // A test that builds its input in a buffer (the 90-character boundary) rather than a literal.
                continue;
            }
            // The scan-data vectors with a raw byte in the input are not one line of console input.
            if (input.Contains('\n') || input.Contains('\r'))
                continue;
            var notes = new[] { extraNote?.Invoke(args), comment.Length == 0 ? null : comment }.Where(n => n is not null);
            var note = string.Join("; ", notes);
            list.Add(new Gs1CaseInput($"{group}-{index:D3}", "syntax-engine/" + group, source, kind, input, note.Length == 0 ? null : note, expected));
            index++;
        }
        return list.ToArray();
    }

    /// <summary>The <c>test_ai_linters</c> table: one bracketed input per linter error the engine can raise.</summary>
    private static Gs1CaseInput[] LinterTable(string aiSource)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(aiSource, @"\{\s*(""(?:[^""\\]|\\.)*""),\s*(GS1_LINTER_[A-Z0-9_]+)\s*\}");
        var index = 0;
        return matches.Select(m => new Gs1CaseInput($"linters-{index++:D3}", "syntax-engine/linters", "ai.c test_ai_linters", Gs1InputKind.Bracketed, CSource.Literal(m.Groups[1].Value),
            Note: m.Groups[2].Value, Expected: new Gs1ExpectedVerdict(m.Groups[2].Value == "GS1_LINTER_OK", null))).ToArray();
    }
}
