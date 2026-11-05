using System.Text;
using FeatherQR.SkiaSharp;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The bytes calls of <see cref="QRCodeDecoder"/> and <see cref="QRCodeDecoderExtensions"/>, and the ECI assignment both outputs report.
/// </summary>
public class QRCodeDecoderBytesTest
{
    private const int Byte = 0b0100;
    private const int Eci = 0b0111;

    private static readonly byte[] binaryPayload = [0x00, 0xFF, 0x10, 0x80];

    /// <summary>Version 1-M, ECI 899 (binary data), then the payload in one Byte segment: content the text calls refuse.</summary>
    private static byte[] Eci899Symbol()
        => PayloadSymbols.StandardQr(1, QREccLevel.M, (Eci, 4), ((0b10 << 14) | 899, 16), (Byte, 4), (binaryPayload.Length, 8),
            (binaryPayload[0], 8), (binaryPayload[1], 8), (binaryPayload[2], 8), (binaryPayload[3], 8));

    [Test]
    [Arguments("hello", -1)]
    [Arguments("héllo", 3)]
    [Arguments("こんにちは 🎉", 26)]
    public async Task GeneratorText_BytesAreWhatItWrote_AndBothCallsReportTheAssignment(string text, int assignment)
    {
        var data = QRCodeGenerator.Create(text, QREccLevel.M);
        var expectedBytes = assignment == 3 ? Encoding.Latin1.GetBytes(text) : Encoding.UTF8.GetBytes(text);
        int? expectedAssignment = assignment < 0 ? null : assignment;

        await Assert.That(QRCodeDecoder.TryDecode(data, out _, out var textInfo)).IsTrue();
        await Assert.That(textInfo.EciAssignment).IsEqualTo(expectedAssignment);

        await Assert.That(QRCodeDecoder.TryDecodeBytes(data, out var bytes, out var info)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(expectedBytes, CollectionOrdering.Matching);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(info.EciAssignment).IsEqualTo(expectedAssignment);
    }

    [Test]
    public async Task AssignmentWithoutACharset_TextFailsAndNamesIt_BytesRead()
    {
        var modules = Eci899Symbol();

        await Assert.That(QRCodeDecoder.TryDecode(modules, 21, out var text, out var textInfo)).IsFalse();
        await Assert.That(text).IsEmpty();
        await Assert.That(textInfo.Status).IsEqualTo(DecodeStatus.UnsupportedContent);
        await Assert.That(textInfo.EciAssignment).IsEqualTo(899);

        await Assert.That(QRCodeDecoder.TryDecodeBytes(modules, 21, out var bytes, out var info)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(binaryPayload, CollectionOrdering.Matching);
        await Assert.That(info.EciAssignment).IsEqualTo(899);
        await Assert.That(info.Version).IsEqualTo(1);
        await Assert.That(info.EccLevel).IsEqualTo(QREccLevel.M);
    }

    [Test]
    public async Task KanjiCellOutsideJisX0208_TextFailsAndNamesTheAssignment_BytesGiveThePair()
    {
        // ECI 20 (Shift_JIS), then one Kanji character, 0x8740, which JIS X 0208 does not assign
        var modules = PayloadSymbols.StandardQr(1, QREccLevel.M, (Eci, 4), (20, 8), (0b1000, 4), (1, 8), (1152, 13));

        await Assert.That(QRCodeDecoder.TryDecode(modules, 21, out _, out var textInfo)).IsFalse();
        await Assert.That(textInfo.Status).IsEqualTo(DecodeStatus.UnmappedCharacter);
        await Assert.That(textInfo.EciAssignment).IsEqualTo(20);

        await Assert.That(QRCodeDecoder.TryDecodeBytes(modules, 21, out var bytes, out var info)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x87, 0x40 }, CollectionOrdering.Matching);
        await Assert.That(info.EciAssignment).IsEqualTo(20);
    }

    [Test]
    public async Task SpanForm_ExactRoomReads_OneShortIsDestinationTooSmall()
    {
        var modules = Eci899Symbol();

        var exact = new byte[binaryPayload.Length];
        await Assert.That(QRCodeDecoder.TryDecodeBytes(modules, 21, exact, out var written, out var info)).IsTrue();
        await Assert.That(written).IsEqualTo(binaryPayload.Length);
        await Assert.That(exact).IsEquivalentTo(binaryPayload, CollectionOrdering.Matching);
        await Assert.That(info.EciAssignment).IsEqualTo(899);

        var shortRoom = new byte[binaryPayload.Length - 1];
        shortRoom.AsSpan().Fill(0xA5);
        await Assert.That(QRCodeDecoder.TryDecodeBytes(modules, 21, shortRoom, out written, out info)).IsFalse();
        await Assert.That(written).IsEqualTo(0);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(info.EciAssignment).IsNull().Because("the symbol declares 899, and only a read or a refused content reports it");
        await Assert.That(shortRoom.All(b => b == 0xA5)).IsTrue().Because("the destination is left as it was");
    }

    /// <summary>No symbol holds more bytes than version 40 does, so a destination far longer than that takes no buffer as long as itself.</summary>
    [Test]
    public async Task SpanForms_DestinationFarLargerThanAnySymbol_TakeNoBufferThatLong()
    {
        var modules = Eci899Symbol();
        var (luminance, width, height) = PayloadSymbols.Render(modules, 21, 21);
        var destination = new byte[16 << 20];

        var (read, readImage, allocated) = DecodeBothSpanForms(modules, luminance, width, height, destination);

        await Assert.That(read).IsTrue();
        await Assert.That(readImage).IsTrue();
        await Assert.That(allocated).IsLessThan(destination.Length).Because("a char buffer as long as the destination takes twice its length");
    }

    /// <summary>
    /// The bytes calls mark their own decode only. A text call right after them on the same thread reads text, also after a bytes call that threw.
    /// A mark left set would read the ECI 899 symbol as bytes and the UTF-8 one one byte a char.
    /// </summary>
    [Test]
    public async Task TextCalls_AfterBytesCallsOnTheSameThread_ReadText()
    {
        var (afterRead, afterThrow) = TextAfterBytes(Eci899Symbol(), QRCodeGenerator.Create("日本", QREccLevel.M));

        await Assert.That(afterRead).IsEqualTo(DecodeStatus.UnsupportedContent);
        await Assert.That(afterThrow).IsEqualTo("日本");
    }

    /// <summary>
    /// A restored matrix whose whole edge row or column is light still holds its data at most levels, since Reed-Solomon corrects the cleared data modules.
    /// The text call decodes the core as it stands, so the bytes call reads every matrix the text call reads.
    /// </summary>
    [Test]
    public async Task QRCodeData_WithALightEdge_IsReadAsTheTextCallReadsIt()
    {
        var readByText = 0;
        foreach (var level in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
        {
            for (var edge = 0; edge < 4; edge++)
            {
                var restored = WithLightEdge(QRCodeGenerator.Create("HELLO BYTES", level), edge);
                if (restored is null || !QRCodeDecoder.TryDecode(restored, out var text, out _))
                    continue;
                readByText++;

                await Assert.That(QRCodeDecoder.TryDecodeBytes(restored, out var bytes, out var info)).IsTrue();
                await Assert.That(info.Status).IsEqualTo(DecodeStatus.Success);
                await Assert.That(bytes).IsEquivalentTo(Encoding.ASCII.GetBytes(text), CollectionOrdering.Matching);
            }
        }

        await Assert.That(readByText).IsGreaterThan(0).Because("the premise: some light edges leave the matrix readable as text");
    }

    [Test]
    public async Task NullData_Throws_AsTheTextCallDoes()
    {
        await Assert.That(() => QRCodeDecoder.TryDecodeBytes((QRCodeData)null!, out _, out _)).ThrowsExactly<ArgumentNullException>();
        await Assert.That(() => QRCodeDecoderExtensions.TryDecodeBytes((global::SkiaSharp.SKBitmap)null!, out _, out _)).ThrowsExactly<ArgumentNullException>();
    }

    [Test]
    public async Task BorderedModules_AreRead_AsTheTextCallReadsThem()
    {
        var options = new QRCodeGeneratorOptions { EciMode = EciMode.Utf8 };
        var size = QRCodeGenerator.Create("bytes", QREccLevel.L, options).Size;
        var modules = new byte[size * size];
        QRCodeGenerator.Create("bytes", QREccLevel.L, modules, options);

        await Assert.That(QRCodeDecoder.TryDecodeBytes(modules, size, out var bytes, out var info)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo("bytes"u8.ToArray(), CollectionOrdering.Matching);
        await Assert.That(info.EciAssignment).IsEqualTo(26);
    }

    [Test]
    public async Task NoSymbol_IsFalse_WithNoBytes()
    {
        await Assert.That(QRCodeDecoder.TryDecodeBytes(new byte[21 * 21], 21, out var bytes, out var info)).IsFalse();
        await Assert.That(bytes).IsEmpty();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.InvalidMatrix);
        await Assert.That(info.EciAssignment).IsNull();
    }

    [Test]
    public async Task ModuleBufferTooSmall_Throws_AsTheTextCallDoes()
    {
        await Assert.That(() => QRCodeDecoder.TryDecodeBytes(new byte[20], 21, out _, out _)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => QRCodeDecoder.TryDecodeBytes(new byte[20], 21, new byte[8], out _, out _)).ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task Image_BothForms_ReadTheBytes_WithCornersAndAssignment()
    {
        var (luminance, width, height) = PayloadSymbols.Render(Eci899Symbol(), 21, 21);

        await Assert.That(QRCodeDecoder.TryDecodeImage(luminance, width, height, out _, out var textInfo)).IsFalse();
        await Assert.That(textInfo.Status).IsEqualTo(DecodeStatus.UnsupportedContent).Because("the premise: the text call finds the symbol and refuses its content");

        await Assert.That(QRCodeDecoder.TryDecodeImageBytes(luminance, width, height, out var bytes, out var info)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(binaryPayload, CollectionOrdering.Matching);
        await Assert.That(info.EciAssignment).IsEqualTo(899);
        await Assert.That(info.Corners.IsEmpty).IsFalse();

        var destination = new byte[QRCodeDecoder.GetMaxDecodedLength(1)];
        await Assert.That(QRCodeDecoder.TryDecodeImageBytes(luminance, width, height, destination, out var written, out _)).IsTrue();
        await Assert.That(destination.AsSpan(0, written).ToArray()).IsEquivalentTo(binaryPayload, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ImageText_ReportsTheAssignment_BesideTheCorners()
    {
        var data = QRCodeGenerator.Create("café", QREccLevel.M);
        var size = data.Size;
        var modules = new byte[size * size];
        QRCodeGenerator.Create("café", QREccLevel.M, modules);
        var (luminance, width, height) = PayloadSymbols.Render(modules, size, size, quietZone: 0);

        await Assert.That(QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info)).IsTrue();
        await Assert.That(text).IsEqualTo("café");
        await Assert.That(info.EciAssignment).IsEqualTo(3);
        await Assert.That(info.Corners.IsEmpty).IsFalse();
    }

    [Test]
    public async Task Bitmap_ReadsTheBytes()
    {
        using var bitmap = PayloadSymbols.Bitmap(Eci899Symbol(), 21, 21);

        await Assert.That(QRCodeDecoderExtensions.TryDecodeBytes(bitmap, out var bytes, out var info)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(binaryPayload, CollectionOrdering.Matching);
        await Assert.That(info.EciAssignment).IsEqualTo(899);
    }

    [Test]
    public async Task Bitmap_TooSmallForASymbol_IsFalse_WithNoBytes()
    {
        using var bitmap = new global::SkiaSharp.SKBitmap(8, 8);

        await Assert.That(QRCodeDecoderExtensions.TryDecodeBytes(bitmap, out var bytes, out var info)).IsFalse();
        await Assert.That(bytes).IsEmpty();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.NotDetected);
    }

    [Test]
    public async Task StructuredAppend_PartsJoinedInOrder_AreTheBytesTheSetWasMadeFrom()
    {
        const string text = "日本語のテキストを複数のシンボルに分けて、バイト列で読み戻す。";
        var parts = QRCodeGenerator.CreateStructuredAppend(text, QREccLevel.L, new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(2) });
        await Assert.That(parts.Length).IsGreaterThan(1).Because("the premise: the text needs a set");

        var joined = new List<byte>();
        for (var i = 0; i < parts.Length; i++)
        {
            await Assert.That(QRCodeDecoder.TryDecodeBytes(parts[i], out var bytes, out var info)).IsTrue();
            await Assert.That(info.StructuredAppend.Index).IsEqualTo(i);
            joined.AddRange(bytes);
        }

        await Assert.That(joined.ToArray()).IsEquivalentTo(Encoding.UTF8.GetBytes(text), CollectionOrdering.Matching);
    }

    // Helpers

    private static (bool Read, bool ReadImage, long Allocated) DecodeBothSpanForms(byte[] modules, byte[] luminance, int width, int height, byte[] destination)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var read = QRCodeDecoder.TryDecodeBytes(modules, 21, destination, out _, out _);
        var readImage = QRCodeDecoder.TryDecodeImageBytes(luminance, width, height, destination, out _, out _);
        return (read, readImage, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Text calls made right after bytes calls, with no await between that could move the work to another thread.</summary>
    private static (DecodeStatus AfterRead, string AfterThrow) TextAfterBytes(byte[] eci899, QRCodeData utf8)
    {
        QRCodeDecoder.TryDecodeBytes(eci899, 21, out _, out _);
        QRCodeDecoder.TryDecode(eci899, 21, out _, out var afterRead);

        try
        {
            QRCodeDecoder.TryDecodeBytes(new byte[20], 21, out _, out _);
        }
        catch (ArgumentException)
        {
        }
        QRCodeDecoder.TryDecode(utf8, out var text, out _);
        return (afterRead.Status, text);
    }

    /// <summary>The symbol restored from its raw data with one whole edge light: 0 top, 1 bottom, 2 left, 3 right. <c>null</c> when the restore refuses the format information that leaves.</summary>
    private static QRCodeData? WithLightEdge(QRCodeData data, int edge)
    {
        var raw = data.GetRawData();
        int size = raw[3];
        for (var i = 0; i < size; i++)
        {
            var (row, col) = edge switch
            {
                0 => (0, i),
                1 => (size - 1, i),
                2 => (i, 0),
                _ => (i, size - 1),
            };
            var bit = row * size + col;
            raw[4 + (bit >> 3)] &= (byte)~(1 << (7 - (bit & 7)));
        }

        try
        {
            return new QRCodeData(raw, 0);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Numeric gives the most bytes a bit (a byte a digit, three digits in 10 bits), so its full symbol is the largest byte count a version holds.
    /// Kanji is the one mode whose bytes outnumber its characters (two bytes a character), so its full symbol shows the bound holds bytes and not only characters.
    /// The counts are the standard's capacities at level L.
    /// </summary>
    [Test]
    [Arguments(1, 41, 10)]
    [Arguments(40, 7089, 1817)]
    public async Task MaxDecodedLength_HoldsTheDensestPayloadsAsBytes(int version, int digits, int kanji)
    {
        (string Content, int Bytes, QRCodeGeneratorOptions Options)[] full =
        [
            (new string('7', digits), digits, new QRCodeGeneratorOptions { Version = version }),
            (new string('漢', kanji), 2 * kanji, new QRCodeGeneratorOptions { Version = version, AllowKanji = true }),
        ];

        foreach (var (content, expectedBytes, options) in full)
        {
            var size = QRCodeGenerator.Create(content, QREccLevel.L, options).Size;
            var modules = new byte[size * size];
            QRCodeGenerator.Create(content, QREccLevel.L, modules, options);

            var destination = new byte[QRCodeDecoder.GetMaxDecodedLength(version)];
            await Assert.That(QRCodeDecoder.TryDecodeBytes(modules, size, destination, out var written, out _)).IsTrue();
            await Assert.That(written).IsEqualTo(expectedBytes);
        }
    }
}
