using TUnit.Assertions.Enums;
namespace FeatherQR.Tests;

public class QRCodeDataUnitTest
{
    [Test]
    [Arguments(1, 21)]   // Version 1: 21繝ｻ繝ｻ繝ｻ1 = 441 bits (441 % 8 = 1, padding = 7)
    [Arguments(2, 25)]   // Version 2: 25繝ｻ繝ｻ繝ｻ5 = 625 bits (625 % 8 = 1, padding = 7)
    [Arguments(3, 29)]   // Version 3: 29繝ｻ繝ｻ繝ｻ9 = 841 bits (841 % 8 = 1, padding = 7)
    [Arguments(10, 57)]  // Version 10: 57繝ｻ繝ｻ繝ｻ7 = 3249 bits (3249 % 8 = 1, padding = 7)
    [Arguments(40, 177)] // Version 40: 177繝ｻ繝ｻ繝ｻ77 = 31329 bits (31329 % 8 = 1, padding = 7)
    public async Task GetRawData_CorrectPadding_ByVersion(int version, int expectedSize)
    {
        // Create QR code data with specific version
        var qrCode = new QRCodeData(version, quietZoneSize: 0);

        // Verify size matches expected
        await Assert.That(qrCode.Size).IsEqualTo(expectedSize);

        // Get raw data
        var rawData = qrCode.GetRawData();

        // Verify byte alignment
        // Expected bytes = ceil((size * size) / 8) + header (4 bytes)
        var totalBits = expectedSize * expectedSize;
        var dataBytesCount = (totalBits + 7) / 8;  // Round up to nearest byte
        var expectedBytes = dataBytesCount + 4;     // +4 for header

        await Assert.That(rawData.Length).IsEqualTo(expectedBytes);
    }

    [Test]
    [Arguments(1, 0, 21)]   // Version 1, no quiet zone: 21繝ｻ繝ｻ繝ｻ1
    [Arguments(1, 4, 29)]   // Version 1, quiet zone 4: 29繝ｻ繝ｻ繝ｻ9
    [Arguments(2, 0, 25)]   // Version 2, no quiet zone: 25繝ｻ繝ｻ繝ｻ5
    [Arguments(2, 4, 33)]   // Version 2, quiet zone 4: 33繝ｻ繝ｻ繝ｻ3
    public async Task GetRawData_CorrectPadding_WithQuietZone(int version, int quietZone, int expectedSize)
    {
        // Create QR code
        var qrCode = new QRCodeData(version, quietZoneSize: quietZone);

        // Verify size
        await Assert.That(qrCode.Size).IsEqualTo(expectedSize);

        // Get raw data
        var rawData = qrCode.GetRawData();

        // Verify byte alignment
        var baseSize = QRCodeData.SizeFromVersion(version);
        var totalBits = baseSize * baseSize;
        var expectedBytes = (totalBits + 7) / 8 + 4;

        await Assert.That(rawData.Length).IsEqualTo(expectedBytes);
    }

    /// <summary>
    /// Tests padding formula in isolation - verifies mathematical correctness.
    /// </summary>
    [Test]
    [Arguments(0, 0)]    // Already aligned
    [Arguments(1, 7)]    // 1 bit 驕ｶ鄙ｫ繝ｻneed 7 bits padding
    [Arguments(7, 1)]    // 7 bits 驕ｶ鄙ｫ繝ｻneed 1 bit padding
    [Arguments(256, 0)]  // 256 bits (32 bytes) 驕ｶ鄙ｫ繝ｻno padding 驕ｶ鄙ｫ繝ｻEdge case
    [Arguments(257, 7)]  // 257 bits 驕ｶ鄙ｫ繝ｻ7 bits padding
    [Arguments(441, 7)]  // Version 1 QR (21繝ｻ繝ｻ繝ｻ1)
    public async Task PaddingFormula_CalculatesCorrectBoundary(int totalBits, int expectedPadding)
    {
        var remainder = totalBits % 8;

        // Correct formula
        var boundary = (8 - remainder) % 8;

        await Assert.That(boundary).IsEqualTo(expectedPadding);

        // Verify old formula fails for edge cases
        if (remainder == 0)
        {
            var oldBoundary = 8 - remainder;  // = 8 (wrong!)
            await Assert.That(oldBoundary).IsEqualTo(8);
            await Assert.That(oldBoundary).IsNotEqualTo(boundary);
        }
    }

    // Constructors

    [Test]
    [Arguments(-1)]
    [Arguments(10_001)]
    [Arguments((1 << 30) - 1)]
    [Arguments(int.MaxValue)]
    public async Task Constructor_QuietZoneOutOfRange_Throws(int quietZoneSize)
    {
        // The bound Micro QR and rMQR data share, which keeps the side and its square in int
        var raw = QRCodeGenerator.Create("123", QREccLevel.L).GetRawData();

        var empty = Assert.Throws<ArgumentOutOfRangeException>(() => new QRCodeData(1, quietZoneSize));
        var restored = Assert.Throws<ArgumentOutOfRangeException>(() => new QRCodeData(raw, quietZoneSize));
        await Assert.That(empty.ParamName).IsEqualTo("quietZoneSize");
        await Assert.That(restored.ParamName).IsEqualTo("quietZoneSize");
    }

    [Test]
    public async Task Constructor_QuietZoneAtTheBound_IsAccepted()
    {
        var raw = QRCodeGenerator.Create("123", QREccLevel.L).GetRawData();

        await Assert.That(new QRCodeData(40, 10_000).Size).IsEqualTo(177 + 2 * 10_000);
        await Assert.That(new QRCodeData(raw, 10_000).Size).IsEqualTo(21 + 2 * 10_000);
    }

    // SetCoreData

    /// <summary>
    /// Tests that QuietZone area remains white (false) after SetCoreData
    /// </summary>
    [Test]
    [Arguments(2, 4)]
    [Arguments(5, 2)]
    [Arguments(10, 8)]
    public async Task SetCoreData_PreservesQuietZone(int version, int quietZoneSize)
    {
        var baseSize = QRCodeData.SizeFromVersion(version);
        var fullSize = baseSize + (quietZoneSize * 2);
        var qrCode = new QRCodeData(version, quietZoneSize: quietZoneSize);

        // Create core data with pattern
        var coreData = new byte[baseSize * baseSize];
        for (int i = 0; i < coreData.Length; i++)
        {
            coreData[i] = (byte)(i % 5 == 0 ? 1 : 0);
        }

        // Set core data
        qrCode.SetCoreData(coreData);

        // Verify QuietZone remains white
        for (int i = 0; i < fullSize; i++)
        {
            // Top and bottom
            for (int row = 0; row < quietZoneSize; row++)
            {
                await Assert.That(qrCode[row, i]).IsFalse().Because($"Top QuietZone corrupted at ({row}, {i})");
                await Assert.That(qrCode[fullSize - 1 - row, i]).IsFalse().Because($"Bottom QuietZone corrupted at ({fullSize - 1 - row}, {i})");
            }
            // Left and right
            for (int col = 0; col < quietZoneSize; col++)
            {
                await Assert.That(qrCode[i, col]).IsFalse().Because($"Left QuietZone corrupted at ({i}, {col})");
                await Assert.That(qrCode[i, fullSize - 1 - col]).IsFalse().Because($"Right QuietZone corrupted at ({i}, {fullSize - 1 - col})");
            }
        }

        // Verify core data was set correctly
        var retrievedCoreData = new byte[baseSize * baseSize];
        qrCode.GetCoreData(retrievedCoreData);
        await Assert.That(retrievedCoreData).IsEquivalentTo(coreData, CollectionOrdering.Matching);
    }

    // Serialization Tests

    /// <summary>
    /// Round-trip test with VALID QR sizes only
    /// </summary>
    /// <remarks>
    /// if matrixSize is 21 (Version 1), then the pattern is:
    /// ---------------------------------------------
    /// (row, col) 驕ｶ鄙ｫ繝ｻ1D index 驕ｶ鄙ｫ繝ｻpattern
    /// ---------------------------------------------
    /// (0, 0) 驕ｶ鄙ｫ繝ｻ0 * 21 + 0 = 0   驕ｶ鄙ｫ繝ｻ0 % 7 = 0 驕ｶ鄙ｫ繝ｻtrue  髫ｨ・ｨ郢晢ｽｻ    /// (0, 1) 驕ｶ鄙ｫ繝ｻ0 * 21 + 1 = 1   驕ｶ鄙ｫ繝ｻ1 % 7 = 1 驕ｶ鄙ｫ繝ｻfalse
    /// (0, 2) 驕ｶ鄙ｫ繝ｻ0 * 21 + 2 = 2   驕ｶ鄙ｫ繝ｻ2 % 7 = 2 驕ｶ鄙ｫ繝ｻfalse
    /// ...
    /// (0, 6) 驕ｶ鄙ｫ繝ｻ0 * 21 + 6 = 6   驕ｶ鄙ｫ繝ｻ6 % 7 = 6 驕ｶ鄙ｫ繝ｻfalse
    /// (0, 7) 驕ｶ鄙ｫ繝ｻ0 * 21 + 7 = 7   驕ｶ鄙ｫ繝ｻ7 % 7 = 0 驕ｶ鄙ｫ繝ｻtrue  髫ｨ・ｨ郢晢ｽｻ    /// (0, 14) 驕ｶ鄙ｫ繝ｻ0 * 21 + 14 = 14 驕ｶ鄙ｫ繝ｻ14 % 7 = 0 驕ｶ鄙ｫ繝ｻtrue 髫ｨ・ｨ郢晢ｽｻ    /// (1, 6) 驕ｶ鄙ｫ繝ｻ1 * 21 + 6 = 27  驕ｶ鄙ｫ繝ｻ27 % 7 = 6 驕ｶ鄙ｫ繝ｻfalse
    /// (1, 7) 驕ｶ鄙ｫ繝ｻ1 * 21 + 7 = 28  驕ｶ鄙ｫ繝ｻ28 % 7 = 0 驕ｶ鄙ｫ繝ｻtrue  髫ｨ・ｨ郢晢ｽｻ    ///
    /// ---------------------------------------------
    /// Visualize
    /// ---------------------------------------------
    /// 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡
    /// 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡
    /// 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡
    /// 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡
    /// ...
    /// </remarks>
    [Test]
    [Arguments(21)]   // Version 1
    [Arguments(25)]   // Version 2
    [Arguments(29)]   // Version 3
    [Arguments(57)]   // Version 10
    public async Task Serialization_RoundTrip_PreservesData(int matrixSize)
    {
        var quietZoneSize = 0;
        // Use (row * matrixSize + col) % 7 to create a pattern that is not too dense.
        // 7 is not 8's multiple, it means it won't dup with byte border.
        // see remarks.
        var pattern = 7;
        var version = (matrixSize - 21) / 4 + 1;

        var original = new QRCodeData(version, quietZoneSize: quietZoneSize);

        // Verify size matched
        await Assert.That(original.Size).IsEqualTo(matrixSize);

        // Get core buffer and fill pattern
        var coreSize = original.GetCoreSize();
        Span<byte> coreBuffer = stackalloc byte[coreSize * coreSize];

        for (int i = 0; i < coreBuffer.Length; i++)
        {
            coreBuffer[i] = (byte)(i % pattern == 0 ? 1 : 0);
        }

        // Set core data
        original.SetCoreData(coreBuffer);

        // Serialize and Deserialize
        var rawData = original.GetRawData();
        var restored = new QRCodeData(rawData, quietZoneSize: quietZoneSize);

        await Assert.That(restored.Size).IsEqualTo(matrixSize);

        // Verify pattern
        for (int row = 0; row < matrixSize; row++)
        {
            for (int col = 0; col < matrixSize; col++)
            {
                var index = row * matrixSize + col;
                var expected = index % pattern == 0;
                await Assert.That(restored[row, col]).IsEquivalentTo(expected);
            }
        }
    }

    /// <summary>
    /// Tests serialization/deserialization with all compression modes
    /// </summary>
    /// <remarks>
    /// if matrixSize is 37 (Version 5), then the pattern is:
    /// ---------------------------------------------
    /// (row, col) 驕ｶ鄙ｫ繝ｻsum 驕ｶ鄙ｫ繝ｻpattern
    /// ---------------------------------------------
    /// (0, 0) 驕ｶ鄙ｫ繝ｻ0 + 0 = 0 驕ｶ鄙ｫ繝ｻ0 % 3 = 0 驕ｶ鄙ｫ繝ｻtrue  髫ｨ・ｨ郢晢ｽｻ    /// (0, 1) 驕ｶ鄙ｫ繝ｻ0 + 1 = 1 驕ｶ鄙ｫ繝ｻ1 % 3 = 1 驕ｶ鄙ｫ繝ｻfalse
    /// (0, 2) 驕ｶ鄙ｫ繝ｻ0 + 2 = 2 驕ｶ鄙ｫ繝ｻ2 % 3 = 2 驕ｶ鄙ｫ繝ｻfalse
    /// (0, 3) 驕ｶ鄙ｫ繝ｻ0 + 3 = 3 驕ｶ鄙ｫ繝ｻ3 % 3 = 0 驕ｶ鄙ｫ繝ｻtrue  髫ｨ・ｨ郢晢ｽｻ    /// (1, 0) 驕ｶ鄙ｫ繝ｻ1 + 0 = 1 驕ｶ鄙ｫ繝ｻ1 % 3 = 1 驕ｶ鄙ｫ繝ｻfalse
    /// (1, 1) 驕ｶ鄙ｫ繝ｻ1 + 1 = 2 驕ｶ鄙ｫ繝ｻ2 % 3 = 2 驕ｶ鄙ｫ繝ｻfalse
    /// (1, 2) 驕ｶ鄙ｫ繝ｻ1 + 2 = 3 驕ｶ鄙ｫ繝ｻ3 % 3 = 0 驕ｶ鄙ｫ繝ｻtrue  髫ｨ・ｨ郢晢ｽｻ    /// (2, 1) 驕ｶ鄙ｫ繝ｻ2 + 1 = 3 驕ｶ鄙ｫ繝ｻ3 % 3 = 0 驕ｶ鄙ｫ繝ｻtrue  髫ｨ・ｨ郢晢ｽｻ    /// 
    /// ---------------------------------------------
    /// Visualize
    /// ---------------------------------------------
    /// 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡
    /// 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ
    /// 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡
    /// 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡
    /// 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ
    /// 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡
    /// 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡
    /// 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ
    /// 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・ｽ・｡ 髫ｨ繝ｻ・｣・ｰ 髫ｨ繝ｻ・ｽ・｡
    /// </remarks>
    [Test]
    public async Task Serialization_RoundTrip()
    {
        var quietZoneSize = 0;
        // Use (row + col) % 3 to create a pattern that is not too dense.
        // 3 is not 8's multiple, it means it won't dup with byte border.
        // see remarks.
        var pattern = 3;

        // Arrange
        var original = new QRCodeData(5, quietZoneSize: quietZoneSize);  // Version 5: 37繝ｻ繝ｻ繝ｻ7
        var size = original.Size;

        // Set recognizable pattern
        for (int row = 0; row < size; row++)
        {
            for (int col = 0; col < size; col++)
            {
                original[row, col] = (row + col) % pattern == 0;
            }
        }

        // Act
        var rawData = original.GetRawData();
        var restored = new QRCodeData(rawData, quietZoneSize);

        // Assert
        await Assert.That(restored.Version).IsEqualTo(original.Version);
        await Assert.That(restored.Size).IsEqualTo(original.Size);

        for (int row = 0; row < size; row++)
        {
            for (int col = 0; col < size; col++)
            {
                await Assert.That(restored[row, col]).IsEqualTo(original[row, col]);
            }
        }
    }

    /// <summary>
    /// Tests serialization/deserialization with QuietZone
    /// Verifies that QuietZone is properly excluded from serialized data and can be restored
    /// </summary>
    [Test]
    [Arguments(1, 4)]   // Version 1, QuietZone 4
    [Arguments(2, 2)]   // Version 2, QuietZone 2
    [Arguments(5, 4)]   // Version 5, QuietZone 4
    [Arguments(10, 8)]  // Version 10, QuietZone 8
    public async Task Serialization_WithQuietZone_PreservesOnlyCoreData(int version, int quietZoneSize)
    {
        var baseSize = QRCodeData.SizeFromVersion(version);
        var fullSize = baseSize + (quietZoneSize * 2);

        // Create QR code with QuietZone
        var original = new QRCodeData(version, quietZoneSize: quietZoneSize);
        await Assert.That(original.Size).IsEqualTo(fullSize);

        // Set pattern in core data area only
        for (int row = quietZoneSize; row < fullSize - quietZoneSize; row++)
        {
            for (int col = quietZoneSize; col < fullSize - quietZoneSize; col++)
            {
                var coreRow = row - quietZoneSize;
                var coreCol = col - quietZoneSize;
                original[row, col] = (coreRow + coreCol) % 3 == 0;
            }
        }

        // Verify QuietZone is white (false/0)
        for (int i = 0; i < fullSize; i++)
        {
            // Top and bottom rows
            for (int row = 0; row < quietZoneSize; row++)
            {
                await Assert.That(original[row, i]).IsFalse().Because($"Top QuietZone should be white at ({row}, {i})");
                await Assert.That(original[fullSize - 1 - row, i]).IsFalse().Because($"Bottom QuietZone should be white at ({fullSize - 1 - row}, {i})");
            }
            // Left and right columns
            for (int col = 0; col < quietZoneSize; col++)
            {
                await Assert.That(original[i, col]).IsFalse().Because($"Left QuietZone should be white at ({i}, {col})");
                await Assert.That(original[i, fullSize - 1 - col]).IsFalse().Because($"Right QuietZone should be white at ({i}, {fullSize - 1 - col})");
            }
        }

        // Serialize and deserialize with same QuietZone size
        var rawData = original.GetRawData();
        var restored = new QRCodeData(rawData, quietZoneSize: quietZoneSize);

        await Assert.That(restored.Version).IsEqualTo(original.Version);
        await Assert.That(restored.Size).IsEqualTo(original.Size);

        // Verify core data is preserved
        for (int row = quietZoneSize; row < fullSize - quietZoneSize; row++)
        {
            for (int col = quietZoneSize; col < fullSize - quietZoneSize; col++)
            {
                await Assert.That(restored[row, col]).IsEqualTo(original[row, col]);
            }
        }

        // Verify QuietZone is restored as white
        for (int i = 0; i < fullSize; i++)
        {
            for (int row = 0; row < quietZoneSize; row++)
            {
                await Assert.That(restored[row, i]).IsFalse();
                await Assert.That(restored[fullSize - 1 - row, i]).IsFalse();
            }
            for (int col = 0; col < quietZoneSize; col++)
            {
                await Assert.That(restored[i, col]).IsFalse();
                await Assert.That(restored[i, fullSize - 1 - col]).IsFalse();
            }
        }
    }

    /// <summary>
    /// Tests that different QuietZone sizes can be specified during deserialization
    /// Serialized data contains only core data, so any QuietZone size can be applied
    /// </summary>
    [Test]
    [Arguments(2, 0, 4)]   // Serialize with no QuietZone, deserialize with QuietZone 4
    [Arguments(2, 4, 0)]   // Serialize with QuietZone 4, deserialize with no QuietZone
    [Arguments(2, 2, 8)]   // Serialize with QuietZone 2, deserialize with QuietZone 8
    [Arguments(5, 4, 2)]   // Serialize with QuietZone 4, deserialize with QuietZone 2
    public async Task Serialization_DifferentQuietZoneSizes_WorksCorrectly(int version, int serializeQuietZone, int deserializeQuietZone)
    {
        var baseSize = QRCodeData.SizeFromVersion(version);

        // Create with first QuietZone size
        var original = new QRCodeData(version, quietZoneSize: serializeQuietZone);
        var originalFullSize = baseSize + (serializeQuietZone * 2);

        // Set pattern in core area
        for (int row = serializeQuietZone; row < originalFullSize - serializeQuietZone; row++)
        {
            for (int col = serializeQuietZone; col < originalFullSize - serializeQuietZone; col++)
            {
                var coreRow = row - serializeQuietZone;
                var coreCol = col - serializeQuietZone;
                original[row, col] = (coreRow * baseSize + coreCol) % 7 == 0;
            }
        }

        // Serialize and deserialize with different QuietZone size
        var rawData = original.GetRawData();
        var restored = new QRCodeData(rawData, quietZoneSize: deserializeQuietZone);

        var restoredFullSize = baseSize + (deserializeQuietZone * 2);

        await Assert.That(restored.Version).IsEqualTo(version);
        await Assert.That(restored.Size).IsEqualTo(restoredFullSize);

        // Verify core data matches
        for (int coreRow = 0; coreRow < baseSize; coreRow++)
        {
            for (int coreCol = 0; coreCol < baseSize; coreCol++)
            {
                var originalRow = coreRow + serializeQuietZone;
                var originalCol = coreCol + serializeQuietZone;
                var restoredRow = coreRow + deserializeQuietZone;
                var restoredCol = coreCol + deserializeQuietZone;

                await Assert.That(restored[restoredRow, restoredCol]).IsEqualTo(original[originalRow, originalCol]);
            }
        }
    }

    // IBufferWriter<byte> Test

    [Test]
    [Arguments(1, 0)]   // Version 1, no quiet zone
    [Arguments(1, 4)]   // Version 1, with quiet zone
    [Arguments(10, 0)]  // Version 10, no quiet zone
    [Arguments(10, 4)]  // Version 10, with quiet zone
    [Arguments(40, 0)]  // Version 40 (max), no quiet zone
    [Arguments(40, 4)]  // Version 40 (max), with quiet zone
    public async Task GetRawData_ArrayAndBufferWriter_ProduceSameResult(int version, int quietZoneSize)
    {
        // Arrange
        var qrData = CreateTestQRCode(version, quietZoneSize);

        // Act
        var arrayResult = qrData.GetRawData();

        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        var bytesWritten = qrData.GetRawData(writer);

        // Assert
        await Assert.That(bytesWritten).IsEqualTo(arrayResult.Length);
        await Assert.That(writer.WrittenSpan.ToArray()).IsEquivalentTo(arrayResult, CollectionOrdering.Matching);
    }

    [Test]
    public async Task GetRawData_BufferWriter_AdvancesCorrectly()
    {
        // Arrange
        var qrData = CreateTestQRCode(1, 0);
        var writer = new System.Buffers.ArrayBufferWriter<byte>();

        // Act
        var bytesWritten = qrData.GetRawData(writer);

        // Assert
        await Assert.That(writer.WrittenCount).IsEqualTo(bytesWritten);
        await Assert.That(writer.WrittenCount > 0).IsTrue();

        // Verify header
        var data = writer.WrittenSpan.ToArray();
        await Assert.That(data[0]).IsEqualTo((byte)0x51);
        await Assert.That(data[1]).IsEqualTo((byte)0x52);
        await Assert.That(data[2]).IsEqualTo((byte)0x52);
    }

    [Test]
    public async Task GetRawData_BufferWriter_CanBeCalledMultipleTimes()
    {
        // Arrange
        var qrData = CreateTestQRCode(1, 0);
        var writer = new System.Buffers.ArrayBufferWriter<byte>();

        // Act
        var bytesWritten1 = qrData.GetRawData(writer);
        var data1 = writer.WrittenSpan.ToArray();

        writer.Clear();

        var bytesWritten2 = qrData.GetRawData(writer);
        var data2 = writer.WrittenSpan.ToArray();

        // Assert
        await Assert.That(bytesWritten2).IsEqualTo(bytesWritten1);
        await Assert.That(data2).IsEquivalentTo(data1, CollectionOrdering.Matching);
    }

    [Test]
    public async Task GetRawDataSize_ReturnsCorrectSize()
    {
        // Arrange
        var qrData = CreateTestQRCode(1, 0);

        // Act
        var expectedSize = qrData.GetRawDataSize();
        var actualData = qrData.GetRawData();

        // Assert
        await Assert.That(actualData.Length).IsEqualTo(expectedSize);
    }

    [Test]
    [Arguments(1)]
    [Arguments(5)]
    [Arguments(10)]
    [Arguments(20)]
    [Arguments(40)]
    public async Task Roundtrip_WithBufferWriter_PreservesData(int version)
    {
        // Arrange
        var original = CreateTestQRCode(version, quietZoneSize: 4);

        // Act - Serialize
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        original.GetRawData(writer);
        var serialized = writer.WrittenSpan.ToArray();

        // Act - Deserialize
        var restored = new QRCodeData(serialized, quietZoneSize: 4);

        // Assert
        await Assert.That(restored.Version).IsEqualTo(original.Version);
        await Assert.That(restored.Size).IsEqualTo(original.Size);
        await AssertQRCodeDataEqual(original, restored);
    }

    [Test]
    public async Task GetRawData_BufferWriter_WorksWithCustomWriter()
    {
        // Arrange
        var qrData = CreateTestQRCode(1, 0);
        var customWriter = new TestBufferWriter();

        // Act
        var bytesWritten = qrData.GetRawData(customWriter);

        // Assert
        await Assert.That(customWriter.WrittenCount).IsEqualTo(bytesWritten);
        await Assert.That(customWriter.AdvanceCalled).IsTrue().Because("Advance() must be called");
    }

    [Test]
    [Arguments(1, 0, 4)]   // Serialize with no QuietZone, deserialize with QuietZone 4
    [Arguments(2, 4, 0)]   // Serialize with QuietZone 4, deserialize with no QuietZone
    [Arguments(5, 4, 2)]   // Different QuietZone sizes
    public async Task BufferWriter_DifferentQuietZoneSizes_WorksCorrectly(int version, int serializeQuietZone, int deserializeQuietZone)
    {
        var baseSize = QRCodeData.SizeFromVersion(version);

        // Create with first QuietZone size
        var original = new QRCodeData(version, quietZoneSize: serializeQuietZone);
        var originalFullSize = baseSize + (serializeQuietZone * 2);

        // Set pattern in core area
        for (int row = serializeQuietZone; row < originalFullSize - serializeQuietZone; row++)
        {
            for (int col = serializeQuietZone; col < originalFullSize - serializeQuietZone; col++)
            {
                var coreRow = row - serializeQuietZone;
                var coreCol = col - serializeQuietZone;
                original[row, col] = (coreRow * baseSize + coreCol) % 7 == 0;
            }
        }

        // Serialize with BufferWriter and deserialize
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        original.GetRawData(writer);
        var rawData = writer.WrittenSpan.ToArray();

        var restored = new QRCodeData(rawData, quietZoneSize: deserializeQuietZone);

        var restoredFullSize = baseSize + (deserializeQuietZone * 2);

        await Assert.That(restored.Version).IsEqualTo(version);
        await Assert.That(restored.Size).IsEqualTo(restoredFullSize);

        // Verify core data matches
        for (int coreRow = 0; coreRow < baseSize; coreRow++)
        {
            for (int coreCol = 0; coreCol < baseSize; coreCol++)
            {
                var originalRow = coreRow + serializeQuietZone;
                var originalCol = coreCol + serializeQuietZone;
                var restoredRow = coreRow + deserializeQuietZone;
                var restoredCol = coreCol + deserializeQuietZone;

                await Assert.That(restored[restoredRow, restoredCol]).IsEqualTo(original[originalRow, originalCol]);
            }
        }
    }

    [Test]
    public async Task GetRawData_BufferWriter_ReusesBufferEfficiently()
    {
        // Arrange
        var qrData1 = CreateTestQRCode(1, 0);
        var qrData2 = CreateTestQRCode(2, 0);
        var writer = new System.Buffers.ArrayBufferWriter<byte>();

        // Act - First write
        var bytes1 = qrData1.GetRawData(writer);
        var data1 = writer.WrittenSpan.ToArray();

        writer.Clear();

        // Act - Second write (buffer reused)
        var bytes2 = qrData2.GetRawData(writer);
        var data2 = writer.WrittenSpan.ToArray();

        // Assert
        await Assert.That(bytes2).IsNotEqualTo(bytes1); // Different sizes
        await Assert.That(data2).IsNotEqualTo(data1); // Different data

        // Both should produce valid QR codes
        var restored1 = new QRCodeData(data1, quietZoneSize: 0);
        var restored2 = new QRCodeData(data2, quietZoneSize: 0);

        await Assert.That(restored1.Version).IsEqualTo(1);
        await Assert.That(restored2.Version).IsEqualTo(2);
    }

    // helpers

    private static QRCodeData CreateTestQRCode(int version, int quietZoneSize)
    {
        var qrData = new QRCodeData(version, quietZoneSize);

        // Fill with test pattern
        var coreSize = QRCodeData.SizeFromVersion(version);
        var fullSize = coreSize + (quietZoneSize * 2);

        for (var row = quietZoneSize; row < fullSize - quietZoneSize; row++)
        {
            for (var col = quietZoneSize; col < fullSize - quietZoneSize; col++)
            {
                // Checkerboard-like pattern using prime number
                var coreRow = row - quietZoneSize;
                var coreCol = col - quietZoneSize;
                var index = coreRow * coreSize + coreCol;
                var isDark = index % 7 == 0; // Prime number to avoid alignment with byte boundaries
                qrData[row, col] = isDark;
            }
        }

        return qrData;
    }

    private static async Task AssertQRCodeDataEqual(QRCodeData expected, QRCodeData actual)
    {
        await Assert.That(actual.Version).IsEqualTo(expected.Version);
        await Assert.That(actual.Size).IsEqualTo(expected.Size);

        for (var row = 0; row < expected.Size; row++)
        {
            for (var col = 0; col < expected.Size; col++)
            {
                await Assert.That(actual[row, col]).IsEqualTo(expected[row, col]);
            }
        }
    }

    // Custom test writer to verify Advance() is called
    private class TestBufferWriter : System.Buffers.IBufferWriter<byte>
    {
        private readonly System.Buffers.ArrayBufferWriter<byte> _inner = new();
        public bool AdvanceCalled { get; private set; }
        public int WrittenCount => _inner.WrittenCount;

        public void Advance(int count)
        {
            AdvanceCalled = true;
            _inner.Advance(count);
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
    }
}
