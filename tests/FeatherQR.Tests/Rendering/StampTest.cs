using System.Buffers;
using FeatherQR.Internals.ImageWriters;

namespace FeatherQR.Tests;

/// <summary>
/// The coverage sampler on its own: the shapes it draws at sizes the image tests cannot afford, and the radius it clamps.
/// </summary>
public class StampTest
{
    /// <summary>
    /// A circle finder pattern's box of 3,000 pixels: the product of the ellipse's axes passes the exact range there, so the test runs at a
    /// coarser unit, and the box's corners still have to stay light and the ring and the centre keep their share, 8.25 π over 49 of the box.
    /// </summary>
    [Test]
    public async Task CircleFinder_AtABoxOfThreeThousandPixels_KeepsItsShape()
    {
        const int box = 3000;
        var options = new SymbolImageOptions { FinderPatternStyle = FinderPatternStyle.Circle };
        var levels = new byte[box * box];
        Stamp.Finder(levels, box, box, in options, 16);

        await Assert.That(levels[0]).IsEqualTo((byte)0);
        await Assert.That(levels[box - 1]).IsEqualTo((byte)0);
        await Assert.That(levels[(box - 1) * box]).IsEqualTo((byte)0);
        await Assert.That(levels[box * box - 1]).IsEqualTo((byte)0);
        await Assert.That(levels[(box / 2) * box + box / 2]).IsEqualTo((byte)15).Because("the centre");

        long dark = 0;
        foreach (var level in levels)
            dark += level;
        var share = dark / 15.0 / levels.Length;
        await Assert.That(Math.Abs(share - 8.25 * Math.PI / 49)).IsLessThan(0.002).Because($"the ring and the centre cover {share:F4} of the box");
    }

    /// <summary>
    /// A box of 11,300 pixels takes the ellipse test to a unit where a sample's offset from the centre is odd, so the row has to read the
    /// same from both ends: a halving that rounds toward one side would give a pixel and its mirror different levels.
    /// </summary>
    [Test]
    public async Task CircleFinder_PastThreeHalvings_ReadsTheSameFromBothEnds()
    {
        const int box = 11_300;
        var options = new SymbolImageOptions { FinderPatternStyle = FinderPatternStyle.Circle };
        var row = new byte[box];
        Stamp.FinderRow(row, box, box, box / 2, in options, 16);

        var differing = 0;
        for (var x = 0; x < box / 2; x++)
        {
            if (row[x] != row[box - 1 - x])
                differing++;
        }
        await Assert.That(differing).IsEqualTo(0);
        await Assert.That(row[0]).IsEqualTo((byte)15).Because("the centre row crosses the ring at the box's edge");
    }

    /// <summary>
    /// The stamp's levels are each pixel's share of samples inside the shape. Testing every sample against the regions, the plain
    /// definition, gives the same levels for every module and finder pattern shape over a seeded spread of sizes, scales and radii,
    /// square and a pixel off, and for rows of cells and boxes large enough to take the ellipse test to a coarser unit.
    /// </summary>
    [Test]
    public async Task Levels_AreTheSampleBySampleCount()
    {
        var random = new Random(20261108);
        var mismatches = new List<string>();
        for (var k = 0; k < 600; k++)
        {
            var width = random.Next(1, 49);
            var height = random.Next(3) == 0 ? Math.Max(1, width + random.Next(-1, 2)) : width;
            var options = new SymbolImageOptions { ModuleStyle = (ModuleStyle)random.Next(3), ModuleScalePercent = random.Next(50, 101), CornerRadiusPercent = random.Next(0, 101) };
            var actual = new byte[width * height];
            Stamp.Module(actual, width, height, in options, 16);
            Stamp.ModuleRegion(width, height, in options, out var outer);
            if (!actual.AsSpan().SequenceEqual(Reference(width, 0, height, outer, default, default)))
                mismatches.Add($"module {options.ModuleStyle} {width} × {height} at {options.ModuleScalePercent} %, radius {options.CornerRadiusPercent}");
        }
        for (var k = 0; k < 160; k++)
        {
            var width = random.Next(7, 121);
            var height = random.Next(3) == 0 ? width + random.Next(-1, 2) : width;
            var options = new SymbolImageOptions { FinderPatternStyle = (FinderPatternStyle)random.Next(1, 4), CornerRadiusPercent = random.Next(0, 101) };
            var actual = new byte[width * height];
            Stamp.Finder(actual, width, height, in options, 16);
            Stamp.FinderRegions(width, height, in options, out var outer, out var hole, out var centre);
            if (!actual.AsSpan().SequenceEqual(Reference(width, 0, height, outer, hole, centre)))
                mismatches.Add($"finder {options.FinderPatternStyle} {width} × {height}, radius {options.CornerRadiusPercent}");
        }
        // From 256 pixels a row the counts are rented. At a power of two a rent one too small comes back exactly that size, so the slice to the
        // counts throws. The pool hands an array back as its last renter left it, so one is returned dirty first for the next rent to take.
        foreach (var width in new[] { 256, 512 })
        {
            var dirty = ArrayPool<int>.Shared.Rent(width + 1);
            dirty.AsSpan().Fill(1000);
            ArrayPool<int>.Shared.Return(dirty);
            var module = new SymbolImageOptions { ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 92 };
            var cell = new byte[width];
            Stamp.ModuleRow(cell, width, width, width / 3, in module, 16);
            Stamp.ModuleRegion(width, width, in module, out var outer);
            if (!cell.AsSpan().SequenceEqual(Reference(width, width / 3, width / 3 + 1, outer, default, default)))
                mismatches.Add($"module row of a {width} px cell");
            var finder = new SymbolImageOptions { FinderPatternStyle = FinderPatternStyle.RoundedSquareCircle };
            var row = new byte[width];
            Stamp.FinderRow(row, width, width, width / 2, in finder, 16);
            Stamp.FinderRegions(width, width, in finder, out outer, out var hole, out var centre);
            if (!row.AsSpan().SequenceEqual(Reference(width, width / 2, width / 2 + 1, outer, hole, centre)))
                mismatches.Add($"finder row of a {width} px box");
        }
        foreach (var box in new[] { 3000, 11_300 })
        {
            foreach (var style in new[] { FinderPatternStyle.Circle, FinderPatternStyle.RoundedSquare, FinderPatternStyle.RoundedSquareCircle })
            {
                var options = new SymbolImageOptions { FinderPatternStyle = style };
                Stamp.FinderRegions(box, box, in options, out var outer, out var hole, out var centre);
                foreach (var y in new[] { 0, box / 7, box / 2, box - 1 })
                {
                    var row = new byte[box];
                    Stamp.FinderRow(row, box, box, y, in options, 16);
                    if (!row.AsSpan().SequenceEqual(Reference(box, y, y + 1, outer, hole, centre)))
                        mismatches.Add($"finder {style} {box} px, row {y}");
                }
            }
        }
        // At these sizes the ellipse test's coarser unit leaves a sample row within a circle's rows with no sample inside it. The run carried
        // down from the row above has to come back empty there.
        var circles = new SymbolImageOptions { ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 99, FinderPatternStyle = FinderPatternStyle.Circle };
        {
            var row = new byte[6013];
            Stamp.ModuleRow(row, 6013, 6013, 5982, in circles, 16);
            Stamp.ModuleRegion(6013, 6013, in circles, out var outer);
            if (!row.AsSpan().SequenceEqual(Reference(6013, 5982, 5983, outer, default, default)))
                mismatches.Add("module row 5,982 of a 6,013 px circle");
        }
        foreach (var (box, y) in new[] { (7893, 6765), (11_302, 9687) })
        {
            var row = new byte[box];
            Stamp.FinderRow(row, box, box, y, in circles, 16);
            Stamp.FinderRegions(box, box, in circles, out var outer, out var hole, out var centre);
            if (!row.AsSpan().SequenceEqual(Reference(box, y, y + 1, outer, hole, centre)))
                mismatches.Add($"finder Circle {box} px, row {y}");
        }

        await Assert.That(mismatches).IsEmpty();
    }

    // Every sample of every pixel tested on its own, the way the levels are defined.
    private static byte[] Reference(int width, int fromRow, int toRow, in Stamp.Region outer, in Stamp.Region hole, in Stamp.Region centre)
    {
        const int s = Stamp.SamplesPerSide;
        var levels = new byte[width * (toRow - fromRow)];
        var at = 0;
        for (var py = fromRow; py < toRow; py++)
        {
            for (var px = 0; px < width; px++)
            {
                var count = 0;
                for (var j = 0; j < s; j++)
                {
                    long y = 4L * (py * s + j) + 2;
                    for (var i = 0; i < s; i++)
                    {
                        long x = 4L * (px * s + i) + 2;
                        if (centre.Contains(x, y) || (outer.Contains(x, y) && !hole.Contains(x, y)))
                            count++;
                    }
                }
                levels[at++] = (byte)((count * 15 + s * s / 2) / (s * s));
            }
        }
        return levels;
    }

    /// <summary>A rounded square whose corner radius reaches or passes half its side is the circle the SVG draws for it.</summary>
    [Test]
    [Arguments(50)]
    [Arguments(80)]
    [Arguments(100)]
    public async Task RoundedSquare_AtHalfTheSideOrMore_IsTheCircle(int radius)
    {
        var rounded = new SymbolImageOptions { ModuleStyle = ModuleStyle.RoundedSquare, CornerRadiusPercent = radius, ModuleScalePercent = 90 };
        var circle = rounded with { ModuleStyle = ModuleStyle.Circle };
        var a = new byte[40 * 40];
        var b = new byte[40 * 40];
        Stamp.Module(a, 40, 40, in rounded, 16);
        Stamp.Module(b, 40, 40, in circle, 16);

        await Assert.That(a).IsEquivalentTo(b, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
