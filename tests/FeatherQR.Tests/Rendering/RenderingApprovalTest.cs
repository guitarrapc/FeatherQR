using SkiaSharp;
using System.Runtime.CompilerServices;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// Approval tests for the styled rendering surface (module shapes, gradients,
/// finder pattern shapes, icon overlays) that the matrix-level golden tests
/// (<see cref="QRCodeVisualCompatibilityTest"/>) cannot see. Each case renders
/// through the image builder and is compared against a committed golden PNG in
/// <c>testdata/rendering/</c>, pixel by pixel with a small per-channel tolerance.
/// Comparing decoded pixels instead of encoded bytes keeps the assertion
/// independent of PNG encoder differences, and the tolerance absorbs
/// platform-marginal antialiasing while still catching real regressions
/// (orientation flips, color or geometry changes move many pixels by a lot).
/// </summary>
/// <remarks>
/// <para>
/// Text rendering is deliberately excluded (no <see cref="ImageTextIconShape"/>
/// case): glyph rasterization depends on the platform font stack and would make
/// the goldens machine-specific. The icon case draws its icon with Skia shapes.
/// </para>
/// <para>
/// When a golden is missing the test generates it into the source tree and passes
/// (commit the new file with the PR); on CI a missing golden fails instead. When a
/// case mismatches, the rendered output is saved next to the golden as
/// <c>*.actual.png</c> for visual diffing. To intentionally change rendering,
/// delete the affected goldens, rerun, review, and commit the regenerated files.
/// </para>
/// </remarks>
public class RenderingApprovalTest
{
    /// <summary>Maximum per-channel difference tolerated per pixel.</summary>
    private const int Tolerance = 2;

    private const string StandardContent = "APPROVAL TEST 2026";

    public static IEnumerable<string> CaseIds() => Cases.Keys;

    private static readonly SortedDictionary<string, Func<SKBitmap>> Cases = new(StringComparer.Ordinal)
    {
        // Standard QR: every styling axis, one case each, plus one combination.
        ["standardqr-default"] = static () =>
            StandardBuilder().ToBitmap(),
        ["standardqr-circle-modules"] = static () =>
            StandardBuilder().WithModuleShape(CircleModuleShape.Default).ToBitmap(),
        ["standardqr-rounded-modules-80"] = static () =>
            StandardBuilder().WithModuleShape(RoundedRectangleModuleShape.Default, sizePercent: 0.8f).ToBitmap(),
        ["standardqr-gradient-diagonal"] = static () =>
            StandardBuilder().WithGradient(new GradientOptions([SKColors.DarkOrange, SKColors.Firebrick], GradientDirection.TopLeftToBottomRight)).ToBitmap(),
        ["standardqr-gradient-three-stops"] = static () =>
            StandardBuilder().WithGradient(new GradientOptions([SKColors.Indigo, SKColors.Teal, SKColors.Goldenrod], GradientDirection.TopToBottom, [0.0f, 0.3f, 1.0f])).ToBitmap(),
        ["standardqr-finder-circle"] = static () =>
            StandardBuilder().WithFinderPatternShape(CircleFinderPatternShape.Default).ToBitmap(),
        ["standardqr-finder-rounded-circle"] = static () =>
            StandardBuilder().WithFinderPatternShape(RoundedRectangleCircleFinderPatternShape.Default).ToBitmap(),
        ["standardqr-icon"] = static () =>
        {
            using var icon = CreateIconBitmap();
            return new QRCodeImageBuilder(StandardContent)
                .WithErrorCorrection(QREccLevel.H)
                .WithModulePixelSize(4)
                .WithColors(SKColors.Black, SKColors.White, SKColors.White)
                .WithIcon(IconData.FromImage(icon))
                .ToBitmap();
        },
        ["standardqr-styled-combo"] = static () =>
            StandardBuilder()
                .WithModuleShape(CircleModuleShape.Default, sizePercent: 0.9f)
                .WithGradient(new GradientOptions([SKColors.MidnightBlue, SKColors.SeaGreen], GradientDirection.LeftToRight))
                .WithFinderPatternShape(RoundedRectangleFinderPatternShape.Default)
                .ToBitmap(),

        // Micro QR: default and the styled path.
        ["microqr-default"] = static () =>
            new MicroQRCodeImageBuilder("MICRO1")
                .WithErrorCorrection(MicroQREccLevel.M)
                .WithModulePixelSize(4)
                .WithColors(SKColors.Black, SKColors.White, SKColors.White)
                .ToBitmap(),
        ["microqr-gradient-circle"] = static () =>
            new MicroQRCodeImageBuilder("MICRO1")
                .WithErrorCorrection(MicroQREccLevel.M)
                .WithModulePixelSize(4)
                .WithColors(SKColors.Black, SKColors.White, SKColors.White)
                .WithModuleShape(CircleModuleShape.Default)
                .WithGradient(new GradientOptions([SKColors.DarkSlateBlue, SKColors.Crimson], GradientDirection.LeftToRight))
                .ToBitmap(),

        // rMQR: default (pins the rectangular layout) and the styled path.
        ["rmqr-default"] = static () =>
            new RmQRCodeImageBuilder("RMQR-STYLE")
                .WithErrorCorrection(RmQREccLevel.M)
                .WithModulePixelSize(4)
                .WithColors(SKColors.Black, SKColors.White, SKColors.White)
                .ToBitmap(),
        ["rmqr-gradient-rounded"] = static () =>
            new RmQRCodeImageBuilder("RMQR-STYLE")
                .WithErrorCorrection(RmQREccLevel.M)
                .WithModulePixelSize(4)
                .WithColors(SKColors.Black, SKColors.White, SKColors.White)
                .WithModuleShape(RoundedRectangleModuleShape.Default, sizePercent: 0.85f)
                .WithGradient(new GradientOptions([SKColors.DarkGreen, SKColors.OrangeRed], GradientDirection.TopToBottom))
                .ToBitmap(),
    };

    private static QRCodeImageBuilder StandardBuilder() =>
        new QRCodeImageBuilder(StandardContent)
            .WithErrorCorrection(QREccLevel.M)
            .WithModulePixelSize(4)
            .WithColors(SKColors.Black, SKColors.White, SKColors.White);

    /// <summary>Deterministic icon: Skia shapes only, no fonts, no external assets.</summary>
    private static SKBitmap CreateIconBitmap()
    {
        var bitmap = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Firebrick, IsAntialias = true };
        canvas.DrawCircle(32, 32, 24, paint);
        paint.Color = SKColors.White;
        canvas.DrawRect(SKRect.Create(24, 24, 16, 16), paint);
        return bitmap;
    }

    [Test]
    [MethodDataSource(nameof(CaseIds))]
    public async Task Render_StyledCase_MatchesGoldenPng(string caseId)
    {
        var goldenDirectory = GetGoldenDirectory();
        Directory.CreateDirectory(goldenDirectory);
        var goldenPath = Path.Combine(goldenDirectory, caseId + ".png");

        using var actual = Cases[caseId]();

        if (!File.Exists(goldenPath))
        {
            if (Environment.GetEnvironmentVariable("CI") is not null)
            {
                Assert.Fail($"Golden file missing on CI: {goldenPath}. Generate it locally (run this test) and commit it.");
            }

            SavePng(actual, goldenPath);
            await Assert.That(File.Exists(goldenPath)).IsTrue().Because($"Golden file created, commit it: {goldenPath}");
            return;
        }

        using var expected = SKBitmap.Decode(goldenPath);
        await Assert.That(expected).IsNotNull().Because($"golden must be a decodable PNG: {goldenPath}");
        await Assert.That((actual.Width, actual.Height)).IsEqualTo((expected!.Width, expected.Height))
            .Because($"{caseId}: rendered size changed");

        var mismatchCount = 0;
        var maxDelta = 0;
        for (var y = 0; y < expected.Height; y++)
        {
            for (var x = 0; x < expected.Width; x++)
            {
                var e = expected.GetPixel(x, y);
                var a = actual.GetPixel(x, y);
                var delta = Math.Max(
                    Math.Max(Math.Abs(e.Red - a.Red), Math.Abs(e.Green - a.Green)),
                    Math.Max(Math.Abs(e.Blue - a.Blue), Math.Abs(e.Alpha - a.Alpha)));
                if (delta > Tolerance)
                {
                    mismatchCount++;
                    maxDelta = Math.Max(maxDelta, delta);
                }
            }
        }

        if (mismatchCount > 0)
        {
            var actualPath = Path.Combine(goldenDirectory, caseId + ".actual.png");
            SavePng(actual, actualPath);
            var total = expected.Width * expected.Height;
            Assert.Fail($"Rendering changed for '{caseId}': {mismatchCount}/{total} pixels " +
                        $"({mismatchCount * 100.0 / total:F2}%) differ by more than {Tolerance} per channel " +
                        $"(max delta {maxDelta}).\nGolden: {goldenPath}\nActual: {actualPath}\n" +
                        $"If the change is intentional, delete the golden, rerun, review the regenerated file, and commit it.");
        }
    }

    private static void SavePng(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    /// <summary>
    /// Goldens live in the source tree (anchored via CallerFilePath), not the test
    /// output directory, so newly generated files land where they can be committed
    /// and intentional deletions take effect without a rebuild.
    /// </summary>
    private static string GetGoldenDirectory([CallerFilePath] string sourceFilePath = "")
        => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(sourceFilePath))!, "testdata", "rendering");
}
