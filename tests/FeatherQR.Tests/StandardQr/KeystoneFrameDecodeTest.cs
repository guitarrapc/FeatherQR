using FeatherQR.Internals.ImageDecoders;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// Symbols in perspective whose fourth corner the parallelogram of the finder centres misplaces: the bottom-right alignment pattern is searched where the finders' foreshortening puts it, and a symbol with nothing to anchor that corner is sampled through the finders' own frame after the parallelogram.
/// </summary>
public class KeystoneFrameDecodeTest
{
    private const string Content = "FQR KEYSTONE 0123";

    /// <summary>The symbol with the generator's own 4-module quiet zone, which the renderer draws another four modules round.</summary>
    private static QRCodeData StandardQR(int version) => QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version) });

    /// <summary>Large symbols whose bottom-right alignment pattern the parallelogram predicts a lattice step off.</summary>
    [Test]
    [Arguments(25, 3.2f, 80f, 0.2f)]
    [Arguments(35, 3.2f, 240f, 0.2f)]
    [Arguments(40, 4f, 150f, 0.2f)]
    [Arguments(40, 5f, 80f, 0.2f)]
    public async Task LargeVersionKeystone_AnchorsWhereTheFrameExpects(int version, float pixelsPerModule, float degrees, float keystone)
        => await AssertDecodes(version, pixelsPerModule, degrees, keystone, binarized: false);

    /// <summary>Version 1 has no alignment pattern: the parallelogram assumes the symbol flat, and the frame places its fourth corner.</summary>
    [Test]
    [Arguments(4.5f, 100f, 0.15f)]
    [Arguments(3f, 200f, 0.2f)]
    [Arguments(6f, 300f, 0.15f)]
    public async Task Version1Keystone_ReadsThroughTheFrame(float pixelsPerModule, float degrees, float keystone)
        => await AssertDecodes(1, pixelsPerModule, degrees, keystone, binarized: false);

    /// <summary>A binarized image has no grey edges to measure sizes on; whole-pixel runs that differ by more than their resolution still give the frame.</summary>
    [Test]
    [Arguments(28, 2.457f, 244.8f, 0.143f)]
    [Arguments(1, 4.131f, 211f, 0.17f)]
    public async Task BinarizedKeystone_ReadsThroughTheFrameOfWholePixelRuns(int version, float pixelsPerModule, float degrees, float keystone)
        => await AssertDecodes(version, pixelsPerModule, degrees, keystone, binarized: true);

    /// <summary>The frame is one more grid, not a looser read: a keystoned version 1 symbol whose data is destroyed does not decode.</summary>
    [Test]
    public async Task Version1KeystoneWithItsDataDestroyed_DoesNotDecode()
    {
        var qr = StandardQR(1);
        // Every module right of column 8 and below row 8 of the symbol, clear of the finders and both format copies
        bool IsDark(int row, int column) => row >= 13 && column >= 13 && row < qr.Size - 4 && column < qr.Size - 4 ? !qr[row, column] : qr[row, column];
        var (luminance, width, height) = SupersampledRenderer.Render(IsDark, qr.Size, qr.Size, 4.5f, 100f, 0.15f);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsFalse().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(string.Empty);
    }

    /// <summary>Photographs and captures from zxing-cpp's black-box samples (Apache-2.0), each at the four right angles the sweep reads them at.</summary>
    public static IEnumerable<(string, string, int)> RealImages()
    {
        foreach (var (set, file) in new[] { ("qrcode-3", "09.webp"), ("qrcode-3", "21.webp"), ("qrcode-3", "23.webp"), ("qrcode-2", "estimate-tilt.jpg"), ("qrcode-2", "low-res-v1-a.webp") })
        {
            for (var turns = 0; turns < 4; turns++)
                yield return (set, file, turns);
        }
    }

    [Test]
    [MethodDataSource(nameof(RealImages))]
    public async Task RealImageInPerspective_Decodes(string set, string file, int quarterTurns)
    {
        var path = Path.Combine(FixtureLoader.FixtureRoot, "RealImages", "zxing-cpp-samples", set, file);
        var expected = File.ReadAllText(Path.ChangeExtension(path, ".txt"));
        using var bitmap = SKBitmap.Decode(path);
        var luminance = new byte[bitmap.Width * bitmap.Height];
        BitmapLuminanceConverter.Convert(bitmap, luminance);
        var (turned, width, height) = NearestNeighbourRenderer.Turn(luminance, bitmap.Width, bitmap.Height, quarterTurns, mirror: false);

        var success = QRCodeDecoder.TryDecodeImage(turned, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{set}/{file} turned {quarterTurns * 90}°: {info.Status}");
        await Assert.That(text).IsEqualTo(expected);
    }

    /// <summary>Decodes the render and holds each reported corner within half a module of where the render drew it.</summary>
    private static async Task AssertDecodes(int version, float pixelsPerModule, float degrees, float keystone, bool binarized)
    {
        var qr = StandardQR(version);
        var size = qr.Size;
        var (luminance, width, height) = SupersampledRenderer.Render((row, column) => qr[row, column], size, size, pixelsPerModule, degrees, keystone);
        if (binarized)
        {
            for (var i = 0; i < luminance.Length; i++)
                luminance[i] = luminance[i] < 128 ? (byte)0 : (byte)255;
        }

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"version {version} at {pixelsPerModule} px/module turned {degrees}° under {keystone:P1} keystone: {info.Status}");
        await Assert.That(text).IsEqualTo(Content);

        // The symbol's module area sits inside the generator's quiet zone
        var truth = SupersampledGeometry.GridToPixel(size, size, pixelsPerModule, degrees, keystone);
        var far = size - 4f;
        await AssertNear(truth, info.Corners.TopLeft, 4f, 4f);
        await AssertNear(truth, info.Corners.TopRight, far, 4f);
        await AssertNear(truth, info.Corners.BottomRight, far, far);
        await AssertNear(truth, info.Corners.BottomLeft, 4f, far);
    }

    private static async Task AssertNear(PerspectiveTransform truth, ImagePoint actual, float u, float v)
    {
        truth.Transform(u, v, out var x, out var y);
        truth.Transform(u + 1f, v, out var nextX, out var nextY);
        var module = MathF.Sqrt((nextX - x) * (nextX - x) + (nextY - y) * (nextY - y));
        var off = MathF.Sqrt((actual.X - x) * (actual.X - x) + (actual.Y - y) * (actual.Y - y)) / module;
        await Assert.That(off).IsLessThan(0.5f).Because($"corner at grid ({u}, {v}) reported ({actual.X}, {actual.Y}), drawn ({x}, {y})");
    }
}
