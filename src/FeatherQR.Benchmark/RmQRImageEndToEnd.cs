using SkiaSharp;

/// <summary>
/// End-to-end PNG image generation and image decoding through the public rMQR API.
/// RmQRCodeData is pre-generated in setup so the render scenarios cover the Skia
/// render + PNG encode path only (letterboxed into a square canvas, as
/// RmQRCodeImageBuilder does by default), and the decode scenarios cover the image
/// detector (finder candidates → format → sub-finder anchored sampling → matrix
/// decode) on clean 8 px/module renders. The span decode variants must stay
/// allocation-free; the bitmap ones go through string overloads and carry the result.
///
/// Scenarios:
///   R7x43_512px / R17x139_1024px : smallest / largest symbol rendered to PNG
///   R7x43_ImageDecode_Span       : smallest symbol, luminance span → text
///   R17x139_ImageDecode_Span     : largest symbol (2,363 modules), luminance span → text
///   R7x43_BitmapDecode / R17x139_BitmapDecode : the SKBitmap entry point, so the
///                                  grayscale conversion is inside the measurement
///   NoSymbol_Noise / NoSymbol_Gradient : the failure path (both polarities, NotDetected)
/// </summary>
public class RmQRImageEndToEnd
{
    private RmQRCodeData _small = default!;
    private RmQRCodeData _large = default!;
    private SKBitmap _smallBitmap = default!;
    private SKBitmap _largeBitmap = default!;
    private byte[] _noise = default!;
    private byte[] _gradient = default!;
    private byte[] _smallLuminance = default!;
    private byte[] _largeLuminance = default!;
    private (int Width, int Height) _smallImage;
    private (int Width, int Height) _largeImage;
    private char[] _decodeBuffer = default!;

    [GlobalSetup]
    public void Setup()
    {
        _small = RmQRCodeGenerator.Create("RMQR 43", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 });
        _large = RmQRCodeGenerator.Create(
            string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog?! ", 4)).Substring(0, 150),
            RmQREccLevel.M,
            new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 });

        _smallBitmap = new RmQRCodeImageBuilder(_small).WithModulePixelSize(8).ToBitmap();
        _largeBitmap = new RmQRCodeImageBuilder(_large).WithModulePixelSize(8).ToBitmap();
        (_smallLuminance, _smallImage) = Luminance(_small);
        (_largeLuminance, _largeImage) = Luminance(_large);
        _decodeBuffer = new char[RmQRCodeDecoder.GetMaxDecodedLength(RmQRVersion.R17x139)];

        _noise = new byte[1144 * 168];
        _gradient = new byte[1144 * 168];
        var state = 12345u;
        for (var i = 0; i < _noise.Length; i++)
        {
            state = state * 1664525u + 1013904223u;
            _noise[i] = (byte)((state >> 16) % 251 < 60 ? 0 : 255);
            _gradient[i] = (byte)(i % 1144 * 255 / 1144 ^ (state >> 20 & 7));
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        // The two bitmaps are the only native Skia handles this class holds in fields.
        _smallBitmap.Dispose();
        _largeBitmap.Dispose();
    }

    [Benchmark]
    public byte[] R7x43_512px() => RmQRCodeImageBuilder.GetPngBytes(_small, 512);

    [Benchmark]
    public byte[] R17x139_1024px() => RmQRCodeImageBuilder.GetPngBytes(_large, 1024);

    [Benchmark]
    public bool R7x43_ImageDecode_Span() => RmQRCodeDecoder.TryDecodeImage(_smallLuminance, _smallImage.Width, _smallImage.Height, _decodeBuffer, out _, out _);

    [Benchmark]
    public bool R17x139_ImageDecode_Span() => RmQRCodeDecoder.TryDecodeImage(_largeLuminance, _largeImage.Width, _largeImage.Height, _decodeBuffer, out _, out _);

    // Bitmap variants: the same symbols through the SKBitmap entry point, so the
    // grayscale conversion is part of the measurement (the span variants above start
    // from luminance and skip it). Only string overloads exist for bitmaps, so these
    // carry the decoded string's allocation.
    [Benchmark]
    public bool R7x43_BitmapDecode() => RmQRCodeDecoder.TryDecode(_smallBitmap, out _, out _);

    [Benchmark]
    public bool R17x139_BitmapDecode() => RmQRCodeDecoder.TryDecode(_largeBitmap, out _, out _);

    // Failure path: no symbol present, so detection runs the whole pipeline for both
    // reflectance polarities and reports NotDetected. Salt-and-pepper is the adversarial
    // shape (false finder candidates); the gradient is the ordinary photo-ish shape.
    [Benchmark]
    public bool NoSymbol_Noise_1144x168() => RmQRCodeDecoder.TryDecodeImage(_noise, 1144, 168, _decodeBuffer, out _, out _);

    [Benchmark]
    public bool NoSymbol_Gradient_1144x168() => RmQRCodeDecoder.TryDecodeImage(_gradient, 1144, 168, _decodeBuffer, out _, out _);

    private static (byte[] luminance, (int Width, int Height) size) Luminance(RmQRCodeData data)
    {
        // Grayscale of an 8px/module render (2-module quiet zone) for the image-decode scenarios
        using var bitmap = new RmQRCodeImageBuilder(data).WithModulePixelSize(8).ToBitmap();
        var luminance = new byte[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                luminance[y * bitmap.Width + x] = bitmap.GetPixel(x, y).Red;
            }
        }
        return (luminance, (bitmap.Width, bitmap.Height));
    }
}
