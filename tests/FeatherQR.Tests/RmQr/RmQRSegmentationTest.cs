using SkiaSharp;
using FeatherQR.SkiaSharp;
using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="RmQRSegmentation.Optimal"/> end to end. The three properties every
/// case is held to are: the symbol never has more core modules than the single-mode
/// default (which is not the same as never rendering larger — see
/// <see cref="Optimal_FewerCoreModulesCanStillRenderLarger"/>), it
/// still decodes to the original content, and when it lands on the same version as
/// the single-mode default it is byte-for-byte the same matrix (the blast radius
/// bound the feature is designed around). The rest covers the branches of the
/// version decision: requested version present or absent, height constrained or not,
/// each fit strategy, each charset, and both directions of "does not fit".
/// </summary>
public class RmQRSegmentationTest
{
    private static int Area(RmQRCodeData data) => (data.Width - 4) * (data.Height - 4);

    /// <summary>
    /// Contents spanning the equivalence classes of the decision: single-mode only
    /// (no gain possible), mixed with a gain, mixed without a gain, every charset,
    /// and the degenerate lengths.
    /// </summary>
    public static IEnumerable<string> Corpus() =>
    [
        "0",
        "A",
        "a",
        "1234567890",
        "ABCDEFGHIJ",
        "abcdefghij",
        "123Abc",
        "https://example.com/p/1234567890123456",
        "https://example.com/",
        "ABC-1234567890123456",
        "ORDER 12345 ITEM 6789",
        "a1b2c3d4e5f6g7h8i9",
        "12A34B56C78D90E",
        "SN/2024-000123456789",
        "AAAAAAAAAA1111111111AAAAAAAAAA",
        "x1234567890123456789012345678901234567890",
        "1234567890123456789012345678901234567890x",
        "$%*+-./: 0123456789",
        "éèê1234567890",
        "日本語1234567890",
        "こんにちは",
        "😀😁1234567890",
        "é😀A1",

        // Uniform content: the shapes where the trivial bound rejects every candidate
        // outright, so no cost run happens at all. Without them that filter has no
        // differential coverage, and it is the dominant path in practice. Sized to fit
        // ECC H as well, because the property tests below exercise both levels.
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",

        // Alternating every character: the trivial bound cannot rule this out, so the
        // floor runs and no candidate is accepted.
        "a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7",

        // Alternating in tens: a shape where a per-version cost run, not a bound, is
        // what decides the version.
        "aaaaaaaaaa7777777777aaaaaaaaaa7777777777aaaaaaaaaa7777777777",

        // Ordinary mixed content in the middle of the length range.
        "aaaaaaaaaaaaaaaaaaaaaaaaa7777777777777777777777777",
    ];

    /// <summary>
    /// Content sized for ECC M only, so the differential scan is exercised at lengths
    /// the shared corpus cannot reach (ECC H caps at 74 Byte-mode characters). These
    /// mirror the shapes <c>RmQRSegmentationEncode</c> benchmarks, at its longer lengths.
    /// </summary>
    /// <remarks>
    /// The mixed entries over 73 characters also exercise the planner's ArrayPool
    /// branch, where its parent table outgrows the stack budget, so do not trim this
    /// list to short content. (The uniform and all-numeric entries do not: they finish
    /// at zero cost runs and never allocate a parent table at all.)
    /// </remarks>
    public static IEnumerable<string> LongCorpus() =>
    [
        new string('A', 120),
        new string('a', 150),
        string.Concat(Enumerable.Repeat("a7", 60)),
        string.Concat(Enumerable.Repeat(new string('a', 10) + new string('7', 10), 6)),
        new string('a', 60) + new string('7', 60),
        new string('a', 75) + new string('7', 75),
        new string('7', 361),
    ];

    public static IEnumerable<RmQRFitStrategy> Strategies() => Enum.GetValues<RmQRFitStrategy>();

    // -----------------------------------------------------------------
    // Properties that must hold for every content, level and strategy
    // -----------------------------------------------------------------

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task Optimal_IsNeverLargerThanSingle_AndAlwaysRoundTrips(string content)
    {
        foreach (var ecc in new[] { RmQREccLevel.M, RmQREccLevel.H })
        {
            foreach (var strategy in Strategies())
            {
                var single = RmQRCodeGenerator.Create(content, ecc, new RmQRCodeGeneratorOptions { FitStrategy = strategy });
                var optimal = RmQRCodeGenerator.Create(content, ecc, new RmQRCodeGeneratorOptions { FitStrategy = strategy, Segmentation = RmQRSegmentation.Optimal });

                await Assert.That(Area(optimal)).IsLessThanOrEqualTo(Area(single));
                await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded, out var info)).IsTrue();
                await Assert.That(decoded).IsEqualTo(content);
                await Assert.That(info.Version).IsEqualTo(optimal.Version);
                await Assert.That(info.EccLevel).IsEqualTo(ecc);
            }
        }
    }

    /// <summary>
    /// Whenever segmentation does not shrink the symbol the emitted matrix has to be
    /// the single-mode one, unchanged. This is what keeps the option from perturbing
    /// existing output.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task Optimal_SameVersionAsSingle_ProducesTheIdenticalMatrix(string content)
    {
        foreach (var ecc in new[] { RmQREccLevel.M, RmQREccLevel.H })
        {
            foreach (var strategy in Strategies())
            {
                var single = RmQRCodeGenerator.Create(content, ecc, new RmQRCodeGeneratorOptions { FitStrategy = strategy });
                var optimal = RmQRCodeGenerator.Create(content, ecc, new RmQRCodeGeneratorOptions { FitStrategy = strategy, Segmentation = RmQRSegmentation.Optimal });
                if (optimal.Version != single.Version)
                    continue;

                await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData());
            }
        }
    }

    /// <summary>
    /// The version scan prunes and short-circuits candidates instead of pricing every
    /// one. This pins it against the definitional answer: the best-ranked version
    /// whose exhaustively computed mixed-mode cost fits. An unsound bound or an
    /// over-eager skip shows up here as a wrong version, not merely a slow one.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task Optimal_SelectsWhatAnExhaustiveScanSelects(string content)
    {
        foreach (var ecc in new[] { RmQREccLevel.M, RmQREccLevel.H })
        {
            foreach (var strategy in Strategies())
            {
                var actual = RmQRCodeGenerator.Create(content, ecc, new RmQRCodeGeneratorOptions { FitStrategy = strategy, Segmentation = RmQRSegmentation.Optimal }).Version;
                var expected = ExhaustiveBestVersion(content, ecc, strategy);
                await Assert.That(actual).IsEqualTo(expected);
            }
        }
    }

    /// <summary>
    /// The same differential check at lengths the shared corpus cannot reach, where the
    /// scan's filters behave differently: uniform content is rejected by the trivial
    /// bound before any table is allocated, and coarsely alternating content is the one
    /// shape decided by a per-version cost run.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(LongCorpus))]
    public async Task Optimal_SelectsWhatAnExhaustiveScanSelects_LongContent(string content)
    {
        foreach (var strategy in Strategies())
        {
            var actual = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { FitStrategy = strategy, Segmentation = RmQRSegmentation.Optimal });
            await Assert.That(actual.Version).IsEqualTo(ExhaustiveBestVersion(content, RmQREccLevel.M, strategy));

            await Assert.That(RmQRCodeDecoder.TryDecode(actual, out var decoded)).IsTrue();
            await Assert.That(decoded).IsEqualTo(content);

            var single = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { FitStrategy = strategy });
            await Assert.That(Area(actual)).IsLessThanOrEqualTo(Area(single));
        }
    }

    /// <summary>
    /// Definitional reference: price every version from scratch, keep those that hold
    /// the content, and take the best by the strategy comparator. Deliberately does no
    /// pruning, no memoisation and no single-mode ceiling.
    /// </summary>
    private static RmQRVersion ExhaustiveBestVersion(string content, RmQREccLevel ecc, RmQRFitStrategy strategy)
    {
        var analysis = TextAnalyzer.Analyze(content.AsSpan(), EciMode.Default);
        var charset = analysis.EciMode;
        var eciBits = charset == EciMode.Default ? 0 : 11;

        RmQRVersion? best = null;
        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            int cost;
            if (content.Length == 0)
            {
                // Empty content never plans; the single-mode fit is the answer.
                cost = 3 + RmQRConstants.GetCountIndicatorLength(version, analysis.EncodingMode);
            }
            else
            {
                cost = RmQRSegmentPlanner.MinimumPayloadBits(
                    content.AsSpan(),
                    charset,
                    RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Numeric),
                    RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Alphanumeric),
                    RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte));
            }

            if (cost + eciBits > 8 * RmQRConstants.GetDataCodewordCount(version, ecc))
                continue;
            if (best is null || RmQRVersionSelector.IsBetter(version, best.Value, strategy))
                best = version;
        }

        return best ?? throw new InvalidOperationException("nothing fits");
    }

    /// <summary>
    /// The "never larger" guarantee is about core modules, which is what
    /// <see cref="RmQRFitStrategy"/> ranks by. The quiet zone adds a fixed four modules
    /// to each dimension, so minimising height × width does not minimise
    /// (height + 4) × (width + 4): a flatter, wider symbol can have fewer core modules
    /// and a larger rendered grid. This pins that it really happens, so nobody
    /// "corrects" the documented invariant back to the stronger, false one.
    /// </summary>
    [Test]
    public async Task Optimal_FewerCoreModulesCanStillRenderLarger()
    {
        const string content = "37~yhakdP$%F$SMQINKKTSHJ";

        var single = RmQRCodeGenerator.Create(content, RmQREccLevel.H);
        var optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.H, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });

        await Assert.That(single.Version).IsEqualTo(RmQRVersion.R15x59);
        await Assert.That(optimal.Version).IsEqualTo(RmQRVersion.R11x77);

        // Fewer core modules, as guaranteed...
        await Assert.That(Area(optimal)).IsLessThan(Area(single));
        // ...but a larger quiet-zoned grid, which is not guaranteed and must not be.
        await Assert.That(optimal.Width * optimal.Height).IsGreaterThan(single.Width * single.Height);
        await Assert.That(optimal.Width).IsGreaterThan(single.Width);

        await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    /// <summary>
    /// The consequence a caller actually trips over: sizing with one segmentation and
    /// encoding with another can leave the destination too small.
    /// </summary>
    [Test]
    public async Task Optimal_BufferSizedForSingle_CanBeTooSmall()
    {
        const string content = "37~yhakdP$%F$SMQINKKTSHJ";

        var singleSize = Sizing.Required(content.AsSpan(), RmQREccLevel.H);
        var optimalSize = Sizing.Required(content.AsSpan(), RmQREccLevel.H, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });
        await Assert.That(optimalSize.BufferSize).IsGreaterThan(singleSize.BufferSize);

        var undersized = new byte[singleSize.BufferSize];
        var tooSmall = Assert.Throws<ArgumentException>(() => RmQRCodeGenerator.Create(content.AsSpan(), RmQREccLevel.H, undersized, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal }));
        // Specifically the destination check, not some other capacity error.
        await Assert.That(tooSmall!.ParamName).IsEqualTo("destination");

        // Sizing with the same segmentation is what a caller must do, and it works.
        var sized = new byte[optimalSize.BufferSize];
        await Assert.That(RmQRCodeGenerator.Create(content.AsSpan(), RmQREccLevel.H, sized, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal })).IsEqualTo(optimalSize.BufferSize);
    }

    [Test]
    public async Task Optimal_IsNotTheDefault()
    {
        const string content = "https://example.com/p/1234567890123456";
        var implicitDefault = RmQRCodeGenerator.Create(content, RmQREccLevel.M);
        var explicitSingle = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Single });

        await Assert.That(implicitDefault.Version).IsEqualTo(explicitSingle.Version);
        await Assert.That(implicitDefault.GetRawData()).IsEquivalentTo(explicitSingle.GetRawData());
    }

    // -----------------------------------------------------------------
    // The gain itself
    // -----------------------------------------------------------------

    [Test]
    public async Task Optimal_UrlWithNumericTail_SelectsSmallerSymbolThanSingle()
    {
        // 22 Byte-mode characters followed by 16 digits: Byte-only needs 313 bits
        // (R11x77), splitting into Byte + Numeric needs 249 (R15x43).
        const string content = "https://example.com/p/1234567890123456";

        var single = RmQRCodeGenerator.Create(content, RmQREccLevel.M);
        var optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });

        await Assert.That(single.Version).IsEqualTo(RmQRVersion.R11x77);
        await Assert.That(optimal.Version).IsEqualTo(RmQRVersion.R15x43);
        await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    /// <summary>Contents verified to cross a version boundary once the modes are mixed.</summary>
    [Test]
    [Arguments("Order 12345 item 6789", RmQRVersion.R13x43, RmQRVersion.R9x59)]
    [Arguments("ABC-1234567890123456", RmQRVersion.R11x43, RmQRVersion.R13x27)]
    [Arguments("x1234567890123456789012345678901234567890", RmQRVersion.R11x77, RmQRVersion.R9x59)]
    [Arguments("1234567890123456789012345678901234567890x", RmQRVersion.R11x77, RmQRVersion.R9x59)]
    [Arguments("日本語1234567890", RmQRVersion.R13x43, RmQRVersion.R11x43)]
    [Arguments("éèê1234567890", RmQRVersion.R11x43, RmQRVersion.R13x27)]
    public async Task Optimal_MixedContent_ShrinksTheSymbol(string content, RmQRVersion expectedSingle, RmQRVersion expectedOptimal)
    {
        var single = RmQRCodeGenerator.Create(content, RmQREccLevel.M);
        var optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });

        await Assert.That(single.Version).IsEqualTo(expectedSingle);
        await Assert.That(optimal.Version).IsEqualTo(expectedOptimal);
        await Assert.That(Area(optimal)).IsLessThan(Area(single));
        await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    /// <summary>Content whose single mode is already alphanumeric gains nothing from mixing.</summary>
    [Test]
    [Arguments("ORDER 12345 ITEM 6789")]
    [Arguments("SN/2024-000123456789")]
    [Arguments("$%*+-./: 0123456789")]
    public async Task Optimal_AlphanumericContent_KeepsTheSingleModeSymbol(string content)
    {
        var single = RmQRCodeGenerator.Create(content, RmQREccLevel.M);
        var optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });

        await Assert.That(optimal.Version).IsEqualTo(single.Version);
        await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData());
    }

    /// <summary>Content that a single mode already encodes optimally must not move.</summary>
    [Test]
    [Arguments("1234567890")]
    [Arguments("ABCDEFGHIJ")]
    [Arguments("abcdefghij")]
    [Arguments("こんにちは")]
    public async Task Optimal_SingleModeContent_KeepsTheSameVersion(string content)
    {
        var single = RmQRCodeGenerator.Create(content, RmQREccLevel.M);
        var optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });
        await Assert.That(optimal.Version).IsEqualTo(single.Version);
    }

    // -----------------------------------------------------------------
    // Requested version
    // -----------------------------------------------------------------

    [Test]
    public async Task Optimal_RequestedVersion_FitsWhereSingleModeDoesNot()
    {
        // R7x99-M holds 27 Byte-mode characters; this is 30 characters as Byte but
        // fits once the digit tail becomes a Numeric run.
        const string content = "abcdefghij1234567890abcdefghij";
        const RmQRVersion version = RmQRVersion.R7x99;

        await Assert.That(() => RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version })).Throws<ArgumentException>();

        var optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version, Segmentation = RmQRSegmentation.Optimal });
        await Assert.That(optimal.Version).IsEqualTo(version);
        await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    /// <summary>
    /// The requested-version path commits to planning without pricing it first and lets
    /// the plan build decide, so this pins the whole outcome space of that path over
    /// every version: either it encodes and round-trips, or it throws exactly what the
    /// single-mode path throws. It also pins the direction — Optimal must never throw
    /// where Single succeeds.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task Optimal_RequestedVersion_EitherEncodesOrThrowsLikeSingle(string content)
    {
        foreach (var version in Enum.GetValues<RmQRVersion>())
        {
            string? singleError = null;
            Type? singleType = null;
            try
            {
                RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });
            }
            catch (ArgumentException ex)
            {
                singleError = ex.Message;
                singleType = ex.GetType();
            }

            RmQRCodeData? optimal = null;
            string? optimalError = null;
            Type? optimalType = null;
            try
            {
                optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version, Segmentation = RmQRSegmentation.Optimal });
            }
            catch (ArgumentException ex)
            {
                optimalError = ex.Message;
                optimalType = ex.GetType();
            }

            if (optimal is not null)
            {
                await Assert.That(optimal.Version).IsEqualTo(version);
                await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
                await Assert.That(decoded).IsEqualTo(content);
                continue;
            }

            // Optimal failed, so Single must have failed too, with the same message.
            await Assert.That(singleError).IsNotNull();
            await Assert.That(optimalError).IsEqualTo(singleError);
            await Assert.That(optimalType).IsEqualTo(singleType);
        }
    }

    [Test]
    public async Task Optimal_RequestedVersion_SingleModeFits_ProducesTheIdenticalMatrix()
    {
        const string content = "https://example.com/p/1234567890123456";
        const RmQRVersion version = RmQRVersion.R17x139;

        var single = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });
        var optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version, Segmentation = RmQRSegmentation.Optimal });

        await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData());
    }

    [Test]
    public async Task Optimal_RequestedVersion_TooLongEvenSegmented_ThrowsLikeSingle()
    {
        var content = new string('a', 60) + "1234567890";
        var single = Assert.Throws<ArgumentException>(() => RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 }));
        var optimal = Assert.Throws<ArgumentException>(() => RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43, Segmentation = RmQRSegmentation.Optimal }));

        await Assert.That(optimal!.Message).IsEqualTo(single!.Message);
    }

    [Test]
    public async Task Optimal_TooLongForEveryVersion_ThrowsLikeSingle()
    {
        var content = new string('a', 400);
        var single = Assert.Throws<ArgumentException>(() => RmQRCodeGenerator.Create(content, RmQREccLevel.M));
        var optimal = Assert.Throws<ArgumentException>(() => RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal }));

        await Assert.That(optimal!.Message).IsEqualTo(single!.Message);
    }

    /// <summary>
    /// The single-mode fit is only a ceiling when there is one. Content that overflows
    /// every version in one mode can still fit once the modes are mixed, and rejecting
    /// it would fail exactly where the option is worth the most.
    /// </summary>
    [Test]
    public async Task Optimal_TooLongForEverySingleMode_StillEncodesWhenMixedFits()
    {
        // 200 characters: 200 bytes as one Byte run, 50 over the 150 R17x139-M holds,
        // but 811 + 346 = 1157 bits of the 1216 available once the digits split off.
        var content = new string('a', 100) + new string('7', 100);

        await Assert.That(() => RmQRCodeGenerator.Create(content, RmQREccLevel.M)).Throws<ArgumentException>();

        var optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });
        await Assert.That(optimal.Version).IsEqualTo(RmQRVersion.R17x139);
        await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    /// <summary>
    /// The constrained candidate set has its own capacity ceiling: R7x139-M is 352
    /// bits, so the fixture must be a shape that overflows 42 Byte-mode characters yet
    /// costs under 352 bits when split (89 + 210 here), not merely a long one.
    /// </summary>
    [Test]
    [Arguments(10, 60)]
    [Arguments(10, 40)]
    [Arguments(5, 50)]
    [Arguments(20, 40)]
    public async Task Optimal_TooLongForEverySingleMode_HonoursTheHeightConstraint(int letters, int digits)
    {
        var content = new string('a', letters) + new string('7', digits);

        await Assert.That(() => RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Height = RmQRHeight.H7 })).Throws<ArgumentException>();

        var optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Height = RmQRHeight.H7, Segmentation = RmQRSegmentation.Optimal });
        await Assert.That(optimal.Height - 4).IsEqualTo(7);
        await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    /// <summary>
    /// The mixed-only path with no single-mode ceiling, across the remaining
    /// strategies and charsets: those combinations only had coverage where a
    /// single-mode fit existed to bound the scan.
    /// </summary>
    [Test]
    public async Task Optimal_TooLongForEverySingleMode_AcrossStrategiesAndCharsets()
    {
        var ascii = new string('a', 100) + new string('7', 100);
        var latin1 = new string('é', 60) + new string('7', 100);
        var utf8 = new string('あ', 30) + new string('7', 120);

        foreach (var strategy in Strategies())
        {
            await Assert.That(() => RmQRCodeGenerator.Create(ascii, RmQREccLevel.M, new RmQRCodeGeneratorOptions { FitStrategy = strategy })).Throws<ArgumentException>();
            var optimal = RmQRCodeGenerator.Create(ascii, RmQREccLevel.M, new RmQRCodeGeneratorOptions { FitStrategy = strategy, Segmentation = RmQRSegmentation.Optimal });
            await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
            await Assert.That(decoded).IsEqualTo(ascii);
        }

        foreach (var (content, eci) in new[] { (latin1, EciMode.Iso8859_1), (utf8, EciMode.Utf8) })
        {
            await Assert.That(() => RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = eci })).Throws<ArgumentException>();
            var optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = eci, Segmentation = RmQRSegmentation.Optimal });
            await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
            await Assert.That(decoded).IsEqualTo(content);
        }
    }

    [Test]
    public async Task Optimal_TooLongForEverySingleMode_AgreesWithTryGetRequiredBufferSize()
    {
        var content = new string('a', 100) + new string('7', 100);
        var size = Sizing.Required(content.AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });
        var data = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });

        await Assert.That(size.Version).IsEqualTo(data.Version);
        await Assert.That(size.BufferSize).IsEqualTo(data.Width * data.Height);
    }

    // -----------------------------------------------------------------
    // Height and strategy constraints
    // -----------------------------------------------------------------

    [Test]
    [Arguments(RmQRHeight.H7)]
    [Arguments(RmQRHeight.H9)]
    [Arguments(RmQRHeight.H11)]
    [Arguments(RmQRHeight.H13)]
    [Arguments(RmQRHeight.H15)]
    [Arguments(RmQRHeight.H17)]
    public async Task Optimal_HeightConstraint_IsRespected(RmQRHeight height)
    {
        const string content = "https://example.com/p/1234567890123456";
        var optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Height = height, Segmentation = RmQRSegmentation.Optimal });
        var single = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Height = height });

        await Assert.That(optimal.Height - 4).IsEqualTo((int)height);
        await Assert.That(optimal.Width).IsLessThanOrEqualTo(single.Width);
        await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Optimal_MinimizeWidth_PicksANarrowerSymbolThanSingle()
    {
        const string content = "https://example.com/p/1234567890123456";
        var single = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { FitStrategy = RmQRFitStrategy.MinimizeWidth });
        var optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { FitStrategy = RmQRFitStrategy.MinimizeWidth, Segmentation = RmQRSegmentation.Optimal });

        await Assert.That(optimal.Width).IsLessThanOrEqualTo(single.Width);
        await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Optimal_MinimizeHeight_PicksAFlatterOrEqualSymbolThanSingle()
    {
        const string content = "https://example.com/p/1234567890123456";
        var single = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { FitStrategy = RmQRFitStrategy.MinimizeHeight });
        var optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { FitStrategy = RmQRFitStrategy.MinimizeHeight, Segmentation = RmQRSegmentation.Optimal });

        await Assert.That(optimal.Height).IsLessThanOrEqualTo(single.Height);
        await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    // -----------------------------------------------------------------
    // ECI
    // -----------------------------------------------------------------

    [Test]
    public async Task Optimal_LeadingBom_DecodesLikeSingle()
    {
        // A content-leading U+FEFF is stream-initial under Single too, so both arms
        // drop it on decode; the run-at-offset-0 exemption keeps the split allowed
        // and the two must decode identically (not necessarily to the input). The
        // area assertion is strict: the split genuinely wins here, so losing the
        // exemption (which silently falls back to the identical single-mode
        // symbol) fails this test instead of passing it vacuously.
        var content = "\uFEFF" + new string('1', 30) + "a";
        var single = RmQRCodeGenerator.Create(content, RmQREccLevel.M);
        var optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });

        await Assert.That(Area(optimal)).IsLessThan(Area(single));
        await Assert.That(RmQRCodeDecoder.TryDecode(single, out var singleDecoded)).IsTrue();
        await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var optimalDecoded)).IsTrue();
        await Assert.That(optimalDecoded).IsEqualTo(singleDecoded);
    }

    [Test]
    public async Task Optimal_OverflowWhereEveryCheckedPlanIsMisread_ThrowsLikeSingle()
    {
        // 303 UTF-8 bytes fit no single mode, and the minimal-bit plan puts the
        // trailing U+FEFF at a Byte-run start; the documented outcome is "does not
        // fit" rather than a symbol that decodes with the character dropped.
        var content = new string('1', 300) + "\uFEFF";
        var options = new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal };

        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(content, RmQREccLevel.M, out _, options)).IsFalse();
        await Assert.That(() => RmQRCodeGenerator.Create(content, RmQREccLevel.M, options)).Throws<ArgumentException>();

        // The sibling without the BOM is rescued, proving the refusal is BOM-driven.
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(new string('1', 300) + "a", RmQREccLevel.M, out _, options)).IsTrue();
    }

    [Test]
    public async Task Optimal_NegativeQuietZone_WinsOverInvalidSegmentation_OnEveryEntryPoint()
    {
        // Same precedence and parameter names as the Standard QR and Micro QR
        // generators, so every surface reports the same error first.
        var options = new RmQRCodeGeneratorOptions { Segmentation = (RmQRSegmentation)7, QuietZoneSize = -1 };
        var buffer = new byte[4096];

        var fromCreate = Assert.Throws<ArgumentOutOfRangeException>(() => RmQRCodeGenerator.Create("123", RmQREccLevel.M, options));
        var fromCreateSpan = Assert.Throws<ArgumentOutOfRangeException>(() => RmQRCodeGenerator.Create("123".AsSpan(), RmQREccLevel.M, buffer, options));
        var fromSizing = Assert.Throws<ArgumentOutOfRangeException>(() => RmQRCodeGenerator.TryGetRequiredBufferSize("123", RmQREccLevel.M, out _, options));

        await Assert.That(fromCreate.ParamName).IsEqualTo("quietZoneSize");
        await Assert.That(fromCreateSpan.ParamName).IsEqualTo("quietZoneSize");
        await Assert.That(fromSizing.ParamName).IsEqualTo("quietZoneSize");

        var segmentationOnly = new RmQRCodeGeneratorOptions { Segmentation = (RmQRSegmentation)7 };
        var fromSegmentation = Assert.Throws<ArgumentOutOfRangeException>(() => RmQRCodeGenerator.Create("123", RmQREccLevel.M, segmentationOnly));
        await Assert.That(fromSegmentation.ParamName).IsEqualTo("segmentation");
    }

    [Test]
    public async Task Optimal_MidContentBom_EmitsTheSingleModeStream()
    {
        // The byte-segment decoder consumes a leading EF BB BF of every non-Latin-1
        // segment as a BOM, even behind an explicit UTF-8 ECI; a split that relocates
        // a mid-content U+FEFF to a run start would silently drop it, so the planner
        // must fall back to the single-mode stream, where it survives.
        var content = new string('1', 30) + "\uFEFF" + "a";
        var single = RmQRCodeGenerator.Create(content, RmQREccLevel.M);
        var optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });

        await Assert.That(optimal.Version).IsEqualTo(single.Version);
        await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData());

        await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    [Arguments("日本語1234567890")]
    [Arguments("😀😁1234567890")]
    [Arguments("é😀A1")]
    public async Task Optimal_Utf8_RoundTrips(string content)
    {
        var optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8, Segmentation = RmQRSegmentation.Optimal });
        var single = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8 });

        await Assert.That(Area(optimal)).IsLessThanOrEqualTo(Area(single));
        await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    [Arguments("éèê1234567890")]
    [Arguments("Café 12345678901234")]
    public async Task Optimal_Iso88591_RoundTrips(string content)
    {
        var optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1, Segmentation = RmQRSegmentation.Optimal });
        var single = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1 });

        await Assert.That(Area(optimal)).IsLessThanOrEqualTo(Area(single));
        await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Optimal_ExplicitEciOnAsciiContent_StillRoundTrips()
    {
        // The ECI prefix is 11 bits the plan must pay for even though no Byte run needs it.
        const string content = "1234567890ABCDEFGH1234567890";
        var optimal = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8, Segmentation = RmQRSegmentation.Optimal });
        await Assert.That(RmQRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Optimal_Iso88591WithUnrepresentableContent_ThrowsLikeSingle()
    {
        const string content = "日本語";
        var single = Assert.Throws<ArgumentException>(() => RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1 }));
        var optimal = Assert.Throws<ArgumentException>(() => RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1, Segmentation = RmQRSegmentation.Optimal }));

        await Assert.That(optimal!.Message).IsEqualTo(single!.Message);
    }

    // -----------------------------------------------------------------
    // Overload consistency
    // -----------------------------------------------------------------

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task Optimal_SpanOverload_MatchesTheDataOverload(string content)
    {
        foreach (var quietZone in new[] { 0, 2, 5 })
        {
            var size = Sizing.Required(content.AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = quietZone, Segmentation = RmQRSegmentation.Optimal });
            var buffer = new byte[size.BufferSize];
            var written = RmQRCodeGenerator.Create(content.AsSpan(), RmQREccLevel.M, buffer, new RmQRCodeGeneratorOptions { QuietZoneSize = quietZone, Segmentation = RmQRSegmentation.Optimal });

            await Assert.That(written).IsEqualTo(size.BufferSize);

            var data = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = quietZone, Segmentation = RmQRSegmentation.Optimal });
            await Assert.That(size.Version).IsEqualTo(data.Version);
            await Assert.That(size.Width).IsEqualTo(data.Width);
            await Assert.That(size.Height).IsEqualTo(data.Height);

            for (var row = 0; row < data.Height; row++)
            {
                for (var col = 0; col < data.Width; col++)
                    await Assert.That(buffer[row * data.Width + col] != 0).IsEqualTo(data[row, col]);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task Optimal_EciSpanOverload_MatchesTheEciDataOverload(string content)
    {
        var size = Sizing.Required(content.AsSpan(), RmQREccLevel.H, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8, Segmentation = RmQRSegmentation.Optimal });
        var buffer = new byte[size.BufferSize];
        RmQRCodeGenerator.Create(content.AsSpan(), RmQREccLevel.H, buffer, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8, Segmentation = RmQRSegmentation.Optimal });

        var data = RmQRCodeGenerator.Create(content, RmQREccLevel.H, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8, Segmentation = RmQRSegmentation.Optimal });
        await Assert.That(size.Version).IsEqualTo(data.Version);

        for (var row = 0; row < data.Height; row++)
        {
            for (var col = 0; col < data.Width; col++)
                await Assert.That(buffer[row * data.Width + col] != 0).IsEqualTo(data[row, col]);
        }
    }

    // -----------------------------------------------------------------
    // Argument validation
    // -----------------------------------------------------------------

    /// <summary>
    /// All eight entry points, because the span and ECI overloads reach the validation
    /// through their own cold method rather than sharing one guard.
    /// </summary>
    [Test]
    public async Task InvalidSegmentation_Throws_OnEveryEntryPoint()
    {
        const RmQRSegmentation invalid = (RmQRSegmentation)7;
        var buffer = new byte[4096];

        await Assert.That(() => RmQRCodeGenerator.Create("123", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = invalid })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("123".AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = invalid })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("123".AsSpan(), RmQREccLevel.M, buffer, new RmQRCodeGeneratorOptions { Segmentation = invalid })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSize("123".AsSpan(), RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { Segmentation = invalid })).Throws<ArgumentOutOfRangeException>();

        await Assert.That(() => RmQRCodeGenerator.Create("123", RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8, Segmentation = invalid })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("123".AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8, Segmentation = invalid })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("123".AsSpan(), RmQREccLevel.M, buffer, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8, Segmentation = invalid })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSize("123".AsSpan(), RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8, Segmentation = invalid })).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>The ECI validation the ECI entry points share must fire under Optimal too.</summary>
    [Test]
    public async Task Optimal_InvalidEci_ThrowsOnEveryEciEntryPoint()
    {
        var buffer = new byte[4096];
        const EciMode unsupported = (EciMode)4;

        await Assert.That(() => RmQRCodeGenerator.Create("123", RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = unsupported, Segmentation = RmQRSegmentation.Optimal })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("123".AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = unsupported, Segmentation = RmQRSegmentation.Optimal })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("123".AsSpan(), RmQREccLevel.M, buffer, new RmQRCodeGeneratorOptions { EciMode = unsupported, Segmentation = RmQRSegmentation.Optimal })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSize("123".AsSpan(), RmQREccLevel.M, out _, new RmQRCodeGeneratorOptions { EciMode = unsupported, Segmentation = RmQRSegmentation.Optimal })).Throws<ArgumentOutOfRangeException>();
    }

#if !DEBUG
    /// <summary>
    /// The span destination is the library's zero-allocation path; the mixed-mode
    /// planner must keep it that way (its parent table rents from the pool rather
    /// than allocating). Debug builds are excluded per repo notes.
    /// </summary>
    /// <remarks>
    /// Runs alone. Pooled branches take their buffers from <see cref="System.Buffers.ArrayPool{T}"/>,
    /// whose per-core stacks are shared with every other thread: a test running concurrently can
    /// empty the bucket between this thread's return and its next rent, and the rent that would
    /// have hit the cache allocates a fresh array instead. That shows up here as a multiple of one
    /// array (measured: 1,048 bytes and up, in a full parallel run roughly one time in three) and it
    /// is the pool being a shared resource, not the encoder allocating. Exclusivity is the fix rather
    /// than a tolerance, which would blunt the guard this test exists to be.
    /// </remarks>
    [Test]
    [NotInParallel]
    public async Task Optimal_SpanDestination_IsAllocationFree()
    {
        var url = "https://example.com/p/1234567890123456";
        var longMixed = new string('a', 100) + new string('7', 100);   // over 73 chars, so the planner rents
        var utf8 = "rMQR 矩形コード 1234567890";
        var buffer = new byte[4096];

        for (var i = 0; i < 3; i++)
        {
            RmQRCodeGenerator.Create(url.AsSpan(), RmQREccLevel.M, buffer, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });
            RmQRCodeGenerator.Create(longMixed.AsSpan(), RmQREccLevel.M, buffer, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });
            RmQRCodeGenerator.Create(utf8.AsSpan(), RmQREccLevel.M, buffer, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8, Segmentation = RmQRSegmentation.Optimal });
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 16; i++)
        {
            RmQRCodeGenerator.Create(url.AsSpan(), RmQREccLevel.M, buffer, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });
            RmQRCodeGenerator.Create(longMixed.AsSpan(), RmQREccLevel.M, buffer, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });
            RmQRCodeGenerator.Create(utf8.AsSpan(), RmQREccLevel.M, buffer, new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8, Segmentation = RmQRSegmentation.Optimal });
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0);
    }
#endif

    [Test]
    public async Task Optimal_InvalidArguments_ThrowLikeSingle()
    {
        await Assert.That(() => RmQRCodeGenerator.Create("123", (RmQREccLevel)9, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("123", RmQREccLevel.M, new RmQRCodeGeneratorOptions { FitStrategy = (RmQRFitStrategy)9, Segmentation = RmQRSegmentation.Optimal })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("123", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Height = (RmQRHeight)8, Segmentation = RmQRSegmentation.Optimal })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("123", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = (RmQRVersion)99, Segmentation = RmQRSegmentation.Optimal })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create("123", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43, Height = RmQRHeight.H11, Segmentation = RmQRSegmentation.Optimal })).Throws<ArgumentException>();
        await Assert.That(() => RmQRCodeGenerator.Create("123", RmQREccLevel.M, new RmQRCodeGeneratorOptions { QuietZoneSize = -1, Segmentation = RmQRSegmentation.Optimal })).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Optimal_DestinationTooSmall_Throws()
    {
        var buffer = new byte[4];
        await Assert.That(() => RmQRCodeGenerator.Create("https://example.com/p/1234567890123456".AsSpan(), RmQREccLevel.M, buffer, new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal })).Throws<ArgumentException>();
    }

    // -----------------------------------------------------------------
    // Image builder
    // -----------------------------------------------------------------

    [Test]
    public async Task ImageBuilder_WithSegmentation_UsesTheSmallerSymbol()
    {
        const string content = "https://example.com/p/1234567890123456";
        var optimal = new RmQRCodeImageBuilder(content)
            .WithSegmentation(RmQRSegmentation.Optimal)
            .ToByteArray();
        var single = new RmQRCodeImageBuilder(content).ToByteArray();

        await Assert.That(optimal).IsNotEquivalentTo(single);

        using var bitmap = SKBitmap.Decode(optimal);
        await Assert.That(RmQRCodeDecoder.TryDecode(bitmap, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task ImageBuilder_WithSegmentation_RejectsInvalidValueAndPrebuiltData()
    {
        await Assert.That(() => new RmQRCodeImageBuilder("123").WithSegmentation((RmQRSegmentation)7)).Throws<ArgumentOutOfRangeException>();

        var data = RmQRCodeGenerator.Create("123", RmQREccLevel.M);
        await Assert.That(() => new RmQRCodeImageBuilder(data).WithSegmentation(RmQRSegmentation.Optimal)).Throws<InvalidOperationException>();
    }
}
