using FeatherQR.SkiaSharp;
using SkiaSharp;
namespace FeatherQR.Tests;

/// <summary>
/// Micro QR image decoding under keystone, alone and with rotation or mirroring,
/// across the measured envelope.
/// </summary>
public class MicroQRCodeDecoderPerspectiveTest
{
    public enum KeystoneEdge { Top, Bottom, Left, Right }

    [Test]
    [Arguments(MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly, "123", 0.02f)]
    [Arguments(MicroQRVersion.M2, MicroQREccLevel.L, "12345", 0.02f)]
    [Arguments(MicroQRVersion.M3, MicroQREccLevel.L, "HELLO WORLD", 0.04f)]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.M, "MICRO QR M4 TEST", 0.02f)]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.M, "MICRO QR M4 TEST", 0.04f)]
    public async Task Decode_Keystone(MicroQRVersion version, MicroQREccLevel eccLevel, string content, float tilt)
    {
        using var bitmap = RenderKeystone(content, version, eccLevel, tilt, rotateDegrees: 0);

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out var decoded, out var info);

        await Assert.That(success).IsTrue().Because($"version={version}, tilt={tilt:P0}, status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);
        await Assert.That(info.Version).IsEqualTo(version);
    }

    public static IEnumerable<(MicroQRVersion Version, MicroQREccLevel EccLevel, string Content, KeystoneEdge Edge)> EnvelopeEdgeCases()
    {
        foreach (var edge in new[] { KeystoneEdge.Top, KeystoneEdge.Bottom, KeystoneEdge.Left, KeystoneEdge.Right })
        {
            yield return (MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly, "123", edge);
            yield return (MicroQRVersion.M2, MicroQREccLevel.L, "12345", edge);
            yield return (MicroQRVersion.M3, MicroQREccLevel.L, "HELLO WORLD", edge);
            yield return (MicroQRVersion.M4, MicroQREccLevel.M, "MICRO QR M4 TEST", edge);
        }
    }

    [Test]
    [MethodDataSource(nameof(EnvelopeEdgeCases))]
    public async Task Decode_Keystone8Percent_EachEdge(MicroQRVersion version, MicroQREccLevel eccLevel, string content, KeystoneEdge edge)
    {
        using var bitmap = RenderKeystone(content, version, eccLevel, 0.08f, rotateDegrees: 0, edge);

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out var decoded, out var info);

        await Assert.That(success).IsTrue().Because($"version={version}, edge={edge}, status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);
        await Assert.That(info.Version).IsEqualTo(version);
    }

    /// <summary>
    /// The lowest densities the envelope was measured at: 5 px/module drawn crisp, 3 px/module with grey edges.
    /// </summary>
    [Test]
    [Arguments(5, false)]
    [Arguments(3, true)]
    public async Task Decode_Keystone8Percent_LowestDensity(int pixelsPerModule, bool greyEdges)
    {
        const string content = "MICRO QR M4 TEST";
        using var bitmap = RenderKeystone(content, MicroQRVersion.M4, MicroQREccLevel.M, 0.08f, rotateDegrees: 0, KeystoneEdge.Bottom, pixelsPerModule, greyEdges);

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out var decoded, out var info);

        await Assert.That(success).IsTrue().Because($"px/module={pixelsPerModule}, greyEdges={greyEdges}, status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);
    }

    /// <summary>
    /// Keystones only the perspective search read when measured (2026-09-27): with the search removed, each of these fails.
    /// </summary>
    [Test]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.L, "MICRO QR M4 TEST L", KeystoneEdge.Top, 0, false)]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.L, "MICRO QR M4 TEST L", KeystoneEdge.Left, 0, false)]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.M, "MICRO QR M4 TEST", KeystoneEdge.Bottom, 0, false)]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.M, "MICRO QR M4 TEST", KeystoneEdge.Bottom, 0, true)]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.M, "MICRO QR M4 TEST", KeystoneEdge.Right, 170, false)]
    [Arguments(MicroQRVersion.M3, MicroQREccLevel.L, "HELLO WORLD", KeystoneEdge.Bottom, 80, false)]
    public async Task Decode_Keystone8Percent_PerspectiveSearchCases(MicroQRVersion version, MicroQREccLevel eccLevel, string content, KeystoneEdge edge, int degrees, bool mirrored)
    {
        using var bitmap = RenderKeystone(content, version, eccLevel, 0.08f, degrees, edge, mirrored: mirrored);

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out var decoded, out var info);

        await Assert.That(success).IsTrue().Because($"version={version}, edge={edge}, degrees={degrees}, mirrored={mirrored}, status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);
        await Assert.That(info.Version).IsEqualTo(version);
    }

    [Test]
    [Arguments(MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly, "123", 0.02f, 30)]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.M, "MICRO QR M4 TEST", 0.04f, 30)]
    public async Task Decode_RotationPlusKeystone(MicroQRVersion version, MicroQREccLevel eccLevel, string content, float tilt, int degrees)
    {
        using var bitmap = RenderKeystone(content, version, eccLevel, tilt, degrees);

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out var decoded, out var info);

        await Assert.That(success).IsTrue().Because($"version={version}, tilt={tilt:P0}, degrees={degrees}, status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Decode_MirrorPlusKeystone()
    {
        const string content = "MICRO QR M4 TEST";
        using var mirrored = RenderKeystone(content, MicroQRVersion.M4, MicroQREccLevel.M, tilt: 0.02f, rotateDegrees: 0, mirrored: true);

        var success = MicroQRCodeDecoder.TryDecode(mirrored, out var decoded, out var info);

        await Assert.That(success).IsTrue().Because($"status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);
    }

    /// <summary>
    /// The symbol with a 2-module quiet zone, one edge shortened by <paramref name="tilt"/> of its width at each end, then turned about the canvas centre and optionally mirrored.
    /// </summary>
    private static SKBitmap RenderKeystone(
        string content,
        MicroQRVersion version,
        MicroQREccLevel eccLevel,
        float tilt,
        float rotateDegrees,
        KeystoneEdge edge = KeystoneEdge.Top,
        int pixelsPerModule = 8,
        bool greyEdges = false,
        bool mirrored = false)
    {
        var qr = MicroQRCodeGenerator.Create(content, eccLevel, new MicroQRCodeGeneratorOptions { Version = version, QuietZoneSize = 2 });
        var qrPx = qr.Size * pixelsPerModule;

        using var flat = new SKBitmap(new SKImageInfo(qrPx, qrPx, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(flat))
        {
            SymbolRenderer.Render(canvas, SKRect.Create(0, 0, qrPx, qrPx), qr, SKColors.Black, SKColors.White);
            canvas.Flush();
        }

        var canvasPx = (int)(qrPx * 1.6f) + 32;
        var result = new SKBitmap(new SKImageInfo(canvasPx, canvasPx, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(result))
        {
            canvas.Clear(SKColors.White);

            var m = (canvasPx - qrPx) / 2f;
            var s = tilt * qrPx;
            var q = (float)qrPx;
            var (topLeft, topRight, bottomRight, bottomLeft) = edge switch
            {
                KeystoneEdge.Top => (new SKPoint(m + s, m), new SKPoint(m + q - s, m), new SKPoint(m + q, m + q), new SKPoint(m, m + q)),
                KeystoneEdge.Bottom => (new SKPoint(m, m), new SKPoint(m + q, m), new SKPoint(m + q - s, m + q), new SKPoint(m + s, m + q)),
                KeystoneEdge.Left => (new SKPoint(m, m + s), new SKPoint(m + q, m), new SKPoint(m + q, m + q), new SKPoint(m, m + q - s)),
                _ => (new SKPoint(m, m), new SKPoint(m + q, m + s), new SKPoint(m + q, m + q - s), new SKPoint(m, m + q)),
            };
            var warp = SquareToQuad(q, q, topLeft, topRight, bottomRight, bottomLeft);

            if (rotateDegrees != 0)
            {
                var rotation = SKMatrix.CreateRotationDegrees(rotateDegrees, canvasPx / 2f, canvasPx / 2f);
                warp = rotation.PreConcat(warp);
            }

            canvas.SetMatrix(warp);
            canvas.DrawBitmap(flat, 0, 0, greyEdges ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None) : SKSamplingOptions.Default);
            canvas.Flush();
        }

        if (!mirrored)
            return result;

        using (result)
        {
            var mirror = new SKBitmap(new SKImageInfo(result.Width, result.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(mirror);
            canvas.Clear(SKColors.White);
            canvas.Scale(-1, 1, result.Width / 2f, 0);
            canvas.DrawBitmap(result, 0, 0, SKSamplingOptions.Default);
            return mirror;
        }
    }

    private static SKMatrix SquareToQuad(float width, float height, SKPoint topLeft, SKPoint topRight, SKPoint bottomRight, SKPoint bottomLeft)
    {
        var dx3 = topLeft.X - topRight.X + bottomRight.X - bottomLeft.X;
        var dy3 = topLeft.Y - topRight.Y + bottomRight.Y - bottomLeft.Y;
        float a13, a23, a11, a21, a12, a22;
        if (dx3 == 0f && dy3 == 0f)
        {
            a11 = topRight.X - topLeft.X;
            a21 = bottomRight.X - topRight.X;
            a12 = topRight.Y - topLeft.Y;
            a22 = bottomRight.Y - topRight.Y;
            a13 = 0f;
            a23 = 0f;
        }
        else
        {
            var dx1 = topRight.X - bottomRight.X;
            var dx2 = bottomLeft.X - bottomRight.X;
            var dy1 = topRight.Y - bottomRight.Y;
            var dy2 = bottomLeft.Y - bottomRight.Y;
            var denominator = dx1 * dy2 - dx2 * dy1;
            a13 = (dx3 * dy2 - dx2 * dy3) / denominator;
            a23 = (dx1 * dy3 - dx3 * dy1) / denominator;
            a11 = topRight.X - topLeft.X + a13 * topRight.X;
            a21 = bottomLeft.X - topLeft.X + a23 * bottomLeft.X;
            a12 = topRight.Y - topLeft.Y + a13 * topRight.Y;
            a22 = bottomLeft.Y - topLeft.Y + a23 * bottomLeft.Y;
        }

        return new SKMatrix
        {
            ScaleX = a11 / width,
            SkewX = a21 / height,
            TransX = topLeft.X,
            SkewY = a12 / width,
            ScaleY = a22 / height,
            TransY = topLeft.Y,
            Persp0 = a13 / width,
            Persp1 = a23 / height,
            Persp2 = 1f,
        };
    }
}
