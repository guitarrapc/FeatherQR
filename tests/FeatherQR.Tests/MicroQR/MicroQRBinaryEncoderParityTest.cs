using TUnit.Assertions.Enums;
using System.Text;
using FeatherQR.Internals;
using FeatherQR.Internals.MicroQR;

namespace FeatherQR.Tests;

/// <summary>
/// Parity tests for the optimized MicroQRBinaryEncoder against an independent
/// naive reference (string-based bit assembly straight from the ISO/IEC 18004
/// Micro QR rules). Covers all 8 valid (version, ECC) combinations, every
/// supported mode, every length up to capacity, edge contents (min/max chars,
/// full Latin-1 range) and UTF-8 fallbacks including surrogate handling.
/// </summary>
public class MicroQRBinaryEncoderParityTest
{
    private const string AlnumAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    public static IEnumerable<(MicroQRVersion Version, MicroQREccLevel Ecc)> AllCombos()
    {
        yield return (MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly);
        yield return (MicroQRVersion.M2, MicroQREccLevel.L);
        yield return (MicroQRVersion.M2, MicroQREccLevel.M);
        yield return (MicroQRVersion.M3, MicroQREccLevel.L);
        yield return (MicroQRVersion.M3, MicroQREccLevel.M);
        yield return (MicroQRVersion.M4, MicroQREccLevel.L);
        yield return (MicroQRVersion.M4, MicroQREccLevel.M);
        yield return (MicroQRVersion.M4, MicroQREccLevel.Q);
    }

    [Test]
    [MethodDataSource(nameof(AllCombos))]
    public async Task EncodeDataCodewords_Numeric_MatchesNaiveReference(MicroQRVersion version, MicroQREccLevel ecc)
    {
        var rng = new Random(42);
        var max = MaxLength(version, ecc, EncodingMode.Numeric);
        for (var len = 0; len <= max; len++)
        {
            await AssertParity(new string('0', len), version, ecc, EncodingMode.Numeric);
            await AssertParity(new string('9', len), version, ecc, EncodingMode.Numeric);
            await AssertParity(RandomString(rng, len, "0123456789"), version, ecc, EncodingMode.Numeric);
        }
    }

    [Test]
    [MethodDataSource(nameof(AllCombos))]
    public async Task EncodeDataCodewords_Alphanumeric_MatchesNaiveReference(MicroQRVersion version, MicroQREccLevel ecc)
    {
        if (version < MicroQRVersion.M2)
        {
            return; // M1 has no alphanumeric mode
        }

        var rng = new Random(43);
        var max = MaxLength(version, ecc, EncodingMode.Alphanumeric);
        for (var len = 0; len <= max; len++)
        {
            await AssertParity(new string(' ', len), version, ecc, EncodingMode.Alphanumeric);
            await AssertParity(new string(':', len), version, ecc, EncodingMode.Alphanumeric);
            await AssertParity(RandomString(rng, len, AlnumAlphabet), version, ecc, EncodingMode.Alphanumeric);
        }
    }

    [Test]
    [MethodDataSource(nameof(AllCombos))]
    public async Task EncodeDataCodewords_ByteLatin1_MatchesNaiveReference(MicroQRVersion version, MicroQREccLevel ecc)
    {
        if (version < MicroQRVersion.M3)
        {
            return; // byte mode requires M3+
        }

        var rng = new Random(44);
        var max = MaxLength(version, ecc, EncodingMode.Byte);
        for (var len = 0; len <= max; len++)
        {
            await AssertParity(new string('ÿ', len), version, ecc, EncodingMode.Byte);
            await AssertParity(new string('a', len), version, ecc, EncodingMode.Byte);
            // full Latin-1 range incl. NUL
            var chars = new char[len];
            for (var i = 0; i < len; i++)
            {
                chars[i] = (char)rng.Next(0x100);
            }
            await AssertParity(new string(chars), version, ecc, EncodingMode.Byte);
        }
    }

    [Test]
    [MethodDataSource(nameof(AllCombos))]
    public async Task EncodeDataCodewords_ByteUtf8_MatchesNaiveReference(MicroQRVersion version, MicroQREccLevel ecc)
    {
        if (version < MicroQRVersion.M3)
        {
            return;
        }

        var maxBytes = MaxLength(version, ecc, EncodingMode.Byte);

        // 3-byte chars (kana) at every fitting length
        for (var chars = 1; chars * 3 <= maxBytes; chars++)
        {
            await AssertParity(new string('あ', chars), version, ecc, EncodingMode.Byte);
        }
        // 2-byte chars (U+0100)
        for (var chars = 1; chars * 2 <= maxBytes; chars++)
        {
            await AssertParity(new string('Ā', chars), version, ecc, EncodingMode.Byte);
        }
        // mixed 1-byte + multi-byte
        if (maxBytes >= 4)
        {
            await AssertParity("aん" + new string('b', maxBytes - 4), version, ecc, EncodingMode.Byte);
        }
    }

    [Test]
    [MethodDataSource(nameof(AllCombos))]
    public async Task EncodeDataCodewords_ByteSingleNonLatin1AtEveryPosition_FallsBackToUtf8(MicroQRVersion version, MicroQREccLevel ecc)
    {
        if (version < MicroQRVersion.M3)
        {
            return;
        }

        // A single 2-byte char at EVERY index of every payload length: the
        // vectorized Latin-1 detector reads the text as overlapping windows,
        // so each position must be proven visible to it (a missed char would
        // silently emit a truncated byte instead of the UTF-8 fallback).
        var maxBytes = MaxLength(version, ecc, EncodingMode.Byte);
        for (var len = 1; len + 1 <= maxBytes; len++) // encoded bytes = len + 1
        {
            for (var at = 0; at < len; at++)
            {
                var chars = new char[len];
                Array.Fill(chars, 'x');
                chars[at] = 'Ā'; // U+0100: smallest non-Latin-1 code unit
                await AssertParity(new string(chars), version, ecc, EncodingMode.Byte);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(AllCombos))]
    public async Task EncodeDataCodewords_ByteSurrogates_MatchEncodingUtf8(MicroQRVersion version, MicroQREccLevel ecc)
    {
        if (version < MicroQRVersion.M3)
        {
            return;
        }

        var maxBytes = MaxLength(version, ecc, EncodingMode.Byte);

        // Encoding.UTF8 semantics: valid pair -> 4 bytes, lone surrogate -> U+FFFD (3 bytes)
        if (maxBytes >= 4)
        {
            await AssertParity("😀", version, ecc, EncodingMode.Byte);      // valid pair
            await AssertParity("a😀", version, ecc, EncodingMode.Byte);     // pair at odd offset
        }
        if (maxBytes >= 3)
        {
            await AssertParity("\uD800", version, ecc, EncodingMode.Byte);  // lone high
            await AssertParity("\uDC00", version, ecc, EncodingMode.Byte);  // lone low
        }
        if (maxBytes >= 7)
        {
            await AssertParity("\uD800a\uDC00", version, ecc, EncodingMode.Byte);
        }
    }

    [Test]
    public async Task EncodeDataCodewords_ExactSizedDestination_WritesOnlyCodewordCount()
    {
        // destination smaller than 16 bytes must still work and must not be
        // written beyond the returned codeword count
        var expected = EncodeReference("123", MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly, EncodingMode.Numeric);
        var buffer = new byte[3]; // exactly codewordCount for M1
        var written = MicroQRBinaryEncoder.EncodeDataCodewords("123".AsSpan(), MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly, EncodingMode.Numeric, buffer);

        await Assert.That(written).IsEqualTo(3);
        await Assert.That(buffer).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    // ---------------------------------------------------------------
    // Naive reference: string-based bit assembly, independent of the
    // production accumulator (BitsToBytes-style, mirrors ISO 18004 rules).
    // ---------------------------------------------------------------

    private static async Task AssertParity(string text, MicroQRVersion version, MicroQREccLevel ecc, EncodingMode mode)
    {
        var expected = EncodeReference(text, version, ecc, mode);

        var buffer = new byte[16];
        var written = MicroQRBinaryEncoder.EncodeDataCodewords(text.AsSpan(), version, ecc, mode, buffer);

        await Assert.That(written).IsEqualTo(expected.Length);
        await Assert.That(buffer[..written]).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    private static byte[] EncodeReference(string text, MicroQRVersion version, MicroQREccLevel ecc, EncodingMode mode)
    {
        var capacityBits = MicroQRConstants.GetDataBitCapacity(version, ecc);
        var codewordCount = MicroQRConstants.GetDataCodewordCount(version, ecc);

        var bits = new StringBuilder();

        var modeBits = (int)version - 1;
        if (modeBits > 0)
        {
            var modeValue = mode switch
            {
                EncodingMode.Numeric => 0,
                EncodingMode.Alphanumeric => 1,
                EncodingMode.Byte => 2,
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
            AppendBits(bits, modeValue, modeBits);
        }

        var countBits = mode == EncodingMode.Numeric ? (int)version + 2 : (int)version + 1;
        switch (mode)
        {
            case EncodingMode.Numeric:
                AppendBits(bits, text.Length, countBits);
                var i = 0;
                for (; i + 2 < text.Length; i += 3)
                {
                    AppendBits(bits, int.Parse(text.Substring(i, 3)), 10);
                }
                if (i + 1 < text.Length)
                {
                    AppendBits(bits, int.Parse(text.Substring(i, 2)), 7);
                }
                else if (i < text.Length)
                {
                    AppendBits(bits, int.Parse(text.Substring(i, 1)), 4);
                }
                break;

            case EncodingMode.Alphanumeric:
                AppendBits(bits, text.Length, countBits);
                var j = 0;
                for (; j + 1 < text.Length; j += 2)
                {
                    AppendBits(bits, AlnumAlphabet.IndexOf(text[j]) * 45 + AlnumAlphabet.IndexOf(text[j + 1]), 11);
                }
                if (j < text.Length)
                {
                    AppendBits(bits, AlnumAlphabet.IndexOf(text[j]), 6);
                }
                break;

            case EncodingMode.Byte:
                byte[] payload;
                if (text.All(c => c <= 0xFF))
                {
                    payload = text.Select(c => (byte)c).ToArray();
                }
                else
                {
                    payload = Encoding.UTF8.GetBytes(text);
                }
                AppendBits(bits, payload.Length, countBits);
                foreach (var b in payload)
                {
                    AppendBits(bits, b, 8);
                }
                break;
        }

        // terminator (shortened at capacity)
        var terminator = Math.Min(2 * (int)version + 1, capacityBits - bits.Length);
        bits.Append('0', terminator);

        var bitString = bits.ToString();
        var result = new byte[codewordCount];
        for (var k = 0; k < bitString.Length; k++)
        {
            if (bitString[k] == '1')
            {
                result[k / 8] |= (byte)(0x80 >> (k % 8));
            }
        }

        // alternating pad codewords over whole bytes after the data bits;
        // the final half codeword (M1/M3) stays 0000
        var padStart = (bitString.Length + 7) / 8;
        var fullBytes = capacityBits / 8;
        var padIndex = 0;
        for (var k = padStart; k < fullBytes; k++)
        {
            result[k] = (padIndex++ % 2 == 0) ? (byte)0xEC : (byte)0x11;
        }
        return result;
    }

    private static void AppendBits(StringBuilder sb, int value, int count)
    {
        for (var i = count - 1; i >= 0; i--)
        {
            sb.Append((value & (1 << i)) != 0 ? '1' : '0');
        }
    }

    private static int MaxLength(MicroQRVersion version, MicroQREccLevel ecc, EncodingMode mode)
    {
        var capacity = MicroQRConstants.GetDataBitCapacity(version, ecc);
        var headerBits = ((int)version - 1) + (mode == EncodingMode.Numeric ? (int)version + 2 : (int)version + 1);
        var len = 0;
        while (headerBits + DataBits(mode, len + 1) <= capacity)
        {
            len++;
        }
        return len;
    }

    private static int DataBits(EncodingMode mode, int lengthUnits) => mode switch
    {
        EncodingMode.Numeric => 10 * (lengthUnits / 3) + (lengthUnits % 3 == 1 ? 4 : lengthUnits % 3 == 2 ? 7 : 0),
        EncodingMode.Alphanumeric => 11 * (lengthUnits / 2) + 6 * (lengthUnits % 2),
        _ => 8 * lengthUnits,
    };

    private static string RandomString(Random rng, int length, string alphabet)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[rng.Next(alphabet.Length)];
        }
        return new string(chars);
    }
}
