// Images with FeatherQR package. SVG, PNG, raw pixels and text for a terminal.
using System.Buffers;
using System.Text;
using FeatherQR;

Console.OutputEncoding = System.Text.Encoding.UTF8;

static string ParseOutputDir(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--output-dir")
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException("--output-dir requires a path");
            return Path.GetFullPath(args[i + 1]);
        }

        const string prefix = "--output-dir=";
        if (args[i].StartsWith(prefix, StringComparison.Ordinal))
            return Path.GetFullPath(args[i][prefix.Length..]);
    }

    // Default: ./samples relative to cwd (gallery when cwd is the project directory).
    return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "samples"));
}

var outputDir = ParseOutputDir(args);

const string url = "https://github.com/guitarrapc/FeatherQR";

Directory.CreateDirectory(outputDir);

var qr = QRCodeGenerator.Create(url, QREccLevel.M);
var micro = MicroQRCodeGenerator.Create("FeatherQR", MicroQREccLevel.L);
var rmqr = RmQRCodeGenerator.Create(url, RmQREccLevel.M);

// SVG and PNG with the defaults: 8 pixels a module, black on white, the quiet zone the symbol was made with.
SaveText("qr.svg", SymbolImageWriter.GetSvgString(qr));
SaveBytes("qr.png", SymbolImageWriter.GetPngBytes(qr));
SaveText("microqr.svg", SymbolImageWriter.GetSvgString(micro));
SaveBytes("microqr.png", SymbolImageWriter.GetPngBytes(micro));
SaveText("rmqr.svg", SymbolImageWriter.GetSvgString(rmqr));
SaveBytes("rmqr.png", SymbolImageWriter.GetPngBytes(rmqr));

// The module size and the colours. A fully transparent light colour leaves the background out.
var navyOnClear = new SymbolImageOptions
{
    ModulePixelSize = 12,
    DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A),
    LightColor = new SymbolColor(0, 0, 0, 0),
};
SaveText("qr-navy-transparent.svg", SymbolImageWriter.GetSvgString(qr, navyOnClear));
SaveBytes("qr-navy-transparent.png", SymbolImageWriter.GetPngBytes(qr, navyOnClear));

// An image of a set size. The symbol fills the canvas as the image builders' WithSize does, every module still whole pixels,
// and a side left at 0 follows the symbol's aspect.
var fit = new SymbolImageOptions { Width = 512, Height = 512 };
SaveBytes("qr-512.png", SymbolImageWriter.GetPngBytes(qr, fit));
SaveText("rmqr-1000-wide.svg", SymbolImageWriter.GetSvgString(rmqr, new SymbolImageOptions { Width = 1000 }));

// Module and finder pattern styles. The finder patterns keep their own shape, so a reader still finds them.
var styled = new SymbolImageOptions
{
    ModulePixelSize = 12,
    ModuleStyle = ModuleStyle.Circle,
    ModuleScalePercent = 90,
    FinderPatternStyle = FinderPatternStyle.RoundedSquareCircle,
    DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A),
};
SaveText("qr-styled.svg", SymbolImageWriter.GetSvgString(qr, styled));
SaveBytes("qr-styled.png", SymbolImageWriter.GetPngBytes(qr, styled));

// An icon area on a Standard QR code. The symbol relies on its error correction, so it is made at level H. The SVG embeds the image
// given, here the Micro QR code's own PNG, and for the PNG and the pixels GetIconRectangle says where to draw it.
var qrH = QRCodeGenerator.Create(url, QREccLevel.H);
var withIcon = new SymbolImageOptions { IconSizeModules = 7, IconImage = SymbolImageWriter.GetPngBytes(micro) };
SaveText("qr-icon.svg", SymbolImageWriter.GetSvgString(qrH, withIcon));
SaveBytes("qr-icon-area.png", SymbolImageWriter.GetPngBytes(qrH, withIcon));
var (iconX, iconY, iconWidth, iconHeight) = SymbolImageWriter.GetIconRectangle(qrH, withIcon);
Console.WriteLine($"The icon goes at ({iconX}, {iconY}), {iconWidth} by {iconHeight} pixels");

// Into an IBufferWriter<byte>, such as a pipe or a pooled buffer, with no allocation of its own.
var writer = new ArrayBufferWriter<byte>();
var written = SymbolImageWriter.WritePng(qr, writer);
File.WriteAllBytes(Path.Combine(outputDir, "qr-from-writer.png"), writer.WrittenSpan);
Console.WriteLine($"WritePng wrote {written} bytes into the buffer writer");

// Raw pixels into a buffer you own. Gray8 is the luminance the decoders read, so it decodes as it is.
var gray = new byte[SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Gray8)];
SymbolImageWriter.TryWritePixels(qr, gray, PixelLayout.Gray8, out var width, out var height);
QRCodeDecoder.TryDecodeImage(gray, width, height, out var text, out _);
Console.WriteLine($"Gray8 pixels, {width} x {height}, decode as: {text}");

// BGRA for a GPU texture or a platform bitmap: 4 bytes a pixel, rows packed.
var bgra = new byte[SymbolImageWriter.GetPixelBufferSize(micro, PixelLayout.Bgra8888)];
SymbolImageWriter.TryWritePixels(micro, bgra, PixelLayout.Bgra8888, out width, out height);
Console.WriteLine($"Bgra8888 pixels, {width} x {height}, {bgra.Length} bytes");

// Text for a terminal: dark modules drawn for a light background, light modules drawn for a dark one.
Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine();
Console.WriteLine("Micro QR for a light background:");
Console.Write(SymbolImageWriter.GetTerminalText(micro));
Console.WriteLine("Micro QR for a dark background:");
Console.Write(SymbolImageWriter.GetTerminalText(micro, invert: true));

Console.WriteLine();
Console.WriteLine($"Images written to {outputDir}");

void SaveText(string name, string svg)
{
    File.WriteAllText(Path.Combine(outputDir, name), svg);
    Report(name);
}

void SaveBytes(string name, byte[] png)
{
    File.WriteAllBytes(Path.Combine(outputDir, name), png);
    Report(name);
}

void Report(string name) => Console.WriteLine($"{name,-26} {new FileInfo(Path.Combine(outputDir, name)).Length,7:N0} bytes");
