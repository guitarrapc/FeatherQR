namespace FeatherQR.Tests;

public class MicroQRCodeDataUnitTest
{
    [Test]
    public async Task GetRawData_RoundTripsThroughConstructor()
    {
        var original = MicroQRCodeGenerator.CreateMicroQRCode("01234567", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { QuietZoneSize = 2 });
        var raw = original.GetRawData();

        var restored = new MicroQRCodeData(raw, quietZoneSize: 2);

        await Assert.That(restored.Version).IsEqualTo(original.Version);
        await Assert.That(restored.Size).IsEqualTo(original.Size);
        for (var row = 0; row < original.Size; row++)
        {
            for (var col = 0; col < original.Size; col++)
            {
                if (original[row, col] != restored[row, col])
                {
                    Assert.Fail($"Module mismatch at ({row},{col})");
                }
            }
        }
    }

    [Test]
    public async Task GetRawData_HeaderIsQrxWithSymbolTypeAndDimensions()
    {
        var data = MicroQRCodeGenerator.CreateMicroQRCode("12345", MicroQREccLevel.ErrorDetectionOnly);
        var raw = data.GetRawData();

        await Assert.That(raw.Length).IsEqualTo(data.GetRawDataSize());
        // "QRX" magic, symbol type 1 (Micro QR), width, height, then packed core bits.
        await Assert.That(raw[0]).IsEqualTo((byte)0x51);
        await Assert.That(raw[1]).IsEqualTo((byte)0x52);
        await Assert.That(raw[2]).IsEqualTo((byte)0x58);
        await Assert.That(raw[3]).IsEqualTo((byte)1);
        await Assert.That(raw[4]).IsEqualTo((byte)11);
        await Assert.That(raw[5]).IsEqualTo((byte)11);
        await Assert.That(raw.Length).IsEqualTo(6 + (11 * 11 + 7) / 8);
    }

    [Test]
    public async Task Constructor_RejectsInvalidHeader()
    {
        var valid = MicroQRCodeGenerator.CreateMicroQRCode("123", MicroQREccLevel.L).GetRawData();

        var badMagic = (byte[])valid.Clone();
        badMagic[2] = (byte)'R';
        await Assert.That(() => new MicroQRCodeData(badMagic, 0)).Throws<InvalidDataException>();

        var badSymbolType = (byte[])valid.Clone();
        badSymbolType[3] = 9;
        await Assert.That(() => new MicroQRCodeData(badSymbolType, 0)).Throws<InvalidDataException>();

        var badSize = (byte[])valid.Clone();
        badSize[4] = 12; // not a Micro QR size (11/13/15/17)
        badSize[5] = 12;
        await Assert.That(() => new MicroQRCodeData(badSize, 0)).Throws<InvalidDataException>();

        var truncated = valid.AsSpan(0, valid.Length - 1).ToArray();
        await Assert.That(() => new MicroQRCodeData(truncated, 0)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Constructor_RejectsInvalidVersionAndQuietZone()
    {
        await Assert.That(() => new MicroQRCodeData((MicroQRVersion)0, 2)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new MicroQRCodeData((MicroQRVersion)5, 2)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new MicroQRCodeData(MicroQRVersion.M2, -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new MicroQRCodeData(MicroQRVersion.M2, 10_001)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Constructor_Deserialize_RejectsInvalidQuietZone()
    {
        var raw = MicroQRCodeGenerator.CreateMicroQRCode("123", MicroQREccLevel.L).GetRawData();

        await Assert.That(() => new MicroQRCodeData(raw, -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new MicroQRCodeData(raw, 10_001)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SetCoreData_SecondCall_ReplacesPreviousMatrix()
    {
        // SetCoreData must fully replace the matrix, not OR into the previous
        // one: an all-dark core followed by an all-light core must read light.
        var data = new MicroQRCodeData(MicroQRVersion.M1, 0);
        var allDark = new byte[11 * 11];
        Array.Fill(allDark, (byte)1);
        data.SetCoreData(allDark);
        data.SetCoreData(new byte[11 * 11]);

        for (var row = 0; row < 11; row++)
        {
            for (var col = 0; col < 11; col++)
            {
                if (data[row, col])
                {
                    Assert.Fail($"Dark module leaked from the previous SetCoreData at ({row},{col})");
                }
            }
        }
        await Assert.That(data[0, 0]).IsFalse();
    }

    [Test]
    public async Task Indexer_QuietZoneReadsLightAndOutOfRangeThrows()
    {
        var data = MicroQRCodeGenerator.CreateMicroQRCode("123", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { QuietZoneSize = 3 });

        await Assert.That(data.Size).IsEqualTo(13 + 6);
        await Assert.That(data[0, 0]).IsFalse();          // quiet zone
        await Assert.That(data[3, 3]).IsTrue();           // core (0,0) = finder corner
        await Assert.That(() => data[19, 0]).Throws<IndexOutOfRangeException>();
    }
}
