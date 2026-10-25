using System.Text;
using BenchmarkDotNet.Configs;

/// <summary>
/// The async writes against their synchronous twins, both into a MemoryStream, whose async write completes at once.
/// What differs is the async layer alone: the pooled buffer the image is encoded into, the copy into it, and the task.
///
/// Shapes, each a category with the synchronous call as its baseline:
///   Png_Url  : a URL, version 3, at 512 px
///   Png_V40  : version 40 at 1,024 px
///   Svg_Url  : the URL as SVG
///   Svg_V40  : version 40 as SVG, a document that outgrows the buffer's first rental
/// </summary>
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class QRCodeImageAsyncWrite
{
    private const string Url = "https://github.com/guitarrapc/FeatherQR";

    private QRCodeData _url = default!;
    private QRCodeData _v40 = default!;
    private readonly MemoryStream _stream = new(capacity: 8 * 1024 * 1024);

    [GlobalSetup]
    public void Setup()
    {
        _url = QRCodeGenerator.Create(Url, QREccLevel.M);
        _v40 = QRCodeGenerator.Create(BuildDeterministicText(2900), QREccLevel.L);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Png_Url")]
    public void Png_Url_Sync() => QRCodeImageBuilder.SavePng(_url, Rewound(), 512);

    [Benchmark, BenchmarkCategory("Png_Url")]
    public Task Png_Url_Async() => QRCodeImageBuilder.SavePngAsync(_url, Rewound(), 512);

    [Benchmark(Baseline = true), BenchmarkCategory("Png_V40")]
    public void Png_V40_Sync() => QRCodeImageBuilder.SavePng(_v40, Rewound(), 1024);

    [Benchmark, BenchmarkCategory("Png_V40")]
    public Task Png_V40_Async() => QRCodeImageBuilder.SavePngAsync(_v40, Rewound(), 1024);

    [Benchmark(Baseline = true), BenchmarkCategory("Svg_Url")]
    public void Svg_Url_Sync() => QRCodeImageBuilder.SaveSvg(_url, Rewound(), 512);

    [Benchmark, BenchmarkCategory("Svg_Url")]
    public Task Svg_Url_Async() => QRCodeImageBuilder.SaveSvgAsync(_url, Rewound(), 512);

    [Benchmark(Baseline = true), BenchmarkCategory("Svg_V40")]
    public void Svg_V40_Sync() => QRCodeImageBuilder.SaveSvg(_v40, Rewound(), 1024);

    [Benchmark, BenchmarkCategory("Svg_V40")]
    public Task Svg_V40_Async() => QRCodeImageBuilder.SaveSvgAsync(_v40, Rewound(), 1024);

    // One stream reused, so neither side pays for growing it
    private MemoryStream Rewound()
    {
        _stream.Position = 0;
        return _stream;
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
