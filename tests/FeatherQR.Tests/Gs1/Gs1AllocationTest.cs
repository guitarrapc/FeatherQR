using FeatherQR.Gs1;

namespace FeatherQR.Tests;

/// <summary>
/// The GS1 layer allocates nothing on its span paths (qrcode-gs1-data.md, "The data layer"):
/// <see cref="Gs1ElementString.TryValidate"/> on an accepted and on a refused element string, a
/// <c>foreach</c> over <see cref="Gs1ElementString.EnumerateElements"/>, the span forms and length twins
/// of the (AI) text conversions, and the destination and size forms of the generator entry points. Release only, as
/// <see cref="DecodeAllocationTest"/> is, since a Debug build allocates where an optimized one does not.
/// </summary>
[NotInParallel]
public class Gs1AllocationTest
{
#if !DEBUG
    private const string Photograph = "0109504000059101" + "2112345678p901" + "\u001D" + "101234567p" + "\u001D" + "17141120" + "8200http://www.gs1.org/demo/";

    /// <summary>90 characters, the longest value a company-internal AI takes. Six of them put (AI) text past the entry points' 512-character stack buffers.</summary>
    private const string LongValue = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123ABCDEFGHIJKLMNOPQRSTUVWXYZ0123ABCDEFGHIJKLMNOPQRSTUVWXYZ0123";

    private static long SteadyStateBytes(Action call)
    {
        for (var i = 0; i < 3; i++)
            call();
        var fewest = long.MaxValue;
        for (var round = 0; round < 3; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 4; i++)
                call();
            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return fewest;
    }

    [Test]
    public async Task TryValidate_AllocatesNothing()
    {
        var accepted = Photograph;
        var refused = "0109520123456788" + "10ABC" + "\u001D" + "10DEF";
        await Assert.That(SteadyStateBytes(() => Gs1ElementString.TryValidate(accepted.AsSpan(), out _))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => Gs1ElementString.TryValidate(refused.AsSpan(), out _))).IsEqualTo(0);
    }

    [Test]
    public async Task HriSpanForms_AllocateNothing()
    {
        var text = Photograph;
        var hri = "(01)09504000059101(21)12345678p901(10)1234567p(17)141120(8200)http://www.gs1.org/demo/";
        var hriBuffer = new char[Photograph.Length * 2];
        var esBuffer = new char[hri.Length];
        await Assert.That(SteadyStateBytes(() => Gs1ElementString.TryToHri(text.AsSpan(), hriBuffer, out _, out _))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => Gs1ElementString.TryGetToHriLength(text.AsSpan(), out _, out _))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => Gs1ElementString.TryFromHri(hri.AsSpan(), esBuffer, out _, out _))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => Gs1ElementString.TryGetFromHriLength(hri.AsSpan(), out _, out _))).IsEqualTo(0);
    }

    [Test]
    public async Task DigitalLinkAndReadSpanForms_AllocateNothing()
    {
        var text = Photograph;
        var uri = "https://id.gs1.org/01/09504000059101/10/1234567p/21/12345678p901?17=141120&8200=http%3A%2F%2Fwww.gs1.org%2Fdemo%2F";
        var uriBuffer = new char[256];
        var esBuffer = new char[uri.Length];
        var hriBuffer = new char[2 * uri.Length];
        await Assert.That(SteadyStateBytes(() => Gs1ElementString.TryToDigitalLink(text.AsSpan(), uriBuffer, out _, out _))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => Gs1ElementString.TryGetToDigitalLinkLength(text.AsSpan(), out _, out _))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => Gs1ElementString.TryFromDigitalLink(uri.AsSpan(), esBuffer, out _, out _))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => Gs1ElementString.TryGetFromDigitalLinkLength(uri.AsSpan(), out _, out _))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => Gs1ElementString.TryRead(uri.AsSpan(), default, hriBuffer, out _, out _))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => Gs1ElementString.TryRead(text.AsSpan(), Fnc1Mode.FirstPosition, hriBuffer, out _, out _))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => Gs1ElementString.TryGetReadLength(uri.AsSpan(), default, out _, out _))).IsEqualTo(0);
    }

    /// <summary>
    /// The destination and size forms of the entry points on data they take, from (AI) text and from an element
    /// string, under both segmentations, and the size forms on data they refuse, where a destination form throws.
    /// The long text is past the stack buffers, where the pool's arrays come back to the thread that returned them.
    /// </summary>
    [Test]
    [Arguments("(01)09506000134352(10)ABC123(17)201225")]
    [Arguments("0109506000134352" + "10ABC123" + "\u001D" + "17201225")]
    [Arguments("(01)09506000134352(91)" + LongValue + "(92)" + LongValue + "(93)" + LongValue + "(94)" + LongValue + "(95)" + LongValue + "(96)" + LongValue)]
    public async Task StandardQrEntryPoints_SpanAndSizeForms_AllocateNothing(string text)
    {
        var destination = new byte[40_000];
        var optimal = new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal };
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text.AsSpan(), QREccLevel.L, out _, stem: "https://brand.example.com/")).IsTrue().Because("the premise: the data is taken on both carriers");

        await Assert.That(SteadyStateBytes(() => QRCodeGenerator.CreateGs1(text.AsSpan(), QREccLevel.L, destination))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => QRCodeGenerator.CreateGs1(text.AsSpan(), QREccLevel.L, destination, optimal))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => QRCodeGenerator.TryGetRequiredBufferSizeGs1(text.AsSpan(), QREccLevel.L, out _))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => QRCodeGenerator.TryGetRequiredBufferSizeGs1(text.AsSpan(), QREccLevel.L, out _, optimal))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => QRCodeGenerator.CreateGs1DigitalLink(text.AsSpan(), QREccLevel.L, destination))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => QRCodeGenerator.CreateGs1DigitalLink(text.AsSpan(), QREccLevel.L, destination, optimal, "https://brand.example.com/"))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text.AsSpan(), QREccLevel.L, out _))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text.AsSpan(), QREccLevel.L, out _, optimal, "https://brand.example.com/"))).IsEqualTo(0);

        // Refused data: a wrong check digit in the element both forms start with.
        var refused = text.Replace("09506000134352", "09506000134353");
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1(refused.AsSpan(), QREccLevel.L, out _)).IsFalse();
        await Assert.That(SteadyStateBytes(() => QRCodeGenerator.TryGetRequiredBufferSizeGs1(refused.AsSpan(), QREccLevel.L, out _))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(refused.AsSpan(), QREccLevel.L, out _))).IsEqualTo(0);
    }

    [Test]
    [Arguments("(01)09506000134352(10)ABC123(17)201225")]
    [Arguments("0109506000134352" + "10ABC123" + "\u001D" + "17201225")]
    public async Task RmQrEntryPoints_SpanAndSizeForms_AllocateNothing(string text)
    {
        var destination = new byte[40_000];
        var optimal = new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal };
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text.AsSpan(), RmQREccLevel.M, out _, stem: "https://brand.example.com/")).IsTrue().Because("the premise: the data is taken on both carriers");

        await Assert.That(SteadyStateBytes(() => RmQRCodeGenerator.CreateGs1(text.AsSpan(), RmQREccLevel.M, destination))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => RmQRCodeGenerator.CreateGs1(text.AsSpan(), RmQREccLevel.M, destination, optimal))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(text.AsSpan(), RmQREccLevel.M, out _))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(text.AsSpan(), RmQREccLevel.M, out _, optimal))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => RmQRCodeGenerator.CreateGs1DigitalLink(text.AsSpan(), RmQREccLevel.M, destination))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => RmQRCodeGenerator.CreateGs1DigitalLink(text.AsSpan(), RmQREccLevel.M, destination, optimal, "https://brand.example.com/"))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text.AsSpan(), RmQREccLevel.M, out _))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text.AsSpan(), RmQREccLevel.M, out _, optimal, "https://brand.example.com/"))).IsEqualTo(0);

        var refused = text.Replace("09506000134352", "09506000134353");
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(refused.AsSpan(), RmQREccLevel.M, out _)).IsFalse();
        await Assert.That(SteadyStateBytes(() => RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(refused.AsSpan(), RmQREccLevel.M, out _))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(refused.AsSpan(), RmQREccLevel.M, out _))).IsEqualTo(0);
    }

    [Test]
    public async Task EnumerateElements_AllocatesNothing()
    {
        var text = Photograph;
        await Assert.That(SteadyStateBytes(() =>
        {
            var count = 0;
            foreach (var element in Gs1ElementString.EnumerateElements(text.AsSpan()))
                count += element.Value.Length;
            if (count == 0)
                throw new InvalidOperationException();
        })).IsEqualTo(0);
    }
#else
    [Test]
    [Skip("Release only: a Debug build allocates where an optimized one does not.")]
    public Task AllocationTests_RunInReleaseOnly() => Task.CompletedTask;
#endif
}
