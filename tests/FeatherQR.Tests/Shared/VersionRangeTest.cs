using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="QRVersionRange"/> and <see cref="MicroQRVersionRange"/>, the version
/// constraint that replaced the single requested version in the options structs
/// (specs/qrcode-symbologies.md, Public API direction).
/// </summary>
/// <remarks>
/// <para>
/// The range is one concept with the fixed version as its degenerate case, so the two
/// behaviours that were previously separate parameters have to keep working exactly:
/// <c>Any</c> must reproduce automatic selection and <c>Exactly(n)</c> must reproduce a
/// pinned version, byte for byte, against the released parameter list overloads. Those two
/// sweeps are the point of this file.
/// </para>
/// <para>
/// The rest is the constraint semantics that did not exist before: a range that excludes
/// everything that fits, a range whose only fitting member is at either end, and the split
/// between "does not fit" (<c>false</c>) and a contradictory argument (throws).
/// </para>
/// </remarks>
public class VersionRangeTest
{
    private const string Digits = "0123456789";

    // ---- construction and canonical form -------------------------------------------

    [Test]
    public async Task StandardRange_Default_IsTheWholeRange()
    {
        var any = default(QRVersionRange);

        await Assert.That(any.Min).IsEqualTo(1);
        await Assert.That(any.Max).IsEqualTo(40);
        await Assert.That(QRVersionRange.Any).IsEqualTo(any);
    }

    [Test]
    public async Task StandardRange_FullBoundsCollapseOntoAny()
    {
        // The bounds are stored normalised so the canonical form is unique; otherwise
        // Between(1, 40) and Any would compare unequal while behaving identically.
        await Assert.That(QRVersionRange.Between(1, 40)).IsEqualTo(QRVersionRange.Any);
        await Assert.That(QRVersionRange.AtLeast(1)).IsEqualTo(QRVersionRange.Any);
        await Assert.That(QRVersionRange.AtMost(40)).IsEqualTo(QRVersionRange.Any);
        await Assert.That(QRVersionRange.Between(1, 40).GetHashCode()).IsEqualTo(QRVersionRange.Any.GetHashCode());
    }

    [Test]
    [Arguments(1)]
    [Arguments(7)]
    [Arguments(40)]
    public async Task StandardRange_Exactly_IsASingletonRange(int version)
    {
        var range = QRVersionRange.Exactly(version);

        await Assert.That(range.Min).IsEqualTo(version);
        await Assert.That(range.Max).IsEqualTo(version);
        await Assert.That(range).IsEqualTo(QRVersionRange.Between(version, version));
    }

    [Test]
    public async Task StandardRange_AtLeastAndAtMost_BoundOneSide()
    {
        await Assert.That(QRVersionRange.AtLeast(10).Min).IsEqualTo(10);
        await Assert.That(QRVersionRange.AtLeast(10).Max).IsEqualTo(40);
        await Assert.That(QRVersionRange.AtMost(10).Min).IsEqualTo(1);
        await Assert.That(QRVersionRange.AtMost(10).Max).IsEqualTo(10);
    }

    [Test]
    [Arguments(0)]
    [Arguments(41)]
    [Arguments(-1)]
    public async Task StandardRange_OutOfRangeBound_ThrowsFromTheFactory(int version)
    {
        await Assert.That(() => QRVersionRange.Exactly(version)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRVersionRange.AtLeast(version)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRVersionRange.AtMost(version)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRVersionRange.Between(version, 40)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task StandardRange_InvertedBounds_ThrowFromTheFactory()
    {
        await Assert.That(() => QRVersionRange.Between(10, 9)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => QRVersionRange.Between(40, 1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task MicroRange_Default_IsTheWholeRange()
    {
        var any = default(MicroQRVersionRange);

        await Assert.That(any.Min).IsEqualTo(MicroQRVersion.M1);
        await Assert.That(any.Max).IsEqualTo(MicroQRVersion.M4);
        await Assert.That(MicroQRVersionRange.Any).IsEqualTo(any);
        await Assert.That(MicroQRVersionRange.Between(MicroQRVersion.M1, MicroQRVersion.M4)).IsEqualTo(any);
    }

    [Test]
    public async Task MicroRange_InvalidBounds_ThrowFromTheFactory()
    {
        await Assert.That(() => MicroQRVersionRange.Exactly((MicroQRVersion)0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => MicroQRVersionRange.Exactly((MicroQRVersion)5)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => MicroQRVersionRange.Between(MicroQRVersion.M3, MicroQRVersion.M2)).Throws<ArgumentOutOfRangeException>();
    }

    // ---- terse spellings -------------------------------------------------------------

    [Test]
    public async Task StandardRange_Constructor_IsInclusiveOnBothEnds()
    {
        // The reason this is not System.Range: 1..40 there means 1 to 39.
        var range = new QRVersionRange(10, 20);

        await Assert.That(range.Min).IsEqualTo(10);
        await Assert.That(range.Max).IsEqualTo(20);
        await Assert.That(range.Contains(20)).IsTrue();
        await Assert.That(range).IsEqualTo(QRVersionRange.Between(10, 20));
        await Assert.That(new QRVersionRange(1, 40)).IsEqualTo(QRVersionRange.Any);
    }

    [Test]
    public async Task StandardRange_Constructor_ValidatesLikeTheFactories()
    {
        await Assert.That(() => new QRVersionRange(0, 20)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new QRVersionRange(10, 41)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new QRVersionRange(20, 10)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task StandardRange_ImplicitFromInt_PinsThatVersion()
    {
        QRVersionRange range = 15;
        await Assert.That(range).IsEqualTo(QRVersionRange.Exactly(15));

        var options = new QRCodeGeneratorOptions { Version = 15 };
        await Assert.That(QRCodeGenerator.Create(Digits, QREccLevel.M, options).Version).IsEqualTo(15);
    }

    [Test]
    public async Task StandardRange_ImplicitFromInt_RejectsTheLegacyAutomaticSentinel()
    {
        // -1 is automatic only in the released WithVersion(int) builder method. Accepting it
        // here would let a -1 arriving through a variable silently mean "any version".
        await Assert.That(() => { QRVersionRange _ = -1; }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => { QRVersionRange _ = 0; }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task MicroRange_ConstructorAndImplicitConversion()
    {
        await Assert.That(new MicroQRVersionRange(MicroQRVersion.M2, MicroQRVersion.M3)).IsEqualTo(MicroQRVersionRange.Between(MicroQRVersion.M2, MicroQRVersion.M3));
        await Assert.That(new MicroQRVersionRange(MicroQRVersion.M1, MicroQRVersion.M4)).IsEqualTo(MicroQRVersionRange.Any);
        await Assert.That(() => new MicroQRVersionRange(MicroQRVersion.M3, MicroQRVersion.M2)).Throws<ArgumentOutOfRangeException>();

        MicroQRVersionRange pinned = MicroQRVersion.M3;
        await Assert.That(pinned).IsEqualTo(MicroQRVersionRange.Exactly(MicroQRVersion.M3));

        MicroQRVersionRange absent = (MicroQRVersion?)null;
        await Assert.That(absent).IsEqualTo(MicroQRVersionRange.Any);
    }

    // ---- an optional version needs no branch ------------------------------------------

    [Test]
    public async Task StandardRange_ImplicitFromNullableInt_TreatsAbsenceAsAutomatic()
    {
        // The job the old `requestedVersion: -1` convention was really doing: a caller whose
        // version is optional passes it straight through. `null` says that with a type
        // instead of a magic number, so -1 stays an error.
        int? absent = null;
        int? configured = 12;
        int? mistyped = -1;

        await Assert.That((QRVersionRange)absent).IsEqualTo(QRVersionRange.Any);
        await Assert.That((QRVersionRange)configured).IsEqualTo(QRVersionRange.Exactly(12));
        await Assert.That(() => { QRVersionRange _ = mistyped; }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task StandardQr_OptionalVersion_NeedsNoBranchAndMatchesTheBranchedForm()
    {
        foreach (int? configured in new int?[] { null, 12 })
        {
            // one expression, whether or not a version was configured
            var unbranched = QRCodeGenerator.Create(Digits, QREccLevel.M, new QRCodeGeneratorOptions { Version = configured });

            var branched = configured.HasValue
                ? QRCodeGenerator.Create(Digits, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(configured.Value) })
                : QRCodeGenerator.Create(Digits, QREccLevel.M, QRCodeGeneratorOptions.Default);

            await Assert.That(unbranched.Version).IsEqualTo(branched.Version);
            if (!unbranched.GetRawData().AsSpan().SequenceEqual(branched.GetRawData()))
                Assert.Fail($"configured={configured}: branched and unbranched forms differ");
        }
    }

    [Test]
    public async Task NullLiteral_IsAcceptedAndMeansAutomatic()
    {
        // A side effect of the nullable conversion: `Version = null` compiles even though
        // the property is a non-nullable struct. It reads as "no version specified", which
        // is what it does.
        await Assert.That(new QRCodeGeneratorOptions { Version = null }.Version.IsAny).IsTrue();
        await Assert.That(new MicroQRCodeGeneratorOptions { Version = null }.Version.IsAny).IsTrue();
    }

    // ---- the design assumption the range scan rests on -------------------------------

    [Test]
    [MethodDataSource(nameof(FitPredicateInputs))]
    public async Task StandardQr_FitsIsMonotoneInVersion(QREccLevel ecc, EciMode eci, bool utf8BOM)
    {
        // The range resolves by scanning for the first version in [Min, Max] that fits,
        // which is correct whether or not the predicate is monotone. But the reported
        // version being the *smallest* usable one, and callers reasoning about AtLeast,
        // both assume no version stops fitting as versions grow. The count indicator does
        // widen at versions 10 and 27, so this is a real question, and it is cheap to
        // settle exhaustively rather than assume.
        //
        // The mode loop is in the body because EncodingMode is internal and cannot appear
        // in a public test signature.
        foreach (var mode in new[] { EncodingMode.Numeric, EncodingMode.Alphanumeric, EncodingMode.Byte })
        {
            for (var length = 0; length <= 400; length += 7)
            {
                var seenFit = false;
                for (var version = 1; version <= 40; version++)
                {
                    var fits = QRCodeGenerator.FitsVersion(length, mode, ecc, eci, utf8BOM, version);
                    if (fits)
                        seenFit = true;
                    else if (seenFit)
                        Assert.Fail($"{mode}/{ecc}/{eci}/bom={utf8BOM}: length {length} fits a version below {version} but not {version}");
                }
            }
        }

        await Task.CompletedTask;
    }

    public static IEnumerable<(QREccLevel ecc, EciMode eci, bool utf8BOM)> FitPredicateInputs()
    {
        foreach (var ecc in new[] { QREccLevel.L, QREccLevel.M, QREccLevel.Q, QREccLevel.H })
            foreach (var eci in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
                yield return (ecc, eci, utf8BOM: false);

        yield return (QREccLevel.L, EciMode.Utf8, utf8BOM: true);
        yield return (QREccLevel.H, EciMode.Utf8, utf8BOM: true);
    }

    // ---- Any and Exactly reproduce the released behaviour, byte for byte -------------

    public static IEnumerable<(string text, QREccLevel ecc)> SweepPayloads()
    {
        yield return ("", QREccLevel.M);
        yield return ("1", QREccLevel.L);
        yield return (Digits, QREccLevel.M);
        yield return ("HELLO WORLD 123", QREccLevel.Q);
        yield return ("https://example.com/p/1234567890", QREccLevel.H);
        yield return ("Café déjà vu", QREccLevel.M);
        yield return ("日本語のテキスト", QREccLevel.M);
        yield return (new string('7', 300), QREccLevel.L);
        yield return (new string('A', 200), QREccLevel.H);
    }

    [Test]
    [MethodDataSource(nameof(SweepPayloads))]
    public async Task StandardQr_AnyRange_ReproducesAutomaticSelection(string text, QREccLevel ecc)
    {
        var released = QRCodeGenerator.Create(text, ecc);
        var ranged = QRCodeGenerator.Create(text, ecc, new QRCodeGeneratorOptions { Version = QRVersionRange.Any });

        await Assert.That(ranged.Version).IsEqualTo(released.Version);
        await Assert.That(ranged.GetRawData().AsSpan().SequenceEqual(released.GetRawData())).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(SweepPayloads))]
    public async Task StandardQr_ExactlyRange_ReproducesPinnedVersion(string text, QREccLevel ecc)
    {
        // Sweep every version that actually holds the content, so the comparison covers
        // both count-indicator boundaries (10 and 27).
        var smallest = Sizing.Required(text.AsSpan(), ecc).Version;

        for (var version = smallest; version <= 40; version++)
        {
            var released = QRCodeGenerator.Create(text, ecc, new QRCodeGeneratorOptions { Version = version });
            var ranged = QRCodeGenerator.Create(text, ecc, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version) });

            await Assert.That(ranged.Version).IsEqualTo(version);
            if (!ranged.GetRawData().AsSpan().SequenceEqual(released.GetRawData()))
                Assert.Fail($"version {version} differs between requestedVersion and Exactly({version})");
        }
    }

    [Test]
    [MethodDataSource(nameof(MicroSweepPayloads))]
    public async Task MicroQr_AnyAndExactly_ReproduceReleasedBehaviour(string text, MicroQREccLevel ecc, MicroQRVersion version)
    {
        var releasedAuto = MicroQRCodeGenerator.Create(text, ecc);
        var rangedAuto = MicroQRCodeGenerator.Create(text, ecc, new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.Any });
        await Assert.That(rangedAuto.Version).IsEqualTo(releasedAuto.Version);
        await Assert.That(rangedAuto.GetRawData().AsSpan().SequenceEqual(releasedAuto.GetRawData())).IsTrue();

        var releasedPinned = MicroQRCodeGenerator.Create(text, ecc, new MicroQRCodeGeneratorOptions { Version = version });
        var rangedPinned = MicroQRCodeGenerator.Create(text, ecc, new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.Exactly(version) });
        await Assert.That(rangedPinned.Version).IsEqualTo(version);
        await Assert.That(rangedPinned.GetRawData().AsSpan().SequenceEqual(releasedPinned.GetRawData())).IsTrue();
    }

    public static IEnumerable<(string text, MicroQREccLevel ecc, MicroQRVersion version)> MicroSweepPayloads()
    {
        yield return ("12345", MicroQREccLevel.ErrorDetectionOnly, MicroQRVersion.M1);
        yield return ("1234567890", MicroQREccLevel.L, MicroQRVersion.M3);
        yield return ("1234567890", MicroQREccLevel.L, MicroQRVersion.M4);
        yield return ("AC-42", MicroQREccLevel.M, MicroQRVersion.M3);
        yield return ("hello", MicroQREccLevel.L, MicroQRVersion.M4);
    }

    // ---- range constraint semantics ---------------------------------------------------

    [Test]
    public async Task StandardQr_RangeAboveTheFit_UsesTheRangeMinimum()
    {
        // 10 digits fit version 1; AtLeast(15) has to produce 15, not 1.
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtLeast(15) };

        await Assert.That(QRCodeGenerator.Create(Digits, QREccLevel.M, options).Version).IsEqualTo(15);
        await Assert.That(Sizing.Required(Digits.AsSpan(), QREccLevel.M, options).Version).IsEqualTo(15);
    }

    [Test]
    public async Task StandardQr_RangeStraddlingTheFit_PicksTheSmallestFittingMember()
    {
        var content = new string('A', 200);   // alphanumeric, needs a mid-range version
        var smallest = Sizing.Required(content.AsSpan(), QREccLevel.M).Version;

        var straddling = new QRCodeGeneratorOptions { Version = QRVersionRange.Between(smallest - 2, smallest + 2) };
        await Assert.That(Sizing.Required(content.AsSpan(), QREccLevel.M, straddling).Version).IsEqualTo(smallest);

        // only the maximum fits
        var onlyMax = new QRCodeGeneratorOptions { Version = QRVersionRange.Between(smallest - 2, smallest) };
        await Assert.That(Sizing.Required(content.AsSpan(), QREccLevel.M, onlyMax).Version).IsEqualTo(smallest);

        // only the minimum fits
        var onlyMin = new QRCodeGeneratorOptions { Version = QRVersionRange.Between(smallest, smallest) };
        await Assert.That(Sizing.Required(content.AsSpan(), QREccLevel.M, onlyMin).Version).IsEqualTo(smallest);
    }

    [Test]
    public async Task StandardQr_RangeEntirelyBelowTheFit_DoesNotFit()
    {
        var content = new string('A', 200);
        var smallest = Sizing.Required(content.AsSpan(), QREccLevel.M).Version;
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(smallest - 1) };

        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(content.AsSpan(), QREccLevel.M, out var size, options)).IsFalse();
        await Assert.That(size).IsEqualTo(default(QRCodeCalculatedSize));
        await Assert.That(() => QRCodeGenerator.Create(content, QREccLevel.M, options)).Throws<ArgumentException>();
    }

    [Test]
    public async Task StandardQr_ContentTooLongForAnyVersion_DoesNotFit()
    {
        var content = new string('A', 5000);   // beyond version 40 alphanumeric at H

        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(content.AsSpan(), QREccLevel.H, out _, new QRCodeGeneratorOptions { Version = QRVersionRange.AtLeast(20) })).IsFalse();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(content.AsSpan(), QREccLevel.H, out _, QRCodeGeneratorOptions.Default)).IsFalse();
    }

    [Test]
    public async Task MicroQr_RangeAboveTheFit_UsesTheRangeMinimum()
    {
        var options = new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.AtLeast(MicroQRVersion.M4) };

        await Assert.That(MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L, options).Version).IsEqualTo(MicroQRVersion.M4);
    }

    [Test]
    public async Task MicroQr_RangeWhoseVersionsCannotCarryTheMode_IsNotAFit()
    {
        // Byte mode needs M3 or M4; a range capped at M2 is a "does not fit", not an
        // argument error, because the text is what picks the mode.
        var options = new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.AtMost(MicroQRVersion.M2) };

        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize("hello".AsSpan(), MicroQREccLevel.L, out var size, options)).IsFalse();
        await Assert.That(size).IsEqualTo(default(MicroQRCodeCalculatedSize));
        await Assert.That(() => MicroQRCodeGenerator.Create("hello", MicroQREccLevel.L, options)).Throws<ArgumentException>();
    }

    [Test]
    public async Task MicroQr_RangeWithNoValidEccCombination_Throws()
    {
        // Q exists only on M4, so a range capped at M3 contradicts the ECC level whatever
        // the content is: an argument error, not a "does not fit".
        var options = new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.AtMost(MicroQRVersion.M3) };

        await Assert.That(() => MicroQRCodeGenerator.Create("1", MicroQREccLevel.Q, options)).Throws<ArgumentException>();
        await Assert.That(() => MicroQRCodeGenerator.TryGetRequiredBufferSize("1".AsSpan(), MicroQREccLevel.Q, out _, options)).Throws<ArgumentException>();

        // but a range that still contains M4 is fine
        await Assert.That(MicroQRCodeGenerator.Create("1", MicroQREccLevel.Q, new MicroQRCodeGeneratorOptions { Version = MicroQRVersionRange.Any }).Version).IsEqualTo(MicroQRVersion.M4);
    }

    [Test]
    public async Task Ranged_SizingAndEncoding_Agree()
    {
        // The size a ranged call reports has to be the size the ranged encode writes.
        var content = new string('A', 200);
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtLeast(20), QuietZoneSize = 3 };

        var size = Sizing.Required(content.AsSpan(), QREccLevel.M, options);
        var buffer = new byte[size.BufferSize];
        var written = QRCodeGenerator.Create(content.AsSpan(), QREccLevel.M, buffer, options);

        await Assert.That(written).IsEqualTo(size.BufferSize);
        await Assert.That(size.Version).IsEqualTo(20);
        await Assert.That(QRCodeGenerator.Create(content, QREccLevel.M, options).Version).IsEqualTo(20);
    }
}
