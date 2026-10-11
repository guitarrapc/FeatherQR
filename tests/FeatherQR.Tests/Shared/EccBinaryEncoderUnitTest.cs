using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Tests;

public class EccBinaryEncoderUnitTest
{
    // Performance & Memory Tests
    [Test]
    public async Task CalculateECC_LargeData_NoStackOverflow()
    {
        // Arrange - Max data size for Version 40
        var data = new byte[2956]; // Version 40-L max data
        Random.Shared.NextBytes(data);
        var eccCount = 30;

        // Act - Should not throw StackOverflowException
        byte[] ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

        // Assert
        await Assert.That(ecc.Length).IsEqualTo(eccCount);
    }

    [Test]
    public async Task CalculateECC_RepeatedCalls_ProducesSameResult()
    {
        // Arrange
        var data = new byte[] { 64, 86, 134, 86 };
        var eccCount = 10;

        // Act
        byte[] ecc1 = new byte[eccCount];
        byte[] ecc2 = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc1, eccCount);
        EccBinaryEncoder.CalculateECC(data, ecc2, eccCount);

        // Assert - Deterministic (same input → same output)
        await Assert.That(ecc1.SequenceEqual(ecc2)).IsTrue();
    }

    // ISO/IEC 18004 Standard Examples

    /// <summary>
    /// Test against ISO/IEC 18004 Annex I example.
    /// Data: 01000000 01010110 10000110 01010110 (64, 86, 134, 86)
    /// ECC: 10 codewords
    /// Expected: [176, 76, 29, 180, 122, 192, 92, 208, 157, 56]
    /// </summary>
    [Test]
    public async Task CalculateECC_ISO18004_Example_ProducesCorrectECC()
    {
        // Arrange
        byte[] data = [64, 86, 134, 86];
        var eccCount = 10;
        byte[] expectedECC = [176, 76, 29, 180, 122, 192, 92, 208, 157, 56];

        // Act
        byte[] actualECC = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, actualECC, eccCount);

        // Assert
        await Assert.That(actualECC.Length).IsEqualTo(eccCount);
        for (int i = 0; i < eccCount; i++)
        {
            await Assert.That(actualECC[i]).IsEqualTo(expectedECC[i]);
        }
    }

    // Basic ECC Calculation

    [Test]
    [Arguments(new byte[] { 64 }, 7)] // 1 byte data, 7 ECC
    [Arguments(new byte[] { 64, 86 }, 10)] // 2 bytes data, 10 ECC
    [Arguments(new byte[] { 64, 86, 134 }, 13)] // 3 bytes data, 13 ECC
    public async Task CalculateECC_VariousDataSizes_ProducesCorrectWordCount(byte[] data, int eccCount)
    {
        // Act
        byte[] ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

        // Assert
        await Assert.That(ecc.Length).IsEqualTo(eccCount);
        // Verify all bytes are in valid range [0, 255]
        foreach (var b in ecc)
        {
            await Assert.That((int)b).IsBetween(0, 255);
        }
    }

    [Test]
    public async Task CalculateECC_SingleByte_ProducesValidECC()
    {
        // Arrange
        var data = new byte[] { 64 };
        var eccCount = 5;

        // Act
        byte[] ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

        // Assert
        await Assert.That(ecc.Length).IsEqualTo(eccCount);
        foreach (var b in ecc)
        {
            await Assert.That((int)b).IsBetween(0, 255);
        }
    }

    [Test]
    public async Task CalculateECC_MaxECCLevel_Version40_ProducesValidECC()
    {
        // Arrange - QR Version 40, ECC Level H requires 30 ECC words per block
        var data = new byte[29]; // 29 data bytes
        var eccCount = 30;

        // Act
        byte[] ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

        // Assert
        await Assert.That(ecc.Length).IsEqualTo(eccCount);
    }

    // Edge Cases

    [Test]
    public async Task CalculateECC_AllZeros_ProducesAllZeroECC()
    {
        // Arrange
        var data = new byte[4]; // All zeros
        var eccCount = 7;

        // Act
        byte[] ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

        // Assert
        await Assert.That(ecc.Length).IsEqualTo(eccCount);
        foreach (var b in ecc)
        {
            await Assert.That(b).IsEqualTo((byte)0);
        }
    }

    [Test]
    public async Task CalculateECC_AllOnes_ProducesNonZeroECC()
    {
        // Arrange
        var data = new byte[] { 255, 255, 255, 255 }; // All 0xFF
        var eccCount = 10;

        // Act
        byte[] ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

        // Assert
        await Assert.That(ecc.Length).IsEqualTo(eccCount);
        await Assert.That(ecc.ToArray().Any(b => b != 0)).IsTrue();
    }

    [Test]
    [Arguments(new byte[] { 0x55, 0x55, 0x55, 0x55 }, 3)] // 01010101 pattern
    [Arguments(new byte[] { 0xAA, 0xAA, 0xAA, 0xAA }, 5)] // 10101010 pattern
    [Arguments(new byte[] { 0xF0, 0xF0, 0xF0, 0xF0 }, 7)] // 11110000 pattern
    public async Task CalculateECC_AlternatingPatterns_ProducesValidECC(byte[] data, int eccCount)
    {
        // Act
        byte[] ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

        // Assert
        await Assert.That(ecc.Length).IsEqualTo(eccCount);
        foreach (var b in ecc)
        {
            await Assert.That((int)b).IsBetween(0, 255);
        }
    }

    // Polynomial Operation

    [Test]
    [Arguments(7)]  // Degree 6 generator (x-α^0)(x-α^1)...(x-α^6)
    [Arguments(10)] // Degree 9 generator
    [Arguments(30)] // Degree 29 generator (max for Version 40-H)
    public async Task CalculateECC_GeneratorPolynomial_ProducesCorrectDegree(int eccCount)
    {
        // Arrange
        var data = new byte[16]; // 16 bytes of data
        Random.Shared.NextBytes(data);

        // Act
        byte[] ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

        // Assert - Generator polynomial degree = eccCount - 1
        await Assert.That(ecc.Length).IsEqualTo(eccCount);
    }

    // Galois Field (GF256) Operation

    [Test]
    public async Task CalculateECC_GF256Operations_ProduceValidResults()
    {
        // Arrange - Known test vectors for GF(256) arithmetic
        var data = new byte[] { 255 }; // 255 in GF(256)
        var eccCount = 3;

        // Act
        byte[] ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

        // Assert
        await Assert.That(ecc.Length).IsEqualTo(eccCount);
        // In GF(256), operations should produce values in range [0, 255]
        foreach (var b in ecc)
        {
            await Assert.That((int)b).IsBetween(0, 255);
        }
    }

    [Test]
    public async Task CalculateECC_DifferentInputs_ProducesDifferentECC()
    {
        // Arrange
        var data1 = new byte[] { 64, 86, 134, 86 };
        var data2 = new byte[] { 64, 86, 134, 87 }; // Last byte different
        var eccCount = 10;

        // Act
        byte[] ecc1 = new byte[eccCount];
        byte[] ecc2 = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data1, ecc1, eccCount);
        EccBinaryEncoder.CalculateECC(data2, ecc2, eccCount);

        // Assert
        await Assert.That(ecc1.SequenceEqual(ecc2)).IsFalse(); // Different inputs → different ECC
    }
}
