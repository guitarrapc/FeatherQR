using SkiaSharp;
using ZXing;
using ZXing.SkiaSharp;

namespace FeatherQR.Tests;

public class QRCodeDecodabilityTest
{
    private readonly BarcodeReader _reader;

    public QRCodeDecodabilityTest()
    {
        _reader = new BarcodeReader
        {
            AutoRotate = true,
            Options = new ZXing.Common.DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = new[] { BarcodeFormat.QR_CODE },
            }
        };
    }

    [Test]
    [Arguments("0123456789", ECCLevel.L, EciMode.Utf8)]
    [Arguments("Hello, World!", ECCLevel.L, EciMode.Utf8)]
    [Arguments("special", ECCLevel.L, EciMode.Utf8)]
    public void CreateQrCode_Default_ascii_words_IsDecodable(string content, ECCLevel eccLevel, EciMode eciMode)
    {
        // default is ISO-8859-1 for ASCII-only
        AssertQrCodeIsDecodable(content, eccLevel, eciMode);
    }

    [Test]
    [Arguments("こんにちは", ECCLevel.M, EciMode.Utf8)]
    [Arguments("你好世界", ECCLevel.Q, EciMode.Utf8)]
    [Arguments("Привет мир", ECCLevel.H, EciMode.Utf8)]
    [Arguments("🎉🎊🎈", ECCLevel.L, EciMode.Utf8)]
    [Arguments("café", ECCLevel.M, EciMode.Utf8)]
    [Arguments("Café", ECCLevel.L, EciMode.Utf8)]
    [Arguments("Résumé", ECCLevel.M, EciMode.Utf8)]
    [Arguments("Naïve", ECCLevel.Q, EciMode.Utf8)]
    [Arguments("Zürich", ECCLevel.H, EciMode.Utf8)]
    public void CreateQrCode_Default_utf8_words_IsDecodable(string content, ECCLevel eccLevel, EciMode eciMode)
    {
        // automatic ECI mode selection should match Utf8 for non-ASCII
        AssertQrCodeIsDecodable(content, eccLevel, eciMode);
    }

    [Test]
    [Arguments("0123456789", ECCLevel.L, EciMode.Utf8)]
    [Arguments("Hello, World!", ECCLevel.L, EciMode.Utf8)]
    [Arguments("special", ECCLevel.L, EciMode.Utf8)]
    [Arguments("こんにちは", ECCLevel.M, EciMode.Utf8)]
    [Arguments("你好世界", ECCLevel.Q, EciMode.Utf8)]
    [Arguments("Привет мир", ECCLevel.H, EciMode.Utf8)]
    [Arguments("🎉🎊🎈", ECCLevel.L, EciMode.Utf8)]
    [Arguments("café", ECCLevel.M, EciMode.Utf8)]
    [Arguments("Café", ECCLevel.L, EciMode.Utf8)]
    [Arguments("Résumé", ECCLevel.M, EciMode.Utf8)]
    [Arguments("Naïve", ECCLevel.Q, EciMode.Utf8)]
    [Arguments("Zürich", ECCLevel.H, EciMode.Utf8)]
    public void CreateQrCode_Utf8_IsDecodable(string content, ECCLevel eccLevel, EciMode eciMode)
    {
        AssertQrCodeIsDecodable(content, eccLevel, eciMode);
    }

    [Test]
    [Arguments("0123456789", ECCLevel.L, EciMode.Iso8859_1)]
    [Arguments("HELLO WORLD", ECCLevel.M, EciMode.Iso8859_1)]
    [Arguments("special", ECCLevel.L, EciMode.Iso8859_1)]
    [Arguments("ABC-123", ECCLevel.Q, EciMode.Iso8859_1)]
    [Arguments("Test123", ECCLevel.H, EciMode.Iso8859_1)]
    [Arguments("Café", ECCLevel.L, EciMode.Iso8859_1)]
    [Arguments("Résumé", ECCLevel.M, EciMode.Iso8859_1)]
    [Arguments("Naïve", ECCLevel.Q, EciMode.Iso8859_1)]
    [Arguments("Zürich", ECCLevel.H, EciMode.Iso8859_1)]
    public void CreateQrCode_Iso8859_IsDecodable(string content, ECCLevel eccLevel, EciMode eciMode)
    {
        AssertQrCodeIsDecodable(content, eccLevel, eciMode);
    }

    [Test]
    public async Task Debug_Zurich_Version_Check()
    {
        var content = "Zürich";

        // check byte length in UTF-8
        var utf8Bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var byteCount = utf8Bytes.Length;

        var qrH = QRCodeGenerator.CreateQrCode(content, ECCLevel.H, new QRCodeGeneratorOptions { EciMode = EciMode.Utf8 });
        var qrM = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { EciMode = EciMode.Utf8 });
        var qrL = QRCodeGenerator.CreateQrCode(content, ECCLevel.L, new QRCodeGeneratorOptions { EciMode = EciMode.Utf8 });

        // debug output
        Console.WriteLine($"Content: \"{content}\"");
        Console.WriteLine($"UTF-8 Bytes: {byteCount} [{string.Join(", ", utf8Bytes.Select(b => $"0x{b:X2}"))}]");
        Console.WriteLine($"");
        Console.WriteLine($"ECC Level H → Version {qrH.Version} (Size: {qrH.Size}x{qrH.Size})");
        Console.WriteLine($"ECC Level M → Version {qrM.Version} (Size: {qrM.Size}x{qrM.Size})");
        Console.WriteLine($"ECC Level L → Version {qrL.Version} (Size: {qrL.Size}x{qrL.Size})");
        Console.WriteLine($"");

        // Version 1 logical capacity
        Console.WriteLine("Version 1 Byte mode capacity:");
        Console.WriteLine("  ECC L: 17 bytes");
        Console.WriteLine("  ECC M: 14 bytes");
        Console.WriteLine("  ECC Q: 11 bytes");
        Console.WriteLine("  ECC H: 7 bytes");
        Console.WriteLine($"");
        Console.WriteLine($"Required (with ECI header): ~10-12 bytes");
        Console.WriteLine($"");

        // Expected
        Console.WriteLine("Expected versions:");
        Console.WriteLine("  ECC L: Version 1 (17 bytes available)");
        Console.WriteLine("  ECC M: Version 1 (14 bytes available)");
        Console.WriteLine("  ECC Q: Version 1 (11 bytes available)");
        Console.WriteLine("  ECC H: Version 2 (16 bytes available)");

        // Should be automatically upgrade to Version 2
        await Assert.That(qrH.Version >= 2).IsTrue().Because($"ECC H should use Version 2 or higher, but got Version {qrH.Version}");
        await Assert.That(qrM.Version >= 1).IsTrue().Because($"ECC M should use Version 1 or higher, but got Version {qrM.Version}");
    }

    [Test]
    [Arguments("", ECCLevel.L, EciMode.Default)]
    [Arguments("A", ECCLevel.M, EciMode.Default)]
    [Arguments(" ", ECCLevel.Q, EciMode.Default)]
    [Arguments("\t", ECCLevel.H, EciMode.Default)]
    [Arguments("\n", ECCLevel.L, EciMode.Utf8)]
    public void CreateQrCode_EdgeCases_IsDecodable(string content, ECCLevel eccLevel, EciMode eciMode)
    {
        AssertQrCodeIsDecodable(content, eccLevel, eciMode);
    }

    [Test]
    [Arguments(ECCLevel.L, 41)]  // Version 1 max
    [Arguments(ECCLevel.L, 42)]  // Version 2 min
    [Arguments(ECCLevel.M, 34)]  // Version 1 max
    [Arguments(ECCLevel.M, 35)]  // Version 2 min
    [Arguments(ECCLevel.Q, 27)]  // Version 1 max
    [Arguments(ECCLevel.Q, 28)]  // Version 2 min
    [Arguments(ECCLevel.H, 17)]  // Version 1 max
    [Arguments(ECCLevel.H, 18)]  // Version 2 min
    public void CreateQrCode_VersionBoundaries_Number_IsDecodable(ECCLevel eccLevel, int charCount)
    {
        var content = new string('1', charCount);
        AssertQrCodeIsDecodable(content, eccLevel, EciMode.Default);
    }

    [Test]
    [Arguments(ECCLevel.L, 25)]  // Version 1 max
    [Arguments(ECCLevel.L, 26)]  // Version 2 min
    [Arguments(ECCLevel.M, 20)]  // Version 1 max
    [Arguments(ECCLevel.M, 21)]  // Version 2 min
    [Arguments(ECCLevel.Q, 16)]  // Version 1 max
    [Arguments(ECCLevel.Q, 17)]  // Version 2 min
    [Arguments(ECCLevel.H, 10)]  // Version 1 max
    [Arguments(ECCLevel.H, 11)]  // Version 2 min
    public void CreateQrCode_VersionBoundaries_Alphanumeric_IsDecodable(ECCLevel eccLevel, int charCount)
    {
        var content = new string('A', charCount);
        AssertQrCodeIsDecodable(content, eccLevel, EciMode.Default);
    }

    [Test]
    [Arguments(ECCLevel.L, 5)]  // Version 1 max
    [Arguments(ECCLevel.L, 6)]  // Version 2 min
    [Arguments(ECCLevel.M, 4)]  // Version 1 max
    [Arguments(ECCLevel.M, 5)]  // Version 2 min
    [Arguments(ECCLevel.Q, 3)]  // Version 1 max
    [Arguments(ECCLevel.Q, 4)]  // Version 2 min
    [Arguments(ECCLevel.H, 2)]  // Version 1 max
    [Arguments(ECCLevel.H, 3)]  // Version 2 min
    public void CreateQrCode_VersionBoundaries_Byte_IsDecodable(ECCLevel eccLevel, int charCount)
    {
        var content = new string('あ', charCount);
        AssertQrCodeIsDecodable(content, eccLevel, EciMode.Default);
    }

    [Test]
    [Arguments(ECCLevel.L, 100)]
    [Arguments(ECCLevel.M, 500)]
    [Arguments(ECCLevel.Q, 1000)]
    [Arguments(ECCLevel.H, 200)]
    public void CreateQrCode_LargeData_IsDecodable(ECCLevel eccLevel, int charCount)
    {
        var content = new string('A', charCount);
        AssertQrCodeIsDecodable(content, eccLevel, EciMode.Default);
    }

    [Test]
    [Arguments("Hello, World!", ECCLevel.L, EciMode.Utf8)]
    [Arguments("こんにちは", ECCLevel.M, EciMode.Utf8)]
    [Arguments("你好世界", ECCLevel.Q, EciMode.Utf8)]
    [Arguments("🎉🎊🎈", ECCLevel.L, EciMode.Utf8)]
    [Arguments("Zürich", ECCLevel.H, EciMode.Utf8)]
    [Arguments("Résumé", ECCLevel.M, EciMode.Default)]
    public void CreateQrCode_Utf8Bom_IsDecodable(string content, ECCLevel eccLevel, EciMode eciMode)
    {
        // BOM bytes are part of the Byte-mode data stream, so the character count
        // indicator must include them (ISO/IEC 18004). Decoders strip the BOM.
        AssertQrCodeIsDecodable(content, eccLevel, eciMode, utf8BOM: true);
    }

    [Test]
    [Arguments("あ", ECCLevel.L)]
    [Arguments("ああ", ECCLevel.L)]
    public void CreateQrCode_Utf8Bom_ShortMultibyteText_IsDecodable(string content, ECCLevel eccLevel)
    {
        // 1-2 char multi-byte text: encode buffer must reserve room for the 3 BOM bytes
        AssertQrCodeIsDecodable(content, eccLevel, EciMode.Utf8, utf8BOM: true);
    }

    [Test]
    [Arguments("https://example.com/item?id=123456789012345678901234567890")]
    [Arguments("Order 12345 item 6789 ref 0000111122223333")]
    [Arguments("日本語1234567890123456789012345678901234567890")]
    [Arguments("Café 12345678901234567890")]
    public async Task CreateQrCode_OptimalSegmentation_IsDecodableByZXing(string content)
    {
        // Mixed-mode streams (several mode segments in one symbol) must be readable
        // by an independent decoder, not only by this library's own.
        var qr = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal });

        using var bitmap = QrCodeToSKBitmap(qr);
        var result = _reader.Decode(bitmap);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Text).IsEqualTo(content);
    }

    [Test]
    public async Task CreateQrCode_WithEccBoost_IsDecodableByZXing_AtTheBoostedLevel()
    {
        // "HELLO" at L auto-selects version 1; boost raises the level to H (v1-H
        // alphanumeric capacity 10 >= 5). The boosted format information must be
        // readable by an independent decoder, which also reports the level it saw.
        var qr = QRCodeGenerator.CreateQrCode("HELLO", ECCLevel.L, new QRCodeGeneratorOptions { BoostEccLevel = true });

        using var bitmap = QrCodeToSKBitmap(qr);
        var result = _reader.Decode(bitmap);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Text).IsEqualTo("HELLO");
        await Assert.That(result.ResultMetadata.ContainsKey(ZXing.ResultMetadataType.ERROR_CORRECTION_LEVEL)).IsTrue();
        await Assert.That(result.ResultMetadata[ZXing.ResultMetadataType.ERROR_CORRECTION_LEVEL].ToString()).IsEqualTo("H");
    }

    // helpers

    /// <summary>
    /// Assert that generated QR code is decodable and content matches.
    /// </summary>
    private void AssertQrCodeIsDecodable(string expectedContent, ECCLevel eccLevel, EciMode eciMode, bool utf8BOM = false)
    {
        AssertQrCodeIsDecodableBinary(expectedContent, eccLevel, eciMode, utf8BOM);
        AssertQrCodeIsDecodableString(expectedContent, eccLevel, eciMode, utf8BOM);
    }

    private async Task AssertQrCodeIsDecodableBinary(string expectedContent, ECCLevel eccLevel, EciMode eciMode, bool utf8BOM = false)
    {
        var qr = QRCodeGenerator.CreateQrCode(expectedContent.AsSpan(), eccLevel, new QRCodeGeneratorOptions { Utf8BOM = utf8BOM, EciMode = eciMode, QuietZoneSize = 4 });

        // Convert QRCodeData to SKBitmap
        using var bitmap = QrCodeToSKBitmap(qr);

        // Decode using ZXing
        var result = _reader.Decode(bitmap);

        // Assert decoding succeeded
        await Assert.That(result).IsNotNull();
        await Assert.That(result.BarcodeFormat).IsEquivalentTo(BarcodeFormat.QR_CODE);

        // Assert content matches (some decoders surface the BOM as a leading U+FEFF)
        await Assert.That(result.Text.TrimStart('\uFEFF')).IsEquivalentTo(expectedContent);

        // Additional metadata checks
        if (result.ResultMetadata != null)
        {
            // Verify ECC level if available
            if (result.ResultMetadata.ContainsKey(ZXing.ResultMetadataType.ERROR_CORRECTION_LEVEL))
            {
                var decodedEccLevel = result.ResultMetadata[ZXing.ResultMetadataType.ERROR_CORRECTION_LEVEL].ToString();
                var expectedEccString = eccLevel.ToString();
                await Assert.That(decodedEccLevel).IsEquivalentTo(expectedEccString);
            }
        }
    }

    private async Task AssertQrCodeIsDecodableString(string expectedContent, ECCLevel eccLevel, EciMode eciMode, bool utf8BOM = false)
    {
        var qr = QRCodeGenerator.CreateQrCode(expectedContent, eccLevel, new QRCodeGeneratorOptions { Utf8BOM = utf8BOM, EciMode = eciMode, QuietZoneSize = 4 });

        // Convert QRCodeData to SKBitmap
        using var bitmap = QrCodeToSKBitmap(qr);

        // Decode using ZXing
        var result = _reader.Decode(bitmap);

        // Assert decoding succeeded
        await Assert.That(result).IsNotNull();
        await Assert.That(result.BarcodeFormat).IsEquivalentTo(BarcodeFormat.QR_CODE);

        // Assert content matches (some decoders surface the BOM as a leading U+FEFF)
        await Assert.That(result.Text.TrimStart('\uFEFF')).IsEquivalentTo(expectedContent);

        // Additional metadata checks
        if (result.ResultMetadata != null)
        {
            // Verify ECC level if available
            if (result.ResultMetadata.ContainsKey(ZXing.ResultMetadataType.ERROR_CORRECTION_LEVEL))
            {
                var decodedEccLevel = result.ResultMetadata[ZXing.ResultMetadataType.ERROR_CORRECTION_LEVEL].ToString();
                var expectedEccString = eccLevel.ToString();
                await Assert.That(decodedEccLevel).IsEquivalentTo(expectedEccString);
            }
        }
    }

    /// <summary>
    /// Convert QRCodeData to SKBitmap for decoding.
    /// Uses scaling for better decoding reliability.
    /// </summary>
    private static SKBitmap QrCodeToSKBitmap(QRCodeData qr)
    {
        var size = qr.Size;
        var scale = 10; // Scale up for better decoding
        var bitmap = new SKBitmap(size * scale, size * scale);

        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint();

        // Fill white background
        canvas.Clear(SKColors.White);

        // Draw black modules
        paint.Color = SKColors.Black;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (qr[y, x])
                {
                    canvas.DrawRect(x * scale, y * scale, scale, scale, paint);
                }
            }
        }

        return bitmap;
    }
}
