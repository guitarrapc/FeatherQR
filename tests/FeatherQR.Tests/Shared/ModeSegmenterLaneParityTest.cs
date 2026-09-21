#if NET8_0_OR_GREATER
using System.Text;
using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>Lane plans against an independent all-states relaxation with a full predecessor table.</summary>
public class ModeSegmenterLaneParityTest
{
    [Test]
    [Arguments(7)]
    [Arguments(42)]
    [Arguments(20260920)]
    public async Task Lanes_MatchReferenceAcrossGroupsAndOccupancy(int seed)
    {
        if (!ModeSegmenter.LanesAccelerated)
            return;

        var random = new Random(seed);
        const string alphabet = "0AZ xéλあ\uFEFF\uD800\uDC00";
        // Below, at and above the 1.5x useful-work boundary; both four-lane groups,
        // inactive lanes, a singleton, and several lanes ending on the same character.
        int[][] shapes = [[1], [1, 1], [3, 7, 11], [1, 1, 1, 100], [6, 1, 1, 1], [6, 1, 1, 2],
            [7, 7, 7, 7, 1], [8, 8, 8, 8, 6, 3], [9, 9, 9, 9, 6, 2, 1], [31, 32, 33, 34, 1, 2, 3, 4]];
        foreach (var lengths in shapes)
        {
            var chunks = lengths.Select(length => new string(Enumerable.Range(0, length)
                .Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray())).ToArray();
            foreach (var charset in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
                foreach (var (mode, numeric, alnum, bytes) in new[] { (4, 10, 9, 8), (4, 12, 11, 16), (4, 14, 13, 16), (2, 5, 4, 4) })
                    await Check(chunks, charset, mode, numeric, alnum, bytes);
        }
    }

    [Test]
    public async Task Lanes_PreserveTiesMarksAndSliceBoundaries()
    {
        if (!ModeSegmenter.LanesAccelerated)
            return;

        string[][] cases =
        [
            ["xyz,123", "xyz,123", "xyz,123", "xyz,123"],
            ["\uFEFF123456789\uFEFFx", "123456789\uFEFF\uFEFF", "\uD800", "\uDC00"],
            ["x\uD800", "\uDC00x", "\uD800\uD800\uDC00\uDC00", "😀123\uFEFFABC"],
            [new string('x', 3000), new string('あ', 2900), new string('0', 6000), new string('A', 4000)],
        ];
        foreach (var chunks in cases)
            foreach (var charset in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
                await Check(chunks, charset, 4, 14, 13, 16);
    }

    [Test]
    public async Task Lanes_ClassifyEveryUtf16ValueWithoutTableAliasing()
    {
        if (!ModeSegmenter.LanesAccelerated)
            return;

        // Each value follows a Byte character and is followed by a dense run. This
        // makes an erroneous TBL class observable; the first character uses scalar setup.
        for (var first = 0; first < 65536; first += 1024)
        {
            var chunks = new string[4];
            for (var lane = 0; lane < chunks.Length; lane++)
            {
                var text = new StringBuilder();
                for (var c = first + lane * 256; c < first + (lane + 1) * 256; c++)
                    text.Append('x').Append((char)c).Append("123456789ABCDEF");
                chunks[lane] = text.ToString();
            }
            await Check(chunks, EciMode.Utf8, 4, 14, 13, 16);
        }
    }

    private static async Task Check(string[] chunks, EciMode charset, int mode, int numeric, int alnum, int bytes)
    {
        var text = string.Concat(chunks);
        var starts = new int[chunks.Length];
        var lengths = chunks.Select(chunk => chunk.Length).ToArray();
        for (var lane = 1; lane < chunks.Length; lane++)
            starts[lane] = starts[lane - 1] + lengths[lane - 1];
        var tableLength = lengths.Max() * ModeSegmenter.LaneTableBytesPerChar;
        var table = Enumerable.Repeat((byte)0xA5, tableLength + 32).ToArray();
        var costs = new int[chunks.Length];
        var states = new int[chunks.Length];
        ModeSegmenter.ComputeCostsLanes(text, starts, lengths, charset, mode, numeric, alnum, bytes,
            table.AsSpan(16, tableLength), costs, states);
        await Assert.That(table.AsSpan(0, 16).ToArray().All(x => x == 0xA5)).IsTrue();
        await Assert.That(table.AsSpan(16 + tableLength).ToArray().All(x => x == 0xA5)).IsTrue();
        for (var lane = 0; lane < chunks.Length; lane++)
        {
            var expected = Reference(chunks[lane], charset, mode, numeric, alnum, bytes);
            var because = $"lane {lane}, lengths {string.Join(',', lengths)}, charset {charset}, headers {mode}/{numeric}/{alnum}/{bytes}";
            await Assert.That(costs[lane]).IsEqualTo(expected.Cost).Because(because);
            await Assert.That(states[lane]).IsEqualTo(expected.State).Because(because);
            var plan = new ModeSegment[lengths[lane]];
            var built = ModeSegmenter.ReconstructLane(lengths[lane], table.AsSpan(16, tableLength), lane, states[lane], plan, out var count);
            await Assert.That(built).IsTrue().Because(because);
            var actualModes = new byte[lengths[lane]];
            for (var run = 0; run < count; run++)
                actualModes.AsSpan(plan[run].Start, plan[run].Length).Fill(plan[run].ModeIndex);
            await Assert.That(actualModes.AsSpan().SequenceEqual(expected.Modes)).IsTrue().Because(because);
            var expectedRuns = 1;
            for (var i = 1; i < expected.Modes.Length; i++)
                if (expected.Modes[i] != expected.Modes[i - 1]) expectedRuns++;
            await Assert.That(count).IsEqualTo(expectedRuns).Because(because);
        }
    }

    // Seven states, every transition relaxed in ascending predecessor order. No
    // keyed minima, classification table, run skipping or packed parents are shared.
    private static (int Cost, int State, byte[] Modes) Reference(string text, EciMode charset, int mode, int numeric, int alnum, int bytes)
    {
        const int unreachable = int.MaxValue / 4;
        var previous = new int[7];
        var current = new int[7];
        Array.Fill(previous, unreachable);
        previous[6] = 0;
        var parents = new byte[text.Length * 6];
        for (var i = 0; i < text.Length; i++)
        {
            Array.Fill(current, unreachable);
            var c = text[i];
            var byteCount = charset != EciMode.Utf8 ? 1
                : i > 0 && char.IsSurrogatePair(text[i - 1], c) ? 0
                : Encoding.UTF8.GetByteCount(text.AsSpan(i, i + 1 < text.Length && char.IsSurrogatePair(c, text[i + 1]) ? 2 : 1));
            for (var from = 0; from < 7; from++)
            {
                if (previous[from] == unreachable) continue;
                if (c is >= '0' and <= '9')
                    Relax(from < 3 ? (from + 1) % 3 : 1, from < 3 ? (from == 0 ? 4 : 3) : mode + numeric + 4);
                if ("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:".IndexOf(c) >= 0)
                    Relax(from == 4 ? 3 : 4, from is 3 or 4 ? (from == 3 ? 6 : 5) : mode + alnum + 6);
                if (!(charset == EciMode.Utf8 && c == '\uFEFF' && i > 0 && from != 5))
                    Relax(5, byteCount * 8 + (from == 5 ? 0 : mode + bytes));

                void Relax(int target, int added)
                {
                    var candidate = previous[from] + added;
                    if (candidate >= current[target]) return;
                    current[target] = candidate;
                    parents[i * 6 + target] = (byte)from;
                }
            }
            (previous, current) = (current, previous);
        }
        var state = 0;
        for (var s = 1; s < 6; s++)
            if (previous[s] < previous[state]) state = s;
        var finalState = state;
        var modes = new byte[text.Length];
        for (var i = text.Length - 1; i >= 0; i--)
        {
            modes[i] = (byte)(state < 3 ? 0 : state < 5 ? 1 : 2);
            state = parents[i * 6 + state];
        }
        return (previous[finalState], finalState, modes);
    }
}
#endif
