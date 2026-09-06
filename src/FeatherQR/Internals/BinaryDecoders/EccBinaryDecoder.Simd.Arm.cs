#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Threading;

namespace FeatherQR.Internals.BinaryDecoders;

/// <summary>
/// ARM64 / NEON syndrome kernel: all ≤30 syndrome accumulators live in two 128-bit registers, so every group of four data bytes updates every syndrome with one PMULL pair plus three table reads (measured 11-61x the scalar log-domain kernel).
/// </summary>
/// <remarks>
/// This is deliberately NOT a transliteration of the GFNI kernel in EccBinaryDecoder.Simd.cs.
/// GF2P8MULB multiplies every lane by a per-lane constant in one instruction but is hardwired to the AES polynomial, so the x64 kernel spends its design on a field isomorphism to borrow it.
/// NEON has no such instruction, but PMULL/PMUL carry no fixed modulus — so ARM needs no isomorphism at all and instead pays for the reduction mod 0x11D itself.
/// The force applies at the opposite end, and the resulting kernel shape is different:
/// <code>
/// acc' = Reduce(PMULL(acc, α^4i)) ^ T3[c0] ^ T2[c1] ^ T1[c2] ^ broadcast(c3)
/// </code>
/// <para>
/// Three design points, each of which was measured against its alternative rather than assumed; the number that decided each one is quoted with it:
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
/// Blocks needing ≤ 16 syndromes drive only the first accumulator group.
/// Halving the vector work buys only about 20% of the time, which is the clearest evidence that this loop is bound by the acc → PMULL → reduce → EOR chain rather than by throughput: the second group was running mostly in the first group's stall slots.
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

    /// <summary>Lane i = α^(4i) — the four-step multiplier for the unrolled loop.</summary>
    internal static ReadOnlySpan<byte> AdvSimdAlpha4 =>
    [
        0x01, 0x10, 0x1D, 0xCD, 0x4C, 0xB4, 0x8F, 0x18,
        0x9D, 0x25, 0x6A, 0xEE, 0x46, 0x14, 0x5D, 0xB9,
        0x5F, 0x99, 0x65, 0x1E, 0xFD, 0x6B, 0xFE, 0x5B,
        0xD9, 0x11, 0x0D, 0xD0, 0x81, 0xF8, 0x3B, 0x97,
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
            for (; j + 4 <= length; j += 4)
            {
                accLow = GfMulAdvSimd(accLow, alpha4, reduceLow, reduceHigh)
                       ^ Vector128.LoadUnsafe(ref t3, (nuint)Unsafe.Add(ref cw, j) * SyndromeLanes)
                       ^ Vector128.LoadUnsafe(ref t2, (nuint)Unsafe.Add(ref cw, j + 1) * SyndromeLanes)
                       ^ Vector128.LoadUnsafe(ref t1, (nuint)Unsafe.Add(ref cw, j + 2) * SyndromeLanes)
                       ^ Vector128.Create(Unsafe.Add(ref cw, j + 3));
            }
            for (; j < length; j++)
            {
                accLow = GfMulAdvSimd(accLow, alpha1, reduceLow, reduceHigh) ^ Vector128.Create(Unsafe.Add(ref cw, j));
            }

            return StoreSyndromes(accLow, Vector128<byte>.Zero, eccCount, syndromes);
        }

        var alpha1High = Vector128.Create<byte>(AdvSimdAlpha1.Slice(16));
        var alpha4High = Vector128.Create<byte>(AdvSimdAlpha4.Slice(16));
        var accHigh = Vector128<byte>.Zero;

        for (; j + 4 <= length; j += 4)
        {
            var o0 = (nuint)Unsafe.Add(ref cw, j) * SyndromeLanes;
            var o1 = (nuint)Unsafe.Add(ref cw, j + 1) * SyndromeLanes;
            var o2 = (nuint)Unsafe.Add(ref cw, j + 2) * SyndromeLanes;
            var c3 = Vector128.Create(Unsafe.Add(ref cw, j + 3));

            accLow = GfMulAdvSimd(accLow, alpha4, reduceLow, reduceHigh)
                   ^ Vector128.LoadUnsafe(ref t3, o0)
                   ^ Vector128.LoadUnsafe(ref t2, o1)
                   ^ Vector128.LoadUnsafe(ref t1, o2)
                   ^ c3;
            accHigh = GfMulAdvSimd(accHigh, alpha4High, reduceLow, reduceHigh)
                    ^ Vector128.LoadUnsafe(ref t3, o0 + 16)
                    ^ Vector128.LoadUnsafe(ref t2, o1 + 16)
                    ^ Vector128.LoadUnsafe(ref t1, o2 + 16)
                    ^ c3;
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
