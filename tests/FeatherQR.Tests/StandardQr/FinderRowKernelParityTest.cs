using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// Parity test for the row kernels of the finder search.
/// The scalar walk judges the 1:1:3:1:1 window at the end of every dark run from the third on; the mask walk does the same from a dark bitmask; the edge-list kernel takes all edges of the row at once and classifies sixteen windows a step, handing only the flagged ones, in order, to the same follow-ups.
/// All three have to leave the same candidate list behind: every centre, module size and count bit for bit, in the same order, because a candidate is a running average over its hits and the order of the hits is part of it.
/// The scalar kernel also runs the reference cross-check walks, so this holds the whole search to its reference, row kernel and walks together.
/// </summary>
public class FinderRowKernelParityTest
{
    [Test]
    public async Task EdgeListKernel_LeavesTheScalarKernelsCandidates()
    {
        if (!FinderPatternFinder.IsEdgeListKernelSupported)
        {
            Skip.Test("The edge-list kernel needs 256-bit vectors or AdvSimd (net8.0+).");
            return;
        }

        var (scenes, candidates) = await CompareOnEveryScene(FinderRowKernel.EdgeList);
        await Assert.That(scenes).IsGreaterThan(150);
        await Assert.That(candidates).IsGreaterThan(1_000);
    }

    [Test]
    public async Task MaskWalkKernel_LeavesTheScalarKernelsCandidates()
    {
        // The kernel every target without 256-bit vectors runs, and rows of 4,096 pixels and more everywhere. Once the edge-list kernel is what TryFind picks on x64, nothing else reaches it there.
        var (scenes, candidates) = await CompareOnEveryScene(FinderRowKernel.MaskWalk);
        await Assert.That(scenes).IsGreaterThan(150);
        await Assert.That(candidates).IsGreaterThan(1_000);
    }

    [Test]
    public async Task TryFind_PicksTheSamePatterns_UnderEveryKernel()
    {
        var reference = new FinderPattern[3];
        var actual = new FinderPattern[3];
        var found = 0;
        foreach (var version in new[] { 2, 10, 25 })
        {
            var qr = QRCodeGenerator.Create($"row kernel parity {version}".AsSpan(), QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version) });
            foreach (var pitch in new[] { 2f, 3f, 3.4f, 5f })
            {
                foreach (var blurred in new[] { false, true })
                {
                    var scene = Render(qr, pitch, padX: 23, padY: 7, blurred, out var width, out var height);
                    var threshold = Binarizer.ComputeOtsuThreshold(scene, out var grey);
                    var referenceFound = FinderPatternFinder.TryFindWith(scene, width, height, threshold, reference, grey, FinderRowKernel.Scalar);
                    if (referenceFound)
                        found++;
                    foreach (var kernel in new[] { FinderRowKernel.Auto, FinderRowKernel.MaskWalk, FinderRowKernel.EdgeList })
                    {
                        var label = $"version={version}, pitch={pitch}, blurred={blurred}, kernel={kernel}";
                        await Assert.That(FinderPatternFinder.TryFindWith(scene, width, height, threshold, actual, grey, kernel)).IsEqualTo(referenceFound).Because(label);
                        for (var i = 0; referenceFound && i < 3; i++)
                            await Assert.That(Same(actual[i], reference[i])).IsTrue().Because($"{label}, pattern {i}");
                    }
                }
            }
        }
        await Assert.That(found).IsGreaterThan(15);
    }

    private static async Task<(int Scenes, long Candidates)> CompareOnEveryScene(FinderRowKernel kernel)
    {
        var reference = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var actual = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var scenes = 0;
        var total = 0L;
        foreach (var (name, scene, width, height, threshold, grey, stride) in Scenes())
        {
            Array.Clear(reference);
            Array.Clear(actual);
            var expected = FinderPatternFinder.FindCandidatesWith(scene, width, height, threshold, reference, grey, stride, FinderRowKernel.Scalar);
            var count = FinderPatternFinder.FindCandidatesWith(scene, width, height, threshold, actual, grey, stride, kernel);
            scenes++;
            total += expected;

            string? mismatch = count != expected ? $"{count} candidates, reference {expected}" : null;
            for (var i = 0; mismatch is null && i < expected; i++)
            {
                if (!Same(actual[i], reference[i]))
                    mismatch = $"candidate {i}: ({actual[i].X}, {actual[i].Y}, {actual[i].ModuleSize}, {actual[i].Count}), reference ({reference[i].X}, {reference[i].Y}, {reference[i].ModuleSize}, {reference[i].Count})";
            }
            await Assert.That(mismatch).IsNull().Because($"{name}, kernel={kernel}");
        }
        return (scenes, total);
    }

    private static bool Same(in FinderPattern a, in FinderPattern b)
        => BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
            && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
            && BitConverter.SingleToInt32Bits(a.ModuleSize) == BitConverter.SingleToInt32Bits(b.ModuleSize)
            && a.Count == b.Count;

    private static IEnumerable<(string Name, byte[] Scene, int Width, int Height, byte Threshold, GreyLevels Grey, int Stride)> Scenes()
    {
        // Symbols from under two pixels a module (the small crisp route) up, whole and fractional pitches, crisp and blurred, every row and strided
        foreach (var version in new[] { 1, 7, 20 })
        {
            var qr = QRCodeGenerator.Create($"row kernel {version}".AsSpan(), QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version) });
            foreach (var pitch in new[] { 1.3f, 2f, 2.2f, 3f, 3.4f, 4f, 7f })
            {
                foreach (var blurred in new[] { false, true })
                {
                    var scene = Render(qr, pitch, padX: 37, padY: 5, blurred, out var width, out var height);
                    var threshold = Binarizer.ComputeOtsuThreshold(scene, out var grey);
                    foreach (var stride in new[] { 1, 3 })
                        yield return ($"version {version}, pitch {pitch}, blurred {blurred}, stride {stride}", scene, width, height, threshold, grey, stride);
                }
            }
        }

        // Noise at widths on both sides of every block size of the mask build, of the edge words and of the sixteen-window step, thresholds at the extremes, the grey second look on and off.
        // The first rows are the ones a kernel gets wrong at its ends: all dark, all light, starting dark, ending dark.
        foreach (var width in new[] { 16, 17, 31, 32, 33, 47, 63, 64, 65, 96, 127, 128, 129, 191, 192, 193, 255, 256, 257 })
        {
            foreach (var threshold in new byte[] { 0, 1, 128, 255 })
            {
                const int Height = 24;
                var noise = new byte[width * Height];
                new Random(width * 31 + threshold).NextBytes(noise);
                noise.AsSpan(0, width).Fill(0);
                noise.AsSpan(width, width).Fill(255);
                noise[2 * width] = 0;
                noise[3 * width - 1] = 0;
                // runs of two to five pixels in the lower rows, so ratios hold more often than in pixel noise
                var random = new Random(width);
                for (var y = 12; y < Height; y++)
                {
                    for (var x = 0; x < width;)
                    {
                        var run = random.Next(2, 6);
                        var level = random.Next(2) == 0 ? (byte)0 : (byte)255;
                        for (var k = 0; k < run && x < width; k++, x++)
                            noise[y * width + x] = level;
                    }
                }

                var histogram = new int[256];
                foreach (var value in noise)
                    histogram[value]++;
                yield return ($"noise {width} px, threshold {threshold}, grey on", noise, width, Height, threshold, GreyLevels.FromHistogram(histogram, threshold), 1);
                yield return ($"noise {width} px, threshold {threshold}, grey off", noise, width, Height, threshold, default, 1);
            }
        }

        // Fields of finders, every one of them accepted: a hit a kernel drops or adds changes a candidate's count and its averaged centre.
        // The phase moves every finder across the 64-pixel words of the mask and the sixteen-window steps of the classification.
        foreach (var module in new[] { 2, 3, 4, 6 })
        {
            for (var phase = 0; phase < 8; phase++)
            {
                var cell = 9 * module + 3;
                var width = 6 * cell + phase * 9 + 40;
                var height = 4 * cell + 8;
                var field = new byte[width * height];
                field.AsSpan().Fill(255);
                for (var gy = 0; gy < 4; gy++)
                {
                    for (var gx = 0; gx < 6; gx++)
                        WriteFinder(field, width, phase * 9 + 5 + gx * cell + (gy & 1) * module, 4 + gy * cell, module);
                }
                foreach (var stride in new[] { 1, 3 })
                    yield return ($"finder field, module {module}, phase {phase}, stride {stride}", field, width, height, 128, default, stride);
            }
        }

        // Rows at the limit of the sixteen-bit positions: the last width the edge-list kernel takes, and the first ones it hands to the mask walk
        foreach (var width in new[] { 4095, 4096, 4200 })
        {
            const int Height = 27;
            var wide = new byte[width * Height];
            var random = new Random(width);
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < width;)
                {
                    var run = random.Next(3) == 0 ? 9 : 3;
                    var level = random.Next(2) == 0 ? (byte)0 : (byte)255;
                    for (var k = 0; k < run && x < width; k++, x++)
                        wide[y * width + x] = level;
                }
            }
            // finders the cross-checks accept: one ending on the last pixel of the row, one across a 64-pixel word, one at the start
            WriteFinder(wide, width, width - 21, 3, 3);
            WriteFinder(wide, width, 54, 3, 3);
            WriteFinder(wide, width, 0, 3, 3);
            yield return ($"wide {width} px", wide, width, Height, 128, default, 1);
        }
    }

    /// <summary>A 7 x 7 finder at <paramref name="module"/> pixels a module with its light separator, top-left at (<paramref name="x0"/>, <paramref name="y0"/>).</summary>
    private static void WriteFinder(byte[] image, int width, int x0, int y0, int module)
    {
        for (var my = -1; my <= 7; my++)
        {
            for (var mx = -1; mx <= 7; mx++)
            {
                var ring = mx is >= 0 and <= 6 && my is >= 0 and <= 6 ? Math.Max(Math.Abs(mx - 3), Math.Abs(my - 3)) : 2;
                var level = ring == 2 ? (byte)255 : (byte)0;
                for (var y = y0 + my * module; y < y0 + (my + 1) * module; y++)
                {
                    for (var x = x0 + mx * module; x < x0 + (mx + 1) * module; x++)
                    {
                        if (x >= 0 && x < width && y >= 0)
                            image[y * width + x] = level;
                    }
                }
            }
        }
    }

    /// <summary>The symbol at a fractional pitch the way a crisp renderer lays it out (module k spans round(k·pitch) .. round((k+1)·pitch)), off-centre on a canvas that is not square.</summary>
    private static byte[] Render(QRCodeData qr, float pitch, int padX, int padY, bool blurred, out int width, out int height)
    {
        var side = (int)MathF.Round(qr.Size * pitch);
        width = side + padX;
        height = side + padY;
        var crisp = new byte[width * height];
        crisp.AsSpan().Fill(255);
        for (var row = 0; row < qr.Size; row++)
        {
            var y0 = (int)MathF.Round(row * pitch);
            var y1 = (int)MathF.Round((row + 1) * pitch);
            for (var col = 0; col < qr.Size; col++)
            {
                if (!qr[row, col])
                    continue;
                var x0 = (int)MathF.Round(col * pitch);
                var x1 = (int)MathF.Round((col + 1) * pitch);
                for (var y = y0; y < y1; y++)
                    crisp.AsSpan((padY / 2 + y) * width + padX / 2 + x0, x1 - x0).Fill(0);
            }
        }
        if (!blurred)
            return crisp;

        var soft = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                        sum += crisp[Math.Clamp(y + dy, 0, height - 1) * width + Math.Clamp(x + dx, 0, width - 1)];
                soft[y * width + x] = (byte)(sum / 9);
            }
        }
        return soft;
    }
}
