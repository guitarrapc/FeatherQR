using BenchmarkDotNet.Configs;
using SkiaSharp;

/// <summary>
/// Compares Standard QR decoders over the version-grouped corpus of <see cref="SimpleCorpusEncode"/>.
/// </summary>
/// <remarks>
/// Matrix rows cover every pair in a range. Image rows sample evenly spaced pairs to bound bitmap memory.
/// Setup verifies the decoded text. An image another decoder cannot read is replaced by the next corpus pair, with replacements reported.
/// A FeatherQR failure aborts setup. Image rows include RGBA conversion, as in <see cref="SimpleDecode"/>.
/// </remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory, BenchmarkLogicalGroupRule.ByParams)]
[Config(typeof(CodesConfig))]
public class SimpleCorpusDecode
{
    public sealed class CodesConfig() : CorpusCodesConfig(c => c.Descriptor.Categories.Contains(Image)
        ? SampledRanges(RangeOf(c)).Length * ImageSamplesPerRange
        : StandardQRCorpus.PairCount(RangeOf(c)));

    private const string Matrix = "Matrix";
    private const string Image = "Image";
    private const int ImageSamplesPerRange = 20;
    private const int PixelsPerModule = 8;

    [ParamsAllValues]
    public CorpusVersionRange Range { get; set; }

    private string[] _matrixTexts = default!;
    private byte[][] _modules = default!;
    private int[] _moduleSizes = default!;
    private CodeGlyphX.BitMatrix[] _glyphMatrices = default!;
    private char[] _chars = default!;

    private string[] _imageTexts = default!;
    private SKBitmap[]? _bitmaps;
    private byte[][] _rgba = default!;
    private int[] _imageSizes = default!;
    private ZXing.SkiaSharp.BarcodeReader _zxingReader = default!;

    [GlobalSetup(Targets = [nameof(FeatherQR_MatrixDecode), nameof(FeatherQR_MatrixDecodeSpan), nameof(CodeGlyphX_MatrixDecode)])]
    public void SetupMatrix()
    {
        var pairs = StandardQRCorpus.Pairs(Range);
        _matrixTexts = pairs.Select(p => p.Text).ToArray();
        _modules = new byte[pairs.Length][];
        _moduleSizes = new int[pairs.Length];
        _glyphMatrices = new CodeGlyphX.BitMatrix[pairs.Length];
        for (var i = 0; i < pairs.Length; i++)
        {
            var level = StandardQRCorpus.Levels[pairs[i].Level];
            var size = Sizing.Required(pairs[i].Text.AsSpan(), level, 0);
            _modules[i] = new byte[size.BufferSize];
            QRCodeGenerator.Create(pairs[i].Text.AsSpan(), level, _modules[i], new QRCodeGeneratorOptions { QuietZoneSize = 0 });
            _moduleSizes[i] = size.Size;
            _glyphMatrices[i] = GlyphBitMatrix.From(_modules[i], size.Size, size.Size);
        }
        _chars = new char[QRCodeDecoder.GetMaxDecodedLength(40)];

        for (var i = 0; i < pairs.Length; i++)
        {
            QRCodeDecoder.TryDecode(_modules[i], _moduleSizes[i], out var text, out _);
            Check("FeatherQR matrix", i, _matrixTexts[i], text);
            QRCodeDecoder.TryDecode(_modules[i], _moduleSizes[i], _chars, out var written, out _);
            Check("FeatherQR matrix span", i, _matrixTexts[i], new string(_chars, 0, written));
            CodeGlyphX.QrDecoder.TryDecode(_glyphMatrices[i], out var glyph);
            Check("CodeGlyphX matrix", i, _matrixTexts[i], glyph.Text);
        }
    }

    [GlobalSetup(Targets = [nameof(FeatherQR_ImageDecode), nameof(CodeGlyphX_ImageDecode), nameof(ZXing_ImageDecode)])]
    public void SetupImage()
    {
        _zxingReader = new ZXing.SkiaSharp.BarcodeReader
        {
            Options = new ZXing.Common.DecodingOptions { PossibleFormats = [ZXing.BarcodeFormat.QR_CODE] },
        };
        var ranges = SampledRanges(Range);
        var texts = new List<string>();
        var bitmaps = new List<SKBitmap>();
        var rgbas = new List<byte[]>();
        var sizes = new List<int>();
        int glyphMisses = 0, zxingMisses = 0;
        foreach (var range in ranges)
        {
            var pairs = StandardQRCorpus.Pairs(range);
            for (var k = 0; k < ImageSamplesPerRange; k++)
            {
                // The evenly spaced pair, or the next one in corpus order that every decoder reads, so every row decodes the same images.
                for (var i = k * pairs.Length / ImageSamplesPerRange; ; i++)
                {
                    if (i == pairs.Length)
                        throw new InvalidOperationException($"No pair after sample {k} of {range} reads in every decoder.");
                    var (text, level) = pairs[i];
                    var (bitmap, rgba, size) = RenderImage(text, StandardQRCorpus.Levels[level]);
                    QRCodeDecoder.TryDecode(bitmap, out var decoded);
                    Check("FeatherQR image", i, text, decoded);
                    var glyphReads = GlyphDecodeImage(rgba, size) == text;
                    var zxingReads = _zxingReader.Decode(bitmap)?.Text == text;
                    if (glyphReads && zxingReads)
                    {
                        texts.Add(text);
                        bitmaps.Add(bitmap);
                        rgbas.Add(rgba);
                        sizes.Add(size);
                        break;
                    }
                    glyphMisses += glyphReads ? 0 : 1;
                    zxingMisses += zxingReads ? 0 : 1;
                    bitmap.Dispose();
                }
            }
        }
        _imageTexts = [.. texts];
        _bitmaps = [.. bitmaps];
        _rgba = [.. rgbas];
        _imageSizes = [.. sizes];
        Console.WriteLine($"// Image samples passed over for the next pair: CodeGlyphX could not read {glyphMisses}, ZXing {zxingMisses}");
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (_bitmaps is null)
            return;
        foreach (var bitmap in _bitmaps)
            bitmap.Dispose();
        _bitmaps = null;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory(Matrix)]
    public int FeatherQR_MatrixDecode()
    {
        var sum = 0;
        for (var i = 0; i < _modules.Length; i++)
        {
            QRCodeDecoder.TryDecode(_modules[i], _moduleSizes[i], out var text, out _);
            sum += text.Length;
        }
        return sum;
    }

    [Benchmark]
    [BenchmarkCategory(Matrix)]
    public int FeatherQR_MatrixDecodeSpan()
    {
        var sum = 0;
        for (var i = 0; i < _modules.Length; i++)
        {
            QRCodeDecoder.TryDecode(_modules[i], _moduleSizes[i], _chars, out var written, out _);
            sum += written;
        }
        return sum;
    }

    [Benchmark]
    [BenchmarkCategory(Matrix)]
    public int CodeGlyphX_MatrixDecode()
    {
        var sum = 0;
        for (var i = 0; i < _glyphMatrices.Length; i++)
        {
            CodeGlyphX.QrDecoder.TryDecode(_glyphMatrices[i], out var decoded);
            sum += decoded.Text.Length;
        }
        return sum;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory(Image)]
    public int FeatherQR_ImageDecode()
    {
        var sum = 0;
        for (var i = 0; i < _bitmaps!.Length; i++)
        {
            QRCodeDecoder.TryDecode(_bitmaps[i], out var text);
            sum += text!.Length;
        }
        return sum;
    }

    [Benchmark]
    [BenchmarkCategory(Image)]
    public int CodeGlyphX_ImageDecode()
    {
        var sum = 0;
        for (var i = 0; i < _rgba.Length; i++)
            sum += GlyphDecodeImage(_rgba[i], _imageSizes[i])!.Length;
        return sum;
    }

    [Benchmark]
    [BenchmarkCategory(Image)]
    public int ZXing_ImageDecode()
    {
        var sum = 0;
        for (var i = 0; i < _bitmaps!.Length; i++)
            sum += _zxingReader.Decode(_bitmaps[i]).Text.Length;
        return sum;
    }

    private static CorpusVersionRange[] SampledRanges(CorpusVersionRange range) => range == CorpusVersionRange.All
        ? Enum.GetValues<CorpusVersionRange>().Where(r => r != CorpusVersionRange.All).ToArray()
        : [range];

    private static string? GlyphDecodeImage(byte[] rgba, int size)
    {
        CodeGlyphX.QrDecoder.TryDecode(rgba, size, size, size * 4, CodeGlyphX.PixelFormat.Rgba32, out var decoded);
        return decoded?.Text;
    }

    private static void Check(string path, int index, string expected, string? decoded)
    {
        if (decoded != expected)
        {
            throw new InvalidOperationException(
                $"{path} did not round-trip input {index}: expected \"{expected}\", got \"{decoded ?? "<null>"}\".");
        }
    }

    private static (SKBitmap bitmap, byte[] rgba, int size) RenderImage(string content, QREccLevel level)
    {
        var qr = QRCodeGenerator.Create(content.AsSpan(), level);
        var sizePx = qr.Size * PixelsPerModule;
        var bitmap = new SKBitmap(new SKImageInfo(sizePx, sizePx, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            SymbolRenderer.Render(canvas, SKRect.Create(0, 0, sizePx, sizePx), qr, SKColors.Black, SKColors.White);
            canvas.Flush();
        }
        var rgba = new byte[sizePx * sizePx * 4];
        using (var pixmap = bitmap.PeekPixels())
        {
            var pixels = pixmap.GetPixelSpan();
            for (var y = 0; y < sizePx; y++)
                pixels.Slice(y * pixmap.RowBytes, sizePx * 4).CopyTo(rgba.AsSpan(y * sizePx * 4, sizePx * 4));
        }
        return (bitmap, rgba, sizePx);
    }
}
