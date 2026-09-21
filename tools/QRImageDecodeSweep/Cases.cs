using System.Text;
using FeatherQR;

namespace QRImageDecodeSweep;

internal static class Cases
{
    private const string Digits = "0123456789";
    private const string Alnum = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
    private const string UrlChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_/?=&.";

    public static CaseDefinition Create(string symbology, int caseId)
    {
        var random = new Random(caseId * 7919 + symbology.Length * 104729 + 13);
        return symbology switch
        {
            Symbologies.StandardQr => CreateQr(caseId, random),
            Symbologies.MicroQr => CreateMicro(caseId, random),
            _ => CreateRmqr(caseId, random),
        };
    }

    private static string RandomText(Random random, string mode, int length)
    {
        var alphabet = mode switch { "numeric" => Digits, "alphanumeric" => Alnum, _ => UrlChars };
        var sb = new StringBuilder(length + 20);
        if (mode == "byte")
            sb.Append("https://example.com/");
        while (sb.Length < length)
            sb.Append(alphabet[random.Next(alphabet.Length)]);
        // A trailing space is legal but some encoders trim it; avoid it
        if (sb[^1] == ' ')
            sb[^1] = 'A';
        return sb.ToString();
    }

    private static int LongestFit(string full, Func<string, bool> fits)
    {
        int lo = 0, hi = full.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (fits(full[..mid]))
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }

    private static CaseDefinition CreateQr(int caseId, Random random)
    {
        var version = 1 + caseId % 40; // every version equally often
        var ecc = (QREccLevel)random.Next(4);
        var mode = new[] { "numeric", "alphanumeric", "byte" }[random.Next(3)];
        var full = RandomText(random, mode, 7100);
        int Capacity(int atMost) => LongestFit(full, text =>
        {
            try
            {
                var qr = QRCodeGenerator.Create(text, ecc, new QRCodeGeneratorOptions { QuietZoneSize = 0, Version = QRVersionRange.AtMost(atMost) });
                return (qr.Size - 17) / 4 <= atMost;
            }
            catch
            {
                return false;
            }
        });

        // Longer than the version below holds and no longer than this one does, so the smallest version that fits is the one asked for.
        // A fraction of this version's capacity alone would not do: from version 6 up the version below holds over 75 % of it.
        // Another encoder may still land on a neighbour (its own segmentation); the result file carries each symbol's version.
        var capacity = Math.Max(1, Capacity(version));
        var below = version == 1 ? 0 : Capacity(version - 1);
        var length = Math.Min(capacity, below + 1 + (int)(random.NextDouble() * (capacity - below)));
        var payload = full[..length];
        if (payload[^1] == ' ')
            payload = payload[..^1] + "A";
        return new CaseDefinition(Symbologies.StandardQr, caseId, payload, mode, ecc.ToString(), version, "v" + version, 0, 0);
    }

    private static CaseDefinition CreateMicro(int caseId, Random random)
    {
        var version = 1 + caseId % 4;
        var ecc = version switch
        {
            1 => MicroQREccLevel.ErrorDetectionOnly,
            4 => (MicroQREccLevel)(1 + random.Next(3)),
            _ => (MicroQREccLevel)(1 + random.Next(2)),
        };
        var modes = version switch { 1 => new[] { "numeric" }, 2 => ["numeric", "alphanumeric"], _ => ["numeric", "alphanumeric", "byte"] };
        var mode = modes[random.Next(modes.Length)];
        var alphabetText = mode == "byte" ? RandomTextNoPrefix(random, 40) : RandomText(random, mode, 40);
        var capacity = LongestFit(alphabetText, text =>
        {
            try
            {
                MicroQRCodeGenerator.Create(text, ecc, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0, Version = (MicroQRVersion)version });
                return true;
            }
            catch
            {
                return false;
            }
        });
        var length = Math.Max(1, (int)Math.Ceiling(capacity * (0.6 + random.NextDouble() * 0.4)));
        var payload = alphabetText[..Math.Min(length, Math.Max(1, capacity))];
        if (payload[^1] == ' ')
            payload = payload[..^1] + "A";
        return new CaseDefinition(Symbologies.MicroQr, caseId, payload, mode, ecc.ToString(), version, "M" + version, 0, 0);
    }

    private static string RandomTextNoPrefix(Random random, int length)
    {
        var sb = new StringBuilder(length);
        while (sb.Length < length)
            sb.Append(UrlChars[random.Next(UrlChars.Length)]);
        return sb.ToString();
    }

    private static CaseDefinition CreateRmqr(int caseId, Random random)
    {
        var versions = Enum.GetValues<RmQRVersion>();
        var version = versions[caseId % versions.Length];
        var name = version.ToString(); // R{h}x{w}
        var parts = name[1..].Split('x');
        var height = int.Parse(parts[0]);
        var width = int.Parse(parts[1]);
        var ecc = (RmQREccLevel)random.Next(2);
        var mode = new[] { "numeric", "alphanumeric", "byte" }[random.Next(3)];
        var full = mode == "byte" ? RandomTextNoPrefix(random, 400) : RandomText(random, mode, 400);
        var capacity = LongestFit(full, text =>
        {
            try
            {
                RmQRCodeGenerator.Create(text, ecc, new RmQRCodeGeneratorOptions { QuietZoneSize = 0, Version = version });
                return true;
            }
            catch
            {
                return false;
            }
        });
        var length = Math.Max(1, (int)Math.Ceiling(capacity * (0.6 + random.NextDouble() * 0.4)));
        var payload = full[..Math.Min(length, Math.Max(1, capacity))];
        if (payload[^1] == ' ')
            payload = payload[..^1] + "A";
        return new CaseDefinition(Symbologies.RmQr, caseId, payload, mode, ecc.ToString(), (int)version, name, height, width);
    }
}
