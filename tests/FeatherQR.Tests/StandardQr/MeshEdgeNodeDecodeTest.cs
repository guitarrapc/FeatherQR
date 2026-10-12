using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// A version 25 or 30 symbol on a bent card, photographed at a tilt, through blur and sensor noise, with the small modules of a distant shot: the grid through the finders and the alignment anchor reads by no sampling, and the mesh over the alignment lattice reads, its nodes on the timing lines and beside the finders measured with the rest.
/// The shots were chosen from a sweep of tilts, bends, blurs, noise and distances at levels L and M where a mesh whose coordinate-6 row and column were extrapolated from the interior nodes did not read.
/// </summary>
public class MeshEdgeNodeDecodeTest
{
    [Test]
    [Arguments(25, QREccLevel.L, 4f, 15f, 2f, 0f, 0f, 1.5f)]
    [Arguments(25, QREccLevel.L, 3f, 15f, 4f, 0f, 0f, 1.5f)]
    [Arguments(30, QREccLevel.L, 3f, 15f, 4f, 1f, 10f, 1.5f)]
    [Arguments(25, QREccLevel.L, 3f, 25f, 2f, 0f, 0f, 2.5f)]
    [Arguments(30, QREccLevel.M, 3f, 15f, 2f, 1f, 10f, 2.5f)]
    public async Task BentHighVersionPhotograph_ReadsThroughTheMesh(int version, QREccLevel level, float pixelsPerModule, float tiltDegrees, float bowModules, float blurSigma, float noiseSigma, float distance)
    {
        var (photograph, qr, text) = Render(version, level, pixelsPerModule, tiltDegrees, bowModules, blurSigma, noiseSigma, distance, destroyData: false);
        await Assert.That(LensDistortionDecodeTest.FailedPremise(photograph, qr)).IsNull();

        var success = QRCodeDecoder.TryDecodeImage(photograph.Luminance, photograph.Width, photograph.Height, out var decoded, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(decoded).IsEqualTo(text);
    }

    /// <summary>The negative the mesh must not admit: the same photograph with its data destroyed reads nothing.</summary>
    [Test]
    [Arguments(25, QREccLevel.L, 4f, 15f, 2f, 0f, 0f, 1.5f)]
    [Arguments(30, QREccLevel.L, 3f, 15f, 4f, 1f, 10f, 1.5f)]
    public async Task BentHighVersionPhotograph_DestroyedData_ReadsNothing(int version, QREccLevel level, float pixelsPerModule, float tiltDegrees, float bowModules, float blurSigma, float noiseSigma, float distance)
    {
        var (photograph, _, _) = Render(version, level, pixelsPerModule, tiltDegrees, bowModules, blurSigma, noiseSigma, distance, destroyData: true);

        var success = QRCodeDecoder.TryDecodeImage(photograph.Luminance, photograph.Width, photograph.Height, out _, out var info);

        await Assert.That(success).IsFalse().Because(info.Status.ToString());
    }

    /// <summary>The symbol on a card tilted about its columns' axis and bent round a cylinder, photographed from the distance given.</summary>
    internal static (CameraRenderer.Photograph Photograph, QRCodeData Symbol, string Text) Render(int version, QREccLevel level, float pixelsPerModule, float tiltDegrees, float bowModules, float blurSigma, float noiseSigma, float distance, bool destroyData)
    {
        var text = $"FQR MESH EDGE v{version} 0123456789";
        var qr = QRCodeGenerator.Create(text, level, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
        var random = new Random(4321);
        var destroyed = new bool[qr.Size * qr.Size];
        for (var i = 0; i < destroyed.Length; i++)
            destroyed[i] = random.Next(2) == 0;
        // Every module between the finders' rows and columns but the alignment patterns, clear of both format and both version copies
        bool IsDark(int row, int column) => destroyData && row >= 9 && column >= 9 && row < qr.Size - 11 && column < qr.Size - 11 && !IsAlignment(row, column) ? destroyed[row * qr.Size + column] : qr[row, column];
        bool IsAlignment(int row, int column)
        {
            foreach (var r in QRCodeConstants.AlignmentPatternBaseValues.Slice((version - 1) * 7, 7))
            {
                if (r == 0 || Math.Abs(row - r) > 2)
                    continue;
                foreach (var c in QRCodeConstants.AlignmentPatternBaseValues.Slice((version - 1) * 7, 7))
                {
                    if (c != 0 && Math.Abs(column - c) <= 2)
                        return true;
                }
            }
            return false;
        }
        var photograph = CameraRenderer.Render(IsDark, qr.Size, qr.Size, 4, new CameraRenderer.Shot(pixelsPerModule, TiltDegrees: tiltDegrees, TiltAxisDegrees: 90f, BowModules: bowModules, BlurSigma: blurSigma, NoiseSigma: noiseSigma, Distance: distance));
        return (photograph, qr, text);
    }
}
