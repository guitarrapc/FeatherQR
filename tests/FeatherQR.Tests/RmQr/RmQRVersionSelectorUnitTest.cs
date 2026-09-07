using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// rMQR version fit (design record: exact version, or <see cref="RmQRFitStrategy"/>
/// within an optional <see cref="RmQRHeight"/> constraint), the capacity arithmetic
/// behind it, and the actionable error messages. Expected versions are hand-derived
/// from the capacity table (specs/rmqr-encoder.md); areas: R7x43 = 301,
/// R11x27 = 297, R13x27 = 351, R9x43 = 387, R7x59 = 413, R15x43 = 645, R11x59 = 649.
/// </summary>
public class RmQRVersionSelectorUnitTest
{
    public static IEnumerable<(RmQRVersion version, RmQREccLevel ecc)> AllVersionEcc()
    {
        foreach (var v in Enum.GetValues<RmQRVersion>())
        {
            yield return (v, RmQREccLevel.M);
            yield return (v, RmQREccLevel.H);
        }
    }

    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task GetMaxDataLength_IsTheInverseOfGetRequiredBits(RmQRVersion version, RmQREccLevel ecc)
    {
        var capacityBits = 8 * RmQRConstants.GetDataCodewordCount(version, ecc);
        foreach (var mode in new[] { EncodingMode.Numeric, EncodingMode.Alphanumeric, EncodingMode.Byte })
            foreach (var eciMode in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
            {
                var max = RmQRVersionSelector.GetMaxDataLength(version, ecc, mode, eciMode);
                await Assert.That(RmQRVersionSelector.GetRequiredBits(version, mode, max, eciMode)).IsLessThanOrEqualTo(capacityBits);
                await Assert.That(RmQRVersionSelector.GetRequiredBits(version, mode, max + 1, eciMode)).IsGreaterThan(capacityBits);
                await Assert.That(RmQRVersionSelector.Fits(version, ecc, mode, max, eciMode)).IsTrue();
                await Assert.That(RmQRVersionSelector.Fits(version, ecc, mode, max + 1, eciMode)).IsFalse();
            }
    }

    [Test]
    public async Task EciHeader_IsIncludedInRequiredBitsAndCapacity()
    {
        // R7x43-H has 24 data bits. Without ECI, Byte header = 3+3 bits and two
        // bytes fit (22 bits). With ECI, 11+3+3 bits leave only seven: zero bytes.
        await Assert.That(RmQRVersionSelector.GetRequiredBits(RmQRVersion.R7x43, EncodingMode.Byte, 0, EciMode.Utf8)).IsEqualTo(17);
        await Assert.That(RmQRVersionSelector.GetRequiredBits(RmQRVersion.R7x43, EncodingMode.Byte, 1, EciMode.Utf8)).IsEqualTo(25);
        await Assert.That(RmQRVersionSelector.GetMaxDataLength(RmQRVersion.R7x43, RmQREccLevel.H, EncodingMode.Byte, EciMode.Utf8)).IsEqualTo(0);
        await Assert.That(RmQRVersionSelector.Fits(RmQRVersion.R7x43, RmQREccLevel.H, EncodingMode.Byte, 1, EciMode.Utf8)).IsFalse();

        // Assignment 3 and 26 use the same 8-bit designator form.
        await Assert.That(RmQRVersionSelector.GetRequiredBits(RmQRVersion.R7x43, EncodingMode.Byte, 1, EciMode.Iso8859_1)).IsEqualTo(25);
    }

    [Test]
    public async Task Select_EciBoundary_UsesTheNextFittingVersion()
    {
        var withoutEci = RmQRVersionSelector.Select(EncodingMode.Byte, 2, EciMode.Default, RmQREccLevel.H, null, RmQRFitStrategy.MinimizeHeight, null);
        var withEci = RmQRVersionSelector.Select(EncodingMode.Byte, 2, EciMode.Utf8, RmQREccLevel.H, null, RmQRFitStrategy.MinimizeHeight, null);
        await Assert.That(withoutEci).IsEqualTo(RmQRVersion.R7x43);
        await Assert.That(withEci).IsNotEqualTo(RmQRVersion.R7x43);
        await Assert.That(RmQRVersionSelector.Fits(withEci, RmQREccLevel.H, EncodingMode.Byte, 2, EciMode.Utf8)).IsTrue();
    }

    [Test]
    [Arguments(RmQRVersion.R7x43, RmQREccLevel.M, "Numeric", 12)]
    [Arguments(RmQRVersion.R7x43, RmQREccLevel.H, "Byte", 2)]
    [Arguments(RmQRVersion.R11x27, RmQREccLevel.M, "Alphanumeric", 8)]
    [Arguments(RmQRVersion.R17x139, RmQREccLevel.M, "Numeric", 361)]
    [Arguments(RmQRVersion.R17x139, RmQREccLevel.H, "Byte", 74)]
    public async Task GetMaxDataLength_MatchesPublishedCapacities(RmQRVersion version, RmQREccLevel ecc, string mode, int expected)
    {
        await Assert.That(RmQRVersionSelector.GetMaxDataLength(version, ecc, Enum.Parse<EncodingMode>(mode))).IsEqualTo(expected);
    }

    // ---- automatic fit --------------------------------------------------------

    [Test]
    [Arguments(12, RmQRFitStrategy.MinimizeArea, RmQRVersion.R11x27)]    // R7x43 (301) also fits; 297 wins
    [Arguments(12, RmQRFitStrategy.MinimizeWidth, RmQRVersion.R11x27)]
    [Arguments(12, RmQRFitStrategy.MinimizeHeight, RmQRVersion.R7x43)]
    [Arguments(13, RmQRFitStrategy.MinimizeArea, RmQRVersion.R11x27)]    // R7x43 (12) no longer fits
    [Arguments(13, RmQRFitStrategy.MinimizeHeight, RmQRVersion.R7x59)]
    [Arguments(15, RmQRFitStrategy.MinimizeArea, RmQRVersion.R13x27)]    // R11x27 (14) out; 351 < 387 (R9x43) < 413 (R7x59)
    [Arguments(15, RmQRFitStrategy.MinimizeWidth, RmQRVersion.R13x27)]
    [Arguments(15, RmQRFitStrategy.MinimizeHeight, RmQRVersion.R7x59)]
    [Arguments(27, RmQRFitStrategy.MinimizeArea, RmQRVersion.R11x43)]    // R7x59 / R9x43 / R13x27 (26) out; 473 < 531 (R9x59) < 539 (R7x77)
    [Arguments(27, RmQRFitStrategy.MinimizeWidth, RmQRVersion.R11x43)]   // width 43: shortest of R11/R13/R15/R17x43
    [Arguments(27, RmQRFitStrategy.MinimizeHeight, RmQRVersion.R7x77)]
    [Arguments(361, RmQRFitStrategy.MinimizeArea, RmQRVersion.R17x139)]  // only the largest fits
    [Arguments(361, RmQRFitStrategy.MinimizeHeight, RmQRVersion.R17x139)]
    [Arguments(0, RmQRFitStrategy.MinimizeArea, RmQRVersion.R11x27)]
    [Arguments(0, RmQRFitStrategy.MinimizeWidth, RmQRVersion.R11x27)]
    [Arguments(0, RmQRFitStrategy.MinimizeHeight, RmQRVersion.R7x43)]
    public async Task Select_NumericM_ByStrategy(int digits, RmQRFitStrategy strategy, RmQRVersion expected)
    {
        var actual = RmQRVersionSelector.Select(EncodingMode.Numeric, digits, RmQREccLevel.M, requestedVersion: null, strategy, height: null);
        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    [Arguments(RmQRHeight.H7, RmQRVersion.R7x59)]
    [Arguments(RmQRHeight.H9, RmQRVersion.R9x43)]
    [Arguments(RmQRHeight.H11, RmQRVersion.R11x43)]
    [Arguments(RmQRHeight.H13, RmQRVersion.R13x27)]
    [Arguments(RmQRHeight.H15, RmQRVersion.R15x43)]
    [Arguments(RmQRHeight.H17, RmQRVersion.R17x43)]
    public async Task Select_HeightConstraint_PicksNarrowestOfThatHeight(RmQRHeight height, RmQRVersion expected)
    {
        // 15 digits at M within a fixed height, MinimizeArea == MinimizeWidth within one height.
        var area = RmQRVersionSelector.Select(EncodingMode.Numeric, 15, RmQREccLevel.M, null, RmQRFitStrategy.MinimizeArea, height);
        var width = RmQRVersionSelector.Select(EncodingMode.Numeric, 15, RmQREccLevel.M, null, RmQRFitStrategy.MinimizeWidth, height);
        await Assert.That(area).IsEqualTo(expected);
        await Assert.That(width).IsEqualTo(expected);
    }

    [Test]
    public async Task Select_HeightConstraint_TooLong_ReportsThatHeightsMaximum()
    {
        // 103 digits do not fit any height-7 symbol at M (R7x139 holds 102).
        var ex = await Assert.That(() => RmQRVersionSelector.Select(EncodingMode.Numeric, 103, RmQREccLevel.M, null, RmQRFitStrategy.MinimizeArea, RmQRHeight.H7))
            .Throws<ArgumentException>();
        await Assert.That(ex!.Message).Contains("103 digits");
        await Assert.That(ex.Message).Contains("102 digits");
        await Assert.That(ex.Message).Contains("R7x139");
        await Assert.That(ex.Message).Contains("H7");
    }

    [Test]
    public async Task Select_TooLongForAnyVersion_ReportsTheLargestCapacity()
    {
        var ex = await Assert.That(() => RmQRVersionSelector.Select(EncodingMode.Byte, 151, RmQREccLevel.M, null, RmQRFitStrategy.MinimizeArea, null))
            .Throws<ArgumentException>();
        await Assert.That(ex!.Message).Contains("151 bytes");
        await Assert.That(ex.Message).Contains("150 bytes");
        await Assert.That(ex.Message).Contains("R17x139");
        await Assert.That(ex.Message).Contains("QRCodeGenerator");
    }

    // ---- requested version -----------------------------------------------------

    [Test]
    public async Task Select_RequestedVersion_IsHonored_WhenItFits()
    {
        var actual = RmQRVersionSelector.Select(EncodingMode.Numeric, 5, RmQREccLevel.H, RmQRVersion.R17x139, RmQRFitStrategy.MinimizeArea, null);
        await Assert.That(actual).IsEqualTo(RmQRVersion.R17x139);
    }

    [Test]
    public async Task Select_RequestedVersion_TooLong_ReportsThatVersionsMaximum()
    {
        var ex = await Assert.That(() => RmQRVersionSelector.Select(EncodingMode.Alphanumeric, 8, RmQREccLevel.M, RmQRVersion.R7x43, RmQRFitStrategy.MinimizeArea, null))
            .Throws<ArgumentException>();
        await Assert.That(ex!.Message).Contains("R7x43");
        await Assert.That(ex.Message).Contains("8 characters");
        await Assert.That(ex.Message).Contains("7 characters");
    }

    [Test]
    public async Task Select_RequestedVersion_WithAgreeingHeight_IsAccepted_DisagreeingRejected()
    {
        var ok = RmQRVersionSelector.Select(EncodingMode.Numeric, 1, RmQREccLevel.M, RmQRVersion.R9x59, RmQRFitStrategy.MinimizeArea, RmQRHeight.H9);
        await Assert.That(ok).IsEqualTo(RmQRVersion.R9x59);
        await Assert.That(() => RmQRVersionSelector.Select(EncodingMode.Numeric, 1, RmQREccLevel.M, RmQRVersion.R9x59, RmQRFitStrategy.MinimizeArea, RmQRHeight.H7))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Select_RejectsInvalidEnums()
    {
        await Assert.That(() => RmQRVersionSelector.Select(EncodingMode.Numeric, 1, (RmQREccLevel)2, null, RmQRFitStrategy.MinimizeArea, null)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRVersionSelector.Select(EncodingMode.Numeric, 1, RmQREccLevel.M, (RmQRVersion)0, RmQRFitStrategy.MinimizeArea, null)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRVersionSelector.Select(EncodingMode.Numeric, 1, RmQREccLevel.M, (RmQRVersion)33, RmQRFitStrategy.MinimizeArea, null)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRVersionSelector.Select(EncodingMode.Numeric, 1, RmQREccLevel.M, null, (RmQRFitStrategy)3, null)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRVersionSelector.Select(EncodingMode.Numeric, 1, RmQREccLevel.M, null, RmQRFitStrategy.MinimizeArea, (RmQRHeight)8)).Throws<ArgumentOutOfRangeException>();
    }

    // ---- tie-break rules (comparator), areas can tie (R7x99 = R9x77 = 693) ------

    [Test]
    [Arguments(RmQRFitStrategy.MinimizeArea, RmQRVersion.R7x99, RmQRVersion.R9x77, true)]    // equal area → smaller height wins
    [Arguments(RmQRFitStrategy.MinimizeArea, RmQRVersion.R9x77, RmQRVersion.R7x99, false)]
    [Arguments(RmQRFitStrategy.MinimizeArea, RmQRVersion.R11x27, RmQRVersion.R7x43, true)]   // 297 < 301
    [Arguments(RmQRFitStrategy.MinimizeWidth, RmQRVersion.R7x43, RmQRVersion.R9x43, true)]   // equal width → smaller height
    [Arguments(RmQRFitStrategy.MinimizeWidth, RmQRVersion.R9x43, RmQRVersion.R7x43, false)]
    [Arguments(RmQRFitStrategy.MinimizeWidth, RmQRVersion.R13x27, RmQRVersion.R7x43, true)]  // 27 < 43 regardless of height
    [Arguments(RmQRFitStrategy.MinimizeHeight, RmQRVersion.R7x43, RmQRVersion.R7x59, true)]  // equal height → smaller width
    [Arguments(RmQRFitStrategy.MinimizeHeight, RmQRVersion.R7x59, RmQRVersion.R7x43, false)]
    [Arguments(RmQRFitStrategy.MinimizeHeight, RmQRVersion.R7x139, RmQRVersion.R9x43, true)] // 7 < 9 regardless of width
    public async Task IsBetter_TieBreaks(RmQRFitStrategy strategy, RmQRVersion candidate, RmQRVersion incumbent, bool expected)
    {
        await Assert.That(RmQRVersionSelector.IsBetter(candidate, incumbent, strategy)).IsEqualTo(expected);
        await Assert.That(RmQRVersionSelector.IsBetter(candidate, candidate, strategy)).IsFalse();
    }
    /// <summary>
    /// The table-driven auto fit (best-first version order per strategy, precomputed
    /// capacities, height bitmask) must pick exactly what the definitional scan picks:
    /// among all versions of the allowed height that <see cref="RmQRVersionSelector.Fits"/>,
    /// the one no other candidate <see cref="RmQRVersionSelector.IsBetter"/> than — for
    /// every mode × ECC × strategy × height filter × data length up to the largest
    /// capacity plus a margin (where nothing fits, both must fail).
    /// </summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task Select_AutoFit_MatchesDefinitionalScan_EveryLengthStrategyHeight(int modeIndex)
    {
        var mode = new[] { EncodingMode.Numeric, EncodingMode.Alphanumeric, EncodingMode.Byte }[modeIndex];
        var heights = new RmQRHeight?[] { null, RmQRHeight.H7, RmQRHeight.H9, RmQRHeight.H11, RmQRHeight.H13, RmQRHeight.H15, RmQRHeight.H17 };
        var checks = 0;
        foreach (var eciMode in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
        {
            var maxAll = Enum.GetValues<RmQRVersion>().Max(v => RmQRVersionSelector.GetMaxDataLength(v, RmQREccLevel.M, mode, eciMode));
            foreach (var ecc in new[] { RmQREccLevel.M, RmQREccLevel.H })
                foreach (var strategy in Enum.GetValues<RmQRFitStrategy>())
                    foreach (var height in heights)
                        for (var length = 0; length <= maxAll + 3; length++)
                        {
                            RmQRVersion expected = 0;
                            foreach (var candidate in Enum.GetValues<RmQRVersion>())
                            {
                                if (height is { } h && RmQRConstants.GetHeight(candidate) != (int)h) continue;
                                if (RmQRVersionSelector.Fits(candidate, ecc, mode, length, eciMode) && (expected == 0 || RmQRVersionSelector.IsBetter(candidate, expected, strategy)))
                                    expected = candidate;
                            }

                            if (expected == 0)
                            {
                                await Assert.That(() => RmQRVersionSelector.Select(mode, length, eciMode, ecc, null, strategy, height)).Throws<ArgumentException>();
                            }
                            else
                            {
                                var actual = RmQRVersionSelector.Select(mode, length, eciMode, ecc, null, strategy, height);
                                if (actual != expected)
                                    Assert.Fail($"{mode} len {length} {eciMode} {ecc} {strategy} height {height}: expected {expected}, got {actual}");
                            }
                            checks++;
                        }
        }
        await Assert.That(checks).IsGreaterThan(1000);
    }
    /// <summary>
    /// An unsupported mode (ECI / Kanji never come out of the analyzer, but the internal
    /// contract is one exception on every path): the auto-fit path (table index) and
    /// the requested-version path (count-indicator lookup) must throw the same type,
    /// parameter name and message.
    /// </summary>
    [Test]
    public async Task Select_UnsupportedMode_ThrowsTheSameOnAutoFitAndRequestedVersion()
    {
        var auto = Assert.Throws<ArgumentOutOfRangeException>(() => RmQRVersionSelector.Select(EncodingMode.ECI, 5, RmQREccLevel.M, null, RmQRFitStrategy.MinimizeArea, null));
        var requested = Assert.Throws<ArgumentOutOfRangeException>(() => RmQRVersionSelector.Select(EncodingMode.ECI, 5, RmQREccLevel.M, RmQRVersion.R7x43, RmQRFitStrategy.MinimizeArea, null));
        await Assert.That(auto.ParamName).IsEqualTo("mode");
        await Assert.That(requested.ParamName).IsEqualTo("mode");
        await Assert.That(auto.Message).IsEqualTo(requested.Message);
        await Assert.That(auto.Message).Contains("not supported by rMQR");
    }
}
