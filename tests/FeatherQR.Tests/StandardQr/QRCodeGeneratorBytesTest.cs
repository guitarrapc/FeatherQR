using System.Text;
using FeatherQR.Internals;
using FeatherQR.Internals.StandardQR;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The bytes calls of <see cref="QRCodeGenerator"/>: the bytes given are the bytes in the symbol, under the ECI assignment the caller names or under none.
/// The bytes are read back with <see cref="QRCodeDecoder.TryDecodeBytes(QRCodeData, out byte[], out QRCodeDecodeInfo)"/>, and the stream itself from the reference codeword extraction.
/// </summary>
public class QRCodeGeneratorBytesTest
{
    private static readonly QRCodeGeneratorOptions Single = new() { QuietZoneSize = 0, Segmentation = QRSegmentation.Single };
    private static readonly QRCodeGeneratorOptions Optimal = new() { QuietZoneSize = 0, Segmentation = QRSegmentation.Optimal };

    /// <summary>Every byte value once, in order: digits and uppercase letters in runs a plan can take into Numeric and Alphanumeric, and every other value beside them.</summary>
    private static byte[] EveryByteValue()
    {
        var payload = new byte[256];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)i;
        return payload;
    }

    // Round trip

    [Test]
    [Arguments(QRSegmentation.Single, -1)]
    [Arguments(QRSegmentation.Single, 899)]
    [Arguments(QRSegmentation.Optimal, -1)]
    [Arguments(QRSegmentation.Optimal, 899)]
    public async Task EveryByteValue_ReadsBackAsWritten(QRSegmentation segmentation, int assignment)
    {
        var payload = EveryByteValue();
        int? eci = assignment < 0 ? null : assignment;

        var data = QRCodeGenerator.Create(payload, QREccLevel.L, new QRCodeGeneratorOptions { Segmentation = segmentation }, eci);

        await Assert.That(QRCodeDecoder.TryDecodeBytes(data, out var bytes, out var info)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(payload, CollectionOrdering.Matching);
        await Assert.That(info.EciAssignment).IsEqualTo(eci);
    }

    /// <summary>
    /// Payloads of runs drawn from digits, uppercase letters and any byte, so plans mix all three modes, under assignments of every designator length.
    /// The span form writes the class form's modules, and sizing reports the version both use.
    /// Every payload fits, since 699 bytes is less than 40-H holds behind the longest header.
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RandomPayloads_ReadBackAsWritten_AndEveryFormAgrees(int seed)
    {
        int?[] assignments = [null, 0, 3, 26, 127, 128, 899, 16383, 16384, 999999];
        var random = new Random(seed);
        for (var trial = 0; trial < 40; trial++)
        {
            var payload = RunsPayload(random, random.Next(0, 700));
            var eci = assignments[random.Next(assignments.Length)];
            var level = (QREccLevel)random.Next(4);
            var options = new QRCodeGeneratorOptions { QuietZoneSize = 0, Segmentation = random.Next(2) == 0 ? QRSegmentation.Single : QRSegmentation.Optimal };
            var label = $"seed {seed} trial {trial}: {payload.Length} bytes, ECI {eci?.ToString() ?? "none"}, {level}, {options.Segmentation}";

            await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(payload, level, out var size, options, eci)).IsTrue().Because(label);
            var data = QRCodeGenerator.Create(payload, level, options, eci);
            await Assert.That(data.Version).IsEqualTo(size.Version).Because(label);

            await Assert.That(QRCodeDecoder.TryDecodeBytes(data, out var bytes, out var info)).IsTrue().Because(label);
            await Assert.That(bytes).IsEquivalentTo(payload, CollectionOrdering.Matching).Because(label);
            await Assert.That(info.EciAssignment).IsEqualTo(eci).Because(label);

            var destination = new byte[size.BufferSize];
            await Assert.That(QRCodeGenerator.Create(payload, level, destination, options, eci)).IsEqualTo(size.BufferSize).Because(label);
            await Assert.That(destination).IsEquivalentTo(Modules(data), CollectionOrdering.Matching).Because(label);
        }
    }

    /// <summary>An empty payload is a Byte segment of no bytes, declared or not, and reads back empty.</summary>
    [Test]
    [Arguments(-1)]
    [Arguments(899)]
    public async Task EmptyPayload_ReadsBackEmpty(int assignment)
    {
        int? eci = assignment < 0 ? null : assignment;

        var data = QRCodeGenerator.Create(ReadOnlySpan<byte>.Empty, QREccLevel.M, Single, eci);

        await Assert.That(data.Version).IsEqualTo(1);
        await Assert.That(QRCodeDecoder.TryDecodeBytes(data, out var bytes, out var info)).IsTrue();
        await Assert.That(bytes).IsEmpty();
        await Assert.That(info.EciAssignment).IsEqualTo(eci);
    }

    // What the stream holds

    [Test]
    public async Task Undeclared_TheStreamOpensWithByteMode_AndHoldsTheBytes()
    {
        var data = QRCodeGenerator.Create(new byte[] { 0x00, 0xFF, 0x80 }, QREccLevel.L, Single);

        await Assert.That(Convert.ToHexString(Codewords(data), 0, 5)).IsEqualTo("40300FF800")
            .Because("0100 Byte, the count 3, 00000000 11111111 10000000, 0000, and no ECI header");
    }

    /// <summary>The designator is one codeword up to 127, two up to 16383 and three up to 999999, marked by its leading 0, 10 or 110.</summary>
    [Test]
    [Arguments(0, "0" + "0000000")]
    [Arguments(127, "0" + "1111111")]
    [Arguments(128, "10" + "00000010000000")]
    [Arguments(899, "10" + "00001110000011")]
    [Arguments(16383, "10" + "11111111111111")]
    [Arguments(16384, "110" + "000000100000000000000")]
    [Arguments(999999, "110" + "011110100001000111111")]
    public async Task Declared_TheDesignatorTakesTheFormItsNumberNeeds(int assignment, string designator)
    {
        var data = QRCodeGenerator.Create(new byte[] { 0xFF }, QREccLevel.L, Single, assignment);

        var expected = "0111" + designator + "0100" + "00000001" + "11111111";
        await Assert.That(Bits(Codewords(data)).Substring(0, expected.Length)).IsEqualTo(expected)
            .Because("0111 ECI, the designator, 0100 Byte, the count 1, the byte");
        await Assert.That(QRCodeDecoder.TryDecodeBytes(data, out var bytes, out var info)).IsTrue();
        await Assert.That(info.EciAssignment).IsEqualTo(assignment);
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0xFF }, CollectionOrdering.Matching);
    }

    /// <summary>Every symbol of an Optimal plan under an assignment carries one header, ahead of the first run, and its runs read as the bytes.</summary>
    [Test]
    public async Task Declared_Optimal_OneHeaderAheadOfThePlannedRuns()
    {
        var payload = Encoding.ASCII.GetBytes("abc0123456789012345678901234567890xyz");
        var data = QRCodeGenerator.Create(payload, QREccLevel.L, Optimal, 899);

        var shape = Fnc1StreamShape.OfStandardQr(Modules(data), data.Size, data.Version, data.EccLevel, data.MaskPattern);
        await Assert.That(shape.Headers).IsEquivalentTo(new[] { "ECI:899" }, CollectionOrdering.Matching);
        await Assert.That(shape.DataModes).IsEquivalentTo(new[] { "Byte", "Numeric", "Byte" }, CollectionOrdering.Matching);
        await Assert.That(QRCodeDecoder.TryDecodeBytes(data, out var bytes, out _)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(payload, CollectionOrdering.Matching);
    }

    // Capacity

    /// <summary>The standard's Byte-mode capacities: a version's data bits less the mode and count indicators, in whole bytes.</summary>
    [Test]
    [Arguments(1, QREccLevel.L, 17)]
    [Arguments(1, QREccLevel.M, 14)]
    [Arguments(1, QREccLevel.Q, 11)]
    [Arguments(1, QREccLevel.H, 7)]
    [Arguments(10, QREccLevel.L, 271)]
    [Arguments(26, QREccLevel.L, 1367)]
    [Arguments(27, QREccLevel.L, 1465)]
    [Arguments(40, QREccLevel.L, 2953)]
    [Arguments(40, QREccLevel.M, 2331)]
    [Arguments(40, QREccLevel.Q, 1663)]
    [Arguments(40, QREccLevel.H, 1273)]
    public async Task ByteCapacity_IsTheStandardsTable(int version, QREccLevel level, int capacity)
        => await Assert.That(ByteCapacity(version, level, headerBits: 0)).IsEqualTo(capacity);

    public static IEnumerable<Func<(int Version, QREccLevel Level)>> EveryVersionAndLevel()
    {
        for (var version = 1; version <= 40; version++)
        {
            for (var level = QREccLevel.L; level <= QREccLevel.H; level++)
            {
                var (v, l) = (version, level);
                yield return () => (v, l);
            }
        }
    }

    /// <summary>Under single-segment encoding a version holds its Byte-mode capacity of bytes no other mode takes, and one more is refused.</summary>
    [Test]
    [MethodDataSource(nameof(EveryVersionAndLevel))]
    public async Task Undeclared_TheLargestPayloadAVersionHolds_IsItsByteCapacity(int version, QREccLevel level)
    {
        var capacity = ByteCapacity(version, level, headerBits: 0);
        var options = Single with { Version = version };

        var full = QRCodeGenerator.Create(Filled(capacity), level, options);
        await Assert.That(full.Version).IsEqualTo(version);
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(Filled(capacity), level, out var size, options)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(version);
        await Assert.That(() => QRCodeGenerator.Create(Filled(capacity + 1), level, options)).ThrowsExactly<ArgumentException>();
    }

    /// <summary>At 40-L the header's codewords come out of the 2,953 bytes one each: 12 bits leave 2,952, 20 bits 2,951 and 28 bits 2,950.</summary>
    [Test]
    [Arguments(-1, 2953)]
    [Arguments(127, 2952)]
    [Arguments(128, 2951)]
    [Arguments(16383, 2951)]
    [Arguments(16384, 2950)]
    public async Task Declared_TheDesignatorComesOutOfTheCapacity(int assignment, int capacity)
    {
        int? eci = assignment < 0 ? null : assignment;
        var options = Single with { Version = 40 };

        await Assert.That(QRCodeGenerator.Create(Filled(capacity), QREccLevel.L, options, eci).Version).IsEqualTo(40);
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(Filled(capacity), QREccLevel.L, out var size, options, eci)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(40);
        await Assert.That(() => QRCodeGenerator.Create(Filled(capacity + 1), QREccLevel.L, options, eci)).ThrowsExactly<ArgumentException>();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(Filled(capacity + 1), QREccLevel.L, out _, options, eci)).IsFalse();
    }

    /// <summary>
    /// 100 bytes no other mode takes, then digits: the cheapest plan is a Byte segment and a Numeric one, which the test prices from the standard's bit costs.
    /// At 10-L the most digits that plan fits behind each header are written at 10, and one more digit is refused.
    /// </summary>
    [Test]
    [Arguments(-1)]
    [Arguments(127)]
    [Arguments(128)]
    [Arguments(16384)]
    public async Task Optimal_TheDesignatorComesOutOfThePlansRoom(int assignment)
    {
        int? eci = assignment < 0 ? null : assignment;
        var headerBits = eci is { } number ? 4 + (number < 128 ? 8 : number < 16384 ? 16 : 24) : 0;
        var dataBits = QRCodeConstants.GetEccInfo(10, QREccLevel.L).TotalDataCodewords * 8;
        var bytePart = 4 + EncodingMode.Byte.GetCountIndicatorLength(10) + 100 * 8;
        var numericHeader = 4 + EncodingMode.Numeric.GetCountIndicatorLength(10);
        var digits = 0;
        while (headerBits + bytePart + numericHeader + NumericBits(digits + 1) <= dataBits)
            digits++;
        var options = Optimal with { Version = 10 };

        await Assert.That(QRCodeGenerator.Create(BytesThenDigits(digits), QREccLevel.L, options, eci).Version).IsEqualTo(10);
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(BytesThenDigits(digits), QREccLevel.L, out var size, options, eci)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(10);
        await Assert.That(() => QRCodeGenerator.Create(BytesThenDigits(digits + 1), QREccLevel.L, options, eci)).ThrowsExactly<ArgumentException>();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(BytesThenDigits(digits + 1), QREccLevel.L, out _, options, eci)).IsFalse();
    }

    /// <summary>At a pinned version every symbol of a set holds its Byte capacity behind the set header and the assignment's header: two symbols' worth is two symbols, one byte more is three.</summary>
    [Test]
    [Arguments(-1)]
    [Arguments(127)]
    [Arguments(128)]
    [Arguments(16384)]
    public async Task StructuredAppend_EachSymbolHoldsItsCapacityBehindBothHeaders(int assignment)
    {
        int? eci = assignment < 0 ? null : assignment;
        var headerBits = 20 + (eci is { } number ? 4 + (number < 128 ? 8 : number < 16384 ? 16 : 24) : 0);
        var perSymbol = ByteCapacity(5, QREccLevel.L, headerBits);
        var options = Single with { Version = 5 };

        await Assert.That(QRCodeGenerator.CreateStructuredAppend(Filled(2 * perSymbol), QREccLevel.L, options, eci).Length).IsEqualTo(2);
        await Assert.That(QRCodeGenerator.CreateStructuredAppend(Filled(2 * perSymbol + 1), QREccLevel.L, options, eci).Length).IsEqualTo(3);
    }

    /// <summary>Digits are a byte each in Numeric mode, so 7,089 of them fit 40-L though it holds 2,953 bytes in Byte mode, and 7,090 are refused.</summary>
    [Test]
    public async Task Digits_AreWrittenInNumericMode_SoTheyFitPastTheByteCapacity()
    {
        var digits = Enumerable.Repeat((byte)'7', 7089).ToArray();
        var data = QRCodeGenerator.Create(digits, QREccLevel.L);
        await Assert.That(data.Version).IsEqualTo(40);
        await Assert.That(QRCodeDecoder.TryDecodeBytes(data, out var bytes, out _)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(digits, CollectionOrdering.Matching);

        var tooMany = Enumerable.Repeat((byte)'7', 7090).ToArray();
        await Assert.That(() => QRCodeGenerator.Create(tooMany, QREccLevel.L)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(tooMany, QREccLevel.L, out _)).IsFalse();
    }

    /// <summary>Payloads too long for any symbol fail the way text does: unconstrained overflow is an <see cref="InvalidOperationException"/>, a narrowed range an <see cref="ArgumentException"/>.</summary>
    [Test]
    [Arguments(2954)]
    [Arguments(1_000_000)]
    public async Task TooLong_FailsAsTextDoes(int length)
    {
        var payload = Filled(length);

        await Assert.That(() => QRCodeGenerator.Create(payload, QREccLevel.L)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => QRCodeGenerator.Create(payload, QREccLevel.L, Optimal)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => QRCodeGenerator.Create(payload, QREccLevel.L, new byte[100], Single)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => QRCodeGenerator.Create(payload, QREccLevel.L, Single with { Version = QRVersionRange.AtMost(10) })).ThrowsExactly<ArgumentException>();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(payload, QREccLevel.L, out _)).IsFalse();
        await Assert.That(QRCodeGenerator.TryGetRequiredBufferSize(payload, QREccLevel.L, out _, Optimal)).IsFalse();
    }

    /// <summary>
    /// Sixteen 40-L symbols hold 47,216 bytes no mode but Byte takes, 2,951 each behind the set header.
    /// 1,000,000 bytes is past what sixteen symbols hold even in Numeric mode, so it is refused before the planner runs, and names the same argument the planner does.
    /// </summary>
    [Test]
    [Arguments(47_217)]
    [Arguments(1_000_000)]
    public async Task StructuredAppend_TooLongForSixteenSymbols_IsRefused(int length)
    {
        await Assert.That(QRCodeGenerator.CreateStructuredAppend(Filled(47_216), QREccLevel.L).Length).IsEqualTo(16).Because("the premise: the largest set holds this much");

        var exception = Assert.Throws<ArgumentException>(() => QRCodeGenerator.CreateStructuredAppend(Filled(length), QREccLevel.L));

        await Assert.That(exception.GetType()).IsEqualTo(typeof(ArgumentException));
        await Assert.That(exception.ParamName).IsEqualTo("options");
    }

    // The text call's symbols

    /// <summary>ASCII is the same bytes as text, so the bytes call writes the text call's symbol, mode choices included.</summary>
    [Test]
    [Arguments("", QRSegmentation.Single)]
    [Arguments("0123456789", QRSegmentation.Single)]
    [Arguments("HELLO WORLD", QRSegmentation.Single)]
    [Arguments("hello, world", QRSegmentation.Single)]
    [Arguments("abc0123456789012345678901234567890xyz", QRSegmentation.Optimal)]
    [Arguments("HTTPS://EXAMPLE.COM/0123456789012345", QRSegmentation.Optimal)]
    public async Task Ascii_WritesTheTextCallsSymbol(string text, QRSegmentation segmentation)
    {
        var options = Single with { Segmentation = segmentation };

        var fromText = QRCodeGenerator.Create(text, QREccLevel.M, options);
        var fromBytes = QRCodeGenerator.Create(Encoding.ASCII.GetBytes(text), QREccLevel.M, options);

        await Assert.That(Modules(fromBytes)).IsEquivalentTo(Modules(fromText), CollectionOrdering.Matching);
    }

    /// <summary>The assignment a symbol reports passes straight back: its bytes under that assignment write the symbol again.</summary>
    [Test]
    [Arguments("hello")]
    [Arguments("héllo wörld")]
    [Arguments("こんにちは 🎉")]
    public async Task ReadAssignment_PassedBack_WritesTheSymbolAgain(string text)
    {
        var original = QRCodeGenerator.Create(text, QREccLevel.Q, Single);
        await Assert.That(QRCodeDecoder.TryDecodeBytes(original, out var bytes, out var info)).IsTrue();

        var again = QRCodeGenerator.Create(bytes, info.EccLevel, Single, info.EciAssignment);

        await Assert.That(Modules(again)).IsEquivalentTo(Modules(original), CollectionOrdering.Matching);
    }

    // Options

    [Test]
    public async Task BoostAndPinnedMask_ApplyAsToText()
    {
        var payload = Filled(10);

        var boosted = QRCodeGenerator.Create(payload, QREccLevel.L, Single with { BoostEccLevel = true }, 899);
        await Assert.That(boosted.Version).IsEqualTo(1);
        await Assert.That(boosted.EccLevel).IsEqualTo(QREccLevel.M).Because("ECI 899 and ten bytes take 112 bits: within 1-M's 128, over 1-Q's 104");

        var masked = QRCodeGenerator.Create(payload, QREccLevel.L, Single with { MaskPattern = 5 });
        await Assert.That(masked.MaskPattern).IsEqualTo(5);
    }

    public static IEnumerable<Func<QRCodeGeneratorOptions>> TextOnlyOptions()
    {
        yield return () => new QRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1 };
        yield return () => new QRCodeGeneratorOptions { EciMode = EciMode.Utf8 };
        yield return () => new QRCodeGeneratorOptions { EciMode = (EciMode)99 };
        yield return () => new QRCodeGeneratorOptions { Utf8Bom = true };
        yield return () => new QRCodeGeneratorOptions { AllowKanji = true };
        yield return () => new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition };
        yield return () => new QRCodeGeneratorOptions { Fnc1 = Fnc1Mode.SecondPosition(37), Segmentation = QRSegmentation.Optimal };
    }

    /// <summary>A charset, a byte order mark, Kanji mode and FNC1 mean something only for text, so every bytes call refuses them rather than ignore them.</summary>
    [Test]
    [MethodDataSource(nameof(TextOnlyOptions))]
    public async Task TextOnlyOptions_AreRefused(QRCodeGeneratorOptions options)
    {
        byte[] payload = [0x01, 0x02];

        await Assert.That(() => QRCodeGenerator.Create(payload, QREccLevel.M, options)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => QRCodeGenerator.Create(payload, QREccLevel.M, new byte[10_000], options)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => QRCodeGenerator.TryGetRequiredBufferSize(payload, QREccLevel.M, out _, options)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => QRCodeGenerator.CreateStructuredAppend(payload, QREccLevel.M, options)).ThrowsExactly<ArgumentException>();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(1_000_000)]
    [Arguments(int.MinValue)]
    [Arguments(int.MaxValue)]
    public async Task AssignmentOutsideTheRange_IsRefused(int assignment)
    {
        byte[] payload = [0x01, 0x02];

        await Assert.That(() => QRCodeGenerator.Create(payload, QREccLevel.M, default, assignment)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => QRCodeGenerator.Create(payload, QREccLevel.M, new byte[10_000], default, assignment)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => QRCodeGenerator.TryGetRequiredBufferSize(payload, QREccLevel.M, out _, default, assignment)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => QRCodeGenerator.CreateStructuredAppend(payload, QREccLevel.M, default, assignment)).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SpanForm_TooSmallDestination_IsRefused()
    {
        QRCodeGenerator.TryGetRequiredBufferSize(Filled(20), QREccLevel.M, out var size, Single);

        await Assert.That(() => QRCodeGenerator.Create(Filled(20), QREccLevel.M, new byte[size.BufferSize - 1], Single)).ThrowsExactly<ArgumentException>();
    }

    // Structured Append

    /// <summary>A set made from bytes joins back to them exactly, and every part carries the assignment and the XOR of the whole payload.</summary>
    [Test]
    [Arguments(QRSegmentation.Single, -1)]
    [Arguments(QRSegmentation.Single, 899)]
    [Arguments(QRSegmentation.Optimal, -1)]
    [Arguments(QRSegmentation.Optimal, 16384)]
    public async Task StructuredAppend_PartsJoinedInOrder_AreThePayload(QRSegmentation segmentation, int assignment)
    {
        var payload = RunsPayload(new Random(7), 1200);
        int? eci = assignment < 0 ? null : assignment;
        var parity = 0;
        foreach (var b in payload)
            parity ^= b;

        var parts = QRCodeGenerator.CreateStructuredAppend(payload, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.AtMost(10), Segmentation = segmentation }, eci);
        await Assert.That(parts.Length).IsGreaterThan(1).Because("the premise: the payload needs a set");

        var joined = new List<byte>();
        for (var i = 0; i < parts.Length; i++)
        {
            await Assert.That(QRCodeDecoder.TryDecodeBytes(parts[i], out var bytes, out var info)).IsTrue();
            await Assert.That(info.StructuredAppend).IsEqualTo(new QRStructuredAppend(i, parts.Length, (byte)parity));
            await Assert.That(info.EciAssignment).IsEqualTo(eci);
            joined.AddRange(bytes);
        }

        await Assert.That(joined.ToArray()).IsEquivalentTo(payload, CollectionOrdering.Matching);
    }

    [Test]
    public async Task StructuredAppend_PayloadOneSymbolHolds_IsThatSymbol()
    {
        var payload = Filled(30);

        var parts = QRCodeGenerator.CreateStructuredAppend(payload, QREccLevel.M, Single, 899);

        await Assert.That(parts.Length).IsEqualTo(1);
        await Assert.That(Modules(parts[0])).IsEquivalentTo(Modules(QRCodeGenerator.Create(payload, QREccLevel.M, Single, 899)), CollectionOrdering.Matching);
    }

    /// <summary>The boost prices every chunk again, and a declared chunk is priced with its header.</summary>
    [Test]
    [Arguments(-1)]
    [Arguments(899)]
    [Arguments(16384)]
    public async Task StructuredAppend_Boost_RaisesTheWholeSet(int assignment)
    {
        var payload = Filled(220);
        int? eci = assignment < 0 ? null : assignment;

        var parts = QRCodeGenerator.CreateStructuredAppend(payload, QREccLevel.L, new QRCodeGeneratorOptions { Version = 5, BoostEccLevel = true }, eci);

        await Assert.That(parts.Length).IsEqualTo(3).Because("5-L holds 104 bytes behind the set header and 100 behind 16384's header too, so 220 need three symbols");
        foreach (var part in parts)
        {
            await Assert.That(part.EccLevel).IsEqualTo(QREccLevel.M).Because("74 bytes a symbol take 624 bits, 652 behind 16384's header: within 5-M's 688, over 5-Q's 496");
            await Assert.That(QRCodeDecoder.TryDecodeBytes(part, out _, out var info)).IsTrue();
            await Assert.That(info.EciAssignment).IsEqualTo(eci);
        }
    }

    // Helpers

    /// <summary>The standard's Byte capacity of a version and level: its data bits less the header, the Byte mode indicator and count indicator, in whole bytes.</summary>
    private static int ByteCapacity(int version, QREccLevel level, int headerBits)
        => (QRCodeConstants.GetEccInfo(version, level).TotalDataCodewords * 8 - headerBits - 4 - EncodingMode.Byte.GetCountIndicatorLength(version)) / 8;

    /// <summary>The Numeric payload bits of a run of digits: 10 for three, 7 for two left over, 4 for one.</summary>
    private static int NumericBits(int digits) => digits / 3 * 10 + (digits % 3) switch { 1 => 4, 2 => 7, _ => 0 };

    /// <summary>100 bytes of 0xFF, then a run of digits.</summary>
    private static byte[] BytesThenDigits(int digits)
    {
        var payload = new byte[100 + digits];
        payload.AsSpan(0, 100).Fill(0xFF);
        payload.AsSpan(100).Fill((byte)'7');
        return payload;
    }

    /// <summary>A payload of 0xFF bytes, which no mode but Byte holds.</summary>
    private static byte[] Filled(int length)
    {
        var payload = new byte[length];
        payload.AsSpan().Fill(0xFF);
        return payload;
    }

    private static byte[] RunsPayload(Random random, int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < length;)
        {
            var run = Math.Min(length - i, random.Next(1, 40));
            var kind = random.Next(3);
            for (var j = 0; j < run; j++, i++)
            {
                payload[i] = kind switch
                {
                    0 => (byte)('0' + random.Next(10)),
                    1 => (byte)('A' + random.Next(26)),
                    _ => (byte)random.Next(256),
                };
            }
        }
        return payload;
    }

    private static byte[] Modules(QRCodeData data)
    {
        var size = data.Size;
        var modules = new byte[size * size];
        for (var row = 0; row < size; row++)
            for (var col = 0; col < size; col++)
                modules[row * size + col] = data[row, col] ? (byte)1 : (byte)0;
        return modules;
    }

    private static byte[] Codewords(QRCodeData data)
        => Fnc1StreamShape.DataCodewordsOfStandardQr(Modules(data), data.Size, data.Version, data.EccLevel, data.MaskPattern);

    private static string Bits(byte[] codewords)
    {
        var bits = new StringBuilder(codewords.Length * 8);
        foreach (var codeword in codewords)
            bits.Append(Convert.ToString(codeword, 2).PadLeft(8, '0'));
        return bits.ToString();
    }
}
