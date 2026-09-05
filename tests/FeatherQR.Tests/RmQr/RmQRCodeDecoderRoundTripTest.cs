using FeatherQR.Internals.RmQr;

namespace FeatherQR.Tests;

/// <summary>
/// Public <see cref="RmQRCodeDecoder"/> matrix paths: encode → decode for every
/// version × ECC × mode with several quiet zones (uniform and asymmetric padding),
/// class / span / <see cref="RmQRCodeData"/> overload parity, decoded-length bound,
/// cross-symbology rejection, argument validation and Release-only zero allocation.
/// </summary>
public class RmQRCodeDecoderRoundTripTest
{
    public static IEnumerable<(RmQRVersion version, RmQREccLevel ecc)> AllVersionEcc()
    {
        foreach (var v in Enum.GetValues<RmQRVersion>())
        {
            yield return (v, RmQREccLevel.M);
            yield return (v, RmQREccLevel.H);
        }
    }

    private static string Payload(RmQRVersion version, RmQREccLevel ecc, string mode)
    {
        var length = RmQRVersionSelector.GetMaxDataLength(version, ecc, mode switch { "numeric" => Internals.EncodingMode.Numeric, "alphanumeric" => Internals.EncodingMode.Alphanumeric, _ => Internals.EncodingMode.Byte });
        const string numeric = "0123456789";
        const string alnum = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 $%*+-./:";
        const string bytes = "the quick brown fox jumps over the lazy dog?! ";
        var alphabet = mode switch { "numeric" => numeric, "alphanumeric" => alnum, _ => bytes };
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[(i * 7 + (int)version) % alphabet.Length];
        return new string(chars);
    }

    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task RoundTrip_EveryMode_AtCapacity_AllPaths(RmQRVersion version, RmQREccLevel ecc)
    {
        foreach (var mode in new[] { "numeric", "alphanumeric", "byte" })
        {
            var text = Payload(version, ecc, mode);
            foreach (var quietZone in new[] { 0, 2, 4 })
            {
                var data = RmQRCodeGenerator.CreateRmQRCode(text, ecc, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });

                // RmQRCodeData overloads
                await Assert.That(RmQRCodeDecoder.TryDecode(data, out var text1)).IsTrue().Because($"{version}-{ecc} {mode} qz{quietZone}");
                await Assert.That(text1).IsEqualTo(text);
                await Assert.That(RmQRCodeDecoder.TryDecode(data, out var text2, out var info)).IsTrue();
                await Assert.That(text2).IsEqualTo(text);
                await Assert.That(info.Status).IsEqualTo(QRCodeDecodeStatus.Success);
                await Assert.That(info.Version).IsEqualTo(version);
                await Assert.That(info.EccLevel).IsEqualTo(ecc);
                await Assert.That(info.ErrorsCorrected).IsEqualTo(0);

                // Module-matrix overloads (byte per module incl. quiet zone)
                var size = Sizing.Required(text.AsSpan(), ecc, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });
                var modules = new byte[size.BufferSize];
                RmQRCodeGenerator.CreateRmQRCode(text.AsSpan(), ecc, modules, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });
                await Assert.That(RmQRCodeDecoder.TryDecode(modules, size.Width, size.Height, out var text3, out var info3)).IsTrue();
                await Assert.That(text3).IsEqualTo(text);
                await Assert.That(info3.Version).IsEqualTo(version);

                var destination = new char[RmQRCodeDecoder.GetMaxDecodedLength(version)];
                await Assert.That(RmQRCodeDecoder.TryDecode(modules, size.Width, size.Height, destination, out var written, out var info4)).IsTrue();
                await Assert.That(new string(destination, 0, written)).IsEqualTo(text);
                await Assert.That(info4.EccLevel).IsEqualTo(ecc);
                await Assert.That(written).IsLessThanOrEqualTo(RmQRCodeDecoder.GetMaxDecodedLength(version));
            }
        }
    }

    [Test]
    public async Task Decode_AsymmetricPadding_IsLocatedByTheDarkBoundingBox()
    {
        var text = "ASYMMETRIC";
        var core = RmQRCodeGenerator.CreateRmQRCode(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = 0 });
        var cw = core.Width;
        var ch = core.Height;
        // 3 light columns left, 7 right, 1 row top, 5 bottom.
        var width = cw + 10;
        var height = ch + 6;
        var modules = new byte[width * height];
        for (var r = 0; r < ch; r++)
            for (var c = 0; c < cw; c++)
                modules[(r + 1) * width + c + 3] = core[r, c] ? (byte)1 : (byte)0;

        await Assert.That(RmQRCodeDecoder.TryDecode(modules, width, height, out var decoded, out var info)).IsTrue();
        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Version).IsEqualTo(core.Version);
    }

    [Test]
    public async Task Decode_Utf8_AndLatin1_RoundTrip()
    {
        foreach (var text in new[] { "こんにちは世界", "naïve café", "😀 emoji", "" })
        {
            var data = RmQRCodeGenerator.CreateRmQRCode(text, RmQREccLevel.M);
            await Assert.That(RmQRCodeDecoder.TryDecode(data, out var decoded, out var info)).IsTrue().Because(text);
            await Assert.That(decoded).IsEqualTo(text);
            await Assert.That(info.ErrorsCorrected).IsEqualTo(0);
        }
    }

    [Test]
    public async Task MatrixDecoderStackBudgets_CoverEveryVersion()
    {
        foreach (var (version, ecc) in AllVersionEcc())
        {
            var info = RmQRConstants.GetEccInfo(version, ecc);
            var longest = Math.Max(info.CodewordsInGroup1, info.BlocksInGroup2 > 0 ? info.CodewordsInGroup2 : 0) + info.ECCPerBlock;
            await Assert.That(longest).IsLessThanOrEqualTo(RmQRMatrixDecoder.MaxBlockCodewords);
            await Assert.That(info.TotalDataCodewords).IsLessThanOrEqualTo(RmQRMatrixDecoder.MaxDataCodewords);
            await Assert.That(RmQRConstants.GetTotalCodewordCount(version)).IsLessThanOrEqualTo(RmQRMatrixDecoder.MaxTotalCodewords);
        }
    }

    [Test]
    public async Task GetMaxDecodedLength_BoundsEveryModeAtM_AndRejectsInvalidVersion()
    {
        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            var max = RmQRCodeDecoder.GetMaxDecodedLength(version);
            await Assert.That(max).IsGreaterThanOrEqualTo(RmQRVersionSelector.GetMaxDataLength(version, RmQREccLevel.M, Internals.EncodingMode.Numeric));
            await Assert.That(max).IsGreaterThanOrEqualTo(RmQRVersionSelector.GetMaxDataLength(version, RmQREccLevel.M, Internals.EncodingMode.Alphanumeric));
            await Assert.That(max).IsGreaterThanOrEqualTo(RmQRVersionSelector.GetMaxDataLength(version, RmQREccLevel.M, Internals.EncodingMode.Byte));
        }
        await Assert.That(() => RmQRCodeDecoder.GetMaxDecodedLength((RmQRVersion)0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeDecoder.GetMaxDecodedLength((RmQRVersion)33)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Decode_RejectsNonRmqrInputs()
    {
        // Standard QR v1 (21×21) and Micro QR M2 (13×13) matrices are square, not an rMQR size.
        var standard = new byte[21 * 21];
        QRCodeGenerator.CreateQrCode("hello".AsSpan(), ECCLevel.M, standard, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
        await Assert.That(RmQRCodeDecoder.TryDecode(standard, 21, 21, out _, out var info)).IsFalse();
        await Assert.That(info.Status).IsEqualTo(QRCodeDecodeStatus.InvalidMatrix);

        var micro = new byte[13 * 13];
        MicroQRCodeGenerator.CreateMicroQRCode("12345".AsSpan(), MicroQREccLevel.L, micro, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
        await Assert.That(RmQRCodeDecoder.TryDecode(micro, 13, 13, out _, out info)).IsFalse();
        await Assert.That(info.Status).IsEqualTo(QRCodeDecodeStatus.InvalidMatrix);

        // Blank and transposed rMQR sizes.
        await Assert.That(RmQRCodeDecoder.TryDecode(new byte[43 * 7], 43, 7, out _, out info)).IsFalse();
        await Assert.That(info.Status).IsEqualTo(QRCodeDecodeStatus.InvalidMatrix);
        var data = RmQRCodeGenerator.CreateRmQRCode("1", RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = 0 });
        var modules = new byte[data.Width * data.Height];
        for (var r = 0; r < data.Height; r++)
            for (var c = 0; c < data.Width; c++)
                modules[c * data.Height + r] = data[r, c] ? (byte)1 : (byte)0; // transposed layout
        await Assert.That(RmQRCodeDecoder.TryDecode(modules, data.Height, data.Width, out _, out info)).IsFalse();

        // Argument validation.
        await Assert.That(() => RmQRCodeDecoder.TryDecode(new byte[10], 43, 7, out _, out _)).Throws<ArgumentException>();
        await Assert.That(() => RmQRCodeDecoder.TryDecode(new byte[10], 0, 7, out _, out _)).Throws<ArgumentException>();
        await Assert.That(() => RmQRCodeDecoder.TryDecode((RmQRCodeData)null!, out _)).Throws<ArgumentNullException>();
    }

#if !DEBUG
    [Test]
    public async Task Decode_CharSpanDestination_IsAllocationFree()
    {
        var content = "0123456789ABCDEF ZERO ALLOC";
        var calculated = Sizing.Required(content.AsSpan(), RmQREccLevel.H, new RmQRCodeGeneratorOptions { QuietZoneSize = 2 });
        var buffer = new byte[calculated.BufferSize];
        RmQRCodeGenerator.CreateRmQRCode(content.AsSpan(), RmQREccLevel.H, buffer, new RmQRCodeGeneratorOptions { QuietZoneSize = 2 });
        var destination = new char[RmQRCodeDecoder.GetMaxDecodedLength(calculated.Version)];

        for (var i = 0; i < 3; i++)
            RmQRCodeDecoder.TryDecode(buffer, calculated.Width, calculated.Height, destination, out _, out _);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 16; i++)
            RmQRCodeDecoder.TryDecode(buffer, calculated.Width, calculated.Height, destination, out _, out _);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0);
    }
#endif
}
