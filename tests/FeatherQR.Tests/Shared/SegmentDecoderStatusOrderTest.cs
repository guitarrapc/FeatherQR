using SkiaSharp;
using FeatherQR.SkiaSharp.Internals;
using FeatherQR.SkiaSharp;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// <c>DestinationTooSmall</c> must mean "the symbol was read and only your buffer was
/// short", because the image decoders treat it as terminal: it stops the strideless
/// retry and the reflectance-inverted retry.
/// </summary>
/// <remarks>
/// The character count of a numeric or alphanumeric segment is read off the wire and can
/// name more characters than the remaining bits could possibly encode. Checking the
/// caller's buffer before that count is checked against the bitstream reports a
/// malformed symbol as a short buffer, which tells a caller sizing its buffer to grow
/// and stops the decoder from looking for the real symbol elsewhere in the image.
/// </remarks>
public class SegmentDecoderStatusOrderTest
{
    /// <summary>
    /// A short buffer on a genuine symbol still reports <c>DestinationTooSmall</c>, at
    /// every destination length below what the payload needs.
    /// </summary>
    [Test]
    [Arguments("123456", 6)]
    [Arguments("ABC123", 6)]
    [Arguments("Hello!", 6)]
    public async Task ShortBuffer_OnAValidSymbol_ReportsDestinationTooSmall(string content, int expectedLength)
    {
        var size = Sizing.Required(content.AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x27 });
        var modules = new byte[size.BufferSize];
        RmQRCodeGenerator.Create(content.AsSpan(), RmQREccLevel.M, modules, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x27 });

        for (var length = 0; length < expectedLength; length++)
        {
            var status = RmQRCodeDecoder.TryDecode(modules, size.Width, size.Height, new char[length], out _, out var info);
            await Assert.That(status).IsFalse();
            await Assert.That(info.Status).IsEqualTo(DecodeStatus.DestinationTooSmall)
                .Because($"'{content}' with a {length}-char destination");
        }

        var ok = RmQRCodeDecoder.TryDecode(modules, size.Width, size.Height, new char[expectedLength], out var chars, out _);
        await Assert.That(ok).IsTrue();
        await Assert.That(chars).IsEqualTo(expectedLength);
    }

    /// <summary>
    /// The same through the image entry point, where the status also decides whether the
    /// strideless retry and the inverted-polarity retry run.
    /// </summary>
    [Test]
    public async Task ShortBuffer_ThroughTheImageEntryPoint_ReportsDestinationTooSmall()
    {
        var data = RmQRCodeGenerator.Create("123456", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x27 });
        using var bitmap = new RmQRCodeImageBuilder(data).WithModulePixelSize(8).ToBitmap();
        var luminance = new byte[bitmap.Width * bitmap.Height];
        BitmapLuminanceConverter.Convert(bitmap, luminance);

        var status = RmQRCodeDecoder.TryDecodeImage(luminance, bitmap.Width, bitmap.Height, new char[3], out _, out var info);
        await Assert.That(status).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
    }

    /// <summary>
    /// Micro QR ranks its failures the same way. M3-L used to report
    /// <c>DataUncorrectable</c> here, because sizes are tried 17 down to 11 and a
    /// wrong-size attempt reached Reed-Solomon before the right size did.
    /// </summary>
    [Test]
    [Arguments(MicroQRVersion.M3, MicroQREccLevel.L, "ABCDEFGHIJ", 10)]
    [Arguments(MicroQRVersion.M3, MicroQREccLevel.M, "ABCDE", 5)]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.L, "ABCDEFGHIJ", 10)]
    [Arguments(MicroQRVersion.M2, MicroQREccLevel.L, "ABCDE", 5)]
    public async Task MicroQR_ShortBuffer_ThroughTheImageEntryPoint_ReportsDestinationTooSmall(
        MicroQRVersion version, MicroQREccLevel ecc, string content, int expectedLength)
    {
        var data = MicroQRCodeGenerator.Create(content, ecc, new MicroQRCodeGeneratorOptions { Version = version });
        using var bitmap = new MicroQRCodeImageBuilder(data).WithModulePixelSize(8).ToBitmap();
        var luminance = new byte[bitmap.Width * bitmap.Height];
        BitmapLuminanceConverter.Convert(bitmap, luminance);

        for (var length = 1; length < expectedLength; length++)
        {
            var status = MicroQRCodeDecoder.TryDecodeImage(luminance, bitmap.Width, bitmap.Height, new char[length], out _, out var info);
            await Assert.That(status).IsFalse();
            await Assert.That(info.Status).IsEqualTo(DecodeStatus.DestinationTooSmall)
                .Because($"{version}-{ecc} '{content}' with a {length}-char destination");
        }

        var ok = MicroQRCodeDecoder.TryDecodeImage(luminance, bitmap.Width, bitmap.Height, new char[expectedLength], out var chars, out _);
        await Assert.That(ok).IsTrue();
        await Assert.That(chars).IsEqualTo(expectedLength);
    }

    /// <summary>
    /// Once a normal-polarity symbol has been read and only the destination is short,
    /// the reflectance-reversed retry must not replace that terminal result with a
    /// different symbol from the opposite polarity.
    /// </summary>
    [Test]
    public async Task MicroQR_DestinationTooSmall_DoesNotRetryTheOppositePolarity()
    {
        var longData = MicroQRCodeGenerator.Create("ABCDEFGHIJ", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M3 });
        var shortData = MicroQRCodeGenerator.Create("1", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M2 });
        using var normal = new MicroQRCodeImageBuilder(longData)
            .WithModulePixelSize(8)
            .WithColors(SKColors.Black, SKColors.White).WithClearColor(SKColors.White)
            .ToBitmap();
        using var reversed = new MicroQRCodeImageBuilder(shortData)
            .WithModulePixelSize(8)
            .WithColors(SKColors.White, SKColors.Black).WithClearColor(SKColors.Black)
            .ToBitmap();

        const int gap = 32;
        var width = normal.Width + gap + reversed.Width;
        var height = Math.Max(normal.Height, reversed.Height);
        using var scene = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(scene))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(normal, 0, 0, SKSamplingOptions.Default);
            canvas.DrawBitmap(reversed, normal.Width + gap, 0, SKSamplingOptions.Default);
            canvas.Flush();
        }

        // The reversed symbol is the bait, and a bait nobody could take proves nothing: on its own it has
        // to decode, through the very retry this test says must not run, into the one character that would
        // fit the short destination below.
        var baitLuminance = new byte[reversed.Width * reversed.Height];
        BitmapLuminanceConverter.Convert(reversed, baitLuminance);
        var bait = new char[1];
        var baitDecoded = MicroQRCodeDecoder.TryDecodeImage(baitLuminance, reversed.Width, reversed.Height, bait, out var baitLength, out _);
        await Assert.That(baitDecoded).IsTrue().Because("the reversed symbol must be readable, or the scene has no opposite-polarity result to offer");
        await Assert.That(baitLength).IsEqualTo(1);
        await Assert.That(bait[0]).IsEqualTo('1');

        var luminance = new byte[width * height];
        BitmapLuminanceConverter.Convert(scene, luminance);
        var decoded = MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, new char[1], out _, out var info);

        await Assert.That(decoded).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(info.Version).IsEqualTo(MicroQRVersion.M3);
    }

    [Test]
    public async Task StandardQR_DestinationTooSmall_DoesNotRetryTheOppositePolarity()
    {
        var longData = QRCodeGenerator.Create("STANDARD-DESTINATION-TOO-SMALL", QREccLevel.M);
        var shortData = QRCodeGenerator.Create("1", QREccLevel.M);
        using var normal = new QRCodeImageBuilder(longData)
            .WithModulePixelSize(6)
            .WithColors(SKColors.Black, SKColors.White).WithClearColor(SKColors.White)
            .ToBitmap();
        using var reversed = new QRCodeImageBuilder(shortData)
            .WithModulePixelSize(6)
            .WithColors(SKColors.White, SKColors.Black).WithClearColor(SKColors.Black)
            .ToBitmap();

        const int gap = 32;
        var width = normal.Width + gap + reversed.Width;
        var height = Math.Max(normal.Height, reversed.Height);
        using var scene = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(scene))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(normal, 0, 0, SKSamplingOptions.Default);
            canvas.DrawBitmap(reversed, normal.Width + gap, 0, SKSamplingOptions.Default);
            canvas.Flush();
        }

        // Same bait check as the Micro QR case: the reversed symbol has to be readable on its own.
        var baitLuminance = new byte[reversed.Width * reversed.Height];
        BitmapLuminanceConverter.Convert(reversed, baitLuminance);
        var bait = new char[1];
        var baitDecoded = QRCodeDecoder.TryDecodeImage(baitLuminance, reversed.Width, reversed.Height, bait, out var baitLength, out _);
        await Assert.That(baitDecoded).IsTrue().Because("the reversed symbol must be readable, or the scene has no opposite-polarity result to offer");
        await Assert.That(baitLength).IsEqualTo(1);
        await Assert.That(bait[0]).IsEqualTo('1');

        var luminance = new byte[width * height];
        BitmapLuminanceConverter.Convert(scene, luminance);
        var decoded = QRCodeDecoder.TryDecodeImage(luminance, width, height, new char[1], out _, out var info);

        await Assert.That(decoded).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(info.Version).IsEqualTo(longData.Version);
    }

    /// <summary>
    /// The malformed case the check order exists for: a character count read off the wire
    /// naming more characters than the remaining bits could possibly encode. That is a
    /// broken bitstream whatever buffer the caller passed, so it must report
    /// <c>InvalidBitstream</c>; reporting <c>DestinationTooSmall</c> would tell a caller
    /// sizing its buffer to grow, and the image decoders treat that status as terminal and
    /// stop looking for the real symbol.
    /// </summary>
    [Test]
    public async Task CountLargerThanTheRemainingBits_ReportsInvalidBitstream()
    {
        // Micro QR M4: 3-bit mode indicator, then a 6-bit numeric / 5-bit alphanumeric
        // count. Numeric count 63 needs 210 bits, only 72 - 9 = 63 remain.
        var numeric = MicroQRBinaryDecoder.DecodeBitStream(Bits("000111111"), 72, MicroQRVersion.M4, new char[4], out _);
        await Assert.That(numeric).IsEqualTo(DecodeStatus.InvalidBitstream).Because("M4 numeric count 63 in 63 remaining bits");

        // Alphanumeric count 31 needs 171 bits, only 72 - 8 = 64 remain.
        var alnum = MicroQRBinaryDecoder.DecodeBitStream(Bits("00111111"), 72, MicroQRVersion.M4, new char[4], out _);
        await Assert.That(alnum).IsEqualTo(DecodeStatus.InvalidBitstream).Because("M4 alphanumeric count 31 in 64 remaining bits");

        // rMQR R11x27: 3-bit mode indicator, then a 4-bit numeric count. Count 15 needs
        // 50 bits, only 30 - 7 = 23 remain.
        var rmqr = RmQRBinaryDecoder.DecodeBitStream(Bits("0011111"), 30, RmQRVersion.R11x27, new char[4], out _);
        await Assert.That(rmqr).IsEqualTo(DecodeStatus.InvalidBitstream).Because("R11x27 numeric count 15 in 23 remaining bits");
    }

    /// <summary>Packs an MSB-first bit string into bytes, zero-padded.</summary>
    private static byte[] Bits(string bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i] == '1')
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        }
        return bytes;
    }

    /// <summary>
    /// The remainder branches of the two bit-budget formulas, which is where an off-by-one
    /// hides: numeric spends 7 bits on a two-digit remainder and 4 on a single digit,
    /// alphanumeric 6 on an odd final character. A guard that over-charges by one bit
    /// rejects valid symbols whose payload ends exactly at capacity, and then the image
    /// decoder keeps searching and can accept a wrong grid that happens to pass ECC — a
    /// wrong-text result, not just a failure.
    /// </summary>
    [Test]
    [Arguments("01234")]   // 5 digits: count % 3 == 2, the 7-bit remainder
    [Arguments("01")]      // 2 digits: the 7-bit remainder alone
    [Arguments("0123456")] // 7 digits: count % 3 == 1, the 4-bit remainder
    [Arguments("0")]       // 1 digit: the 4-bit remainder alone
    [Arguments("012345")]  // 6 digits: no remainder
    [Arguments("ABC")]     // 3 alphanumerics: count % 2 == 1, the 6-bit remainder
    [Arguments("A")]       // 1 alphanumeric: the 6-bit remainder alone
    [Arguments("ABCD")]    // 4 alphanumerics: no remainder
    public async Task PayloadsEndingOnEveryRemainderBranch_RoundTrip(string content)
    {
        foreach (var version in new[] { MicroQRVersion.M3, MicroQRVersion.M4 })
        {
            var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = version });
            await Assert.That(MicroQRCodeDecoder.TryDecode(data, out var text)).IsTrue().Because($"{version} '{content}'");
            await Assert.That(text).IsEqualTo(content);
        }

        var rmqr = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x27 });
        await Assert.That(RmQRCodeDecoder.TryDecode(rmqr, out var rmqrText)).IsTrue().Because($"R11x27 '{content}'");
        await Assert.That(rmqrText).IsEqualTo(content);
    }

    /// <summary>
    /// M1 holds exactly 5 numeric digits, so its payload ends on the 7-bit two-digit
    /// remainder with no spare bits at all. That makes it the case where a bit budget
    /// over-charged by one is not merely stricter but wrong, and it is the smallest
    /// symbol in the library — the least room for a formula to be sloppy in.
    /// </summary>
    [Test]
    [Arguments("01234")]
    [Arguments("0123")]
    [Arguments("012")]
    [Arguments("01")]
    [Arguments("0")]
    public async Task MicroQrM1_NumericAtEveryLengthToCapacity_RoundTrips(string content)
    {
        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.ErrorDetectionOnly, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M1 });
        await Assert.That(MicroQRCodeDecoder.TryDecode(data, out var text)).IsTrue().Because($"M1 '{content}'");
        await Assert.That(text).IsEqualTo(content);
    }
}
