using System.Text;
using FeatherQR;
using ZXingCpp;
using CppBarcode = ZXingCpp.Barcode;
using CppFormat = ZXingCpp.BarcodeFormat;

namespace QRInteropFixtures;

/// <summary>
/// <c>sweep-fnc1</c>: generated content written under FNC1 by both generators and read back by the pinned readers.
/// Where <c>spot-check-fnc1</c> reads a fixed list of cases, this reads many generated ones: element strings built
/// from the GS1 Syntax Dictionary (each AI with a value of its components' types and lengths, the separator after a
/// value another follows unless the AI is of predefined length), and sequences chosen to stress the <c>%</c> rule
/// (a literal <c>%</c> and the separator U+001D in every order, doubled, at the ends, beside digits, capitals and
/// lowercase), with Latin-1, UTF-8 and Kanji content beside them. Each text is written as a Standard QR and an
/// rMQR symbol under <see cref="QRSegmentation.Single"/> and <see cref="QRSegmentation.Optimal"/> with the other
/// options drawn at random. zxing-cpp must read each with the mode's identifier, GS1 content for first position, the
/// ECI flag and the text. ZXing.Net must read each Standard QR symbol in first position (it has no rMQR reader).
/// A symbol ZXing.Net returns nothing for is diagnosed before it counts as a failure: a read of the same modules
/// with its detector skipped, or of the same stream under another mask, makes it a miss of that reader.
/// This library's decoders must return the text and the mode. The sequence is a function of the seed, so a failure
/// is reproduced by its case number.
/// </summary>
public static class Fnc1Sweep
{
    private const char Gs = '\u001D';
    private const int RmQrQuietZoneModules = 2; // ISO/IEC 23941 quiet zone
    private const string Digits = "0123456789";
    private const string Capitals = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lowercase = "abcdefghijklmnopqrstuvwxyz";

    // GS1 character set 82 apart from letters and digits (General Specifications 7.11), which holds the percent sign.
    private const string Set82Punctuation = "!\"%&'()*+,-./:;<=>?_";
    private const string Set39Punctuation = "#-/";
    private const string AlphanumericPunctuation = " $*+-./:";

    // Kana and kanji that have a Kanji-mode cell, so a text of these alone is one Kanji segment under AllowKanji.
    private static readonly char[] kanjiCells = [(char)0x3042, (char)0x3044, (char)0x30AB, (char)0x30CA, (char)0x65E5, (char)0x672C, (char)0x8A9E, (char)0x6F22, (char)0x5B57];
    private static readonly char[] latin1Letters = [(char)0xE9, (char)0xFC, (char)0xF1, (char)0xDF];

    private sealed record SweepText(string Family, string Text);

    private sealed class Tally
    {
        public int Symbols, NoFit, Mismatches, Unreadable, ReaderMisses;
    }

    public static int Run(string repoRoot, int textCount, ulong seed)
    {
        _ = KanjiPayload.ShiftJis; // registers the code pages ZXing.Net needs for Shift_JIS
        var dictionary = new Gs1SyntaxDictionary(Path.Combine(repoRoot, "tests", "FeatherQR.Tests", "Fixtures", "Gs1", "syntax-dictionary", "gs1-syntax-dictionary.txt"));
        var random = new Xorshift(seed);
        var cpp = new SortedDictionary<string, Tally>(StringComparer.Ordinal);
        var net = new SortedDictionary<string, Tally>(StringComparer.Ordinal);
        var own = new SortedDictionary<string, Tally>(StringComparer.Ordinal);
        var families = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var failures = new List<string>();
        var notes = new List<string>();
        var detectorMisses = 0;

        Console.WriteLine($"FNC1 sweep: {Fnc1Writers.ZintVersion} as the zxing-cpp reader, {Fnc1Writers.ZXingNetVersion}, GS1 Syntax Dictionary {dictionary.Release}");
        Console.WriteLine($"{textCount} texts, seed {seed}");

        for (var index = 0; index < textCount; index++)
        {
            var sample = MakeText(random, dictionary);
            families[sample.Family] = families.GetValueOrDefault(sample.Family) + 1;
            var text = sample.Text;
            // Read from the text: the family of content beyond ASCII can draw kana and kanji alone.
            var allKanji = text.Length > 0 && text.All(c => Array.IndexOf(kanjiCells, c) >= 0);

            // One mode, charset and Kanji choice a text, so its Single and Optimal symbols differ in the segmentation alone.
            var mode = random.Next(100) < 65
                ? Fnc1Mode.FirstPosition
                : random.Next(2) == 0 ? Fnc1Mode.SecondPosition(random.Next(100)) : Fnc1Mode.SecondPosition((char)((random.Next(2) == 0 ? 'A' : 'a') + random.Next(26)));
            var allowKanji = sample.Family == "kanji" ? random.Next(4) != 0 : random.Next(4) == 0;
            var eciPick = random.Next(20);
            var eci = eciPick < 3 ? EciMode.Utf8 : eciPick == 3 && text.All(c => c <= 0xFF) ? EciMode.Iso8859_1 : EciMode.Default;
            var hasNonAscii = text.Any(c => c > 0x7F);
            // A charset is declared when one is requested, and for content beyond ASCII unless it is written in Kanji mode.
            var expectsEci = eci != EciMode.Default || (hasNonAscii && !(allKanji && allowKanji));
            var expectedIdentifier = mode.IsFirstPosition ? "]Q3" : "]Q5";
            var expectedText = mode.IsSecondPosition ? Fnc1Writers.FormatIndicator(mode.ApplicationIndicator!.Value) + text : text;
            var modeName = mode.IsFirstPosition ? "first" : "second";

            foreach (var optimal in new[] { false, true })
            {
                var segmentationName = optimal ? "Optimal" : "Single";

                // Standard QR
                var qrLevel = (QREccLevel)random.Next(4);
                var qrOptions = new QRCodeGeneratorOptions
                {
                    Fnc1 = mode,
                    Segmentation = optimal ? QRSegmentation.Optimal : QRSegmentation.Single,
                    EciMode = eci,
                    AllowKanji = allowKanji,
                    BoostEccLevel = random.Next(5) == 0,
                    MaskPattern = random.Next(5) == 0 ? random.Next(8) : null,
                    Version = random.Next(7) == 0 ? QRVersionRange.AtLeast(1 + random.Next(15)) : QRVersionRange.Any,
                    QuietZoneSize = 0,
                };
                var qrRoute = $"Standard QR {modeName,-6} {segmentationName}";
                var qrCase = $"#{index} {sample.Family} {qrRoute} {qrLevel} eci={eci} kanji={allowKanji} boost={qrOptions.BoostEccLevel} mask={qrOptions.MaskPattern?.ToString() ?? "auto"} version={qrOptions.Version} mode={Describe(mode)} text=\"{Escape(text)}\"";
                if (!QRCodeGenerator.TryGetRequiredBufferSize(text, qrLevel, out _, qrOptions))
                {
                    Of(cpp, qrRoute).NoFit++;
                }
                else
                {
                    var data = QRCodeGenerator.Create(text, qrLevel, qrOptions);
                    var matrix = FromData(data);

                    var ownTally = Of(own, qrRoute);
                    ownTally.Symbols++;
                    if (!QRCodeDecoder.TryDecode(data, out var decoded, out var info))
                        Fail(ownTally, failures, $"this library: {info.Status} | {qrCase}");
                    else if (decoded != text || info.Fnc1 != mode)
                        Fail(ownTally, failures, $"this library: read {Describe(info.Fnc1)} \"{Escape(decoded)}\" | {qrCase}");

                    var cppTally = Of(cpp, qrRoute);
                    cppTally.Symbols++;
                    var cppResult = ReadCpp(matrix, CppFormat.QRCode, FixtureWriter.QuietZoneModules);
                    if (cppResult is null)
                    {
                        cppTally.Unreadable++;
                        failures.Add($"zxing-cpp: no read at 4 or 8 px/module | {qrCase}");
                    }
                    else if (!Matches(cppResult, mode, expectedIdentifier, expectsEci, expectedText))
                    {
                        Fail(cppTally, failures, $"zxing-cpp: {Describe(cppResult)}, expected {expectedIdentifier}{(expectsEci ? " ECI" : "")} | {qrCase}");
                    }

                    if (mode.IsFirstPosition)
                    {
                        var netTally = Of(net, qrRoute);
                        netTally.Symbols++;
                        ZXing.Result? netResult = null;
                        string? netError = null;
                        try
                        {
                            // ZXing.Net's reader misses some clean renders at 4 pixels a module.
                            netResult = Fnc1Writers.ReadNet(matrix) ?? Fnc1Writers.ReadNet(matrix, pixelsPerModule: 8);
                        }
                        catch (Exception ex)
                        {
                            netError = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0].TrimEnd('\r')}";
                        }

                        if (netError is not null)
                        {
                            Fail(netTally, failures, $"ZXing.Net: {netError} | {qrCase}");
                        }
                        else if (netResult is null && Fnc1Writers.ReadNet(matrix, pureBarcode: true) is { } pure && pure.Text == text)
                        {
                            // Its decoder reads these modules once its detector is skipped, so the miss is the detector's.
                            netTally.ReaderMisses++;
                            detectorMisses++;
                            notes.Add($"ZXing.Net: its detector misses the render of a version {data.Version} symbol under mask {info.MaskPattern}, its decoder reads the modules | {qrCase}");
                        }
                        else if (netResult is null)
                        {
                            // Which masks of the same stream it reads tells a reader miss (some masks read) from a symbol it cannot read (none).
                            var readMasks = new List<int>();
                            for (var mask = 0; mask < 8; mask++)
                            {
                                var masked = FromData(QRCodeGenerator.Create(text, qrLevel, qrOptions with { MaskPattern = mask }));
                                if ((Fnc1Writers.ReadNet(masked) ?? Fnc1Writers.ReadNet(masked, pixelsPerModule: 8))?.Text == text)
                                    readMasks.Add(mask);
                            }
                            if (readMasks.Count > 0)
                            {
                                netTally.ReaderMisses++;
                                notes.Add($"ZXing.Net: no read under mask {info.MaskPattern} of a version {data.Version} symbol, reads the same stream under masks {string.Join(",", readMasks)} | {qrCase}");
                            }
                            else
                            {
                                netTally.Unreadable++;
                                failures.Add($"ZXing.Net: no read under any mask | {qrCase}");
                            }
                        }
                        else
                        {
                            var identifier = netResult.ResultMetadata is not null && netResult.ResultMetadata.TryGetValue(ZXing.ResultMetadataType.SYMBOLOGY_IDENTIFIER, out var value) ? value?.ToString() ?? "" : "";
                            // ZXing.Net reports ]Q4 for first position under an ECI header where zxing-cpp reports ]Q3 with HasECI.
                            if (netResult.Text != text || identifier != (expectsEci ? "]Q4" : "]Q3"))
                                Fail(netTally, failures, $"ZXing.Net: {identifier} \"{Escape(netResult.Text ?? "")}\", expected {(expectsEci ? "]Q4" : "]Q3")} | {qrCase}");
                        }
                    }
                }

                // rMQR
                var rmLevel = (RmQREccLevel)random.Next(2);
                var rmOptions = new RmQRCodeGeneratorOptions
                {
                    Fnc1 = mode,
                    Segmentation = optimal ? RmQRSegmentation.Optimal : RmQRSegmentation.Single,
                    EciMode = eci,
                    AllowKanji = allowKanji,
                    FitStrategy = (RmQRFitStrategy)random.Next(3),
                    Height = random.Next(4) == 0 ? (RmQRHeight)(7 + 2 * random.Next(6)) : null,
                    QuietZoneSize = 0,
                };
                var rmRoute = $"rMQR        {modeName,-6} {segmentationName}";
                var rmCase = $"#{index} {sample.Family} {rmRoute} {rmLevel} eci={eci} kanji={allowKanji} fit={rmOptions.FitStrategy} height={rmOptions.Height?.ToString() ?? "any"} mode={Describe(mode)} text=\"{Escape(text)}\"";
                if (!RmQRCodeGenerator.TryGetRequiredBufferSize(text, rmLevel, out _, rmOptions))
                {
                    Of(cpp, rmRoute).NoFit++;
                    continue;
                }

                var rmData = RmQRCodeGenerator.Create(text, rmLevel, rmOptions);
                var rmMatrix = new ModuleMatrix(ModulesOf(rmData), rmData.Width, rmData.Height);

                var rmOwnTally = Of(own, rmRoute);
                rmOwnTally.Symbols++;
                if (!RmQRCodeDecoder.TryDecode(rmData, out var rmDecoded, out var rmInfo))
                    Fail(rmOwnTally, failures, $"this library: {rmInfo.Status} | {rmCase}");
                else if (rmDecoded != text || rmInfo.Fnc1 != mode)
                    Fail(rmOwnTally, failures, $"this library: read {Describe(rmInfo.Fnc1)} \"{Escape(rmDecoded)}\" | {rmCase}");

                var rmCppTally = Of(cpp, rmRoute);
                rmCppTally.Symbols++;
                var rmCppResult = ReadCpp(rmMatrix, CppFormat.RMQRCode, RmQrQuietZoneModules);
                if (rmCppResult is null)
                {
                    rmCppTally.Unreadable++;
                    failures.Add($"zxing-cpp: no read of {rmData.Version} at 4 or 8 px/module | {rmCase}");
                }
                else if (!Matches(rmCppResult, mode, expectedIdentifier, expectsEci, expectedText))
                {
                    Fail(rmCppTally, failures, $"zxing-cpp: {Describe(rmCppResult)}, expected {expectedIdentifier}{(expectsEci ? " ECI" : "")} | {rmCase}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("texts by family: " + string.Join(", ", families.Select(f => $"{f.Key} {f.Value}")));
        Report("zxing-cpp", cpp, showNoFit: true);
        Report("ZXing.Net (Standard QR, first position)", net, showNoFit: false);
        Report("this library's decoders", own, showNoFit: false);

        if (notes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"reader misses ({notes.Count}): {detectorMisses} where ZXing.Net's decoder reads the modules its detector did not find, the rest read under another mask");
            foreach (var note in notes.Take(40))
                Console.WriteLine("  " + note);
        }

        Console.WriteLine();
        Console.WriteLine($"failures: {failures.Count}");
        foreach (var failure in failures.Take(60))
            Console.WriteLine("  FAIL " + failure);
        return failures.Count == 0 ? 0 : 1;
    }

    private static Tally Of(SortedDictionary<string, Tally> tallies, string route)
    {
        if (!tallies.TryGetValue(route, out var tally))
            tallies[route] = tally = new Tally();
        return tally;
    }

    private static void Fail(Tally tally, List<string> failures, string message)
    {
        tally.Mismatches++;
        failures.Add(message);
    }

    private static void Report(string reader, SortedDictionary<string, Tally> tallies, bool showNoFit)
    {
        Console.WriteLine();
        Console.WriteLine($"{reader}:");
        Console.WriteLine($"  {"route",-28} {"symbols",8} {"matched",8} {"mismatch",9} {"no read",8} {"reader miss",12}{(showNoFit ? "   did not fit" : "")}");
        foreach (var (route, t) in tallies)
        {
            var matched = t.Symbols - t.Mismatches - t.Unreadable - t.ReaderMisses;
            Console.WriteLine($"  {route,-28} {t.Symbols,8} {matched,8} {t.Mismatches,9} {t.Unreadable,8} {t.ReaderMisses,12}{(showNoFit ? $"   {t.NoFit,11}" : "")}");
        }
        var total = new Tally();
        foreach (var t in tallies.Values)
        {
            total.Symbols += t.Symbols;
            total.Mismatches += t.Mismatches;
            total.Unreadable += t.Unreadable;
            total.ReaderMisses += t.ReaderMisses;
            total.NoFit += t.NoFit;
        }
        Console.WriteLine($"  {"total",-28} {total.Symbols,8} {total.Symbols - total.Mismatches - total.Unreadable - total.ReaderMisses,8} {total.Mismatches,9} {total.Unreadable,8} {total.ReaderMisses,12}{(showNoFit ? $"   {total.NoFit,11}" : "")}");
    }

    /// <summary>zxing-cpp over a clean render, at 8 pixels a module when 4 gives nothing. <c>null</c> when neither reads one symbol.</summary>
    private static CppBarcode? ReadCpp(ModuleMatrix matrix, CppFormat format, int quietZone)
    {
        foreach (var pixelsPerModule in new[] { 4, 8 })
        {
            var results = Fnc1Writers.ReadCpp(matrix, format, quietZone, pixelsPerModule);
            if (results.Length == 1 && results[0].IsValid)
                return results[0];
        }
        return null;
    }

    private static bool Matches(CppBarcode barcode, Fnc1Mode mode, string expectedIdentifier, bool expectsEci, string expectedText)
        => barcode.SymbologyIdentifier == expectedIdentifier
            && (!mode.IsFirstPosition || barcode.ContentType == ContentType.GS1)
            && barcode.HasECI == expectsEci
            && barcode.Text == expectedText;

    private static string Describe(CppBarcode barcode)
        => $"{barcode.SymbologyIdentifier} {barcode.ContentType}{(barcode.HasECI ? " ECI" : "")} \"{Escape(barcode.Text)}\"";

    private static string Describe(Fnc1Mode mode)
        => mode.IsFirstPosition ? "first" : mode.IsSecondPosition ? $"second({Fnc1Writers.FormatIndicator(mode.ApplicationIndicator!.Value)})" : "none";

    /// <summary>The text as a C# string literal's content, so a failure line can be pasted into a test.</summary>
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

    // Text generation

    private static SweepText MakeText(Xorshift random, Gs1SyntaxDictionary dictionary)
    {
        var pick = random.Next(100);
        if (pick < 40)
            return new SweepText("gs1", MakeElementString(random, dictionary));
        if (pick < 70)
            return new SweepText("percent-and-separator", MakeAdversarial(random));
        if (pick < 80)
            return new SweepText("alphanumeric", MakeRuns(random, Digits + Capitals + AlphanumericPunctuation, percentOneIn: 8, separatorOneIn: 8));
        if (pick < 86)
            return new SweepText("numeric", MakeRuns(random, Digits, percentOneIn: 0, separatorOneIn: 6));
        if (pick < 93)
            return new SweepText("byte", MakeRuns(random, Lowercase + Digits + Set82Punctuation, percentOneIn: 6, separatorOneIn: 6));
        if (pick < 97)
            return new SweepText("beyond-ascii", MakeBeyondAscii(random));

        var kanji = new StringBuilder();
        var length = 1 + random.Next(20);
        for (var i = 0; i < length; i++)
            kanji.Append(kanjiCells[random.Next(kanjiCells.Length)]);
        return new SweepText("kanji", kanji.ToString());
    }

    /// <summary>
    /// One to five elements of AIs drawn from the dictionary. A value has its components' types and lengths, not
    /// their linters' rules: the symbol layer carries any element string, and the data layer's checks have their own
    /// corpus. The separator follows a value another element follows unless the AI is of predefined length, and one
    /// text in eight ends on a separator or carries a doubled one, which data from the field does.
    /// </summary>
    private static string MakeElementString(Xorshift random, Gs1SyntaxDictionary dictionary)
    {
        var sb = new StringBuilder();
        var elements = 1 + random.Next(5);
        // One alphabet policy a text: capitals keep a value inside Alphanumeric mode, the full set 82 does not.
        var capitalsOnly = random.Next(2) == 0;
        for (var e = 0; e < elements; e++)
        {
            var entry = dictionary.Entries[random.Next(dictionary.Entries.Count)];
            var first = int.Parse(entry.FirstAi);
            var last = int.Parse(entry.LastAi);
            sb.Append((first + random.Next(last - first + 1)).ToString().PadLeft(entry.FirstAi.Length, '0'));
            foreach (var component in entry.Components)
            {
                if (component.Optional && random.Next(2) == 0)
                    continue;
                var length = component.IsVariable ? component.Min + random.Next(Math.Min(component.Max, 24) - component.Min + 1) : component.Max;
                var alphabet = component.Type switch
                {
                    'N' => Digits,
                    'Y' => Digits + Capitals + Set39Punctuation,
                    'Z' => Digits + Capitals + Lowercase + "-_",
                    _ => capitalsOnly ? Digits + Capitals + "%*+-./:" : Digits + Capitals + Lowercase + Set82Punctuation,
                };
                for (var i = 0; i < length; i++)
                    sb.Append(alphabet[random.Next(alphabet.Length)]);
            }
            if (!entry.PredefinedLength && e < elements - 1)
                sb.Append(Gs);
        }
        if (random.Next(8) == 0)
            sb.Append(Gs);
        if (random.Next(8) == 0 && sb.Length > 2)
            sb.Insert(1 + random.Next(sb.Length - 1), Gs);
        return sb.ToString();
    }

    /// <summary>
    /// Tokens that meet the <c>%</c> rule from every side: the percent sign and the separator alone, doubled, tripled
    /// and beside each other in both orders, between short runs of digits and capitals. Two texts in three stay
    /// inside what an Alphanumeric segment can hold under FNC1, the third adds lowercase.
    /// </summary>
    private static string MakeAdversarial(Xorshift random)
    {
        var withLowercase = random.Next(3) == 0;
        var sb = new StringBuilder();
        var tokens = 1 + random.Next(random.Next(4) == 0 ? 60 : 16);
        for (var i = 0; i < tokens; i++)
        {
            switch (random.Next(withLowercase ? 13 : 12))
            {
                case 0: sb.Append('%'); break;
                case 1: sb.Append("%%"); break;
                case 2: sb.Append("%%%"); break;
                case 3: sb.Append(Gs); break;
                case 4: sb.Append(Gs).Append(Gs); break;
                case 5: sb.Append(Gs).Append('%'); break;
                case 6: sb.Append('%').Append(Gs); break;
                case 7: sb.Append(Gs).Append("%%").Append(Gs); break;
                case 8: case 9: Append(sb, random, Capitals, 1 + random.Next(4)); break;
                case 10: case 11: Append(sb, random, Digits, 1 + random.Next(6)); break;
                default: Append(sb, random, Lowercase, 1 + random.Next(3)); break;
            }
        }
        return sb.ToString();
    }

    private static string MakeRuns(Xorshift random, string alphabet, int percentOneIn, int separatorOneIn)
    {
        var sb = new StringBuilder();
        var length = 1 + random.Next(random.Next(5) == 0 ? 300 : 70);
        while (sb.Length < length)
        {
            if (percentOneIn > 0 && random.Next(percentOneIn) == 0)
                sb.Append('%');
            else if (separatorOneIn > 0 && random.Next(separatorOneIn) == 0)
                sb.Append(Gs);
            else
                Append(sb, random, alphabet, 1 + random.Next(10));
        }
        return sb.ToString();
    }

    /// <summary>Latin-1 letters (written behind ECI 3) or kana and kanji beside ASCII (behind ECI 26), with the two FNC1 characters among them.</summary>
    private static string MakeBeyondAscii(Xorshift random)
    {
        var latin1 = random.Next(2) == 0;
        var sb = new StringBuilder();
        var tokens = 2 + random.Next(12);
        for (var i = 0; i < tokens; i++)
        {
            switch (random.Next(6))
            {
                case 0: sb.Append('%'); break;
                case 1: sb.Append(Gs); break;
                case 2: Append(sb, random, Digits, 1 + random.Next(6)); break;
                case 3: Append(sb, random, Capitals, 1 + random.Next(4)); break;
                default:
                    for (var k = 1 + random.Next(4); k > 0; k--)
                        sb.Append(latin1 ? latin1Letters[random.Next(latin1Letters.Length)] : kanjiCells[random.Next(kanjiCells.Length)]);
                    break;
            }
        }
        // The family promises content beyond ASCII whatever the tokens drew.
        sb.Append(latin1 ? latin1Letters[0] : kanjiCells[0]);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, Xorshift random, string alphabet, int count)
    {
        for (var i = 0; i < count; i++)
            sb.Append(alphabet[random.Next(alphabet.Length)]);
    }

    private static byte[] ModulesOf(RmQRCodeData data)
    {
        var modules = new byte[data.Width * data.Height];
        for (var row = 0; row < data.Height; row++)
            for (var col = 0; col < data.Width; col++)
                modules[row * data.Width + col] = data[row, col] ? (byte)1 : (byte)0;
        return modules;
    }

    private static ModuleMatrix FromData(QRCodeData data)
    {
        var size = data.Size;
        var modules = new byte[size * size];
        for (var row = 0; row < size; row++)
            for (var col = 0; col < size; col++)
                modules[row * size + col] = data[row, col] ? (byte)1 : (byte)0;
        return new ModuleMatrix(modules, size, size);
    }

    /// <summary>xorshift64: the sequence depends on the seed alone, on every runtime.</summary>
    private sealed class Xorshift(ulong seed)
    {
        private ulong _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

        public int Next(int exclusiveMax)
        {
            _state ^= _state << 13;
            _state ^= _state >> 7;
            _state ^= _state << 17;
            return (int)(_state % (ulong)exclusiveMax);
        }
    }
}
