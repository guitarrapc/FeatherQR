namespace FeatherQR.Tests;

/// <summary>
/// A forced ISO-8859-1 over text it cannot hold is an argument error on every entry point, as rMQR reports it. Writing the text anyway
/// would give a symbol that reads back as another text.
/// </summary>
public class ForcedIso8859_1Test
{
    private const string Gtin = "0109521234543213";

    // A kana, a letter a best-fit encoder would write as 'z', an emoji and U+FEFF are each outside ISO-8859-1.
    public static IEnumerable<Func<string>> TextsOutsideLatin1() =>
        [() => "a" + (char)0x3042 + "b", () => "a" + (char)0x017E + "b", () => "a" + char.ConvertFromUtf32(0x1F600) + "b", () => "ab" + (char)0xFEFF + "cd"];

    public static IEnumerable<Func<(string Name, QRCodeGeneratorOptions Options)>> Options() =>
    [
        () => ("Optimal", new() { EciMode = EciMode.Iso8859_1 }),
        () => ("Single", new() { EciMode = EciMode.Iso8859_1, Segmentation = QRSegmentation.Single }),
        () => ("Kanji allowed", new() { EciMode = EciMode.Iso8859_1, AllowKanji = true }),
        () => ("a byte order mark asked for", new() { EciMode = EciMode.Iso8859_1, Utf8Bom = true }),
        () => ("version 1", new() { EciMode = EciMode.Iso8859_1, Version = 1 }),
        () => ("FNC1 in the first position", new() { EciMode = EciMode.Iso8859_1, Fnc1 = Fnc1Mode.FirstPosition }),
    ];

    [Test]
    [MethodDataSource(nameof(TextsOutsideLatin1))]
    public async Task EveryEntryPoint_RefusesTextOutsideLatin1(string text)
    {
        foreach (var (name, options) in Options().Select(f => f()))
        {
            var content = options.Fnc1 == Fnc1Mode.FirstPosition ? Gtin + text : text;
            var destination = new byte[100_000];
            await AssertRefused(() => QRCodeGenerator.Create(content, QREccLevel.M, options), $"{name}: Create");
            await AssertRefused(() => QRCodeGenerator.Create(content.AsSpan(), QREccLevel.M, destination, options), $"{name}: Create into a span");
            await AssertRefused(() => QRCodeGenerator.TryGetRequiredBufferSize(content.AsSpan(), QREccLevel.M, out _, options), $"{name}: sizing");
            await AssertRefused(() => QRCodeGenerator.CreateStructuredAppend(content, QREccLevel.M, options), $"{name}: Structured Append");
        }
    }

    /// <summary>
    /// The refusal is an argument error, so it comes before any answer about fit: text too long for version 1 is refused for its
    /// charset, not reported as not fitting.
    /// </summary>
    [Test]
    public async Task TheRefusal_WinsOverAContentThatDoesNotFit()
    {
        var text = new string('a', 100) + (char)0x3042;
        var options = new QRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1, Version = 1 };

        await AssertRefused(() => QRCodeGenerator.TryGetRequiredBufferSize(text.AsSpan(), QREccLevel.M, out _, options), "sizing");
        await AssertRefused(() => QRCodeGenerator.Create(text, QREccLevel.M, options), "Create");
    }

    // Text ISO-8859-1 holds, its top char included, is written as before
    [Test]
    public async Task TextLatin1Holds_IsWritten()
    {
        var text = "Zürich " + (char)0xFF;
        var options = new QRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1 };

        await Assert.That(QRCodeDecoder.TryDecode(QRCodeGenerator.Create(text, QREccLevel.M, options), out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(text);
    }

    private static async Task AssertRefused(Action call, string context)
    {
        ArgumentException? error = null;
        try
        {
            call();
        }
        catch (ArgumentException e)
        {
            error = e;
        }
        await Assert.That(error).IsNotNull().Because($"{context}: refuses a forced ISO-8859-1 over text it cannot hold");
        await Assert.That(error!.GetType()).IsEqualTo(typeof(ArgumentException)).Because($"{context}: as an argument error, as rMQR reports it");
        await Assert.That(error.ParamName).IsEqualTo("eciMode").Because(context);
    }
}
