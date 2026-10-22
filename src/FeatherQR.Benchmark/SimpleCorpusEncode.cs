using System.Text;
using SkiaSharp;

/// <summary>
/// Cross-library Standard QR encoding over a generated corpus: 400 payloads at all four ECC levels, timed by the version range FeatherQR selects for each pair.
/// <see cref="SimpleEncode"/> times five short payloads at ECC L, all in versions 1 to 4.
/// The ratio between libraries moves with the version, so those five do not show how an encoder does on large symbols.
///
/// Every row encodes the same (payload, level) pairs with the library's defaults.
/// The span row writes each symbol into one caller buffer sized for the range's largest, the zero-allocation path.
/// ZXing gets the charset this library chooses: ISO-8859-1 where it holds the text, UTF-8 otherwise.
/// The baseline's setup for the whole corpus prints the average version each library selects per range, since a larger symbol is more work.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(CodesConfig))]
public class SimpleCorpusEncode
{
    public sealed class CodesConfig() : CorpusCodesConfig(c => StandardQRCorpus.PairCount(RangeOf(c)));

    private static readonly QREccLevel[] FeatherLevels = StandardQRCorpus.Levels;
    private static readonly Net.Codecrete.QrCodeGenerator.QrCode.Ecc[] CodecreteLevels =
    [
        Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Low, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Medium,
        Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Quartile, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.High,
    ];
    private static readonly QRCoder.QRCodeGenerator.ECCLevel[] QRCoderLevels =
    [
        QRCoder.QRCodeGenerator.ECCLevel.L, QRCoder.QRCodeGenerator.ECCLevel.M,
        QRCoder.QRCodeGenerator.ECCLevel.Q, QRCoder.QRCodeGenerator.ECCLevel.H,
    ];
    private static readonly CodeGlyphX.QrErrorCorrectionLevel[] GlyphLevels =
    [
        CodeGlyphX.QrErrorCorrectionLevel.L, CodeGlyphX.QrErrorCorrectionLevel.M,
        CodeGlyphX.QrErrorCorrectionLevel.Q, CodeGlyphX.QrErrorCorrectionLevel.H,
    ];
    private static readonly ZXing.QrCode.Internal.ErrorCorrectionLevel[] ZxingLevels =
    [
        ZXing.QrCode.Internal.ErrorCorrectionLevel.L, ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
        ZXing.QrCode.Internal.ErrorCorrectionLevel.Q, ZXing.QrCode.Internal.ErrorCorrectionLevel.H,
    ];
    private static readonly QRCodeGeneratorOptions SingleOptions = new() { Segmentation = QRSegmentation.Single };

    [ParamsAllValues]
    public CorpusVersionRange Range { get; set; }

    private string[] _texts = default!;
    private int[] _levels = default!;
    private bool[] _latin1 = default!;
    private byte[] _spanDestination = default!;
    private ZXing.BarcodeWriter<SKBitmap>[] _zxingWriters = default!;

    [GlobalSetup(Target = nameof(FeatherQR_Encode))]
    public void SetupAndReportVersions()
    {
        Setup();
        if (Range == CorpusVersionRange.All)
            ReportVersions();
    }

    [GlobalSetup(Targets = [nameof(FeatherQR_Single_Encode), nameof(FeatherQR_EncodeSpan), nameof(NetCodecreteQrCodeGenerator_Encode), nameof(QRCoder_Encode), nameof(CodeGlyphX_Encode), nameof(ZXing_Encode)])]
    public void Setup()
    {
        var pairs = StandardQRCorpus.Pairs(Range);
        _texts = pairs.Select(p => p.Text).ToArray();
        _levels = pairs.Select(p => p.Level).ToArray();
        _latin1 = _texts.Select(IsLatin1).ToArray();
        _spanDestination = new byte[_texts.Select((t, i) => Sizing.Required(t.AsSpan(), FeatherLevels[_levels[i]]).BufferSize).Max()];

        // One writer per level and charset, so the timed loop only picks one.
        _zxingWriters = new ZXing.BarcodeWriter<SKBitmap>[8];
        for (var level = 0; level < 4; level++)
        {
            _zxingWriters[level * 2] = CreateZxingWriter(level, "ISO-8859-1");
            _zxingWriters[level * 2 + 1] = CreateZxingWriter(level, "UTF-8");
        }
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("FeatherQR")]
    public int FeatherQR_Encode()
    {
        var sum = 0;
        for (var i = 0; i < _texts.Length; i++)
            sum += FeatherQR.QRCodeGenerator.Create(_texts[i].AsSpan(), FeatherLevels[_levels[i]]).Version;
        return sum;
    }

    [Benchmark]
    [BenchmarkCategory("FeatherQR")]
    public int FeatherQR_Single_Encode()
    {
        var sum = 0;
        for (var i = 0; i < _texts.Length; i++)
            sum += FeatherQR.QRCodeGenerator.Create(_texts[i].AsSpan(), FeatherLevels[_levels[i]], in SingleOptions).Version;
        return sum;
    }

    [Benchmark]
    [BenchmarkCategory("FeatherQR_Span")]
    public int FeatherQR_EncodeSpan()
    {
        var sum = 0;
        for (var i = 0; i < _texts.Length; i++)
            sum += FeatherQR.QRCodeGenerator.Create(_texts[i].AsSpan(), FeatherLevels[_levels[i]], _spanDestination);
        return sum;
    }

    [Benchmark]
    [BenchmarkCategory("Net.Codecrete.QrCodeGenerator")]
    public int NetCodecreteQrCodeGenerator_Encode()
    {
        var sum = 0;
        for (var i = 0; i < _texts.Length; i++)
            sum += Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(_texts[i], CodecreteLevels[_levels[i]]).Version;
        return sum;
    }

    [Benchmark]
    [BenchmarkCategory("QRCoder")]
    public int QRCoder_Encode()
    {
        var sum = 0;
        for (var i = 0; i < _texts.Length; i++)
        {
            using var data = QRCoder.QRCodeGenerator.GenerateQrCode(_texts[i], QRCoderLevels[_levels[i]]);
            sum += data.Version;
        }
        return sum;
    }

    [Benchmark]
    [BenchmarkCategory("CodeGlyphX")]
    public int CodeGlyphX_Encode()
    {
        var sum = 0;
        for (var i = 0; i < _texts.Length; i++)
            sum += CodeGlyphX.QrCodeEncoder.EncodeText(_texts[i], GlyphLevels[_levels[i]]).Version;
        return sum;
    }

    [Benchmark]
    [BenchmarkCategory("Zxing")]
    public int ZXing_Encode()
    {
        var sum = 0;
        for (var i = 0; i < _texts.Length; i++)
            sum += _zxingWriters[_levels[i] * 2 + (_latin1[i] ? 0 : 1)].Encode(_texts[i]).Width;
        return sum;
    }

    private static bool IsLatin1(string text)
    {
        foreach (var c in text)
        {
            if (c > 0xFF)
                return false;
        }
        return true;
    }

    private static ZXing.BarcodeWriter<SKBitmap> CreateZxingWriter(int level, string charset) => new()
    {
        Format = ZXing.BarcodeFormat.QR_CODE,
        Options = new ZXing.QrCode.QrCodeEncodingOptions
        {
            ErrorCorrection = ZxingLevels[level],
            CharacterSet = charset,
        },
    };

    private void ReportVersions()
    {
        var libraries = new (string Name, Func<string, int, int> Version)[]
        {
            ("FeatherQR", (t, l) => FeatherQR.QRCodeGenerator.Create(t.AsSpan(), FeatherLevels[l]).Version),
            ("FeatherQR (Single)", (t, l) => FeatherQR.QRCodeGenerator.Create(t.AsSpan(), FeatherLevels[l], in SingleOptions).Version),
            ("Net.Codecrete.QrCodeGenerator", (t, l) => Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(t, CodecreteLevels[l]).Version),
            ("QRCoder", (t, l) => { using var d = QRCoder.QRCodeGenerator.GenerateQrCode(t, QRCoderLevels[l]); return d.Version; }),
            ("CodeGlyphX", (t, l) => CodeGlyphX.QrCodeEncoder.EncodeText(t, GlyphLevels[l]).Version),
            ("Zxing", (t, l) => ZXing.QrCode.Internal.Encoder.encode(t, ZxingLevels[l], new Dictionary<ZXing.EncodeHintType, object>
            {
                [ZXing.EncodeHintType.CHARACTER_SET] = IsLatin1(t) ? "ISO-8859-1" : "UTF-8",
            }).Version.VersionNumber),
        };
        var ranges = Enum.GetValues<CorpusVersionRange>();
        var featherVersions = new int[_texts.Length];
        for (var i = 0; i < _texts.Length; i++)
            featherVersions[i] = FeatherQR.QRCodeGenerator.Create(_texts[i].AsSpan(), FeatherLevels[_levels[i]]).Version;
        var counts = ranges.Select(r => featherVersions.Count(v => StandardQRCorpus.InRange(r, v))).ToArray();

        var sb = new StringBuilder();
        sb.AppendLine("// Average version per range (ranges by FeatherQR's version, pair count in parentheses)");
        sb.AppendLine("// " + "Library".PadRight(30) + string.Concat(ranges.Select((r, k) => $" | {r} ({counts[k]})")));
        foreach (var (name, version) in libraries)
        {
            var sums = new double[ranges.Length];
            for (var i = 0; i < _texts.Length; i++)
            {
                var v = version(_texts[i], _levels[i]);
                for (var k = 0; k < ranges.Length; k++)
                {
                    if (StandardQRCorpus.InRange(ranges[k], featherVersions[i]))
                        sums[k] += v;
                }
            }
            sb.AppendLine("// " + name.PadRight(30) + string.Concat(sums.Select((s, k) => $" | {s / counts[k]:F2}")));
        }
        Console.Write(sb.ToString());
    }
}
