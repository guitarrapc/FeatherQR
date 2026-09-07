using FeatherQR.SkiaSharp;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="MicroQRSegmentation.Optimal"/> end to end. The three properties every
/// case is held to are: the symbol never selects a larger version than the
/// single-mode default, it still decodes to the original content, and when it lands
/// on the same version as the single-mode default it is byte-for-byte the same
/// matrix. The rest covers the Micro QR-specific branches: per-version mode
/// availability (M1 Numeric-only, M2 without Byte), the version range, mask
/// pinning, and both directions of "does not fit".
/// </summary>
public class MicroQRSegmentationTest
{
    /// <summary>
    /// Contents spanning the equivalence classes: single-mode only (no gain
    /// possible), mixed with a gain, mixed without a gain, Latin-1 and UTF-8 Byte
    /// charsets, and the degenerate lengths. Everything here fits under Single too.
    /// </summary>
    public static IEnumerable<string> Corpus() =>
    [
        "1",
        "A",
        "a",
        "12345",
        "ABC123",
        "abc12",
        "AB1234567890",
        "AB12345678901234567",
        "A 12:345/",
        "a1234567890",
        "é12345",
        "あ12",
        "日本1",
    ];

    [Test]
    public async Task Optimal_IsNotTheDefault()
    {
        await Assert.That(default(MicroQRCodeGeneratorOptions).Segmentation).IsEqualTo(MicroQRSegmentation.Single);
        await Assert.That(MicroQRCodeGeneratorOptions.Default.Segmentation).IsEqualTo(MicroQRSegmentation.Single);
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task Optimal_IsNeverLargerThanSingle_AndAlwaysRoundTrips(string content)
    {
        var single = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, MicroQRCodeGeneratorOptions.Default);
        var optimal = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal });

        await Assert.That((int)optimal.Version).IsLessThanOrEqualTo((int)single.Version);

        await Assert.That(MicroQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task Optimal_SameVersionAsSingle_ProducesTheIdenticalMatrix(string content)
    {
        var single = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, MicroQRCodeGeneratorOptions.Default);
        var optimal = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal });

        if (optimal.Version != single.Version)
            return; // a genuine gain; the round-trip test covers it

        await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData());
    }

    [Test]
    [Arguments("AB12345678901234567")]
    [Arguments("ab1234567890123")]
    public async Task Optimal_MixedContent_SelectsSmallerVersionThanSingle(string content)
    {
        var single = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, MicroQRCodeGeneratorOptions.Default);
        var optimal = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal });

        await Assert.That((int)optimal.Version).IsLessThan((int)single.Version);

        await Assert.That(MicroQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    [Arguments("12345")]
    [Arguments("HELLO")]
    [Arguments("hello")]
    public async Task Optimal_SingleModeContent_KeepsTheSameVersion(string content)
    {
        var single = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, MicroQRCodeGeneratorOptions.Default);
        var optimal = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal });

        await Assert.That(optimal.Version).IsEqualTo(single.Version);
        await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData());
    }

    [Test]
    public async Task Optimal_TooLongForEverySingleMode_StillEncodesWhenMixedFits()
    {
        // 21 Byte-mode characters overflow M4-L (15 bytes), but a Byte run plus a
        // Numeric run fit; this is the case Optimal accepts input Single rejects.
        var content = "a" + new string('1', 20);

        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(content, MicroQREccLevel.L, out _, MicroQRCodeGeneratorOptions.Default)).IsFalse();

        var options = new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal };
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(content, MicroQREccLevel.L, out var size, options)).IsTrue();

        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, options);
        await Assert.That(data.Version).IsEqualTo(size.Version);

        await Assert.That(MicroQRCodeDecoder.TryDecode(data, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Optimal_RequestedVersion_FitsWhereSingleModeDoesNot()
    {
        // At M3-L the single Alphanumeric stream (19 chars = 111 bits > 84)
        // overflows, but Alnum(2) + Numeric(17) = 81 bits fits.
        var content = "AB12345678901234567";
        Assert.Throws<ArgumentException>(() => MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M3 }));

        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M3, Segmentation = MicroQRSegmentation.Optimal });
        await Assert.That(data.Version).IsEqualTo(MicroQRVersion.M3);

        await Assert.That(MicroQRCodeDecoder.TryDecode(data, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Optimal_PlansAtM2_WhereByteModeDoesNotExist()
    {
        // M2 offers only Numeric and Alphanumeric; the plan must respect that mode
        // set. "A" + 7 digits: single Alphanumeric is 8 chars = 44 + 4 header = 48
        // bits > M2-L's 40, landing on M3; split Alnum(1) + Numeric(7) =
        // (1+3+6) + (1+4+24) = 39 bits fits M2-L exactly under the cap.
        var content = "A1234567";
        var single = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, MicroQRCodeGeneratorOptions.Default);
        var optimal = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal });

        await Assert.That(optimal.Version).IsEqualTo(MicroQRVersion.M2);
        await Assert.That((int)optimal.Version).IsLessThan((int)single.Version);

        await Assert.That(MicroQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Optimal_WithPinnedMask_UsesThatMask()
    {
        var content = "AB12345678901234567";
        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal, MaskPattern = 2 });

        await Assert.That(MicroQRCodeDecoder.TryDecode(data, out var decoded, out var info)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
        await Assert.That(info.MaskPattern).IsEqualTo(2);
    }

    [Test]
    public async Task Optimal_InvalidSegmentationValue_Throws_OnEveryEntryPoint()
    {
        var options = new MicroQRCodeGeneratorOptions { Segmentation = (MicroQRSegmentation)5 };
        var buffer = new byte[1024];

        var fromCreate = Assert.Throws<ArgumentOutOfRangeException>(() => MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L, options));
        var fromCreateSpan = Assert.Throws<ArgumentOutOfRangeException>(() => MicroQRCodeGenerator.Create("12345".AsSpan(), MicroQREccLevel.L, buffer, options));
        var fromSizing = Assert.Throws<ArgumentOutOfRangeException>(() => MicroQRCodeGenerator.TryGetRequiredBufferSize("12345", MicroQREccLevel.L, out _, options));

        await Assert.That(fromCreate.ParamName).IsEqualTo("segmentation");
        await Assert.That(fromCreateSpan.ParamName).IsEqualTo("segmentation");
        await Assert.That(fromSizing.ParamName).IsEqualTo("segmentation");
    }

    [Test]
    public async Task Optimal_NegativeQuietZone_WinsOverInvalidSegmentation_OnEveryEntryPoint()
    {
        var options = new MicroQRCodeGeneratorOptions { Segmentation = (MicroQRSegmentation)3, QuietZoneSize = -1 };
        var buffer = new byte[1024];

        var fromCreate = Assert.Throws<ArgumentOutOfRangeException>(() => MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L, options));
        var fromCreateSpan = Assert.Throws<ArgumentOutOfRangeException>(() => MicroQRCodeGenerator.Create("12345".AsSpan(), MicroQREccLevel.L, buffer, options));
        var fromSizing = Assert.Throws<ArgumentOutOfRangeException>(() => MicroQRCodeGenerator.TryGetRequiredBufferSize("12345", MicroQREccLevel.L, out _, options));

        await Assert.That(fromCreate.ParamName).IsEqualTo("quietZoneSize");
        await Assert.That(fromCreateSpan.ParamName).IsEqualTo("quietZoneSize");
        await Assert.That(fromSizing.ParamName).IsEqualTo("quietZoneSize");
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task Optimal_SpanDestination_MatchesTheAllocatingOverload(string content)
    {
        foreach (var quietZone in new[] { 0, 2 })
        {
            var options = new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal, QuietZoneSize = quietZone };
            await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(content, MicroQREccLevel.L, out var size, options)).IsTrue();

            var buffer = new byte[size.BufferSize];
            var written = MicroQRCodeGenerator.Create(content.AsSpan(), MicroQREccLevel.L, buffer, options);
            await Assert.That(written).IsEqualTo(size.BufferSize);

            var expected = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, options);
            for (var row = 0; row < size.Size; row++)
            {
                for (var col = 0; col < size.Size; col++)
                {
                    if (expected[row, col] != (buffer[row * size.Size + col] != 0))
                        throw new InvalidOperationException($"module mismatch at ({row}, {col}), quietZone={quietZone}");
                }
            }
        }
    }

    [Test]
    public async Task Builder_WithSegmentation_ProducesADecodableImage()
    {
        var content = "AB12345678901234567";
        var png = new MicroQRCodeImageBuilder(content)
            .WithErrorCorrection(MicroQREccLevel.L)
            .WithSegmentation(MicroQRSegmentation.Optimal)
            .ToByteArray();

        using var bitmap = SKBitmap.Decode(png);
        await Assert.That(MicroQRCodeDecoder.TryDecode(bitmap, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);

        Assert.Throws<ArgumentOutOfRangeException>(() => new MicroQRCodeImageBuilder("12345").WithSegmentation((MicroQRSegmentation)5));
        var data = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L);
        Assert.Throws<InvalidOperationException>(() => new MicroQRCodeImageBuilder(data).WithSegmentation(MicroQRSegmentation.Optimal));
    }

    [Test]
    [Arguments(MicroQREccLevel.M)]
    [Arguments(MicroQREccLevel.Q)]
    public async Task Optimal_HigherEccLevels_NeverLargerAndAlwaysRoundTrip(MicroQREccLevel eccLevel)
    {
        // ECC M narrows every capacity and Q exists only on M4, so the
        // IsValidCombination skip and the smaller windows get real coverage.
        var options = new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal };
        foreach (var content in Corpus())
        {
            var singleFits = MicroQRCodeGenerator.TryGetRequiredBufferSize(content, eccLevel, out var singleSize);
            var optimalFits = MicroQRCodeGenerator.TryGetRequiredBufferSize(content, eccLevel, out var optimalSize, options);

            // Optimal accepts at least what Single accepts, and never a larger version.
            if (singleFits)
            {
                await Assert.That(optimalFits).IsTrue().Because($"content=\"{content}\", ecc={eccLevel}");
                await Assert.That((int)optimalSize.Version).IsLessThanOrEqualTo((int)singleSize.Version);
            }
            if (!optimalFits)
                continue;

            var data = MicroQRCodeGenerator.Create(content, eccLevel, options);
            await Assert.That(data.Version).IsEqualTo(optimalSize.Version);
            await Assert.That(MicroQRCodeDecoder.TryDecode(data, out var decoded)).IsTrue().Because($"content=\"{content}\", ecc={eccLevel}");
            await Assert.That(decoded).IsEqualTo(content);
        }
    }

    [Test]
    public async Task Optimal_EccM_RescuesContentNoSingleModeFits()
    {
        // 19 alphanumeric characters cost 113 bits, over M4-M's 112; split into
        // Alnum(2) + Numeric(17) they cost 85 and fit M4-M.
        var content = "AB12345678901234567";
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(content, MicroQREccLevel.M, out _)).IsFalse();

        var options = new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal };
        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.M, options);
        await Assert.That(data.Version).IsEqualTo(MicroQRVersion.M4);
        await Assert.That(MicroQRCodeDecoder.TryDecode(data, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Optimal_EmptyContent_MatchesSingle()
    {
        var single = MicroQRCodeGenerator.Create("", MicroQREccLevel.L, MicroQRCodeGeneratorOptions.Default);
        var optimal = MicroQRCodeGenerator.Create("", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal });

        await Assert.That(optimal.Version).IsEqualTo(single.Version);
        await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData());
    }

    [Test]
    public async Task Optimal_ErrorDetectionOnly_BehavesLikeSingle()
    {
        var options = new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal };

        // Non-numeric content cannot reach M1, the only ErrorDetectionOnly version:
        // an ordinary "does not fit" on both paths.
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize("A1234567", MicroQREccLevel.ErrorDetectionOnly, out _, options)).IsFalse();
        Assert.Throws<ArgumentException>(() => MicroQRCodeGenerator.Create("A1234567", MicroQREccLevel.ErrorDetectionOnly, options));

        // Numeric content takes the shortcut and must be byte-identical to Single.
        var single = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.ErrorDetectionOnly, MicroQRCodeGeneratorOptions.Default);
        var optimal = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.ErrorDetectionOnly, options);
        await Assert.That(optimal.Version).IsEqualTo(MicroQRVersion.M1);
        await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData());
    }

    [Test]
    public async Task Optimal_VersionRangeBelowTheGain_EmitsTheSingleModeSymbol()
    {
        // The M2 win of "A" + 7 digits is excluded by a range starting at M3, so
        // the single-mode stream (M3) is emitted unchanged.
        var content = "A1234567";
        var range = MicroQRVersionRange.Between(MicroQRVersion.M3, MicroQRVersion.M4);
        var single = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = range });
        var optimal = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = range, Segmentation = MicroQRSegmentation.Optimal });

        await Assert.That(optimal.Version).IsEqualTo(single.Version);
        await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData());
    }

    [Test]
    public async Task Optimal_VersionRange_RescuesWhereNoSingleModeInRangeFits()
    {
        // 19 alphanumeric characters need M4 in a single mode, outside the range;
        // the mixed plan fits M3 inside it.
        var content = "AB12345678901234567";
        var range = MicroQRVersionRange.Between(MicroQRVersion.M2, MicroQRVersion.M3);
        var options = new MicroQRCodeGeneratorOptions { Version = range, Segmentation = MicroQRSegmentation.Optimal };

        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(content, MicroQREccLevel.L, out _, new MicroQRCodeGeneratorOptions { Version = range })).IsFalse();

        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, options);
        await Assert.That(data.Version).IsEqualTo(MicroQRVersion.M3);
        await Assert.That(MicroQRCodeDecoder.TryDecode(data, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Optimal_ContentBeyondEveryPlan_FailsLikeSingle()
    {
        // 41 characters exceed even the all-Numeric maximum (35), so no plan exists;
        // both surfaces report it the single-mode way.
        var content = "a" + new string('1', 40);
        var options = new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal };

        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(content, MicroQREccLevel.L, out _, options)).IsFalse();
        Assert.Throws<ArgumentException>(() => MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, options));
    }

    [Test]
    public async Task Optimal_Latin1RunThatLooksLikeUtf8_EmitsTheSingleModeStream()
    {
        // "Ã©" narrows to C3 A9, which alone is valid UTF-8 for "é". Micro QR has no
        // ECI, so the decoder resolves each byte run's charset heuristically; a split
        // that isolates C3 A9 from the disambiguating trailing E9 would be misread.
        // The planner must refuse such a plan and emit the single-mode stream, which
        // round-trips (the whole payload is invalid UTF-8 thanks to the lone E9).
        var content = "Ã©123456789012é";
        var single = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, MicroQRCodeGeneratorOptions.Default);
        var optimal = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal });

        await Assert.That(optimal.Version).IsEqualTo(single.Version);
        await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData());

        await Assert.That(MicroQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Optimal_Latin1RunWithInvalidUtf8Bytes_StillSplits()
    {
        // The guard must not over-reject: "é" narrows to E9, invalid as UTF-8 in any
        // run, so every byte run still decodes as Latin-1 and the split stays safe.
        var content = "é123456789012é";
        var single = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, MicroQRCodeGeneratorOptions.Default);
        var optimal = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal });

        await Assert.That((int)optimal.Version).IsLessThan((int)single.Version);
        await Assert.That(MicroQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Optimal_Latin1LookalikeOnlyAMixedPlanFits_RefusesRatherThanCorrupts()
    {
        // 19 Latin-1 bytes overflow every single Byte-mode capacity, and the only
        // mixed plan isolates the UTF-8-lookalike "Ã©" run. Refusing is the honest
        // outcome: an encode that decodes to different content is worse than none.
        var content = "Ã©" + new string('1', 16) + "é";
        var options = new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal };

        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(content, MicroQREccLevel.L, out _, options)).IsFalse();
        Assert.Throws<ArgumentException>(() => MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, options));
    }

    [Test]
    public async Task Optimal_MidContentBom_EmitsTheSingleModeStream()
    {
        // A split would relocate the mid-content U+FEFF to a byte-run start, where
        // the decoder consumes it as a BOM; the single-mode stream keeps it interior
        // and intact, so the planner must fall back.
        var content = "123456\uFEFFa";
        var single = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, MicroQRCodeGeneratorOptions.Default);
        var optimal = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal });

        await Assert.That(optimal.Version).IsEqualTo(single.Version);
        await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData());

        await Assert.That(MicroQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Optimal_OverflowWhereEveryCheckedPlanIsMisread_ReportsDoesNotFit()
    {
        // 23 UTF-8 bytes fit no single mode, and the minimal-bit plan puts the
        // trailing U+FEFF at a Byte-run start; the documented outcome is "does not
        // fit" rather than a symbol that decodes with the character dropped.
        var content = new string('1', 20) + "\uFEFF";
        var options = new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal };

        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(content, MicroQREccLevel.L, out _, options)).IsFalse();
        Assert.Throws<ArgumentException>(() => MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, options));

        // The sibling without the BOM is rescued, proving the refusal is BOM-driven.
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(new string('1', 20) + "a", MicroQREccLevel.L, out _, options)).IsTrue();
    }

#if !DEBUG
    /// <summary>
    /// The span destination is the library's zero-allocation path; the mixed-mode
    /// planner must keep it that way (Micro QR plans are all-stackalloc). Debug
    /// builds are excluded per repo notes.
    /// </summary>
    /// <remarks>
    /// Runs alone, as its Standard QR and rMQR twins do. Those two rent from
    /// <see cref="System.Buffers.ArrayPool{T}"/>, whose per-core stacks are shared with every other
    /// thread, so a concurrently running test can empty the bucket and turn a rent that would have hit
    /// the cache into a fresh array. Nothing on this path rents today, so the exclusivity is insurance:
    /// it keeps the assertion honest if Micro QR ever outgrows its stack budgets.
    /// </remarks>
    [Test]
    [NotInParallel]
    public async Task Optimal_SpanDestination_IsAllocationFree()
    {
        var mixed = "AB12345678901234567";
        var latin1 = "é12345";
        var utf8 = "日本1";
        var buffer = new byte[1024];
        var options = new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal };

        for (var i = 0; i < 3; i++)
        {
            MicroQRCodeGenerator.Create(mixed.AsSpan(), MicroQREccLevel.L, buffer, options);
            MicroQRCodeGenerator.Create(latin1.AsSpan(), MicroQREccLevel.L, buffer, options);
            MicroQRCodeGenerator.Create(utf8.AsSpan(), MicroQREccLevel.L, buffer, options);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 16; i++)
        {
            MicroQRCodeGenerator.Create(mixed.AsSpan(), MicroQREccLevel.L, buffer, options);
            MicroQRCodeGenerator.Create(latin1.AsSpan(), MicroQREccLevel.L, buffer, options);
            MicroQRCodeGenerator.Create(utf8.AsSpan(), MicroQREccLevel.L, buffer, options);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0);
    }
#endif

    [Test]
    public async Task Optimal_LeadingBom_DecodesLikeSingle()
    {
        // A content-leading U+FEFF is stream-initial under Single too, so both arms
        // drop it on decode; the run-at-offset-0 exemption keeps the split allowed
        // and the two must decode identically (not necessarily to the input). The
        // version assertion is strict: the split genuinely wins here, so losing the
        // exemption (which silently falls back to the identical single-mode
        // symbol) fails this test instead of passing it vacuously.
        var content = "\uFEFF123456a";
        var single = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, MicroQRCodeGeneratorOptions.Default);
        var optimal = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal });

        await Assert.That((int)optimal.Version).IsLessThan((int)single.Version);
        await Assert.That(MicroQRCodeDecoder.TryDecode(single, out var singleDecoded)).IsTrue();
        await Assert.That(MicroQRCodeDecoder.TryDecode(optimal, out var optimalDecoded)).IsTrue();
        await Assert.That(optimalDecoded).IsEqualTo(singleDecoded);
    }

    [Test]
    public async Task Optimal_AgreesWithTryGetRequiredBufferSize()
    {
        foreach (var content in Corpus())
        {
            var options = new MicroQRCodeGeneratorOptions { Segmentation = MicroQRSegmentation.Optimal };
            await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(content, MicroQREccLevel.L, out var size, options)).IsTrue();

            var buffer = new byte[size.BufferSize];
            var written = MicroQRCodeGenerator.Create(content.AsSpan(), MicroQREccLevel.L, buffer, options);
            await Assert.That(written).IsEqualTo(size.BufferSize);

            var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, options);
            await Assert.That(data.Version).IsEqualTo(size.Version);
        }
    }
}
