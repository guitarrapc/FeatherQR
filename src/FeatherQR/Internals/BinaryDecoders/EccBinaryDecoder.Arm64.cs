#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace FeatherQR.Internals.BinaryDecoders;

/// <summary>
/// ARM64 / NEON syndrome kernel: all ≤30 syndrome accumulators live in two 128-bit registers, and every group of eight data bytes updates every syndrome with two PMULL pairs plus six table reads (measured 11-61x the scalar log-domain kernel as a four-byte step; the eight-byte tree below is a further 2.3x on version 40 blocks).
/// </summary>
/// <remarks>
/// This is deliberately NOT a transliteration of the GFNI kernel in EccBinaryDecoder.Simd.cs.
/// GF2P8MULB multiplies every lane by a per-lane constant in one instruction but is hardwired to the AES polynomial, so the x64 kernel spends its design on a field isomorphism to borrow it.
/// NEON has no such instruction, but PMULL/PMUL carry no fixed modulus — so ARM needs no isomorphism at all and instead pays for the reduction mod 0x11D itself.
/// The force applies at the opposite end, and the resulting kernel shape is different:
/// <code>
/// D(c0..c3) = T3[c0] ^ T2[c1] ^ T1[c2] ^ broadcast(c3)
/// acc'      = Reduce(PMULL(acc, α^8i)) ^ Reduce(PMULL(D(c0..c3), α^4i)) ^ D(c4..c7)
/// </code>
/// <para>
/// Four design points, each of which was measured against its alternative rather than assumed; the number that decided each one is quoted with it:
/// </para>
/// <para>
/// <b>Table reads, not multiplies, for the data terms.</b> <c>c·α^(k·i)</c> depends only on the byte c and the step k, so <see cref="AlphaTables"/> holds it as a ready-made 32-byte vector: three loads + three XORs replace six PMULL + six EOR.
/// The tables cost 24 KB.
/// A 3 KB nibble-split alternative was measured and lost by 20-78%, so the footprint is buying real work — but that verdict is from a core with a 128 KB L1D, which is also why the scalar fallback is kept fast.
/// </para>
/// <para>
/// <b>Reduction by table lookup.</b> The high half of a PMULL product has degree ≤ 6, so splitting it into nibbles indexes two 16-entry tables holding the already reduced contribution: <c>UZP → AND → TBL → EOR</c> instead of <c>UZP → PMULL → UZP → PMUL → EOR</c>.
/// The reduction sits on the carried dependency chain, and every change that shortened that chain won.
/// </para>
/// <para>
/// <b>The reduction tables are hoisted into locals.</b> This is load-bearing, not style: the JIT does not CSE <c>Vector128.Create</c> over a static array across the loop body, so leaving them inline makes every reduction pay two extra loads — worth up to 20% of the kernel.
/// </para>
/// <para>
/// <b>Eight bytes a step, as a tree.</b> With a four-byte step the loop was bound by the carried chain (acc → PMULL → UZP → TBL → EOR, about 14 cycles a step on Apple M2), not by throughput; the second accumulator group ran mostly in the first group's stall slots, which is why halving the vector work bought only about 20%.
/// Horner re-associates: the multiply that joins two four-byte data terms depends on data alone and falls off the chain, so eight bytes cost one carried reduce instead of two. Measured 0.44x of the four-byte step on the syndrome pass alone at version 40-L blocks (148 bytes, 30 ECC) and 0.59x at 40-H (45 bytes), and through <see cref="TryCorrect"/> on a clean block 0.53x to 0.54x at 145 to 153 bytes, 0.75x to 0.83x over rMQR's 25 to 68 byte blocks, 0.76x to 0.81x at Micro QR M3 and M4, level at M2 and 1.07x (5 ns) at M1's five-byte block, Apple M2.
/// A sixteen-byte tree (three off-chain multiplies a step) measured the same at 148 bytes and 10% slower at 45, so the loop is now throughput-bound at about 100 instructions per eight bytes, and two throughput hypotheses did not move it: pairing the two half-row loads through immediate offsets lost 10%, and pre-splitting the multiplier halves to remove the per-PMULL register copy tied.
/// </para>
/// </remarks>
internal static partial class EccBinaryDecoder
{
    /// <summary>Lane i = α^i — the one-step Horner multipliers for the scalar tail.</summary>
    internal static ReadOnlySpan<byte> AdvSimdAlpha1 =>
    [
        0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80,
        0x1D, 0x3A, 0x74, 0xE8, 0xCD, 0x87, 0x13, 0x26,
        0x4C, 0x98, 0x2D, 0x5A, 0xB4, 0x75, 0xEA, 0xC9,
        0x8F, 0x03, 0x06, 0x0C, 0x18, 0x30, 0x60, 0xC0,
    ];

    /// <summary>Lane i = α^(4i) — the multiplier that joins the two four-byte data terms of a step, and the one-step multiplier of the four-byte remainder.</summary>
    internal static ReadOnlySpan<byte> AdvSimdAlpha4 =>
    [
        0x01, 0x10, 0x1D, 0xCD, 0x4C, 0xB4, 0x8F, 0x18,
        0x9D, 0x25, 0x6A, 0xEE, 0x46, 0x14, 0x5D, 0xB9,
        0x5F, 0x99, 0x65, 0x1E, 0xFD, 0x6B, 0xFE, 0x5B,
        0xD9, 0x11, 0x0D, 0xD0, 0x81, 0xF8, 0x3B, 0x97,
    ];

    /// <summary>Lane i = α^(8i) — the carried Horner multiplier of the eight-byte step.</summary>
    internal static ReadOnlySpan<byte> AdvSimdAlpha8 =>
    [
        0x01, 0x1D, 0x4C, 0x8F, 0x9D, 0x6A, 0x46, 0x5D,
        0x5F, 0x65, 0xFD, 0xFE, 0xD9, 0x0D, 0x81, 0x3B,
        0x85, 0x4F, 0xA8, 0x49, 0xE6, 0xFC, 0xE3, 0x95,
        0x82, 0x1C, 0x51, 0xC3, 0x12, 0xF7, 0x2C, 0x1B,
    ];

    /// <summary>Reduction table for the low nibble of a product's high byte: n·x^8 mod 0x11D.</summary>
    internal static ReadOnlySpan<byte> AdvSimdReduceLow =>
    [
        0x00, 0x1D, 0x3A, 0x27, 0x74, 0x69, 0x4E, 0x53,
        0xE8, 0xF5, 0xD2, 0xCF, 0x9C, 0x81, 0xA6, 0xBB,
    ];

    /// <summary>Reduction table for the high nibble of a product's high byte: (n≪4)·x^8 mod 0x11D.</summary>
    internal static ReadOnlySpan<byte> AdvSimdReduceHigh =>
    [
        0x00, 0xCD, 0x87, 0x4A, 0x13, 0xDE, 0x94, 0x59,
        0x26, 0xEB, 0xA1, 0x6C, 0x35, 0xF8, 0xB2, 0x7F,
    ];

    /// <summary>
    /// Lazily built step tables, one 24 KB array holding three 256-entry tables of 32-byte vectors: entry (k-1, c) is lane i → c·α^(k·i) for k = 1..3.
    /// Only the AdvSimd tier reads it, so nothing is allocated on other targets.
    /// </summary>
    private static byte[]? alphaStepTables;

    internal static bool IsAdvSimdTierSupported => AdvSimd.Arm64.IsSupported;

    private const int StepTableStride = 256 * SyndromeLanes;

    private static byte[] AlphaTables
    {
        get
        {
            var tables = Volatile.Read(ref alphaStepTables);
            return tables ?? BuildAlphaTables();
        }
    }

    private static byte[] BuildAlphaTables()
    {
        // Benign race: two threads may build identical tables; the release store
        // guarantees a reader never observes a partially filled array.
        var tables = new byte[3 * StepTableStride];
        var exp = GaloisField.Exp;
        for (var k = 1; k <= 3; k++)
        {
            var baseOffset = (k - 1) * StepTableStride;
            for (var c = 1; c < 256; c++)
            {
                var logC = GaloisField.Log[c];
                var row = baseOffset + c * SyndromeLanes;
                // exponent = (k·i) mod 255, stepped by k rather than divided per lane.
                // With k ≤ 3 and 32 lanes it never actually wraps, but the wrap keeps
                // the loop correct if either bound ever grows. logC + exponent stays
                // under 512, which is why GaloisField.Exp is double length.
                var exponent = 0;
                for (var i = 0; i < SyndromeLanes; i++)
                {
                    // c · α^(k·i); lane 0 is c itself and α^0 = 1.
                    tables[row + i] = exp[logC + exponent];
                    exponent += k;
                    if (exponent >= 255)
                        exponent -= 255;
                }
            }
            // c = 0 stays zero.
        }

        Volatile.Write(ref alphaStepTables, tables);
        return tables;
    }

    /// <summary>Multiplies 16 lanes by 16 per-lane constants, reducing mod 0x11D via nibble tables.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> GfMulAdvSimd(Vector128<byte> a, Vector128<byte> b, Vector128<byte> reduceLow, Vector128<byte> reduceHigh)
    {
        var p0 = AdvSimd.PolynomialMultiplyWideningLower(a.GetLower(), b.GetLower());
        var p1 = AdvSimd.PolynomialMultiplyWideningUpper(a, b);
        var low = AdvSimd.Arm64.UnzipEven(p0.AsByte(), p1.AsByte());
        var high = AdvSimd.Arm64.UnzipOdd(p0.AsByte(), p1.AsByte()); // degree ≤ 6
        return low
             ^ AdvSimd.Arm64.VectorTableLookup(reduceLow, high & Vector128.Create((byte)0x0F))
             ^ AdvSimd.Arm64.VectorTableLookup(reduceHigh, AdvSimd.ShiftRightLogical(high, 4));
    }

    /// <summary>
    /// Computes the syndromes for one block.
    /// <paramref name="syndromes"/> must have room for <see cref="SyndromeLanes"/> bytes; lanes past <paramref name="eccCount"/> receive syndromes of roots the code does not use and must not be read.
    /// </summary>
    internal static bool ComputeSyndromesAdvSimd(ReadOnlySpan<byte> codeword, int eccCount, Span<byte> syndromes)
    {
        var reduceLow = Vector128.Create<byte>(AdvSimdReduceLow);
        var reduceHigh = Vector128.Create<byte>(AdvSimdReduceHigh);
        var alpha1 = Vector128.Create<byte>(AdvSimdAlpha1);
        var alpha4 = Vector128.Create<byte>(AdvSimdAlpha4);
        var alpha8 = Vector128.Create<byte>(AdvSimdAlpha8);

        ref var tables = ref MemoryMarshal.GetArrayDataReference(AlphaTables);
        ref var t1 = ref tables;
        ref var t2 = ref Unsafe.Add(ref tables, StepTableStride);
        ref var t3 = ref Unsafe.Add(ref tables, 2 * StepTableStride);
        ref var cw = ref MemoryMarshal.GetReference(codeword);
        var length = codeword.Length;

        var accLow = Vector128<byte>.Zero;
        nint j = 0;

        if (eccCount <= 16)
        {
            for (; j + 8 <= length; j += 8)
            {
                var d0 = DataTerm(ref t1, ref t2, ref t3, ref cw, j, 0);
                var d1 = DataTerm(ref t1, ref t2, ref t3, ref cw, j + 4, 0);
                accLow = GfMulAdvSimd(accLow, alpha8, reduceLow, reduceHigh)
                       ^ GfMulAdvSimd(d0, alpha4, reduceLow, reduceHigh)
                       ^ d1;
            }
            for (; j + 4 <= length; j += 4)
            {
                accLow = GfMulAdvSimd(accLow, alpha4, reduceLow, reduceHigh) ^ DataTerm(ref t1, ref t2, ref t3, ref cw, j, 0);
            }
            for (; j < length; j++)
            {
                accLow = GfMulAdvSimd(accLow, alpha1, reduceLow, reduceHigh) ^ Vector128.Create(Unsafe.Add(ref cw, j));
            }

            return StoreSyndromes(accLow, Vector128<byte>.Zero, eccCount, syndromes);
        }

        var alpha1High = Vector128.Create<byte>(AdvSimdAlpha1.Slice(16));
        var alpha4High = Vector128.Create<byte>(AdvSimdAlpha4.Slice(16));
        var alpha8High = Vector128.Create<byte>(AdvSimdAlpha8.Slice(16));
        var accHigh = Vector128<byte>.Zero;

        for (; j + 8 <= length; j += 8)
        {
            var d0 = DataTerm(ref t1, ref t2, ref t3, ref cw, j, 0);
            var d1 = DataTerm(ref t1, ref t2, ref t3, ref cw, j + 4, 0);
            var e0 = DataTerm(ref t1, ref t2, ref t3, ref cw, j, 16);
            var e1 = DataTerm(ref t1, ref t2, ref t3, ref cw, j + 4, 16);
            accLow = GfMulAdvSimd(accLow, alpha8, reduceLow, reduceHigh)
                   ^ GfMulAdvSimd(d0, alpha4, reduceLow, reduceHigh)
                   ^ d1;
            accHigh = GfMulAdvSimd(accHigh, alpha8High, reduceLow, reduceHigh)
                    ^ GfMulAdvSimd(e0, alpha4High, reduceLow, reduceHigh)
                    ^ e1;
        }
        for (; j + 4 <= length; j += 4)
        {
            accLow = GfMulAdvSimd(accLow, alpha4, reduceLow, reduceHigh) ^ DataTerm(ref t1, ref t2, ref t3, ref cw, j, 0);
            accHigh = GfMulAdvSimd(accHigh, alpha4High, reduceLow, reduceHigh) ^ DataTerm(ref t1, ref t2, ref t3, ref cw, j, 16);
        }
        for (; j < length; j++)
        {
            var c = Vector128.Create(Unsafe.Add(ref cw, j));
            accLow = GfMulAdvSimd(accLow, alpha1, reduceLow, reduceHigh) ^ c;
            accHigh = GfMulAdvSimd(accHigh, alpha1High, reduceLow, reduceHigh) ^ c;
        }

        return StoreSyndromes(accLow, accHigh, eccCount, syndromes);
    }

    /// <summary>
    /// The data term of four codeword bytes for one 16-lane accumulator group: T3[c0] ^ T2[c1] ^ T1[c2] ^ broadcast(c3), read from lanes <paramref name="half"/>..+15 of each 32-byte table row.
    /// Depends on the data alone, so the JIT is free to schedule it off the carried chain.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> DataTerm(ref byte t1, ref byte t2, ref byte t3, ref byte cw, nint j, nuint half)
        => Vector128.LoadUnsafe(ref t3, (nuint)Unsafe.Add(ref cw, j) * SyndromeLanes + half)
         ^ Vector128.LoadUnsafe(ref t2, (nuint)Unsafe.Add(ref cw, j + 1) * SyndromeLanes + half)
         ^ Vector128.LoadUnsafe(ref t1, (nuint)Unsafe.Add(ref cw, j + 2) * SyndromeLanes + half)
         ^ Vector128.Create(Unsafe.Add(ref cw, j + 3));

    /// <summary>
    /// Stores both accumulator groups and reports whether any live syndrome is non-zero.
    /// Lanes at or past eccCount are masked out of the test: they hold syndromes of unused roots and are non-zero even for a clean block.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool StoreSyndromes(Vector128<byte> low, Vector128<byte> high, int eccCount, Span<byte> syndromes)
    {
        var lanes = Vector128.Create((byte)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);
        var limit = Vector128.Create((byte)eccCount);
        var live = (low & Vector128.LessThan(lanes, limit))
                 | (high & Vector128.LessThan(lanes + Vector128.Create((byte)16), limit));

        ref var destination = ref MemoryMarshal.GetReference(syndromes);
        low.StoreUnsafe(ref destination);
        high.StoreUnsafe(ref destination, 16);
        return live != Vector128<byte>.Zero;
    }
}
#endif
