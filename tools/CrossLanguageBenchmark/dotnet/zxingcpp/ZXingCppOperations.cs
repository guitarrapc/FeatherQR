using System.Text;
using ZXingCpp;

/// <summary>
/// The calls zxing-cpp's .NET package (ZXingCpp) makes for each operation, shared by its CLI and the BenchmarkDotNet reference project.
/// Against the native zxing-cpp CLI, built from the commit the package ships, the difference is what the wrapper adds.
/// </summary>
/// <remarks>
/// The package reads images and writes symbols, and has no matrix decoder, so decode-matrix reports unsupported.
/// The calls match the native CLI's: reading stops at the first symbol, as the native ReadBarcode does (the package's reader otherwise
/// looks for every symbol), and writing takes the same options string, so both write through the same bundled libzint.
/// </remarks>
internal static class ZXingCppOperations
{
    private const string Failed = "\"status\":\"failed\"";

    public static Operation Load(string op, string symbology, string input, string? ecc, string? version)
    {
        if (symbology is not ("qr" or "microqr" or "rmqr"))
            throw new NotSupportedException(symbology);
        return op switch
        {
            "decode-image" => DecodeImage(Pgm.Read(input), symbology switch
            {
                "qr" => BarcodeFormat.QRCodeModel2,
                "microqr" => BarcodeFormat.MicroQRCode,
                _ => BarcodeFormat.RMQRCode,
            }),
            "encode" => Encode(
                Encoding.UTF8.GetString(File.ReadAllBytes(input)),
                symbology switch
                {
                    "qr" => BarcodeFormat.QRCode,
                    "microqr" => BarcodeFormat.MicroQRCode,
                    _ => BarcodeFormat.RMQRCode,
                },
                $"ecLevel={ecc ?? throw new ArgumentException("encode needs --ecc.")},version={ZintVersion(symbology, version ?? throw new ArgumentException("encode needs --version."))}"),
            _ => throw new NotSupportedException(op),
        };
    }

    private static Operation DecodeImage((byte[] Pixels, int Width, int Height) image, BarcodeFormat format)
    {
        // An ImageView keeps a pointer to the pixels without pinning them, so they live on the pinned heap.
        var pixels = GC.AllocateArray<byte>(image.Pixels.Length, pinned: true);
        image.Pixels.CopyTo(pixels, 0);
        var reader = new BarcodeReader { Formats = format, MaxNumberOfSymbols = 1 };
        return new(
            () =>
            {
                var barcodes = reader.From(new ImageView(pixels, image.Width, image.Height, ImageFormat.Lum));
                var fold = barcodes.Length > 0 ? Fold(barcodes[0].Text) : 0;
                foreach (var barcode in barcodes)
                    barcode.Dispose();
                return fold;
            },
            () =>
            {
                var barcodes = reader.From(new ImageView(pixels, image.Width, image.Height, ImageFormat.Lum));
                try
                {
                    return barcodes.Length > 0 && barcodes[0].IsValid ? $"\"status\":\"ok\",\"text\":\"{Convert.ToHexString(Encoding.UTF8.GetBytes(barcodes[0].Text))}\"" : Failed;
                }
                finally
                {
                    foreach (var barcode in barcodes)
                        barcode.Dispose();
                }
            });
    }

    private static Operation Encode(string text, BarcodeFormat format, string options)
    {
        var creator = new CreatorOptions(format, options);
        return new(
            () =>
            {
                using var barcode = new Barcode(text, creator);
                // The package exposes no module matrix, so the fold reads what it does expose. A call into native code is never dropped.
                return barcode.IsValid ? (ulong)(int)barcode.Format + 1 : 0;
            },
            () =>
            {
                try
                {
                    using var barcode = new Barcode(text, creator);
                    using var writer = new WriterOptions { Scale = 1, AddQuietZones = false };
                    using var image = barcode.ToImage(writer);
                    var pixels = image.ToArray();
                    var json = new StringBuilder($"\"status\":\"ok\",\"matrix\":{{\"width\":{image.Width},\"height\":{image.Height},\"rows\":[");
                    for (var row = 0; row < image.Height; row++)
                    {
                        json.Append(row == 0 ? "\"" : ",\"");
                        for (var col = 0; col < image.Width; col++)
                            json.Append(pixels[row * image.Width + col] < 128 ? '1' : '0');
                        json.Append('"');
                    }
                    return json.Append("]}").ToString();
                }
                catch (Exception)
                {
                    // The package reports a refused symbol with a plain Exception.
                    return Failed;
                }
            });
    }

    private static ulong Fold(string text) => text is { Length: > 0 } ? (ulong)text.Length * 31 + text[^1] : 0;

    // libzint's numbering, which the writer hands to libzint as it is: the Standard QR version, M1 to M4 as 1 to 4, or an rMQR size's index in libzint's order.
    private static readonly string[] RmQRSizes =
    [
        "R7x43", "R7x59", "R7x77", "R7x99", "R7x139", "R9x43", "R9x59", "R9x77", "R9x99", "R9x139", "R11x27",
        "R11x43", "R11x59", "R11x77", "R11x99", "R11x139", "R13x27", "R13x43", "R13x59", "R13x77", "R13x99", "R13x139",
        "R15x43", "R15x59", "R15x77", "R15x99", "R15x139", "R17x43", "R17x59", "R17x77", "R17x99", "R17x139",
    ];

    private static int ZintVersion(string symbology, string version) => symbology switch
    {
        "qr" => int.Parse(version),
        "microqr" when version is ['M', var digit] => digit - '0',
        "rmqr" when Array.IndexOf(RmQRSizes, version) is var index and >= 0 => index + 1,
        _ => throw new ArgumentException($"unknown {symbology} version {version}"),
    };
}
