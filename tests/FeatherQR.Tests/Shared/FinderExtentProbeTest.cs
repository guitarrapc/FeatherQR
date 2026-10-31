using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The probe that says whether the symbol a finder belongs to extends past a length along the finder's axes (<see cref="FinderExtentProbe"/>).
/// No Micro QR symbol extends past 15 modules from its finder's separator. Every rMQR symbol does along its width, and every Standard QR code of version 3 and up along both axes.
/// Where the walk cannot tell, as with a shadow the threshold calls dark or the image's edge, it says no.
/// Versions 1 and 2 of Standard QR put the other finders' light separators within the length, 8.5 and 12.5 modules out, where the walk may end, so the probe promises nothing about them.
/// Nor does it promise anything about a symbol whose band holds a light column of its own data. Small Standard QR versions can hold one, and so can the 7-module rMQR heights, whose band is two timing rows and five of data.
/// </summary>
public class FinderExtentProbeTest
{
    /// <summary>The length the read of every symbology asks about: Micro QR's longest, 17 modules less the finder and its separator, and a margin.</summary>
    private const int NotMicroQR = 15;

    public sealed record Case(string Name, Func<bool[,]> Core, int QuietZone, float PixelsPerModule, float Degrees, float Keystone)
    {
        public override string ToString() => $"{Name} {PixelsPerModule:F1}px {Degrees}deg k{Keystone}";
    }

    private static readonly (float PixelsPerModule, float Degrees, float Keystone)[] Renders =
    [
        (4f, 0f, 0f), (4f, 15f, 0f), (4f, 45f, 0f), (4f, 90f, 0f), (4f, 30f, 0.06f), (2.5f, 0f, 0f), (2.5f, 20f, 0f), (6f, 75f, 0.04f),
    ];

    public static IEnumerable<Case> MicroQRCases()
    {
        (string Text, MicroQRVersion Version)[] symbols = [("12345", MicroQRVersion.M1), ("AB12", MicroQRVersion.M2), ("MICRO QR 3", MicroQRVersion.M3), ("MICRO QR CODE M4 TEST", MicroQRVersion.M4)];
        foreach (var (text, version) in symbols)
        {
            foreach (var (ppm, degrees, keystone) in Renders)
                yield return new Case(version.ToString(), () => MicroCore(text, version), 2, ppm, degrees, keystone);
        }
    }

    public static IEnumerable<Case> RmQRCases()
    {
        (string Text, RmQRVersion Version)[] symbols = [("RMQR 27", RmQRVersion.R13x27), ("RMQR 59", RmQRVersion.R9x59), ("RMQR AT ITS WIDEST, ONE HUNDRED AND THIRTY-NINE MODULES WIDE", RmQRVersion.R17x139)];
        foreach (var (text, version) in symbols)
        {
            foreach (var (ppm, degrees, keystone) in Renders)
                yield return new Case(version.ToString(), () => RmQRCore(text, version), 2, ppm, degrees, keystone);
        }
    }

    public static IEnumerable<Case> StandardQRCases()
    {
        (string Text, int Version)[] symbols = [("https://example.com/5", 5), ("STANDARD QR VERSION TEN HOLDS A LONGER TEXT THAN THE SMALL ONES, SO IT IS WRITTEN OUT HERE", 10)];
        foreach (var (text, version) in symbols)
        {
            foreach (var (ppm, degrees, keystone) in Renders)
                yield return new Case($"v{version}", () => StandardQRCore(text, version), 4, ppm, degrees, keystone);
        }
    }

    /// <summary>A Micro QR symbol, whatever its version, turn, keystone or density, does not extend past the length.</summary>
    [Test]
    [MethodDataSource(nameof(MicroQRCases))]
    public async Task MicroQR_DoesNotExtendPastTheLength(Case c)
    {
        var (extends, premise) = Probe(c.Core(), c.QuietZone, c.PixelsPerModule, c.Degrees, c.Keystone);
        await Assert.That(premise).IsNull();
        await Assert.That(extends).IsFalse();
    }

    /// <summary>An rMQR symbol extends past the length along its width, the shortest, 27 modules, included.</summary>
    [Test]
    [MethodDataSource(nameof(RmQRCases))]
    public async Task RmQR_ExtendsPastTheLength(Case c)
    {
        var (extends, premise) = Probe(c.Core(), c.QuietZone, c.PixelsPerModule, c.Degrees, c.Keystone);
        await Assert.That(premise).IsNull();
        await Assert.That(extends).IsTrue();
    }

    /// <summary>A Standard QR symbol of version 5 and up extends past the length from its finder: its other finders' separators lie 24.5 modules out at least, and its band of seven rows is dense.</summary>
    [Test]
    [MethodDataSource(nameof(StandardQRCases))]
    public async Task StandardQR_ExtendsPastTheLength(Case c)
    {
        var (extends, premise) = Probe(c.Core(), c.QuietZone, c.PixelsPerModule, c.Degrees, c.Keystone);
        await Assert.That(premise).IsNull();
        await Assert.That(extends).IsTrue();
    }

    /// <summary>A neighbour two modules past a Micro QR symbol's quiet zone does not lengthen it, and a Micro QR symbol two modules past a Standard QR code does not shorten that.</summary>
    [Test]
    [Arguments(0f)]
    [Arguments(30f)]
    public async Task NeighbourPastTheQuietZone_DoesNotChangeTheAnswer(float degrees)
    {
        var micro = MicroCore("MICRO QR 3", MicroQRVersion.M3);
        var qr = StandardQRCore("https://example.com/5", 5);
        // Micro QR, two light modules, Standard QR, on one row
        int microSize = micro.GetLength(0), qrSize = qr.GetLength(0), gap = 2;
        var columns = microSize + gap + qrSize;
        var rows = Math.Max(microSize, qrSize);
        bool IsDark(int row, int col) => col < microSize ? row < microSize && micro[row, col] : col >= microSize + gap && row < qrSize && qr[row, col - microSize - gap];
        var (luminance, width, height) = SupersampledRenderer.Render(IsDark, columns, rows, 4f, degrees, 0f);
        var truth = SupersampledGeometry.GridToPixel(columns, rows, 4f, degrees, 0f);

        truth.Transform(3.5f, 3.5f, out var microX, out var microY);
        var (microExtends, microPremise) = ProbeAt(luminance, width, height, microX, microY);
        await Assert.That(microPremise).IsNull();
        await Assert.That(microExtends).IsFalse();

        truth.Transform(microSize + gap + 3.5f, 3.5f, out var qrX, out var qrY);
        var (qrExtends, qrPremise) = ProbeAt(luminance, width, height, qrX, qrY);
        await Assert.That(qrPremise).IsNull();
        await Assert.That(qrExtends).IsTrue();
    }

    /// <summary>
    /// A shadow the global threshold calls dark over a Micro QR symbol's quiet zone and the paper beyond runs the walk on past the symbol.
    /// Two steps of it are almost all dark, which a symbol's modules never are, so the probe gives no answer rather than a wrong one.
    /// </summary>
    [Test]
    public async Task ShadowOverTheQuietZone_GivesNoAnswer()
    {
        var micro = MicroCore("MICRO QR 3", MicroQRVersion.M3);
        var size = micro.GetLength(0);
        // The symbol at the left of a wide strip, two light modules above, below and to its left
        int columns = size + 2 + 40, rows = size + 4;
        bool IsDark(int row, int col) => row >= 2 && row < 2 + size && col >= 2 && col < 2 + size && micro[row - 2, col - 2];
        var (luminance, width, height) = NearestNeighbourRenderer.Render(IsDark, columns, rows, 4f, 0f, 0f);
        // From the symbol's right edge on, its quiet zone included, the paper is a dark grey
        var edge = (2 + size) * 4;
        for (var y = 0; y < height; y++)
        {
            for (var x = edge; x < width; x++)
                luminance[y * width + x] = 40;
        }

        var threshold = Threshold(luminance, out _);
        // The premise: the shaded paper is dark under the global threshold, and the symbol's own light modules are not
        await Assert.That(luminance[(height / 2) * width + edge + 8] < threshold).IsTrue();
        await Assert.That(luminance[(2 + 3) * 4 * width + (2 + 7) * 4 + 2] < threshold).IsFalse();

        var (extends, premise) = ProbeAt(luminance, width, height, (2 + 3.5f) * 4f, (2 + 3.5f) * 4f);
        await Assert.That(premise).IsNull();
        await Assert.That(extends).IsFalse();
    }

    /// <summary>An rMQR symbol cut by the image's edge along its width gives no answer: the walk leaves the image inside the modules.</summary>
    [Test]
    public async Task SymbolCutByTheImageEdge_GivesNoAnswer()
    {
        var rmqr = RmQRCore("RMQR 27", RmQRVersion.R13x27);
        int rows = rmqr.GetLength(0), columns = rmqr.GetLength(1);
        var (full, width, height) = SupersampledRenderer.Render((r, c) => rmqr[r, c], columns, rows, 4f, 0f, 0f);
        var truth = SupersampledGeometry.GridToPixel(columns, rows, 4f, 0f, 0f);
        truth.Transform(3.5f, 3.5f, out var finderX, out var finderY);
        // The image ends ten modules past the finder, so the symbol's last ten are gone
        truth.Transform(17f, 0f, out var edge, out _);
        var kept = (int)edge;
        var cut = new byte[kept * height];
        for (var y = 0; y < height; y++)
            full.AsSpan(y * width, kept).CopyTo(cut.AsSpan(y * kept));

        var (extends, premise) = ProbeAt(cut, kept, height, finderX, finderY);
        await Assert.That(premise).IsNull();
        await Assert.That(extends).IsFalse();
    }

    /// <summary>Axes shorter than half a pixel a module cannot be walked: no answer.</summary>
    [Test]
    public async Task AxesBelowHalfAPixel_GiveNoAnswer()
    {
        var qr = StandardQRCore("QR V1", 1);
        var (luminance, width, height) = SupersampledRenderer.Render((r, c) => qr[r, c], qr.GetLength(0), qr.GetLength(0), 4f, 0f, 0f);
        var threshold = Threshold(luminance, out var grey);
        var image = new ImageView(luminance, width, height, threshold, grey);

        await Assert.That(FinderExtentProbe.ExtendsBeyond(image, (4 + 3.5f) * 4f, (4 + 3.5f) * 4f, new OrientationCandidate(0.3f, 0f, 0f, 0.3f, 0.3f, 0.3f), NotMicroQR)).IsFalse();
    }

    /// <summary>Renders the core with its quiet zone, finds its top-left finder and probes from there.</summary>
    private static (bool Extends, string? Premise) Probe(bool[,] core, int quietZone, float pixelsPerModule, float degrees, float keystone)
    {
        int rows = core.GetLength(0), columns = core.GetLength(1);
        int gridColumns = columns + 2 * quietZone, gridRows = rows + 2 * quietZone;
        bool IsDark(int row, int col) => row >= quietZone && row < quietZone + rows && col >= quietZone && col < quietZone + columns && core[row - quietZone, col - quietZone];
        var (luminance, width, height) = SupersampledRenderer.Render(IsDark, gridColumns, gridRows, pixelsPerModule, degrees, keystone);
        var truth = SupersampledGeometry.GridToPixel(gridColumns, gridRows, pixelsPerModule, degrees, keystone);
        truth.Transform(quietZone + 3.5f, quietZone + 3.5f, out var x, out var y);
        return ProbeAt(luminance, width, height, x, y);
    }

    /// <summary>The probe at the finder candidate nearest (x, y), on its first orientation candidate's axes. The premise names what was not found.</summary>
    private static (bool Extends, string? Premise) ProbeAt(byte[] luminance, int width, int height, float x, float y)
    {
        var threshold = Threshold(luminance, out var grey);
        var candidates = new FinderPattern[64];
        var count = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey);
        var best = -1;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var distance = (candidates[i].X - x) * (candidates[i].X - x) + (candidates[i].Y - y) * (candidates[i].Y - y);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        if (best < 0 || bestDistance > 4f * candidates[best].ModuleSize * candidates[best].ModuleSize)
            return (false, $"no finder candidate near ({x:F0}, {y:F0}) among {count}: {string.Join(", ", candidates.Take(count).Select(c => $"({c.X:F0}, {c.Y:F0}) m {c.ModuleSize:F1}"))}");

        var orientations = new OrientationCandidate[FinderAxisEstimator.MaxOrientationCandidates];
        var orientationCount = FinderAxisEstimator.FindOrientationCandidates(luminance, width, height, threshold, candidates[best], orientations);
        if (orientationCount == 0)
            return (false, "no orientation candidate");

        var image = new ImageView(luminance, width, height, threshold, grey);
        return (FinderExtentProbe.ExtendsBeyond(image, candidates[best].X, candidates[best].Y, orientations[0], NotMicroQR), null);
    }

    private static byte Threshold(byte[] luminance, out GreyLevels grey)
    {
        var histogram = new int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(luminance, histogram);
        return Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out grey);
    }

    private static bool[,] MicroCore(string text, MicroQRVersion version)
    {
        var data = MicroQRCodeGenerator.Create(text, version == MicroQRVersion.M1 ? MicroQREccLevel.ErrorDetectionOnly : MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 });
        return Matrix(data.Size, data.Size, (r, c) => data[r, c]);
    }

    private static bool[,] RmQRCore(string text, RmQRVersion version)
    {
        var data = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 });
        return Matrix(data.Height, data.Width, (r, c) => data[r, c]);
    }

    private static bool[,] StandardQRCore(string text, int version)
    {
        var data = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
        return Matrix(data.Size, data.Size, (r, c) => data[r, c]);
    }

    private static bool[,] Matrix(int rows, int columns, Func<int, int, bool> dark)
    {
        var matrix = new bool[rows, columns];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
                matrix[r, c] = dark(r, c);
        }
        return matrix;
    }
}
