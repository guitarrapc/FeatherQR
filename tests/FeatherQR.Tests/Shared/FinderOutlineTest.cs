using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// A finder's own frame from the outline of its light ring, against the frame it was drawn with, and the cases it must refuse.
/// The finders are an rMQR symbol's, whose finder a keystone along the symbol's short axis shears up to 40° at the near end.
/// </summary>
public class FinderOutlineTest
{
    private const string Content = "RMQR 43";

    /// <summary>Square to 40° of shear, 2.5 to 5 px/module; the seed a square frame turned up to 15° off the finder's rows (measured 2026-09-28: every such seed measures these).</summary>
    public static IEnumerable<(RmQRVersion Version, float Keystone, float PixelsPerModule, float SeedDegrees)> Finders()
    {
        foreach (var (version, keystone) in new[] { (RmQRVersion.R11x43, 0f), (RmQRVersion.R11x43, 0.2f), (RmQRVersion.R7x99, 0.12f), (RmQRVersion.R17x139, 0.2f), (RmQRVersion.R7x139, 0.2f) })
        {
            foreach (var pixelsPerModule in new[] { 2.5f, 3.5f, 5f })
            {
                foreach (var seedDegrees in new[] { -15f, 0f, 15f })
                    yield return (version, keystone, pixelsPerModule, seedDegrees);
            }
        }
    }

    /// <summary>Measured and refined, the frame is the drawn one: its axes within 2°, their lengths within 5 %, the centre within a fifth of a module.</summary>
    [Test]
    [MethodDataSource(nameof(Finders))]
    public async Task TryMeasure_ThenRefine_MatchesTheDrawnFrame(RmQRVersion version, float keystone, float pixelsPerModule, float seedDegrees)
    {
        var (luminance, side, drawn, level) = Render(version, keystone, pixelsPerModule, 23f);
        Seed(drawn, seedDegrees, out var seedX, out var seedY, out var uX, out var uY, out var vX, out var vY);

        await Assert.That(FinderOutline.TryMeasure(luminance, side, side, level, seedX, seedY, uX, uY, vX, vY, out var outline)).IsTrue();
        await Assert.That(outline.TryRefine(luminance, side, side, level, out var refined)).IsTrue();

        Frame(drawn, out var centerX, out var centerY, out var trueUX, out var trueUY, out var trueVX, out var trueVY);
        var moduleLength = Length(trueUX, trueUY);
        await Assert.That(Angle(refined.UX, refined.UY, trueUX, trueUY)).IsLessThan(2f);
        await Assert.That(Angle(refined.VX, refined.VY, trueVX, trueVY)).IsLessThan(2f);
        await Assert.That(Math.Abs(Length(refined.UX, refined.UY) / moduleLength - 1f)).IsLessThan(0.05f);
        await Assert.That(Math.Abs(Length(refined.VX, refined.VY) / Length(trueVX, trueVY) - 1f)).IsLessThan(0.05f);
        await Assert.That(Length(refined.CenterX - centerX, refined.CenterY - centerY) / moduleLength).IsLessThan(0.2f);
    }

    /// <summary>The refined shear is the drawn one to 2°, a square finder's and a sheared one's alike.</summary>
    [Test]
    [Arguments(RmQRVersion.R11x43, 0f, 0f, 1f)]
    [Arguments(RmQRVersion.R7x99, 0.12f, 19f, 22f)]
    [Arguments(RmQRVersion.R7x139, 0.2f, 40f, 43f)]
    public async Task TryRefine_Sheared_KeepsTheDrawnShear(RmQRVersion version, float keystone, float drawnAtLeast, float drawnAtMost)
    {
        var (luminance, side, drawn, level) = Render(version, keystone, 3.5f, 23f);
        Seed(drawn, 0f, out var seedX, out var seedY, out var uX, out var uY, out var vX, out var vY);
        Frame(drawn, out _, out _, out var trueUX, out var trueUY, out var trueVX, out var trueVY);
        var drawnShear = Math.Abs(90f - Angle(trueUX, trueUY, trueVX, trueVY));
        await Assert.That(drawnShear).IsBetween(drawnAtLeast, drawnAtMost).Because("the drawn shear");

        await Assert.That(FinderOutline.TryMeasure(luminance, side, side, level, seedX, seedY, uX, uY, vX, vY, out var outline)).IsTrue();
        await Assert.That(outline.TryRefine(luminance, side, side, level, out var refined)).IsTrue();

        var refinedShear = Math.Abs(90f - Angle(refined.UX, refined.UY, refined.VX, refined.VY));
        await Assert.That(Math.Abs(refinedShear - drawnShear)).IsLessThan(2f);
    }

    /// <summary>A seed in the light ring, two modules off the centre, where the chord through it meets the centre square on one side and the dark ring on the other.</summary>
    [Test]
    public async Task TryMeasure_SeedInTheLightRing_Refused()
    {
        var (luminance, side, drawn, level) = Render(RmQRVersion.R11x43, 0f, 4f, 23f);
        Seed(drawn, 0f, out var seedX, out var seedY, out var uX, out var uY, out var vX, out var vY);

        await Assert.That(FinderOutline.TryMeasure(luminance, side, side, level, seedX, seedY, uX, uY, vX, vY, out _)).IsTrue().Because("the seed at the centre measures");
        await Assert.That(FinderOutline.TryMeasure(luminance, side, side, level, seedX + 2f * uX, seedY + 2f * uY, uX, uY, vX, vY, out _)).IsFalse();
    }

    /// <summary>
    /// A 5 × 5 pattern of rings, the rMQR sub-finder or an alignment pattern, seeded with its own module: its dark ring's inner edge lies 1.5 modules out, short of the 1.8 a finder's centre chord reaches, where the rest of the measurement would take it for a finder three-fifths its size.
    /// </summary>
    [Test]
    [Arguments(0f, 3f)]
    [Arguments(0f, 6f)]
    [Arguments(20f, 4f)]
    [Arguments(45f, 3f)]
    public async Task TryMeasure_FiveModuleRings_Refused(float degrees, float pixelsPerModule)
    {
        static bool IsDark(int row, int column)
        {
            var ring = Math.Max(Math.Abs(row - 4), Math.Abs(column - 4));
            return ring is 0 or 2;
        }
        var (luminance, side, _) = SupersampledRenderer.Render(IsDark, 9, 9, pixelsPerModule, degrees, 0f);
        var drawn = SupersampledGeometry.GridToPixel(9, 9, pixelsPerModule, degrees, 0f);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        drawn.Transform(4.5f, 4.5f, out var centerX, out var centerY);
        drawn.Transform(5.5f, 4.5f, out var rowX, out var rowY);
        drawn.Transform(4.5f, 5.5f, out var columnX, out var columnY);

        await Assert.That(FinderOutline.TryMeasure(luminance, side, side, grey.EdgeLevel(threshold), centerX, centerY, rowX - centerX, rowY - centerY, columnX - centerX, columnY - centerY, out _)).IsFalse();
    }

    /// <summary>The rMQR symbol's own sub-finder, seeded at its centre with the finder's frame.</summary>
    [Test]
    public async Task TryMeasure_SubFinder_Refused()
    {
        var (luminance, side, drawn, level) = Render(RmQRVersion.R11x43, 0f, 4f, 0f);
        Frame(drawn, out _, out _, out var uX, out var uY, out var vX, out var vY);
        drawn.Transform(43f - 2.5f, 11f - 2.5f, out var subX, out var subY);

        await Assert.That(FinderOutline.TryMeasure(luminance, side, side, level, subX, subY, uX, uY, vX, vY, out _)).IsFalse();
    }

    /// <summary>A square with no light ring has no inner edge.</summary>
    [Test]
    public async Task TryMeasure_SolidSquare_Refused()
    {
        var (luminance, side, drawn, level) = Render(RmQRVersion.R11x43, 0f, 4f, 23f, (row, column) => row <= 6 && column <= 6);
        Seed(drawn, 0f, out var seedX, out var seedY, out var uX, out var uY, out var vX, out var vY);

        await Assert.That(FinderOutline.TryMeasure(luminance, side, side, level, seedX, seedY, uX, uY, vX, vY, out _)).IsFalse();
    }

    /// <summary>
    /// A seed off the centre along the chord: the chord's two reaches differ by twice the offset, and past half a module off (a module between them) the seed is refused as not the finder's centre.
    /// A quarter of a module off it measures; from 0.4 the outer chords of the second set run along the centre square's edge, whose grey pixels cross the level.
    /// </summary>
    [Test]
    [Arguments(0.25f, true)]
    [Arguments(0.6f, false)]
    public async Task TryMeasure_SeedOffTheCentre_RefusedPastHalfAModule(float offsetModules, bool measures)
    {
        var (luminance, side, drawn, level) = Render(RmQRVersion.R11x43, 0f, 5f, 0f);
        Frame(drawn, out var centerX, out var centerY, out var uX, out var uY, out var vX, out var vY);

        var measured = FinderOutline.TryMeasure(luminance, side, side, level, centerX + offsetModules * uX, centerY + offsetModules * uY, uX, uY, vX, vY, out _);

        await Assert.That(measured).IsEqualTo(measures);
    }

    /// <summary>
    /// A round bullseye the size of a finder (a disc of 3.5 modules, a light ring to 2.5, a dark centre of 1.5): chords along its own axes, a module apart, meet an inner edge that is a circle, whose points lie off any pair of lines by more than a quarter of a module.
    /// </summary>
    [Test]
    public async Task TryRefine_RoundBullseye_Refused()
    {
        const int Cells = 10;
        const float PixelsPerModule = 4f;
        static bool IsDark(int row, int column)
        {
            var dy = (row + 0.5f) / Cells - 7.5f;
            var dx = (column + 0.5f) / Cells - 7.5f;
            var r = MathF.Sqrt(dx * dx + dy * dy);
            return r <= 1.5f || (r >= 2.5f && r <= 3.5f);
        }
        var (luminance, side, _) = SupersampledRenderer.Render(IsDark, 15 * Cells, 15 * Cells, PixelsPerModule / Cells, 0f, 0f);
        Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        await Assert.That(grey.IsEnabled).IsTrue().Because("the bullseye is drawn with grey edges");
        var center = side / 2f;

        var measured = FinderOutline.TryMeasure(luminance, side, side, grey.Midpoint, center, center, PixelsPerModule, 0f, 0f, PixelsPerModule, out var outline);

        await Assert.That(measured && outline.TryRefine(luminance, side, side, grey.Midpoint, out _)).IsFalse();
    }

    /// <summary>Two-level noise with finder-sized cells: no seed gives an outline.</summary>
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task TryMeasure_Noise_Refused(int cell)
    {
        const int Side = 192;
        var random = new Random(cell * 97 + 1);
        var luminance = new byte[Side * Side];
        for (var y = 0; y < Side; y += cell)
        {
            for (var x = 0; x < Side; x += cell)
            {
                var value = (byte)(random.Next(100) < 45 ? 0 : 255);
                for (var dy = 0; dy < cell && y + dy < Side; dy++)
                    for (var dx = 0; dx < cell && x + dx < Side; dx++)
                        luminance[(y + dy) * Side + x + dx] = value;
            }
        }

        var outlines = 0;
        for (var i = 0; i < 400; i++)
        {
            var x = 20f + (float)random.NextDouble() * (Side - 40);
            var y = 20f + (float)random.NextDouble() * (Side - 40);
            var radians = (float)(random.NextDouble() * Math.PI / 2);
            var module = cell * (1f + (float)random.NextDouble());
            var uX = MathF.Cos(radians) * module;
            var uY = MathF.Sin(radians) * module;
            if (FinderOutline.TryMeasure(luminance, Side, Side, 127.5f, x, y, uX, uY, -uY, uX, out _))
                outlines++;
        }

        await Assert.That(outlines).IsEqualTo(0);
    }

    private static (byte[] Luminance, int Side, PerspectiveTransform Drawn, float Level) Render(RmQRVersion version, float keystone, float pixelsPerModule, float degrees, Func<int, int, bool>? overwriteDark = null)
    {
        var qr = RmQRCodeGenerator.Create(Content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 });
        bool IsDark(int row, int column) => overwriteDark is not null && overwriteDark(row, column) || qr[row, column];
        var (luminance, side, _) = SupersampledRenderer.Render(IsDark, qr.Width, qr.Height, pixelsPerModule, degrees, keystone);
        var drawn = SupersampledGeometry.GridToPixel(qr.Width, qr.Height, pixelsPerModule, degrees, keystone);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        // As the decoder reads it: halfway between the two levels
        return (luminance, side, drawn, grey.EdgeLevel(threshold));
    }

    /// <summary>The drawn frame at the finder centre: the centre and one module along each grid axis.</summary>
    private static void Frame(in PerspectiveTransform drawn, out float centerX, out float centerY, out float uX, out float uY, out float vX, out float vY)
    {
        drawn.Transform(3.5f, 3.5f, out centerX, out centerY);
        drawn.Transform(4f, 3.5f, out var x1, out var y1);
        drawn.Transform(3f, 3.5f, out var x0, out var y0);
        uX = x1 - x0;
        uY = y1 - y0;
        drawn.Transform(3.5f, 4f, out x1, out y1);
        drawn.Transform(3.5f, 3f, out x0, out y0);
        vX = x1 - x0;
        vY = y1 - y0;
    }

    /// <summary>A square seed: along the finder's rows turned by <paramref name="degrees"/>, the other axis square to it at the module height across the rows, from a point a third of a pixel off the centre.</summary>
    private static void Seed(in PerspectiveTransform drawn, float degrees, out float x, out float y, out float uX, out float uY, out float vX, out float vY)
    {
        Frame(drawn, out var centerX, out var centerY, out var trueUX, out var trueUY, out var trueVX, out var trueVY);
        var length = Length(trueUX, trueUY);
        var height = Math.Abs(trueUX * trueVY - trueUY * trueVX) / length;
        var radians = MathF.Atan2(trueUY, trueUX) + degrees * MathF.PI / 180f;
        uX = MathF.Cos(radians) * length;
        uY = MathF.Sin(radians) * length;
        vX = -MathF.Sin(radians) * height;
        vY = MathF.Cos(radians) * height;
        x = centerX + 0.3f;
        y = centerY - 0.3f;
    }

    private static float Length(float x, float y) => MathF.Sqrt(x * x + y * y);

    private static float Angle(float ax, float ay, float bx, float by)
        => MathF.Acos(Math.Clamp((ax * bx + ay * by) / (Length(ax, ay) * Length(bx, by)), -1f, 1f)) * 180f / MathF.PI;
}
