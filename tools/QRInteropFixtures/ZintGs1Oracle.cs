using ZXing;
using ZXing.QrCode.Internal;
using ZXingCpp;
using ZXingEncoder = ZXing.QrCode.Internal.Encoder;

namespace QRInteropFixtures;

/// <summary>
/// The symbol-side GS1 oracles. libzint's <c>gs1</c> option (through the pinned ZXingCpp package) takes
/// bracketed AI syntax, checks it against its own generated table and writes the element string into a
/// GS1 QR Code, or refuses. The pinned zxing-cpp reader then reads the symbol twice: in
/// <c>TextMode.Plain</c> for the element string libzint wrote (its separator placement) and in
/// <c>TextMode.HRI</c> for its <c>(AI)</c> text (its hand-maintained table's split). For an input that is
/// already an element string, ZXing.Net's <c>GS1_FORMAT</c> writer puts it into a symbol unchanged, and
/// the same reader gives zxing-cpp's <c>(AI)</c> text of it. ZXing.Net does not double a literal <c>%</c>,
/// so an element string holding one has no reader verdict.
/// </summary>
public static class ZintGs1Oracle
{
    public static string LibzintVersion => Fnc1Writers.ZintVersion;

    public static string ZxingCppVersion => $"ZXingCpp {typeof(BarcodeReader).Assembly.GetName().Version?.ToString(3) ?? "unknown"} bundled zxing-cpp";

    public static (Gs1LibzintVerdict Libzint, Gs1ReaderVerdict? Reader) RunBracketed(string bracketed)
    {
        ModuleMatrix matrix;
        try
        {
            var creator = new BarcodeCreator(ZXingCpp.BarcodeFormat.QRCode) { Options = "gs1" };
            using var barcode = creator.From(bracketed);
            if (!barcode.IsValid)
                return (new Gs1LibzintVerdict { Valid = false, Error = barcode.ErrorMsg }, null);
            matrix = Fnc1Writers.FromZint(barcode);
            // zint 2.16.0 writes the symbol and reports a linter failure as a non-compliance warning (its gs1.c keeps
            // only an unknown AI and a bad length as errors, "for backward compatibility"). The wrapper may carry it.
            var warning = string.IsNullOrEmpty(barcode.ErrorMsg) ? null : barcode.ErrorMsg;
            return (new Gs1LibzintVerdict { Valid = true, Warning = warning }, Read(matrix, "zint-libzint"));
        }
        catch (Exception ex)
        {
            return (new Gs1LibzintVerdict { Valid = false, Error = FirstLine(ex.Message) }, null);
        }
    }

    public static Gs1ReaderVerdict? RunElementString(string elementString)
    {
        if (elementString.Length == 0 || elementString.Contains('%') || elementString.Any(c => c > 'ÿ'))
            return null; // ZXing.Net writes a lone % for the separator, and its default charset is ISO-8859-1
        try
        {
            var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.GS1_FORMAT] = true };
            var qr = ZXingEncoder.encode(elementString, ErrorCorrectionLevel.M, hints);
            var reader = Read(Fnc1Writers.FromZXingNet(qr), "zxing-net");
            // A symbol that does not read back as the input is not a symbol of the input.
            return reader is not null && reader.ElementString == elementString ? reader : null;
        }
        catch
        {
            return null;
        }
    }

    private static Gs1ReaderVerdict? Read(ModuleMatrix matrix, string writer)
    {
        var plain = ReadOne(matrix, TextMode.Plain);
        var hri = ReadOne(matrix, TextMode.HRI);
        if (plain is null || hri is null)
            return null;
        return new Gs1ReaderVerdict
        {
            Writer = writer,
            SymbologyIdentifier = plain.Value.Identifier,
            ElementString = plain.Value.Text,
            Hri = hri.Value.Text,
        };
    }

    private static (string Identifier, string Text)? ReadOne(ModuleMatrix matrix, TextMode mode)
    {
        var (luminance, width, height) = Fnc1Writers.Render(matrix, FixtureWriter.QuietZoneModules, 4);
        var reader = new BarcodeReader { Formats = ZXingCpp.BarcodeFormat.QRCode, TryHarder = true, TextMode = mode };
        var results = reader.From(new ImageView(luminance, width, height, ImageFormat.Lum));
        if (results.Length != 1 || !results[0].IsValid)
            return null;
        return (results[0].SymbologyIdentifier, results[0].Text);
    }

    private static string FirstLine(string message) => message.Split('\n')[0].TrimEnd('\r');
}
