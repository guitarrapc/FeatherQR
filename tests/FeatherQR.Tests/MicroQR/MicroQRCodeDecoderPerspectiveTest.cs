using FeatherQR.SkiaSharp;
using SkiaSharp;
namespace FeatherQR.Tests;

/// <summary>
/// Tier-2 Micro QR image decoding: mild perspective (keystone), including
/// composition with arbitrary rotation and mirroring. Micro QR has no alignment
/// patterns, so its measured perspective envelope is intentionally conservative.
/// </summary>
public class MicroQRCodeDecoderPerspectiveTest
{
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
        using var source = RenderKeystone(content, MicroQRVersion.M4, MicroQREccLevel.M, tilt: 0.02f, rotateDegrees: 0);
        using var mirrored = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(mirrored))
        {
            canvas.Clear(SKColors.White);
            canvas.Scale(-1, 1, source.Width / 2f, 0);
            canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
        }

        var success = MicroQRCodeDecoder.TryDecode(mirrored, out var decoded, out var info);

        await Assert.That(success).IsTrue().Because($"status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);
    }

    private static SKBitmap RenderKeystone(
        string content,
        MicroQRVersion version,
        MicroQREccLevel eccLevel,
        float tilt,
        float rotateDegrees)
    {
        var qr = MicroQRCodeGenerator.Create(content, eccLevel, new MicroQRCodeGeneratorOptions { Version = version, QuietZoneSize = 2 });
        var qrPx = qr.Size * 8;

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

            var margin = (canvasPx - qrPx) / 2f;
            var shrink = tilt * qrPx;
            var warp = SquareToQuad(
                qrPx,
                qrPx,
                new SKPoint(margin + shrink, margin),
                new SKPoint(margin + qrPx - shrink, margin),
                new SKPoint(margin + qrPx, margin + qrPx),
                new SKPoint(margin, margin + qrPx));

            if (rotateDegrees != 0)
            {
                var rotation = SKMatrix.CreateRotationDegrees(rotateDegrees, canvasPx / 2f, canvasPx / 2f);
                warp = rotation.PreConcat(warp);
            }

            canvas.SetMatrix(warp);
            canvas.DrawBitmap(flat, 0, 0, SKSamplingOptions.Default);
            canvas.Flush();
        }

        return result;
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
