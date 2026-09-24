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
/// pipeline, so what the decoder sees is a genuine level L symbol (version 1 here).
/// </para>
/// </remarks>
public class KanjiUnmappedCharacterEndToEndTest
{
    private const int Version = 1;
    internal const int Size = 21;

    /// <summary>ISO/IEC 18004 8.4.5 compaction, computed independently of the production helper.</summary>
    private static int Kanji(int sjis)
    {
        var shifted = sjis >= 0xE040 ? sjis - 0xC140 : sjis - 0x8140;
        return ((shifted >> 8) * 0xC0) + (shifted & 0xFF);
    }

    /// <summary>Builds a real level L symbol carrying one Kanji segment of one cell, version 1 unless another is given.</summary>
    internal static byte[] BuildSymbol(int sjis, int version = Version)
    {
        var eccInfo = QRCodeConstants.GetEccInfo(version, QREccLevel.L);
        var size = 17 + 4 * version;

        var data = new byte[eccInfo.TotalDataCodewords];
        var writer = new BitWriter(data);
        writer.Write(0b1000, 4);              // Kanji mode indicator
        writer.Write(1, version <= 9 ? 8 : version <= 26 ? 10 : 12); // Kanji count indicator width
        writer.Write(Kanji(sjis), 13);
        writer.Write(0b0000, 4);              // terminator
        writer.Flush();
        for (var i = writer.GetData().Length; i < data.Length; i++)
            data[i] = (i & 1) == 0 ? (byte)0xEC : (byte)0x11; // ISO/IEC 18004 pad codewords

        var blocks = eccInfo.BlocksInGroup1 + eccInfo.BlocksInGroup2;
        var ecc = new byte[blocks * eccInfo.ECCPerBlock];
        for (int b = 0, d = 0; b < blocks; b++)
        {
            var length = b < eccInfo.BlocksInGroup1 ? eccInfo.CodewordsInGroup1 : eccInfo.CodewordsInGroup2;
            EccBinaryEncoder.CalculateECC(data.AsSpan(d, length), ecc.AsSpan(b * eccInfo.ECCPerBlock, eccInfo.ECCPerBlock), eccInfo.ECCPerBlock);
            d += length;
        }
        var codewords = new byte[BinaryInterleaver.CalculateInterleavedSize(eccInfo, QRCodeConstants.GetRemainderBits(version))];
        BinaryInterleaver.InterleaveCodewords(data, ecc, codewords, eccInfo);

        var layout = ModulePlacer.GetLayout(version);
        var modules = new byte[size * size];
        layout.Template.AsSpan().CopyTo(modules);
        ModulePlacer.PlaceDataWords(modules, layout, codewords);
        var mask = ModulePlacer.MaskCode(modules, size, version, layout.BlockedMask, QREccLevel.L);
        ModulePlacer.PlaceFormat(modules, size, QRCodeConstants.GetFormatBits(QREccLevel.L, mask));
        if (version >= 7)
            ModulePlacer.PlaceVersion(modules, size, QRCodeConstants.GetVersionBits(version));

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
    /// or rendered Japanese symbol actually takes, and it retries inverted on any failure and
    /// mirrored or at neighbouring dimensions on a failure short of the content, so a status
    /// that is correct at the matrix level could be lost to a retry's verdict before the
    /// caller sees it.
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

    /// <summary>
    /// A verdict read by a later attempt reaches the caller.
    /// Reversed reflectance is read by the inverted retry, uneven light only by the regional pass after both polarities.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnmappedCell_ReadOnlyByALaterAttempt_ReachesTheCaller(bool unevenLight)
    {
        const int Quiet = 4;
        var modules = BuildSymbol(0x8740);
        Func<int, int, bool> isDark = (row, column) => row >= Quiet && column >= Quiet && row < Quiet + Size && column < Quiet + Size && modules[(row - Quiet) * Size + column - Quiet] != 0;
        var (luminance, width, height) = UnevenLightingRenderer.Render(isDark, Size + 2 * Quiet, Size + 2 * Quiet, 4, UnevenLight.Shadow, 0f, unevenLight ? 0.55f : 0f);
        if (!unevenLight)
        {
            for (var i = 0; i < luminance.Length; i++)
                luminance[i] = (byte)(255 - luminance[i]);
        }

        var ok = QRCodeDecoder.TryDecodeImage(luminance, width, height, out _, out var info);

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
