using System.Security.Cryptography;
using System.Text;

namespace FeatherQR.Tests;

/// <summary>
/// Visual compatibility test using pixel compatison instead of binary comparison.
/// </summary>
public class QRCodeVisualCompatibilityTest
{
    private static readonly string directoryName = "testdata/pixels";

    public QRCodeVisualCompatibilityTest()
    {
        if (!Directory.Exists(directoryName))
        {
            Directory.CreateDirectory(directoryName);
        }
    }

    /// <summary>
    /// Generate HTML report of all golden files for visual inspection.
    /// </summary>
    //[Fact(Skip = "Manual execution only")]
    [Test]
    public async Task GenerateGoldenFilesReport()
    {
        VisualCompatibilityTestHelper.GenerateGoldenFilesReport(directoryName);
    }

    // tests

    [Test]
    [Arguments("0123456789", QREccLevel.L)]
    [Arguments("HELLO WORLD", QREccLevel.M)]
    [Arguments("ABC-123", QREccLevel.Q)]
    [Arguments("Test123", QREccLevel.H)]
    [Arguments("Hello, World!", QREccLevel.L)]
    [Arguments("縺薙ｓ縺ｫ縺｡縺ｯ", QREccLevel.M)]
    [Arguments("菴螂ｽ荳也阜", QREccLevel.Q)]
    [Arguments("ﾐ湲ﾐｸﾐｲﾐｵﾑ・ﾐｼﾐｸﾑ", QREccLevel.H)]
    [Arguments("脂至肢", QREccLevel.L)]
    [Arguments("cafﾃｩ", QREccLevel.M)]
    [Arguments("ﾃ双ﾃｱo", QREccLevel.Q)]
    public async Task Create_Default_PixelsMatchSample(string content, QREccLevel eccLevel)
    {
        AssertPixelsMatchSample(content, eccLevel, EciMode.Default);
    }

    [Test]
    [Arguments("0123456789", QREccLevel.L)]
    [Arguments("HELLO WORLD", QREccLevel.M)]
    [Arguments("ABC-123", QREccLevel.Q)]
    [Arguments("Test123", QREccLevel.H)]
    [Arguments("Hello, World!", QREccLevel.L)]
    [Arguments("縺薙ｓ縺ｫ縺｡縺ｯ", QREccLevel.M)]
    [Arguments("菴螂ｽ荳也阜", QREccLevel.Q)]
    [Arguments("ﾐ湲ﾐｸﾐｲﾐｵﾑ・ﾐｼﾐｸﾑ", QREccLevel.H)]
    [Arguments("脂至肢", QREccLevel.L)]
    [Arguments("cafﾃｩ", QREccLevel.M)]
    [Arguments("ﾃ双ﾃｱo", QREccLevel.Q)]
    public async Task Create_Utf8_PixelsMatchSample(string content, QREccLevel eccLevel)
    {
        AssertPixelsMatchSample(content, eccLevel, EciMode.Utf8);
    }

    [Test]
    [Arguments("Cafﾃｩ", QREccLevel.L)]
    [Arguments("Rﾃｩsumﾃｩ", QREccLevel.M)]
    [Arguments("Naﾃｯve", QREccLevel.Q)]
    [Arguments("Zﾃｼrich", QREccLevel.H)]
    public async Task Create_Iso8859_1_PixelsMatchSample(string content, QREccLevel eccLevel)
    {
        AssertPixelsMatchSample(content, eccLevel, EciMode.Iso8859_1);
    }

    // edge cases

    [Test]
    [Arguments("", QREccLevel.L, EciMode.Default)]
    [Arguments("", QREccLevel.M, EciMode.Utf8)]
    [Arguments("", QREccLevel.Q, EciMode.Iso8859_1)]
    [Arguments("", QREccLevel.H, EciMode.Default)]
    public async Task Create_EmptyString_PixelsMatchTest(string content, QREccLevel eccLevel, EciMode eciMode)
    {
        AssertPixelsMatchSample(content, eccLevel, eciMode);
    }

    [Test]
    [Arguments("A", QREccLevel.M, EciMode.Default)]  // Single character
    [Arguments("0", QREccLevel.Q, EciMode.Default)]  // Single digit
    [Arguments(" ", QREccLevel.Q, EciMode.Default)]  // Single space
    [Arguments("\t", QREccLevel.H, EciMode.Default)] // Tab
    [Arguments("\n", QREccLevel.L, EciMode.Utf8)]    // Newline
    public async Task Create_EdgeCases_PixelsMatchSample(string content, QREccLevel eccLevel, EciMode eciMode)
    {
        AssertPixelsMatchSample(content, eccLevel, eciMode);
    }

    [Test]
    [Arguments(QREccLevel.L)] // Version 1 max
    [Arguments(QREccLevel.M)]
    [Arguments(QREccLevel.Q)]
    [Arguments(QREccLevel.H)]
    public async Task Create_MaxCapacity_Version1_PixelsMatchSample(QREccLevel eccLevel)
    {
        // Max capacity for Version 1 (numeric mode)
        var maxChars = eccLevel switch
        {
            QREccLevel.L => 41,
            QREccLevel.M => 34,
            QREccLevel.Q => 27,
            QREccLevel.H => 17,
            _ => throw new ArgumentOutOfRangeException()
        };

        var content = new string('1', maxChars);
        AssertPixelsMatchSample(content, eccLevel, EciMode.Default);
    }

    // Version Boundary Tests

    [Test]
    [MethodDataSource(nameof(GetVersionBoundaryTestCases))]
    public async Task Create_VersionBoundaries_PixelsMatchSample(string content, QREccLevel eccLevel, EciMode eciMode, int expectedVersion)
    {
        var qr = QRCodeGenerator.Create(content, eccLevel, new QRCodeGeneratorOptions { EciMode = eciMode });

        // Verify version is as expected
        await Assert.That(qr.Version).IsEqualTo(expectedVersion);

        // Verify pixels match golden
        AssertPixelsMatchSample(content, eccLevel, eciMode);
    }

    // generator

    public static IEnumerable<object[]> GetVersionBoundaryTestCases()
    {
        // Default mode (no ECI header)
        // Numeric mode
        // V1-L: 152 bits - 4 (mode) - 10 (count) = 138 bits 遶翫・41 digits
        yield return new object[] { new string('1', 41), QREccLevel.L, EciMode.Default, 1 };  // V1-L max
        yield return new object[] { new string('1', 42), QREccLevel.L, EciMode.Default, 2 };  // V2-L min

        // V1-M: 128 bits - 4 - 10 = 114 bits 遶翫・34 digits
        yield return new object[] { new string('1', 34), QREccLevel.M, EciMode.Default, 1 };  // V1-M max
        yield return new object[] { new string('1', 35), QREccLevel.M, EciMode.Default, 2 };  // V2-M min

        // V1-H: 72 bits - 4 - 10 = 58 bits 遶翫・17 digits
        yield return new object[] { new string('1', 17), QREccLevel.H, EciMode.Default, 1 };  // V1-H max
        yield return new object[] { new string('1', 18), QREccLevel.H, EciMode.Default, 2 };  // V2-H min

        //Alphanumeric mode
        // V1-L: 152 bits - 4 - 9 = 139 bits 遶翫・25 chars
        yield return new object[] { new string('A', 25), QREccLevel.L, EciMode.Default, 1 };  // V1-L max
        yield return new object[] { new string('A', 26), QREccLevel.L, EciMode.Default, 2 };  // V2-L min

        // V1-M: 128 bits - 4 - 9 = 115 bits 遶翫・20 chars
        yield return new object[] { new string('A', 20), QREccLevel.M, EciMode.Default, 1 };  // V1-M max
        yield return new object[] { new string('A', 21), QREccLevel.M, EciMode.Default, 2 };  // V2-M min

        //  V1-H: 72 bits - 4 - 9 = 59 bits 遶翫・10 chars
        yield return new object[] { new string('A', 10), QREccLevel.H, EciMode.Default, 1 };  // V1-H max
        yield return new object[] { new string('A', 11), QREccLevel.H, EciMode.Default, 2 };  // V2-H min

        // Encoding with ECI header (12 bits)
        foreach (var eci in new[] { EciMode.Iso8859_1, EciMode.Utf8 })
        {
            // Numeric mode
            // V1-L: 152 bits - 12 (ECI) - 4 (mode) - 10 (count) = 126 bits 遶翫・37 digits
            yield return new object[] { new string('1', 37), QREccLevel.L, eci, 1 };  // V1-L max
            yield return new object[] { new string('1', 38), QREccLevel.L, eci, 2 };  // V2-L min

            // V1-M: 128 bits - 12 - 4 - 10 = 102 bits 遶翫・30 digits
            yield return new object[] { new string('1', 30), QREccLevel.M, eci, 1 };  // V1-M max
            yield return new object[] { new string('1', 31), QREccLevel.M, eci, 2 };  // V2-M min

            // V1-H: 72 bits - 12 - 4 - 10 = 46 bits 遶翫・13 digits
            yield return new object[] { new string('1', 13), QREccLevel.H, eci, 1 };  // V1-H max
            yield return new object[] { new string('1', 14), QREccLevel.H, eci, 2 };  // V2-H min

            //Alphanumeric mode
            // V1-L: 152 bits - 12 - 4 - 9 = 127 bits 遶翫・23 chars
            yield return new object[] { new string('A', 23), QREccLevel.L, eci, 1 };  // V1-L max
            yield return new object[] { new string('A', 24), QREccLevel.L, eci, 2 };  // V2-L min

            // V1-M: 128 bits - 12 - 4 - 9 = 103 bits 遶翫・18 chars
            yield return new object[] { new string('A', 18), QREccLevel.M, eci, 1 };  // V1-M max
            yield return new object[] { new string('A', 19), QREccLevel.M, eci, 2 };  // V2-M min

            //  V1-H: 72 bits - 12 - 4 - 9 = 47 bits 遶翫・8 chars
            yield return new object[] { new string('A', 8), QREccLevel.H, eci, 1 };  // V1-H max
            yield return new object[] { new string('A', 9), QREccLevel.H, eci, 2 };  // V2-H min
        }
    }

    // helpers

    /// <summary>
    /// Assert that generated QR code pixels match sample file.
    /// </summary>
    private async Task AssertPixelsMatchSample(string content, QREccLevel eccLevel, EciMode eciMode)
    {
        var qr = QRCodeGenerator.Create(content.AsSpan(), eccLevel, new QRCodeGeneratorOptions { EciMode = eciMode });

        // Render to pixel array
        var actualPixels = RenderToPixelArray(qr);
        var samplePath = GetSampleFilePath(content, eccLevel, eciMode);

        if (!File.Exists(samplePath))
        {
            // First run: save as sample
            SavePixelData(samplePath, actualPixels, qr.Size);
            await Assert.That(File.Exists(samplePath)).IsTrue().Because($"Sample file created: {samplePath}");
        }
        else
        {
            // Load and compare with sample
            var (expectedPixels, expectedSize) = VisualCompatibilityTestHelper.LoadPixelData(samplePath);

            if (!actualPixels.SequenceEqual(expectedPixels))
            {
                // Save actual for comparison
                var actualPath = samplePath.Replace(".pixels", ".actual.pixels");
                SavePixelData(actualPath, actualPixels, qr.Size);

                // Calculate difference percentage
                var diffCount = actualPixels.Zip(expectedPixels, (a, e) => a != e ? 1 : 0).Sum();
                var diffPercent = (diffCount * 100.0) / actualPixels.Length;

                Assert.Fail($"QR code pixels changed!\n" +
                           $"Content: {content}\n" +
                           $"ECC Level: {eccLevel}\n" +
                           $"ECI Mode: {eciMode}\n" +
                           $"Path: {samplePath}\n" +
                           $"Actual: {actualPath}\n" +
                           $"Size: {qr.Size}x{qr.Size}\n" +
                           $"Difference: {diffCount}/{actualPixels.Length} pixels ({diffPercent:F2}%)\n" +
                           $"Run visual diff tool to compare.");
            }

            // Also verify size matches
            await Assert.That(qr.Size).IsEqualTo(expectedSize);
        }
    }

    /// <summary>
    /// Render QR code to pixel array (black=0, white=255).
    /// </summary>
    private static byte[] RenderToPixelArray(QRCodeData qr)
    {
        var size = qr.Size;
        var pixels = new byte[size * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                pixels[y * size + x] = qr[y, x] ? (byte)0 : (byte)255;
            }
        }

        return pixels;
    }

    /// <summary>
    /// Save pixel data with metadata.
    /// Format: [4 bytes size][pixel data]
    /// </summary>
    private static async Task SavePixelData(string path, byte[] pixels, int size)
    {
        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);

        // Write metadata
        writer.Write(size); // 4 bytes: matrix size
        writer.Write(pixels.Length); // 4 bytes: pixel count

        // Write pixel data
        writer.Write(pixels);
    }

    /// <summary>
    /// Get sample file path for test case.
    /// </summary>
    private static string GetSampleFilePath(string content, QREccLevel eccLevel, EciMode eciMode)
    {
        // Sanitize content for filename
        var safeContent = SanitizeForFilename(content);

        // Truncate if too long
        if (safeContent.Length > 50)
        {
            safeContent = safeContent.Substring(0, 50) + "_" + GetContentHash(content);
        }

        var filename = $"{safeContent}_ecc{eccLevel}_eci{(int)eciMode}.pixels";
        return Path.Combine(directoryName, filename);
    }

    /// <summary>
    /// Sanitize string for use in filename.
    /// </summary>
    private static string SanitizeForFilename(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "empty";

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", input.Split(invalid, StringSplitOptions.RemoveEmptyEntries));

        // Replace spaces and special chars
        sanitized = sanitized
            .Replace(" ", "_")
            .Replace(".", "_")
            .Replace(",", "_")
            .Replace("!", "_")
            .Replace("?", "_");

        return string.IsNullOrWhiteSpace(sanitized) ? "this is non-readable chars" : sanitized;
    }

    /// <summary>
    /// Get hash of content for unique identification.
    /// </summary>
    private static string GetContentHash(string content)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = sha.ComputeHash(bytes);
        return BitConverter.ToString(hash)
            .Replace("-", "")
            .Substring(0, 8)
            .ToLower();
    }
}
