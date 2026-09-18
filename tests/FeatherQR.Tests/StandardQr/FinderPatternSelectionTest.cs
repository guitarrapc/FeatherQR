using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The three-of-many finder selection behind every Standard QR image decode.
/// A real finder must not lose to a false candidate that the row scan confirmed, whatever
/// order the scan found them in and even when every candidate measures the same module size,
/// which is the ordinary case for a render at a whole number of pixels per module.
/// </summary>
public class FinderPatternSelectionTest
{
    private const string StructuredAppendSymbology = "StandardQrStructuredAppend";

    [Test]
    public async Task Select_FewerThanThree_ReturnsFalse()
    {
        FinderPattern[] candidates = [Fp(35, 35, 8, 8), Fp(179, 35, 8, 8)];

        await Assert.That(FinderPatternFinder.TrySelectBestThree(candidates, new FinderPattern[3])).IsFalse();
    }

    [Test]
    public async Task Select_ExactlyThree_ReturnsThemWhateverTheirShape()
    {
        // No new rejection: three candidates that form no right angle still come back.
        FinderPattern[] candidates = [Fp(35, 35, 8, 8), Fp(179, 35, 8, 8), Fp(179, 155, 8, 3)];

        var selected = Select(candidates);

        await Assert.That(selected).IsEquivalentTo(new[] { (35f, 35f), (179f, 35f), (179f, 155f) });
    }

    /// <summary>
    /// The F10 shape: all four candidates measure the same module size, the false one is
    /// confirmed on fewer rows, and the scan found it before the real bottom-left finder.
    /// </summary>
    [Test]
    public async Task Select_EqualModuleSizes_FalseCandidateScannedFirst_PicksTheRealTriple()
    {
        FinderPattern[] candidates = [Fp(60, 60, 8, 8), Fp(204, 60, 8, 8), Fp(204, 180, 8, 3), Fp(60, 204, 8, 8)];

        var selected = Select(candidates);

        await Assert.That(selected).IsEquivalentTo(new[] { (60f, 60f), (204f, 60f), (60f, 204f) });
    }

    [Test]
    public async Task Select_EveryScanOrder_PicksTheSameTriple()
    {
        FinderPattern[] candidates = [Fp(60, 60, 8, 8), Fp(204, 60, 8, 8), Fp(204, 180, 8, 3), Fp(60, 204, 8, 8)];

        foreach (var order in Permutations([0, 1, 2, 3]))
        {
            var selected = Select(order.Select(i => candidates[i]).ToArray());
            await Assert.That(selected).IsEquivalentTo(new[] { (60f, 60f), (204f, 60f), (60f, 204f) }).Because(string.Join(",", order));
        }
    }

    /// <summary>
    /// Four candidates on the corners of a square make four right isosceles triangles, so
    /// geometry and module size tie; the corner confirmed on fewer rows is the one left out.
    /// </summary>
    [Test]
    public async Task Select_GeometryAndSizeTie_PrefersTheMoreConfirmedTriple()
    {
        FinderPattern[] candidates = [Fp(204, 204, 8, 3), Fp(60, 60, 8, 8), Fp(204, 60, 8, 8), Fp(60, 204, 8, 8)];

        var selected = Select(candidates);

        await Assert.That(selected).IsEquivalentTo(new[] { (60f, 60f), (204f, 60f), (60f, 204f) });
    }

    /// <summary>
    /// A candidate that would close a perfect right angle but measures twice the module size
    /// does not beat a real triple whose geometry is slightly off (keystone).
    /// </summary>
    [Test]
    public async Task Select_PerfectGeometryButWrongModuleSize_LosesToTheRealTriple()
    {
        FinderPattern[] candidates = [Fp(60, 60, 8, 8), Fp(200, 64, 8, 8), Fp(60, 200, 16, 8), Fp(64, 196, 8, 8)];

        var selected = Select(candidates);

        await Assert.That(selected).IsEquivalentTo(new[] { (60f, 60f), (200f, 64f), (64f, 196f) });
    }

    /// <summary>
    /// A candidate seen on one row only is dropped before scoring when three or more are
    /// confirmed, even where it would close a better triangle than the real one.
    /// </summary>
    [Test]
    public async Task Select_UnconfirmedCandidateWithBetterGeometry_IsNotConsidered()
    {
        FinderPattern[] candidates = [Fp(60, 60, 8, 8), Fp(200, 64, 8, 8), Fp(60, 200, 8, 1), Fp(64, 196, 8, 8)];

        var selected = Select(candidates);

        await Assert.That(selected).IsEquivalentTo(new[] { (60f, 60f), (200f, 64f), (64f, 196f) });
    }

    [Test]
    public async Task Select_TwoSymbolsInTheFrame_DoesNotMixThem()
    {
        FinderPattern[] candidates =
        [
            Fp(40, 40, 5, 5), Fp(40, 400, 8, 8), Fp(160, 40, 5, 5), Fp(40, 160, 5, 5),
            Fp(250, 400, 8, 8), Fp(40, 610, 8, 8),
        ];

        var selected = Select(candidates);

        await Assert.That(selected).IsEquivalentTo(new[] { (40f, 40f), (160f, 40f), (40f, 160f) }).Or.IsEquivalentTo(new[] { (40f, 400f), (250f, 400f), (40f, 610f) });
    }

    [Test]
    public async Task Select_RotatedSymbolWithInteriorFalseCandidate_PicksTheRealTriple()
    {
        // 30 degree rotation of a 144 px finder triangle, false candidate inside the symbol.
        var c = MathF.Cos(MathF.PI / 6);
        var s = MathF.Sin(MathF.PI / 6);
        (float X, float Y) Rotate(float x, float y) => (300 + x * c - y * s, 100 + x * s + y * c);
        var tl = Rotate(0, 0);
        var tr = Rotate(144, 0);
        var bl = Rotate(0, 144);
        var fake = Rotate(144, 120);
        FinderPattern[] candidates = [Fp(tl.X, tl.Y, 8, 8), Fp(tr.X, tr.Y, 8, 8), Fp(fake.X, fake.Y, 8, 3), Fp(bl.X, bl.Y, 8, 8)];

        var selected = Select(candidates);

        await Assert.That(selected).IsEquivalentTo(new[] { tl, tr, bl });
    }

    /// <summary>
    /// The corpus symbol that exposed the defect (F10), rendered axis-aligned from its matrix
    /// at every density: from 5 px/module up its false candidate is confirmed and used to win.
    /// </summary>
    [Test]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(8)]
    [Arguments(11)]
    [Arguments(16)]
    public async Task Decode_AlignmentRingFalseFinderSymbol_ReadsAtEveryDensity(int pixelsPerModule)
    {
        const string fixtureId = "codeglyphx/sixteen-symbols-max-v2-l-2of16";
        var manifest = FixtureLoader.Load(StructuredAppendSymbology, fixtureId).Manifest;
        var (modules, size) = FixtureLoader.ReadMatrix(Path.Combine(FixtureLoader.FixtureRoot, StructuredAppendSymbology, fixtureId.Replace('/', Path.DirectorySeparatorChar) + ".matrix.txt"));
        var (luminance, side) = Render(modules, size, pixelsPerModule);

        var success = QRCodeDecoder.TryDecodeImage(luminance, side, side, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"ppm={pixelsPerModule}: {info.Status}, version {info.Version}");
        await Assert.That(text).IsEqualTo(manifest.PayloadText);
        await Assert.That(info.Version).IsEqualTo(manifest.Version);
    }

    /// <summary>
    /// Symbols from this library's own encoder that carry a confirmed false finder, found by a
    /// differential decode sweep: flat renders at 5 and 6 px/module that read one to two
    /// versions low before the selection scored geometry.
    /// </summary>
    [Test]
    [Arguments("05327189515657818903205615751649015094209316468065994690971143887664281460389915768574431402316652989488343421105547421", QREccLevel.M, 1, 6)]
    [Arguments("gnxab7SK?1wkLJY.6=B9&wtRBuz s7js&oV utJf:4P%&vSoyomCW_94/gZ2HdnKSDls9iRM8q?PhtSacLMEVgWC0l_OUc", QREccLevel.M, 3, 5)]
    public async Task Decode_FlatRenderWithConfirmedFalseFinder_Reads(string content, QREccLevel eccLevel, int mask, int pixelsPerModule)
    {
        var qr = QRCodeGenerator.Create(content, eccLevel, new QRCodeGeneratorOptions { MaskPattern = mask, QuietZoneSize = 0 });
        var (luminance, side) = Render(ToModules(qr), qr.Size, pixelsPerModule);

        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var found = FinderPatternFinder.FindCandidatesFullSweep(luminance, side, side, 128, candidates);
        await Assert.That(candidates.Take(found).Count(c => c.Count >= 2)).IsGreaterThan(3).Because("the symbol no longer carries a confirmed false finder: pick another from the sweep");

        var success = QRCodeDecoder.TryDecodeImage(luminance, side, side, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{info.Status}, version {info.Version}");
        await Assert.That(text).IsEqualTo(content);
    }

    /// <summary>
    /// Two symbols side by side: six candidates, and the selection must take all three from one
    /// symbol. Selecting by module size alone mixed them whenever both symbols were rendered at
    /// the same density, since every candidate then measured the same size; different
    /// densities (the 5/6 case) were already told apart.
    /// </summary>
    [Test]
    [Arguments(3, 3)]
    [Arguments(5, 5)]
    [Arguments(8, 8)]
    [Arguments(12, 12)]
    [Arguments(5, 6)]
    public async Task Decode_TwoSymbolsSideBySide_ReadsOneOfThem(int leftPixelsPerModule, int rightPixelsPerModule)
    {
        const string left = "LEFT SYMBOL 0123456789";
        const string right = "https://example.com/right";
        var leftQr = QRCodeGenerator.Create(left, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
        var rightQr = QRCodeGenerator.Create(right, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
        var (leftLuminance, leftSide) = Render(ToModules(leftQr), leftQr.Size, leftPixelsPerModule);
        var (rightLuminance, rightSide) = Render(ToModules(rightQr), rightQr.Size, rightPixelsPerModule);
        var width = leftSide + rightSide;
        var height = Math.Max(leftSide, rightSide);
        var luminance = new byte[width * height];
        Array.Fill(luminance, (byte)255);
        for (var y = 0; y < leftSide; y++)
            leftLuminance.AsSpan(y * leftSide, leftSide).CopyTo(luminance.AsSpan(y * width));
        for (var y = 0; y < rightSide; y++)
            rightLuminance.AsSpan(y * rightSide, rightSide).CopyTo(luminance.AsSpan(y * width + leftSide));

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{info.Status}, version {info.Version}");
        await Assert.That(text == left || text == right).IsTrue().Because(text);
    }

    private static byte[] ToModules(QRCodeData qr)
    {
        var modules = new byte[qr.Size * qr.Size];
        for (var row = 0; row < qr.Size; row++)
        {
            for (var col = 0; col < qr.Size; col++)
                modules[row * qr.Size + col] = qr[row, col] ? (byte)1 : (byte)0;
        }
        return modules;
    }

    private static FinderPattern Fp(float x, float y, float moduleSize, int count) => new() { X = x, Y = y, ModuleSize = moduleSize, Count = count };

    private static (float X, float Y)[] Select(FinderPattern[] candidates)
    {
        var patterns = new FinderPattern[3];
        if (!FinderPatternFinder.TrySelectBestThree(candidates, patterns))
            throw new InvalidOperationException("no triple selected");
        return patterns.Select(p => (p.X, p.Y)).ToArray();
    }

    private static IEnumerable<int[]> Permutations(int[] items)
    {
        if (items.Length == 1)
        {
            yield return items;
            yield break;
        }
        for (var i = 0; i < items.Length; i++)
        {
            foreach (var rest in Permutations(items.Where((_, j) => j != i).ToArray()))
                yield return [items[i], .. rest];
        }
    }

    private static (byte[] Luminance, int Side) Render(byte[] modules, int size, int pixelsPerModule)
    {
        const int quietZone = 4;
        var side = (size + 2 * quietZone) * pixelsPerModule;
        var luminance = new byte[side * side];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var row = y / pixelsPerModule - quietZone;
                var col = x / pixelsPerModule - quietZone;
                var dark = row >= 0 && col >= 0 && row < size && col < size && modules[row * size + col] != 0;
                luminance[y * side + x] = dark ? (byte)0 : (byte)255;
            }
        }
        return (luminance, side);
    }
}
