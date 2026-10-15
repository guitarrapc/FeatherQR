#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

// Fetches the pinned qrtool prebuilt release binary into tools/QRInteropFixtures/external/qrtool/ and checks it
// against the pinned SHA-256 before extracting it. qrtool is the second external encoder lineage of the Micro QR
// and rMQR fixtures, and the image decode sweep encodes with it too.
//
// qrtool is a Rust CLI building on the qrcode crate: https://github.com/sorairolake/qrtool
// The checksum below is taken from the release's official sha256sums.txt and re-pinned here so a re-tagged
// release cannot silently change the oracle.
//
//   dotnet run tools/get_qrtool.cs
//
// A zip fetched before is checked against the pin instead of fetched again. The pinned asset is the Windows
// build, so on other systems the app stops before fetching it: the tools that run qrtool look for its .exe
// first and would fail to start it. There the release's own build is fetched by hand, as the Regeneration
// section of qrcode-test-fixtures.md says.

const string Version = "0.13.2";
const string Asset = $"qrtool-v{Version}-x86_64-pc-windows-msvc.zip";
const string AssetSha256 = "c0cad331b96632e74da48f4e9d32e924cc5d3bd2be4258a4a0a39da99202d712";

try
{
    if (!OperatingSystem.IsWindows())
    {
        throw new InvalidOperationException($"{Asset} is a Windows build. On this system fetch the qrtool {Version} build for it by hand into tools/QRInteropFixtures/external/qrtool and check it against the release's sha256sums.txt.");
    }

    var targetDir = Path.Combine(ToolsDirectory(), "QRInteropFixtures", "external", "qrtool");
    Directory.CreateDirectory(targetDir);
    var zipPath = Path.Combine(targetDir, Asset);
    if (!File.Exists(zipPath))
    {
        var url = $"https://github.com/sorairolake/qrtool/releases/download/v{Version}/{Asset}";
        Console.WriteLine($"downloading {url}");
        using var http = new HttpClient();
        await File.WriteAllBytesAsync(zipPath, await http.GetByteArrayAsync(url));
    }

    var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zipPath)));
    if (actual != AssetSha256)
    {
        File.Delete(zipPath);
        throw new InvalidOperationException($"SHA-256 mismatch for {Asset}: expected {AssetSha256}, got {actual}. Download removed.");
    }

    ZipFile.ExtractToDirectory(zipPath, targetDir, overwriteFiles: true);
    var exe = Directory.EnumerateFiles(targetDir, "qrtool.exe", SearchOption.AllDirectories).FirstOrDefault()
        ?? throw new InvalidOperationException($"{Asset} holds no qrtool.exe.");
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
