using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The strided and full-sweep candidate lists of one pass over the rows (<see cref="FinderPatternFinder.FindCandidatesStridedAndFullSweep"/>), and of a strided scan that keeps its rows' hits and a sweep that merges them in those rows' places (<see cref="FinderPatternFinder.FindCandidatesKeepingRowHits"/>, <see cref="FinderPatternFinder.FindCandidatesFullSweepReplaying"/>), against the two scans run apart.
/// </summary>
/// <remarks>
/// A candidate is a running average over its hits, so each list has to get the same hits in the same order: every centre, module size and count bit for bit.
/// </remarks>
public class FinderCandidatesOneRowPassParityTest
{
    /// <summary>The room the every-symbol scan keeps hits in, as many as a candidate list holds.</summary>
    private const int KeptRoom = 4096;

    /// <summary>A room two entries past one row's gathering list: it keeps the first hits, then has too few entries left for a row.</summary>
    private const int SmallRoom = FinderPatternFinder.RowHitCapacity + 2;

    [Test]
    [Arguments(false, KeptRoom)]
    [Arguments(true, KeptRoom)]
    [Arguments(false, SmallRoom)]
    [Arguments(true, SmallRoom)]
    public async Task BothLists_AreTheTwoScans(bool negative, int keptRoom)
    {
        var scenes = 0;
        var candidates = 0L;
        var markedAfterKept = 0;
        foreach (var (name, luminance, width, height) in Scenes())
        {
            var image = luminance;
            if (negative)
            {
                image = new byte[luminance.Length];
                LuminanceInverter.Invert(luminance, image);
            }
            var threshold = Binarizer.ComputeOtsuThreshold(image, out var grey);
            var mismatch = Compare(image, width, height, threshold, grey, keptRoom, out var found, out var marked);
            await Assert.That(mismatch).IsNull().Because($"{name}, negative {negative}, room {keptRoom}");
            scenes++;
            candidates += found;
            markedAfterKept += marked;
        }

        // The lists compared have to hold candidates
        await Assert.That(scenes).IsGreaterThan(100);
        await Assert.That(candidates).IsGreaterThan(negative ? 40 : 120);
        // The small room has to run out after it kept hits, so the sweep scans the rows after again
        if (keptRoom < KeptRoom)
            await Assert.That(markedAfterKept).IsGreaterThan(negative ? 2 : 20);
    }

    /// <summary>
    /// The rows a scan of another stride kept, given to both scans: each merges a kept row's hits where it would scan the row, and both lists are still the two scans', at the stride of the Standard QR read for every symbol (3 on these scenes, 12 on a 12-megapixel photograph), the scan's own, and strides sharing no row but the first.
    /// </summary>
    [Test]
    [Arguments(3, KeptRoom)]
    [Arguments(4, KeptRoom)]
    [Arguments(5, KeptRoom)]
    [Arguments(7, KeptRoom)]
    [Arguments(12, KeptRoom)]
    [Arguments(3, SmallRoom)]
    [Arguments(12, SmallRoom)]
    public async Task GivenRowsOfAnotherStride_BothListsAreTheTwoScans(int givenStride, int givenRoom)
    {
        var scenes = 0;
        var rowsGiven = 0;
        foreach (var (name, luminance, width, height) in Scenes())
        {
            var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
            var mismatch = CompareGiven(luminance, width, height, threshold, grey, givenStride, givenRoom, out var kept);
            await Assert.That(mismatch).IsNull().Because($"{name}, given stride {givenStride}, room {givenRoom}");
            scenes++;
            rowsGiven += kept;
        }

        await Assert.That(scenes).IsGreaterThan(100);
        // Rows with hits have to be given, or nothing was merged from them. The wider strides reach fewer rows of these small scenes
        await Assert.That(rowsGiven).IsGreaterThan(givenRoom < KeptRoom ? 10 : 40);
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
            DrawFinder(luminance, width, Margin + f * Pitch, Margin, Module);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);

        var mismatch = Compare(luminance, width, height, threshold, grey, KeptRoom, out _, out _);
        await Assert.That(mismatch).IsNull();

        // The premise: every finder is found, all on one band of rows, so each strided row through the band holds a hit of every finder, more than the row's list holds
        var strided = new FinderPattern[4096];
        var count = FinderPatternFinder.FindCandidates(luminance, width, height, threshold, strided, grey);
        await Assert.That(count).IsEqualTo(Finders);
        await Assert.That(Finders).IsGreaterThan(FinderPatternFinder.RowHitCapacity);
        var centreY = Margin + 3.5f * Module;
        await Assert.That(strided.AsSpan(0, count).ToArray().All(c => Math.Abs(c.Y - centreY) < 1f)).IsTrue();
    }

    /// <summary>
    /// The sweep takes a row's kept hits from where the rows before it left off, past the rows it scans again: a finder above a band of more finders than a row gathers, and one below it.
    /// </summary>
    [Test]
    public async Task KeptRowsAfterARowScannedAgain_BothListsAreTheTwoScans()
    {
        const int Finders = 70;
        const int Module = 2;
        const int Pitch = 9 * Module;
        const int Margin = 4 * Module;
        const int Band = 11 * Module;
        var width = 2 * Margin + Finders * Pitch;
        var height = 2 * Margin + 2 * Band + 7 * Module;
        var luminance = new byte[width * height];
        Array.Fill(luminance, (byte)220);
        DrawFinder(luminance, width, Margin, Margin, Module);
        for (var f = 0; f < Finders; f++)
            DrawFinder(luminance, width, Margin + f * Pitch, Margin + Band, Module);
        DrawFinder(luminance, width, Margin, Margin + 2 * Band, Module);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);

        var mismatch = Compare(luminance, width, height, threshold, grey, KeptRoom, out _, out _);
        await Assert.That(mismatch).IsNull();

        // The premise: a strided row is kept after a row scanned again, while hits of the rows before are kept, so the sweep has to skip past them
        var kept = new FinderPattern[KeptRoom];
        var rowEnds = new int[FinderPatternFinder.StridedRowCount(height)];
        FinderPatternFinder.FindCandidatesKeepingRowHits(luminance, width, height, threshold, grey, new FinderPattern[4096], out _, kept, rowEnds);
        var keptBefore = 0;
        var scannedAgain = false;
        var reached = false;
        foreach (var end in rowEnds)
        {
            if (end < 0)
                scannedAgain = true;
            else if (scannedAgain && keptBefore > 0 && end > keptBefore)
                reached = true;
            else if (!scannedAgain)
                keptBefore = end;
        }
        await Assert.That(reached).IsTrue();
    }

    /// <summary>A finder pattern of 7 by 7 modules, dark on a light image, its top-left module at <paramref name="left"/>, <paramref name="top"/>.</summary>
    private static void DrawFinder(byte[] luminance, int width, int left, int top, int module)
    {
        for (var row = 0; row < 7; row++)
        {
            for (var column = 0; column < 7; column++)
            {
                if (Math.Max(Math.Abs(row - 3), Math.Abs(column - 3)) == 2)
                    continue;
                for (var dy = 0; dy < module; dy++)
                    luminance.AsSpan((top + row * module + dy) * width + left + column * module, module).Fill(30);
            }
        }
    }

    /// <remarks><c>markedAfterKept</c> counts the strided rows the sweep scans again that come after a row whose hits were kept: rows past the kept room, and rows whose hits merged or filled their gathering list.</remarks>
    private static string? Compare(byte[] luminance, int width, int height, byte threshold, in GreyLevels grey, int keptRoom, out int found, out int markedAfterKept)
    {
        var strided = new FinderPattern[4096];
        var swept = new FinderPattern[4096];
        var stridedAlone = new FinderPattern[4096];
        var sweptAlone = new FinderPattern[4096];
        var stridedAloneCount = FinderPatternFinder.FindCandidates(luminance, width, height, threshold, stridedAlone, grey);
        var sweptAloneCount = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, sweptAlone, grey);
        var onePassStrided = new FinderPattern[4096];
        var onePassSwept = new FinderPattern[4096];
        FinderPatternFinder.FindCandidatesStridedAndFullSweep(luminance, width, height, threshold, grey, onePassStrided, out var onePassStridedCount, onePassSwept, out var onePassSweptCount);
        var kept = new FinderPattern[keptRoom];
        var rowEnds = new int[FinderPatternFinder.StridedRowCount(height)];
        FinderPatternFinder.FindCandidatesKeepingRowHits(luminance, width, height, threshold, grey, strided, out var stridedCount, kept, rowEnds);
        var sweptCount = FinderPatternFinder.FindCandidatesFullSweepReplaying(luminance, width, height, threshold, grey, swept, kept, rowEnds);
        found = sweptAloneCount;
        markedAfterKept = 0;
        var anyKept = false;
        foreach (var end in rowEnds)
        {
            if (end > 0)
                anyKept = true;
            else if (end < 0 && anyKept)
                markedAfterKept++;
        }
        return Mismatch("one pass, strided", onePassStrided, onePassStridedCount, stridedAlone, stridedAloneCount)
            ?? Mismatch("one pass, full sweep", onePassSwept, onePassSweptCount, sweptAlone, sweptAloneCount)
            ?? Mismatch("kept hits, strided", strided, stridedCount, stridedAlone, stridedAloneCount)
            ?? Mismatch("kept hits, full sweep", swept, sweptCount, sweptAlone, sweptAloneCount);
    }

    /// <remarks><c>rowsWithHits</c> counts the given rows kept with a hit in them.</remarks>
    private static string? CompareGiven(byte[] luminance, int width, int height, byte threshold, in GreyLevels grey, int givenStride, int givenRoom, out int rowsWithHits)
    {
        var stridedAlone = new FinderPattern[4096];
        var sweptAlone = new FinderPattern[4096];
        var stridedAloneCount = FinderPatternFinder.FindCandidates(luminance, width, height, threshold, stridedAlone, grey);
        var sweptAloneCount = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, sweptAlone, grey);

        // The other scan, at its stride, keeping its rows
        var givenStridedAlone = new FinderPattern[4096];
        var givenStridedAloneCount = FinderPatternFinder.FindCandidatesWith(luminance, width, height, threshold, givenStridedAlone, grey, givenStride, FinderRowKernel.Auto);
        var givenHits = new FinderPattern[givenRoom];
        var givenRowEnds = new int[FinderPatternFinder.StridedRowCount(height, givenStride)];
        var givenStrided = new FinderPattern[4096];
        FinderPatternFinder.FindCandidatesKeepingRowHits(luminance, width, height, threshold, grey, givenStrided, out var givenStridedCount, givenHits, givenRowEnds, givenStride, given: default);
        var given = new FinderPatternFinder.KeptRows(givenHits, givenRowEnds, givenStride);
        rowsWithHits = 0;
        var start = 0;
        foreach (var end in givenRowEnds)
        {
            if (end > start)
                rowsWithHits++;
            if (end >= 0)
                start = end;
        }

        var onePassStrided = new FinderPattern[4096];
        var onePassSwept = new FinderPattern[4096];
        FinderPatternFinder.FindCandidatesStridedAndFullSweep(luminance, width, height, threshold, grey, onePassStrided, out var onePassStridedCount, onePassSwept, out var onePassSweptCount, given);
        var strided = new FinderPattern[4096];
        var swept = new FinderPattern[4096];
        var kept = new FinderPattern[KeptRoom];
        var rowEnds = new int[FinderPatternFinder.StridedRowCount(height)];
        FinderPatternFinder.FindCandidatesKeepingRowHits(luminance, width, height, threshold, grey, strided, out var stridedCount, kept, rowEnds, FinderPatternFinder.CandidateRowStride, given);
        var sweptCount = FinderPatternFinder.FindCandidatesFullSweepReplaying(luminance, width, height, threshold, grey, swept, new FinderPatternFinder.KeptRows(kept, rowEnds, FinderPatternFinder.CandidateRowStride), given);
        var sweptFromGiven = new FinderPattern[4096];
        var sweptFromGivenCount = FinderPatternFinder.FindCandidatesFullSweepReplaying(luminance, width, height, threshold, grey, sweptFromGiven, own: default, given);

        return Mismatch($"kept hits at stride {givenStride}", givenStrided, givenStridedCount, givenStridedAlone, givenStridedAloneCount)
            ?? Mismatch("one pass given rows, strided", onePassStrided, onePassStridedCount, stridedAlone, stridedAloneCount)
            ?? Mismatch("one pass given rows, full sweep", onePassSwept, onePassSweptCount, sweptAlone, sweptAloneCount)
            ?? Mismatch("kept hits given rows, strided", strided, stridedCount, stridedAlone, stridedAloneCount)
            ?? Mismatch("kept hits given rows, full sweep", swept, sweptCount, sweptAlone, sweptAloneCount)
            ?? Mismatch("full sweep from the given rows alone", sweptFromGiven, sweptFromGivenCount, sweptAlone, sweptAloneCount);
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
