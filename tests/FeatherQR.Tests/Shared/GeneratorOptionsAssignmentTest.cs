using System.Reflection;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The three generator options can be written three ways, and the three agree: an object
/// initializer, an assignment after construction, and <c>with</c>.
/// </summary>
/// <remarks>
/// <para>
/// The options have <c>set</c> accessors so that the object initializer compiles at every
/// language version. An <c>init</c> accessor needs C# 9, and the constructor that served
/// older compilers had to list every option, so adding one changed its signature under
/// every compiled caller (specs/qrcode-symbologies.md).
/// </para>
/// <para>
/// A setter adds one spelling and no new state: <c>o.P = v</c> does what
/// <c>o = o with { P = v }</c> already did. So each option is written by all three routes
/// and the results compared, with its default named as well as a value that is not the
/// default. The quiet zone is stored as an offset from the specified default and the mask
/// pattern is validated, which makes those two the hand-written accessors a route could
/// get wrong. The rest are covered so that the next option is.
/// </para>
/// </remarks>
public class GeneratorOptionsAssignmentTest
{
    private delegate void Assign<T>(ref T options) where T : struct;

    /// <summary>
    /// One option of one struct: a value that is not its default, then its default, each by
    /// assignment, by initializer and by <c>with</c>.
    /// </summary>
    private sealed record OptionCase<T>(
        string Name,
        Assign<T> AssignValue, T ValueByInitializer, T ValueByWith,
        Assign<T> AssignDefault, T DefaultByInitializer, T DefaultByWith) where T : struct;

    private static readonly OptionCase<QRCodeGeneratorOptions>[] StandardQrOptions =
    [
        new(nameof(QRCodeGeneratorOptions.EciMode),
            (ref QRCodeGeneratorOptions o) => o.EciMode = EciMode.Utf8, new() { EciMode = EciMode.Utf8 }, default(QRCodeGeneratorOptions) with { EciMode = EciMode.Utf8 },
            (ref QRCodeGeneratorOptions o) => o.EciMode = EciMode.Default, new() { EciMode = EciMode.Default }, default(QRCodeGeneratorOptions) with { EciMode = EciMode.Default }),
        new(nameof(QRCodeGeneratorOptions.Utf8Bom),
            (ref QRCodeGeneratorOptions o) => o.Utf8Bom = true, new() { Utf8Bom = true }, default(QRCodeGeneratorOptions) with { Utf8Bom = true },
            (ref QRCodeGeneratorOptions o) => o.Utf8Bom = false, new() { Utf8Bom = false }, default(QRCodeGeneratorOptions) with { Utf8Bom = false }),
        new(nameof(QRCodeGeneratorOptions.Version),
            (ref QRCodeGeneratorOptions o) => o.Version = 5, new() { Version = 5 }, default(QRCodeGeneratorOptions) with { Version = 5 },
            (ref QRCodeGeneratorOptions o) => o.Version = QRVersionRange.Any, new() { Version = QRVersionRange.Any }, default(QRCodeGeneratorOptions) with { Version = QRVersionRange.Any }),
        new(nameof(QRCodeGeneratorOptions.QuietZoneSize),
            (ref QRCodeGeneratorOptions o) => o.QuietZoneSize = 0, new() { QuietZoneSize = 0 }, default(QRCodeGeneratorOptions) with { QuietZoneSize = 0 },
            (ref QRCodeGeneratorOptions o) => o.QuietZoneSize = 4, new() { QuietZoneSize = 4 }, default(QRCodeGeneratorOptions) with { QuietZoneSize = 4 }),
        new(nameof(QRCodeGeneratorOptions.MaskPattern),
            (ref QRCodeGeneratorOptions o) => o.MaskPattern = 3, new() { MaskPattern = 3 }, default(QRCodeGeneratorOptions) with { MaskPattern = 3 },
            (ref QRCodeGeneratorOptions o) => o.MaskPattern = null, new() { MaskPattern = null }, default(QRCodeGeneratorOptions) with { MaskPattern = null }),
        new(nameof(QRCodeGeneratorOptions.BoostEccLevel),
            (ref QRCodeGeneratorOptions o) => o.BoostEccLevel = true, new() { BoostEccLevel = true }, default(QRCodeGeneratorOptions) with { BoostEccLevel = true },
            (ref QRCodeGeneratorOptions o) => o.BoostEccLevel = false, new() { BoostEccLevel = false }, default(QRCodeGeneratorOptions) with { BoostEccLevel = false }),
        new(nameof(QRCodeGeneratorOptions.Segmentation),
            (ref QRCodeGeneratorOptions o) => o.Segmentation = QRSegmentation.Optimal, new() { Segmentation = QRSegmentation.Optimal }, default(QRCodeGeneratorOptions) with { Segmentation = QRSegmentation.Optimal },
            (ref QRCodeGeneratorOptions o) => o.Segmentation = QRSegmentation.Single, new() { Segmentation = QRSegmentation.Single }, default(QRCodeGeneratorOptions) with { Segmentation = QRSegmentation.Single }),
        new(nameof(QRCodeGeneratorOptions.AllowKanji),
            (ref QRCodeGeneratorOptions o) => o.AllowKanji = true, new() { AllowKanji = true }, default(QRCodeGeneratorOptions) with { AllowKanji = true },
            (ref QRCodeGeneratorOptions o) => o.AllowKanji = false, new() { AllowKanji = false }, default(QRCodeGeneratorOptions) with { AllowKanji = false }),
    ];

    private static readonly OptionCase<MicroQRCodeGeneratorOptions>[] MicroQrOptions =
    [
        new(nameof(MicroQRCodeGeneratorOptions.Version),
            (ref MicroQRCodeGeneratorOptions o) => o.Version = MicroQRVersion.M3, new() { Version = MicroQRVersion.M3 }, default(MicroQRCodeGeneratorOptions) with { Version = MicroQRVersion.M3 },
            (ref MicroQRCodeGeneratorOptions o) => o.Version = MicroQRVersionRange.Any, new() { Version = MicroQRVersionRange.Any }, default(MicroQRCodeGeneratorOptions) with { Version = MicroQRVersionRange.Any }),
        new(nameof(MicroQRCodeGeneratorOptions.MaskPattern),
            (ref MicroQRCodeGeneratorOptions o) => o.MaskPattern = 2, new() { MaskPattern = 2 }, default(MicroQRCodeGeneratorOptions) with { MaskPattern = 2 },
            (ref MicroQRCodeGeneratorOptions o) => o.MaskPattern = null, new() { MaskPattern = null }, default(MicroQRCodeGeneratorOptions) with { MaskPattern = null }),
        new(nameof(MicroQRCodeGeneratorOptions.QuietZoneSize),
            (ref MicroQRCodeGeneratorOptions o) => o.QuietZoneSize = 0, new() { QuietZoneSize = 0 }, default(MicroQRCodeGeneratorOptions) with { QuietZoneSize = 0 },
            (ref MicroQRCodeGeneratorOptions o) => o.QuietZoneSize = 2, new() { QuietZoneSize = 2 }, default(MicroQRCodeGeneratorOptions) with { QuietZoneSize = 2 }),
        new(nameof(MicroQRCodeGeneratorOptions.Segmentation),
            (ref MicroQRCodeGeneratorOptions o) => o.Segmentation = MicroQRSegmentation.Optimal, new() { Segmentation = MicroQRSegmentation.Optimal }, default(MicroQRCodeGeneratorOptions) with { Segmentation = MicroQRSegmentation.Optimal },
            (ref MicroQRCodeGeneratorOptions o) => o.Segmentation = MicroQRSegmentation.Single, new() { Segmentation = MicroQRSegmentation.Single }, default(MicroQRCodeGeneratorOptions) with { Segmentation = MicroQRSegmentation.Single }),
        new(nameof(MicroQRCodeGeneratorOptions.AllowKanji),
            (ref MicroQRCodeGeneratorOptions o) => o.AllowKanji = true, new() { AllowKanji = true }, default(MicroQRCodeGeneratorOptions) with { AllowKanji = true },
            (ref MicroQRCodeGeneratorOptions o) => o.AllowKanji = false, new() { AllowKanji = false }, default(MicroQRCodeGeneratorOptions) with { AllowKanji = false }),
    ];

    private static readonly OptionCase<RmQRCodeGeneratorOptions>[] RmQrOptions =
    [
        new(nameof(RmQRCodeGeneratorOptions.EciMode),
            (ref RmQRCodeGeneratorOptions o) => o.EciMode = EciMode.Utf8, new() { EciMode = EciMode.Utf8 }, default(RmQRCodeGeneratorOptions) with { EciMode = EciMode.Utf8 },
            (ref RmQRCodeGeneratorOptions o) => o.EciMode = EciMode.Default, new() { EciMode = EciMode.Default }, default(RmQRCodeGeneratorOptions) with { EciMode = EciMode.Default }),
        new(nameof(RmQRCodeGeneratorOptions.Version),
            (ref RmQRCodeGeneratorOptions o) => o.Version = RmQRVersion.R7x43, new() { Version = RmQRVersion.R7x43 }, default(RmQRCodeGeneratorOptions) with { Version = RmQRVersion.R7x43 },
            (ref RmQRCodeGeneratorOptions o) => o.Version = null, new() { Version = null }, default(RmQRCodeGeneratorOptions) with { Version = null }),
        new(nameof(RmQRCodeGeneratorOptions.FitStrategy),
            (ref RmQRCodeGeneratorOptions o) => o.FitStrategy = RmQRFitStrategy.MinimizeWidth, new() { FitStrategy = RmQRFitStrategy.MinimizeWidth }, default(RmQRCodeGeneratorOptions) with { FitStrategy = RmQRFitStrategy.MinimizeWidth },
            (ref RmQRCodeGeneratorOptions o) => o.FitStrategy = RmQRFitStrategy.MinimizeArea, new() { FitStrategy = RmQRFitStrategy.MinimizeArea }, default(RmQRCodeGeneratorOptions) with { FitStrategy = RmQRFitStrategy.MinimizeArea }),
        new(nameof(RmQRCodeGeneratorOptions.Height),
            (ref RmQRCodeGeneratorOptions o) => o.Height = RmQRHeight.H7, new() { Height = RmQRHeight.H7 }, default(RmQRCodeGeneratorOptions) with { Height = RmQRHeight.H7 },
            (ref RmQRCodeGeneratorOptions o) => o.Height = null, new() { Height = null }, default(RmQRCodeGeneratorOptions) with { Height = null }),
        new(nameof(RmQRCodeGeneratorOptions.QuietZoneSize),
            (ref RmQRCodeGeneratorOptions o) => o.QuietZoneSize = 0, new() { QuietZoneSize = 0 }, default(RmQRCodeGeneratorOptions) with { QuietZoneSize = 0 },
            (ref RmQRCodeGeneratorOptions o) => o.QuietZoneSize = 2, new() { QuietZoneSize = 2 }, default(RmQRCodeGeneratorOptions) with { QuietZoneSize = 2 }),
        new(nameof(RmQRCodeGeneratorOptions.Segmentation),
            (ref RmQRCodeGeneratorOptions o) => o.Segmentation = RmQRSegmentation.Optimal, new() { Segmentation = RmQRSegmentation.Optimal }, default(RmQRCodeGeneratorOptions) with { Segmentation = RmQRSegmentation.Optimal },
            (ref RmQRCodeGeneratorOptions o) => o.Segmentation = RmQRSegmentation.Single, new() { Segmentation = RmQRSegmentation.Single }, default(RmQRCodeGeneratorOptions) with { Segmentation = RmQRSegmentation.Single }),
        new(nameof(RmQRCodeGeneratorOptions.AllowKanji),
            (ref RmQRCodeGeneratorOptions o) => o.AllowKanji = true, new() { AllowKanji = true }, default(RmQRCodeGeneratorOptions) with { AllowKanji = true },
            (ref RmQRCodeGeneratorOptions o) => o.AllowKanji = false, new() { AllowKanji = false }, default(RmQRCodeGeneratorOptions) with { AllowKanji = false }),
    ];

    [Test]
    public async Task StandardQr_EveryOption_IsWrittenAlikeByAssignmentInitializerAndWith()
        => await CheckEveryOption(StandardQrOptions, new QRCodeGeneratorOptions
        {
            EciMode = EciMode.Utf8,
            Utf8Bom = true,
            Version = 5,
            QuietZoneSize = 0,
            MaskPattern = 3,
            BoostEccLevel = true,
            Segmentation = QRSegmentation.Optimal,
            AllowKanji = true,
        });

    [Test]
    public async Task MicroQr_EveryOption_IsWrittenAlikeByAssignmentInitializerAndWith()
        => await CheckEveryOption(MicroQrOptions, new MicroQRCodeGeneratorOptions
        {
            Version = MicroQRVersion.M3,
            MaskPattern = 2,
            QuietZoneSize = 0,
            Segmentation = MicroQRSegmentation.Optimal,
            AllowKanji = true,
        });

    [Test]
    public async Task RmQr_EveryOption_IsWrittenAlikeByAssignmentInitializerAndWith()
        => await CheckEveryOption(RmQrOptions, new RmQRCodeGeneratorOptions
        {
            EciMode = EciMode.Utf8,
            Version = RmQRVersion.R7x43,
            FitStrategy = RmQRFitStrategy.MinimizeWidth,
            Height = RmQRHeight.H7,
            QuietZoneSize = 0,
            Segmentation = RmQRSegmentation.Optimal,
            AllowKanji = true,
        });

    private static async Task CheckEveryOption<T>(OptionCase<T>[] cases, T everyOptionSet) where T : struct
    {
        // A case for every option, so an option added later fails here until it has one.
        // An option is a property a caller can set; one that only computes a value needs no case.
        var options = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal);
        await Assert.That(cases.Select(c => c.Name).Order(StringComparer.Ordinal)).IsEquivalentTo(options, CollectionOrdering.Matching);

        foreach (var c in cases)
        {
            var assigned = default(T);
            c.AssignValue(ref assigned);
            await Assert.That(assigned).IsNotEqualTo(default(T))
                .Because($"{c.Name}: the case has to use a value that is not the default, or it shows nothing");
            await Assert.That(assigned).IsEqualTo(c.ValueByInitializer).Because($"{c.Name}: assignment and initializer");
            await Assert.That(assigned).IsEqualTo(c.ValueByWith).Because($"{c.Name}: assignment and with");

            // The default, named. It has to collapse onto the unset form by every route, or
            // the generated equality calls two identical option sets different.
            c.AssignDefault(ref assigned);
            await Assert.That(assigned).IsEqualTo(default(T)).Because($"{c.Name}: the default assigned over a value");
            await Assert.That(assigned.GetHashCode()).IsEqualTo(default(T).GetHashCode()).Because($"{c.Name}: hash of the default assigned");
            await Assert.That(c.DefaultByInitializer).IsEqualTo(default(T)).Because($"{c.Name}: the default by initializer");
            await Assert.That(c.DefaultByWith).IsEqualTo(default(T)).Because($"{c.Name}: the default by with");
        }

        // Every option assigned in turn, in both orders: a setter that wrote another member's
        // state would undo whichever option was assigned before it.
        var forward = default(T);
        foreach (var c in cases)
            c.AssignValue(ref forward);
        var backward = default(T);
        // Enumerable.Reverse by name: on net8.0, C# 14 binds `cases.Reverse()` to the in-place span method.
        foreach (var c in Enumerable.Reverse(cases))
            c.AssignValue(ref backward);

        await Assert.That(forward).IsEqualTo(everyOptionSet);
        await Assert.That(backward).IsEqualTo(everyOptionSet);
    }

    // ---- MaskPattern is validated by every route ---------------------------------------

    [Test]
    [Arguments(0)]
    [Arguments(7)]
    public async Task StandardQrMaskPattern_AtItsBounds_IsAcceptedByEveryRoute(int maskPattern)
    {
        var assigned = new QRCodeGeneratorOptions();
        assigned.MaskPattern = maskPattern;

        await Assert.That(assigned.MaskPattern).IsEqualTo(maskPattern);
        await Assert.That(new QRCodeGeneratorOptions { MaskPattern = maskPattern }).IsEqualTo(assigned);
        await Assert.That(default(QRCodeGeneratorOptions) with { MaskPattern = maskPattern }).IsEqualTo(assigned);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(8)]
    public async Task StandardQrMaskPattern_PastItsBounds_IsRefusedByEveryRoute(int maskPattern)
    {
        await Assert.That(() => new QRCodeGeneratorOptions { MaskPattern = maskPattern }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => default(QRCodeGeneratorOptions) with { MaskPattern = maskPattern }).Throws<ArgumentOutOfRangeException>();

        var assigned = new QRCodeGeneratorOptions { MaskPattern = 3 };
        await Assert.That(() => { assigned.MaskPattern = maskPattern; }).Throws<ArgumentOutOfRangeException>();
        // A refused assignment leaves the option as it was.
        await Assert.That(assigned.MaskPattern).IsEqualTo(3);
    }

    [Test]
    [Arguments(0)]
    [Arguments(3)]
    public async Task MicroQrMaskPattern_AtItsBounds_IsAcceptedByEveryRoute(int maskPattern)
    {
        var assigned = new MicroQRCodeGeneratorOptions();
        assigned.MaskPattern = maskPattern;

        await Assert.That(assigned.MaskPattern).IsEqualTo(maskPattern);
        await Assert.That(new MicroQRCodeGeneratorOptions { MaskPattern = maskPattern }).IsEqualTo(assigned);
        await Assert.That(default(MicroQRCodeGeneratorOptions) with { MaskPattern = maskPattern }).IsEqualTo(assigned);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(4)]
    public async Task MicroQrMaskPattern_PastItsBounds_IsRefusedByEveryRoute(int maskPattern)
    {
        await Assert.That(() => new MicroQRCodeGeneratorOptions { MaskPattern = maskPattern }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => default(MicroQRCodeGeneratorOptions) with { MaskPattern = maskPattern }).Throws<ArgumentOutOfRangeException>();

        var assigned = new MicroQRCodeGeneratorOptions { MaskPattern = 1 };
        await Assert.That(() => { assigned.MaskPattern = maskPattern; }).Throws<ArgumentOutOfRangeException>();
        // A refused assignment leaves the option as it was.
        await Assert.That(assigned.MaskPattern).IsEqualTo(1);
    }

    // ---- an assignment reaches one variable --------------------------------------------

    [Test]
    public async Task Assignment_ChangesTheVariableAssignedTo_AndNoCopyOfIt()
    {
        // Why a setter on these structs is safe: a struct is copied when it is stored or
        // passed, so nothing else holds the value a caller assigns to.
        var original = new QRCodeGeneratorOptions { QuietZoneSize = 1, MaskPattern = 3 };
        var copy = original;
        copy.QuietZoneSize = 0;
        copy.MaskPattern = null;

        await Assert.That(original).IsEqualTo(new QRCodeGeneratorOptions { QuietZoneSize = 1, MaskPattern = 3 });
        await Assert.That(copy).IsEqualTo(new QRCodeGeneratorOptions { QuietZoneSize = 0 });

        // `Default` hands out a value, not a shared instance.
        var fromDefault = QRCodeGeneratorOptions.Default;
        fromDefault.QuietZoneSize = 0;
        await Assert.That(QRCodeGeneratorOptions.Default).IsEqualTo(default(QRCodeGeneratorOptions));

        var micro = MicroQRCodeGeneratorOptions.Default;
        micro.QuietZoneSize = 0;
        await Assert.That(MicroQRCodeGeneratorOptions.Default).IsEqualTo(default(MicroQRCodeGeneratorOptions));

        var rm = RmQRCodeGeneratorOptions.Default;
        rm.QuietZoneSize = 0;
        await Assert.That(RmQRCodeGeneratorOptions.Default).IsEqualTo(default(RmQRCodeGeneratorOptions));
    }
}
