using BenchmarkDotNet.Configs;
using SkiaSharp;

/// <summary>
/// Cross-library Standard QR encoding: the shortest call each library offers for "text in, module matrix out", over five representative payloads.
///
/// Comparability: every library selects the same QR version for every payload here (numeric 1, alphanumeric 2, url 3, unicode 4, wifi 3 at ECC L), so each row encodes the same amount of data into the same symbol size.
/// Two differences are inherent to the libraries and are not normalized away:
///
///   Segmentation - FeatherQR encodes the payload in a single mode by default,
///     while Net.Codecrete.QrCodeGenerator, QRCoder and CodeGlyphX plan mixed-mode
///     segments by default. The version comes out the same for these payloads either way.
///   Quiet zone - FeatherQR and QRCoder place a quiet zone in the returned matrix;
///     Net.Codecrete.QrCodeGenerator and CodeGlyphX return the bare symbol.
///
/// ZXing is configured to match this library's ECI behavior (Latin-1 for the ASCII payloads, UTF-8 for the unicode one) rather than left on its own default.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class SimpleEncode
{
    private string _textNumber = default!;
    private string _textAlphanumeric = default!;
    private string _textUrl = default!;
    private string _textUnicode = default!;
    private string _textWifi = default!;
    private byte[] _spanDestination = default!;
    ZXing.BarcodeWriter<SKBitmap> _zxingWriter_ascii = default!;
    ZXing.BarcodeWriter<SKBitmap> _zxingWriter_utf8 = default!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _textNumber = "0123456789012345678901234567890123456789";
        _textAlphanumeric = "0123456789ABCDEFG0123456789HIJKLMN";
        _textUrl = "https://example.com/user/repo?foo=value&bar=piyo";
        _textUnicode = "FooBar你好世界こんにちはПривет мир🎉🎊🎈Zürich";
        _textWifi = "WIFI:S:foobar-wifi;T:WPA;P:test123;H:false;;";
        // Shared destination for the span-based zero-allocation benchmarks,
        // sized for the largest of the five payloads.
        var maxBufferSize = new[] { _textNumber, _textAlphanumeric, _textUrl, _textUnicode, _textWifi }
            .Max(text => Sizing.Required(text.AsSpan(), QREccLevel.L).BufferSize);
        _spanDestination = new byte[maxBufferSize];
        _zxingWriter_ascii = new()
        {
            Format = ZXing.BarcodeFormat.QR_CODE,
            Options = new ZXing.QrCode.QrCodeEncodingOptions()
            {
                DisableECI = true,
                CharacterSet = "ISO-8859-1"
            },
        };
        _zxingWriter_utf8 = new()
        {
            Format = ZXing.BarcodeFormat.QR_CODE,
            Options = new ZXing.QrCode.QrCodeEncodingOptions()
            {
                DisableECI = true,
                CharacterSet = "UTF8"
            },
        };
    }

    // FeatherQR

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("FeatherQR")]
    public QRCodeData SkiaSharpQrCode_Number_Encode()
    {
        return FeatherQR.QRCodeGenerator.Create(_textNumber.AsSpan(), QREccLevel.L);
    }

    [Benchmark]
    [BenchmarkCategory("FeatherQR")]
    public QRCodeData SkiaSharpQrCode_Alphanumeric_Encode()
    {
        return FeatherQR.QRCodeGenerator.Create(_textAlphanumeric.AsSpan(), QREccLevel.L);
    }

    [Benchmark]
    [BenchmarkCategory("FeatherQR")]
    public QRCodeData SkiaSharpQrCode_Url_Encode()
    {
        return FeatherQR.QRCodeGenerator.Create(_textUrl.AsSpan(), QREccLevel.L);
    }

    [Benchmark]
    [BenchmarkCategory("FeatherQR")]
    public QRCodeData SkiaSharpQrCode_Unicode_Encode()
    {
        return FeatherQR.QRCodeGenerator.Create(_textUnicode.AsSpan(), QREccLevel.L);
    }

    [Benchmark]
    [BenchmarkCategory("FeatherQR")]
    public QRCodeData SkiaSharpQrCode_Wifi_Encode()
    {
        return FeatherQR.QRCodeGenerator.Create(_textWifi.AsSpan(), QREccLevel.L);
    }

    // Span-destination (zero-allocation) variants

    [Benchmark(Description = "SkiaSharpQrCode_Number_Encode (Span)")]
    [BenchmarkCategory("FeatherQR")]
    public int SkiaSharpQrCode_Number_EncodeSpan()
    {
        return FeatherQR.QRCodeGenerator.Create(_textNumber.AsSpan(), QREccLevel.L, _spanDestination);
    }

    [Benchmark(Description = "SkiaSharpQrCode_Alphanumeric_Encode (Span)")]
    [BenchmarkCategory("FeatherQR")]
    public int SkiaSharpQrCode_Alphanumeric_EncodeSpan()
    {
        return FeatherQR.QRCodeGenerator.Create(_textAlphanumeric.AsSpan(), QREccLevel.L, _spanDestination);
    }

    [Benchmark(Description = "SkiaSharpQrCode_Url_Encode (Span)")]
    [BenchmarkCategory("FeatherQR")]
    public int SkiaSharpQrCode_Url_EncodeSpan()
    {
        return FeatherQR.QRCodeGenerator.Create(_textUrl.AsSpan(), QREccLevel.L, _spanDestination);
    }

    [Benchmark(Description = "SkiaSharpQrCode_Unicode_Encode (Span)")]
    [BenchmarkCategory("FeatherQR")]
    public int SkiaSharpQrCode_Unicode_EncodeSpan()
    {
        return FeatherQR.QRCodeGenerator.Create(_textUnicode.AsSpan(), QREccLevel.L, _spanDestination);
    }

    [Benchmark(Description = "SkiaSharpQrCode_Wifi_Encode (Span)")]
    [BenchmarkCategory("FeatherQR")]
    public int SkiaSharpQrCode_Wifi_EncodeSpan()
    {
        return FeatherQR.QRCodeGenerator.Create(_textWifi.AsSpan(), QREccLevel.L, _spanDestination);
    }

    // Net.Codecrete.QrCodeGenerator

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Net.Codecrete.QrCodeGenerator")]
    public Net.Codecrete.QrCodeGenerator.QrCode NetCodecreteQrCodeGenerator_Number_Encode()
    {
        return Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(_textNumber, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Low);
    }

    [Benchmark]
    [BenchmarkCategory("Net.Codecrete.QrCodeGenerator")]
    public Net.Codecrete.QrCodeGenerator.QrCode NetCodecreteQrCodeGenerator_Alphanumeric_Encode()
    {
        return Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(_textAlphanumeric, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Low);
    }

    [Benchmark]
    [BenchmarkCategory("Net.Codecrete.QrCodeGenerator")]
    public Net.Codecrete.QrCodeGenerator.QrCode NetCodecreteQrCodeGenerator_Url_Encode()
    {
        return Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(_textUrl, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Low);
    }

    [Benchmark]
    [BenchmarkCategory("Net.Codecrete.QrCodeGenerator")]
    public Net.Codecrete.QrCodeGenerator.QrCode NetCodecreteQrCodeGenerator_Unicode_Encode()
    {
        return Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(_textUnicode, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Low);
    }

    [Benchmark]
    [BenchmarkCategory("Net.Codecrete.QrCodeGenerator")]
    public Net.Codecrete.QrCodeGenerator.QrCode NetCodecreteQrCodeGenerator_Wifi_Encode()
    {
        return Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(_textWifi, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Low);
    }

    // QRCoder

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("QRCoder")]
    public QRCoder.QRCodeData QRCoder_Number_Encode()
    {
        return QRCoder.QRCodeGenerator.GenerateQrCode(_textNumber, QRCoder.QRCodeGenerator.ECCLevel.L);
    }

    [Benchmark]
    [BenchmarkCategory("QRCoder")]
    public QRCoder.QRCodeData QRCoder_Alphanumeric_Encode()
    {
        return QRCoder.QRCodeGenerator.GenerateQrCode(_textAlphanumeric, QRCoder.QRCodeGenerator.ECCLevel.L);
    }

    [Benchmark]
    [BenchmarkCategory("QRCoder")]
    public QRCoder.QRCodeData QRCoder_Url_Encode()
    {
        return QRCoder.QRCodeGenerator.GenerateQrCode(_textUrl, QRCoder.QRCodeGenerator.ECCLevel.L);
    }

    [Benchmark]
    [BenchmarkCategory("QRCoder")]
    public QRCoder.QRCodeData QRCoder_Unicode_Encode()
    {
        return QRCoder.QRCodeGenerator.GenerateQrCode(_textUnicode, QRCoder.QRCodeGenerator.ECCLevel.L);
    }

    [Benchmark]
    [BenchmarkCategory("QRCoder")]
    public QRCoder.QRCodeData QRCoder_Wifi_Encode()
    {
        return QRCoder.QRCodeGenerator.GenerateQrCode(_textWifi, QRCoder.QRCodeGenerator.ECCLevel.L);
    }

    // CodeGlyphX

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("CodeGlyphX")]
    public CodeGlyphX.QrCode CodeGlyphX_Number_Encode()
    {
        return CodeGlyphX.QrCodeEncoder.EncodeText(_textNumber, CodeGlyphX.QrErrorCorrectionLevel.L);
    }

    [Benchmark]
    [BenchmarkCategory("CodeGlyphX")]
    public CodeGlyphX.QrCode CodeGlyphX_Alphanumeric_Encode()
    {
        return CodeGlyphX.QrCodeEncoder.EncodeText(_textAlphanumeric, CodeGlyphX.QrErrorCorrectionLevel.L);
    }

    [Benchmark]
    [BenchmarkCategory("CodeGlyphX")]
    public CodeGlyphX.QrCode CodeGlyphX_Url_Encode()
    {
        return CodeGlyphX.QrCodeEncoder.EncodeText(_textUrl, CodeGlyphX.QrErrorCorrectionLevel.L);
    }

    [Benchmark]
    [BenchmarkCategory("CodeGlyphX")]
    public CodeGlyphX.QrCode CodeGlyphX_Unicode_Encode()
    {
        return CodeGlyphX.QrCodeEncoder.EncodeText(_textUnicode, CodeGlyphX.QrErrorCorrectionLevel.L);
    }

    [Benchmark]
    [BenchmarkCategory("CodeGlyphX")]
    public CodeGlyphX.QrCode CodeGlyphX_Wifi_Encode()
    {
        return CodeGlyphX.QrCodeEncoder.EncodeText(_textWifi, CodeGlyphX.QrErrorCorrectionLevel.L);
    }

    // Zxing

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Zxing")]
    public ZXing.Common.BitMatrix ZXing_Number_Encode()
    {
        return _zxingWriter_ascii.Encode(_textNumber);
    }

    [Benchmark]
    [BenchmarkCategory("Zxing")]
    public ZXing.Common.BitMatrix ZXing_Alphanumeric_Encode()
    {
        return _zxingWriter_ascii.Encode(_textAlphanumeric);
    }

    [Benchmark]
    [BenchmarkCategory("Zxing")]
    public ZXing.Common.BitMatrix ZXing_Url_Encode()
    {
        return _zxingWriter_ascii.Encode(_textUrl);
    }

    [Benchmark]
    [BenchmarkCategory("Zxing")]
    public ZXing.Common.BitMatrix ZXing_Unicode_Encode()
    {
        return _zxingWriter_utf8.Encode(_textUnicode);
    }

    [Benchmark]
    [BenchmarkCategory("Zxing")]
    public ZXing.Common.BitMatrix ZXing_Wifi_Encode()
    {
        return _zxingWriter_utf8.Encode(_textWifi);
    }
}
