using BenchmarkDotNet.Configs;
using SkiaSharp;
using System.Text;

/// <summary>
/// The same 512 × 512 image from either package: SymbolImageWriter in FeatherQR against the builders in FeatherQR.SkiaSharp.
/// Each pair uses the calls the README shows side by side, and the plain PNG pair and the pixel rows write identical pixels.
///
///   V2  : "https://example.com" at level M, the README's example symbol
///   V40 : version 40, about 31k modules
///   Styled : circle modules at 92 % with circle finder patterns
///   Pixels : an SKBitmap. FeatherQR asks for the image's size, makes the bitmap and writes into its memory at the bitmap's stride.
///            ViaBuffer is the way before the size query and the stride: a buffer of its own, then a copy of each row.
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
    // Filling the canvas as the builders' WithSize does, so both packages write the same image.
    private static readonly SymbolImageOptions Plain = new() { Width = 512, Height = 512, FillCanvas = true };
    private static readonly SymbolImageOptions Styled = new()
    {
        Width = 512,
        Height = 512,
        FillCanvas = true,
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

        // The pixel rows compare like for like only while the two bitmaps hold the same pixels.
        foreach (var data in new[] { _v2, _v40 })
        {
            var (width, height) = SymbolImageWriter.GetImageSize(data, Plain);
            using var core = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            SymbolImageWriter.TryWritePixels(data, core.GetPixelSpan(), core.RowBytes, PixelLayout.Bgra8888, out _, out _, Plain);
            using var skia = new QRCodeImageBuilder(data).ToBitmap();
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (core.GetPixel(x, y) != skia.GetPixel(x, y))
                        throw new InvalidOperationException($"Version {data.Version}: the packages differ at ({x}, {y}).");
                }
            }
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

    // Pixels

    [Benchmark(Baseline = true, Description = "FeatherQR_Pixels_V2")]
    [BenchmarkCategory("9a_Pixels_V2")]
    public int Core_Pixels_V2() => CoreIntoBitmap(_v2);

    [Benchmark(Description = "FeatherQR_PixelsViaBuffer_V2")]
    [BenchmarkCategory("9a_Pixels_V2")]
    public int Core_PixelsViaBuffer_V2() => CoreViaBuffer(_v2);

    [Benchmark(Description = "FeatherQR.SkiaSharp_Bitmap_V2")]
    [BenchmarkCategory("9a_Pixels_V2")]
    public int Skia_Bitmap_V2() => SkiaBitmap(_v2);

    [Benchmark(Baseline = true, Description = "FeatherQR_Pixels_V40")]
    [BenchmarkCategory("9b_Pixels_V40")]
    public int Core_Pixels_V40() => CoreIntoBitmap(_v40);

    [Benchmark(Description = "FeatherQR_PixelsViaBuffer_V40")]
    [BenchmarkCategory("9b_Pixels_V40")]
    public int Core_PixelsViaBuffer_V40() => CoreViaBuffer(_v40);

    [Benchmark(Description = "FeatherQR.SkiaSharp_Bitmap_V40")]
    [BenchmarkCategory("9b_Pixels_V40")]
    public int Skia_Bitmap_V40() => SkiaBitmap(_v40);

    private static int CoreIntoBitmap(QRCodeData data)
    {
        var (width, height) = SymbolImageWriter.GetImageSize(data, Plain);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        SymbolImageWriter.TryWritePixels(data, bitmap.GetPixelSpan(), bitmap.RowBytes, PixelLayout.Bgra8888, out _, out _, Plain);
        return bitmap.RowBytes;
    }

    private static int CoreViaBuffer(QRCodeData data)
    {
        var pixels = new byte[SymbolImageWriter.GetPixelBufferSize(data, PixelLayout.Bgra8888, Plain)];
        SymbolImageWriter.TryWritePixels(data, pixels, PixelLayout.Bgra8888, out var width, out var height, Plain);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        var memory = bitmap.GetPixelSpan();
        for (var row = 0; row < height; row++)
            pixels.AsSpan(row * width * 4, width * 4).CopyTo(memory.Slice(row * bitmap.RowBytes));
        return bitmap.RowBytes;
    }

    private static int SkiaBitmap(QRCodeData data)
    {
        using var bitmap = new QRCodeImageBuilder(data).ToBitmap();
        return bitmap.RowBytes;
    }

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
