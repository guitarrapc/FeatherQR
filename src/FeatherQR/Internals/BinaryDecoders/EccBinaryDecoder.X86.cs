#if NET10_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR.Internals.BinaryDecoders;

/// <summary>
/// GFNI syndrome kernel: all ≤30 syndrome accumulators live in one 256-bit register, so every data byte updates every syndrome with one gf2p8mulb, replacing up to 30 scalar GF multiplies per byte (measured ~114x on a version 40 block).
/// </summary>
/// <remarks>
/// GF2P8MULB is hardwired to the AES polynomial 0x11B while QR uses 0x11D, so the kernel runs in an isomorphic image of the field: φ maps GF(0x11D) → GF(0x11B) (constructed from β = the first root of x⁸+x⁴+x³+x²+1 in GF(0x11B); it happens to be an involution, φ = φ⁻¹).
/// One gf2p8affineqb maps each operand in, one maps the accumulator back out at the end.
/// XOR is field addition and φ is GF(2)-linear, so accumulation commutes with the mapping.
/// <para>
/// The Horner recurrence is unrolled ×4 (acc·A⁴ ^ φ(c0)·A³ ^ φ(c1)·A² ^ φ(c2)·A ^ φ(c3), xor-tree reassociated) because the loop is latency-bound on the acc → mulb → xor carried chain; the c·Aⁿ multiplies are off-chain.
/// Measured 29% over unroll ×2 and 47% over the plain recurrence; unroll ×8 stalled (throughput-bound), so ×4 is the converged shape.
/// The constants are baked and locked to the runtime construction by GfniIsomorphismConstants_MatchFirstPrinciplesConstruction.
/// </para>
/// </remarks>
internal static partial class EccBinaryDecoder
{
    /// <summary>
    /// φ = φ⁻¹ as a gf2p8affineqb bit matrix (qword byte (7-i) = matrix row for result bit i, row bit k = bit i of φ(2^k)).
    /// </summary>
    internal const ulong GfniPhiMatrix = 0xFFAACC88F0A0C080UL;

    /// <summary>Lane i = φ(α^i): the per-syndrome Horner multipliers, mapped.</summary>
    internal static ReadOnlySpan<byte> GfniAlphas =>
    [
        0x01, 0x03, 0x05, 0x0F, 0x11, 0x33, 0x55, 0xFF,
        0x1A, 0x2E, 0x72, 0x96, 0xA1, 0xF8, 0x13, 0x35,
        0x5F, 0xE1, 0x38, 0x48, 0xD8, 0x73, 0x95, 0xA4,
        0xF7, 0x02, 0x06, 0x0A, 0x1E, 0x22, 0x66, 0xAA,
    ];

    /// <summary>Lane i = φ(α^2i): the two-step multipliers for the unrolled loop.</summary>
    internal static ReadOnlySpan<byte> GfniAlphasSquared =>
    [
        0x01, 0x05, 0x11, 0x55, 0x1A, 0x72, 0xA1, 0x13,
        0x5F, 0x38, 0xD8, 0x95, 0xF7, 0x06, 0x1E, 0x66,
        0xE5, 0x5C, 0x37, 0xEB, 0x6A, 0xD9, 0x90, 0xE6,
        0x53, 0x04, 0x14, 0x44, 0x4F, 0x68, 0xD3, 0xB2,
    ];

    /// <summary>Lane i = φ(α^3i): the three-step multipliers for the unrolled loop.</summary>
    internal static ReadOnlySpan<byte> GfniAlphasCubed =>
    [
        0x01, 0x0F, 0x55, 0x2E, 0xA1, 0x35, 0x38, 0x73,
        0xF7, 0x0A, 0x66, 0x34, 0x37, 0x26, 0xD9, 0xAB,
        0x53, 0x0C, 0x44, 0xD1, 0xD3, 0xCD, 0x67, 0x3B,
        0x62, 0x08, 0x78, 0x9E, 0x6B, 0x7F, 0xB3, 0xDB,
    ];

    /// <summary>Lane i = φ(α^4i): the four-step multipliers for the unrolled loop.</summary>
    internal static ReadOnlySpan<byte> GfniAlphasFourth =>
    [
        0x01, 0x11, 0x1A, 0xA1, 0x5F, 0xD8, 0xF7, 0x1E,
        0xE5, 0x37, 0x6A, 0x90, 0x53, 0x14, 0x4F, 0xD3,
        0x4C, 0xE0, 0x62, 0x18, 0x83, 0x6B, 0x81, 0x49,
        0xB5, 0x10, 0x0B, 0xBB, 0xFE, 0x87, 0x2F, 0xE9,
    ];

    /// <summary>
    /// Computes the syndromes for one block.
    /// <paramref name="syndromes"/> must have room for <see cref="SyndromeLanes"/> bytes; lanes past <paramref name="eccCount"/> receive syndromes of roots the code does not use and must not be read.
    /// </summary>
    internal static bool ComputeSyndromesGfni(ReadOnlySpan<byte> codeword, int eccCount, Span<byte> syndromes)
    {
        var phi = Vector256.Create(GfniPhiMatrix).AsByte();
        var alphas = Vector256.Create<byte>(GfniAlphas);
        var alphasSquared = Vector256.Create<byte>(GfniAlphasSquared);
        var alphasCubed = Vector256.Create<byte>(GfniAlphasCubed);
        var alphasFourth = Vector256.Create<byte>(GfniAlphasFourth);

        // Horner in the mapped domain, four steps folded per iteration:
        // acc' = acc·A⁴ ^ φ(c0)·A³ ^ φ(c1)·A² ^ φ(c2)·A ^ φ(c3)
        var acc = Vector256<byte>.Zero;
        ref var cw = ref MemoryMarshal.GetReference(codeword);
        var length = codeword.Length;
        nint j = 0;
        for (; j + 4 <= length; j += 4)
        {
            var c0 = Gfni.V256.GaloisFieldAffineTransform(Vector256.Create(Unsafe.Add(ref cw, j)), phi, 0);
            var c1 = Gfni.V256.GaloisFieldAffineTransform(Vector256.Create(Unsafe.Add(ref cw, j + 1)), phi, 0);
            var c2 = Gfni.V256.GaloisFieldAffineTransform(Vector256.Create(Unsafe.Add(ref cw, j + 2)), phi, 0);
            var c3 = Gfni.V256.GaloisFieldAffineTransform(Vector256.Create(Unsafe.Add(ref cw, j + 3)), phi, 0);
            var high = Gfni.V256.GaloisFieldMultiply(c0, alphasCubed) ^ Gfni.V256.GaloisFieldMultiply(c1, alphasSquared);
            var low = Gfni.V256.GaloisFieldMultiply(c2, alphas) ^ c3;
            acc = Gfni.V256.GaloisFieldMultiply(acc, alphasFourth) ^ (high ^ low);
        }
        for (; j < length; j++)
        {
            var mapped = Gfni.V256.GaloisFieldAffineTransform(Vector256.Create(Unsafe.Add(ref cw, j)), phi, 0);
            acc = Gfni.V256.GaloisFieldMultiply(acc, alphas) ^ mapped;
        }

        // Map back (φ is its own inverse) and store the register whole, the same shape
        // the AdvSimd tier stores in: the destination is SyndromeLanes wide by contract,
        // and lanes at or past eccCount hold syndromes of roots the code does not use.
        var result = Gfni.V256.GaloisFieldAffineTransform(acc, phi, 0);
        result.CopyTo(syndromes);

        byte any = 0;
        for (var i = 0; i < eccCount; i++)
        {
            any |= syndromes[i];
        }
        return any != 0;
    }
}
#endif
