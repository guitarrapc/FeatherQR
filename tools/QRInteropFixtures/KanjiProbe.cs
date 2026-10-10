using System.Reflection;
using System.Text;
using ZXing;
using ZXing.QrCode.Internal;
using ZXingCpp;
using CppFormat = ZXingCpp.BarcodeFormat;
using CppImage = ZXingCpp.Image;
using ZXingEncoder = ZXing.QrCode.Internal.Encoder;

namespace QRInteropFixtures;

/// <summary>
/// Kanji-mode oracle probe. Answers, for the Kanji decode work, which external
/// encoder lineages can actually emit ISO/IEC 18004 Kanji mode (mode indicator
/// 1000 / rMQR 100 / Micro QR M3-M4 11) for each symbology, and what the
/// zxing-cpp reader reports for those symbols. Findings are recorded in
/// .github/docs/specs/qrcode-test-fixtures.md; nothing here runs in CI.
/// </summary>
public static class KanjiProbe
{
    // All characters are JIS X 0208 double-byte, so ZXing's chooseMode can pick Kanji.
    private const string Short = "こんにちは世界";
    private const string Long = "日本語漢字符号化試験用文字列漢字漢字漢字漢字漢字漢字漢字漢字漢字漢字漢字漢字漢字漢字漢字漢字";

    public static int Run()
    {
        Console.WriteLine("=== Kanji-mode oracle probe ===");
        Console.WriteLine();

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        ProbeEncodingAvailability();
        ProbeZXingNet();
        ProbeZXingCppApi();
        ProbeLibzintString();
        ProbeLibzintBytes();

        return 0;
    }

    private static void ProbeEncodingAvailability()
    {
        Console.WriteLine("-- Shift_JIS availability (after RegisterProvider(CodePages)) --");
        foreach (var name in new[] { "Shift_JIS", "shift_jis", "SJIS", "windows-932" })
        {
            try
            {
                var enc = Encoding.GetEncoding(name);
                Console.WriteLine($"  GetEncoding(\"{name}\") -> {enc.WebName} (cp {enc.CodePage})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  GetEncoding(\"{name}\") -> {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.WriteLine();
    }

    private static void ProbeZXingNet()
    {
        Console.WriteLine("-- ZXing.Net Standard QR: can it emit Kanji mode? --");
        foreach (var charset in new[] { "Shift_JIS", "SJIS", "UTF-8", null })
        {
            foreach (var (label, payload) in new[] { ("short", Short), ("long", Long) })
            {
                var hints = charset is null
                    ? null
                    : new Dictionary<EncodeHintType, object> { [EncodeHintType.CHARACTER_SET] = charset };
                try
                {
                    var qr = ZXingEncoder.encode(payload, ErrorCorrectionLevel.M, hints);
                    Console.WriteLine($"  charset={charset ?? "(default)"} {label}: mode={qr.Mode} version={qr.Version.VersionNumber} size={qr.Matrix.Width} mask={qr.MaskPattern}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  charset={charset ?? "(default)"} {label}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        Console.WriteLine();
    }

    private static void ProbeZXingCppApi()
    {
        Console.WriteLine("-- ZXingCpp wrapper surface --");
        foreach (var type in new[] { typeof(BarcodeCreator), typeof(Barcode) })
        {
            Console.WriteLine($"  {type.Name}:");
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                if (member is MethodInfo m && m.IsSpecialName) continue;
                Console.WriteLine($"    {member}");
            }
        }
        Console.WriteLine();
    }

    private static void ProbeLibzintString()
    {
        Console.WriteLine("-- libzint via From(string): Japanese input --");
        RunLibzintMatrix(
            static (format, options, label) =>
            {
                var payload = label == "short" ? Short : Long;
                return ZXingCppCalls.Create(new BarcodeCreator(format) { Options = options }, payload);
            },
            ["short", "long"]);
    }

    private static void ProbeLibzintBytes()
    {
        Console.WriteLine("-- libzint via From(byte[]): UTF-8 bytes vs Shift_JIS bytes --");
        var sjis = Encoding.GetEncoding("Shift_JIS");
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["utf8-short"] = Encoding.UTF8.GetBytes(Short),
            ["sjis-short"] = sjis.GetBytes(Short),
            ["sjis-long"] = sjis.GetBytes(Long),
            ["ascii"] = Encoding.ASCII.GetBytes("ABC123"),
        };

        RunLibzintMatrix(
            (format, options, label) =>
            {
                return ZXingCppCalls.Create(new BarcodeCreator(format) { Options = options }, payloads[label]);
            },
            [.. payloads.Keys]);
    }

    private static void RunLibzintMatrix(Func<CppFormat, string, string, Barcode> make, string[] labels)
    {
        var formats = new[] { CppFormat.QRCode, CppFormat.MicroQRCode, CppFormat.RMQRCode };
        foreach (var format in formats)
        {
            foreach (var options in new[] { "", "eci=20", "eci=26" })
            {
                foreach (var label in labels)
                {
                    try
                    {
                        using var barcode = make(format, options, label);
                        if (!barcode.IsValid)
                        {
                            Console.WriteLine($"  {format} '{options}' {label}: invalid ({barcode.ErrorMsg})");
                            continue;
                        }

                        using var image = ZXingCppCalls.ToImage(barcode, new WriterOptions { Scale = 1, AddQuietZones = false });
                        Console.WriteLine($"  {format} '{options}' {label}: {image.Height}x{image.Width} modules, {ReadBack(image, format)}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  {format} '{options}' {label}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }
        Console.WriteLine();
    }

    private static string ReadBack(CppImage image, CppFormat format)
    {
        // Re-render at 8 px/module with a quiet zone so the reader can lock on.
        const int Scale = 8;
        const int Quiet = 4;
        var w = (image.Width + Quiet * 2) * Scale;
        var h = (image.Height + Quiet * 2) * Scale;
        var lum = new byte[w * h];
        lum.AsSpan().Fill(255);
        var src = image.ToArray();
        for (var row = 0; row < image.Height; row++)
        {
            for (var col = 0; col < image.Width; col++)
            {
                if (src[row * image.Width + col] >= 128) continue;
                for (var y = 0; y < Scale; y++)
                    lum.AsSpan(((Quiet + row) * Scale + y) * w + (Quiet + col) * Scale, Scale).Clear();
            }
        }

        var results = ZXingCppCalls.Read(new BarcodeReader { Formats = format, TryHarder = true }, lum, w, h);
        if (results.Length != 1)
            return $"reader found {results.Length} symbols";

        var r = results[0];
        return $"text=\"{r.Text}\" bytes={Convert.ToHexString(r.Bytes)} hasEci={r.HasECI} symId={r.SymbologyIdentifier} version={r.Extra("Version")} ecc={r.Extra("EcLevel")}";
    }
}
