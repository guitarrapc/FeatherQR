using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The image decoders keep coordinates in which integers are pixel edges, so the pixel
/// containing a point is its floor. The samplers used to round to nearest instead, which
/// reads half a pixel right of and below every module centre: the neighbouring module at
/// 1 px/module and a module edge at 1.5-2 px/module, where the builder's fixed-size
/// renders land. Clean renders at those densities failed in all three symbologies.
/// </summary>
public class PixelConventionTest
{
    private const string QrContent = "https://github.com/guitarrapc/FeatherQR";

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(7)]
    [Arguments(20)]
    [Arguments(40)]
    public async Task StandardQr_OnePixelPerModule_Decodes(int version)
    {
        const string content = "PIXEL EDGES";
        var qr = QRCodeGenerator.Create(content, QREccLevel.L, new QRCodeGeneratorOptions { Version = version });
        using var bitmap = new QRCodeImageBuilder(qr).WithModulePixelSize(1).ToBitmap();

        var success = QRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"v{version}: {info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    [Arguments(MicroQRVersion.M1)]
    [Arguments(MicroQRVersion.M2)]
    [Arguments(MicroQRVersion.M3)]
    [Arguments(MicroQRVersion.M4)]
    public async Task MicroQr_OnePixelPerModule_Decodes(MicroQRVersion version)
    {
        const string content = "123";
        var ecc = version == MicroQRVersion.M1 ? MicroQREccLevel.ErrorDetectionOnly : MicroQREccLevel.L;
        var data = MicroQRCodeGenerator.Create(content, ecc, new MicroQRCodeGeneratorOptions { Version = version });
        using var bitmap = new MicroQRCodeImageBuilder(data).WithModulePixelSize(1).ToBitmap();

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version}: {info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    [Arguments(RmQRVersion.R7x43)]
    [Arguments(RmQRVersion.R13x27)]
    [Arguments(RmQRVersion.R11x77)]
    [Arguments(RmQRVersion.R17x139)]
    public async Task RmQr_OnePixelPerModule_Decodes(RmQRVersion version)
    {
        const string content = "1234";
        var data = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });
        using var bitmap = new RmQRCodeImageBuilder(data).WithModulePixelSize(1).ToBitmap();

        var success = RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version}: {info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    /// <summary>Fixed-size builder renders between 1.5 and 2.2 px/module, each of which failed before.</summary>
    [Test]
    [Arguments(3, 55)]
    [Arguments(10, 97)]
    [Arguments(20, 157)]
    [Arguments(25, 187)]
    public async Task StandardQr_FractionalBuilderSize_Decodes(int version, int sizePx)
    {
        var qr = QRCodeGenerator.Create(QrContent, QREccLevel.M, new QRCodeGeneratorOptions { Version = version });
        using var bitmap = new QRCodeImageBuilder(qr).WithSize(sizePx, sizePx).ToBitmap();

        var success = QRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"v{version} at {sizePx} px: {info.Status}, read as v{info.Version}");
        await Assert.That(text).IsEqualTo(QrContent);
    }

    [Test]
    [Arguments(MicroQRVersion.M2, 25)]
    [Arguments(MicroQRVersion.M3, 33)]
    [Arguments(MicroQRVersion.M4, 31)]
    public async Task MicroQr_FractionalBuilderSize_Decodes(MicroQRVersion version, int sizePx)
    {
        const string content = "12345";
        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = version });
        using var bitmap = new MicroQRCodeImageBuilder(data).WithSize(sizePx, sizePx).ToBitmap();

        var success = MicroQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version} at {sizePx} px: {info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    [Test]
    [Arguments(RmQRVersion.R11x77, 127)]
    [Arguments(RmQRVersion.R13x99, 156)]
    [Arguments(RmQRVersion.R17x139, 218)]
    public async Task RmQr_FractionalBuilderSize_Decodes(RmQRVersion version, int sizePx)
    {
        const string content = "RMQR 12345";
        var data = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });
        using var bitmap = new RmQRCodeImageBuilder(data).WithSize(sizePx, sizePx).ToBitmap();

        var success = RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version} at {sizePx} px: {info.Status}");
        await Assert.That(text).IsEqualTo(content);
    }

    /// <summary>
    /// The sampler on its own: a grid mapped exactly onto the pixel grid at 1 px/module must read
    /// every module back, through the scalar kernel and the vector one.
    /// </summary>
    [Test]
    [Arguments(21)]
    [Arguments(57)]
    public async Task SampleGrid_PixelAlignedAtOnePixelPerModule_ReadsEveryModule(int dimension)
    {
        const int quietZone = 4;
        var side = dimension + 2 * quietZone;
        var random = new Random(dimension);
        var expected = new byte[dimension * dimension];
        var luminance = new byte[side * side];
        Array.Fill(luminance, (byte)255);
        for (var i = 0; i < expected.Length; i++)
        {
            expected[i] = (byte)random.Next(2);
            if (expected[i] != 0)
                luminance[(i / dimension + quietZone) * side + i % dimension + quietZone] = 0;
        }
        var transform = PerspectiveTransform.QuadrilateralToQuadrilateral(
            0, 0, dimension, 0, dimension, dimension, 0, dimension,
            quietZone, quietZone, quietZone + dimension, quietZone, quietZone + dimension, quietZone + dimension, quietZone, quietZone + dimension);

        var scalar = new byte[expected.Length];
        var vector = new byte[expected.Length];
        QRImageDecoder.SampleGridScalar(luminance, side, side, 128, transform, dimension, scalar);
        QRImageDecoder.SampleGrid(luminance, side, side, 128, transform, dimension, vector);

        await Assert.That(Mismatches(scalar, expected)).IsEqualTo(0).Because("scalar kernel, modules read wrong");
        await Assert.That(Mismatches(vector, expected)).IsEqualTo(0).Because("vector kernel, modules read wrong");

        static int Mismatches(byte[] actual, byte[] expected) => actual.Where((value, i) => value != expected[i]).Count();
    }
}
