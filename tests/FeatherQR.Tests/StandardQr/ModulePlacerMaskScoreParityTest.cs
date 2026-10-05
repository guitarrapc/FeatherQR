using System.Runtime.Intrinsics;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// Every mask scorer against the textbook penalty (<see cref="ModulePlacerMaskPackedParityTest.ReferenceScore"/>), score for score.
/// The selection tests compare chosen patterns, which a scorer can get right for the wrong reason. These feed each tier's
/// scorer whole matrices and compare the penalty itself: every single-word size, and the scalar scorer of two- and three-word rows
/// (CalculateScorePacked) at its first and last sizes and between. The transposed tiers for versions 12-40 (AVX2, 128-bit and ARM64) pack and transpose their own
/// input, so their scores are held to the textbook in <see cref="ModulePlacerMaskTransposedParityTest"/>.
/// </summary>
/// <remarks>
/// <para>
/// A scorer is a pure function of the matrix, so the inputs need not be QR symbols: random matrices at three densities,
/// degenerate ones (all light, all dark, stripes both ways), and matrices with finder-like windows planted in rows and columns,
/// forward and backward, at the first and last start a row allows, across the 64-bit word boundaries the multi-word tiers
/// split rows at, and beside near misses (one module of the window flipped) that must not count.
/// </para>
/// <para>
/// The early abort is held to its contract: with the bound at or above the score the result is the score, and below it the
/// result is the score or <see cref="int.MaxValue"/>, never another value. The lane scorers are held to it lane by lane. ARM64's
/// single-word scorer has no abort; its transposed tier's abort is held in <see cref="ModulePlacerMaskTransposedParityTest"/>.
/// </para>
/// <para>
/// Every call gets fresh row buffers: a scorer may use its rows as scratch for the column finder windows.
/// </para>
/// </remarks>
public class ModulePlacerMaskScoreParityTest
{
    // Symbol sizes of the tiers: single word (versions 1-11), two words (12-27), three words (28-40).
    private static readonly int[] SingleWordSizes = [21, 25, 29, 33, 37, 41, 45, 49, 53, 57, 61];
    private static readonly int[] TwoWordSizes = [65, 69, 81, 97, 113, 125];
    private static readonly int[] ThreeWordSizes = [129, 133, 149, 165, 177];

    [Test]
    public async Task Score64_MatchesTextbook()
    {
        foreach (var size in SingleWordSizes)
        {
            foreach (var (name, matrix) in Matrices(size))
            {
                var rows = PackWords(matrix, size, 0);
                var expected = ModulePlacerMaskPackedParityTest.ReferenceScore(matrix, size);
                await Assert.That(ModulePlacer.CalculateScore64(rows.ToArray(), new ulong[size], size, int.MaxValue)).IsEqualTo(expected).Because($"size {size}, {name}");
                await AssertAbortContract(abort => ModulePlacer.CalculateScore64(rows.ToArray(), new ulong[size], size, abort), expected, $"size {size}, {name}");
            }
        }
    }

    [Test]
    public async Task ScorePacked_MatchesTextbook()
    {
        foreach (var size in TwoWordSizes.Concat(ThreeWordSizes))
        {
            foreach (var (name, matrix) in Matrices(size))
            {
                var w0 = PackWords(matrix, size, 0);
                var w1 = PackWords(matrix, size, 1);
                var w2 = PackWords(matrix, size, 2);
                var rows = new ModulePlacer.Row192[size];
                for (var y = 0; y < size; y++) rows[y] = new ModulePlacer.Row192(w0[y], w1[y], w2[y]);
                var expected = ModulePlacerMaskPackedParityTest.ReferenceScore(matrix, size);
                await Assert.That(ModulePlacer.CalculateScorePacked(rows, new ModulePlacer.Row192[size], size)).IsEqualTo(expected).Because($"size {size}, {name}");
            }
        }
    }

    [Test]
    public async Task ScoreLanes64_MatchesTextbook()
    {
        if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            Skip.Test("AVX2 not supported on this machine");
            return;
        }

        foreach (var size in SingleWordSizes)
        {
            var matrices = Matrices(size).ToArray();
            // Four candidates per call, one per lane, rotated so every matrix visits every lane.
            for (var first = 0; first < matrices.Length; first++)
            {
                var lanes = new (string Name, byte[] Matrix)[4];
                for (var lane = 0; lane < 4; lane++) lanes[lane] = matrices[(first + lane) % matrices.Length];
                var expected = lanes.Select(l => ModulePlacerMaskPackedParityTest.ReferenceScore(l.Matrix, size)).ToArray();
                var words = lanes.Select(l => PackWords(l.Matrix, size, 0)).ToArray();

                Vector128<int> Score(int abort)
                {
                    // Fresh rows each call: the scorer may use them as scratch once it no longer needs them.
                    var rows = new Vector256<ulong>[64];
                    for (var y = 0; y < size; y++) rows[y] = Vector256.Create(words[0][y], words[1][y], words[2][y], words[3][y]);
                    return ModulePlacer.ScoreLanes64(rows, new Vector256<ulong>[64], size, abort);
                }

                var scores = Score(int.MaxValue);
                for (var lane = 0; lane < 4; lane++)
                    await Assert.That(scores.GetElement(lane)).IsEqualTo(expected[lane]).Because($"size {size}, {lanes[lane].Name} in lane {lane}");
                await AssertLaneAbortContract(abort => Score(abort), expected, $"size {size}, from {lanes[0].Name}");
            }
        }
    }

    [Test]
    public async Task ScoreLanes64Vector128_MatchesTextbook()
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            Skip.Test("Vector128 not hardware accelerated on this machine");
            return;
        }

        foreach (var size in SingleWordSizes)
        {
            var matrices = Matrices(size).ToArray();
            for (var first = 0; first < matrices.Length; first++)
            {
                var lanes = new[] { matrices[first], matrices[(first + 1) % matrices.Length] };
                var expected = lanes.Select(l => ModulePlacerMaskPackedParityTest.ReferenceScore(l.Matrix, size)).ToArray();
                var words = lanes.Select(l => PackWords(l.Matrix, size, 0)).ToArray();

                Vector128<int> Score(int abort)
                {
                    var rows = new Vector128<ulong>[64];
                    for (var y = 0; y < size; y++) rows[y] = Vector128.Create(words[0][y], words[1][y]);
                    var (a, b) = ModulePlacer.ScoreLanes64Vector128(rows, new Vector128<ulong>[64], size, abort);
                    return Vector128.Create(a, b, 0, 0);
                }

                var scores = Score(int.MaxValue);
                for (var lane = 0; lane < 2; lane++)
                    await Assert.That(scores.GetElement(lane)).IsEqualTo(expected[lane]).Because($"size {size}, {lanes[lane].Name} in lane {lane}");
                await AssertLaneAbortContract(abort => Score(abort), expected, $"size {size}, from {lanes[0].Name}");
            }
        }
    }

    [Test]
    public async Task Score64AdvSimd_MatchesTextbook()
    {
        if (!System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
        {
            Skip.Test("AdvSimd.Arm64 not supported on this machine");
            return;
        }

        foreach (var size in SingleWordSizes)
        {
            foreach (var (name, matrix) in Matrices(size))
            {
                var rows = PackWords(matrix, size, 0);
                var expected = ModulePlacerMaskPackedParityTest.ReferenceScore(matrix, size);
                await Assert.That(ModulePlacer.CalculateScore64AdvSimd(rows, new ulong[size], new ulong[64], new ulong[64], size)).IsEqualTo(expected).Because($"size {size}, {name}");
            }
        }
    }

    // ---- the abort contract ----

    private static async Task AssertAbortContract(Func<int, int> score, int expected, string because)
    {
        foreach (var bound in new[] { expected, expected - 1, expected / 2, 0 })
        {
            var result = score(bound);
            if (bound >= expected)
                await Assert.That(result).IsEqualTo(expected).Because($"{because}, bound {bound} at or above the score");
            else if (result != int.MaxValue)
                await Assert.That(result).IsEqualTo(expected).Because($"{because}, bound {bound}: the score or int.MaxValue");
        }
    }

    private static async Task AssertLaneAbortContract(Func<int, Vector128<int>> score, int[] expected, string because)
    {
        // A lane whose score is at or under the bound can still win or tie, so it reads its exact score whatever the other lanes
        // do. Each lane's own score, and one under it, put a bound on both sides of every lane.
        foreach (var bound in expected.SelectMany(e => new[] { e, e - 1 }).Append(0).Distinct())
        {
            var result = score(bound);
            for (var lane = 0; lane < expected.Length; lane++)
            {
                var value = result.GetElement(lane);
                if (bound >= expected[lane])
                    await Assert.That(value).IsEqualTo(expected[lane]).Because($"{because}, lane {lane}, bound {bound} at or above the lane's score");
                else if (value != int.MaxValue)
                    await Assert.That(value).IsEqualTo(expected[lane]).Because($"{because}, lane {lane}, bound {bound}: the score or int.MaxValue");
            }
        }
    }

    // ---- inputs ----

    /// <summary>Bits 64k..64k+63 of every row (bit c of the word = module (y, 64k + c)).</summary>
    private static ulong[] PackWords(byte[] matrix, int size, int word)
    {
        var words = new ulong[size];
        for (var y = 0; y < size; y++)
            for (var x = 64 * word; x < Math.Min(size, 64 * word + 64); x++)
                if (matrix[y * size + x] != 0) words[y] |= 1ul << (x - 64 * word);
        return words;
    }

    private static readonly byte[] Forward = [0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1];
    private static readonly byte[] Backward = [1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0];

    private static IEnumerable<(string Name, byte[] Matrix)> Matrices(int size)
    {
        var random = new Random(size);
        foreach (var density in new[] { 0.5, 0.2, 0.8 })
            for (var seed = 0; seed < 2; seed++)
                yield return ($"random {density}#{seed}", Fill(size, (_, _) => random.NextDouble() < density));

        yield return ("all light", Fill(size, (_, _) => false));
        yield return ("all dark", Fill(size, (_, _) => true));
        yield return ("row stripes", Fill(size, (y, _) => y % 2 == 0));
        yield return ("column stripes", Fill(size, (_, x) => x % 3 == 0));
        yield return ("checker", Fill(size, (y, x) => ((y + x) & 1) == 0));

        // Windows where the row's own bounds and the word boundaries cut: the first start, the last, and each start that
        // straddles bit 64 or 128. Light background so only the planted windows (and their overlaps) can match.
        var starts = new List<int> { 0, size - 11 };
        foreach (var boundary in new[] { 64, 128 })
            for (var start = boundary - 10; start < boundary; start++)
                if (start >= 0 && start <= size - 11) starts.Add(start);
        foreach (var pattern in new[] { Forward, Backward })
        {
            var name = pattern == Forward ? "forward" : "backward";
            var rows = Fill(size, (_, _) => false);
            var columns = Fill(size, (_, _) => false);
            for (var i = 0; i < starts.Count; i++)
            {
                var line = (2 * i + 1) % size;
                Plant(rows, size, pattern, line, starts[i], vertical: false);
                Plant(columns, size, pattern, line, starts[i], vertical: true);
            }
            yield return ($"{name} at the bounds, rows", rows);
            yield return ($"{name} at the bounds, columns", columns);
        }

        // Near misses: every module of each window flipped in turn, beside a true window, on random lines.
        foreach (var pattern in new[] { Forward, Backward })
        {
            var name = pattern == Forward ? "forward" : "backward";
            foreach (var vertical in new[] { false, true })
            {
                var matrix = Fill(size, (_, _) => random.NextDouble() < 0.5);
                for (var flip = 0; flip < 11; flip++)
                {
                    var near = (byte[])pattern.Clone();
                    near[flip] ^= 1;
                    Plant(matrix, size, near, random.Next(size), random.Next(size - 10), vertical);
                    Plant(matrix, size, pattern, random.Next(size), random.Next(size - 10), vertical);
                }
                yield return ($"{name} near misses, {(vertical ? "columns" : "rows")}", matrix);
            }
        }
    }

    private static byte[] Fill(int size, Func<int, int, bool> dark)
    {
        var matrix = new byte[size * size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                matrix[y * size + x] = dark(y, x) ? (byte)1 : (byte)0;
        return matrix;
    }

    private static void Plant(byte[] matrix, int size, byte[] window, int line, int start, bool vertical)
    {
        for (var i = 0; i < window.Length; i++)
        {
            if (vertical) matrix[(start + i) * size + line] = window[i];
            else matrix[line * size + start + i] = window[i];
        }
    }
}
