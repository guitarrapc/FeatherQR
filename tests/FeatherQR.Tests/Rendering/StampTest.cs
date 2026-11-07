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
