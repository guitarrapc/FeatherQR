#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

// Fetches the pinned qrtool prebuilt release binary for this system into tools/QRInteropFixtures/external/qrtool/
// and checks it against the pinned SHA-256 before extracting it. qrtool is the second external encoder lineage
// of the Micro QR and rMQR fixtures, and the image decode sweep encodes with it too.
//
// qrtool is a Rust CLI building on the qrcode crate: https://github.com/sorairolake/qrtool
// The checksums below are taken from the release's official sha256sums.txt and re-pinned here so a re-tagged
// release cannot silently change the oracle.
//
//   dotnet run tools/get_qrtool.cs
//
// Windows takes the x64 build, and Linux on x64 the x64 musl build. On any other system the app stops before
// fetching: there the release's build for it is fetched by hand, as the Regeneration section of
// qrcode-test-fixtures.md says. An archive fetched before is checked against its pin instead of fetched again.

const string Version = "0.13.2";

// One build a system. The folder keeps another system's build if it has one (a checkout shared with WSL), and each
// tool that runs qrtool takes the build named for its own system.
var build = OperatingSystem.IsWindows()
    ? new Build($"qrtool-v{Version}-x86_64-pc-windows-msvc.zip", "c0cad331b96632e74da48f4e9d32e924cc5d3bd2be4258a4a0a39da99202d712", "qrtool.exe")
    : OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64
        ? new Build($"qrtool-v{Version}-x86_64-unknown-linux-musl.tar.gz", "06a45a292a314a650bb8c91d61374b80634ad3899134201273d353dc42cb5002", "qrtool")
        : null;

try
{
    if (build is null)
    {
        throw new InvalidOperationException($"No qrtool build is pinned for {RuntimeInformation.OSDescription} on {RuntimeInformation.OSArchitecture}. Fetch the qrtool {Version} build for it by hand into tools/QRInteropFixtures/external/qrtool and check it against the release's sha256sums.txt.");
    }

    var targetDir = Path.Combine(ToolsDirectory(), "QRInteropFixtures", "external", "qrtool");
    Directory.CreateDirectory(targetDir);
    var archivePath = Path.Combine(targetDir, build.Asset);
    if (!File.Exists(archivePath))
    {
        var url = $"https://github.com/sorairolake/qrtool/releases/download/v{Version}/{build.Asset}";
        Console.WriteLine($"downloading {url}");
        using var http = new HttpClient();
        await File.WriteAllBytesAsync(archivePath, await http.GetByteArrayAsync(url));
    }

    var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archivePath)));
    if (actual != build.Sha256)
    {
        File.Delete(archivePath);
        throw new InvalidOperationException($"SHA-256 mismatch for {build.Asset}: expected {build.Sha256}, got {actual}. Download removed.");
    }

    if (build.Asset.EndsWith(".zip", StringComparison.Ordinal))
    {
        ZipFile.ExtractToDirectory(archivePath, targetDir, overwriteFiles: true);
    }
    else
    {
        using var archive = new GZipStream(File.OpenRead(archivePath), CompressionMode.Decompress);
        TarFile.ExtractToDirectory(archive, targetDir, overwriteFiles: true);
    }
    var exe = Directory.EnumerateFiles(targetDir, build.ExeName, SearchOption.AllDirectories).FirstOrDefault()
        ?? throw new InvalidOperationException($"{build.Asset} holds no {build.ExeName}.");
    Run(exe, "--version");
    Console.WriteLine($"qrtool {Version} ready at {exe}");
    return 0;
}
catch (Exception e) when (e is InvalidOperationException or HttpRequestException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static string ToolsDirectory()
    => AppContext.GetData("EntryPointFileDirectoryPath") as string
        ?? throw new InvalidOperationException("Run this file as a file-based app: dotnet run tools/get_qrtool.cs");

static void Run(string fileName, params string[] arguments)
{
    var start = new ProcessStartInfo(fileName) { UseShellExecute = false };
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start.");
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"{Path.GetFileName(fileName)} {string.Join(' ', arguments)} exited with {process.ExitCode}.");
    }
}

record Build(string Asset, string Sha256, string ExeName);
