using System.Text.Json;
using FeatherQR;
using FeatherQR.Gs1;
using ZXingCpp;
using CppBarcode = ZXingCpp.Barcode;
using CppFormat = ZXingCpp.BarcodeFormat;

namespace QRInteropFixtures;

/// <summary>
/// <c>spot-check-gs1</c>: the symbols the GS1 entry points write, read by the pinned zxing-cpp reader. The inputs
/// are the bracketed and element string inputs of the committed GS1 fixtures that the entry points take. Each is
/// written by <c>CreateGs1</c> as a Standard QR symbol and, where it fits, an rMQR symbol, under <c>Single</c> and
/// <c>Optimal</c> at level M, and zxing-cpp must report <c>]Q3</c>, GS1 content, no ECI header, the element string
/// in <c>TextMode.Plain</c> and, in <c>TextMode.HRI</c>, the (AI) text <c>TryToHri</c> gives, where its own table
/// can split the data (it falls back to escaped text otherwise, and those are counted, not compared). Each input a
/// URI can carry is also written by <c>CreateGs1DigitalLink</c>, and zxing-cpp must report <c>]Q1</c>, text
/// content, no ECI header and the URI. Findings go to the fixture spec and the GS1 plan.
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
        var qr = new Tally();
        var rmqr = new Tally();
        var qrLink = new Tally();
        var rmqrLink = new Tally();
        foreach (var (id, input) in inputs)
        {
            if (!QRCodeGenerator.TryGetRequiredBufferSizeGs1(input, QREccLevel.M, out _))
            {
                refused++;
                continue;
            }

            taken++;
            var elementString = input;
            if (input[0] == '(' && !Gs1ElementString.TryFromHri(input, out elementString, out var hriError))
                throw new InvalidOperationException($"{id}: the entry point takes (AI) text the data layer refuses ({hriError})");
            if (!Gs1ElementString.TryToHri(elementString, out var hri, out var formatError))
                throw new InvalidOperationException($"{id}: an element string the entry point takes does not format ({formatError})");

            foreach (var segmentation in new[] { QRSegmentation.Single, QRSegmentation.Optimal })
            {
                var data = QRCodeGenerator.CreateGs1(input, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0, Segmentation = segmentation });
                CheckGs1(qr, $"{id} {segmentation} v{data.Version}", FromData(data), CppFormat.QRCode, FixtureWriter.QuietZoneModules, elementString, hri);
            }

            foreach (var segmentation in new[] { RmQRSegmentation.Single, RmQRSegmentation.Optimal })
            {
                var options = new RmQRCodeGeneratorOptions { QuietZoneSize = 0, Segmentation = segmentation };
                if (!RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(input, RmQREccLevel.M, out _, options))
                {
                    rmqr.DoesNotFit++;
                    continue;
                }
                var data = RmQRCodeGenerator.CreateGs1(input, RmQREccLevel.M, options);
                CheckGs1(rmqr, $"{id} {segmentation} {data.Version}", FromData(data), CppFormat.RMQRCode, RmQrQuietZoneModules, elementString, hri);
            }

            if (!QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(input, QREccLevel.M, out _))
                continue;

            linked++;
            if (!Gs1ElementString.TryToDigitalLink(elementString, out var uri, out var linkError))
                throw new InvalidOperationException($"{id}: the entry point builds a URI the data layer does not ({linkError})");
            var link = QRCodeGenerator.CreateGs1DigitalLink(input, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
            CheckLink(qrLink, $"{id} v{link.Version}", FromData(link), CppFormat.QRCode, FixtureWriter.QuietZoneModules, uri);

            var rmqrOptions = new RmQRCodeGeneratorOptions { QuietZoneSize = 0 };
            if (!RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(input, RmQREccLevel.M, out _, rmqrOptions))
            {
                rmqrLink.DoesNotFit++;
                continue;
            }
            var rmqrData = RmQRCodeGenerator.CreateGs1DigitalLink(input, RmQREccLevel.M, rmqrOptions);
            CheckLink(rmqrLink, $"{id} {rmqrData.Version}", FromData(rmqrData), CppFormat.RMQRCode, RmQrQuietZoneModules, uri);
        }

        Console.WriteLine($"{taken} inputs taken, {refused} refused, {linked} of the taken with a Digital Link URI");
        Console.WriteLine($"CreateGs1, Standard QR: {qr.Describe()}");
        Console.WriteLine($"CreateGs1, rMQR: {rmqr.Describe()}");
        Console.WriteLine($"CreateGs1DigitalLink, Standard QR: {qrLink.Describe()}");
        Console.WriteLine($"CreateGs1DigitalLink, rMQR: {rmqrLink.Describe()}");
        return qr.Failures + rmqr.Failures + qrLink.Failures + rmqrLink.Failures == 0 ? 0 : 1;
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
                    if (input.Length > 0 && seen.Add(input))
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
