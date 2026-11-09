using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.Wasm;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR.Tests;

/// <summary>
/// Every tier of the three-byte luminance conversion against the formula written out here, byte for byte: the per-pixel loop,
/// the AVX2 kernel, the 128-bit kernel and the NEON kernel, each entered directly so it runs wherever its instructions exist,
/// not only where the dispatch picks it.
/// </summary>
/// <remarks>
/// The widths go one pixel at a time past two 32-pixel blocks, so every block count and every overlapping tail of each kernel is taken.
/// Rows are packed, padded by bytes that are not a whole pixel, and padded to four bytes. The padding holds bytes that would change
/// the result if a kernel took a colour from them.
/// </remarks>
public class LuminanceConverterRgb24ParityTest
{
    private delegate void Tier(ReadOnlySpan<byte> pixels, Span<byte> luminance, int width, int height, int rowBytes, bool bgr);

    /// <summary>The tiers this machine can run, each with the narrowest row it takes.</summary>
    private static IEnumerable<(string Name, Tier Convert, int MinWidth)> Tiers()
    {
        yield return ("scalar", LuminanceConverter.ConvertRgb24Scalar, 1);
        if (Avx2.IsSupported)
            yield return ("AVX2", LuminanceConverter.ConvertRgb24Avx2, 1);
        if (Ssse3.IsSupported || PackedSimd.IsSupported)
            yield return ("128-bit", LuminanceConverter.ConvertRgb24Vector128, LuminanceConverter.Rgb24BlockPixels);
        if (Dp.IsSupported && AdvSimd.Arm64.IsSupported)
            yield return ("NEON", LuminanceConverter.ConvertRgb24AdvSimd, LuminanceConverter.Rgb24BlockPixels);
    }

    /// <summary>The specification, one pixel at a time with plain indexing.</summary>
    private static byte[] Reference(ReadOnlySpan<byte> pixels, int width, int height, int rowBytes, bool bgr)
    {
        var result = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = y * rowBytes + x * 3;
                int r = pixels[p + (bgr ? 2 : 0)];
                int g = pixels[p + 1];
                int b = pixels[p + (bgr ? 0 : 2)];
                result[y * width + x] = (byte)((77 * r + 150 * g + 29 * b) / 256);
            }
        }
        return result;
    }

    /// <summary>
    /// A machine with vector instructions runs a vector tier. Without this a build that lost its flags would compare the per-pixel
    /// loop with the formula and pass.
    /// </summary>
    [Test]
    public async Task ThisMachine_RunsTheTiersItsInstructionsGive()
    {
        var names = Tiers().Select(t => t.Name).ToArray();

        await Assert.That(names).Contains("scalar");
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)
        {
            // Every x64 CPU the runtime supports has SSSE3 unless a knob turns it off.
            if (Ssse3.IsSupported)
                await Assert.That(names).Contains("128-bit");
            if (Avx2.IsSupported)
                await Assert.That(names).Contains("AVX2");
        }
        if (Dp.IsSupported && AdvSimd.Arm64.IsSupported)
            await Assert.That(names).Contains("NEON");
    }

    [Test]
    public async Task EveryWidthAndPadding_MatchesTheFormula()
    {
        var random = new Random(20261110);
        var mismatches = new List<string>();
        var compared = 0;
        foreach (var (name, convert, minWidth) in Tiers())
        {
            foreach (var bgr in new[] { false, true })
            {
                for (var width = minWidth; width <= 140; width++)
                {
                    foreach (var padding in new[] { 0, 1, 5, (4 - width * 3 % 4) % 4 })
                    {
                        const int height = 3;
                        var rowBytes = width * 3 + padding;
                        // The last row ends at its last pixel.
                        var pixels = new byte[(height - 1) * rowBytes + width * 3];
                        random.NextBytes(pixels);
                        var expected = Reference(pixels, width, height, rowBytes, bgr);
                        // A byte either side of the destination, which no tier may write.
                        var luminance = new byte[width * height + 2];
                        luminance[0] = 0xA5;
                        luminance[^1] = 0x5A;

                        convert(pixels, luminance.AsSpan(1, width * height), width, height, rowBytes, bgr);

                        compared++;
                        if (!luminance.AsSpan(1, width * height).SequenceEqual(expected) || luminance[0] != 0xA5 || luminance[^1] != 0x5A)
                            mismatches.Add($"{name}, {(bgr ? "Bgr24" : "Rgb24")}, {width} wide, {padding} bytes of padding");
                    }
                }
            }
        }

        await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches.Take(8)));
        await Assert.That(compared).IsGreaterThanOrEqualTo(2 * 140 * 4);
    }

    /// <summary>
    /// Each channel through all 256 values against the other two at 0, at 255 and at a value that moves with it. The weighted sum is
    /// exact by an argument about 16-bit partial sums, and the extremes are where a partial sum that saturated would show.
    /// </summary>
    [Test]
    public async Task EveryChannelValue_MatchesTheFormula()
    {
        const int width = 256;
        var others = new Func<int, byte>[] { _ => 0, _ => 255, c => (byte)(255 - c), c => (byte)(c ^ 0x55) };
        var mismatches = new List<string>();
        foreach (var (name, convert, _) in Tiers())
        {
            foreach (var bgr in new[] { false, true })
            {
                // One row for each channel that sweeps and each rule for the other two.
                var height = 3 * others.Length * others.Length;
                var pixels = new byte[width * height * 3];
                var y = 0;
                for (var swept = 0; swept < 3; swept++)
                {
                    foreach (var first in others)
                    {
                        foreach (var second in others)
                        {
                            for (var c = 0; c < width; c++)
                            {
                                var p = (y * width + c) * 3;
                                pixels[p + swept] = (byte)c;
                                pixels[p + (swept + 1) % 3] = first(c);
                                pixels[p + (swept + 2) % 3] = second(c);
                            }
                            y++;
                        }
                    }
                }
                var expected = Reference(pixels, width, height, width * 3, bgr);
                var luminance = new byte[width * height];

                convert(pixels, luminance, width, height, width * 3, bgr);

                var at = luminance.AsSpan().CommonPrefixLength(expected);
                if (at != expected.Length)
                    mismatches.Add($"{name}, {(bgr ? "Bgr24" : "Rgb24")}, pixel {at} [{pixels[at * 3]}, {pixels[at * 3 + 1]}, {pixels[at * 3 + 2]}]: {luminance[at]}, formula {expected[at]}");
            }
        }

        await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches));
    }

    /// <summary>
    /// A source that ends where readable memory ends, and a destination that ends where writable memory ends. A kernel that loads
    /// or stores a whole vector past the last pixel faults here, where over an array it would touch the next object unseen.
    /// </summary>
    [Test]
    public async Task EveryTier_ReadsAndWritesNothingPastTheLastPixel()
    {
        if (!PageEndMemory.IsSupported)
            return;
        using var sourcePage = new PageEndMemory();
        using var destinationPage = new PageEndMemory();
        var random = new Random(7);
        var mismatches = new List<string>();
        var converted = 0;
        foreach (var (name, convert, minWidth) in Tiers())
        {
            foreach (var bgr in new[] { false, true })
            {
                for (var width = minWidth; width <= 140; width++)
                {
                    foreach (var (height, padding) in new[] { (1, 0), (2, 0), (3, 5) })
                    {
                        var rowBytes = width * 3 + padding;
                        var managed = new byte[(height - 1) * rowBytes + width * 3];
                        random.NextBytes(managed);
                        var expected = Reference(managed, width, height, rowBytes, bgr);

                        converted++;
                        if (!ConvertAtPageEnds(convert, sourcePage, destinationPage, managed, width, height, rowBytes, bgr, expected))
                            mismatches.Add($"{name}, {(bgr ? "Bgr24" : "Rgb24")}, {width} by {height}, {padding} bytes of padding");
                    }
                }
            }
        }

        await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches.Take(8)));
        await Assert.That(converted).IsGreaterThanOrEqualTo(2 * 140 * 3);

        static bool ConvertAtPageEnds(Tier convert, PageEndMemory sourcePage, PageEndMemory destinationPage, byte[] managed, int width, int height, int rowBytes, bool bgr, byte[] expected)
        {
            ReadOnlySpan<byte> source = sourcePage.AtPageEnd(managed);
            var destination = destinationPage.AtPageEnd(width * height);
            convert(source, destination, width, height, rowBytes, bgr);
            return destination.SequenceEqual(expected);
        }
    }

    /// <summary>
    /// The public call takes whichever tier the dispatch picks, at every width from one pixel, and gives the formula's bytes.
    /// On a machine without AVX2 a row under 16 pixels is the per-pixel loop and a wider one a kernel, and both sides of that are here.
    /// </summary>
    [Test]
    public async Task Convert_GivesTheFormulasBytes_AtEveryWidth()
    {
        var random = new Random(11);
        var mismatches = new List<string>();
        foreach (var layout in new[] { PixelLayout.Rgb24, PixelLayout.Bgr24 })
        {
            for (var width = 1; width <= 140; width++)
            {
                const int height = 2;
                var rowBytes = width * 3 + 2;
                var pixels = new byte[rowBytes * height];
                random.NextBytes(pixels);
                var luminance = new byte[width * height];

                LuminanceConverter.Convert(pixels, width, height, rowBytes, layout, premultipliedAlpha: false, luminance);

                if (!luminance.AsSpan().SequenceEqual(Reference(pixels, width, height, rowBytes, layout == PixelLayout.Bgr24)))
                    mismatches.Add($"{layout}, {width} wide");
            }
        }

        await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches.Take(8)));
    }
}
