using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The triples a failed decode tries after the selected one (<see cref="FinderPatternFinder.AlternativeTriples"/>): by the height their least confirmed member was seen over, in modules, then by the selection score; never the selected triple again.
/// </summary>
public class FinderAlternativeTriplesTest
{
    /// <summary>
    /// Three finders confirmed on many rows and a false candidate confirmed on a few, which with two of them makes the better triangle and was selected: the three finders come first, whatever their shape.
    /// </summary>
    [Test]
    public async Task WeakestMemberFirst_TheBestConfirmedTripleComesFirst()
    {
        // A keystoned symbol's three finders: the far one drawn larger, the triangle far from right isosceles
        var topLeft = Candidate(100f, 100f, 4f, count: 12);
        var topRight = Candidate(300f, 120f, 4f, count: 11);
        var bottomLeft = Candidate(80f, 420f, 7f, count: 25);
        // A false candidate inside the symbol, making a near right isosceles triangle with the top two
        var inside = Candidate(120f, 300f, 4.2f, count: 2);

        var seen = HandedOut([inside, topLeft, bottomLeft, topRight], [topLeft, topRight, inside]);

        await Assert.That(seen.Count).IsGreaterThan(0);
        await Assert.That(Holds(seen[0], topLeft) && Holds(seen[0], topRight) && Holds(seen[0], bottomLeft)).IsTrue();
    }

    /// <summary>Heights are rows over module size: a candidate of twice the module size confirmed on more rows than a finder can still be less believable, and its triple comes after.</summary>
    [Test]
    public async Task Height_IsInModules_NotRows()
    {
        var a = Candidate(100f, 100f, 4f, count: 10);
        var b = Candidate(300f, 100f, 4f, count: 10);
        var c = Candidate(100f, 300f, 4f, count: 8);
        // More rows than c, at twice the module size: 1.25 modules against c's 2
        var large = Candidate(300f, 300f, 8f, count: 10);

        var seen = HandedOut([large, a, b, c], []);

        await Assert.That(seen.Count).IsGreaterThan(0);
        await Assert.That(Holds(seen[0], a) && Holds(seen[0], b) && Holds(seen[0], c)).IsTrue();
    }

    /// <summary>Past the selected triple the next least confirmed member decides, its triples by the selection score, and the list ends.</summary>
    [Test]
    public async Task SameWeakestMember_ByScore_SelectedSkipped_ThenExhausted()
    {
        var a = Candidate(100f, 100f, 4f, count: 20);
        var b = Candidate(300f, 100f, 4f, count: 19);
        var c = Candidate(100f, 300f, 4f, count: 18);
        // The least confirmed of the four, below the fourth corner of the square, so its three triples score apart: 0.258 with a and c, 0.278 with a and b, 0.345 with b and c
        var d = Candidate(300f, 330f, 4f, count: 3);
        // Least of all, off every corner
        var e = Candidate(200f, 150f, 4f, count: 1);

        var seen = HandedOut([e, d, c, b, a], [a, b, c]);

        // (a, b, c) is the selected triple and never comes again; d's three triples come next in score order, then e's six
        await Assert.That(seen.Any(t => Holds(t, a) && Holds(t, b) && Holds(t, c))).IsFalse();
        await Assert.That(seen.Count).IsEqualTo(3 + 6);
        FinderPattern[][] withD = [[a, c, d], [a, b, d], [b, c, d]];
        for (var i = 0; i < 3; i++)
            await Assert.That(withD[i].All(member => Holds(seen[i], member))).IsTrue().Because($"triple {i} with d");
        for (var i = 3; i < seen.Count; i++)
            await Assert.That(Holds(seen[i], e)).IsTrue();
        for (var i = 4; i < seen.Count; i++)
            await Assert.That(Score(seen[i])).IsGreaterThanOrEqualTo(Score(seen[i - 1]) - 1e-6f);
    }

    /// <summary>Three candidates at one point make no triangle and are never handed out; the next member's triples still are.</summary>
    [Test]
    public async Task CoincidentCandidates_AreNeverATriple()
    {
        var a = Candidate(100f, 100f, 4f, count: 9);
        var twin = Candidate(100f, 100f, 4f, count: 8);
        var triplet = Candidate(100f, 100f, 4f, count: 7);
        var b = Candidate(300f, 100f, 4f, count: 6);

        var seen = HandedOut([a, twin, triplet, b], []);

        await Assert.That(seen.Count).IsEqualTo(3);
        foreach (var triple in seen)
            await Assert.That(Holds(triple, b)).IsTrue().Because("the only triples with any extent hold the fourth candidate");
    }

    /// <summary>
    /// The selected triple is recognized by the merge rule, within a module, because the complementary rescan merges more rows into a candidate after the stride pass selected it and moves it.
    /// </summary>
    [Test]
    public async Task SelectedTriple_MovedByTheRescan_IsStillSkipped()
    {
        var a = Candidate(100f, 100f, 4f, count: 9);
        var b = Candidate(300f, 100f, 4f, count: 9);
        var c = Candidate(100f, 300f, 4f, count: 9);
        FinderPattern[] selected = [Candidate(100.8f, 99.4f, 4f, count: 3), Candidate(299.1f, 100.6f, 4f, count: 3), Candidate(100.2f, 300.9f, 4f, count: 3)];

        var seen = HandedOut([a, b, c], selected);

        await Assert.That(seen.Count).IsEqualTo(0);
    }

    /// <summary>Every triple the enumerator hands out, in order.</summary>
    private static List<FinderPattern[]> HandedOut(FinderPattern[] candidates, FinderPattern[] selected)
    {
        var alternatives = new FinderPatternFinder.AlternativeTriples(candidates, selected);
        Span<FinderPattern> triple = stackalloc FinderPattern[3];
        var seen = new List<FinderPattern[]>();
        while (alternatives.TryNext(triple))
            seen.Add(triple.ToArray());
        return seen;
    }

    private static FinderPattern Candidate(float x, float y, float moduleSize, int count) => new() { X = x, Y = y, ModuleSize = moduleSize, Count = count };

    private static bool Holds(FinderPattern[] triple, FinderPattern candidate)
        => triple.Any(member => member.X == candidate.X && member.Y == candidate.Y && member.ModuleSize == candidate.ModuleSize);

    /// <summary>The selection score: distance from a right isosceles triangle plus relative module-size spread.</summary>
    private static float Score(FinderPattern[] triple)
    {
        var smallest = triple.Min(static t => t.ModuleSize);
        var largest = triple.Max(static t => t.ModuleSize);
        float[] sides = [DistanceSquared(triple[0], triple[1]), DistanceSquared(triple[0], triple[2]), DistanceSquared(triple[1], triple[2])];
        Array.Sort(sides);
        return (Math.Abs(sides[2] - 2f * sides[0]) + Math.Abs(sides[2] - 2f * sides[1])) / sides[2] + (largest - smallest) / smallest;

        static float DistanceSquared(FinderPattern a, FinderPattern b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
    }
}
