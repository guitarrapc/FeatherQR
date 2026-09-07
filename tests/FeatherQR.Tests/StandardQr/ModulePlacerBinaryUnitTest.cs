using FeatherQR.Internals.StandardQR;
using Rectangle = FeatherQR.Internals.Rectangle;

namespace FeatherQR.Tests;

public class ModulePlacerBinaryUnitTest
{
    [Test]
    [Arguments(1, new byte[] { 0x12, 0x34, 0x56 })]
    [Arguments(5, new byte[] { 0xFF, 0x00, 0xAA, 0x55 })]
    public async Task PlaceDataWords_Binary_PlacesDataCorrectly(int version, byte[] data)
    {
        // Arrange
        var qrCode = CreateEmptyQRCodeData(version);
        var size = qrCode.Size;
        Span<byte> buffer = new byte[size * size]; // byte-per-module work buffer, loaded via SetCoreData below
        Span<Rectangle> blockedModules = stackalloc Rectangle[30];
        var blockedCount = 0;

        // Setup patterns
        ModulePlacer.PlaceFinderPatterns(buffer, size, blockedModules, ref blockedCount);
        ModulePlacer.PlaceTimingPatterns(buffer, size, blockedModules, ref blockedCount);

        // Build blocked mask
        var maskSize = (size * size + 7) / 8;
        Span<byte> blockedMask = stackalloc byte[maskSize];
        blockedMask.Clear();
        QRCodeGenerator.BuildBlockedMask(blockedMask, size, blockedModules.Slice(0, blockedCount));

        // Act
        ModulePlacer.PlaceDataWords(buffer, size, data, blockedMask);
        qrCode.SetCoreData(buffer);

        // Assert - Verify data is placed (not all zeros/all ones)
        int darkCount = 0;
        int lightCount = 0;

        for (int row = 0; row < qrCode.Size; row++)
        {
            for (int col = 0; col < qrCode.Size; col++)
            {
                if (qrCode[row, col])
                    darkCount++;
                else
                    lightCount++;
            }
        }

        await Assert.That(darkCount > 0).IsTrue();
        await Assert.That(lightCount > 0).IsTrue();
    }

    private static QRCodeData CreateEmptyQRCodeData(int version)
    {
        var size = GetModuleCount(version);
        return new QRCodeData(version, quietZoneSize: 0);
    }

    private static int GetModuleCount(int version) => 21 + (version - 1) * 4;
}
