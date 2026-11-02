using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// Symbols carrying a stream the test wrote field by field, for content no generator writes (an ECI assignment without a charset, bytes that are no text), and their images.
/// Every symbol is the core only, row-major, one byte per module.
/// </summary>
internal static class PayloadSymbols
{
    public static byte[] StandardQr(int version, QREccLevel ecc, params (int Value, int Bits)[] fields)
        => KanjiSymbolBuilder.StandardQr(DataCodewords(QRCodeConstants.GetEccInfo(version, ecc).TotalDataCodewords, 4, fields), version, ecc, mask: 0);

    public static byte[] MicroQrM4(MicroQREccLevel ecc, params (int Value, int Bits)[] fields)
        => KanjiSymbolBuilder.MicroQr(DataCodewords(MicroQRConstants.GetDataCodewordCount(MicroQRVersion.M4, ecc), 9, fields), MicroQRVersion.M4, ecc, mask: 0);

    public static byte[] RmQr(RmQRVersion version, RmQREccLevel ecc, params (int Value, int Bits)[] fields)
        => KanjiSymbolBuilder.RmQr(DataCodewords(RmQRConstants.GetDataCodewordCount(version, ecc), 3, fields), version, ecc);

    /// <summary>The fields, the terminator as far as room allows, zero bits to the byte, then the pad codewords 0xEC and 0x11 in turn.</summary>
    public static byte[] DataCodewords(int dataCodewords, int terminatorBits, params (int Value, int Bits)[] fields)
    {
        var buffer = new byte[dataCodewords + 8];
        var writer = new BitWriter(buffer);
        var bits = 0;
        foreach (var (value, count) in fields)
        {
            writer.Write(value, count);
            bits += count;
        }
        var terminator = Math.Min(terminatorBits, dataCodewords * 8 - bits);
        if (terminator > 0)
            writer.Write(0, terminator);
        writer.Flush();

        var data = new byte[dataCodewords];
        var written = writer.GetData();
        written.Slice(0, Math.Min(written.Length, dataCodewords)).CopyTo(data);
        for (var i = written.Length; i < dataCodewords; i++)
            data[i] = (i - written.Length) % 2 == 0 ? (byte)0xEC : (byte)0x11;
        return data;
    }

    /// <summary>A core matrix drawn crisp at <paramref name="pixelsPerModule"/> inside a light quiet zone: 0 dark, 255 light.</summary>
    public static (byte[] Luminance, int Width, int Height) Render(byte[] modules, int columns, int rows, int pixelsPerModule = 4, int quietZone = 4)
    {
        var width = (columns + 2 * quietZone) * pixelsPerModule;
        var height = (rows + 2 * quietZone) * pixelsPerModule;
        var luminance = new byte[width * height];
        luminance.AsSpan().Fill(255);
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                if (modules[y * columns + x] == 0)
                    continue;
                for (var dy = 0; dy < pixelsPerModule; dy++)
                    luminance.AsSpan(((y + quietZone) * pixelsPerModule + dy) * width + (x + quietZone) * pixelsPerModule, pixelsPerModule).Clear();
            }
        }
        return (luminance, width, height);
    }

    /// <summary><see cref="Render"/> as a bitmap.</summary>
    public static SKBitmap Bitmap(byte[] modules, int columns, int rows, int pixelsPerModule = 4, int quietZone = 4)
    {
        var (luminance, width, height) = Render(modules, columns, rows, pixelsPerModule, quietZone);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = luminance[y * width + x];
                bitmap.SetPixel(x, y, new SKColor(v, v, v));
            }
        }
        return bitmap;
    }
}
