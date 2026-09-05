using FeatherQR.SkiaSharp;
using SkiaSharp;
using ZXing;
using ZXing.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="QRCodeGeneratorOptions.MaskPattern"/>: pinning one of the eight
/// ISO/IEC 18004 data mask patterns instead of the automatic penalty-scored
/// selection. Any pattern is a legal symbol (the spec only recommends the best
/// scorer), so a pinned pattern must round-trip through the decoder, report
/// itself in the format information, and stay readable by an independent reader.
/// </summary>
public class MaskPatternOverrideTest
{
    private const string Content = "HELLO WORLD 123";

    // ---- pinned pattern is honored ---------------------------------------------------

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    public async Task PinnedMask_IsWrittenToFormatInformation_AndRoundTrips(int maskPattern)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { MaskPattern = maskPattern });

        await Assert.That(QRCodeDecoder.TryDecode(qr, out var decoded, out var info)).IsTrue().Because($"mask={maskPattern}, status={info.Status}");
        await Assert.That(decoded).IsEqualTo(Content);
        await Assert.That(info.MaskPattern).IsEqualTo(maskPattern);
        await Assert.That(info.ErrorsCorrected).IsEqualTo(0);
    }

    public static IEnumerable<(int version, int maskPattern)> VersionMaskCombinations()
    {
        // Version 1-11 and 12-40 are separate masking kernels (single-word vs
        // triple-word rows), and version 7+ adds version information blocks.
        foreach (var version in new[] { 1, 7, 15 })
        {
            for (var mask = 0; mask < 8; mask++)
            {
                yield return (version, mask);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(VersionMaskCombinations))]
    public async Task PinnedMask_AtPinnedVersion_RoundTrips(int version, int maskPattern)
    {
        var options = new QRCodeGeneratorOptions { Version = version, MaskPattern = maskPattern };
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, options);

        await Assert.That(QRCodeDecoder.TryDecode(qr, out var decoded, out var info)).IsTrue().Because($"v={version}, mask={maskPattern}, status={info.Status}");
        await Assert.That(decoded).IsEqualTo(Content);
        await Assert.That(info.Version).IsEqualTo(version);
        await Assert.That(info.MaskPattern).IsEqualTo(maskPattern);
        await Assert.That(info.ErrorsCorrected).IsEqualTo(0);
    }

    [Test]
    [Arguments(1)]
    [Arguments(15)]
    public async Task PinnedMask_MatchingTheAutomaticWinner_IsByteIdenticalToAutomatic(int version)
    {
        // Pinning the pattern the scorer would pick must reproduce the automatic
        // symbol exactly: the pinned application path and MaskCode's winner
        // application are the same transformation.
        var auto = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = version });
        await Assert.That(QRCodeDecoder.TryDecode(auto, out _, out var autoInfo)).IsTrue();

        var pinned = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = version, MaskPattern = autoInfo.MaskPattern });

        await Assert.That(pinned.GetRawData().AsSpan().SequenceEqual(auto.GetRawData())).IsTrue();
    }

    [Test]
    public async Task PinnedMask_DifferingFromTheAutomaticWinner_ProducesADifferentButValidSymbol()
    {
        var auto = QRCodeGenerator.Create(Content, QREccLevel.M, QRCodeGeneratorOptions.Default);
        await Assert.That(QRCodeDecoder.TryDecode(auto, out _, out var autoInfo)).IsTrue();

        var otherMask = (autoInfo.MaskPattern + 1) % 8;
        var pinned = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { MaskPattern = otherMask });

        await Assert.That(pinned.GetRawData().AsSpan().SequenceEqual(auto.GetRawData())).IsFalse();
        await Assert.That(QRCodeDecoder.TryDecode(pinned, out var decoded, out var pinnedInfo)).IsTrue();
        await Assert.That(decoded).IsEqualTo(Content);
        await Assert.That(pinnedInfo.MaskPattern).IsEqualTo(otherMask);
    }

    // ---- default stays automatic -----------------------------------------------------

    [Test]
    public async Task UnsetMask_IsAutomatic_AndMatchesTheReleasedOverload()
    {
        var released = QRCodeGenerator.Create(Content, QREccLevel.M);
        var unset = QRCodeGenerator.Create(Content, QREccLevel.M, QRCodeGeneratorOptions.Default);
        var explicitNull = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { MaskPattern = null });

        await Assert.That(unset.GetRawData().AsSpan().SequenceEqual(released.GetRawData())).IsTrue();
        await Assert.That(explicitNull.GetRawData().AsSpan().SequenceEqual(released.GetRawData())).IsTrue();
    }

    [Test]
    public async Task ExplicitNullMask_IsIndistinguishableFromUnset()
    {
        await Assert.That(new QRCodeGeneratorOptions { MaskPattern = null }).IsEqualTo(default(QRCodeGeneratorOptions));
    }

    // ---- destination overload --------------------------------------------------------

    [Test]
    public async Task PinnedMask_DestinationOverload_MatchesTheAllocatingOverload()
    {
        var options = new QRCodeGeneratorOptions { MaskPattern = 5 };

        var allocating = QRCodeGenerator.Create(Content, QREccLevel.M, options);
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(Content.AsSpan(), QREccLevel.M, out var size, options)).IsTrue();

        var buffer = new byte[size.BufferSize];
        var written = QRCodeGenerator.Create(Content.AsSpan(), QREccLevel.M, buffer, options);

        await Assert.That(written).IsEqualTo(size.BufferSize);
        await Assert.That(QRCodeDecoder.TryDecode(buffer.AsSpan(0, written), size.Size, out var decoded, out var info)).IsTrue();
        await Assert.That(decoded).IsEqualTo(Content);
        await Assert.That(info.MaskPattern).IsEqualTo(5);
        await Assert.That(info.MaskPattern).IsEqualTo(GetDecodedMask(allocating));
    }

    // ---- interaction with other options ----------------------------------------------

    [Test]
    public async Task PinnedMask_WithEccBoost_BoostsTheLevelAndKeepsTheMask()
    {
        // "HELLO" at L boosts to H within version 1; the pinned mask must survive
        // the rewritten format information.
        var qr = QRCodeGenerator.Create("HELLO", QREccLevel.L, new QRCodeGeneratorOptions { BoostEccLevel = true, MaskPattern = 6 });

        await Assert.That(QRCodeDecoder.TryDecode(qr, out var decoded, out var info)).IsTrue();
        await Assert.That(decoded).IsEqualTo("HELLO");
        await Assert.That(info.EccLevel).IsEqualTo(QREccLevel.H);
        await Assert.That(info.MaskPattern).IsEqualTo(6);
    }

    // ---- argument errors -------------------------------------------------------------

    [Test]
    [Arguments(-1)]
    [Arguments(8)]
    [Arguments(int.MinValue)]
    [Arguments(int.MaxValue)]
    public async Task InvalidMask_IsRejectedWhenTheOptionsAreConstructed(int maskPattern)
    {
        // As with Version: an impossible option set cannot be built at all, so no
        // per-entry-point validation order questions arise.
        await Assert.That(() => new QRCodeGeneratorOptions { MaskPattern = maskPattern }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(8)]
    [Arguments(int.MaxValue)]
    public async Task ApplyMaskPattern_OutOfRange_Throws(int patternIndex)
    {
        // Internal seam guard: without it, an out-of-range pattern silently
        // applies no mask (GetMaskBit's default arm) while the caller still
        // writes format information claiming that pattern.
        var layout = FeatherQR.Internals.StandardQR.ModulePlacer.GetLayout(1);
        var buffer = new byte[21 * 21];
        await Assert.That(() => FeatherQR.Internals.StandardQR.ModulePlacer.ApplyMaskPattern(buffer, 21, layout.BlockedMask, patternIndex))
            .Throws<ArgumentOutOfRangeException>();
    }

    // ---- image builder ---------------------------------------------------------------

    [Test]
    public async Task Builder_WithMaskPattern_PinsTheMask()
    {
        using var bitmap = new QRCodeImageBuilder(Content)
            .WithMaskPattern(3)
            .WithModulePixelSize(10)
            .ToBitmap();

        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out var decoded, out var info)).IsTrue().Because($"status={info.Status}");
        await Assert.That(decoded).IsEqualTo(Content);
        await Assert.That(info.MaskPattern).IsEqualTo(3);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(8)]
    public async Task Builder_WithMaskPattern_OutOfRange_Throws(int maskPattern)
    {
        await Assert.That(() => new QRCodeImageBuilder(Content).WithMaskPattern(maskPattern)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Builder_WithMaskPattern_OnPrebuiltData_Throws()
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M);
        await Assert.That(() => new QRCodeImageBuilder(qr).WithMaskPattern(3)).Throws<InvalidOperationException>();
    }

    // ---- independent reader ----------------------------------------------------------

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    public async Task PinnedMask_IsDecodableByZXing(int maskPattern)
    {
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { MaskPattern = maskPattern });

        var reader = new BarcodeReader
        {
            Options = new ZXing.Common.DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = new[] { BarcodeFormat.QR_CODE },
            }
        };
        using var bitmap = QrCodeToSKBitmap(qr);
        var result = reader.Decode(bitmap);

        await Assert.That(result).IsNotNull().Because($"ZXing could not read the mask-{maskPattern} symbol");
        await Assert.That(result!.Text).IsEqualTo(Content);
    }

    // helpers

    private static int GetDecodedMask(QRCodeData qr)
    {
        QRCodeDecoder.TryDecode(qr, out _, out var info);
        return info.MaskPattern;
    }

    private static SKBitmap QrCodeToSKBitmap(QRCodeData qr)
    {
        var size = qr.Size;
        var scale = 10;
        var bitmap = new SKBitmap(size * scale, size * scale);

        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint();
        canvas.Clear(SKColors.White);
        paint.Color = SKColors.Black;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (qr[y, x])
                {
                    canvas.DrawRect(x * scale, y * scale, scale, scale, paint);
                }
            }
        }

        return bitmap;
    }
}
