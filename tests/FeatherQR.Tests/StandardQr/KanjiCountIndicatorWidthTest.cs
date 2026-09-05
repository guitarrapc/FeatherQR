using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// Standard QR Kanji character count indicator widths (ISO/IEC 18004 Table 3):
/// 8 bits for versions 1-9, 10 for 10-26, 12 for 27-40.
/// </summary>
/// <remarks>
/// The 12-bit band was unguarded when Kanji decoding shipped: the committed Kanji
/// fixtures are versions 1, 2 and 12, and <see cref="QRBinaryDecoderUnitTest"/> builds
/// every hand-made stream at version 1, so nothing reached versions 27-40. Deleting
/// that band (returning 10 for 27-40) passed the entire suite; the 10-bit band was
/// covered, but by exactly one fixture (`kanji-long-q`, version 12). These tests pin
/// every band and both boundaries, at the accessor and through the decoder, because
/// the accessor alone would not catch a caller that stopped using it.
/// </remarks>
public class KanjiCountIndicatorWidthTest
{
    /// <summary>ISO/IEC 18004 8.4.5 compaction, computed independently of the production helper.</summary>
    private static int Kanji(int sjis)
    {
        var shifted = sjis >= 0xE040 ? sjis - 0xC140 : sjis - 0x8140;
        return ((shifted >> 8) * 0xC0) + (shifted & 0xFF);
    }

    [Test]
    [Arguments(1, 8)]
    [Arguments(2, 8)]
    [Arguments(9, 8)]   // last version of the 8-bit band
    [Arguments(10, 10)] // first version of the 10-bit band
    [Arguments(12, 10)]
    [Arguments(26, 10)] // last version of the 10-bit band
    [Arguments(27, 12)] // first version of the 12-bit band
    [Arguments(40, 12)]
    public async Task GetKanjiCountIndicatorLength_MatchesTable3(int version, int expected)
    {
        await Assert.That(EncodingModeExtensions.GetKanjiCountIndicatorLength(version)).IsEqualTo(expected);
    }

    /// <summary>
    /// Every version in 1-40 returns one of the three legal widths and never throws.
    /// Deliberately does NOT recompute the band expression: a test that restates the
    /// implementation stops being a spec check the moment the implementation is
    /// rewritten. The bands themselves are pinned by the table above.
    /// </summary>
    [Test]
    public async Task GetKanjiCountIndicatorLength_ReturnsALegalWidthForEveryVersion()
    {
        var previous = 0;
        for (var version = 1; version <= 40; version++)
        {
            var width = EncodingModeExtensions.GetKanjiCountIndicatorLength(version);
            await Assert.That(new[] { 8, 10, 12 }.Contains(width)).IsTrue().Because($"version {version} returned {width}");
            await Assert.That(width).IsGreaterThanOrEqualTo(previous).Because("count widths never shrink with version");
            previous = width;
        }
    }

    /// <summary>
    /// End-to-end through the decoder, one version per band. The stream is built with
    /// the band's own width, so reading the count at any other width consumes the wrong
    /// number of bits and the payload cannot come back intact.
    /// </summary>
    [Test]
    [Arguments(1, 8)]
    [Arguments(9, 8)]
    [Arguments(10, 10)]
    [Arguments(26, 10)]
    [Arguments(27, 12)]
    [Arguments(40, 12)]
    public async Task KanjiSegment_DecodesAtEveryVersionBand(int version, int countBits)
    {
        var data = Build(
            (0b1000, 4), (3, countBits),
            (Kanji(0x93FA), 13), (Kanji(0x967B), 13), (Kanji(0x8CEA), 13),
            (0b0000, 4));

        var (status, text) = Decode(data, version);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success).Because($"version {version}");
        await Assert.That(text).IsEqualTo("日本語");
    }

    /// <summary>
    /// The negative half: a stream written for one band must NOT decode cleanly when a
    /// neighbouring band's width is applied. Without this, a decoder that used one
    /// width everywhere would still pass the positive cases at that width.
    /// </summary>
    [Test]
    [Arguments(9, 8, 10)]   // 8-bit stream read by the 10-bit band
    [Arguments(10, 10, 9)]  // 10-bit stream read by the 8-bit band
    [Arguments(27, 12, 26)] // 12-bit stream read by the 10-bit band
    [Arguments(26, 10, 27)] // 10-bit stream read by the 12-bit band
    public async Task KanjiSegment_ReadAtTheWrongVersionBand_DoesNotReproduceThePayload(int writtenAtVersion, int countBits, int readAtVersion)
    {
        var data = Build(
            (0b1000, 4), (3, countBits),
            (Kanji(0x93FA), 13), (Kanji(0x967B), 13), (Kanji(0x8CEA), 13),
            (0b0000, 4));

        var (status, text) = Decode(data, readAtVersion);

        await Assert.That(status == DecodeStatus.Success && text == "日本語").IsFalse()
            .Because($"a stream written for version {writtenAtVersion} ({countBits}-bit count) must not read cleanly at version {readAtVersion}");
    }

    private static (DecodeStatus Status, string Text) Decode(byte[] data, int version)
    {
        Span<char> destination = stackalloc char[64];
        var status = QRBinaryDecoder.DecodeBitStream(data, version, destination, out var charsWritten);
        return (status, destination.Slice(0, charsWritten).ToString());
    }

    private static byte[] Build(params (int value, int bits)[] fields)
    {
        var buffer = new byte[64];
        var writer = new BitWriter(buffer);
        foreach (var (value, bits) in fields)
        {
            writer.Write(value, bits);
        }
        writer.Flush();
        return writer.GetData().ToArray();
    }
}
