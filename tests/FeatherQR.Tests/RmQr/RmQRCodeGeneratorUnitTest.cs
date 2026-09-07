using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// Public rMQR generator surface (design record: signatures frozen in Phase 5.0):
/// class / span / span-destination / <c>TryGetRequiredBufferSize</c> agreement, the
/// default fit strategy decision (MinimizeArea, matching both external encoders'
/// automatic choice), argument validation, actionable capacity messages, and
/// Release-only zero allocation on the span path.
/// </summary>
public class RmQRCodeGeneratorUnitTest
{
    public static IEnumerable<(RmQRVersion version, RmQREccLevel ecc)> AllVersionEcc()
    {
        foreach (var v in Enum.GetValues<RmQRVersion>())
        {
            yield return (v, RmQREccLevel.M);
            yield return (v, RmQREccLevel.H);
        }
    }

    // ---- default fit strategy (decision recorded in specs/rmqr-encoder.md) -------

    [Test]
    public async Task DefaultFit_IsMinimizeArea_MatchingExternalEncoders()
    {
        // 12 digits at M fit R7x43 (301 modules) but R11x27 (297) is smaller: the default
        // (and libzint / qrtool auto) choose R11x27; MinimizeHeight gives the flatter R7x43.
        await Assert.That(RmQRCodeGenerator.Create("012345678901", RmQREccLevel.M).Version).IsEqualTo(RmQRVersion.R11x27);
        await Assert.That(RmQRCodeGenerator.Create("012345678901", RmQREccLevel.M, new RmQRCodeGeneratorOptions { FitStrategy = RmQRFitStrategy.MinimizeHeight }).Version).IsEqualTo(RmQRVersion.R7x43);
        await Assert.That(RmQRCodeGenerator.Create(new string('0', 100), RmQREccLevel.M).Version).IsEqualTo(RmQRVersion.R11x77);
        await Assert.That(RmQRCodeGenerator.Create(new string('0', 100), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Height = RmQRHeight.H7 }).Version).IsEqualTo(RmQRVersion.R7x139);
    }

    // ---- data model / dimensions ---------------------------------------------------

    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task Create_RequestedVersion_ClassAndSpanPaths_Agree(RmQRVersion version, RmQREccLevel ecc)
    {
        var text = "RM" + (int)version; // alphanumeric, 3-4 chars: fits every version × ECC (R7x43-H holds 3)
        const int quietZone = 2;

        var data = RmQRCodeGenerator.Create(text, ecc, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });
        await Assert.That(data.Version).IsEqualTo(version);
        await Assert.That(data.Width).IsEqualTo(RmQRConstants.GetWidth(version) + 2 * quietZone);
        await Assert.That(data.Height).IsEqualTo(RmQRConstants.GetHeight(version) + 2 * quietZone);

        var calculated = Sizing.Required(text.AsSpan(), ecc, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });
        await Assert.That(calculated.Version).IsEqualTo(version);
        await Assert.That(calculated.Width).IsEqualTo(data.Width);
        await Assert.That(calculated.Height).IsEqualTo(data.Height);
        await Assert.That(calculated.BufferSize).IsEqualTo(data.Width * data.Height);

        var buffer = new byte[calculated.BufferSize];
        var written = RmQRCodeGenerator.Create(text.AsSpan(), ecc, buffer, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });
        await Assert.That(written).IsEqualTo(calculated.BufferSize);
        for (var row = 0; row < data.Height; row++)
            for (var col = 0; col < data.Width; col++)
                if ((buffer[row * data.Width + col] != 0) != data[row, col])
                    Assert.Fail($"{version}-{ecc}: module ({row},{col}) differs between span and class paths");

        // Quiet zone is light in the span output.
        for (var col = 0; col < data.Width; col++)
        {
            await Assert.That(buffer[col]).IsEqualTo((byte)0);
            await Assert.That(buffer[(data.Height - 1) * data.Width + col]).IsEqualTo((byte)0);
        }
        // Core (0,0) finder corner dark at the quiet-zone offset.
        await Assert.That(buffer[quietZone * data.Width + quietZone]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Create_QuietZoneZero_WritesCoreDirectly()
    {
        var text = "0123456789";
        var calculated = Sizing.Required(text.AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = 0 });
        await Assert.That(calculated.Version).IsEqualTo(RmQRVersion.R11x27);
        await Assert.That(calculated.Width).IsEqualTo(27);
        await Assert.That(calculated.Height).IsEqualTo(11);
        var buffer = new byte[calculated.BufferSize];
        RmQRCodeGenerator.Create(text.AsSpan(), RmQREccLevel.M, buffer, new RmQRCodeGeneratorOptions { QuietZoneSize = 0 });
        var data = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = 0 });
        for (var i = 0; i < buffer.Length; i++)
            if ((buffer[i] != 0) != data[i / 27, i % 27])
                Assert.Fail($"module {i} differs");
        await Assert.That(buffer[0]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Create_MatchesExternalOracleSymbol_ThroughThePublicApi()
    {
        var fixture = FixtureLoader.Load("RmQr", "zint-libzint/r17x139-m-numeric-max");
        var (oracle, width, height) = FixtureLoader.ReadRectangularMatrix(fixture.MatrixPath);
        var data = RmQRCodeGenerator.Create(fixture.Manifest.PayloadText, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139, QuietZoneSize = 0 });
        for (var row = 0; row < height; row++)
            for (var col = 0; col < width; col++)
                if (data[row, col] != (oracle[row * width + col] != 0))
                    Assert.Fail($"module ({row},{col}) differs from the libzint symbol");
        await Assert.That(data.Version).IsEqualTo(RmQRVersion.R17x139);
    }

    [Test]
    public async Task Create_DefaultEci_AutoDetectsUtf8_AndExplicitUtf8Matches()
    {
        const string text = "こんにちは世界";
        var automatic = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R13x59, QuietZoneSize = 0 });
        var explicitUtf8 = RmQRCodeGenerator.Create(text, RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8, Version = RmQRVersion.R13x59, QuietZoneSize = 0 });

        await Assert.That(automatic.GetRawData().AsSpan().SequenceEqual(explicitUtf8.GetRawData())).IsTrue();
        await Assert.That(RmQRCodeDecoder.TryDecode(automatic, out var decoded, out var info)).IsTrue();
        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.Success);
    }

    [Test]
    public async Task Create_DefaultEci_AutoDetectsLatin1_AndAccountsForItsHeaderAtCapacityBoundary()
    {
        const string text = "éé";
        var automaticSize = Sizing.Required(text, RmQREccLevel.H, new RmQRCodeGeneratorOptions { FitStrategy = RmQRFitStrategy.MinimizeHeight });
        var explicitSize = Sizing.Required(text, RmQREccLevel.H, new RmQRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1, FitStrategy = RmQRFitStrategy.MinimizeHeight });

        await Assert.That(automaticSize).IsEqualTo(explicitSize);
        await Assert.That(automaticSize.Version).IsNotEqualTo(RmQRVersion.R7x43);
        var automatic = RmQRCodeGenerator.Create(text, RmQREccLevel.H, new RmQRCodeGeneratorOptions { FitStrategy = RmQRFitStrategy.MinimizeHeight, QuietZoneSize = 0 });
        var explicitLatin1 = RmQRCodeGenerator.Create(text, RmQREccLevel.H, new RmQRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1, FitStrategy = RmQRFitStrategy.MinimizeHeight, QuietZoneSize = 0 });
        await Assert.That(automatic.GetRawData().AsSpan().SequenceEqual(explicitLatin1.GetRawData())).IsTrue();
    }

    [Test]
    public async Task Create_ExplicitEci_ChangesAsciiStream_AndLatin1RoundTrips()
    {
        var noEci = RmQRCodeGenerator.Create("a", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43, QuietZoneSize = 0 });
        var utf8 = RmQRCodeGenerator.Create("a", RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8, Version = RmQRVersion.R7x43, QuietZoneSize = 0 });
        await Assert.That(noEci.GetRawData().AsSpan().SequenceEqual(utf8.GetRawData())).IsFalse();

        var latin1 = RmQRCodeGenerator.Create("Café", RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1 });
        var automaticLatin1 = RmQRCodeGenerator.Create("Café", RmQREccLevel.M);
        await Assert.That(automaticLatin1.GetRawData().AsSpan().SequenceEqual(latin1.GetRawData())).IsTrue();
        await Assert.That(RmQRCodeDecoder.TryDecode(latin1, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo("Café");

        var size = Sizing.Required("Café".AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1 });
        var destination = new byte[size.BufferSize + 4];
        destination.AsSpan().Fill(0xA5);
        var written = RmQRCodeGenerator.Create("Café".AsSpan(), RmQREccLevel.M, destination, new RmQRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1 });
        await Assert.That(written).IsEqualTo(size.BufferSize);
        for (var row = 0; row < size.Height; row++)
            for (var col = 0; col < size.Width; col++)
                if ((destination[row * size.Width + col] != 0) != latin1[row, col])
                    Assert.Fail($"ECI span/class module ({row},{col}) differs");
        await Assert.That(destination.AsSpan(size.BufferSize).ToArray()).IsEquivalentTo(new byte[] { 0xA5, 0xA5, 0xA5, 0xA5 });
        await Assert.That(RmQRCodeDecoder.TryDecode(destination.AsSpan(0, size.BufferSize), size.Width, size.Height, out var spanDecoded, out _)).IsTrue();
        await Assert.That(spanDecoded).IsEqualTo("Café");
    }

    [Test]
    public async Task Create_ExplicitIso88591_RejectsUnrepresentableText()
    {
        await Assert.That(() => RmQRCodeGenerator.Create("日本語", RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1 })).Throws<ArgumentException>();
        await Assert.That(() => RmQRCodeGenerator.Create("a", RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = (EciMode)999 })).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task DefaultLiteralInTheOptionsSlot_ResolvesAndMeansAllDefaults()
    {
        // `default` in the options position has to bind to RmQRCodeGeneratorOptions and not
        // to the Span<byte> destination overload. It compiles either way for a string first
        // argument, so the assertion is that it produced a symbol rather than an empty write.
        var data = RmQRCodeGenerator.Create("1", RmQREccLevel.M, default);
        var omitted = RmQRCodeGenerator.Create("1", RmQREccLevel.M);

        await Assert.That(data).IsNotNull();
        await Assert.That(data.Version).IsEqualTo(omitted.Version);
        await Assert.That(data.GetRawData().AsSpan().SequenceEqual(omitted.GetRawData())).IsTrue();
    }

    // ---- validation and messages ------------------------------------------------------

    [Test]
    public async Task Create_RejectsInvalidArguments()
    {
        await Assert.That(() => RmQRCodeGenerator.Create("1", (RmQREccLevel)2)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("1", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = (RmQRVersion)0 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("1", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = (RmQRVersion)33 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("1", RmQREccLevel.M, new RmQRCodeGeneratorOptions { FitStrategy = (RmQRFitStrategy)5 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("1", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Height = (RmQRHeight)10 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("1", RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = -1 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("1", RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = 10_001 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("1", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R9x43, Height = RmQRHeight.H7 })).Throws<ArgumentException>();
    }

    [Test]
    public async Task Create_SpanDestinationTooSmall_Throws_WithSizingHint()
    {
        var ex = await Assert.That(() => RmQRCodeGenerator.Create("1".AsSpan(), RmQREccLevel.M, new byte[10])).Throws<ArgumentException>();
        await Assert.That(ex!.Message).Contains(nameof(RmQRCodeGenerator.TryGetRequiredBufferSize));
    }

    [Test]
    public async Task Create_TooLong_MessagesAreActionable()
    {
        var auto = await Assert.That(() => RmQRCodeGenerator.Create(new string('a', 151), RmQREccLevel.M)).Throws<ArgumentException>();
        await Assert.That(auto!.Message).Contains("151 bytes");
        await Assert.That(auto.Message).Contains("150 bytes");
        await Assert.That(auto.Message).Contains("R17x139");
        await Assert.That(auto.Message).Contains("QRCodeGenerator");

        var fixedVersion = await Assert.That(() => RmQRCodeGenerator.Create("ABCDEFGH", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 })).Throws<ArgumentException>();
        await Assert.That(fixedVersion!.Message).Contains("R7x43");
        await Assert.That(fixedVersion.Message).Contains("8 characters");
        await Assert.That(fixedVersion.Message).Contains("7 characters");

        var fixedHeight = await Assert.That(() => RmQRCodeGenerator.Create(new string('7', 103), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Height = RmQRHeight.H7 })).Throws<ArgumentException>();
        await Assert.That(fixedHeight!.Message).Contains("103 digits");
        await Assert.That(fixedHeight.Message).Contains("102 digits");
    }

    [Test]
    public async Task Create_EmptyText_IsAllowed()
    {
        var data = RmQRCodeGenerator.Create("", RmQREccLevel.H);
        await Assert.That(data.Version).IsEqualTo(RmQRVersion.R11x27);
    }

#if !DEBUG
    [Test]
    public async Task Create_SpanDestination_IsAllocationFree()
    {
        // Steady-state span-destination encoding (Latin-1 and UTF-8, largest version) must
        // not allocate; Debug builds are excluded per repo notes.
        var latin = "the quick brown fox jumps over the lazy dog?! the quick brown fox jumps over the lazy dog?! the quick brown fox";
        var utf8 = "rMQR 矩形コード ✓ naïve café";
        var size = Sizing.Required(latin.AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 });
        var buffer = new byte[size.BufferSize];
        for (var i = 0; i < 3; i++)
        {
            RmQRCodeGenerator.Create(latin.AsSpan(), RmQREccLevel.M, buffer, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 });
            RmQRCodeGenerator.Create(utf8.AsSpan(), RmQREccLevel.H, buffer, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 });
            RmQRCodeGenerator.Create("012345678901".AsSpan(), RmQREccLevel.M, buffer);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 16; i++)
        {
            RmQRCodeGenerator.Create(latin.AsSpan(), RmQREccLevel.M, buffer, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 });
            RmQRCodeGenerator.Create(utf8.AsSpan(), RmQREccLevel.H, buffer, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 });
            RmQRCodeGenerator.Create("012345678901".AsSpan(), RmQREccLevel.M, buffer);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsEqualTo(0);
    }
#endif

    // ---- span destination with a quiet zone: identical to the class API, nothing written past the required size ----

    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task CreateSpan_QuietZones_MatchClassApiAndTouchOnlyRequiredBytes(RmQRVersion version, RmQREccLevel ecc)
    {
        var text = new string('7', 3);
        foreach (var quietZone in new[] { 0, 1, 2, 5 })
        {
            var data = RmQRCodeGenerator.Create(text, ecc, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });
            var size = Sizing.Required(text.AsSpan(), ecc, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });
            var buffer = new byte[size.BufferSize + 4];
            buffer.AsSpan().Fill(0xA5);
            var written = RmQRCodeGenerator.Create(text.AsSpan(), ecc, buffer, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone });
            await Assert.That(written).IsEqualTo(size.BufferSize);
            for (var row = 0; row < data.Height; row++)
                for (var col = 0; col < data.Width; col++)
                    if ((buffer[row * data.Width + col] != 0) != data[row, col] || buffer[row * data.Width + col] > 1)
                        Assert.Fail($"{version}-{ecc} qz {quietZone}: module ({row},{col}) = {buffer[row * data.Width + col]}, class API {data[row, col]}");
            await Assert.That(buffer.AsSpan(size.BufferSize).ToArray()).IsEquivalentTo(new byte[] { 0xA5, 0xA5, 0xA5, 0xA5 });
        }
    }
}
