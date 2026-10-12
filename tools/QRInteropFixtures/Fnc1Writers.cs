using System.Reflection;
using ZXing.Common;
using ZXingCpp;
using GlyphQrCode = CodeGlyphX.QrCode;
using GlyphRmQrCode = CodeGlyphX.RmQrCode;
using NetQRCode = ZXing.QrCode.Internal.QRCode;
using NetReader = ZXing.QrCode.QRCodeReader;

namespace QRInteropFixtures;

/// <summary>A symbol as its core module matrix: one byte per module (1 dark), row-major, no quiet zone.</summary>
public sealed record ModuleMatrix(byte[] Modules, int Width, int Height);

/// <summary>
/// What the FNC1 generators, their sanity gate and <c>probe-fnc1</c> share: each writer's output as a
/// module matrix, a clean render of it, and the two pinned readers over that render.
/// </summary>
public static class Fnc1Writers
{
    /// <summary>libzint's symbol at scale 1 without quiet zones, which is module-exact.</summary>
    public static ModuleMatrix FromZint(Barcode barcode)
    {
        using var image = barcode.ToImage(new WriterOptions { Scale = 1, AddQuietZones = false });
        var pixels = image.ToArray();
        var modules = new byte[image.Width * image.Height];
        for (var i = 0; i < modules.Length; i++)
            modules[i] = pixels[i] < 128 ? (byte)1 : (byte)0; // luminance: dark < 128 -> module = 1
        return new ModuleMatrix(modules, image.Width, image.Height);
    }

    public static ModuleMatrix FromZXingNet(NetQRCode qr)
    {
        var matrix = qr.Matrix;
        var rows = matrix.Array;
        var modules = new byte[matrix.Width * matrix.Height];
        for (var row = 0; row < matrix.Height; row++)
        {
            for (var col = 0; col < matrix.Width; col++)
                modules[row * matrix.Width + col] = rows[row][col] != 0 ? (byte)1 : (byte)0;
        }
        return new ModuleMatrix(modules, matrix.Width, matrix.Height);
    }

    public static ModuleMatrix FromCodeGlyphX(GlyphQrCode code)
    {
        var size = code.Size;
        var modules = new byte[size * size];
        for (var row = 0; row < size; row++)
        {
            for (var col = 0; col < size; col++)
                modules[row * size + col] = code.Modules[col, row] ? (byte)1 : (byte)0;
        }
        return new ModuleMatrix(modules, size, size);
    }

    public static ModuleMatrix FromCodeGlyphX(GlyphRmQrCode code)
    {
        var modules = new byte[code.Width * code.Height];
        for (var row = 0; row < code.Height; row++)
        {
            for (var col = 0; col < code.Width; col++)
                modules[row * code.Width + col] = code.Modules[col, row] ? (byte)1 : (byte)0;
        }
        return new ModuleMatrix(modules, code.Width, code.Height);
    }

    /// <summary>A clean luminance render: 0 dark, 255 light, a light quiet zone.</summary>
    public static (byte[] Luminance, int Width, int Height) Render(ModuleMatrix matrix, int quietZone, int pixelsPerModule)
    {
        var width = (matrix.Width + 2 * quietZone) * pixelsPerModule;
        var height = (matrix.Height + 2 * quietZone) * pixelsPerModule;
        var luminance = new byte[width * height];
        luminance.AsSpan().Fill(255);
        for (var row = 0; row < matrix.Height; row++)
        {
            for (var col = 0; col < matrix.Width; col++)
            {
                if (matrix.Modules[row * matrix.Width + col] == 0)
                    continue;
                var pixelRow = (quietZone + row) * pixelsPerModule;
                var pixelCol = (quietZone + col) * pixelsPerModule;
                for (var y = 0; y < pixelsPerModule; y++)
                    luminance.AsSpan((pixelRow + y) * width + pixelCol, pixelsPerModule).Clear();
            }
        }
        return (luminance, width, height);
    }

    /// <summary>
    /// zxing-cpp over a clean render, in <see cref="TextMode.Plain"/>: the default (HRI) mode re-formats GS1 data
    /// as <c>(AI)</c> text, and Plain returns the element string with GS as 0x1D, the form the fixtures hold.
    /// </summary>
    public static Barcode[] ReadCpp(ModuleMatrix matrix, BarcodeFormat format, int quietZone, int pixelsPerModule = 4)
    {
        var (luminance, width, height) = Render(matrix, quietZone, pixelsPerModule);
        var reader = new BarcodeReader { Formats = format, TryHarder = true, TextMode = TextMode.Plain };
        return reader.From(new ImageView(luminance, width, height, ImageFormat.Lum));
    }

    /// <summary>
    /// ZXing.Net's Standard QR reader over the same kind of render. With <paramref name="pureBarcode"/> it skips its
    /// detector and samples the render as an axis-aligned symbol, which tells a symbol its detector does not find
    /// from one its decoder cannot read.
    /// </summary>
    public static ZXing.Result? ReadNet(ModuleMatrix matrix, int quietZone = FixtureWriter.QuietZoneModules, int pixelsPerModule = 4, bool pureBarcode = false)
    {
        var (luminance, width, height) = Render(matrix, quietZone, pixelsPerModule);
        var bitmap = new ZXing.BinaryBitmap(new HybridBinarizer(new ZXing.RGBLuminanceSource(luminance, width, height, ZXing.RGBLuminanceSource.BitmapFormat.Gray8)));
        return pureBarcode
            ? new NetReader().decode(bitmap, new Dictionary<ZXing.DecodeHintType, object> { [ZXing.DecodeHintType.PURE_BARCODE] = true })
            : new NetReader().decode(bitmap);
    }

    /// <summary>Text for a console column: the separator as <c>&lt;GS&gt;</c>, a line feed as <c>\n</c>.</summary>
    public static string Escape(string text) => text.Replace("\u001D", "<GS>").Replace("\n", "\\n");

    /// <summary>
    /// The application indicator as a reader transmits it before the data (ISO/IEC 18004 6.4.8.2): two digits for
    /// the codewords 0-99, one letter for 165-190 (<c>A</c>-<c>Z</c>) and 197-222 (<c>a</c>-<c>z</c>).
    /// </summary>
    public static string FormatIndicator(int codeword) => codeword < 100 ? codeword.ToString("D2") : ((char)(codeword - 100)).ToString();

    public static string ZintVersion => $"ZXingCpp {typeof(BarcodeCreator).Assembly.GetName().Version?.ToString(3) ?? "unknown"} bundled libzint";

    public static string ZXingNetVersion => InformationalVersion(typeof(NetQRCode).Assembly);

    public static string CodeGlyphXVersion => InformationalVersion(typeof(GlyphQrCode).Assembly);

    private static string InformationalVersion(Assembly assembly)
        => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
}
