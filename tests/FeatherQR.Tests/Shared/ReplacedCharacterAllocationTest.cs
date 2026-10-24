namespace FeatherQR.Tests;

/// <summary>
/// Sizing and span encode allocate nothing on text whose charset replaces some of its characters, a lone surrogate under UTF-8, written
/// as U+FFFD. The runtime's encoders allocate in each call that meets one. Each case also pins what a reader gets back, so the library's
/// own UTF-8 code writes the same symbol.
/// </summary>
/// <remarks>
/// The allocation checks are Release only, and run alone, as <see cref="DecodeAllocationTest"/>'s do. <see cref="NetStandardBuildParityTest"/>
/// holds the netstandard builds to no allocation on such text.
/// </remarks>
[NotInParallel]
public class ReplacedCharacterAllocationTest
{
    private const string Gtin = "0109521234543213";

    public sealed record QRCase(string Name, string Text, QRCodeGeneratorOptions Options, string Decoded)
    {
        public override string ToString() => Name;
    }

    public sealed record MicroQRCase(string Name, string Text, MicroQRCodeGeneratorOptions Options, string Decoded)
    {
        public override string ToString() => Name;
    }

    public sealed record RmQRCase(string Name, string Text, RmQRCodeGeneratorOptions Options, string Decoded)
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<Func<QRCase>> QRCases()
    {
        yield return () => new("a high, a low and a trailing high surrogate alone", "a\uD800b\uDC00c\uD83D", default, "a\uFFFDb\uFFFDc\uFFFD");
        yield return () => new("a reversed pair", "\uDE00\uD83Dx", default, "\uFFFD\uFFFDx");
        yield return () => new("Single", "a\uD800b", new() { Segmentation = QRSegmentation.Single }, "a\uFFFDb");
        yield return () => new("with a byte order mark", "a\uD800b", new() { Utf8Bom = true }, "a\uFFFDb");
        yield return () => new("ECI UTF-8", "a\uD800b", new() { EciMode = EciMode.Utf8 }, "a\uFFFDb");
        yield return () => new("beside Kanji", "日本\uD800語", new() { AllowKanji = true }, "日本\uFFFD語");
        yield return () => new("FNC1 in the first position", Gtin + "\u001d10AB\uD800C", new() { Fnc1 = Fnc1Mode.FirstPosition }, Gtin + "\u001d10AB\uFFFDC");
        // Past the stack budget for one run, so the run transcodes into a pooled buffer
        yield return () => new("a long run, Single", string.Concat(Enumerable.Repeat("こんにちは\uD800", 20)), new() { Segmentation = QRSegmentation.Single }, string.Concat(Enumerable.Repeat("こんにちは\uFFFD", 20)));
    }

    public static IEnumerable<Func<MicroQRCase>> MicroQRCases()
    {
        yield return () => new("a high surrogate alone", "a\uD800", default, "a\uFFFD");
        yield return () => new("a low surrogate alone after digits, Optimal", "123\uDC00", new() { Segmentation = MicroQRSegmentation.Optimal }, "123\uFFFD");
    }

    public static IEnumerable<Func<RmQRCase>> RmQRCases()
    {
        yield return () => new("a high and a low surrogate alone", "a\uD800b\uDC00c", default, "a\uFFFDb\uFFFDc");
        yield return () => new("Single", "a\uD800b", new() { Segmentation = RmQRSegmentation.Single }, "a\uFFFDb");
        yield return () => new("ECI UTF-8", "a\uD800b", new() { EciMode = EciMode.Utf8 }, "a\uFFFDb");
        yield return () => new("beside Kanji", "日本\uD800語", new() { AllowKanji = true }, "日本\uFFFD語");
        yield return () => new("FNC1 in the first position", Gtin + "\u001d10AB\uD800C", new() { Fnc1 = Fnc1Mode.FirstPosition }, Gtin + "\u001d10AB\uFFFDC");
    }

    [Test]
    [MethodDataSource(nameof(QRCases))]
    public async Task StandardQR_ReadsTheReplacementsAndAllocatesNothing(QRCase c)
    {
        var options = c.Options;
        var size = Sizing.Required(c.Text, QREccLevel.M, options);
        var matrix = new byte[size.BufferSize];
        QRCodeGenerator.Create(c.Text, QREccLevel.M, matrix, options);
        await Assert.That(QRCodeDecoder.TryDecode(matrix, size.Size, out string text, out _) ? text : null).IsEqualTo(c.Decoded);

#if !DEBUG
        await Assert.That(DecodeAllocationTest.SteadyStateBytes(() => QRCodeGenerator.TryGetRequiredBufferSize(c.Text, QREccLevel.M, out _, in options))).IsEqualTo(0)
            .Because("bytes the sizing allocates once warm");
        await Assert.That(DecodeAllocationTest.SteadyStateBytes(() => QRCodeGenerator.Create(c.Text, QREccLevel.M, matrix, in options))).IsEqualTo(0)
            .Because("bytes the encode allocates once warm");
#endif
    }

#if !DEBUG
    /// <summary>
    /// The fallback buffer kept for replaced characters costs nothing to text that holds none, even on a thread's first sizing and encode.
    /// </summary>
    /// <remarks>
    /// The calls on this thread first build the process's lookup tables, so the new thread's calls meet nothing built once a process.
    /// Its first calls are measured, since a cost a thread pays once would not show in later ones.
    /// </remarks>
    [Test]
    [Arguments("hello world")] // Byte, no ECI
    [Arguments("Zürich")] // Byte, ISO-8859-1
    [Arguments("こんにちは")] // Byte, UTF-8
    [Arguments("a😀b")] // Byte, UTF-8, a surrogate pair
    public async Task StandardQR_AThreadsFirstEncodeAllocatesNothingWithoutAReplacedCharacter(string text)
    {
        var size = Sizing.Required(text, QREccLevel.M, default(QRCodeGeneratorOptions));
        var matrix = new byte[size.BufferSize];
        QRCodeGenerator.Create(text, QREccLevel.M, matrix);

        var allocated = -1L;
        var thread = new Thread(() =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            QRCodeGenerator.TryGetRequiredBufferSize(text, QREccLevel.M, out _);
            QRCodeGenerator.Create(text, QREccLevel.M, matrix);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        });
        thread.Start();
        thread.Join();
        await Assert.That(allocated).IsEqualTo(0L);
    }
#endif

    [Test]
    [MethodDataSource(nameof(MicroQRCases))]
    public async Task MicroQR_ReadsTheReplacementsAndAllocatesNothing(MicroQRCase c)
    {
        var options = c.Options;
        var size = Sizing.Required(c.Text, MicroQREccLevel.L, options);
        var matrix = new byte[size.BufferSize];
        MicroQRCodeGenerator.Create(c.Text, MicroQREccLevel.L, matrix, options);
        await Assert.That(MicroQRCodeDecoder.TryDecode(matrix, size.Size, out string text, out _) ? text : null).IsEqualTo(c.Decoded);

#if !DEBUG
        await Assert.That(DecodeAllocationTest.SteadyStateBytes(() => MicroQRCodeGenerator.TryGetRequiredBufferSize(c.Text, MicroQREccLevel.L, out _, in options))).IsEqualTo(0)
            .Because("bytes the sizing allocates once warm");
        await Assert.That(DecodeAllocationTest.SteadyStateBytes(() => MicroQRCodeGenerator.Create(c.Text, MicroQREccLevel.L, matrix, in options))).IsEqualTo(0)
            .Because("bytes the encode allocates once warm");
#endif
    }

    [Test]
    [MethodDataSource(nameof(RmQRCases))]
    public async Task RmQR_ReadsTheReplacementsAndAllocatesNothing(RmQRCase c)
    {
        var options = c.Options;
        var size = Sizing.Required(c.Text, RmQREccLevel.M, options);
        var matrix = new byte[size.BufferSize];
        RmQRCodeGenerator.Create(c.Text, RmQREccLevel.M, matrix, options);
        await Assert.That(RmQRCodeDecoder.TryDecode(matrix, size.Width, size.Height, out string text, out _) ? text : null).IsEqualTo(c.Decoded);

#if !DEBUG
        await Assert.That(DecodeAllocationTest.SteadyStateBytes(() => RmQRCodeGenerator.TryGetRequiredBufferSize(c.Text, RmQREccLevel.M, out _, in options))).IsEqualTo(0)
            .Because("bytes the sizing allocates once warm");
        await Assert.That(DecodeAllocationTest.SteadyStateBytes(() => RmQRCodeGenerator.Create(c.Text, RmQREccLevel.M, matrix, in options))).IsEqualTo(0)
            .Because("bytes the encode allocates once warm");
#endif
    }
}
