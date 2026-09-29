using ZXing.Common;
using ZXing.QrCode.Internal;
using static FeatherQR.Tests.KanjiStreamReference;

namespace FeatherQR.Tests;

/// <summary>
/// ZXing.Net reads the Kanji symbols <see cref="QRCodeGenerator"/> writes, every encoder cell included (kanji-encoding-plan.md, phase 6.3).
/// ZXing.Net applies CP932 to a Kanji segment; the two agree on every encoder cell, because the seven cells where they differ have none, so any disagreement here is a symbol one reader or the other gets wrong.
/// The symbols are read from their module matrix, not from an image: what is under test is the bit stream.
/// </summary>
public class KanjiZXingCrossTest
{
    /// <summary>Chunk lengths that land in every count band (versions 1-9, 10-26, 27-40) at level L.</summary>
    private static readonly int[] ChunkLengths = [10, 150, 400, 1000, 1817];

    [Test]
    public async Task EveryEncoderCell_IsReadByZXing()
    {
        var decoder = new Decoder();
        var bands = new HashSet<int>();
        var start = 0;
        for (var k = 0; start < EncoderCells.Length; k++)
        {
            var text = Cells(start, Math.Min(ChunkLengths[k % ChunkLengths.Length], EncoderCells.Length - start));
            var symbol = QRCodeGenerator.Create(text, QREccLevel.L, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
            bands.Add(symbol.Version < 10 ? 0 : symbol.Version < 27 ? 1 : 2);

            var result = decoder.decode(ToBitMatrix(symbol), null);
            await Assert.That(result).IsNotNull().Because($"ZXing.Net could not read cells {start}..{start + text.Length - 1} (version {symbol.Version})");
            await Assert.That(result!.Text).IsEqualTo(text).Because($"cells {start}..{start + text.Length - 1}");
            start += text.Length;
        }

        await Assert.That(bands.Count).IsEqualTo(3).Because("every Kanji count width is read");
    }

    /// <summary>The text classes that stay in Byte mode read back too, so a reader never meets the two classes differently.</summary>
    [Test]
    [Arguments("日本語")]
    [Arguments("Привет")]
    [Arguments("QRコード")]
    [Arguments("日本～")]
    public async Task EligibleAndIneligibleText_IsReadByZXing(string text)
    {
        var symbol = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
        var result = new Decoder().decode(ToBitMatrix(symbol), null);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Text).IsEqualTo(text);
    }

    private static BitMatrix ToBitMatrix(QRCodeData symbol)
    {
        var matrix = new BitMatrix(symbol.Size);
        for (var row = 0; row < symbol.Size; row++)
            for (var col = 0; col < symbol.Size; col++)
                matrix[col, row] = symbol[row, col];
        return matrix;
    }
}
