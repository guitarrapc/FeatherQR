using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// An anti-aliased render at about 2 px/module has a grey pixel on every module edge, and whichever side of the threshold it falls, a whole-pixel run is a pixel off its module: a 2.1 px light module reads 1 px, which no 1:1:3:1:1 check with a half-module tolerance accepts.
/// The grey level says how much of the pixel the module covers, so the finder is measured from that before it is refused.
/// </summary>
public class AntiAliasedEdgeDecodeTest
{
    private const string Content = "FQR 2.0";

    public static IEnumerable<(float, float, float)> ScalesAndOffsets()
    {
        foreach (var pixelsPerModule in new[] { 2.0f, 2.1f, 2.2f, 2.3f, 2.4f, 2.5f })
        {
            foreach (var offsetX in new[] { 0f, 0.25f, 0.5f, 0.75f })
            {
                foreach (var offsetY in new[] { 0f, 0.5f })
                    yield return (pixelsPerModule, offsetX, offsetY);
            }
        }
    }

    /// <summary>
    /// Which edges a case renders follows from its geometry, not from the platform: a whole scale at a whole offset puts every module edge on a pixel boundary and comes out crisp, and every other case is anti-aliased.
    /// Both directions are asserted, so a renderer that stopped anti-aliasing cannot quietly retire this class, and the crisp case of each symbology stays a decode on the whole-pixel path.
    /// </summary>
    private static async Task AssertExpectedEdges(byte[] luminance, float pixelsPerModule, float offsetX, float offsetY)
    {
        var crisp = pixelsPerModule % 1f == 0f && offsetX == 0f && offsetY == 0f;
        Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        await Assert.That(grey.IsEnabled).IsEqualTo(!crisp).Because($"{pixelsPerModule} px/module at ({offsetX}, {offsetY}) did not render the edges this case is for");
    }

    [Test]
    [MethodDataSource(nameof(ScalesAndOffsets))]
    public async Task StandardQR_AntiAliasedAtLowDensity_Decodes(float pixelsPerModule, float offsetX, float offsetY)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 });
        var (luminance, width, height) = AntiAliasedRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule, offsetX, offsetY);

        await AssertExpectedEdges(luminance, pixelsPerModule, offsetX, offsetY);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{pixelsPerModule} px/module at ({offsetX}, {offsetY}): {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
    }

    [Test]
    [MethodDataSource(nameof(ScalesAndOffsets))]
    public async Task MicroQR_AntiAliasedAtLowDensity_Decodes(float pixelsPerModule, float offsetX, float offsetY)
    {
        var qr = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L);
        var (luminance, width, height) = AntiAliasedRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule, offsetX, offsetY);

        await AssertExpectedEdges(luminance, pixelsPerModule, offsetX, offsetY);

        var success = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{pixelsPerModule} px/module at ({offsetX}, {offsetY}): {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
    }

    [Test]
    [MethodDataSource(nameof(ScalesAndOffsets))]
    public async Task RmQR_AntiAliasedAtLowDensity_Decodes(float pixelsPerModule, float offsetX, float offsetY)
    {
        var qr = RmQRCodeGenerator.Create(Content, RmQREccLevel.M);
        var (luminance, width, height) = AntiAliasedRenderer.Render((row, column) => qr[row, column], qr.Width, qr.Height, pixelsPerModule, offsetX, offsetY);

        await AssertExpectedEdges(luminance, pixelsPerModule, offsetX, offsetY);

        var success = RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{pixelsPerModule} px/module at ({offsetX}, {offsetY}): {info.Status}");
        await Assert.That(text).IsEqualTo(Content);
    }
}
