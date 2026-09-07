using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="RmQRBinaryEncoder"/> vs the independent naive bit-string reference
/// (<see cref="RmQRNaiveReference.NaiveDataCodewords"/>) across every version × ECC,
/// every mode, every length up to capacity, with minimum / maximum / pseudo-random
/// contents, plus the full Latin-1 range and UTF-8 fallbacks (surrogate pairs,
/// lone surrogates). This is the parity net the fast-path follow-up will be held to.
/// </summary>
public class RmQRBinaryEncoderParityTest
{
    private const string AlphanumericAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    public static IEnumerable<(RmQRVersion version, RmQREccLevel ecc)> AllVersionEcc()
    {
        foreach (var v in Enum.GetValues<RmQRVersion>())
        {
            yield return (v, RmQREccLevel.M);
            yield return (v, RmQREccLevel.H);
        }
    }

    private static string Cyclic(string alphabet, int length, int offset)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[(i + offset) % alphabet.Length];
        return new string(chars);
    }

    private static string PseudoRandom(string alphabet, int length, int seed)
    {
        var chars = new char[length];
        var state = (uint)seed * 2654435761u + 7u;
        for (var i = 0; i < length; i++)
        {
            state = state * 1664525u + 1013904223u;
            chars[i] = alphabet[(int)(state >> 8) % alphabet.Length];
        }
        return new string(chars);
    }

    private static async Task AssertParity(string text, RmQRVersion version, RmQREccLevel ecc, string mode, bool utf8, EciMode eciMode = EciMode.Default)
    {
        var count = RmQRConstants.GetDataCodewordCount(version, ecc);
        var encodingMode = mode switch { "Numeric" => EncodingMode.Numeric, "Alphanumeric" => EncodingMode.Alphanumeric, _ => EncodingMode.Byte };
        var dataLength = mode == "Byte" ? (utf8 ? System.Text.Encoding.UTF8.GetByteCount(text) : text.Length) : text.Length;
        var resolvedEciMode = utf8 ? EciMode.Utf8 : eciMode;
        var analysis = new TextAnalysisResult(encodingMode, resolvedEciMode, dataLength);
        var expected = RmQRNaiveReference.NaiveDataCodewords(text, count, RmQRConstants.GetModeIndicatorValue(encodingMode), RmQRConstants.GetCountIndicatorLength(version, encodingMode), mode, utf8, analysis.EciMode);

        var actual = new byte[count];
        var written = RmQRBinaryEncoder.EncodeDataCodewords(text, version, ecc, in analysis, actual);

        await Assert.That(written).IsEqualTo(count);
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            Assert.Fail($"{version}-{ecc} {mode} len {text.Length}: expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}");
        }
    }

    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task Numeric_EveryLengthUpToCapacity_MinMaxRandom(RmQRVersion version, RmQREccLevel ecc)
    {
        var max = RmQRVersionSelector.GetMaxDataLength(version, ecc, EncodingMode.Numeric);
        for (var length = 0; length <= max; length++)
        {
            await AssertParity(new string('0', length), version, ecc, "Numeric", false);
            await AssertParity(new string('9', length), version, ecc, "Numeric", false);
            await AssertParity(PseudoRandom("0123456789", length, length + (int)version), version, ecc, "Numeric", false);
        }
    }

    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task Alphanumeric_EveryLengthUpToCapacity_MinMaxRandom(RmQRVersion version, RmQREccLevel ecc)
    {
        var max = RmQRVersionSelector.GetMaxDataLength(version, ecc, EncodingMode.Alphanumeric);
        for (var length = 0; length <= max; length++)
        {
            await AssertParity(new string('0', length), version, ecc, "Alphanumeric", false);
            await AssertParity(new string(':', length), version, ecc, "Alphanumeric", false);
            await AssertParity(Cyclic(AlphanumericAlphabet, length, length), version, ecc, "Alphanumeric", false);
            await AssertParity(PseudoRandom(AlphanumericAlphabet, length, 31 * length + (int)version), version, ecc, "Alphanumeric", false);
        }
    }

    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task ByteLatin1_EveryLengthUpToCapacity_MinMaxRandom(RmQRVersion version, RmQREccLevel ecc)
    {
        var max = RmQRVersionSelector.GetMaxDataLength(version, ecc, EncodingMode.Byte);
        var latin1 = new string(Enumerable.Range(0, 256).Select(c => (char)c).ToArray());
        for (var length = 0; length <= max; length++)
        {
            await AssertParity(new string('\0', length), version, ecc, "Byte", false);
            await AssertParity(new string('ÿ', length), version, ecc, "Byte", false);
            await AssertParity(Cyclic(latin1, length, length * 7), version, ecc, "Byte", false);
        }
    }

    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task ByteLatin1Eci3_EveryLengthUpToEciCapacity_FullRange(RmQRVersion version, RmQREccLevel ecc)
    {
        var max = RmQRVersionSelector.GetMaxDataLength(version, ecc, EncodingMode.Byte, EciMode.Iso8859_1);
        var latin1 = new string(Enumerable.Range(0, 256).Select(c => (char)c).ToArray());
        for (var length = 0; length <= max; length++)
            await AssertParity(Cyclic(latin1, length, length * 7), version, ecc, "Byte", false, EciMode.Iso8859_1);
    }

    [Test]
    [MethodDataSource(nameof(AllVersionEcc))]
    public async Task ByteUtf8_MultiByteAndSurrogates_UpToCapacity(RmQRVersion version, RmQREccLevel ecc)
    {
        var maxBytes = RmQRVersionSelector.GetMaxDataLength(version, ecc, EncodingMode.Byte, EciMode.Utf8);
        // 2-, 3- and 4-byte sequences (é, こ, 😀) and a lone surrogate (encoded as U+FFFD, 3 bytes, by UTF8Encoding).
        var pieces = new[] { "é", "こ", "😀", "\uD800", "a" };
        var text = "";
        var i = 0;
        while (true)
        {
            var next = text + pieces[i % pieces.Length];
            if (System.Text.Encoding.UTF8.GetByteCount(next) > maxBytes)
                break;
            text = next;
            i++;
            await AssertParity(text, version, ecc, "Byte", true);
        }
    }

    [Test]
    public async Task Terminator_AllShorteningClasses_AtR7x43M()
    {
        // 48 data bits at R7x43-M numeric: header 7 bits, so lengths 12 (47 bits: 1-bit
        // terminator), 11 (44 → 3-bit terminator + 1 align), 10 (41 → 3 + 4 align),
        // 9 (37 → 3 + 8), i.e. every terminator/alignment class the naive reference exercises.
        foreach (var length in new[] { 12, 11, 10, 9, 8, 0 })
            await AssertParity(new string('5', length), RmQRVersion.R7x43, RmQREccLevel.M, "Numeric", false);
    }
}
