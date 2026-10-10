using System.Reflection;

namespace FeatherQR.Tests;

/// <summary>
/// The three generator options can be written three ways, and the three agree: an object
/// initializer, an assignment after construction, and <c>with</c>.
/// </summary>
/// <remarks>
/// <para>
/// The options have <c>set</c> accessors so that the object initializer compiles at C# 7.3,
/// where an <c>init</c> accessor needs C# 9. The constructor that served the older
/// compilers had to list every option, so adding one changed its signature under every
/// compiled caller (specs/qrcode-symbologies.md).
/// </para>
/// <para>
/// A setter adds one spelling and no new state: <c>o.P = v</c> does what
/// <c>o = o with { P = v }</c> already did. So each option is written by all three routes
/// and the results compared, with its default named as well as a value that is not the
/// default. The three routes go through the same accessor, so the comparison cannot show
/// what a getter returns. It shows a setter that stores nothing, a default that does not
/// collapse onto the unset form, and a setter that writes another member.
/// </para>
/// <para>
/// The two assignments of a case are also checked through the property its name gives:
/// after the first the option has to read something other than its default, and the second
/// has to bring the value back to <c>default</c>. The name alone would pass a row whose
/// assignments are another option's. The two cells that write the value are compared with
/// the first assignment, so they are tied to the option as well. The two cells that write
/// the default are compared with <c>default</c> only, so one that named another option's
/// default would pass.
/// </para>
/// <para>
/// What the getters return is read back by the round-trip tests of
/// <see cref="GeneratorOptionsTest"/> and <see cref="RmQRCodeGeneratorOptionsTest"/> for a
/// quiet zone a generator accepts, and below for one it refuses and for the mask pattern
/// at its bounds.
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
            .ToArray();
        var named = cases.Select(c => c.Name).ToArray();
        // Three checks and not one comparison, so that a failure names the option.
        await Assert.That(options.Except(named)).IsEmpty().Because("every option has a case");
        await Assert.That(named.Except(options)).IsEmpty().Because("every case is named for an option");
        await Assert.That(named.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key)).IsEmpty().Because("an option has one case");

        // What the option a case is named for reads on a value.
        static object? Read(OptionCase<T> c, T on) => typeof(T).GetProperty(c.Name)!.GetValue(on);

        foreach (var c in cases)
        {
            var assigned = default(T);
            c.AssignValue(ref assigned);
            // Read through the name, so that a row copied from another option does not pass as this one's.
            await Assert.That(Equals(Read(c, assigned), Read(c, default(T)))).IsFalse()
                .Because($"{c.Name} still reads its default ({Read(c, default(T)) ?? "null"}) after the case assigned it: the setter did not store the value, or the case assigns the default or another option");
            await Assert.That(assigned).IsEqualTo(c.ValueByInitializer).Because($"{c.Name}: assignment and initializer");
            await Assert.That(assigned).IsEqualTo(c.ValueByWith).Because($"{c.Name}: assignment and with");
            // Not a contract: unequal values may collide. The generated hash has no random seed for
            // these member types, so the assertion is what catches a hash that leaves the option out.
            await Assert.That(assigned.GetHashCode()).IsNotEqualTo(default(T).GetHashCode()).Because($"{c.Name}: hash of a value that is not the default");

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

        // Option by option first, against the value the case assigns on its own. `everyOptionSet`
        // is written through the same setters, so a setter that writes another member is in it too.
        foreach (var c in cases)
        {
            var own = Read(c, c.ValueByInitializer);
            await Assert.That(Equals(Read(c, forward), own)).IsTrue()
                .Because($"{c.Name} has to read {own ?? "null"}, the value its case assigns, after every option was assigned in the table's order, and reads {Read(c, forward) ?? "null"}");
            await Assert.That(Equals(Read(c, backward), own)).IsTrue()
                .Because($"{c.Name} has to read {own ?? "null"}, the value its case assigns, after every option was assigned in reverse, and reads {Read(c, backward) ?? "null"}");
        }

        await Assert.That(forward).IsEqualTo(everyOptionSet).Because("every option assigned in the table's order, against the same options in one initializer");
        await Assert.That(backward).IsEqualTo(everyOptionSet).Because("every option assigned in reverse, against the same options in one initializer");

        // Each option to its default and back, on a value that has every option set: a setter
        // that cleared another member only when it was given its own default would pass
        // everything above, because there the default is only ever assigned on its own.
        foreach (var c in cases)
        {
            var roundTrip = everyOptionSet;
            c.AssignDefault(ref roundTrip);
            await Assert.That(roundTrip).IsNotEqualTo(everyOptionSet).Because($"{c.Name}: its default beside every other option");
            c.AssignValue(ref roundTrip);
            await Assert.That(roundTrip).IsEqualTo(everyOptionSet).Because($"{c.Name}: its default and then its value, beside every other option");
        }
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
        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => { assigned.MaskPattern = maskPattern; });
        // A refused assignment leaves the option as it was.
        await Assert.That(assigned.MaskPattern).IsEqualTo(3);

        // The three routes run one accessor, so what the exception says is read once.
        await Assert.That(refused.ParamName).IsEqualTo(nameof(QRCodeGeneratorOptions.MaskPattern));
        await Assert.That(refused.Message).Contains($"Mask pattern must be 0-7, or null for automatic selection, but was {maskPattern}");
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
        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => { assigned.MaskPattern = maskPattern; });
        // A refused assignment leaves the option as it was.
        await Assert.That(assigned.MaskPattern).IsEqualTo(1);

        // The three routes run one accessor, so what the exception says is read once.
        await Assert.That(refused.ParamName).IsEqualTo(nameof(MicroQRCodeGeneratorOptions.MaskPattern));
        await Assert.That(refused.Message).Contains($"Mask pattern must be 0-3, or null for automatic selection, but was {maskPattern}");
    }

    // ---- QuietZoneSize keeps what it is given -------------------------------------------

    [Test]
    [Arguments(int.MinValue)]
    [Arguments(-65_540)]
    [Arguments(-1)]
    [Arguments(65_540)]
    [Arguments(int.MaxValue)]
    public async Task QuietZoneSize_OutsideWhatAGeneratorAccepts_ReadsBackAsAssigned(int quietZoneSize)
    {
        // The accessor refuses no value and stores an offset from the default in an int. A
        // generator checks the range at the call, so what it reads has to be what was assigned.
        // int.MinValue is here because the offset from the default wraps there, and has to wrap back.
        await Assert.That(new QRCodeGeneratorOptions { QuietZoneSize = quietZoneSize }.QuietZoneSize).IsEqualTo(quietZoneSize);
        await Assert.That(new MicroQRCodeGeneratorOptions { QuietZoneSize = quietZoneSize }.QuietZoneSize).IsEqualTo(quietZoneSize);
        await Assert.That(new RmQRCodeGeneratorOptions { QuietZoneSize = quietZoneSize }.QuietZoneSize).IsEqualTo(quietZoneSize);
    }

    // ---- an assignment reaches one variable --------------------------------------------

    [Test]
    public async Task Assignment_ChangesTheVariableAssignedTo_AndNoCopyOfIt()
    {
        // Each variable holds its own value: an assignment reaches the variable assigned to
        // and no copy of it. One member to a pair of values, so that a setter that writes
        // another member does not fail here: the tests above report that one.
        var original = new QRCodeGeneratorOptions { QuietZoneSize = 1 };
        var copy = original;
        copy.QuietZoneSize = 0;
        var masked = new QRCodeGeneratorOptions { MaskPattern = 3 };
        var unmasked = masked;
        unmasked.MaskPattern = null;

        // Read member by member first, before any other value is built. The comparisons below
        // go through the setters and the generated equality, which do not show state that
        // two values share.
        await Assert.That(original.QuietZoneSize).IsEqualTo(1);
        await Assert.That(copy.QuietZoneSize).IsEqualTo(0);
        await Assert.That(masked.MaskPattern).IsEqualTo(3);
        await Assert.That(unmasked.MaskPattern).IsNull();

        await Assert.That(original).IsEqualTo(new QRCodeGeneratorOptions { QuietZoneSize = 1 });
        await Assert.That(copy).IsEqualTo(new QRCodeGeneratorOptions { QuietZoneSize = 0 });
        await Assert.That(masked).IsEqualTo(new QRCodeGeneratorOptions { MaskPattern = 3 });
        await Assert.That(unmasked).IsEqualTo(default(QRCodeGeneratorOptions));

        // A copy taken from `Default` is the caller's own, so assigning to it leaves `Default`
        // as it was. That `Default` is a get-only property and not a field is the rule
        // TypeShapeTest.GeneratorOptions_ExposeNoFieldAndDefaultIsAValue states. The quiet
        // zone is read as well as compared, for the reason above: 4, 2 and 2 are the defaults.
        var fromDefault = QRCodeGeneratorOptions.Default;
        fromDefault.QuietZoneSize = 0;
        await Assert.That(QRCodeGeneratorOptions.Default.QuietZoneSize).IsEqualTo(4);
        await Assert.That(QRCodeGeneratorOptions.Default).IsEqualTo(default(QRCodeGeneratorOptions));

        var micro = MicroQRCodeGeneratorOptions.Default;
        micro.QuietZoneSize = 0;
        await Assert.That(MicroQRCodeGeneratorOptions.Default.QuietZoneSize).IsEqualTo(2);
        await Assert.That(MicroQRCodeGeneratorOptions.Default).IsEqualTo(default(MicroQRCodeGeneratorOptions));

        var rm = RmQRCodeGeneratorOptions.Default;
        rm.QuietZoneSize = 0;
        await Assert.That(RmQRCodeGeneratorOptions.Default.QuietZoneSize).IsEqualTo(2);
        await Assert.That(RmQRCodeGeneratorOptions.Default).IsEqualTo(default(RmQRCodeGeneratorOptions));
    }
}
