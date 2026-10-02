using System.Text;
using SkiaSharp;
using ZXing;
using ZXing.SkiaSharp;
namespace FeatherQR.Tests;

/// <summary>
/// Sets written by <see cref="QRCodeGenerator.CreateStructuredAppend"/> read by an
/// independent decoder: every symbol must be read with the same position, count, parity
/// and text this library's decoder reports, and the parts must concatenate to the input.
/// ZXing.Net exposes the header as one byte (position in the high nibble, count minus one
/// in the low) and the parity as an integer.
/// </summary>
public class StructuredAppendZXingCrossTest
{
    private const string Sentence = "The quick brown fox jumps over the lazy dog. ";

    [Test]
    [Arguments("ascii", 600, 5, QREccLevel.M, QRSegmentation.Single, false, false, false)]
    [Arguments("digits", 1201, 6, QREccLevel.M, QRSegmentation.Single, false, false, false)]
    [Arguments("latin1", 329, 3, QREccLevel.M, QRSegmentation.Single, false, false, false)]
    [Arguments("japanese", 67, 5, QREccLevel.M, QRSegmentation.Single, false, false, false)]
    [Arguments("emoji", 120, 2, QREccLevel.L, QRSegmentation.Single, false, false, false)]
    [Arguments("mixed", 600, 6, QREccLevel.M, QRSegmentation.Optimal, false, false, false)]
    [Arguments("ascii", 470, 2, QREccLevel.L, QRSegmentation.Single, true, false, false)]
    [Arguments("latin1", 329, 3, QREccLevel.M, QRSegmentation.Optimal, true, false, false)]
    [Arguments("japanese", 67, 5, QREccLevel.M, QRSegmentation.Single, false, true, false)]
    [Arguments("japanese", 67, 5, QREccLevel.M, QRSegmentation.Optimal, false, false, true)] // a Kanji set: Kanji runs beside the ASCII, no ECI, the Shift_JIS parity
    [Arguments("japaneseCells", 61, 3, QREccLevel.M, QRSegmentation.Single, false, false, true)] // a Kanji set: one Kanji segment a symbol
    [Arguments("japaneseCells", 61, 3, QREccLevel.M, QRSegmentation.Optimal, true, false, true)]
    public async Task Set_IsReadByZXingWithTheSameHeaderAndText(string kind, int length, int maxVersion, QREccLevel ecc, QRSegmentation segmentation, bool boost, bool bom, bool kanjiSet)
    {
        var text = Text(kind, length);
        var options = new QRCodeGeneratorOptions
        {
            Version = QRVersionRange.AtMost(maxVersion),
            Segmentation = segmentation,
            BoostEccLevel = boost,
            Utf8Bom = bom,
            EciMode = bom ? EciMode.Utf8 : EciMode.Default,
            AllowKanji = kanjiSet,
        };
        var symbols = QRCodeGenerator.CreateStructuredAppend(text, ecc, options);
        await Assert.That(symbols.Length).IsBetween(2, 16);

        // A Kanji set carries the XOR of the text's Shift_JIS bytes. The UTF-8 set the option falls back to carries the XOR of
        // its UTF-8 bytes, a different value for these texts, so a row that came out UTF-8 fails here rather than passing as one.
        var shiftJisParity = kanjiSet ? Xor(KanjiStreamReference.ShiftJisBytes(text)) : (byte)0;
        if (kanjiSet)
            await Assert.That(Xor(Encoding.UTF8.GetBytes(text))).IsNotEqualTo(shiftJisParity).Because("the premise: the two charsets give this text different parities");

        var reader = new BarcodeReader
        {
            Options = new ZXing.Common.DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true, PureBarcode = true },
        };
        var parts = new StringBuilder();
        for (var i = 0; i < symbols.Length; i++)
        {
            await Assert.That(QRCodeDecoder.TryDecode(symbols[i], out var ourText, out var ourInfo)).IsTrue();
            using var bitmap = ToBitmap(symbols[i]);
            var result = reader.Decode(bitmap);

            await Assert.That(result).IsNotNull().Because($"ZXing.Net could not read symbol {i} of {kind}");
            await Assert.That(result!.ResultMetadata.ContainsKey(ResultMetadataType.STRUCTURED_APPEND_SEQUENCE)).IsTrue();
            var sequence = (int)result.ResultMetadata[ResultMetadataType.STRUCTURED_APPEND_SEQUENCE];
            var parity = (int)result.ResultMetadata[ResultMetadataType.STRUCTURED_APPEND_PARITY];
            await Assert.That(sequence >> 4).IsEqualTo(ourInfo.StructuredAppend.Index);
            await Assert.That((sequence & 0x0F) + 1).IsEqualTo(ourInfo.StructuredAppend.Count);
            await Assert.That(parity).IsEqualTo((int)ourInfo.StructuredAppend.Parity);
            if (kanjiSet)
                await Assert.That(parity).IsEqualTo((int)shiftJisParity).Because($"symbol {i} of a Kanji set carries the Shift_JIS parity");

            // ZXing.Net keeps a byte order mark in the text; this library's decoder consumes it.
            var zxingText = result.Text.TrimStart('﻿');
            await Assert.That(zxingText).IsEqualTo(ourText);
            parts.Append(zxingText);
        }

        await Assert.That(parts.ToString()).IsEqualTo(text);
    }

    private static SKBitmap ToBitmap(QRCodeData data)
    {
        const int scale = 4;
        var size = data.Size;
        var bitmap = new SKBitmap(size * scale, size * scale);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black };
        for (var row = 0; row < size; row++)
            for (var col = 0; col < size; col++)
                if (data[row, col])
                    canvas.DrawRect(col * scale, row * scale, scale, scale, paint);
        return bitmap;
    }

    private static byte Xor(byte[] bytes)
    {
        byte parity = 0;
        foreach (var b in bytes)
            parity ^= b;
        return parity;
    }

    private static string Text(string kind, int length)
    {
        var unit = kind switch
        {
            "digits" => "0123456789",
            "latin1" => "Crème brûlée à la carte, jalapeño, naïve café. ",
            "japanese" => "こんにちは世界、QRコードの分割テストです。",
            "japaneseCells" => "こんにちは世界、日本語の分割テストです。",
            "emoji" => "🎉🎊🎈",
            "mixed" => "order 20260915 item 0000123456 qty 42 ",
            _ => Sentence,
        };
        var sb = new StringBuilder(length);
        while (sb.Length < length)
            sb.Append(unit);
        var text = sb.ToString(0, length);
        return char.IsHighSurrogate(text[^1]) ? text[..^1] : text;
    }
}
