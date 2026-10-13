using System.Diagnostics;
using FeatherQR.Gs1;
using FeatherQR.Gs1.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The GS1 entry points member by member. Each of the fourteen members passes its own arguments on, so what one
/// member is held to here every member is held to: the level it writes at, the order in which it reports a wrong
/// argument and wrong data, the century it reads a two-digit year in, and whether it reads text that is longer
/// than any symbol holds. What one signature cannot show follows:
/// which form a bare third argument selects and what it writes, the destination forms' errors for a buffer that
/// is too small and for content that does not fit, the stem's share of the URI buffer, and text too long for any
/// symbol.
/// </summary>
public class Gs1SymbolMemberTest
{
    private const string Gtin = "09506000134352";

    /// <summary>
    /// One member behind a signature all fourteen share: the text, the level (0 for M, 1 for H, anything else an
    /// undefined value) and the FNC1 option. The result is the size in bytes of the symbol written or sized, or -1
    /// where a size form answers <c>false</c>.
    /// </summary>
    public sealed record Member(string Name, bool IsLink, bool IsRmQr, bool IsSizeForm, Func<string, int, Fnc1Mode, int> Bytes)
    {
        public override string ToString() => Name;
    }

    private static QREccLevel Qr(int level) => level switch { 0 => QREccLevel.M, 1 => QREccLevel.H, _ => (QREccLevel)9 };

    private static RmQREccLevel Rm(int level) => level switch { 0 => RmQREccLevel.M, 1 => RmQREccLevel.H, _ => (RmQREccLevel)9 };

    public static IEnumerable<Func<Member>> Members()
    {
        yield return () => new("QRCodeGenerator.CreateGs1", false, false, false, (text, level, fnc1) =>
        {
            var data = QRCodeGenerator.CreateGs1(text, Qr(level), new QRCodeGeneratorOptions { Fnc1 = fnc1 });
            return data.Size * data.Size;
        });
        yield return () => new("QRCodeGenerator.CreateGs1 (destination)", false, false, false, (text, level, fnc1)
            => QRCodeGenerator.CreateGs1(text, Qr(level), new byte[40_000], new QRCodeGeneratorOptions { Fnc1 = fnc1 }));
        yield return () => new("QRCodeGenerator.TryGetRequiredBufferSizeGs1", false, false, true, (text, level, fnc1)
            => QRCodeGenerator.TryGetRequiredBufferSizeGs1(text, Qr(level), out var size, new QRCodeGeneratorOptions { Fnc1 = fnc1 }) ? size.BufferSize : -1);
        yield return () => new("QRCodeGenerator.CreateGs1DigitalLink", true, false, false, (text, level, fnc1) =>
        {
            var data = QRCodeGenerator.CreateGs1DigitalLink(text, Qr(level), new QRCodeGeneratorOptions { Fnc1 = fnc1 }, null);
            return data.Size * data.Size;
        });
        yield return () => new("QRCodeGenerator.CreateGs1DigitalLink (options only)", true, false, false, (text, level, fnc1) =>
        {
            var options = new QRCodeGeneratorOptions { Fnc1 = fnc1 };
            var data = QRCodeGenerator.CreateGs1DigitalLink(text, Qr(level), in options);
            return data.Size * data.Size;
        });
        yield return () => new("QRCodeGenerator.CreateGs1DigitalLink (destination)", true, false, false, (text, level, fnc1)
            => QRCodeGenerator.CreateGs1DigitalLink(text, Qr(level), new byte[40_000], new QRCodeGeneratorOptions { Fnc1 = fnc1 }));
        yield return () => new("QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink", true, false, true, (text, level, fnc1)
            => QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text, Qr(level), out var size, new QRCodeGeneratorOptions { Fnc1 = fnc1 }) ? size.BufferSize : -1);

        yield return () => new("RmQRCodeGenerator.CreateGs1", false, true, false, (text, level, fnc1) =>
        {
            var data = RmQRCodeGenerator.CreateGs1(text, Rm(level), new RmQRCodeGeneratorOptions { Fnc1 = fnc1 });
            return data.Width * data.Height;
        });
        yield return () => new("RmQRCodeGenerator.CreateGs1 (destination)", false, true, false, (text, level, fnc1)
            => RmQRCodeGenerator.CreateGs1(text, Rm(level), new byte[40_000], new RmQRCodeGeneratorOptions { Fnc1 = fnc1 }));
        yield return () => new("RmQRCodeGenerator.TryGetRequiredBufferSizeGs1", false, true, true, (text, level, fnc1)
            => RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(text, Rm(level), out var size, new RmQRCodeGeneratorOptions { Fnc1 = fnc1 }) ? size.BufferSize : -1);
        yield return () => new("RmQRCodeGenerator.CreateGs1DigitalLink", true, true, false, (text, level, fnc1) =>
        {
            var data = RmQRCodeGenerator.CreateGs1DigitalLink(text, Rm(level), new RmQRCodeGeneratorOptions { Fnc1 = fnc1 }, null);
            return data.Width * data.Height;
        });
        yield return () => new("RmQRCodeGenerator.CreateGs1DigitalLink (options only)", true, true, false, (text, level, fnc1) =>
        {
            var options = new RmQRCodeGeneratorOptions { Fnc1 = fnc1 };
            var data = RmQRCodeGenerator.CreateGs1DigitalLink(text, Rm(level), in options);
            return data.Width * data.Height;
        });
        yield return () => new("RmQRCodeGenerator.CreateGs1DigitalLink (destination)", true, true, false, (text, level, fnc1)
            => RmQRCodeGenerator.CreateGs1DigitalLink(text, Rm(level), new byte[40_000], new RmQRCodeGeneratorOptions { Fnc1 = fnc1 }));
        yield return () => new("RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink", true, true, true, (text, level, fnc1)
            => RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text, Rm(level), out var size, new RmQRCodeGeneratorOptions { Fnc1 = fnc1 }) ? size.BufferSize : -1);
    }

    /// <summary>The size the core gives the string the member ends in: the element string under FNC1 in first position, or the URI as plain text.</summary>
    private static int SizedByTheCore(Member member, string hri, int level)
    {
        if (!Gs1ElementString.TryFromHri(hri, out var elementString, out var error))
            throw new InvalidOperationException($"the text is not valid (AI) text: {error}");
        var content = elementString;
        if (member.IsLink && !Gs1ElementString.TryToDigitalLink(elementString, out content, out error))
            throw new InvalidOperationException($"the data has no URI: {error}");
        var fnc1 = member.IsLink ? Fnc1Mode.None : Fnc1Mode.FirstPosition;
        if (member.IsRmQr)
            return RmQRCodeGenerator.TryGetRequiredBufferSize(content, Rm(level), out var rmqr, new RmQRCodeGeneratorOptions { Fnc1 = fnc1 }) ? rmqr.BufferSize : -1;
        return QRCodeGenerator.TryGetRequiredBufferSize(content, Qr(level), out var qr, new QRCodeGeneratorOptions { Fnc1 = fnc1 }) ? qr.BufferSize : -1;
    }

    [Test]
    [MethodDataSource(nameof(Members))]
    public async Task EveryMember_WritesAtTheLevelAsked(Member member)
    {
        const string hri = "(01)09506000134352(10)ABC123";
        var atM = SizedByTheCore(member, hri, 0);
        var atH = SizedByTheCore(member, hri, 1);
        await Assert.That(atM).IsGreaterThan(0);
        await Assert.That(atH).IsGreaterThan(atM).Because("the premise: this data needs a larger symbol at H than at M, so a member that ignored the level would show");

        await Assert.That(member.Bytes(hri, 0, default)).IsEqualTo(atM);
        await Assert.That(member.Bytes(hri, 1, default)).IsEqualTo(atH);
    }

    /// <summary>
    /// Create reports a wrong argument before wrong content. The entry points read the data before they reach
    /// Create, so each has to keep that order itself: for the FNC1 mode it refuses, and for a level Create refuses.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Members))]
    public async Task EveryMember_ReportsARefusedFnc1ModeOrLevel_BeforeRefusedData(Member member)
    {
        const string refused = "(01)09506000134353";
        if (member.IsSizeForm)
            await Assert.That(member.Bytes(refused, 0, default)).IsEqualTo(-1).Because("the premise: alone, the data is refused as data");
        else
            await Assert.That(() => member.Bytes(refused, 0, default)).Throws<Gs1FormatException>().Because("the premise: alone, the data is refused as data");

        // CreateGs1 refuses second position, and CreateGs1DigitalLink any mode.
        var mode = member.IsLink ? Fnc1Mode.FirstPosition : Fnc1Mode.SecondPosition(1);
        var byMode = await Assert.That(() => member.Bytes(refused, 0, mode)).ThrowsExactly<ArgumentException>();
        await Assert.That(byMode!.ParamName).IsEqualTo("options");

        var byLevel = await Assert.That(() => member.Bytes(refused, 9, default)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(byLevel!.ParamName).IsEqualTo("eccLevel");
    }

    /// <summary>
    /// 29 February of year 00 is a date only where 00 means 2000, so whether a member takes it shows the century it
    /// resolved the year in. The public members take no year, so the expectation is the data layer's verdict under
    /// the same clock.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Members))]
    public async Task EveryMember_ResolvesATwoDigitYearAsTheDataLayerDoes(Member member)
    {
        const string leapDay = "(01)09520123456788(17)000229";
        var taken = Gs1ElementString.TryFromHri(leapDay, out _, out var error);
        if (taken)
            await Assert.That(member.Bytes(leapDay, 0, default)).IsGreaterThan(0);
        else if (member.IsSizeForm)
            await Assert.That(member.Bytes(leapDay, 0, default)).IsEqualTo(-1);
        else
            await Assert.That((await Assert.That(() => member.Bytes(leapDay, 0, default)).Throws<Gs1FormatException>())!.Error.Kind).IsEqualTo(error.Kind);
    }

    // The Digital Link form that takes options and no stem

    /// <summary>
    /// A bare <c>default</c> or a target-typed <c>new()</c> as the third argument is the options, as it is for
    /// Create and CreateGs1. With only the forms that end in an optional stem the destination form would be the
    /// better match for it. The declared types below are what make this a test: the destination form returns an int.
    /// </summary>
    [Test]
    public async Task CreateGs1DigitalLink_ABareDefaultOrTargetTypedThirdArgument_IsTheOptions()
    {
        const string hri = "(01)09506000134352(10)ABC123";

        QRCodeData expected = QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.M);
        QRCodeData byDefault = QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.M, default);
        QRCodeData byName = Gs1QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.M, default);
        QRCodeData byNew = QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.M, new() { QuietZoneSize = 0 });
        await Assert.That(byDefault.Size).IsEqualTo(expected.Size);
        await Assert.That(byName.Size).IsEqualTo(expected.Size);
        await Assert.That(byNew.Size).IsEqualTo(expected.Size - 8).Because("the options reached Create: no quiet zone of four modules a side");
        await Assert.That(QRCodeDecoder.TryDecode(byDefault, out var canonical)).IsTrue();
        await Assert.That(canonical).IsEqualTo("https://id.gs1.org/01/09506000134352/10/ABC123").Because("the options-only form writes under the canonical stem, which a size cannot show");

        RmQRCodeData rmqrExpected = RmQRCodeGenerator.CreateGs1DigitalLink(hri, RmQREccLevel.M);
        RmQRCodeData rmqrByDefault = RmQRCodeGenerator.CreateGs1DigitalLink(hri, RmQREccLevel.M, default);
        RmQRCodeData rmqrByName = Gs1RmQRCodeGenerator.CreateGs1DigitalLink(hri, RmQREccLevel.M, default);
        RmQRCodeData rmqrByNew = RmQRCodeGenerator.CreateGs1DigitalLink(hri, RmQREccLevel.M, new() { QuietZoneSize = 0 });
        await Assert.That((rmqrByDefault.Width, rmqrByDefault.Height)).IsEqualTo((rmqrExpected.Width, rmqrExpected.Height));
        await Assert.That((rmqrByName.Width, rmqrByName.Height)).IsEqualTo((rmqrExpected.Width, rmqrExpected.Height));
        await Assert.That(rmqrByNew.Width).IsLessThan(rmqrExpected.Width).Because("the options reached Create: no quiet zone");
        await Assert.That(RmQRCodeDecoder.TryDecode(rmqrByDefault, out var rmqrCanonical)).IsTrue();
        await Assert.That(rmqrCanonical).IsEqualTo("https://id.gs1.org/01/09506000134352/10/ABC123");

        // The forms beside it keep their callers: the stem by name, and the destination by its type.
        QRCodeData withStem = QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.M, stem: "https://brand.example.com");
        await Assert.That(QRCodeDecoder.TryDecode(withStem, out var uri)).IsTrue();
        await Assert.That(uri).StartsWith("https://brand.example.com/01/");
        int written = QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.M, new byte[40_000]);
        await Assert.That(written).IsEqualTo(expected.Size * expected.Size);
    }

    /// <summary>The options-only form refuses what the form with a stem refuses.</summary>
    [Test]
    public async Task CreateGs1DigitalLink_TheOptionsOnlyForm_RefusesAnFnc1ModeAndBadData()
    {
        const string hri = "(01)09506000134352";
        var first = new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition };
        var refusedMode = await Assert.That(() => QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.M, in first)).ThrowsExactly<ArgumentException>();
        await Assert.That(refusedMode!.ParamName).IsEqualTo("options");
        var rmqrFirst = new RmQRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition };
        var rmqrRefusedMode = await Assert.That(() => RmQRCodeGenerator.CreateGs1DigitalLink(hri, RmQREccLevel.M, in rmqrFirst)).ThrowsExactly<ArgumentException>();
        await Assert.That(rmqrRefusedMode!.ParamName).IsEqualTo("options");

        var noKey = await Assert.That(() => QRCodeGenerator.CreateGs1DigitalLink("(410)1234567890128", QREccLevel.M, default)).Throws<Gs1FormatException>();
        await Assert.That(noKey!.Error.Kind).IsEqualTo(Gs1ErrorKind.MissingPrimaryKey);
        var rmqrNoKey = await Assert.That(() => RmQRCodeGenerator.CreateGs1DigitalLink("(410)1234567890128", RmQREccLevel.M, default)).Throws<Gs1FormatException>();
        await Assert.That(rmqrNoKey!.Error.Kind).IsEqualTo(Gs1ErrorKind.MissingPrimaryKey);
    }

    // A destination that is too small

    /// <summary>
    /// Create's own error for a short destination names Create's sizing member, which sizes the caller's text as
    /// plain text and so answers for another symbol. The destination forms name the member that sizes theirs, with
    /// the size that member gives for the same level and options. The cases differ in the level, the quiet zone
    /// and the version, each of which changes the size of this data, as FNC1 does. The destinations are empty and
    /// one byte short, so the size in the message cannot be read off the destination.
    /// </summary>
    [Test]
    public async Task DestinationForms_ADestinationTooSmall_NamesTheMemberThatSizesIt()
    {
        const string hri = "(01)09506000134352(10)ABC123(17)201225";
        await Assert.That(Gs1ElementString.TryFromHri(hri, out var elementString, out _)).IsTrue();

        var sizes = new List<int>();
        var linkSizes = new List<int>();
        var cases = new[]
        {
            (QREccLevel.M, default(QRCodeGeneratorOptions)),
            (QREccLevel.H, default),
            (QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 }),
            (QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(10) }),
        };
        foreach (var (level, options) in cases)
        {
            await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1(hri, level, out var size, options)).IsTrue();
            sizes.Add(size.BufferSize);
            await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(hri, level, out var linkSize, options)).IsTrue();
            linkSizes.Add(linkSize.BufferSize);
            foreach (var missing in new[] { 1, size.BufferSize })
            {
                var length = size.BufferSize - missing;
                var thrown = await Assert.That(() => QRCodeGenerator.CreateGs1(hri, level, new byte[length], options)).ThrowsExactly<ArgumentException>();
                await Assert.That(thrown!.ParamName).IsEqualTo("destination");
                await Assert.That(thrown.Message).StartsWith($"Destination buffer too small: {size.BufferSize} bytes required, got {length} bytes. Use TryGetRequiredBufferSizeGs1 to calculate the required size.");

                length = linkSize.BufferSize - (missing == 1 ? 1 : linkSize.BufferSize);
                thrown = await Assert.That(() => QRCodeGenerator.CreateGs1DigitalLink(hri, level, new byte[length], options)).ThrowsExactly<ArgumentException>();
                await Assert.That(thrown!.ParamName).IsEqualTo("destination");
                await Assert.That(thrown.Message).StartsWith($"Destination buffer too small: {linkSize.BufferSize} bytes required, got {length} bytes. Use TryGetRequiredBufferSizeGs1DigitalLink to calculate the required size.");
            }

            await Assert.That(QRCodeGenerator.CreateGs1(hri, level, new byte[size.BufferSize], options)).IsEqualTo(size.BufferSize);
            await Assert.That(QRCodeGenerator.CreateGs1DigitalLink(hri, level, new byte[linkSize.BufferSize], options)).IsEqualTo(linkSize.BufferSize);
        }

        await Assert.That(sizes.Distinct().Count()).IsEqualTo(cases.Length).Because("the premise: the level, the quiet zone and the version each change the GS1 symbol's size");
        await Assert.That(linkSizes.Distinct().Count()).IsEqualTo(cases.Length).Because("the premise: and the Digital Link symbol's");
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(elementString, QREccLevel.M, out var withoutFnc1)).IsTrue();
        await Assert.That(withoutFnc1.BufferSize).IsNotEqualTo(sizes[0]).Because("the premise: so does FNC1");

        var rmqrSizes = new List<int>();
        var rmqrLinkSizes = new List<int>();
        var rmqrCases = new[]
        {
            (RmQREccLevel.M, default(RmQRCodeGeneratorOptions)),
            (RmQREccLevel.H, default),
            (RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = 0 }),
            (RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 }),
        };
        foreach (var (level, options) in rmqrCases)
        {
            await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(hri, level, out var size, options)).IsTrue();
            rmqrSizes.Add(size.BufferSize);
            await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(hri, level, out var linkSize, options)).IsTrue();
            rmqrLinkSizes.Add(linkSize.BufferSize);
            foreach (var missing in new[] { 1, size.BufferSize })
            {
                var length = size.BufferSize - missing;
                var thrown = await Assert.That(() => RmQRCodeGenerator.CreateGs1(hri, level, new byte[length], options)).ThrowsExactly<ArgumentException>();
                await Assert.That(thrown!.ParamName).IsEqualTo("destination");
                await Assert.That(thrown.Message).StartsWith($"Destination buffer too small: {size.BufferSize} bytes required, got {length} bytes. Use TryGetRequiredBufferSizeGs1 to calculate the required size.");

                length = linkSize.BufferSize - (missing == 1 ? 1 : linkSize.BufferSize);
                thrown = await Assert.That(() => RmQRCodeGenerator.CreateGs1DigitalLink(hri, level, new byte[length], options)).ThrowsExactly<ArgumentException>();
                await Assert.That(thrown!.ParamName).IsEqualTo("destination");
                await Assert.That(thrown.Message).StartsWith($"Destination buffer too small: {linkSize.BufferSize} bytes required, got {length} bytes. Use TryGetRequiredBufferSizeGs1DigitalLink to calculate the required size.");
            }

            await Assert.That(RmQRCodeGenerator.CreateGs1(hri, level, new byte[size.BufferSize], options)).IsEqualTo(size.BufferSize);
            await Assert.That(RmQRCodeGenerator.CreateGs1DigitalLink(hri, level, new byte[linkSize.BufferSize], options)).IsEqualTo(linkSize.BufferSize);
        }

        await Assert.That(rmqrSizes.Distinct().Count()).IsEqualTo(rmqrCases.Length).Because("the premise: the level, the quiet zone and the version each change the rMQR symbol's size");
        await Assert.That(rmqrLinkSizes.Distinct().Count()).IsEqualTo(rmqrCases.Length).Because("the premise: and the rMQR Digital Link symbol's");
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(elementString, RmQREccLevel.M, out var rmqrWithoutFnc1)).IsTrue();
        await Assert.That(rmqrWithoutFnc1.BufferSize).IsNotEqualTo(rmqrSizes[0]).Because("the premise: so does FNC1");

        // Data that is refused is still reported before the destination, which no member can size without it.
        await Assert.That(() => QRCodeGenerator.CreateGs1("(01)09506000134353", QREccLevel.M, new byte[1])).Throws<Gs1FormatException>();
        await Assert.That(() => RmQRCodeGenerator.CreateGs1DigitalLink("(01)09506000134353", RmQREccLevel.M, new byte[1])).Throws<Gs1FormatException>();
    }

    /// <summary>
    /// Content that fits no symbol under the options is the fit's error from a destination form too, and not the
    /// short destination's. Both are an ArgumentException, so the message and the parameter name are what tell
    /// them apart, and the data form's exception for the same call is the expectation.
    /// </summary>
    [Test]
    public async Task DestinationForms_ContentThatDoesNotFit_IsReportedAsTheDataFormReportsIt()
    {
        const string hri = "(01)09506000134352(10)ABC123(17)201225";
        var destination = new byte[40_000];

        var versionOne = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1) };
        var fit = await Assert.That(() => QRCodeGenerator.CreateGs1(hri, QREccLevel.H, versionOne)).ThrowsExactly<ArgumentException>();
        var fromDestinationForm = await Assert.That(() => QRCodeGenerator.CreateGs1(hri, QREccLevel.H, destination, versionOne)).ThrowsExactly<ArgumentException>();
        await Assert.That((fromDestinationForm!.ParamName, fromDestinationForm.Message)).IsEqualTo((fit!.ParamName, fit.Message));
        await Assert.That(fromDestinationForm.ParamName).IsNotEqualTo("destination");
        fit = await Assert.That(() => QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.H, versionOne, null)).ThrowsExactly<ArgumentException>();
        fromDestinationForm = await Assert.That(() => QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.H, destination, versionOne)).ThrowsExactly<ArgumentException>();
        await Assert.That((fromDestinationForm!.ParamName, fromDestinationForm.Message)).IsEqualTo((fit!.ParamName, fit.Message));
        await Assert.That(fromDestinationForm.ParamName).IsNotEqualTo("destination");

        var smallest = new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 };
        fit = await Assert.That(() => RmQRCodeGenerator.CreateGs1(hri, RmQREccLevel.H, smallest)).ThrowsExactly<ArgumentException>();
        fromDestinationForm = await Assert.That(() => RmQRCodeGenerator.CreateGs1(hri, RmQREccLevel.H, destination, smallest)).ThrowsExactly<ArgumentException>();
        await Assert.That((fromDestinationForm!.ParamName, fromDestinationForm.Message)).IsEqualTo((fit!.ParamName, fit.Message));
        await Assert.That(fromDestinationForm.ParamName).IsNotEqualTo("destination");
        fit = await Assert.That(() => RmQRCodeGenerator.CreateGs1DigitalLink(hri, RmQREccLevel.H, smallest, null)).ThrowsExactly<ArgumentException>();
        fromDestinationForm = await Assert.That(() => RmQRCodeGenerator.CreateGs1DigitalLink(hri, RmQREccLevel.H, destination, smallest)).ThrowsExactly<ArgumentException>();
        await Assert.That((fromDestinationForm!.ParamName, fromDestinationForm.Message)).IsEqualTo((fit!.ParamName, fit.Message));
        await Assert.That(fromDestinationForm.ParamName).IsNotEqualTo("destination");
    }

    // The stem's share of the URI buffer

    /// <summary>
    /// The URI buffer is sized from the text and the stem. Here the stem alone takes the URI past the stack buffer
    /// while three times the text stays far inside it, so a size that left the stem out would hold too little.
    /// </summary>
    [Test]
    public async Task CreateGs1DigitalLink_AStemThatTakesTheUriPastTheStackBuffer_WritesTheSameSymbol()
    {
        const string hri = "(01)09506000134352";
        var stem = "https://brand.example.com/" + new string('a', 469);
        await Assert.That(Gs1ElementString.TryToDigitalLink("0109506000134352", out var uri, out var error, stem)).IsTrue().Because(error.ToString());
        await Assert.That(uri.Length).IsGreaterThan(Gs1SymbolData.StackLimit).Because("the premise: the URI does not fit the stack buffer");
        await Assert.That(3 * hri.Length + 2).IsLessThan(Gs1SymbolData.StackLimit).Because("the premise: without the stem the size would stay in the stack buffer");

        var composed = QRCodeGenerator.Create(uri, QREccLevel.L);
        var data = QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.L, stem: stem);
        await Assert.That(QRCodeDecoder.TryDecode(data, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(uri);
        await Assert.That(data.Version).IsEqualTo(composed.Version);
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(hri, QREccLevel.L, out var size, stem: stem)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(composed.Version);
        await Assert.That(QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.L, new byte[size.BufferSize], stem: stem)).IsEqualTo(size.BufferSize);

        // No rMQR symbol holds a URI of this length, which is content that does not fit and not a fault of the data.
        await Assert.That(() => RmQRCodeGenerator.CreateGs1DigitalLink(hri, RmQREccLevel.M, stem: stem)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => RmQRCodeGenerator.CreateGs1DigitalLink(hri, RmQREccLevel.M, new byte[40_000], stem: stem)).ThrowsExactly<ArgumentException>();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(hri, RmQREccLevel.M, out _, stem: stem)).IsFalse();
    }

    // Text too long for any symbol

    /// <summary>The two capacities the size forms compare a text's length with are the core's: one digit more fits no symbol.</summary>
    [Test]
    public async Task Capacities_AreTheMostDigitsTheCoreSizes()
    {
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(new string('1', Gs1SymbolData.StandardQrCapacity), QREccLevel.L, out _)).IsTrue();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(new string('1', Gs1SymbolData.StandardQrCapacity + 1), QREccLevel.L, out _)).IsFalse();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(new string('1', Gs1SymbolData.RmQrCapacity), RmQREccLevel.M, out _)).IsTrue();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(new string('1', Gs1SymbolData.RmQrCapacity + 1), RmQREccLevel.M, out _)).IsFalse();
    }

    /// <summary>
    /// A size form answers for text longer than any symbol holds from its length. The checks cost the square of the
    /// number of elements, which for the data here is seconds, so the time is what shows that they did not run. A
    /// wrong option is still reported first, and the longest data that fits is still sized.
    /// </summary>
    [Test]
    public async Task TryGetRequiredBufferSizeGs1_TextTooLongForAnySymbol_IsRefusedFromItsLength()
    {
        // (01) and then (91)A 19,996 times: valid data, since an AI may repeat with one value.
        var tooLong = "01" + Gtin + string.Concat(Enumerable.Repeat("91A\u001D", 19_996));
        var tooLongAsAiText = "(01)" + Gtin + string.Concat(Enumerable.Repeat("(91)A", 19_996));
        await Assert.That(tooLong.Length).IsGreaterThan(Gs1SymbolData.StandardQrCapacity);
        await Assert.That(tooLongAsAiText.Length).IsGreaterThan(2 * Gs1SymbolData.StandardQrCapacity);

        var clock = Stopwatch.StartNew();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1(tooLong, QREccLevel.L, out _)).IsFalse();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1(tooLongAsAiText, QREccLevel.L, out _)).IsFalse();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(tooLong, RmQREccLevel.M, out _)).IsFalse();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(tooLongAsAiText, RmQREccLevel.M, out _)).IsFalse();
        await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(2)).Because("four runs of the checks on this data take many seconds");

        var second = new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.SecondPosition(1) };
        await Assert.That(() => QRCodeGenerator.TryGetRequiredBufferSizeGs1(tooLong, QREccLevel.L, out _, second)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => QRCodeGenerator.TryGetRequiredBufferSizeGs1(tooLong, (QREccLevel)9, out _)).ThrowsExactly<ArgumentOutOfRangeException>();
        var rmqrSecond = new RmQRCodeGeneratorOptions { Fnc1 = Fnc1Mode.SecondPosition(1) };
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(tooLong, RmQREccLevel.M, out _, rmqrSecond)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(tooLong, (RmQREccLevel)9, out _)).ThrowsExactly<ArgumentOutOfRangeException>();

        // Second position is refused before the length is looked at. An option Create refuses is one only the
        // check beside the length's answer can report.
        var badQuietZone = new QRCodeGeneratorOptions { QuietZoneSize = -1 };
        await Assert.That(() => QRCodeGenerator.TryGetRequiredBufferSizeGs1(tooLong, QREccLevel.L, out _, badQuietZone)).ThrowsExactly<ArgumentOutOfRangeException>();
        var rmqrBadQuietZone = new RmQRCodeGeneratorOptions { QuietZoneSize = -1 };
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(tooLong, RmQREccLevel.M, out _, rmqrBadQuietZone)).ThrowsExactly<ArgumentOutOfRangeException>();

        // The longest of this shape that fits: 7,084 digits in version 40 at L, and 360 in R17x139 at M. Their
        // (AI) text is half as long again, which a bound on the text's length alone would cut off, and as an
        // element string each is within a few digits of the capacity, which a bound below it would cut off.
        var longestQr = "(01)" + Gtin + string.Concat(Enumerable.Repeat("(20)12", 1767));
        var longestQrElementString = "01" + Gtin + string.Concat(Enumerable.Repeat("2012", 1767));
        await Assert.That(longestQr.Length).IsGreaterThan(Gs1SymbolData.StandardQrCapacity);
        await Assert.That(longestQrElementString.Length).IsEqualTo(7084);
        foreach (var text in new[] { longestQr, longestQrElementString })
        {
            await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1(text, QREccLevel.L, out var qrSize)).IsTrue();
            await Assert.That(qrSize.Version).IsEqualTo(40);
        }

        var longestRmQr = "(01)" + Gtin + string.Concat(Enumerable.Repeat("(20)12", 86));
        var longestRmQrElementString = "01" + Gtin + string.Concat(Enumerable.Repeat("2012", 86));
        await Assert.That(longestRmQr.Length).IsGreaterThan(Gs1SymbolData.RmQrCapacity);
        await Assert.That(longestRmQrElementString.Length).IsEqualTo(360);
        foreach (var text in new[] { longestRmQr, longestRmQrElementString })
        {
            await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(text, RmQREccLevel.M, out var rmqrSize)).IsTrue();
            await Assert.That(rmqrSize.Version).IsEqualTo(RmQRVersion.R17x139);
        }
    }

    /// <summary>
    /// Only the two size forms of CreateGs1 answer from the length. A Create form owes the caller the data's error
    /// before the fit's, and a Digital Link URI can be far shorter than its text, since an AI repeated with one
    /// value is written once. So for text longer than any symbol holds, every other member still reads the data:
    /// the Digital Link members write or size the short URI, and every Create form reports refused data as data.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Members))]
    public async Task EveryMember_ReadsTextLongerThanAnySymbolHolds_UnlessItsAnswerCannotDependOnTheData(Member member)
    {
        var repeats = string.Concat(Enumerable.Repeat("(20)12", 2400));
        var valid = "(01)" + Gtin + repeats;
        var refused = "(01)09506000134353" + repeats;
        await Assert.That(valid.Length).IsGreaterThan(2 * Gs1SymbolData.StandardQrCapacity).Because("the premise: the size forms of CreateGs1 answer for this text from its length, in both symbologies");

        if (member.IsLink)
        {
            // The URI is https://id.gs1.org/01/09506000134352?20=12, which every symbol here holds.
            var expected = SizedByTheCore(member, valid, 0);
            await Assert.That(expected).IsGreaterThan(0);
            await Assert.That(member.Bytes(valid, 0, default)).IsEqualTo(expected);
        }
        else if (member.IsSizeForm)
        {
            await Assert.That(member.Bytes(valid, 0, default)).IsEqualTo(-1);
        }
        else
        {
            var doesNotFit = await Assert.That(() => member.Bytes(valid, 0, default)).Throws<Exception>();
            await Assert.That(doesNotFit is Gs1FormatException).IsFalse().Because("valid data that fits no symbol is the fit's error");
        }

        if (member.IsSizeForm)
        {
            await Assert.That(member.Bytes(refused, 0, default)).IsEqualTo(-1);
        }
        else
        {
            var thrown = await Assert.That(() => member.Bytes(refused, 0, default)).Throws<Gs1FormatException>();
            await Assert.That(thrown!.Error.Kind).IsEqualTo(Gs1ErrorKind.InvalidCheckDigit);
        }
    }
}
