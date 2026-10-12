using System.Text;

namespace FeatherQR.Tests;

/// <summary>
/// Generated content through both generators and this library's decoders, with no FNC1 mode, first position and
/// second position, under <c>Single</c> and <c>Optimal</c>. The texts are drawn to meet the FNC1 <c>%</c> rule from
/// every side (a literal <c>%</c> and the separator U+001D alone, doubled and beside each other, between digits,
/// capitals and lowercase, with Latin-1 and kana among them), and the same texts run without FNC1, where a
/// <c>%</c> is one character and the separator a byte. Each case holds what needs no second implementation to
/// state: the text and the mode read back, sizing agrees with <c>Create</c>, the span form is the data form, and
/// <c>Optimal</c> writes <c>Single</c>'s symbol where it keeps <c>Single</c>'s version and a smaller one otherwise.
/// The sequence is a function of the seed, so a failure names its case.
/// </summary>
public class Fnc1RoundTripSweepTest
{
    private const int Texts = 400;
    private const ulong Seed = 0x2545F4914F6CDD1DUL;
    private const char Gs = '\u001D';
    private const string Digits = "0123456789";
    private const string Capitals = "ABCDEFGHIJKLMNOPQRSTUVWXYZ $*+-./:";
    private const string Lowercase = "abcdefghijklmnopqrstuvwxyz";
    private static readonly char[] latin1 = [(char)0xE9, (char)0xFC, (char)0xF1];
    private static readonly char[] kana = [(char)0x3042, (char)0x65E5, (char)0x672C, (char)0x8A9E];

    [Test]
    [Arguments("none")]
    [Arguments("first")]
    [Arguments("second")]
    public async Task StandardQr_EveryGeneratedText_ReadsBack_AndOptimalKeepsItsContract(string position)
    {
        var random = new Xorshift(Seed);
        var failures = new List<string>();
        var encoded = 0;
        for (var index = 0; index < Texts; index++)
        {
            var text = MakeText(random);
            var mode = MakeMode(random, position);
            var level = (QREccLevel)random.Next(4);
            var allowKanji = random.Next(4) == 0;
            var eci = random.Next(6) == 0 ? EciMode.Utf8 : EciMode.Default;
            var single = new QRCodeGeneratorOptions { Fnc1 = mode, AllowKanji = allowKanji, EciMode = eci, QuietZoneSize = 0 };
            var optimal = single with { Segmentation = QRSegmentation.Optimal };
            var name = $"#{index} {level} kanji={allowKanji} eci={eci} \"{Escape(text)}\"";

            var singleModules = Encode(text, level, single, mode, "Single " + name, failures, out var singleVersion);
            var optimalModules = Encode(text, level, optimal, mode, "Optimal " + name, failures, out var optimalVersion);
            if (singleModules is null)
                continue;
            encoded++;

            if (optimalModules is null)
                failures.Add($"Optimal does not fit what Single fits: {name}");
            else if (optimalVersion > singleVersion)
                failures.Add($"Optimal chose version {optimalVersion} over Single's {singleVersion}: {name}");
            else if (optimalVersion == singleVersion && !optimalModules.AsSpan().SequenceEqual(singleModules))
                failures.Add($"Optimal kept version {singleVersion} and wrote another symbol than Single: {name}");
        }

        await Assert.That(failures).IsEmpty();
        await Assert.That(encoded).IsGreaterThan(Texts * 9 / 10).Because("the sweep proves nothing about texts that do not fit");
    }

    [Test]
    [Arguments("none")]
    [Arguments("first")]
    [Arguments("second")]
    public async Task RmQr_EveryGeneratedText_ReadsBack_AndOptimalKeepsItsContract(string position)
    {
        var random = new Xorshift(Seed);
        var failures = new List<string>();
        var encoded = 0;
        for (var index = 0; index < Texts; index++)
        {
            var text = MakeText(random);
            var mode = MakeMode(random, position);
            var level = (RmQREccLevel)random.Next(2);
            var allowKanji = random.Next(4) == 0;
            var eci = random.Next(6) == 0 ? EciMode.Utf8 : EciMode.Default;
            var single = new RmQRCodeGeneratorOptions { Fnc1 = mode, AllowKanji = allowKanji, EciMode = eci, QuietZoneSize = 0 };
            var optimal = single with { Segmentation = RmQRSegmentation.Optimal };
            var name = $"#{index} {level} kanji={allowKanji} eci={eci} \"{Escape(text)}\"";

            var singleModules = Encode(text, level, single, mode, "Single " + name, failures, out var singleVersion);
            var optimalModules = Encode(text, level, optimal, mode, "Optimal " + name, failures, out var optimalVersion);
            if (singleModules is null)
                continue;
            encoded++;

            // The default strategy takes the fewest modules, so that is what Optimal must not exceed.
            if (optimalModules is null)
                failures.Add($"Optimal does not fit what Single fits: {name}");
            else if (optimalModules.Length > singleModules.Length)
                failures.Add($"Optimal chose {optimalVersion} over Single's smaller {singleVersion}: {name}");
            else if (optimalVersion == singleVersion && !optimalModules.AsSpan().SequenceEqual(singleModules))
                failures.Add($"Optimal kept {singleVersion} and wrote another symbol than Single: {name}");
        }

        await Assert.That(failures).IsEmpty();
        await Assert.That(encoded).IsGreaterThan(Texts / 2).Because("the sweep proves nothing about texts that do not fit, and rMQR holds far less than Standard QR");
    }

    /// <summary>
    /// Sizes, writes in both forms and reads back one Standard QR symbol. Returns its modules, or <c>null</c> when the
    /// text does not fit. Every disagreement goes to <paramref name="failures"/>.
    /// </summary>
    private static byte[]? Encode(string text, QREccLevel level, QRCodeGeneratorOptions options, Fnc1Mode mode, string name, List<string> failures, out int version)
    {
        version = 0;
        if (!QRCodeGenerator.TryGetRequiredBufferSize(text, level, out var size, options))
            return null;

        var data = QRCodeGenerator.Create(text, level, options);
        version = data.Version;
        var modules = new byte[data.Size * data.Size];
        for (var row = 0; row < data.Size; row++)
            for (var col = 0; col < data.Size; col++)
                modules[row * data.Size + col] = data[row, col] ? (byte)1 : (byte)0;

        var spanForm = new byte[size.BufferSize];
        var written = QRCodeGenerator.Create(text, level, spanForm, options);
        if (size.Version != data.Version || size.Size != data.Size || written != modules.Length || !spanForm.AsSpan().SequenceEqual(modules))
            failures.Add($"sizing, the span form and the data form disagree: {name}");

        if (!QRCodeDecoder.TryDecode(data, out var decoded, out var info))
            failures.Add($"{info.Status}: {name}");
        else if (decoded != text)
            failures.Add($"read \"{Escape(decoded)}\": {name}");
        else if (info.Fnc1 != mode)
            failures.Add($"read the mode {info.Fnc1}: {name}");
        return modules;
    }

    /// <inheritdoc cref="Encode(string, QREccLevel, QRCodeGeneratorOptions, Fnc1Mode, string, List{string}, out int)"/>
    private static byte[]? Encode(string text, RmQREccLevel level, RmQRCodeGeneratorOptions options, Fnc1Mode mode, string name, List<string> failures, out RmQRVersion version)
    {
        version = default;
        if (!RmQRCodeGenerator.TryGetRequiredBufferSize(text, level, out var size, options))
            return null;

        var data = RmQRCodeGenerator.Create(text, level, options);
        version = data.Version;
        var modules = new byte[data.Width * data.Height];
        for (var row = 0; row < data.Height; row++)
            for (var col = 0; col < data.Width; col++)
                modules[row * data.Width + col] = data[row, col] ? (byte)1 : (byte)0;

        var spanForm = new byte[size.BufferSize];
        var written = RmQRCodeGenerator.Create(text, level, spanForm, options);
        if (size.Version != data.Version || size.Width != data.Width || size.Height != data.Height || written != modules.Length || !spanForm.AsSpan().SequenceEqual(modules))
            failures.Add($"sizing, the span form and the data form disagree: {name}");

        if (!RmQRCodeDecoder.TryDecode(data, out var decoded, out var info))
            failures.Add($"{info.Status}: {name}");
        else if (decoded != text)
            failures.Add($"read \"{Escape(decoded)}\": {name}");
        else if (info.Fnc1 != mode)
            failures.Add($"read the mode {info.Fnc1}: {name}");
        return modules;
    }

    private static Fnc1Mode MakeMode(Xorshift random, string position)
    {
        // Drawn for every position, so the three runs see the same texts and options.
        var number = random.Next(100);
        var letter = (char)((random.Next(2) == 0 ? 'A' : 'a') + random.Next(26));
        var useLetter = random.Next(2) == 0;
        return position switch
        {
            "none" => Fnc1Mode.None,
            "first" => Fnc1Mode.FirstPosition,
            _ => useLetter ? Fnc1Mode.SecondPosition(letter) : Fnc1Mode.SecondPosition(number),
        };
    }

    /// <summary>
    /// Five kinds in eight stay inside what an Alphanumeric segment holds under FNC1, where the <c>%</c> rule acts.
    /// The others add lowercase, then Latin-1, then kana.
    /// </summary>
    private static string MakeText(Xorshift random)
    {
        var kind = random.Next(8);
        var length = random.Next(100) < 75 ? random.Next(50) : random.Next(160);
        var sb = new StringBuilder();
        while (sb.Length < length)
        {
            var runLength = 1 + random.Next(random.Next(4) == 0 ? 30 : 8);
            switch (random.Next(kind <= 4 ? 5 : kind == 5 ? 6 : kind == 6 ? 7 : 8))
            {
                case 0: Append(sb, random, Digits, runLength); break;
                case 1: Append(sb, random, Capitals, runLength); break;
                case 2: sb.Append('%', 1 + random.Next(3)); break;
                case 3: sb.Append(Gs, 1 + random.Next(2)); break;
                case 4: sb.Append(random.Next(2) == 0 ? "\u001D%" : "%\u001D"); break;
                case 5: Append(sb, random, Lowercase, runLength); break;
                case 6:
                    for (var i = 1 + random.Next(4); i > 0; i--)
                        sb.Append(latin1[random.Next(latin1.Length)]);
                    break;
                default:
                    for (var i = 1 + random.Next(6); i > 0; i--)
                        sb.Append(kana[random.Next(kana.Length)]);
                    break;
            }
        }
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, Xorshift random, string alphabet, int count)
    {
        for (var i = 0; i < count; i++)
            sb.Append(alphabet[random.Next(alphabet.Length)]);
    }

    /// <summary>The text as a C# string literal's content, so a failure can be pasted into a test.</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c == '"' || c == '\\')
                sb.Append('\\').Append(c);
            else if (c is >= ' ' and <= '~')
                sb.Append(c);
            else
                sb.Append("\\u").Append(((int)c).ToString("X4"));
        }
        return sb.ToString();
    }

    /// <summary>xorshift64: the sequence depends on the seed alone, on every runtime.</summary>
    private sealed class Xorshift(ulong seed)
    {
        private ulong _state = seed;

        public int Next(int exclusiveMax)
        {
            _state ^= _state << 13;
            _state ^= _state >> 7;
            _state ^= _state << 17;
            return (int)(_state % (ulong)exclusiveMax);
        }
    }
}
