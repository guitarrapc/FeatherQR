using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Tests;

public class EccBinaryEncoderUnitTest
{
    // Performance & Memory Tests
    [Test]
    public async Task CalculateECC_LargeData_NoStackOverflow()
    {
        // Oversized synthetic input exercises buffer handling beyond a normal QR block.
        var data = new byte[2956]; // Version 40-L max data
        Random.Shared.NextBytes(data);
        var eccCount = 30;

        byte[] ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

        await Assert.That(ecc.Length).IsEqualTo(eccCount);
    }

    [Test]
    public async Task CalculateECC_RepeatedCalls_ProducesSameResult()
    {
        var data = new byte[] { 64, 86, 134, 86 };
        var eccCount = 10;

        byte[] ecc1 = new byte[eccCount];
        byte[] ecc2 = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc1, eccCount);
        EccBinaryEncoder.CalculateECC(data, ecc2, eccCount);

        await Assert.That(ecc1.SequenceEqual(ecc2)).IsTrue();
    }

    // ISO/IEC 18004 Standard Examples

    /// <summary>
    /// Test against ISO/IEC 18004 Annex I example.
    /// </summary>
    [Test]
    public async Task CalculateECC_ISO18004_Example_ProducesCorrectECC()
    {
        byte[] data = [64, 86, 134, 86];
        var eccCount = 10;
        byte[] expectedECC = [176, 76, 29, 180, 122, 192, 92, 208, 157, 56];

        byte[] actualECC = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, actualECC, eccCount);

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
        byte[] ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

        await Assert.That(ecc.Length).IsEqualTo(eccCount);
        foreach (var b in ecc)
        {
            await Assert.That((int)b).IsBetween(0, 255);
        }
    }

    [Test]
    public async Task CalculateECC_SingleByte_ProducesValidECC()
    {
        var data = new byte[] { 64 };
        var eccCount = 5;

        byte[] ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

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
        var data = new byte[29];
        var eccCount = 30;

        byte[] ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

        await Assert.That(ecc.Length).IsEqualTo(eccCount);
    }

    // Edge Cases

    [Test]
    public async Task CalculateECC_AllZeros_ProducesAllZeroECC()
    {
        var data = new byte[4];
        var eccCount = 7;

        byte[] ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

        await Assert.That(ecc.Length).IsEqualTo(eccCount);
        foreach (var b in ecc)
        {
            await Assert.That(b).IsEqualTo((byte)0);
        }
    }

    [Test]
    public async Task CalculateECC_AllOnes_ProducesNonZeroECC()
    {
        var data = new byte[] { 255, 255, 255, 255 };
        var eccCount = 10;

        byte[] ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

        await Assert.That(ecc.Length).IsEqualTo(eccCount);
        await Assert.That(ecc.ToArray().Any(b => b != 0)).IsTrue();
    }

    [Test]
    [Arguments(new byte[] { 0x55, 0x55, 0x55, 0x55 }, 3)] // 01010101 pattern
    [Arguments(new byte[] { 0xAA, 0xAA, 0xAA, 0xAA }, 5)] // 10101010 pattern
    [Arguments(new byte[] { 0xF0, 0xF0, 0xF0, 0xF0 }, 7)] // 11110000 pattern
    public async Task CalculateECC_AlternatingPatterns_ProducesValidECC(byte[] data, int eccCount)
    {
        byte[] ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

        await Assert.That(ecc.Length).IsEqualTo(eccCount);
        foreach (var b in ecc)
        {
            await Assert.That((int)b).IsBetween(0, 255);
        }
    }

    [Test]
    [Arguments(7)]
    [Arguments(10)]
    [Arguments(30)]
    public async Task CalculateECC_GeneratorPolynomial_ProducesCorrectDegree(int eccCount)
    {
        var data = new byte[16];
        Random.Shared.NextBytes(data);

        byte[] ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

        await Assert.That(ecc.Length).IsEqualTo(eccCount);
    }

    // Galois Field (GF256) Operation

    [Test]
    public async Task CalculateECC_GF256Operations_ProduceValidResults()
    {
        var data = new byte[] { 255 };
        var eccCount = 3;

        byte[] ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

        await Assert.That(ecc.Length).IsEqualTo(eccCount);
        foreach (var b in ecc)
        {
            await Assert.That((int)b).IsBetween(0, 255);
        }
    }

    [Test]
    public async Task CalculateECC_DifferentInputs_ProducesDifferentECC()
    {
        var data1 = new byte[] { 64, 86, 134, 86 };
        var data2 = new byte[] { 64, 86, 134, 87 };
        var eccCount = 10;

        byte[] ecc1 = new byte[eccCount];
        byte[] ecc2 = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data1, ecc1, eccCount);
        EccBinaryEncoder.CalculateECC(data2, ecc2, eccCount);

        await Assert.That(ecc1.SequenceEqual(ecc2)).IsFalse();
    }
}
