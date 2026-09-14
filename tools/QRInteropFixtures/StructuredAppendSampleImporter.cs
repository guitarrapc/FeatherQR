using System.Text;
using System.Text.Json;
using FeatherQR;
using FeatherQR.SkiaSharp;
using SkiaSharp;
using ZXingCpp;

namespace QRInteropFixtures;

/// <summary>
/// Imports a third-party Structured Append capture set as image-only fixtures: the PNGs
/// are copied as they are, every manifest field comes from the zxing-cpp reader, and the
/// set's texts concatenated in index order must equal the expected text file when one
/// sits beside the images. Writes PROVENANCE.md and copies the source LICENSE so the
/// files can be redistributed. Also reports what this library's image decoder makes of
/// each capture, which is the Phase 5a red test in miniature.
/// </summary>
public static class StructuredAppendSampleImporter
{
    public const string LineageName = "zxing-cpp-samples";

    public static int Run(string repoRoot, string sourceRoot, string sourceCommit)
    {
        var sampleDir = Path.Combine(sourceRoot, "test", "samples", "qrcode-7");
        var licensePath = Path.Combine(sourceRoot, "LICENSE");
        if (!Directory.Exists(sampleDir) || !File.Exists(licensePath))
        {
            Console.Error.WriteLine($"expected {sampleDir} and {licensePath}");
            return 1;
        }

        var targetDir = Path.Combine(repoRoot, "tests", "FeatherQR.Tests", "Fixtures", "StandardQrStructuredAppend", LineageName);
        if (Directory.Exists(targetDir))
            Directory.Delete(targetDir, recursive: true);
        Directory.CreateDirectory(targetDir);

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var groups = Directory.EnumerateFiles(sampleDir, "*.png").OrderBy(x => x, StringComparer.Ordinal)
            .GroupBy(p => Path.GetFileNameWithoutExtension(p).Split('-')[0]);

        var provenance = new StringBuilder();
        provenance.AppendLine("# Provenance");
        provenance.AppendLine();
        provenance.AppendLine($"Captures copied unmodified from the zxing-cpp repository, `test/samples/qrcode-7/`, commit `{sourceCommit}`, Apache License 2.0 (see LICENSE beside this file). They are third-party Structured Append symbols this library did not produce; every manifest field is what the pinned zxing-cpp reader reports, and no module matrix exists for them (image path only).");
        provenance.AppendLine();
        provenance.AppendLine("| Fixture | Source file | Index | Count | Parity |");
        provenance.AppendLine("|---|---|---|---|---|");

        foreach (var group in groups)
        {
            var setId = $"sample{group.Key}";
            var texts = new SortedDictionary<int, string>();
            var count = -1;

            foreach (var png in group)
            {
                using var decoded = SKBitmap.Decode(png) ?? throw new InvalidOperationException($"cannot decode {png}");
                using var gray = decoded.ColorType == SKColorType.Gray8 ? decoded.Copy() : decoded.Copy(SKColorType.Gray8);
                var pixels = gray.GetPixelSpan().ToArray();
                var results = new BarcodeReader { Formats = BarcodeFormat.QRCode, TryHarder = true }.From(new ImageView(pixels, gray.Width, gray.Height, ImageFormat.Lum, gray.RowBytes));
                if (results.Length != 1)
                    throw new InvalidOperationException($"zxing-cpp found {results.Length} symbols in {png}");
                var r = results[0];
                if (r.SequenceIndex < 0 || r.SequenceSize < 1)
                    throw new InvalidOperationException($"{png} is not a Structured Append symbol");
                if (count >= 0 && r.SequenceSize != count)
                    throw new InvalidOperationException($"{png} reports count {r.SequenceSize}, earlier symbols {count}");
                count = r.SequenceSize;
                texts[r.SequenceIndex] = r.Text;

                var version = StructuredAppendSanityGate.ParseVersion(r.Extra("Version"));
                var id = $"{setId}-{r.SequenceIndex}of{r.SequenceSize}";
                var manifest = new FixtureManifest
                {
                    Id = id,
                    Generator = LineageName,
                    GeneratorVersion = sourceCommit,
                    SymbolType = "StandardQR",
                    Version = version,
                    Width = 17 + 4 * version,
                    Height = 17 + 4 * version,
                    ErrorCorrectionLevel = r.Extra("EcLevel"),
                    Mode = "Unknown",
                    MaskPattern = int.TryParse(r.Extra("DataMask"), out var mask) ? mask : -1,
                    PayloadText = r.Text,
                    PayloadUtf8Hex = Convert.ToHexString(Encoding.UTF8.GetBytes(r.Text)),
                    EciCharset = r.HasECI ? "UTF-8" : null,
                    QuietZoneModules = 0,
                    PixelsPerModule = 0,
                    StructuredAppend = new StructuredAppendManifest { SetId = setId, Index = r.SequenceIndex, Count = r.SequenceSize, Parity = int.Parse(r.SequenceId) },
                };

                File.Copy(png, Path.Combine(targetDir, id + ".png"));
                File.WriteAllText(Path.Combine(targetDir, id + ".json"), JsonSerializer.Serialize(manifest, jsonOptions) + "\n");
                provenance.AppendLine($"| `{id}` | `{Path.GetFileName(png)}` | {r.SequenceIndex} | {r.SequenceSize} | {r.SequenceId} |");

                var ours = QRCodeDecoder.TryDecode(decoded, out var text, out var info);
                Console.WriteLine($"{id}: v{version}-{manifest.ErrorCorrectionLevel} mask {manifest.MaskPattern}, {r.SequenceIndex} of {r.SequenceSize}, parity {r.SequenceId}, {r.Text.Length} chars; FeatherQR: {(ours ? "read" : info.Status.ToString())}");
            }

            var expectedPath = Path.Combine(sampleDir, group.Key + ".txt");
            if (File.Exists(expectedPath))
            {
                var expected = File.ReadAllText(expectedPath);
                var joined = string.Concat(texts.Values);
                if (joined != expected)
                    throw new InvalidOperationException($"set {setId} concatenates to {joined.Length} chars, {expectedPath} has {expected.Length}");
                Console.WriteLine($"{setId}: {count} symbols concatenate to {expectedPath} exactly");
            }
        }

        File.WriteAllText(Path.Combine(targetDir, "PROVENANCE.md"), provenance.ToString());
        File.Copy(licensePath, Path.Combine(targetDir, "LICENSE"));
        return 0;
    }
}
