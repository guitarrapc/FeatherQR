using FeatherQR.SkiaSharp;
namespace FeatherQR.Tests;

/// <summary>
/// <see cref="MicroQRCodeGeneratorOptions.MaskPattern"/>: pinning one of the four
/// Micro QR data mask patterns (ISO/IEC 18004 Table 10) instead of the automatic
/// edge-score selection. Any pattern is a legal symbol, so a pinned pattern must
/// round-trip through the decoder and report itself in the format information.
/// The Standard QR counterpart lives in MaskPatternOverrideTest; tier-by-tier
/// matrix parity for pinned patterns lives in MicroQRModulePlacerParityTest.
/// </summary>
public class MicroQRMaskPatternOverrideTest
{
    private const string Content = "12345";

    // ---- pinned pattern is honored ---------------------------------------------------

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PinnedMask_IsWrittenToFormatInformation_AndRoundTrips(int maskPattern)
    {
        var qr = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { MaskPattern = maskPattern });

        await Assert.That(MicroQRCodeDecoder.TryDecode(qr, out var decoded, out var info)).IsTrue().Because($"mask={maskPattern}, status={info.Status}");
        await Assert.That(decoded).IsEqualTo(Content);
        await Assert.That(info.MaskPattern).IsEqualTo(maskPattern);
    }

    public static IEnumerable<(string content, MicroQREccLevel ecc, MicroQRVersion version, int maskPattern)> VersionMaskCombinations()
    {
        // One (content, ECC) per version, all legal: M1 is numeric + error
        // detection only; each version has a different symbol size, so all four
        // fused placement sizes are exercised.
        var perVersion = new (string content, MicroQREccLevel ecc, MicroQRVersion version)[]
        {
            ("12345", MicroQREccLevel.ErrorDetectionOnly, MicroQRVersion.M1),
            ("1234567", MicroQREccLevel.L, MicroQRVersion.M2),
            ("AC-42", MicroQREccLevel.M, MicroQRVersion.M3),
            ("hello", MicroQREccLevel.Q, MicroQRVersion.M4),
        };

        foreach (var (content, ecc, version) in perVersion)
        {
            for (var mask = 0; mask < 4; mask++)
            {
                yield return (content, ecc, version, mask);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(VersionMaskCombinations))]
    public async Task PinnedMask_AtPinnedVersion_RoundTrips(string content, MicroQREccLevel ecc, MicroQRVersion version, int maskPattern)
    {
        var options = new MicroQRCodeGeneratorOptions { Version = version, MaskPattern = maskPattern };
        var qr = MicroQRCodeGenerator.Create(content, ecc, options);

        await Assert.That(MicroQRCodeDecoder.TryDecode(qr, out var decoded, out var info)).IsTrue().Because($"v={version}, mask={maskPattern}, status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);
        await Assert.That(info.Version).IsEqualTo(version);
        await Assert.That(info.MaskPattern).IsEqualTo(maskPattern);
    }

    [Test]
    public async Task PinnedMask_MatchingTheAutomaticWinner_IsByteIdenticalToAutomatic()
    {
        var auto = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L, MicroQRCodeGeneratorOptions.Default);
        await Assert.That(MicroQRCodeDecoder.TryDecode(auto, out _, out var autoInfo)).IsTrue();

        var pinned = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { MaskPattern = autoInfo.MaskPattern });

        await Assert.That(pinned.GetRawData().AsSpan().SequenceEqual(auto.GetRawData())).IsTrue();
    }

    [Test]
    public async Task PinnedMask_DifferingFromTheAutomaticWinner_ProducesADifferentButValidSymbol()
    {
        var auto = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L, MicroQRCodeGeneratorOptions.Default);
        await Assert.That(MicroQRCodeDecoder.TryDecode(auto, out _, out var autoInfo)).IsTrue();

        var otherMask = (autoInfo.MaskPattern + 1) % 4;
        var pinned = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { MaskPattern = otherMask });

        await Assert.That(pinned.GetRawData().AsSpan().SequenceEqual(auto.GetRawData())).IsFalse();
        await Assert.That(MicroQRCodeDecoder.TryDecode(pinned, out var decoded, out var pinnedInfo)).IsTrue();
        await Assert.That(decoded).IsEqualTo(Content);
        await Assert.That(pinnedInfo.MaskPattern).IsEqualTo(otherMask);
    }

    // ---- default stays automatic -----------------------------------------------------

    [Test]
    public async Task UnsetMask_IsAutomatic_AndMatchesTheReleasedOverload()
    {
        var released = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L);
        var unset = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L, MicroQRCodeGeneratorOptions.Default);
        var explicitNull = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { MaskPattern = null });

        await Assert.That(unset.GetRawData().AsSpan().SequenceEqual(released.GetRawData())).IsTrue();
        await Assert.That(explicitNull.GetRawData().AsSpan().SequenceEqual(released.GetRawData())).IsTrue();
    }

    [Test]
    public async Task ExplicitNullMask_IsIndistinguishableFromUnset()
    {
        await Assert.That(new MicroQRCodeGeneratorOptions { MaskPattern = null }).IsEqualTo(default(MicroQRCodeGeneratorOptions));
    }

    // ---- destination overload --------------------------------------------------------

    [Test]
    public async Task PinnedMask_DestinationOverload_MatchesTheAllocatingOverload()
    {
        var options = new MicroQRCodeGeneratorOptions { MaskPattern = 2 };

        var allocating = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L, options);
        await Assert.That(MicroQRCodeDecoder.TryDecode(allocating, out _, out var allocatingInfo)).IsTrue();
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(Content.AsSpan(), MicroQREccLevel.L, out var size, options)).IsTrue();

        var buffer = new byte[size.BufferSize];
        var written = MicroQRCodeGenerator.Create(Content.AsSpan(), MicroQREccLevel.L, buffer, options);

        await Assert.That(written).IsEqualTo(size.BufferSize);
        await Assert.That(MicroQRCodeDecoder.TryDecode(buffer.AsSpan(0, written), size.Size, out var decoded, out var info)).IsTrue().Because($"status={info.Status}");
        await Assert.That(decoded).IsEqualTo(Content);
        await Assert.That(info.MaskPattern).IsEqualTo(2);
        await Assert.That(info.MaskPattern).IsEqualTo(allocatingInfo.MaskPattern);
    }

    // ---- argument errors -------------------------------------------------------------

    [Test]
    [Arguments(-1)]
    [Arguments(4)]
    [Arguments(8)]
    [Arguments(int.MinValue)]
    public async Task InvalidMask_IsRejectedWhenTheOptionsAreConstructed(int maskPattern)
    {
        // Micro QR has four patterns, so 4-7 (valid for Standard QR) are argument
        // errors here. As with Version: rejected at construction, not at use.
        await Assert.That(() => new MicroQRCodeGeneratorOptions { MaskPattern = maskPattern }).Throws<ArgumentOutOfRangeException>();
    }

    // ---- image builder ---------------------------------------------------------------

    [Test]
    public async Task Builder_WithMaskPattern_PinsTheMask()
    {
        using var bitmap = new MicroQRCodeImageBuilder(Content)
            .WithErrorCorrection(MicroQREccLevel.L)
            .WithMaskPattern(1)
            .WithModulePixelSize(10)
            .ToBitmap();

        await Assert.That(MicroQRCodeDecoder.TryDecode(bitmap, out var decoded, out var info)).IsTrue().Because($"status={info.Status}");
        await Assert.That(decoded).IsEqualTo(Content);
        await Assert.That(info.MaskPattern).IsEqualTo(1);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(4)]
    public async Task Builder_WithMaskPattern_OutOfRange_Throws(int maskPattern)
    {
        await Assert.That(() => new MicroQRCodeImageBuilder(Content).WithMaskPattern(maskPattern)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Builder_WithMaskPattern_OnPrebuiltData_Throws()
    {
        var qr = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.L);
        await Assert.That(() => new MicroQRCodeImageBuilder(qr).WithMaskPattern(1)).Throws<InvalidOperationException>();
    }
}
