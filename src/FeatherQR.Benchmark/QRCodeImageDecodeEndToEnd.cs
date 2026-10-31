using SkiaSharp;

/// <summary>
/// Measures Standard QR image decoding on large symbols, a small reference symbol and symbol-free images.
/// </summary>
/// <remarks>
/// Whole and fractional module pitches, rotation, blur, low contrast, noise and perspective separate the detector's cost profiles.
/// Whole-pixel renders alone favor the histogram's uniform-group shortcut and are not representative of image decoding.
/// Luminance-span rows must remain allocation-free. Bitmap rows include RGBA conversion.
/// </remarks>
public class QRCodeImageDecodeEndToEnd
{
    private static readonly string[] shapeKeys =
    [
        "v40-3px", "v40-3.4px", "v40-4px-rot17", "v40-4px-soft", "v25-4px-keystone15", "v6-4px", "none-noise", "none-gradient",
    ];

    private SKBitmap _bitmap = default!;
    private byte[] _luminance = default!;
    private int _side;
    private char[] _chars = default!;

    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = default!;

    public static IEnumerable<string> Shapes() => shapeKeys;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var large = QRCodeGenerator.Create(DeterministicText(2900).AsSpan(), QREccLevel.L, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(40) });
        var small = QRCodeGenerator.Create("https://github.com/guitarrapc/FeatherQR".AsSpan(), QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(6) });
        var medium = QRCodeGenerator.Create(DeterministicText(400).AsSpan(), QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(25) });

        _bitmap = Shape switch
        {
            "v40-3px" => Render(large, large.Size * 3),
            "v40-3.4px" => Render(large, (int)MathF.Round(large.Size * 3.4f)),
            "v40-4px-rot17" => Rotate(Render(large, large.Size * 4), 17f),
            "v40-4px-soft" => Soften(Render(large, large.Size * 4)),
            "v25-4px-keystone15" => Keystone(Render(medium, medium.Size * 4), 0.15f, 23f),
            "v6-4px" => Render(small, small.Size * 4),
            "none-noise" => NoSymbol(740, noise: true),
            "none-gradient" => NoSymbol(740, noise: false),
            _ => throw new ArgumentOutOfRangeException(nameof(Shape), Shape, "unknown shape"),
        };
        _side = _bitmap.Width;
        _luminance = Luminance(_bitmap);
        _chars = new char[QRCodeDecoder.GetMaxDecodedLength(40)];

        // A shape that stops decoding would go on being measured as a fast failure.
        var decoded = QRCodeDecoder.TryDecodeImage(_luminance, _side, _side, _chars, out _, out var info);
        if (decoded == Shape.StartsWith("none-", StringComparison.Ordinal))
            throw new InvalidOperationException($"shape {Shape}: decoded = {decoded}, status {info.Status}");
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _bitmap.Dispose();

    [Benchmark(Baseline = true, Description = "Luminance (Span)")]
    public bool Luminance_Span() => QRCodeDecoder.TryDecodeImage(_luminance, _side, _side, _chars, out _, out _);

    [Benchmark(Description = "Bitmap")]
    public bool Bitmap() => QRCodeDecoder.TryDecode(_bitmap, out _);

    private static SKBitmap Render(QRCodeData qr, int side)
    {
        var bitmap = new SKBitmap(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        SymbolRenderer.Render(canvas, SKRect.Create(0, 0, side, side), qr, SKColors.Black, SKColors.White);
        canvas.Flush();
        return bitmap;
    }

    private static SKBitmap Rotate(SKBitmap source, float degrees)
    {
        using (source)
        {
            var radians = degrees * MathF.PI / 180f;
            var side = (int)MathF.Ceiling(source.Width * (MathF.Abs(MathF.Cos(radians)) + MathF.Abs(MathF.Sin(radians))));
            var rotated = new SKBitmap(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(rotated);
            using var image = SKImage.FromBitmap(source);
            canvas.Clear(SKColors.White);
            canvas.Translate(side / 2f, side / 2f);
            canvas.RotateDegrees(degrees);
            canvas.DrawImage(image, -source.Width / 2f, -source.Height / 2f, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            canvas.Flush();
            return rotated;
        }
    }

    /// <summary>The top edge shortened by <paramref name="keystone"/> of the width, then turned about the centre of a canvas half as wide again.</summary>
    private static SKBitmap Keystone(SKBitmap source, float keystone, float degrees)
    {
        using (source)
        {
            var width = source.Width;
            var side = (int)(width * 1.5f);
            var margin = (side - width) / 2f;
            var shrink = keystone * width / 2f;

            // The unit square's homography onto the quadrilateral (top-left, top-right, bottom-right, bottom-left)
            float x0 = margin + shrink, y0 = margin, x1 = margin + width - shrink, y1 = margin, x2 = margin + width, y2 = margin + width, x3 = margin, y3 = margin + width;
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
                SkewX = (x3 - x0 + a23 * x3) / width,
                TransX = x0,
                SkewY = (y1 - y0 + a13 * y1) / width,
                ScaleY = (y3 - y0 + a23 * y3) / width,
                TransY = y0,
                Persp0 = a13 / width,
                Persp1 = a23 / width,
                Persp2 = 1f,
            };

            var result = new SKBitmap(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(result);
            using var image = SKImage.FromBitmap(source);
            canvas.Clear(SKColors.White);
            canvas.SetMatrix(SKMatrix.CreateRotationDegrees(degrees, side / 2f, side / 2f).PreConcat(warp));
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            canvas.Flush();
            return result;
        }
    }

    private static SKBitmap Soften(SKBitmap source)
    {
        using (source)
        {
            var side = source.Width;
            var crisp = Luminance(source);
            var soft = new SKBitmap(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
            var pixels = soft.GetPixelSpan();
            var state = 12345u;
            for (var y = 0; y < side; y++)
            {
                for (var x = 0; x < side; x++)
                {
                    var sum = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                            sum += crisp[Math.Clamp(y + dy, 0, side - 1) * side + Math.Clamp(x + dx, 0, side - 1)];
                    }
                    state = state * 1664525u + 1013904223u;
                    var noise = (int)(state >> 24) % 17 - 8;
                    var ramp = 24 * x / side - 12;
                    Gray(pixels, (y * side + x) * 4, (byte)Math.Clamp(40 + sum / 9 * 175 / 255 + ramp + noise, 0, 255));
                }
            }
            return soft;
        }
    }

    private static SKBitmap NoSymbol(int side, bool noise)
    {
        var bitmap = new SKBitmap(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
        var pixels = bitmap.GetPixelSpan();
        var state = 777u;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                state = state * 1664525u + 1013904223u;
                Gray(pixels, (y * side + x) * 4, noise ? (byte)(state >> 24) : (byte)(255 * (x + y) / (2 * side)));
            }
        }
        return bitmap;
    }

    private static void Gray(Span<byte> rgba, int offset, byte value)
    {
        rgba[offset] = value;
        rgba[offset + 1] = value;
        rgba[offset + 2] = value;
        rgba[offset + 3] = 255;
    }

    // Every shape is grey, so one channel is the luminance.
    private static byte[] Luminance(SKBitmap bitmap)
    {
        var luminance = new byte[bitmap.Width * bitmap.Height];
        var pixels = bitmap.GetPixelSpan();
        for (var i = 0; i < luminance.Length; i++)
            luminance[i] = pixels[i * 4];
        return luminance;
    }

    private static string DeterministicText(int length)
    {
        var text = new char[length];
        var rng = new Random(42);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,:/?&=-_";
        for (var i = 0; i < length; i++)
            text[i] = alphabet[rng.Next(alphabet.Length)];
        return new string(text);
    }
}
