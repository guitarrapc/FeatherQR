namespace FeatherQR.Tests;

/// <summary>
/// The camera renderer's geometry, checked before a sweep or a decode test relies on it: front-on it is a plain scaling, a tilt, a bow and a lens move points as a camera would, the same shot draws the same pixels, and the decoder reports the corners the renderer says it drew.
/// </summary>
public class CameraRendererTest
{
    private const string Content = "CAMERA RENDERER 0123456789";

    private static QRCodeData StandardQR(int version) => QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });

    private static CameraRenderer.Photograph Photograph(QRCodeData qr, CameraRenderer.Shot shot) => CameraRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, 4, shot);

    private static double Distance((float X, float Y) a, (float X, float Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    [Test]
    public async Task FrontOn_IsAPlainScaling_AndDrawsWhereItSays()
    {
        var qr = StandardQR(3);
        var photo = Photograph(qr, new CameraRenderer.Shot(PixelsPerModule: 4f));
        var (topLeft, topRight, bottomRight, bottomLeft) = (photo.Corners[0], photo.Corners[1], photo.Corners[2], photo.Corners[3]);

        await Assert.That(topRight.X - topLeft.X).IsEqualTo(qr.Size * 4f).Within(1e-3f);
        await Assert.That(bottomLeft.Y - topLeft.Y).IsEqualTo(qr.Size * 4f).Within(1e-3f);
        await Assert.That(topRight.Y - topLeft.Y).IsEqualTo(0f).Within(1e-3f);
        await Assert.That(bottomRight.X - bottomLeft.X).IsEqualTo(qr.Size * 4f).Within(1e-3f);

        // The ink is the symbol: its finders reach every edge of the module area. The table under the card is grey, lighter than the ink
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < photo.Height; y++)
        {
            for (var x = 0; x < photo.Width; x++)
            {
                if (photo.Luminance[y * photo.Width + x] >= 62)
                    continue;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x + 1);
                maxY = Math.Max(maxY, y + 1);
            }
        }
        await Assert.That(Math.Abs(minX - topLeft.X)).IsLessThanOrEqualTo(1f);
        await Assert.That(Math.Abs(minY - topLeft.Y)).IsLessThanOrEqualTo(1f);
        await Assert.That(Math.Abs(maxX - bottomRight.X)).IsLessThanOrEqualTo(1f);
        await Assert.That(Math.Abs(maxY - bottomRight.Y)).IsLessThanOrEqualTo(1f);
    }

    [Test]
    public async Task Tilt_ShortensTheFarEdge_AsAPinholeDoes()
    {
        var qr = StandardQR(5);
        const float tilt = 50f;
        const float distance = 2.5f;
        var photo = Photograph(qr, new CameraRenderer.Shot(PixelsPerModule: 4f, TiltDegrees: tilt, Distance: distance));

        // Tilted about the rows' axis, the top edge comes h·sin(tilt) nearer than the centre and the bottom edge goes as far behind
        var top = Distance(photo.Corners[0], photo.Corners[1]);
        var bottom = Distance(photo.Corners[3], photo.Corners[2]);
        var depth = distance * qr.Size;
        var offset = qr.Size / 2.0 * Math.Sin(tilt * Math.PI / 180.0);
        await Assert.That(top / bottom).IsEqualTo((depth + offset) / (depth - offset)).Within(1e-6);
    }

    [Test]
    public async Task Bow_CurvesTheTopEdge_AndAFlatCardDoesNot()
    {
        var qr = StandardQR(5);
        foreach (var (bow, curved) in new[] { (3f, true), (0f, false) })
        {
            var photo = Photograph(qr, new CameraRenderer.Shot(PixelsPerModule: 4f, BowModules: bow));
            var (left, right) = (photo.Corners[0], photo.Corners[1]);
            var middle = photo.ToPixel(qr.Size / 2.0, 0);
            var chordY = (left.Y + right.Y) / 2f;
            var offChord = Math.Abs(middle.Y - chordY) / 4f;
            if (curved)
                await Assert.That(offChord).IsGreaterThan(0.25f).Because($"a {bow}-module bow left the top edge {offChord:F3} modules off its chord");
            else
                await Assert.That(offChord).IsLessThan(1e-3f);
        }
    }

    [Test]
    public async Task Barrel_DrawsTheCornersNearer_ByTheFactorItIsGiven()
    {
        var qr = StandardQR(5);
        var straight = Photograph(qr, new CameraRenderer.Shot(PixelsPerModule: 4f));
        var barrel = Photograph(qr, new CameraRenderer.Shot(PixelsPerModule: 4f, Distortion: -0.1f));
        var straightCentre = straight.ToPixel(qr.Size / 2.0, qr.Size / 2.0);
        var barrelCentre = barrel.ToPixel(qr.Size / 2.0, qr.Size / 2.0);
        for (var i = 0; i < 4; i++)
            await Assert.That(Distance(barrel.Corners[i], barrelCentre) / Distance(straight.Corners[i], straightCentre)).IsEqualTo(0.9).Within(1e-5);
    }

    [Test]
    public async Task SameShot_DrawsTheSamePixels_AndAnotherSeedDoesNot()
    {
        var qr = StandardQR(4);
        var shot = new CameraRenderer.Shot(PixelsPerModule: 3.5f, TiltDegrees: 25f, TiltAxisDegrees: 30f, RollDegrees: 10f, BlurSigma: 0.7f, NoiseSigma: 6f, SceneScale: 2.5f, Seed: 11);
        var first = Photograph(qr, shot);
        var again = Photograph(qr, shot);
        var other = Photograph(qr, shot with { Seed = 12 });

        await Assert.That(again.Luminance).IsEquivalentTo(first.Luminance, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(other.Luminance.AsSpan().SequenceEqual(first.Luminance)).IsFalse();
    }

    [Test]
    public async Task Scene_IsLargerThanTheCard_AndHoldsIt()
    {
        var qr = StandardQR(3);
        var card = Photograph(qr, new CameraRenderer.Shot(PixelsPerModule: 3f));
        var scene = Photograph(qr, new CameraRenderer.Shot(PixelsPerModule: 3f, SceneScale: 3f, Seed: 5));

        await Assert.That(scene.Width).IsGreaterThan(2 * card.Width);
        foreach (var (x, y) in scene.Corners)
        {
            await Assert.That(x).IsBetween(0f, scene.Width);
            await Assert.That(y).IsBetween(0f, scene.Height);
        }
    }

    public static IEnumerable<(int Version, float Tilt, float Axis, float Roll)> TiltedStandardQR()
    {
        yield return (2, 30f, 20f, 35f);
        yield return (7, 40f, 110f, 200f);
        yield return (15, 20f, 60f, 300f);
    }

    [Test]
    [MethodDataSource(nameof(TiltedStandardQR))]
    public async Task TiltedPhotograph_Decodes_WithTheCornersItWasDrawnWith(int version, float tilt, float axis, float roll)
    {
        var qr = StandardQR(version);
        var photo = Photograph(qr, new CameraRenderer.Shot(PixelsPerModule: 5f, TiltDegrees: tilt, TiltAxisDegrees: axis, RollDegrees: roll, BlurSigma: 0.6f, NoiseSigma: 3f, Seed: version));

        var success = QRCodeDecoder.TryDecodeImage(photo.Luminance, photo.Width, photo.Height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"version {version}, tilt {tilt}° about {axis}°, roll {roll}°: {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
        ImagePoint[] reported = [info.Corners.TopLeft, info.Corners.TopRight, info.Corners.BottomRight, info.Corners.BottomLeft];
        for (var i = 0; i < 4; i++)
        {
            var off = Distance((reported[i].X, reported[i].Y), photo.Corners[i]) / 5f;
            await Assert.That(off).IsLessThan(0.75).Because($"corner {i} is {off:F2} modules from where it was drawn");
        }
    }

    [Test]
    public async Task MicroQRAndRmQR_Photographs_Decode()
    {
        var micro = MicroQRCodeGenerator.Create("CAMERA 42", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.Exactly(MicroQRVersion.M4), QuietZoneSize = 0 });
        var microPhoto = CameraRenderer.Render((row, column) => micro[row, column], micro.Size, micro.Size, 2, new CameraRenderer.Shot(PixelsPerModule: 6f, TiltDegrees: 8f, TiltAxisDegrees: 45f, RollDegrees: 70f, BlurSigma: 0.5f, NoiseSigma: 2f, Seed: 1));
        await Assert.That(MicroQRCodeDecoder.TryDecodeImage(microPhoto.Luminance, microPhoto.Width, microPhoto.Height, out var microText, out var microInfo)).IsTrue().Because(microInfo.Status.ToString());
        await Assert.That(microText).IsEqualTo("CAMERA 42");

        var rmqr = RmQRCodeGenerator.Create("CAMERA 42", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x59, QuietZoneSize = 0 });
        var rmqrPhoto = CameraRenderer.Render((row, column) => rmqr[row, column], rmqr.Width, rmqr.Height, 2, new CameraRenderer.Shot(PixelsPerModule: 6f, TiltDegrees: 8f, TiltAxisDegrees: 0f, RollDegrees: 15f, BlurSigma: 0.5f, NoiseSigma: 2f, Seed: 2));
        await Assert.That(RmQRCodeDecoder.TryDecodeImage(rmqrPhoto.Luminance, rmqrPhoto.Width, rmqrPhoto.Height, out var rmqrText, out var rmqrInfo)).IsTrue().Because(rmqrInfo.Status.ToString());
        await Assert.That(rmqrText).IsEqualTo("CAMERA 42");
    }
}
