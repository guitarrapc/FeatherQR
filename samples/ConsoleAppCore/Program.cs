// Images with FeatherQR package. SVG, PNG, raw pixels and text for a terminal.
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
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

// The README shows qr-navy-transparent.png, qr-styled.png and qr-icon.png at their pixel size, so they are 248 px like its other pictures.

// The colours. A fully transparent light colour leaves the background out.
var navyOnClear = new SymbolImageOptions
{
    Width = 248,
    Height = 248,
    DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A),
    LightColor = new SymbolColor(0, 0, 0, 0),
};
SaveText("qr-navy-transparent.svg", SymbolImageWriter.GetSvgString(qr, navyOnClear));
SaveBytes("qr-navy-transparent.png", SymbolImageWriter.GetPngBytes(qr, navyOnClear));

// An image of a set size. Every module is the same whole number of pixels, the largest that fits, with the rest light,
// and a side left at 0 follows the symbol's aspect. FillCanvas fills the canvas instead, as the image builders' WithSize does.
var fit = new SymbolImageOptions { Width = 512, Height = 512 };
SaveBytes("qr-512.png", SymbolImageWriter.GetPngBytes(qr, fit));
SaveBytes("qr-512-fill.png", SymbolImageWriter.GetPngBytes(qr, fit with { FillCanvas = true }));
SaveText("rmqr-1000-wide.svg", SymbolImageWriter.GetSvgString(rmqr, new SymbolImageOptions { Width = 1000 }));

// Module and finder pattern styles. The finder patterns keep their own shape, so a reader still finds them.
var styled = new SymbolImageOptions
{
    Width = 248,
    Height = 248,
    ModuleStyle = ModuleStyle.Circle,
    ModuleScalePercent = 90,
    FinderPatternStyle = FinderPatternStyle.RoundedSquareCircle,
    DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A),
};
SaveText("qr-styled.svg", SymbolImageWriter.GetSvgString(qr, styled));
SaveBytes("qr-styled.png", SymbolImageWriter.GetPngBytes(qr, styled));

// An icon area on a Standard QR code. The symbol relies on its error correction, so it is made at level H. The SVG embeds the image
// given, here the Micro QR code's own PNG in navy. The PNG and the pixels leave the area light.
var qrH = QRCodeGenerator.Create(url, QREccLevel.H);
var navy = new SymbolColor(0x1E, 0x3A, 0x8A);
var withIcon = new SymbolImageOptions { Width = 248, Height = 248, IconSizeModules = 7, IconImage = SymbolImageWriter.GetPngBytes(micro, new SymbolImageOptions { DarkColor = navy }) };
SaveText("qr-icon.svg", SymbolImageWriter.GetSvgString(qrH, withIcon));
SaveBytes("qr-icon-area.png", SymbolImageWriter.GetPngBytes(qrH, withIcon));

// Draw the icon into the pixels where GetIconRectangle says. Here the Micro QR code is written at that size straight into the area:
// the destination starts at the area's first pixel, and the row stride is the whole image's, so each row lands under the one before.
var (iconX, iconY, iconWidth, iconHeight) = SymbolImageWriter.GetIconRectangle(qrH, withIcon);
Console.WriteLine($"The icon goes at ({iconX}, {iconY}), {iconWidth} by {iconHeight} pixels");
var composed = new byte[SymbolImageWriter.GetPixelBufferSize(qrH, PixelLayout.Rgba8888, withIcon)];
SymbolImageWriter.TryWritePixels(qrH, composed, PixelLayout.Rgba8888, out var composedWidth, out var composedHeight, withIcon);
// The icon fills its area. With equal whole-pixel modules, 19 modules in 35 pixels would be 1 pixel each, 19 of the 35 pixels across.
var iconOptions = new SymbolImageOptions { Width = iconWidth, Height = iconHeight, DarkColor = navy, FillCanvas = true };
var iconStart = (iconY * composedWidth + iconX) * 4;
SymbolImageWriter.TryWritePixels(micro, composed.AsSpan(iconStart), composedWidth * 4, PixelLayout.Rgba8888, out _, out _, iconOptions);
SaveBytes("qr-icon.png", EncodeRgbaPng(composed, composedWidth, composedHeight));

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

// A camera frame in NV12, NV21 or I420 holds luminance in its Y plane, often with padded rows.
// Read the plane as Gray8 with the frame's row stride. The colour planes after it are never read.
var rowStride = width + 64;
var frame = new byte[rowStride * height * 3 / 2];
for (var row = 0; row < height; row++)
    gray.AsSpan(row * width, width).CopyTo(frame.AsSpan(row * rowStride));
var yPlane = new byte[width * height];
LuminanceConverter.Convert(frame, width, height, rowStride, PixelLayout.Gray8, premultipliedAlpha: false, yPlane);
QRCodeDecoder.TryDecodeImage(yPlane, width, height, out text, out _);
Console.WriteLine($"Y plane of an NV12 frame, row stride {rowStride}, decode as: {text}");

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

// The PNG writer starts from a symbol, so composed pixels need an encoder of their own.
// This is the smallest valid one: 8-bit RGBA, rows unfiltered, one zlib stream.
static byte[] EncodeRgbaPng(byte[] rgba, int width, int height)
{
    using var idat = new MemoryStream();
    using (var zlib = new ZLibStream(idat, CompressionLevel.SmallestSize, leaveOpen: true))
    {
        for (var row = 0; row < height; row++)
        {
            zlib.WriteByte(0); // filter type None
            zlib.Write(rgba, row * width * 4, width * 4);
        }
    }

    var ihdr = new byte[13];
    BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
    BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
    ihdr[8] = 8; // bit depth
    ihdr[9] = 6; // colour type RGBA

    using var png = new MemoryStream();
    png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
    WriteChunk(png, "IHDR"u8, ihdr);
    WriteChunk(png, "IDAT"u8, idat.ToArray());
    WriteChunk(png, "IEND"u8, []);
    return png.ToArray();
}

static void WriteChunk(Stream png, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
{
    Span<byte> word = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
    png.Write(word);
    png.Write(type);
    png.Write(data);
    BinaryPrimitives.WriteUInt32BigEndian(word, ~Crc32(Crc32(~0u, type), data));
    png.Write(word);
}

// The CRC-32 a PNG chunk ends with, a bit at a time.
static uint Crc32(uint crc, ReadOnlySpan<byte> bytes)
{
    foreach (var b in bytes)
    {
        crc ^= b;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
    }
    return crc;
}
