using System.Text;
using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The bytes calls of <see cref="RmQRCodeGenerator"/>: the bytes given are the bytes in the symbol, under the ECI assignment the caller names or under none.
/// </summary>
public class RmQRCodeGeneratorBytesTest
{
    private static readonly RmQRCodeGeneratorOptions Single = new() { QuietZoneSize = 0, Segmentation = RmQRSegmentation.Single };
    private static readonly RmQRCodeGeneratorOptions Optimal = new() { QuietZoneSize = 0, Segmentation = RmQRSegmentation.Optimal };

    // Round trip

    /// <summary>Every byte value, in two halves, since R17x139-M holds 150 bytes.</summary>
    [Test]
    [Arguments(0, RmQRSegmentation.Single, -1)]
    [Arguments(128, RmQRSegmentation.Single, -1)]
    [Arguments(0, RmQRSegmentation.Single, 899)]
    [Arguments(128, RmQRSegmentation.Single, 899)]
    [Arguments(0, RmQRSegmentation.Optimal, -1)]
    [Arguments(0, RmQRSegmentation.Optimal, 899)]
    public async Task EveryByteValue_ReadsBackAsWritten(int first, RmQRSegmentation segmentation, int assignment)
    {
        var payload = new byte[128];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(first + i);
        int? eci = assignment < 0 ? null : assignment;

        var data = RmQRCodeGenerator.Create(payload, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Segmentation = segmentation }, eci);

        await Assert.That(RmQRCodeDecoder.TryDecodeBytes(data, out var bytes, out var info)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(payload, CollectionOrdering.Matching);
        await Assert.That(info.EciAssignment).IsEqualTo(eci);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RandomPayloads_ReadBackAsWritten_AndEveryFormAgrees(int seed)
    {
        int?[] assignments = [null, 0, 3, 26, 127, 128, 899, 16383, 16384, 999999];
        var random = new Random(seed);
        var fitted = 0;
        for (var trial = 0; trial < 40; trial++)
        {
            var payload = RunsPayload(random, random.Next(0, 160));
            var eci = assignments[random.Next(assignments.Length)];
            var level = random.Next(2) == 0 ? RmQREccLevel.M : RmQREccLevel.H;
            var options = new RmQRCodeGeneratorOptions { QuietZoneSize = 0, Segmentation = random.Next(2) == 0 ? RmQRSegmentation.Single : RmQRSegmentation.Optimal, FitStrategy = (RmQRFitStrategy)random.Next(3) };
            var label = $"seed {seed} trial {trial}: {payload.Length} bytes, ECI {eci?.ToString() ?? "none"}, {level}, {options.Segmentation}, {options.FitStrategy}";

            if (!RmQRCodeGenerator.TryGetRequiredBufferSize(payload, level, out var size, options, eci))
            {
                await Assert.That(() => RmQRCodeGenerator.Create(payload, level, options, eci)).ThrowsExactly<ArgumentException>().Because($"{label}: sizing says it does not fit");
                continue;
            }
            fitted++;
            var data = RmQRCodeGenerator.Create(payload, level, options, eci);
            await Assert.That(data.Version).IsEqualTo(size.Version).Because(label);

            await Assert.That(RmQRCodeDecoder.TryDecodeBytes(data, out var bytes, out var info)).IsTrue().Because(label);
            await Assert.That(bytes).IsEquivalentTo(payload, CollectionOrdering.Matching).Because(label);
            await Assert.That(info.EciAssignment).IsEqualTo(eci).Because(label);

            var destination = new byte[size.BufferSize];
            await Assert.That(RmQRCodeGenerator.Create(payload, level, destination, options, eci)).IsEqualTo(size.BufferSize).Because(label);
            await Assert.That(destination).IsEquivalentTo(Modules(data), CollectionOrdering.Matching).Because(label);
        }

        await Assert.That(fitted).IsGreaterThan(0).Because("the premise: the trials exercise the forms, not only the refusal");
    }

    /// <summary>An empty payload is a Byte segment of no bytes, declared or not, and reads back empty.</summary>
    [Test]
    [Arguments(-1)]
    [Arguments(899)]
    public async Task EmptyPayload_ReadsBackEmpty(int assignment)
    {
        int? eci = assignment < 0 ? null : assignment;

        var data = RmQRCodeGenerator.Create(ReadOnlySpan<byte>.Empty, RmQREccLevel.M, Single, eci);

        await Assert.That(RmQRCodeDecoder.TryDecodeBytes(data, out var bytes, out var info)).IsTrue();
        await Assert.That(bytes).IsEmpty();
        await Assert.That(info.EciAssignment).IsEqualTo(eci);
    }

    // What the stream holds

    [Test]
    public async Task Undeclared_TheStreamOpensWithByteMode_AndHoldsTheBytes()
    {
        var data = RmQRCodeGenerator.Create(new byte[] { 0x00, 0xFF, 0x80 }, RmQREccLevel.M, Single with { Version = RmQRVersion.R7x43 });

        var count = RmQRConstants.GetCountIndicatorLength(RmQRVersion.R7x43, EncodingMode.Byte);
        var expected = "011" + Convert.ToString(3, 2).PadLeft(count, '0') + "00000000" + "11111111" + "10000000";
        await Assert.That(Bits(Codewords(data)).Substring(0, expected.Length)).IsEqualTo(expected)
            .Because("011 Byte, the count 3, the bytes, and no ECI header");
    }

    /// <summary>The designator is one codeword up to 127, two up to 16383 and three up to 999999, marked by its leading 0, 10 or 110, behind rMQR's 3-bit ECI indicator.</summary>
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
        var data = RmQRCodeGenerator.Create(new byte[] { 0xFF }, RmQREccLevel.M, Single, assignment);

        var count = RmQRConstants.GetCountIndicatorLength(data.Version, EncodingMode.Byte);
        var expected = "111" + designator + "011" + Convert.ToString(1, 2).PadLeft(count, '0') + "11111111";
        await Assert.That(Bits(Codewords(data)).Substring(0, expected.Length)).IsEqualTo(expected)
            .Because("111 ECI, the designator, 011 Byte, the count 1, the byte");
        await Assert.That(RmQRCodeDecoder.TryDecodeBytes(data, out var bytes, out var info)).IsTrue();
        await Assert.That(info.EciAssignment).IsEqualTo(assignment);
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0xFF }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Declared_Optimal_OneHeaderAheadOfThePlannedRuns()
    {
        var payload = Encoding.ASCII.GetBytes("abc0123456789012345678901234567890xyz");
        var data = RmQRCodeGenerator.Create(payload, RmQREccLevel.M, Optimal, 16384);

        var shape = Fnc1StreamShape.OfRmQr(Modules(data), data.Width, data.Height, data.Version, data.EccLevel);
        await Assert.That(shape.Headers).IsEquivalentTo(new[] { "ECI:16384" }, CollectionOrdering.Matching);
        await Assert.That(shape.DataModes).IsEquivalentTo(new[] { "Byte", "Numeric", "Byte" }, CollectionOrdering.Matching);
        await Assert.That(RmQRCodeDecoder.TryDecodeBytes(data, out var bytes, out _)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(payload, CollectionOrdering.Matching);
    }

    // Capacity

    [Test]
    [Arguments(RmQRVersion.R7x43, RmQREccLevel.M, 5)]
    [Arguments(RmQRVersion.R7x43, RmQREccLevel.H, 2)]
    [Arguments(RmQRVersion.R17x139, RmQREccLevel.M, 150)]
    [Arguments(RmQRVersion.R17x139, RmQREccLevel.H, 74)]
    public async Task ByteCapacity_IsTheStandardsTable(RmQRVersion version, RmQREccLevel level, int capacity)
        => await Assert.That(ByteCapacity(version, level, headerBits: 0)).IsEqualTo(capacity);

    public static IEnumerable<Func<(RmQRVersion Version, RmQREccLevel Level)>> EveryVersionAndLevel()
    {
        for (var version = 1; version <= RmQRConstants.VersionCount; version++)
        {
            foreach (var level in new[] { RmQREccLevel.M, RmQREccLevel.H })
            {
                var (v, l) = ((RmQRVersion)version, level);
                yield return () => (v, l);
            }
        }
    }

    /// <summary>Under single-segment encoding a version holds its Byte-mode capacity of bytes no other mode takes, and one more is refused. With assignment 899 the two designator codewords and the 3-bit indicator come out of it.</summary>
    [Test]
    [MethodDataSource(nameof(EveryVersionAndLevel))]
    public async Task TheLargestPayloadAVersionHolds_IsItsByteCapacity(RmQRVersion version, RmQREccLevel level)
    {
        var options = Single with { Version = version };
        foreach (var (eci, headerBits) in new (int?, int)[] { (null, 0), (899, 3 + 16) })
        {
            var capacity = ByteCapacity(version, level, headerBits);
            if (capacity < 0)
                continue;

            await Assert.That(RmQRCodeGenerator.Create(Filled(capacity), level, options, eci).Version).IsEqualTo(version);
            await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(Filled(capacity), level, out var size, options, eci)).IsTrue();
            await Assert.That(size.Version).IsEqualTo(version);
            await Assert.That(() => RmQRCodeGenerator.Create(Filled(capacity + 1), level, options, eci)).ThrowsExactly<ArgumentException>();
            await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(Filled(capacity + 1), level, out _, options, eci)).IsFalse();
        }
    }

    /// <summary>
    /// The automatic fit prices the designator's codewords too.
    /// One byte more than R7x139-M holds behind the assignment's header is refused at height 7, and without the height it takes another version.
    /// </summary>
    [Test]
    [Arguments(127)]
    [Arguments(128)]
    [Arguments(16384)]
    public async Task AutomaticFit_PricesTheDesignator(int assignment)
    {
        var headerBits = 3 + (assignment < 128 ? 8 : assignment < 16384 ? 16 : 24);
        var options = Single with { FitStrategy = RmQRFitStrategy.MinimizeHeight, Height = RmQRHeight.H7 };
        var capacity = ByteCapacity(RmQRVersion.R7x139, RmQREccLevel.M, headerBits);

        await Assert.That(RmQRCodeGenerator.Create(Filled(capacity), RmQREccLevel.M, options, assignment).Version).IsEqualTo(RmQRVersion.R7x139);
        await Assert.That(() => RmQRCodeGenerator.Create(Filled(capacity + 1), RmQREccLevel.M, options, assignment)).ThrowsExactly<ArgumentException>()
            .Because("R7x139 is the largest height-7 version");
        await Assert.That(RmQRCodeGenerator.Create(Filled(capacity + 1), RmQREccLevel.M, options with { Height = null }, assignment).Version).IsNotEqualTo(RmQRVersion.R7x139);
    }

    [Test]
    [Arguments(151)]
    [Arguments(1_000_000)]
    public async Task TooLong_IsRefused(int length)
    {
        await Assert.That(() => RmQRCodeGenerator.Create(Filled(length), RmQREccLevel.M)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => RmQRCodeGenerator.Create(Filled(length), RmQREccLevel.M, Optimal)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => RmQRCodeGenerator.Create(Filled(length), RmQREccLevel.M, new byte[100], Single)).ThrowsExactly<ArgumentException>();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(Filled(length), RmQREccLevel.M, out _)).IsFalse();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(Filled(length), RmQREccLevel.M, out _, Optimal)).IsFalse();
    }

    /// <summary>More bytes than any symbol holds are refused before the analysis, and the error still names the mode the text call names.</summary>
    [Test]
    [Arguments('7')]
    [Arguments('A')]
    public async Task TooLong_ReportsWhatTheTextCallReports(char character)
    {
        var text = new string(character, 362);
        var payload = Encoding.ASCII.GetBytes(text);
        foreach (var options in new[] { Single, Optimal })
        {
            var expected = Assert.Throws<ArgumentException>(() => RmQRCodeGenerator.Create(text, RmQREccLevel.M, options));
            foreach (var actual in new[] { Assert.Throws<ArgumentException>(() => RmQRCodeGenerator.Create(payload, RmQREccLevel.M, options)), Assert.Throws<ArgumentException>(() => RmQRCodeGenerator.Create(payload, RmQREccLevel.M, new byte[100], options)) })
            {
                await Assert.That(actual.Message).IsEqualTo(expected.Message).Because($"{options.Segmentation}");
                await Assert.That(actual.ParamName).IsEqualTo(expected.ParamName).Because($"{options.Segmentation}");
            }
        }
    }

    /// <summary>Digits are a byte each in Numeric mode, so 361 of them fit R17x139-M though it holds 150 bytes in Byte mode, and 362 are refused.</summary>
    [Test]
    public async Task Digits_AreWrittenInNumericMode_SoTheyFitPastTheByteCapacity()
    {
        var digits = Enumerable.Repeat((byte)'7', 361).ToArray();
        var data = RmQRCodeGenerator.Create(digits, RmQREccLevel.M);
        await Assert.That(data.Version).IsEqualTo(RmQRVersion.R17x139);
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(digits, RmQREccLevel.M, out var size)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(RmQRVersion.R17x139);
        await Assert.That(RmQRCodeDecoder.TryDecodeBytes(data, out var bytes, out _)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(digits, CollectionOrdering.Matching);

        var tooMany = Enumerable.Repeat((byte)'7', 362).ToArray();
        await Assert.That(() => RmQRCodeGenerator.Create(tooMany, RmQREccLevel.M)).ThrowsExactly<ArgumentException>();
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(tooMany, RmQREccLevel.M, out _)).IsFalse();
    }

    /// <summary>The span form of a single-segment stream lays out the class form's modules, quiet zone included, over a destination that held anything.</summary>
    [Test]
    [Arguments(-1)]
    [Arguments(899)]
    public async Task SpanForm_WithAQuietZone_WritesTheClassFormsModules(int assignment)
    {
        int? eci = assignment < 0 ? null : assignment;
        var options = Single with { QuietZoneSize = 2 };
        var payload = Filled(20);
        await Assert.That(RmQRCodeGenerator.TryGetRequiredBufferSize(payload, RmQREccLevel.M, out var size, options, eci)).IsTrue();

        var destination = new byte[size.BufferSize];
        destination.AsSpan().Fill(1);
        await Assert.That(RmQRCodeGenerator.Create(payload, RmQREccLevel.M, destination, options, eci)).IsEqualTo(size.BufferSize);

        await Assert.That(destination).IsEquivalentTo(Modules(RmQRCodeGenerator.Create(payload, RmQREccLevel.M, options, eci)), CollectionOrdering.Matching);
        await Assert.That(() => RmQRCodeGenerator.Create(payload, RmQREccLevel.M, new byte[size.BufferSize - 1], options, eci)).ThrowsExactly<ArgumentException>();
    }

    // The text call's symbols

    [Test]
    [Arguments("", RmQRSegmentation.Single)]
    [Arguments("0123456789", RmQRSegmentation.Single)]
    [Arguments("HELLO WORLD", RmQRSegmentation.Single)]
    [Arguments("hello, world", RmQRSegmentation.Single)]
    [Arguments("abc0123456789012345678901234567890xyz", RmQRSegmentation.Optimal)]
    public async Task Ascii_WritesTheTextCallsSymbol(string text, RmQRSegmentation segmentation)
    {
        var options = Single with { Segmentation = segmentation };

        var fromText = RmQRCodeGenerator.Create(text, RmQREccLevel.M, options);
        var fromBytes = RmQRCodeGenerator.Create(Encoding.ASCII.GetBytes(text), RmQREccLevel.M, options);

        await Assert.That(Modules(fromBytes)).IsEquivalentTo(Modules(fromText), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("hello")]
    [Arguments("héllo wörld")]
    [Arguments("こんにちは")]
    public async Task ReadAssignment_PassedBack_WritesTheSymbolAgain(string text)
    {
        var original = RmQRCodeGenerator.Create(text, RmQREccLevel.M, Single);
        await Assert.That(RmQRCodeDecoder.TryDecodeBytes(original, out var bytes, out var info)).IsTrue();

        var again = RmQRCodeGenerator.Create(bytes, info.EccLevel, Single, info.EciAssignment);

        await Assert.That(Modules(again)).IsEquivalentTo(Modules(original), CollectionOrdering.Matching);
    }

    // Options

    public static IEnumerable<Func<RmQRCodeGeneratorOptions>> TextOnlyOptions()
    {
        yield return () => new RmQRCodeGeneratorOptions { EciMode = EciMode.Iso8859_1 };
        yield return () => new RmQRCodeGeneratorOptions { EciMode = EciMode.Utf8 };
        yield return () => new RmQRCodeGeneratorOptions { EciMode = (EciMode)99 };
        yield return () => new RmQRCodeGeneratorOptions { AllowKanji = true };
        yield return () => new RmQRCodeGeneratorOptions { Fnc1 = Fnc1Mode.FirstPosition };
        yield return () => new RmQRCodeGeneratorOptions { Fnc1 = Fnc1Mode.SecondPosition(37), Segmentation = RmQRSegmentation.Optimal };
    }

    [Test]
    [MethodDataSource(nameof(TextOnlyOptions))]
    public async Task TextOnlyOptions_AreRefused(RmQRCodeGeneratorOptions options)
    {
        byte[] payload = [0x01, 0x02];

        await Assert.That(() => RmQRCodeGenerator.Create(payload, RmQREccLevel.M, options)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => RmQRCodeGenerator.Create(payload, RmQREccLevel.M, new byte[10_000], options)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSize(payload, RmQREccLevel.M, out _, options)).ThrowsExactly<ArgumentException>();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(1_000_000)]
    [Arguments(int.MinValue)]
    [Arguments(int.MaxValue)]
    public async Task AssignmentOutsideTheRange_IsRefused(int assignment)
    {
        byte[] payload = [0x01, 0x02];

        await Assert.That(() => RmQRCodeGenerator.Create(payload, RmQREccLevel.M, default, assignment)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.Create(payload, RmQREccLevel.M, new byte[10_000], default, assignment)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => RmQRCodeGenerator.TryGetRequiredBufferSize(payload, RmQREccLevel.M, out _, default, assignment)).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    // Helpers

    private static int ByteCapacity(RmQRVersion version, RmQREccLevel level, int headerBits)
    {
        var dataBits = RmQRConstants.GetDataCodewordCount(version, level) * 8 - headerBits - RmQRConstants.ModeIndicatorLength - RmQRConstants.GetCountIndicatorLength(version, EncodingMode.Byte);
        return dataBits < 0 ? -1 : dataBits / 8;
    }

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
            var run = Math.Min(length - i, random.Next(1, 30));
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

    private static byte[] Modules(RmQRCodeData data)
    {
        var modules = new byte[data.Width * data.Height];
        for (var row = 0; row < data.Height; row++)
            for (var col = 0; col < data.Width; col++)
                modules[row * data.Width + col] = data[row, col] ? (byte)1 : (byte)0;
        return modules;
    }

    private static byte[] Codewords(RmQRCodeData data)
        => Fnc1StreamShape.DataCodewordsOfRmQr(Modules(data), data.Width, data.Height, data.Version, data.EccLevel);

    private static string Bits(byte[] codewords)
    {
        var bits = new StringBuilder(codewords.Length * 8);
        foreach (var codeword in codewords)
            bits.Append(Convert.ToString(codeword, 2).PadLeft(8, '0'));
        return bits.ToString();
    }
}
