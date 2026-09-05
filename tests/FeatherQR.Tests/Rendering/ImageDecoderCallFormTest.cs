using FeatherQR.SkiaSharp;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The <c>SKBitmap</c> decode overloads live in the rendering package as C# 14 extension
/// members on the core decoders. A C# 14 consumer writes <c>QRCodeDecoder.TryDecode(bitmap, ...)</c>;
/// a C# 12 or 13 consumer (net8.0's default) can only name the enclosing class,
/// <c>QRCodeImageDecoder.TryDecode(bitmap, ...)</c>, which is the documented spelling.
/// Both forms, both overloads, all three symbologies, plus the argument contract that
/// moved with them.
/// </summary>
public class ImageDecoderCallFormTest
{
    private const string QrContent = "extension member call forms";
    private const string MicroContent = "MICRO 42";
    private const string RmContent = "rMQR call forms";

    private static SKBitmap RenderQr() => new QRCodeImageBuilder(QrContent).WithModulePixelSize(6).ToBitmap();
    private static SKBitmap RenderMicro() => new MicroQRCodeImageBuilder(MicroContent).WithModulePixelSize(8).ToBitmap();
    private static SKBitmap RenderRm() => new RmQRCodeImageBuilder(RmContent).WithModulePixelSize(6).ToBitmap();

    [Test]
    public async Task StandardQr_BothCallForms_BothOverloads_Decode()
    {
        using var bitmap = RenderQr();

        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out var a)).IsTrue();
        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out var b, out var bInfo)).IsTrue();
        await Assert.That(QRCodeImageDecoder.TryDecode(bitmap, out var c)).IsTrue();
        await Assert.That(QRCodeImageDecoder.TryDecode(bitmap, out var d, out var dInfo)).IsTrue();

        await Assert.That(a).IsEqualTo(QrContent);
        await Assert.That(b).IsEqualTo(QrContent);
        await Assert.That(c).IsEqualTo(QrContent);
        await Assert.That(d).IsEqualTo(QrContent);
        await Assert.That(bInfo.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(dInfo).IsEqualTo(bInfo);
    }

    [Test]
    public async Task MicroQr_BothCallForms_BothOverloads_Decode()
    {
        using var bitmap = RenderMicro();

        await Assert.That(MicroQRCodeDecoder.TryDecode(bitmap, out var a)).IsTrue();
        await Assert.That(MicroQRCodeDecoder.TryDecode(bitmap, out var b, out var bInfo)).IsTrue();
        await Assert.That(MicroQRCodeImageDecoder.TryDecode(bitmap, out var c)).IsTrue();
        await Assert.That(MicroQRCodeImageDecoder.TryDecode(bitmap, out var d, out var dInfo)).IsTrue();

        await Assert.That(a).IsEqualTo(MicroContent);
        await Assert.That(b).IsEqualTo(MicroContent);
        await Assert.That(c).IsEqualTo(MicroContent);
        await Assert.That(d).IsEqualTo(MicroContent);
        await Assert.That(bInfo.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(dInfo).IsEqualTo(bInfo);
    }

    [Test]
    public async Task RmQr_BothCallForms_BothOverloads_Decode()
    {
        using var bitmap = RenderRm();

        await Assert.That(RmQRCodeDecoder.TryDecode(bitmap, out var a)).IsTrue();
        await Assert.That(RmQRCodeDecoder.TryDecode(bitmap, out var b, out var bInfo)).IsTrue();
        await Assert.That(RmQRCodeImageDecoder.TryDecode(bitmap, out var c)).IsTrue();
        await Assert.That(RmQRCodeImageDecoder.TryDecode(bitmap, out var d, out var dInfo)).IsTrue();

        await Assert.That(a).IsEqualTo(RmContent);
        await Assert.That(b).IsEqualTo(RmContent);
        await Assert.That(c).IsEqualTo(RmContent);
        await Assert.That(d).IsEqualTo(RmContent);
        await Assert.That(bInfo.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(dInfo).IsEqualTo(bInfo);
    }

    /// <summary>The same symbol through the other symbology's scanner is a clean miss, as before the split.</summary>
    [Test]
    public async Task WrongSymbology_ReturnsFalseWithNotDetected()
    {
        using var micro = RenderMicro();

        await Assert.That(QRCodeImageDecoder.TryDecode(micro, out var text, out var info)).IsFalse();
        await Assert.That(text).IsEmpty();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.NotDetected);
    }

    [Test]
    public async Task NullBitmap_Throws_InBothForms()
    {
        await Assert.That(() => QRCodeDecoder.TryDecode((SKBitmap)null!, out _)).Throws<ArgumentNullException>();
        await Assert.That(() => QRCodeImageDecoder.TryDecode((SKBitmap)null!, out _, out _)).Throws<ArgumentNullException>();
        await Assert.That(() => MicroQRCodeDecoder.TryDecode((SKBitmap)null!, out _)).Throws<ArgumentNullException>();
        await Assert.That(() => MicroQRCodeImageDecoder.TryDecode((SKBitmap)null!, out _, out _)).Throws<ArgumentNullException>();
        await Assert.That(() => RmQRCodeDecoder.TryDecode((SKBitmap)null!, out _)).Throws<ArgumentNullException>();
        await Assert.That(() => RmQRCodeImageDecoder.TryDecode((SKBitmap)null!, out _, out _)).Throws<ArgumentNullException>();
    }

    /// <summary>Below the smallest symbol, nothing is scanned or rented; the answer is a plain false.</summary>
    [Test]
    public async Task BitmapSmallerThanTheSmallestSymbol_ReturnsFalse()
    {
        using var tiny = new SKBitmap(6, 6);

        await Assert.That(QRCodeImageDecoder.TryDecode(tiny, out _, out var qr)).IsFalse();
        await Assert.That(qr.Status).IsEqualTo(DecodeStatus.NotDetected);
        await Assert.That(MicroQRCodeImageDecoder.TryDecode(tiny, out _, out var micro)).IsFalse();
        await Assert.That(micro.Status).IsEqualTo(DecodeStatus.NotDetected);
        await Assert.That(RmQRCodeImageDecoder.TryDecode(tiny, out _, out var rm)).IsFalse();
        await Assert.That(rm.Status).IsEqualTo(DecodeStatus.NotDetected);
    }
}
