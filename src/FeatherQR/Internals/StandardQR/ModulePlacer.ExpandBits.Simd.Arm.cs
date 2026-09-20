#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace FeatherQR.Internals.StandardQR;

internal static partial class ModulePlacer
{
    /// <summary>ARM64 expansion using per-byte table selection and variable logical shifts; requires AdvSimd.Arm64.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe void ExpandBitsAdvSimd(ReadOnlySpan<byte> message, int byteCount, Span<byte> bits)
    {
        ref var src = ref MemoryMarshal.GetReference(message);
        ref var dst = ref MemoryMarshal.GetReference(bits);
        var shift = Vector128.Create((sbyte)-7, -6, -5, -4, -3, -2, -1, 0, -7, -6, -5, -4, -3, -2, -1, 0);
        var one = Vector128.Create((byte)1);
        var sel0 = Vector128.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1);
        var sel1 = Vector128.Create((byte)2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3);
        var sel2 = Vector128.Create((byte)4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5);
        var sel3 = Vector128.Create((byte)6, 6, 6, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7);
        var sel4 = Vector128.Create((byte)8, 8, 8, 8, 8, 8, 8, 8, 9, 9, 9, 9, 9, 9, 9, 9);
        var sel5 = Vector128.Create((byte)10, 10, 10, 10, 10, 10, 10, 10, 11, 11, 11, 11, 11, 11, 11, 11);
        var sel6 = Vector128.Create((byte)12, 12, 12, 12, 12, 12, 12, 12, 13, 13, 13, 13, 13, 13, 13, 13);
        var sel7 = Vector128.Create((byte)14, 14, 14, 14, 14, 14, 14, 14, 15, 15, 15, 15, 15, 15, 15, 15);
        if (byteCount < 16)
        {
            ExpandBitsAdvSimdShort(message, byteCount, bits);
            return;
        }
        nint k = 0;
        // One scalar byte aligns the common 8-byte-offset destination for the bulk loop.
        // Restrict this setup to long streams; short streams keep their original start.
        if (byteCount >= 256 && ((nuint)Unsafe.AsPointer(ref dst) & 15) == 8)
        {
            // Only inspect the address for an alignment hint. All accesses still use
            // tracked refs and unaligned stores, so a GC move cannot affect correctness.
            Unsafe.WriteUnaligned(ref dst, ExpandByte(src));
            k = 1;
        }
        nint last = byteCount - 16;
        while (true)
        {
            var v = Vector128.LoadUnsafe(ref src, (nuint)k);
            (AdvSimd.ShiftLogical(AdvSimd.Arm64.VectorTableLookup(v, sel0), shift) & one).StoreUnsafe(ref dst, (nuint)(k * 8));
            (AdvSimd.ShiftLogical(AdvSimd.Arm64.VectorTableLookup(v, sel1), shift) & one).StoreUnsafe(ref dst, (nuint)(k * 8 + 16));
            (AdvSimd.ShiftLogical(AdvSimd.Arm64.VectorTableLookup(v, sel2), shift) & one).StoreUnsafe(ref dst, (nuint)(k * 8 + 32));
            (AdvSimd.ShiftLogical(AdvSimd.Arm64.VectorTableLookup(v, sel3), shift) & one).StoreUnsafe(ref dst, (nuint)(k * 8 + 48));
            (AdvSimd.ShiftLogical(AdvSimd.Arm64.VectorTableLookup(v, sel4), shift) & one).StoreUnsafe(ref dst, (nuint)(k * 8 + 64));
            (AdvSimd.ShiftLogical(AdvSimd.Arm64.VectorTableLookup(v, sel5), shift) & one).StoreUnsafe(ref dst, (nuint)(k * 8 + 80));
            (AdvSimd.ShiftLogical(AdvSimd.Arm64.VectorTableLookup(v, sel6), shift) & one).StoreUnsafe(ref dst, (nuint)(k * 8 + 96));
            (AdvSimd.ShiftLogical(AdvSimd.Arm64.VectorTableLookup(v, sel7), shift) & one).StoreUnsafe(ref dst, (nuint)(k * 8 + 112));
            // Re-expand the last complete block when the length is not a multiple of
            // sixteen. Overlap is safe, and every load/store stays within logical bounds.
            if (k == last) break;
            k = Math.Min(k + 16, last);
        }
    }

    // Eight-byte loads cover the short vector range without reading past the source.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ExpandBitsAdvSimdShort(ReadOnlySpan<byte> message, int byteCount, Span<byte> bits)
    {
        ref var src = ref MemoryMarshal.GetReference(message);
        ref var dst = ref MemoryMarshal.GetReference(bits);
        var shift = Vector128.Create((sbyte)-7, -6, -5, -4, -3, -2, -1, 0, -7, -6, -5, -4, -3, -2, -1, 0);
        var one = Vector128.Create((byte)1);
        var sel0 = Vector128.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1);
        var sel1 = Vector128.Create((byte)2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3);
        var sel2 = Vector128.Create((byte)4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5);
        var sel3 = Vector128.Create((byte)6, 6, 6, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7);
        if (byteCount < 8)
        {
            ExpandBitsScalar(message, byteCount, bits);
            return;
        }
        var k = 0;
        var last = byteCount - 8;
        while (true)
        {
            var v = Vector64.LoadUnsafe(ref src, (nuint)k).ToVector128();
            (AdvSimd.ShiftLogical(AdvSimd.Arm64.VectorTableLookup(v, sel0), shift) & one).StoreUnsafe(ref dst, (nuint)(k * 8));
            (AdvSimd.ShiftLogical(AdvSimd.Arm64.VectorTableLookup(v, sel1), shift) & one).StoreUnsafe(ref dst, (nuint)(k * 8 + 16));
            (AdvSimd.ShiftLogical(AdvSimd.Arm64.VectorTableLookup(v, sel2), shift) & one).StoreUnsafe(ref dst, (nuint)(k * 8 + 32));
            (AdvSimd.ShiftLogical(AdvSimd.Arm64.VectorTableLookup(v, sel3), shift) & one).StoreUnsafe(ref dst, (nuint)(k * 8 + 48));
            if (k == last) break;
            k = Math.Min(k + 8, last);
        }
    }
}
#endif
