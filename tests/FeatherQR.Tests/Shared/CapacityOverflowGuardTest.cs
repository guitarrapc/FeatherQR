using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// Byte-mode capacity pricing multiplies the data length by 8, which wraps <see cref="int"/>
/// past ~268M units and makes an absurd payload look like it fits. Reachable only with a
/// quarter-gigabyte span, so these drive the selectors directly rather than allocating one.
/// </summary>
public class CapacityOverflowGuardTest
{
    // 8 x this exceeds int.MaxValue; the largest symbol of any symbology holds 7,089 units.
    private const int OverflowingByteLength = 300_000_000;

    [Test]
    [Arguments(RmQRVersion.R7x43)]
    [Arguments(RmQRVersion.R17x139)]
    public async Task RmQR_Fits_RejectsLengthThatWrapsTheBitCount(RmQRVersion version)
    {
        await Assert.That(RmQRVersionSelector.Fits(version, RmQREccLevel.M, EncodingMode.Byte, OverflowingByteLength)).IsFalse();
        await Assert.That(RmQRVersionSelector.Fits(version, RmQREccLevel.M, EncodingMode.Byte, OverflowingByteLength, EciMode.Utf8)).IsFalse();
    }

    [Test]
    public async Task RmQR_TrySelect_RequestedVersion_RejectsWrappingLength()
    {
        // The auto-fit scan compares against a capacity table and was never at risk;
        // the requested-version branch is the one that prices the content itself.
        await Assert.That(RmQRVersionSelector.TrySelect(EncodingMode.Byte, OverflowingByteLength, EciMode.Default, RmQREccLevel.M, RmQRVersion.R7x43, RmQRFitStrategy.MinimizeArea, null, out _)).IsFalse();
        await Assert.That(RmQRVersionSelector.TrySelect(EncodingMode.Byte, OverflowingByteLength, EciMode.Default, RmQREccLevel.M, null, RmQRFitStrategy.MinimizeArea, null, out _)).IsFalse();
    }

    [Test]
    [Arguments(MicroQRVersion.M3)]
    [Arguments(MicroQRVersion.M4)]
    public async Task MicroQR_TrySelectVersion_RejectsWrappingLength(MicroQRVersion version)
    {
        var analysis = new TextAnalysisResult(EncodingMode.Byte, EciMode.Default, OverflowingByteLength);

        await Assert.That(MicroQRCodeGenerator.TrySelectVersion(in analysis, MicroQREccLevel.L, version, out _)).IsFalse();
        await Assert.That(MicroQRCodeGenerator.TrySelectVersion(in analysis, MicroQREccLevel.L, null, out _)).IsFalse();
    }

    [Test]
    public async Task StandardQR_TryGetVersion_RejectsWrappingLength()
    {
        await Assert.That(QRCodeGenerator.TryGetVersion(OverflowingByteLength, EncodingMode.Byte, QREccLevel.L, EciMode.Default, utf8BOM: false, out _)).IsFalse();
        await Assert.That(QRCodeGenerator.TryGetVersion(OverflowingByteLength, EncodingMode.Byte, QREccLevel.L, EciMode.Utf8, utf8BOM: true, out _)).IsFalse();
    }

    [Test]
    public async Task OversizedContent_DoesNotSwallowArgumentErrors()
    {
        // An overflow fast-path placed before the argument checks would answer "does not
        // fit" for a bad enum, and would then hand an unvalidated version to the error
        // builder, which indexes the capacity tables by version.
        var analysis = new TextAnalysisResult(EncodingMode.Byte, EciMode.Default, OverflowingByteLength);

        await Assert.That(() => MicroQRCodeGenerator.TrySelectVersion(in analysis, MicroQREccLevel.L, (MicroQRVersion)0, out _)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => MicroQRCodeGenerator.TrySelectVersion(in analysis, MicroQREccLevel.L, (MicroQRVersion)5, out _)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => MicroQRCodeGenerator.TrySelectVersion(in analysis, (MicroQREccLevel)9, null, out _)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => MicroQRCodeGenerator.TrySelectVersion(in analysis, MicroQREccLevel.L, MicroQRVersion.M1, out _)).Throws<ArgumentException>();

        // Standard QR validates the ECC level inside the version scan, so an oversized
        // payload must still reach it rather than short-cutting to false.
        await Assert.That(() => QRCodeGenerator.TryGetVersion(OverflowingByteLength, EncodingMode.Byte, (QREccLevel)9, EciMode.Default, utf8BOM: false, out _)).Throws<ArgumentException>();

        await Assert.That(() => RmQRVersionSelector.TrySelect(EncodingMode.Byte, OverflowingByteLength, EciMode.Default, (RmQREccLevel)2, null, RmQRFitStrategy.MinimizeArea, null, out _)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRVersionSelector.TrySelect(EncodingMode.Byte, OverflowingByteLength, EciMode.Default, RmQREccLevel.M, (RmQRVersion)33, RmQRFitStrategy.MinimizeArea, null, out _)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task RmQR_Fits_ReportsTheModeError_AtEveryLength()
    {
        // Whether a mode is priceable must not depend on how long the content is; a
        // length fast-path ahead of the mode switch makes the same call throw at 10 and
        // answer false at 300M.
        await Assert.That(() => RmQRVersionSelector.Fits(RmQRVersion.R7x43, RmQREccLevel.M, EncodingMode.ECI, 10)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRVersionSelector.Fits(RmQRVersion.R7x43, RmQREccLevel.M, EncodingMode.ECI, OverflowingByteLength)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRVersionSelector.Fits(RmQRVersion.R7x43, RmQREccLevel.M, EncodingMode.ECI, OverflowingByteLength, EciMode.Utf8)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task StandardQR_TryGetVersion_BomDoesNotWrapTheEffectiveLength()
    {
        // effectiveLength += 3 must not wrap before the widened multiply, or a maximal
        // Byte payload reads as Version 1.
        await Assert.That(QRCodeGenerator.TryGetVersion(int.MaxValue, EncodingMode.Byte, QREccLevel.L, EciMode.Utf8, utf8BOM: true, out _)).IsFalse();
        await Assert.That(QRCodeGenerator.TryGetVersion(int.MaxValue - 2, EncodingMode.Byte, QREccLevel.L, EciMode.Utf8, utf8BOM: true, out _)).IsFalse();
        await Assert.That(QRCodeGenerator.TryGetVersion(int.MaxValue, EncodingMode.Numeric, QREccLevel.L, EciMode.Default, utf8BOM: false, out _)).IsFalse();
    }

    [Test]
    public async Task LargestFittingLengths_StillFit()
    {
        // The guard must not clip legitimate content: these are the published maxima.
        await Assert.That(RmQRVersionSelector.Fits(RmQRVersion.R17x139, RmQREccLevel.M, EncodingMode.Byte, 150)).IsTrue();
        await Assert.That(RmQRVersionSelector.Fits(RmQRVersion.R17x139, RmQREccLevel.M, EncodingMode.Numeric, 361)).IsTrue();

        var micro = new TextAnalysisResult(EncodingMode.Numeric, EciMode.Default, 35);
        await Assert.That(MicroQRCodeGenerator.TrySelectVersion(in micro, MicroQREccLevel.L, MicroQRVersion.M4, out _)).IsTrue();

        await Assert.That(QRCodeGenerator.TryGetVersion(2953, EncodingMode.Byte, QREccLevel.L, EciMode.Default, utf8BOM: false, out var v40Byte)).IsTrue();
        await Assert.That(v40Byte).IsEqualTo(40);
        await Assert.That(QRCodeGenerator.TryGetVersion(7089, EncodingMode.Numeric, QREccLevel.L, EciMode.Default, utf8BOM: false, out var v40Numeric)).IsTrue();
        await Assert.That(v40Numeric).IsEqualTo(40);
    }
}
