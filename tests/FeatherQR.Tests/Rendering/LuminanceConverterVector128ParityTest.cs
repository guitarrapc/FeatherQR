using System.Runtime.Intrinsics;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The 128-bit luminance tier (<c>ConvertRgbaVector128</c>, the tier of x64 without AVX2, WebAssembly and ARM64 without the dot product)
/// against the scalar tier, entered directly so it runs on every machine with 128-bit vectors, not only where the dispatch picks it.
/// </summary>
/// <remarks>
/// Every channel value against every alpha, straight and premultiplied, is one image: the composite and the premultiplied sum are exact
/// by an argument about 16-bit lanes, and a premultiplied channel above its alpha is where the scalar byte cast wraps. Random scenes take
/// the row modes (optimistic, classified, composite-only), widths across the 16-pixel block and its overlapping tail, and padded rows.
/// </remarks>
public class LuminanceConverterVector128ParityTest
{
    private static readonly (int R, int G, int B, int A, bool Bgra)[] Layouts =
    [
        (2, 1, 0, 3, true),
        (0, 1, 2, 3, false),
        (0, 1, 2, -1, false),
    ];

    [Test]
    public async Task EveryChannelAgainstEveryAlpha_MatchesScalar()
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Skip.Test("Vector128 not accelerated on this machine");
            return;
        }

        const int Width = 256;
        const int Height = 256;
        var mismatches = new List<string>();
        foreach (var (ro, go, bo, ao, bgra) in Layouts)
        {
            foreach (var channels in new[] { 0, 1 })
            {
                // Pixel (c, a): every channel c, or the three channels apart, under alpha a
                var pixels = new byte[Width * Height * 4];
                for (var a = 0; a < Height; a++)
                {
                    for (var c = 0; c < Width; c++)
                    {
                        var p = (a * Width + c) * 4;
                        pixels[p + ro] = (byte)c;
                        pixels[p + go] = (byte)(channels == 0 ? c : 255 - c);
                        pixels[p + bo] = (byte)(channels == 0 ? c : c ^ 0x55);
                        pixels[p + (ao < 0 ? 3 : ao)] = (byte)a;
                    }
                }
                foreach (var premultiplied in ao < 0 ? new[] { false } : [false, true])
                    Compare(pixels, Width, Height, Width * 4, ro, go, bo, ao, bgra, premultiplied, $"sweep {channels}", mismatches);
            }
        }

        await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches.Take(8)));
    }

    [Test]
    public async Task RowModesWidthsAndPadding_MatchScalar()
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Skip.Test("Vector128 not accelerated on this machine");
            return;
        }

        var mismatches = new List<string>();
        var random = new Random(20260930);
        foreach (var (ro, go, bo, ao, bgra) in Layouts)
        {
            foreach (var width in new[] { 16, 17, 31, 32, 33, 47, 48, 63, 64, 100, 139 })
            {
                foreach (var pad in new[] { 0, 12 })
                {
                    // Alpha by row: opaque rows first (the optimistic mode), then 0-or-255 rows (whitening), a partial row (the
                    // composite, which turns the mode sticky), and opaque and 0-or-255 rows after it
                    const int Height = 9;
                    var rowBytes = width * 4 + pad;
                    var pixels = new byte[rowBytes * Height];
                    random.NextBytes(pixels);
                    for (var y = 0; y < Height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var alpha = (y, random.Next(4)) switch
                            {
                                (0 or 1 or 6, _) => 255,
                                (2 or 3 or 7, 0) => 0,
                                (2 or 3 or 7, _) => 255,
                                (4 or 8, 0) => (byte)random.Next(256),
                                _ => (byte)random.Next(2) * 255,
                            };
                            pixels[y * rowBytes + x * 4 + (ao < 0 ? 3 : ao)] = (byte)alpha;
                        }
                    }
                    foreach (var premultiplied in ao < 0 ? new[] { false } : [false, true])
                        Compare(pixels, width, Height, rowBytes, ro, go, bo, ao, bgra, premultiplied, $"width {width}, pad {pad}", mismatches);
                }
            }
        }

        await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches.Take(8)));
    }

    private static void Compare(byte[] pixels, int width, int height, int rowBytes, int ro, int go, int bo, int ao, bool bgra, bool premultiplied, string label, List<string> mismatches)
    {
        var expected = new byte[width * height];
        var actual = new byte[width * height];
        LuminanceConverter.ConvertRgbaForTest(pixels, expected, width, height, rowBytes, ro, go, bo, ao, premultiplied, LuminanceConverter.ConvertTier.Scalar);
        LuminanceConverter.ConvertRgbaVector128(pixels, actual, width, height, rowBytes, bgra, hasAlpha: ao >= 0, premultiplied);
        for (var i = 0; i < expected.Length; i++)
        {
            if (actual[i] != expected[i])
            {
                var p = i / width * rowBytes + i % width * 4;
                mismatches.Add($"{label}, offsets ({ro},{go},{bo},{ao}), premultiplied {premultiplied}, pixel {i} [{pixels[p]}, {pixels[p + 1]}, {pixels[p + 2]}, {pixels[p + 3]}]: {actual[i]}, scalar {expected[i]}");
                return;
            }
        }
    }
}
