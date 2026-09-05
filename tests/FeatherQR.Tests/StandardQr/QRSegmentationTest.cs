using SkiaSharp;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="QRCodeSegmentation.Optimal"/> end to end. The three properties every
/// case is held to are: the symbol never selects a larger version than the
/// single-mode default, it still decodes to the original content, and when it lands
/// on the same version as the single-mode default it is byte-for-byte the same
/// matrix (the blast radius bound the feature is designed around). The rest covers
/// the branches of the version decision: requested version or range present or
/// absent, each charset, ECC boost and mask pinning composition, and both
/// directions of "does not fit".
/// </summary>
public class QRCodeSegmentationTest
{
    /// <summary>
    /// Contents spanning the equivalence classes of the decision: single-mode only
    /// (no gain possible), mixed with a gain, mixed without a gain, every charset,
    /// and the degenerate lengths.
    /// </summary>
    public static IEnumerable<string> Corpus() =>
    [
        "0",
        "A",
        "a",
        "1234567890",
        "ABCDEFGHIJ",
        "abcdefghij",
        "HELLO WORLD 12345",
        "hello world 12345",
        "https://example.com/item?id=123456789012345678901234567890",
        "SN/2024-000123456789",
        "Order 12345 item 6789",
        "ABC-1234567890123456",
        "x1234567890123456789012345678901234567890",
        "1234567890123456789012345678901234567890x",
        "日本語1234567890",
        "éèê1234567890",
        "😀😁1234567890",
        // 64 / 65 / 73 / 74 characters: both sides of the plan buffer's
        // stack-to-pool boundary (64) and the DP parents boundary (73).
        "ABCD" + "123456789012345678901234567890123456789012345678901234567890",
        "ABCDE" + "123456789012345678901234567890123456789012345678901234567890",
        "abcdefgh" + "12345678901234567890123456789012345678901234567890123456789012345",
        "abcdefghi" + "12345678901234567890123456789012345678901234567890123456789012345",
        // three-run content: a digit island between Byte runs, so the optimal plan
        // switches modes twice (Byte / Numeric / Byte)
        "abcdefghijkl123456789012mnopqrstuvwx",
    ];

    [Test]
    public async Task Optimal_IsNotTheDefault()
    {
        await Assert.That(default(QRCodeGeneratorOptions).Segmentation).IsEqualTo(QRCodeSegmentation.Single);
        await Assert.That(QRCodeGeneratorOptions.Default.Segmentation).IsEqualTo(QRCodeSegmentation.Single);
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task Optimal_IsNeverLargerThanSingle_AndAlwaysRoundTrips(string content)
    {
        var single = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, QRCodeGeneratorOptions.Default);
        var optimal = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal });

        await Assert.That(optimal.Version).IsLessThanOrEqualTo(single.Version);

        await Assert.That(QRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task Optimal_SameVersionAsSingle_ProducesTheIdenticalMatrix(string content)
    {
        var single = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, QRCodeGeneratorOptions.Default);
        var optimal = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal });

        if (optimal.Version != single.Version)
            return; // a genuine gain; the round-trip test covers it

        await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData());
    }

    [Test]
    [Arguments("https://example.com/item?id=123456789012345678901234567890")]
    [Arguments("x12345678901234567890123456789012345678901234567890")]
    [Arguments("日本語12345678901234567890123456789012345678901234567890")]
    public async Task Optimal_MixedContent_SelectsSmallerVersionThanSingle(string content)
    {
        var single = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, QRCodeGeneratorOptions.Default);
        var optimal = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal });

        await Assert.That(optimal.Version).IsLessThan(single.Version);

        await Assert.That(QRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    [Arguments("1234567890")]
    [Arguments("HELLO WORLD")]
    [Arguments("こんにちは")]
    public async Task Optimal_SingleModeContent_KeepsTheSameVersion(string content)
    {
        var single = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, QRCodeGeneratorOptions.Default);
        var optimal = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal });

        await Assert.That(optimal.Version).IsEqualTo(single.Version);
        await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData());
    }

    [Test]
    public async Task Optimal_TooLongForEverySingleMode_StillEncodesWhenMixedFits()
    {
        // 5,500 characters overflow Byte mode at every version (v40-L holds 2,953),
        // but split into a Byte run and a Numeric run the stream fits version 40-L.
        var content = new string('x', 1000) + new string('1', 4500);

        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(content, ECCLevel.L, out _, QRCodeGeneratorOptions.Default)).IsFalse();

        var options = new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal };
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(content, ECCLevel.L, out var size, options)).IsTrue();

        var data = QRCodeGenerator.CreateQrCode(content, ECCLevel.L, options);
        await Assert.That(data.Version).IsEqualTo(size.Version);

        await Assert.That(QRCodeDecoder.TryDecode(data, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Optimal_WithBoost_RaisesTheLevelWhenThePlannedStreamHasHeadroom()
    {
        // Plan: Byte(1) + Numeric(40) = 20 + 148 = 168 bits. Version 2-M holds 224,
        // version 2-Q holds 176 (>= 168), version 2-H holds 128 (< 168), so the
        // boost must land exactly on Q without changing the version.
        var content = "x" + new string('1', 40);
        var plain = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal });
        var boosted = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal, BoostEccLevel = true });

        await Assert.That(plain.Version).IsEqualTo(2);
        await Assert.That(boosted.Version).IsEqualTo(2);

        await Assert.That(QRCodeDecoder.TryDecode(boosted, out var decoded, out var info)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
        await Assert.That(info.EccLevel).IsEqualTo(ECCLevel.Q);
    }

    [Test]
    public async Task Optimal_MidContentBom_EmitsTheSingleModeStream()
    {
        // The byte-segment decoder consumes a leading EF BB BF of every non-Latin-1
        // segment as a BOM; a split that relocates a mid-content U+FEFF to a run
        // start would silently drop it, so the planner must fall back to the
        // single-mode stream, where it sits interior and survives.
        var content = new string('1', 40) + "\uFEFF" + "a";
        var single = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, QRCodeGeneratorOptions.Default);
        var optimal = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal });

        await Assert.That(optimal.Version).IsEqualTo(single.Version);
        await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData());

        await Assert.That(QRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Optimal_LeadingBom_DecodesLikeSingle()
    {
        // A content-leading U+FEFF is stream-initial under Single too, so both arms
        // drop it on decode; the run-at-offset-0 exemption keeps the split allowed
        // and the two must decode identically (not necessarily to the input). The
        // version assertion is strict: the split genuinely wins here, so losing the
        // exemption (which silently falls back to the identical single-mode
        // symbol) fails this test instead of passing it vacuously.
        var content = "\uFEFF" + new string('1', 40) + "a";
        var single = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, QRCodeGeneratorOptions.Default);
        var optimal = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal });

        await Assert.That(optimal.Version).IsLessThan(single.Version);
        await Assert.That(QRCodeDecoder.TryDecode(single, out var singleDecoded)).IsTrue();
        await Assert.That(QRCodeDecoder.TryDecode(optimal, out var optimalDecoded)).IsTrue();
        await Assert.That(optimalDecoded).IsEqualTo(singleDecoded);
    }

    [Test]
    public async Task Optimal_OverflowWhereEveryCheckedPlanIsMisread_ReportsDoesNotFit()
    {
        // No single mode holds 5,001 characters, and the minimal-bit plan puts the
        // trailing U+FEFF at a Byte-run start, where the decoder would consume it;
        // the documented outcome is "does not fit" rather than a corrupted symbol.
        var content = new string('1', 5000) + "\uFEFF";
        var options = new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal };

        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(content, ECCLevel.L, out _, options)).IsFalse();
        Assert.Throws<InvalidOperationException>(() => QRCodeGenerator.CreateQrCode(content, ECCLevel.L, options));

        // The sibling without the BOM is rescued, proving the refusal is BOM-driven.
        var sibling = new string('1', 5000) + "a";
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(sibling, ECCLevel.L, out _, options)).IsTrue();
    }

#if !DEBUG
    /// <summary>
    /// The span destination is the library's zero-allocation path; the mixed-mode
    /// planner must keep it that way, pooled branches included (the plan buffer
    /// rents above 64 characters, the parent table above 73). Debug builds are
    /// excluded per repo notes.
    /// </summary>
    /// <remarks>
    /// Runs alone. Pooled branches take their buffers from <see cref="System.Buffers.ArrayPool{T}"/>,
    /// whose per-core stacks are shared with every other thread: a test running concurrently can
    /// empty the bucket between this thread's return and its next rent, and the rent that would
    /// have hit the cache allocates a fresh array instead. That shows up here as a multiple of one
    /// array (measured: 1,048 bytes and up, in a full parallel run roughly one time in three) and it
    /// is the pool being a shared resource, not the encoder allocating. Exclusivity is the fix rather
    /// than a tolerance, which would blunt the guard this test exists to be.
    /// </remarks>
    [Test]
    [NotInParallel]
    public async Task Optimal_SpanDestination_IsAllocationFree()
    {
        var url = "https://example.com/item?id=123456789012345678901234567890";
        var longMixed = new string('a', 100) + new string('1', 100);   // over both stack budgets, so the planner rents
        var utf8 = "日本語1234567890123456789012345678901234567890";
        var buffer = new byte[40_000];
        var options = new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal };

        for (var i = 0; i < 3; i++)
        {
            QRCodeGenerator.CreateQrCode(url.AsSpan(), ECCLevel.M, buffer, options);
            QRCodeGenerator.CreateQrCode(longMixed.AsSpan(), ECCLevel.M, buffer, options);
            QRCodeGenerator.CreateQrCode(utf8.AsSpan(), ECCLevel.M, buffer, options);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 16; i++)
        {
            QRCodeGenerator.CreateQrCode(url.AsSpan(), ECCLevel.M, buffer, options);
            QRCodeGenerator.CreateQrCode(longMixed.AsSpan(), ECCLevel.M, buffer, options);
            QRCodeGenerator.CreateQrCode(utf8.AsSpan(), ECCLevel.M, buffer, options);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0);
    }
#endif

    [Test]
    public async Task Optimal_EmptyContent_MatchesSingle()
    {
        var single = QRCodeGenerator.CreateQrCode("", ECCLevel.M, QRCodeGeneratorOptions.Default);
        var optimal = QRCodeGenerator.CreateQrCode("", ECCLevel.M, new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal });

        await Assert.That(optimal.Version).IsEqualTo(single.Version);
        await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData());
    }

    [Test]
    [Arguments("HELLO WORLD 1234567890123456789012345678901234567890")]
    [Arguments("Order 12345 item 6789 ref 0000111122223333")]
    public async Task Optimal_ExplicitUtf8Eci_RoundTripsAndNeverGrows(string content)
    {
        // An explicitly requested UTF-8 charset forces the 12-bit ECI prefix into
        // the planned cost; the split must still round-trip and never grow.
        var options = new QRCodeGeneratorOptions { EciMode = EciMode.Utf8, Segmentation = QRCodeSegmentation.Optimal };
        var single = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { EciMode = EciMode.Utf8 });
        var optimal = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, options);

        await Assert.That(optimal.Version).IsLessThanOrEqualTo(single.Version);
        await Assert.That(QRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);

        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(content, ECCLevel.M, out var size, options)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(optimal.Version);
    }

    [Test]
    [Arguments("Café 1234567890123456789012345678901234567890")]
    [Arguments("éèê12345678901234567890")]
    public async Task Optimal_ExplicitIso88591Eci_RoundTripsAndNeverGrows(string content)
    {
        var options = new QRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1, Segmentation = QRCodeSegmentation.Optimal };
        var single = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1 });
        var optimal = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, options);

        await Assert.That(optimal.Version).IsLessThanOrEqualTo(single.Version);
        await Assert.That(QRCodeDecoder.TryDecode(optimal, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);

        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(content, ECCLevel.M, out var size, options)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(optimal.Version);
    }

    [Test]
    public async Task Optimal_WithBoost_KeepsTheVersionAndNeverLowersTheLevel()
    {
        var content = "https://example.com/item?id=123456789012345678901234567890";
        var plain = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal });
        var boosted = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal, BoostEccLevel = true });

        await Assert.That(boosted.Version).IsEqualTo(plain.Version);

        await Assert.That(QRCodeDecoder.TryDecode(boosted, out var decoded, out var info)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
        await Assert.That(info.EccLevel >= ECCLevel.M).IsTrue().Because($"boost must not lower the level, got {info.EccLevel}");
    }

    [Test]
    public async Task Optimal_WithPinnedMask_UsesThatMask()
    {
        var content = "https://example.com/item?id=123456789012345678901234567890";
        var data = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal, MaskPattern = 3 });

        await Assert.That(QRCodeDecoder.TryDecode(data, out var decoded, out var info)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
        await Assert.That(info.MaskPattern).IsEqualTo(3);
    }

    [Test]
    public async Task Optimal_WithUtf8Bom_EmitsTheSingleModeStream()
    {
        // The BOM is a stream-level prefix; a split would relocate it into the middle
        // of the decoded text, so the combination falls back to the single-mode stream.
        var content = "日本語1234567890123456789012345678901234567890";
        var single = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Utf8BOM = true });
        var optimal = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Utf8BOM = true, Segmentation = QRCodeSegmentation.Optimal });

        await Assert.That(optimal.Version).IsEqualTo(single.Version);
        await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData());
    }

    [Test]
    public async Task Optimal_VersionRangeBelowTheGain_EmitsTheSingleModeSymbol()
    {
        // The single-mode fit is version 4 and the mixed-mode fit version 3; a range
        // that starts at 4 excludes the gain, so the single-mode stream is emitted.
        var content = "https://example.com/item?id=123456789012345678901234567890";
        var single = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Version = new QRCodeVersionRange(4, 40) });
        var optimal = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Version = new QRCodeVersionRange(4, 40), Segmentation = QRCodeSegmentation.Optimal });

        await Assert.That(optimal.Version).IsEqualTo(single.Version);
        await Assert.That(optimal.GetRawData()).IsEquivalentTo(single.GetRawData());
    }

    [Test]
    public async Task Optimal_RequestedVersion_FitsWhereSingleModeDoesNot()
    {
        // At version 3-M the single Byte-mode stream overflows, but the mixed plan
        // fits; requesting that exact version must succeed under Optimal.
        var content = "https://example.com/item?id=123456789012345678901234567890";
        Assert.Throws<ArgumentException>(() => QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Version = 3 }));

        var data = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Version = 3, Segmentation = QRCodeSegmentation.Optimal });
        await Assert.That(data.Version).IsEqualTo(3);

        await Assert.That(QRCodeDecoder.TryDecode(data, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Optimal_RequestedVersion_TooSmallEvenSegmented_ThrowsLikeSingle()
    {
        var content = "https://example.com/item?id=123456789012345678901234567890";
        var exception = Assert.Throws<ArgumentException>(() => QRCodeGenerator.CreateQrCode(content, ECCLevel.M, new QRCodeGeneratorOptions { Version = 1, Segmentation = QRCodeSegmentation.Optimal }));
        await Assert.That(exception.Message).Contains("does not fit");
    }

    [Test]
    public async Task Optimal_TooLongForEveryVersion_ThrowsLikeSingle()
    {
        // 8,000 characters exceed even the all-Numeric maximum (7,089), so no plan
        // exists; the error must be the single-mode path's exact exception type.
        var content = new string('1', 7000) + new string('x', 1000);
        Assert.Throws<InvalidOperationException>(() => QRCodeGenerator.CreateQrCode(content, ECCLevel.L, new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal }));
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(content, ECCLevel.L, out _, new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal })).IsFalse();
    }

    [Test]
    public async Task Optimal_InvalidSegmentationValue_Throws_OnEveryEntryPoint()
    {
        var options = new QRCodeGeneratorOptions { Segmentation = (QRCodeSegmentation)5 };
        var buffer = new byte[1024];

        // ParamName matches the rMQR generator and the builder, so the three surfaces
        // report the same argument for the same mistake.
        var fromCreate = Assert.Throws<ArgumentOutOfRangeException>(() => QRCodeGenerator.CreateQrCode("HELLO", ECCLevel.M, options));
        var fromCreateSpan = Assert.Throws<ArgumentOutOfRangeException>(() => QRCodeGenerator.CreateQrCode("HELLO".AsSpan(), ECCLevel.M, buffer, options));
        var fromSizing = Assert.Throws<ArgumentOutOfRangeException>(() => QRCodeGenerator.TryGetRequiredBufferSize("HELLO", ECCLevel.M, out _, options));

        await Assert.That(fromCreate.ParamName).IsEqualTo("segmentation");
        await Assert.That(fromCreateSpan.ParamName).IsEqualTo("segmentation");
        await Assert.That(fromSizing.ParamName).IsEqualTo("segmentation");
    }

    [Test]
    public async Task Optimal_NegativeQuietZone_WinsOverInvalidSegmentation_OnEveryEntryPoint()
    {
        // The quiet zone is validated before the segmentation value on every surface,
        // matching TryGetRequiredBufferSize and the rMQR generator, so a caller moving
        // between entry points debugs the same error first.
        var options = new QRCodeGeneratorOptions { Segmentation = (QRCodeSegmentation)3, QuietZoneSize = -1 };
        var buffer = new byte[1024];

        var fromCreate = Assert.Throws<ArgumentOutOfRangeException>(() => QRCodeGenerator.CreateQrCode("HELLO", ECCLevel.M, options));
        var fromCreateSpan = Assert.Throws<ArgumentOutOfRangeException>(() => QRCodeGenerator.CreateQrCode("HELLO".AsSpan(), ECCLevel.M, buffer, options));
        var fromSizing = Assert.Throws<ArgumentOutOfRangeException>(() => QRCodeGenerator.TryGetRequiredBufferSize("HELLO", ECCLevel.M, out _, options));

        await Assert.That(fromCreate.ParamName).IsEqualTo("quietZoneSize");
        await Assert.That(fromCreateSpan.ParamName).IsEqualTo("quietZoneSize");
        await Assert.That(fromSizing.ParamName).IsEqualTo("quietZoneSize");
    }

    [Test]
    public async Task Optimal_Utf8BomOverNonByteContent_StillSplits()
    {
        // The BOM is written only into UTF-8 Byte-mode streams. Content whose single
        // mode is Alphanumeric never carries one, even with Utf8BOM requested over an
        // explicit UTF-8 charset, so suppressing the split there would forgo a smaller
        // symbol for nothing.
        var content = "ABCDEFGHIJ" + new string('1', 50);
        var options = new QRCodeGeneratorOptions { EciMode = EciMode.Utf8, Utf8BOM = true, Segmentation = QRCodeSegmentation.Optimal };
        var single = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, options with { Segmentation = QRCodeSegmentation.Single });
        var withoutBom = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, options with { Utf8BOM = false });
        var withBom = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, options);

        // The split must actually happen (self-verifying against a capacity-table
        // change that could otherwise make this test vacuous), and the BOM flag must
        // not disturb it.
        await Assert.That(withBom.Version).IsLessThan(single.Version);
        await Assert.That(withBom.Version).IsEqualTo(withoutBom.Version);
        await Assert.That(withBom.GetRawData()).IsEquivalentTo(withoutBom.GetRawData());

        await Assert.That(QRCodeDecoder.TryDecode(withBom, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);

        // Sizing must agree with the encode under the same options.
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(content, ECCLevel.M, out var size, options)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(withBom.Version);
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task Optimal_SpanDestination_MatchesTheAllocatingOverload(string content)
    {
        var options = new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal, QuietZoneSize = 0 };
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(content, ECCLevel.M, out var size, options)).IsTrue();

        var buffer = new byte[size.BufferSize];
        var written = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, buffer, options);
        await Assert.That(written).IsEqualTo(size.BufferSize);

        AssertSameModules(QRCodeGenerator.CreateQrCode(content, ECCLevel.M, options), buffer, size.QrSize);

        await Assert.That(QRCodeDecoder.TryDecode(buffer.AsSpan(0, written), size.QrSize, out var decoded, out var info)).IsTrue().Because($"status={info.Status}");
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task Optimal_SpanDestination_WithQuietZone_MatchesTheAllocatingOverload(string content)
    {
        // The quiet-zone branch of the span path (clear + centered row copies) must
        // agree with the allocating overload module for module, quiet zone included.
        var options = new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal, QuietZoneSize = 4 };
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(content, ECCLevel.M, out var size, options)).IsTrue();

        var buffer = new byte[size.BufferSize];
        var written = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, buffer, options);
        await Assert.That(written).IsEqualTo(size.BufferSize);

        AssertSameModules(QRCodeGenerator.CreateQrCode(content, ECCLevel.M, options), buffer, size.QrSize);
    }

    private static void AssertSameModules(QRCodeData expected, ReadOnlySpan<byte> actual, int size)
    {
        if (expected.Size != size)
            throw new InvalidOperationException($"matrix size mismatch: {expected.Size} vs {size}");
        for (var row = 0; row < size; row++)
        {
            for (var col = 0; col < size; col++)
            {
                if (expected[row, col] != (actual[row * size + col] != 0))
                    throw new InvalidOperationException($"module mismatch at ({row}, {col})");
            }
        }
    }

    [Test]
    public async Task Builder_WithSegmentation_ProducesADecodableImage()
    {
        var content = "https://example.com/item?id=123456789012345678901234567890";
        var png = new QRCodeImageBuilder(content)
            .WithSegmentation(QRCodeSegmentation.Optimal)
            .ToByteArray();

        using var bitmap = SKBitmap.Decode(png);
        await Assert.That(QRCodeDecoder.TryDecode(bitmap, out var decoded)).IsTrue();
        await Assert.That(decoded).IsEqualTo(content);
    }

    [Test]
    public async Task Builder_WithSegmentation_RejectsInvalidValueAndPrebuiltData()
    {
        await Assert.That(() => new QRCodeImageBuilder("HELLO").WithSegmentation((QRCodeSegmentation)5)).Throws<ArgumentOutOfRangeException>();

        var data = QRCodeGenerator.CreateQrCode("HELLO", ECCLevel.M, QRCodeGeneratorOptions.Default);
        await Assert.That(() => new QRCodeImageBuilder(data).WithSegmentation(QRCodeSegmentation.Optimal)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Optimal_AgreesWithTryGetRequiredBufferSize()
    {
        foreach (var content in Corpus())
        {
            var options = new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal };
            await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(content, ECCLevel.M, out var size, options)).IsTrue();

            var buffer = new byte[size.BufferSize];
            var written = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, buffer, options);
            await Assert.That(written).IsEqualTo(size.BufferSize);

            var data = QRCodeGenerator.CreateQrCode(content, ECCLevel.M, options);
            await Assert.That(data.Version).IsEqualTo(size.Version);
        }
    }
}
