using System.Text;
using FeatherQR.Internals;
using FeatherQR.Internals.BinaryDecoders;
using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;
using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace FeatherQR.Tests;

/// <summary>
/// Shift_JIS in Byte segments: read when ECI 20 declares it, and guessed when nothing is declared and the bytes are not UTF-8.
/// The guess reads a segment as Shift_JIS when its bytes are well formed as Shift_JIS and hold a run of three half-width katakana, or a byte from 0x80 to 0x9F, which ISO-8859-1 text never does.
/// Each class of that rule has a case here, and the cases that must stay ISO-8859-1 outnumber the ones that turn Japanese.
/// </summary>
public class ByteSegmentShiftJisTest
{
    private static byte[] ShiftJis(string text) => Encoding.GetEncoding(932).GetBytes(text);

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    private static string Widened(byte[] bytes) => string.Concat(bytes.Select(static b => (char)b));

    private static (DecodeStatus Status, string Text) DecodeBytes(byte[] payload, ByteSegmentCharset charset, int destinationLength = 128)
    {
        var reader = new BitReader(payload);
        var destination = new char[destinationLength];
        var charsWritten = 0;
        var status = SegmentDecoders.DecodeBytePayload(ref reader, payload.Length * 8, payload.Length, charset, new byte[Math.Max(payload.Length, 1)], destination, ref charsWritten);
        return (status, new string(destination, 0, charsWritten));
    }

    private static byte[] Build(params (int Value, int Bits)[] fields)
    {
        var buffer = new byte[128];
        var writer = new BitWriter(buffer);
        foreach (var (value, bits) in fields)
            writer.Write(value, bits);
        writer.Flush();
        return writer.GetData().ToArray();
    }

    private static (int, int)[] Bytes(byte[] payload) => [.. payload.Select(static b => ((int)b, 8))];

    // The guess, on undeclared segments.

    [Test]
    [Arguments("Google モバイル\r\nhttp://google.jp")]          // a run of four double-byte characters
    [Arguments("ﾃﾞｻﾞｲﾝQR\r\nhttp://d-qr.net/ex/")]             // a run of six half-width katakana
    [Arguments("*ﾀﾞﾌﾞﾙQR*")]
    [Arguments("<ﾃﾞｻﾞｲﾝQR> \r\nｲﾗｽﾄ入りｶﾗｰQR")]                 // half-width katakana beside kanji and hiragana
    [Arguments("100円")]                                         // one double-byte character: its lead byte is no ISO-8859-1 text
    [Arguments("日本")]
    [Arguments("価格は\\100~200円です")]                          // ASCII \ and ~ stay themselves
    [Arguments("名前\t住所")]                                     // a tab is text
    public async Task Undeclared_JapaneseText_ReadsAsShiftJis(string text)
    {
        var (status, decoded) = DecodeBytes(ShiftJis(text), ByteSegmentCharset.Unspecified);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(decoded).IsEqualTo(text);
    }

    /// <summary>
    /// ISO-8859-1 text as people write it. None of it is well formed Shift_JIS with a run of three half-width katakana or a byte from 0x80 to 0x9F:
    /// a lowercase accented letter before a space or at the end is a lead byte with no trail, ñ, ö and ü are no lead byte at all, and uppercase accented letters seldom stand three in a row.
    /// A lowercase accented letter before another letter is a well formed pair, and French puts three of them in a row; pairs alone do not make the segment Shift_JIS.
    /// </summary>
    [Test]
    [Arguments("café")]
    [Arguments("été")]
    [Arguments("naïve")]
    [Arguments("déjà vu")]
    [Arguments("Müller")]
    [Arguments("Straße")]
    [Arguments("GRÖßE")]         // two in a row
    [Arguments("señor")]
    [Arguments("smörgåsbord")]
    [Arguments("ÃÀ")]
    [Arguments("éaèb")]          // two lead-and-trail pairs in a row
    [Arguments("£5")]
    [Arguments("½ cup")]
    [Arguments("À la carte")]
    [Arguments("ÉTÉ")]
    [Arguments("©2024")]
    [Arguments("«oui»")]
    [Arguments("¿Qué?")]
    [Arguments("générée")]       // three lead-and-trail pairs in a row
    [Arguments("Télémétrie")]
    [Arguments("préférée.")]
    [Arguments("Déréférencement d'une éventuelle référence null.")]
    public async Task Undeclared_Latin1Text_StaysLatin1(string text)
    {
        var (status, decoded) = DecodeBytes(Latin1(text), ByteSegmentCharset.Unspecified);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(decoded).IsEqualTo(text);
    }

    /// <summary>
    /// What the rule costs: three ISO-8859-1 characters from 0xA1 to 0xDF in a row are a run of three half-width katakana.
    /// An ISO-8859-1 symbol that holds such a run needs its ECI header to be read as written.
    /// </summary>
    [Test]
    [Arguments("ÄÖÜ", "ﾄﾖﾜ")]
    [Arguments("¼½¾", "ｼｽｾ")]
    public async Task Undeclared_ThreeHighLatin1CharactersInARow_ReadAsHalfWidthKatakana(string written, string read)
    {
        var (status, decoded) = DecodeBytes(Latin1(written), ByteSegmentCharset.Unspecified);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(decoded).IsEqualTo(read);
    }

    /// <summary>
    /// What leaving pairs out of the evidence costs: Japanese written only in kanji whose two bytes are both ISO-8859-1 letters, with no kana or commoner kanji beside them, keeps the ISO-8859-1 reading.
    /// </summary>
    [Test]
    [Arguments("薔薇")]
    [Arguments("薔薇饂飩")]
    public async Task Undeclared_KanjiWhoseBytesAreAllIso8859_1Letters_StayLatin1(string text)
    {
        var payload = ShiftJis(text);
        await Assert.That(payload.Any(static b => b is (>= 0x80 and <= 0x9F) or (>= 0xA1 and <= 0xDF))).IsFalse();

        var (status, decoded) = DecodeBytes(payload, ByteSegmentCharset.Unspecified);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(decoded).IsEqualTo(Widened(payload));
    }

    /// <summary>
    /// Bytes that are not well formed Shift_JIS, each for one reason. They keep the ISO-8859-1 reading, one character a byte, whatever else they hold.
    /// </summary>
    [Test]
    [Arguments(new byte[] { 0x83, 0x82, 0x83, 0x6F, 0x83 })]              // a lead byte with no trail
    [Arguments(new byte[] { 0x83, 0x20, 0x83, 0x82, 0x83, 0x6F })]        // a trail below 0x40
    [Arguments(new byte[] { 0x83, 0x7F, 0x83, 0x82, 0x83, 0x6F })]        // 0x7F is no trail
    [Arguments(new byte[] { 0x83, 0xFD, 0x83, 0x82, 0x83, 0x6F })]        // a trail above 0xFC
    [Arguments(new byte[] { 0x80, 0x83, 0x82, 0x83, 0x6F })]              // 0x80 is no lead
    [Arguments(new byte[] { 0xA0, 0x83, 0x82, 0x83, 0x6F })]              // 0xA0 is no lead
    [Arguments(new byte[] { 0xF0, 0x40, 0x83, 0x82, 0x83, 0x6F })]        // 0xF0 and above are no lead
    [Arguments(new byte[] { 0x01, 0x83, 0x82, 0x83, 0x6F, 0x83, 0x43 })]  // a control character other than tab, CR and LF marks binary data
    public async Task Undeclared_NotWellFormedShiftJis_StaysLatin1(byte[] payload)
    {
        var (status, decoded) = DecodeBytes(payload, ByteSegmentCharset.Unspecified);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(decoded).IsEqualTo(Widened(payload));
    }

    /// <summary>Valid UTF-8 is read as UTF-8 first, also where the same bytes are well formed Shift_JIS with a run of three.</summary>
    [Test]
    [Arguments("éé")]     // C3 A9 C3 A9: four bytes in the half-width katakana range
    [Arguments("あ")]
    [Arguments("日本語")]
    public async Task Undeclared_ValidUtf8_StaysUtf8(string text)
    {
        var (status, decoded) = DecodeBytes(Encoding.UTF8.GetBytes(text), ByteSegmentCharset.Unspecified);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(decoded).IsEqualTo(text);
    }

    /// <summary>
    /// A cell outside JIS X 0208 fails the segment, as it does in Kanji mode: 0x8740 is a circled one in the vendor rows, and a guessed character cannot be told from a correct one.
    /// </summary>
    [Test]
    public async Task Undeclared_CellOutsideJisX0208_IsUnmappedCharacter()
    {
        var (status, _) = DecodeBytes([0x83, 0x82, 0x87, 0x40], ByteSegmentCharset.Unspecified);

        await Assert.That(status).IsEqualTo(DecodeStatus.UnmappedCharacter);
    }

    /// <summary>The Shift_JIS reading needs a character a symbol, not a byte: four fit where eight bytes were read, three do not, and room is judged before a cell is looked up.</summary>
    [Test]
    public async Task Undeclared_ShiftJis_DestinationIsCountedInCharacters()
    {
        var payload = ShiftJis("モバイル");

        await Assert.That(DecodeBytes(payload, ByteSegmentCharset.Unspecified, destinationLength: 4)).IsEqualTo((DecodeStatus.Success, "モバイル"));
        await Assert.That(DecodeBytes(payload, ByteSegmentCharset.Unspecified, destinationLength: 3).Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(DecodeBytes([0x83, 0x82, 0x87, 0x40], ByteSegmentCharset.Unspecified, destinationLength: 1).Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
    }

    /// <summary>ASCII ahead of the Japanese text fills a short destination before the first Shift_JIS byte is met.</summary>
    [Test]
    public async Task Undeclared_ShiftJisAfterAscii_DestinationOneShort_IsDestinationTooSmall()
    {
        var text = "Google モバイル";
        var payload = ShiftJis(text);

        await Assert.That(DecodeBytes(payload, ByteSegmentCharset.Unspecified, destinationLength: text.Length)).IsEqualTo((DecodeStatus.Success, text));
        await Assert.That(DecodeBytes(payload, ByteSegmentCharset.Unspecified, destinationLength: text.Length - 1).Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(DecodeBytes(payload, ByteSegmentCharset.Unspecified, destinationLength: 3).Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
    }

    // ECI 20, declared.

    /// <summary>A declared segment needs no run and no telling byte: the symbol said what it holds.</summary>
    [Test]
    [Arguments("モ")]
    [Arguments("ﾃ")]
    [Arguments("A\\~")]
    public async Task Declared_ReadsEverySegmentAsShiftJis(string text)
    {
        await Assert.That(DecodeBytes(ShiftJis(text), ByteSegmentCharset.ShiftJis)).IsEqualTo((DecodeStatus.Success, text));
    }

    /// <summary>Two bytes the guess leaves as ISO-8859-1 are two half-width katakana once declared.</summary>
    [Test]
    public async Task Declared_TwoHighBytes_AreHalfWidthKatakana()
    {
        await Assert.That(DecodeBytes(Latin1("ÄÖ"), ByteSegmentCharset.Unspecified)).IsEqualTo((DecodeStatus.Success, "ÄÖ"));
        await Assert.That(DecodeBytes(Latin1("ÄÖ"), ByteSegmentCharset.ShiftJis)).IsEqualTo((DecodeStatus.Success, "ﾄﾖ"));
    }

    /// <summary>Bytes that are valid UTF-8, a byte order mark among them, are still Shift_JIS when the symbol declares it.</summary>
    [Test]
    public async Task Declared_ValidUtf8Bytes_AreStillShiftJis()
    {
        // E3 81 and 82 A0 are two Shift_JIS pairs, where E3 81 82 would be one UTF-8 character
        var (status, decoded) = DecodeBytes([0xE3, 0x81, 0x82, 0xA0], ByteSegmentCharset.ShiftJis);
        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(decoded).IsEqualTo(Encoding.GetEncoding(932).GetString([0xE3, 0x81, 0x82, 0xA0]));
        await Assert.That(decoded.Length).IsEqualTo(2);

        // EF BB BF is not consumed as a mark: EF BB is a pair past the cells Kanji mode has
        await Assert.That(DecodeBytes([0xEF, 0xBB, 0xBF], ByteSegmentCharset.ShiftJis).Status).IsEqualTo(DecodeStatus.UnmappedCharacter);
    }

    [Test]
    [Arguments(new byte[] { 0x83 })]              // a lead byte with no trail
    [Arguments(new byte[] { 0x83, 0x20 })]        // a trail below 0x40
    [Arguments(new byte[] { 0x83, 0x7F })]
    [Arguments(new byte[] { 0x83, 0xFD })]
    [Arguments(new byte[] { 0x80 })]
    [Arguments(new byte[] { 0xA0 })]
    [Arguments(new byte[] { 0xF0, 0x40 })]
    public async Task Declared_NotWellFormed_IsInvalidBitstream(byte[] payload)
    {
        await Assert.That(DecodeBytes(payload, ByteSegmentCharset.ShiftJis).Status).IsEqualTo(DecodeStatus.InvalidBitstream);
    }

    [Test]
    public async Task Declared_CellOutsideJisX0208_IsUnmappedCharacter()
    {
        await Assert.That(DecodeBytes([0x87, 0x40], ByteSegmentCharset.ShiftJis).Status).IsEqualTo(DecodeStatus.UnmappedCharacter);
    }

    // Through each symbology's bit stream.

    [Test]
    public async Task StandardQR_Eci20_ReadsTheByteSegmentAsShiftJis()
    {
        var payload = ShiftJis("ﾓﾊﾞ"); // three half-width katakana would be guessed too, so two segments follow that would not
        var one = ShiftJis("ﾓ");
        var data = Build([(0b0111, 4), (20, 8), (0b0100, 4), (payload.Length, 8), .. Bytes(payload), (0b0100, 4), (one.Length, 8), .. Bytes(one), (0b0000, 4)]);

        var destination = new char[64];
        var status = QRBinaryDecoder.DecodeBitStream(data, 1, destination, out var charsWritten, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(new string(destination, 0, charsWritten)).IsEqualTo("ﾓﾊﾞﾓ");
    }

    /// <summary>The shape of a photographed symbol in the corpus: Kanji segments, then an undeclared Byte segment holding half-width katakana.</summary>
    [Test]
    public async Task StandardQR_KanjiThenUndeclaredBytes_ReadsBoth()
    {
        // 外 is 0x8A4F, and its 13-bit Kanji value is (0x8A4F - 0x8140) folded: 0x09 * 0xC0 + 0x0F
        var bytes = ShiftJis("*ﾀﾞﾌﾞﾙQR*");
        var data = Build([(0b1000, 4), (1, 8), (0x09 * 0xC0 + 0x0F, 13), (0b0100, 4), (bytes.Length, 8), .. Bytes(bytes), (0b0000, 4)]);

        var destination = new char[64];
        var status = QRBinaryDecoder.DecodeBitStream(data, 1, destination, out var charsWritten, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(new string(destination, 0, charsWritten)).IsEqualTo("外*ﾀﾞﾌﾞﾙQR*");
    }

    [Test]
    public async Task RmQR_Eci20AndUndeclared_ReadShiftJis()
    {
        const RmQRVersion version = RmQRVersion.R11x59;
        var countBits = RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte);
        var one = ShiftJis("ﾓ");
        var declared = Build([(0b111, 3), (20, 8), (0b011, 3), (one.Length, countBits), .. Bytes(one), (0b000, 3)]);
        var text = ShiftJis("日本語");
        var undeclared = Build([(0b011, 3), (text.Length, countBits), .. Bytes(text), (0b000, 3)]);

        var destination = new char[64];
        await Assert.That(RmQRBinaryDecoder.DecodeBitStream(declared, declared.Length * 8, version, destination, out var written)).IsEqualTo(DecodeStatus.Success);
        await Assert.That(new string(destination, 0, written)).IsEqualTo("ﾓ");
        await Assert.That(RmQRBinaryDecoder.DecodeBitStream(undeclared, undeclared.Length * 8, version, destination, out written)).IsEqualTo(DecodeStatus.Success);
        await Assert.That(new string(destination, 0, written)).IsEqualTo("日本語");
    }

    /// <summary>Micro QR has no ECI, so its Byte segments are always guessed.</summary>
    [Test]
    public async Task MicroQR_UndeclaredBytes_ReadShiftJis()
    {
        // M4: a 3-bit mode indicator (Byte is 2) and a 5-bit count
        var text = ShiftJis("日本語ﾃｽﾄ");
        var data = Build([(2, 3), (text.Length, 5), .. Bytes(text), (0, 9)]);

        var destination = new char[32];
        var status = MicroQRBinaryDecoder.DecodeBitStream(data, data.Length * 8, MicroQRVersion.M4, destination, out var written);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(new string(destination, 0, written)).IsEqualTo("日本語ﾃｽﾄ");
    }

    // Symbols another encoder writes.

    public static IEnumerable<(string Text, bool Eci)> ForeignShiftJisSymbols()
    {
        foreach (var eci in new[] { false, true })
        {
            yield return ("Google モバイル\r\nhttp://google.jp", eci);
            yield return ("ﾃﾞｻﾞｲﾝQR\r\nhttp://d-qr.net/ex/", eci);
            yield return ("TEL:03-1234-5678;NAME:山田 太郎;", eci);
        }
    }

#if !DEBUG
    /// <summary>The bytes one read of a Byte segment allocates once warm, the fewest over a few rounds.</summary>
    private static long AllocatedBytes(byte[] payload, ByteSegmentCharset charset)
    {
        var buffer = new byte[payload.Length];
        var destination = new char[payload.Length];
        var fewest = long.MaxValue;
        for (var round = 0; round < 4; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 8; i++)
            {
                var reader = new BitReader(payload);
                var written = 0;
                SegmentDecoders.DecodeBytePayload(ref reader, payload.Length * 8, payload.Length, charset, buffer, destination, ref written);
            }
            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return fewest;
    }

    /// <summary>The guess and the Shift_JIS reading allocate nothing, whichever way the guess goes. Release only, as the other allocation tests are.</summary>
    [Test]
    public async Task ShiftJis_GuessedDeclaredOrRuledOut_AllocatesNothing()
    {
        var japanese = AllocatedBytes(ShiftJis("<ﾃﾞｻﾞｲﾝQR> \r\nｲﾗｽﾄ入りｶﾗｰQR"), ByteSegmentCharset.Unspecified);
        var declared = AllocatedBytes(ShiftJis("価格は100円です"), ByteSegmentCharset.ShiftJis);
        var french = AllocatedBytes(Latin1("Déréférencement d'une éventuelle référence null."), ByteSegmentCharset.Unspecified);

        await Assert.That(japanese).IsEqualTo(0);
        await Assert.That(declared).IsEqualTo(0);
        await Assert.That(french).IsEqualTo(0);
    }
#endif

    /// <summary>
    /// Standard QR and rMQR declare ISO-8859-1 wherever they write a byte past ASCII, so text whose bytes look like UTF-8 or Shift_JIS reads back as written, in one Byte segment or split into runs.
    /// Micro QR cannot declare it and is covered by <see cref="MicroQRUndeclaredCharsetTest"/>.
    /// </summary>
    [Test]
    [Arguments("ÄÖÜ")]
    [Arguments("¼½¾")]
    [Arguments("Ã©")]
    [Arguments("don\u0092t")]
    [Arguments("ÄÖÜ 0123456789012345 ¼½¾")]
    public async Task OwnSymbols_Latin1TextThatLooksOtherwise_RoundTrips(string text)
    {
        foreach (var options in new[] { default, new QRCodeGeneratorOptions { Segmentation = QRSegmentation.Optimal } })
        {
            var symbol = QRCodeGenerator.Create(text, QREccLevel.M, options);

            await Assert.That(QRCodeDecoder.TryDecode(symbol, out var decoded)).IsTrue();
            await Assert.That(decoded).IsEqualTo(text).Because($"Standard QR, {options.Segmentation}");
        }
        foreach (var options in new[] { default, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal } })
        {
            var symbol = RmQRCodeGenerator.Create(text, RmQREccLevel.M, options);

            await Assert.That(RmQRCodeDecoder.TryDecode(symbol, out var decoded)).IsTrue();
            await Assert.That(decoded).IsEqualTo(text).Because($"rMQR, {options.Segmentation}");
        }
    }

    /// <summary>
    /// Another encoder's Byte-mode Shift_JIS, with ECI 20 and without, through the public decoder. Text that is not all double-byte keeps that encoder out of Kanji mode.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ForeignShiftJisSymbols))]
    public async Task ForeignEncoder_ByteModeShiftJis_Decodes(string text, bool eci)
    {
        var writer = new BarcodeWriterGeneric
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions { ErrorCorrection = ErrorCorrectionLevel.M, Margin = 4, CharacterSet = "Shift_JIS", DisableECI = !eci },
        };
        var matrix = writer.Encode(text);
        var size = matrix.Width;
        var modules = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
                modules[y * size + x] = matrix[x, y] ? (byte)1 : (byte)0;
        }

        await Assert.That(QRCodeDecoder.TryDecode(modules, size, out var decoded, out var info)).IsTrue().Because($"eci {eci}: {info.Status}");
        await Assert.That(decoded).IsEqualTo(text);
    }
}
