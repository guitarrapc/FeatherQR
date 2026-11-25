namespace FeatherQR.Tests;

/// <summary>
/// Encode → decode round trips through the public Micro QR pipeline
/// (<see cref="MicroQRCodeGenerator"/> → <see cref="MicroQRCodeDecoder"/>):
/// every version/ECC combination, every supported mode, boundary lengths,
/// quiet-zone handling, and span/class API parity.
/// </summary>
public class MicroQRCodeDecoderRoundTripTest
{
    public static IEnumerable<(string text, MicroQREccLevel ecc, MicroQRVersion version, bool allowKanji)> RoundTripCases()
    {
        yield return ("1", MicroQREccLevel.ErrorDetectionOnly, MicroQRVersion.M1, false);
        yield return ("12345", MicroQREccLevel.ErrorDetectionOnly, MicroQRVersion.M1, false);
        yield return ("0123456789", MicroQREccLevel.L, MicroQRVersion.M2, false);
        yield return ("12345678", MicroQREccLevel.M, MicroQRVersion.M2, false);
        yield return ("AC-42", MicroQREccLevel.L, MicroQRVersion.M2, false);
        yield return ("HELLO", MicroQREccLevel.M, MicroQRVersion.M2, false);
        yield return ("12345678901234567890123", MicroQREccLevel.L, MicroQRVersion.M3, false);
        yield return ("HELLO WORLD 14", MicroQREccLevel.L, MicroQRVersion.M3, false);
        yield return ("byte hi", MicroQREccLevel.M, MicroQRVersion.M3, false);
        yield return ("99999999999999999999999999999999999", MicroQREccLevel.L, MicroQRVersion.M4, false);
        yield return ("HELLO WORLD PLUS 21ST", MicroQREccLevel.L, MicroQRVersion.M4, false);
        yield return ("bytes m4 mode", MicroQREccLevel.M, MicroQRVersion.M4, false);
        yield return ("bytes!!!!", MicroQREccLevel.Q, MicroQRVersion.M4, false);
        yield return ("Café au lait", MicroQREccLevel.L, MicroQRVersion.M4, false);
        yield return ("こんにちは", MicroQREccLevel.L, MicroQRVersion.M4, false);          // UTF-8 by default: M4-L's 15 bytes
        yield return ("こんにちは", MicroQREccLevel.L, MicroQRVersion.M3, true);           // AllowKanji: 5 of M3-L's 6 characters
        yield return ("こんにちは世界です", MicroQREccLevel.L, MicroQRVersion.M4, true);   // AllowKanji: M4-L's 9 characters, the full count
        yield return ("こんにち～", MicroQREccLevel.L, MicroQRVersion.M4, true);           // ～ has no Kanji cell: UTF-8 even with AllowKanji, M4-L's 15 bytes
    }

    [Test]
    [MethodDataSource(nameof(RoundTripCases))]
    public async Task RoundTrip_MicroQRCodeData(string text, MicroQREccLevel ecc, MicroQRVersion version, bool allowKanji)
    {
        var data = MicroQRCodeGenerator.Create(text, ecc, new MicroQRCodeGeneratorOptions { AllowKanji = allowKanji });

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
    public async Task RoundTrip_ModuleMatrix_WithAndWithoutQuietZone(string text, MicroQREccLevel ecc, MicroQRVersion version, bool allowKanji)
    {
        foreach (var quietZone in (int[])[0, 2, 4])
        {
            var options = new MicroQRCodeGeneratorOptions { QuietZoneSize = quietZone, AllowKanji = allowKanji };
            var calculated = Sizing.Required(text.AsSpan(), ecc, options);
            var modules = new byte[calculated.BufferSize];
            MicroQRCodeGenerator.Create(text.AsSpan(), ecc, modules, options);

            var success = MicroQRCodeDecoder.TryDecode(modules, calculated.Size, out var decoded, out var info);

            await Assert.That(success).IsTrue();
            await Assert.That(decoded).IsEqualTo(text);
            await Assert.That(info.Version).IsEqualTo(version);
            await Assert.That(info.EccLevel).IsEqualTo(ecc);
        }
    }

    [Test]
    [MethodDataSource(nameof(RoundTripCases))]
    public async Task RoundTrip_SpanDestination_MatchesStringOverload(string text, MicroQREccLevel ecc, MicroQRVersion version, bool allowKanji)
    {
        // Quiet zone 0 exercises the in-place fast path, 2 the core-copy branch.
        foreach (var quietZone in (int[])[0, 2])
        {
            var options = new MicroQRCodeGeneratorOptions { QuietZoneSize = quietZone, AllowKanji = allowKanji };
            var calculated = Sizing.Required(text.AsSpan(), ecc, options);
            var modules = new byte[calculated.BufferSize];
            MicroQRCodeGenerator.Create(text.AsSpan(), ecc, modules, options);

            var destination = new char[MicroQRCodeDecoder.GetMaxDecodedLength(version)];
            var success = MicroQRCodeDecoder.TryDecode(modules, calculated.Size, destination, out var charsWritten, out var info);

            await Assert.That(success).IsTrue();
            await Assert.That(new string(destination, 0, charsWritten)).IsEqualTo(text);
            await Assert.That(info.Version).IsEqualTo(version);
        }
    }

    [Test]
    [MethodDataSource(nameof(RoundTripCases))]
    public async Task TryDecode_AsymmetricBorder_IsLocatedByTheDarkBoundingBox(string text, MicroQREccLevel ecc, MicroQRVersion version, bool allowKanji)
    {
        // The timing patterns end dark at (0, n-1) and (n-1, 0), so the dark bounding box is the core under any light border.
        var core = MicroQRCodeGenerator.Create(text, ecc, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0, AllowKanji = allowKanji });
        foreach (var (top, left, bottom, right) in ((int, int, int, int)[])[(1, 0, 1, 2), (0, 3, 3, 0), (4, 1, 0, 3), (0, 0, 2, 2)])
        {
            var size = core.Size + left + right;
            await Assert.That(core.Size + top + bottom).IsEqualTo(size);
            var modules = Place(core, size, top, left);

            await Assert.That(MicroQRCodeDecoder.TryDecode(modules, size, out var decoded, out var info)).IsTrue();
            await Assert.That(decoded).IsEqualTo(text);
            await Assert.That(info.Version).IsEqualTo(version);

            var destination = new char[MicroQRCodeDecoder.GetMaxDecodedLength(version)];
            await Assert.That(MicroQRCodeDecoder.TryDecode(modules, size, destination, out var charsWritten, out _)).IsTrue();
            await Assert.That(new string(destination, 0, charsWritten)).IsEqualTo(text);
        }
    }

    [Test]
    public async Task TryDecode_DarkBoundingBoxNotSquare_ReportsInvalidMatrix()
    {
        // An M1 core at the top-left of a 13×13 buffer with one dark module below it: the box is 11 wide and 13 tall.
        var core = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.ErrorDetectionOnly, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
        const int size = 13;
        var modules = Place(core, size, 0, 0);
        modules[12 * size] = 1;

        await Assert.That(MicroQRCodeDecoder.TryDecode(modules, size, out _, out var info)).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.InvalidMatrix);
        await Assert.That(MicroQRCodeDecoder.TryDecode(modules, size, new char[16], out var charsWritten, out info)).IsFalse();
        await Assert.That(charsWritten).IsEqualTo(0);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.InvalidMatrix);
    }

    [Test]
    public async Task TryDecode_DarkModuleInTheBorder_DoesNotDecode()
    {
        // An M1 core at (1, 1) of a 15×15 buffer and a dark module at (13, 13): the square box is 13, an M2 size, and holds no M2 symbol.
        var core = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.ErrorDetectionOnly, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
        const int size = 15;
        var modules = Place(core, size, 1, 1);
        modules[13 * size + 13] = 1;

        await Assert.That(MicroQRCodeDecoder.TryDecode(modules, size, out var text, out var info)).IsFalse();
        await Assert.That(text).IsEqualTo("");
        await Assert.That(info.Status).IsNotEqualTo(DecodeStatus.Success);
    }

    [Test]
    public async Task TryDecode_StandardQRMatrix_ReportsInvalidMatrix()
    {
        // A version 1 Standard QR core is a square 21 modules wide, not a Micro QR size.
        var standard = new byte[21 * 21];
        QRCodeGenerator.Create("hello".AsSpan(), QREccLevel.M, standard, new QRCodeGeneratorOptions { QuietZoneSize = 0 });

        await Assert.That(MicroQRCodeDecoder.TryDecode(standard, 21, out _, out var info)).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.InvalidMatrix);
    }

    private static byte[] Place(MicroQRCodeData core, int size, int top, int left)
    {
        var modules = new byte[size * size];
        for (var r = 0; r < core.Size; r++)
            for (var c = 0; c < core.Size; c++)
                modules[(r + top) * size + c + left] = core[r, c] ? (byte)1 : (byte)0;
        return modules;
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
