namespace FeatherQR.Tests;

/// <summary>
/// A destination too short for an rMQR symbol's text, on the image path: the read that does not fit ends its finder candidate
/// (rmqr-decoder.md, Decisions), and a later candidate inside the symbol that read is skipped, since it is a finder-like pattern
/// in that symbol's own data, not another symbol. <see cref="RmQRCodeDecoderImageTest"/> holds the other symbols that are still read.
/// </summary>
public class RmQRCodeDecoderDestinationTest
{
    /// <summary>
    /// The scan ranks a finder-like pattern at (76,34), in the symbol's data, after the real finder, and it was searched in full
    /// after the read that did not fit: about 90 times a sized call (2026-09-30). Skipped, the call costs what a sized one does.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task DecodeImage_DestinationTooSmall_SkipsACandidateInsideTheSymbolThatRead()
    {
        const string content = "HELLO12345ABCDE";
        const int pixelsPerModule = 4;
        var data = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R13x27 });
        var width = data.Width * pixelsPerModule;
        var height = data.Height * pixelsPerModule;
        var luminance = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                luminance[y * width + x] = data[y / pixelsPerModule, x / pixelsPerModule] ? (byte)0 : (byte)255;
        }

        var sized = new char[RmQRCodeDecoder.GetMaxDecodedLength(RmQRVersion.R17x139)];
        var tiny = new char[2];
        await Assert.That(RmQRCodeDecoder.TryDecodeImage(luminance, width, height, sized, out var sizedWritten, out _)).IsTrue();
        await Assert.That(new string(sized, 0, sizedWritten)).IsEqualTo(content);
        RmQRCodeDecoder.TryDecodeImage(luminance, width, height, tiny, out _, out _);

        const int iterations = 500;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            RmQRCodeDecoder.TryDecodeImage(luminance, width, height, sized, out _, out _);
        var sizedElapsed = stopwatch.Elapsed;

        stopwatch.Restart();
        var info = default(RmQRCodeDecodeInfo);
        for (var i = 0; i < iterations; i++)
            RmQRCodeDecoder.TryDecodeImage(luminance, width, height, tiny, out _, out info);
        var tinyElapsed = stopwatch.Elapsed;

        await Assert.That((info.Status, info.Version, info.Corners.IsEmpty)).IsEqualTo((DecodeStatus.DestinationTooSmall, RmQRVersion.R13x27, true));
        await Assert.That(tinyElapsed).IsLessThan(TimeSpan.FromTicks(sizedElapsed.Ticks * 5) + TimeSpan.FromMilliseconds(30))
            .Because($"a candidate inside the symbol that read is skipped: sized {sizedElapsed.TotalMilliseconds:F2} ms against tiny {tinyElapsed.TotalMilliseconds:F2} ms over {iterations} runs");
    }
}
