namespace FeatherQR.Tests;

public class QRCodeGeneratorVersionBoundaryTest
{
    /// <summary>
    /// Tests exact version boundaries using actual measured capacities.
    /// These values come from DiagnoseActualCapacities_AllModes output.
    /// </summary>
    [Test]
    // Version 1 boundaries (Numeric mode)
    [Arguments(QREccLevel.L, 41, 1)]
    [Arguments(QREccLevel.M, 34, 1)]
    [Arguments(QREccLevel.Q, 27, 1)]
    [Arguments(QREccLevel.H, 17, 1)]
    // Version 2 boundaries (Numeric mode)
    [Arguments(QREccLevel.L, 77, 2)]
    [Arguments(QREccLevel.M, 63, 2)]
    [Arguments(QREccLevel.Q, 48, 2)]
    [Arguments(QREccLevel.H, 34, 2)]
    // Version 10 boundaries (Numeric mode)
    [Arguments(QREccLevel.L, 652, 10)]
    [Arguments(QREccLevel.M, 513, 10)]
    [Arguments(QREccLevel.Q, 364, 10)]
    [Arguments(QREccLevel.H, 288, 10)]
    // Version 15 boundaries (Numeric mode)
    [Arguments(QREccLevel.L, 1250, 15)]
    [Arguments(QREccLevel.M, 991, 15)]
    [Arguments(QREccLevel.Q, 703, 15)]
    [Arguments(QREccLevel.H, 530, 15)]
    // Version 16 boundaries (Numeric mode) - Stack allocation limit
    [Arguments(QREccLevel.L, 1408, 16)]
    [Arguments(QREccLevel.M, 1082, 16)]
    [Arguments(QREccLevel.Q, 775, 16)]
    [Arguments(QREccLevel.H, 602, 16)]
    // Version 17 boundaries (Numeric mode) - Heap allocation starts
    [Arguments(QREccLevel.L, 1548, 17)]
    [Arguments(QREccLevel.M, 1212, 17)]
    [Arguments(QREccLevel.Q, 876, 17)]
    [Arguments(QREccLevel.H, 674, 17)]
    // Version 20 boundaries (Numeric mode)
    [Arguments(QREccLevel.L, 2061, 20)]
    [Arguments(QREccLevel.M, 1600, 20)]
    [Arguments(QREccLevel.Q, 1159, 20)]
    [Arguments(QREccLevel.H, 919, 20)]
    // Version 30 boundaries (Numeric mode)
    [Arguments(QREccLevel.L, 4158, 30)]
    [Arguments(QREccLevel.M, 3289, 30)]
    [Arguments(QREccLevel.Q, 2358, 30)]
    [Arguments(QREccLevel.H, 1782, 30)]
    // Version 40 boundaries (Numeric mode) - Maximum capacity
    [Arguments(QREccLevel.L, 7089, 40)]
    [Arguments(QREccLevel.M, 5596, 40)]
    [Arguments(QREccLevel.Q, 3993, 40)]
    [Arguments(QREccLevel.H, 3057, 40)]
    public async Task Create_ExactBoundary_Numeric(QREccLevel eccLevel, int maxChars, int expectedVersion)
    {
        // Arrange
        var plainText = new string('1', maxChars); // Numeric mode

        // Act
        var qrCode = QRCodeGenerator.Create(plainText, eccLevel);

        // Assert
        await Assert.That(qrCode.Version).IsEquivalentTo(expectedVersion);
        await Assert.That(qrCode.Size > 0).IsTrue();
    }

    /// <summary>
    /// Tests exact version boundaries for Alphanumeric mode.
    /// </summary>
    [Test]
    // Version 1 boundaries (Alphanumeric mode)
    [Arguments(QREccLevel.L, 25, 1)]
    [Arguments(QREccLevel.M, 20, 1)]
    [Arguments(QREccLevel.Q, 16, 1)]
    [Arguments(QREccLevel.H, 10, 1)]
    // Version 10 boundaries (Alphanumeric mode)
    [Arguments(QREccLevel.L, 395, 10)]
    [Arguments(QREccLevel.M, 311, 10)]
    [Arguments(QREccLevel.Q, 221, 10)]
    [Arguments(QREccLevel.H, 174, 10)]
    // Version 16 boundaries (Alphanumeric mode) - Stack allocation limit
    [Arguments(QREccLevel.L, 854, 16)]
    [Arguments(QREccLevel.M, 656, 16)]
    [Arguments(QREccLevel.Q, 470, 16)]
    [Arguments(QREccLevel.H, 365, 16)]
    // Version 17 boundaries (Alphanumeric mode) - Heap allocation starts
    [Arguments(QREccLevel.L, 938, 17)]
    [Arguments(QREccLevel.M, 734, 17)]
    [Arguments(QREccLevel.Q, 531, 17)]
    [Arguments(QREccLevel.H, 408, 17)]
    // Version 40 boundaries (Alphanumeric mode)
    [Arguments(QREccLevel.L, 4296, 40)]
    [Arguments(QREccLevel.M, 3391, 40)]
    [Arguments(QREccLevel.Q, 2420, 40)]
    [Arguments(QREccLevel.H, 1852, 40)]
    public async Task Create_ExactBoundary_Alphanumeric(QREccLevel eccLevel, int maxChars, int expectedVersion)
    {
        // Arrange
        var plainText = new string('A', maxChars); // Alphanumeric mode

        // Act
        var qrCode = QRCodeGenerator.Create(plainText, eccLevel);

        // Assert
        await Assert.That(qrCode.Version).IsEquivalentTo(expectedVersion);
    }

    /// <summary>
    /// Tests exact version boundaries for Byte mode (UTF-8 multi-byte characters).
    /// </summary>
    [Test]
    // Version 1 boundaries (Byte mode, UTF-8 'あ')
    [Arguments(QREccLevel.L, 5, 1)]
    [Arguments(QREccLevel.M, 4, 1)]
    [Arguments(QREccLevel.Q, 3, 1)]
    [Arguments(QREccLevel.H, 2, 1)]
    // Version 10 boundaries (Byte mode, UTF-8 'あ')
    [Arguments(QREccLevel.L, 90, 10)]
    [Arguments(QREccLevel.M, 70, 10)]
    [Arguments(QREccLevel.Q, 50, 10)]
    [Arguments(QREccLevel.H, 39, 10)]
    // Version 16 boundaries (Byte mode, UTF-8 'あ') - Stack allocation limit
    [Arguments(QREccLevel.L, 195, 16)]
    [Arguments(QREccLevel.M, 149, 16)]
    [Arguments(QREccLevel.Q, 107, 16)]
    [Arguments(QREccLevel.H, 83, 16)]
    // Version 17 boundaries (Byte mode, UTF-8 'あ') - Heap allocation starts
    [Arguments(QREccLevel.L, 214, 17)]
    [Arguments(QREccLevel.M, 167, 17)]
    [Arguments(QREccLevel.Q, 121, 17)]
    [Arguments(QREccLevel.H, 93, 17)]
    // Version 40 boundaries (Byte mode, UTF-8 'あ')
    [Arguments(QREccLevel.L, 984, 40)]
    [Arguments(QREccLevel.M, 776, 40)]
    [Arguments(QREccLevel.Q, 554, 40)]
    [Arguments(QREccLevel.H, 424, 40)]
    public async Task Create_ExactBoundary_ByteUtf8(QREccLevel eccLevel, int maxChars, int expectedVersion)
    {
        // Arrange
        var plainText = new string('あ', maxChars); // UTF-8 multi-byte (3 bytes per char)

        // Act
        var qrCode = QRCodeGenerator.Create(plainText, eccLevel);

        // Assert
        await Assert.That(qrCode.Version).IsEquivalentTo(expectedVersion);
    }

    /// <summary>
    /// Tests that exceeding capacity by 1 character bumps to next version.
    /// </summary>
    [Test]
    // Numeric mode overflow tests
    [Arguments(QREccLevel.L, 42, 2)]    // v1 max=41, 42 should be v2
    [Arguments(QREccLevel.L, 78, 3)]    // v2 max=77, 78 should be v3
    [Arguments(QREccLevel.L, 1409, 17)] // v16 max=1408, 1409 should be v17
    [Arguments(QREccLevel.L, 7090, -1)] // v40 max=7089, 7090 should fail or stay v40
    [Arguments(QREccLevel.M, 5597, -1)] // v40 max=7089, 7090 should fail or stay v40
    [Arguments(QREccLevel.Q, 3994, -1)] // v40 max=7089, 7090 should fail or stay v40
    [Arguments(QREccLevel.H, 3058, -1)] // v40 max=7089, 7090 should fail or stay v40
    public async Task Create_OverflowBoundary_Numeric(QREccLevel eccLevel, int charCount, int expectedNextVersion)
    {
        // Arrange
        var plainText = new string('1', charCount);

        // Act & Assert
        if (expectedNextVersion == -1)
        {
            // Data too long for max version
            Assert.Throws<InvalidOperationException>(() => QRCodeGenerator.Create(plainText, eccLevel));
        }
        else
        {
            var qrCode = QRCodeGenerator.Create(plainText, eccLevel);
            await Assert.That(qrCode.Version).IsEquivalentTo(expectedNextVersion);
        }
    }

    /// <summary>
    /// Tests version selection with forced version parameter.
    /// This bypasses automatic version selection logic.
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(5)]
    [Arguments(10)]
    [Arguments(15)]
    [Arguments(16)]  // Stack allocation boundary
    [Arguments(17)]  // Heap allocation boundary
    [Arguments(20)]
    [Arguments(25)]
    [Arguments(30)]
    [Arguments(35)]
    [Arguments(40)]
    public async Task Create_ForcedVersion_GeneratesCorrectly(int requestedVersion)
    {
        // Arrange
        var plainText = "TEST";

        // Act
        var qrCode = QRCodeGenerator.Create(plainText, QREccLevel.L, new QRCodeGeneratorOptions { Version = requestedVersion });

        // Assert
        await Assert.That(qrCode.Version).IsEquivalentTo(requestedVersion);
        var expectedSize = 21 + (requestedVersion - 1) * 4 + 8; // Core size + quiet zone
        await Assert.That(qrCode.Size).IsEquivalentTo(expectedSize);
    }

    /// <summary>
    /// Stress test: Generate all versions from 1 to 40 using requestedVersion.
    /// </summary>
    [Test]
    public async Task Create_AllVersions_WithRequestedVersion_GenerateSuccessfully()
    {
        // Act & Assert
        for (int version = 1; version <= 40; version++)
        {
            var plainText = "1";
            var qrCode = QRCodeGenerator.Create(plainText, QREccLevel.L, new QRCodeGeneratorOptions { Version = version });

            await Assert.That(qrCode.Version).IsEquivalentTo(version);
            var expectedSize = 21 + (version - 1) * 4 + 8;
            await Assert.That(qrCode.Size).IsEquivalentTo(expectedSize);
        }
    }

    /// <summary>
    /// Tests critical version boundaries (v15/v16/v17) for stack/heap allocation transition.
    /// </summary>
    [Test]
    [Arguments(QREccLevel.L, 1250, 15)]  // v15 max (stack)
    [Arguments(QREccLevel.L, 1251, 16)]  // v15 overflow 竊・v16 (still stack)
    [Arguments(QREccLevel.L, 1408, 16)]  // v16 max (stack limit)
    [Arguments(QREccLevel.L, 1409, 17)]  // v16 overflow 竊・v17 (heap starts)
    [Arguments(QREccLevel.L, 1548, 17)]  // v17 max (heap)
    [Arguments(QREccLevel.M, 991, 15)]   // v15 max (M)
    [Arguments(QREccLevel.M, 992, 16)]   // v15 overflow (M)
    [Arguments(QREccLevel.M, 1082, 16)]  // v16 max (M)
    [Arguments(QREccLevel.M, 1083, 17)]  // v16 overflow (M)
    public async Task Create_StackHeapBoundary_Numeric(QREccLevel eccLevel, int charCount, int expectedVersion)
    {
        // Arrange
        var plainText = new string('1', charCount);

        // Act
        var qrCode = QRCodeGenerator.Create(plainText, eccLevel);

        // Assert
        await Assert.That(qrCode.Version).IsEquivalentTo(expectedVersion);
    }

    /// <summary>
    /// Tests automatic version selection for various data sizes (with tolerance).
    /// Kept for backward compatibility and fuzzy testing.
    /// </summary>
    [Test]
    [Arguments(10, 1, 2)]      // Small data -> v1 or v2
    [Arguments(50, 2, 3)]      // Small-medium -> v2 or v3
    [Arguments(200, 5, 7)]     // Medium -> v5-v7
    [Arguments(500, 9, 11)]    // Large -> v9-v11
    [Arguments(1000, 13, 15)]  // Very large -> v13-v15
    [Arguments(2000, 20, 22)]  // Huge -> v20-v22
    [Arguments(5000, 32, 35)]  // Maximum -> v32-v35
    public async Task Create_AutoVersionSelection_InRange(int charCount, int minVersion, int maxVersion)
    {
        // Arrange
        var plainText = new string('1', charCount);

        // Act
        var qrCode = QRCodeGenerator.Create(plainText, QREccLevel.L);

        // Assert
        await Assert.That(qrCode.Version).IsBetween(minVersion, maxVersion);
    }

    /// <summary>
    /// Diagnostic test: Discovers actual capacity boundaries for documentation.
    /// This test always passes but outputs useful capacity information.
    /// </summary>
    [Test]
    [Skip("Diagnostic test - run manually to discover actual capacities")]
    public async Task DiagnoseActualCapacities_AllModes()
    {
        var results = new System.Text.StringBuilder();
        results.AppendLine("# Actual QR Code Capacities (including overhead)");
        results.AppendLine();
        results.AppendLine("**Test Characters:**");
        results.AppendLine("- Numeric: `'1'` (digit)");
        results.AppendLine("- Alphanumeric: `'A'` (uppercase letter)");
        results.AppendLine("- Byte (UTF-8): `'あ'` (hiragana, 3 bytes)");
        results.AppendLine();

        foreach (var eccLevel in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
        {
            results.AppendLine($"## ECC Level: {eccLevel}");
            results.AppendLine();
            results.AppendLine("| Version | Numeric | Alphanumeric | Byte (UTF-8 Multi-byte*) |");
            results.AppendLine("|---------|---------|--------------|------|");

            for (int version = 1; version <= 40; version++)
            {
                int numericCap = FindMaxCapacity(version, eccLevel, '1');
                int alphaCap = FindMaxCapacity(version, eccLevel, 'A');
                int byteCap = FindMaxCapacity(version, eccLevel, 'あ'); // Multi-byte

                results.AppendLine($"| {version,2} | {numericCap,7} | {alphaCap,12} | {byteCap,4} |");
            }

            results.AppendLine();
        }

        var output = results.ToString();
        System.IO.File.WriteAllText("actual_qr_capacities.md", output);
        await Assert.That(File.Exists("actual_qr_capacities.md")).IsTrue().Because("Capacity report written to actual_qr_capacities.md");
    }

    private static int FindMaxCapacity(int targetVersion, QREccLevel eccLevel, char fillChar)
    {
        int low = 1, high = 10000;
        int maxCapacity = 0;

        while (low <= high)
        {
            int mid = (low + high) / 2;
            var text = new string(fillChar, mid);

            try
            {
                var qr = QRCodeGenerator.Create(text, eccLevel);

                if (qr.Version == targetVersion)
                {
                    maxCapacity = mid;
                    low = mid + 1;
                }
                else if (qr.Version < targetVersion)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }
            catch
            {
                high = mid - 1;
            }
        }

        return maxCapacity;
    }
}
