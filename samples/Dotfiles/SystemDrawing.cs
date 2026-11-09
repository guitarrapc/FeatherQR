#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0-windows
#:project ../../src/FeatherQR/FeatherQR.csproj
#:package System.Drawing.Common
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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
}

// Any Bitmap back to text, whatever its pixel format.
using (var png = new Bitmap(Path.Combine(outputDirectory, "qr.png")))
    Check($"PNG, {png.PixelFormat}", png);
using (var jpeg = new Bitmap(Path.Combine(outputDirectory, "qr.jpg")))
    Check($"JPEG, {jpeg.PixelFormat}", jpeg);

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

static Bitmap ToBitmap(QRCodeData data, SymbolImageOptions options)
{
    var pixels = new byte[SymbolImageWriter.GetPixelBufferSize(data, PixelLayout.Bgra8888, options)];
    SymbolImageWriter.TryWritePixels(data, pixels, PixelLayout.Bgra8888, out var width, out var height, options);

    var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
    var locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
    try
    {
        // TryWritePixels packs its rows. The bitmap's rows start Stride bytes apart.
        for (var row = 0; row < height; row++)
            Marshal.Copy(pixels, row * width * 4, locked.Scan0 + row * locked.Stride, width * 4);
    }
    finally
    {
        bitmap.UnlockBits(locked);
    }
    return bitmap;
}

static bool TryDecode(Bitmap bitmap, out string text)
{
    var (width, height) = (bitmap.Width, bitmap.Height);

    // Asking LockBits for Format32bppArgb converts a 24-bit or indexed bitmap on the way out.
    var locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var stride = locked.Stride;
    var pixels = new byte[stride * height];
    try
    {
        Marshal.Copy(locked.Scan0, pixels, 0, pixels.Length);
    }
    finally
    {
        bitmap.UnlockBits(locked);
    }

    var luminance = new byte[width * height];
    LuminanceConverter.Convert(pixels, width, height, stride, PixelLayout.Bgra8888, premultipliedAlpha: false, luminance);
    return QRCodeDecoder.TryDecodeImage(luminance, width, height, out text, out _);
}
