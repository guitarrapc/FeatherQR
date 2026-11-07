namespace FeatherQR.Tests;

/// <summary>
/// Text for a terminal: two module rows a line in half blocks, the quiet zone included, every line ended by <c>\n</c>.
/// By default a dark module is the filled half, for a light background. Inverted, a light module is, for a dark background.
/// </summary>
public class SymbolImageWriterTextTest
{
    [Test]
    [MethodDataSource(typeof(SymbolImageWriterSvgTest), nameof(SymbolImageWriterSvgTest.StandardQrVersionsAndLevels))]
    public async Task StandardQr_IsTheMatrixInHalfBlocks_EveryVersionAndLevel(int version, QREccLevel level)
    {
        var data = QRCodeGenerator.Create("1", level, new QRCodeGeneratorOptions { Version = version });
        await Assert.That(SymbolImageWriter.GetTerminalText(data)).IsEqualTo(Reference(SymbolImageWriterPngTest.Grid.Of(data), invert: false));
        await Assert.That(SymbolImageWriter.GetTerminalText(data, invert: true)).IsEqualTo(Reference(SymbolImageWriterPngTest.Grid.Of(data), invert: true));
    }

    [Test]
    [MethodDataSource(typeof(SymbolImageWriterSvgTest), nameof(SymbolImageWriterSvgTest.MicroQrVersionsAndLevels))]
    public async Task MicroQr_IsTheMatrixInHalfBlocks_EveryVersionAndLevel(MicroQRVersion version, MicroQREccLevel level)
    {
        var data = MicroQRCodeGenerator.Create("1", level, new MicroQRCodeGeneratorOptions { Version = version });
        await Assert.That(SymbolImageWriter.GetTerminalText(data)).IsEqualTo(Reference(SymbolImageWriterPngTest.Grid.Of(data), invert: false));
        await Assert.That(SymbolImageWriter.GetTerminalText(data, invert: true)).IsEqualTo(Reference(SymbolImageWriterPngTest.Grid.Of(data), invert: true));
    }

    [Test]
    [MethodDataSource(typeof(SymbolImageWriterSvgTest), nameof(SymbolImageWriterSvgTest.RmQrVersionsAndLevels))]
    public async Task RmQr_IsTheMatrixInHalfBlocks_EveryVersionAndLevel(RmQRVersion version, RmQREccLevel level)
    {
        var data = RmQRCodeGenerator.Create("1", level, new RmQRCodeGeneratorOptions { Version = version });
        await Assert.That(SymbolImageWriter.GetTerminalText(data)).IsEqualTo(Reference(SymbolImageWriterPngTest.Grid.Of(data), invert: false));
        await Assert.That(SymbolImageWriter.GetTerminalText(data, invert: true)).IsEqualTo(Reference(SymbolImageWriterPngTest.Grid.Of(data), invert: true));
    }

    /// <summary>
    /// Worked by hand from a Micro QR M1 with its quiet zone of 2, 15 modules a side. The first line is the two quiet zone rows.
    /// The second holds core rows 0 and 1: the quiet zone, the finder's corner (dark over dark), its top edge over its light inside,
    /// its other corner, and the light separator.
    /// </summary>
    [Test]
    public async Task FirstLines_AreTheDocumentedGlyphs()
    {
        var micro = MicroQRCodeGenerator.Create("1", MicroQREccLevel.ErrorDetectionOnly, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M1 });

        var lines = SymbolImageWriter.GetTerminalText(micro).Split('\n');
        await Assert.That(lines[0]).IsEqualTo(new string(' ', 15));
        await Assert.That(lines[1]).StartsWith("  █▀▀▀▀▀█ ");

        var inverted = SymbolImageWriter.GetTerminalText(micro, invert: true).Split('\n');
        await Assert.That(inverted[0]).IsEqualTo(new string('█', 15));
        await Assert.That(inverted[1]).StartsWith("██ ▄▄▄▄▄ █");
    }

    /// <summary>
    /// Every symbol has an odd number of rows with its quiet zone, so the last line's lower half lies past the matrix and is light.
    /// </summary>
    [Test]
    public async Task LastLine_HasALightLowerHalf()
    {
        var rm = RmQRCodeGenerator.Create("1", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 });
        var lines = SymbolImageWriter.GetTerminalText(rm).Split('\n');
        var inverted = SymbolImageWriter.GetTerminalText(rm, invert: true).Split('\n');

        // 7 rows and a quiet zone of 2 above and below make 11 rows, so 6 lines and an empty string after the last \n.
        await Assert.That(lines.Length).IsEqualTo(7);
        await Assert.That(lines[6]).IsEqualTo("");
        await Assert.That(lines[5].All(c => c is ' ' or '▀')).IsTrue();
        await Assert.That(inverted[5].All(c => c is '▄' or '█')).IsTrue();
    }

    [Test]
    public async Task EveryLine_IsTheSymbolsWidth_EndedByANewline()
    {
        var qr = QRCodeGenerator.Create("lines", QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 1 });
        var text = SymbolImageWriter.GetTerminalText(qr);

        await Assert.That(text).EndsWith("\n");
        await Assert.That(text).DoesNotContain("\r");
        var lines = text.TrimEnd('\n').Split('\n');
        await Assert.That(lines.Length).IsEqualTo((qr.Size + 1) / 2);
        await Assert.That(lines.All(l => l.Length == qr.Size)).IsTrue();
        await Assert.That(text.All(c => c is ' ' or '▀' or '▄' or '█' or '\n')).IsTrue();
    }

    [Test]
    public async Task NullData_Throws()
    {
        await Assert.That(() => SymbolImageWriter.GetTerminalText((QRCodeData)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.GetTerminalText((MicroQRCodeData)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => SymbolImageWriter.GetTerminalText((RmQRCodeData)null!)).Throws<ArgumentNullException>();
    }

    /// <summary>Half blocks from the module grid: upper half, lower half, both, or neither, with a row past the end light.</summary>
    private static string Reference(SymbolImageWriterPngTest.Grid grid, bool invert)
    {
        var text = new System.Text.StringBuilder();
        for (var row = 0; row < grid.Height; row += 2)
        {
            for (var col = 0; col < grid.Width; col++)
            {
                var top = grid[row, col] != invert;
                var bottom = (row + 1 < grid.Height && grid[row + 1, col]) != invert;
                text.Append(top ? (bottom ? '█' : '▀') : (bottom ? '▄' : ' '));
            }
            text.Append('\n');
        }
        return text.ToString();
    }
}
