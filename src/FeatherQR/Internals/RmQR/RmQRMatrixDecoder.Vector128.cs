#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals.RmQR;

/// <summary>
/// The ARM64 pair-plane kernel on portable vectors, for x64 without fast PEXT and WebAssembly: the same layout, block order and
/// run compression (<see cref="Emit"/>), with NEON's three instructions that have no portable form replaced.
/// </summary>
/// <remarks>
/// A pair's two columns are adjacent bytes, so one 16-bit lane holds both and <c>x | x &gt;&gt; 7</c> merges them where NEON unzips;
/// the lanes stay 16-bit, so no widening feeds the accumulator. The row insert is a shift and an OR where NEON has SLI, and the
/// row-reversed word is four swap steps where NEON has RBIT and REV32, once per block.
/// </remarks>
internal static partial class RmQRMatrixDecoder
{
    /// <summary>Extracts the codeword stream through pair-interleaved column planes. Writes every byte of <paramref name="stream"/>.</summary>
    private static void ExtractCodewordsPairPlanesVector128(ReadOnlySpan<byte> modules, int width, int height, PairPlaneLayout layout, Span<byte> stream)
    {
        ref var src = ref MemoryMarshal.GetReference(modules);
        ref var dst = ref MemoryMarshal.GetReference(stream);
        ref var runs = ref MemoryMarshal.GetArrayDataReference(layout.Runs);
        ref var blockEnd = ref MemoryMarshal.GetArrayDataReference(layout.BlockRunEnd);
        ref var planeXor = ref MemoryMarshal.GetArrayDataReference(layout.PlaneXor);

        Span<uint> block = stackalloc uint[16];
        ref var lane = ref MemoryMarshal.GetReference(block);

        var one = Vector128.Create((byte)1);
        var align = 32 - layout.FieldBits;
        var downwardLanes = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(layout.DownwardLanes));

        var dataRows = height - 2;
        var lowRows = dataRows < 8 ? dataRows : 8;
        var join = 2 * lowRows;

        ulong accumulator = 0;
        var accumulated = 0;
        nint written = 0;
        nuint at = 0;

        var current = layout.Blocks - 1;
        while (current >= 3)
        {
            var start = current - 3;
            var lowUpper = Vector128<ushort>.Zero;
            var lowLower = Vector128<ushort>.Zero;
            var highUpper = Vector128<ushort>.Zero;
            var highLower = Vector128<ushort>.Zero;
            ref var q = ref Unsafe.Add(ref src, (nint)(height - 2) * width + start * 8);
            for (var row = height - 2; row > lowRows; row--)
            {
                lowUpper = (lowUpper << 2) | PairBits(ref q, one);
                highUpper = (highUpper << 2) | PairBits(ref Unsafe.Add(ref q, 16), one);
                q = ref Unsafe.Subtract(ref q, width);
            }
            for (var row = lowRows; row >= 1; row--)
            {
                lowLower = (lowLower << 2) | PairBits(ref q, one);
                highLower = (highLower << 2) | PairBits(ref Unsafe.Add(ref q, 16), one);
                q = ref Unsafe.Subtract(ref q, width);
            }

            var mask = (nuint)(start * 4);
            StorePairs(JoinRowsVector128(lowUpper, lowLower, join, false), downwardLanes, align, ref planeXor, mask, ref lane);
            StorePairs(JoinRowsVector128(lowUpper, lowLower, join, true), downwardLanes, align, ref planeXor, mask + 4, ref Unsafe.Add(ref lane, 4));
            StorePairs(JoinRowsVector128(highUpper, highLower, join, false), downwardLanes, align, ref planeXor, mask + 8, ref Unsafe.Add(ref lane, 8));
            StorePairs(JoinRowsVector128(highUpper, highLower, join, true), downwardLanes, align, ref planeXor, mask + 12, ref Unsafe.Add(ref lane, 12));

            for (var quarter = 3; quarter >= 0; quarter--)
            {
                Emit(ref dst, ref Unsafe.Add(ref lane, (nuint)(quarter * 4)), ref runs,
                    (nuint)Unsafe.Add(ref blockEnd, (nuint)(start + quarter)),
                    ref accumulator, ref accumulated, ref written, ref at);
            }
            current = start - 1;
        }

        while (current >= 0)
        {
            // With a single block left the step overlaps the block above it, which has already been emitted; the emit below skips it.
            var start = current >= 1 ? current - 1 : 0;
            var upper = Vector128<ushort>.Zero;
            var lower = Vector128<ushort>.Zero;
            ref var q = ref Unsafe.Add(ref src, (nint)(height - 2) * width + start * 8);
            for (var row = height - 2; row > lowRows; row--)
            {
                upper = (upper << 2) | PairBits(ref q, one);
                q = ref Unsafe.Subtract(ref q, width);
            }
            for (var row = lowRows; row >= 1; row--)
            {
                lower = (lower << 2) | PairBits(ref q, one);
                q = ref Unsafe.Subtract(ref q, width);
            }

            var mask = (nuint)(start * 4);
            StorePairs(JoinRowsVector128(upper, lower, join, false), downwardLanes, align, ref planeXor, mask, ref lane);
            StorePairs(JoinRowsVector128(upper, lower, join, true), downwardLanes, align, ref planeXor, mask + 4, ref Unsafe.Add(ref lane, 4));

            for (var half = 1; half >= 0; half--)
            {
                if (start + half > current) continue;
                Emit(ref dst, ref Unsafe.Add(ref lane, (nuint)(half * 4)), ref runs,
                    (nuint)Unsafe.Add(ref blockEnd, (nuint)(start + half)),
                    ref accumulator, ref accumulated, ref written, ref at);
            }
            current = start - 1;
        }

        while (accumulated >= 8)
        {
            accumulated -= 8;
            Unsafe.Add(ref dst, written++) = (byte)(accumulator >> accumulated);
        }
        for (; written < stream.Length; written++)
        {
            Unsafe.Add(ref dst, written) = 0;
        }
    }

    /// <summary>
    /// One row of 16 columns as 8 lanes of <c>(right &lt;&lt; 1) | left</c>. The load reads past the columns it uses, as the ARM64 kernel's
    /// does (see <see cref="PairBits16"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> PairBits(ref byte q, Vector128<byte> one)
    {
        var dark = Vector128.Min(Vector128.LoadUnsafe(ref q), one).AsUInt16();
        return (dark | (dark >> 7)) & Vector128.Create((ushort)3);
    }

    /// <summary>Rejoins the two row groups of four pairs into their 32-bit words.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> JoinRowsVector128(Vector128<ushort> upper, Vector128<ushort> lower, int join, bool high)
        => high
            ? (Vector128.WidenUpper(upper) << join) | Vector128.WidenUpper(lower)
            : (Vector128.WidenLower(upper) << join) | Vector128.WidenLower(lower);

    /// <summary>
    /// Finishes four pairs and stores them: pairs walked downward take the word with its 2-bit groups in reverse order, then the data
    /// mask is applied in plane coordinates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StorePairs(Vector128<uint> forward, Vector128<uint> downwardLanes, int align, ref uint planeXor, nuint at, ref uint destination)
    {
        var x = ((forward >> 2) & Vector128.Create(0x33333333u)) | ((forward & Vector128.Create(0x33333333u)) << 2);
        x = ((x >> 4) & Vector128.Create(0x0F0F0F0Fu)) | ((x & Vector128.Create(0x0F0F0F0Fu)) << 4);
        var bytes = x.AsUInt16();
        x = ((bytes >> 8) | (bytes << 8)).AsUInt32();
        x = (x >> 16) | (x << 16);
        (Vector128.ConditionalSelect(downwardLanes, x >> align, forward) ^ Vector128.LoadUnsafe(ref planeXor, at))
            .StoreUnsafe(ref destination);
    }
}
#endif
