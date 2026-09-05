using FeatherQR.Internals.StandardQr;

namespace FeatherQR.Tests;

/// <summary>
/// Robustness tests for the public decode API: adversarial argument values and
/// random garbage input must fail cleanly (documented exception type or a false
/// return with a status), never crash, over-read, or misdecode.
/// </summary>
public class QRCodeDecoderRobustnessTest
{
    [Test]
    public void TryDecode_SizeSquareOverflowsInt_ThrowsArgumentException()
    {
        // 65536² overflows int to 0; the guard must use long arithmetic instead of
        // wrapping and letting an interior Slice throw something else.
        var modules = new byte[100];

        Assert.Throws<ArgumentException>(() => QRCodeDecoder.TryDecode(modules, 65536, out _, out _));
        Assert.Throws<ArgumentException>(() => QRCodeDecoder.TryDecode(modules, int.MaxValue, out _, out _));
    }

    [Test]
    public void TryDecode_CharSpanOverload_SizeSquareOverflowsInt_ThrowsArgumentException()
    {
        var modules = new byte[100];
        var destination = new char[16];

        Assert.Throws<ArgumentException>(() => QRCodeDecoder.TryDecode(modules, 65536, destination, out _, out _));
    }

    [Test]
    public void TryDecodeImage_DimensionsOverflowInt_ThrowsArgumentException()
    {
        var luminance = new byte[100];

        Assert.Throws<ArgumentException>(() => QRCodeDecoder.TryDecodeImage(luminance, 65536, 65536, out _, out _));
        Assert.Throws<ArgumentException>(() => QRCodeDecoder.TryDecodeImage(luminance, int.MaxValue, 2, out _, out _));
    }

    [Test]
    public async Task DecodeLuminance_DimensionsOverflowInt_ReturnsNotDetected()
    {
        var status = QRImageDecoder.DecodeLuminance(
            ReadOnlySpan<byte>.Empty,
            65_536,
            65_536,
            Span<char>.Empty,
            out var charsWritten,
            out var info);

        await Assert.That(status).IsEqualTo(QRCodeDecodeStatus.NotDetected);
        await Assert.That(charsWritten).IsEqualTo(0);
        await Assert.That(info.Status).IsEqualTo(QRCodeDecodeStatus.NotDetected);
    }

    [Test]
    public void TryDecode_RandomMatrices_NeverThrows()
    {
        var random = new Random(20260712);
        for (var round = 0; round < 200; round++)
        {
            // Valid QR dimensions with random module noise
            var version = random.Next(1, 11);
            var size = 17 + version * 4;
            var modules = new byte[size * size];
            for (var i = 0; i < modules.Length; i++)
            {
                modules[i] = (byte)(random.Next(2));
            }

            // Any result is fine; throwing is not.
            QRCodeDecoder.TryDecode(modules, size, out _, out _);
        }
    }

    [Test]
    public async Task TryDecode_ValidPatternsCorruptedDataArea_FailsOrDecodesExactly()
    {
        // Take a real QR and corrupt random module subsets of increasing size: the
        // decoder must either fail or return the exact original text, a wrong text
        // (misdecode) is never acceptable.
        var content = "fail closed";
        var qr = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
        var size = qr.Size;
        var pristine = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                pristine[y * size + x] = qr[y, x] ? (byte)1 : (byte)0;
            }
        }

        var random = new Random(42);
        for (var round = 0; round < 300; round++)
        {
            var modules = (byte[])pristine.Clone();
            var flips = random.Next(1, size * size / 4);
            for (var i = 0; i < flips; i++)
            {
                modules[random.Next(modules.Length)] ^= 1;
            }

            if (QRCodeDecoder.TryDecode(modules, size, out var text, out _))
            {
                await Assert.That(text).IsEquivalentTo(content);
            }
        }
    }

    [Test]
    public void TryDecodeImage_RandomLuminance_NeverThrows()
    {
        var random = new Random(20260712);
        for (var round = 0; round < 50; round++)
        {
            var width = random.Next(1, 120);
            var height = random.Next(1, 120);
            var luminance = new byte[width * height];
            random.NextBytes(luminance);

            QRCodeDecoder.TryDecodeImage(luminance, width, height, out _, out _);
        }
    }
}
