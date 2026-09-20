using FeatherQR.SkiaSharp;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// Micro QR samples the whole symbol from one finder, so the finder's module size is
/// extrapolated across it. The builder snaps every module to whole pixels, and at about
/// 1.95 px/module a finder that lands on 2 px modules measures 2.000 against a true 1.947:
/// the far modules are sampled most of a pixel off. The timing patterns run from the finder
/// to the far edge along row 0 and column 0, so they measure the whole symbol instead.
/// </summary>
public class MicroQRTimingFrameTest
{
    private const string Content = "12345";

    [Test]
    [Arguments(MicroQRVersion.M3, 37)]
    [Arguments(MicroQRVersion.M4, 41)]
    public async Task Decode_SnappedModuleWidths_Decodes(MicroQRVersion version, int sizePx)
    {
        var data = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = version });
        using var bitmap = new MicroQRCodeImageBuilder(data).WithSize(sizePx, sizePx).ToBitmap();

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version} at {sizePx} px: {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
        await DrawnCorners.AssertMatch(bitmap, info.Corners, 2 * (int)version + 9, 0.5f);
    }

    /// <summary>
    /// The same renders flipped left to right: the timing frame reads the grid transposed, and the
    /// corners must follow the symbol, not the image.
    /// </summary>
    [Test]
    [Arguments(MicroQRVersion.M3, 37)]
    [Arguments(MicroQRVersion.M4, 41)]
    public async Task Decode_SnappedModuleWidths_Mirrored_Decodes(MicroQRVersion version, int sizePx)
    {
        var data = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = version });
        using var drawn = new MicroQRCodeImageBuilder(data).WithSize(sizePx, sizePx).ToBitmap();
        using var bitmap = DrawnCorners.MirrorLeftToRight(drawn);

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version} at {sizePx} px, mirrored: {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
        await DrawnCorners.AssertMatch(bitmap, info.Corners, 2 * (int)version + 9, 0.5f, mirrored: true);
    }

    public static IEnumerable<(MicroQRVersion, int)> EveryFractionalSize()
    {
        foreach (var version in new[] { MicroQRVersion.M1, MicroQRVersion.M2, MicroQRVersion.M3, MicroQRVersion.M4 })
        {
            var size = (int)version * 2 + 9 + 4;
            for (var px = size * 3 / 2; px <= size * 3; px++)
                yield return (version, px);
        }
    }

    /// <summary>Every builder size from 1.5 to 3 px/module, with all four right-angle rotations.</summary>
    [Test]
    [MethodDataSource(nameof(EveryFractionalSize))]
    public async Task Decode_EveryFractionalBuilderSize_Decodes(MicroQRVersion version, int sizePx)
    {
        // M1 has error detection only, and so the least protection against a misread grid
        var eccLevel = version == MicroQRVersion.M1 ? MicroQREccLevel.ErrorDetectionOnly : MicroQREccLevel.L;
        var data = MicroQRCodeGenerator.Create(Content, eccLevel, new MicroQRCodeGeneratorOptions { Version = version });
        using var bitmap = new MicroQRCodeImageBuilder(data).WithSize(sizePx, sizePx).ToBitmap();

        foreach (var degrees in new[] { 0, 90, 180, 270 })
        {
            using var rotated = Rotate(bitmap, degrees);
            var success = MicroQRCodeDecoder.TryDecode(rotated, out var text, out var info);

            await Assert.That(success).IsTrue().Because($"{version} at {sizePx} px, {degrees} deg: {info.Status}");
            await Assert.That(text).IsEqualTo(Content);
        }
    }

    private static SKBitmap Rotate(SKBitmap source, int degrees)
    {
        var swap = degrees % 180 != 0;
        var rotated = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height);
        using var canvas = new SKCanvas(rotated);
        canvas.Clear(SKColors.White);
        canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);
        return rotated;
    }
}
