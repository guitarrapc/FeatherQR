using System.Buffers;
using System.Runtime.Intrinsics;
using FeatherQR;
using FeatherQR.Internals;
using FeatherQR.Internals.ImageWriters;

/// <summary>
/// The PNG and SVG writers (<c>--shape png/</c>, <c>svg/</c>): the benchmark's symbols and module sizes written into a buffer that is
/// kept, so a row times the writer alone. The PNG's match-start marks (<c>kernel/PngStartMarks-*</c>): each tier entered directly over
/// the scanlines of those symbols as the counting pass marks them, every row inside the core against the row above it, the quiet
/// zone's rows skipped. <c>--writer-digest</c> prints one hash over the files of a fixed set of symbols and options, so two builds are
/// held to the same bytes.
/// </summary>
internal static partial class TierTiming
{
    private sealed record ImageSymbol(string Name, Func<IBufferWriter<byte>, SymbolImageOptions, int> Png, Func<IBufferWriter<byte>, SymbolImageOptions, int> Svg, int Width, int Height, int CoreWidth, int CoreHeight, Func<int, int, bool> CoreModule);

    private static ImageSymbol Symbol(string name, QRCodeData data)
    {
        var view = new StandardQrMatrixView(data);
        return new(name, (w, o) => SymbolImageWriter.WritePng(data, w, o), (w, o) => SymbolImageWriter.WriteSvg(data, w, o), view.Width, view.Height, view.CoreWidth, view.CoreHeight, view.GetCoreModule);
    }

    private static ImageSymbol Symbol(string name, MicroQRCodeData data)
    {
        var view = new MicroQRMatrixView(data);
        return new(name, (w, o) => SymbolImageWriter.WritePng(data, w, o), (w, o) => SymbolImageWriter.WriteSvg(data, w, o), view.Width, view.Height, view.CoreWidth, view.CoreHeight, view.GetCoreModule);
    }

    private static ImageSymbol Symbol(string name, RmQRCodeData data)
    {
        var view = new RmQRMatrixView(data);
        return new(name, (w, o) => SymbolImageWriter.WritePng(data, w, o), (w, o) => SymbolImageWriter.WriteSvg(data, w, o), view.Width, view.Height, view.CoreWidth, view.CoreHeight, view.GetCoreModule);
    }

    // The benchmark's symbols (SymbolImageWrite).
    private static ImageSymbol[] ImageSymbols() =>
    [
        Symbol("qr-v1-alnum-M", QRCodeGenerator.Create("HELLO WORLD 2026", QREccLevel.M)),
        Symbol("qr-v40-byte-L", Large()),
        Symbol("micro-m4-alnum-L", MicroQRCodeGenerator.Create("HELLO2026", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M4 })),
        Symbol("rmqr-r17x139-byte-M", RmQRCodeGenerator.Create(DeterministicText(100), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 })),
    ];

    private static Shape[] ImageWriterShapes() =>
    [
        .. ImageShapes("qr-v1-alnum-M", [8, 10]),
        .. ImageShapes("qr-v40-byte-L", [8, 3, 10]),
        .. ImageShapes("micro-m4-alnum-L", [8]),
        .. ImageShapes("rmqr-r17x139-byte-M", [8]),
    ];

    private static Shape[] ImageShapes(string name, int[] sizes) =>
    [
        new($"svg/{name}", () => TimedWriter(name, 8, svg: true)),
        .. sizes.SelectMany(pixels => new Shape[]
        {
            new($"png/{name}{Size(pixels)}", () => TimedWriter(name, pixels, svg: false)),
            new($"kernel/PngStartMarks-{name}{Size(pixels)}-scalar", () => TimedMarks(name, pixels, PngEncoder.MarkStartsScalar)),
            new($"kernel/PngStartMarks-{name}{Size(pixels)}-v128", () => TimedMarks(name, pixels, PngEncoder.MarkStartsVector128), Vector128.IsHardwareAccelerated),
            new($"kernel/PngStartMarks-{name}{Size(pixels)}-v256", () => TimedMarks(name, pixels, PngEncoder.MarkStartsVector256), Vector256.IsHardwareAccelerated),
        }),
    ];

    private static string Size(int pixels) => pixels == 8 ? "" : $"-{pixels}px";

    private static Func<int> TimedWriter(string name, int pixels, bool svg)
    {
        var symbol = Array.Find(ImageSymbols(), s => s.Name == name)!;
        var options = new SymbolImageOptions { ModulePixelSize = pixels };
        var buffer = new ArrayBufferWriter<byte>(1 << 16);
        var write = svg ? symbol.Svg : symbol.Png;
        return () =>
        {
            buffer.ResetWrittenCount();
            return write(buffer, options);
        };
    }

    private delegate void StartMarker(ReadOnlySpan<byte> slot, ReadOnlySpan<byte> aboveSlot, int stride, bool hasAbove, Span<ulong> before, Span<ulong> equal, Span<ulong> starts);

    /// <summary>The marks of every core row's first scanline, each against the row above it, as the counting pass asks for them.</summary>
    private static Func<int> TimedMarks(string name, int pixels, StartMarker mark)
    {
        var symbol = Array.Find(ImageSymbols(), s => s.Name == name)!;
        var stride = (symbol.Width * pixels + 7) / 8 + 1;
        var slotLength = PngEncoder.SlotLead + stride + PngEncoder.Slack;
        var quietZone = (symbol.Width - symbol.CoreWidth) / 2;
        var slots = new byte[symbol.Height * slotLength];
        for (var m = 0; m < symbol.Height; m++)
            PackScanline(symbol, m, pixels, slots.AsSpan(m * slotLength, slotLength));
        var words = (stride + 63) >> 6;
        var before = new ulong[words + 1];
        var equal = new ulong[words + 1];
        var starts = new ulong[words + 1];
        return () =>
        {
            var sink = 0;
            for (var m = quietZone; m < quietZone + symbol.CoreHeight; m++)
            {
                mark(slots.AsSpan(m * slotLength, slotLength), m > 0 ? slots.AsSpan((m - 1) * slotLength, slotLength) : default, stride, m > 0, before, equal, starts);
                sink += (int)starts[0];
            }
            return sink;
        };
    }

    /// <summary>A scanline as the encoder packs it, from the slot's lead: filter byte 0, a bit a pixel, light 1 and dark 0, MSB first, padded with light.</summary>
    private static void PackScanline(ImageSymbol symbol, int m, int pixels, Span<byte> slot)
    {
        slot.Fill(0xFF);
        var line = slot.Slice(PngEncoder.SlotLead);
        line[0] = 0;
        var quietZone = (symbol.Width - symbol.CoreWidth) / 2;
        var coreRow = m - quietZone;
        if ((uint)coreRow >= (uint)symbol.CoreHeight)
            return;
        for (var column = 0; column < symbol.CoreWidth; column++)
        {
            if (!symbol.CoreModule(coreRow, column))
                continue;
            var first = (quietZone + column) * pixels;
            for (var x = first; x < first + pixels; x++)
                line[1 + (x >> 3)] &= (byte)~(0x80 >> (x & 7));
        }
    }

    /// <summary>One FNV-1a hash over the PNG and SVG files of a fixed set of symbols, sizes and colours.</summary>
    private static void WriterDigest()
    {
        ImageSymbol[] symbols =
        [
            .. ImageSymbols(),
            Symbol("qr-v10-alnum-M", QRCodeGenerator.Create(Pick(300, AlphanumericAlphabet), QREccLevel.M)),
            Symbol("micro-m2-num-L", MicroQRCodeGenerator.Create("0123456789", MicroQREccLevel.L)),
            Symbol("rmqr-r7x43-num-M", RmQRCodeGenerator.Create("012345678901", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 })),
        ];
        SymbolImageOptions[] options =
        [
            default,
            new() { ModulePixelSize = 1 },
            new() { ModulePixelSize = 3 },
            new() { ModulePixelSize = 10 },
            new() { ModulePixelSize = 13 },
            new() { ModulePixelSize = 33 },
            new() { DarkColor = new SymbolColor(10, 20, 30), LightColor = new SymbolColor(250, 240, 230) },
            new() { LightColor = new SymbolColor(255, 255, 255, 128) },
            new() { DarkColor = new SymbolColor(0, 0, 0, 0), LightColor = new SymbolColor(255, 255, 255, 128) },
        ];
        var buffer = new ArrayBufferWriter<byte>(1 << 16);
        var hash = 0xCBF29CE484222325UL;
        var files = 0;
        foreach (var symbol in symbols)
        {
            foreach (var option in options)
            {
                foreach (var write in new[] { symbol.Png, symbol.Svg })
                {
                    buffer.ResetWrittenCount();
                    write(buffer, option);
                    foreach (var b in buffer.WrittenSpan)
                        hash = (hash ^ b) * 0x100000001B3UL;
                    files++;
                }
            }
        }
        Console.WriteLine($"# writer digest {hash:x16} over {files} files");
    }
}
