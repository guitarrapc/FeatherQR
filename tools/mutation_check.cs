#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

// Plants faults in the library one at a time and records which tests catch each: how much data a
// heavy test needs, and whether a test catches anything the rest of the suite misses.
//
//   dotnet run tools/mutation_check.cs -- validate <mutants.tsv>
//   dotnet run tools/mutation_check.cs -- run <mutants.tsv> <out-dir> [--filter <treenode-filter>]... [--tfm net10.0] [--only <id>] [--timeout <minutes>]
//   dotnet run tools/mutation_check.cs -- report <out-dir>
//   dotnet run tools/mutation_check.cs -- evaluate <out-dir> --keep <[Class.]Method>=<regex>...
//   dotnet run tools/mutation_check.cs -- compare <before-dir> <after-dir>
//   dotnet run tools/mutation_check.cs -- helper <path-in-the-test-project>
//
// A mutants file is tab-separated, one edit a line: id, path from the repository root, the text to
// find and the text to put in its place, both literal. The text must occur exactly once in the file;
// a '␤' in either stands for a line break, written as the file writes its own, so a line repeated
// elsewhere in the file is found with the line beside it that is not.
// Lines sharing an id are one fault, applied together; '#' starts a comment. A fault is small and of
// the kind the test exists to catch: a bound one step too tight, `<` for `<=`, a constant off by one.
//
//   A5	src/FeatherQR/Internals/ImageDecoders/FinderPatternFinder.RunWalk.cs	Math.Min(3 * totalLow / 14 + 1,	Math.Min((3 * totalLow + 1) / 14 + 1,
//
// `run` builds the test project in Release (a Debug.Assert would take the test host down instead of
// failing a test) and runs the suite once unchanged, which must catch nothing, then once a fault: the
// fault written into src, built, run, and the file put back from memory. The files a fault touches must
// be clean in git, so that `git checkout -- <file>` is a way back should the run be killed. Each run
// leaves <id>.json (the failed test cases and the logged sub-cases), the TRX and the logs in <out-dir>.
//
// A test that walks many sub-cases (seeds, versions, budgets) stops at its first failure, which says
// nothing about the others. To see each, write the helper into the test project with `helper` and wrap
// each sub-case of the test for the time of the experiment:
//
//   await MutationLog.Try($"v{version} seed {seed}", async () => { ...the sub-case's asserts... });
//
// Outside `run` the wrapper only awaits the check. Under `run` a failure is logged under the class, the
// method and the key instead of failing the test, and the walk goes on. Delete the helper and the
// wrappers afterwards.
//
// `report` lists what caught each fault, the faults no test caught, and those only one method caught.
// `evaluate` asks what a smaller data set would still catch: a --keep keeps only the sub-cases or test
// cases of that method whose key or arguments match the regex, and the answer is the faults the method,
// its class and the suite would stop catching, and the fewest catches any fault is left with. After
// reducing a test for real, remove the wrappers, `run` the same faults into a second directory and
// `compare` the two: a fault a method, a class or the suite caught before and no longer does is lost.
//
// What this does not do: it knows only the faults it is given, so an axis that carries meaning (a count
// indicator band, a surrogate edge) is worth keeping even where no planted fault needs it; and it runs
// one target framework on one machine, so code that only another architecture runs is not measured.

const string LogVariable = "MUTATION_LOG";
const string Baseline = "BASE";

if (args.Length == 0)
    return Usage();

try
{
    return args[0] switch
    {
        "validate" when args.Length == 2 => Validate(args[1]),
        "run" when args.Length >= 3 => Run(args[1], args[2], args[3..]),
        "report" when args.Length == 2 => Report(args[1]),
        "evaluate" when args.Length >= 4 => Evaluate(args[1], args[2..]),
        "compare" when args.Length == 3 => Compare(args[1], args[2]),
        "helper" when args.Length == 2 => Helper(args[1]),
        _ => Usage(),
    };
}
catch (ToolException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("""
        usage: mutation_check validate <mutants.tsv>
               mutation_check run <mutants.tsv> <out-dir> [--filter <treenode-filter>]... [--tfm net10.0] [--only <id>] [--timeout <minutes>]
               mutation_check report <out-dir>
               mutation_check evaluate <out-dir> --keep <[Class.]Method>=<regex>...
               mutation_check compare <before-dir> <after-dir>
               mutation_check helper <path-in-the-test-project>
        """);
    return 2;
}

// ---- validate -----------------------------------------------------------------------------------

static int Validate(string mutantsPath)
{
    var failed = false;
    foreach (var mutant in LoadMutants(mutantsPath))
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var edit in mutant.Edits)
        {
            if (!File.Exists(edit.Path))
            {
                problems.Add($"line {edit.Line}: no file {edit.Path}");
                continue;
            }
            var text = texts.TryGetValue(edit.Path, out var edited) ? edited : TextFile.Decode(File.ReadAllBytes(edit.Path)).Text;
            var count = TextFile.Occurrences(text, edit.Find);
            if (count != 1)
            {
                problems.Add($"line {edit.Line}: found {count} times in {edit.Path}");
                continue;
            }
            texts[edit.Path] = TextFile.Replace(text, edit.Find, edit.Replace);
        }
        failed |= problems.Count > 0;
        Console.WriteLine(problems.Count == 0 ? $"{mutant.Id,-10} ok ({mutant.Edits.Count} edit{(mutant.Edits.Count == 1 ? "" : "s")})" : $"{mutant.Id,-10} {string.Join("; ", problems)}");
    }
    return failed ? 1 : 0;
}

// ---- run ----------------------------------------------------------------------------------------

static int Run(string mutantsPath, string outDir, string[] options)
{
    var filters = new List<string>();
    var tfm = "net10.0";
    var project = "tests/FeatherQR.Tests/FeatherQR.Tests.csproj";
    string? only = null;
    var timeout = TimeSpan.FromMinutes(15);
    var allowDirty = false;
    for (var i = 0; i < options.Length; i++)
    {
        switch (options[i])
        {
            case "--filter" when i + 1 < options.Length: filters.Add(options[++i]); break;
            case "--tfm" when i + 1 < options.Length: tfm = options[++i]; break;
            case "--project" when i + 1 < options.Length: project = options[++i]; break;
            case "--only" when i + 1 < options.Length: only = options[++i]; break;
            case "--timeout" when i + 1 < options.Length: timeout = TimeSpan.FromMinutes(double.Parse(options[++i])); break;
            case "--allow-dirty": allowDirty = true; break;
            default: throw new ToolException($"unknown option {options[i]}");
        }
    }
    if (!File.Exists(project))
        throw new ToolException($"no test project at {project}; run from the repository root");

    var mutants = LoadMutants(mutantsPath);
    if (only is not null)
        mutants = mutants.Where(m => m.Id == only).ToList();
    if (mutants.Count == 0)
        throw new ToolException("no fault to run");
    if (Validate(mutantsPath) != 0)
        throw new ToolException("fix the mutants file first");

    var targets = mutants.SelectMany(m => m.Edits).Select(e => e.Path).Distinct().ToArray();
    var dirty = Git(["status", "--porcelain", "--", .. targets]);
    if (dirty.Length > 0 && !allowDirty)
        throw new ToolException($"files a fault touches have changes of their own, so git checkout would not restore them (--allow-dirty to go on):\n{dirty}");

    outDir = Path.GetFullPath(outDir);
    Directory.CreateDirectory(outDir);
    var projectDir = Path.GetDirectoryName(Path.GetFullPath(project))!;
    var dll = Path.Combine(projectDir, "bin", "Release", tfm, Path.GetFileNameWithoutExtension(project) + ".dll");

    Console.CancelKeyPress += (_, _) => Pending.Restore();
    var index = 0;
    var sequence = only is null ? mutants.Prepend(new Mutant(Baseline, [])).ToList() : mutants;
    foreach (var mutant in sequence)
    {
        var result = RunOne(mutant, index++, project, tfm, dll, filters, outDir, timeout);
        File.WriteAllText(Path.Combine(outDir, mutant.Id + ".json"), JsonSerializer.Serialize(result, ResultJson.Default.Result));
        var methods = result.Catches.Select(c => $"{c.Class}.{c.Method}").Distinct().Count();
        var status = !result.Built ? "BUILD FAILED" : result.TimedOut ? "TIMED OUT" : result.Catches.Count == 0 ? "not caught" : $"caught by {methods} method{(methods == 1 ? "" : "s")}";
        Console.WriteLine($"{mutant.Id,-10} {status,-24} tests {result.Total}, failed {result.Failed}, logged {result.Catches.Count(c => c.Source == "log")}");
        if (mutant.Id == Baseline && (!result.Built || result.Catches.Count > 0))
            throw new ToolException("the unchanged suite must build and catch nothing; see the logs in " + outDir);
    }
    return 0;
}

static Result RunOne(Mutant mutant, int index, string project, string tfm, string dll, List<string> filters, string outDir, TimeSpan timeout)
{
    try
    {
        Pending.Apply(mutant);
        var built = Exec("dotnet", ["build", project, "-c", "Release", "-f", tfm, "--nologo"], ".", Path.Combine(outDir, mutant.Id + ".build.log"), null, timeout, out _) == 0;
        if (!built)
            return new Result(mutant.Id, index, false, false, 0, 0, []);

        var log = Path.Combine(outDir, mutant.Id + ".kills.txt");
        File.Delete(log);
        var catches = new List<Catch>();
        int total = 0, failed = 0;
        var timedOut = false;
        List<string?> runs = filters.Count == 0 ? [null] : [.. filters];
        for (var i = 0; i < runs.Count; i++)
        {
            var trx = $"{mutant.Id}.{i}.trx";
            List<string> arguments = [dll, "--no-progress", "--report-trx", "--report-trx-filename", trx, "--results-directory", outDir];
            if (runs[i] is { } filter)
                arguments.AddRange(["--treenode-filter", filter]);
            Exec("dotnet", arguments, Path.GetDirectoryName(dll)!, Path.Combine(outDir, $"{mutant.Id}.{i}.log"), new() { [LogVariable] = log }, timeout, out var over);
            timedOut |= over;
            var trxPath = Path.Combine(outDir, trx);
            if (File.Exists(trxPath))
            {
                var (t, f, c) = ReadTrx(trxPath);
                total += t;
                failed += f;
                catches.AddRange(c);
            }
        }
        if (File.Exists(log))
        {
            foreach (var line in File.ReadAllLines(log))
            {
                var parts = line.Split(' ', 4);
                if (parts.Length >= 3 && parts[0] == "KILL")
                    catches.Add(new Catch(parts[1], parts[2], parts.Length == 4 ? parts[3] : "", "log"));
            }
        }
        return new Result(mutant.Id, index, true, timedOut, total, failed, catches);
    }
    finally
    {
        Pending.Restore();
    }
}

static (int Total, int Failed, List<Catch> Catches) ReadTrx(string path)
{
    XNamespace t = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    var doc = XDocument.Load(path);
    var classOf = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var test in doc.Descendants(t + "UnitTest"))
    {
        var className = (string?)test.Element(t + "TestMethod")?.Attribute("className") ?? "?";
        classOf.TryAdd((string)test.Attribute("id")!, className[(className.LastIndexOf('.') + 1)..]);
    }
    var catches = new List<Catch>();
    foreach (var result in doc.Descendants(t + "UnitTestResult"))
    {
        if ((string?)result.Attribute("outcome") != "Failed")
            continue;
        var name = (string)result.Attribute("testName")!;
        var open = name.IndexOf('(');
        var method = open < 0 ? name : name[..open];
        var arguments = open < 0 ? "" : name[(open + 1)..].TrimEnd(')');
        catches.Add(new Catch(classOf.GetValueOrDefault((string)result.Attribute("testId")!, "?"), method, arguments, "test"));
    }
    var counters = doc.Descendants(t + "Counters").FirstOrDefault();
    return ((int?)counters?.Attribute("total") ?? 0, (int?)counters?.Attribute("failed") ?? 0, catches);
}

// ---- report, evaluate, compare --------------------------------------------------------------------

static int Report(string dir)
{
    var results = LoadResults(dir).Where(r => r.Id != Baseline).ToList();
    var caughtBy = results.ToDictionary(r => r.Id, r => r.Catches.GroupBy(MethodOf).ToDictionary(g => g.Key, g => g.Count()));
    foreach (var r in results)
    {
        var methods = caughtBy[r.Id];
        var what = !r.Built ? "build failed" : methods.Count == 0 ? "not caught" : string.Join(", ", methods.OrderByDescending(m => m.Value).Select(m => $"{m.Key} ({m.Value})"));
        Console.WriteLine($"{r.Id,-10} {what}");
    }
    Console.WriteLine();
    Console.WriteLine($"Not caught: {Join(results.Where(r => r.Built && caughtBy[r.Id].Count == 0).Select(r => r.Id))}");
    Console.WriteLine($"Caught by one method only: {Join(results.Where(r => caughtBy[r.Id].Count == 1).Select(r => $"{r.Id} ({caughtBy[r.Id].Keys.Single()})"))}");
    Console.WriteLine();
    Console.WriteLine("Per method: faults caught, and caught by no other method");
    foreach (var method in caughtBy.Values.SelectMany(m => m.Keys).Distinct().Order())
    {
        var catching = results.Where(r => caughtBy[r.Id].ContainsKey(method)).ToList();
        Console.WriteLine($"  {method}: {catching.Count}, alone {catching.Count(r => caughtBy[r.Id].Count == 1)}");
    }
    return 0;
}

static int Evaluate(string dir, string[] options)
{
    var keeps = new List<(string Method, Regex Pattern)>();
    for (var i = 0; i < options.Length; i++)
    {
        if (options[i] != "--keep" || i + 1 >= options.Length)
            throw new ToolException("expected --keep <[Class.]Method>=<regex>");
        var spec = options[++i];
        var eq = spec.IndexOf('=');
        if (eq <= 0)
            throw new ToolException($"expected <[Class.]Method>=<regex>, not {spec}");
        keeps.Add((spec[..eq], new Regex(spec[(eq + 1)..])));
    }
    var results = LoadResults(dir).Where(r => r.Id != Baseline).ToList();
    bool Kept(Catch c) => keeps.Where(k => Matches(c, k.Method)).All(k => k.Pattern.IsMatch(c.Detail));

    foreach (var (method, pattern) in keeps)
    {
        var before = results.Where(r => r.Catches.Any(c => Matches(c, method))).ToList();
        var left = before.Select(r => (r.Id, Count: r.Catches.Count(c => Matches(c, method) && Kept(c)))).ToList();
        var lost = left.Where(l => l.Count == 0).Select(l => l.Id);
        var fewest = left.Where(l => l.Count > 0).OrderBy(l => l.Count).FirstOrDefault();
        Console.WriteLine($"{method} keeping /{pattern}/: {before.Count} -> {before.Count - lost.Count()} faults, lost: {Join(lost)}{(fewest.Id is null ? "" : $", fewest catches left: {fewest.Count} ({fewest.Id})")}");
    }
    foreach (var cls in results.SelectMany(r => r.Catches).Where(c => keeps.Any(k => Matches(c, k.Method))).Select(c => c.Class).Distinct().Order())
    {
        var before = results.Where(r => r.Catches.Any(c => c.Class == cls)).Select(r => r.Id).ToList();
        var after = results.Where(r => r.Catches.Any(c => c.Class == cls && Kept(c))).Select(r => r.Id).ToHashSet();
        Console.WriteLine($"class {cls}: {before.Count} -> {after.Count} faults, lost: {Join(before.Where(id => !after.Contains(id)))}");
    }
    var suiteBefore = results.Where(r => r.Catches.Count > 0).Select(r => r.Id).ToList();
    var suiteAfter = results.Where(r => r.Catches.Any(Kept)).Select(r => r.Id).ToHashSet();
    Console.WriteLine($"suite: {suiteBefore.Count} -> {suiteAfter.Count} faults, lost: {Join(suiteBefore.Where(id => !suiteAfter.Contains(id)))}");
    return 0;
}

static int Compare(string beforeDir, string afterDir)
{
    var before = LoadResults(beforeDir).Where(r => r.Id != Baseline).ToDictionary(r => r.Id);
    var after = LoadResults(afterDir).Where(r => r.Id != Baseline).ToDictionary(r => r.Id);
    var ids = before.Keys.Where(after.ContainsKey).Order().ToList();
    var missing = before.Keys.Except(after.Keys).Concat(after.Keys.Except(before.Keys)).ToList();
    if (missing.Count > 0)
        Console.WriteLine($"Run in only one of the two: {Join(missing)}");

    var anyLost = false;
    void Level(string title, Func<Catch, string> key)
    {
        Console.WriteLine(title);
        var keys = ids.SelectMany(id => before[id].Catches.Concat(after[id].Catches)).Select(key).Distinct().Order();
        foreach (var k in keys)
        {
            var b = ids.Where(id => before[id].Catches.Any(c => key(c) == k)).ToList();
            var a = ids.Where(id => after[id].Catches.Any(c => key(c) == k)).ToHashSet();
            var lost = b.Where(id => !a.Contains(id)).ToList();
            anyLost |= lost.Count > 0;
            Console.WriteLine($"  {k}: {b.Count} -> {a.Count}{(lost.Count > 0 ? $", lost: {Join(lost)}" : "")}");
        }
    }
    Level("Per method:", MethodOf);
    Level("Per class:", c => c.Class);
    var suiteLost = ids.Where(id => before[id].Catches.Count > 0 && after[id].Catches.Count == 0).ToList();
    anyLost |= suiteLost.Count > 0;
    Console.WriteLine($"Suite: {ids.Count(id => before[id].Catches.Count > 0)} -> {ids.Count(id => after[id].Catches.Count > 0)}, lost: {Join(suiteLost)}");
    return anyLost ? 1 : 0;
}

// ---- helper -------------------------------------------------------------------------------------

static int Helper(string path)
{
    if (File.Exists(path))
        throw new ToolException($"{path} exists already");
    File.WriteAllText(path, $$"""
        using System.Runtime.CompilerServices;

        namespace FeatherQR.Tests;

        // TEMPORARY, written by tools/mutation_check.cs for the time of an experiment: delete it and its wrappers afterwards.
        internal static class MutationLog
        {
            private static readonly object Gate = new();
            private static readonly string? LogPath = Environment.GetEnvironmentVariable("{{LogVariable}}");

            /// <summary>Awaits the check; under mutation_check a failure is logged under the key instead of failing the test, so every sub-case reports.</summary>
            public static async Task Try(string key, Func<Task> check, [CallerFilePath] string file = "", [CallerMemberName] string method = "")
            {
                if (LogPath is null)
                {
                    await check();
                    return;
                }
                try
                {
                    await check();
                }
                catch (Exception)
                {
                    Caught(key, file, method);
                }
            }

            /// <summary>Logs a sub-case as caught, for a check that counts instead of throwing (the first trial that finds a violation, say).</summary>
            public static void Caught(string key, [CallerFilePath] string file = "", [CallerMemberName] string method = "")
            {
                if (LogPath is null)
                    return;
                lock (Gate)
                    File.AppendAllText(LogPath, $"KILL {Path.GetFileNameWithoutExtension(file)} {method} {key.Replace('\n', ' ')}{Environment.NewLine}");
            }
        }

        """);
    Console.WriteLine($"Wrote {path}. Wrap each sub-case in MutationLog.Try(key, ...), and delete the file and the wrappers after the experiment.");
    return 0;
}

// ---- shared -------------------------------------------------------------------------------------

static List<Mutant> LoadMutants(string path)
{
    var edits = new List<(string Id, Edit Edit)>();
    var lineNumber = 0;
    foreach (var line in File.ReadLines(path))
    {
        lineNumber++;
        if (line.Length == 0 || line.StartsWith('#'))
            continue;
        var parts = line.Split('\t');
        if (parts.Length != 4 || parts[0].Length == 0 || parts[2].Length == 0)
            throw new ToolException($"{path}:{lineNumber}: expected id, path, find and replace, separated by tabs");
        if (parts[0] == Baseline)
            throw new ToolException($"{path}:{lineNumber}: {Baseline} is the unchanged run's id");
        edits.Add((parts[0], new Edit(parts[1].Replace('\\', '/'), parts[2], parts[3], lineNumber)));
    }
    return edits.GroupBy(e => e.Id).Select(g => new Mutant(g.Key, g.Select(e => e.Edit).ToList())).ToList();
}

static List<Result> LoadResults(string dir)
    => Directory.GetFiles(dir, "*.json").Select(f => JsonSerializer.Deserialize(File.ReadAllText(f), ResultJson.Default.Result)!).OrderBy(r => r.Index).ToList();

static string MethodOf(Catch c) => $"{c.Class}.{c.Method}";

static bool Matches(Catch c, string method) => method.Contains('.') ? MethodOf(c) == method : c.Method == method;

static string Join(IEnumerable<string> items) => items.Any() ? string.Join(", ", items) : "-";

static string Git(string[] arguments)
{
    var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var a in arguments)
        psi.ArgumentList.Add(a);
    using var process = Process.Start(psi)!;
    var output = process.StandardOutput.ReadToEnd().Trim();
    process.WaitForExit();
    return output;
}

static int Exec(string file, IEnumerable<string> arguments, string workingDirectory, string logPath, Dictionary<string, string>? environment, TimeSpan timeout, out bool timedOut)
{
    var psi = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in arguments)
        psi.ArgumentList.Add(a);
    if (environment is not null)
    {
        foreach (var (name, value) in environment)
            psi.Environment[name] = value;
    }
    using var log = new StreamWriter(logPath);
    using var process = new Process { StartInfo = psi };
    process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
    process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
    process.Start();
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    timedOut = !process.WaitForExit(timeout);
    if (timedOut)
        process.Kill(entireProcessTree: true);
    process.WaitForExit();
    return timedOut ? -1 : process.ExitCode;
}

sealed record Edit(string Path, string Find, string Replace, int Line);

sealed record Mutant(string Id, List<Edit> Edits);

sealed record Catch(string Class, string Method, string Detail, string Source);

sealed record Result(string Id, int Index, bool Built, bool TimedOut, int Total, int Failed, List<Catch> Catches);

sealed class ToolException(string message) : Exception(message);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Result))]
partial class ResultJson : JsonSerializerContext;

/// <summary>The files of the fault being run, as they were: written back after the run, and on Ctrl+C.</summary>
static class Pending
{
    private static readonly Lock Gate = new();
    private static Dictionary<string, byte[]> originals = [];

    public static void Apply(Mutant mutant)
    {
        lock (Gate)
        {
            foreach (var group in mutant.Edits.GroupBy(e => e.Path))
            {
                var bytes = File.ReadAllBytes(group.Key);
                originals[group.Key] = bytes;
                var (text, bom) = TextFile.Decode(bytes);
                foreach (var edit in group)
                    text = TextFile.Replace(text, edit.Find, edit.Replace);
                File.WriteAllBytes(group.Key, TextFile.Encode(text, bom));
            }
        }
    }

    public static void Restore()
    {
        lock (Gate)
        {
            foreach (var (path, bytes) in originals)
                File.WriteAllBytes(path, bytes);
            originals = [];
        }
    }
}

/// <summary>Source files as UTF-8 text, their byte order mark and line endings kept as they were.</summary>
static class TextFile
{
    public static (string Text, bool Bom) Decode(byte[] bytes)
    {
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return (new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }

    public static byte[] Encode(string text, bool bom)
    {
        var encoded = new UTF8Encoding(false).GetBytes(text);
        return bom ? [0xEF, 0xBB, 0xBF, .. encoded] : encoded;
    }

    public static int Occurrences(string text, string find)
    {
        find = WithLineBreaks(find, text);
        var count = 0;
        for (var at = text.IndexOf(find, StringComparison.Ordinal); at >= 0; at = text.IndexOf(find, at + find.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    public static string Replace(string text, string find, string replace)
    {
        find = WithLineBreaks(find, text);
        var at = text.IndexOf(find, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, at), WithLineBreaks(replace, text), text.AsSpan(at + find.Length));
    }

    // A '␤' is a line break in the file's own line ending
    private static string WithLineBreaks(string value, string text)
        => value.Contains('␤') ? value.Replace("␤", text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n", StringComparison.Ordinal) : value;
}
