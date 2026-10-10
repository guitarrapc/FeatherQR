namespace FeatherQR.Tests;

/// <summary>
/// Micro QR has no ECI, so a reader guesses what a Byte segment holds, and the generator must write no ISO-8859-1 bytes this library's decoder would read as UTF-8 or as Shift_JIS.
/// Such a text is written as UTF-8, which the decoder reads as written, or refused when UTF-8 does not fit.
/// </summary>
public class MicroQRUndeclaredCharsetTest
{
    private static readonly MicroQRCodeGeneratorOptions optimal = new() { Segmentation = MicroQRSegmentation.Optimal };

    public static IEnumerable<string> Lookalikes()
    {
        yield return "Ã©";             // C3 A9 is the UTF-8 of é
        yield return "Ã©x";
        yield return "ÄÖÜ";            // three bytes in the half-width katakana range
        yield return "¼½¾";
        yield return "don\u0092t";     // 0x92 before a letter is a Shift_JIS pair
        yield return "\u0093Hi\u0094";
    }

    [Test]
    [MethodDataSource(nameof(Lookalikes))]
    public async Task Create_Latin1TextTheDecoderWouldReadOtherwise_RoundTrips(string text)
    {
        foreach (var options in new[] { MicroQRCodeGeneratorOptions.Default, optimal })
        {
            var symbol = MicroQRCodeGenerator.Create(text, MicroQREccLevel.L, options);

            await Assert.That(MicroQRCodeDecoder.TryDecode(symbol, out var decoded)).IsTrue();
            await Assert.That(decoded).IsEqualTo(text).Because($"{options.Segmentation}");
        }
    }

    /// <summary>Text the decoder reads as ISO-8859-1 keeps its one byte a character: thirteen characters fit M4-L, where their sixteen UTF-8 bytes would not.</summary>
    [Test]
    public async Task Create_Latin1TextTheDecoderReadsAsWritten_KeepsOneByteACharacter()
    {
        const string text = "café à Zürich";

        var symbol = MicroQRCodeGenerator.Create(text, MicroQREccLevel.L);

        await Assert.That(symbol.Version).IsEqualTo(MicroQRVersion.M4);
        await Assert.That(MicroQRCodeDecoder.TryDecode(symbol, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(text);
    }

    /// <summary>A lookalike whose UTF-8 does not fit is refused, not written as bytes that read as another text: eight characters are sixteen UTF-8 bytes, past M4-L's fifteen.</summary>
    [Test]
    public async Task Create_LookalikeWhoseUtf8DoesNotFit_Throws()
    {
        const string text = "ÄÖÜÄÖÜÄÖ";

        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(text, MicroQREccLevel.L, out _)).IsFalse();
        Assert.Throws<ArgumentException>(() => MicroQRCodeGenerator.Create(text, MicroQREccLevel.L));
    }

    /// <summary>
    /// Every pair of characters from U+0080 to U+00FF, alone and between ASCII letters, and every triple from a set that holds each class of byte the guess looks at.
    /// A symbol that is written reads back as the text. One that cannot be written throws.
    /// </summary>
    [Test]
    public async Task Create_EveryShortLatin1Text_ReadsBackOrIsRefused()
    {
        var failures = new List<string>();
        var written = 0;
        void Check(string text)
        {
            foreach (var options in new[] { MicroQRCodeGeneratorOptions.Default, optimal })
            {
                MicroQRCodeData symbol;
                try
                {
                    symbol = MicroQRCodeGenerator.Create(text, MicroQREccLevel.L, options);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                written++;
                if (!MicroQRCodeDecoder.TryDecode(symbol, out var decoded) || decoded != text)
                    failures.Add($"{string.Join(' ', text.Select(static c => ((int)c).ToString("X2")))} ({options.Segmentation}) read as '{decoded}'");
            }
        }

        for (var a = 0x80; a <= 0xFF; a++)
        {
            for (var b = 0x80; b <= 0xFF; b++)
            {
                Check($"{(char)a}{(char)b}");
                Check($"a{(char)a}{(char)b}z");
            }
        }
        char[] classes = ['A', '~', '\u0092', '¡', 'Ä', 'ß', 'é', 'ü'];
        foreach (var a in classes)
        {
            foreach (var b in classes)
            {
                foreach (var c in classes)
                {
                    Check($"{a}{b}{c}");
                    Check($"{a}{b}{c}123456");
                }
            }
        }

        await Assert.That(failures).IsEmpty();
        await Assert.That(written).IsGreaterThan(60000);
    }
}
