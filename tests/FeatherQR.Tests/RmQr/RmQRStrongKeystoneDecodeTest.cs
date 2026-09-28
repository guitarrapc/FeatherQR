using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// rMQR symbols in strong perspective: a plane narrowed by 12 to 20 % along the symbol's short axis, its long axis, or toward a corner, turned and drawn with grey edges at 3.5-4.5 px/module.
/// On a wide symbol the finder is sheared up to 40° and the sub-finder leans the other way, so neither the finder's square frame nor a search round it reaches the far end; the frame comes from the finder's measured outline and the symbol's traced perimeter.
/// </summary>
public class RmQRStrongKeystoneDecodeTest
{
    private const string LongContent = "RMQR IMAGE 123";
    private const string ShortContent = "RMQR 43";

    /// <summary>
    /// Each case was checked (2026-09-28) to read, plain and mirrored, under every combination of ±0.05 px/module, ±0.5° and ±0.005 keystone, and to fail at those 27 points on the decoder before the traced frame, but one mirrored nudge of the R15x43 case.
    /// </summary>
    public static IEnumerable<(RmQRVersion Version, float PixelsPerModule, float Degrees, float Keystone, float Tilt)> Cases()
    {
        // Short axis narrowed: the finder sheared along the symbol's rows
        yield return (RmQRVersion.R17x139, 4.5f, 17f, 0.2f, 0f);
        yield return (RmQRVersion.R7x139, 4.5f, 251f, 0.2f, 0f);
        yield return (RmQRVersion.R7x139, 3.5f, 0f, 0.12f, 0f);
        // Long axis narrowed: the finder square, the columns converging along the width
        yield return (RmQRVersion.R13x99, 3.5f, 163f, 0.2f, 90f);
        yield return (RmQRVersion.R11x59, 4.5f, 305f, 0.2f, 90f);
        // Toward a corner
        yield return (RmQRVersion.R13x99, 4.5f, 251f, 0.2f, 45f);
        yield return (RmQRVersion.R15x43, 3.5f, 163f, 0.2f, 225f);
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Decode_StrongKeystone_Reads(RmQRVersion version, float pixelsPerModule, float degrees, float keystone, float tilt)
    {
        var (content, qr) = Symbol(version);
        var (luminance, side, _) = SupersampledRenderer.Render((row, column) => qr[row, column], qr.Width, qr.Height, pixelsPerModule, degrees, keystone, tilt);
        await AssertReadableThroughDrawnGeometry(luminance, side, qr, pixelsPerModule, degrees, keystone, tilt);

        var success = RmQRCodeDecoder.TryDecodeImage(luminance, side, side, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version} at {pixelsPerModule} px/module, {degrees}°, keystone {keystone:P0} along {tilt}°: {info.Status}");
        await Assert.That(text).IsEqualTo(content);
        await Assert.That(info.Version).IsEqualTo(version);
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Decode_StrongKeystoneMirrored_Reads(RmQRVersion version, float pixelsPerModule, float degrees, float keystone, float tilt)
    {
        var (content, qr) = Symbol(version);
        var (drawn, side, _) = SupersampledRenderer.Render((row, column) => qr[row, column], qr.Width, qr.Height, pixelsPerModule, degrees, keystone, tilt);
        var (luminance, width, height) = NearestNeighbourRenderer.Turn(drawn, side, side, 0, mirror: true);

        var success = RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version} mirrored at {pixelsPerModule} px/module, {degrees}°, keystone {keystone:P0} along {tilt}°: {info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    /// <summary>
    /// Keystones of 6 to 20 %, plain, at 2-3 px/module, blurred or squeezed into low contrast with noise, each read only with one part of the frame as it is.
    /// Checked 2026-09-28 in a scratch build with that part changed, and each reads at 23 to 27 of 27 nudges of ±0.05 px/module, ±0.5° and ±0.005 keystone.
    /// </summary>
    public static IEnumerable<(string Part, RmQRVersion Version, RmQREccLevel Level, string Content, int Seed, float MinPixelsPerModule, float MaxPixelsPerModule, float Tilt, Degradation Degradation)> DegradedCases()
    {
        yield return ("the outline measured again along its own axes", RmQRVersion.R7x139, RmQREccLevel.M, "VKAG.KXZ:7DS91 7C9ES76%7C/B$D3TABVH-5H0*.NN787NWY+W.3", 101527, 3f, 6f, 0f, Degradation.None);
        yield return ("the perspective search behind the trace", RmQRVersion.R13x139, RmQREccLevel.H, "PYQ4UW%7%C1/N- EQ+XQ56UHJK*X5VZDW:BEPXOF10RP 7FY7O:CQEGI", 118804, 3f, 6f, 0f, Degradation.LowContrastNoise);
        yield return ("an outline frame's format copy read within 3 bits, not only exactly", RmQRVersion.R9x139, RmQREccLevel.H, "-G.VN0/qZ&hvrckJzz4i", 494121, 2f, 3f, 0f, Degradation.None);
        yield return ("edges located at the midpoint of the two levels, not at the threshold", RmQRVersion.R11x77, RmQREccLevel.M, "15922135010946837867033724603909823784188130388716278717129853211002315368890344588762011", 78475, 3f, 6f, 90f, Degradation.Blur);
        yield return ("a run's ends looked for within a module, not 0.8", RmQRVersion.R9x99, RmQREccLevel.M, "3949126179972261246774683840376334557540773310706224553836528580263760479690027", 428567, 3f, 6f, 45f, Degradation.LowContrastNoise);
        yield return ("the outer edge looked for within 0.45 of a module, not 0.35", RmQRVersion.R17x139, RmQREccLevel.H, "43702195502937002678687022113896192200323444977245947965568098260683480225414314597795822663232517339375471245125450047475181709299698511132467754787247085319", 128863, 2f, 3f, 45f, Degradation.None);
        yield return ("the outer edge looked for within 0.45 of a module, not 0.6", RmQRVersion.R11x139, RmQREccLevel.M, "pOt7kkC.B.dhP9Iv.t7mM&?XUpo-IJkSybKRzR?y8ZMhFRBtJNO7TtDrSMm_/IH8", 629358, 3f, 6f, 90f, Degradation.LowContrastNoise);
        yield return ("the step moved halfway to each run's own, not replaced by it", RmQRVersion.R13x77, RmQREccLevel.M, "CP7VUW1%F+H%E.*GO/BN%CE44EHQCS JX:OBLPGXL1*H0Y 0UO.V.WV:A2RFFJQV3.5EH", 181362, 3f, 6f, 90f, Degradation.LowContrastNoise);
        yield return ("the perimeter traced in the frame that read the version as well as from the outline", RmQRVersion.R15x139, RmQREccLevel.M, ":274M-J*VUTL-$-9JCYV*M9A $N/VC+:K.XCC+LZ8WNM*KN0D1.$VSNM$.CTHK7IYU0/3T-N /N6:WFGDNUUE2D JBKFS HWP4W8JP.S5J1W5GN4R-F*ZL4WH9N 9Q+:PZP*06-XP1P+0PV2XFX", 123849, 3f, 6f, 90f, Degradation.LowContrastNoise);
    }

    [Test]
    [MethodDataSource(nameof(DegradedCases))]
    public async Task Decode_DegradedKeystone_Reads(string part, RmQRVersion version, RmQREccLevel level, string content, int seed, float minPixelsPerModule, float maxPixelsPerModule, float tilt, Degradation degradation)
    {
        var qr = RmQRCodeGenerator.Create(content, level, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 });
        var (luminance, side) = Degraded(qr, seed, minPixelsPerModule, maxPixelsPerModule, tilt, degradation);

        var success = RmQRCodeDecoder.TryDecodeImage(luminance, side, side, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version} {degradation}, read by {part}: {info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    /// <summary>
    /// Every data module replaced by a random one, the function patterns kept: the frame is found and a grid sampled, Reed-Solomon refuses it, and nothing is read.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Decode_StrongKeystoneDataDestroyed_DoesNotRead(RmQRVersion version, float pixelsPerModule, float degrees, float keystone, float tilt)
    {
        var (_, qr) = Symbol(version);
        var random = new Random((int)version * 131 + (int)degrees);
        var dark = new bool[qr.Width * qr.Height];
        for (var row = 0; row < qr.Height; row++)
        {
            for (var column = 0; column < qr.Width; column++)
                dark[row * qr.Width + column] = RmQRModulePlacer.IsFunctionModule(version, row, column) ? qr[row, column] : random.Next(2) == 0;
        }
        var width = qr.Width;
        var (luminance, side, _) = SupersampledRenderer.Render((row, column) => dark[row * width + column], qr.Width, qr.Height, pixelsPerModule, degrees, keystone, tilt);

        var success = RmQRCodeDecoder.TryDecodeImage(luminance, side, side, out var text, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(text).IsEmpty();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DataUncorrectable).Because($"{version}: the grid is found and its data refused");
    }

#if !DEBUG
    /// <summary>Reads through the finder's outline and the traced perimeter into a span, a symbol narrowed along its short axis and one along its long axis: nothing allocated.</summary>
    [Test]
    [Arguments(RmQRVersion.R17x139, 4.5f, 17f, 0.2f, 0f)]
    [Arguments(RmQRVersion.R13x99, 3.5f, 163f, 0.2f, 90f)]
    public async Task Decode_StrongKeystone_SpanDestination_DoesNotAllocate(RmQRVersion version, float pixelsPerModule, float degrees, float keystone, float tilt)
    {
        var (content, qr) = Symbol(version);
        var (luminance, side, _) = SupersampledRenderer.Render((row, column) => qr[row, column], qr.Width, qr.Height, pixelsPerModule, degrees, keystone, tilt);
        var destination = new char[RmQRCodeDecoder.GetMaxDecodedLength(version)];

        // Warm up (JIT, pool)
        RmQRCodeDecoder.TryDecodeImage(luminance, side, side, destination, out _, out _);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var success = RmQRCodeDecoder.TryDecodeImage(luminance, side, side, destination, out var charsWritten, out _);
        var after = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(success).IsTrue();
        await Assert.That(new string(destination, 0, charsWritten)).IsEqualTo(content);
        await Assert.That(after - before).IsEqualTo(0L);
    }
#endif

    /// <summary>
    /// A render drawn from <paramref name="seed"/>: a module size in <paramref name="minPixelsPerModule"/> to <paramref name="maxPixelsPerModule"/>, a turn and a keystone of 6 to 20 % drawn from the same generator, which then adds the noise or the blur.
    /// </summary>
    private static (byte[] Luminance, int Side) Degraded(RmQRCodeData qr, int seed, float minPixelsPerModule, float maxPixelsPerModule, float tilt, Degradation degradation)
    {
        var random = new Random(seed);
        var pixelsPerModule = minPixelsPerModule + (float)random.NextDouble() * (maxPixelsPerModule - minPixelsPerModule);
        var degrees = (float)random.NextDouble() * 360f;
        var keystone = 0.06f + (float)random.NextDouble() * (0.2f - 0.06f);
        var (luminance, side, _) = SupersampledRenderer.Render((row, column) => qr[row, column], qr.Width, qr.Height, pixelsPerModule, degrees, keystone, tilt);
        switch (degradation)
        {
            case Degradation.LowContrastNoise:
                // The levels squeezed into 100-170, then ±24 a pixel
                for (var i = 0; i < luminance.Length; i++)
                    luminance[i] = (byte)Math.Clamp(100 + luminance[i] * 70 / 255 + random.Next(-24, 25), 0, 255);
                break;
            case Degradation.Blur:
                var sharp = luminance;
                luminance = new byte[sharp.Length];
                for (var y = 0; y < side; y++)
                {
                    for (var x = 0; x < side; x++)
                    {
                        var sum = 0;
                        for (var dy = -1; dy <= 1; dy++)
                            for (var dx = -1; dx <= 1; dx++)
                                sum += sharp[Math.Clamp(y + dy, 0, side - 1) * side + Math.Clamp(x + dx, 0, side - 1)];
                        luminance[y * side + x] = (byte)(sum / 9);
                    }
                }
                break;
        }
        return (luminance, side);
    }

    public enum Degradation
    {
        None,
        LowContrastNoise,
        Blur,
    }

    /// <summary>Sampled through the geometry it was drawn with, the render reads: a failure is the decoder's frame, not an illegible image.</summary>
    private static async Task AssertReadableThroughDrawnGeometry(byte[] luminance, int side, RmQRCodeData qr, float pixelsPerModule, float degrees, float keystone, float tilt)
    {
        var drawn = SupersampledGeometry.GridToPixel(qr.Width, qr.Height, pixelsPerModule, degrees, keystone, tilt);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance);
        var grid = new byte[qr.Width * qr.Height];
        RmQRImageDecoder.SampleGridScalar(luminance, side, side, threshold, drawn, qr.Width, qr.Height, grid);
        var status = RmQRMatrixDecoder.DecodeMatrix(grid, qr.Width, qr.Height, new char[64], out _, out _);
        await Assert.That(status).IsEqualTo(DecodeStatus.Success).Because("the render does not read through its own geometry");
    }

    private static (string Content, RmQRCodeData Symbol) Symbol(RmQRVersion version)
    {
        var content = RmQRConstants.GetWidth(version) <= 43 ? ShortContent : LongContent;
        return (content, RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 }));
    }
}
