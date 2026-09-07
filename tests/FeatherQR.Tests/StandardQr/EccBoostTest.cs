using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// ECC boost (<see cref="QRCodeGeneratorOptions.BoostEccLevel"/>): the version is chosen
/// for the requested level, then the level is raised as far as that version's capacity
/// allows. The version never changes, which is also why sizing is unaffected.
/// </summary>
/// <remarks>
/// Off by default: raising the level rewrites the format information and can change the
/// chosen mask, so a default of on would silently change every existing symbol.
/// </remarks>
public class EccBoostTest
{
    // ---- options surface -------------------------------------------------------------

    [Test]
    public async Task BoostEccLevel_DefaultsToFalse_AndFalseIsIndistinguishableFromUnset()
    {
        await Assert.That(default(QRCodeGeneratorOptions).BoostEccLevel).IsFalse();
        await Assert.That(new QRCodeGeneratorOptions { BoostEccLevel = false }).IsEqualTo(default(QRCodeGeneratorOptions));
        await Assert.That(new QRCodeGeneratorOptions { BoostEccLevel = true }).IsNotEqualTo(default(QRCodeGeneratorOptions));
    }

    // ---- boost outcome per headroom class --------------------------------------------

    // Alphanumeric capacities (ISO/IEC 18004 Table 7):
    //   v1: L 25, M 20, Q 16, H 10
    //   v2: L 47, M 38, Q 29, H 20
    public static IEnumerable<(string text, QREccLevel requested, QREccLevel expectedBoosted, int expectedVersion)> BoostCases()
    {
        yield return ("HELLO", QREccLevel.L, QREccLevel.H, 1);              // headroom all the way to H (5 <= 10)
        yield return ("HELLO", QREccLevel.M, QREccLevel.H, 1);
        yield return ("HELLO", QREccLevel.Q, QREccLevel.H, 1);
        yield return ("HELLO", QREccLevel.H, QREccLevel.H, 1);              // already at the top: no-op
        yield return (new string('A', 33), QREccLevel.L, QREccLevel.M, 2);  // M fits v2 (38 >= 33), Q (29) does not
        yield return (new string('A', 40), QREccLevel.L, QREccLevel.L, 2);  // no headroom at all (M 38 < 40)
    }

    [Test]
    [MethodDataSource(nameof(BoostCases))]
    public async Task Boost_RaisesTheLevelToTheVersionCapacity_WithoutChangingTheVersion(string text, QREccLevel requested, QREccLevel expectedBoosted, int expectedVersion)
    {
        var unboosted = QRCodeGenerator.Create(text, requested, new QRCodeGeneratorOptions());
        var boosted = QRCodeGenerator.Create(text, requested, new QRCodeGeneratorOptions { BoostEccLevel = true });

        await Assert.That(boosted.Version).IsEqualTo(expectedVersion);
        await Assert.That(boosted.Version).IsEqualTo(unboosted.Version);

        await Assert.That(QRCodeDecoder.TryDecode(boosted, out var decoded, out var info)).IsTrue();
        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.EccLevel).IsEqualTo(expectedBoosted);
    }

    [Test]
    [MethodDataSource(nameof(BoostCases))]
    public async Task Boost_DestinationOverload_ProducesTheSameSymbolAsTheAllocatingOverload(string text, QREccLevel requested, QREccLevel expectedBoosted, int expectedVersion)
    {
        var options = new QRCodeGeneratorOptions { BoostEccLevel = true };
        var boosted = QRCodeGenerator.Create(text, requested, options);

        // The released overload at the resolved (version, level) pair is the reference:
        // boost is only a resolution step, not a different encoding.
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text.AsSpan(), requested, out var size, options)).IsTrue();
        var fromOptions = new byte[size.BufferSize];
        var fromReleased = new byte[size.BufferSize];

        var optionsWritten = QRCodeGenerator.Create(text.AsSpan(), requested, fromOptions, options);
        var releasedWritten = QRCodeGenerator.Create(text.AsSpan(), expectedBoosted, fromReleased, new QRCodeGeneratorOptions { Version = expectedVersion });

        await Assert.That(optionsWritten).IsEqualTo(releasedWritten);
        await Assert.That(fromOptions).IsEquivalentTo(fromReleased);

        await Assert.That(boosted.GetRawData().AsSpan().SequenceEqual(
            QRCodeGenerator.Create(text, expectedBoosted, new QRCodeGeneratorOptions { Version = expectedVersion }).GetRawData())).IsTrue();
    }

    // ---- interaction with the version range ------------------------------------------

    [Test]
    public async Task Boost_PinnedVersion_UsesThePaddingOfThatVersion()
    {
        // Version 10 is far larger than "HELLO" needs, so every level fits: boost lands on H.
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(10), BoostEccLevel = true };
        var data = QRCodeGenerator.Create("HELLO", QREccLevel.L, options);

        await Assert.That(data.Version).IsEqualTo(10);
        await Assert.That(QRCodeDecoder.TryDecode(data, out _, out var info)).IsTrue();
        await Assert.That(info.EccLevel).IsEqualTo(QREccLevel.H);
    }

    [Test]
    public async Task Boost_ConstrainedRange_BoostsWithinTheChosenVersion()
    {
        var options = new QRCodeGeneratorOptions { Version = QRVersionRange.AtLeast(5), BoostEccLevel = true };
        var data = QRCodeGenerator.Create("HELLO", QREccLevel.L, options);

        await Assert.That(data.Version).IsEqualTo(5);
        await Assert.That(QRCodeDecoder.TryDecode(data, out _, out var info)).IsTrue();
        await Assert.That(info.EccLevel).IsEqualTo(QREccLevel.H);
    }

    // ---- error contract is unchanged by boost ----------------------------------------

    [Test]
    public async Task Boost_ContentThatDoesNotFitTheRange_ReportsTheSameErrorAsWithoutBoost()
    {
        var tooLong = new string('A', 50);   // does not fit version 1 at any level

        var withoutBoost = Assert.Throws<ArgumentException>(() => QRCodeGenerator.Create(
            tooLong, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1) }));
        var withBoost = Assert.Throws<ArgumentException>(() => QRCodeGenerator.Create(
            tooLong, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(1), BoostEccLevel = true }));

        await Assert.That(withBoost!.Message).IsEqualTo(withoutBoost!.Message);
        await Assert.That(withBoost.ParamName).IsEqualTo(withoutBoost.ParamName);
    }

    [Test]
    public async Task Boost_ContentThatDoesNotFitAnyVersion_ReportsTheSameErrorAsWithoutBoost()
    {
        // 5000 alphanumeric characters exceed the version 40 L capacity (4296). The
        // unconstrained overflow is InvalidOperationException on every existing path,
        // and turning boost on must not reclassify it as an argument error.
        var tooLong = new string('A', 5000);

        var withoutBoost = Assert.Throws<InvalidOperationException>(() => QRCodeGenerator.Create(
            tooLong, QREccLevel.L, new QRCodeGeneratorOptions()));
        var withBoost = Assert.Throws<InvalidOperationException>(() => QRCodeGenerator.Create(
            tooLong, QREccLevel.L, new QRCodeGeneratorOptions { BoostEccLevel = true }));

        await Assert.That(withBoost!.Message).IsEqualTo(withoutBoost!.Message);
    }

    [Test]
    public async Task Boost_NegativeQuietZone_IsStillTheFirstErrorReported()
    {
        // Same precedence contract as ArgumentErrorPrecedenceTest: the argument error
        // wins over the fit, boost or not.
        var options = new QRCodeGeneratorOptions { QuietZoneSize = -1, BoostEccLevel = true };

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => QRCodeGenerator.Create("HELLO", QREccLevel.L, options));
        await Assert.That(error!.ParamName).IsEqualTo("quietZoneSize");
    }

    // ---- sizing is indifferent to boost ----------------------------------------------

    [Test]
    [MethodDataSource(nameof(BoostCases))]
    public async Task Boost_DoesNotChangeTheRequiredBufferSize(string text, QREccLevel requested, QREccLevel expectedBoosted, int expectedVersion)
    {
        _ = expectedBoosted;
        _ = expectedVersion;

        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text.AsSpan(), requested, out var without, new QRCodeGeneratorOptions())).IsTrue();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(text.AsSpan(), requested, out var with, new QRCodeGeneratorOptions { BoostEccLevel = true })).IsTrue();

        await Assert.That(with).IsEqualTo(without);
    }

    // ---- boost off keeps the released symbol -----------------------------------------

    [Test]
    [Arguments("HELLO", QREccLevel.L)]
    [Arguments("HELLO WORLD 123", QREccLevel.M)]
    [Arguments("日本語のテキスト", QREccLevel.Q)]
    public async Task Boost_Off_ProducesTheReleasedOverloadSymbol(string text, QREccLevel ecc)
    {
        var released = QRCodeGenerator.Create(text, ecc);
        var viaOptions = QRCodeGenerator.Create(text, ecc, new QRCodeGeneratorOptions { BoostEccLevel = false });

        await Assert.That(viaOptions.GetRawData().AsSpan().SequenceEqual(released.GetRawData())).IsTrue();
    }

    // ---- round trip ------------------------------------------------------------------

    [Test]
    [Arguments("https://example.com/path?q=1")]
    [Arguments("Café déjà vu")]
    [Arguments("日本語のテキスト")]
    public async Task Boost_RoundTrips(string text)
    {
        var data = QRCodeGenerator.Create(text, QREccLevel.L, new QRCodeGeneratorOptions { BoostEccLevel = true });

        await Assert.That(QRCodeDecoder.TryDecode(data, out var decoded, out var info)).IsTrue();
        await Assert.That(decoded).IsEqualTo(text);
        await Assert.That(info.EccLevel >= QREccLevel.L).IsTrue();
        await Assert.That(data.Version).IsEqualTo(QRCodeGenerator.Create(text, QREccLevel.L).Version);
    }

    // ---- image builder ---------------------------------------------------------------

    [Test]
    public async Task Builder_WithErrorCorrectionBoost_MatchesThePreBoostedData()
    {
        var boostedData = QRCodeGenerator.Create("HELLO", QREccLevel.L, new QRCodeGeneratorOptions { BoostEccLevel = true });

        var viaBuilder = new QRCodeImageBuilder("HELLO")
            .WithErrorCorrection(QREccLevel.L)
            .WithErrorCorrectionBoost()
            .ToByteArray();
        var viaData = new QRCodeImageBuilder(boostedData).ToByteArray();

        await Assert.That(viaBuilder).IsEquivalentTo(viaData);
    }

    [Test]
    public async Task Builder_WithoutBoost_KeepsTheRequestedLevel()
    {
        var unboostedData = QRCodeGenerator.Create("HELLO", QREccLevel.L);

        var viaBuilder = new QRCodeImageBuilder("HELLO")
            .WithErrorCorrection(QREccLevel.L)
            .ToByteArray();
        var viaData = new QRCodeImageBuilder(unboostedData).ToByteArray();

        await Assert.That(viaBuilder).IsEquivalentTo(viaData);
    }
}
