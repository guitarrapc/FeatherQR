using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// rMQR reads its version from the finder-side format copy through the finder's own frame,
/// before anything else refines it. The builder snaps every module to whole pixels, and
/// below 2 px/module a finder can measure 3-6 % over the symbol's pitch: at column 11 that is
/// most of a pixel, the copy does not read, and the symbol is lost before the sub-finder can
/// correct the scale. The decoder now tries the scales nearest the measured one until the
/// copy reads.
/// </summary>
public class RmQRFormatScaleTest
{
    /// <summary>R7x43-M holds 7 alphanumerics; every other version takes the longer payload.</summary>
    private static string ContentFor(RmQRVersion version) => version == RmQRVersion.R7x43 ? "RMQR 43" : "RMQR 12345";

    [Test]
    [Arguments(RmQRVersion.R11x77, 137)]
    [Arguments(RmQRVersion.R13x99, 178)]
    [Arguments(RmQRVersion.R15x99, 178)]
    public async Task Decode_SnappedModuleWidths_Decodes(RmQRVersion version, int sizePx)
    {
        var content = ContentFor(version);
        var data = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });
        using var bitmap = new RmQRCodeImageBuilder(data).WithSize(sizePx, sizePx).ToBitmap();

        var success = RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version} at {sizePx} px: {info.Status}");
        await Assert.That(text).IsEqualTo(content);
        await Assert.That(info.Version).IsEqualTo(version);
    }

    public static IEnumerable<(RmQRVersion, int)> FractionalSizes()
    {
        foreach (var version in new[] { RmQRVersion.R7x43, RmQRVersion.R11x77, RmQRVersion.R13x99, RmQRVersion.R15x99, RmQRVersion.R17x139 })
        {
            var modules = int.Parse(version.ToString().Split('x')[1]) + 4;
            for (var px = modules * 3 / 2; px <= modules * 5 / 2; px += 3)
                yield return (version, px);
        }
    }

    /// <summary>Builder sizes from 1.5 to 2.5 px/module, every third pixel.</summary>
    [Test]
    [MethodDataSource(nameof(FractionalSizes))]
    public async Task Decode_FractionalBuilderSize_Decodes(RmQRVersion version, int sizePx)
    {
        var content = ContentFor(version);
        var data = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });
        using var bitmap = new RmQRCodeImageBuilder(data).WithSize(sizePx, sizePx).ToBitmap();

        var success = RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version} at {sizePx} px: {info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }
}
