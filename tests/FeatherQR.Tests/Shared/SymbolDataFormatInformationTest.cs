using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The error correction level and mask a generated symbol reports, read from its own format information, and the restore that refuses a matrix whose format information is not exactly one a generator writes.
/// </summary>
public class SymbolDataFormatInformationTest
{
    private static readonly QREccLevel[] StandardLevels = [QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H];

    // ---- Standard QR -----------------------------------------------------------------

    [Test]
    [Arguments(1)]
    [Arguments(7)]
    [Arguments(40)]
    public async Task QRCodeData_ReportsTheLevelAndMaskDecodingReads(int version)
    {
        foreach (var level in StandardLevels)
        {
            for (var mask = 0; mask < 8; mask++)
            {
                var data = QRCodeGenerator.Create("FORMAT", level, new QRCodeGeneratorOptions { Version = version, MaskPattern = mask });

                await Assert.That(QRCodeDecoder.TryDecode(data, out _, out var info)).IsTrue();
                await Assert.That(info.EccLevel).IsEqualTo(level);
                await Assert.That(info.MaskPattern).IsEqualTo(mask);
                await Assert.That(data.EccLevel).IsEqualTo(level);
                await Assert.That(data.MaskPattern).IsEqualTo(mask);
            }
        }
    }

    [Test]
    public async Task QRCodeData_ReportsTheBoostedLevelAndTheChosenMask()
    {
        var data = QRCodeGenerator.Create("HELLO", QREccLevel.L, new QRCodeGeneratorOptions { BoostEccLevel = true });

        await Assert.That(QRCodeDecoder.TryDecode(data, out _, out var info)).IsTrue();
        await Assert.That(info.EccLevel).IsEqualTo(QREccLevel.H);
        await Assert.That(data.EccLevel).IsEqualTo(QREccLevel.H);
        await Assert.That(data.MaskPattern).IsEqualTo(info.MaskPattern);
    }

    [Test]
    public async Task QRCodeData_KeepsTheLevelAndMaskThroughSerialization()
    {
        var data = QRCodeGenerator.Create("FORMAT", QREccLevel.Q, new QRCodeGeneratorOptions { MaskPattern = 5 });

        var restored = new QRCodeData(data.GetRawData(), quietZoneSize: 1);

        await Assert.That(restored.EccLevel).IsEqualTo(QREccLevel.Q);
        await Assert.That(restored.MaskPattern).IsEqualTo(5);
    }

    [Test]
    [Arguments(1)]
    [Arguments(7)]
    public async Task QRCodeData_RefusesABlankMatrix(int version)
    {
        var size = 17 + 4 * version;
        var raw = new byte[4 + (size * size + 7) / 8];
        "QRR"u8.CopyTo(raw);
        raw[3] = (byte)size;

        await Assert.That(() => new QRCodeData(raw, 0)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task QRCodeData_RefusesFormatInformationOneModuleOff_ThatDecodingCorrects()
    {
        var data = QRCodeGenerator.Create("FORMAT", QREccLevel.M, new QRCodeGeneratorOptions { MaskPattern = 2, QuietZoneSize = 0 });
        var size = data.Size;
        var raw = data.GetRawData();
        // Bit 0 of both copies
        FlipModule(raw, StandardHeader, size, 0, 8);
        FlipModule(raw, StandardHeader, size, 8, size - 1);

        await Assert.That(QRCodeDecoder.TryDecode(Unpack(raw, StandardHeader, size, size), size, out var text, out _)).IsTrue();
        await Assert.That(text).IsEqualTo("FORMAT");
        await Assert.That(() => new QRCodeData(raw, 0)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task QRCodeData_RefusesCopiesThatDisagree()
    {
        var data = QRCodeGenerator.Create("FORMAT", QREccLevel.M, new QRCodeGeneratorOptions { MaskPattern = 2, QuietZoneSize = 0 });
        var size = data.Size;
        var raw = data.GetRawData();
        var other = QRCodeConstants.GetFormatBits(QREccLevel.H, 2);
        for (var i = 0; i < 15; i++)
        {
            // Copy 2: bits 0-7 along row 8 from the right edge, bits 8-14 down column 8 to the bottom edge
            var (row, col) = i < 8 ? (8, size - 1 - i) : (size - 15 + i, 8);
            SetModule(raw, StandardHeader, size, row, col, ((other >> i) & 1) != 0);
        }

        await Assert.That(() => new QRCodeData(raw, 0)).Throws<InvalidDataException>();
    }

    // ---- Micro QR --------------------------------------------------------------------

    [Test]
    public async Task MicroQRCodeData_ReportsTheLevelAndMaskDecodingReads()
    {
        (MicroQRVersion Version, MicroQREccLevel Level)[] symbols =
        [
            (MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly),
            (MicroQRVersion.M2, MicroQREccLevel.L), (MicroQRVersion.M2, MicroQREccLevel.M),
            (MicroQRVersion.M3, MicroQREccLevel.L), (MicroQRVersion.M3, MicroQREccLevel.M),
            (MicroQRVersion.M4, MicroQREccLevel.L), (MicroQRVersion.M4, MicroQREccLevel.M), (MicroQRVersion.M4, MicroQREccLevel.Q),
        ];
        foreach (var (version, level) in symbols)
        {
            for (var mask = 0; mask < 4; mask++)
            {
                var data = MicroQRCodeGenerator.Create("1", level, new MicroQRCodeGeneratorOptions { Version = version, MaskPattern = mask });

                await Assert.That(MicroQRCodeDecoder.TryDecode(data, out _, out var info)).IsTrue();
                await Assert.That(info.EccLevel).IsEqualTo(level);
                await Assert.That(info.MaskPattern).IsEqualTo(mask);
                await Assert.That(data.EccLevel).IsEqualTo(level);
                await Assert.That(data.MaskPattern).IsEqualTo(mask);
            }
        }
    }

    [Test]
    public async Task MicroQRCodeData_KeepsTheLevelAndMaskThroughSerialization()
    {
        var data = MicroQRCodeGenerator.Create("1", MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M3, MaskPattern = 3 });

        var restored = new MicroQRCodeData(data.GetRawData(), quietZoneSize: 1);

        await Assert.That(restored.EccLevel).IsEqualTo(MicroQREccLevel.M);
        await Assert.That(restored.MaskPattern).IsEqualTo(3);
    }

    [Test]
    [Arguments(MicroQRVersion.M2)]
    [Arguments(MicroQRVersion.M4)]
    public async Task MicroQRCodeData_RefusesABlankMatrix(MicroQRVersion version)
    {
        var size = 9 + 2 * (int)version;
        var raw = new byte[ExtendedHeader + (size * size + 7) / 8];
        WriteExtendedHeader(raw, MicroQRConstants.SymbolTypeMicroQR, size, size);

        await Assert.That(() => new MicroQRCodeData(raw, 0)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task AllLightMicroQRFormat_IsWithinTheCorrectionDistanceOfM4L()
    {
        // An all-light format word is within the correction distance of M4-L, mask 1, so a reader that corrects it names a level and a mask the matrix never had.
        await Assert.That(MicroQRFormatInformationDecoder.TryDecode(0, out var version, out var level, out var mask, out var distance)).IsTrue();
        await Assert.That(version).IsEqualTo(MicroQRVersion.M4);
        await Assert.That(level).IsEqualTo(MicroQREccLevel.L);
        await Assert.That(mask).IsEqualTo(1);
        await Assert.That(distance).IsEqualTo(3);
    }

    [Test]
    public async Task MicroQRCodeData_RefusesFormatInformationOneModuleOff_ThatDecodingCorrects()
    {
        var data = MicroQRCodeGenerator.Create("1", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M3, MaskPattern = 0, QuietZoneSize = 0 });
        var size = data.Size;
        var raw = data.GetRawData();
        FlipModule(raw, ExtendedHeader, size, 8, 1);

        await Assert.That(MicroQRCodeDecoder.TryDecode(Unpack(raw, ExtendedHeader, size, size), size, out var text, out _)).IsTrue();
        await Assert.That(text).IsEqualTo("1");
        await Assert.That(() => new MicroQRCodeData(raw, 0)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task MicroQRCodeData_RefusesTheFormatOfAnotherVersion()
    {
        var data = MicroQRCodeGenerator.Create("1", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M2, MaskPattern = 0, QuietZoneSize = 0 });
        var size = data.Size;
        var raw = data.GetRawData();
        var m3 = MicroQRConstants.GetFormatBits(MicroQRVersion.M3, MicroQREccLevel.L, 0);
        // Bits 14-7 along row 8, columns 1-8, then bits 6-0 up column 8, rows 7-1
        var bit = 14;
        for (var col = 1; col <= 8; col++, bit--)
            SetModule(raw, ExtendedHeader, size, 8, col, ((m3 >> bit) & 1) != 0);
        for (var row = 7; row >= 1; row--, bit--)
            SetModule(raw, ExtendedHeader, size, row, 8, ((m3 >> bit) & 1) != 0);

        await Assert.That(() => new MicroQRCodeData(raw, 0)).Throws<InvalidDataException>();
    }

    // ---- rMQR ------------------------------------------------------------------------

    [Test]
    [Arguments(RmQRVersion.R7x43)]
    [Arguments(RmQRVersion.R11x27)]
    [Arguments(RmQRVersion.R17x139)]
    public async Task RmQRCodeData_ReportsTheLevelDecodingReads(RmQRVersion version)
    {
        foreach (var level in new[] { RmQREccLevel.M, RmQREccLevel.H })
        {
            var data = RmQRCodeGenerator.Create("1", level, new RmQRCodeGeneratorOptions { Version = version });

            await Assert.That(RmQRCodeDecoder.TryDecode(data, out _, out var info)).IsTrue();
            await Assert.That(info.EccLevel).IsEqualTo(level);
            await Assert.That(data.EccLevel).IsEqualTo(level);
        }
    }

    [Test]
    public async Task RmQRCodeData_KeepsTheLevelThroughSerialization()
    {
        var data = RmQRCodeGenerator.Create("1", RmQREccLevel.H, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R9x77 });

        var restored = new RmQRCodeData(data.GetRawData(), quietZoneSize: 1);

        await Assert.That(restored.EccLevel).IsEqualTo(RmQREccLevel.H);
    }

    [Test]
    public async Task RmQRCodeData_RefusesABlankMatrix()
    {
        var (width, height) = (RmQRConstants.GetWidth(RmQRVersion.R7x43), RmQRConstants.GetHeight(RmQRVersion.R7x43));
        var raw = new byte[ExtendedHeader + (width * height + 7) / 8];
        WriteExtendedHeader(raw, RmQRConstants.SymbolTypeRmQR, width, height);

        await Assert.That(() => new RmQRCodeData(raw, 0)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task RmQRCodeData_RefusesFormatInformationOneModuleOff_ThatDecodingCorrects()
    {
        var data = RmQRCodeGenerator.Create("1", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43, QuietZoneSize = 0 });
        var (width, height) = (data.Width, data.Height);
        var raw = data.GetRawData();
        RmQRConstants.GetFormatModule(0, subFinderSide: false, height, width, out var row, out var col);
        FlipModule(raw, ExtendedHeader, width, row, col);

        await Assert.That(RmQRCodeDecoder.TryDecode(Unpack(raw, ExtendedHeader, width, height), width, height, out var text, out _)).IsTrue();
        await Assert.That(text).IsEqualTo("1");
        await Assert.That(() => new RmQRCodeData(raw, 0)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task RmQRCodeData_RefusesCopiesThatDisagree()
    {
        var data = RmQRCodeGenerator.Create("1", RmQREccLevel.H, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43, QuietZoneSize = 0 });
        var (width, height) = (data.Width, data.Height);
        var raw = data.GetRawData();
        WriteFormatCopy(raw, width, height, RmQRConstants.GetFormatBits(RmQRVersion.R7x43, RmQREccLevel.M, subFinderSide: false), subFinderSide: false);

        await Assert.That(() => new RmQRCodeData(raw, 0)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task RmQRCodeData_RefusesTheFormatOfAnotherVersion()
    {
        var data = RmQRCodeGenerator.Create("1", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43, QuietZoneSize = 0 });
        var (width, height) = (data.Width, data.Height);
        var raw = data.GetRawData();
        WriteFormatCopy(raw, width, height, RmQRConstants.GetFormatBits(RmQRVersion.R7x59, RmQREccLevel.M, subFinderSide: false), subFinderSide: false);
        WriteFormatCopy(raw, width, height, RmQRConstants.GetFormatBits(RmQRVersion.R7x59, RmQREccLevel.M, subFinderSide: true), subFinderSide: true);

        await Assert.That(() => new RmQRCodeData(raw, 0)).Throws<InvalidDataException>();
    }

    // ---- helpers ---------------------------------------------------------------------

    // "QRR" and the size for Standard QR, "QRX", the symbol type, the width and the height for the other two
    private const int StandardHeader = 4;
    private const int ExtendedHeader = 6;

    private static void WriteExtendedHeader(byte[] raw, byte symbolType, int width, int height)
    {
        "QRX"u8.CopyTo(raw);
        raw[3] = symbolType;
        raw[4] = (byte)width;
        raw[5] = (byte)height;
    }

    private static void WriteFormatCopy(byte[] raw, int width, int height, int bits, bool subFinderSide)
    {
        for (var bit = 0; bit < 18; bit++)
        {
            RmQRConstants.GetFormatModule(bit, subFinderSide, height, width, out var row, out var col);
            SetModule(raw, ExtendedHeader, width, row, col, ((bits >> bit) & 1) != 0);
        }
    }

    // The serialized modules are row-major, most significant bit first
    private static void SetModule(byte[] raw, int header, int width, int row, int col, bool dark)
    {
        var index = row * width + col;
        var bit = (byte)(0x80 >> (index & 7));
        if (dark)
            raw[header + (index >> 3)] |= bit;
        else
            raw[header + (index >> 3)] &= (byte)~bit;
    }

    private static void FlipModule(byte[] raw, int header, int width, int row, int col)
    {
        var index = row * width + col;
        raw[header + (index >> 3)] ^= (byte)(0x80 >> (index & 7));
    }

    private static byte[] Unpack(byte[] raw, int header, int width, int height)
    {
        var modules = new byte[width * height];
        for (var i = 0; i < modules.Length; i++)
            modules[i] = (byte)((raw[header + (i >> 3)] >> (7 - (i & 7))) & 1);
        return modules;
    }
}
