using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The bounds the axis cross-checks stop their walk at (<see cref="FinderPatternFinder.AxisRunBounds"/>) are exact only if no cross section the verdict could accept lies outside them.
/// That is a property of the verdict alone, so it is held here against the verdict's own checks, not against images: every run vector that passes the total check and either ratio check has to be inside every bound.
/// The grey second look is left out on purpose: it is asked only of runs that already pass the near-miss check, so it can refuse more and never accept more.
/// </summary>
public class FinderRunBoundsTest
{
    [Test]
    public async Task EveryAcceptableCrossSection_IsInsideTheBounds_SmallTotalsExhaustive()
    {
        // Every vector of five runs, zero included, whose sum can still pass the total check, for every expected total up to 40
        var acceptable = 0L;
        var outside = 0L;
        string? first = null;
        Span<int> runs = stackalloc int[5];
        for (var expected = 1; expected <= 40; expected++)
        {
            var bounds = FinderPatternFinder.AxisRunBounds.From(expected);
            var limit = 7 * expected / 5 + 1;
            for (var r0 = 0; r0 <= limit; r0++)
                for (var r1 = 0; r0 + r1 <= limit; r1++)
                    for (var r2 = 0; r0 + r1 + r2 <= limit; r2++)
                        for (var r3 = 0; r0 + r1 + r2 + r3 <= limit; r3++)
                            for (var r4 = 0; r0 + r1 + r2 + r3 + r4 <= limit; r4++)
                            {
                                runs[0] = r0; runs[1] = r1; runs[2] = r2; runs[3] = r3; runs[4] = r4;
                                if (!CouldBeAccepted(expected, runs))
                                    continue;
                                acceptable++;
                                if (Violation(bounds, expected, runs) is { } violation)
                                {
                                    outside++;
                                    first ??= $"expected={expected}, runs=[{r0},{r1},{r2},{r3},{r4}]: {violation}";
                                }
                            }
        }

        await Assert.That(first).IsNull();
        await Assert.That(outside).IsEqualTo(0);
        await Assert.That(acceptable).IsGreaterThan(10_000);
    }

    [Test]
    public async Task EveryAcceptableCrossSection_IsInsideTheBounds_LargeTotalsAtTheEdges()
    {
        // Large totals cannot be enumerated, so the vectors are built where a bound could be wrong: around 1:1:3:1:1 at a module size, every run pushed to and past its tolerance
        var random = new Random(20260922);
        var acceptable = 0L;
        string? first = null;
        Span<int> runs = stackalloc int[5];
        Span<int> expectedTotals = stackalloc int[5];
        for (var trial = 0; trial < 4_000_000; trial++)
        {
            var module = random.Next(1, 400);
            for (var i = 0; i < 5; i++)
            {
                var nominal = i == 2 ? 3 * module : module;
                var reach = nominal / 2 + 3;
                runs[i] = Math.Max(0, nominal + random.Next(-reach, reach + 1));
            }
            var total = runs[0] + runs[1] + runs[2] + runs[3] + runs[4];
            // The expected total anywhere the total check can still pass, and both corners of that window, where a bound is
            // one step away from a total it must not refuse: the largest expected total accepting this one and the smallest
            expectedTotals[0] = Math.Max(1, (int)(total * (0.68 + 0.80 * random.NextDouble())));
            expectedTotals[1] = 5 * total / 3;
            expectedTotals[2] = 5 * total / 3 - 1;
            expectedTotals[3] = 5 * total / 7 + 1;
            expectedTotals[4] = 5 * total / 7 + 2;
            foreach (var expected in expectedTotals)
            {
                if (expected < 1 || !CouldBeAccepted(expected, runs))
                    continue;
                acceptable++;
                if (Violation(FinderPatternFinder.AxisRunBounds.From(expected), expected, runs) is { } violation)
                    first ??= $"expected={expected}, runs=[{runs[0]},{runs[1]},{runs[2]},{runs[3]},{runs[4]}]: {violation}";
            }
        }

        await Assert.That(first).IsNull();
        await Assert.That(acceptable).IsGreaterThan(100_000);
    }

    [Test]
    public async Task EveryAcceptableCentre_IsInsideTheBounds_AcrossTheWholeTotalWindow()
    {
        // The vectors above are drawn around a true 1:1:3:1:1, which leaves the corners of the total window unvisited: a centre run at
        // its floor beside near-module side runs is acceptable there, and a bound one step too tight would refuse it and lose the candidate.
        // Every (expected, total, centre) of the window is walked here instead, with the side runs spread evenly and pushed to one side,
        // and every witness the verdict accepts has to lie inside the bounds.
        var acceptable = 0L;
        string? first = null;
        Span<int> runs = stackalloc int[5];
        for (var expected = 1; expected <= 200; expected++)
        {
            var bounds = FinderPatternFinder.AxisRunBounds.From(expected);
            for (var total = 3 * expected / 5; total <= 7 * expected / 5 + 1; total++)
            {
                for (var centre = 1; centre + 4 <= total; centre++)
                {
                    var sides = total - centre;
                    for (var skew = 0; skew < 2; skew++)
                    {
                        // evenly, then as short as the first three runs can be with the last one taking the rest
                        var least = skew == 0 ? sides / 4 : Math.Max(1, sides / 4 - 2);
                        runs[0] = runs[1] = runs[3] = least;
                        runs[4] = sides - 3 * least;
                        if (skew == 0)
                        {
                            for (var i = 0; i < sides % 4; i++)
                                runs[i >= 2 ? i + 1 : i]++;
                            runs[4] = sides - runs[0] - runs[1] - runs[3];
                        }
                        runs[2] = centre;
                        if (runs[4] < 1 || !CouldBeAccepted(expected, runs))
                            continue;
                        acceptable++;
                        if (Violation(bounds, expected, runs) is { } violation)
                            first ??= $"expected={expected}, total={total}, runs=[{runs[0]},{runs[1]},{runs[2]},{runs[3]},{runs[4]}]: {violation}";
                    }
                }
            }
        }

        await Assert.That(first).IsNull();
        await Assert.That(acceptable).IsGreaterThan(10_000);
    }

    [Test]
    public async Task Bounds_AreNotVacuous()
    {
        // A bound that never refuses holds trivially. For a 3 px a module window (21 px), a centre of one module leaves no room for a side run of one module, and a side run of three modules is past every centre.
        var bounds = FinderPatternFinder.AxisRunBounds.From(21);
        await Assert.That(bounds.CentreLow).IsGreaterThan(1);
        await Assert.That(bounds.CentreHigh).IsLessThan(21);
        await Assert.That(bounds.SideCap(3)).IsLessThan(3);
        await Assert.That(bounds.SideCap(9)).IsLessThan(9);
        await Assert.That(bounds.TotalCap(9)).IsLessThanOrEqualTo(29);

        // Below the smallest total any ratio check accepts there is nothing to walk for
        await Assert.That(FinderPatternFinder.AxisRunBounds.From(4).CentreHigh).IsLessThan(FinderPatternFinder.AxisRunBounds.From(4).CentreLow);
    }

    [Test]
    public async Task SideCap_NeverExceedsTheExpectedTotal()
    {
        // The reference walk caps a side run at the expected total. A bound above that would let the two walks stop a run at different lengths without either refusing.
        for (var expected = 1; expected <= 5000; expected++)
        {
            var bounds = FinderPatternFinder.AxisRunBounds.From(expected);
            for (var centre = bounds.CentreLow; centre <= bounds.CentreHigh; centre++)
            {
                if (bounds.SideCap(centre) > expected)
                {
                    await Assert.That(bounds.SideCap(centre)).IsLessThanOrEqualTo(expected).Because($"expected={expected}, centre={centre}");
                }
            }
        }
    }

    /// <summary>The verdict of an axis cross-check without its grey second look: the total within 40 % of the expected one, and the strict ratio or the near-miss one with four non-empty side runs.</summary>
    internal static bool CouldBeAccepted(int expected, ReadOnlySpan<int> runs)
    {
        var total = runs[0] + runs[1] + runs[2] + runs[3] + runs[4];
        if (5 * Math.Abs(total - expected) >= 2 * expected)
            return false;
        return FinderPatternFinder.IsSmallCrispFinderRuns(runs[0], runs[1], runs[2], runs[3], runs[4])
            || FinderPatternFinder.IsFinderRatio(runs)
            || (runs[0] != 0 && runs[1] != 0 && runs[3] != 0 && runs[4] != 0 && FinderPatternFinder.IsNearFinderRatio(runs[0], runs[1], runs[2], runs[3], runs[4]));
    }

    private static string? Violation(FinderPatternFinder.AxisRunBounds bounds, int expected, ReadOnlySpan<int> runs)
    {
        var centre = runs[2];
        if (centre < bounds.CentreLow || centre > bounds.CentreHigh)
            return $"centre {centre} outside [{bounds.CentreLow}, {bounds.CentreHigh}]";
        var total = runs[0] + runs[1] + runs[2] + runs[3] + runs[4];
        if (total > bounds.TotalCap(centre))
            return $"total {total} above {bounds.TotalCap(centre)}";
        foreach (var side in new[] { runs[0], runs[1], runs[3], runs[4] })
        {
            if (side < 1)
                return "an empty side run";
            if (side > bounds.SideCap(centre))
                return $"side run {side} above {bounds.SideCap(centre)} for centre {centre}";
        }
        return null;
    }
}
