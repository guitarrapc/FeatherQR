using System.Text;

/// <summary>
/// One image's pixels to the luminance the decoders read, in the layout a caller holds them in. The image is Standard QR version 40
/// at 8 pixels a module, 1,480 pixels a side, made in setup, so only the conversion is measured.
///
///   Bgra8888         : four bytes with alpha, a 32-bit bitmap
///   Bgr888x          : four bytes whose fourth carries no colour
///   Bgr24            : three bytes, rows padded to four bytes as a 24-bit bitmap pads them, read as they are
///   Bgr24ViaBgra8888 : the same three-byte rows widened to four bytes first, the way to read them before Bgr24 existed
/// </summary>
[MemoryDiagnoser]
public class LuminanceConversion
{
    private int _width;
    private int _height;
    private int _rowBytes24;
    private byte[] _bgra = default!;
    private byte[] _bgrx = default!;
    private byte[] _bgr24 = default!;
    private byte[] _luminance = default!;

    [GlobalSetup]
    public void Setup()
    {
        var qr = QRCodeGenerator.Create(BuildDeterministicText(2900), QREccLevel.L);
        var options = new SymbolImageOptions { ModulePixelSize = 8, DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A) };
        (_width, _height) = SymbolImageWriter.GetImageSize(qr, options);
        _rowBytes24 = (_width * 3 + 3) & ~3;
        _bgra = new byte[_width * _height * 4];
        _bgrx = new byte[_width * _height * 4];
        _bgr24 = new byte[_rowBytes24 * _height];
        _luminance = new byte[_width * _height];
        SymbolImageWriter.TryWritePixels(qr, _bgra, PixelLayout.Bgra8888, out _, out _, options);
        SymbolImageWriter.TryWritePixels(qr, _bgrx, PixelLayout.Bgr888x, out _, out _, options);
        SymbolImageWriter.TryWritePixels(qr, _bgr24, _rowBytes24, PixelLayout.Bgr24, out _, out _, options);
    }

    [Benchmark(Baseline = true)]
    public byte Bgra8888()
    {
        LuminanceConverter.Convert(_bgra, _width, _height, _width * 4, PixelLayout.Bgra8888, premultipliedAlpha: false, _luminance);
        return _luminance[0];
    }

    [Benchmark]
    public byte Bgr888x()
    {
        LuminanceConverter.Convert(_bgrx, _width, _height, _width * 4, PixelLayout.Bgr888x, premultipliedAlpha: false, _luminance);
        return _luminance[0];
    }

    [Benchmark]
    public byte Bgr24()
    {
        LuminanceConverter.Convert(_bgr24, _width, _height, _rowBytes24, PixelLayout.Bgr24, premultipliedAlpha: false, _luminance);
        return _luminance[0];
    }

    [Benchmark]
    public byte Bgr24ViaBgra8888()
    {
        var widened = new byte[_width * _height * 4];
        for (var y = 0; y < _height; y++)
        {
            var row = _bgr24.AsSpan(y * _rowBytes24, _width * 3);
            var to = widened.AsSpan(y * _width * 4, _width * 4);
            for (var x = 0; x < _width; x++)
            {
                to[x * 4] = row[x * 3];
                to[x * 4 + 1] = row[x * 3 + 1];
                to[x * 4 + 2] = row[x * 3 + 2];
                to[x * 4 + 3] = 255;
            }
        }
        LuminanceConverter.Convert(widened, _width, _height, _width * 4, PixelLayout.Bgra8888, premultipliedAlpha: false, _luminance);
        return _luminance[0];
    }

    private static string BuildDeterministicText(int length)
    {
        var sb = new StringBuilder(length);
        var rng = new Random(42);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,:/?&=-_";
        for (var i = 0; i < length; i++)
        {
            sb.Append(alphabet[rng.Next(alphabet.Length)]);
        }
        return sb.ToString();
    }
}
