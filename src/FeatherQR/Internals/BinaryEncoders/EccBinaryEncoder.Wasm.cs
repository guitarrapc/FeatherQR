#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Wasm;

namespace FeatherQR.Internals.BinaryEncoders;

/// <summary>
/// Reed-Solomon kernel for WebAssembly: the NEON kernel with i8x16.swizzle for TBL, which zeroes the same out-of-range lanes.
/// </summary>
/// <remarks>
/// WebAssembly has no byte shift across a vector, so the register's shift is a swizzle by constant indices, whose indices past
/// 15 bring in the zero lanes; across the two halves of a 32-byte register it is two swizzles and an OR.
/// </remarks>
internal static partial class EccBinaryEncoder
{
    // Data bytes times ECC codewords below which the interpreter's setup costs more than the scalar division; one flag gates both
    // WebAssembly builds. Checked here rather than in the dispatch, where reading the span's length changed the JIT's code for x64.
    private const int MinPackedSimdBlockWork = 90;

    /// <summary>
    /// Entry point for WebAssembly: the kernel, or the scalar kernel for blocks under <see cref="MinPackedSimdBlockWork"/>.
    /// Caller guarantees PackedSimd.IsSupported and eccCount ≤ 32 (QR maximum is 30).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CalculateEccPackedSimd(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
    {
        if (data.Length * eccCount < MinPackedSimdBlockWork)
        {
            CalculateEccScalar(data, ecc, eccCount);
            return;
        }
        PackedSimdKernel(data, ecc, eccCount);
    }

    /// <summary>The kernel on any block, past the size gate. Caller guarantees PackedSimd.IsSupported and eccCount ≤ 32.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void PackedSimdKernel(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
    {
        if (eccCount <= 16)
        {
            PackedSimdCore128(data, ecc, eccCount);
        }
        else
        {
            PackedSimdCoreDual128(data, ecc, eccCount);
        }
    }

    private static void PackedSimdCore128(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
    {
        var nib = GetNibbleTables(eccCount);
        ref var nibRef = ref MemoryMarshal.GetArrayDataReference(nib);
        ref var mulBase = ref MemoryMarshal.GetArrayDataReference(GetNibbleMulTable());
        ref var qf = ref MemoryMarshal.GetArrayDataReference(GetQuadFactorTables(eccCount));
        ref var tu = ref MemoryMarshal.GetArrayDataReference(GetComposedTUTables(eccCount));
        ref var dataRef = ref MemoryMarshal.GetReference(data);

        var genLo = Vector128.LoadUnsafe(ref nibRef, NibGenLoA);
        var genHi = Vector128.LoadUnsafe(ref nibRef, NibGenHiA);
        var genS1Lo = Vector128.LoadUnsafe(ref nibRef, NibGenS1LoA);
        var genS1Hi = Vector128.LoadUnsafe(ref nibRef, NibGenS1HiA);
        var genS2Lo = Vector128.LoadUnsafe(ref nibRef, NibGenS2LoA);
        var genS2Hi = Vector128.LoadUnsafe(ref nibRef, NibGenS2HiA);
        var genS3Lo = Vector128.LoadUnsafe(ref nibRef, NibGenS3LoA);
        var genS3Hi = Vector128.LoadUnsafe(ref nibRef, NibGenS3HiA);
        var down4 = Vector128.Create((byte)4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0xFF, 0xFF, 0xFF, 0xFF);
        var down1 = Vector128.Create((byte)1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0xFF);

        var reg = Vector128<byte>.Zero; // remainder register, ecc[0] = byte 0

        var i = 0;
        if (data.Length >= 4)
        {
            var d0 = Unsafe.ReadUnaligned<uint>(ref dataRef);
            var f = QuadLookup(ref qf, d0);

            for (i = 4; i + 3 < data.Length; i += 4)
            {
                var z = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref dataRef, i)) ^ reg.AsUInt32().GetElement(1);
                var y = QuadLookup(ref qf, z);

                ref var t0 = ref Unsafe.Add(ref mulBase, (nint)((f & 0xFF) * 32));
                ref var t1 = ref Unsafe.Add(ref mulBase, (nint)(((f >> 8) & 0xFF) * 32));
                ref var t2 = ref Unsafe.Add(ref mulBase, (nint)(((f >> 16) & 0xFF) * 32));
                ref var t3 = ref Unsafe.Add(ref mulBase, (nint)((f >> 24) * 32));

                reg = PackedSimd.Swizzle(reg, down4)
                    ^ PackedSimd.Swizzle(Vector128.LoadUnsafe(ref t0), genS3Lo) ^ PackedSimd.Swizzle(Vector128.LoadUnsafe(ref t0, 16), genS3Hi)
                    ^ PackedSimd.Swizzle(Vector128.LoadUnsafe(ref t1), genS2Lo) ^ PackedSimd.Swizzle(Vector128.LoadUnsafe(ref t1, 16), genS2Hi)
                    ^ PackedSimd.Swizzle(Vector128.LoadUnsafe(ref t2), genS1Lo) ^ PackedSimd.Swizzle(Vector128.LoadUnsafe(ref t2, 16), genS1Hi)
                    ^ PackedSimd.Swizzle(Vector128.LoadUnsafe(ref t3), genLo) ^ PackedSimd.Swizzle(Vector128.LoadUnsafe(ref t3, 16), genHi);

                f = QuadLookup(ref tu, f) ^ y;
            }

            {
                ref var t0 = ref Unsafe.Add(ref mulBase, (nint)((f & 0xFF) * 32));
                ref var t1 = ref Unsafe.Add(ref mulBase, (nint)(((f >> 8) & 0xFF) * 32));
                ref var t2 = ref Unsafe.Add(ref mulBase, (nint)(((f >> 16) & 0xFF) * 32));
                ref var t3 = ref Unsafe.Add(ref mulBase, (nint)((f >> 24) * 32));

                reg = PackedSimd.Swizzle(reg, down4)
                    ^ PackedSimd.Swizzle(Vector128.LoadUnsafe(ref t0), genS3Lo) ^ PackedSimd.Swizzle(Vector128.LoadUnsafe(ref t0, 16), genS3Hi)
                    ^ PackedSimd.Swizzle(Vector128.LoadUnsafe(ref t1), genS2Lo) ^ PackedSimd.Swizzle(Vector128.LoadUnsafe(ref t1, 16), genS2Hi)
                    ^ PackedSimd.Swizzle(Vector128.LoadUnsafe(ref t2), genS1Lo) ^ PackedSimd.Swizzle(Vector128.LoadUnsafe(ref t2, 16), genS1Hi)
                    ^ PackedSimd.Swizzle(Vector128.LoadUnsafe(ref t3), genLo) ^ PackedSimd.Swizzle(Vector128.LoadUnsafe(ref t3, 16), genHi);
            }
        }

        for (; i < data.Length; i++)
        {
            var f = (uint)(Unsafe.Add(ref dataRef, i) ^ reg.ToScalar());
            ref var t = ref Unsafe.Add(ref mulBase, (nint)(f * 32));
            reg = PackedSimd.Swizzle(reg, down1)
                ^ PackedSimd.Swizzle(Vector128.LoadUnsafe(ref t), genLo)
                ^ PackedSimd.Swizzle(Vector128.LoadUnsafe(ref t, 16), genHi);
        }

        Span<byte> tmp = stackalloc byte[16];
        reg.StoreUnsafe(ref MemoryMarshal.GetReference(tmp));
        tmp.Slice(0, eccCount).CopyTo(ecc);
    }

    private static void PackedSimdCoreDual128(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
    {
        var nib = GetNibbleTables(eccCount);
        ref var nibRef = ref MemoryMarshal.GetArrayDataReference(nib);
        ref var mulBase = ref MemoryMarshal.GetArrayDataReference(GetNibbleMulTable());
        ref var qf = ref MemoryMarshal.GetArrayDataReference(GetQuadFactorTables(eccCount));
        ref var tu = ref MemoryMarshal.GetArrayDataReference(GetComposedTUTables(eccCount));
        ref var dataRef = ref MemoryMarshal.GetReference(data);

        var genLoA = Vector128.LoadUnsafe(ref nibRef, NibGenLoA);
        var genHiA = Vector128.LoadUnsafe(ref nibRef, NibGenHiA);
        var genLoB = Vector128.LoadUnsafe(ref nibRef, NibGenLoB);
        var genHiB = Vector128.LoadUnsafe(ref nibRef, NibGenHiB);
        var genS1LoA = Vector128.LoadUnsafe(ref nibRef, NibGenS1LoA);
        var genS1HiA = Vector128.LoadUnsafe(ref nibRef, NibGenS1HiA);
        var genS1LoB = Vector128.LoadUnsafe(ref nibRef, NibGenS1LoB);
        var genS1HiB = Vector128.LoadUnsafe(ref nibRef, NibGenS1HiB);
        var genS2LoA = Vector128.LoadUnsafe(ref nibRef, NibGenS2LoA);
        var genS2HiA = Vector128.LoadUnsafe(ref nibRef, NibGenS2HiA);
        var genS2LoB = Vector128.LoadUnsafe(ref nibRef, NibGenS2LoB);
        var genS2HiB = Vector128.LoadUnsafe(ref nibRef, NibGenS2HiB);
        var genS3LoA = Vector128.LoadUnsafe(ref nibRef, NibGenS3LoA);
        var genS3HiA = Vector128.LoadUnsafe(ref nibRef, NibGenS3HiA);
        var genS3LoB = Vector128.LoadUnsafe(ref nibRef, NibGenS3LoB);
        var genS3HiB = Vector128.LoadUnsafe(ref nibRef, NibGenS3HiB);
        var down4 = Vector128.Create((byte)4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0xFF, 0xFF, 0xFF, 0xFF);
        var up12 = Vector128.Create((byte)0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 1, 2, 3);
        var down1 = Vector128.Create((byte)1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0xFF);
        var up15 = Vector128.Create((byte)0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0);

        // Remainder register as two 128-bit halves: lo = ecc[0..15], hi = ecc[16..31].
        var lo = Vector128<byte>.Zero;
        var hi = Vector128<byte>.Zero;

        var i = 0;
        if (data.Length >= 4)
        {
            var d0 = Unsafe.ReadUnaligned<uint>(ref dataRef);
            var f = QuadLookup(ref qf, d0);

            for (i = 4; i + 3 < data.Length; i += 4)
            {
                var z = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref dataRef, i)) ^ lo.AsUInt32().GetElement(1);
                var y = QuadLookup(ref qf, z);

                ref var t0 = ref Unsafe.Add(ref mulBase, (nint)((f & 0xFF) * 32));
                ref var t1 = ref Unsafe.Add(ref mulBase, (nint)(((f >> 8) & 0xFF) * 32));
                ref var t2 = ref Unsafe.Add(ref mulBase, (nint)(((f >> 16) & 0xFF) * 32));
                ref var t3 = ref Unsafe.Add(ref mulBase, (nint)((f >> 24) * 32));

                var tblLo0 = Vector128.LoadUnsafe(ref t0);
                var tblHi0 = Vector128.LoadUnsafe(ref t0, 16);
                var tblLo1 = Vector128.LoadUnsafe(ref t1);
                var tblHi1 = Vector128.LoadUnsafe(ref t1, 16);
                var tblLo2 = Vector128.LoadUnsafe(ref t2);
                var tblHi2 = Vector128.LoadUnsafe(ref t2, 16);
                var tblLo3 = Vector128.LoadUnsafe(ref t3);
                var tblHi3 = Vector128.LoadUnsafe(ref t3, 16);

                // newLo = lo[4..15] ++ hi[0..3], newHi = hi[4..15] ++ 0000.
                var newLo = PackedSimd.Swizzle(lo, down4) | PackedSimd.Swizzle(hi, up12);
                var newHi = PackedSimd.Swizzle(hi, down4);

                lo = newLo
                    ^ PackedSimd.Swizzle(tblLo0, genS3LoA) ^ PackedSimd.Swizzle(tblHi0, genS3HiA)
                    ^ PackedSimd.Swizzle(tblLo1, genS2LoA) ^ PackedSimd.Swizzle(tblHi1, genS2HiA)
                    ^ PackedSimd.Swizzle(tblLo2, genS1LoA) ^ PackedSimd.Swizzle(tblHi2, genS1HiA)
                    ^ PackedSimd.Swizzle(tblLo3, genLoA) ^ PackedSimd.Swizzle(tblHi3, genHiA);
                hi = newHi
                    ^ PackedSimd.Swizzle(tblLo0, genS3LoB) ^ PackedSimd.Swizzle(tblHi0, genS3HiB)
                    ^ PackedSimd.Swizzle(tblLo1, genS2LoB) ^ PackedSimd.Swizzle(tblHi1, genS2HiB)
                    ^ PackedSimd.Swizzle(tblLo2, genS1LoB) ^ PackedSimd.Swizzle(tblHi2, genS1HiB)
                    ^ PackedSimd.Swizzle(tblLo3, genLoB) ^ PackedSimd.Swizzle(tblHi3, genHiB);

                f = QuadLookup(ref tu, f) ^ y;
            }

            {
                ref var t0 = ref Unsafe.Add(ref mulBase, (nint)((f & 0xFF) * 32));
                ref var t1 = ref Unsafe.Add(ref mulBase, (nint)(((f >> 8) & 0xFF) * 32));
                ref var t2 = ref Unsafe.Add(ref mulBase, (nint)(((f >> 16) & 0xFF) * 32));
                ref var t3 = ref Unsafe.Add(ref mulBase, (nint)((f >> 24) * 32));

                var tblLo0 = Vector128.LoadUnsafe(ref t0);
                var tblHi0 = Vector128.LoadUnsafe(ref t0, 16);
                var tblLo1 = Vector128.LoadUnsafe(ref t1);
                var tblHi1 = Vector128.LoadUnsafe(ref t1, 16);
                var tblLo2 = Vector128.LoadUnsafe(ref t2);
                var tblHi2 = Vector128.LoadUnsafe(ref t2, 16);
                var tblLo3 = Vector128.LoadUnsafe(ref t3);
                var tblHi3 = Vector128.LoadUnsafe(ref t3, 16);

                var newLo = PackedSimd.Swizzle(lo, down4) | PackedSimd.Swizzle(hi, up12);
                var newHi = PackedSimd.Swizzle(hi, down4);

                lo = newLo
                    ^ PackedSimd.Swizzle(tblLo0, genS3LoA) ^ PackedSimd.Swizzle(tblHi0, genS3HiA)
                    ^ PackedSimd.Swizzle(tblLo1, genS2LoA) ^ PackedSimd.Swizzle(tblHi1, genS2HiA)
                    ^ PackedSimd.Swizzle(tblLo2, genS1LoA) ^ PackedSimd.Swizzle(tblHi2, genS1HiA)
                    ^ PackedSimd.Swizzle(tblLo3, genLoA) ^ PackedSimd.Swizzle(tblHi3, genHiA);
                hi = newHi
                    ^ PackedSimd.Swizzle(tblLo0, genS3LoB) ^ PackedSimd.Swizzle(tblHi0, genS3HiB)
                    ^ PackedSimd.Swizzle(tblLo1, genS2LoB) ^ PackedSimd.Swizzle(tblHi1, genS2HiB)
                    ^ PackedSimd.Swizzle(tblLo2, genS1LoB) ^ PackedSimd.Swizzle(tblHi2, genS1HiB)
                    ^ PackedSimd.Swizzle(tblLo3, genLoB) ^ PackedSimd.Swizzle(tblHi3, genHiB);
            }
        }

        for (; i < data.Length; i++)
        {
            var f = (uint)(Unsafe.Add(ref dataRef, i) ^ lo.ToScalar());
            ref var t = ref Unsafe.Add(ref mulBase, (nint)(f * 32));
            var tblLo = Vector128.LoadUnsafe(ref t);
            var tblHi = Vector128.LoadUnsafe(ref t, 16);
            var newLo = PackedSimd.Swizzle(lo, down1) | PackedSimd.Swizzle(hi, up15);
            var newHi = PackedSimd.Swizzle(hi, down1);
            lo = newLo ^ PackedSimd.Swizzle(tblLo, genLoA) ^ PackedSimd.Swizzle(tblHi, genHiA);
            hi = newHi ^ PackedSimd.Swizzle(tblLo, genLoB) ^ PackedSimd.Swizzle(tblHi, genHiB);
        }

        Span<byte> tmp = stackalloc byte[32];
        lo.StoreUnsafe(ref MemoryMarshal.GetReference(tmp));
        hi.StoreUnsafe(ref MemoryMarshal.GetReference(tmp), 16);
        tmp.Slice(0, eccCount).CopyTo(ecc);
    }
}
#endif // NET8_0_OR_GREATER
