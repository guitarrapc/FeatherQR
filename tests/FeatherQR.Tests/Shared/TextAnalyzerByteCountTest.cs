using System.Text;
using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// What <see cref="TextAnalyzer"/> reports as the Byte-mode data length, which is the value the
/// character count indicator carries and therefore how many payload bytes a reader will take.
/// Pinned against <see cref="Encoding"/> per charset and content class, so the analyser may
/// shortcut the count but never change it.
/// </summary>
public class TextAnalyzerByteCountTest
{
    private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

    public static IEnumerable<(string Name, string Text)> Texts =>
    [
        ("ascii", "https://example.com/item?id=42"),
        ("ascii-long", new string('a', 300)),
        ("latin1", "Crème brûlée à la carte, jalapeño, naïve café."),
        ("latin1-boundary", "ÿþ"),
        ("utf8-2byte", "Āǿ߿"),
        ("utf8-3byte", "こんにちは世界、QRコードのテストです。"),
        ("surrogate-pair", "PHOTO 📷 GALLERY 🎉🎊🎈"),
        ("mixed-widths", "aéあ🎉"),
        ("empty", ""),
    ];

    [Test]
    [MethodDataSource(nameof(Texts))]
    public async Task DataLength_AutoCharset_IsTheBytesOfTheCharsetItChose(string name, string text)
    {
        var result = TextAnalyzer.Analyze(text, EciMode.Default);
        if (result.EncodingMode != EncodingMode.Byte)
            return; // Numeric and Alphanumeric count characters, not bytes

        // Auto-detection picks Default for ASCII, ISO-8859-1 up to U+00FF and UTF-8 above it,
        // so the declared charset always represents the text and its byte count is exact.
        var expected = result.EciMode == EciMode.Utf8 ? Encoding.UTF8.GetByteCount(text) : Latin1.GetByteCount(text);

        await Assert.That(result.DataLength).IsEqualTo(expected).Because(name);
    }

    [Test]
    [MethodDataSource(nameof(Texts))]
    public async Task DataLength_ForcedUtf8_IsTheUtf8Bytes(string name, string text)
    {
        var result = TextAnalyzer.Analyze(text, EciMode.Utf8);
        if (result.EncodingMode != EncodingMode.Byte)
            return;

        await Assert.That(result.DataLength).IsEqualTo(Encoding.UTF8.GetByteCount(text)).Because(name);
    }

    [Test]
    [MethodDataSource(nameof(Texts))]
    public async Task DataLength_ForcedLatin1_IsOneBytePerChar(string name, string text)
    {
        // The generators refuse a forced ISO-8859-1 over text it cannot hold before they analyze it, so only Latin-1 text reaches
        // this count.
        if (!text.All(c => c <= 0xFF))
            return;
        var result = TextAnalyzer.Analyze(text, EciMode.Iso8859_1);
        if (result.EncodingMode != EncodingMode.Byte)
            return;

        await Assert.That(result.DataLength).IsEqualTo(text.Length).Because(name);
        await Assert.That(Latin1.GetByteCount(text)).IsEqualTo(text.Length).Because($"{name}: the encoder agrees, which is what lets the count skip it");
    }

    [Test]
    [MethodDataSource(nameof(Texts))]
    public async Task Encode_RoundTrips_SoTheCountMatchesTheBytesWritten(string name, string text)
    {
        // A data length that disagreed with what the writer emits would leave the reader
        // taking the wrong number of payload bytes, which the round trip catches.
        foreach (var eci in new[] { EciMode.Default, EciMode.Utf8 })
        {
            var symbol = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { EciMode = eci });

            await Assert.That(QRCodeDecoder.TryDecode(symbol, out var decoded, out var info)).IsTrue().Because($"{name} in {eci}: {info.Status}");
            await Assert.That(decoded).IsEqualTo(text).Because($"{name} in {eci}");
        }
    }

    [Test]
    [MethodDataSource(nameof(Texts))]
    public async Task Encode_ForcedLatin1_RoundTripsOrRefusesTheContent(string name, string text)
    {
        // A count that disagreed with the bytes written would leave the reader taking the wrong number of payload bytes, which the
        // round trip catches. Text ISO-8859-1 cannot hold is refused instead of written.
        var options = new QRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1 };
        if (!text.All(c => c <= 0xFF))
        {
            await Assert.That(() => QRCodeGenerator.Create(text, QREccLevel.M, options)).Throws<ArgumentException>().Because(name);
            return;
        }
        var symbol = QRCodeGenerator.Create(text, QREccLevel.M, options);

        await Assert.That(QRCodeDecoder.TryDecode(symbol, out var decoded, out var info)).IsTrue().Because($"{name}: {info.Status}");
        await Assert.That(decoded).IsEqualTo(text).Because(name);
    }
}
