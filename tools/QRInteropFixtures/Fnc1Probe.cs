using CodeGlyphX;
using ZXing;
using ZXingCpp;
using CppBarcode = ZXingCpp.Barcode;
using CppFormat = ZXingCpp.BarcodeFormat;
using NetEcc = ZXing.QrCode.Internal.ErrorCorrectionLevel;
using NetEncoder = ZXing.QrCode.Internal.Encoder;

namespace QRInteropFixtures;

/// <summary>
/// Diagnostic for the FNC1 writers and readers the fixture corpus rests on, recorded in the oracle
/// capability matrix of specs/qrcode-test-fixtures.md: which writer can write which mode in which
/// symbology, what input form it takes, in which order it writes FNC1 and an ECI header, how it
/// handles a literal <c>%</c> and the separator pairs in an Alphanumeric segment, and how zxing-cpp
/// (<c>TextMode.Plain</c>) and ZXing.Net read each symbol. A writer that refuses an input is a row
/// too, with its message, because the refusals (libzint and CodeGlyphX's rMQR validator on a raw
/// separator) decide what the corpus can hold.
/// </summary>
public static class Fnc1Probe
{
    private const string Gs = "\u001D";
    private const string Bracketed = "(01)09524000059109(21)12345678p901(10)1234567p(17)231120";
    private const string ElementString = "0109524000059109" + "2112345678p901" + Gs + "101234567p" + Gs + "17231120";

    public static int Run()
    {
        Console.WriteLine("FNC1 writer probe: each row is one writer's symbol read by zxing-cpp (TextMode.Plain: identifier, content type, ECI, text)");
        Console.WriteLine("and, for Standard QR, by ZXing.Net (identifier, text). GS is shown as <GS>. Findings go to specs/qrcode-test-fixtures.md.");
        Console.WriteLine();
        Console.WriteLine($"{"writer and input",-70}  {"zxing-cpp",-66}  ZXing.Net");

        Row("libzint QR gs1, [01]...[17] bracketed", CppFormat.QRCode, () => Zint(CppFormat.QRCode, "gs1", "[01]09524000059109[21]12345678p901[10]1234567p[17]231120"));
        Row("libzint QR gs1, (01)...(17) parenthesized", CppFormat.QRCode, () => Zint(CppFormat.QRCode, "gs1", Bracketed));
        Row("libzint QR gs1=true", CppFormat.QRCode, () => Zint(CppFormat.QRCode, "gs1=true", Bracketed));
        Row("libzint QR gs1, element string with raw GS", CppFormat.QRCode, () => Zint(CppFormat.QRCode, "gs1", ElementString));
        Row("libzint QR gs1, (10)AB%CD literal %", CppFormat.QRCode, () => Zint(CppFormat.QRCode, "gs1", "(01)09524000059109(10)AB%CD(17)231120"));
        Row("libzint QR gs1,eci=26 (ASCII under ECI)", CppFormat.QRCode, () => Zint(CppFormat.QRCode, "gs1,eci=26", Bracketed));
        Row("libzint rMQR gs1", CppFormat.RMQRCode, () => Zint(CppFormat.RMQRCode, "gs1", Bracketed));
        Row("libzint rMQR gs1,eci=26", CppFormat.RMQRCode, () => Zint(CppFormat.RMQRCode, "gs1,eci=26", Bracketed));
        Row("libzint Micro QR gs1, [17]231120", CppFormat.MicroQRCode, () => Zint(CppFormat.MicroQRCode, "gs1", "[17]231120"));

        Row("ZXing.Net GS1_FORMAT, element string with GS", CppFormat.QRCode, () => Net(ElementString, null));
        Row("ZXing.Net GS1_FORMAT, 010952400005910917231120 digits", CppFormat.QRCode, () => Net("010952400005910917231120", null));
        Row("ZXing.Net GS1_FORMAT, 123% literal %", CppFormat.QRCode, () => Net("123%", null));
        Row("ZXing.Net GS1_FORMAT + UTF-8, 10é<GS>17231120", CppFormat.QRCode, () => Net("10é" + Gs + "17231120", "UTF-8"));

        Row("CodeGlyphX EncodeGs1, element string with GS", CppFormat.QRCode, () => GlyphFirst(ElementString, false));
        Row("CodeGlyphX EncodeGs1, 123% literal %", CppFormat.QRCode, () => GlyphFirst("123%", false));
        Row("CodeGlyphX EncodeGs1, A<GS>%B", CppFormat.QRCode, () => GlyphFirst("A" + Gs + "%B", false));
        Row("CodeGlyphX EncodeGs1, A<GS><GS>B", CppFormat.QRCode, () => GlyphFirst("A" + Gs + Gs + "B", false));
        Row("CodeGlyphX EncodeGs1, A%<GS>B", CppFormat.QRCode, () => GlyphFirst("A%" + Gs + "B", false));
        Row("CodeGlyphX EncodeGs1 + UTF-8 ECI, 10é<GS>17231120", CppFormat.QRCode, () => GlyphFirst("10é" + Gs + "17231120", true));
        foreach (var indicator in new[] { 0, 37, 99, 'A' + 100, 'a' + 100 })
            Row($"CodeGlyphX second position {indicator}, ISO 6.4.8.2 example text", CppFormat.QRCode, () => GlyphSecond(Fnc1Corpus.IsoSecondPositionExample, indicator));
        Row("CodeGlyphX second position 37, AB<GS>%CD", CppFormat.QRCode, () => GlyphSecond("AB" + Gs + "%CD", 37));
        Row("CodeGlyphX second position 37, AB<GS><GS>CD", CppFormat.QRCode, () => GlyphSecond("AB" + Gs + Gs + "CD", 37));
        Row("CodeGlyphX second position 37, AB%<GS>CD", CppFormat.QRCode, () => GlyphSecond("AB%" + Gs + "CD", 37));
        Row("CodeGlyphX second position 164 (invalid indicator)", CppFormat.QRCode, () => GlyphSecond("AB", 164));
        Row("CodeGlyphX rMQR EncodeGs1, (01)...(17)", CppFormat.RMQRCode, () => GlyphRmQR(Bracketed));
        Row("CodeGlyphX rMQR EncodeGs1, element string with raw GS", CppFormat.RMQRCode, () => GlyphRmQR(ElementString));
        Row("CodeGlyphX rMQR EncodeGs1, raw GS after fixed-length AI", CppFormat.RMQRCode, () => GlyphRmQR("0109524000059109" + Gs + "17231120"));
        return 0;
    }

    private static void Row(string label, CppFormat format, Func<ModuleMatrix> write)
    {
        ModuleMatrix matrix;
        try
        {
            matrix = write();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{label,-70}  refused: {ex.GetType().Name}: {FirstLine(ex.Message)}");
            return;
        }

        var quietZone = format == CppFormat.QRCode ? FixtureWriter.QuietZoneModules : FixtureWriter.RmQRQuietZoneModules;
        string cpp;
        try
        {
            var results = Fnc1Writers.ReadCpp(matrix, format, quietZone);
            cpp = results.Length == 1 ? Describe(results[0]) : $"{results.Length} results";
        }
        catch (Exception ex)
        {
            cpp = $"{ex.GetType().Name}: {FirstLine(ex.Message)}";
        }

        var net = "";
        if (format == CppFormat.QRCode)
        {
            try
            {
                var result = Fnc1Writers.ReadNet(matrix);
                net = result is null
                    ? "no read"
                    : $"{(result.ResultMetadata is not null && result.ResultMetadata.TryGetValue(ResultMetadataType.SYMBOLOGY_IDENTIFIER, out var id) ? id : "(no identifier)")} \"{Fnc1Writers.Escape(result.Text ?? "")}\"";
            }
            catch (Exception ex)
            {
                net = $"{ex.GetType().Name}";
            }
        }

        Console.WriteLine($"{label,-70}  {cpp,-66}  {net}");
    }

    private static string Describe(CppBarcode barcode)
        => barcode.IsValid
            ? $"{barcode.SymbologyIdentifier} {barcode.ContentType}{(barcode.HasECI ? " ECI" : "")} \"{Fnc1Writers.Escape(barcode.Text)}\""
            : $"invalid: {barcode.ErrorMsg}";

    private static string FirstLine(string message) => message.Split('\n')[0].TrimEnd('\r');

    private static ModuleMatrix Zint(CppFormat format, string options, string input)
    {
        var creator = new BarcodeCreator(format) { Options = options };
        using var barcode = creator.From(input);
        return Fnc1Writers.FromZint(barcode);
    }

    private static ModuleMatrix Net(string input, string? characterSet)
    {
        var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.GS1_FORMAT] = true };
        if (characterSet is not null)
            hints[EncodeHintType.CHARACTER_SET] = characterSet;
        return Fnc1Writers.FromZXingNet(NetEncoder.encode(input, NetEcc.M, hints));
    }

    private static ModuleMatrix GlyphFirst(string input, bool utf8)
    {
        var options = new QrEncodingOptions
        {
            ErrorCorrectionLevel = QrErrorCorrectionLevel.M,
            TextEncoding = utf8 ? QrTextEncoding.Utf8 : QrTextEncoding.Latin1,
            EciMode = utf8 ? QrEciMode.Always : QrEciMode.Auto,
        };
        return Fnc1Writers.FromCodeGlyphX(QrCodeEncoder.EncodeGs1(input, options));
    }

    private static ModuleMatrix GlyphSecond(string input, int indicator)
    {
        var options = new QrEncodingOptions
        {
            ErrorCorrectionLevel = QrErrorCorrectionLevel.M,
            Fnc1Mode = QrFnc1Mode.SecondPosition,
            Fnc1ApplicationIndicator = indicator,
        };
        return Fnc1Writers.FromCodeGlyphX(QrCodeEncoder.EncodeText(input, options));
    }

    private static ModuleMatrix GlyphRmQR(string input)
        => Fnc1Writers.FromCodeGlyphX(RmQrCodeEncoder.EncodeGs1(input, QrErrorCorrectionLevel.M));
}
