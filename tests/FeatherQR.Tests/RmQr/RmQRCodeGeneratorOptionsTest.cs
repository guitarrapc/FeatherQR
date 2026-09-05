namespace FeatherQR.Tests;

/// <summary>
/// <see cref="RmQRCodeGeneratorOptions"/>, the options struct that replaced the rMQR
/// generator's parameter lists (specs/qrcode-symbologies.md, Public API direction).
/// </summary>
/// <remarks>
/// <para>
/// The tests that matter here are about <c>default</c>. Every generator entry point
/// declares <c>in RmQRCodeGeneratorOptions options = default</c>, so <c>default(T)</c>
/// is the value every unadorned call sends, and any member whose documented default is
/// not the zero value has to encode that offset in its backing field. Quiet zone is the
/// only such member today: ISO/IEC 23941 specifies 2 modules for rMQR, and 0 is a
/// legitimate caller choice, so "unset" cannot simply mean 0.
/// </para>
/// <para>
/// It is stored as an offset from the default rather than as a <c>value + 1</c>
/// sentinel so that the canonical form is unique: writing the default value explicitly
/// has to produce a value indistinguishable from not writing it, or the struct's
/// equality reports two behaviourally identical option sets as different.
/// </para>
/// </remarks>
public class RmQRCodeGeneratorOptionsTest
{
    private const int SpecQuietZone = 2;      // ISO/IEC 23941
    private const int R7x43CoreWidth = 43;
    private const int R7x43CoreHeight = 7;

    // ---- default(T) is the value an unadorned call actually sends ------------------

    [Test]
    public async Task Default_CarriesTheDocumentedDefaults()
    {
        var options = default(RmQRCodeGeneratorOptions);

        await Assert.That(options.EciMode).IsEqualTo(EciMode.Default);
        await Assert.That(options.Version.HasValue).IsFalse();
        await Assert.That(options.FitStrategy).IsEqualTo(RmQRFitStrategy.MinimizeArea);
        await Assert.That(options.Height.HasValue).IsFalse();
        await Assert.That(options.QuietZoneSize).IsEqualTo(SpecQuietZone);
        await Assert.That(options.Segmentation).IsEqualTo(RmQRSegmentation.Single);
    }

    [Test]
    public async Task DefaultProperty_IsTheSameValueAsTheDefaultKeyword()
    {
        await Assert.That(RmQRCodeGeneratorOptions.Default).IsEqualTo(default(RmQRCodeGeneratorOptions));
    }

    // ---- the quiet-zone sentinel ---------------------------------------------------

    [Test]
    public async Task QuietZoneSize_WrittenAsItsDefaultValue_IsIndistinguishableFromUnset()
    {
        // Offset encoding, not value+1: an explicit 2 must collapse onto the unset form,
        // otherwise equality reports two identical option sets as different.
        var written = new RmQRCodeGeneratorOptions { QuietZoneSize = SpecQuietZone };

        await Assert.That(written).IsEqualTo(default(RmQRCodeGeneratorOptions));
        await Assert.That(written.GetHashCode()).IsEqualTo(default(RmQRCodeGeneratorOptions).GetHashCode());
    }

    [Test]
    public async Task QuietZoneSize_Zero_IsExpressibleAndDiffersFromUnset()
    {
        var noQuietZone = new RmQRCodeGeneratorOptions { QuietZoneSize = 0 };

        await Assert.That(noQuietZone.QuietZoneSize).IsEqualTo(0);
        await Assert.That(noQuietZone).IsNotEqualTo(default(RmQRCodeGeneratorOptions));
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(10_000)]
    public async Task QuietZoneSize_RoundTripsOverItsWholeRange(int quietZoneSize)
    {
        await Assert.That(new RmQRCodeGeneratorOptions { QuietZoneSize = quietZoneSize }.QuietZoneSize).IsEqualTo(quietZoneSize);
    }

    // ---- ordinary value semantics --------------------------------------------------

    [Test]
    public async Task With_ChangesOnlyTheNamedMember()
    {
        var source = new RmQRCodeGeneratorOptions
        {
            EciMode = EciMode.Utf8,
            Version = RmQRVersion.R11x27,
            FitStrategy = RmQRFitStrategy.MinimizeHeight,
            Height = RmQRHeight.H11,
            QuietZoneSize = 5,
            Segmentation = RmQRSegmentation.Optimal,
        };

        var changed = source with { QuietZoneSize = 0 };

        await Assert.That(changed.QuietZoneSize).IsEqualTo(0);
        await Assert.That(changed.EciMode).IsEqualTo(EciMode.Utf8);
        await Assert.That(changed.Version).IsEqualTo(RmQRVersion.R11x27);
        await Assert.That(changed.FitStrategy).IsEqualTo(RmQRFitStrategy.MinimizeHeight);
        await Assert.That(changed.Height).IsEqualTo(RmQRHeight.H11);
        await Assert.That(changed.Segmentation).IsEqualTo(RmQRSegmentation.Optimal);
        await Assert.That(source.QuietZoneSize).IsEqualTo(5);   // the source is untouched
    }

    [Test]
    public async Task ObjectInitializerAndWith_ProduceEqualValues()
    {
        var initialized = new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43, QuietZoneSize = 0 };
        var withed = RmQRCodeGeneratorOptions.Default with { Version = RmQRVersion.R7x43, QuietZoneSize = 0 };

        await Assert.That(initialized).IsEqualTo(withed);
    }

    // ---- the generator actually reads the defaults ---------------------------------

    [Test]
    public async Task Generator_UnadornedCall_UsesTheSpecQuietZone()
    {
        // The end-to-end proof of the sentinel: if "unset" meant 0, this symbol would be
        // 43x7 instead of 47x11, and every existing rMQR expectation would shift.
        var size = Sizing.Required("1".AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 });

        await Assert.That(size.Width).IsEqualTo(R7x43CoreWidth + SpecQuietZone * 2);
        await Assert.That(size.Height).IsEqualTo(R7x43CoreHeight + SpecQuietZone * 2);
    }

    [Test]
    public async Task Generator_ExplicitZeroQuietZone_DropsTheMargin()
    {
        var size = Sizing.Required("1".AsSpan(), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43, QuietZoneSize = 0 });

        await Assert.That(size.Width).IsEqualTo(R7x43CoreWidth);
        await Assert.That(size.Height).IsEqualTo(R7x43CoreHeight);
    }

    [Test]
    public async Task Generator_OmittedOptions_MatchesAnExplicitlyDefaultedOptions()
    {
        var omitted = RmQRCodeGenerator.Create("123456", RmQREccLevel.M);
        var explicitly = RmQRCodeGenerator.Create("123456", RmQREccLevel.M, RmQRCodeGeneratorOptions.Default);

        await Assert.That(omitted.Version).IsEqualTo(explicitly.Version);
        await Assert.That(omitted.GetRawData().AsSpan().SequenceEqual(explicitly.GetRawData())).IsTrue();
    }
}
