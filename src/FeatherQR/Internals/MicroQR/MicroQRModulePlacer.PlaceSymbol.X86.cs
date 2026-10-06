#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR.Internals.MicroQR;

internal static partial class MicroQRModulePlacer
{
    /// <summary>
    /// BMI2+AVX2 pipeline: placement is a static per-row PEXT/PDEP permutation of the packed stream words (the zigzag is a fixed bit permutation per size, so each row's data bits are gathered/scattered branch-free with no cross-row dependency), the stream word count is dispatched per size (M1 fits one word, M2 two), and the unpack expands 32 modules per AVX2 step.
    /// Measured over the SSSE3 pipeline: M4 -29%, M3 -22%, M2 -10%, M1 -5% (kernel benchmark rounds 8-11).
    /// </summary>
    private static int PlaceCoreBmi2(Span<byte> matrix, int size, int stride, ReadOnlySpan<ulong> stream, MicroQRVersion version, MicroQREccLevel eccLevel, int forcedMask)
    {
        var w0 = stream[0];
        var w1 = stream[1];
        var w2 = stream[2];

        Span<ulong> rows = stackalloc ulong[17];
        rows = rows.Slice(0, size);
        FuncPackedRows(size).CopyTo(rows);

        // Zero extract masks would make unused words no-ops, but the loads and
        // BMI ops are not free: sizes 11/13 skip the guaranteed-zero words.
        var tbl = _pextPlaceTables[(size - 11) >> 1];
        ref var te = ref MemoryMarshal.GetArrayDataReference(tbl);
        if (size >= 15)
        {
            for (var r = 1; r < size; r++)
            {
                ref var e = ref Unsafe.Add(ref te, r * 6);
                rows[r] |= Bmi2.X64.ParallelBitDeposit(Bmi2.X64.ParallelBitExtract(w0, e), Unsafe.Add(ref e, 1))
                         | Bmi2.X64.ParallelBitDeposit(Bmi2.X64.ParallelBitExtract(w1, Unsafe.Add(ref e, 2)), Unsafe.Add(ref e, 3))
                         | Bmi2.X64.ParallelBitDeposit(Bmi2.X64.ParallelBitExtract(w2, Unsafe.Add(ref e, 4)), Unsafe.Add(ref e, 5));
            }
        }
        else if (size == 13)
        {
            for (var r = 1; r < 13; r++)
            {
                ref var e = ref Unsafe.Add(ref te, r * 6);
                rows[r] |= Bmi2.X64.ParallelBitDeposit(Bmi2.X64.ParallelBitExtract(w0, e), Unsafe.Add(ref e, 1))
                         | Bmi2.X64.ParallelBitDeposit(Bmi2.X64.ParallelBitExtract(w1, Unsafe.Add(ref e, 2)), Unsafe.Add(ref e, 3));
            }
        }
        else
        {
            for (var r = 1; r < 11; r++)
            {
                ref var e = ref Unsafe.Add(ref te, r * 6);
                rows[r] |= Bmi2.X64.ParallelBitDeposit(Bmi2.X64.ParallelBitExtract(w0, e), Unsafe.Add(ref e, 1));
            }
        }

        // Scoring and format information, identical to BuildPackedRows.
        var last = size - 1;
        ulong colDark = 0;
        for (var i = 1; i < size; i++)
        {
            colDark |= ((rows[i] >> last) & 1) << i;
        }
        var rowDark = rows[last] & ~1ul;

        var mask = forcedMask >= 0 ? forcedMask : SelectMaskFromEdges(colDark, rowDark, size);

        var formatBits = _formatBitsTable[(MicroQRConstants.GetSymbolNumber(version, eccLevel) << 2) | mask];
        rows[8] = (rows[8] & ~0x1FEul) | ((ulong)_reverseByte[(formatBits >> 7) & 0xFF] << 1);
        for (var row = 7; row >= 1; row--)
        {
            var bit = (ulong)((formatBits >> (row - 1)) & 1);
            rows[row] = (rows[row] & ~(1ul << 8)) | (bit << 8);
        }

        // Unpack with the mask applied on the fly: one 32-module AVX2 step per
        // row while the store ends at or before the last row's last module
        // (bits >= size are zero, so the overrun writes light modules: the
        // bytes between rows stay light, and following rows are rewritten in
        // ascending order), 16-module steps for the last rows, scalar-safe tail
        // for the final row.
        ref var buf = ref MemoryMarshal.GetReference(matrix);
        var windowEnd = (size - 1) * stride + size;
        var rowOffset = 0;
        for (var y = 0; y < last; y++, rowOffset += stride)
        {
            var bits = rows[y] ^ MaskDelta(mask, y, size);
            if (rowOffset + 32 <= windowEnd)
            {
                WriteExpand32(ref Unsafe.Add(ref buf, rowOffset), (uint)bits);
            }
            else
            {
                for (var c = 0; c < size; c += 16)
                {
                    WriteExpand16(ref Unsafe.Add(ref buf, rowOffset + c), (uint)((bits >> c) & 0xFFFF));
                }
            }
        }
        {
            var bits = rows[last] ^ MaskDelta(mask, last, size);
            var c = 0;
            for (; c + 8 <= size; c += 8)
            {
                WriteSpread8(ref Unsafe.Add(ref buf, rowOffset + c), bits >> c);
            }
            if (size - c >= 4)
            {
                var c2 = size - 8;
                WriteSpread8(ref Unsafe.Add(ref buf, rowOffset + c2), bits >> c2);
            }
            else
            {
                for (; c < size; c++)
                {
                    matrix[rowOffset + c] = (byte)((bits >> c) & 1);
                }
            }
        }

        return mask;
    }

    /// <summary>
    /// Expands 32 bits to 32 (0/1) module bytes with AVX2: broadcast the 32-bit lane to all uint lanes (each 128-bit lane then holds all four source bytes), in-lane shuffle spreads byte 0/1 across the low lane and byte 2/3 across the high lane, AND with per-byte bit masks, compare-equal.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteExpand32(ref byte p, uint bits32)
    {
        var src = Vector256.Create(bits32).AsByte();
        var sel = Vector256.Create(
            (byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
            2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3);
        var bitm = Vector256.Create(
            (byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128,
            1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);
        var m = Avx2.Shuffle(src, sel) & bitm;
        var ones = Vector256.Equals(m, bitm) & Vector256.Create((byte)1);
        ones.StoreUnsafe(ref p);
    }

    /// <summary>
    /// Per-(size, row) PEXT/PDEP masks, 6 ulongs per row: [extract0, deposit0, extract1, deposit1, extract2, deposit2].
    /// Built by walking the reference zigzag and recording, for every stream bit p placed at (row, col): word p&gt;&gt;6, source bit 63-(p&amp;63) (MSB-first stream), deposit bit col.
    /// Within a word, ascending source bit means descending stream position, which the zigzag maps to strictly ascending columns, so PEXT's packing order matches PDEP's deposit order (guarded by MicroQRModulePlacerParityTest).
    /// </summary>
    private static readonly ulong[][] _pextPlaceTables = BuildPextPlaceTables();

    private static ulong[][] BuildPextPlaceTables()
    {
        var tables = new ulong[4][];
        for (var sizeIndex = 0; sizeIndex < 4; sizeIndex++)
        {
            var size = 11 + 2 * sizeIndex;
            var tbl = new ulong[size * 6];
            var p = 0;
            var upward = true;
            for (var right = size - 1; right >= 2; right -= 2)
            {
                var rowStart = right >= 10 ? 1 : 9;
                var row = upward ? size - 1 : rowStart;
                var stepDir = upward ? -1 : 1;
                var count = size - rowStart;
                for (var i = 0; i < count; i++, row += stepDir)
                {
                    for (var side = 0; side < 2; side++, p++)
                    {
                        var col = right - side;
                        var w = p >> 6;
                        tbl[row * 6 + w * 2] |= 1ul << (63 - (p & 63));
                        tbl[row * 6 + w * 2 + 1] |= 1ul << col;
                    }
                }
                upward = !upward;
            }
            tables[sizeIndex] = tbl;
        }
        return tables;
    }
}
#endif
