using FeatherQR.SkiaSharp;
using FeatherQR.SkiaSharp.Internals;
using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// The two guards that make a failed <see cref="RmQRImageDecoder.TryLocateSubFinder"/>
/// cheap: the bounding-box rejection before the ring search, and the row-wise early
/// exit inside the 5x5 template.
/// </summary>
/// <remarks>
/// Both are pure optimizations — they may only make a search that was going to fail
/// fail sooner. What has to be pinned is that neither ever changes an answer: a
/// tightened bound or an off-by-one acceptance floor is invisible to a decode test,
/// because the sub-finder is one step of a pipeline with several fallbacks behind it.
/// These tests call the locator directly and compare against a reference that has
/// neither guard.
/// </remarks>
public class RmQRSubFinderGuardTest
{
    private const int SubFinderMinScore = 24;

    private static (byte[] Luminance, int Width, int Height, byte Threshold, float ModuleSize) Render(RmQRVersion version, string content, int modulePixelSize)
    {
        var data = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });
        using var bitmap = new RmQRCodeImageBuilder(data).WithModulePixelSize(modulePixelSize).ToBitmap();
        var luminance = new byte[bitmap.Width * bitmap.Height];
        BitmapLuminanceConverter.Convert(bitmap, luminance);
        return (luminance, bitmap.Width, bitmap.Height, Binarizer.ComputeOtsuThreshold(luminance), modulePixelSize);
    }

    /// <summary>
    /// Reference locator: the same 5x5 template over the same half-module lattice, with
    /// no bounding-box rejection and no early exit. Deliberately a separate transcription
    /// rather than a flag on the production one, so a guard that changes an answer shows
    /// up as a disagreement. It models the axis-aligned case only — production also tries
    /// three shear leans and stops at the first perfect 25/25 — which is why every fixture
    /// here renders axis-aligned.
    /// </summary>
    private static bool ReferenceLocate(
        ReadOnlySpan<byte> luminance, int width, int height, byte threshold,
        float centreX, float centreY, float uX, float uY, float vX, float vY,
        int symbolWidth, int symbolHeight, out float bestX, out float bestY)
    {
        var dX = symbolWidth - 2.5f - 3.5f;
        var dY = symbolHeight - 2.5f - 3.5f;
        var predictedX = centreX + dX * uX + dY * vX;
        var predictedY = centreY + dX * uY + dY * vY;
        var radius = 12 + symbolWidth / 10;

        var bestScore = -1;
        bestX = 0f;
        bestY = 0f;

        for (var offV = -radius; offV <= radius; offV++)
        {
            for (var offU = -radius; offU <= radius; offU++)
            {
                var originX = predictedX + offU * 0.5f * uX + offV * 0.5f * vX;
                var originY = predictedY + offU * 0.5f * uY + offV * 0.5f * vY;

                var score = 0;
                for (var j = -2; j <= 2; j++)
                {
                    for (var i = -2; i <= 2; i++)
                    {
                        var sx = originX + i * uX + j * vX;
                        var sy = originY + i * uY + j * vY;
                        var px = (int)(sx + 0.5f);
                        var py = (int)(sy + 0.5f);
                        if (px < 0 || px >= width || py < 0 || py >= height)
                            continue;

                        var dark = luminance[py * width + px] < threshold;
                        // 5x5 sub-finder: dark ring, light ring, dark centre.
                        var ring = Math.Max(Math.Abs(i), Math.Abs(j));
                        var expected = ring != 1;
                        if (dark == expected)
                            score++;
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestX = originX;
                    bestY = originY;
                }
            }
        }

        return bestScore >= SubFinderMinScore;
    }

    /// <summary>
    /// On a real symbol the guarded locator must find the sub-finder, and must find it
    /// where an unguarded search does. A bounding box tightened past the true reach, or
    /// an early exit that abandons a position scoring exactly the acceptance floor,
    /// both break this.
    /// </summary>
    [Test]
    [Arguments(RmQRVersion.R7x43, "RMQR 43", 8)]
    [Arguments(RmQRVersion.R7x43, "RMQR 43", 3)]
    [Arguments(RmQRVersion.R11x27, "27", 8)]
    [Arguments(RmQRVersion.R17x139, "the quick brown fox jumps over the lazy dog", 5)]
    public async Task GuardedLocator_AgreesWithAnUnguardedSearch_OnRealSymbols(RmQRVersion version, string content, int modulePixelSize)
    {
        var (luminance, width, height, threshold, module) = Render(version, content, modulePixelSize);
        var quiet = 2 * modulePixelSize; // the image builder's quiet zone
        var symbolWidth = (width - 2 * quiet) / modulePixelSize;
        var symbolHeight = (height - 2 * quiet) / modulePixelSize;

        // Axis-aligned frame anchored on the finder centre at grid (3.5, 3.5).
        var candidate = new FinderPattern
        {
            X = quiet + 3.5f * module,
            Y = quiet + 3.5f * module,
            ModuleSize = module,
            Count = 8,
        };

        var located = RmQRImageDecoder.TryLocateSubFinder(
            luminance, width, height, threshold, candidate,
            module, 0f, 0f, module, symbolWidth, symbolHeight,
            out var gotX, out var gotY);

        var expected = ReferenceLocate(
            luminance, width, height, threshold, candidate.X, candidate.Y,
            module, 0f, 0f, module, symbolWidth, symbolHeight,
            out var wantX, out var wantY);

        await Assert.That(located).IsEqualTo(expected)
            .Because($"{version} at {modulePixelSize} px/module: guarded and unguarded searches disagree on whether the sub-finder is there");

        if (expected)
        {
            // Same lattice, so an accepted position must be the same one; the locator
            // then refines the centre, so allow a module of slack.
            await Assert.That(Math.Abs(gotX - wantX)).IsLessThanOrEqualTo(module)
                .Because($"{version} at {modulePixelSize} px/module: sub-finder X moved");
            await Assert.That(Math.Abs(gotY - wantY)).IsLessThanOrEqualTo(module)
                .Because($"{version} at {modulePixelSize} px/module: sub-finder Y moved");
        }
    }

    /// <summary>
    /// The bounding box must reject only predictions that genuinely cannot reach the
    /// image. A prediction just outside the frame is still within reach of the search
    /// radius, so it has to be searched, not rejected.
    /// </summary>
    [Test]
    public async Task BoundingBox_DoesNotRejectAPredictionTheSearchRadiusCanStillReach()
    {
        var (luminance, width, height, threshold, module) = Render(RmQRVersion.R7x43, "RMQR 43", 8);
        var quiet = 2 * 8;
        var symbolWidth = (width - 2 * quiet) / 8;
        var symbolHeight = (height - 2 * quiet) / 8;

        // Shift the anchor so the predicted sub-finder centre walks off the right edge.
        // The ring radius is 8 modules here, so the true position stays reachable well
        // after the prediction itself has left the image: exactly the case a bounding
        // box with too little margin would reject.
        var coveredAPredictionOutsideTheImage = false;

        for (var shift = 0f; shift <= 7f * module; shift += module / 2f)
        {
            var candidate = new FinderPattern
            {
                X = quiet + 3.5f * module + shift,
                Y = quiet + 3.5f * module,
                ModuleSize = module,
                Count = 8,
            };

            var located = RmQRImageDecoder.TryLocateSubFinder(
                luminance, width, height, threshold, candidate,
                module, 0f, 0f, module, symbolWidth, symbolHeight,
                out _, out _);

            var expected = ReferenceLocate(
                luminance, width, height, threshold, candidate.X, candidate.Y,
                module, 0f, 0f, module, symbolWidth, symbolHeight,
                out _, out _);

            await Assert.That(located).IsEqualTo(expected)
                .Because($"anchor shifted {shift} px: the bounding box changed the outcome");

            // The prediction the box is tested against, recomputed here.
            var predictedX = candidate.X + (symbolWidth - 2.5f - 3.5f) * module;
            if (expected && predictedX >= width)
                coveredAPredictionOutsideTheImage = true;
        }

        await Assert.That(coveredAPredictionOutsideTheImage).IsTrue()
            .Because("the sweep never reached a locatable sub-finder whose prediction was off-image, so it does not exercise the box's margin at all");
    }

    /// <summary>
    /// <c>SubFinderMinScore</c> is 24 of 25, i.e. one mismatched sample is tolerated
    /// deliberately, and a clean render scores 25 so no other test here exercises that
    /// tolerance at all. This one damages a whole sample module and requires the
    /// sub-finder to still be located.
    /// </summary>
    /// <remarks>
    /// It does NOT pin the acceptance floor itself. The ring search walks a half-module
    /// lattice, so a neighbouring position re-scores the damaged module against a
    /// different pixel and reaches 25 anyway; raising the early exit's threshold by one
    /// leaves this green (verified by mutation). Pinning the floor exactly would need a
    /// fixture that damages the template identically at every lattice offset, which no
    /// real symbol produces. What is pinned is the tolerance being reachable end to end.
    /// </remarks>
    [Test]
    public async Task EarlyExit_KeepsAPositionScoringExactlyTheAcceptanceFloor()
    {
        var (luminance, width, height, threshold, module) = Render(RmQRVersion.R7x43, "RMQR 43", 8);
        var quiet = 2 * 8;
        var symbolWidth = (width - 2 * quiet) / 8;
        var symbolHeight = (height - 2 * quiet) / 8;

        var candidate = new FinderPattern
        {
            X = quiet + 3.5f * module,
            Y = quiet + 3.5f * module,
            ModuleSize = module,
            Count = 8,
        };

        // Locate the undamaged sub-finder, then break exactly one of its 25 samples.
        var found = ReferenceLocate(
            luminance, width, height, threshold, candidate.X, candidate.Y,
            module, 0f, 0f, module, symbolWidth, symbolHeight,
            out var originX, out var originY);
        await Assert.That(found).IsTrue().Because("the undamaged symbol must locate, or the fixture is wrong");

        // The (-2, -2) corner is a dark ring sample; make it light. The whole module is
        // cleared, not one pixel: the ring search walks a half-module lattice, so a
        // single flipped pixel leaves a neighbouring position still scoring 25 and the
        // acceptance floor never decides anything.
        var cx = originX - 2 * module;
        var cy = originY - 2 * module;
        var px = (int)(cx + 0.5f);
        var py = (int)(cy + 0.5f);
        await Assert.That(luminance[py * width + px]).IsLessThan(threshold).Because("the corner sample should start dark");
        for (var y = (int)(cy - module / 2f); y <= (int)(cy + module / 2f); y++)
        {
            for (var x = (int)(cx - module / 2f); x <= (int)(cx + module / 2f); x++)
            {
                if (x >= 0 && x < width && y >= 0 && y < height)
                    luminance[y * width + x] = 255;
            }
        }

        var expected = ReferenceLocate(
            luminance, width, height, threshold, candidate.X, candidate.Y,
            module, 0f, 0f, module, symbolWidth, symbolHeight,
            out _, out _);
        await Assert.That(expected).IsTrue()
            .Because("24 of 25 is the documented acceptance floor, so the damaged symbol must still locate");

        var located = RmQRImageDecoder.TryLocateSubFinder(
            luminance, width, height, threshold, candidate,
            module, 0f, 0f, module, symbolWidth, symbolHeight,
            out _, out _);

        await Assert.That(located).IsTrue()
            .Because("the early exit abandoned a position whose score bound reached the acceptance floor");
    }

    /// <summary>
    /// A prediction far enough outside the image that no sample of any ring position
    /// can land in it must be rejected — that is the case the box exists for, and it
    /// must still return the same answer an unguarded search would.
    /// </summary>
    [Test]
    public async Task BoundingBox_RejectsAPredictionNoSampleCanReach()
    {
        var (luminance, width, height, threshold, module) = Render(RmQRVersion.R7x43, "RMQR 43", 8);

        var candidate = new FinderPattern
        {
            X = width * 40f,
            Y = height * 40f,
            ModuleSize = module,
            Count = 8,
        };

        var located = RmQRImageDecoder.TryLocateSubFinder(
            luminance, width, height, threshold, candidate,
            module, 0f, 0f, module, 43, 7,
            out var centerX, out var centerY);

        var expected = ReferenceLocate(
            luminance, width, height, threshold, candidate.X, candidate.Y,
            module, 0f, 0f, module, 43, 7, out _, out _);

        await Assert.That(expected).IsFalse().Because("the reference search must also fail here, or the fixture is wrong");
        await Assert.That(located).IsFalse();
        await Assert.That(centerX).IsEqualTo(0f);
        await Assert.That(centerY).IsEqualTo(0f);
    }
}
