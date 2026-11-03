using System.Text;
using FeatherQR.Internals;
using FeatherQR.Internals.MicroQR;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The bytes calls of <see cref="MicroQRCodeGenerator"/>: the bytes given are the bytes in the symbol, with nothing to declare them, since Micro QR has no ECI.
/// </summary>
public class MicroQRCodeGeneratorBytesTest
{
    private static readonly MicroQRCodeGeneratorOptions Single = new() { QuietZoneSize = 0, Segmentation = MicroQRSegmentation.Single };
    private static readonly MicroQRCodeGeneratorOptions Optimal = new() { QuietZoneSize = 0, Segmentation = MicroQRSegmentation.Optimal };

    // Round trip

    /// <summary>Every byte value, fifteen at a time, since M4-L holds 15 bytes.</summary>
    [Test]
    [Arguments(MicroQRSegmentation.Single)]
    [Arguments(MicroQRSegmentation.Optimal)]
    public async Task EveryByteValue_ReadsBackAsWritten(MicroQRSegmentation segmentation)
    {
        var options = new MicroQRCodeGeneratorOptions { Segmentation = segmentation };
        for (var first = 0; first < 256; first += 15)
        {
            var payload = new byte[Math.Min(15, 256 - first)];
            for (var i = 0; i < payload.Length; i++)
                payload[i] = (byte)(first + i);

            var data = MicroQRCodeGenerator.Create(payload, MicroQREccLevel.L, options);

            await Assert.That(MicroQRCodeDecoder.TryDecodeBytes(data, out var bytes, out _)).IsTrue().Because($"from 0x{first:X2}");
            await Assert.That(bytes).IsEquivalentTo(payload, CollectionOrdering.Matching).Because($"from 0x{first:X2}");
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RandomPayloads_ReadBackAsWritten_AndEveryFormAgrees(int seed)
    {
        MicroQREccLevel[] levels = [MicroQREccLevel.ErrorDetectionOnly, MicroQREccLevel.L, MicroQREccLevel.M, MicroQREccLevel.Q];
        MicroQRVersionRange[] ranges = [MicroQRVersionRange.Any, MicroQRVersionRange.Exactly(MicroQRVersion.M3), MicroQRVersionRange.Exactly(MicroQRVersion.M4), new(MicroQRVersion.M2, MicroQRVersion.M3)];
        var random = new Random(seed);
        var fitted = 0;
        var refused = 0;
        for (var trial = 0; trial < 60; trial++)
        {
            var payload = RunsPayload(random, random.Next(0, 40));
            var level = levels[random.Next(levels.Length)];
            int? mask = random.Next(5) is var m && m < 4 ? m : null;
            var options = new MicroQRCodeGeneratorOptions
            {
                QuietZoneSize = random.Next(2) * 2,
                Segmentation = random.Next(2) == 0 ? MicroQRSegmentation.Single : MicroQRSegmentation.Optimal,
                Version = ranges[random.Next(ranges.Length)],
                MaskPattern = mask,
            };
            var label = $"seed {seed} trial {trial}: {payload.Length} bytes, {level}, {options.Segmentation}, {options.Version}, mask {mask?.ToString() ?? "auto"}";

            bool fits;
            MicroQRCodeCalculatedSize size;
            try
            {
                fits = MicroQRCodeGenerator.TryGetRequiredBufferSize(payload, level, out size, options);
            }
            catch (ArgumentException)
            {
                // A range that offers the level nowhere, which no content could satisfy
                await Assert.That(() => MicroQRCodeGenerator.Create(payload, level, options)).Throws<ArgumentException>().Because(label);
                continue;
            }

            if (!fits)
            {
                refused++;
                await Assert.That(() => MicroQRCodeGenerator.Create(payload, level, options)).ThrowsExactly<ArgumentException>().Because($"{label}: sizing says it does not fit");
                continue;
            }
            fitted++;
            var data = MicroQRCodeGenerator.Create(payload, level, options);
            await Assert.That(data.Version).IsEqualTo(size.Version).Because(label);
            await Assert.That(data.EccLevel).IsEqualTo(level).Because(label);
            if (mask is { } pinned)
                await Assert.That(data.MaskPattern).IsEqualTo(pinned).Because(label);

            await Assert.That(MicroQRCodeDecoder.TryDecodeBytes(data, out var bytes, out _)).IsTrue().Because(label);
            await Assert.That(bytes).IsEquivalentTo(payload, CollectionOrdering.Matching).Because(label);

            var destination = new byte[size.BufferSize];
            destination.AsSpan().Fill(1);
            await Assert.That(MicroQRCodeGenerator.Create(payload, level, destination, options)).IsEqualTo(size.BufferSize).Because(label);
            await Assert.That(destination).IsEquivalentTo(Modules(data), CollectionOrdering.Matching).Because(label);
        }

        await Assert.That(fitted).IsGreaterThan(0).Because("the premise: the trials exercise the forms, not only the refusal");
        await Assert.That(refused).IsGreaterThan(0).Because("the premise: the trials reach payloads that do not fit");
    }

    /// <summary>An empty payload reads back empty and is the text call's empty symbol.</summary>
    [Test]
    [Arguments(MicroQRSegmentation.Single)]
    [Arguments(MicroQRSegmentation.Optimal)]
    public async Task EmptyPayload_ReadsBackEmpty(MicroQRSegmentation segmentation)
    {
        var options = Single with { Segmentation = segmentation };

        var data = MicroQRCodeGenerator.Create(ReadOnlySpan<byte>.Empty, MicroQREccLevel.L, options);

        await Assert.That(MicroQRCodeDecoder.TryDecodeBytes(data, out var bytes, out _)).IsTrue();
        await Assert.That(bytes).IsEmpty();
        await Assert.That(Modules(data)).IsEquivalentTo(Modules(MicroQRCodeGenerator.Create("", MicroQREccLevel.L, options)), CollectionOrdering.Matching);
    }

    // What the stream holds

    [Test]
    public async Task TheStreamOpensWithByteMode_AndHoldsTheBytes()
    {
        var data = MicroQRCodeGenerator.Create(new byte[] { 0x00, 0xFF, 0x80 }, MicroQREccLevel.L, Single);

        await Assert.That(data.Version).IsEqualTo(MicroQRVersion.M3).Because("the first version with Byte mode");
        var expected = "10" + "0011" + "00000000" + "11111111" + "10000000";
        await Assert.That(Bits(Codewords(data)).Substring(0, expected.Length)).IsEqualTo(expected)
            .Because("M3's 2-bit indicator 10 for Byte, the 4-bit count 3, and the bytes");
    }

    /// <summary>
    /// With no ECI, the decoder guesses the charset of a Byte segment from its bytes, so the text call writes as UTF-8 the characters whose one-byte form it would read as another text.
    /// The bytes call writes the bytes it was given, which the text read then guesses about as it does about any undeclared bytes.
    /// </summary>
    [Test]
    [Arguments("C3A9", "é")]             // the UTF-8 of é
    [Arguments("C4D6DC", "ﾄﾖﾜ")]         // half-width katakana in Shift_JIS
    [Arguments("646F6E9274", null)]      // 0x92 before a letter is a Shift_JIS pair
    public async Task BytesTheTextCallWouldRewrite_AreWrittenAsGiven(string hex, string? guessed)
    {
        var payload = Convert.FromHexString(hex);
        var latin1 = Encoding.Latin1.GetString(payload);

        var data = MicroQRCodeGenerator.Create(payload, MicroQREccLevel.L, Single);

        await Assert.That(MicroQRCodeDecoder.TryDecodeBytes(data, out var bytes, out _)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(payload, CollectionOrdering.Matching);
        if (guessed is not null)
        {
            await Assert.That(MicroQRCodeDecoder.TryDecode(data, out var text)).IsTrue();
            await Assert.That(text).IsEqualTo(guessed).Because("the premise: the decoder reads these bytes as another text than their ISO-8859-1 one");
        }

        var fromText = MicroQRCodeGenerator.Create(latin1, MicroQREccLevel.L, Single);
        await Assert.That(MicroQRCodeDecoder.TryDecodeBytes(fromText, out var written, out _)).IsTrue();
        await Assert.That(written).IsEquivalentTo(Encoding.UTF8.GetBytes(latin1), CollectionOrdering.Matching)
            .Because("the premise: the text call writes the same characters as UTF-8");
    }

    [Test]
    public async Task Optimal_SplitsDigitsFromBytes_IntoASmallerSymbol()
    {
        var payload = new byte[15];
        payload[0] = 0xFF;
        Encoding.ASCII.GetBytes("12345678901234", payload.AsSpan(1));

        var single = MicroQRCodeGenerator.Create(payload, MicroQREccLevel.L, Single);
        var optimal = MicroQRCodeGenerator.Create(payload, MicroQREccLevel.L, Optimal);

        await Assert.That(single.Version).IsEqualTo(MicroQRVersion.M4).Because("15 bytes need M4-L in Byte mode");
        await Assert.That(optimal.Version).IsEqualTo(MicroQRVersion.M3).Because("one Byte run and one Numeric run take 68 of M3-L's 84 bits");
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(payload, MicroQREccLevel.L, out var size, Optimal)).IsTrue();
        await Assert.That(size.Version).IsEqualTo(MicroQRVersion.M3);
        foreach (var data in new[] { single, optimal })
        {
            await Assert.That(MicroQRCodeDecoder.TryDecodeBytes(data, out var bytes, out _)).IsTrue();
            await Assert.That(bytes).IsEquivalentTo(payload, CollectionOrdering.Matching);
        }
    }

    // Capacity

    public static IEnumerable<Func<(MicroQRVersion Version, MicroQREccLevel Level, int Capacity)>> ByteVersions()
    {
        yield return () => (MicroQRVersion.M3, MicroQREccLevel.L, 9);
        yield return () => (MicroQRVersion.M3, MicroQREccLevel.M, 7);
        yield return () => (MicroQRVersion.M4, MicroQREccLevel.L, 15);
        yield return () => (MicroQRVersion.M4, MicroQREccLevel.M, 13);
        yield return () => (MicroQRVersion.M4, MicroQREccLevel.Q, 9);
    }

    /// <summary>A version holds its published Byte-mode capacity of bytes no other mode takes, and one more is refused, from every form.</summary>
    [Test]
    [MethodDataSource(nameof(ByteVersions))]
    public async Task TheLargestPayloadAVersionHolds_IsItsByteCapacity(MicroQRVersion version, MicroQREccLevel level, int capacity)
    {
        foreach (var options in new[] { Single, Optimal })
        {
            var pinned = options with { Version = MicroQRVersionRange.Exactly(version) };
            await Assert.That(MicroQRCodeGenerator.Create(Filled(capacity), level, pinned).Version).IsEqualTo(version);
            await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(Filled(capacity), level, out var size, pinned)).IsTrue();
            await Assert.That(size.Version).IsEqualTo(version);
            await Assert.That(() => MicroQRCodeGenerator.Create(Filled(capacity + 1), level, pinned)).ThrowsExactly<ArgumentException>();
            await Assert.That(() => MicroQRCodeGenerator.Create(Filled(capacity + 1), level, new byte[1000], pinned)).ThrowsExactly<ArgumentException>();
            await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(Filled(capacity + 1), level, out _, pinned)).IsFalse();
        }
    }

    /// <summary>M1 and M2 have no Byte mode, so one byte no other mode takes is refused there, and digits are not.</summary>
    [Test]
    [Arguments(MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly)]
    [Arguments(MicroQRVersion.M2, MicroQREccLevel.L)]
    public async Task VersionsWithoutByteMode_RefuseBytes_AndTakeDigits(MicroQRVersion version, MicroQREccLevel level)
    {
        var options = Single with { Version = MicroQRVersionRange.Exactly(version) };

        await Assert.That(() => MicroQRCodeGenerator.Create(new byte[] { 0xFF }, level, options)).ThrowsExactly<ArgumentException>();
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(new byte[] { 0xFF }, level, out _, options)).IsFalse();

        var digits = Encoding.ASCII.GetBytes("12345");
        var data = MicroQRCodeGenerator.Create(digits, level, options);
        await Assert.That(data.Version).IsEqualTo(version);
        await Assert.That(MicroQRCodeDecoder.TryDecodeBytes(data, out var bytes, out _)).IsTrue();
        await Assert.That(bytes).IsEquivalentTo(digits, CollectionOrdering.Matching);
    }

    /// <summary>Digits are a byte each in Numeric mode, so 35 of them fit M4-L though it holds 15 bytes in Byte mode, and 36 are refused.</summary>
    [Test]
    public async Task Digits_AreWrittenInNumericMode_SoTheyFitPastTheByteCapacity()
    {
        var digits = Enumerable.Repeat((byte)'7', 35).ToArray();
        foreach (var options in new[] { Single, Optimal })
        {
            var data = MicroQRCodeGenerator.Create(digits, MicroQREccLevel.L, options);
            await Assert.That(data.Version).IsEqualTo(MicroQRVersion.M4);
            await Assert.That(MicroQRCodeDecoder.TryDecodeBytes(data, out var bytes, out _)).IsTrue();
            await Assert.That(bytes).IsEquivalentTo(digits, CollectionOrdering.Matching);
        }

        var tooMany = Enumerable.Repeat((byte)'7', 36).ToArray();
        await Assert.That(() => MicroQRCodeGenerator.Create(tooMany, MicroQREccLevel.L)).ThrowsExactly<ArgumentException>();
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(tooMany, MicroQREccLevel.L, out _)).IsFalse();
    }

    [Test]
    [Arguments(36)]
    [Arguments(1_000_000)]
    public async Task TooLong_IsRefused(int length)
    {
        await Assert.That(() => MicroQRCodeGenerator.Create(Filled(length), MicroQREccLevel.L)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => MicroQRCodeGenerator.Create(Filled(length), MicroQREccLevel.L, Single)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => MicroQRCodeGenerator.Create(Filled(length), MicroQREccLevel.L, Single with { Version = MicroQRVersionRange.Exactly(MicroQRVersion.M4) })).ThrowsExactly<ArgumentException>();
        await Assert.That(() => MicroQRCodeGenerator.Create(Filled(length), MicroQREccLevel.L, new byte[1000], Optimal)).ThrowsExactly<ArgumentException>();
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(Filled(length), MicroQREccLevel.L, out _)).IsFalse();
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(Filled(length), MicroQREccLevel.L, out _, Single)).IsFalse();
    }

    /// <summary>A payload too long for any symbol still reports a bad argument first, as the text call does.</summary>
    [Test]
    [Arguments(10)]
    [Arguments(36)]
    public async Task ArgumentErrors_ComeBeforeTheLength(int length)
    {
        var payload = Filled(length);
        foreach (var options in new[] { Single, Optimal })
        {
            await Assert.That(() => MicroQRCodeGenerator.Create(payload, (MicroQREccLevel)9, options)).ThrowsExactly<ArgumentOutOfRangeException>();
            await Assert.That(() => MicroQRCodeGenerator.TryGetRequiredBufferSize(payload, (MicroQREccLevel)9, out _, options)).ThrowsExactly<ArgumentOutOfRangeException>();
            await Assert.That(() => MicroQRCodeGenerator.Create(payload, MicroQREccLevel.L, options with { QuietZoneSize = -1 })).ThrowsExactly<ArgumentOutOfRangeException>();

            // Q is on M4 alone, so a range below it offers the level nowhere
            var noQ = options with { Version = new MicroQRVersionRange(MicroQRVersion.M1, MicroQRVersion.M3) };
            var error = Assert.Throws<ArgumentException>(() => MicroQRCodeGenerator.Create(payload, MicroQREccLevel.Q, noQ));
            await Assert.That(error.ParamName).IsEqualTo("eccLevel");
            error = Assert.Throws<ArgumentException>(() => MicroQRCodeGenerator.TryGetRequiredBufferSize(payload, MicroQREccLevel.Q, out _, noQ));
            await Assert.That(error.ParamName).IsEqualTo("eccLevel");
        }

        await Assert.That(() => MicroQRCodeGenerator.Create(payload, MicroQREccLevel.L, Single with { Segmentation = (MicroQRSegmentation)7 })).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    /// <summary>The span form lays out the class form's modules, quiet zone included, over a destination that held anything, and refuses one byte less.</summary>
    [Test]
    [Arguments(MicroQRSegmentation.Single)]
    [Arguments(MicroQRSegmentation.Optimal)]
    public async Task SpanForm_WithAQuietZone_WritesTheClassFormsModules(MicroQRSegmentation segmentation)
    {
        var options = Single with { QuietZoneSize = 2, Segmentation = segmentation };
        var payload = new byte[] { 0xFF, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38 };
        await Assert.That(MicroQRCodeGenerator.TryGetRequiredBufferSize(payload, MicroQREccLevel.M, out var size, options)).IsTrue();

        var destination = new byte[size.BufferSize];
        destination.AsSpan().Fill(1);
        await Assert.That(MicroQRCodeGenerator.Create(payload, MicroQREccLevel.M, destination, options)).IsEqualTo(size.BufferSize);

        await Assert.That(destination).IsEquivalentTo(Modules(MicroQRCodeGenerator.Create(payload, MicroQREccLevel.M, options)), CollectionOrdering.Matching);
        await Assert.That(() => MicroQRCodeGenerator.Create(payload, MicroQREccLevel.M, new byte[size.BufferSize - 1], options)).ThrowsExactly<ArgumentException>();
    }

    // The text call's symbols

    /// <summary>Text the text call writes one byte a character, ASCII and ISO-8859-1 the decoder reads as written, gives the same symbol as its bytes.</summary>
    [Test]
    [Arguments("", MicroQRSegmentation.Single)]
    [Arguments("01234", MicroQRSegmentation.Single)]
    [Arguments("HELLO", MicroQRSegmentation.Single)]
    [Arguments("hello", MicroQRSegmentation.Single)]
    [Arguments("café", MicroQRSegmentation.Single)]
    [Arguments("café à Zürich", MicroQRSegmentation.Single)]
    [Arguments("ab0123456789", MicroQRSegmentation.Optimal)]
    [Arguments("HELLO12345678", MicroQRSegmentation.Optimal)]
    public async Task OneByteACharacterText_WritesTheTextCallsSymbol(string text, MicroQRSegmentation segmentation)
    {
        foreach (var mask in new int?[] { null, 2 })
        {
            var options = Single with { Segmentation = segmentation, MaskPattern = mask };

            var fromText = MicroQRCodeGenerator.Create(text, MicroQREccLevel.L, options);
            var fromBytes = MicroQRCodeGenerator.Create(Encoding.Latin1.GetBytes(text), MicroQREccLevel.L, options);

            await Assert.That(Modules(fromBytes)).IsEquivalentTo(Modules(fromText), CollectionOrdering.Matching).Because($"mask {mask?.ToString() ?? "auto"}");
        }
    }

    /// <summary>The bytes a bytes read gives write the symbol again, whichever charset the text call wrote them in.</summary>
    [Test]
    [Arguments("hello")]
    [Arguments("café")]
    [Arguments("Ã©")]
    [Arguments("こんにちは")]
    public async Task ReadBytes_PassedBack_WriteTheSymbolAgain(string text)
    {
        var original = MicroQRCodeGenerator.Create(text, MicroQREccLevel.L, Single);
        await Assert.That(MicroQRCodeDecoder.TryDecodeBytes(original, out var bytes, out var info)).IsTrue();

        var again = MicroQRCodeGenerator.Create(bytes, info.EccLevel, Single);

        await Assert.That(Modules(again)).IsEquivalentTo(Modules(original), CollectionOrdering.Matching);
    }

    // Options

    [Test]
    [Arguments(MicroQRSegmentation.Single)]
    [Arguments(MicroQRSegmentation.Optimal)]
    public async Task AllowKanji_IsRefused(MicroQRSegmentation segmentation)
    {
        var options = new MicroQRCodeGeneratorOptions { AllowKanji = true, Segmentation = segmentation };
        byte[] payload = [0x82, 0xA0];

        await Assert.That(() => MicroQRCodeGenerator.Create(payload, MicroQREccLevel.L, options)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => MicroQRCodeGenerator.Create(payload, MicroQREccLevel.L, new byte[1000], options)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => MicroQRCodeGenerator.TryGetRequiredBufferSize(payload, MicroQREccLevel.L, out _, options)).ThrowsExactly<ArgumentException>();
    }

#if !DEBUG
    /// <summary>The span form and the sizing widen on the stack and allocate nothing, under both segmentations. Release only: Debug allocates where Release does not.</summary>
    [Test]
    [NotInParallel]
    public async Task SpanFormAndSizing_AreAllocationFree()
    {
        byte[][] payloads = [[0xFF, 0x00, 0x80], [0xFF, .. Encoding.ASCII.GetBytes("12345678901234")], Encoding.ASCII.GetBytes("12345678901234567890")];
        MicroQRCodeGeneratorOptions[] optionSets = [Single, Optimal];
        var buffer = new byte[1024];
        void Run()
        {
            foreach (var payload in payloads)
            {
                foreach (var options in optionSets)
                {
                    MicroQRCodeGenerator.Create(payload, MicroQREccLevel.L, buffer, options);
                    MicroQRCodeGenerator.TryGetRequiredBufferSize(payload, MicroQREccLevel.L, out _, options);
                }
            }
        }

        for (var i = 0; i < 3; i++)
            Run();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 16; i++)
            Run();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0);
    }
#endif

    // Helpers

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
            var run = Math.Min(length - i, random.Next(1, 12));
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

    private static byte[] Modules(MicroQRCodeData data)
    {
        var modules = new byte[data.Size * data.Size];
        for (var row = 0; row < data.Size; row++)
            for (var col = 0; col < data.Size; col++)
                modules[row * data.Size + col] = data[row, col] ? (byte)1 : (byte)0;
        return modules;
    }

    /// <summary>The data codewords read from the symbol's own modules, the last half codeword of M1 and M3 in a high nibble.</summary>
    private static byte[] Codewords(MicroQRCodeData data)
    {
        var size = data.GetCoreSize();
        var core = new byte[size * size];
        data.GetCoreData(core);
        var codewords = new byte[MicroQRConstants.GetDataCodewordCount(data.Version, data.EccLevel)];
        MicroQRMatrixDecoder.ExtractCodewords(core, new MatrixModules(size), size, data.MaskPattern, MicroQRConstants.GetDataBitCapacity(data.Version, data.EccLevel), codewords.Length, codewords);
        return codewords;
    }

    private static string Bits(byte[] codewords)
    {
        var bits = new StringBuilder(codewords.Length * 8);
        foreach (var codeword in codewords)
            bits.Append(Convert.ToString(codeword, 2).PadLeft(8, '0'));
        return bits.ToString();
    }
}
