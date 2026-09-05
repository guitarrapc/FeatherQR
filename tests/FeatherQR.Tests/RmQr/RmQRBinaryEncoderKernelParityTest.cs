using System.Runtime.InteropServices;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// Vectorized segment writers of <see cref="RmQRBinaryEncoder"/> (x64 SSSE3/SSE4.1
/// numeric + alphanumeric, SSE2 Latin-1) versus their scalar SWAR / table fallbacks,
/// called directly with <c>vectorized</c> = true / false on the same writer state.
/// Covers every length up to the largest rMQR capacity, every pending-bit phase the
/// header can leave (0..12 bits), min / max / cyclic / pseudo-random contents.
/// On hardware without the vector ISA both calls take the scalar path and the test
/// degenerates to a self-check; <see cref="RmQRBinaryEncoderParityTest"/> pins the
/// end-to-end stream against the naive reference either way.
/// </summary>
public class RmQRBinaryEncoderKernelParityTest
{
    private const string AlphanumericAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
    private const int MaxNumeric = 361;      // R17x139-M
    private const int MaxAlphanumeric = 219; // R17x139-M
    private const int MaxByte = 150;         // R17x139-M

    private delegate void Writer(ref byte dest, ref ulong acc, ref int accBits, ref int bytePos, ReadOnlySpan<char> text, bool vectorized);

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

    private static string Cyclic(string alphabet, int length, int offset)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[(i + offset) % alphabet.Length];
        return new string(chars);
    }

    /// <summary>Runs the writer twice (scalar / vectorized) from the same pre-seeded state and compares bytes + pending state.</summary>
    private static async Task AssertKernelParity(Writer writer, string text, int headerBits, string label)
    {
        // seed: headerBits pending bits (all ones keeps the merge with the first append visible)
        var seedAcc = headerBits == 0 ? 0UL : ulong.MaxValue << (64 - headerBits);

        var scalar = new byte[256];
        var vector = new byte[256];
        ulong accS = seedAcc, accV = seedAcc;
        int bitsS = headerBits, bitsV = headerBits;
        int posS = 0, posV = 0;

        writer(ref MemoryMarshal.GetArrayDataReference(scalar), ref accS, ref bitsS, ref posS, text.AsSpan(), vectorized: false);
        writer(ref MemoryMarshal.GetArrayDataReference(vector), ref accV, ref bitsV, ref posV, text.AsSpan(), vectorized: true);

        // The two paths may split the same bit stream differently between stored bytes
        // and pending bits (AppendWide can leave exactly 32 bits pending where Append
        // would have flushed them), so compare the logical stream: stored bytes followed
        // by the pending bits, MSB first.
        var streamS = Logical(scalar, posS, accS, bitsS);
        var streamV = Logical(vector, posV, accV, bitsV);
        if (streamS != streamV)
        {
            Assert.Fail($"{label} len {text.Length} header {headerBits}: scalar pos {posS} bits {bitsS} / vector pos {posV} bits {bitsV}\n scalar {streamS}\n vector {streamV}");
        }
        await Task.CompletedTask;
    }

    private static string Logical(byte[] stored, int bytePos, ulong acc, int accBits)
    {
        var bits = new System.Text.StringBuilder(bytePos * 8 + accBits);
        for (var i = 0; i < bytePos; i++)
            bits.Append(Convert.ToString(stored[i], 2).PadLeft(8, '0'));
        for (var i = 0; i < accBits; i++)
            bits.Append((acc >> (63 - i)) & 1);
        return bits.ToString();
    }

    public static IEnumerable<int> HeaderPhases() => Enumerable.Range(0, 13); // 3-bit mode + 0..9-bit count = 3..12 pending, plus 0

    [Test]
    [MethodDataSource(nameof(HeaderPhases))]
    public async Task Numeric_VectorizedMatchesScalar_EveryLengthAndPhase(int headerBits)
    {
        for (var length = 0; length <= MaxNumeric; length++)
        {
            await AssertKernelParity(RmQRBinaryEncoder.WriteNumeric, new string('0', length), headerBits, "Numeric zeros");
            await AssertKernelParity(RmQRBinaryEncoder.WriteNumeric, new string('9', length), headerBits, "Numeric nines");
            await AssertKernelParity(RmQRBinaryEncoder.WriteNumeric, PseudoRandom("0123456789", length, length * 3 + headerBits), headerBits, "Numeric random");
        }
    }

    [Test]
    [MethodDataSource(nameof(HeaderPhases))]
    public async Task Alphanumeric_VectorizedMatchesScalar_EveryLengthAndPhase(int headerBits)
    {
        for (var length = 0; length <= MaxAlphanumeric; length++)
        {
            await AssertKernelParity(RmQRBinaryEncoder.WriteAlphanumeric, new string('0', length), headerBits, "Alnum zeros");
            await AssertKernelParity(RmQRBinaryEncoder.WriteAlphanumeric, new string(':', length), headerBits, "Alnum colons");
            await AssertKernelParity(RmQRBinaryEncoder.WriteAlphanumeric, Cyclic(AlphanumericAlphabet, length, length), headerBits, "Alnum cyclic");
            await AssertKernelParity(RmQRBinaryEncoder.WriteAlphanumeric, PseudoRandom(AlphanumericAlphabet, length, 31 * length + headerBits), headerBits, "Alnum random");
        }
    }

    [Test]
    public async Task Alphanumeric_EverySymbolInEveryLane()
    {
        // each of the 45 symbols through every one of the 8 vector lanes (offset classes:
        // row 0x2_ specials, row 0x3_ digits + ':', letters)
        foreach (var symbol in AlphanumericAlphabet)
        {
            for (var lane = 0; lane < 8; lane++)
            {
                var chars = new string('A', 16).ToCharArray();
                chars[lane] = symbol;
                chars[8 + (7 - lane)] = symbol;
                await AssertKernelParity(RmQRBinaryEncoder.WriteAlphanumeric, new string(chars), 8, $"Alnum symbol '{symbol}' lane {lane}");
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(HeaderPhases))]
    public async Task Latin1_VectorizedMatchesScalar_EveryLengthAndPhase(int headerBits)
    {
        var latin1 = new string(Enumerable.Range(0, 256).Select(c => (char)c).ToArray());
        for (var length = 0; length <= MaxByte; length++)
        {
            await AssertKernelParity(RmQRBinaryEncoder.WriteLatin1, new string('\0', length), headerBits, "Latin1 zeros");
            await AssertKernelParity(RmQRBinaryEncoder.WriteLatin1, new string('ÿ', length), headerBits, "Latin1 0xFF");
            await AssertKernelParity(RmQRBinaryEncoder.WriteLatin1, Cyclic(latin1, length, length * 7), headerBits, "Latin1 cyclic");
        }
    }
}
