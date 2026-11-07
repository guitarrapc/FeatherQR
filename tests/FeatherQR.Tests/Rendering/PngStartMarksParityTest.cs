using System.Runtime.Intrinsics;
using FeatherQR.Internals.ImageWriters;

namespace FeatherQR.Tests;

/// <summary>
/// The PNG's match-start marks in each vector tier against the scalar form, which the netstandard builds run: the same start words over
/// random scanlines, scanlines of runs of one byte, copies of the scanline above with a few bytes changed, and the packed scanlines of
/// real symbols at 1 to 33 pixels a module, with and without a scanline above. The slot's lead byte and slack hold different noise for
/// each form, so a mark that came from them shows as a difference. The start words are compared and not the equal-byte masks, since
/// those may differ past the scanline, where the marks are forced or cleared.
/// </summary>
public class PngStartMarksParityTest
{
    private delegate void Marker(ReadOnlySpan<byte> slot, ReadOnlySpan<byte> aboveSlot, int stride, bool hasAbove, Span<ulong> before, Span<ulong> equal, Span<ulong> starts);

    private static readonly int[] Strides = [2, 3, 4, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 100, 127, 128, 129, 186, 255, 256, 257, 1000, 4097, 32768];

    [Test]
    public async Task Vector128_MatchesTheScalarForm()
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Skip.Test("no 128-bit vectors on this build");
            return;
        }
        var mismatches = Mismatches("Vector128", PngEncoder.MarkStartsVector128);
        await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches.Take(10)));
    }

    [Test]
    public async Task Vector256_MatchesTheScalarForm()
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            Skip.Test("no 256-bit vectors on this build");
            return;
        }
        var mismatches = Mismatches("Vector256", PngEncoder.MarkStartsVector256);
        await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches.Take(10)));
    }

    private static List<string> Mismatches(string name, Marker tier)
    {
        var mismatches = new List<string>();
        var random = new Random(20261107);
        foreach (var stride in Strides)
        {
            foreach (var hasAbove in new[] { false, true })
            {
                for (var trial = 0; trial < 8; trial++)
                {
                    var above = new byte[stride];
                    random.NextBytes(above);
                    var row = new byte[stride];
                    var kind = trial % 4;
                    switch (kind)
                    {
                        case 0:
                            random.NextBytes(row);
                            break;
                        case 1:
                            // Runs of one byte, as a 1-bit image's scanlines mostly are.
                            for (var i = 0; i < stride;)
                            {
                                var value = random.Next(2) == 0 ? (byte)0x00 : (byte)0xFF;
                                var length = Math.Min(stride - i, 1 + random.Next(40));
                                row.AsSpan(i, length).Fill(value);
                                i += length;
                            }
                            break;
                        case 2:
                            above.CopyTo(row, 0);
                            for (var k = random.Next(6); k > 0; k--)
                                row[random.Next(stride)] ^= 0xFF;
                            break;
                        default:
                            // Runs copied from above, with a few runs moved by a byte.
                            above = Runs(random, stride);
                            above.CopyTo(row, 0);
                            for (var k = random.Next(4); k > 0; k--)
                            {
                                var at = random.Next(stride - 1);
                                (row[at], row[at + 1]) = (row[at + 1], row[at]);
                            }
                            break;
                    }
                    Check(mismatches, name, tier, random, row, above, hasAbove, $"stride {stride} kind {kind}");
                }
            }
        }

        foreach (var (symbol, rows) in Symbols())
        {
            foreach (var pixels in new[] { 1, 2, 3, 4, 5, 7, 8, 9, 10, 13, 16, 24, 32, 33 })
            {
                byte[]? above = null;
                for (var m = 0; m < rows.Length; m++)
                {
                    var row = Pack(rows[m], pixels);
                    Check(mismatches, name, tier, random, row, above ?? row, above is not null, $"{symbol} {pixels}px row {m}");
                    above = row;
                }
            }
        }
        return mismatches;
    }

    private static byte[] Runs(Random random, int stride)
    {
        var row = new byte[stride];
        for (var i = 0; i < stride;)
        {
            var value = random.Next(2) == 0 ? (byte)0x00 : (byte)0xFF;
            var length = Math.Min(stride - i, 1 + random.Next(12));
            row.AsSpan(i, length).Fill(value);
            i += length;
        }
        return row;
    }

    /// <summary>Both forms over the same scanlines, each in slots with their own noise before and after the scanline.</summary>
    private static void Check(List<string> mismatches, string name, Marker tier, Random random, byte[] row, byte[] above, bool hasAbove, string what)
    {
        var stride = row.Length;
        var words = (stride + 63) >> 6;
        var expected = new ulong[words + 1];
        PngEncoder.MarkStartsScalar(Slot(random, row), Slot(random, above), stride, hasAbove, new ulong[words + 1], new ulong[words + 1], expected);
        var actual = new ulong[words + 1];
        tier(Slot(random, row), Slot(random, above), stride, hasAbove, new ulong[words + 1], new ulong[words + 1], actual);
        if (!actual.AsSpan().SequenceEqual(expected))
            mismatches.Add($"{name} {what}{(hasAbove ? " with above" : "")}");
    }

    private static byte[] Slot(Random random, byte[] row)
    {
        var slot = new byte[PngEncoder.SlotLead + row.Length + PngEncoder.Slack];
        random.NextBytes(slot);
        row.CopyTo(slot, PngEncoder.SlotLead);
        return slot;
    }

    /// <summary>A scanline as the encoder packs it: filter byte 0, a bit a pixel, light 1 and dark 0, MSB first, padded with light.</summary>
    private static byte[] Pack(bool[] modules, int pixels)
    {
        var row = new byte[(modules.Length * pixels + 7) / 8 + 1];
        row.AsSpan(1).Fill(0xFF);
        for (var column = 0; column < modules.Length; column++)
        {
            if (!modules[column])
                continue;
            for (var x = column * pixels; x < (column + 1) * pixels; x++)
                row[1 + (x >> 3)] &= (byte)~(0x80 >> (x & 7));
        }
        return row;
    }

    /// <summary>Each symbol's module rows, quiet zone included, dark true.</summary>
    private static IEnumerable<(string Name, bool[][] Rows)> Symbols()
    {
        yield return ("v1", Modules(QRCodeGenerator.Create("HELLO WORLD 2026", QREccLevel.M)));
        yield return ("v10", Modules(QRCodeGenerator.Create(new string('A', 300), QREccLevel.M)));
        yield return ("v40", Modules(QRCodeGenerator.Create(new string('x', 2900), QREccLevel.L)));
        yield return ("M4", Modules(MicroQRCodeGenerator.Create("HELLO2026", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M4 })));
        yield return ("R17x139", Modules(RmQRCodeGenerator.Create(new string('r', 100), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 })));
    }

    private static bool[][] Modules(QRCodeData data) => Modules(SymbolImageWriter.GetPixelBufferSize(data, PixelLayout.Gray8, OneModule), (span, out w, out h) => SymbolImageWriter.TryWritePixels(data, span, PixelLayout.Gray8, out w, out h, OneModule));
    private static bool[][] Modules(MicroQRCodeData data) => Modules(SymbolImageWriter.GetPixelBufferSize(data, PixelLayout.Gray8, OneModule), (span, out w, out h) => SymbolImageWriter.TryWritePixels(data, span, PixelLayout.Gray8, out w, out h, OneModule));
    private static bool[][] Modules(RmQRCodeData data) => Modules(SymbolImageWriter.GetPixelBufferSize(data, PixelLayout.Gray8, OneModule), (span, out w, out h) => SymbolImageWriter.TryWritePixels(data, span, PixelLayout.Gray8, out w, out h, OneModule));

    private static readonly SymbolImageOptions OneModule = new() { ModulePixelSize = 1 };

    private delegate bool Writer(Span<byte> destination, out int width, out int height);

    private static bool[][] Modules(int size, Writer write)
    {
        var pixels = new byte[size];
        if (!write(pixels, out var width, out var height))
            throw new InvalidOperationException("the buffer is the size the writer asked for");
        var rows = new bool[height][];
        for (var y = 0; y < height; y++)
        {
            rows[y] = new bool[width];
            for (var x = 0; x < width; x++)
                rows[y][x] = pixels[y * width + x] == 0;
        }
        return rows;
    }
}
