using FeatherQR;
using Net.Codecrete.QrCodeGenerator;
using ZXing.Common;
using CppFormat = ZXingCpp.BarcodeFormat;
using CppReader = ZXingCpp.BarcodeReader;
using GlyphDecoder = CodeGlyphX.QrDecoder;
using GlyphMatrix = CodeGlyphX.BitMatrix;
using NetReader = ZXing.QrCode.QRCodeReader;
using NetResultMetadataType = ZXing.ResultMetadataType;

namespace QRInteropFixtures;

/// <summary>
/// Interop spot check for Structured Append encoding: every symbol of a set this library
/// writes is read by the three pinned readers (zxing-cpp and ZXing.Net from a rendered
/// luminance image, CodeGlyphX from the module matrix), and each must report the position,
/// count, parity and text this library's own decoder reports. Then the parity of the set is
/// compared with the one the pinned balancing encoder writes for the same text and level,
/// which is the same definition (the whole text's bytes in the set's charset) and so must
/// be the same byte. Findings are recorded in specs/qrcode-test-fixtures.md.
/// </summary>
public static class StructuredAppendSpotCheck
{
    private const int PixelsPerModule = 8;

    private sealed record Variant(string Name, QRSegmentation Segmentation, bool Boost, bool Bom, EciMode Eci);

    public static int Run()
    {
        var variants = new[]
        {
            new Variant("single", QRSegmentation.Single, false, false, EciMode.Default),
            new Variant("optimal", QRSegmentation.Optimal, false, false, EciMode.Default),
            new Variant("boost", QRSegmentation.Single, true, false, EciMode.Default),
            new Variant("utf8-eci", QRSegmentation.Single, false, false, EciMode.Utf8),
            new Variant("utf8-bom", QRSegmentation.Single, false, true, EciMode.Utf8),
        };
        var cpp = new CppReader { Formats = CppFormat.QRCode, TryHarder = true };
        var net = new NetReader();

        var symbols = 0;
        var mismatches = 0;
        var parityChecks = 0;
        Console.WriteLine("case                              variant   n  ver ecc  zxing-cpp  ZXing.Net  CodeGlyphX  parity");
        foreach (var caseDefinition in StructuredAppendCorpus.Cases)
        {
            foreach (var variant in variants)
            {
                var ecc = ToEcc(caseDefinition.ErrorCorrectionLevel);
                var options = new QRCodeGeneratorOptions
                {
                    Version = QRVersionRange.Between(caseDefinition.MinVersion, caseDefinition.MaxVersion),
                    Segmentation = variant.Segmentation,
                    BoostEccLevel = variant.Boost,
                    Utf8Bom = variant.Bom,
                    EciMode = variant.Eci,
                };

                QRCodeData[] set;
                try
                {
                    set = QRCodeGenerator.CreateStructuredAppend(caseDefinition.PayloadText, ecc, options);
                }
                catch (ArgumentException ex)
                {
                    Console.WriteLine($"{caseDefinition.Id,-33} {variant.Name,-9} skipped: {ex.Message.Split('.')[0]}");
                    continue;
                }
                if (set.Length < 2)
                {
                    Console.WriteLine($"{caseDefinition.Id,-33} {variant.Name,-9} fits one symbol");
                    continue;
                }

                var cppOk = 0;
                var netOk = 0;
                var glyphOk = 0;
                var texts = new string[set.Length];
                byte parity = 0;
                var version = set[0].Version;
                var level = "";
                for (var i = 0; i < set.Length; i++)
                {
                    if (!QRCodeDecoder.TryDecode(set[i], out var ourText, out var ourInfo))
                        throw new InvalidOperationException($"{caseDefinition.Id}/{variant.Name}: this library cannot read its own symbol {i} ({ourInfo.Status})");
                    var ours = ourInfo.StructuredAppend;
                    texts[i] = ourText;
                    parity = ours.Parity;
                    level = ourInfo.EccLevel.ToString();
                    symbols++;

                    var (luminance, width) = RenderLuminance(set[i]);
                    var cppResults = cpp.From(new ZXingCpp.ImageView(luminance, width, width, ZXingCpp.ImageFormat.Lum));
                    var cppText = cppResults.Length == 1 ? cppResults[0].Text.TrimStart('﻿') : null;
                    if (cppResults.Length == 1 && cppResults[0].SequenceIndex == ours.Index && cppResults[0].SequenceSize == ours.Count && cppResults[0].SequenceId == ours.Parity.ToString() && cppText == ourText)
                        cppOk++;
                    else
                        Console.WriteLine($"  zxing-cpp disagrees on symbol {i}: {(cppResults.Length == 1 ? $"{cppResults[0].SequenceIndex}/{cppResults[0].SequenceSize} p{cppResults[0].SequenceId} text {(cppText == ourText ? "same" : "differs")}" : $"{cppResults.Length} results")}");

                    var netResult = net.decode(new ZXing.BinaryBitmap(new HybridBinarizer(new ZXing.RGBLuminanceSource(luminance, width, width, ZXing.RGBLuminanceSource.BitmapFormat.Gray8))));
                    if (netResult is not null
                        && netResult.ResultMetadata.TryGetValue(NetResultMetadataType.STRUCTURED_APPEND_SEQUENCE, out var sequence)
                        && netResult.ResultMetadata.TryGetValue(NetResultMetadataType.STRUCTURED_APPEND_PARITY, out var netParity)
                        && ((int)sequence >> 4) == ours.Index && (((int)sequence & 0x0F) + 1) == ours.Count && (int)netParity == ours.Parity
                        && netResult.Text.TrimStart('﻿') == ourText)
                        netOk++;
                    else
                        Console.WriteLine($"  ZXing.Net disagrees on symbol {i}: {(netResult is null ? "no read" : $"metadata {string.Join(",", netResult.ResultMetadata.Keys)} text {(netResult.Text.TrimStart('﻿') == ourText ? "same" : "differs")}")}");

                    if (GlyphDecoder.TryDecode(ToGlyphMatrix(set[i]), out var glyph)
                        && glyph.StructuredAppend is { } header
                        && header.Index - 1 == ours.Index && header.Total == ours.Count && header.Parity == ours.Parity
                        && glyph.Text.TrimStart('﻿') == ourText)
                        glyphOk++;
                    else
                        Console.WriteLine($"  CodeGlyphX disagrees on symbol {i}");
                }

                if (string.Concat(texts) != caseDefinition.PayloadText)
                    throw new InvalidOperationException($"{caseDefinition.Id}/{variant.Name}: the parts do not concatenate to the text");

                // Parity against the balancing encoder oracle, for the variants whose bytes it writes the same way:
                // it picks ISO-8859-1 or UTF-8 from the text as this library does by default, has no forced
                // charset, and writes no byte order mark.
                var parityNote = "n/a";
                if (!variant.Bom && variant.Eci == EciMode.Default)
                {
                    var oracleSet = QrCode.EncodeTextInMultipleBalancedCodes(caseDefinition.PayloadText, ToOracleEcc(caseDefinition.ErrorCorrectionLevel), caseDefinition.MinVersion, caseDefinition.MaxVersion);
                    if (oracleSet.Count >= 2 && GlyphDecoder.TryDecode(ToGlyphMatrix(oracleSet[0]), out var oracle) && oracle.StructuredAppend is { } oracleHeader)
                    {
                        parityChecks++;
                        parityNote = oracleHeader.Parity == parity ? $"{parity} = oracle" : $"{parity} != oracle {oracleHeader.Parity}";
                        if (oracleHeader.Parity != parity)
                            mismatches++;
                    }
                }

                var bad = set.Length * 3 - cppOk - netOk - glyphOk;
                mismatches += bad;
                Console.WriteLine($"{caseDefinition.Id,-33} {variant.Name,-9} {set.Length,2} {version,4} {level,-3} {cppOk,4}/{set.Length,-2}    {netOk,4}/{set.Length,-2}    {glyphOk,4}/{set.Length,-2}     {parityNote}");
            }
        }

        Console.WriteLine($"{symbols} symbols read by three readers, {parityChecks} parity comparisons, {mismatches} mismatches");
        return mismatches == 0 ? 0 : 1;
    }

    private static (byte[] Luminance, int Width) RenderLuminance(QRCodeData data)
    {
        var size = data.Size;
        var width = size * PixelsPerModule;
        var luminance = new byte[width * width];
        luminance.AsSpan().Fill(255);
        for (var row = 0; row < size; row++)
        {
            for (var col = 0; col < size; col++)
            {
                if (!data[row, col])
                    continue;
                for (var y = 0; y < PixelsPerModule; y++)
                    luminance.AsSpan((row * PixelsPerModule + y) * width + col * PixelsPerModule, PixelsPerModule).Clear();
            }
        }
        return (luminance, width);
    }

    /// <summary>The core modules (quiet zone stripped) as a CodeGlyphX matrix.</summary>
    private static GlyphMatrix ToGlyphMatrix(QRCodeData data)
    {
        var core = 17 + 4 * data.Version;
        var offset = (data.Size - core) / 2;
        var matrix = new GlyphMatrix(core, core);
        for (var row = 0; row < core; row++)
            for (var col = 0; col < core; col++)
                matrix.Set(col, row, data[row + offset, col + offset]);
        return matrix;
    }

    private static GlyphMatrix ToGlyphMatrix(QrCode code)
    {
        var matrix = new GlyphMatrix(code.Size, code.Size);
        for (var row = 0; row < code.Size; row++)
            for (var col = 0; col < code.Size; col++)
                matrix.Set(col, row, code.GetModule(col, row));
        return matrix;
    }

    private static QREccLevel ToEcc(string ecc) => Enum.Parse<QREccLevel>(ecc);

    private static QrCode.Ecc ToOracleEcc(string ecc) => ecc switch
    {
        "L" => QrCode.Ecc.Low,
        "M" => QrCode.Ecc.Medium,
        "Q" => QrCode.Ecc.Quartile,
        _ => QrCode.Ecc.High,
    };
}
