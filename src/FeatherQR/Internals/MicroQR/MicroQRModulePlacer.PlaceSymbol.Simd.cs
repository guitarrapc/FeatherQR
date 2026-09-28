#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics.Arm;

namespace FeatherQR.Internals.MicroQR;

internal static partial class MicroQRModulePlacer
{
    private static int PlaceCoreVector(Span<byte> matrix, int size, ReadOnlySpan<ulong> stream, MicroQRVersion version, MicroQREccLevel eccLevel, int forcedMask)
    {
        Span<ulong> rows = stackalloc ulong[17];
        rows = rows.Slice(0, size);
        var mask = BuildPackedRows(rows, size, stream, version, eccLevel, forcedMask);

        // Unpack with the mask applied on the fly, 16 modules per SIMD step
        // (SSSE3 or NEON, see WriteExpand16).
        // Rows 0..size-2 may overrun into the following row: bits >= size are
        // zero and rows unpack in ascending order, so the overwritten zeros are
        // immediately replaced by that row's own unpack. The last row (buffer
        // edge) uses the scalar-safe tail instead.
        ref var buf = ref MemoryMarshal.GetReference(matrix);
        var last = size - 1;
        var rowOffset = 0;
        for (var y = 0; y < last; y++, rowOffset += size)
        {
            var bits = rows[y] ^ MaskDelta(mask, y, size);
            for (var c = 0; c < size; c += 16)
            {
                WriteExpand16(ref Unsafe.Add(ref buf, rowOffset + c), (uint)((bits >> c) & 0xFFFF));
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
    /// Expands 16 bits to 16 (0/1) module bytes: broadcast the 16-bit lane, shuffle byte 0/1 across the halves, then per-lane bit test (byte k = 1 iff bit k set).
    /// Caller (via <see cref="PlaceCoreVector"/> dispatch) guarantees SSSE3 or AdvSimd.Arm64; IsSupported is a JIT constant so the untaken branch is eliminated. x86: PSHUFB + PAND + PCMPEQB, beat the GFNI bit-expand in the kernel benchmark loop (GFNI's matrix-operand preparation outweighs its single-instruction expand).
    /// ARM64: TBL + CMTST, CMTST (compare-test: 0xFF iff (a &amp; b) != 0) fuses the AND+CMEQ pair; x86 has no per-lane bit-test compare.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteExpand16(ref byte p, uint bits16)
    {
        var src = Vector128.Create((ushort)bits16).AsByte();
        var sel = Vector128.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1);
        var bitm = Vector128.Create((byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);
        Vector128<byte> ones;
        if (Ssse3.IsSupported)
        {
            var m = Ssse3.Shuffle(src, sel) & bitm;
            ones = Vector128.Equals(m, bitm) & Vector128.Create((byte)1);
        }
        else
        {
            var repl = AdvSimd.Arm64.VectorTableLookup(src, sel);
            ones = AdvSimd.CompareTest(repl, bitm) & Vector128.Create((byte)1);
        }
        ones.StoreUnsafe(ref p);
    }
}
#endif
