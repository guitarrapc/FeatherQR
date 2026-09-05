using System.Text;
using FeatherQR.Internals;
using static FeatherQR.QRCodeGenerator;

namespace FeatherQR.Tests;

// NOTE: Actual QR Code size is always same for same version and ECC level,
//
// Version 2, ECC-L, Numeric mode (Minimum data for 42 digits)
// Total capacity: 44 bytes = 352 bits
// 髫ｨ荳橸ｽｨ・ｯ隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ荳翫・// 髫ｨ荳翫・1. Mode indicator: 4 bits                髫ｨ荳翫・// 髫ｨ荳翫・2. Character count: 10 bits              髫ｨ荳翫・// 髫ｨ荳翫・3. Data (42 digits): ~140 bits           髫ｨ荳翫・// 髫ｨ荳翫・4. Terminator: 4 bits                    髫ｨ荳翫・// 髫ｨ荳翫・5. Byte alignment: 0-7 bits              髫ｨ荳翫・// 髫ｨ荳翫・6. Padding: ~190 bits (0xEC 0x11...)     髫ｨ荳翫・// 髫ｨ荳翫・7. ECC: Fixed length                     髫ｨ荳翫・// 髫ｨ荵励飴隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ謫ｾ・ｽ・､
// 髫ｨ荳翫・Total: 352 bits (44 bytes)               髫ｨ荳翫・// 髫ｨ荳翫・Matrix size: 25 繝ｻ繝ｻ繝ｻ25 (Version 2)         髫ｨ荳翫・// 髫ｨ荳翫・With quiet zone: 33 繝ｻ繝ｻ繝ｻ33                髫ｨ荳翫・// 髫ｨ荵怜繭隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ荳翫・//
// Version 2, ECC-L, Numeric mode (Maximum data for 77 digits)
// Total capacity: 44 bytes = 352 bits (Same as minimum data for same version)
//
// 髫ｨ荳橸ｽｨ・ｯ隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ荳翫・// 髫ｨ荳翫・1. Mode indicator: 4 bits                髫ｨ荳翫・// 髫ｨ荳翫・2. Character count: 10 bits              髫ｨ荳翫・// 髫ｨ荳翫・3. Data (77 digits): ~257 bits           髫ｨ荳翫・// 髫ｨ荳翫・4. Terminator: 4 bits                    髫ｨ荳翫・// 髫ｨ荳翫・5. Byte alignment: 0-7 bits              髫ｨ荳翫・// 髫ｨ荳翫・6. Padding: ~70 bits (0xEC 0x11...)      髫ｨ荳翫・// 髫ｨ荳翫・7. ECC: Fixed length                     髫ｨ荳翫・// 髫ｨ荵励飴隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ謫ｾ・ｽ・､
// 髫ｨ荳翫・Total: 352 bits (44 bytes)               髫ｨ荳翫・// 髫ｨ荳翫・Matrix size: 25 繝ｻ繝ｻ繝ｻ25 (Version 2)         髫ｨ荳翫・// 髫ｨ荳翫・With quiet zone: 33 繝ｻ繝ｻ繝ｻ33                 髫ｨ荳翫・// 髫ｨ荵怜繭隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ荳翫・
public class QRCodeGeneratorUnitTest
{
    [Test]
    [Arguments(0)]
    [Arguments(41)]
    internal async Task CalculateMaxBitStringLength_InvalidVersionShouldFail(int version)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => CalculateMaxBitStringLength(version, QREccLevel.L, EncodingMode.Alphanumeric));
        await Assert.That(ex.Message).Contains($"Version must be 1-40, but was {version}");
    }

    [Test]
    [Arguments(1, QREccLevel.L, EncodingMode.Numeric, 152)]         // 19 繝ｻ繝ｻ繝ｻ8
    [Arguments(1, QREccLevel.M, EncodingMode.Alphanumeric, 128)]    // 16 繝ｻ繝ｻ繝ｻ8
    [Arguments(1, QREccLevel.Q, EncodingMode.Byte, 104)]            // 13 繝ｻ繝ｻ繝ｻ8
    [Arguments(1, QREccLevel.H, EncodingMode.Byte, 72)]             // 9 繝ｻ繝ｻ繝ｻ8
    [Arguments(40, QREccLevel.L, EncodingMode.Numeric, 23648)]      // 2956 繝ｻ繝ｻ繝ｻ8
    [Arguments(40, QREccLevel.M, EncodingMode.Alphanumeric, 18672)] // 2334 繝ｻ繝ｻ繝ｻ8
    [Arguments(40, QREccLevel.Q, EncodingMode.Byte, 13328)]         // 1666 繝ｻ繝ｻ繝ｻ8
    [Arguments(40, QREccLevel.H, EncodingMode.Byte, 10208)]         // 1276 繝ｻ繝ｻ繝ｻ8
    internal async Task CalculateMaxBitStringLength_ReturnsCapacityWithBuffer(int version, QREccLevel eccLevel, EncodingMode encoding, int expected)
    {
        var actual = CalculateMaxBitStringLength(version, eccLevel, encoding);
        await Assert.That(actual).IsEquivalentTo(expected);
    }

    [Test]
    public async Task CalculateMaxBitStringLength_IsIndependentOfInputText()
    {
        // Capacity doesn't depend on input text (always padded to full capacity)
        var capacity1 = CalculateMaxBitStringLength(1, QREccLevel.L, EncodingMode.Byte);
        var capacity2 = CalculateMaxBitStringLength(1, QREccLevel.L, EncodingMode.Byte);

        await Assert.That(capacity2).IsEqualTo(capacity1);
    }

    // Encoding Mode Tests - Numeric

    // QR Code Data Encoding Overview
    // 髫ｨ荳橸ｽｨ・ｯ隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ荳翫・    // 髫ｨ荳翫・Version 1, ECC Level H, Numeric Mode                    髫ｨ荳翫・    // 髫ｨ荳翫・Total Capacity (ECCInfo.TotalDataCodewords)             髫ｨ荳翫・    // 髫ｨ荳翫・= 9 bytes = 72 bits                                     髫ｨ荳翫・    // 髫ｨ荵励飴隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ謫ｾ・ｽ・､
    // 髫ｨ荳翫・1. Mode indicator (Numeric)                             髫ｨ荳翫・    // 髫ｨ荳翫・   = 4 bits (0001)                                      髫ｨ荳翫・    // 髫ｨ荵励飴隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ謫ｾ・ｽ・､
    // 髫ｨ荳翫・2. Character count indicator (Version 1-9)              髫ｨ荳翫・    // 髫ｨ荳翫・   = 10 bits (max 1023 digits)                          髫ｨ荳翫・    // 髫ｨ荵励飴隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ謫ｾ・ｽ・､
    // 髫ｨ荳翫・3. Data (17 digits)                                     髫ｨ荳翫・    // 髫ｨ荳翫・   = 5 groups 繝ｻ繝ｻ繝ｻ10 bits + 1 group 繝ｻ繝ｻ繝ｻ4 bits              髫ｨ荳翫・    // 髫ｨ荳翫・   = 50 + 4 = 54 bits                                   髫ｨ荳翫・    // 髫ｨ荳翫・   (3 digits per group: 000-999 驕ｶ鄙ｫ繝ｻ10 bits)              髫ｨ荳翫・    // 髫ｨ荳翫・   (Last 2 digits: 00-99 驕ｶ鄙ｫ繝ｻ7 bits, but padded to 4)     髫ｨ荳翫・    // 髫ｨ荵励飴隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ謫ｾ・ｽ・､
    // 髫ｨ荳翫・4. Terminator                                           髫ｨ荳翫・    // 髫ｨ荳翫・   = min(72 - 4 - 10 - 54, 4) = 4 bits (0000)           髫ｨ荳翫・    // 髫ｨ荵励飴隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ謫ｾ・ｽ・､
    // 髫ｨ荳翫・Total: 4 + 10 + 54 + 4 = 72 bits                        髫ｨ荳翫・    // 髫ｨ荵怜繭隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ荳翫・

    [Test]
    [Arguments("0123456789", QREccLevel.M)]
    [Arguments("123456789012345678901234567890123", QREccLevel.M)]
    public async Task Create_Numeric_ProducesValidQr(string text, QREccLevel eccLevel)
    {
        var qr = QRCodeGenerator.Create(text, eccLevel);

        await Assert.That(qr.Size >= 21).IsTrue(); // Min size = 21x21
    }

    [Test]
    [Arguments(QREccLevel.L, 41, 1)]   // V1-L max
    [Arguments(QREccLevel.M, 34, 1)]   // V1-M max
    [Arguments(QREccLevel.H, 17, 1)]   // V1-H max
    [Arguments(QREccLevel.L, 42, 2)]   // V1-L max + 1 驕ｶ鄙ｫ繝ｻshould upgrade to V2
    [Arguments(QREccLevel.M, 35, 2)]   // V1-M max + 1 驕ｶ鄙ｫ繝ｻshould upgrade to V2
    [Arguments(QREccLevel.H, 18, 2)]   // V1-H max + 1 驕ｶ鄙ｫ繝ｻshould upgrade to V2
    [Arguments(QREccLevel.L, 77, 2)]   // V2-L max
    [Arguments(QREccLevel.M, 48, 2)]   // V2-M max
    [Arguments(QREccLevel.H, 34, 2)]   // V2-H max
    public async Task Create_Numeric_Versions_MaxCapacity(QREccLevel eccLevel, int maxChars, int expectedVersion)
    {
        var expectedSize = CalculateSize(expectedVersion);

        var text = new string('1', maxChars);

        var qr = QRCodeGenerator.Create(text, eccLevel);
        var version = CalculateVersion(qr.Size);
        var actualSize = qr.Size;

        await Assert.That(version).IsEqualTo(expectedVersion);
        await Assert.That(actualSize).IsEqualTo(expectedSize);
    }

    // Encoding Mode Tests - Alphanumeric (ASCII)

    // QR Code Data Encoding Overview
    // 髫ｨ荳橸ｽｨ・ｯ隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ荳翫・    // 髫ｨ荳翫・Version 1, ECC Level H, Alphanumeric Mode               髫ｨ荳翫・    // 髫ｨ荳翫・Total Capacity (ECCInfo.TotalDataCodewords)             髫ｨ荳翫・    // 髫ｨ荳翫・= 9 bytes = 72 bits                                     髫ｨ荳翫・    // 髫ｨ荵励飴隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ謫ｾ・ｽ・､
    // 髫ｨ荳翫・1. Mode indicator (Alphanumeric)                        髫ｨ荳翫・    // 髫ｨ荳翫・   = 4 bits (0010)                                      髫ｨ荳翫・    // 髫ｨ荵励飴隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ謫ｾ・ｽ・､
    // 髫ｨ荳翫・2. Character count indicator (Version 1-9)              髫ｨ荳翫・    // 髫ｨ荳翫・   = 9 bits (max 511 chars)                             髫ｨ荳翫・    // 髫ｨ荵励飴隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ謫ｾ・ｽ・､
    // 髫ｨ荳翫・3. Data (10 chars)                                      髫ｨ荳翫・    // 髫ｨ荳翫・   = 5 groups 繝ｻ繝ｻ繝ｻ11 bits = 55 bits                       髫ｨ荳翫・    // 髫ｨ荳翫・   (2 chars per group: 00-44 驕ｶ鄙ｫ繝ｻ11 bits)                 髫ｨ荳翫・    // 髫ｨ荵励飴隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ謫ｾ・ｽ・､
    // 髫ｨ荳翫・4. Terminator                                           髫ｨ荳翫・    // 髫ｨ荳翫・   = min(72 - 4 - 9 - 55, 4) = 4 bits (0000)            髫ｨ荳翫・    // 髫ｨ荵励飴隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ謫ｾ・ｽ・､
    // 髫ｨ荳翫・Total: 4 + 9 + 55 + 4 = 72 bits                         髫ｨ荳翫・    // 髫ｨ荵怜繭隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ荳翫・
    [Test]
    [Arguments("HELLO WORLD", QREccLevel.M)]
    [Arguments("ABC-123 $%*+-./:", QREccLevel.Q)]
    public async Task Create_Alphanumeric_ProducesValidQr(string text, QREccLevel eccLevel)
    {
        var qr = QRCodeGenerator.Create(text, eccLevel);

        await Assert.That(qr.Size >= 21).IsTrue();
    }

    [Test]
    [Arguments(QREccLevel.L, 25, 1)]   // V1-L max
    [Arguments(QREccLevel.M, 20, 1)]   // V1-M max
    [Arguments(QREccLevel.H, 10, 1)]   // V1-H max
    [Arguments(QREccLevel.L, 26, 2)]   // V1-L max + 1 驕ｶ鄙ｫ繝ｻshould upgrade to V2
    [Arguments(QREccLevel.M, 21, 2)]   // V1-M max + 1 驕ｶ鄙ｫ繝ｻshould upgrade to V2
    [Arguments(QREccLevel.H, 11, 2)]   // V1-H max + 1 驕ｶ鄙ｫ繝ｻshould upgrade to V2
    [Arguments(QREccLevel.L, 47, 2)]   // V2-L max
    [Arguments(QREccLevel.M, 38, 2)]   // V2-M max
    [Arguments(QREccLevel.H, 20, 2)]   // V2-H max
    public async Task Create_Alphanumeric_Versions_MaxCapacity(QREccLevel eccLevel, int maxChars, int expectedVersion)
    {
        var expectedSize = CalculateSize(expectedVersion);

        var text = new string('A', maxChars);

        var qr = QRCodeGenerator.Create(text, eccLevel);
        var version = CalculateVersion(qr.Size);
        var actualSize = qr.Size;

        await Assert.That(version).IsEqualTo(expectedVersion);
        await Assert.That(actualSize).IsEqualTo(expectedSize);
    }

    // Encoding Mode Test - Byte (UTF-8)

    // QR Code Data Encoding Overview
    // 髫ｨ荳橸ｽｨ・ｯ隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ荳翫・    // 髫ｨ荳翫・Version 1, ECC Level H, Byte Mode (with UTF-8 ECI)      髫ｨ荳翫・    // 髫ｨ荳翫・Total Capacity (ECCInfo.TotalDataCodewords)             髫ｨ荳翫・    // 髫ｨ荳翫・= 9 bytes = 72 bits                                     髫ｨ荳翫・    // 髫ｨ荵励飴隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ謫ｾ・ｽ・､
    // 髫ｨ荳翫・1. ECI header (UTF-8)                                   髫ｨ荳翫・    // 髫ｨ荳翫・   = 4 bits (0111) + 8 bits (26 = UTF-8) = 12 bits      髫ｨ荳翫・    // 髫ｨ荵励飴隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ謫ｾ・ｽ・､
    // 髫ｨ荳翫・2. Mode indicator (Byte)                                髫ｨ荳翫・    // 髫ｨ荳翫・   = 4 bits (0100)                                      髫ｨ荳翫・    // 髫ｨ荵励飴隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ謫ｾ・ｽ・､
    // 髫ｨ荳翫・3. Character count indicator (Version 1-9)              髫ｨ荳翫・    // 髫ｨ荳翫・   = 8 bits (max 255 bytes)                             髫ｨ荳翫・    // 髫ｨ荵励飴隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ謫ｾ・ｽ・､
    // 髫ｨ荳翫・4. Data (7 bytes ASCII = "aaaaaaa")                     髫ｨ荳翫・    // 髫ｨ荳翫・   = 7 bytes 繝ｻ繝ｻ繝ｻ8 bits = 56 bits                         髫ｨ荳翫・    // 髫ｨ荵励飴隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ謫ｾ・ｽ・､
    // 髫ｨ荳翫・5. Terminator                                           髫ｨ荳翫・    // 髫ｨ荳翫・   = min(72 - 12 - 4 - 8 - 56, 4) = 0 bits (full)       髫ｨ荳翫・    // 髫ｨ荵励飴隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ謫ｾ・ｽ・､
    // 髫ｨ荳翫・Total: 12 + 4 + 8 + 56 + 0 = 80 bits                    髫ｨ荳翫・    // 髫ｨ荳翫・!! Exceeds 72 bits 驕ｶ鄙ｫ繝ｻUpgrades to Version 2              髫ｨ荳翫・    // 髫ｨ荳翫・                                                        髫ｨ荳翫・    // 髫ｨ荳翫・Without ECI header (EciMode.Default):                   髫ｨ荳翫・    // 髫ｨ荳翫・Total: 4 + 8 + 56 + 4 = 72 bits                         髫ｨ荳翫・    // 髫ｨ荵怜繭隶鯉ｽｳ髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ貂可髫ｨ荳翫・
    [Test]
    [Arguments("hello world", EciMode.Utf8)]
    [Arguments("こんにちは", EciMode.Utf8)]
    [Arguments("🎉", EciMode.Utf8)]
    public async Task Create_Utf8_ProducesValidQr(string text, EciMode eciMode)
    {
        var qr = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { EciMode = eciMode });

        await Assert.That(qr.Size >= 21).IsTrue();
    }

    [Test]
    [Arguments(QREccLevel.L, 16, 1)]   // V1-L: 16 bytes 驕ｶ鄙ｫ繝ｻV1 (21+8=29)
    [Arguments(QREccLevel.M, 13, 1)]   // V1-M: 13 bytes 驕ｶ鄙ｫ繝ｻV1 (21+8=29)
    [Arguments(QREccLevel.H, 6, 1)]    // V1-H: 6 bytes 驕ｶ鄙ｫ繝ｻV1 (21+8=29)
    [Arguments(QREccLevel.L, 17, 2)]   // V1-L max + 1 驕ｶ鄙ｫ繝ｻshould upgrade to V2
    [Arguments(QREccLevel.M, 14, 2)]   // V1-M max + 1 驕ｶ鄙ｫ繝ｻshould upgrade to V2
    [Arguments(QREccLevel.H, 7, 2)]   // V1-H max + 1 驕ｶ鄙ｫ繝ｻshould upgrade to V2
    [Arguments(QREccLevel.L, 31, 2)]   // V2-L max
    [Arguments(QREccLevel.M, 25, 2)]   // V2-M max
    [Arguments(QREccLevel.H, 13, 2)]   // V2-H max
    public async Task Create_Utf8_Version1MaxCapacity(QREccLevel eccLevel, int maxBytes, int expectedVersion)
    {
        var expectedSize = CalculateSize(expectedVersion);

        var text = new string('a', maxBytes); // ASCII = 1 byte each

        var qr = QRCodeGenerator.Create(text, eccLevel, new QRCodeGeneratorOptions { EciMode = EciMode.Utf8 });
        var version = CalculateVersion(qr.Size);
        var actualSize = qr.Size;

        await Assert.That(version).IsEqualTo(expectedVersion);
        await Assert.That(actualSize).IsEqualTo(expectedSize);
    }

    // ECI Mode Tests

    [Test]
    [Arguments("Café", EciMode.Iso8859_1)]
    [Arguments("Café", EciMode.Utf8)]
    public async Task Create_DifferentEci_ProducesValidQr(string text, EciMode eciMode)
    {
        var qr = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { EciMode = eciMode });

        await Assert.That(qr.Size > 0).IsTrue();
    }

    [Test]
    public async Task Create_DifferentEci_ProducesDifferentQr()
    {
        var qrDefault = QRCodeGenerator.Create("HELLO", QREccLevel.M, new QRCodeGeneratorOptions { EciMode = EciMode.Default });
        var qrIso = QRCodeGenerator.Create("HELLO", QREccLevel.M, new QRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1 });
        var qrUtf8 = QRCodeGenerator.Create("HELLO", QREccLevel.M, new QRCodeGeneratorOptions { EciMode = EciMode.Utf8 });

        // Different ECI headers 驕ｶ鄙ｫ繝ｻdifferent QR codes
        await Assert.That(SerializeMatrix(qrIso)).IsNotEqualTo(SerializeMatrix(qrDefault));
        await Assert.That(SerializeMatrix(qrUtf8)).IsNotEqualTo(SerializeMatrix(qrIso));
        await Assert.That(SerializeMatrix(qrUtf8)).IsNotEqualTo(SerializeMatrix(qrDefault));
    }

    [Test]
    [Arguments("Zürich", QREccLevel.H, EciMode.Utf8, 2)]  // 髣厄ｽｫ繝ｻ・ｮ髮弱・・ｽ・｣髯溯ｼ斐・ Version 2
    [Arguments("Zürich", QREccLevel.M, EciMode.Utf8, 1)]  // Version 1 驍ｵ・ｺ繝ｻ・ｧ OK
    [Arguments("Zürich", QREccLevel.L, EciMode.Utf8, 1)]  // Version 1 驍ｵ・ｺ繝ｻ・ｧ OK
    [Arguments("café", QREccLevel.H, EciMode.Utf8, 1)]    // Version 1 驍ｵ・ｺ繝ｻ・ｧ OK
    public async Task Create_VersionSelection_IsCorrect(string content, QREccLevel eccLevel, EciMode eciMode, int expectedVersion)
    {
        var qr = QRCodeGenerator.Create(content, eccLevel, new QRCodeGeneratorOptions { EciMode = eciMode });

        await Assert.That(qr.Version).IsEqualTo(expectedVersion);
    }

    // UTF-8 BOM Tests

    [Test]
    [Arguments("hello", QREccLevel.H, true, 2)]   // With BOM: 24 bits (3-byte UTF-8 BOM: 0xEF, 0xBB, 0xBF) overhead 驕ｶ鄙ｫ繝ｻVersion 2
    [Arguments("hello", QREccLevel.H, false, 1)]  // Without BOM 驕ｶ鄙ｫ繝ｻVersion 1
    [Arguments("test", QREccLevel.M, true, 1)]    // With BOM but still fits in Version 1
    [Arguments("test", QREccLevel.M, false, 1)]   // Without BOM 驕ｶ鄙ｫ繝ｻVersion 1
    public async Task Create_Utf8BOM_VersionSelection(string text, QREccLevel eccLevel, bool utf8BOM, int expectedVersion)
    {
        var qr = QRCodeGenerator.Create(text.AsSpan(), eccLevel, new QRCodeGeneratorOptions { Utf8Bom = utf8BOM, EciMode = EciMode.Utf8 });

        await Assert.That(qr.Version).IsEqualTo(expectedVersion);
    }

    [Test]
    public async Task Create_Utf8BOM_ProducesDifferentQr()
    {
        var qrWithBOM = QRCodeGenerator.Create("こんにちは".AsSpan(), QREccLevel.M, new QRCodeGeneratorOptions { Utf8Bom = true, EciMode = EciMode.Utf8 });
        var qrWithoutBOM = QRCodeGenerator.Create("こんにちは".AsSpan(), QREccLevel.M, new QRCodeGeneratorOptions { Utf8Bom = false, EciMode = EciMode.Utf8 });

        // UTF-8 BOM adds 24 bits 驕ｶ鄙ｫ繝ｻdifferent QR codes
        await Assert.That(SerializeMatrix(qrWithoutBOM)).IsNotEqualTo(SerializeMatrix(qrWithBOM));
    }

    [Test]
    [Arguments(QREccLevel.H, 6, false, 1)]    // V1-H: 6 bytes without BOM 驕ｶ鄙ｫ繝ｻV1
    [Arguments(QREccLevel.H, 7, false, 2)]    // V1-H: 7 bytes without BOM 驕ｶ鄙ｫ繝ｻV2 (exceeds capacity)
    [Arguments(QREccLevel.H, 3, true, 1)]    // V1-H: 3 bytes with BOM (3 + 3 BOM = 6 bytes) 驕ｶ鄙ｫ繝ｻV1
    [Arguments(QREccLevel.H, 4, true, 2)]    // V1-H: 4 bytes with BOM (4 + 3 BOM = 7 bytes) 驕ｶ鄙ｫ繝ｻV2
    public async Task Create_Utf8BOM_MaxCapacity(QREccLevel eccLevel, int textLength, bool utf8BOM, int expectedVersion)
    {
        var text = new string('a', textLength);

        var qr = QRCodeGenerator.Create(text.AsSpan(), eccLevel, new QRCodeGeneratorOptions { Utf8Bom = utf8BOM, EciMode = EciMode.Utf8 });

        await Assert.That(qr.Version).IsEqualTo(expectedVersion);
    }

    [Test]
    [Arguments("HELLO", QREccLevel.M, true)]
    [Arguments("こんにちは", QREccLevel.Q, true)]
    [Arguments("🎉", QREccLevel.H, true)]
    public async Task Create_Span_Utf8BOM_MatchesStringImplementation(string text, QREccLevel level, bool utf8BOM)
    {
        var qrString = QRCodeGenerator.Create(text, level, new QRCodeGeneratorOptions { Utf8Bom = utf8BOM, EciMode = EciMode.Utf8, QuietZoneSize = 4 });
        var qrSpan = QRCodeGenerator.Create(text.AsSpan(), level, new QRCodeGeneratorOptions { Utf8Bom = utf8BOM, EciMode = EciMode.Utf8, QuietZoneSize = 4 });

        // Compare sizes
        await Assert.That(qrSpan.Size).IsEqualTo(qrString.Size);

        // Compare every module
        for (int y = 0; y < qrString.Size; y++)
        {
            for (int x = 0; x < qrString.Size; x++)
            {
                await Assert.That(qrSpan[y, x]).IsEqualTo(qrString[y, x]);
            }
        }
    }

    // Maximum Capacity Tests

    [Test]
    [Arguments(QREccLevel.L, 7089)]  // V40-L numeric max
    [Arguments(QREccLevel.H, 3057)]  // V40-H numeric max
    public async Task Create_MaxNumeric_FitsInVersion40(QREccLevel eccLevel, int maxChars)
    {
        var text = new string('1', maxChars);

        var qr = QRCodeGenerator.Create(text, eccLevel);
        var version = CalculateVersion(qr.Size);

        await Assert.That(version).IsEqualTo(40);
    }

    [Test]
    [Arguments(QREccLevel.L, 7089)]  // V40-L numeric max
    [Arguments(QREccLevel.H, 3057)]  // V40-H numeric max
    public async Task Create_Span_MaxNumeric_FitsInVersion40(QREccLevel eccLevel, int maxChars)
    {
        var text = new string('1', maxChars);

        var qr = QRCodeGenerator.Create(text.AsSpan(), eccLevel);
        var version = CalculateVersion(qr.Size);

        await Assert.That(version).IsEqualTo(40);
    }

    [Test]
    [Arguments(QREccLevel.L, 4296)]  // V40-L alphanumeric max
    [Arguments(QREccLevel.H, 1852)]  // V40-H alphanumeric max
    public async Task Create_MaxAlphanumeric_FitsInVersion40(QREccLevel eccLevel, int maxChars)
    {
        var text = new string('A', maxChars);

        var qr = QRCodeGenerator.Create(text, eccLevel);
        var version = CalculateVersion(qr.Size);

        await Assert.That(version).IsEqualTo(40);
    }

    [Test]
    [Arguments(QREccLevel.L, 4296)]  // V40-L alphanumeric max
    [Arguments(QREccLevel.H, 1852)]  // V40-H alphanumeric max
    public async Task Create_Span_MaxAlphanumeric_FitsInVersion40(QREccLevel eccLevel, int maxChars)
    {
        var text = new string('A', maxChars);

        var qr = QRCodeGenerator.Create(text.AsSpan(), eccLevel);
        var version = CalculateVersion(qr.Size);

        await Assert.That(version).IsEqualTo(40);
    }

    [Test]
    [Arguments(QREccLevel.L, 984)]  // V40-L byte max
    [Arguments(QREccLevel.H, 424)]  // V40-H byte max
    public async Task Create_MaxByte_FitsInVersion40(QREccLevel eccLevel, int maxChars)
    {
        var text = new string('\u3042', maxChars);

        var qr = QRCodeGenerator.Create(text, eccLevel);
        var version = CalculateVersion(qr.Size);

        await Assert.That(version).IsEquivalentTo(40);
    }

    [Test]
    [Arguments(QREccLevel.L, 984)]  // V40-L byte max
    [Arguments(QREccLevel.H, 424)]  // V40-H byte max
    public async Task Create_Span_MaxByte_FitsInVersion40(QREccLevel eccLevel, int maxChars)
    {
        var text = new string('\u3042', maxChars);

        var qr = QRCodeGenerator.Create(text.AsSpan(), eccLevel);
        var version = CalculateVersion(qr.Size);

        await Assert.That(version).IsEquivalentTo(40);
    }

    [Test]
    public async Task Create_ExceedsMaxCapacity_Throws()
    {
        var tooLarge = new string('1', 7090); // V40-L max + 1

        Assert.Throws<InvalidOperationException>(() => QRCodeGenerator.Create(tooLarge, QREccLevel.L));
    }

    // Consistency Tests

    [Test]
    public async Task Create_SameInput_ProducesSameOutput()
    {
        var qr1 = QRCodeGenerator.Create("HELLO WORLD", QREccLevel.M);
        var qr2 = QRCodeGenerator.Create("HELLO WORLD", QREccLevel.M);

        await Assert.That(SerializeMatrix(qr2)).IsEqualTo(SerializeMatrix(qr1));
    }

    // Text and Binary consistency

    [Test]
    [Arguments("HELLO WORLD", QREccLevel.Q, 1)]
    [Arguments("https://example.com", QREccLevel.M, -1)]
    [Arguments("0123456789", QREccLevel.H, 5)]
    public async Task Create_Binary_MatchesTextImplementation(string text, QREccLevel level, int version)
    {
        // -1 was the released parameter list's "pick a version"; the range spells it Any
        // and rejects -1, so a defaulted field cannot pass for automatic selection.
        var options = new QRCodeGeneratorOptions
        {
            Version = version == -1 ? QRVersionRange.Any : QRVersionRange.Exactly(version),
            QuietZoneSize = 4,
        };

        var qrText = QRCodeGenerator.Create(text, level, options);
        var qrBinary = QRCodeGenerator.Create(text.AsSpan(), level, options);

        // Compare sizes
        await Assert.That(qrBinary.Size).IsEqualTo(qrText.Size);
        await Assert.That(qrBinary.Version).IsEqualTo(qrText.Version);

        // Compare every module
        for (int y = 0; y < qrText.Size; y++)
        {
            for (int x = 0; x < qrText.Size; x++)
            {
                await Assert.That(qrBinary[y, x]).IsEqualTo(qrText[y, x]);
            }
        }
    }

    // Sizing tests. The throwing GetRequiredBufferSize these were written against was
    // removed in 2.0.0 (specs/qrcode-symbologies.md, Public API direction); they now go
    // through TryGetRequiredBufferSize, via the Sizing helper where the fit is a
    // precondition rather than the subject.

    [Test]
    public async Task TryGetRequiredBufferSize_MatchesActualQrCodeGeneration()
    {
        var text = "https://example.com/foobar";
        var eccLevel = QREccLevel.L;
        var quietZoneSize = 4;

        // Calculate expected size
        var (expectedBufferSize, expectedQrSize, expectedVersion) = Sizing.Required(text.AsSpan(), eccLevel, quietZoneSize);

        // Generate actual QR code
        var qrCode = QRCodeGenerator.Create(text.AsSpan(), eccLevel, new QRCodeGeneratorOptions { QuietZoneSize = quietZoneSize });

        // Verify
        await Assert.That(qrCode.Version).IsEqualTo(expectedVersion);
        await Assert.That(qrCode.Size).IsEqualTo(expectedQrSize);
        await Assert.That(qrCode.Size * qrCode.Size).IsEqualTo(expectedBufferSize);
    }

    [Test]
    [Arguments("HELLO WORLD", QREccLevel.L, 0, 21 * 21, 21, 1)]  // Version 1, no quiet zone
    [Arguments("HELLO WORLD", QREccLevel.L, 4, 29 * 29, 29, 1)]  // Version 1, with quiet zone
    [Arguments("https://example.com/foobar", QREccLevel.L, 0, 25 * 25, 25, 2)]  // Version 2
    [Arguments("https://example.com/foobar", QREccLevel.L, 4, 33 * 33, 33, 2)]  // Version 2 with quiet zone
    public async Task TryGetRequiredBufferSize_ReturnsCorrectSize(string text, QREccLevel eccLevel, int quietZoneSize, int expectedBufferSize, int expectedQrSize, int expectedVersion)
    {
        // Act
        var (bufferSize, qrSize, version) = Sizing.Required(text.AsSpan(), eccLevel, quietZoneSize);

        // Assert
        await Assert.That(bufferSize).IsEqualTo(expectedBufferSize);
        await Assert.That(qrSize).IsEqualTo(expectedQrSize);
        await Assert.That(version).IsEqualTo(expectedVersion);
    }

    [Test]
    [Arguments("hello", QREccLevel.L, false, EciMode.Default, 0, 21 * 21, 21, 1)]  // Default ECI, no BOM
    [Arguments("hello", QREccLevel.L, false, EciMode.Iso8859_1, 0, 21 * 21, 21, 1)]  // ISO-8859-1, no BOM
    [Arguments("hello", QREccLevel.L, false, EciMode.Utf8, 0, 21 * 21, 21, 1)]  // UTF-8 ECI, no BOM
    [Arguments("hello", QREccLevel.H, false, EciMode.Utf8, 0, 21 * 21, 21, 1)]  // UTF-8 ECI, no BOM, ECC-H
    [Arguments("hello", QREccLevel.H, true, EciMode.Utf8, 0, 25 * 25, 25, 2)]   // UTF-8 ECI, with BOM 驕ｶ鄙ｫ繝ｻVersion 2 (BOM adds 3 bytes)
    [Arguments("hello", QREccLevel.H, false, EciMode.Utf8, 4, 29 * 29, 29, 1)]  // UTF-8 ECI, no BOM, with quiet zone
    [Arguments("hello", QREccLevel.H, true, EciMode.Utf8, 4, 33 * 33, 33, 2)]   // UTF-8 ECI, with BOM, with quiet zone 驕ｶ鄙ｫ繝ｻVersion 2
    [Arguments("Café", QREccLevel.M, false, EciMode.Iso8859_1, 0, 21 * 21, 21, 1)]  // ISO-8859-1 encoding
    [Arguments("こんにちは", QREccLevel.M, false, EciMode.Utf8, 0, 25 * 25, 25, 2)]  // Japanese UTF-8
    [Arguments("こんにちは", QREccLevel.M, true, EciMode.Utf8, 0, 25 * 25, 25, 2)]   // Japanese UTF-8 with BOM
    [Arguments("🎉", QREccLevel.M, false, EciMode.Utf8, 0, 21 * 21, 21, 1)]      // Emoji UTF-8
    [Arguments("🎉", QREccLevel.M, true, EciMode.Utf8, 0, 21 * 21, 21, 1)]       // Emoji UTF-8 with BOM (still fits V1)
    public async Task TryGetRequiredBufferSize_WithEciAndBOM_ReturnsCorrectSize(string text, QREccLevel eccLevel, bool utf8BOM, EciMode eciMode, int quietZoneSize, int expectedBufferSize, int expectedQrSize, int expectedVersion)
    {
        // Act
        var (bufferSize, qrSize, version) = Sizing.Required(text.AsSpan(), eccLevel, new QRCodeGeneratorOptions { Utf8Bom = utf8BOM, EciMode = eciMode, QuietZoneSize = quietZoneSize });

        // Assert
        await Assert.That(bufferSize).IsEqualTo(expectedBufferSize);
        await Assert.That(qrSize).IsEqualTo(expectedQrSize);
        await Assert.That(version).IsEqualTo(expectedVersion);
    }

    [Test]
    [Arguments(QREccLevel.H, 6, false, EciMode.Utf8, 1)]    // V1-H: 6 bytes without BOM 驕ｶ鄙ｫ繝ｻV1
    [Arguments(QREccLevel.H, 7, false, EciMode.Utf8, 2)]    // V1-H: 7 bytes without BOM 驕ｶ鄙ｫ繝ｻV2 (exceeds V1 capacity)
    [Arguments(QREccLevel.H, 3, true, EciMode.Utf8, 1)]     // V1-H: 3 bytes + 3 BOM = 6 bytes 驕ｶ鄙ｫ繝ｻV1
    [Arguments(QREccLevel.H, 4, true, EciMode.Utf8, 2)]     // V1-H: 4 bytes + 3 BOM = 7 bytes 驕ｶ鄙ｫ繝ｻV2
    [Arguments(QREccLevel.M, 13, false, EciMode.Utf8, 1)]   // V1-M: 13 bytes without BOM 驕ｶ鄙ｫ繝ｻV1
    [Arguments(QREccLevel.M, 14, false, EciMode.Utf8, 2)]   // V1-M: 14 bytes without BOM 驕ｶ鄙ｫ繝ｻV2
    [Arguments(QREccLevel.M, 10, true, EciMode.Utf8, 1)]    // V1-M: 10 bytes + 3 BOM = 13 bytes 驕ｶ鄙ｫ繝ｻV1
    [Arguments(QREccLevel.M, 11, true, EciMode.Utf8, 2)]    // V1-M: 11 bytes + 3 BOM = 14 bytes 驕ｶ鄙ｫ繝ｻV2
    public async Task TryGetRequiredBufferSize_Utf8BOM_AffectsVersionSelection(QREccLevel eccLevel, int textLength, bool utf8BOM, EciMode eciMode, int expectedVersion)
    {
        var text = new string('a', textLength); // ASCII = 1 byte each

        // Act
        var (_, _, version) = Sizing.Required(text.AsSpan(), eccLevel, new QRCodeGeneratorOptions { Utf8Bom = utf8BOM, EciMode = eciMode });

        // Assert
        await Assert.That(version).IsEqualTo(expectedVersion);
    }

    [Test]
    [Arguments("HELLO", EciMode.Default, EciMode.Iso8859_1)]
    [Arguments("HELLO", EciMode.Default, EciMode.Utf8)]
    [Arguments("HELLO", EciMode.Iso8859_1, EciMode.Utf8)]
    public async Task TryGetRequiredBufferSize_DifferentEciModes_MayProduceDifferentVersions(string text, EciMode eciMode1, EciMode eciMode2)
    {
        // Act
        var (bufferSize1, qrSize1, version1) = Sizing.Required(text.AsSpan(), QREccLevel.M, new QRCodeGeneratorOptions { EciMode = eciMode1 });

        var (bufferSize2, qrSize2, version2) = Sizing.Required(text.AsSpan(), QREccLevel.M, new QRCodeGeneratorOptions { EciMode = eciMode2 });

        // Assert - Different ECI modes may produce different buffer sizes due to ECI header overhead
        // For short text like "HELLO", all should fit in Version 1, but buffer sizes differ based on final QR size
        // This test documents the behavior rather than asserting specific values
        if (eciMode1 == eciMode2)
        {
            await Assert.That(bufferSize2).IsEqualTo(bufferSize1);
            await Assert.That(version2).IsEqualTo(version1);
        }
        else
        {
            // ECI header overhead might not affect version for short text
            // but we document that different modes can produce different results
            await Assert.That(version1 >= 1 && version1 <= 40).IsTrue();
            await Assert.That(version2 >= 1 && version2 <= 40).IsTrue();
        }
    }

    // Create (Span destination) Tests

    [Test]
    [Arguments("HELLO WORLD", QREccLevel.L, 0)]
    [Arguments("HELLO WORLD", QREccLevel.L, 4)]
    [Arguments("https://example.com/foobar", QREccLevel.M, 4)]
    [Arguments("0123456789012345678901234567890123456789", QREccLevel.Q, 4)]
    [Arguments("こんにちは世界", QREccLevel.H, 4)]
    [Arguments("", QREccLevel.L, 4)]
    public async Task Create_SpanDestination_MatchesQRCodeData(string text, QREccLevel eccLevel, int quietZoneSize)
    {
        var calculated = Sizing.Required(text.AsSpan(), eccLevel, quietZoneSize);
        var buffer = new byte[calculated.BufferSize];

        // Act
        var written = Create(text.AsSpan(), eccLevel, buffer.AsSpan(), new QRCodeGeneratorOptions { QuietZoneSize = quietZoneSize });

        // Assert - written bytes match the size calculation API
        await Assert.That(written).IsEqualTo(calculated.BufferSize);

        // Assert - every module matches the class-based API
        var qrCode = Create(text.AsSpan(), eccLevel, new QRCodeGeneratorOptions { QuietZoneSize = quietZoneSize });
        await Assert.That(qrCode.Size).IsEqualTo(calculated.Size);
        for (var row = 0; row < qrCode.Size; row++)
        {
            for (var col = 0; col < qrCode.Size; col++)
            {
                await Assert.That(buffer[row * calculated.Size + col] != 0).IsEqualTo(qrCode[row, col]);
            }
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(7)]
    [Arguments(40)]
    public async Task Create_SpanDestination_RequestedVersion_MatchesQRCodeData(int requestedVersion)
    {
        var text = "https://example.com/foobar";
        var quietZoneSize = 4;
        var qrCode = Create(text.AsSpan(), QREccLevel.L, new QRCodeGeneratorOptions { Version = requestedVersion, QuietZoneSize = quietZoneSize });
        var buffer = new byte[qrCode.Size * qrCode.Size];

        // Act
        var written = Create(text.AsSpan(), QREccLevel.L, buffer.AsSpan(), new QRCodeGeneratorOptions { Version = requestedVersion, QuietZoneSize = quietZoneSize });

        // Assert
        await Assert.That(written).IsEqualTo(qrCode.Size * qrCode.Size);
        for (var row = 0; row < qrCode.Size; row++)
        {
            for (var col = 0; col < qrCode.Size; col++)
            {
                await Assert.That(buffer[row * qrCode.Size + col] != 0).IsEqualTo(qrCode[row, col]);
            }
        }
    }

    [Test]
    public async Task Create_SpanDestination_BufferTooSmall_Throws()
    {
        var text = "https://example.com/foobar";
        var calculated = Sizing.Required(text.AsSpan(), QREccLevel.L);
        var buffer = new byte[calculated.BufferSize - 1];

        var ex = Assert.Throws<ArgumentException>(() => Create(text.AsSpan(), QREccLevel.L, buffer.AsSpan()));
        await Assert.That(ex.Message).Contains($"{calculated.BufferSize} bytes required");
    }

    [Test]
    public async Task Create_SpanDestination_DirtyOversizedBuffer_WritesCleanOutputAndLeavesTailUntouched()
    {
        var text = "HELLO WORLD";
        var quietZoneSize = 4;
        var calculated = Sizing.Required(text.AsSpan(), QREccLevel.L, quietZoneSize);

        // Simulate a dirty pooled buffer, larger than required
        var buffer = new byte[calculated.BufferSize + 100];
        buffer.AsSpan().Fill(0xFF);

        // Act
        var written = Create(text.AsSpan(), QREccLevel.L, buffer.AsSpan(), new QRCodeGeneratorOptions { QuietZoneSize = quietZoneSize });

        // Assert - written region contains only 0/1 and the quiet zone is light
        await Assert.That(written).IsEqualTo(calculated.BufferSize);
        var qrSize = calculated.Size;
        for (var row = 0; row < qrSize; row++)
        {
            for (var col = 0; col < qrSize; col++)
            {
                var module = buffer[row * qrSize + col];
                await Assert.That(module is 0 or 1).IsTrue().Because($"Module at ({row}, {col}) should be 0 or 1, got {module}");

                var isQuietZone = row < quietZoneSize || row >= qrSize - quietZoneSize
                    || col < quietZoneSize || col >= qrSize - quietZoneSize;
                if (isQuietZone)
                {
                    await Assert.That(module).IsEqualTo((byte)0);
                }
            }
        }

        // Assert - bytes beyond the written region are untouched
        for (var i = written; i < buffer.Length; i++)
        {
            await Assert.That(buffer[i]).IsEqualTo((byte)0xFF);
        }
    }

    // Placement writes every core module from a per-version template (no destination
    // clear on the quiet-zone-0 path, no clear of the pooled work buffer on the class
    // path), so a dirty destination must come out identical to a clean one for every
    // placer / mask tier: v1-2 (stack bit scratch), v3+ (pooled), v7+ (version bits),
    // v12 / v28 (two- and three-word mask tiers), and both quiet-zone branches.
    public static IEnumerable<(string text, int quietZone)> DirtyDestinationCases()
    {
        string[] texts =
        [
            "HELLO WORLD",                                                    // v1
            new string('A', 40),                                              // v3
            new string('7', 300),                                             // v8 (numeric, version bits)
            new string('Q', 500),                                             // v12+
            new string('z', 1500),                                            // v28+ (byte)
        ];
        foreach (var text in texts)
        {
            yield return (text, 0);
            yield return (text, 4);
        }
    }

    [Test]
    [MethodDataSource(nameof(DirtyDestinationCases))]
    public async Task Create_SpanDestination_DirtyBuffer_MatchesCleanBuffer_AllQuietZones(string text, int quietZone)
    {
        var calculated = Sizing.Required(text.AsSpan(), QREccLevel.M, quietZone);
        var clean = new byte[calculated.BufferSize];
        var written = Create(text.AsSpan(), QREccLevel.M, clean, new QRCodeGeneratorOptions { QuietZoneSize = quietZone });
        await Assert.That(written).IsEqualTo(calculated.BufferSize);

        foreach (var fill in new byte[] { 0xFF, 0xA5, 0x01 })
        {
            var dirty = new byte[calculated.BufferSize + 7];
            dirty.AsSpan().Fill(fill);
            var writtenDirty = Create(text.AsSpan(), QREccLevel.M, dirty, new QRCodeGeneratorOptions { QuietZoneSize = quietZone });
            await Assert.That(writtenDirty).IsEqualTo(written);
            await Assert.That(dirty.AsSpan(0, written).SequenceEqual(clean)).IsTrue()
                .Because($"dirty fill 0x{fill:X2}, quiet zone {quietZone}, version {calculated.Version} must reproduce the clean matrix byte for byte");
            for (var i = written; i < dirty.Length; i++)
                await Assert.That(dirty[i]).IsEqualTo(fill);
        }

        // Class API: its pooled work buffer is deliberately not cleared; the result must
        // agree with the span path module for module. Poison the pool bucket the generator
        // will rent (same length, returned dirty) so a fresh pool cannot hide an unwritten
        // module.
        var coreSize = calculated.Size - 2 * quietZone;
        var poison = System.Buffers.ArrayPool<byte>.Shared.Rent(coreSize * coreSize);
        poison.AsSpan().Fill(0xFF);
        System.Buffers.ArrayPool<byte>.Shared.Return(poison, clearArray: false);
        var data = Create(text.AsSpan(), QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = quietZone });
        await Assert.That(data.Version).IsEqualTo(calculated.Version);
        var side = calculated.Size;
        for (var row = 0; row < side; row++)
            for (var col = 0; col < side; col++)
                if (data[row, col] != (clean[row * side + col] != 0))
                    Assert.Fail($"class API differs from span API at ({row},{col}) for version {calculated.Version}, quiet zone {quietZone}");
    }

    [Test]
    public async Task Create_SpanDestination_StringOverload_MatchesSpanOverload()
    {
        var text = "https://example.com/foobar";
        var calculated = Sizing.Required(text.AsSpan(), QREccLevel.M);
        var bufferFromString = new byte[calculated.BufferSize];
        var bufferFromSpan = new byte[calculated.BufferSize];

        // Act
        var writtenFromString = Create(text, QREccLevel.M, bufferFromString.AsSpan());
        var writtenFromSpan = Create(text.AsSpan(), QREccLevel.M, bufferFromSpan.AsSpan());

        // Assert
        await Assert.That(writtenFromString).IsEqualTo(writtenFromSpan);
        await Assert.That(bufferFromString).IsEquivalentTo(bufferFromSpan);
    }

#if !DEBUG
    // Release-only: unoptimized (Debug) JIT allocates small temporaries inside the
    // SIMD ECC kernel that the optimizing JIT eliminates; the zero-allocation
    // guarantee applies to shipped (Release) binaries.
    [Test]
    public async Task Create_SpanDestination_DoesNotAllocate()
    {
        var text = "https://example.com/foobar";
        var buffer = new byte[Sizing.Required(text.AsSpan(), QREccLevel.M).BufferSize];

        // Warm up JIT, ArrayPool and lazily-built static tables
        for (var i = 0; i < 3; i++)
        {
            Sizing.Required(text.AsSpan(), QREccLevel.M);
            Create(text.AsSpan(), QREccLevel.M, buffer.AsSpan());
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        Sizing.Required(text.AsSpan(), QREccLevel.M);
        Create(text.AsSpan(), QREccLevel.M, buffer.AsSpan());
        var after = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(after - before).IsEqualTo(0);
    }
#endif

    [Test]
    [Arguments(0)]
    [Arguments(41)]
    public async Task Create_SpanDestination_InvalidVersion_Throws(int requestedVersion)
    {
        var buffer = new byte[64 * 64];
        Assert.Throws<ArgumentOutOfRangeException>(() => Create("HELLO".AsSpan(), QREccLevel.L, buffer.AsSpan(), new QRCodeGeneratorOptions { Version = requestedVersion }));
    }

    [Test]
    public async Task Create_SpanDestination_NegativeQuietZone_Throws()
    {
        var buffer = new byte[64 * 64];
        Assert.Throws<ArgumentOutOfRangeException>(() => Create("HELLO".AsSpan(), QREccLevel.L, buffer.AsSpan(), new QRCodeGeneratorOptions { QuietZoneSize = -1 }));
    }

    [Test]
    [Arguments(40_000)]        // totalSize fits in int, but totalSize繝ｻ繧托ｽｽ・ｲ overflows
    [Arguments(int.MaxValue)]  // totalSize itself exceeds int.MaxValue
    public async Task Create_SpanDestination_OversizedQuietZone_Throws(int quietZoneSize)
    {
        var buffer = new byte[64 * 64];
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Create("HELLO".AsSpan(), QREccLevel.L, buffer.AsSpan(), new QRCodeGeneratorOptions { QuietZoneSize = quietZoneSize }));
        await Assert.That(ex.ParamName).IsEqualTo("quietZoneSize");
    }

    [Test]
    [Arguments(-1)]
    [Arguments(40_000)]
    [Arguments(int.MaxValue)]
    public async Task TryGetRequiredBufferSize_InvalidQuietZone_Throws(int quietZoneSize)
    {
        // An argument error, not a "does not fit": the Try shape reports the second as
        // false and keeps throwing for the first.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => QRCodeGenerator.TryGetRequiredBufferSize("HELLO".AsSpan(), QREccLevel.L, out _, new QRCodeGeneratorOptions { QuietZoneSize = quietZoneSize }));
        await Assert.That(ex.ParamName).IsEqualTo("quietZoneSize");
    }

    // Helpers

    /// <summary>
    /// Calculates QR code version from module matrix size.
    /// Handles quiet zone automatically.
    /// </summary>
    /// <param name="matrixSize">Total size of module matrix (including quiet zone).</param>
    /// <param name="quietZoneSize">Size of quiet zone in modules.</param>
    /// <returns>QR code version (1-40).</returns>
    private static int CalculateVersion(int matrixSize, int quietZoneSize = 4)
    {
        // Remove quiet zone
        var sizeWithoutQuietZone = matrixSize - (quietZoneSize * 2);

        // Formula: size = 21 + (version - 1) * 4
        // Inverse: version = (size - 21) / 4 + 1
        return (sizeWithoutQuietZone - 21) / 4 + 1;
    }

    /// <summary>
    /// Calculate QR code size from version.
    /// </summary>
    /// <param name="version"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    private static int CalculateSize(int version, int quietZoneSize = 4)
    {
        if (version is < 1 or > 40)
            throw new ArgumentOutOfRangeException(nameof(version), $"Version must be -1 (auto) or 1-40, got {version}");

        // Formula: size = 21 + (version - 1) * 4
        var sizeWithoutQuietSone = 21 + (version - 1) * 4;
        return sizeWithoutQuietSone + (quietZoneSize * 2);
    }

    private static string SerializeMatrix(QRCodeData qrCode)
    {
        var size = qrCode.Size;
        var sb = new StringBuilder(size * size);
        for (var row = 0; row < size; row++)
        {
            for (var col = 0; col < size; col++)
            {
                sb.Append(qrCode[row, col] ? '1' : '0');
            }
        }
        return sb.ToString();
    }
}
