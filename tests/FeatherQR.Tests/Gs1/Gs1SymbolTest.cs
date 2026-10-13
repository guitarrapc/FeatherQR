using FeatherQR.Gs1;
using FeatherQR.Gs1.Internals;
using SkiaSharp;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The GS1 entry points on the generators, end to end: <c>CreateGs1</c> writes the checked element string under FNC1
/// in first position and <c>CreateGs1DigitalLink</c> the data's URI as plain text, each from (AI) text or from an
/// element string, in Standard QR and rMQR, in the data form, the span form and the size form. A decoder and
/// <see cref="Gs1ElementString.TryRead"/> give the (AI) text back from either carrier. Data that is not valid GS1
/// data is a <see cref="Gs1FormatException"/> with the data layer's error, and a wrong option is reported before it.
/// </summary>
public class Gs1SymbolTest
{
    private const int Year = 2026;
    private const string Gs = "\u001D";
    private const string Gtin = "09506000134352";

    // CreateGs1, Standard QR

    [Test]
    [Arguments("(01)09506000134352(17)201225(10)ABC123", "0109506000134352" + "17201225" + "10ABC123")]
    [Arguments("(01)09506000134352(10)ABC123(17)201225", "0109506000134352" + "10ABC123" + Gs + "17201225")]
    [Arguments("(00)095060001343528938", "00095060001343528938")]
    public async Task StandardQr_CreateGs1_FromAiText_WritesItsElementStringUnderFnc1(string hri, string elementString)
    {
        var data = QRCodeGenerator.CreateGs1(hri, QREccLevel.M);

        await Assert.That(QRCodeDecoder.TryDecode(data, out var text, out var info)).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(elementString);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition);
        await Assert.That(Gs1ElementString.TryRead(text, info.Fnc1, out var read, out var error)).IsTrue().Because(error.ToString());
        await Assert.That(read).IsEqualTo(hri);

        // The entry point is the element string through Create with the FNC1 option, and nothing more.
        var composed = QRCodeGenerator.Create(elementString, QREccLevel.M, new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition });
        await Assert.That(Modules(data)).IsEquivalentTo(Modules(composed), CollectionOrdering.Matching);
    }

    [Test]
    public async Task StandardQr_CreateGs1_FromAnElementString_WritesTheSymbolOfItsAiText()
    {
        var elementString = "01" + Gtin + "10ABC123" + Gs + "17201225";
        var fromElementString = QRCodeGenerator.CreateGs1(elementString, QREccLevel.Q);
        var fromText = QRCodeGenerator.CreateGs1("(01)" + Gtin + "(10)ABC123(17)201225", QREccLevel.Q);

        await Assert.That(Modules(fromElementString)).IsEquivalentTo(Modules(fromText), CollectionOrdering.Matching);
        await Assert.That(QRCodeDecoder.TryDecode(fromElementString, out var text, out var info)).IsTrue();
        await Assert.That(text).IsEqualTo(elementString);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition);
    }

    /// <summary>A consumer below C# 14 calls the enclosing class by name. Both spellings are one method.</summary>
    [Test]
    public async Task StandardQr_TheNamedClassAndTheExtension_AreOneMethod()
    {
        var hri = "(01)" + Gtin + "(10)ABC123";
        var options = new QRCodeGeneratorOptions { QuietZoneSize = 0 };
        var byExtension = QRCodeGenerator.CreateGs1(hri, QREccLevel.M, options);
        var byName = Gs1QRCodeGenerator.CreateGs1(hri, QREccLevel.M, options);
        await Assert.That(Modules(byName)).IsEquivalentTo(Modules(byExtension), CollectionOrdering.Matching);

        var linkByExtension = QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.M, options);
        var linkByName = Gs1QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.M, options);
        await Assert.That(Modules(linkByName)).IsEquivalentTo(Modules(linkByExtension), CollectionOrdering.Matching);

        await Assert.That(Gs1QRCodeGenerator.TryGetRequiredBufferSizeGs1(hri, QREccLevel.M, out var size, options)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(byName.Version);
        await Assert.That(Gs1QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(hri, QREccLevel.M, out var linkSize, options)).IsTrue();
        await Assert.That(linkSize.Version).IsEqualTo(linkByName.Version);
    }

    [Test]
    [Arguments("(01)09506000134352(17)201225(10)ABC123")]
    [Arguments("0109506000134352" + "10ABC123" + Gs + "17201225")]
    public async Task StandardQr_CreateGs1_TheSpanFormAndTheSizing_AgreeWithTheDataForm(string text)
    {
        foreach (var options in new[] { default(QRCodeGeneratorOptions), new QRCodeGeneratorOptions { QuietZoneSize = 0, Segmentation = QRSegmentation.Optimal } })
        {
            var data = QRCodeGenerator.CreateGs1(text, QREccLevel.M, options);
            await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1(text, QREccLevel.M, out var size, options)).IsTrue();
            await Assert.That((size.Version, size.Size)).IsEqualTo((data.Version, data.Size));

            var destination = new byte[size.BufferSize];
            var written = QRCodeGenerator.CreateGs1(text, QREccLevel.M, destination, options);
            await Assert.That(written).IsEqualTo(size.BufferSize);
            await Assert.That(destination).IsEquivalentTo(Modules(data), CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task StandardQr_CreateGs1_PassesTheOtherOptionsThrough()
    {
        var hri = "(01)" + Gtin + "(10)ABC123";
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(6), MaskPattern = 5, Segmentation = QRSegmentation.Optimal, QuietZoneSize = 0, Fnc1 = Fnc1Mode.FirstPosition };
        var data = QRCodeGenerator.CreateGs1(hri, QREccLevel.H, options);

        await Assert.That(QRCodeDecoder.TryDecode(data, out var text, out var info)).IsTrue();
        await Assert.That((info.Version, info.EccLevel, info.MaskPattern)).IsEqualTo((6, QREccLevel.H, 5));
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition).Because("first position named in the options is what the entry point sets anyway");
        await Assert.That(text).IsEqualTo("01" + Gtin + "10ABC123");
        await Assert.That(data.Size).IsEqualTo(41).Because("a quiet zone of 0 leaves the 41 modules of version 6");
    }

    [Test]
    public async Task StandardQr_CreateGs1_AnFnc1ModeOtherThanFirstPosition_IsRefused()
    {
        var hri = "(01)" + Gtin;
        var options = new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.SecondPosition(37) };
        var destination = new byte[10_000];

        var error = await Assert.That(() => QRCodeGenerator.CreateGs1(hri, QREccLevel.M, options)).Throws<ArgumentException>();
        await Assert.That(error!.ParamName).IsEqualTo("options");
        await Assert.That(() => QRCodeGenerator.CreateGs1(hri, QREccLevel.M, destination, options)).Throws<ArgumentException>();
        await Assert.That(() => QRCodeGenerator.TryGetRequiredBufferSizeGs1(hri, QREccLevel.M, out _, options)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("(01)09506000134353", Gs1ErrorKind.InvalidCheckDigit)]
    [Arguments("0109506000134353", Gs1ErrorKind.InvalidCheckDigit)]
    [Arguments("(01)09506000134352(10)", Gs1ErrorKind.EmptyValue)]
    [Arguments("(01)09506000134352(10)ABC(10)DEF", Gs1ErrorKind.RepeatedAi)]
    [Arguments("(17)201225", Gs1ErrorKind.MissingRequiredAi)]
    [Arguments("(01)09506000134352(17)201325", Gs1ErrorKind.InvalidDate)]
    [Arguments("(01)0950600013435", Gs1ErrorKind.ValueTooShort)]
    [Arguments("(" + Gs1UnassignedAi.Ai7050 + ")ABC", Gs1ErrorKind.UnknownAi)]
    [Arguments("(01)09506000134352(10", Gs1ErrorKind.InvalidText)]
    [Arguments("", Gs1ErrorKind.InvalidText)]
    [Arguments("(", Gs1ErrorKind.InvalidText)]
    [Arguments(Gs + "0109506000134352", Gs1ErrorKind.UnexpectedSeparator)]
    public async Task StandardQr_CreateGs1_DataThatIsNotGs1Data_ThrowsWithTheDataLayersError(string text, Gs1ErrorKind kind)
    {
        // The data layer's own answer for the same text is the error the exception must carry.
        var expected = default(Gs1Error);
        var valid = text.StartsWith('(')
            ? Gs1ElementString.TryFromHri(text, out _, out expected)
            : Gs1ElementString.TryValidate(text, out expected);
        await Assert.That(valid).IsFalse();
        await Assert.That(expected.Kind).IsEqualTo(kind);

        var thrown = await Assert.That(() => QRCodeGenerator.CreateGs1(text, QREccLevel.M)).Throws<Gs1FormatException>();
        await Assert.That(thrown!.Error).IsEqualTo(expected);
        await Assert.That(thrown.Message).IsEqualTo(expected.ToString());
        await Assert.That(thrown).IsAssignableTo<FormatException>();

        var destination = new byte[10_000];
        var fromSpanForm = await Assert.That(() => QRCodeGenerator.CreateGs1(text, QREccLevel.M, destination)).Throws<Gs1FormatException>();
        await Assert.That(fromSpanForm!.Error).IsEqualTo(expected);
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1(text, QREccLevel.M, out var size)).IsFalse();
        await Assert.That(size).IsEqualTo(default(QRCodeCalculatedSize));
    }

    /// <summary>
    /// Create reports a wrong option before content that does not fit, and the entry points keep that order for data
    /// that is not GS1 data: the option's exception, whatever the data.
    /// </summary>
    [Test]
    [Arguments("(01)09506000134352")]
    [Arguments("(01)09506000134353")]
    public async Task StandardQr_AWrongOption_IsReportedBeforeTheData(string text)
    {
        var destination = new byte[10_000];
        var quietZone = new QRCodeGeneratorOptions { QuietZoneSize = -1 };
        await Assert.That(() => QRCodeGenerator.CreateGs1(text, QREccLevel.M, quietZone)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRCodeGenerator.CreateGs1(text, QREccLevel.M, destination, quietZone)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRCodeGenerator.TryGetRequiredBufferSizeGs1(text, QREccLevel.M, out _, quietZone)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRCodeGenerator.CreateGs1DigitalLink(text, QREccLevel.M, quietZone)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text, QREccLevel.M, out _, quietZone)).Throws<ArgumentOutOfRangeException>();

        await Assert.That(() => QRCodeGenerator.CreateGs1(text, (QREccLevel)9)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRCodeGenerator.CreateGs1(text, QREccLevel.M, new QRCodeGeneratorOptions { Segmentation = (QRSegmentation)7 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRCodeGenerator.CreateGs1DigitalLink(text, QREccLevel.M, stem: "ftp://example.com")).Throws<ArgumentException>();
    }

    [Test]
    public async Task StandardQr_DataThatDoesNotFit_ThrowsAsCreateDoes_AndSizesAsFalse()
    {
        var hri = "(01)" + Gtin + "(10)ABC123(17)201225";
        var narrow = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1) };
        await Assert.That(() => QRCodeGenerator.CreateGs1(hri, QREccLevel.H, narrow)).Throws<ArgumentException>();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1(hri, QREccLevel.H, out _, narrow)).IsFalse();
        await Assert.That(() => QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.H, narrow)).Throws<ArgumentException>();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(hri, QREccLevel.H, out _, narrow)).IsFalse();

        // An AI may repeat with one value, so valid data can outgrow version 40.
        var long91 = "(91)" + new string('A', 90);
        var tooLong = string.Concat(Enumerable.Repeat(long91, 60));
        await Assert.That(Gs1ElementString.TryFromHri(tooLong, out _, out _)).IsTrue().Because("the premise: the data is valid, only too long");
        await Assert.That(() => QRCodeGenerator.CreateGs1(tooLong, QREccLevel.L)).Throws<InvalidOperationException>();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1(tooLong, QREccLevel.L, out _)).IsFalse();
    }

    // CreateGs1DigitalLink, Standard QR

    [Test]
    [Arguments("(01)09506000134352(10)ABC123", null, "https://id.gs1.org/01/09506000134352/10/ABC123", "(01)09506000134352(10)ABC123")]
    [Arguments("(01)09506000134352(17)201225(10)ABC123", null, "https://id.gs1.org/01/09506000134352/10/ABC123?17=201225", "(01)09506000134352(10)ABC123(17)201225")]
    [Arguments("0109506000134352" + "10ABC123" + Gs + "17201225", "https://brand.example.com", "https://brand.example.com/01/09506000134352/10/ABC123?17=201225", "(01)09506000134352(10)ABC123(17)201225")]
    [Arguments("(01)09506000134352(21)A/B+C", "http://example.org/p", "http://example.org/p/01/09506000134352/21/A%2FB%2BC", "(01)09506000134352(21)A/B+C")]
    public async Task StandardQr_CreateGs1DigitalLink_WritesTheUriAsPlainText(string text, string? stem, string uri, string readBack)
    {
        var data = QRCodeGenerator.CreateGs1DigitalLink(text, QREccLevel.M, stem: stem);

        await Assert.That(QRCodeDecoder.TryDecode(data, out var decoded, out var info)).IsTrue().Because(info.Status.ToString());
        await Assert.That(decoded).IsEqualTo(uri);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.None).Because("a Digital Link URI is plain text, and FNC1 would make a scanner read it as an element string");
        await Assert.That(Gs1ElementString.TryRead(decoded, info.Fnc1, out var hri, out var error)).IsTrue().Because(error.ToString());
        await Assert.That(hri).IsEqualTo(readBack);

        // The entry point is the URI through Create, and nothing more.
        var composed = QRCodeGenerator.Create(uri, QREccLevel.M);
        await Assert.That(Modules(data)).IsEquivalentTo(Modules(composed), CollectionOrdering.Matching);
    }

    /// <summary>
    /// Valid GS1 data a URI cannot carry is refused with the place in the text the caller gave: the whole text where
    /// no AI is a primary key, the pair of the AI that is no data attribute in (AI) text, and that AI's digits in an
    /// element string.
    /// </summary>
    [Test]
    [Arguments("(410)1234567890128", Gs1ErrorKind.MissingPrimaryKey, "(410)1234567890128")]
    [Arguments("4101234567890128", Gs1ErrorKind.MissingPrimaryKey, "4101234567890128")]
    [Arguments("(01)09520123456788(8200)http://example.com/", Gs1ErrorKind.AiNotAllowedInUri, "(8200)http://example.com/")]
    [Arguments("0109520123456788" + "8200http://example.com/", Gs1ErrorKind.AiNotAllowedInUri, "8200")]
    [Arguments("(01)09520123456788(10)A\\(B(8200)http://example.com/", Gs1ErrorKind.AiNotAllowedInUri, "(8200)http://example.com/")]
    public async Task StandardQr_CreateGs1DigitalLink_DataAUriCannotCarry_ThrowsWithThePlaceInTheCallersText(string text, Gs1ErrorKind kind, string place)
    {
        await Assert.That(() => QRCodeGenerator.CreateGs1(text, QREccLevel.M)).ThrowsNothing().Because("the premise: the data is valid GS1 data");

        var thrown = await Assert.That(() => QRCodeGenerator.CreateGs1DigitalLink(text, QREccLevel.M)).Throws<Gs1FormatException>();
        await Assert.That(thrown!.Error.Kind).IsEqualTo(kind);
        await Assert.That(text.Substring(thrown.Error.Offset, thrown.Error.Length)).IsEqualTo(place);
        await Assert.That(thrown.Message).IsEqualTo(thrown.Error.ToString());

        var destination = new byte[10_000];
        var fromSpanForm = await Assert.That(() => QRCodeGenerator.CreateGs1DigitalLink(text, QREccLevel.M, destination)).Throws<Gs1FormatException>();
        await Assert.That(fromSpanForm!.Error).IsEqualTo(thrown.Error);
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text, QREccLevel.M, out _)).IsFalse();
    }

    [Test]
    [Arguments("(01)09506000134353", Gs1ErrorKind.InvalidCheckDigit)]
    [Arguments("0109506000134353", Gs1ErrorKind.InvalidCheckDigit)]
    [Arguments("", Gs1ErrorKind.InvalidText)]
    public async Task StandardQr_CreateGs1DigitalLink_DataThatIsNotGs1Data_ThrowsWithTheDataLayersError(string text, Gs1ErrorKind kind)
    {
        var expected = default(Gs1Error);
        var valid = text.StartsWith('(')
            ? Gs1ElementString.TryFromHri(text, out _, out expected)
            : Gs1ElementString.TryValidate(text, out expected);
        await Assert.That(valid).IsFalse();
        await Assert.That(expected.Kind).IsEqualTo(kind);

        var thrown = await Assert.That(() => QRCodeGenerator.CreateGs1DigitalLink(text, QREccLevel.M)).Throws<Gs1FormatException>();
        await Assert.That(thrown!.Error).IsEqualTo(expected);
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text, QREccLevel.M, out _)).IsFalse();
    }

    [Test]
    public async Task StandardQr_CreateGs1DigitalLink_AnFnc1ModeOrABadStem_IsAnArgumentError()
    {
        var hri = "(01)" + Gtin;
        var destination = new byte[10_000];
        foreach (var mode in new[] { Fnc1Mode.FirstPosition, Fnc1Mode.SecondPosition(1) })
        {
            var options = new QRCodeGeneratorOptions { Fnc1 = mode };
            var error = await Assert.That(() => QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.M, options)).Throws<ArgumentException>();
            await Assert.That(error!.ParamName).IsEqualTo("options");
            await Assert.That(() => QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.M, destination, options)).Throws<ArgumentException>();
            await Assert.That(() => QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(hri, QREccLevel.M, out _, options)).Throws<ArgumentException>();
        }

        foreach (var stem in new[] { "ftp://example.com", "example.com", "https://", "https://example.com?x=1" })
        {
            await Assert.That(() => QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.M, stem: stem)).Throws<ArgumentException>().Because(stem);
            await Assert.That(() => QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.M, destination, stem: stem)).Throws<ArgumentException>().Because(stem);
            await Assert.That(() => QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(hri, QREccLevel.M, out _, stem: stem)).Throws<ArgumentException>().Because(stem);
        }
    }

    [Test]
    [Arguments("(01)09506000134352(17)201225(10)ABC123")]
    [Arguments("0109506000134352" + "10ABC123" + Gs + "17201225")]
    public async Task StandardQr_CreateGs1DigitalLink_TheSpanFormAndTheSizing_AgreeWithTheDataForm(string text)
    {
        foreach (var stem in new[] { null, "https://brand.example.com/products" })
        {
            var options = new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal };
            var data = QRCodeGenerator.CreateGs1DigitalLink(text, QREccLevel.M, options, stem);
            await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text, QREccLevel.M, out var size, options, stem)).IsTrue();
            await Assert.That((size.Version, size.Size)).IsEqualTo((data.Version, data.Size));

            var destination = new byte[size.BufferSize];
            var written = QRCodeGenerator.CreateGs1DigitalLink(text, QREccLevel.M, destination, options, stem);
            await Assert.That(written).IsEqualTo(size.BufferSize);
            await Assert.That(destination).IsEquivalentTo(Modules(data), CollectionOrdering.Matching);
        }
    }

    // rMQR: the same members with its types

    [Test]
    [Arguments("(01)09506000134352(17)201225(10)ABC123", "0109506000134352" + "17201225" + "10ABC123")]
    [Arguments("0109506000134352" + "10ABC123" + Gs + "17201225", "0109506000134352" + "10ABC123" + Gs + "17201225")]
    public async Task RmQr_CreateGs1_WritesTheElementStringUnderFnc1_InEveryForm(string text, string elementString)
    {
        var options = new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal };
        var data = RmQRCodeGenerator.CreateGs1(text, RmQREccLevel.M, options);

        await Assert.That(RmQRCodeDecoder.TryDecode(data, out var decoded, out var info)).IsTrue().Because(info.Status.ToString());
        await Assert.That(decoded).IsEqualTo(elementString);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition);
        await Assert.That(Gs1ElementString.TryRead(decoded, info.Fnc1, out var hri, out _)).IsTrue();
        await Assert.That(Gs1ElementString.TryFromHri(hri, out var rebuilt, out _)).IsTrue();
        await Assert.That(rebuilt).IsEqualTo(elementString);

        var composed = RmQRCodeGenerator.Create(elementString, RmQREccLevel.M, options with { Fnc1 = Fnc1Mode.FirstPosition });
        await Assert.That(Modules(data)).IsEquivalentTo(Modules(composed), CollectionOrdering.Matching);
        await Assert.That(Modules(Gs1RmQRCodeGenerator.CreateGs1(text, RmQREccLevel.M, options))).IsEquivalentTo(Modules(data), CollectionOrdering.Matching);

        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(text, RmQREccLevel.M, out var size, options)).IsTrue();
        await Assert.That((size.Version, size.Width, size.Height)).IsEqualTo((data.Version, data.Width, data.Height));
        var destination = new byte[size.BufferSize];
        await Assert.That(RmQRCodeGenerator.CreateGs1(text, RmQREccLevel.M, destination, options)).IsEqualTo(size.BufferSize);
        await Assert.That(destination).IsEquivalentTo(Modules(data), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("(01)09506000134352(10)ABC123", null, "https://id.gs1.org/01/09506000134352/10/ABC123")]
    [Arguments("0109506000134352" + "10ABC123", "https://brand.example.com/products/catalogue", "https://brand.example.com/products/catalogue/01/09506000134352/10/ABC123")]
    public async Task RmQr_CreateGs1DigitalLink_WritesTheUriAsPlainText_InEveryForm(string text, string? stem, string uri)
    {
        var data = RmQRCodeGenerator.CreateGs1DigitalLink(text, RmQREccLevel.M, stem: stem);

        await Assert.That(RmQRCodeDecoder.TryDecode(data, out var decoded, out var info)).IsTrue().Because(info.Status.ToString());
        await Assert.That(decoded).IsEqualTo(uri);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.None);
        await Assert.That(Gs1ElementString.TryRead(decoded, info.Fnc1, out var hri, out _)).IsTrue();
        await Assert.That(hri).IsEqualTo("(01)09506000134352(10)ABC123");
        await Assert.That(Modules(data)).IsEquivalentTo(Modules(RmQRCodeGenerator.Create(uri, RmQREccLevel.M)), CollectionOrdering.Matching);
        await Assert.That(Modules(Gs1RmQRCodeGenerator.CreateGs1DigitalLink(text, RmQREccLevel.M, stem: stem))).IsEquivalentTo(Modules(data), CollectionOrdering.Matching);

        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text, RmQREccLevel.M, out var size, stem: stem)).IsTrue();
        await Assert.That((size.Version, size.Width, size.Height)).IsEqualTo((data.Version, data.Width, data.Height));
        var destination = new byte[size.BufferSize];
        await Assert.That(RmQRCodeGenerator.CreateGs1DigitalLink(text, RmQREccLevel.M, destination, stem: stem)).IsEqualTo(size.BufferSize);
        await Assert.That(destination).IsEquivalentTo(Modules(data), CollectionOrdering.Matching);

        if (stem is not null)
        {
            // The premise: this stem changes the symbol, so a form that ignored it would answer for another one.
            await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text, RmQREccLevel.M, out var canonical)).IsTrue();
            await Assert.That(canonical.Version).IsNotEqualTo(size.Version);
        }
    }

    [Test]
    public async Task RmQr_BadDataBadOptionsAndNoFit_AreReportedAsInStandardQr()
    {
        var hri = "(01)" + Gtin + "(10)ABC123";
        var destination = new byte[4_000];

        // Data
        var thrown = await Assert.That(() => RmQRCodeGenerator.CreateGs1("(01)09506000134353", RmQREccLevel.M)).Throws<Gs1FormatException>();
        await Assert.That(thrown!.Error.Kind).IsEqualTo(Gs1ErrorKind.InvalidCheckDigit);
        await Assert.That(() => RmQRCodeGenerator.CreateGs1("(01)09506000134353", RmQREccLevel.M, destination)).Throws<Gs1FormatException>();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1("(01)09506000134353", RmQREccLevel.M, out _)).IsFalse();
        var noKey = await Assert.That(() => RmQRCodeGenerator.CreateGs1DigitalLink("(410)1234567890128", RmQREccLevel.M)).Throws<Gs1FormatException>();
        await Assert.That(noKey!.Error.Kind).IsEqualTo(Gs1ErrorKind.MissingPrimaryKey);
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink("(410)1234567890128", RmQREccLevel.M, out _)).IsFalse();

        // The FNC1 option
        var second = new RmQRCodeGeneratorOptions { Fnc1 = Fnc1Mode.SecondPosition('A') };
        var refused = await Assert.That(() => RmQRCodeGenerator.CreateGs1(hri, RmQREccLevel.M, second)).Throws<ArgumentException>();
        await Assert.That(refused!.ParamName).IsEqualTo("options");
        await Assert.That(() => RmQRCodeGenerator.CreateGs1(hri, RmQREccLevel.M, destination, second)).Throws<ArgumentException>();
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(hri, RmQREccLevel.M, out _, second)).Throws<ArgumentException>();
        var first = new RmQRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition };
        await Assert.That(() => RmQRCodeGenerator.CreateGs1(hri, RmQREccLevel.M, first)).ThrowsNothing();
        foreach (var mode in new[] { first, second })
        {
            refused = await Assert.That(() => RmQRCodeGenerator.CreateGs1DigitalLink(hri, RmQREccLevel.M, mode)).Throws<ArgumentException>();
            await Assert.That(refused!.ParamName).IsEqualTo("options");
            await Assert.That(() => RmQRCodeGenerator.CreateGs1DigitalLink(hri, RmQREccLevel.M, destination, mode)).Throws<ArgumentException>();
            await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(hri, RmQREccLevel.M, out _, mode)).Throws<ArgumentException>();
        }
        foreach (var stem in new[] { "ftp://x.example", "x.example", "https://" })
        {
            await Assert.That(() => RmQRCodeGenerator.CreateGs1DigitalLink(hri, RmQREccLevel.M, stem: stem)).Throws<ArgumentException>().Because(stem);
            await Assert.That(() => RmQRCodeGenerator.CreateGs1DigitalLink(hri, RmQREccLevel.M, destination, stem: stem)).Throws<ArgumentException>().Because(stem);
            await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(hri, RmQREccLevel.M, out _, stem: stem)).Throws<ArgumentException>().Because(stem);
        }

        // A wrong option before the data
        var disagreeing = new RmQRCodeGeneratorOptions { Version = RmQRVersion.R13x99, Height = RmQRHeight.H7 };
        foreach (var text in new[] { hri, "(01)09506000134353" })
        {
            await Assert.That(() => RmQRCodeGenerator.CreateGs1(text, RmQREccLevel.M, disagreeing)).Throws<ArgumentException>().Because(text);
            await Assert.That(() => RmQRCodeGenerator.CreateGs1(text, RmQREccLevel.M, disagreeing)).ThrowsExactly<ArgumentException>().Because("the option's error, not the data's");
            await Assert.That(() => RmQRCodeGenerator.CreateGs1(text, RmQREccLevel.M, destination, disagreeing)).ThrowsExactly<ArgumentException>().Because(text);
            await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(text, RmQREccLevel.M, out _, disagreeing)).Throws<ArgumentException>().Because(text);
            await Assert.That(() => RmQRCodeGenerator.CreateGs1DigitalLink(text, RmQREccLevel.M, disagreeing)).ThrowsExactly<ArgumentException>().Because(text);
            await Assert.That(() => RmQRCodeGenerator.CreateGs1DigitalLink(text, RmQREccLevel.M, destination, disagreeing)).ThrowsExactly<ArgumentException>().Because(text);
            await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text, RmQREccLevel.M, out _, disagreeing)).Throws<ArgumentException>().Because(text);
        }

        // No fit
        var narrow = new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 };
        await Assert.That(() => RmQRCodeGenerator.CreateGs1(hri, RmQREccLevel.H, narrow)).ThrowsExactly<ArgumentException>();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(hri, RmQREccLevel.H, out _, narrow)).IsFalse();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(hri, RmQREccLevel.H, out _, narrow)).IsFalse();
    }

    // Text past the stack buffers

    /// <summary>
    /// Text longer than the entry points' stack buffers goes through pooled ones, and the URI buffer is sized from
    /// the text alone, so the case here is the URI that grows most: every value character a slash, written as
    /// three. The symbols are the ones <c>Create</c> writes for the data layer's element string and URI.
    /// </summary>
    [Test]
    public async Task StandardQr_TextPastTheStackBuffers_WritesTheSameSymbols()
    {
        var hri = "(01)" + Gtin + string.Concat(Enumerable.Range(91, 9).Select(ai => $"({ai})" + new string('/', 90)));
        await Assert.That(Gs1ElementString.TryFromHri(hri, out var elementString, out var error)).IsTrue().Because(error.ToString());
        await Assert.That(Gs1ElementString.TryToDigitalLink(elementString, out var uri, out error)).IsTrue().Because(error.ToString());
        await Assert.That(elementString.Length).IsGreaterThan(Gs1SymbolData.StackLimit).Because("the premise: both forms of the text are past the stack buffer");
        await Assert.That(uri.Length).IsGreaterThan(2 * hri.Length).Because("the premise: the URI is far longer than the text it is built from");

        var composed = QRCodeGenerator.Create(elementString, QREccLevel.L, new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition });
        var composedLink = QRCodeGenerator.Create(uri, QREccLevel.L);
        foreach (var text in new[] { hri, elementString })
        {
            var data = QRCodeGenerator.CreateGs1(text, QREccLevel.L);
            await Assert.That(Modules(data)).IsEquivalentTo(Modules(composed), CollectionOrdering.Matching);
            await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1(text, QREccLevel.L, out var size)).IsTrue();
            var destination = new byte[size.BufferSize];
            await Assert.That(QRCodeGenerator.CreateGs1(text, QREccLevel.L, destination)).IsEqualTo(size.BufferSize);
            await Assert.That(destination).IsEquivalentTo(Modules(composed), CollectionOrdering.Matching);

            var link = QRCodeGenerator.CreateGs1DigitalLink(text, QREccLevel.L);
            await Assert.That(Modules(link)).IsEquivalentTo(Modules(composedLink), CollectionOrdering.Matching);
            await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text, QREccLevel.L, out var linkSize)).IsTrue();
            var linkDestination = new byte[linkSize.BufferSize];
            await Assert.That(QRCodeGenerator.CreateGs1DigitalLink(text, QREccLevel.L, linkDestination)).IsEqualTo(linkSize.BufferSize);
            await Assert.That(linkDestination).IsEquivalentTo(Modules(composedLink), CollectionOrdering.Matching);
        }

        // Refused data past the stack buffers is still the data layer's error, at its place in the long text.
        var refused = hri + "(10)\u00E9";
        await Assert.That(Gs1ElementString.TryFromHri(refused, out _, out var expected)).IsFalse();
        var thrown = await Assert.That(() => QRCodeGenerator.CreateGs1(refused, QREccLevel.L)).Throws<Gs1FormatException>();
        await Assert.That(thrown!.Error).IsEqualTo(expected);
        await Assert.That(thrown.Error.Offset).IsGreaterThan(Gs1SymbolData.StackLimit);
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1(refused, QREccLevel.L, out _)).IsFalse();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(refused, QREccLevel.L, out _)).IsFalse();
    }

    /// <summary>
    /// The same in rMQR, whose largest symbol holds 361 digits: (AI) text of 516 characters whose element string
    /// is 348 digits, since an AI may repeat with one value.
    /// </summary>
    [Test]
    public async Task RmQr_TextPastTheStackBuffers_WritesTheSameSymbol()
    {
        var hri = "(01)" + Gtin + string.Concat(Enumerable.Repeat("(20)12", 83));
        await Assert.That(hri.Length).IsGreaterThan(Gs1SymbolData.StackLimit);
        await Assert.That(Gs1ElementString.TryFromHri(hri, out var elementString, out var error)).IsTrue().Because(error.ToString());
        await Assert.That(elementString).IsEqualTo("01" + Gtin + string.Concat(Enumerable.Repeat("2012", 83)));

        var data = RmQRCodeGenerator.CreateGs1(hri, RmQREccLevel.M);
        var composed = RmQRCodeGenerator.Create(elementString, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition });
        await Assert.That(Modules(data)).IsEquivalentTo(Modules(composed), CollectionOrdering.Matching);
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(hri, RmQREccLevel.M, out var size)).IsTrue();
        var destination = new byte[size.BufferSize];
        await Assert.That(RmQRCodeGenerator.CreateGs1(hri, RmQREccLevel.M, destination)).IsEqualTo(size.BufferSize);
        await Assert.That(destination).IsEquivalentTo(Modules(composed), CollectionOrdering.Matching);

        // Ship-to locations alone are valid data with no primary key, so no URI: the error covers the whole of the long text.
        var noKey = string.Concat(Enumerable.Repeat("(410)1234567890128", 29));
        await Assert.That(noKey.Length).IsGreaterThan(Gs1SymbolData.StackLimit);
        await Assert.That(Gs1ElementString.TryFromHri(noKey, out _, out error)).IsTrue().Because(error.ToString());
        var thrown = await Assert.That(() => RmQRCodeGenerator.CreateGs1DigitalLink(noKey, RmQREccLevel.M)).Throws<Gs1FormatException>();
        await Assert.That(thrown!.Error).IsEqualTo(new Gs1Error(Gs1ErrorKind.MissingPrimaryKey, 0, noKey.Length));
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(noKey, RmQREccLevel.M, out _)).IsFalse();

        // A URI too long for any rMQR symbol is content that does not fit, as Create reports it.
        var withKey = "(01)" + Gtin + string.Concat(Enumerable.Range(91, 9).Select(ai => $"({ai})" + new string('/', 90)));
        await Assert.That(() => RmQRCodeGenerator.CreateGs1DigitalLink(withKey, RmQREccLevel.M)).ThrowsExactly<ArgumentException>();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(withKey, RmQREccLevel.M, out _)).IsFalse();
        await Assert.That(() => RmQRCodeGenerator.CreateGs1(withKey, RmQREccLevel.M)).ThrowsExactly<ArgumentException>();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(withKey, RmQREccLevel.M, out _)).IsFalse();
    }

    // The corpus: every bracketed and element string input of the GS1 fixtures through the entry points

    /// <summary>
    /// Fixtures of the bracketed kind whose text the entry points read as an element string and take, where the
    /// Syntax Engine, told the text is bracketed, refuses it. The entry points take the form from the first
    /// character, so text without an opening parenthesis is an element string to them.
    /// </summary>
    private static readonly HashSet<string> FormDifferences =
    [
        "eq-text-element-string-given-as-bracketed",    // 010952012345678810ABC: (01) and (10), valid as an element string
    ];

    public static IEnumerable<Func<(string Id, string Input, bool Valid, string? ElementString, bool Link)>> CorpusCases()
    {
        foreach (var c in Gs1FixtureLoader.EnumerateCases())
        {
            if (c.InputKind is not (Gs1InputKind.Bracketed or Gs1InputKind.ElementString))
                continue;
            var asElementString = FormDifferences.Contains(c.Id);
            var captured = (
                c.Id,
                c.Input,
                c.SyntaxEngine.Valid || asElementString,
                asElementString ? c.Input : c.SyntaxEngine.ElementString,
                // The URI is the data layer's, which builds one for the data its tests list as a deliberate difference.
                c.SyntaxEngine.DigitalLink is not null || asElementString || Gs1DigitalLinkTest.BuildDifferenceIds.Contains(c.Id));
            yield return () => captured;
        }
    }

    /// <summary>
    /// The entry points agree with the Syntax Engine on what is GS1 data, write the engine's element string, and keep
    /// the plan's compliance property: a GS1 QR Code written with default options holds FNC1 in first position, no
    /// ECI header and no Kanji segment. The same under <c>Optimal</c> and in rMQR where the data fits a symbol.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(CorpusCases))]
    public async Task Corpus_CreateGs1_TakesWhatTheSyntaxEngineTakes_AndWritesACompliantSymbol((string Id, string Input, bool Valid, string? ElementString, bool Link) c)
    {
        var (resolved, elementString, error) = Resolve(c.Input, Year);
        await Assert.That(resolved).IsEqualTo(c.Valid).Because($"{c.Id}: {(resolved ? "taken" : error.ToString())}");

        // The public entry points read the clock for two-digit years. A case whose verdict the year changes is left to the data layer's tests.
        if (Resolve(c.Input, DateTime.UtcNow.Year).Taken != resolved)
            return;

        if (!c.Valid)
        {
            var thrown = await Assert.That(() => QRCodeGenerator.CreateGs1(c.Input, QREccLevel.M)).Throws<Gs1FormatException>().Because(c.Id);
            await Assert.That(thrown!.Error).IsEqualTo(error).Because(c.Id);
            await Assert.That(thrown.Error.Offset + thrown.Error.Length).IsLessThanOrEqualTo(Math.Max(c.Input.Length, 1)).Because(c.Id);
            await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1(c.Input, QREccLevel.M, out _)).IsFalse().Because(c.Id);
            await Assert.That(() => RmQRCodeGenerator.CreateGs1(c.Input, RmQREccLevel.M)).Throws<Gs1FormatException>().Because(c.Id);
            return;
        }

        if (c.ElementString is not null)
            await Assert.That(elementString).IsEqualTo(c.ElementString).Because(c.Id);

        foreach (var segmentation in new[] { QRSegmentation.Single, QRSegmentation.Optimal })
        {
            var data = QRCodeGenerator.CreateGs1(c.Input, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0, Segmentation = segmentation });
            await Assert.That(QRCodeDecoder.TryDecode(data, out var text, out var info)).IsTrue().Because($"{c.Id} ({segmentation}): {info.Status}");
            await Assert.That(text).IsEqualTo(elementString).Because(c.Id);
            await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition).Because(c.Id);

            var shape = Fnc1StreamShape.OfStandardQr(Modules(data), data.Size, info.Version, info.EccLevel, info.MaskPattern);
            await Assert.That(shape.Headers).IsEquivalentTo(["FNC1First"], CollectionOrdering.Matching).Because($"{c.Id} ({segmentation}): no ECI header");
            await Assert.That(shape.DataModes.Where(m => m is not ("Numeric" or "Alphanumeric" or "Byte"))).IsEmpty().Because($"{c.Id} ({segmentation}): no Kanji segment");
        }

        foreach (var segmentation in new[] { RmQRSegmentation.Single, RmQRSegmentation.Optimal })
        {
            var options = new RmQRCodeGeneratorOptions { QuietZoneSize = 0, Segmentation = segmentation };
            if (!RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(c.Input, RmQREccLevel.M, out _, options))
                continue;
            var data = RmQRCodeGenerator.CreateGs1(c.Input, RmQREccLevel.M, options);
            await Assert.That(RmQRCodeDecoder.TryDecode(data, out var text, out var info)).IsTrue().Because($"{c.Id} ({segmentation}): {info.Status}");
            await Assert.That(text).IsEqualTo(elementString).Because(c.Id);
            await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition).Because(c.Id);

            var shape = Fnc1StreamShape.OfRmQr(Modules(data), data.Width, data.Height, info.Version, info.EccLevel);
            await Assert.That(shape.Headers).IsEquivalentTo(["FNC1First"], CollectionOrdering.Matching).Because($"{c.Id} ({segmentation}): no ECI header");
            await Assert.That(shape.DataModes.Where(m => m is not ("Numeric" or "Alphanumeric" or "Byte"))).IsEmpty().Because($"{c.Id} ({segmentation}): no Kanji segment");
        }
    }

    /// <summary>
    /// The retail carrier over the corpus: where the data layer builds a URI the entry point writes that URI as plain
    /// text, which reads back as GS1 data, and where it does not the entry point throws its error.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(CorpusCases))]
    public async Task Corpus_CreateGs1DigitalLink_WritesTheDataLayersUri((string Id, string Input, bool Valid, string? ElementString, bool Link) c)
    {
        var (built, uri, error) = ResolveLink(c.Input, Year);
        await Assert.That(built).IsEqualTo(c.Valid && c.Link).Because($"{c.Id}: {(built ? uri : error.ToString())}");
        if (ResolveLink(c.Input, DateTime.UtcNow.Year).Built != built)
            return;

        if (!built)
        {
            var thrown = await Assert.That(() => QRCodeGenerator.CreateGs1DigitalLink(c.Input, QREccLevel.M)).Throws<Gs1FormatException>().Because(c.Id);
            await Assert.That(thrown!.Error).IsEqualTo(error).Because(c.Id);
            await Assert.That(thrown.Error.Offset + thrown.Error.Length).IsLessThanOrEqualTo(Math.Max(c.Input.Length, 1)).Because($"{c.Id}: the place is in the caller's text");
            await Assert.That(QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(c.Input, QREccLevel.M, out _)).IsFalse().Because(c.Id);
            return;
        }

        // The data layer's own URI for the element string, which its tests hold to the Syntax Engine's.
        await Assert.That(Gs1DigitalLink.TryToDigitalLink(Resolve(c.Input, Year).ElementString, Year, null, out var expected, out _)).IsTrue();
        await Assert.That(uri).IsEqualTo(expected).Because(c.Id);

        var data = QRCodeGenerator.CreateGs1DigitalLink(c.Input, QREccLevel.M);
        await Assert.That(QRCodeDecoder.TryDecode(data, out var text, out var info)).IsTrue().Because($"{c.Id}: {info.Status}");
        await Assert.That(text).IsEqualTo(uri).Because(c.Id);
        await Assert.That(info.Fnc1).IsEqualTo(Fnc1Mode.None).Because(c.Id);
        await Assert.That(Gs1ElementString.TryRead(text, info.Fnc1, out _, out var readError)).IsTrue().Because($"{c.Id}: {readError}");
    }

    /// <summary>The corpus must reach both verdicts on both carriers, or the two tests above prove less than they read.</summary>
    [Test]
    public async Task Corpus_HasValidAndInvalidData_AndDataAUriCannotCarry()
    {
        var cases = CorpusCases().Select(f => f()).ToList();
        await Assert.That(cases.Count(c => c.Valid)).IsEqualTo(677);
        await Assert.That(cases.Count(c => !c.Valid)).IsEqualTo(321);
        await Assert.That(cases.Count(c => c.Valid && c.Link)).IsEqualTo(632);
        await Assert.That(cases.Count(c => c.Valid && !c.Link)).IsEqualTo(45);
    }

    // The photograph: a symbol from the field through the whole layer

    public static IEnumerable<int> QuarterTurns() => [0, 1, 2, 3];

    /// <summary>
    /// GS1 General Specifications figure 4.15.1-2 as photographed: decoded at every right angle, read as (AI) text,
    /// and written again from that text into a symbol that decodes to the photograph's element string. Its (8200)
    /// keeps it out of a Digital Link URI, which the retail entry point says.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(QuarterTurns))]
    public async Task Photograph_ReadsAsAiText_AndIsWrittenAgainFromIt(int quarterTurns)
    {
        const string expectedHri = "(01)09504000059101(21)12345678p901(10)1234567p(17)141120(8200)http://www.gs1.org/demo/";
        var path = Path.Combine(FixtureLoader.FixtureRoot, "RealImages", "zxing-cpp-samples", "qrcode-2", "gs1-figure-4.15.1-2.png");
        using var bitmap = SKBitmap.Decode(path);
        var luminance = new byte[bitmap.Width * bitmap.Height];
        FeatherQR.SkiaSharp.Internals.BitmapLuminanceConverter.Convert(bitmap, luminance);
        var (turned, width, height) = NearestNeighbourRenderer.Turn(luminance, bitmap.Width, bitmap.Height, quarterTurns, mirror: false);

        await Assert.That(QRCodeDecoder.TryDecodeImage(turned, width, height, out var text, out var info)).IsTrue().Because($"turned {quarterTurns * 90} degrees: {info.Status}");
        await Assert.That(Gs1ElementString.TryRead(text, info.Fnc1, out var hri, out var error)).IsTrue().Because(error.ToString());
        await Assert.That(hri).IsEqualTo(expectedHri);

        var again = QRCodeGenerator.CreateGs1(hri, info.EccLevel);
        await Assert.That(QRCodeDecoder.TryDecode(again, out var textAgain, out var infoAgain)).IsTrue();
        await Assert.That(textAgain).IsEqualTo(text);
        await Assert.That(infoAgain.Fnc1).IsEqualTo(Fnc1Mode.FirstPosition);

        var thrown = await Assert.That(() => QRCodeGenerator.CreateGs1DigitalLink(hri, QREccLevel.M)).Throws<Gs1FormatException>();
        await Assert.That(thrown!.Error.Kind).IsEqualTo(Gs1ErrorKind.AiNotAllowedInUri);
        await Assert.That(hri.Substring(thrown.Error.Offset, thrown.Error.Length)).IsEqualTo("(8200)http://www.gs1.org/demo/");
    }

    // The exception

    [Test]
    public async Task Gs1FormatException_CarriesTheErrorAndItsMessage()
    {
        var error = new Gs1Error(Gs1ErrorKind.InvalidCheckDigit, 17, 1);
        var exception = new Gs1FormatException(error);

        await Assert.That(exception.Error).IsEqualTo(error);
        await Assert.That(exception.Message).IsEqualTo("InvalidCheckDigit at 17 (1 character): the check digit is wrong.");
        await Assert.That(exception).IsAssignableTo<FormatException>();
        await Assert.That(typeof(Gs1FormatException).IsSealed).IsTrue();
    }

    // Helpers

    /// <summary>The entry points' element string for a text in a given year, as strings, so it can be held across an await.</summary>
    private static (bool Taken, string ElementString, Gs1Error Error) Resolve(string text, int year)
    {
        var taken = Gs1SymbolData.TryGetElementString(text, year, new char[Math.Max(text.Length, 1)], out var elementString, out var error);
        return (taken, elementString.ToString(), error);
    }

    /// <summary>The entry points' Digital Link URI for a text in a given year under the canonical stem.</summary>
    private static (bool Built, string Uri, Gs1Error Error) ResolveLink(string text, int year)
    {
        var stem = Gs1DigitalLink.ValidateStem(null);
        var built = Gs1SymbolData.TryGetDigitalLink(text, year, stem, new char[Math.Max(text.Length, 1)], new char[Gs1SymbolData.DigitalLinkBound(text.Length, stem.Length)], out var uri, out var error);
        return (built, uri.ToString(), error);
    }

    /// <summary>The modules of a symbol, quiet zone included, 1 for dark, as the span form writes them.</summary>
    private static byte[] Modules(QRCodeData data)
    {
        var size = data.Size;
        var modules = new byte[size * size];
        for (var row = 0; row < size; row++)
            for (var col = 0; col < size; col++)
                modules[row * size + col] = data[row, col] ? (byte)1 : (byte)0;
        return modules;
    }

    private static byte[] Modules(RmQRCodeData data)
    {
        var modules = new byte[data.Width * data.Height];
        for (var row = 0; row < data.Height; row++)
            for (var col = 0; col < data.Width; col++)
                modules[row * data.Width + col] = data[row, col] ? (byte)1 : (byte)0;
        return modules;
    }
}
