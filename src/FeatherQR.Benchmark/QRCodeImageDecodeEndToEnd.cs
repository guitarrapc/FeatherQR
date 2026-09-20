using SkiaSharp;

/// <summary>
/// Reading a Standard QR symbol from an image, large symbols first: the path a Structured Append set is read through, which the matrix-level benchmarks do not touch.
///
/// The shapes differ in what the pixels look like, because the stages do not cost the same on each.
/// A render at a whole number of pixels a module, in an image whose width is a multiple of eight, is the flattering case: every eight-pixel group the threshold's histogram folds is one value.
/// The same symbol at a fractional module size is still two-valued and costs the threshold several times as much, and a rotated one brings in the grey edge pixels a resampler makes.
///
///   v40-3px       : version 40, 3 px a module, 555 x 555
///   v40-3.4px     : version 40 at a fractional module size, 629 x 629, still crisp
///   v40-4px-rot17 : version 40, 4 px a module, rotated 17 degrees with a linear filter, 925 x 925
///   v40-4px-soft  : version 40, 4 px a module, blurred, contrast reduced, a brightness ramp and noise: no pure black or white left
///   v6-4px        : a version 6 URL, the small-symbol reference
///   none-noise    : 740 x 740 noise, no symbol: the failure path, both polarities
///   none-gradient : 740 x 740 smooth ramp, no symbol: the histogram's worst case, both polarities
///
/// Luminance is the span overload and must stay allocation-free; Bitmap is the SKBitmap entry, so the RGBA conversion is inside the measurement.
/// </summary>
public class QRCodeImageDecodeEndToEnd
{
    private static readonly string[] shapeKeys =
    [
        "v40-3px", "v40-3.4px", "v40-4px-rot17", "v40-4px-soft", "v6-4px", "none-noise", "none-gradient",
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

        _bitmap = Shape switch
        {
            "v40-3px" => Render(large, large.Size * 3),
            "v40-3.4px" => Render(large, (int)MathF.Round(large.Size * 3.4f)),
            "v40-4px-rot17" => Rotate(Render(large, large.Size * 4), 17f),
            "v40-4px-soft" => Soften(Render(large, large.Size * 4)),
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
