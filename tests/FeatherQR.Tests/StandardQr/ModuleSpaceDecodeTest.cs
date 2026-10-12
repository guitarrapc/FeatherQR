using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// A grid that fails by the threshold is read again in module space: each module's luminance the mean of nine samples over its middle, the module dark when no lighter than the mean of the modules around it.
/// The level a module is split at then follows the light across the symbol, where a threshold over the image, global or regional, is set in pixels and reads one pixel a module.
/// At the global pass the grey image is the one read. At the regional pass it is the image the binarization was made from, handed to the pass with its polarity.
/// </summary>
public class ModuleSpaceDecodeTest
{
    private const string LitContent = "FQR 2.0";

    /// <summary>
    /// A symbol under a shadow or a ramp: its grid fails by the threshold and by coverage, both of which split every module at one level, and reads in module space, so the global pass reads it with no regional pass.
    /// The renders are <see cref="UnevenLightingDecodeTest"/>'s, crisp at 4 px/module with the grid drawn exactly, so the premises sample the grid as drawn.
    /// </summary>
    [Test]
    [Arguments(UnevenLight.Shadow, 0f, 0.55f)]
    [Arguments(UnevenLight.Shadow, 90f, 0.55f)]
    [Arguments(UnevenLight.Ramp, 0f, 0.8f)]
    [Arguments(UnevenLight.Ramp, 90f, 0.8f)]
    public async Task UnevenlyLitSymbol_ReadsAtTheGlobalPassInModuleSpace(UnevenLight light, float degrees, float depth)
    {
        var qr = QRCodeGenerator.Create(LitContent, QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 });
        var (luminance, width, height) = UnevenLightingRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, 4, light, degrees, depth);

        // Premises: the grid as drawn does not decode by the global threshold nor by coverage, and decodes in module space
        const int QuietZone = 4, Scale = 4;
        var d = qr.Size - 2 * QuietZone;
        float near = QuietZone * Scale, far = (QuietZone + d) * Scale;
        var transform = PerspectiveTransform.QuadrilateralToQuadrilateral(0f, 0f, d, 0f, d, d, 0f, d, near, near, far, near, far, far, near, far);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var destination = new char[QRCodeDecoder.GetMaxDecodedLength(3)];
        var modules = new byte[d * d];
        PerspectiveGridSampler.Sample(luminance, width, height, threshold, transform, d, modules);
        await Assert.That(QRImageDecoder.DecodeWithMirrorRetry(modules, d, destination, out _, out _, out _)).IsNotEqualTo(DecodeStatus.Success).Because("the grid reads by the threshold");
        QRImageDecoder.SampleGridByCoverage(luminance, width, height, grey.Midpoint, transform, d, modules);
        await Assert.That(QRImageDecoder.DecodeWithMirrorRetry(modules, d, destination, out _, out _, out _)).IsNotEqualTo(DecodeStatus.Success).Because("the grid reads by coverage");
        await Assert.That(QRImageDecoder.TimingPatternsAlternate(luminance, width, height, transform, d, nearest: false, negative: false)).IsTrue();
        QRImageDecoder.SampleGridInModuleSpace(luminance, width, height, transform, d, nearest: false, negative: false, new int[QRImageDecoder.ModuleSpaceScratchLength(d)], modules);
        await Assert.That(QRImageDecoder.DecodeWithMirrorRetry(modules, d, destination, out _, out _, out _)).IsEqualTo(DecodeStatus.Success);

        var histogram = new int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(luminance, histogram);
        var status = QRImageDecoder.DecodeLuminanceCore(luminance, histogram, width, height, destination, out var written, out var info);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success).Because(info.Status.ToString());
        await Assert.That(new string(destination, 0, written)).IsEqualTo(LitContent);
    }

    /// <summary>
    /// A small symbol at 2.2 to 3 px/module, anti-aliased, with a deep shadow across it: neither global polarity reads it, and the regional binarization places the symbol without its one pixel a module reading it.
    /// The same grid read in module space from the grey image reads. Negated, the negative's binarization does, from the same grey image with its polarity turned.
    /// The shots were chosen from a sweep of versions, densities, turns and lights where the regional pass reads with the grey image and not without.
    /// </summary>
    [Test]
    [Arguments(2, QREccLevel.L, 2.6f, 0f, 180f, false)]
    [Arguments(3, QREccLevel.M, 3f, 20f, 0f, false)]
    [Arguments(3, QREccLevel.M, 2.6f, 200f, 45f, false)]
    [Arguments(4, QREccLevel.M, 2.2f, 0f, 135f, false)]
    [Arguments(2, QREccLevel.L, 2.6f, 0f, 180f, true)]
    [Arguments(3, QREccLevel.M, 3f, 20f, 0f, true)]
    public async Task ShadowedSmallSymbol_ReadsAtTheRegionalPassInModuleSpace(int version, QREccLevel level, float pixelsPerModule, float turnDegrees, float lightDegrees, bool negated)
    {
        var (luminance, width, height, text) = Shadowed(version, level, pixelsPerModule, turnDegrees, lightDegrees, negated, destroyData: false);

        // Premises: no global polarity reads it, and no regional polarity does from its binarization alone
        await Assert.That(UnevenLightingDecodeTest.GlobalAttemptsRead(UnevenLightingDecodeTest.Symbology.StandardQR, luminance, width, height)).IsFalse().Because("a global pass reads it");
        await Assert.That(RegionalReads(luminance, width, height, text, withGreyImage: false)).IsFalse().Because("the regional binarization reads it without the grey image");
        await Assert.That(RegionalReads(luminance, width, height, text, withGreyImage: true)).IsTrue();

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var decoded, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(decoded).IsEqualTo(text);
    }

    /// <summary>
    /// The re-read follows every grid of the symbol, not the first alone: <see cref="EdgeFourthPointDecodeTest"/>'s version 1 bent round a cylinder and tilted reads through the grid of the fourth point from the finders' edges and no earlier one, and under a deep shadow that grid reads in module space, at the regional pass.
    /// The one shot of a sweep of that test's three photographs under 80 lights that reads with the re-read on the later grids and not without.
    /// </summary>
    [Test]
    public async Task BentSymbolUnderAShadow_ReadsThroughALaterGridInModuleSpace()
    {
        var (photograph, _) = EdgeFourthPointDecodeTest.Render(tiltDegrees: 25f, rollDegrees: 200f, distance: 2.5f, bowModules: 3f, destroyData: false);
        var luminance = photograph.Luminance.ToArray();
        int width = photograph.Width, height = photograph.Height;
        UnevenLightingRenderer.Light(luminance, width, height, 8f, UnevenLight.Shadow, 0f, 0.75f);

        // Premises: no global polarity reads it, and no regional polarity does from its binarization alone
        await Assert.That(UnevenLightingDecodeTest.GlobalAttemptsRead(UnevenLightingDecodeTest.Symbology.StandardQR, luminance, width, height)).IsFalse().Because("a global pass reads it");
        await Assert.That(RegionalReads(luminance, width, height, EdgeFourthPointDecodeTest.Content, withGreyImage: false)).IsFalse().Because("the regional binarization reads it without the grey image");

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var decoded, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(decoded).IsEqualTo(EdgeFourthPointDecodeTest.Content);
    }

    /// <summary>The negative the re-read must not admit: the same renders with their data destroyed read nothing.</summary>
    [Test]
    [Arguments(2, QREccLevel.L, 2.6f, 0f, 180f, false)]
    [Arguments(3, QREccLevel.M, 3f, 20f, 0f, true)]
    public async Task ShadowedSmallSymbol_DestroyedData_ReadsNothing(int version, QREccLevel level, float pixelsPerModule, float turnDegrees, float lightDegrees, bool negated)
    {
        var (luminance, width, height, _) = Shadowed(version, level, pixelsPerModule, turnDegrees, lightDegrees, negated, destroyData: true);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out _, out var info);

        await Assert.That(success).IsFalse().Because(info.Status.ToString());
    }

    /// <summary>
    /// On a clean render the module-space read is the symbol, module for module, and the negative of the render read as a negative is the same symbol.
    /// </summary>
    [Test]
    [Arguments(1, 4f, 0f, 0f)]
    [Arguments(5, 3.5f, 20f, 0.1f)]
    [Arguments(10, 2.5f, 200f, 0f)]
    public async Task SampleGridInModuleSpace_CleanRender_ReadsEveryModule(int version, float pixelsPerModule, float degrees, float keystone)
    {
        var qr = QRCodeGenerator.Create($"FQR MS v{version}", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
        var d = qr.Size;
        var (luminance, width, height) = SupersampledRenderer.Render((row, column) => qr[row, column], d, d, pixelsPerModule, degrees, keystone);
        var truth = SupersampledGeometry.GridToPixel(d, d, pixelsPerModule, degrees, keystone);
        var nearest = QRImageDecoder.IsBelowInterpolation(truth, d);
        var expected = new byte[d * d];
        for (var i = 0; i < expected.Length; i++)
            expected[i] = qr[i / d, i % d] ? (byte)1 : (byte)0;
        var negative = new byte[luminance.Length];
        LuminanceInverter.Invert(luminance, negative);
        var scratch = new int[QRImageDecoder.ModuleSpaceScratchLength(d)];

        var modules = new byte[d * d];
        QRImageDecoder.SampleGridInModuleSpace(luminance, width, height, truth, d, nearest, negative: false, scratch, modules);
        var fromNegative = new byte[d * d];
        QRImageDecoder.SampleGridInModuleSpace(negative, width, height, truth, d, nearest, negative: true, scratch, fromNegative);

        await Assert.That(nearest).IsEqualTo(pixelsPerModule < 3f);
        await Assert.That(modules.AsSpan().SequenceEqual(expected)).IsTrue();
        await Assert.That(fromNegative.AsSpan().SequenceEqual(expected)).IsTrue();
    }

    /// <summary>
    /// The gate before the re-read: the timing row and column alternate on the symbol's own grid, in either polarity, and on no grid a module off it, where the timing row is read a module out of step or a row of data is read in its place.
    /// </summary>
    [Test]
    [Arguments(1, 4f, 0f)]
    [Arguments(5, 3.5f, 20f)]
    [Arguments(10, 2.5f, 200f)]
    public async Task TimingPatternsAlternate_OnlyOnTheSymbolsOwnGrid(int version, float pixelsPerModule, float degrees)
    {
        var qr = QRCodeGenerator.Create($"FQR MS v{version}", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
        var d = qr.Size;
        var (luminance, width, height) = SupersampledRenderer.Render((row, column) => qr[row, column], d, d, pixelsPerModule, degrees, 0f);
        var truth = SupersampledGeometry.GridToPixel(d, d, pixelsPerModule, degrees, 0f);
        var nearest = QRImageDecoder.IsBelowInterpolation(truth, d);
        var negative = new byte[luminance.Length];
        LuminanceInverter.Invert(luminance, negative);

        await Assert.That(QRImageDecoder.TimingPatternsAlternate(luminance, width, height, truth, d, nearest, negative: false)).IsTrue();
        await Assert.That(QRImageDecoder.TimingPatternsAlternate(negative, width, height, truth, d, nearest, negative: true)).IsTrue();
        await Assert.That(QRImageDecoder.TimingPatternsAlternate(luminance, width, height, truth, d, nearest, negative: true)).IsFalse().Because("the wrong polarity reads the pattern reversed");
        await Assert.That(QRImageDecoder.TimingPatternsAlternate(luminance, width, height, Shifted(truth, d, 1f, 1f), d, nearest, negative: false)).IsFalse().Because("a grid a module off along both axes reads the pattern out of step");
        await Assert.That(QRImageDecoder.TimingPatternsAlternate(luminance, width, height, Shifted(truth, d, 3f, 3f), d, nearest, negative: false)).IsFalse().Because("a grid three modules off reads data in the timing lines' place");
    }

    /// <summary>The grid whose module (u, v) is <paramref name="truth"/>'s module (u + du, v + dv).</summary>
    private static PerspectiveTransform Shifted(in PerspectiveTransform truth, int d, float du, float dv)
    {
        truth.Transform(du, dv, out var x0, out var y0);
        truth.Transform(d + du, dv, out var x1, out var y1);
        truth.Transform(d + du, d + dv, out var x2, out var y2);
        truth.Transform(du, d + dv, out var x3, out var y3);
        return PerspectiveTransform.QuadrilateralToQuadrilateral(0f, 0f, d, 0f, d, d, 0f, d, x0, y0, x1, y1, x2, y2, x3, y3);
    }

    /// <summary>Whether either regional polarity reads the text: each binarization decoded as the regional pass decodes it, with the grey image it was made from or without.</summary>
    private static bool RegionalReads(byte[] luminance, int width, int height, string text, bool withGreyImage)
    {
        var destination = new char[QRCodeDecoder.GetMaxDecodedLength(40)];
        var negativeImage = new byte[luminance.Length];
        LuminanceInverter.Invert(luminance, negativeImage);
        var scratch = new int[LocalBinarizer.ScratchLength(width, height)];
        var binarized = new byte[luminance.Length];
        foreach (var negative in new[] { false, true })
        {
            var threshold = Binarizer.ComputeOtsuThreshold(negative ? negativeImage : luminance, out _);
            if (!LocalBinarizer.TryBinarize(luminance, width, height, negative, threshold, binarized, scratch, out var darkCount))
                continue;
            var histogram = new int[Binarizer.HistogramBins];
            histogram[LocalBinarizer.Dark] = darkCount;
            histogram[LocalBinarizer.Light] = binarized.Length - darkCount;
            var status = withGreyImage
                ? QRImageDecoder.DecodeLuminanceCore(binarized, luminance, negative, histogram, width, height, destination, out var written, out _)
                : QRImageDecoder.DecodeLuminanceCore(binarized, histogram, width, height, destination, out written, out _);
            if (status == DecodeStatus.Success && destination.AsSpan(0, written).SequenceEqual(text))
                return true;
        }
        return false;
    }

    /// <summary>The symbol anti-aliased under a shadow that takes 0.7 of the light from half the image, turned, a fraction of a pixel off the pixel grid, in the given polarity.</summary>
    internal static (byte[] Luminance, int Width, int Height, string Text) Shadowed(int version, QREccLevel level, float pixelsPerModule, float turnDegrees, float lightDegrees, bool negated, bool destroyData)
    {
        var text = $"FQR MODULE SPACE v{version}";
        var qr = QRCodeGenerator.Create(text, level, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 4 });
        var random = new Random(4321);
        var destroyed = new bool[qr.Size * qr.Size];
        for (var i = 0; i < destroyed.Length; i++)
            destroyed[i] = random.Next(2) == 0;
        // Past the finders, their separators, the timing patterns and the format information, and clear of the alignment pattern. The quiet zone is 4 modules.
        var inner = qr.Size - 8;
        bool IsData(int row, int column) => row >= 4 + 9 && column >= 4 + 9 && row < 4 + inner && column < 4 + inner && !(Math.Abs(row - (4 + inner - 7)) <= 2 && Math.Abs(column - (4 + inner - 7)) <= 2);
        bool IsDark(int row, int column) => destroyData && IsData(row, column) ? destroyed[row * qr.Size + column] : qr[row, column];
        var (luminance, width, height) = UnevenLightingRenderer.Render(IsDark, qr.Size, qr.Size, pixelsPerModule, turnDegrees, 0.3f, 0.6f, antiAliased: true, UnevenLight.Shadow, lightDegrees, 0.7f);
        if (negated)
        {
            for (var i = 0; i < luminance.Length; i++)
                luminance[i] = (byte)(255 - luminance[i]);
        }
        return (luminance, width, height, text);
    }
}
