using BenchmarkDotNet.Configs;
using System.Text;

/// <summary>
/// The same 512 × 512 image from either package: SymbolImageWriter in FeatherQR against the builders in FeatherQR.SkiaSharp.
/// Each pair uses the calls the README shows side by side, and the plain PNG pair writes identical pixels.
///
///   V2  : "https://example.com" at level M, the README's example symbol
///   V40 : version 40, about 31k modules
///   Styled : circle modules at 92 % with circle finder patterns
///
/// The symbol is made in setup, so only the image output is measured.
/// The allocation column counts managed memory only, so SkiaSharp's native surface is not in it.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ImageOutputByPackage
{
    private QRCodeData _v2 = default!;
    private QRCodeData _v40 = default!;
    private static readonly SymbolImageOptions Plain = new() { Width = 512, Height = 512 };
    private static readonly SymbolImageOptions Styled = new()
    {
        Width = 512,
        Height = 512,
        ModuleStyle = ModuleStyle.Circle,
        ModuleScalePercent = 92,
        FinderPatternStyle = FinderPatternStyle.Circle,
    };

    [GlobalSetup]
    public void Setup()
    {
        _v2 = QRCodeGenerator.Create("https://example.com", QREccLevel.M);
        _v40 = QRCodeGenerator.Create(BuildDeterministicText(2900), QREccLevel.L);
        if (_v2.Version != 2 || _v40.Version != 40)
        {
            throw new InvalidOperationException($"Expected versions 2 and 40, got {_v2.Version} and {_v40.Version}.");
        }
    }

    // PNG

    [Benchmark(Baseline = true, Description = "FeatherQR_Png_V2")]
    [BenchmarkCategory("1_Png_V2")]
    public byte[] Core_Png_V2() => SymbolImageWriter.GetPngBytes(_v2, Plain);

    [Benchmark(Description = "FeatherQR.SkiaSharp_Png_V2")]
    [BenchmarkCategory("1_Png_V2")]
    public byte[] Skia_Png_V2() => QRCodeImageBuilder.GetPngBytes(_v2);

    [Benchmark(Baseline = true, Description = "FeatherQR_Png_V40")]
    [BenchmarkCategory("2_Png_V40")]
    public byte[] Core_Png_V40() => SymbolImageWriter.GetPngBytes(_v40, Plain);

    [Benchmark(Description = "FeatherQR.SkiaSharp_Png_V40")]
    [BenchmarkCategory("2_Png_V40")]
    public byte[] Skia_Png_V40() => QRCodeImageBuilder.GetPngBytes(_v40);

    [Benchmark(Baseline = true, Description = "FeatherQR_PngStyled_V2")]
    [BenchmarkCategory("3_PngStyled_V2")]
    public byte[] Core_PngStyled_V2() => SymbolImageWriter.GetPngBytes(_v2, Styled);

    [Benchmark(Description = "FeatherQR.SkiaSharp_PngStyled_V2")]
    [BenchmarkCategory("3_PngStyled_V2")]
    public byte[] Skia_PngStyled_V2() => StyledBuilder(_v2).ToByteArray();

    [Benchmark(Baseline = true, Description = "FeatherQR_PngStyled_V40")]
    [BenchmarkCategory("4_PngStyled_V40")]
    public byte[] Core_PngStyled_V40() => SymbolImageWriter.GetPngBytes(_v40, Styled);

    [Benchmark(Description = "FeatherQR.SkiaSharp_PngStyled_V40")]
    [BenchmarkCategory("4_PngStyled_V40")]
    public byte[] Skia_PngStyled_V40() => StyledBuilder(_v40).ToByteArray();

    // SVG

    [Benchmark(Baseline = true, Description = "FeatherQR_Svg_V2")]
    [BenchmarkCategory("5_Svg_V2")]
    public string Core_Svg_V2() => SymbolImageWriter.GetSvgString(_v2, Plain);

    [Benchmark(Description = "FeatherQR.SkiaSharp_Svg_V2")]
    [BenchmarkCategory("5_Svg_V2")]
    public string Skia_Svg_V2() => QRCodeImageBuilder.GetSvgString(_v2);

    [Benchmark(Baseline = true, Description = "FeatherQR_Svg_V40")]
    [BenchmarkCategory("6_Svg_V40")]
    public string Core_Svg_V40() => SymbolImageWriter.GetSvgString(_v40, Plain);

    [Benchmark(Description = "FeatherQR.SkiaSharp_Svg_V40")]
    [BenchmarkCategory("6_Svg_V40")]
    public string Skia_Svg_V40() => QRCodeImageBuilder.GetSvgString(_v40);

    [Benchmark(Baseline = true, Description = "FeatherQR_SvgStyled_V2")]
    [BenchmarkCategory("7_SvgStyled_V2")]
    public string Core_SvgStyled_V2() => SymbolImageWriter.GetSvgString(_v2, Styled);

    [Benchmark(Description = "FeatherQR.SkiaSharp_SvgStyled_V2")]
    [BenchmarkCategory("7_SvgStyled_V2")]
    public string Skia_SvgStyled_V2() => StyledBuilder(_v2).ToSvgString();

    [Benchmark(Baseline = true, Description = "FeatherQR_SvgStyled_V40")]
    [BenchmarkCategory("8_SvgStyled_V40")]
    public string Core_SvgStyled_V40() => SymbolImageWriter.GetSvgString(_v40, Styled);

    [Benchmark(Description = "FeatherQR.SkiaSharp_SvgStyled_V40")]
    [BenchmarkCategory("8_SvgStyled_V40")]
    public string Skia_SvgStyled_V40() => StyledBuilder(_v40).ToSvgString();

    private static QRCodeImageBuilder StyledBuilder(QRCodeData data) => new QRCodeImageBuilder(data)
        .WithModuleStyle(ModuleStyle.Circle, scalePercent: 92)
        .WithFinderPatternStyle(FinderPatternStyle.Circle);

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
