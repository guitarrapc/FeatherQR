namespace FeatherQR.Tests;

/// <summary>
/// The renderer-neutral geometry surface (<c>GetModuleRectangles</c> family) across all
/// three symbologies. The contract is three invariants — rectangles are disjoint, cover
/// only dark modules, and cover every dark module — verified with a witness grid against
/// the public indexer. Decomposition shape and ordering are deliberately not asserted:
/// they are documented as unspecified so the merge algorithm can change.
/// </summary>
public class ModuleRectanglesTest
{
    // ---- invariant harness --------------------------------------------------------

    /// <summary>
    /// Marks every module covered by <paramref name="rects"/> in a witness grid,
    /// failing on out-of-bounds, non-positive extents, or double coverage, then
    /// compares the witness grid to the dark modules reported by <paramref name="isDark"/>.
    /// </summary>
    private static async Task AssertCoversExactlyTheDarkModules(IReadOnlyList<ModuleRect> rects, int width, int height, Func<int, int, bool> isDark)
    {
        var witness = new bool[height, width];
        foreach (var rect in rects)
        {
            await Assert.That(rect.Width).IsGreaterThan(0);
            await Assert.That(rect.Height).IsGreaterThan(0);
            await Assert.That(rect.X).IsGreaterThanOrEqualTo(0);
            await Assert.That(rect.Y).IsGreaterThanOrEqualTo(0);
            await Assert.That(rect.X + rect.Width).IsLessThanOrEqualTo(width);
            await Assert.That(rect.Y + rect.Height).IsLessThanOrEqualTo(height);

            for (var y = rect.Y; y < rect.Y + rect.Height; y++)
            {
                for (var x = rect.X; x < rect.X + rect.Width; x++)
                {
                    if (witness[y, x])
                        Assert.Fail($"Module ({x}, {y}) is covered by more than one rectangle.");
                    witness[y, x] = true;
                }
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (witness[y, x] != isDark(x, y))
                    Assert.Fail($"Module ({x}, {y}): rectangles say {witness[y, x]}, indexer says {isDark(x, y)}.");
            }
        }
    }

    // ---- Standard QR --------------------------------------------------------------

    [Test]
    [Arguments("https://example.com/", QREccLevel.M)]
    [Arguments("HELLO WORLD 123", QREccLevel.L)]
    [Arguments("0123456789", QREccLevel.H)]
    public async Task StandardQr_Rectangles_CoverExactlyTheDarkModules(string text, QREccLevel ecc)
    {
        var data = QRCodeGenerator.Create(text, ecc);
        var rects = data.GetModuleRectangles();
        await AssertCoversExactlyTheDarkModules(rects, data.Size, data.Size, (x, y) => data[y, x]);
    }

    [Test]
    public async Task StandardQr_Rectangles_AreMerged()
    {
        var data = QRCodeGenerator.Create("https://example.com/", QREccLevel.M);
        var rects = data.GetModuleRectangles();

        // Merging must actually reduce the element count below one-rect-per-module.
        var darkCount = 0;
        for (var row = 0; row < data.Size; row++)
            for (var col = 0; col < data.Size; col++)
                if (data[row, col])
                    darkCount++;
        await Assert.That(rects.Length).IsLessThan(darkCount);

        // Coordinates are quiet-zone-inclusive: the first dark module (the top-left
        // finder corner) sits at offset 4 with the default quiet zone, so no rectangle
        // starts before (4, 4) and some rectangle starts exactly there. Asserted via
        // the minimum coordinates so no particular decomposition shape is required.
        await Assert.That(rects.Min(r => r.X)).IsEqualTo(4);
        await Assert.That(rects.Min(r => r.Y)).IsEqualTo(4);
    }

    [Test]
    public async Task StandardQr_NoQuietZone_RectanglesStartAtOrigin()
    {
        var options = new QRCodeGeneratorOptions { QuietZoneSize = 0 };
        var data = QRCodeGenerator.Create("https://example.com/", QREccLevel.M, in options);
        var rects = data.GetModuleRectangles();

        // With no quiet zone the finder corner is the origin; shape-independent
        // minimum-coordinate check, same rationale as StandardQr_Rectangles_AreMerged.
        await Assert.That(rects.Min(r => r.X)).IsEqualTo(0);
        await Assert.That(rects.Min(r => r.Y)).IsEqualTo(0);
        await AssertCoversExactlyTheDarkModules(rects, data.Size, data.Size, (x, y) => data[y, x]);
    }

    // ---- Micro QR -----------------------------------------------------------------

    [Test]
    [Arguments("12345", MicroQREccLevel.ErrorDetectionOnly)] // M1: smallest matrix (11x11 core)
    [Arguments("HELLO", MicroQREccLevel.L)]
    public async Task MicroQr_Rectangles_CoverExactlyTheDarkModules(string text, MicroQREccLevel ecc)
    {
        var data = MicroQRCodeGenerator.Create(text, ecc);
        var rects = data.GetModuleRectangles();
        await AssertCoversExactlyTheDarkModules(rects, data.Size, data.Size, (x, y) => data[y, x]);
    }

    // ---- rMQR (non-square) --------------------------------------------------------

    [Test]
    [Arguments("https://example.com/")]
    [Arguments("0123456789012345678901234567890123456789")]
    public async Task RmQr_Rectangles_CoverExactlyTheDarkModules(string text)
    {
        var data = RmQRCodeGenerator.Create(text, RmQREccLevel.M);
        var rects = data.GetModuleRectangles();
        await AssertCoversExactlyTheDarkModules(rects, data.Width, data.Height, (x, y) => data[y, x]);
    }

    // ---- max count ----------------------------------------------------------------

    [Test]
    public async Task MaxCount_BoundsTheActualCount_AllSymbologies()
    {
        var qr = QRCodeGenerator.Create("https://example.com/", QREccLevel.M);
        await Assert.That(qr.GetModuleRectangles().Length).IsLessThanOrEqualTo(qr.GetModuleRectanglesMaxCount());

        var micro = MicroQRCodeGenerator.Create("HELLO", MicroQREccLevel.L);
        await Assert.That(micro.GetModuleRectangles().Length).IsLessThanOrEqualTo(micro.GetModuleRectanglesMaxCount());

        var rm = RmQRCodeGenerator.Create("https://example.com/", RmQREccLevel.M);
        await Assert.That(rm.GetModuleRectangles().Length).IsLessThanOrEqualTo(rm.GetModuleRectanglesMaxCount());
    }

    [Test]
    public async Task MaxCount_IsIndependentOfQuietZone()
    {
        // The bound depends only on the core matrix; the quiet zone contributes no runs.
        var withQz = QRCodeGenerator.Create("https://example.com/", QREccLevel.M);
        var options = new QRCodeGeneratorOptions { QuietZoneSize = 0 };
        var withoutQz = QRCodeGenerator.Create("https://example.com/", QREccLevel.M, in options);
        await Assert.That(withQz.GetModuleRectanglesMaxCount()).IsEqualTo(withoutQz.GetModuleRectanglesMaxCount());
    }

    // ---- Try-pattern contract -----------------------------------------------------

    [Test]
    public async Task TryGetModuleRectangles_DestinationTooSmall_ReturnsFalse()
    {
        var data = QRCodeGenerator.Create("https://example.com/", QREccLevel.M);
        var expected = data.GetModuleRectangles();

        var tooSmall = new ModuleRect[expected.Length - 1];
        var ok = data.TryGetModuleRectangles(tooSmall, out var written);
        await Assert.That(ok).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task TryGetModuleRectangles_ExactSizeBuffer_MatchesAllocatingOverload()
    {
        var data = QRCodeGenerator.Create("https://example.com/", QREccLevel.M);
        var expected = data.GetModuleRectangles();

        var buffer = new ModuleRect[expected.Length];
        var ok = data.TryGetModuleRectangles(buffer, out var written);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(expected.Length);
        await Assert.That(buffer).IsEquivalentTo(expected);
    }

    [Test]
    public async Task TryGetModuleRectangles_MaxCountBuffer_AlwaysSucceeds_AllSymbologies()
    {
        var qr = QRCodeGenerator.Create("https://example.com/", QREccLevel.M);
        await Assert.That(qr.TryGetModuleRectangles(new ModuleRect[qr.GetModuleRectanglesMaxCount()], out _)).IsTrue();

        var micro = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.ErrorDetectionOnly);
        await Assert.That(micro.TryGetModuleRectangles(new ModuleRect[micro.GetModuleRectanglesMaxCount()], out _)).IsTrue();

        var rm = RmQRCodeGenerator.Create("https://example.com/", RmQREccLevel.M);
        await Assert.That(rm.TryGetModuleRectangles(new ModuleRect[rm.GetModuleRectanglesMaxCount()], out _)).IsTrue();
    }
}
