#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

// Fetches the GS1 data oracles used by `regenerate-gs1` into tools/QRInteropFixtures/external/, each pinned
// so a re-tagged release cannot silently change the oracle:
//
//  - The GS1 Barcode Syntax Engine console app (prebuilt Windows release asset), pinned by version and
//    SHA-256. It is the reference implementation of the Syntax Dictionary:
//    https://github.com/gs1/gs1-syntax-engine (Apache-2.0)
//  - The Syntax Engine source at the same release tag, pinned by commit, for the unit-test vectors that
//    `regenerate-gs1` imports (src/c-lib/ai.c, dl.c, scandata.c).
//  - The GS1 Barcode Syntax Dictionary at the release this library pins, pinned by commit:
//    https://github.com/gs1/gs1-syntax-dictionary (Apache-2.0). Its dictionary file and the linters whose
//    lists are not in the dictionary (ISO 3166, ISO 4217, media and package types) are copied into the
//    test fixtures by `import-gs1-syntax-dictionary`, and the linter unit tests are imported as fixtures
//    by `regenerate-gs1`.
//
//   dotnet run tools/get_gs1_syntax_engine.cs
//
// A part fetched before is checked against its pin instead of fetched again. The console app is a Windows
// build, so on other systems it is fetched and checked but not started.
//
// Moving to a new dictionary release, one pull request a release (the cadence, what a release can
// change and how the packages ship it are in qrcode-gs1-data.md, "The dictionary and its updates", and
// .github/workflows/gs1-dictionary.yaml opens an issue for each release):
//
//  1. Set the release and its commit here and in Gs1FixtureGenerator (DictionaryRelease and
//     DictionaryCommit in both), run this app, then
//     `dotnet run --project tools/QRInteropFixtures -- import-gs1-syntax-dictionary`. It copies the
//     dictionary, its LICENSE and the seven list linters (lint_*.c) into the test fixtures and writes
//     PROVENANCE.md with each file's SHA-256.
//  2. Run Gs1AiTableGeneratorTest outside CI, which rewrites the generated table and code lists (in
//     CI it fails on drift). A new or renamed linter makes it refuse the release: implement the
//     linter in Gs1Linters with its Gs1Linter member, held to its vectors in Gs1LinterTest. A change
//     to the dictionary file's syntax goes into Gs1SyntaxDictionary.cs.
//  3. Run `regenerate-gs1`, which rebuilds the corpus against the oracles and imports the linter
//     unit tests, and read its disagreement report by cause. A new disagreement is a pinned oracle
//     falling behind, recorded in qrcode-test-fixtures.md, or a defect, fixed before the pull
//     request. The console app loads the new dictionary through --syndict, but a release that adds
//     a linter needs the engine release that implements it.
//  4. Update the counts the tests pin (Gs1AiTableTest: entries and AIs, Gs1FixtureTest: per-AI cases
//     and linters), THIRD-PARTY-NOTICES.md and the release qrcode-gs1-data.md names, and run the
//     full suite on both frameworks.
//  5. The pull request lists the release's GSCNs, the AIs, flags, linters and code lists that
//     changed, and the fixtures that moved.

const string EngineVersion = "1.4.1";
const string EngineCommit = "ec595ff68364dd0056abd3b89d087ff5fbc9bf8e";
const string EngineAsset = "gs1encoders-windows-console-app.zip";
const string EngineAssetSha256 = "2c0383bd2ca82825c8c40c9c6cce2efe4583e3d31d7c51e08a7f88ed6aade68e";

const string DictionaryRelease = "2026-01-27";
const string DictionaryCommit = "8ad003f83a4ba6c2179ce15d578fae27d6580909";

try
{
    var externalDir = Path.Combine(ToolsDirectory(), "QRInteropFixtures", "external");
    var exe = await FetchEngine(Path.Combine(externalDir, "gs1-syntax-engine"));
    PinnedClone(Path.Combine(externalDir, "gs1-syntax-engine-src"), "https://github.com/gs1/gs1-syntax-engine.git", EngineVersion, EngineCommit);
    PinnedClone(Path.Combine(externalDir, "gs1-syntax-dictionary"), "https://github.com/gs1/gs1-syntax-dictionary.git", DictionaryRelease, DictionaryCommit);
    Console.WriteLine($"GS1 Syntax Engine {EngineVersion} ready at {exe}");
    return 0;
}
catch (Exception e) when (e is InvalidOperationException or HttpRequestException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static string ToolsDirectory()
    => AppContext.GetData("EntryPointFileDirectoryPath") as string
        ?? throw new InvalidOperationException("Run this file as a file-based app: dotnet run tools/get_gs1_syntax_engine.cs");

static async Task<string> FetchEngine(string engineDir)
{
    Directory.CreateDirectory(engineDir);
    var zipPath = Path.Combine(engineDir, EngineAsset);
    if (!File.Exists(zipPath))
    {
        var url = $"https://github.com/gs1/gs1-syntax-engine/releases/download/{EngineVersion}/{EngineAsset}";
        Console.WriteLine($"downloading {url}");
        using var http = new HttpClient();
        await File.WriteAllBytesAsync(zipPath, await http.GetByteArrayAsync(url));
    }

    var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zipPath)));
    if (actual != EngineAssetSha256)
    {
        File.Delete(zipPath);
        throw new InvalidOperationException($"SHA-256 mismatch for {EngineAsset}: expected {EngineAssetSha256}, got {actual}. Download removed.");
    }

    ZipFile.ExtractToDirectory(zipPath, engineDir, overwriteFiles: true);
    var exe = Path.Combine(engineDir, "gs1encoders-cpp-app.exe");
    if (OperatingSystem.IsWindows())
    {
        // Started anywhere else, the app warns that it cannot read the dictionary file the zip puts beside it.
        Run(exe, engineDir, capture: false, "--version");
    }
    else
    {
        Console.WriteLine($"{Path.GetFileName(exe)} is a Windows build, not started here");
    }
    return exe;
}

static void PinnedClone(string dir, string repo, string tag, string commit)
{
    var name = Path.GetFileName(dir);
    if (!Path.Exists(Path.Combine(dir, ".git")))
    {
        Console.WriteLine($"cloning {repo} at {tag}");
        // The files copied into the fixtures keep the repository's bytes, so their SHA-256 in PROVENANCE.md is the same on every machine.
        Run("git", null, capture: false, "-c", "core.autocrlf=false", "-c", "advice.detachedHead=false", "clone", "--quiet", "--depth", "1", "--branch", tag, repo, dir);
    }
    var head = Run("git", null, capture: true, "-C", dir, "rev-parse", "HEAD").Trim();
    if (head != commit)
    {
        throw new InvalidOperationException($"{name} at tag {tag} is commit {head}, expected {commit}. Delete {dir} and re-run, or re-pin.");
    }
    Console.WriteLine($"{name} {tag} ({head}) ready at {dir}");
}

static string Run(string fileName, string? workingDirectory, bool capture, params string[] arguments)
{
    var start = new ProcessStartInfo(fileName) { RedirectStandardOutput = capture, UseShellExecute = false };
    if (workingDirectory is not null)
    {
        start.WorkingDirectory = workingDirectory;
    }
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start.");
    var output = capture ? process.StandardOutput.ReadToEnd() : "";
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"{Path.GetFileName(fileName)} {string.Join(' ', arguments)} exited with {process.ExitCode}.");
    }
    return output;
}
