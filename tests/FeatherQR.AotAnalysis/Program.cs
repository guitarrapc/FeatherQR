using FeatherQR;

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
return 0;
