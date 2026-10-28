using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The strided and full-sweep candidate lists from one pass over the rows (<see cref="FinderPatternFinder.FindCandidatesStridedAndFullSweep"/>), against the two scans run apart.
/// </summary>
/// <remarks>
/// A candidate is a running average over its hits, so each list has to get the same hits in the same order: every centre, module size and count bit for bit.
/// </remarks>
public class FinderCandidatesOneRowPassParityTest
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BothLists_AreTheTwoScans(bool negative)
    {
        var scenes = 0;
        var candidates = 0L;
        foreach (var (name, luminance, width, height) in Scenes())
        {
            var image = luminance;
            if (negative)
            {
                image = new byte[luminance.Length];
                LuminanceInverter.Invert(luminance, image);
            }
            var threshold = Binarizer.ComputeOtsuThreshold(image, out var grey);
            var mismatch = Compare(image, width, height, threshold, grey, out var found);
            await Assert.That(mismatch).IsNull().Because($"{name}, negative {negative}");
            scenes++;
            candidates += found;
        }

        // The lists compared have to hold candidates: 151 in the positive and 54 in the negative (2026-10-28)
        await Assert.That(scenes).IsGreaterThan(100);
        await Assert.That(candidates).IsGreaterThan(negative ? 40 : 120);
    }

    /// <summary>
    /// A strided row with more hits than the list one row's hits are gathered in is scanned into each list on its own, so no hit past the room is lost.
    /// </summary>
    [Test]
    public async Task RowOfMoreHitsThanItsList_BothListsAreTheTwoScans()
    {
        const int Finders = 70;
        const int Module = 2;
        const int Pitch = 9 * Module;
        const int Margin = 4 * Module;
        var width = 2 * Margin + Finders * Pitch;
        var height = 2 * Margin + 7 * Module;
        var luminance = new byte[width * height];
        Array.Fill(luminance, (byte)220);
        for (var f = 0; f < Finders; f++)
        {
            for (var row = 0; row < 7; row++)
            {
                for (var column = 0; column < 7; column++)
                {
                    var ring = Math.Max(Math.Abs(row - 3), Math.Abs(column - 3));
                    if (ring == 2)
                        continue;
                    for (var dy = 0; dy < Module; dy++)
                        luminance.AsSpan((Margin + row * Module + dy) * width + Margin + f * Pitch + column * Module, Module).Fill(30);
                }
            }
        }
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);

        var mismatch = Compare(luminance, width, height, threshold, grey, out _);
        await Assert.That(mismatch).IsNull();

        // The premise: every finder is found, all on one band of rows, so each strided row through the band holds a hit of every finder, more than the row's list holds
        var strided = new FinderPattern[4096];
        var count = FinderPatternFinder.FindCandidates(luminance, width, height, threshold, strided, grey);
        await Assert.That(count).IsEqualTo(Finders);
        await Assert.That(Finders).IsGreaterThan(FinderPatternFinder.RowHitCapacity);
        var centreY = Margin + 3.5f * Module;
        await Assert.That(strided.AsSpan(0, count).ToArray().All(c => Math.Abs(c.Y - centreY) < 1f)).IsTrue();
    }

    private static string? Compare(byte[] luminance, int width, int height, byte threshold, in GreyLevels grey, out int found)
    {
        var strided = new FinderPattern[4096];
        var swept = new FinderPattern[4096];
        var stridedAlone = new FinderPattern[4096];
        var sweptAlone = new FinderPattern[4096];
        var stridedAloneCount = FinderPatternFinder.FindCandidates(luminance, width, height, threshold, stridedAlone, grey);
        var sweptAloneCount = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, sweptAlone, grey);
        FinderPatternFinder.FindCandidatesStridedAndFullSweep(luminance, width, height, threshold, grey, strided, out var stridedCount, swept, out var sweptCount);
        found = sweptAloneCount;
        return Mismatch("strided", strided, stridedCount, stridedAlone, stridedAloneCount) ?? Mismatch("full sweep", swept, sweptCount, sweptAlone, sweptAloneCount);
    }

    private static string? Mismatch(string list, FinderPattern[] actual, int count, FinderPattern[] expected, int expectedCount)
    {
        if (count != expectedCount)
            return $"{list}: {count} candidates, apart {expectedCount}";
        for (var i = 0; i < count; i++)
        {
            ref readonly var a = ref actual[i];
            ref readonly var e = ref expected[i];
            if (BitConverter.SingleToInt32Bits(a.X) != BitConverter.SingleToInt32Bits(e.X)
                || BitConverter.SingleToInt32Bits(a.Y) != BitConverter.SingleToInt32Bits(e.Y)
                || BitConverter.SingleToInt32Bits(a.ModuleSize) != BitConverter.SingleToInt32Bits(e.ModuleSize)
                || a.Count != e.Count)
            {
                return $"{list} candidate {i}: ({a.X}, {a.Y}, {a.ModuleSize}, {a.Count}), apart ({e.X}, {e.Y}, {e.ModuleSize}, {e.Count})";
            }
        }
        return null;
    }

    /// <summary>Micro QR and rMQR symbols turned, keystoned, blurred and under noise, a sheet of several, and noise alone.</summary>
    private static IEnumerable<(string Name, byte[] Luminance, int Width, int Height)> Scenes()
    {
        var random = new Random(20261028);
        var micro = MicroQRCodeGenerator.Create("ONE ROW PASS", MicroQREccLevel.L);
        var rmqr = RmQRCodeGenerator.Create("ONE ROW PASS 2026", RmQREccLevel.M);
        Func<int, int, bool> microDark = (row, column) => micro[row, column];
        Func<int, int, bool> rmqrDark = (row, column) => rmqr[row, column];
        foreach (var (symbology, isDark, columns, rows) in new[] { ("Micro QR", microDark, micro.Size, micro.Size), ("rMQR", rmqrDark, rmqr.Width, rmqr.Height) })
        {
            for (var i = 0; i < 20; i++)
            {
                var ppm = 1.5f + (float)random.NextDouble() * 5f;
                var degrees = (float)random.NextDouble() * 360f;
                var keystone = (float)random.NextDouble() * 0.1f;
                var (luminance, width, height) = SupersampledRenderer.Render(isDark, columns, rows, ppm, degrees, keystone);
                yield return ($"{symbology} supersampled {ppm:F2} px {degrees:F0} deg keystone {keystone:F2}", luminance, width, height);
            }
            for (var i = 0; i < 20; i++)
            {
                var ppm = 3f + (float)random.NextDouble() * 4f;
                var shot = new CameraRenderer.Shot(ppm, TiltDegrees: (float)random.NextDouble() * 35f, TiltAxisDegrees: (float)random.NextDouble() * 360f, RollDegrees: (float)random.NextDouble() * 360f, BlurSigma: ppm * (float)random.NextDouble() * 0.4f, NoiseSigma: (float)random.NextDouble() * 25f, SceneScale: 1f + (float)random.NextDouble(), Seed: random.Next());
                var photo = CameraRenderer.Render(isDark, columns, rows, quietZone: 0, shot);
                yield return ($"{symbology} camera {ppm:F2} px noise {shot.NoiseSigma:F0}", photo.Luminance, photo.Width, photo.Height);
            }
        }

        // Several symbols side by side, finders on shared rows
        {
            var (luminance, width, height) = NearestNeighbourRenderer.Render((row, column) => column % (micro.Size + 2) < micro.Size && micro[row, column % (micro.Size + 2)], 5 * (micro.Size + 2), micro.Size, 3f, 0.3f, 0.6f);
            yield return ("five Micro QR in a row", luminance, width, height);
        }

        for (var i = 0; i < 30; i++)
        {
            var width = random.Next(40, 600);
            var height = random.Next(40, 400);
            var luminance = new byte[width * height];
            random.NextBytes(luminance);
            if (i % 2 == 0)
            {
                for (var p = 0; p < luminance.Length; p++)
                    luminance[p] = luminance[p] < 128 ? (byte)0 : (byte)255;
            }
            yield return ($"noise {i}", luminance, width, height);
        }
    }
}
