#if NET8_0_OR_GREATER
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR.Internals.RmQr;

/// <summary>
/// Bit-plane extraction kernel: the byte grid is transposed once into per-column bit
/// planes, then each column pair of the zigzag walk is emitted with one PEXT + PDEP
/// per column, so no module byte is touched more than once and no output bit is
/// handled individually.
/// </summary>
/// <remarks>
/// A column of a symbol spans at most height − 2 = 15 data rows, so it fits in a
/// <see cref="ushort"/> and the transpose covers 16 columns per 256-bit vector.
/// Two planes are produced in the same pass: one in walk order for upward pairs and
/// a row-reversed one for downward pairs, which lets a single pair of PEXT/PDEP
/// masks serve both directions (see <c>BuildExtractLayout</c> for why the orders
/// line up).
/// <para>
/// Requires fast PDEP/PEXT (see <see cref="HardwareCapabilities.HasFastPext"/>);
/// pre-Zen 3 AMD parts and every non-x64 target take the portable tier instead.
/// Measured 16-64x over the per-module reference walk across R7x43..R17x139, versus
/// 8-12x for the portable tier; AVX-512 (32 columns per step) measured inside noise
/// of AVX2 on Zen 4, so there is no 512-bit tier.
/// </para>
/// </remarks>
internal static partial class RmQRMatrixDecoder
{
    /// <summary>
    /// Extracts the codeword stream through the column bit planes. Writes every byte
    /// of <paramref name="stream"/>.
    /// </summary>
    private static void ExtractCodewordsBitPlanes(ReadOnlySpan<byte> modules, int width, int height, uint[] pairs, Span<byte> stream)
    {
        // Two planes of PlaneStride columns each; the second holds the row-reversed copy.
        Span<ushort> planes = stackalloc ushort[PlaneStride * 2];
        ref var plane = ref MemoryMarshal.GetReference(planes);

        BuildColumnPlanes(ref MemoryMarshal.GetReference(modules), width, height, ref plane);
        EmitColumnPairs(ref MemoryMarshal.GetReference(stream), ref plane, pairs, stream.Length);
    }

    /// <summary>
    /// Transposes rows 1..h−2 (rows 0 and h−1 are timing patterns and never carry
    /// data) into column bit planes: <c>plane[c]</c> bit <c>r−1</c> is row <c>r</c>,
    /// and <c>plane[PlaneStride + c]</c> bit <c>h−2−r</c> is the same module in
    /// reversed row order.
    /// </summary>
    /// <remarks>
    /// The vector step reads 16 bytes at a time and runs past the end of a row rather
    /// than peeling a scalar tail: rows 1..h−2 always have at least one row below
    /// them, so the read stays inside the width × height grid for every rMQR width
    /// (the narrowest is 27, and the bound is width ≥ 15). The lanes past the width
    /// hold whatever follows in the next row, and no column pair ever reads column
    /// w−1 or beyond, so they are never observed. The dark test is
    /// <c>min(value, 1)</c>, not a compare: modules are "0 = light, non-zero = dark",
    /// and on x64 a compare would round-trip through a mask register.
    /// </remarks>
    private static void BuildColumnPlanes(ref byte src, int width, int height, ref ushort plane)
    {
        // Preconditions, both guaranteed by the only caller: AVX2 is supported (the
        // tier gate in ExtractCodewords requires it) and width >= 16 (the narrowest
        // rMQR symbol is 27 wide, and the overread argument above needs width >= 15).
        // They are asserted rather than branched on, so this file has one honest entry
        // condition instead of a scalar half that can never run and is never tested.
        Debug.Assert(Avx2.IsSupported, "The bit-plane tier is only dispatched on AVX2.");
        Debug.Assert(width >= 16, "The 16-byte column step needs at least 16 columns.");

        ref var reversed = ref Unsafe.Add(ref plane, PlaneStride);
        var top = (ushort)(height - 3); // highest bit index of a plane word

        var one = Vector256.Create((ushort)1);
        var topShift = Vector128.CreateScalar(top);
        var oneShift = Vector128.CreateScalar((ushort)1);
        for (nint c = 0; c < width; c += 16)
        {
            var forward = Vector256<ushort>.Zero;
            var backward = Vector256<ushort>.Zero;
            ref var q = ref Unsafe.Add(ref src, (nint)(height - 2) * width + c);
            for (var row = height - 2; row >= 1; row--)
            {
                var value = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref q)).AsUInt16();
                var dark = Vector256.Min(value, one);
                forward = Avx2.ShiftLeftLogical(forward, oneShift) | dark;
                backward = Avx2.ShiftRightLogical(backward, oneShift) | Avx2.ShiftLeftLogical(dark, topShift);
                q = ref Unsafe.Subtract(ref q, (nint)width);
            }
            forward.StoreUnsafe(ref plane, (nuint)c);
            backward.StoreUnsafe(ref reversed, (nuint)c);
        }
    }

    /// <summary>
    /// Emits the codeword stream one column pair at a time. PEXT selects the rows of
    /// a column that carry data, PDEP scatters them into the pair's MSB-first bit
    /// field, and the pair's precomputed data mask is applied with one XOR. Pairs
    /// interrupted by function patterns are handled by the same two instructions as
    /// clean ones, so there is no per-bit fallback path.
    /// </summary>
    private static void EmitColumnPairs(ref byte dst, ref ushort plane, uint[] pairs, int codewords)
    {
        ref var p = ref MemoryMarshal.GetReference(pairs.AsSpan());
        var length = (nuint)pairs.Length;
        ulong accumulator = 0;
        var accumulated = 0;
        nint written = 0;

        for (nuint k = 0; k < length; k += 6)
        {
            var header = Unsafe.Add(ref p, k);
            var column = (nuint)(header & 0xFFFF);
            var right = (uint)Unsafe.Add(ref plane, column);
            var left = (uint)Unsafe.Add(ref plane, column - 1);

            var bits = (Bmi2.ParallelBitDeposit(Bmi2.ParallelBitExtract(right, Unsafe.Add(ref p, k + 1)), Unsafe.Add(ref p, k + 2))
                      | Bmi2.ParallelBitDeposit(Bmi2.ParallelBitExtract(left, Unsafe.Add(ref p, k + 3)), Unsafe.Add(ref p, k + 4)))
                      ^ Unsafe.Add(ref p, k + 5);

            // A pair contributes at most 2 * (17 - 2) = 30 bits and the accumulator is
            // drained below 32 after every pair, so it never holds more than 61.
            var count = (int)(header >> 16);
            accumulator = (accumulator << count) | bits;
            accumulated += count;
            if (accumulated >= 32)
            {
                accumulated -= 32;
                var word = (uint)(accumulator >> accumulated);
                Unsafe.Add(ref dst, written) = (byte)(word >> 24);
                Unsafe.Add(ref dst, written + 1) = (byte)(word >> 16);
                Unsafe.Add(ref dst, written + 2) = (byte)(word >> 8);
                Unsafe.Add(ref dst, written + 3) = (byte)word;
                written += 4;
            }
        }

        while (accumulated >= 8)
        {
            accumulated -= 8;
            Unsafe.Add(ref dst, written++) = (byte)(accumulator >> accumulated);
        }
        // The walk is truncated to whole codewords, so this only pads a stream whose
        // final pair was cut off mid-byte, which the layout builder never produces.
        for (; written < codewords; written++)
        {
            Unsafe.Add(ref dst, written) = 0;
        }
    }
}
#endif
