using System.Text.Json;
using FeatherQR;
using FeatherQR.Gs1;
using ZXingCpp;
using CppBarcode = ZXingCpp.Barcode;
using CppFormat = ZXingCpp.BarcodeFormat;

namespace QRInteropFixtures;

/// <summary>
/// <c>spot-check-gs1</c>: the symbols the GS1 entry points write, read by the pinned zxing-cpp reader. The inputs
/// are the bracketed and element string inputs of the committed GS1 fixtures, each text once. For every input each
/// size form, in both symbologies, is asked twice: under the options the symbols are written with, and under the
/// smallest version, which most data does not fit, so that the fit decides answers too. Each answer and its size
/// must be what the data layer and the core give together: -1 where the data is not valid or, for a Digital Link
/// form, no URI can carry it, and otherwise what the core sizes the element string under FNC1 or the URI at under
/// the same options. An answer that differs fails the run, so a member that refuses what it should write cannot
/// shorten it. Each input the Standard QR size form takes is written by <c>CreateGs1</c> as a Standard QR symbol
/// and, where the rMQR size form takes it, an rMQR symbol, under <c>Single</c> and <c>Optimal</c> at level M, and
/// zxing-cpp must report <c>]Q3</c>, GS1 content, no ECI header, the element string in <c>TextMode.Plain</c> and,
/// in <c>TextMode.HRI</c>, the (AI) text <c>TryToHri</c> gives without its escapes (zxing-cpp writes a parenthesis
/// in a value as it is), where its own table can split the data (it falls back to escaped text otherwise, and
/// those are counted, not compared). Each input a URI can carry is also written by <c>CreateGs1DigitalLink</c>,
/// and zxing-cpp must report <c>]Q1</c>, text content, no ECI header and the URI. Findings go to the fixture spec.
/// </summary>
public static class Gs1SpotCheck
{
    private const string Gs = "\u001D";
    private const int RmQrQuietZoneModules = 2; // ISO/IEC 23941 quiet zone

    public static int Run(string repoRoot)
    {
        var inputs = LoadInputs(Path.Combine(repoRoot, "tests", "FeatherQR.Tests", "Fixtures", "Gs1"));
        Console.WriteLine($"GS1 spot check: {ZintGs1Oracle.ZxingCppVersion} as the reader, {inputs.Count} distinct bracketed and element string inputs");

        var refused = 0;
        var taken = 0;
        var linked = 0;
        var compared = 0;
        var differing = 0;
        var qr = new Tally();
        var rmqr = new Tally();
        var qrLink = new Tally();
        var rmqrLink = new Tally();
        var versionOne = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1) };

        // One size form's answer against the expected one, each as the symbol's size in bytes, or -1 for false.
        // A difference is reported and fails the run.
        bool Differs(string id, string member, int actual, int expected)
        {
            compared++;
            if (actual == expected)
                return false;
            differing++;
            Console.WriteLine($"FAIL {id}: {member} answers {Describe(actual)} where the data layer and the core answer {Describe(expected)}");
            return true;

            static string Describe(int size) => size < 0 ? "false" : $"{size} bytes";
        }

        foreach (var (id, input) in inputs)
        {
            // The data layer's own verdicts on the text, in the form its first character names.
            var elementString = input;
            var valid = input.StartsWith('(') ? Gs1ElementString.TryFromHri(input, out elementString, out _) : Gs1ElementString.TryValidate(input, out _);
            var uri = "";
            var carried = valid && Gs1ElementString.TryToDigitalLink(elementString, out uri, out _);
            var hri = "";
            if (valid && !Gs1ElementString.TryToHri(elementString, out hri, out var formatError))
                throw new InvalidOperationException($"{id}: an element string the data layer takes does not format ({formatError})");

            // What the core sizes the element string at under FNC1 in first position, and the URI at as plain text.
            int Gs1Size(in QRCodeGeneratorOptions options) => valid && QRCodeGenerator.TryGetRequiredBufferSize(elementString, QREccLevel.M, out var size, options with { Fnc1 = Fnc1Mode.FirstPosition }) ? size.BufferSize : -1;
            int LinkSize(in QRCodeGeneratorOptions options) => carried && QRCodeGenerator.TryGetRequiredBufferSize(uri, QREccLevel.M, out var size, in options) ? size.BufferSize : -1;
            int RmQrGs1Size(in RmQRCodeGeneratorOptions options) => valid && RmQRCodeGenerator.TryGetRequiredBufferSize(elementString, RmQREccLevel.M, out var size, options with { Fnc1 = Fnc1Mode.FirstPosition }) ? size.BufferSize : -1;
            int RmQrLinkSize(in RmQRCodeGeneratorOptions options) => carried && RmQRCodeGenerator.TryGetRequiredBufferSize(uri, RmQREccLevel.M, out var size, in options) ? size.BufferSize : -1;

            // What each size form answers, as its size or -1.
            int Gs1Answer(in QRCodeGeneratorOptions options) => QRCodeGenerator.TryGetRequiredBufferSizeGs1(input, QREccLevel.M, out var size, in options) ? size.BufferSize : -1;
            int LinkAnswer(in QRCodeGeneratorOptions options) => QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(input, QREccLevel.M, out var size, in options) ? size.BufferSize : -1;
            int RmQrGs1Answer(in RmQRCodeGeneratorOptions options) => RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(input, RmQREccLevel.M, out var size, in options) ? size.BufferSize : -1;
            int RmQrLinkAnswer(in RmQRCodeGeneratorOptions options) => RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(input, RmQREccLevel.M, out var size, in options) ? size.BufferSize : -1;

            var takes = Gs1Answer(default);
            var takesAgrees = !Differs(id, "QRCodeGenerator.TryGetRequiredBufferSizeGs1", takes, Gs1Size(default));
            Differs(id, "QRCodeGenerator.TryGetRequiredBufferSizeGs1 (version 1)", Gs1Answer(in versionOne), Gs1Size(in versionOne));
            if (takesAgrees && takes >= 0)
            {
                taken++;
                foreach (var segmentation in new[] { QRSegmentation.Single, QRSegmentation.Optimal })
                {
                    var data = QRCodeGenerator.CreateGs1(input, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0, Segmentation = segmentation });
                    CheckGs1(qr, $"{id} {segmentation} v{data.Version}", FromData(data), CppFormat.QRCode, FixtureWriter.QuietZoneModules, elementString, hri);
                }
            }
            else if (takesAgrees)
            {
                refused++;
            }

            foreach (var segmentation in new[] { RmQRSegmentation.Single, RmQRSegmentation.Optimal })
            {
                var options = new RmQRCodeGeneratorOptions { QuietZoneSize = 0, Segmentation = segmentation };
                var smallest = options with { Version = RmQRVersion.R7x43 };
                var takesRmQr = RmQrGs1Answer(in options);
                var agrees = !Differs(id, $"RmQRCodeGenerator.TryGetRequiredBufferSizeGs1 ({segmentation})", takesRmQr, RmQrGs1Size(in options));
                Differs(id, $"RmQRCodeGenerator.TryGetRequiredBufferSizeGs1 ({segmentation}, R7x43)", RmQrGs1Answer(in smallest), RmQrGs1Size(in smallest));
                if (!agrees)
                    continue;
                if (takesRmQr < 0)
                {
                    if (valid)
                        rmqr.DoesNotFit++;
                    continue;
                }
                var data = RmQRCodeGenerator.CreateGs1(input, RmQREccLevel.M, options);
                CheckGs1(rmqr, $"{id} {segmentation} {data.Version}", FromData(data), CppFormat.RMQRCode, RmQrQuietZoneModules, elementString, hri);
            }

            var links = LinkAnswer(default);
            var linksAgrees = !Differs(id, "QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink", links, LinkSize(default));
            Differs(id, "QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink (version 1)", LinkAnswer(in versionOne), LinkSize(in versionOne));
            if (linksAgrees && links >= 0)
            {
                linked++;
                var link = QRCodeGenerator.CreateGs1DigitalLink(input, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
                CheckLink(qrLink, $"{id} v{link.Version}", FromData(link), CppFormat.QRCode, FixtureWriter.QuietZoneModules, uri);
            }

            var rmqrOptions = new RmQRCodeGeneratorOptions { QuietZoneSize = 0 };
            var rmqrSmallest = rmqrOptions with { Version = RmQRVersion.R7x43 };
            var linksRmQr = RmQrLinkAnswer(in rmqrOptions);
            var linksRmQrAgrees = !Differs(id, "RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink", linksRmQr, RmQrLinkSize(in rmqrOptions));
            Differs(id, "RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink (R7x43)", RmQrLinkAnswer(in rmqrSmallest), RmQrLinkSize(in rmqrSmallest));
            if (!linksRmQrAgrees)
                continue;
            if (linksRmQr < 0)
            {
                if (carried)
                    rmqrLink.DoesNotFit++;
                continue;
            }
            var rmqrData = RmQRCodeGenerator.CreateGs1DigitalLink(input, RmQREccLevel.M, rmqrOptions);
            CheckLink(rmqrLink, $"{id} {rmqrData.Version}", FromData(rmqrData), CppFormat.RMQRCode, RmQrQuietZoneModules, uri);
        }

        Console.WriteLine($"{taken} inputs taken, {refused} refused, {linked} with a Digital Link URI, {differing} of {compared} size form answers that differ from the data layer's and the core's");
        Console.WriteLine($"CreateGs1, Standard QR: {qr.Describe()}");
        Console.WriteLine($"CreateGs1, rMQR: {rmqr.Describe()}");
        Console.WriteLine($"CreateGs1DigitalLink, Standard QR: {qrLink.Describe()}");
        Console.WriteLine($"CreateGs1DigitalLink, rMQR: {rmqrLink.Describe()}");
        return differing + qr.Failures + rmqr.Failures + qrLink.Failures + rmqrLink.Failures == 0 ? 0 : 1;
    }

    /// <summary>A GS1 symbol: the mode's identifier, GS1 content, the element string, and the (AI) text where zxing-cpp's table splits the data.</summary>
    private static void CheckGs1(Tally tally, string label, ModuleMatrix matrix, CppFormat format, int quietZone, string elementString, string hri)
    {
        tally.Symbols++;
        var plain = Read(matrix, format, quietZone, TextMode.Plain, out var plainFault);
        if (plain is null || plain.SymbologyIdentifier != "]Q3" || plain.ContentType != ContentType.GS1 || plain.HasECI || plain.Text != elementString)
        {
            tally.Fail($"{label}: Plain read {plainFault ?? Describe(plain!)}, expected ]Q3 GS1 \"{Fnc1Writers.Escape(elementString)}\"");
            return;
        }

        var formatted = Read(matrix, format, quietZone, TextMode.HRI, out var hriFault);
        if (formatted is null)
        {
            tally.Fail($"{label}: HRI read {hriFault}");
            return;
        }

        // zxing-cpp falls back to the raw text, and keeps a raw separator in its (AI) text, where its own table could not split.
        if (!formatted.Text.StartsWith('(') || formatted.Text.Contains("<GS>", StringComparison.Ordinal) || formatted.Text.Contains(Gs, StringComparison.Ordinal))
        {
            tally.NotSplit++;
            return;
        }

        // This library escapes an opening parenthesis inside a value, zxing-cpp does not.
        tally.Compared++;
        if (formatted.Text != hri.Replace("\\(", "("))
            tally.Fail($"{label}: HRI read \"{formatted.Text}\", expected \"{hri}\"");
    }

    /// <summary>A Digital Link symbol: plain text, the URI.</summary>
    private static void CheckLink(Tally tally, string label, ModuleMatrix matrix, CppFormat format, int quietZone, string uri)
    {
        tally.Symbols++;
        var plain = Read(matrix, format, quietZone, TextMode.Plain, out var fault);
        if (plain is null || plain.SymbologyIdentifier != "]Q1" || plain.ContentType != ContentType.Text || plain.HasECI || plain.Text != uri)
            tally.Fail($"{label}: read {fault ?? Describe(plain!)}, expected ]Q1 Text \"{uri}\"");
    }

    private static CppBarcode? Read(ModuleMatrix matrix, CppFormat format, int quietZone, TextMode textMode, out string? fault)
    {
        fault = null;
        try
        {
            var (luminance, width, height) = Fnc1Writers.Render(matrix, quietZone, 4);
            var results = ZXingCppCalls.Read(new BarcodeReader { Formats = format, TryHarder = true, TextMode = textMode }, luminance, width, height);
            if (results.Length == 1 && results[0].IsValid)
                return results[0];
            fault = results.Length == 1 ? $"invalid: {results[0].ErrorMsg}" : $"{results.Length} results";
        }
        catch (Exception ex)
        {
            fault = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0].TrimEnd('\r')}";
        }
        return null;
    }

    private static string Describe(CppBarcode barcode)
        => $"{barcode.SymbologyIdentifier} {barcode.ContentType}{(barcode.HasECI ? " ECI" : "")} \"{Fnc1Writers.Escape(barcode.Text)}\"";

    /// <summary>The bracketed and element string inputs of the committed GS1 fixtures, each text once, under the first case id that has it.</summary>
    private static List<(string Id, string Input)> LoadInputs(string gs1Root)
    {
        var inputs = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in new[] { "corpus", "syntax-engine" })
        {
            foreach (var path in Directory.GetFiles(Path.Combine(gs1Root, directory), "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty("cases", out var cases))
                    continue;
                foreach (var c in cases.EnumerateArray())
                {
                    if (!c.TryGetProperty("inputKind", out var kind) || kind.GetString() is not ("Bracketed" or "ElementString"))
                        continue;
                    var input = c.GetProperty("input").GetString() ?? "";
                    if (seen.Add(input))
                        inputs.Add((c.GetProperty("id").GetString() ?? "", input));
                }
            }
        }
        return inputs;
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

    private static ModuleMatrix FromData(RmQRCodeData data)
    {
        var modules = new byte[data.Width * data.Height];
        for (var row = 0; row < data.Height; row++)
            for (var col = 0; col < data.Width; col++)
                modules[row * data.Width + col] = data[row, col] ? (byte)1 : (byte)0;
        return new ModuleMatrix(modules, data.Width, data.Height);
    }

    private sealed class Tally
    {
        public int Symbols;
        public int Failures;
        public int Compared;
        public int NotSplit;
        public int DoesNotFit;

        public void Fail(string message)
        {
            Failures++;
            Console.WriteLine($"FAIL {message}");
        }

        public string Describe()
            => $"{Symbols} symbols, {Failures} failures"
                + (Compared > 0 ? $", {Compared} with the (AI) text compared" : "")
                + (NotSplit > 0 ? $", {NotSplit} whose (AI) text zxing-cpp's table could not split" : "")
                + (DoesNotFit > 0 ? $", {DoesNotFit} that fit no rMQR symbol and were skipped" : "");
    }
}
