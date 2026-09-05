namespace FeatherQR.Tests;

/// <summary>
/// The non-throwing sizing surface across all three symbologies: <c>false</c> only ever
/// means "does not fit", argument errors still throw. The load-bearing case is
/// <c>ReportedSize_MatchesTheEncodeItDescribes</c> — a divergence there means a buffer sized by
/// <c>Try</c> is the wrong size for the matrix <c>Create</c> writes.
/// </summary>
public class TryGetRequiredBufferSizeTest
{
    // Content that fits nothing: rMQR tops out at 361 digits / 150 bytes (R17x139-M),
    // Micro QR at 35 digits (M4-L), Standard QR at 2,953 bytes (v40-L).
    private static readonly string TooLongForRmQR = new('0', 380);

    // ---- rMQR: fits / does not fit ------------------------------------------------

    [Test]
    public async Task RmQR_Fits_ReturnsTrue_AndDescribesTheSymbol()
    {
        var ok = RmQRCodeGenerator.TryGetRequiredBufferSize("012345678901", RmQREccLevel.M, out var size);
        await Assert.That(ok).IsTrue();

        // rMQR has no throwing sizing overload to agree with, so the symbol an encode
        // actually produces is the reference the reported size is checked against.
        var symbol = RmQRCodeGenerator.Create("012345678901", RmQREccLevel.M);
        await Assert.That(size.Version).IsEqualTo(symbol.Version);
        await Assert.That(size.Width).IsEqualTo(symbol.Width);
        await Assert.That(size.Height).IsEqualTo(symbol.Height);
        await Assert.That(size.BufferSize).IsEqualTo(symbol.Width * symbol.Height);
    }

    [Test]
    public async Task RmQR_TooLong_ReturnsFalse_AndLeavesSizeDefault()
    {
        var ok = RmQRCodeGenerator.TryGetRequiredBufferSize(TooLongForRmQR, RmQREccLevel.M, out var size);

        await Assert.That(ok).IsFalse();
        await Assert.That(size.BufferSize).IsEqualTo(0);
        await Assert.That(size.Width).IsEqualTo(0);
        await Assert.That(size.Height).IsEqualTo(0);
    }

    [Test]
    public async Task RmQR_RequestedVersionTooSmall_ReturnsFalse()
    {
        // R7x43 at M holds 12 digits; 13 is one over.
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize("0123456789012", RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 })).IsFalse();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize("012345678901", RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 })).IsTrue();
    }

    [Test]
    public async Task RmQR_HeightConstraintExcludesEveryFittingVersion_ReturnsFalse()
    {
        // 150 digits fit at M (R11x139 holds 198) but no 7-module-high version does
        // (R7x139, the widest, holds 102). The height is legal, the content is not.
        var content = new string('0', 150);
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(content, RmQREccLevel.M, out _)).IsTrue();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(content, RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { Height = RmQRHeight.H7 })).IsFalse();
    }

    // ---- rMQR: mixed-mode segmentation --------------------------------------------

    [Test]
    public async Task RmQR_OptimalSegmentation_FitsOnlyWhenSplit_ReturnsTrue()
    {
        // 100 letters then 100 digits is 200 Byte-mode characters, 50 over the largest
        // capacity, but fits once the digit run is split off into Numeric.
        var content = new string('a', 100) + new string('0', 100);

        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(content, RmQREccLevel.M, out _)).IsFalse();

        var ok = RmQRCodeGenerator.TryGetRequiredBufferSize(content, RmQREccLevel.M, out var size, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });
        var symbol = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });

        await Assert.That(ok).IsTrue();
        await Assert.That(size.Version).IsEqualTo(symbol.Version);
        await Assert.That(size.BufferSize).IsEqualTo(symbol.Width * symbol.Height);
    }

    [Test]
    public async Task RmQR_OptimalSegmentation_StillTooLong_ReturnsFalse()
    {
        var content = new string('a', 200) + new string('0', 200);
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(content, RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal })).IsFalse();
    }

    [Test]
    public async Task RmQR_OptimalSegmentation_RequestedVersionTooSmall_ReturnsFalse()
    {
        var content = new string('a', 100) + new string('0', 100);
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(content, RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43, Segmentation = RmQRSegmentation.Optimal })).IsFalse();
    }

    // ---- rMQR: ECI overload --------------------------------------------------------

    [Test]
    public async Task RmQR_WithEci_Fits_AndDescribesTheSymbol()
    {
        var ok = RmQRCodeGenerator.TryGetRequiredBufferSize("日本語", RmQREccLevel.M, out var size, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8 });
        var symbol = RmQRCodeGenerator.Create("日本語", RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8 });

        await Assert.That(ok).IsTrue();
        await Assert.That(size.Version).IsEqualTo(symbol.Version);
        await Assert.That(size.BufferSize).IsEqualTo(symbol.Width * symbol.Height);
    }

    [Test]
    public async Task RmQR_WithEci_TooLong_ReturnsFalse()
    {
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(TooLongForRmQR, RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8 })).IsFalse();
    }

    public static IEnumerable<(string content, RmQREccLevel ecc, EciMode eciMode, RmQRSegmentation segmentation)> EciAgreementCases()
    {
        // Mixed-mode planning under an explicit ECI is its own path: the 12-bit ECI
        // prefix is priced separately from the per-run headers, so it can land on a
        // different version than either the non-ECI or the single-mode fit.
        string[] contents =
        [
            "", "1", "abc123", "日本語", "café-42",
            "https://example.com/p/1234567890123456",
            new string('a', 60) + new string('0', 60),
            new string('a', 100) + new string('0', 100),
        ];

        foreach (var content in contents)
            foreach (var ecc in new[] { RmQREccLevel.M, RmQREccLevel.H })
                foreach (var eciMode in new[] { EciMode.Default, EciMode.Utf8 })
                    foreach (var segmentation in Enum.GetValues<RmQRSegmentation>())
                        yield return (content, ecc, eciMode, segmentation);
    }

    /// <summary>
    /// rMQR has no throwing sizing overload to agree with, since the sizing surface became
    /// Try-only before 1.2.0 shipped, so the
    /// agreement asserted here is the one that was always the point: the size and version
    /// reported for a set of options are the size and version an encode with those same
    /// options produces, and the result decodes back to the input.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(EciAgreementCases))]
    public async Task RmQR_WithEci_ReportedSize_MatchesTheEncodeItDescribes(string content, RmQREccLevel ecc, EciMode eciMode, RmQRSegmentation segmentation)
    {
        var options = new RmQRCodeGeneratorOptions { EciMode = eciMode, FitStrategy = RmQRFitStrategy.MinimizeArea, Height = null, QuietZoneSize = 2, Segmentation = segmentation };

        if (!RmQRCodeGenerator.TryGetRequiredBufferSize(content, ecc, out var size, options))
        {
            // "Does not fit" must be the whole meaning of false: encoding the same content
            // with the same options has to fail too, rather than quietly succeeding.
            await Assert.That(() => RmQRCodeGenerator.Create(content, ecc, options)).Throws<ArgumentException>();
            await Assert.That(size).IsEqualTo(default(RmQRCodeCalculatedSize));
            return;
        }

        // Encoding under the reported options must fill exactly the reported buffer.
        var buffer = new byte[size.BufferSize];
        var written = RmQRCodeGenerator.Create(content, ecc, buffer, options);
        await Assert.That(written).IsEqualTo(size.BufferSize);

        // ...and the version it chose must be the one that was reported, which is what
        // makes feeding size.Version back into a second encode safe.
        var pinned = RmQRCodeGenerator.Create(content, ecc, options with { Version = size.Version });
        await Assert.That(pinned.Version).IsEqualTo(size.Version);

        if (content.Length > 0)
        {
            await Assert.That(RmQRCodeDecoder.TryDecode(buffer, size.Width, size.Height, out var decoded, out _)).IsTrue();
            await Assert.That(decoded).IsEqualTo(content);
        }
    }

    // ---- rMQR: argument errors still throw ----------------------------------------

    [Test]
    public async Task RmQR_InvalidArguments_Throw_NotFalse()
    {
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSize("1", (RmQREccLevel)2, out _)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSize("1", RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { Version = (RmQRVersion)0 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSize("1", RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { Version = (RmQRVersion)33 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSize("1", RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { FitStrategy = (RmQRFitStrategy)5 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSize("1", RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { Height = (RmQRHeight)10 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSize("1", RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { QuietZoneSize = -1 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSize("1", RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { QuietZoneSize = 10_001 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSize("1", RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { Segmentation = (RmQRSegmentation)7 })).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task RmQR_VersionAndHeightContradiction_Throws_NotFalse()
    {
        // Both are individually legal; the caller asked for two different things.
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSize("1", RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R9x43, Height = RmQRHeight.H7 })).Throws<ArgumentException>();
    }

    [Test]
    public async Task RmQR_WithEci_InvalidEciOrCharsetMismatch_Throws_NotFalse()
    {
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSize("a", RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { EciMode = (EciMode)999 })).Throws<ArgumentOutOfRangeException>();
        // The caller declared Latin-1 and the content is not Latin-1: a broken promise,
        // not a capacity outcome. Reporting "too long" here would hide the real cause.
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSize("日本語", RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1 })).Throws<ArgumentException>();
    }

    // ---- rMQR: the reported size describes the encode, over a broad matrix --------

    public static IEnumerable<(string content, RmQREccLevel ecc, RmQRFitStrategy strategy, RmQRHeight? height, RmQRSegmentation segmentation)> AgreementCases()
    {
        string[] contents =
        [
            "",
            "1",
            "012345678901",
            "HELLO WORLD",
            "https://example.com/p/1234567890123456",
            new string('0', 150),
            new string('0', 361),
            new string('0', 362),
            new string('a', 100) + new string('0', 100),
            new string('a', 200) + new string('0', 200),
            TooLongForRmQR,
        ];

        foreach (var content in contents)
            foreach (var ecc in new[] { RmQREccLevel.M, RmQREccLevel.H })
                foreach (var strategy in Enum.GetValues<RmQRFitStrategy>())
                    foreach (var height in new RmQRHeight?[] { null, RmQRHeight.H7, RmQRHeight.H11, RmQRHeight.H17 })
                        foreach (var segmentation in Enum.GetValues<RmQRSegmentation>())
                            yield return (content, ecc, strategy, height, segmentation);
    }

    /// <summary>
    /// The broad matrix, over the same invariant as the ECI case: <c>true</c> means an
    /// encode with these options fills exactly the reported buffer at the reported
    /// version, and <c>false</c> means that encode fails.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(AgreementCases))]
    public async Task RmQR_ReportedSize_MatchesTheEncodeItDescribes(string content, RmQREccLevel ecc, RmQRFitStrategy strategy, RmQRHeight? height, RmQRSegmentation segmentation)
    {
        var options = new RmQRCodeGeneratorOptions { FitStrategy = strategy, Height = height, QuietZoneSize = 2, Segmentation = segmentation };

        if (!RmQRCodeGenerator.TryGetRequiredBufferSize(content, ecc, out var size, options))
        {
            await Assert.That(() => RmQRCodeGenerator.Create(content, ecc, options)).Throws<ArgumentException>();
            await Assert.That(size).IsEqualTo(default(RmQRCodeCalculatedSize));
            return;
        }

        var buffer = new byte[size.BufferSize];
        var written = RmQRCodeGenerator.Create(content, ecc, buffer, options);
        await Assert.That(written).IsEqualTo(size.BufferSize);

        var symbol = RmQRCodeGenerator.Create(content, ecc, options);
        await Assert.That(symbol.Version).IsEqualTo(size.Version);
    }

    // ---- the documented two-call pattern -------------------------------------------

    [Test]
    [Arguments("012345678901", RmQRSegmentation.Single)]
    [Arguments("https://example.com/p/1234567890123456", RmQRSegmentation.Single)]
    [Arguments("https://example.com/p/1234567890123456", RmQRSegmentation.Optimal)]
    [Arguments("HELLO WORLD 123", RmQRSegmentation.Optimal)]
    public async Task RmQR_ReportedVersion_EncodesIntoTheReportedBuffer(string content, RmQRSegmentation segmentation)
    {
        // README and migration notes promise this composition: feeding the reported
        // version back removes the fit, so the encode cannot fail on length.
        var ok = RmQRCodeGenerator.TryGetRequiredBufferSize(content, RmQREccLevel.M, out var size, new RmQRCodeGeneratorOptions { Segmentation = segmentation });
        await Assert.That(ok).IsTrue();

        var buffer = new byte[size.BufferSize];
        var written = RmQRCodeGenerator.Create(content, RmQREccLevel.M, buffer, new RmQRCodeGeneratorOptions { Version = size.Version, Segmentation = segmentation });

        await Assert.That(written).IsEqualTo(size.BufferSize);
        await Assert.That(RmQRCodeDecoder.TryDecode(buffer, size.Width, size.Height, out var decoded, out _)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task MicroQR_ReportedVersion_EncodesIntoTheReportedBuffer()
    {
        var ok = MicroQRCodeGenerator.TryGetRequiredBufferSize("12345", MicroQREccLevel.L, out var size);
        await Assert.That(ok).IsTrue();

        var buffer = new byte[size.BufferSize];
        var written = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L, buffer, new MicroQRCodeGeneratorOptions { Version = size.Version });
        await Assert.That(written).IsEqualTo(size.BufferSize);
    }

    [Test]
    public async Task StandardQR_ReportedSize_EncodesIntoTheReportedBuffer()
    {
        var ok = QRCodeGenerator.TryGetRequiredBufferSize("hello world", QREccLevel.M, out var size);
        await Assert.That(ok).IsTrue();

        var buffer = new byte[size.BufferSize];
        var written = QRCodeGenerator.Create("hello world".AsSpan(), QREccLevel.M, buffer);
        await Assert.That(written).IsEqualTo(size.BufferSize);
    }

    // ---- Micro QR ------------------------------------------------------------------

    [Test]
    public async Task MicroQR_TooLong_ReturnsFalse_AndLeavesSizeDefault()
    {
        var ok = MicroQRCodeGenerator.TryGetRequiredBufferSize(new string('0', 40), MicroQREccLevel.L, out var size);

        await Assert.That(ok).IsFalse();
        await Assert.That(size.BufferSize).IsEqualTo(0);
        await Assert.That(size.Size).IsEqualTo(0);
    }

    [Test]
    public async Task MicroQR_ModeUnavailableForContent_ReturnsFalse()
    {
        // The text picks the mode, so "M1 cannot carry Byte" is a property of the
        // content, not of the arguments: false, not a throw.
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize("abc", MicroQREccLevel.ErrorDetectionOnly, out _, new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.Exactly(MicroQRVersion.M1) })).IsFalse();
        // No version at all supports Byte at ErrorDetectionOnly (that level pins M1).
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize("abc", MicroQREccLevel.ErrorDetectionOnly, out _)).IsFalse();
    }

    [Test]
    public async Task MicroQR_InvalidArguments_Throw_NotFalse()
    {
        await Assert.That(() => MicroQRCodeGenerator.TryGetRequiredBufferSize("1", (MicroQREccLevel)9, out _)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => MicroQRCodeGenerator.TryGetRequiredBufferSize("1", MicroQREccLevel.L, out _, new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.Exactly((MicroQRVersion)0) })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => MicroQRCodeGenerator.TryGetRequiredBufferSize("1", MicroQREccLevel.L, out _, new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.Exactly((MicroQRVersion)5) })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => MicroQRCodeGenerator.TryGetRequiredBufferSize("1", MicroQREccLevel.L, out _, new MicroQRCodeGeneratorOptions { QuietZoneSize = -1 })).Throws<ArgumentOutOfRangeException>();
        // M1 accepts ErrorDetectionOnly only: a version/ECC contradiction, text-independent.
        await Assert.That(() => MicroQRCodeGenerator.TryGetRequiredBufferSize("1", MicroQREccLevel.L, out _, new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.Exactly(MicroQRVersion.M1) })).Throws<ArgumentException>();
    }

    public static IEnumerable<(string content, MicroQREccLevel ecc, MicroQRVersion? version)> MicroAgreementCases()
    {
        string[] contents = ["", "1", "12345", "123456", "HELLO", "abc", new string('0', 35), new string('0', 36)];
        foreach (var content in contents)
            foreach (var ecc in Enum.GetValues<MicroQREccLevel>())
                foreach (var version in new MicroQRVersion?[] { null, MicroQRVersion.M1, MicroQRVersion.M2, MicroQRVersion.M3, MicroQRVersion.M4 })
                    yield return (content, ecc, version);
    }

    /// <summary>
    /// Sizing and encoding have to answer the same question. Until 2.0.0 this swept the
    /// throwing overload against the <c>Try</c> one; with the throwing overload removed,
    /// the encode itself is the reference, which is the comparison that actually matters
    /// to a caller: a buffer sized here has to be the buffer the encode fills.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(MicroAgreementCases))]
    public async Task MicroQR_Sizing_AgreesWithEncoding(string content, MicroQREccLevel ecc, MicroQRVersion? version)
    {
        var options = new MicroQRCodeGeneratorOptions { Version = version };

        // A version/ECC contradiction is an argument error rather than a fit question, and
        // both surfaces report it that way.
        if (version is { } v && !IsValidMicroCombination(v, ecc))
        {
            await Assert.That(() => MicroQRCodeGenerator.TryGetRequiredBufferSize(content, ecc, out _, options)).Throws<ArgumentException>();
            await Assert.That(() => MicroQRCodeGenerator.Create(content, ecc, options)).Throws<ArgumentException>();
            return;
        }

        var ok = MicroQRCodeGenerator.TryGetRequiredBufferSize(content, ecc, out var size, options);

        if (ok)
        {
            var data = MicroQRCodeGenerator.Create(content, ecc, options);
            await Assert.That(data.Version).IsEqualTo(size.Version);
            await Assert.That(data.Size).IsEqualTo(size.Size);

            var buffer = new byte[size.BufferSize];
            await Assert.That(MicroQRCodeGenerator.Create(content.AsSpan(), ecc, buffer, options)).IsEqualTo(size.BufferSize);
        }
        else
        {
            await Assert.That(() => MicroQRCodeGenerator.Create(content, ecc, options)).Throws<ArgumentException>()
                .Because("content that does not fit must not encode either");
        }
    }

    private static bool IsValidMicroCombination(MicroQRVersion version, MicroQREccLevel ecc) => version switch
    {
        MicroQRVersion.M1 => ecc == MicroQREccLevel.ErrorDetectionOnly,
        MicroQRVersion.M2 or MicroQRVersion.M3 => ecc is MicroQREccLevel.L or MicroQREccLevel.M,
        _ => ecc is MicroQREccLevel.L or MicroQREccLevel.M or MicroQREccLevel.Q,
    };

    // ---- Standard QR ---------------------------------------------------------------

    [Test]
    public async Task StandardQR_TooLong_ReturnsFalse_AndLeavesSizeDefault()
    {
        // Version 40 at L holds 2,953 bytes.
        var ok = QRCodeGenerator.TryGetRequiredBufferSize(new string('a', 3000), QREccLevel.L, out var size);

        await Assert.That(ok).IsFalse();
        await Assert.That(size.BufferSize).IsEqualTo(0);
        await Assert.That(size.Size).IsEqualTo(0);
        await Assert.That(size.Version).IsEqualTo(0);
    }

    [Test]
    public async Task StandardQR_InvalidArguments_Throw_NotFalse()
    {
        await Assert.That(() => QRCodeGenerator.TryGetRequiredBufferSize("1", QREccLevel.M, out _, new QRCodeGeneratorOptions { QuietZoneSize = -1 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRCodeGenerator.TryGetRequiredBufferSize("1", QREccLevel.M, out _, new QRCodeGeneratorOptions { QuietZoneSize = int.MaxValue })).Throws<ArgumentOutOfRangeException>();
    }

    public static IEnumerable<(string content, QREccLevel ecc, bool utf8BOM, EciMode eciMode)> StandardAgreementCases()
    {
        // The BOM adds 3 bytes and ECI a 12-bit header, so both shift the version boundary;
        // the cases straddle it at v40-L (2,953 bytes / 7,089 digits) and at v1.
        string[] contents =
        [
            "", "1", "hello world", "日本語",
            new string('a', 2953), new string('a', 2954),
            new string('a', 2950), new string('a', 2951),
            new string('0', 7089), new string('0', 7090),
            new string('a', 17), new string('a', 18),
        ];

        foreach (var content in contents)
            foreach (var ecc in Enum.GetValues<QREccLevel>())
                foreach (var eciMode in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
                    foreach (var utf8BOM in new[] { false, true })
                        yield return (content, ecc, utf8BOM, eciMode);
    }

    /// <inheritdoc cref="MicroQR_Sizing_AgreesWithEncoding"/>
    [Test]
    [MethodDataSource(nameof(StandardAgreementCases))]
    public async Task StandardQR_Sizing_AgreesWithEncoding(string content, QREccLevel ecc, bool utf8BOM, EciMode eciMode)
    {
        var options = new QRCodeGeneratorOptions { Utf8Bom = utf8BOM, EciMode = eciMode };
        var ok = QRCodeGenerator.TryGetRequiredBufferSize(content, ecc, out var size, options);

        if (ok)
        {
            var data = QRCodeGenerator.Create(content, ecc, options);
            await Assert.That(data.Version).IsEqualTo(size.Version);
            await Assert.That(data.Size).IsEqualTo(size.Size);

            var buffer = new byte[size.BufferSize];
            await Assert.That(QRCodeGenerator.Create(content.AsSpan(), ecc, buffer, options)).IsEqualTo(size.BufferSize);
        }
        else
        {
            var threw = false;
            try
            {
                QRCodeGenerator.Create(content, ecc, options);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                threw = true;
            }

            await Assert.That(threw).IsTrue().Because("content that does not fit must not encode either");
        }
    }
}
