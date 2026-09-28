using SkiaSharp;

/// <summary>
/// End-to-end PNG image generation and image decoding through the public rMQR API.
/// RmQRCodeData is pre-generated in setup so the render scenarios cover the Skia render + PNG encode path only (letterboxed into a square canvas, as RmQRCodeImageBuilder does by default), and the decode scenarios cover the image detector (finder candidates → format → sub-finder anchored sampling → matrix decode) on clean 8 px/module renders.
/// The span decode variants must stay allocation-free; the bitmap ones go through string overloads and carry the result.
///
/// Scenarios:
///   R7x43_512px / R17x139_1024px : smallest / largest symbol rendered to PNG
///   R7x43_ImageDecode_Span       : smallest symbol, luminance span → text
///   R17x139_ImageDecode_Span     : largest symbol (2,363 modules), luminance span → text
///   R7x43_BitmapDecode / R17x139_BitmapDecode : the SKBitmap entry point, so the
///                                  grayscale conversion is inside the measurement
///   R17x139_Keystone15_Span      : 4 px/module, the top edge 15 % shorter, turned 23 degrees:
///                                  a sheared finder, read through its outline's frame and the traced perimeter
///   R11x77_Keystone5_Span        : 4 px/module, the top edge 5 % shorter, turned 17 degrees:
///                                  a mild keystone, read the same way
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
    private byte[] _strongKeystone = default!;
    private int _strongKeystoneSide;
    private byte[] _mildKeystone = default!;
    private int _mildKeystoneSide;
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

        var medium = RmQRCodeGenerator.Create("RMQR KEYSTONE 0123456789", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x77 });
        (_strongKeystone, _strongKeystoneSide) = Keystoned(_large, 0.15f, 23f);
        (_mildKeystone, _mildKeystoneSide) = Keystoned(medium, 0.05f, 17f);
        // A shape that stops decoding would go on being measured as a fast failure.
        if (!RmQRCodeDecoder.TryDecodeImage(_strongKeystone, _strongKeystoneSide, _strongKeystoneSide, _decodeBuffer, out _, out _)
            || !RmQRCodeDecoder.TryDecodeImage(_mildKeystone, _mildKeystoneSide, _mildKeystoneSide, _decodeBuffer, out _, out _))
        {
            throw new InvalidOperationException("a keystone shape no longer decodes");
        }

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

    [Benchmark]
    public bool R17x139_Keystone15_Span() => RmQRCodeDecoder.TryDecodeImage(_strongKeystone, _strongKeystoneSide, _strongKeystoneSide, _decodeBuffer, out _, out _);

    [Benchmark]
    public bool R11x77_Keystone5_Span() => RmQRCodeDecoder.TryDecodeImage(_mildKeystone, _mildKeystoneSide, _mildKeystoneSide, _decodeBuffer, out _, out _);

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

    /// <summary>A 4 px/module render with its top edge shortened by <paramref name="keystone"/> of the width, turned about the centre of a square canvas half as wide again, with a linear filter.</summary>
    private static (byte[] Luminance, int Side) Keystoned(RmQRCodeData data, float keystone, float degrees)
    {
        using var source = new RmQRCodeImageBuilder(data).WithModulePixelSize(4).ToBitmap();
        float width = source.Width;
        float height = source.Height;
        var side = (int)(width * 1.5f);
        var marginX = (side - width) / 2f;
        var marginY = (side - height) / 2f;
        var shrink = keystone * width / 2f;

        // The source rectangle's homography onto the quadrilateral (top-left, top-right, bottom-right, bottom-left)
        float x0 = marginX + shrink, y0 = marginY, x1 = marginX + width - shrink, y1 = marginY, x2 = marginX + width, y2 = marginY + height, x3 = marginX, y3 = marginY + height;
        var dx1 = x1 - x2;
        var dx2 = x3 - x2;
        var dx3 = x0 - x1 + x2 - x3;
        var dy1 = y1 - y2;
        var dy2 = y3 - y2;
        var dy3 = y0 - y1 + y2 - y3;
        var denominator = dx1 * dy2 - dx2 * dy1;
        var a13 = (dx3 * dy2 - dx2 * dy3) / denominator;
        var a23 = (dx1 * dy3 - dx3 * dy1) / denominator;
        var warp = new SKMatrix
        {
            ScaleX = (x1 - x0 + a13 * x1) / width,
            SkewX = (x3 - x0 + a23 * x3) / height,
            TransX = x0,
            SkewY = (y1 - y0 + a13 * y1) / width,
            ScaleY = (y3 - y0 + a23 * y3) / height,
            TransY = y0,
            Persp0 = a13 / width,
            Persp1 = a23 / height,
            Persp2 = 1f,
        };

        using var result = new SKBitmap(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(result))
        using (var image = SKImage.FromBitmap(source))
        {
            canvas.Clear(SKColors.White);
            canvas.SetMatrix(SKMatrix.CreateRotationDegrees(degrees, side / 2f, side / 2f).PreConcat(warp));
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            canvas.Flush();
        }
        var pixels = result.GetPixelSpan();
        var luminance = new byte[side * side];
        for (var i = 0; i < luminance.Length; i++)
            luminance[i] = pixels[i * 4];
        return (luminance, side);
    }
}
