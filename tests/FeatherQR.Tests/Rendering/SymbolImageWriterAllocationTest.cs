using System.Buffers;

namespace FeatherQR.Tests;

/// <summary>
/// The image writer allocates nothing beyond its output: <c>WriteSvg</c> nothing into a writer that already has room, and
/// <c>GetSvgString</c> the string alone. Release only, as <see cref="DecodeAllocationTest"/> is, since a Debug build
/// allocates where an optimized one does not.
/// </summary>
[NotInParallel]
public class SymbolImageWriterAllocationTest
{
#if !DEBUG
    private static long SteadyStateBytes(Action call)
    {
        for (var i = 0; i < 3; i++)
            call();
        var fewest = long.MaxValue;
        for (var round = 0; round < 3; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 4; i++)
                call();
            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return fewest;
    }

    [Test]
    public async Task WriteSvg_IntoAWriterWithRoom_AllocatesNothing()
    {
        var qr = QRCodeGenerator.Create("allocation", QREccLevel.M, new QRCodeGeneratorOptions { Version = 40 });
        var micro = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L);
        var rm = RmQRCodeGenerator.Create("allocation", RmQREccLevel.M);
        var options = new SymbolImageOptions { DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A, 200), LightColor = new SymbolColor(255, 255, 255, 51) };
        var writer = new RewindingWriter(1 << 20);

        await Assert.That(SteadyStateBytes(() => { writer.Rewind(); SymbolImageWriter.WriteSvg(qr, writer, options); })).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => { writer.Rewind(); SymbolImageWriter.WriteSvg(micro, writer); })).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => { writer.Rewind(); SymbolImageWriter.WriteSvg(rm, writer); })).IsEqualTo(0);

        var fitted = new SymbolImageOptions { Width = 600, Height = 400 };
        await Assert.That(SteadyStateBytes(() => { writer.Rewind(); SymbolImageWriter.WriteSvg(qr, writer, fitted); })).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => { writer.Rewind(); SymbolImageWriter.WriteSvg(rm, writer, fitted); })).IsEqualTo(0);

        var shaped = new SymbolImageOptions { ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 90, FinderPatternStyle = FinderPatternStyle.RoundedSquareCircle };
        await Assert.That(SteadyStateBytes(() => { writer.Rewind(); SymbolImageWriter.WriteSvg(qr, writer, shaped); })).IsEqualTo(0);

        // The icon's image goes in as base64 written straight into the document.
        var icon = new SymbolImageOptions { IconSizeModules = 9, IconImage = new byte[3000] };
        await Assert.That(SteadyStateBytes(() => { writer.Rewind(); SymbolImageWriter.WriteSvg(qr, writer, icon); })).IsEqualTo(0);
    }

    [Test]
    public async Task GetSvgString_AllocatesTheStringAlone()
    {
        var qr = QRCodeGenerator.Create("allocation", QREccLevel.M, new QRCodeGeneratorOptions { Version = 40 });
        var length = SymbolImageWriter.GetSvgString(qr).Length;

        // Four calls a round, so four strings of the same length.
        var oneString = SteadyStateBytes(() => _ = new string('x', length)) / 4;
        await Assert.That(SteadyStateBytes(() => SymbolImageWriter.GetSvgString(qr)) / 4).IsEqualTo(oneString);
    }

    [Test]
    public async Task WritePng_IntoAWriterWithRoom_AllocatesNothing()
    {
        var qr = QRCodeGenerator.Create("allocation", QREccLevel.M, new QRCodeGeneratorOptions { Version = 40 });
        var micro = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L);
        var rm = RmQRCodeGenerator.Create("allocation", RmQREccLevel.M);
        var options = new SymbolImageOptions { ModulePixelSize = 3, DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A, 200), LightColor = new SymbolColor(255, 255, 255, 0) };
        var writer = new RewindingWriter(1 << 20);

        await Assert.That(SteadyStateBytes(() => { writer.Rewind(); SymbolImageWriter.WritePng(qr, writer); })).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => { writer.Rewind(); SymbolImageWriter.WritePng(qr, writer, options); })).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => { writer.Rewind(); SymbolImageWriter.WritePng(micro, writer); })).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => { writer.Rewind(); SymbolImageWriter.WritePng(rm, writer, options); })).IsEqualTo(0);

        var fitted = new SymbolImageOptions { Width = 600, Height = 400 };
        await Assert.That(SteadyStateBytes(() => { writer.Rewind(); SymbolImageWriter.WritePng(qr, writer, fitted); })).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => { writer.Rewind(); SymbolImageWriter.WritePng(rm, writer, fitted); })).IsEqualTo(0);

        var shaped = new SymbolImageOptions { ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 90, FinderPatternStyle = FinderPatternStyle.RoundedSquareCircle };
        await Assert.That(SteadyStateBytes(() => { writer.Rewind(); SymbolImageWriter.WritePng(micro, writer, shaped); })).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => { writer.Rewind(); SymbolImageWriter.WritePng(rm, writer, shaped with { Width = 600, Height = 400 }); })).IsEqualTo(0);
    }

    [Test]
    public async Task TryWritePixels_Styled_AllocatesNothing()
    {
        var qr = QRCodeGenerator.Create("allocation", QREccLevel.H, new QRCodeGeneratorOptions { Version = 10 });
        var shaped = new SymbolImageOptions { ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 92, FinderPatternStyle = FinderPatternStyle.Circle, IconSizeModules = 9 };
        var pixels = new byte[SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Rgba8888, shaped)];

        await Assert.That(SteadyStateBytes(() => SymbolImageWriter.TryWritePixels(qr, pixels, PixelLayout.Rgba8888, out _, out _, shaped))).IsEqualTo(0);
        await Assert.That(SteadyStateBytes(() => SymbolImageWriter.TryWritePixels(qr, pixels, PixelLayout.Gray8, out _, out _, shaped with { Width = 500, Height = 400 }))).IsEqualTo(0);
    }

    [Test]
    public async Task GetPngBytes_AllocatesTheArrayAlone()
    {
        var qr = QRCodeGenerator.Create("allocation", QREccLevel.M, new QRCodeGeneratorOptions { Version = 40 });
        var length = SymbolImageWriter.GetPngBytes(qr).Length;

        // Four calls a round, so four arrays of the same length.
        var oneArray = SteadyStateBytes(() => _ = new byte[length]) / 4;
        await Assert.That(SteadyStateBytes(() => SymbolImageWriter.GetPngBytes(qr)) / 4).IsEqualTo(oneArray);
    }

    [Test]
    public async Task TryWritePixels_AndItsSize_AllocateNothing()
    {
        var qr = QRCodeGenerator.Create("allocation", QREccLevel.M, new QRCodeGeneratorOptions { Version = 40 });
        var micro = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L);
        var rm = RmQRCodeGenerator.Create("allocation", RmQREccLevel.M);
        var options = new SymbolImageOptions { ModulePixelSize = 3, DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A, 200) };
        var buffer = new byte[1 << 22];

        foreach (var layout in new[] { PixelLayout.Gray8, PixelLayout.Rgba8888, PixelLayout.Bgra8888, PixelLayout.Rgb888x })
        {
            await Assert.That(SteadyStateBytes(() => SymbolImageWriter.TryWritePixels(qr, buffer, layout, out _, out _, options))).IsEqualTo(0);
            await Assert.That(SteadyStateBytes(() => SymbolImageWriter.TryWritePixels(micro, buffer, layout, out _, out _))).IsEqualTo(0);
            await Assert.That(SteadyStateBytes(() => SymbolImageWriter.TryWritePixels(rm, buffer, layout, out _, out _, options))).IsEqualTo(0);
            await Assert.That(SteadyStateBytes(() => SymbolImageWriter.GetPixelBufferSize(qr, layout, options))).IsEqualTo(0);
        }
    }

    [Test]
    public async Task GetTerminalText_AllocatesTheStringAlone()
    {
        var qr = QRCodeGenerator.Create("allocation", QREccLevel.M, new QRCodeGeneratorOptions { Version = 10 });
        var length = SymbolImageWriter.GetTerminalText(qr).Length;

        // Four calls a round, so four strings of the same length.
        var oneString = SteadyStateBytes(() => _ = new string('x', length)) / 4;
        await Assert.That(SteadyStateBytes(() => SymbolImageWriter.GetTerminalText(qr)) / 4).IsEqualTo(oneString);
        await Assert.That(SteadyStateBytes(() => SymbolImageWriter.GetTerminalText(qr, invert: true)) / 4).IsEqualTo(oneString);
    }

    /// <summary>A writer over one array that starts again at its front on <see cref="Rewind"/>.</summary>
    private sealed class RewindingWriter(int capacity) : IBufferWriter<byte>
    {
        private readonly byte[] _buffer = new byte[capacity];
        private int _written;

        public void Rewind() => _written = 0;
        public void Advance(int count) => _written += count;
        public Memory<byte> GetMemory(int sizeHint = 0) => _buffer.AsMemory(_written);
        public Span<byte> GetSpan(int sizeHint = 0) => _buffer.AsSpan(_written);
    }
#endif
}
