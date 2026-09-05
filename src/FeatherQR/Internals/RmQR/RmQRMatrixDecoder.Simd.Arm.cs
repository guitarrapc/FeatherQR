#if NET8_0_OR_GREATER
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace FeatherQR.Internals.RmQr;

/// <summary>
/// ARM64 pair-plane extraction kernel: the byte grid is transposed straight into
/// per-column-PAIR words, which are then compressed to the codeword stream a run at
/// a time. No module byte is touched more than once and no output bit is handled
/// individually.
/// </summary>
/// <remarks>
/// Deliberately not a port of the x64 bit-plane kernel. NEON has neither PEXT nor
/// PDEP, so instead of a plane per column plus a deposit step, one 32-bit lane holds a
/// whole column pair: bit <c>2j+1</c> is the pair's right column and <c>2j</c> its
/// left, where j counts data rows in walk order. The walk alternates between a pair's
/// two columns on every row, so that word already <em>is</em> the pair's output field
/// with the function modules still in it, and the pair operation collapses to a single
/// PEXT-shaped compress with no deposit at all.
/// <para>
/// The compress uses what ARM does have. A pair's data mask is a handful of runs of
/// consecutive bits, because function modules come from rectangular blocks (finder,
/// sub-finder, format, alignment) rather than scattered modules, and one run is one AND
/// plus one shift. The runs of the whole symbol are one flat table, so no pair pays for
/// another pair's worst case.
/// </para>
/// <para>
/// Measured ratios, the per-bit cost model behind having no size switch, and the
/// rejected alternatives are recorded in .github/docs/specs/rmqr-decoder.md.
/// </para>
/// </remarks>
internal static partial class RmQRMatrixDecoder
{
    /// <summary>
    /// Extracts the codeword stream through pair-interleaved column planes. Writes
    /// every byte of <paramref name="stream"/>.
    /// </summary>
    /// <remarks>
    /// Blocks are transposed in descending order, which is walk order, so each block's
    /// four pairs are consumed straight out of the vector that produced them and no
    /// plane buffer is materialized. Steps take 32 columns while four whole blocks
    /// remain and 16 afterwards: a fixed 32-column step would round a 43-column symbol
    /// up to 64 columns and pay for half of its work twice.
    /// </remarks>
    private static void ExtractCodewordsPairPlanes(ReadOnlySpan<byte> modules, int width, int height, PairPlaneLayout layout, Span<byte> stream)
    {
        Debug.Assert(AdvSimd.Arm64.IsSupported, "The pair-plane tier is only dispatched on ARM64.");
        Debug.Assert(width >= 16, "The column step needs at least 16 columns; the narrowest rMQR symbol is 27.");

        ref var src = ref MemoryMarshal.GetReference(modules);
        ref var dst = ref MemoryMarshal.GetReference(stream);
        ref var runs = ref MemoryMarshal.GetArrayDataReference(layout.Runs);
        ref var blockEnd = ref MemoryMarshal.GetArrayDataReference(layout.BlockRunEnd);
        ref var planeXor = ref MemoryMarshal.GetArrayDataReference(layout.PlaneXor);

        // Four pairs per block, four blocks per widest step.
        Span<uint> block = stackalloc uint[16];
        ref var lane = ref MemoryMarshal.GetReference(block);

        var one = Vector128.Create((byte)1);
        var odd = Vector128.Create(0x55555555u);
        var align = Vector128.Create(-(32 - layout.FieldBits));
        var downwardLanes = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(layout.DownwardLanes));

        // 2 bits x up to 15 data rows does not fit in a ushort, so the rows are
        // accumulated in two groups of at most 8 and rejoined once per block.
        var dataRows = height - 2;
        var lowRows = dataRows < 8 ? dataRows : 8;
        var join = Vector128.Create(2 * lowRows);

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
                var merged = PairBits32(ref q, one);
                lowUpper = AdvSimd.ShiftLeftAndInsert(AdvSimd.ZeroExtendWideningLower(merged.GetLower()), lowUpper, 2);
                highUpper = AdvSimd.ShiftLeftAndInsert(AdvSimd.ZeroExtendWideningUpper(merged), highUpper, 2);
                q = ref Unsafe.Subtract(ref q, width);
            }
            for (var row = lowRows; row >= 1; row--)
            {
                var merged = PairBits32(ref q, one);
                lowLower = AdvSimd.ShiftLeftAndInsert(AdvSimd.ZeroExtendWideningLower(merged.GetLower()), lowLower, 2);
                highLower = AdvSimd.ShiftLeftAndInsert(AdvSimd.ZeroExtendWideningUpper(merged), highLower, 2);
                q = ref Unsafe.Subtract(ref q, width);
            }

            var mask = (nuint)(start * 4);
            Store(JoinRows(lowUpper, lowLower, join, false), downwardLanes, odd, align, ref planeXor, mask, ref lane);
            Store(JoinRows(lowUpper, lowLower, join, true), downwardLanes, odd, align, ref planeXor, mask + 4, ref Unsafe.Add(ref lane, 4));
            Store(JoinRows(highUpper, highLower, join, false), downwardLanes, odd, align, ref planeXor, mask + 8, ref Unsafe.Add(ref lane, 8));
            Store(JoinRows(highUpper, highLower, join, true), downwardLanes, odd, align, ref planeXor, mask + 12, ref Unsafe.Add(ref lane, 12));

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
            // With a single block left the step overlaps the block above it, which has
            // already been emitted; transposing it twice is cheaper than carrying a
            // third step shape, and the emit below skips it.
            var start = current >= 1 ? current - 1 : 0;
            var upper = Vector128<ushort>.Zero;
            var lower = Vector128<ushort>.Zero;
            ref var q = ref Unsafe.Add(ref src, (nint)(height - 2) * width + start * 8);
            for (var row = height - 2; row > lowRows; row--)
            {
                upper = AdvSimd.ShiftLeftAndInsert(PairBits16(ref q, one), upper, 2);
                q = ref Unsafe.Subtract(ref q, width);
            }
            for (var row = lowRows; row >= 1; row--)
            {
                lower = AdvSimd.ShiftLeftAndInsert(PairBits16(ref q, one), lower, 2);
                q = ref Unsafe.Subtract(ref q, width);
            }

            var mask = (nuint)(start * 4);
            Store(JoinRows(upper, lower, join, false), downwardLanes, odd, align, ref planeXor, mask, ref lane);
            Store(JoinRows(upper, lower, join, true), downwardLanes, odd, align, ref planeXor, mask + 4, ref Unsafe.Add(ref lane, 4));

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
        // The walk is truncated to whole codewords, so this only pads a stream whose
        // final pair was cut off mid-byte, which the layout builder never produces.
        for (; written < stream.Length; written++)
        {
            Unsafe.Add(ref dst, written) = 0;
        }
    }

    /// <summary>
    /// One row of 16 columns as 8 lanes of <c>(right &lt;&lt; 1) | left</c>, right being
    /// the odd column. The two columns are merged while still bytes — a pair value is at
    /// most 3 — so one widening feeds the accumulator instead of one per column.
    /// </summary>
    /// <remarks>
    /// The load reads past the columns it uses and past the row end rather than peeling
    /// a tail: rows 1..h−2 always have a row below them and every rMQR width is at least
    /// 27, so the read stays inside the width × height grid and the extra lanes are
    /// discarded. The dark test is <c>min(value, 1)</c>, not a compare, because modules
    /// are "0 = light, non-zero = dark".
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> PairBits16(ref byte q, Vector128<byte> one)
    {
        var dark = Vector128.Min(Vector128.LoadUnsafe(ref q), one);
        var merged = AdvSimd.ShiftLeftAndInsert(AdvSimd.Arm64.UnzipEven(dark, dark), AdvSimd.Arm64.UnzipOdd(dark, dark), 1);
        return AdvSimd.ZeroExtendWideningLower(merged.GetLower());
    }

    /// <summary>
    /// The same for 32 columns, as 16 byte lanes. UZP1/UZP2 take two source vectors, so
    /// unzipping two loaded rows against each other splits 32 columns into evens and odds
    /// for the same two instructions that split 16.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> PairBits32(ref byte q, Vector128<byte> one)
    {
        var first = Vector128.Min(Vector128.LoadUnsafe(ref q), one);
        var second = Vector128.Min(Vector128.LoadUnsafe(ref q, 16), one);
        return AdvSimd.ShiftLeftAndInsert(AdvSimd.Arm64.UnzipEven(first, second), AdvSimd.Arm64.UnzipOdd(first, second), 1);
    }

    /// <summary>Rejoins the two row groups of four pairs into their 32-bit words.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> JoinRows(Vector128<ushort> upper, Vector128<ushort> lower, Vector128<int> join, bool high)
        => high
            ? AdvSimd.ShiftLogical(AdvSimd.ZeroExtendWideningUpper(upper), join) | AdvSimd.ZeroExtendWideningUpper(lower)
            : AdvSimd.ShiftLogical(AdvSimd.ZeroExtendWideningLower(upper.GetLower()), join) | AdvSimd.ZeroExtendWideningLower(lower.GetLower());

    /// <summary>
    /// Finishes four pairs and stores them: pairs walked downward need the row-reversed
    /// word, then the data mask is applied in plane coordinates.
    /// </summary>
    /// <remarks>
    /// The reversed word is the forward word with its 2-bit groups in reverse order, so
    /// it is produced once per block from the finished accumulator (RBIT + REV32 reverses
    /// all 32 bits, one adjacent-bit swap restores each group's internal order, and the
    /// shift drops the zero bits the reversal moved to the bottom) instead of costing a
    /// shift, a shift and an OR on every row. Which of the two a lane needs is fixed by
    /// the pair's parity, so the choice is a constant vector and one BSL.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(Vector128<uint> forward, Vector128<uint> downwardLanes, Vector128<uint> odd, Vector128<int> align, ref uint planeXor, nuint at, ref uint destination)
    {
        var flipped = AdvSimd.ReverseElement8(AdvSimd.Arm64.ReverseElementBits(forward.AsByte()).AsUInt32());
        var swapped = ((flipped >> 1) & odd) | ((flipped & odd) << 1);
        var backward = AdvSimd.ShiftLogical(swapped, align);
        (AdvSimd.BitwiseSelect(downwardLanes, backward, forward) ^ Vector128.LoadUnsafe(ref planeXor, at))
            .StoreUnsafe(ref destination);
    }

    /// <summary>
    /// Compresses one block's pairs into the stream: each run contributes one AND, one
    /// shift and one OR into the bit accumulator, which is drained a whole 32-bit word at
    /// a time. The drain is a branch on purpose — the run lengths are per-version
    /// constants, so its pattern is periodic and the predictor learns it; making it
    /// branchless measured 13-22 % slower.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Emit(ref byte dst, ref uint lanes, ref uint runs, nuint end, ref ulong accumulator, ref int accumulated, ref nint written, ref nuint at)
    {
        for (; at < end; at += 3)
        {
            var word = Unsafe.Add(ref lanes, (nuint)Unsafe.Add(ref runs, at));
            var pack = Unsafe.Add(ref runs, at + 2);
            var bits = (word & Unsafe.Add(ref runs, at + 1)) >> (int)(pack & 0xFFFF);

            // A pair contributes at most 2 * (17 - 2) = 30 bits and the accumulator is
            // drained below 32 after every run, so it never holds more than 61.
            var count = (int)(pack >> 16);
            accumulator = (accumulator << count) | bits;
            accumulated += count;
            if (accumulated >= 32)
            {
                accumulated -= 32;
                // The stream is big-endian and the accumulator is not, which on ARM is
                // one REV plus one store rather than four extracted byte stores.
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, written),
                    BinaryPrimitives.ReverseEndianness((uint)(accumulator >> accumulated)));
                written += 4;
            }
        }
    }
}
#endif
