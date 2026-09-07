namespace FeatherQR.Tests;

/// <summary>
/// Encode → decode round trips through the public Micro QR pipeline
/// (<see cref="MicroQRCodeGenerator"/> → <see cref="MicroQRCodeDecoder"/>):
/// every version/ECC combination, every supported mode, boundary lengths,
/// quiet-zone handling, and span/class API parity.
/// </summary>
public class MicroQRCodeDecoderRoundTripTest
{
    public static IEnumerable<(string text, MicroQREccLevel ecc, MicroQRVersion version)> RoundTripCases()
    {
        yield return ("1", MicroQREccLevel.ErrorDetectionOnly, MicroQRVersion.M1);
        yield return ("12345", MicroQREccLevel.ErrorDetectionOnly, MicroQRVersion.M1);
        yield return ("0123456789", MicroQREccLevel.L, MicroQRVersion.M2);
        yield return ("12345678", MicroQREccLevel.M, MicroQRVersion.M2);
        yield return ("AC-42", MicroQREccLevel.L, MicroQRVersion.M2);
        yield return ("HELLO", MicroQREccLevel.M, MicroQRVersion.M2);
        yield return ("12345678901234567890123", MicroQREccLevel.L, MicroQRVersion.M3);
        yield return ("HELLO WORLD 14", MicroQREccLevel.L, MicroQRVersion.M3);
        yield return ("byte hi", MicroQREccLevel.M, MicroQRVersion.M3);
        yield return ("99999999999999999999999999999999999", MicroQREccLevel.L, MicroQRVersion.M4);
        yield return ("HELLO WORLD PLUS 21ST", MicroQREccLevel.L, MicroQRVersion.M4);
        yield return ("bytes m4 mode", MicroQREccLevel.M, MicroQRVersion.M4);
        yield return ("bytes!!!!", MicroQREccLevel.Q, MicroQRVersion.M4);
        yield return ("Café au lait", MicroQREccLevel.L, MicroQRVersion.M4);
        yield return ("こんにちは", MicroQREccLevel.L, MicroQRVersion.M4);
    }

    [Test]
    [MethodDataSource(nameof(RoundTripCases))]
    public async Task RoundTrip_MicroQRCodeData(string text, MicroQREccLevel ecc, MicroQRVersion version)
    {
        var data = MicroQRCodeGenerator.Create(text, ecc);

        var success = MicroQRCodeDecoder.TryDecode(data, out var decoded, out var info);

        await Assert.That(success).IsTrue();
        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(info.Version).IsEqualTo(version);
        await Assert.That(info.EccLevel).IsEqualTo(ecc);
        await Assert.That(info.MaskPattern).IsBetween(0, 3);
        await Assert.That(info.ErrorsCorrected).IsEqualTo(0);
    }

    [Test]
    [MethodDataSource(nameof(RoundTripCases))]
    public async Task RoundTrip_ModuleMatrix_WithAndWithoutQuietZone(string text, MicroQREccLevel ecc, MicroQRVersion version)
    {
        foreach (var quietZone in (int[])[0, 2, 4])
        {
            var calculated = Sizing.Required(text.AsSpan(), ecc, quietZoneSize: quietZone);
            var modules = new byte[calculated.BufferSize];
            MicroQRCodeGenerator.Create(text.AsSpan(), ecc, modules, new MicroQRCodeGeneratorOptions { QuietZoneSize = quietZone });

            var success = MicroQRCodeDecoder.TryDecode(modules, calculated.Size, out var decoded, out var info);

            await Assert.That(success).IsTrue();
            await Assert.That(decoded).IsEqualTo(text);
            await Assert.That(info.Version).IsEqualTo(version);
            await Assert.That(info.EccLevel).IsEqualTo(ecc);
        }
    }

    [Test]
    [MethodDataSource(nameof(RoundTripCases))]
    public async Task RoundTrip_SpanDestination_MatchesStringOverload(string text, MicroQREccLevel ecc, MicroQRVersion version)
    {
        // Quiet zone 0 exercises the in-place fast path, 2 the core-copy branch.
        foreach (var quietZone in (int[])[0, 2])
        {
            var calculated = Sizing.Required(text.AsSpan(), ecc, quietZoneSize: quietZone);
            var modules = new byte[calculated.BufferSize];
            MicroQRCodeGenerator.Create(text.AsSpan(), ecc, modules, new MicroQRCodeGeneratorOptions { QuietZoneSize = quietZone });

            var destination = new char[MicroQRCodeDecoder.GetMaxDecodedLength(version)];
            var success = MicroQRCodeDecoder.TryDecode(modules, calculated.Size, destination, out var charsWritten, out var info);

            await Assert.That(success).IsTrue();
            await Assert.That(new string(destination, 0, charsWritten)).IsEqualTo(text);
            await Assert.That(info.Version).IsEqualTo(version);
        }
    }

    [Test]
    public async Task TryDecode_AsymmetricBorder_ReportsInvalidMatrix()
    {
        // A valid M1 core placed at row 1, column 0 of a 13×13 buffer: the first
        // dark row (1) and the minimum dark column (0) disagree, so this is not a
        // uniform quiet zone and must be rejected rather than misread.
        var calculated = Sizing.Required("12345".AsSpan(), MicroQREccLevel.ErrorDetectionOnly, quietZoneSize: 0);
        var core = new byte[calculated.BufferSize];
        MicroQRCodeGenerator.Create("12345".AsSpan(), MicroQREccLevel.ErrorDetectionOnly, core, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });

        const int size = 13;
        var modules = new byte[size * size];
        for (var row = 0; row < calculated.Size; row++)
        {
            core.AsSpan(row * calculated.Size, calculated.Size).CopyTo(modules.AsSpan((row + 1) * size));
        }

        var success = MicroQRCodeDecoder.TryDecode(modules, size, out _, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.InvalidMatrix);
    }

    [Test]
    public async Task TryDecode_NullData_Throws()
    {
        await Assert.That(() => MicroQRCodeDecoder.TryDecode((MicroQRCodeData)null!, out _)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task GetMaxDecodedLength_InvalidVersion_Throws()
    {
        await Assert.That(() => MicroQRCodeDecoder.GetMaxDecodedLength((MicroQRVersion)0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => MicroQRCodeDecoder.GetMaxDecodedLength((MicroQRVersion)5)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task TryDecode_NonMicroSize_ReportsInvalidMatrix()
    {
        // 12×12 is not a Micro QR size (11/13/15/17)
        var modules = new byte[12 * 12];
        modules[0] = 1; // avoid the all-light early exit hiding the size check

        var success = MicroQRCodeDecoder.TryDecode(modules, 12, out var text, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(text).IsEqualTo("");
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.InvalidMatrix);
    }

    [Test]
    public async Task TryDecode_AllLightMatrix_Fails()
    {
        var modules = new byte[11 * 11];

        var success = MicroQRCodeDecoder.TryDecode(modules, 11, out _, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsNotEqualTo(DecodeStatus.Success);
    }

    [Test]
    public async Task GetMaxDecodedLength_CoversDensestPayload()
    {
        // The densest legal payloads must fit the advertised bound.
        await Assert.That(MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M1)).IsGreaterThanOrEqualTo(5);
        await Assert.That(MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M2)).IsGreaterThanOrEqualTo(10);
        await Assert.That(MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M3)).IsGreaterThanOrEqualTo(23);
        await Assert.That(MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4)).IsGreaterThanOrEqualTo(35);
    }

    [Test]
    public async Task TryDecode_BufferTooSmall_Throws()
    {
        var modules = new byte[11 * 11 - 1];

        await Assert.That(() => MicroQRCodeDecoder.TryDecode(modules, 11, out _, out _)).Throws<ArgumentException>();
    }
}
