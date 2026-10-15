using FeatherQR;
using FeatherQR.Gs1;
using System.Text;

// The gate is the publish itself (see the csproj): TrimmerRootAssembly roots the whole library,
// so ILC analyzes every public and internal member regardless of what runs here.
// This entry point is a minimal encode/decode smoke so the produced binary is still runnable.
// With --simd-class, it also holds the SIMD tiers this build takes to SimdTiers.Expected for that
// build class and fails on a disagreement. CI passes the class each build is meant to be.
// With --time, it times the benchmark shapes on this build instead (TierTiming).
if (TierTiming.TryRun(args, out var timingExit))
    return timingExit;
if (!SimdReport.TryParseClass(args, out var simdClass))
    return 2;

var content = "FeatherQR AOT analysis gate";
var qr = QRCodeGenerator.Create(content, QREccLevel.M);
if (!QRCodeDecoder.TryDecode(qr, out var decoded) || decoded != content)
{
    Console.Error.WriteLine("Round-trip failed.");
    return 1;
}

Console.WriteLine($"OK: version {qr.Version}, {qr.Size}x{qr.Size} modules.");
// Exercise charset and segmentation paths through the public API after native compilation.
ulong digest = 14695981039346656037;
foreach (var (pattern, charset) in new[]
{
    ("Crème brûlée 1234567890 ", EciMode.Iso8859_1),
    ("ご注文 20260919 🎉🎊 ABC ", EciMode.Utf8),
})
{
    var message = string.Concat(Enumerable.Repeat(pattern, 7)) + " tail 7";
    foreach (var segmentation in new[] { QRSegmentation.Single, QRSegmentation.Optimal })
        foreach (var bom in new[] { false, true })
        {
            var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(5), EciMode = charset, Utf8Bom = bom, Segmentation = segmentation };
            var symbols = QRCodeGenerator.CreateStructuredAppend(message, QREccLevel.L, options);
            if (symbols.Length < 2)
                throw new InvalidOperationException("Structured Append smoke must produce a set.");
            var bytes = charset == EciMode.Utf8 ? Encoding.UTF8.GetBytes(message) : Encoding.Latin1.GetBytes(message);
            byte parity = (byte)(bom && charset == EciMode.Utf8 ? 0xEF ^ 0xBB ^ 0xBF : 0);
            foreach (var b in bytes)
                parity ^= b;
            var restored = new StringBuilder();
            foreach (var symbol in symbols)
            {
                if (!QRCodeDecoder.TryDecode(symbol, out var part, out var info)
                    || info.StructuredAppend.IsEmpty || info.StructuredAppend.Parity != parity)
                    throw new InvalidOperationException("Structured Append parity/round-trip failed.");
                restored.Append(part);
                foreach (var b in symbol.GetRawData())
                    digest = unchecked((digest ^ b) * 1099511628211UL);
            }
            if (restored.ToString() != message)
                throw new InvalidOperationException("Structured Append text differs.");
        }
}
Console.WriteLine($"Structured Append FNV64: {digest:X16}");

// The GS1 layer after native compilation: (AI) text into a GS1 QR Code and an rMQR code and back, the Digital Link
// URI of the same data as plain text and back, and refused data as the exception that carries the data layer's error.
const string gs1Text = "(01)09506000134352(10)ABC123(17)201225";
var gs1Symbol = QRCodeGenerator.CreateGs1(gs1Text, QREccLevel.M);
if (!QRCodeDecoder.TryDecode(gs1Symbol, out var gs1Data, out var gs1Info) || gs1Info.Fnc1 != Fnc1Mode.FirstPosition
    || !Gs1ElementString.TryRead(gs1Data, gs1Info.Fnc1, out var gs1Read, out _) || gs1Read != gs1Text)
    throw new InvalidOperationException("GS1 QR Code round-trip failed.");
var gs1RmQr = RmQRCodeGenerator.CreateGs1(gs1Text, RmQREccLevel.M);
if (!RmQRCodeDecoder.TryDecode(gs1RmQr, out var gs1RmQrData, out var gs1RmQrInfo) || gs1RmQrInfo.Fnc1 != Fnc1Mode.FirstPosition || gs1RmQrData != gs1Data)
    throw new InvalidOperationException("GS1 rMQR round-trip failed.");
var gs1Link = QRCodeGenerator.CreateGs1DigitalLink(gs1Text, QREccLevel.M);
if (!QRCodeDecoder.TryDecode(gs1Link, out var gs1Uri, out var gs1LinkInfo) || gs1LinkInfo.Fnc1 != Fnc1Mode.None
    || gs1Uri != "https://id.gs1.org/01/09506000134352/10/ABC123?17=201225"
    || !Gs1ElementString.TryRead(gs1Uri, gs1LinkInfo.Fnc1, out var gs1LinkRead, out _) || gs1LinkRead != gs1Text)
    throw new InvalidOperationException("GS1 Digital Link round-trip failed.");
try
{
    QRCodeGenerator.CreateGs1("(01)09506000134353", QREccLevel.M);
    throw new InvalidOperationException("GS1 data with a wrong check digit was taken.");
}
catch (Gs1FormatException ex) when (ex.Error.Kind == Gs1ErrorKind.InvalidCheckDigit)
{
}
Console.WriteLine($"GS1: version {gs1Symbol.Version}, {gs1RmQr.Version}, Digital Link version {gs1Link.Version}.");

// Which SIMD tier each kernel runs in this native build, held to the table for --simd-class. With --parity, the vector tiers
// held to their scalar forms on the code ILC emitted.
var tierResult = SimdReport.PrintAndCheck(simdClass);
var parityResult = SimdParity.Requested(args) ? SimdParity.Run() : 0;
return tierResult != 0 ? tierResult : parityResult;
