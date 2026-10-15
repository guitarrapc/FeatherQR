using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace QRInteropFixtures;

/// <summary>
/// <c>regenerate-gs1</c>: writes the GS1 data fixtures under <c>Fixtures/Gs1/</c>. Every input of the
/// corpus (<see cref="Gs1Corpus"/>) and every unit-test vector imported from the Syntax Engine
/// (<see cref="SyntaxEngineVectors"/>) goes through the pinned oracles, and the fixture records each
/// verdict beside the input: the Syntax Engine console app with the committed dictionary, CodeGlyphX,
/// libzint, and the zxing-cpp reader over a symbol of the data. The Syntax Dictionary's own linter
/// unit tests are imported as they are. At the end it prints where the oracles disagree, for the
/// fixture spec to explain by release or defect.
/// <c>import-gs1-syntax-dictionary</c> copies the pinned dictionary release and its list linters from
/// the clone <c>get-gs1-syntax-engine.ps1</c> makes into the fixtures, with a provenance note.
/// </summary>
public static class Gs1FixtureGenerator
{
    public const string DictionaryRelease = "2026-01-27";
    public const string DictionaryCommit = "8ad003f83a4ba6c2179ce15d578fae27d6580909";

    /// <summary>The linters whose code lists (ISO 3166, ISO 4217, media and package types) are not in the dictionary file.</summary>
    public static readonly string[] ListLinters = ["iso3166", "iso3166alpha2", "iso3166999", "iso4217", "iso5218", "mediatype", "packagetype"];

    public static string Gs1Root(string fixturesBase) => Path.Combine(fixturesBase, "Gs1");

    public static string DictionaryPath(string fixturesBase) => Path.Combine(Gs1Root(fixturesBase), "syntax-dictionary", "gs1-syntax-dictionary.txt");

    private static string ExternalDir(string repoRoot) => Path.Combine(repoRoot, "tools", "QRInteropFixtures", "external");

    public static int Import(string repoRoot, string fixturesBase)
    {
        var clone = Path.Combine(ExternalDir(repoRoot), "gs1-syntax-dictionary");
        if (!Directory.Exists(clone))
        {
            Console.Error.WriteLine($"No dictionary clone at {clone}. Run pwsh tools/QRInteropFixtures/get-gs1-syntax-engine.ps1 first.");
            return 1;
        }
        var target = Path.Combine(Gs1Root(fixturesBase), "syntax-dictionary");
        Directory.CreateDirectory(target);

        var files = new List<(string Name, string Source)> { ("gs1-syntax-dictionary.txt", Path.Combine(clone, "gs1-syntax-dictionary.txt")), ("LICENSE", Path.Combine(clone, "LICENSE")) };
        files.AddRange(ListLinters.Select(l => ($"lint_{l}.c", Path.Combine(clone, "src", $"lint_{l}.c"))));

        var rows = new List<string>();
        foreach (var (name, source) in files)
        {
            var bytes = File.ReadAllBytes(source);
            File.WriteAllBytes(Path.Combine(target, name), bytes);
            rows.Add($"| `{name}` | {bytes.Length} | `{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}` |");
            Console.WriteLine($"copied: {name} ({bytes.Length} bytes)");
        }

        var dictionary = new Gs1SyntaxDictionary(Path.Combine(target, "gs1-syntax-dictionary.txt"));
        if (dictionary.Release != DictionaryRelease)
            throw new InvalidOperationException($"The clone's dictionary is release {dictionary.Release}, the pin is {DictionaryRelease}.");

        var provenance = $$"""
            # Provenance

            The GS1 Barcode Syntax Dictionary, release {{DictionaryRelease}}, copied unmodified from https://github.com/gs1/gs1-syntax-dictionary at tag `{{DictionaryRelease}}` (commit `{{DictionaryCommit}}`), Apache License 2.0 (see LICENSE beside this file; the repository has no NOTICE file). Copyright (c) 2020-2021 BWIPP project, (c) 2020-2021 Zint Project, (c) 2021-2025 GS1 AISBL.

            `gs1-syntax-dictionary.txt` is the AI table this library's GS1 layer is generated from (qrcode-symbologies.md, "GS1 data"). The `lint_*.c` files are the reference linters whose code lists are not in the dictionary file: ISO 3166-1 numeric and alpha-2, ISO 3166-1 numeric or 999, ISO 4217 numeric, ISO/IEC 5218, the AIDC media types of AI (7241) and the package types of AI (7041). The ISO lists and the media types are bit fields indexed by code, the package types a sorted string list, and each file's unit tests enumerate its list. `regenerate-gs1` imports those unit tests into `linters/`. The other linters (check digits, dates, positions) hold rules, not lists, and are not copied.

            Fetched by `pwsh tools/QRInteropFixtures/get-gs1-syntax-engine.ps1` and copied by `dotnet run --project tools/QRInteropFixtures -- import-gs1-syntax-dictionary`. The dictionary's own header says: "Entries are maintained in lexical order of their AI." It holds {{dictionary.Entries.Count}} entry lines covering {{dictionary.Ais.Count()}} AIs and names {{dictionary.Linters.Count}} linters.

            | File | Bytes | SHA-256 |
            |---|---|---|
            {{string.Join("\n", rows)}}

            """;
        File.WriteAllText(Path.Combine(target, "PROVENANCE.md"), provenance.Replace("\r\n", "\n"));
        Console.WriteLine($"wrote: {Path.Combine(target, "PROVENANCE.md")}");
        return 0;
    }

    public static int Regenerate(string repoRoot, string fixturesBase, bool required)
    {
        var dictionaryPath = DictionaryPath(fixturesBase);
        var engine = new SyntaxEngineOracle(repoRoot, dictionaryPath);
        var engineSource = Path.Combine(ExternalDir(repoRoot), "gs1-syntax-engine-src");
        var dictionarySource = Path.Combine(ExternalDir(repoRoot), "gs1-syntax-dictionary", "src");
        if (!engine.IsAvailable || !Directory.Exists(engineSource) || !Directory.Exists(dictionarySource))
        {
            var message = $"GS1 oracles not present (need {engine.ExePath}, {engineSource}, {dictionarySource} and the imported dictionary at {dictionaryPath}). Run pwsh tools/QRInteropFixtures/get-gs1-syntax-engine.ps1 and import-gs1-syntax-dictionary.";
            if (required)
            {
                Console.Error.WriteLine(message);
                return 1;
            }
            Console.WriteLine("skip: " + message);
            return 0;
        }

        var dictionary = new Gs1SyntaxDictionary(dictionaryPath);
        var engineVersion = engine.ReportedVersion();
        var oracles = new Gs1OracleVersions
        {
            SyntaxEngine = new Gs1OracleVersion($"{SyntaxEngineOracle.Version} (the library reports its build date, {engineVersion})", dictionary.Release + " (--syndict)"),
            CodeGlyphX = new Gs1OracleVersion(CodeGlyphXGs1Oracle.Version, CodeGlyphXGs1Oracle.DictionaryRelease),
            Libzint = new Gs1OracleVersion(ZintGs1Oracle.LibzintVersion, "generated from the dictionary's main branch at zint's build (release not recorded)"),
            ZxingCpp = new Gs1OracleVersion(ZintGs1Oracle.ZxingCppVersion, "hand-maintained table citing 2025-12-08"),
        };
        Console.WriteLine($"Syntax Engine {oracles.SyntaxEngine.Version} with dictionary {dictionary.Release}, CodeGlyphX {oracles.CodeGlyphX.Version} ({oracles.CodeGlyphX.Dictionary}), {oracles.Libzint.Version}");

        var root = Gs1Root(fixturesBase);
        foreach (var dir in new[] { "corpus", "syntax-engine", Path.Combine("syntax-dictionary", "linters") })
        {
            var path = Path.Combine(root, dir);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }

        // The dictionary's linter unit tests, as they are.
        var linterVectors = new Dictionary<string, Gs1LinterVector[]>(StringComparer.Ordinal);
        var linterTotal = 0;
        foreach (var (linter, vectors, notImported) in Gs1LinterVectors.Read(dictionarySource))
        {
            linterVectors[linter] = vectors;
            linterTotal += vectors.Length;
            Gs1FixtureWriter.Write(Path.Combine(root, "syntax-dictionary", "linters", linter + ".json"), new Gs1LinterVectorFile
            {
                Linter = linter,
                Source = $"gs1-syntax-dictionary {dictionary.Release} src/lint_{linter}.c unit tests" + (notImported > 0 ? $" ({notImported} tests build their value in code and are not imported)" : ""),
                Cases = vectors,
            });
        }
        Console.WriteLine($"wrote: {linterVectors.Count} linter vector files, {linterTotal} vectors");

        var groups = new List<(string Dir, string File, string Source, Gs1CaseInput[] Inputs)>();
        foreach (var (group, source, inputs) in Gs1Corpus.Build(dictionary, linterVectors))
            groups.Add(("corpus", group, source, inputs));
        foreach (var (file, source, inputs) in SyntaxEngineVectors.Read(engineSource))
            groups.Add(("syntax-engine", file, SyntaxEngineVectors.SourceName + ", " + source, inputs));

        var allInputs = groups.SelectMany(g => g.Inputs).ToArray();
        var duplicate = allInputs.GroupBy(i => i.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate case id {duplicate.Key}.");

        // The engine is a process per input, so those run in parallel. The in-process oracles run serially.
        var engineVerdicts = new ConcurrentDictionary<string, Gs1SyntaxEngineVerdict>();
        Parallel.ForEach(allInputs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) },
            input => engineVerdicts[input.Id] = engine.Run(input.InputKind, input.Input));

        var cases = new List<Gs1FixtureCase>();
        var total = 0;
        foreach (var (dir, file, source, inputs) in groups)
        {
            var fileCases = inputs.Select(input => Judge(input, engineVerdicts[input.Id])).ToArray();
            cases.AddRange(fileCases);
            Gs1FixtureWriter.Write(Path.Combine(root, dir, file + ".json"), new Gs1FixtureFile { Oracles = oracles, Source = source, Cases = fileCases });
            total += fileCases.Length;
            Console.WriteLine($"wrote: Gs1/{dir}/{file}.json ({fileCases.Length} cases, {fileCases.Count(c => c.SyntaxEngine.Valid)} valid for the Syntax Engine)");
        }

        Report(cases);
        Console.WriteLine($"done: {total} GS1 cases and {linterTotal} linter vectors under {root}");
        return 0;
    }

    private static Gs1FixtureCase Judge(Gs1CaseInput input, Gs1SyntaxEngineVerdict engine)
    {
        Gs1LibzintVerdict? libzint = null;
        Gs1ReaderVerdict? reader = null;
        if (input.InputKind == Gs1InputKind.Bracketed)
            (libzint, reader) = ZintGs1Oracle.RunBracketed(input.Input);
        else if (input.InputKind == Gs1InputKind.ElementString)
            reader = ZintGs1Oracle.RunElementString(input.Input);

        return new Gs1FixtureCase
        {
            Id = input.Id,
            Group = input.Group,
            Source = input.Source,
            InputKind = input.InputKind,
            Input = input.Input,
            Note = input.Note,
            Expected = input.Expected,
            SyntaxEngine = engine,
            CodeGlyphX = CodeGlyphXGs1Oracle.Run(input.InputKind, input.Input),
            Libzint = libzint,
            Reader = reader,
        };
    }

    /// <summary>Where the oracles disagree, for the fixture spec: each line is an input and the verdicts that differ.</summary>
    private static void Report(List<Gs1FixtureCase> cases)
    {
        static string Show(string? s) => s is null ? "null" : "\"" + Fnc1Writers.Escape(s) + "\"";
        static string Hri(string[] lines) => string.Concat(lines.Select(l => l.Replace(") ", ")")));

        Section("Imported vectors where the pinned binary disagrees with its source",
            cases.Where(c => c.Expected is not null && c.Expected.Valid != BinaryAccepts(c))
                 .Select(c => $"{c.Id}: source {(c.Expected!.Valid ? "accepts" : "refuses")}, binary {(BinaryAccepts(c) ? "accepts" : "refuses")} {Show(c.Input)} {c.SyntaxEngine.Error ?? c.SyntaxEngine.DigitalLinkError}"));

        Section("Imported vectors where the binary's output differs from the source's",
            cases.Where(c => c.Expected is { Valid: true, Output: not null } && c.SyntaxEngine.Valid && !OutputMatches(c))
                 .Select(c => $"{c.Id}: source {Show(c.Expected!.Output)}, binary {Show(EngineOutput(c))}"));

        Section("CodeGlyphX verdict differs from the Syntax Engine",
            cases.Where(c => c.CodeGlyphX is not null && c.CodeGlyphX.Valid != c.SyntaxEngine.Valid)
                 .Select(c => $"{c.Id}: engine {(c.SyntaxEngine.Valid ? "accepts" : "refuses")} ({c.SyntaxEngine.Error}), CodeGlyphX {(c.CodeGlyphX!.Valid ? "accepts" : "refuses")} ({string.Join(", ", c.CodeGlyphX.Issues)}) {Show(c.Input)}"));

        Section("libzint verdict differs from the Syntax Engine",
            cases.Where(c => c.Libzint is not null && c.Libzint.Valid != c.SyntaxEngine.Valid)
                 .Select(c => $"{c.Id}: engine {(c.SyntaxEngine.Valid ? "accepts" : "refuses")} ({c.SyntaxEngine.Error}), libzint {(c.Libzint!.Valid ? "accepts" : "refuses")} ({c.Libzint.Error}) {Show(c.Input)}"));

        Section("Element string differs between the Syntax Engine and CodeGlyphX (both accept)",
            cases.Where(c => c.SyntaxEngine.Valid && c.CodeGlyphX is { Valid: true } && c.SyntaxEngine.ElementString is not null && c.CodeGlyphX.ElementString != c.SyntaxEngine.ElementString)
                 .Select(c => $"{c.Id}: engine {Show(c.SyntaxEngine.ElementString)}, CodeGlyphX {Show(c.CodeGlyphX!.ElementString)}"));

        Section("Element string differs between the Syntax Engine and libzint's symbol (both accept)",
            cases.Where(c => c.SyntaxEngine.Valid && c.Reader is { Writer: "zint-libzint" } && c.SyntaxEngine.ElementString is not null && c.Reader.ElementString != c.SyntaxEngine.ElementString)
                 .Select(c => $"{c.Id}: engine {Show(c.SyntaxEngine.ElementString)}, libzint {Show(c.Reader!.ElementString)}"));

        Section("zxing-cpp (AI) text differs from the Syntax Engine's HRI (engine accepts)",
            cases.Where(c => c.SyntaxEngine.Valid && c.SyntaxEngine.Hri is not null && c.Reader is not null && c.Reader.Hri != Hri(c.SyntaxEngine.Hri))
                 .Select(c => $"{c.Id}: engine {Show(Hri(c.SyntaxEngine.Hri!))}, zxing-cpp {Show(c.Reader!.Hri)} (writer {c.Reader.Writer})"));

        Section("Digital Link differs between the Syntax Engine and CodeGlyphX (both produce one)",
            cases.Where(c => c.SyntaxEngine.DigitalLink is not null && c.CodeGlyphX?.DigitalLink is not null && c.SyntaxEngine.DigitalLink != c.CodeGlyphX.DigitalLink)
                 .Select(c => $"{c.Id}: engine {Show(c.SyntaxEngine.DigitalLink)}, CodeGlyphX {Show(c.CodeGlyphX!.DigitalLink)}"));

        Section("Digital Link produced by one and refused by the other",
            cases.Where(c => c.CodeGlyphX is not null && (c.SyntaxEngine.DigitalLink is null) != (c.CodeGlyphX.DigitalLink is null) && c.SyntaxEngine.Valid && c.CodeGlyphX.Valid)
                 .Select(c => $"{c.Id}: engine {Show(c.SyntaxEngine.DigitalLink)} ({c.SyntaxEngine.DigitalLinkError}), CodeGlyphX {Show(c.CodeGlyphX!.DigitalLink)} ({c.CodeGlyphX.DigitalLinkError})"));

        Section("Corpus cases meant to be valid (per-ai) that the Syntax Engine refuses",
            cases.Where(c => c.Group == "corpus/per-ai" && !c.SyntaxEngine.Valid).Select(c => $"{c.Id}: {c.SyntaxEngine.Error} {Show(c.Input)}"));

        Section("Linter mutations the Syntax Engine accepts",
            cases.Where(c => c.Group == "corpus/linter-mutations" && c.SyntaxEngine.Valid).Select(c => $"{c.Id}: {Show(c.Input)}"));
    }

    /// <summary>What "accepts" means per vector group: a generated URI for the generation vectors, a valid input otherwise.</summary>
    public static bool BinaryAccepts(Gs1FixtureCase c)
        => c.Group.EndsWith("generate-dl-uri", StringComparison.Ordinal) ? c.SyntaxEngine.DigitalLink is not null : c.SyntaxEngine.Valid;

    /// <summary>The binary's output in the source's notation: the generated URI, or the element string with <c>^</c> for FNC1 and the separator.</summary>
    public static string? EngineOutput(Gs1FixtureCase c)
    {
        if (c.Group.EndsWith("generate-dl-uri", StringComparison.Ordinal))
            return c.SyntaxEngine.DigitalLink;
        if (c.SyntaxEngine.ElementString is not null)
            return "^" + c.SyntaxEngine.ElementString.Replace('\u001D', '^');
        return c.SyntaxEngine.DataString;
    }

    /// <summary>
    /// The source's expected output against the binary's. Generation vectors with a non-canonical stem cannot be
    /// compared, since the console app generates the canonical URI only. The Digital Link parse vectors expect the
    /// element string sorted by AI (<c>gs1_sortAIs</c>), the binary keeps URI order, so those compare sorted.
    /// </summary>
    public static bool OutputMatches(Gs1FixtureCase c)
    {
        var expected = c.Expected!.Output!;
        var output = EngineOutput(c);
        if (output == expected || c.SyntaxEngine.DataString == expected)
            return true;
        if (c.Group.EndsWith("generate-dl-uri", StringComparison.Ordinal))
            return output is not null && !expected.StartsWith("https://id.gs1.org/", StringComparison.Ordinal);
        if (c.InputKind == Gs1InputKind.DigitalLink && output is not null && output.StartsWith("^", StringComparison.Ordinal))
            return SortedElements(output) == SortedElements(expected);
        return false;
    }

    private static string SortedElements(string caret) => string.Join("^", caret.TrimStart('^').Split('^').OrderBy(x => x, StringComparer.Ordinal));

    private static void Section(string title, IEnumerable<string> lines)
    {
        var list = lines.ToList();
        Console.WriteLine();
        Console.WriteLine($"== {title}: {list.Count}");
        foreach (var line in list)
            Console.WriteLine("   " + line);
    }
}
