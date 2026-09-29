using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The matrix span overloads report no characters written when they return <c>false</c>, as the image overloads do.
/// Each symbol carries more than one segment, so a destination too short for the last one fails after the earlier
/// segments were written; until 2.0.0 the overloads counted those.
/// </summary>
/// <remarks>
/// A module matrix with a quiet zone is decoded from a copy of its core, one without it in place, so each case runs both.
/// </remarks>
public class MatrixDecodeCharsWrittenTest
{
    /// <summary>Lowercase text then 60 digits: a Byte segment, then a Numeric one.</summary>
    private static readonly string StandardQRText = "hello" + new string('7', 60);

    /// <summary>Digits then uppercase: a Numeric segment, then an Alphanumeric one.</summary>
    private const string MicroQRText = "1234567890ABCDEF";

    private const string RmQRText = "12345678901234567890ABCDEFGH";

    [Test]
    [Arguments(0)]
    [Arguments(4)]
    public async Task StandardQR_FailingAfterTheFirstSegment_WritesNoCharacters(int quietZone)
    {
        var qr = QRCodeGenerator.Create(StandardQRText, QREccLevel.M, new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal, QuietZoneSize = quietZone });
        var modules = Modules(qr.Size, qr.Size, (row, col) => qr[row, col]);

        for (var length = 0; length < StandardQRText.Length; length++)
        {
            var ok = QRCodeDecoder.TryDecode(modules, qr.Size, new char[length], out var charsWritten, out var info);

            await Assert.That((ok, info.Status, charsWritten)).IsEqualTo((false, DecodeStatus.DestinationTooSmall, 0)).Because($"destination of {length}");
        }
        await Assert.That(QRCodeDecoder.TryDecode(modules, qr.Size, new char[StandardQRText.Length], out var written, out _)).IsTrue();
        await Assert.That(written).IsEqualTo(StandardQRText.Length);
    }

    [Test]
    [Arguments(0)]
    [Arguments(2)]
    public async Task MicroQR_FailingAfterTheFirstSegment_WritesNoCharacters(int quietZone)
    {
        var micro = MicroQRCodeGenerator.Create(MicroQRText, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal, QuietZoneSize = quietZone });
        var modules = Modules(micro.Size, micro.Size, (row, col) => micro[row, col]);

        for (var length = 0; length < MicroQRText.Length; length++)
        {
            var ok = MicroQRCodeDecoder.TryDecode(modules, micro.Size, new char[length], out var charsWritten, out var info);

            await Assert.That((ok, info.Status, charsWritten)).IsEqualTo((false, DecodeStatus.DestinationTooSmall, 0)).Because($"destination of {length}");
        }
        await Assert.That(MicroQRCodeDecoder.TryDecode(modules, micro.Size, new char[MicroQRText.Length], out var written, out _)).IsTrue();
        await Assert.That(written).IsEqualTo(MicroQRText.Length);
    }

    [Test]
    [Arguments(0)]
    [Arguments(2)]
    public async Task RmQR_FailingAfterTheFirstSegment_WritesNoCharacters(int quietZone)
    {
        var rmqr = RmQRCodeGenerator.Create(RmQRText, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal, QuietZoneSize = quietZone });
        var modules = Modules(rmqr.Width, rmqr.Height, (row, col) => rmqr[row, col]);

        for (var length = 0; length < RmQRText.Length; length++)
        {
            var ok = RmQRCodeDecoder.TryDecode(modules, rmqr.Width, rmqr.Height, new char[length], out var charsWritten, out var info);

            await Assert.That((ok, info.Status, charsWritten)).IsEqualTo((false, DecodeStatus.DestinationTooSmall, 0)).Because($"destination of {length}");
        }
        await Assert.That(RmQRCodeDecoder.TryDecode(modules, rmqr.Width, rmqr.Height, new char[RmQRText.Length], out var written, out _)).IsTrue();
        await Assert.That(written).IsEqualTo(RmQRText.Length);
    }

    /// <summary>
    /// Not the destination alone: a symbol whose second segment is a Kanji cell CP932 adds to JIS X 0208 fails with
    /// <see cref="DecodeStatus.UnmappedCharacter"/> after its first segment was written, and writes no characters either.
    /// </summary>
    [Test]
    public async Task StandardQR_UnmappedCharacterAfterTheFirstSegment_WritesNoCharacters()
    {
        var modules = BuildByteThenKanjiSymbol("OK", sjis: 0x8740);

        var ok = QRCodeDecoder.TryDecode(modules, 21, new char[64], out var charsWritten, out var info);

        await Assert.That((ok, info.Status, charsWritten)).IsEqualTo((false, DecodeStatus.UnmappedCharacter, 0));
    }

    /// <summary>The same symbol with a mappable cell decodes, so the failure above is the cell's.</summary>
    [Test]
    public async Task StandardQR_ByteThenMappableKanji_Decodes()
    {
        var modules = BuildByteThenKanjiSymbol("OK", sjis: 0x889F); // 亜

        var ok = QRCodeDecoder.TryDecode(modules, 21, new char[64], out var charsWritten, out var info);

        await Assert.That((ok, info.Status, charsWritten)).IsEqualTo((true, DecodeStatus.Success, 3));
    }

    private static byte[] Modules(int width, int height, Func<int, int, bool> dark)
    {
        var modules = new byte[width * height];
        for (var row = 0; row < height; row++)
            for (var col = 0; col < width; col++)
                modules[row * width + col] = dark(row, col) ? (byte)1 : (byte)0;
        return modules;
    }

    /// <summary>A version 1-L symbol holding a Byte segment of <paramref name="text"/> and then one Kanji cell.</summary>
    private static byte[] BuildByteThenKanjiSymbol(string text, int sjis)
    {
        const int version = 1;
        const int size = 21;
        var eccInfo = QRCodeConstants.GetEccInfo(version, QREccLevel.L);

        var data = new byte[eccInfo.TotalDataCodewords];
        var writer = new BitWriter(data);
        writer.Write(0b0100, 4);         // Byte mode indicator
        writer.Write(text.Length, 8);    // count indicator, versions 1-9
        foreach (var c in text)
            writer.Write(c, 8);
        writer.Write(0b1000, 4);         // Kanji mode indicator
        writer.Write(1, 8);              // one cell
        var shifted = sjis >= 0xE040 ? sjis - 0xC140 : sjis - 0x8140;
        writer.Write((shifted >> 8) * 0xC0 + (shifted & 0xFF), 13);
        writer.Write(0b0000, 4);         // terminator
        writer.Flush();
        for (var i = writer.GetData().Length; i < data.Length; i++)
            data[i] = (i & 1) == 0 ? (byte)0xEC : (byte)0x11;

        var ecc = new byte[eccInfo.ECCPerBlock];
        EccBinaryEncoder.CalculateECC(data, ecc, eccInfo.ECCPerBlock);
        var codewords = new byte[BinaryInterleaver.CalculateInterleavedSize(eccInfo, QRCodeConstants.GetRemainderBits(version))];
        BinaryInterleaver.InterleaveCodewords(data, ecc, codewords, eccInfo);

        var layout = ModulePlacer.GetLayout(version);
        var modules = new byte[size * size];
        layout.Template.AsSpan().CopyTo(modules);
        ModulePlacer.PlaceDataWords(modules, layout, codewords);
        var mask = ModulePlacer.MaskCode(modules, size, version, layout.BlockedMask, QREccLevel.L);
        ModulePlacer.PlaceFormat(modules, size, QRCodeConstants.GetFormatBits(QREccLevel.L, mask));
        return modules;
    }
}
