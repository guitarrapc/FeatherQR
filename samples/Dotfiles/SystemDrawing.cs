#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0-windows
// A Span<byte> over the memory LockBits hands out takes a pointer.
#:property AllowUnsafeBlocks=true
#:project ../../src/FeatherQR/FeatherQR.csproj
#:package System.Drawing.Common
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using FeatherQR;

// System.Drawing with the FeatherQR package: a symbol into a Bitmap, any Bitmap back to text, and a symbol drawn into a Graphics.
// Run from the repository root on Windows: dotnet run samples/Dotfiles/SystemDrawing.cs

const string url = "https://github.com/guitarrapc/FeatherQR";
var outputDirectory = Path.Combine(Environment.CurrentDirectory, "samples", "Dotfiles", "output", "systemdrawing");
Directory.CreateDirectory(outputDirectory);
var failures = 0;

var qr = QRCodeGenerator.Create(url, QREccLevel.M);

// A symbol into a Bitmap. Format32bppArgb keeps blue, green, red, alpha in memory, which is PixelLayout.Bgra8888.
using (var bitmap = ToBitmap(qr, new SymbolImageOptions { ModulePixelSize = 8 }))
{
    Check("Bitmap from the symbol", bitmap);
    bitmap.Save(Path.Combine(outputDirectory, "qr.png"), ImageFormat.Png);
    bitmap.Save(Path.Combine(outputDirectory, "qr.jpg"), ImageFormat.Jpeg);
    bitmap.Save(Path.Combine(outputDirectory, "qr.bmp"), ImageFormat.Bmp);
}

// Any Bitmap back to text, whatever its pixel format. Each line prints the format the file loaded as.
using (var png = new Bitmap(Path.Combine(outputDirectory, "qr.png")))
    Check($"PNG, {png.PixelFormat}", png);
using (var jpeg = new Bitmap(Path.Combine(outputDirectory, "qr.jpg")))
    Check($"JPEG, {jpeg.PixelFormat}", jpeg);
using (var bmp = new Bitmap(Path.Combine(outputDirectory, "qr.bmp")))
    Check($"BMP, {bmp.PixelFormat}", bmp);

// A symbol drawn into a Graphics at a larger scale, as a WinForms OnPaint or a printed page does.
// The default interpolation blurs the module edges, and NearestNeighbor alone moves them half a pixel.
// With both settings below, the drawn symbol matches the one written at 8 pixels a module, pixel for pixel.
using (var symbol = ToBitmap(qr, new SymbolImageOptions { ModulePixelSize = 1 }))
using (var page = new Bitmap(360, 400, PixelFormat.Format32bppArgb))
using (var graphics = Graphics.FromImage(page))
using (var font = new Font(FontFamily.GenericSansSerif, 12))
{
    graphics.Clear(Color.White);
    graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
    graphics.PixelOffsetMode = PixelOffsetMode.Half;
    graphics.DrawImage(symbol, new Rectangle(20, 20, symbol.Width * 8, symbol.Height * 8));
    graphics.DrawString("FeatherQR", font, Brushes.Black, 20, 20 + symbol.Height * 8 + 8);
    Check("Drawn into a Graphics", page);
    page.Save(Path.Combine(outputDirectory, "qr-page.png"), ImageFormat.Png);
}

Console.WriteLine($"Images written to {outputDirectory}");
return failures == 0 ? 0 : 1;

void Check(string label, Bitmap bitmap)
{
    var decoded = TryDecode(bitmap, out var text) && text == url;
    Console.WriteLine($"{label,-32} {bitmap.Width} x {bitmap.Height} {(decoded ? "decodes" : "DOES NOT DECODE")}");
    if (!decoded)
        failures++;
}

static unsafe Bitmap ToBitmap(QRCodeData data, SymbolImageOptions options)
{
    // The size comes first, so the symbol is written straight into the bitmap with no buffer between.
    var (width, height) = SymbolImageWriter.GetImageSize(data, options);
    var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
    var locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
    try
    {
        // The bitmap's rows start Stride bytes apart.
        var memory = new Span<byte>((void*)locked.Scan0, locked.Stride * height);
        SymbolImageWriter.TryWritePixels(data, memory, locked.Stride, PixelLayout.Bgra8888, out _, out _, options);
    }
    finally
    {
        bitmap.UnlockBits(locked);
    }
    return bitmap;
}

static unsafe bool TryDecode(Bitmap bitmap, out string text)
{
    var (width, height) = (bitmap.Width, bitmap.Height);

    // These formats are read as the bitmap holds them. Format24bppRgb keeps blue, green, red in memory despite its name.
    // For any other format, an indexed one for example, LockBits converts to Format32bppArgb on the way out.
    var (format, layout) = bitmap.PixelFormat switch
    {
        PixelFormat.Format24bppRgb => (PixelFormat.Format24bppRgb, PixelLayout.Bgr24),
        PixelFormat.Format32bppRgb => (PixelFormat.Format32bppRgb, PixelLayout.Bgra8888),
        _ => (PixelFormat.Format32bppArgb, PixelLayout.Bgra8888),
    };
    var luminance = new byte[width * height];
    var locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, format);
    try
    {
        // A 24-bit row is padded to four bytes, and the stride says where the next row starts.
        var pixels = new ReadOnlySpan<byte>((void*)locked.Scan0, locked.Stride * height);
        LuminanceConverter.Convert(pixels, width, height, locked.Stride, layout, premultipliedAlpha: false, luminance);
    }
    finally
    {
        bitmap.UnlockBits(locked);
    }
    return QRCodeDecoder.TryDecodeImage(luminance, width, height, out text, out _);
}
