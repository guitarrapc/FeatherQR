using FeatherQR;
using System.Text;

// The gate is the publish itself (see the csproj): TrimmerRootAssembly roots the whole library,
// so ILC analyzes every public and internal member regardless of what runs here.
// This entry point is a minimal encode/decode smoke so the produced binary is still runnable.
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
return 0;
