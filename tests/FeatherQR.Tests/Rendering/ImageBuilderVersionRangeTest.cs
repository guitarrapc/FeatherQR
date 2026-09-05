using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// The range-taking <c>WithVersion</c> overload on the Standard QR and Micro QR image
/// builders, and the existing version-taking overload keeping its meaning as the pinned case
/// (specs/qrcode-symbologies.md, Public API direction).
/// </summary>
/// <remarks>
/// The builders now assemble an options value instead of passing positional arguments, so
/// what these tests pin is that the version constraint survives the trip: a builder
/// configured with a range must render the same image as one handed the symbol the
/// generator produces for that same range.
/// </remarks>
public class ImageBuilderVersionRangeTest
{
    private const string Content = "https://example.com/p/1234567890";
    private const string MicroContent = "1234567890";

    // ---- Standard QR ------------------------------------------------------------------

    [Test]
    public async Task StandardQr_WithVersionOverload_MatchesTheGeneratorForTheSameRange()
    {
        var range = QRVersionRange.AtLeast(15);
        var options = new QRCodeGeneratorOptions { Version = range };

        var viaBuilder = new QRCodeImageBuilder(Content).WithVersion(range).ToByteArray();
        var viaData = new QRCodeImageBuilder(QRCodeGenerator.Create(Content, QREccLevel.M, options)).ToByteArray();

        await Assert.That(viaBuilder).IsEquivalentTo(viaData);
    }

    [Test]
    public async Task StandardQr_WithVersionExactly_EqualsWithVersionInt()
    {
        // The smallest version that holds the content, plus both count-indicator
        // boundaries and the top of the range.
        var smallest = Sizing.Required(Content.AsSpan(), QREccLevel.M).Version;

        foreach (var version in new[] { smallest, 10, 27, 40 })
        {
            var pinned = new QRCodeImageBuilder(Content).WithVersion(version).ToByteArray();
            var ranged = new QRCodeImageBuilder(Content).WithVersion(QRVersionRange.Exactly(version)).ToByteArray();

            if (!ranged.AsSpan().SequenceEqual(pinned))
                Assert.Fail($"version {version}: WithVersion(int) and WithVersion(Exactly) rendered differently");
        }

        await Assert.That(smallest).IsLessThan(10);   // the loop really does cover three distinct bands
    }

    [Test]
    public async Task StandardQr_AutomaticSpellings_AllAgree()
    {
        int? absent = null;
        var untouched = new QRCodeImageBuilder(Content).ToByteArray();
        var minusOne = new QRCodeImageBuilder(Content).WithVersion(-1).ToByteArray();
        var any = new QRCodeImageBuilder(Content).WithVersion(QRVersionRange.Any).ToByteArray();
        var nullable = new QRCodeImageBuilder(Content).WithVersion(absent).ToByteArray();

        await Assert.That(minusOne).IsEquivalentTo(untouched);
        await Assert.That(any).IsEquivalentTo(untouched);
        await Assert.That(nullable).IsEquivalentTo(untouched);
    }

    [Test]
    public async Task StandardQr_OptionalVersion_FlowsThroughTheBuilderWithoutABranch()
    {
        // The fluent chain stays one expression whether or not a version was configured.
        foreach (int? configured in new int?[] { null, 20 })
        {
            var unbranched = new QRCodeImageBuilder(Content).WithVersion(configured).ToByteArray();

            var builder = new QRCodeImageBuilder(Content);
            if (configured.HasValue)
                builder = builder.WithVersion(configured.Value);
            var branched = builder.ToByteArray();

            if (!unbranched.AsSpan().SequenceEqual(branched))
                Assert.Fail($"configured={configured}: branched and unbranched builder chains differ");

            await Assert.That(unbranched.Length).IsGreaterThan(0);
        }
    }

    [Test]
    public async Task StandardQr_WithVersionOverload_RejectsAPreBuiltSymbol()
    {
        var data = QRCodeGenerator.Create(Content, QREccLevel.M);

        await Assert.That(() => new QRCodeImageBuilder(data).WithVersion(QRVersionRange.AtLeast(15))).Throws<InvalidOperationException>();
        await Assert.That(() => new QRCodeImageBuilder(data).WithVersion(15)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task StandardQr_RangeThatCannotHoldTheContent_ReportsItAsAnArgumentError()
    {
        // Before Phase 4 the builder handed the version straight to the encoder, which
        // failed deep inside with ArgumentOutOfRangeException (Parameter 'length'). Going
        // through the options overload means the fit is checked up front instead.
        var builder = new QRCodeImageBuilder(new string('A', 500)).WithVersion(1);

        var error = Assert.Throws<ArgumentException>(() => builder.ToByteArray());
        await Assert.That(error!.Message).Contains("does not fit");
    }

    // ---- Micro QR ---------------------------------------------------------------------

    [Test]
    public async Task MicroQr_WithVersionOverload_MatchesTheGeneratorForTheSameRange()
    {
        var range = MicroQRVersionRange.AtLeast(MicroQRVersion.M4);
        var options = new MicroQRCodeGeneratorOptions { Version = range };

        var viaBuilder = new MicroQRCodeImageBuilder(MicroContent).WithErrorCorrection(MicroQREccLevel.L).WithVersion(range).ToByteArray();
        var viaData = new MicroQRCodeImageBuilder(MicroQRCodeGenerator.Create(MicroContent, MicroQREccLevel.L, options)).ToByteArray();

        await Assert.That(viaBuilder).IsEquivalentTo(viaData);
    }

    [Test]
    [Arguments(MicroQRVersion.M3)]
    [Arguments(MicroQRVersion.M4)]
    public async Task MicroQr_WithVersionExactly_EqualsWithVersionInt(MicroQRVersion version)
    {
        var pinned = new MicroQRCodeImageBuilder(MicroContent).WithErrorCorrection(MicroQREccLevel.L).WithVersion(version).ToByteArray();
        var ranged = new MicroQRCodeImageBuilder(MicroContent).WithErrorCorrection(MicroQREccLevel.L).WithVersion(MicroQRVersionRange.Exactly(version)).ToByteArray();

        await Assert.That(ranged).IsEquivalentTo(pinned);
    }

    [Test]
    public async Task MicroQr_AutomaticSpellings_Agree()
    {
        var untouched = new MicroQRCodeImageBuilder(MicroContent).WithErrorCorrection(MicroQREccLevel.L).ToByteArray();
        var any = new MicroQRCodeImageBuilder(MicroContent).WithErrorCorrection(MicroQREccLevel.L).WithVersion(MicroQRVersionRange.Any).ToByteArray();

        await Assert.That(any).IsEquivalentTo(untouched);
    }

    [Test]
    public async Task MicroQr_WithVersionOverload_RejectsAPreBuiltSymbol()
    {
        var data = MicroQRCodeGenerator.Create(MicroContent, MicroQREccLevel.L);

        await Assert.That(() => new MicroQRCodeImageBuilder(data).WithVersion(MicroQRVersionRange.AtLeast(MicroQRVersion.M4))).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task MicroQr_RangeThatCannotCarryTheMode_ReportsItAsAnArgumentError()
    {
        // Byte content needs M3 or M4; capping the range at M2 leaves nothing usable.
        var builder = new MicroQRCodeImageBuilder("hello").WithErrorCorrection(MicroQREccLevel.L).WithVersion(MicroQRVersionRange.AtMost(MicroQRVersion.M2));

        await Assert.That(() => builder.ToByteArray()).Throws<ArgumentException>();
    }
}
