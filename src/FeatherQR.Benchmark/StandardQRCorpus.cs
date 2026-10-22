using System.Text;

/// <summary>
/// Version ranges of the corpus benchmarks, by the version FeatherQR selects with its defaults.
/// </summary>
public enum CorpusVersionRange { V1To4, V5To11, V12To27, V28To40, All }

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

    /// <summary>The four ECC levels, L to H. A pair's level is an index into this.</summary>
    public static readonly QREccLevel[] Levels = [QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H];

    /// <summary>Every (payload, level) pair whose symbol FeatherQR's defaults put in the range, in corpus order.</summary>
    public static (string Text, int Level)[] Pairs(CorpusVersionRange range)
    {
        var pairs = new List<(string, int)>();
        foreach (var text in Payloads)
        {
            for (var level = 0; level < Levels.Length; level++)
            {
                if (InRange(range, FeatherQR.QRCodeGenerator.Create(text.AsSpan(), Levels[level]).Version))
                    pairs.Add((text, level));
            }
        }
        return [.. pairs];
    }

    private static readonly Lazy<int[]> PairCounts = new(() => Enum.GetValues<CorpusVersionRange>().Select(r => Pairs(r).Length).ToArray());

    public static int PairCount(CorpusVersionRange range) => PairCounts.Value[(int)range];

    public static bool InRange(CorpusVersionRange range, int version) => range switch
    {
        CorpusVersionRange.V1To4 => version <= 4,
        CorpusVersionRange.V5To11 => version is >= 5 and <= 11,
        CorpusVersionRange.V12To27 => version is >= 12 and <= 27,
        CorpusVersionRange.V28To40 => version >= 28,
        _ => true,
    };

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
