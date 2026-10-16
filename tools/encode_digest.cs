#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:project ../src/FeatherQR/FeatherQR.csproj

using System.Text;
using FeatherQR;

// Prints one line a case for a fixed pseudo-random corpus of texts and option sets through the three generators: a hash of
// the modules written, "nofit" with the error, or the exception. The corpus is a function of the count and the seed, so the
// same source run against two commits prints the same lines unless the generators changed what they write. That is the check
// for a change meant to leave the output alone: a refactoring, an optimization, or a new option that must change nothing
// while it is unset (the corpus sets every option this file knows, so a new one stays at its default on both sides). An
// option joins the corpus once it is in both commits compared, since a commit without it cannot compile the line that sets it.
//
//   dotnet run tools/encode_digest.cs > new.txt                         this tree
//   git archive <commit> | tar -x -C <dir>                              the commit to compare with, as plain files
//   dotnet run <dir>/tools/encode_digest.cs > base.txt                  copy this file there first if the commit predates it
//   diff base.txt new.txt
//
//   dotnet run tools/encode_digest.cs -- <texts> <seed>                 another size or sequence (defaults 4000 and a fixed seed)
//
// Each case is sized, written in the span form and in the data form, and a Standard QR case in four also as a Structured
// Append set. The run fails when the two forms or the sizing disagree within one tree. The summary and one digest of all
// the lines go to stderr, so two runs can also be compared by that digest alone.
//
// A corpus of generated text finds what a fixed list does not: the defect this was written after (a literal % written twice
// under Optimal without FNC1) differs on 1,930 of the default corpus's 72,928 lines, and no test of the suite failed on it
// at the time.

var textCount = args.Length > 0 ? int.Parse(args[0]) : 4000;
var state = args.Length > 1 ? ulong.Parse(args[1]) : 0x9E3779B97F4A7C15UL;
int Next(int n) { state ^= state << 13; state ^= state >> 7; state ^= state << 17; return (int)(state % (ulong)n); }

const string Digits = "0123456789";
const string Alnum = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
const string Lower = "abcdefghijklmnopqrstuvwxyz";
var punct = new string(['!', '?', '&', '=', '_', '#', '@', ',', ';', '(', ')', '[', ']', '{', '}', '<', '>', '|', '~', '^']);
var latin1 = new string([(char)0xE9, (char)0xFC, (char)0xF1, (char)0xDF, (char)0xBF, (char)0xA1, (char)0xA9, (char)0xAE, (char)0xFF, (char)0xA0]);
var kana = new string([(char)0x3042, (char)0x3044, (char)0x3046, (char)0x30AB, (char)0x30AD, (char)0x65E5, (char)0x672C, (char)0x8A9E, (char)0x6F22, (char)0x5B57, (char)0x6771, (char)0x4EAC, (char)0x3001, (char)0x3002]);
// The characters the generators treat apart: the FNC1 separator, the byte order mark, line ends, text beyond Latin-1, a surrogate pair, the wave dash, the percent sign.
var special = new[] { "\u001D", "\uFEFF", "\r", "\n", "\t", "\u20AC", "\u2713", char.ConvertFromUtf32(0x1F600), "\u301C", "%" };

string Run(string alphabet, int length)
{
    var sb = new StringBuilder(length);
    for (var i = 0; i < length; i++)
        sb.Append(alphabet[Next(alphabet.Length)]);
    return sb.ToString();
}

int Length()
{
    var pick = Next(100);
    if (pick < 55) return Next(81);
    if (pick < 85) return Next(401);
    if (pick < 97) return Next(1201);
    return Next(3201);
}

string MakeText(out int kind)
{
    kind = Next(10);
    var length = Length();
    switch (kind)
    {
        case 0: return Run(Digits, length);
        case 1: return Run(Alnum, length);
        case 2: return Run("%%%AB1 ", length);
        case 3: return Run(Lower + punct, length);
        case 7: return Run(kana, Math.Min(length, 600));
        default:
            {
                // Runs of different alphabets, which is what a mixed-mode plan splits.
                var sb = new StringBuilder();
                while (sb.Length < length)
                {
                    var runLength = 1 + Next(Next(4) == 0 ? 60 : 12);
                    switch (Next(kind == 4 ? 4 : kind == 5 ? 6 : kind == 6 ? 7 : 9))
                    {
                        case 0: sb.Append(Run(Digits, runLength)); break;
                        case 1: sb.Append(Run(Alnum, runLength)); break;
                        case 2: sb.Append(Run(Lower, runLength)); break;
                        case 3: sb.Append(Run(punct, 1 + Next(3))); break;
                        case 4: case 5: sb.Append(Run(latin1, 1 + Next(6))); break;
                        case 6: sb.Append(Run(kana, runLength)); break;
                        default: sb.Append(special[Next(special.Length)]); break;
                    }
                }
                return sb.ToString();
            }
    }
}

// Six cases in ten set no FNC1 mode, which is the route every caller without GS1 data takes.
Fnc1Mode MakeFnc1()
{
    var pick = Next(10);
    var number = Next(100);
    var letter = (char)((Next(2) == 0 ? 'A' : 'a') + Next(26));
    if (pick < 6)
        return Fnc1Mode.None;
    return pick < 8 ? Fnc1Mode.FirstPosition : pick == 8 ? Fnc1Mode.SecondPosition(number) : Fnc1Mode.SecondPosition(letter);
}

static string Name(Fnc1Mode mode) => mode.IsFirstPosition ? "first" : mode.IsSecondPosition ? $"second:{mode.ApplicationIndicator}" : "none";

static ulong Hash(ReadOnlySpan<byte> bytes, ulong h = 14695981039346656037UL)
{
    foreach (var b in bytes)
        h = (h ^ b) * 1099511628211UL;
    return h;
}

static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message.Replace("\r", " ").Replace("\n", " ")}";

static string Escape(string text)
{
    var sb = new StringBuilder();
    foreach (var c in text.Length > 48 ? text.Substring(0, 48) : text)
        sb.Append(c is >= ' ' and <= '~' and not '|' ? c.ToString() : $"\\u{(int)c:X4}");
    return sb.ToString();
}

var eciModes = new[] { EciMode.Default, EciMode.Default, EciMode.Default, EciMode.Default, EciMode.Utf8, EciMode.Iso8859_1 };
var heights = new RmQRHeight?[] { null, null, null, RmQRHeight.H7, RmQRHeight.H9, RmQRHeight.H11, RmQRHeight.H13, RmQRHeight.H15, RmQRHeight.H17 };
int ok = 0, noFit = 0, refused = 0, sets = 0, formsDiffer = 0;
var output = new StringBuilder();

for (var t = 0; t < textCount; t++)
{
    var text = MakeText(out var kind);

    // Standard QR
    for (var k = 0; k < 8; k++)
    {
        var level = (QREccLevel)Next(4);
        var options = new QRCodeGeneratorOptions
        {
            Segmentation = (QRSegmentation)Next(2),
            AllowKanji = Next(2) == 0,
            EciMode = eciModes[Next(eciModes.Length)],
            Utf8Bom = Next(4) == 0,
            BoostEccLevel = Next(4) == 0,
            MaskPattern = Next(3) == 0 ? Next(8) : null,
            QuietZoneSize = Next(3) switch { 0 => 0, 1 => 1, _ => 4 },
            Version = Next(9) switch
            {
                0 => QRVersionRange.Exactly(1 + Next(40)),
                1 => QRVersionRange.AtLeast(1 + Next(40)),
                2 => QRVersionRange.AtMost(1 + Next(40)),
                3 => QRVersionRange.Between(1 + Next(10), 10 + Next(31)),
                _ => QRVersionRange.Any,
            },
            Fnc1 = MakeFnc1(),
        };
        var head = $"Q|{t}.{k}|kind={kind}|len={text.Length}|{level}|{options.Segmentation}|kanji={options.AllowKanji}|eci={options.EciMode}|bom={options.Utf8Bom}|boost={options.BoostEccLevel}|mask={options.MaskPattern}|qz={options.QuietZoneSize}|v={options.Version}|fnc1={Name(options.Fnc1)}|{Escape(text)}";
        string result;
        try
        {
            if (QRCodeGenerator.TryGetRequiredBufferSize(text, level, out var size, options))
            {
                var buffer = new byte[size.BufferSize];
                var written = QRCodeGenerator.Create(text, level, buffer, options);
                var spanHash = Hash(buffer.AsSpan(0, written));
                var data = QRCodeGenerator.Create(text, level, options);
                var modules = new byte[data.Size * data.Size];
                for (var row = 0; row < data.Size; row++)
                    for (var col = 0; col < data.Size; col++)
                        modules[row * data.Size + col] = data[row, col] ? (byte)1 : (byte)0;
                var same = written == modules.Length && Hash(modules) == spanHash && data.Version == size.Version;
                if (!same)
                    formsDiffer++;
                result = $"v{size.Version} {size.Size} {spanHash:X16}{(same ? "" : " FORMS-DIFFER")}";
                ok++;
            }
            else
            {
                noFit++;
                try
                {
                    QRCodeGenerator.Create(text, level, options);
                    result = "nofit, but Create returned";
                    formsDiffer++;
                }
                catch (Exception ex)
                {
                    result = "nofit, " + Describe(ex);
                }
            }
        }
        catch (Exception ex)
        {
            refused++;
            result = Describe(ex);
        }
        output.Append(head).Append(" => ").Append(result).Append('\n');

        // A Structured Append set for one case in four: the set's planner shares the mixed-mode route's helpers.
        if (k % 4 == 0 && text.Length > 0)
        {
            try
            {
                var set = QRCodeGenerator.CreateStructuredAppend(text, level, options);
                var h = 14695981039346656037UL;
                foreach (var symbol in set)
                {
                    var modules = new byte[symbol.Size * symbol.Size];
                    for (var row = 0; row < symbol.Size; row++)
                        for (var col = 0; col < symbol.Size; col++)
                            modules[row * symbol.Size + col] = symbol[row, col] ? (byte)1 : (byte)0;
                    h = Hash(modules, h);
                }
                sets++;
                result = $"{set.Length} symbols v{set[0].Version} {h:X16}";
            }
            catch (Exception ex)
            {
                result = Describe(ex);
            }
            output.Append("S|").Append(t).Append('.').Append(k).Append(" => ").Append(result).Append('\n');
        }
    }

    // rMQR holds 361 digits at most, so longer texts only prove the refusal.
    if (text.Length <= 400)
    {
        for (var k = 0; k < 8; k++)
        {
            var level = (RmQREccLevel)Next(2);
            var options = new RmQRCodeGeneratorOptions
            {
                Segmentation = (RmQRSegmentation)Next(2),
                AllowKanji = Next(2) == 0,
                EciMode = eciModes[Next(eciModes.Length)],
                FitStrategy = (RmQRFitStrategy)Next(3),
                Height = heights[Next(heights.Length)],
                Version = Next(5) == 0 ? (RmQRVersion)(1 + Next(32)) : null,
                QuietZoneSize = Next(3) == 0 ? 0 : 2,
                Fnc1 = MakeFnc1(),
            };
            var head = $"R|{t}.{k}|kind={kind}|len={text.Length}|{level}|{options.Segmentation}|kanji={options.AllowKanji}|eci={options.EciMode}|fit={options.FitStrategy}|h={options.Height}|v={options.Version}|qz={options.QuietZoneSize}|fnc1={Name(options.Fnc1)}|{Escape(text)}";
            string result;
            try
            {
                if (RmQRCodeGenerator.TryGetRequiredBufferSize(text, level, out var size, options))
                {
                    var buffer = new byte[size.BufferSize];
                    var written = RmQRCodeGenerator.Create(text, level, buffer, options);
                    var spanHash = Hash(buffer.AsSpan(0, written));
                    var data = RmQRCodeGenerator.Create(text, level, options);
                    var modules = new byte[data.Width * data.Height];
                    for (var row = 0; row < data.Height; row++)
                        for (var col = 0; col < data.Width; col++)
                            modules[row * data.Width + col] = data[row, col] ? (byte)1 : (byte)0;
                    var same = written == modules.Length && Hash(modules) == spanHash && data.Version == size.Version;
                    if (!same)
                        formsDiffer++;
                    result = $"{size.Version} {spanHash:X16}{(same ? "" : " FORMS-DIFFER")}";
                    ok++;
                }
                else
                {
                    noFit++;
                    try
                    {
                        RmQRCodeGenerator.Create(text, level, options);
                        result = "nofit, but Create returned";
                        formsDiffer++;
                    }
                    catch (Exception ex)
                    {
                        result = "nofit, " + Describe(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                refused++;
                result = Describe(ex);
            }
            output.Append(head).Append(" => ").Append(result).Append('\n');
        }
    }

    // Micro QR holds 35 digits at most.
    if (text.Length <= 40)
    {
        for (var k = 0; k < 4; k++)
        {
            var level = (MicroQREccLevel)Next(4);
            var options = new MicroQRCodeGeneratorOptions
            {
                Segmentation = (MicroQRSegmentation)Next(2),
                AllowKanji = Next(2) == 0,
                MaskPattern = Next(3) == 0 ? Next(4) : null,
                QuietZoneSize = Next(3) == 0 ? 0 : 2,
                Version = Next(4) == 0 ? MicroQRVersionRange.Exactly((MicroQRVersion)(1 + Next(4))) : MicroQRVersionRange.Any,
            };
            var head = $"M|{t}.{k}|kind={kind}|len={text.Length}|{level}|{options.Segmentation}|kanji={options.AllowKanji}|mask={options.MaskPattern}|qz={options.QuietZoneSize}|v={options.Version}|{Escape(text)}";
            string result;
            try
            {
                if (MicroQRCodeGenerator.TryGetRequiredBufferSize(text, level, out var size, options))
                {
                    var buffer = new byte[size.BufferSize];
                    var written = MicroQRCodeGenerator.Create(text, level, buffer, options);
                    var spanHash = Hash(buffer.AsSpan(0, written));
                    var data = MicroQRCodeGenerator.Create(text, level, options);
                    var same = written == size.BufferSize && data.Version == size.Version;
                    for (var row = 0; same && row < size.Size; row++)
                        for (var col = 0; col < size.Size; col++)
                            same &= data[row, col] == (buffer[row * size.Size + col] != 0);
                    if (!same)
                        formsDiffer++;
                    result = $"{size.Version} {size.Size} {spanHash:X16}{(same ? "" : " FORMS-DIFFER")}";
                    ok++;
                }
                else
                {
                    noFit++;
                    try
                    {
                        MicroQRCodeGenerator.Create(text, level, options);
                        result = "nofit, but Create returned";
                        formsDiffer++;
                    }
                    catch (Exception ex)
                    {
                        result = "nofit, " + Describe(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                refused++;
                result = Describe(ex);
            }
            output.Append(head).Append(" => ").Append(result).Append('\n');
        }
    }
}

var lines = output.ToString();
Console.OutputEncoding = Encoding.UTF8;
Console.Out.Write(lines);
Console.Error.WriteLine($"texts {textCount}: encoded {ok}, did not fit {noFit}, refused {refused}, structured append sets {sets}, cases where sizing and the two forms disagree {formsDiffer}");
Console.Error.WriteLine($"digest {Hash(Encoding.UTF8.GetBytes(lines)):X16}");
return formsDiffer == 0 ? 0 : 1;
