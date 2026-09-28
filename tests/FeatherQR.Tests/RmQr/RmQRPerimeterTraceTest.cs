using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// The rMQR grid traced round the symbol's edges from a frame at the finder, against the grid it was drawn with; and the edges it must not trace.
/// </summary>
public class RmQRPerimeterTraceTest
{
    private const string Content = "RMQR 43";

    /// <summary>Every width, heights 7 to 17, narrowed 20 % along the short axis, the long axis and toward a corner, turned.</summary>
    public static IEnumerable<(RmQRVersion Version, float Tilt, float Degrees)> Symbols()
    {
        foreach (var version in new[] { RmQRVersion.R7x43, RmQRVersion.R11x27, RmQRVersion.R9x59, RmQRVersion.R13x77, RmQRVersion.R15x99, RmQRVersion.R7x139, RmQRVersion.R17x139 })
        {
            foreach (var (tilt, degrees) in new[] { (0f, 17f), (90f, 200f), (45f, 290f) })
                yield return (version, tilt, degrees);
        }
    }

    /// <summary>From the drawn frame at the finder, every module centre within 0.3 of a module of where it was drawn, the far end of the widest symbol included.</summary>
    [Test]
    [MethodDataSource(nameof(Symbols))]
    public async Task TryTracePerimeter_StrongKeystone_MatchesTheDrawnGrid(RmQRVersion version, float tilt, float degrees)
    {
        const float PixelsPerModule = 4f;
        var qr = Symbol(version);
        var (luminance, side, _) = SupersampledRenderer.Render((row, column) => qr[row, column], qr.Width, qr.Height, PixelsPerModule, degrees, 0.2f, tilt);
        var drawn = SupersampledGeometry.GridToPixel(qr.Width, qr.Height, PixelsPerModule, degrees, 0.2f, tilt);
        var level = Level(luminance);
        Frame(drawn, out var centerX, out var centerY, out var uX, out var uY, out var vX, out var vY);

        var traced = RmQRImageDecoder.TryTracePerimeter(luminance, side, side, level, centerX, centerY, uX, uY, vX, vY, version, out var transform);

        await Assert.That(traced).IsTrue();
        await Assert.That(LargestModuleError(drawn, transform, qr.Width, qr.Height, PixelsPerModule)).IsLessThan(0.3f);
    }

    /// <summary>
    /// Two dark runs of the top row painted light in a row are missed and the trace goes on; a third in a row ends it.
    /// The runs are timing modules at columns 30, 32 and 34 of an R13x77, clear of its alignment pattern at 25.
    /// </summary>
    [Test]
    [Arguments(2, true)]
    [Arguments(3, false)]
    public async Task TryTracePerimeter_ConsecutiveRunsMissing_TracesUpToTwo(int missing, bool traces)
    {
        var qr = Symbol(RmQRVersion.R13x77);
        bool IsDark(int row, int column) => qr[row, column] && !(row == 0 && column >= 30 && column < 30 + 2 * missing);
        var (luminance, side, _) = SupersampledRenderer.Render(IsDark, qr.Width, qr.Height, 4f, 17f, 0.1f);
        var drawn = SupersampledGeometry.GridToPixel(qr.Width, qr.Height, 4f, 17f, 0.1f);
        Frame(drawn, out var centerX, out var centerY, out var uX, out var uY, out var vX, out var vY);

        var traced = RmQRImageDecoder.TryTracePerimeter(luminance, side, side, Level(luminance), centerX, centerY, uX, uY, vX, vY, RmQRVersion.R13x77, out var transform);

        await Assert.That(traced).IsEqualTo(traces);
        if (traced)
            await Assert.That(LargestModuleError(drawn, transform, qr.Width, qr.Height, 4f)).IsLessThan(0.3f);
    }

    /// <summary>Traced as the next width up, the top row runs off the symbol into the quiet zone and the trace ends there.</summary>
    [Test]
    [Arguments(RmQRVersion.R13x77, RmQRVersion.R13x99)]
    [Arguments(RmQRVersion.R7x43, RmQRVersion.R7x59)]
    public async Task TryTracePerimeter_WiderVersionThanDrawn_Fails(RmQRVersion drawnVersion, RmQRVersion tracedVersion)
    {
        var qr = Symbol(drawnVersion);
        var (luminance, side, _) = SupersampledRenderer.Render((row, column) => qr[row, column], qr.Width, qr.Height, 4f, 17f, 0.1f);
        var drawn = SupersampledGeometry.GridToPixel(qr.Width, qr.Height, 4f, 17f, 0.1f);
        Frame(drawn, out var centerX, out var centerY, out var uX, out var uY, out var vX, out var vY);
        await Assert.That(RmQRImageDecoder.TryTracePerimeter(luminance, side, side, Level(luminance), centerX, centerY, uX, uY, vX, vY, drawnVersion, out _)).IsTrue().Because("its own version traces");

        await Assert.That(RmQRImageDecoder.TryTracePerimeter(luminance, side, side, Level(luminance), centerX, centerY, uX, uY, vX, vY, tracedVersion, out _)).IsFalse();
    }

    /// <summary>
    /// An anti-aliased R17x99 at 1.31 px/module: the grid traced from its frame gets past the format information and fails in the data, and the anisotropic grid reads it afterwards.
    /// A failed trace counted as the frame's result would end the search over scales before that grid.
    /// </summary>
    [Test]
    public async Task Decode_LowDensityTraceFails_AnchoredGridStillReads()
    {
        const string content = "QKDRSVtoGExL/eYnEKj3I1=9/UUlX_8fPY??-e?=a?AW3wZznDEAkCG5MKW&aiIMNr?nh1uANl";
        var qr = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x99, QuietZoneSize = 2 });
        var (luminance, width, height) = AntiAliasedRenderer.Render((row, column) => qr[row, column], qr.Width, qr.Height, 1.3149171f, 0.9328947f, 0.05425163f);

        var success = RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    private static RmQRCodeData Symbol(RmQRVersion version)
        => RmQRCodeGenerator.Create(Content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 });

    /// <summary>As the decoder reads it: halfway between the two levels.</summary>
    private static float Level(byte[] luminance)
    {
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        return grey.EdgeLevel(threshold);
    }

    /// <summary>The drawn frame at the finder centre, as a measured outline gives it: the centre and one module along each grid axis.</summary>
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

    private static float LargestModuleError(in PerspectiveTransform drawn, in PerspectiveTransform traced, int width, int height, float pixelsPerModule)
    {
        var worst = 0f;
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                drawn.Transform(column + 0.5f, row + 0.5f, out var x0, out var y0);
                traced.Transform(column + 0.5f, row + 0.5f, out var x1, out var y1);
                worst = Math.Max(worst, MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)));
            }
        }
        // In modules at the density the render is drawn at, which a keystone shrinks toward the far edge
        return worst / pixelsPerModule;
    }
}
