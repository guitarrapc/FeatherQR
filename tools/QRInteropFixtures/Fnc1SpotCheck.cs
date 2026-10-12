using System.Text;
using FeatherQR;
using ZXingCpp;
using CppBarcode = ZXingCpp.Barcode;
using CppFormat = ZXingCpp.BarcodeFormat;

namespace QRInteropFixtures;

/// <summary>
/// <c>spot-check-fnc1</c>: every FNC1 symbol this library writes, read by the two pinned readers. Each case is
/// written under <see cref="QRSegmentation.Single"/> and <see cref="QRSegmentation.Optimal"/> at each level, and
/// zxing-cpp (<c>TextMode.Plain</c>) must report the identifier of the mode (<c>]Q3</c> or <c>]Q5</c>, one higher
/// under an ECI header), GS1 content for first position, and the text, with the application indicator before it in
/// second position. ZXing.Net must read first position with <c>]Q3</c> and the text. The cases are the FNC1 fixture
/// corpus's data (the GS1 examples, each data mode under FNC1, a literal <c>%</c>, the two separator pairs, FNC1
/// beside an ECI header, each indicator form), the two examples of ISO/IEC 18004 6.4.8, and adversarial <c>%</c>
/// and separator sequences. Findings go to the fixture spec and the FNC1 plan.
/// </summary>
public static class Fnc1SpotCheck
{
    private const string Gs = "\u001D";
    private const string GtinElement = "0109524000059109";

    private static readonly (string Id, string Text, Fnc1Mode Mode, EciMode Eci)[] Cases =
    [
        ("gs1-numeric-only", GtinElement + "17231120", Fnc1Mode.FirstPosition, EciMode.Default),
        ("gs1-alphanumeric", GtinElement + "10ABC123" + Gs + "17231120", Fnc1Mode.FirstPosition, EciMode.Default),
        ("gs1-mixed-case-serial", GtinElement + "2112345678p901" + Gs + "101234567p" + Gs + "17231120", Fnc1Mode.FirstPosition, EciMode.Default),
        ("gs1-percent-literal", GtinElement + "10AB%CD" + Gs + "17231120", Fnc1Mode.FirstPosition, EciMode.Default),
        ("gs1-percent-then-gs", GtinElement + "10AB%" + Gs + "17231120", Fnc1Mode.FirstPosition, EciMode.Default),
        ("gs1-gs-gs-pair", GtinElement + "10AB" + Gs + Gs + "17231120", Fnc1Mode.FirstPosition, EciMode.Default),
        ("gs1-gs-percent-pair", GtinElement + "10AB" + Gs + "%CD" + Gs + "17231120", Fnc1Mode.FirstPosition, EciMode.Default),
        ("gs1-eci-utf8", GtinElement + "10ABC", Fnc1Mode.FirstPosition, EciMode.Utf8),
        ("gs1-latin1", "10é" + Gs + "17231120", Fnc1Mode.FirstPosition, EciMode.Default),
        ("gs1-utf8-content", "10€" + Gs + "17231120", Fnc1Mode.FirstPosition, EciMode.Default),
        ("gs1-trailing-gs", GtinElement + "10ABC" + Gs, Fnc1Mode.FirstPosition, EciMode.Default),
        ("gs1-lone-gs", Gs, Fnc1Mode.FirstPosition, EciMode.Default),
        ("gs1-lone-percent", "%", Fnc1Mode.FirstPosition, EciMode.Default),
        ("gs1-percent-storm", "%" + Gs + "%%" + Gs + Gs + "%" + Gs + "%%%", Fnc1Mode.FirstPosition, EciMode.Default),
        ("gs1-long-alphanumeric", GtinElement + "10" + new string('A', 60) + Gs + "21" + new string('7', 20) + Gs + "90ABC-DEF/GHI.JKL", Fnc1Mode.FirstPosition, EciMode.Default),
        ("iso-6-4-8-1-example", "01049123451234591597033130128" + Gs + "10ABC123", Fnc1Mode.FirstPosition, EciMode.Default),
        ("iso-6-4-8-2-example", Fnc1Corpus.IsoSecondPositionExample, Fnc1Mode.SecondPosition(37), EciMode.Default),
        ("aim-00", "AA1234BBB112", Fnc1Mode.SecondPosition(0), EciMode.Default),
        ("aim-99", "AA1234BBB112", Fnc1Mode.SecondPosition(99), EciMode.Default),
        ("aim-A", "AA1234BBB112", Fnc1Mode.SecondPosition('A'), EciMode.Default),
        ("aim-z", "AA1234BBB112", Fnc1Mode.SecondPosition('z'), EciMode.Default),
        ("aim-37-percent-and-gs", "AB%" + Gs + "CD" + Gs + Gs + "EF%%G", Fnc1Mode.SecondPosition(37), EciMode.Default),
        ("aim-37-byte", "text text text text\r", Fnc1Mode.SecondPosition(37), EciMode.Default),
        ("aim-37-eci-utf8", "AA1234", Fnc1Mode.SecondPosition(37), EciMode.Utf8),
    ];

    public static int Run()
    {
        Console.WriteLine($"FNC1 spot check: {Fnc1Writers.ZintVersion} as the zxing-cpp reader, {Fnc1Writers.ZXingNetVersion}");
        Console.WriteLine("id | segmentation | level | version | zxing-cpp | ZXing.Net");
        var failures = 0;
        var total = 0;
        foreach (var (id, text, mode, eci) in Cases)
        {
            foreach (var segmentation in new[] { QRSegmentation.Single, QRSegmentation.Optimal })
            {
                foreach (var level in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
                {
                    total++;
                    var options = new QRCodeGeneratorOptions { Fnc1 = mode, Segmentation = segmentation, EciMode = eci, QuietZoneSize = 0 };
                    var data = QRCodeGenerator.Create(text, level, options);
                    var matrix = FromData(data);

                    // zxing-cpp's SymbologyIdentifier names the symbol's mode and reports an ECI header through HasECI (the
                    // identifier modifier for the ECI protocol is the transmitting reader's, Annex F). ZXing.Net raises the
                    // identifier to ]Q4 under an ECI header instead.
                    var expectedIdentifier = mode.IsFirstPosition ? "]Q3" : "]Q5";
                    var expectedText = mode.IsSecondPosition ? Fnc1Writers.FormatIndicator(mode.ApplicationIndicator!.Value) + text : text;
                    var expectsEci = eci != EciMode.Default || text.Any(c => c > 0x7F);

                    string cpp;
                    var cppOk = false;
                    try
                    {
                        var results = Fnc1Writers.ReadCpp(matrix, CppFormat.QRCode, FixtureWriter.QuietZoneModules);
                        if (results.Length != 1)
                        {
                            cpp = $"{results.Length} results";
                        }
                        else
                        {
                            var barcode = results[0];
                            cpp = Describe(barcode);
                            cppOk = barcode.IsValid
                                && barcode.SymbologyIdentifier == expectedIdentifier
                                && (!mode.IsFirstPosition || barcode.ContentType == ContentType.GS1)
                                && barcode.HasECI == expectsEci
                                && barcode.Text == expectedText;
                        }
                    }
                    catch (Exception ex)
                    {
                        cpp = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0].TrimEnd('\r')}";
                    }

                    var net = "-";
                    var netOk = true;
                    if (mode.IsFirstPosition)
                    {
                        try
                        {
                            // ZXing.Net's reader misses some clean renders at 4 pixels a module. A second try at 8 tells a
                            // reader miss from a symbol it cannot read.
                            var result = Fnc1Writers.ReadNet(matrix) ?? Fnc1Writers.ReadNet(matrix, pixelsPerModule: 8);
                            if (result is null)
                            {
                                // Which masks of the same stream ZXing.Net does read tells a reader miss (some masks read) from a symbol defect (none).
                                var readMasks = new List<int>();
                                for (var mask = 0; mask < 8; mask++)
                                {
                                    var masked = FromData(QRCodeGenerator.Create(text, level, options with { MaskPattern = mask }));
                                    if (Fnc1Writers.ReadNet(masked)?.Text == text)
                                        readMasks.Add(mask);
                                }
                                QRCodeDecoder.TryDecode(data, out _, out var info);
                                net = $"no read at 4 or 8 px/module (mask {info.MaskPattern}); reads masks {string.Join(",", readMasks)}";
                                netOk = false;
                            }
                            else
                            {
                                var identifier = result.ResultMetadata is not null && result.ResultMetadata.TryGetValue(ZXing.ResultMetadataType.SYMBOLOGY_IDENTIFIER, out var value) ? value?.ToString() ?? "" : "";
                                net = $"{identifier} \"{Fnc1Writers.Escape(result.Text ?? "")}\"";
                                // ZXing.Net reports ]Q4 for first position under an ECI header where zxing-cpp reports ]Q3 with HasECI.
                                netOk = identifier == (expectsEci ? "]Q4" : "]Q3") && result.Text == text;
                            }
                        }
                        catch (Exception ex)
                        {
                            net = ex.GetType().Name;
                            netOk = false;
                        }
                    }

                    var ok = cppOk && netOk;
                    if (!ok)
                        failures++;
                    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {id,-26} {segmentation,-7} {level} v{data.Version,-2} | {cpp,-70} | {net}");
                }
            }
        }

        Console.WriteLine($"{total} Standard QR symbols, {failures} failures");
        var rmqrFailures = RunRmQr(out var rmqrTotal, out var rmqrSkipped);
        Console.WriteLine($"{rmqrTotal} rMQR symbols, {rmqrFailures} failures, {rmqrSkipped} texts that fit no rMQR symbol under the options");
        return failures + rmqrFailures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The same texts as rMQR symbols (both positions, Single and Optimal, M and H), read by zxing-cpp's rMQR reader
    /// only: ZXing.Net has no rMQR reader. A text that fits no rMQR symbol under the options is reported and skipped,
    /// not counted as a failure (R17x139-H holds 76 data codewords).
    /// </summary>
    private static int RunRmQr(out int total, out int skipped)
    {
        Console.WriteLine();
        Console.WriteLine("rMQR: id | segmentation | level | version | zxing-cpp");
        var failures = 0;
        total = 0;
        skipped = 0;
        foreach (var (id, text, mode, eci) in Cases)
        {
            foreach (var segmentation in new[] { RmQRSegmentation.Single, RmQRSegmentation.Optimal })
            {
                foreach (var level in new[] { RmQREccLevel.M, RmQREccLevel.H })
                {
                    var options = new RmQRCodeGeneratorOptions { Fnc1 = mode, Segmentation = segmentation, EciMode = eci, QuietZoneSize = 0 };
                    if (!RmQRCodeGenerator.TryGetRequiredBufferSize(text, level, out _, options))
                    {
                        skipped++;
                        Console.WriteLine($"skip {id,-26} {segmentation,-7} {level} fits no rMQR symbol");
                        continue;
                    }

                    total++;
                    var data = RmQRCodeGenerator.Create(text, level, options);
                    var matrix = new ModuleMatrix(ModulesOf(data), data.Width, data.Height);
                    var expectedIdentifier = mode.IsFirstPosition ? "]Q3" : "]Q5";
                    var expectedText = mode.IsSecondPosition ? Fnc1Writers.FormatIndicator(mode.ApplicationIndicator!.Value) + text : text;
                    var expectsEci = eci != EciMode.Default || text.Any(c => c > 0x7F);

                    string cpp;
                    var cppOk = false;
                    try
                    {
                        var results = Fnc1Writers.ReadCpp(matrix, CppFormat.RMQRCode, RmQrQuietZoneModules);
                        if (results.Length != 1)
                        {
                            cpp = $"{results.Length} results";
                        }
                        else
                        {
                            var barcode = results[0];
                            cpp = Describe(barcode);
                            cppOk = barcode.IsValid
                                && barcode.SymbologyIdentifier == expectedIdentifier
                                && (!mode.IsFirstPosition || barcode.ContentType == ContentType.GS1)
                                && barcode.HasECI == expectsEci
                                && barcode.Text == expectedText;
                        }
                    }
                    catch (Exception ex)
                    {
                        cpp = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0].TrimEnd('\r')}";
                    }

                    if (!cppOk)
                        failures++;
                    Console.WriteLine($"{(cppOk ? "ok  " : "FAIL")} {id,-26} {segmentation,-7} {level} {data.Version,-8} | {cpp}");
                }
            }
        }

        return failures;
    }

    private const int RmQrQuietZoneModules = 2; // ISO/IEC 23941 quiet zone

    private static byte[] ModulesOf(RmQRCodeData data)
    {
        var modules = new byte[data.Width * data.Height];
        for (var row = 0; row < data.Height; row++)
            for (var col = 0; col < data.Width; col++)
                modules[row * data.Width + col] = data[row, col] ? (byte)1 : (byte)0;
        return modules;
    }

    private static string Describe(CppBarcode barcode)
        => barcode.IsValid
            ? $"{barcode.SymbologyIdentifier} {barcode.ContentType}{(barcode.HasECI ? " ECI" : "")} \"{Fnc1Writers.Escape(barcode.Text)}\""
            : $"invalid: {barcode.ErrorMsg}";

    private static ModuleMatrix FromData(QRCodeData data)
    {
        var size = data.Size;
        var modules = new byte[size * size];
        for (var row = 0; row < size; row++)
            for (var col = 0; col < size; col++)
                modules[row * size + col] = data[row, col] ? (byte)1 : (byte)0;
        return new ModuleMatrix(modules, size, size);
    }
}
