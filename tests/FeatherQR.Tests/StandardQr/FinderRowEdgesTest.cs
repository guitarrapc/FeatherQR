using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The two stages of the edge-list row kernel held to what they stand for, below the level where <see cref="FinderRowKernelParityTest"/> can see.
/// That test compares candidates, and a row kernel that reports a window too many changes none: the cross-checks judge the same row again with the scalar checks and refuse it. So the edge extraction is held here to a pixel-by-pixel scan, and the window classification to the scalar predicates themselves, window by window.
/// </summary>
public class FinderRowEdgesTest
{
    [Test]
    public async Task ExtractRowEdges_MatchesAPixelByPixelScan()
    {
        if (!FinderPatternFinder.IsEdgeListKernelSupported)
        {
            Skip.Test("The edge-list kernel needs 256-bit vectors or AdvSimd (net8.0+).");
            return;
        }

        var rows = 0;
        var runsSeen = 0L;
        string? first = null;
        // every length around the 32-pixel compares and the 64-pixel words, and the last one the kernel takes
        var lengths = Enumerable.Range(1, 200).Concat([255, 256, 257, 511, 512, 513, 1000, 4032, 4094, 4095]);
        foreach (var length in lengths)
        {
            foreach (var threshold in new byte[] { 0, 1, 128, 255 })
            {
                foreach (var kind in new[] { "noise", "runs", "dark", "light", "dark ends", "edges at the threshold" })
                {
                    var row = BuildRow(length, threshold, kind, seed: length * 7 + threshold);
                    var capacity = (length + 1) / 2 + 1;
                    var starts = new short[capacity];
                    var ends = new short[capacity];
                    Array.Fill(starts, (short)-1);
                    Array.Fill(ends, (short)-1);
                    var count = FinderPatternFinder.ExtractRowEdges(row, threshold, starts, ends);

                    var expectedStarts = new List<int>();
                    var expectedEnds = new List<int>();
                    for (var x = 0; x < length; x++)
                    {
                        var dark = row[x] < threshold;
                        var before = x > 0 && row[x - 1] < threshold;
                        if (dark && !before)
                            expectedStarts.Add(x);
                        if (!dark && before)
                            expectedEnds.Add(x);
                    }
                    if (row[length - 1] < threshold)
                        expectedEnds.Add(length);

                    rows++;
                    runsSeen += expectedEnds.Count;
                    if (count != expectedEnds.Count
                        || !starts.AsSpan(0, count).ToArray().Select(v => (int)v).SequenceEqual(expectedStarts)
                        || !ends.AsSpan(0, count).ToArray().Select(v => (int)v).SequenceEqual(expectedEnds))
                    {
                        first ??= $"length={length}, threshold={threshold}, {kind}: {count} runs, expected {expectedEnds.Count}";
                    }
                }
            }
        }

        await Assert.That(first).IsNull();
        await Assert.That(rows).IsGreaterThan(4_000);
        await Assert.That(runsSeen).IsGreaterThan(30_000);
    }

    [Test]
    public async Task ClassifyWindows_MatchesTheScalarChecks_EveryRunVectorUpTo14()
    {
        if (!FinderPatternFinder.IsEdgeListKernelSupported)
        {
            Skip.Test("The edge-list kernel needs 256-bit vectors or AdvSimd (net8.0+).");
            return;
        }

        // Windows overlap, so a lane takes an arbitrary vector only every third dark run: six vectors a call, the lanes between them judged too
        var checker = new WindowChecker();
        Span<int> vector = stackalloc int[5];
        for (var r0 = 1; r0 <= 14; r0++)
            for (var r1 = 1; r1 <= 14; r1++)
                for (var r2 = 1; r2 <= 14; r2++)
                    for (var r3 = 1; r3 <= 14; r3++)
                        for (var r4 = 1; r4 <= 14; r4++)
                        {
                            vector[0] = r0; vector[1] = r1; vector[2] = r2; vector[3] = r3; vector[4] = r4;
                            checker.Add(vector);
                        }
        checker.Flush();

        await Assert.That(checker.FirstMismatch).IsNull();
        await Assert.That(checker.Strict).IsGreaterThan(1_000);
        await Assert.That(checker.Near).IsGreaterThan(1_000);
        await Assert.That(checker.Crisp).IsGreaterThan(40);
        await Assert.That(checker.Windows - checker.Strict).IsGreaterThan(100_000);
    }

    [Test]
    public async Task ClassifyWindows_MatchesTheScalarChecks_AtTheToleranceEdgesUpToTheWidthLimit()
    {
        if (!FinderPatternFinder.IsEdgeListKernelSupported)
        {
            Skip.Test("The edge-list kernel needs 256-bit vectors or AdvSimd (net8.0+).");
            return;
        }

        // Around 1:1:3:1:1 at every module size whose window still fits a row the kernel takes, each run pushed to and past its tolerance.
        // A window of up to 4,095 pixels is where 7 · run and 3 · total come closest to the end of a sixteen-bit lane.
        var random = new Random(20260922);
        var checker = new WindowChecker();
        Span<int> vector = stackalloc int[5];
        for (var trial = 0; trial < 3_000_000; trial++)
        {
            var module = trial % 3 == 0 ? random.Next(1, 8) : random.Next(1, 580);
            for (var i = 0; i < 5; i++)
            {
                var nominal = i == 2 ? 3 * module : module;
                var reach = nominal / 2 + 2;
                vector[i] = Math.Max(1, nominal + random.Next(-reach, reach + 1));
            }
            if (vector[0] + vector[1] + vector[2] + vector[3] + vector[4] >= FinderPatternFinder.EdgeListWidthLimit)
                continue;
            checker.Add(vector);
        }
        checker.Flush();

        await Assert.That(checker.FirstMismatch).IsNull();
        await Assert.That(checker.Strict).IsGreaterThan(100_000);
        await Assert.That(checker.Near).IsGreaterThan(10_000);
        await Assert.That(checker.Windows - checker.Strict).IsGreaterThan(100_000);
    }

    /// <summary>Lays run vectors out as dark runs of one row, classifies them a step of windows at a time (<see cref="FinderPatternFinder.ClassifyWindowLanes"/>), and holds every window of the row, the ones between the vectors included, to the scalar checks.</summary>
    private sealed class WindowChecker
    {
        private readonly short[] _starts = new short[64];
        private readonly short[] _ends = new short[64];
        private readonly int[] _runs = new int[5];
        private int _darkRuns;
        private int _position;

        public long Windows { get; private set; }
        public long Strict { get; private set; }
        public long Near { get; private set; }
        public long Crisp { get; private set; }
        public string? FirstMismatch { get; private set; }

        public void Add(ReadOnlySpan<int> vector)
        {
            // three dark runs and the two gaps between them; the gap before the next vector is one pixel
            if (_darkRuns + 3 > FinderPatternFinder.ClassifyWindowLanes + 2 || _position + vector[0] + vector[1] + vector[2] + vector[3] + vector[4] + 1 >= FinderPatternFinder.EdgeListWidthLimit)
                Flush();
            for (var i = 0; i < 5; i += 2)
            {
                _starts[_darkRuns] = (short)_position;
                _position += vector[i];
                _ends[_darkRuns] = (short)_position;
                _darkRuns++;
                _position += i < 4 ? vector[i + 1] : 1;
            }
        }

        public void Flush()
        {
            var windows = _darkRuns - 2;
            if (windows > 0)
            {
                FinderPatternFinder.ClassifyWindows(ref _starts[0], ref _ends[0], 0, out var strict, out var near, out var crisp);
                for (var w = 0; w < windows; w++)
                {
                    _runs[0] = _ends[w] - _starts[w];
                    _runs[1] = _starts[w + 1] - _ends[w];
                    _runs[2] = _ends[w + 1] - _starts[w + 1];
                    _runs[3] = _starts[w + 2] - _ends[w + 1];
                    _runs[4] = _ends[w + 2] - _starts[w + 2];
                    var expectedStrict = FinderPatternFinder.IsFinderRatio(_runs);
                    var expectedNear = FinderPatternFinder.IsNearFinderRatio(_runs[0], _runs[1], _runs[2], _runs[3], _runs[4]);
                    var expectedCrisp = FinderPatternFinder.IsSmallCrispFinderRuns(_runs[0], _runs[1], _runs[2], _runs[3], _runs[4]);
                    Windows++;
                    if (expectedStrict) Strict++;
                    if (expectedNear) Near++;
                    if (expectedCrisp) Crisp++;
                    var bit = 1u << w;
                    if (((strict & bit) != 0) != expectedStrict || ((near & bit) != 0) != expectedNear || ((crisp & bit) != 0) != expectedCrisp)
                        FirstMismatch ??= $"runs [{string.Join(",", _runs)}]: strict {(strict & bit) != 0} / {expectedStrict}, near {(near & bit) != 0} / {expectedNear}, crisp {(crisp & bit) != 0} / {expectedCrisp}";
                }
            }
            _darkRuns = 0;
            _position = 0;
            Array.Clear(_starts);
            Array.Clear(_ends);
        }
    }

    private static byte[] BuildRow(int length, byte threshold, string kind, int seed)
    {
        var random = new Random(seed);
        var row = new byte[length];
        switch (kind)
        {
            case "noise":
                random.NextBytes(row);
                break;
            case "dark":
                break;
            case "light":
                row.AsSpan().Fill(255);
                break;
            default:
                // runs of 1 to 9 pixels; "dark ends" makes the first and the last run dark, "edges at the threshold" takes its levels from next to it
                var dark = kind == "dark ends" || random.Next(2) == 0;
                for (var x = 0; x < length;)
                {
                    var run = random.Next(1, 10);
                    var level = kind == "edges at the threshold"
                        ? (dark ? (byte)Math.Max(threshold - 1, 0) : threshold)
                        : (dark ? (byte)0 : (byte)255);
                    for (var k = 0; k < run && x < length; k++, x++)
                        row[x] = level;
                    dark = !dark;
                }
                if (kind == "dark ends")
                    row[length - 1] = 0;
                break;
        }
        return row;
    }
}
