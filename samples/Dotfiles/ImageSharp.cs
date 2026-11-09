#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/FeatherQR/FeatherQR.csproj
#:package SixLabors.ImageSharp
using FeatherQR;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

// ImageSharp with the FeatherQR package: a symbol into an Image<Rgba32>, saved as PNG, JPEG and WebP, and each file read back to text.
// Run from the repository root: dotnet run samples/Dotfiles/ImageSharp.cs
// ImageSharp 3.2.0 and later look for a Six Labors license key when a project references it directly.
// Without one, this Debug run builds with a warning and a Release build fails. See https://sixlabors.com/pricing/.

const string url = "https://github.com/guitarrapc/FeatherQR";
var outputDirectory = Path.Combine(Environment.CurrentDirectory, "samples", "Dotfiles", "output", "imagesharp");
Directory.CreateDirectory(outputDirectory);
var failures = 0;

var qr = QRCodeGenerator.Create(url, QREccLevel.M);

// A symbol into an Image<Rgba32>. Rgba32 holds red, green, blue, alpha, the order of PixelLayout.Rgba8888.
using (var image = ToImage(qr, new SymbolImageOptions { ModulePixelSize = 8 }))
{
    Check("Image<Rgba32>", image);

    // The core writes PNG itself. JPEG and WebP come from ImageSharp.
    image.SaveAsPng(Path.Combine(outputDirectory, "qr.png"));
    image.SaveAsJpeg(Path.Combine(outputDirectory, "qr.jpg"));
    image.SaveAsWebp(Path.Combine(outputDirectory, "qr.webp"));
}

// Any file back to text. Image.Load<Rgba32> converts whatever the file holds to RGBA as it loads.
foreach (var name in new[] { "qr.png", "qr.jpg", "qr.webp" })
{
    using var loaded = Image.Load<Rgba32>(Path.Combine(outputDirectory, name));
    Check(name, loaded);
}

Console.WriteLine($"Images written to {outputDirectory}");
return failures == 0 ? 0 : 1;

void Check(string label, Image<Rgba32> image)
{
    var decoded = TryDecode(image, out var text) && text == url;
    Console.WriteLine($"{label,-16} {image.Width} x {image.Height} {(decoded ? "decodes" : "DOES NOT DECODE")}");
    if (!decoded)
        failures++;
}

static Image<Rgba32> ToImage(QRCodeData data, SymbolImageOptions options)
{
    var pixels = new byte[SymbolImageWriter.GetPixelBufferSize(data, PixelLayout.Rgba8888, options)];
    SymbolImageWriter.TryWritePixels(data, pixels, PixelLayout.Rgba8888, out var width, out var height, options);
    return Image.LoadPixelData<Rgba32>(pixels, width, height);
}

static bool TryDecode(Image<Rgba32> image, out string text)
{
    var (width, height) = (image.Width, image.Height);
    var pixels = new byte[width * height * 4];
    image.CopyPixelDataTo(pixels);

    var luminance = new byte[width * height];
    LuminanceConverter.Convert(pixels, width, height, width * 4, PixelLayout.Rgba8888, premultipliedAlpha: false, luminance);
    return QRCodeDecoder.TryDecodeImage(luminance, width, height, out text, out _);
}
