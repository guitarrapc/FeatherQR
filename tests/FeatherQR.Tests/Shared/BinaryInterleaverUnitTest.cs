using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals;
using static FeatherQR.Internals.StandardQR.QRCodeConstants;

namespace FeatherQR.Tests;

public class BinaryInterleaverUnitTest
{
    // Basic Interleaving
    [Test]
    internal async Task InterleaveCodewords_TwoBlocks_CorrectOrder()
    {
        var version = 1;
        // Arrange
        var eccInfo = new ECCInfo(version, QREccLevel.M, 6, 2, 2, 3, 0, 0);

        // data: Block1 (D1,D2,D3) + Block2 (D4,D5,D6)
        byte[] allData = [0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6];

        // ecc: Block1 (E1,E2) + Block2 (E3,E4)
        byte[] allEcc = [0xE1, 0xE2, 0xE3, 0xE4];

        var size = BinaryInterleaver.CalculateInterleavedSize(eccInfo, GetRemainderBits(version));
        byte[] output = new byte[size];

        // Act
        BinaryInterleaver.InterleaveCodewords(allData, allEcc, output, eccInfo);

        // Assert
        // Expected: D1, D4, D2, D5, D3, D6 | E1, E3, E2, E4
        await Assert.That(output[0]).IsEqualTo((byte)0xD1);
        await Assert.That(output[1]).IsEqualTo((byte)0xD4);
        await Assert.That(output[2]).IsEqualTo((byte)0xD2);
        await Assert.That(output[3]).IsEqualTo((byte)0xD5);
        await Assert.That(output[4]).IsEqualTo((byte)0xD3);
        await Assert.That(output[5]).IsEqualTo((byte)0xD6);
        await Assert.That(output[6]).IsEqualTo((byte)0xE1);
        await Assert.That(output[7]).IsEqualTo((byte)0xE3);
        await Assert.That(output[8]).IsEqualTo((byte)0xE2);
        await Assert.That(output[9]).IsEqualTo((byte)0xE4);
    }

    [Test]
    public async Task InterleaveCodewords_UnequalBlockSizes_CorrectPadding()
    {
        var version = 5;
        // Arrange - Group 1 (3 bytes), Group 2 (4 bytes)
        var eccInfo = new ECCInfo(version, QREccLevel.H, 7, 2, 1, 3, 1, 4);

        // data: Group1 Block0 (3 bytes) + Group2 Block0 (4 bytes)
        byte[] allData = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07];

        // ECC: Block0 (2 bytes) + Block1 (2 bytes)
        byte[] allEcc = [0xE1, 0xE2, 0xE3, 0xE4];

        var size = BinaryInterleaver.CalculateInterleavedSize(eccInfo, GetRemainderBits(version));
        byte[] output = new byte[size];

        // Act
        BinaryInterleaver.InterleaveCodewords(allData, allEcc, output, eccInfo);

        // Assert
        // Expected: 01, 04, 02, 05, 03, 06, 07 | E1, E3, E2, E4
        await Assert.That(output[0]).IsEqualTo((byte)0x01);
        await Assert.That(output[1]).IsEqualTo((byte)0x04);
        await Assert.That(output[2]).IsEqualTo((byte)0x02);
        await Assert.That(output[3]).IsEqualTo((byte)0x05);
        await Assert.That(output[4]).IsEqualTo((byte)0x03);
        await Assert.That(output[5]).IsEqualTo((byte)0x06);
        await Assert.That(output[6]).IsEqualTo((byte)0x07);
        await Assert.That(output[7]).IsEqualTo((byte)0xE1);
        await Assert.That(output[8]).IsEqualTo((byte)0xE3);
        await Assert.That(output[9]).IsEqualTo((byte)0xE2);
        await Assert.That(output[10]).IsEqualTo((byte)0xE4);
    }

    [Test]
    public async Task InterleaveCodewords_SingleBlock_NoInterleaving()
    {
        var version = 1;
        // Arrange: 1 block (no interleaves)
        var eccInfo = GetEccInfo(version, QREccLevel.L);

        byte[] allData = new byte[19];
        for (int i = 0; i < 19; i++)
        {
            allData[i] = (byte)(i + 1);
        }

        byte[] allEcc = new byte[7];
        for (int i = 0; i < 7; i++)
        {
            allEcc[i] = (byte)(0xE0 + i);
        }

        var size = BinaryInterleaver.CalculateInterleavedSize(eccInfo, GetRemainderBits(version));
        byte[] output = new byte[size];

        // Act
        BinaryInterleaver.InterleaveCodewords(allData, allEcc, output, eccInfo);

        // Assert - No interleaving, data and ECC remain in original order
        for (int i = 0; i < 19; i++)
        {
            await Assert.That(output[i]).IsEqualTo(allData[i]);
        }
        for (int i = 0; i < 7; i++)
        {
            await Assert.That(output[19 + i]).IsEqualTo(allEcc[i]);
        }
    }

    [Test]
    public async Task InterleaveCodewords_MaximumBlocks_Version40()
    {
        var version = 40;
        // Arrange: Version 40 (maximum 30 blocks)
        var eccInfo = GetEccInfo(version, QREccLevel.H);

        byte[] allData = new byte[eccInfo.TotalDataCodewords];
        byte[] allEcc = new byte[(eccInfo.BlocksInGroup1 + eccInfo.BlocksInGroup2) * eccInfo.ECCPerBlock];

        // Generate data
        for (int i = 0; i < allData.Length; i++)
        {
            allData[i] = (byte)((i + 1) % 256); // 1,2,3,...255,0,1,2...
        }
        for (int i = 0; i < allEcc.Length; i++)
        {
            allEcc[i] = (byte)((i + 100) % 256);
        }

        var size = BinaryInterleaver.CalculateInterleavedSize(eccInfo, GetRemainderBits(version));
        byte[] output = new byte[size];

        // Act
        BinaryInterleaver.InterleaveCodewords(allData, allEcc, output, eccInfo);

        // Assert
        await Assert.That(output.Length).IsEqualTo(size);
        await Assert.That(output[0]).IsNotEqualTo((byte)0);
    }

    [Test]
    [Arguments(1, QREccLevel.L)]
    [Arguments(10, QREccLevel.M)]
    [Arguments(20, QREccLevel.Q)]
    [Arguments(40, QREccLevel.H)]
    public async Task CalculateInterleavedSize_AllVersions_CorrectSize(int version, QREccLevel level)
    {
        // Arrange
        var eccInfo = GetEccInfo(version, level);

        // Act
        var size = BinaryInterleaver.CalculateInterleavedSize(eccInfo, GetRemainderBits(version));

        // Assert
        int expectedDataBytes = eccInfo.TotalDataCodewords;
        int expectedEccBytes = (eccInfo.BlocksInGroup1 + eccInfo.BlocksInGroup2) * eccInfo.ECCPerBlock;
        int expectedSize = expectedDataBytes + expectedEccBytes;

        await Assert.That(size >= expectedSize).IsTrue(); // consider remainder bits
        await Assert.That(size <= expectedSize + 1).IsTrue(); // maximum added 1 byte
    }
}
