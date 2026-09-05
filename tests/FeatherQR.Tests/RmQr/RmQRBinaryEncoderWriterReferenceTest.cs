using System.Runtime.InteropServices;
using System.Text;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// Every <see cref="RmQRBinaryEncoder"/> segment writer against an independently
/// written per-character reference, for every length up to the largest rMQR
/// capacity and every pending-bit phase the header can leave.
/// </summary>
/// <remarks>
/// This is deliberately NOT the same check as
/// <see cref="RmQRBinaryEncoderKernelParityTest"/>. That one runs the same writer
/// with <c>vectorized</c> true and false and compares the two, which only proves the
/// two tiers agree — on a machine whose vector ISA the writer does not use, both
/// calls take the same code and the comparison degenerates into a self-check. It
/// also cannot see a bug in a batching loop that BOTH tiers share.
///
/// The reference below emits the spec's bit groups one at a time (3 digits ⇒ 10
/// bits, 2 ⇒ 7, 1 ⇒ 4; a character pair ⇒ 11 bits, a lone character ⇒ 6; one byte
/// ⇒ 8 bits) with no batching, no SWAR and no accumulator, so it shares nothing
/// with the writers' grouping loops. Comparison is on the LOGICAL bit stream
/// (stored bytes followed by pending bits, MSB first) because a batched append may
/// legitimately split the same stream differently between stored and pending.
/// </remarks>
public class RmQRBinaryEncoderWriterReferenceTest
{
    private const string AlphanumericAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
    private const int MaxNumeric = 361;      // R17x139-M
    private const int MaxAlphanumeric = 219; // R17x139-M
    private const int MaxByte = 150;         // R17x139-M

    private delegate void Writer(ref byte dest, ref ulong acc, ref int accBits, ref int bytePos, ReadOnlySpan<char> text, bool vectorized);

    public static IEnumerable<int> HeaderPhases() => Enumerable.Range(0, 13);

    [Test]
    [MethodDataSource(nameof(HeaderPhases))]
    public async Task Numeric_MatchesReference_EveryLengthAndPhase(int headerBits)
    {
        for (var length = 0; length <= MaxNumeric; length++)
        {
            var text = PseudoRandom("0123456789", length, length * 3 + headerBits);
            await AssertAgainstReference(RmQRBinaryEncoder.WriteNumeric, text, headerBits, NumericReference, "Numeric");
        }

        // Alphabet extremes: all-zero and all-nine digits exercise the SWAR bias fold.
        foreach (var length in new[] { 0, 1, 2, 3, 8, 9, 11, 12, 13, 24, 25, MaxNumeric })
        {
            await AssertAgainstReference(RmQRBinaryEncoder.WriteNumeric, new string('0', length), headerBits, NumericReference, "Numeric zeros");
            await AssertAgainstReference(RmQRBinaryEncoder.WriteNumeric, new string('9', length), headerBits, NumericReference, "Numeric nines");
        }
    }

    [Test]
    [MethodDataSource(nameof(HeaderPhases))]
    public async Task Alphanumeric_MatchesReference_EveryLengthAndPhase(int headerBits)
    {
        for (var length = 0; length <= MaxAlphanumeric; length++)
        {
            var text = PseudoRandom(AlphanumericAlphabet, length, 31 * length + headerBits);
            await AssertAgainstReference(RmQRBinaryEncoder.WriteAlphanumeric, text, headerBits, AlphanumericReference, "Alnum");
        }

        // Value extremes: '0' is value 0 and ':' is value 44, the ends of the 45-value
        // alphabet, so a pair of them is the smallest / largest 11-bit group.
        foreach (var length in new[] { 0, 1, 2, 3, 4, 7, 8, 9, 15, 16, 17, MaxAlphanumeric })
        {
            await AssertAgainstReference(RmQRBinaryEncoder.WriteAlphanumeric, new string('0', length), headerBits, AlphanumericReference, "Alnum zeros");
            await AssertAgainstReference(RmQRBinaryEncoder.WriteAlphanumeric, new string(':', length), headerBits, AlphanumericReference, "Alnum colons");
        }
    }

    [Test]
    [MethodDataSource(nameof(HeaderPhases))]
    public async Task Latin1_MatchesReference_EveryLengthAndPhase(int headerBits)
    {
        var latin1 = BuildLatin1Alphabet();
        for (var length = 0; length <= MaxByte; length++)
        {
            var text = PseudoRandom(latin1, length, 17 * length + headerBits);
            await AssertAgainstReference(RmQRBinaryEncoder.WriteLatin1, text, headerBits, Latin1Reference, "Latin1");
        }

        // Byte extremes: 0x00 and 0xFF pin the ends of the byte range, and 0xFF also
        // pins that a narrowing kernel truncates rather than saturating something else.
        foreach (var length in new[] { 0, 1, 7, 8, 9, 15, 16, 17, 31, 32, 33, MaxByte })
        {
            await AssertAgainstReference(RmQRBinaryEncoder.WriteLatin1, new string('\0', length), headerBits, Latin1Reference, "Latin1 zeros");
            await AssertAgainstReference(RmQRBinaryEncoder.WriteLatin1, new string('ÿ', length), headerBits, Latin1Reference, "Latin1 0xFF");
        }
    }

    private static async Task AssertAgainstReference(Writer writer, string text, int headerBits, Func<string, string> reference, string label)
    {
        var seedAcc = headerBits == 0 ? 0UL : ulong.MaxValue << (64 - headerBits);
        var expected = new string('1', headerBits) + reference(text);

        foreach (var vectorized in new[] { false, true })
        {
            var stored = new byte[512];
            var acc = seedAcc;
            var accBits = headerBits;
            var bytePos = 0;

            writer(ref MemoryMarshal.GetArrayDataReference(stored), ref acc, ref accBits, ref bytePos, text.AsSpan(), vectorized);

            var actual = Logical(stored, bytePos, acc, accBits);
            if (actual != expected)
            {
                Assert.Fail($"{label} len {text.Length} header {headerBits} vectorized {vectorized}:\n expected {expected}\n actual   {actual}");
            }
        }
        await Task.CompletedTask;
    }

    // ---- independent references: one spec bit group at a time, no batching ----

    private static string NumericReference(string digits)
    {
        var bits = new StringBuilder();
        var i = 0;
        for (; i + 3 <= digits.Length; i += 3)
            bits.Append(Bits((digits[i] - '0') * 100 + (digits[i + 1] - '0') * 10 + (digits[i + 2] - '0'), 10));
        if (digits.Length - i == 2)
            bits.Append(Bits((digits[i] - '0') * 10 + (digits[i + 1] - '0'), 7));
        else if (digits.Length - i == 1)
            bits.Append(Bits(digits[i] - '0', 4));
        return bits.ToString();
    }

    private static string AlphanumericReference(string chars)
    {
        var bits = new StringBuilder();
        var i = 0;
        for (; i + 2 <= chars.Length; i += 2)
            bits.Append(Bits(AlphanumericValue(chars[i]) * 45 + AlphanumericValue(chars[i + 1]), 11));
        if (i < chars.Length)
            bits.Append(Bits(AlphanumericValue(chars[i]), 6));
        return bits.ToString();
    }

    private static string Latin1Reference(string text)
    {
        var bits = new StringBuilder();
        foreach (var c in text)
            bits.Append(Bits((byte)c, 8));
        return bits.ToString();
    }

    /// <summary>Independent alphabet mapping (ISO/IEC 18004 Table 5), written from the spec order.</summary>
    private static int AlphanumericValue(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'A' && c <= 'Z') return c - 'A' + 10;
        return c switch
        {
            ' ' => 36,
            '$' => 37,
            '%' => 38,
            '*' => 39,
            '+' => 40,
            '-' => 41,
            '.' => 42,
            '/' => 43,
            ':' => 44,
            _ => throw new ArgumentOutOfRangeException(nameof(c), $"'{c}' is not alphanumeric"),
        };
    }

    private static string Bits(int value, int width)
    {
        var chars = new char[width];
        for (var i = 0; i < width; i++)
            chars[i] = (char)('0' + ((value >> (width - 1 - i)) & 1));
        return new string(chars);
    }

    private static string Logical(byte[] stored, int bytePos, ulong acc, int accBits)
    {
        var bits = new StringBuilder(bytePos * 8 + accBits);
        for (var i = 0; i < bytePos; i++)
            bits.Append(Convert.ToString(stored[i], 2).PadLeft(8, '0'));
        for (var i = 0; i < accBits; i++)
            bits.Append((char)('0' + ((acc >> (63 - i)) & 1)));
        return bits.ToString();
    }

    private static string BuildLatin1Alphabet()
    {
        var chars = new char[256];
        for (var i = 0; i < 256; i++)
            chars[i] = (char)i;
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
}
