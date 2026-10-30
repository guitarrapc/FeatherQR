using BenchmarkDotNet.Configs;
using SkiaSharp;

/// <summary>
/// The stream reads against decoding the image into a bitmap and reading that, the call a caller makes without them.
/// The image is a 4032 × 3024 JPEG made in setup, a camera photograph's size: one code over a textured background.
///
/// Shapes, each a category with the bitmap pair as its baseline:
///   Photo_Exposed   : a MemoryStream over the array that exposes it, which the read uses in place
///   Photo_Unexposed : a MemoryStream over an array, which the read copies as it does any stream
///   Render          : a 512 px PNG of a URL, where the per-call cost shows most
/// </summary>
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class QRCodeImageStreamDecode
{
    private byte[] _photo = default!;
    private byte[] _render = default!;

    [GlobalSetup]
    public void Setup()
    {
        _photo = Photograph();
        _render = QRCodeImageBuilder.GetPngBytes("https://github.com/guitarrapc/FeatherQR");
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Photo_Exposed")]
    public QRCodeDecodeInfo Photo_Exposed_Bitmap() => Bitmap(Exposed(_photo));

    [Benchmark, BenchmarkCategory("Photo_Exposed")]
    public Task<QRCodeDecodeResult> Photo_Exposed_Async() => QRCodeDecoder.DecodeAsync(Exposed(_photo));

    [Benchmark, BenchmarkCategory("Photo_Exposed")]
    public QRCodeDecodeResult Photo_Exposed_Sync() => QRCodeDecoder.Decode(Exposed(_photo));

    [Benchmark(Baseline = true), BenchmarkCategory("Photo_Unexposed")]
    public QRCodeDecodeInfo Photo_Unexposed_Bitmap() => Bitmap(new MemoryStream(_photo));

    [Benchmark, BenchmarkCategory("Photo_Unexposed")]
    public Task<QRCodeDecodeResult> Photo_Unexposed_Async() => QRCodeDecoder.DecodeAsync(new MemoryStream(_photo));

    [Benchmark(Baseline = true), BenchmarkCategory("Render")]
    public QRCodeDecodeInfo Render_Bitmap() => Bitmap(new MemoryStream(_render));

    [Benchmark, BenchmarkCategory("Render")]
    public Task<QRCodeDecodeResult> Render_Async() => QRCodeDecoder.DecodeAsync(new MemoryStream(_render));

    private static QRCodeDecodeInfo Bitmap(Stream stream)
    {
        using var bitmap = SKBitmap.Decode(stream);
        QRCodeDecoder.TryDecode(bitmap, out _, out var info);
        return info;
    }

    // Each call gets a new stream, since SKBitmap.Decode(Stream) disposes the stream it is given
    private static MemoryStream Exposed(byte[] bytes) => new(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);

    private static byte[] Photograph()
    {
        const int Width = 4032, Height = 3024;
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(180, 170, 160));

        // Texture gives the JPEG a photograph's detail to compress rather than flat colour
        var random = new Random(42);
        using var paint = new SKPaint { IsAntialias = true };
        for (var i = 0; i < 20_000; i++)
        {
            paint.Color = new SKColor((byte)random.Next(90, 230), (byte)random.Next(90, 230), (byte)random.Next(90, 230));
            canvas.DrawCircle(random.Next(Width), random.Next(Height), random.Next(4, 40), paint);
        }

        using var code = new QRCodeImageBuilder("https://github.com/guitarrapc/FeatherQR/benchmark").WithSize(1200, 1200).ToImage();
        canvas.DrawImage(code, 1400, 900, SKSamplingOptions.Default);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }
}
