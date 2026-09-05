using FeatherQR.SkiaSharp;
using SkiaSharp;
namespace FeatherQR.Tests;

/// <summary>
/// Tier-2 image decoding: mild perspective (keystone) distortion, alone and
/// combined with rotation and mirroring. The asserted tilt levels sit inside the
/// measured envelope with margin (probe results: versions 2-5 decode to ~15%
/// keystone, 10-15 to ~6-10%, version 1, no alignment pattern, parallelogram
/// fallback, to ~2%).
/// </summary>
public class QRCodeDecoderPerspectiveTest
{
    [Test]
    [Arguments(2, 0.04f)]
    [Arguments(2, 0.08f)]
    [Arguments(2, 0.12f)]
    [Arguments(5, 0.04f)]
    [Arguments(5, 0.08f)]
    [Arguments(5, 0.12f)]
    [Arguments(10, 0.04f)]
    [Arguments(10, 0.08f)]
    [Arguments(15, 0.04f)]
    [Arguments(15, 0.06f)]
    // Version 14+ engages the piecewise alignment mesh: local anchors extend the
    // envelope where the single global homography's fourth anchor degrades
    [Arguments(15, 0.08f)]
    [Arguments(20, 0.04f)] // regression guard: snapped one version low / bent global anchor before the mesh
    [Arguments(25, 0.04f)]
    [Arguments(25, 0.08f)]
    public async Task Decode_Keystone(int version, float tilt)
    {
        var content = $"perspective v{version} tilt {tilt}";
        using var bitmap = RenderKeystone(content, version, tilt, rotateDegrees: 0);

        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out var decoded, out var info)).IsTrue().Because($"v={version}, tilt={tilt:P0}, status={info.Status}, detected v{info.Version}");
        await Assert.That(decoded).IsEquivalentTo(content);
        await Assert.That(info.Version).IsEquivalentTo(version);
    }

    [Test]
    public async Task Decode_Keystone_Version1_ParallelogramFallback()
    {
        // Version 1 has no alignment pattern; the fourth point is estimated, so
        // only very mild perspective is absorbed, that boundary is by design.
        var content = "v1 fallback";
        using var bitmap = RenderKeystone(content, version: 1, tilt: 0.02f, rotateDegrees: 0);

        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out var decoded, out var info)).IsTrue().Because($"status={info.Status}");
        await Assert.That(decoded).IsEquivalentTo(content);
    }

    [Test]
    [Arguments(2, 30, 0.05f)]
    [Arguments(5, 30, 0.05f)]
    [Arguments(10, 30, 0.05f)]
    [Arguments(5, -20, 0.08f)]
    public async Task Decode_RotationPlusKeystone(int version, float degrees, float tilt)
    {
        var content = $"rot{degrees} tilt{tilt} v{version}";
        using var bitmap = RenderKeystone(content, version, tilt, degrees);

        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out var decoded, out var info)).IsTrue().Because($"v={version}, rot={degrees}, tilt={tilt:P0}, status={info.Status}");
        await Assert.That(decoded).IsEquivalentTo(content);
    }

    [Test]
    public async Task Decode_MirrorPlusKeystone()
    {
        // The alignment pattern sits on the transpose-invariant diagonal, so the
        // mirror retry composes with perspective sampling.
        var content = "mirror + keystone";
        using var source = RenderKeystone(content, version: 5, tilt: 0.05f, rotateDegrees: 0);
        using var mirrored = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(mirrored))
        {
            canvas.Clear(SKColors.White);
            canvas.Scale(-1, 1, source.Width / 2f, 0);
            canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
        }

        await Assert.That(QRCodeDecoder.TryDecode(mirrored, out var decoded, out var info)).IsTrue().Because($"status={info.Status}");
        await Assert.That(decoded).IsEquivalentTo(content);
    }

    /// <summary>
    /// Renders a QR code and warps it with a keystone homography (top edge shrunk
    /// by <paramref name="tilt"/> of the width per side), optionally rotated —
    /// simulating an off-axis capture of a flat print.
    /// </summary>
    private static SKBitmap RenderKeystone(string content, int version, float tilt, float rotateDegrees)
    {
        var qr = QRCodeGenerator.Create(content, QREccLevel.M, new QRCodeGeneratorOptions { Version = version, QuietZoneSize = 4 });
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
                qrPx, qrPx,
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

    /// <summary>
    /// Homography mapping the (0,0)-(w,h) rectangle onto the given quadrilateral
    /// (top-left, top-right, bottom-right, bottom-left).
    /// </summary>
    private static SKMatrix SquareToQuad(float w, float h, SKPoint tl, SKPoint tr, SKPoint br, SKPoint bl)
    {
        var dx3 = tl.X - tr.X + br.X - bl.X;
        var dy3 = tl.Y - tr.Y + br.Y - bl.Y;
        float a13, a23, a11, a21, a12, a22;
        if (dx3 == 0f && dy3 == 0f)
        {
            a11 = tr.X - tl.X;
            a21 = br.X - tr.X;
            a12 = tr.Y - tl.Y;
            a22 = br.Y - tr.Y;
            a13 = 0f;
            a23 = 0f;
        }
        else
        {
            var dx1 = tr.X - br.X;
            var dx2 = bl.X - br.X;
            var dy1 = tr.Y - br.Y;
            var dy2 = bl.Y - br.Y;
            var denominator = dx1 * dy2 - dx2 * dy1;
            a13 = (dx3 * dy2 - dx2 * dy3) / denominator;
            a23 = (dx1 * dy3 - dx3 * dy1) / denominator;
            a11 = tr.X - tl.X + a13 * tr.X;
            a21 = bl.X - tl.X + a23 * bl.X;
            a12 = tr.Y - tl.Y + a13 * tr.Y;
            a22 = bl.Y - tl.Y + a23 * bl.Y;
        }

        return new SKMatrix
        {
            ScaleX = a11 / w,
            SkewX = a21 / h,
            TransX = tl.X,
            SkewY = a12 / w,
            ScaleY = a22 / h,
            TransY = tl.Y,
            Persp0 = a13 / w,
            Persp1 = a23 / h,
            Persp2 = 1f,
        };
    }
}
