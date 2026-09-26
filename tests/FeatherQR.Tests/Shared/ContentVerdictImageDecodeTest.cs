using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// A symbol whose content decodes to a verdict reports that verdict, whichever attempt read it.
/// The symbols are built from codewords through each symbology's ECC and placement: no generator here emits Kanji.
/// </summary>
public class ContentVerdictImageDecodeTest
{
    private const int Mapped = 0x889F; // 亜
    private const int Unmapped = 0x8740; // CP932 circled digit one, outside JIS X 0208

    public enum Symbology { StandardQR, MicroQR, RmQR }

    public enum Path
    {
        /// <summary>Evenly lit: the first attempt reads it.</summary>
        First,
        /// <summary>Light modules on dark paper: only the inverted retry reads it.</summary>
        Inverted,
        /// <summary>Under a shadow: only the regional pass reads it.</summary>
        Regional,
    }

    [Test]
    [Arguments(Symbology.MicroQR, Path.First)]
    [Arguments(Symbology.MicroQR, Path.Inverted)]
    [Arguments(Symbology.MicroQR, Path.Regional)]
    [Arguments(Symbology.RmQR, Path.First)]
    [Arguments(Symbology.RmQR, Path.Inverted)]
    [Arguments(Symbology.RmQR, Path.Regional)]
    public async Task MappableCell_Decodes(Symbology symbology, Path path)
    {
        var (status, text) = Decode(symbology, path, Mapped);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("亜");
    }

    [Test]
    [Arguments(Symbology.MicroQR, Path.First)]
    [Arguments(Symbology.MicroQR, Path.Inverted)]
    [Arguments(Symbology.MicroQR, Path.Regional)]
    [Arguments(Symbology.RmQR, Path.First)]
    [Arguments(Symbology.RmQR, Path.Inverted)]
    [Arguments(Symbology.RmQR, Path.Regional)]
    public async Task UnmappedCell_ReachesTheCallerAsUnmappedCharacter(Symbology symbology, Path path)
    {
        var (status, text) = Decode(symbology, path, Unmapped);

        await Assert.That(status).IsEqualTo(DecodeStatus.UnmappedCharacter);
        await Assert.That(text).IsEmpty();
    }

    /// <summary>
    /// On a turned symbol the right grid's verdict outranks a wrong grid's correction failure tried first.
    /// </summary>
    [Test]
    [Arguments(Symbology.MicroQR, 7f)]
    [Arguments(Symbology.MicroQR, 30f)]
    public async Task UnmappedCell_Turned_ReachesTheCallerAsUnmappedCharacter(Symbology symbology, float turn)
    {
        var (modules, columns, rows) = Build(symbology, Unmapped);
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => IsDark(modules, columns, rows, row, column, 2), columns + 4, rows + 4, 4f, turn, 0f, 0f, true, UnevenLight.Shadow, 0f, 0f);

        var (status, _) = DecodeImage(symbology, luminance, width, height);

        await Assert.That(status).IsEqualTo(DecodeStatus.UnmappedCharacter);
    }

    /// <summary>
    /// A low-density symbol read by the timing frame or the full sweep reports its verdict over another grid's correction failure.
    /// </summary>
    [Test]
    [Arguments(Symbology.StandardQR, false, 1.20f, 0.33f)]
    [Arguments(Symbology.MicroQR, false, 1.15f, 0f)]
    [Arguments(Symbology.RmQR, false, 1.00f, 0.66f)]
    [Arguments(Symbology.RmQR, true, 1.35f, 0f)]
    public async Task UnmappedCell_ReadByAFallbackInsideTheAttempt_ReachesTheCallerAsUnmappedCharacter(Symbology symbology, bool antiAliased, float pixelsPerModule, float offset)
    {
        var (modules, columns, rows) = Build(symbology, Unmapped);
        var (mapped, _, _) = Build(symbology, Mapped);
        const int quietZone = 2;
        (byte[] Luminance, int Width, int Height) Render(byte[] grid) => antiAliased
            ? AntiAliasedRenderer.Render((row, column) => IsDark(grid, columns, rows, row, column, quietZone), columns + 2 * quietZone, rows + 2 * quietZone, pixelsPerModule, offset, offset)
            : NearestNeighbourRenderer.Render((row, column) => IsDark(grid, columns, rows, row, column, quietZone), columns + 2 * quietZone, rows + 2 * quietZone, pixelsPerModule, offset, offset);
        var (mappedLuminance, mappedWidth, mappedHeight) = Render(mapped);
        var (luminance, width, height) = Render(modules);

        var (mappedStatus, _) = DecodeImage(symbology, mappedLuminance, mappedWidth, mappedHeight);
        var (status, _) = DecodeImage(symbology, luminance, width, height);

        await Assert.That(mappedStatus).IsEqualTo(DecodeStatus.Success);
        await Assert.That(status).IsEqualTo(DecodeStatus.UnmappedCharacter);
    }

    /// <summary>
    /// Standard QR's later grids for one symbol report their verdict over the first grid's failure.
    /// </summary>
    [Test]
    [Arguments(5, 4, true, 0f, 0f, 1.8f)]
    [Arguments(3, 4, true, 3f, 0f, 1.8f)]
    [Arguments(2, 4, false, 0f, 0.33f, 2.1f)]
    [Arguments(8, 2, true, 5f, 0.3f, 1.875f)]
    public async Task StandardQR_UnmappedCellReadByALaterGrid_ReachesTheCallerAsUnmappedCharacter(int version, int quietZone, bool antiAliased, float turn, float offset, float pixelsPerModule)
    {
        var size = 17 + 4 * version;
        (byte[] Luminance, int Width, int Height) Render(int sjis)
        {
            var modules = KanjiUnmappedCharacterEndToEndTest.BuildSymbol(sjis, version);
            return UnevenLightingRenderer.Render((row, column) => IsDark(modules, size, size, row, column, quietZone), size + 2 * quietZone, size + 2 * quietZone, pixelsPerModule, turn, offset, offset, antiAliased, UnevenLight.Shadow, 0f, 0f);
        }
        var (mappedLuminance, mappedWidth, mappedHeight) = Render(Mapped);
        var (luminance, width, height) = Render(Unmapped);

        var (mappedStatus, _) = DecodeImage(Symbology.StandardQR, mappedLuminance, mappedWidth, mappedHeight);
        var (status, _) = DecodeImage(Symbology.StandardQR, luminance, width, height);

        await Assert.That(mappedStatus).IsEqualTo(DecodeStatus.Success);
        await Assert.That(status).IsEqualTo(DecodeStatus.UnmappedCharacter);
    }

    /// <summary>A real verdict at level M's correction limit still reaches the caller when the regional pass, tried after it, reads nothing.</summary>
    [Test]
    public async Task MicroQR_UnmappedCellAtTheCorrectionLimit_ReachesTheCallerAsUnmappedCharacter()
    {
        var (modules, columns, rows) = BuildMicroQR(Unmapped, MicroQREccLevel.M);
        foreach (var column in new[] { 16, 14, 12, 10, 8 })
            modules[16 * columns + column] ^= 1;
        MicroQRCodeDecoder.TryDecode(modules, columns, out _, out var matrixInfo);
        await Assert.That(matrixInfo.Status).IsEqualTo(DecodeStatus.UnmappedCharacter);
        await Assert.That(matrixInfo.ErrorsCorrected).IsEqualTo(MicroQRConstants.GetErrorCorrectionCapacity(MicroQRVersion.M4, MicroQREccLevel.M));
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => IsDark(modules, columns, rows, row, column, 2), columns + 4, rows + 4, 4, UnevenLight.Shadow, 0f, 0f);

        var (status, _) = DecodeImage(Symbology.MicroQR, luminance, width, height);

        await Assert.That(status).IsEqualTo(DecodeStatus.UnmappedCharacter);
    }

    /// <summary>A keystoned symbol read by the mesh reports the mesh's verdict over the global grid's correction failure.</summary>
    [Test]
    [Arguments(35, 0.11f)]
    public async Task StandardQR_UnmappedCellReadByTheMesh_ReachesTheCallerAsUnmappedCharacter(int version, float keystone)
    {
        var size = 17 + 4 * version;
        (byte[] Luminance, int Width, int Height) Render(int sjis)
        {
            var modules = KanjiUnmappedCharacterEndToEndTest.BuildSymbol(sjis, version);
            return SupersampledRenderer.Render((row, column) => IsDark(modules, size, size, row, column, 4), size + 8, size + 8, 4f, 0f, keystone);
        }
        var (mappedLuminance, mappedWidth, mappedHeight) = Render(Mapped);
        var (luminance, width, height) = Render(Unmapped);

        var (mappedStatus, _) = DecodeImage(Symbology.StandardQR, mappedLuminance, mappedWidth, mappedHeight);
        var (status, _) = DecodeImage(Symbology.StandardQR, luminance, width, height);

        await Assert.That(mappedStatus).IsEqualTo(DecodeStatus.Success);
        await Assert.That(status).IsEqualTo(DecodeStatus.UnmappedCharacter);
    }

    /// <summary>
    /// A verdict found by the strided finder scan does not skip the full sweep: a readable symbol only the sweep finds is still read.
    /// </summary>
    [Test]
    [Arguments(Symbology.MicroQR, false, 1.30f, 0.75f)]
    [Arguments(Symbology.RmQR, false, 1.30f, 0.75f)]
    [Arguments(Symbology.RmQR, true, 1.40f, 0f)]
    public async Task UnmappedSymbolBesideALowDensityReadableOne_ReadsTheReadableOne(Symbology symbology, bool antiAliased, float pixelsPerModule, float offset)
    {
        var (unmapped, columns, rows) = Build(symbology, Unmapped);
        var (mapped, _, _) = Build(symbology, Mapped);
        const int quietZone = 4;
        var left = NearestNeighbourRenderer.Render((row, column) => IsDark(unmapped, columns, rows, row, column, quietZone), columns + 2 * quietZone, rows + 2 * quietZone, 4f, 0f, 0f);
        var right = antiAliased
            ? AntiAliasedRenderer.Render((row, column) => IsDark(mapped, columns, rows, row, column, quietZone), columns + 2 * quietZone, rows + 2 * quietZone, pixelsPerModule, offset, offset)
            : NearestNeighbourRenderer.Render((row, column) => IsDark(mapped, columns, rows, row, column, quietZone), columns + 2 * quietZone, rows + 2 * quietZone, pixelsPerModule, offset, offset);
        // Side by side, top-aligned, on paper
        var width = left.Width + right.Width;
        var height = Math.Max(left.Height, right.Height);
        var luminance = new byte[width * height];
        Array.Fill(luminance, (byte)255);
        for (var y = 0; y < left.Height; y++)
            Array.Copy(left.Luminance, y * left.Width, luminance, y * width, left.Width);
        for (var y = 0; y < right.Height; y++)
            Array.Copy(right.Luminance, y * right.Width, luminance, y * width + left.Width, right.Width);

        var (status, text) = DecodeImage(symbology, luminance, width, height);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("亜");
    }

    /// <summary>
    /// When the strided scan settles on a verdict, the sweep decodes that symbol again: beside a second unmapped symbol only the sweep finds, the verdict reported is the first symbol's, which the sweep ranks first.
    /// A sweep that skipped the candidates the strided scan tried would report the second one's.
    /// </summary>
    [Test]
    [Arguments(Symbology.MicroQR)]
    [Arguments(Symbology.RmQR)]
    public async Task TwoUnmappedSymbols_TheSweepReportsTheOneTheStridedScanFound(Symbology symbology)
    {
        var (first, columns, rows) = symbology == Symbology.MicroQR ? BuildMicroQR(Unmapped, MicroQREccLevel.L) : BuildRmQR(Unmapped, RmQREccLevel.M);
        var (second, _, _) = symbology == Symbology.MicroQR ? BuildMicroQR(Unmapped, MicroQREccLevel.M) : BuildRmQR(Unmapped, RmQREccLevel.H);
        const int quietZone = 4;
        var left = NearestNeighbourRenderer.Render((row, column) => IsDark(first, columns, rows, row, column, quietZone), columns + 2 * quietZone, rows + 2 * quietZone, 4f, 0f, 0f);
        var right = NearestNeighbourRenderer.Render((row, column) => IsDark(second, columns, rows, row, column, quietZone), columns + 2 * quietZone, rows + 2 * quietZone, 1.30f, 0.75f, 0.75f);
        var width = left.Width + right.Width;
        var height = Math.Max(left.Height, right.Height);
        var luminance = new byte[width * height];
        Array.Fill(luminance, (byte)255);
        for (var y = 0; y < left.Height; y++)
            Array.Copy(left.Luminance, y * left.Width, luminance, y * width, left.Width);
        for (var y = 0; y < right.Height; y++)
            Array.Copy(right.Luminance, y * right.Width, luminance, y * width + left.Width, right.Width);

        // Each alone gives its own verdict, so the level says which symbol was reported
        await Assert.That(DecodeLevel(symbology, left.Luminance, left.Width, left.Height)).IsEqualTo((DecodeStatus.UnmappedCharacter, 0));
        await Assert.That(DecodeLevel(symbology, right.Luminance, right.Width, right.Height)).IsEqualTo((DecodeStatus.UnmappedCharacter, 1));

        await Assert.That(DecodeLevel(symbology, luminance, width, height)).IsEqualTo((DecodeStatus.UnmappedCharacter, 0));

        // Level 0 is the first symbol's: Micro QR L against M, rMQR M against H
        static (DecodeStatus Status, int Level) DecodeLevel(Symbology symbology, byte[] luminance, int width, int height)
        {
            if (symbology == Symbology.MicroQR)
            {
                MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out _, out var micro);
                return (micro.Status, micro.EccLevel == MicroQREccLevel.L ? 0 : 1);
            }
            RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out _, out var rmqr);
            return (rmqr.Status, rmqr.EccLevel == RmQREccLevel.M ? 0 : 1);
        }
    }

    /// <summary>
    /// A verdict from the global threshold skips the regional pass, so a shadowed readable symbol beside it is not looked for.
    /// </summary>
    [Test]
    [Arguments(Symbology.StandardQR)]
    [Arguments(Symbology.MicroQR)]
    [Arguments(Symbology.RmQR)]
    public async Task UnmappedSymbolBesideAShadowedReadableOne_ReportsTheVerdict(Symbology symbology)
    {
        var (unmapped, columns, rows) = Build(symbology, Unmapped);
        var (mapped, _, _) = Build(symbology, Mapped);
        const int quietZone = 4;
        var tileWidth = columns + 2 * quietZone;
        // The readable symbol on the left, in the shadow; the unmapped one on the right, lit
        (byte[] Luminance, int Width, int Height) Render(bool drawUnmapped) => UnevenLightingRenderer.Render((row, column) =>
        {
            var tile = column / tileWidth;
            return (tile == 0 || drawUnmapped) && IsDark(tile == 0 ? mapped : unmapped, columns, rows, row, column % tileWidth, quietZone);
        }, 2 * tileWidth, rows + 2 * quietZone, 4, UnevenLight.Shadow, 180f, 0.55f);
        var alone = Render(drawUnmapped: false);
        await Assert.That(DecodeImage(symbology, alone.Luminance, alone.Width, alone.Height).Text).IsEqualTo("亜");
        var (luminance, width, height) = Render(drawUnmapped: true);

        var (status, _) = DecodeImage(symbology, luminance, width, height);

        await Assert.That(status).IsEqualTo(DecodeStatus.UnmappedCharacter);
    }

    /// <summary>A mirrored Standard QR symbol is read on its transposed grid; the untransposed grid's format failure does not hide the verdict.</summary>
    [Test]
    public async Task StandardQR_UnmappedCellMirrored_ReachesTheCallerAsUnmappedCharacter()
    {
        const int size = KanjiUnmappedCharacterEndToEndTest.Size;
        var modules = KanjiUnmappedCharacterEndToEndTest.BuildSymbol(Unmapped);
        // Mirrored: rows and columns swapped
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => IsDark(modules, size, size, column, row, 4), size + 8, size + 8, 4, UnevenLight.Shadow, 0f, 0f);

        QRCodeDecoder.TryDecodeImage(luminance, width, height, out _, out var info);

        await Assert.That(info.Status).IsEqualTo(DecodeStatus.UnmappedCharacter);
    }

    /// <summary>
    /// A verdict is reported only when no attempt reads a symbol: beside an unmapped symbol, a light-on-dark readable one is still read by the inverted retry.
    /// </summary>
    [Test]
    [Arguments(Symbology.StandardQR)]
    [Arguments(Symbology.MicroQR)]
    [Arguments(Symbology.RmQR)]
    public async Task UnmappedSymbolBesideAReversedReadableOne_ReadsTheReadableOne(Symbology symbology)
    {
        var (unmapped, columns, rows) = Build(symbology, Unmapped);
        var (mapped, _, _) = Build(symbology, Mapped);
        const int quietZone = 4;
        var tileWidth = columns + 2 * quietZone;
        // Two tiles side by side: the unmapped symbol dark on light, the mappable one light on dark
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) =>
        {
            var tile = column / tileWidth;
            var dark = IsDark(tile == 0 ? unmapped : mapped, columns, rows, row, column % tileWidth, quietZone);
            return tile == 0 ? dark : !dark;
        }, 2 * tileWidth, rows + 2 * quietZone, 4, UnevenLight.Shadow, 0f, 0f);

        var (status, text) = DecodeImage(symbology, luminance, width, height);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(text).IsEqualTo("亜");
    }

    private static (byte[] Modules, int Columns, int Rows) Build(Symbology symbology, int sjis) => symbology switch
    {
        Symbology.StandardQR => (KanjiUnmappedCharacterEndToEndTest.BuildSymbol(sjis), KanjiUnmappedCharacterEndToEndTest.Size, KanjiUnmappedCharacterEndToEndTest.Size),
        Symbology.MicroQR => BuildMicroQR(sjis),
        _ => BuildRmQR(sjis),
    };

    private static (DecodeStatus Status, string Text) DecodeImage(Symbology symbology, byte[] luminance, int width, int height)
    {
        switch (symbology)
        {
            case Symbology.StandardQR:
                {
                    QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
                    return (info.Status, text);
                }
            case Symbology.MicroQR:
                {
                    MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
                    return (info.Status, text);
                }
            default:
                {
                    RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
                    return (info.Status, text);
                }
        }
    }

    /// <summary>The module at (row, column) of a grid drawn with a quiet zone around it.</summary>
    private static bool IsDark(byte[] modules, int columns, int rows, int row, int column, int quietZone)
    {
        var r = row - quietZone;
        var c = column - quietZone;
        return r >= 0 && c >= 0 && r < rows && c < columns && modules[r * columns + c] != 0;
    }

    private static (DecodeStatus Status, string Text) Decode(Symbology symbology, Path path, int sjis)
    {
        var (modules, columns, rows) = symbology == Symbology.MicroQR ? BuildMicroQR(sjis) : BuildRmQR(sjis);
        const int quietZone = 2;
        Func<int, int, bool> isDark = (row, column) =>
        {
            var r = row - quietZone;
            var c = column - quietZone;
            return r >= 0 && c >= 0 && r < rows && c < columns && modules[r * columns + c] != 0;
        };
        var (luminance, width, height) = UnevenLightingRenderer.Render(isDark, columns + 2 * quietZone, rows + 2 * quietZone, 4, UnevenLight.Shadow, 0f, path == Path.Regional ? 0.55f : 0f);
        if (path == Path.Inverted)
        {
            for (var i = 0; i < luminance.Length; i++)
                luminance[i] = (byte)(255 - luminance[i]);
        }

        if (symbology == Symbology.MicroQR)
        {
            MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
            return (info.Status, text);
        }
        else
        {
            RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
            return (info.Status, text);
        }
    }

    /// <summary>ISO/IEC 18004 8.4.5 compaction.</summary>
    private static int Kanji(int sjis)
    {
        var shifted = sjis >= 0xE040 ? sjis - 0xC140 : sjis - 0x8140;
        return ((shifted >> 8) * 0xC0) + (shifted & 0xFF);
    }

    /// <summary>An M4 symbol holding one Kanji cell, level L unless given.</summary>
    private static (byte[] Modules, int Columns, int Rows) BuildMicroQR(int sjis, MicroQREccLevel eccLevel = MicroQREccLevel.L)
    {
        const MicroQRVersion version = MicroQRVersion.M4;
        var dataCount = MicroQRConstants.GetDataCodewordCount(version, eccLevel);
        var eccCount = MicroQRConstants.GetEccCodewordCount(version, eccLevel);
        var data = new byte[dataCount];
        var writer = new BitWriter(data);
        writer.Write(0b011, MicroQRConstants.GetModeIndicatorLength(version)); // Kanji
        writer.Write(1, MicroQRConstants.GetKanjiCountIndicatorLength(version));
        writer.Write(Kanji(sjis), 13);
        writer.Write(0, MicroQRConstants.GetTerminatorLength(version));
        writer.Flush();
        for (var i = writer.GetData().Length; i < dataCount; i++)
            data[i] = (i & 1) == 0 ? (byte)0xEC : (byte)0x11;
        var ecc = new byte[eccCount];
        EccBinaryEncoder.CalculateECC(data, ecc, eccCount);

        var size = MicroQRConstants.SizeFromVersion(version);
        var modules = new byte[size * size];
        MicroQRModulePlacer.PlaceSymbol(modules, size, data, ecc, MicroQRConstants.GetDataBitCapacity(version, eccLevel), version, eccLevel);
        return (modules, size, size);
    }

    /// <summary>An R11x43 symbol holding one Kanji cell, level M unless given.</summary>
    private static (byte[] Modules, int Columns, int Rows) BuildRmQR(int sjis, RmQREccLevel eccLevel = RmQREccLevel.M)
    {
        const RmQRVersion version = RmQRVersion.R11x43;
        var dataCount = RmQRConstants.GetDataCodewordCount(version, eccLevel);
        var data = new byte[dataCount];
        var writer = new BitWriter(data);
        writer.Write(RmQRConstants.KanjiModeIndicatorValue, 3);
        writer.Write(1, RmQRConstants.GetKanjiCountIndicatorLength(version));
        writer.Write(Kanji(sjis), 13);
        writer.Write(0, 3); // terminator
        writer.Flush();
        for (var i = writer.GetData().Length; i < dataCount; i++)
            data[i] = (i & 1) == 0 ? (byte)0xEC : (byte)0x11;
        var message = new byte[RmQRCodewordEncoder.GetFinalMessageSize(version)];
        RmQRCodewordEncoder.AssembleFinalMessage(data, version, eccLevel, message);

        var width = RmQRConstants.GetWidth(version);
        var height = RmQRConstants.GetHeight(version);
        var modules = new byte[width * height];
        RmQRModulePlacer.PlaceSymbol(modules, version, eccLevel, message);
        return (modules, width, height);
    }
}
