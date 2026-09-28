using TUnit.Assertions.Enums;
using System.Text;
using FeatherQR.Internals.MicroQR;

namespace FeatherQR.Tests;

/// <summary>
/// Boundary tests for MicroQRBinaryEncoder's 128-bit register accumulator
/// primitives (Append / Append64), which back every Micro QR bit-stream write.
/// Positions 0 and 64 are special (shift-by-64 would wrap on x64, guarded by
/// the two-step shift / lo-only branches), and word-straddling appends split
/// across hi and lo. The reference is an independent bit-string builder.
/// </summary>
public class MicroQRBitAccumulatorUnitTest
{
    public static IEnumerable<int> Append64Positions() => [0, 1, 7, 8, 31, 32, 63, 64];

    [Test]
    [MethodDataSource(nameof(Append64Positions))]
    public async Task Append64_BoundaryPositions_MatchBitStringReference(int pos)
    {
        ulong hi = 0, lo = 0;
        var p = 0;
        var bits = new StringBuilder();
        AppendPrefix(ref hi, ref lo, ref p, bits, pos);

        const ulong value = 0xF0DEBC9A78563412UL; // asymmetric: detects any misalignment
        MicroQRBinaryEncoder.Append64(ref hi, ref lo, ref p, value);
        AppendValueBits(bits, value, 64);

        await Assert.That(p).IsEqualTo(pos + 64);
        await Assert.That(ToBytes(hi, lo)).IsEquivalentTo(BitsToBytes16(bits.ToString()), CollectionOrdering.Matching);
    }

    public static IEnumerable<(int Pos, int BitCount)> AppendCases()
    {
        // hi-only (end <= 64)
        yield return (0, 1);
        yield return (0, 32);
        yield return (7, 8);
        yield return (31, 32);
        yield return (32, 32);
        yield return (62, 2);
        yield return (63, 1);
        // straddle (pos < 64 < end)
        yield return (63, 2);
        yield return (48, 32);
        yield return (33, 32);
        yield return (57, 30);
        // lo-only (pos >= 64)
        yield return (64, 1);
        yield return (64, 32);
        yield return (96, 32);
        yield return (127, 1);
    }

    [Test]
    [MethodDataSource(nameof(AppendCases))]
    public async Task Append_BoundaryPositions_MatchBitStringReference(int pos, int bitCount)
    {
        ulong hi = 0, lo = 0;
        var p = 0;
        var bits = new StringBuilder();
        AppendPrefix(ref hi, ref lo, ref p, bits, pos);

        var value = unchecked((int)0xA5C3F169);
        MicroQRBinaryEncoder.Append(ref hi, ref lo, ref p, value, bitCount);
        AppendValueBits(bits, (uint)value, bitCount);

        await Assert.That(p).IsEqualTo(pos + bitCount);
        await Assert.That(ToBytes(hi, lo)).IsEquivalentTo(BitsToBytes16(bits.ToString()), CollectionOrdering.Matching);
    }

    public static IEnumerable<(int Pos, int BitCount)> AppendWideCases()
    {
        // hi-only (end <= 64)
        yield return (0, 8);
        yield return (0, 56);
        yield return (8, 56);
        yield return (24, 40);
        yield return (56, 8);
        // straddle (pos < 64 < end)
        yield return (16, 56);
        yield return (40, 32);
        yield return (56, 16);
        yield return (63, 8);
        // lo-only (pos >= 64)
        yield return (64, 8);
        yield return (64, 56);
        yield return (72, 56);
        yield return (120, 8);
    }

    [Test]
    [MethodDataSource(nameof(AppendWideCases))]
    public async Task AppendWide_BoundaryPositions_MatchBitStringReference(int pos, int bitCount)
    {
        ulong hi = 0, lo = 0;
        var p = 0;
        var bits = new StringBuilder();
        AppendPrefix(ref hi, ref lo, ref p, bits, pos);

        const ulong pattern = 0xA5C3_F169_5B87_2D4EUL; // asymmetric: detects any misalignment
        var value = pattern & ((1UL << bitCount) - 1); // AppendWide contract: pre-masked
        MicroQRBinaryEncoder.AppendWide(ref hi, ref lo, ref p, value, bitCount);
        AppendValueBits(bits, value, bitCount);

        await Assert.That(p).IsEqualTo(pos + bitCount);
        await Assert.That(ToBytes(hi, lo)).IsEquivalentTo(BitsToBytes16(bits.ToString()), CollectionOrdering.Matching);
    }

    /// <summary>Advances the accumulator and the reference bit string by
    /// <paramref name="count"/> bits of a non-uniform pattern, in 1-32 bit chunks.</summary>
    private static void AppendPrefix(ref ulong hi, ref ulong lo, ref int pos, StringBuilder bits, int count)
    {
        const uint pattern = 0x6D2B93A1;
        while (count > 0)
        {
            var chunk = Math.Min(32, count);
            MicroQRBinaryEncoder.Append(ref hi, ref lo, ref pos, unchecked((int)pattern), chunk);
            AppendValueBits(bits, pattern, chunk);
            count -= chunk;
        }
    }

    private static void AppendValueBits(StringBuilder bits, ulong value, int count)
    {
        for (var i = count - 1; i >= 0; i--)
        {
            bits.Append((value >> i & 1) != 0 ? '1' : '0');
        }
    }

    private static byte[] ToBytes(ulong hi, ulong lo)
    {
        var bytes = new byte[16];
        for (var i = 0; i < 8; i++)
        {
            bytes[i] = (byte)(hi >> (56 - i * 8));
            bytes[8 + i] = (byte)(lo >> (56 - i * 8));
        }
        return bytes;
    }

    private static byte[] BitsToBytes16(string bits)
    {
        var bytes = new byte[16];
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i] == '1')
            {
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }
        return bytes;
    }
}
