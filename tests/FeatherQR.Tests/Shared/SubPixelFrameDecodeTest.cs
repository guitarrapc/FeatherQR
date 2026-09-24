using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// Symbols under 2 px/module drawn with grey edges: an anti-aliased path, or a 1 px/module image scaled up bilinearly.
/// Sampled through their true geometry they read; what fails is the frame (finder centres from thresholded runs are good to half a pixel, a third of a module here) and a single pixel against a threshold that edge greys pull toward light.
/// </summary>
public class SubPixelFrameDecodeTest
{
    private const string Content = "FEATHERQR LOW DENSITY 0123456789";

    private static QRCodeData StandardQR(int version) => QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version) });

    private static async Task AssertGreyEdges(byte[] luminance)
    {
        Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        await Assert.That(grey.IsEnabled).IsTrue().Because("the render has no grey edges");
    }

    public static IEnumerable<(int, float, float, float)> StandardQRAntiAliased()
    {
        yield return (12, 1.26f, 0.1f, 0.6f);
        yield return (18, 1.26f, 0.1f, 0.1f);
        yield return (24, 1.26f, 0.1f, 0.1f);
        yield return (30, 1.26f, 0.1f, 0.85f);
        yield return (36, 1.26f, 0.1f, 0.35f);
    }

    [Test]
    [MethodDataSource(nameof(StandardQRAntiAliased))]
    public async Task StandardQR_AntiAliasedUnderOneAndAHalfPixels_Decodes(int version, float pixelsPerModule, float offsetX, float offsetY)
    {
        var qr = StandardQR(version);
        var (luminance, width, height) = AntiAliasedRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule, offsetX, offsetY);
        await AssertGreyEdges(luminance);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"version {version} at {pixelsPerModule} px/module ({offsetX}, {offsetY}): {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
    }

    public static IEnumerable<(int, float)> StandardQRBilinear()
    {
        yield return (2, 1.54f);
        yield return (2, 2.00f);
        yield return (4, 1.52f);
        yield return (6, 1.56f);
        yield return (6, 2.00f);
    }

    [Test]
    [MethodDataSource(nameof(StandardQRBilinear))]
    public async Task StandardQR_BilinearUpscale_Decodes(int version, float pixelsPerModule)
    {
        var qr = StandardQR(version);
        var (luminance, width, height) = BilinearUpscaleRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule);
        await AssertGreyEdges(luminance);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"version {version} at {pixelsPerModule} px/module: {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
    }

    /// <summary>At version 40 a module size a few percent off puts the estimate past the largest version, and blur or a turn breaks the timing runs the count relies on.</summary>
    [Test]
    [Arguments(1.72f, 0f)]
    [Arguments(1.92f, 0f)]
    [Arguments(1.93f, 0f)]
    [Arguments(2.05f, 19f)]
    [Arguments(2.5f, 47f)]
    public async Task StandardQR_Version40_DimensionFromTimingModules(float pixelsPerModule, float degrees)
    {
        var qr = StandardQR(40);
        var (luminance, width, height) = degrees == 0f
            ? BilinearUpscaleRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule)
            : SupersampledRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule, degrees);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{pixelsPerModule} px/module turned {degrees}°: {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
        await Assert.That(info.Version).IsEqualTo(40);
    }

    /// <summary>An estimate a version or more out that neither the timing count nor the version information corrects: the timing modules alternate on the grid of the right dimension.</summary>
    [Test]
    [Arguments(30)]
    [Arguments(36)]
    public async Task StandardQR_EstimateAVersionOut_DimensionFromTimingModules(int version)
    {
        var qr = StandardQR(version);
        var (luminance, width, height) = BilinearUpscaleRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, 1.88f);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(Content);
        await Assert.That(info.Version).IsEqualTo(version);
    }

    public static IEnumerable<(MicroQRVersion, float, float, float)> MicroQRAntiAliased()
    {
        yield return (MicroQRVersion.M2, 1.30f, 0.1f, 0.6f);
        yield return (MicroQRVersion.M3, 1.26f, 0.1f, 0.1f);
        yield return (MicroQRVersion.M4, 1.38f, 0.1f, 0.35f);
    }

    [Test]
    [MethodDataSource(nameof(MicroQRAntiAliased))]
    public async Task MicroQR_AntiAliasedUnderOneAndAHalfPixels_Decodes(MicroQRVersion version, float pixelsPerModule, float offsetX, float offsetY)
    {
        var content = MicroContent(version);
        var qr = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = version });
        var (luminance, width, height) = AntiAliasedRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule, offsetX, offsetY);
        await AssertGreyEdges(luminance);

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version} at {pixelsPerModule} px/module ({offsetX}, {offsetY}): {info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    [Arguments(MicroQRVersion.M3, 1.56f)]
    [Arguments(MicroQRVersion.M3, 1.76f)]
    [Arguments(MicroQRVersion.M4, 1.54f)]
    public async Task MicroQR_BilinearUpscale_Decodes(MicroQRVersion version, float pixelsPerModule)
    {
        var content = MicroContent(version);
        var qr = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = version });
        var (luminance, width, height) = BilinearUpscaleRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule);
        await AssertGreyEdges(luminance);

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version} at {pixelsPerModule} px/module: {info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    public static IEnumerable<(RmQRVersion, float, float, float)> RmQRAntiAliased()
    {
        yield return (RmQRVersion.R11x59, 1.34f, 0.1f, 0.35f);
        yield return (RmQRVersion.R15x77, 1.30f, 0.1f, 0.35f);
        yield return (RmQRVersion.R13x59, 1.26f, 0.1f, 0.6f);
    }

    [Test]
    [MethodDataSource(nameof(RmQRAntiAliased))]
    public async Task RmQR_AntiAliasedUnderOneAndAHalfPixels_Decodes(RmQRVersion version, float pixelsPerModule, float offsetX, float offsetY)
    {
        var qr = RmQRCodeGenerator.Create(Content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });
        var (luminance, width, height) = AntiAliasedRenderer.Render((row, column) => qr[row, column], qr.Width, qr.Height, pixelsPerModule, offsetX, offsetY);
        await AssertGreyEdges(luminance);

        var success = RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version} at {pixelsPerModule} px/module ({offsetX}, {offsetY}): {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
    }

    [Test]
    [Arguments(RmQRVersion.R11x59, 1.56f)]
    [Arguments(RmQRVersion.R11x59, 2.00f)]
    [Arguments(RmQRVersion.R13x59, 2.00f)]
    public async Task RmQR_BilinearUpscale_Decodes(RmQRVersion version, float pixelsPerModule)
    {
        var qr = RmQRCodeGenerator.Create(Content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });
        var (luminance, width, height) = BilinearUpscaleRenderer.Render((row, column) => qr[row, column], qr.Width, qr.Height, pixelsPerModule);
        await AssertGreyEdges(luminance);

        var success = RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version} at {pixelsPerModule} px/module: {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
    }

    /// <summary>
    /// Scaled up bilinearly to about 2.5 px/module, the finder's light ring keeps one pixel above the global threshold, which edge greys pull toward light, and no 1:1:3:1:1 run is found at it; halfway between the two levels the ring is its width.
    /// The premise is asserted: no finder candidate at the global threshold, one at the midpoint.
    /// </summary>
    [Test]
    [Arguments(MicroQRVersion.M1, 2.54f)]
    [Arguments(MicroQRVersion.M2, 2.52f)]
    [Arguments(MicroQRVersion.M3, 2.53f)]
    public async Task MicroQR_FinderBlurredPastTheGlobalThreshold_Decodes(MicroQRVersion version, float pixelsPerModule)
    {
        var content = version <= MicroQRVersion.M2 ? "01234" : "FQR 2.0";
        var qr = MicroQRCodeGenerator.Create(content, version == MicroQRVersion.M1 ? MicroQREccLevel.ErrorDetectionOnly : MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = version, QuietZoneSize = 3 });
        var (luminance, width, height) = BilinearUpscaleRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule);
        await AssertFinderOnlyAtTheMidpoint(luminance, width, height);

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version} at {pixelsPerModule} px/module: {info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    [Arguments(RmQRVersion.R11x59, 2.51f)]
    public async Task RmQR_FinderBlurredPastTheGlobalThreshold_Decodes(RmQRVersion version, float pixelsPerModule)
    {
        const string content = "RMQR 01";
        var qr = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = 3 });
        var (luminance, width, height) = BilinearUpscaleRenderer.Render((row, column) => qr[row, column], qr.Width, qr.Height, pixelsPerModule);
        await AssertFinderOnlyAtTheMidpoint(luminance, width, height);

        var success = RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version} at {pixelsPerModule} px/module: {info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    private static async Task AssertFinderOnlyAtTheMidpoint(byte[] luminance, int width, int height)
    {
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        await Assert.That(grey.IsEnabled).IsTrue();
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        await Assert.That(FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey)).IsEqualTo(0);
        await Assert.That(FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, (byte)Math.Round(grey.Midpoint), candidates, grey)).IsGreaterThan(0);
    }

    private static string MicroContent(MicroQRVersion version) => version == MicroQRVersion.M2 ? "0123456" : "FQR 2.0";
}
