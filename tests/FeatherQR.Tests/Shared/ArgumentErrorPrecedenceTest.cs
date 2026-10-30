namespace FeatherQR.Tests;

/// <summary>
/// When an argument is invalid <em>and</em> the content does not fit, the argument error is
/// the one reported, and every entry point reports the same one.
/// </summary>
/// <remarks>
/// specs/rmqr-encoder.md states that argument errors are raised "with the same type, message
/// and precedence" across the surface. A fit that runs before the validation reports a
/// negative quiet zone as "content does not fit". The agreement is between <c>Create</c>, the
/// destination overload and <c>TryGetRequiredBufferSize</c>.
/// </remarks>
public class ArgumentErrorPrecedenceTest
{
    private const string TooLongForVersion1 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ByteContent = "hello";   // needs M3/M4, so M1 rules it out on mode

    [Test]
    public async Task StandardQr_NegativeQuietZoneWins_OverAContentThatDoesNotFit()
    {
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1), QuietZoneSize = -1 };

        await AssertQuietZone(() => QRCodeGenerator.Create(TooLongForVersion1, QREccLevel.M, options));
        await AssertQuietZone(() => QRCodeGenerator.Create(TooLongForVersion1.AsSpan(), QREccLevel.M, options));
        await AssertQuietZone(() => QRCodeGenerator.Create(TooLongForVersion1, QREccLevel.M, new byte[10_000], options));
        await AssertQuietZone(() => QRCodeGenerator.Create(TooLongForVersion1.AsSpan(), QREccLevel.M, new byte[10_000], options));
        await AssertQuietZone(() => QRCodeGenerator.TryGetRequiredBufferSize(TooLongForVersion1.AsSpan(), QREccLevel.M, out _, options));
    }

    [Test]
    public async Task MicroQr_NegativeQuietZoneWins_OverAVersionThatCannotCarryTheMode()
    {
        // M1 offers neither Byte mode nor ECC L, so both a "does not fit" and an ECC
        // contradiction are available, but the quiet zone is still the first thing reported.
        var options = new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.Exactly(MicroQRVersion.M1), QuietZoneSize = -1 };

        await AssertQuietZone(() => MicroQRCodeGenerator.Create(ByteContent, MicroQREccLevel.L, options));
        await AssertQuietZone(() => MicroQRCodeGenerator.Create(ByteContent.AsSpan(), MicroQREccLevel.L, options));
        await AssertQuietZone(() => MicroQRCodeGenerator.Create(ByteContent.AsSpan(), MicroQREccLevel.L, new byte[10_000], options));
        await AssertQuietZone(() => MicroQRCodeGenerator.TryGetRequiredBufferSize(ByteContent.AsSpan(), MicroQREccLevel.L, out _, options));
    }

    [Test]
    public async Task RmQr_NegativeQuietZoneWins_OverAContentThatDoesNotFit()
    {
        var options = new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43, QuietZoneSize = -1 };
        var tooLong = new string('A', 500);

        await AssertQuietZone(() => RmQRCodeGenerator.Create(tooLong, RmQREccLevel.M, options));
        await AssertQuietZone(() => RmQRCodeGenerator.Create(tooLong.AsSpan(), RmQREccLevel.M, options));
        await AssertQuietZone(() => RmQRCodeGenerator.Create(tooLong.AsSpan(), RmQREccLevel.M, new byte[10_000], options));
        await AssertQuietZone(() => RmQRCodeGenerator.TryGetRequiredBufferSize(tooLong.AsSpan(), RmQREccLevel.M, out _, options));
    }

    [Test]
    public async Task EveryEntryPoint_ReportsTheSameArgumentError()
    {
        // The contract is not merely "an argument error" but the same one, so a caller
        // moving between the entry points debugs the same problem.
        var options = new QRCodeGeneratorOptions { Version = 1, QuietZoneSize = -1 };

        var viaCreate = Assert.Throws<ArgumentOutOfRangeException>(
            () => QRCodeGenerator.Create(TooLongForVersion1, QREccLevel.M, options));
        var viaDestination = Assert.Throws<ArgumentOutOfRangeException>(
            () => QRCodeGenerator.Create(TooLongForVersion1, QREccLevel.M, new byte[10_000], options));
        var viaSizing = Assert.Throws<ArgumentOutOfRangeException>(
            () => QRCodeGenerator.TryGetRequiredBufferSize(TooLongForVersion1.AsSpan(), QREccLevel.M, out _, options));

        await Assert.That(viaDestination!.ParamName).IsEqualTo(viaCreate!.ParamName);
        await Assert.That(viaDestination.Message).IsEqualTo(viaCreate.Message);
        await Assert.That(viaSizing!.ParamName).IsEqualTo(viaCreate.ParamName);
        await Assert.That(viaSizing.Message).IsEqualTo(viaCreate.Message);
    }

    [Test]
    public async Task UndefinedEccLevelWins_OverAContentThatDoesNotFit()
    {
        var narrowed = new QRCodeGeneratorOptions { Version = 1 };
        var tooLongForSixteen = new string('A', 1_000);

        var viaCreate = await AssertArgument("eccLevel", () => QRCodeGenerator.Create(TooLongForVersion1, (QREccLevel)9, narrowed));
        var viaDestination = await AssertArgument("eccLevel", () => QRCodeGenerator.Create(TooLongForVersion1.AsSpan(), (QREccLevel)9, new byte[10_000], narrowed));
        var viaSizing = await AssertArgument("eccLevel", () => QRCodeGenerator.TryGetRequiredBufferSize(TooLongForVersion1.AsSpan(), (QREccLevel)9, out _, narrowed));
        var viaSet = await AssertArgument("eccLevel", () => QRCodeGenerator.CreateStructuredAppend(tooLongForSixteen, (QREccLevel)9, narrowed));
        await AssertArgument("eccLevel", () => MicroQRCodeGenerator.Create(ByteContent, (MicroQREccLevel)9, new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.Exactly(MicroQRVersion.M1) }));
        await AssertArgument("eccLevel", () => RmQRCodeGenerator.Create(new string('A', 500), (RmQREccLevel)9, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 }));

        await Assert.That(viaDestination).IsEqualTo(viaCreate);
        await Assert.That(viaSizing).IsEqualTo(viaCreate);
        await Assert.That(viaSet).IsEqualTo(viaCreate);
    }

    [Test]
    public async Task QuietZoneAndSegmentationWin_OverAnUndefinedEccLevel()
    {
        // The order all three report in: quiet zone, segmentation, then level
        await AssertQuietZone(() => QRCodeGenerator.Create("1", (QREccLevel)9, new QRCodeGeneratorOptions { QuietZoneSize = -1 }));
        await AssertQuietZone(() => QRCodeGenerator.TryGetRequiredBufferSize("1", (QREccLevel)9, out _, new QRCodeGeneratorOptions { QuietZoneSize = 10_001 }));
        await AssertQuietZone(() => QRCodeGenerator.CreateStructuredAppend("1", (QREccLevel)9, new QRCodeGeneratorOptions { QuietZoneSize = 10_001 }));
        await AssertQuietZone(() => MicroQRCodeGenerator.Create("1", (MicroQREccLevel)9, new MicroQRCodeGeneratorOptions { QuietZoneSize = -1 }));
        await AssertQuietZone(() => RmQRCodeGenerator.Create("1", (RmQREccLevel)9, new RmQRCodeGeneratorOptions { QuietZoneSize = -1 }));

        await AssertArgument("segmentation", () => QRCodeGenerator.Create("1", (QREccLevel)9, new QRCodeGeneratorOptions { Segmentation = (QRSegmentation)7 }));
        await AssertArgument("segmentation", () => QRCodeGenerator.TryGetRequiredBufferSize("1", (QREccLevel)9, out _, new QRCodeGeneratorOptions { Segmentation = (QRSegmentation)7 }));
        await AssertArgument("segmentation", () => QRCodeGenerator.CreateStructuredAppend("1", (QREccLevel)9, new QRCodeGeneratorOptions { Segmentation = (QRSegmentation)7 }));
        await AssertArgument("segmentation", () => MicroQRCodeGenerator.Create("1", (MicroQREccLevel)9, new MicroQRCodeGeneratorOptions { Segmentation = (MicroQRSegmentation)7 }));
        await AssertArgument("segmentation", () => RmQRCodeGenerator.Create("1", (RmQREccLevel)9, new RmQRCodeGeneratorOptions { Segmentation = (RmQRSegmentation)7 }));
    }

    [Test]
    public async Task UndefinedEciWins_OverAnUndefinedEccLevel()
    {
        // rMQR's order, which Standard QR shares: quiet zone, segmentation, ECI, then level
        var eci = new QRCodeGeneratorOptions { EciMode = (EciMode)77 };

        foreach (var (route, call) in PlannedRoutes("1", (QREccLevel)9, eci).Concat(SingleRoutes("1", (QREccLevel)9, eci)))
            await AssertRefusal(route, typeof(ArgumentOutOfRangeException), "eciMode", call);
        await AssertArgument("eciMode", () => RmQRCodeGenerator.Create("1", (RmQREccLevel)9, new RmQRCodeGeneratorOptions { EciMode = (EciMode)77 }));

        foreach (var (route, call) in PlannedRoutes("1", QREccLevel.M, eci with { QuietZoneSize = -1 }).Concat(SingleRoutes("1", QREccLevel.M, eci with { QuietZoneSize = -1 })))
            await AssertRefusal(route, typeof(ArgumentOutOfRangeException), "quietZoneSize", call);
        foreach (var (route, call) in PlannedRoutes("1", QREccLevel.M, eci with { Segmentation = (QRSegmentation)7 }))
            await AssertRefusal(route, typeof(ArgumentOutOfRangeException), "segmentation", call);
        await AssertArgument("segmentation", () => RmQRCodeGenerator.Create("1", RmQREccLevel.M, new RmQRCodeGeneratorOptions { EciMode = (EciMode)77, Segmentation = (RmQRSegmentation)7 }));
    }

    [Test]
    public async Task UndefinedEciWins_OverAContentThatDoesNotFit()
    {
        await AssertArgument("eciMode", () => QRCodeGenerator.Create(TooLongForVersion1, QREccLevel.M, new QRCodeGeneratorOptions { Version = 1, EciMode = (EciMode)77 }));
        await AssertArgument("eciMode", () => QRCodeGenerator.TryGetRequiredBufferSize(TooLongForVersion1.AsSpan(), QREccLevel.M, out _, new QRCodeGeneratorOptions { Version = 1, EciMode = (EciMode)77 }));
        await AssertArgument("eciMode", () => RmQRCodeGenerator.Create(new string('A', 500), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43, EciMode = (EciMode)77 }));
    }

    [Test]
    public async Task ForcedLatin1OverOtherText_WinsOverAnUndefinedEccLevel()
    {
        // The refusal is the ECI step's, so it keeps the ECI's place: after the quiet zone and the segmentation, before the level.
        var text = "a" + (char)0x3042;
        var iso = new QRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1 };

        foreach (var (route, call) in PlannedRoutes(text, (QREccLevel)9, iso).Concat(SingleRoutes(text, (QREccLevel)9, iso)))
            await AssertRefusal(route, typeof(ArgumentException), "eciMode", call);
        await AssertRefusal("rMQR Create", typeof(ArgumentException), "eciMode", () => RmQRCodeGenerator.Create(text, (RmQREccLevel)9, new RmQRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1 }));

        foreach (var (route, call) in PlannedRoutes(text, QREccLevel.M, iso with { QuietZoneSize = -1 }).Concat(SingleRoutes(text, QREccLevel.M, iso with { QuietZoneSize = -1 })))
            await AssertRefusal(route, typeof(ArgumentOutOfRangeException), "quietZoneSize", call);
        foreach (var (route, call) in PlannedRoutes(text, QREccLevel.M, iso with { Segmentation = (QRSegmentation)7 }))
            await AssertRefusal(route, typeof(ArgumentOutOfRangeException), "segmentation", call);
    }

    // Each of these entry points validates in its own code, so each needs the order. They keep the options' segmentation, so an
    // undefined one reaches their checks.
    private static IEnumerable<(string Route, Action Call)> PlannedRoutes(string text, QREccLevel level, QRCodeGeneratorOptions options) =>
    [
        ("Create", () => QRCodeGenerator.Create(text, level, options)),
        ("Create into a destination", () => QRCodeGenerator.Create(text.AsSpan(), level, new byte[10_000], options)),
        ("TryGetRequiredBufferSize", () => QRCodeGenerator.TryGetRequiredBufferSize(text, level, out _, options)),
        ("CreateStructuredAppend", () => QRCodeGenerator.CreateStructuredAppend(text, level, options)),
    ];

    // Only Single takes these routes, and it is always defined, so they check no segmentation. With any version and no boost, each
    // entry point validates in its own code. A narrowed range or a boost sends both Create overloads through one shared step instead.
    private static IEnumerable<(string Route, Action Call)> SingleRoutes(string text, QREccLevel level, QRCodeGeneratorOptions options)
    {
        var single = options with { Segmentation = QRSegmentation.Single };
        return
        [
            ("Create, Single", () => QRCodeGenerator.Create(text, level, single)),
            ("TryGetRequiredBufferSize, Single", () => QRCodeGenerator.TryGetRequiredBufferSize(text, level, out _, single)),
            ("Create into a destination, Single", () => QRCodeGenerator.Create(text.AsSpan(), level, new byte[10_000], single)),
            ("Create, Single, a narrowed range", () => QRCodeGenerator.Create(text, level, single with { Version = QRVersionRange.AtLeast(2) })),
            ("Create, Single, a boost", () => QRCodeGenerator.Create(text, level, single with { BoostEccLevel = true })),
            ("Create into a destination, Single, a narrowed range", () => QRCodeGenerator.Create(text.AsSpan(), level, new byte[10_000], single with { Version = QRVersionRange.AtLeast(2) })),
        ];
    }

    private static async Task AssertQuietZone(Action call) => await AssertArgument("quietZoneSize", call);

    // The type is compared exactly, since ArgumentOutOfRangeException derives from ArgumentException, the type of the charset refusal.
    private static async Task AssertRefusal(string route, Type type, string paramName, Action call)
    {
        Exception? error = null;
        try
        {
            call();
        }
        catch (Exception e)
        {
            error = e;
        }
        await Assert.That(error?.GetType()).IsEqualTo(type).Because($"through {route}");
        await Assert.That((error as ArgumentException)?.ParamName).IsEqualTo(paramName).Because($"through {route}");
    }

    /// <summary>The message of the <see cref="ArgumentOutOfRangeException"/> <paramref name="call"/> throws, once it names <paramref name="paramName"/>.</summary>
    private static async Task<string> AssertArgument(string paramName, Action call)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(call);
        await Assert.That(error!.ParamName).IsEqualTo(paramName);
        return error.Message;
    }
}
