using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="DecodeStatus.UnmappedCharacter"/> reaches the caller through the
/// public decoder, not just the internal bitstream layer.
/// </summary>
/// <remarks>
/// <para>
/// The status exists so a caller can route the symbols a CP932-capable reader could
/// still read, and nothing else. That contract is only worth anything if the status
/// survives the whole matrix path — Reed-Solomon, de-interleaving, the segment loop —
/// and arrives in <see cref="QRCodeDecodeInfo.Status"/>. A status the caller cannot
/// observe is the same as no status.
/// </para>
/// <para>
/// The symbol has to be built here because no generator in this library emits Kanji:
/// hand-made data codewords go through the real ECC, placement, mask and format
/// pipeline, so what the decoder sees is a genuine version 1-L symbol.
/// </para>
/// </remarks>
public class KanjiUnmappedCharacterEndToEndTest
{
    private const int Version = 1;
    private const int Size = 21;

    /// <summary>ISO/IEC 18004 8.4.5 compaction, computed independently of the production helper.</summary>
    private static int Kanji(int sjis)
    {
        var shifted = sjis >= 0xE040 ? sjis - 0xC140 : sjis - 0x8140;
        return ((shifted >> 8) * 0xC0) + (shifted & 0xFF);
    }

    /// <summary>Builds a real version 1-L symbol carrying one Kanji segment of one cell.</summary>
    private static byte[] BuildSymbol(int sjis)
    {
        var eccInfo = QRCodeConstants.GetEccInfo(Version, QREccLevel.L);

        var data = new byte[eccInfo.TotalDataCodewords];
        var writer = new BitWriter(data);
        writer.Write(0b1000, 4);              // Kanji mode indicator
        writer.Write(1, 8);                   // version 1 Kanji count indicator is 8 bits
        writer.Write(Kanji(sjis), 13);
        writer.Write(0b0000, 4);              // terminator
        writer.Flush();
        for (var i = writer.GetData().Length; i < data.Length; i++)
            data[i] = (i & 1) == 0 ? (byte)0xEC : (byte)0x11; // ISO/IEC 18004 pad codewords

        var ecc = new byte[eccInfo.ECCPerBlock];
        EccBinaryEncoder.CalculateECC(data, ecc, eccInfo.ECCPerBlock);

        // Version 1 has a single block, so interleaving is concatenation.
        var codewords = new byte[data.Length + ecc.Length];
        data.CopyTo(codewords, 0);
        ecc.CopyTo(codewords, data.Length);

        var layout = ModulePlacer.GetLayout(Version);
        var modules = new byte[Size * Size];
        layout.Template.AsSpan().CopyTo(modules);
        ModulePlacer.PlaceDataWords(modules, layout, codewords);
        var mask = ModulePlacer.MaskCode(modules, Size, Version, layout.BlockedMask, QREccLevel.L);
        ModulePlacer.PlaceFormat(modules, Size, QRCodeConstants.GetFormatBits(QREccLevel.L, mask));

        return modules;
    }

    /// <summary>The construction itself is sound: a mappable cell round-trips.</summary>
    [Test]
    public async Task MappableCell_DecodesThroughThePublicApi()
    {
        var modules = BuildSymbol(0x889F); // 亜

        var ok = QRCodeDecoder.TryDecode(modules, Size, out var text, out var info);

        await Assert.That(ok).IsTrue();
        await Assert.That(text).IsEqualTo("亜");
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(info.ErrorsCorrected).IsEqualTo(0);
    }

    /// <summary>
    /// A CP932-only cell (NEC row 13) reaches the caller as
    /// <see cref="DecodeStatus.UnmappedCharacter"/>, distinct from the structural
    /// <see cref="DecodeStatus.UnsupportedContent"/>.
    /// </summary>
    [Test]
    [Arguments(0x8740)] // CP932 U+2460, circled digit one
    [Arguments(0x875F)] // CP932 roman numeral
    [Arguments(0xEAA5)] // inside the Kanji-mode range, past the JIS X 0208 repertoire
    public async Task UnmappedCell_ReachesTheCallerAsUnmappedCharacter(int sjis)
    {
        var modules = BuildSymbol(sjis);

        var ok = QRCodeDecoder.TryDecode(modules, Size, out _, out var info);

        await Assert.That(ok).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.UnmappedCharacter);
        await Assert.That(info.Status).IsNotEqualTo(DecodeStatus.UnsupportedContent);
        // The symbol itself was read cleanly; only the character could not be mapped.
        await Assert.That(info.Version).IsEqualTo(Version);
        await Assert.That(info.EccLevel).IsEqualTo(QREccLevel.L);
        await Assert.That(info.ErrorsCorrected).IsEqualTo(0);
    }

    /// <summary>
    /// The image path reports it too. This is the path a caller scanning a photographed
    /// or rendered Japanese symbol actually takes, and it retries (inverted, mirrored,
    /// neighbouring dimensions) on any non-terminal status — so a status that is correct
    /// at the matrix level can still be lost to a retry's verdict before the caller
    /// sees it.
    /// </summary>
    [Test]
    public async Task UnmappedCell_ReachesTheCallerThroughTheImagePath()
    {
        const int Scale = 8;
        const int Quiet = 4;
        var modules = BuildSymbol(0x8740);
        var side = (Size + Quiet * 2) * Scale;
        var luminance = new byte[side * side];
        luminance.AsSpan().Fill(255);
        for (var row = 0; row < Size; row++)
        {
            for (var col = 0; col < Size; col++)
            {
                if (modules[row * Size + col] == 0) continue;
                for (var y = 0; y < Scale; y++)
                    luminance.AsSpan(((Quiet + row) * Scale + y) * side + (Quiet + col) * Scale, Scale).Clear();
            }
        }

        var ok = QRCodeDecoder.TryDecodeImage(luminance, side, side, out _, out var info);

        await Assert.That(ok).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.UnmappedCharacter);
    }

    /// <summary>The span overload reports the same status, so neither path is special.</summary>
    [Test]
    public async Task UnmappedCell_ReportsTheSameStatusThroughTheSpanOverload()
    {
        var modules = BuildSymbol(0x8740);

        var ok = QRCodeDecoder.TryDecode(modules, Size, new char[64], out var charsWritten, out var info);

        await Assert.That(ok).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.UnmappedCharacter);
        await Assert.That(charsWritten).IsEqualTo(0);
    }
}
