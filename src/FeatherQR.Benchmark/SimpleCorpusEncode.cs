using System.Text;
using SkiaSharp;

/// <summary>
/// Cross-library Standard QR encoding over a generated corpus: 400 payloads at all four ECC levels, timed by the version range FeatherQR selects for each pair.
/// <see cref="SimpleEncode"/> times five short payloads at ECC L, all in versions 1 to 4.
/// The ratio between libraries moves with the version, so those five do not show how an encoder does on large symbols.
///
/// Every row encodes the same (payload, level) pairs with the library's defaults.
/// ZXing gets the charset this library chooses: ISO-8859-1 where it holds the text, UTF-8 otherwise.
/// The baseline's setup for the whole corpus prints the average version each library selects per range, since a larger symbol is more work.
/// </summary>
[MemoryDiagnoser]
public class SimpleCorpusEncode
{
    public enum VersionRange { V1To4, V5To11, V12To27, V28To40, All }

    private static readonly QREccLevel[] FeatherLevels = [QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H];
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
    public VersionRange Range { get; set; }

    private string[] _texts = default!;
    private int[] _levels = default!;
    private bool[] _latin1 = default!;
    private ZXing.BarcodeWriter<SKBitmap>[] _zxingWriters = default!;

    [GlobalSetup(Target = nameof(FeatherQR_Encode))]
    public void SetupAndReportVersions()
    {
        Setup();
        if (Range == VersionRange.All)
            ReportVersions();
    }

    [GlobalSetup(Targets = [nameof(FeatherQR_Single_Encode), nameof(NetCodecreteQrCodeGenerator_Encode), nameof(QRCoder_Encode), nameof(CodeGlyphX_Encode), nameof(ZXing_Encode)])]
    public void Setup()
    {
        var texts = new List<string>();
        var levels = new List<int>();
        foreach (var text in StandardQRCorpus.Payloads)
        {
            for (var level = 0; level < 4; level++)
            {
                if (!InRange(Range, FeatherQR.QRCodeGenerator.Create(text.AsSpan(), FeatherLevels[level]).Version))
                    continue;
                texts.Add(text);
                levels.Add(level);
            }
        }
        _texts = [.. texts];
        _levels = [.. levels];
        _latin1 = _texts.Select(IsLatin1).ToArray();

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

    private static bool InRange(VersionRange range, int version) => range switch
    {
        VersionRange.V1To4 => version <= 4,
        VersionRange.V5To11 => version is >= 5 and <= 11,
        VersionRange.V12To27 => version is >= 12 and <= 27,
        VersionRange.V28To40 => version >= 28,
        _ => true,
    };

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
        var ranges = Enum.GetValues<VersionRange>();
        var featherVersions = new int[_texts.Length];
        for (var i = 0; i < _texts.Length; i++)
            featherVersions[i] = FeatherQR.QRCodeGenerator.Create(_texts[i].AsSpan(), FeatherLevels[_levels[i]]).Version;
        var counts = ranges.Select(r => featherVersions.Count(v => InRange(r, v))).ToArray();

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
                    if (InRange(ranges[k], featherVersions[i]))
                        sums[k] += v;
                }
            }
            sb.AppendLine("// " + name.PadRight(30) + string.Concat(sums.Select((s, k) => $" | {s / counts[k]:F2}")));
        }
        Console.Write(sb.ToString());
    }
}

/// <summary>
/// A deterministic Standard QR corpus of everyday payloads, short and long, ASCII and beyond ISO-8859-1.
/// One payload in five is a long text of 400 to 900 characters, which lands in versions 12 and up.
/// </summary>
internal static class StandardQRCorpus
{
    private const int PayloadCount = 400;

    // Byte mode holds 1,273 bytes at version 40-H. The cap leaves room for mode and ECI headers.
    private const int MaxUtf8Bytes = 1150;

    private static readonly string[] Hosts =
    [
        "https://example.com", "https://shop.example.com", "https://www.example.org",
        "http://intranet.example.net", "https://docs.example.dev", "https://tickets.example.jp",
    ];

    private static readonly string[] PathWords =
    [
        "products", "item", "orders", "track", "account", "settings", "en-us", "ja-jp",
        "2026", "spring-sale", "events", "menu", "support", "a7f3c9e1", "9b2e44d0c1",
    ];

    private static readonly string[] QueryKeys = ["id", "ref", "utm_source", "utm_campaign", "lang", "q", "page", "session"];

    private static readonly string[] Sentences =
    [
        "Thank you for your order.",
        "Scan to check in at the front desk.",
        "Doors open at 18:30, the show starts at 19:00.",
        "Use code SPRING25 for 25% off your next purchase.",
        "Meet us at Gate 12, Terminal 2.",
        "Return within 30 days with the receipt.",
        "Zürich Hauptbahnhof, Gleis 7, Abfahrt 08:15.",
        "Mañana: café con leche y pan tostado.",
        "ご来店ありがとうございます。またのお越しをお待ちしております。",
        "東京都千代田区丸の内1-9-1",
        "欢迎光临，请扫码点餐。",
        "Добро пожаловать! Вход через главный подъезд.",
        "Καλώς ήρθατε στην Αθήνα.",
        "서울특별시 중구 세종대로 110",
        "Party tonight 🎉 bring snacks 🍕 and good vibes 🎈",
    ];

    private static readonly string[] Names =
    [
        "Alice Smith", "Bob Johnson", "José García", "Chen Wei", "山田太郎", "Иван Петров", "Fatima Zahra", "Ole Nordmann",
    ];

    private static readonly string[] Ssids = ["HomeNet", "Cafe-Guest", "Office_5G", "Hotel Lobby", "IoT-Devices"];

    private const string Alphanumeric = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
    private const string PasswordChars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#";

    public static readonly string[] Payloads = Generate();

    private static string[] Generate()
    {
        var random = new Random(20261021);
        var payloads = new string[PayloadCount];
        for (var i = 0; i < payloads.Length; i++)
            payloads[i] = random.Next(5) == 0 ? LongText(random) : ShortPayload(random);
        return payloads;
    }

    private static string ShortPayload(Random random) => random.Next(7) switch
    {
        0 => Url(random),
        1 => string.Join(" ", Enumerable.Range(0, random.Next(1, 4)).Select(_ => Pick(random, Sentences))),
        2 => $"WIFI:T:WPA;S:{Pick(random, Ssids)};P:{Chars(random, PasswordChars, random.Next(8, 25))};;",
        3 => Contact(random),
        4 => Chars(random, "0123456789", random.Next(6, 201)),
        5 => Chars(random, Alphanumeric, random.Next(8, 121)),
        _ => $"geo:{random.Next(-89, 90)}.{random.Next(1000, 10000)},{random.Next(-179, 180)}.{random.Next(1000, 10000)}",
    };

    private static string LongText(Random random)
    {
        var target = random.Next(400, 901);
        var sb = new StringBuilder();
        while (sb.Length < target)
        {
            var fragment = random.Next(4) switch
            {
                0 => Url(random),
                1 => Contact(random),
                _ => Pick(random, Sentences),
            };
            var separator = sb.Length == 0 ? "" : random.Next(3) == 0 ? "\n" : " ";
            if (Encoding.UTF8.GetByteCount(sb.ToString()) + Encoding.UTF8.GetByteCount(separator + fragment) > MaxUtf8Bytes)
                break;
            sb.Append(separator).Append(fragment);
        }
        return sb.ToString();
    }

    private static string Url(Random random)
    {
        var sb = new StringBuilder(Pick(random, Hosts));
        for (var i = random.Next(1, 5); i > 0; i--)
            sb.Append('/').Append(Pick(random, PathWords));
        for (var i = random.Next(0, 4); i > 0; i--)
            sb.Append(sb.ToString().Contains('?') ? '&' : '?').Append(Pick(random, QueryKeys)).Append('=').Append(random.Next(1, 1_000_000));
        return sb.ToString();
    }

    private static string Contact(Random random)
    {
        var name = Pick(random, Names);
        var phone = $"+{random.Next(1, 99)}-{random.Next(10, 99)}-{random.Next(1000, 9999)}-{random.Next(1000, 9999)}";
        var email = $"{Chars(random, "abcdefghijklmnopqrstuvwxyz", random.Next(3, 10))}@example.com";
        return random.Next(2) == 0
            ? $"MECARD:N:{name};TEL:{phone};EMAIL:{email};;"
            : $"BEGIN:VCARD\nVERSION:3.0\nFN:{name}\nTEL:{phone}\nEMAIL:{email}\nEND:VCARD";
    }

    private static string Pick(Random random, string[] items) => items[random.Next(items.Length)];

    private static string Chars(Random random, string alphabet, int length)
    {
        var chars = new char[length];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[random.Next(alphabet.Length)];
        return new string(chars);
    }
}
