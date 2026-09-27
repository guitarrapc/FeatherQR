#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace FeatherQR.Internals.BinaryEncoders;

/// <summary>
/// Vectorized Reed-Solomon kernel for ARM64 (Apple Silicon / Graviton).
/// Selected at runtime by <see cref="EccBinaryEncoder.CalculateECC"/>; produces byte-identical output to <see cref="EccBinaryEncoder.CalculateEccScalar"/>.
/// </summary>
/// <remarks>
/// Faithful port of the SSSE3 kernel (see EccBinaryEncoder.Simd.cs for the shared architecture notes).
/// Instruction mapping:
///
/// - PSHUFB → TBL (<see cref="AdvSimd.Arm64.VectorTableLookup(Vector128{byte}, Vector128{byte})"/>).
///   Both zero out-of-range lanes; the nibble indices here are always 0-15, so the
///   semantic difference (PSHUFB keys on bit 7, TBL on index ≥ 16) is never exercised.
/// - PSRLDQ (byte shift right) → EXT (<see cref="AdvSimd.ExtractVector128(Vector128{byte}, Vector128{byte}, byte)"/>
///   with a zero upper operand).
/// - PALIGNR(hi, lo, n) → EXT(lo, hi, n), ARM's operand order is (lower, upper, index).
///
/// The factor tables (nibble-split multiply, quad factors T, composed T∘U) are ISA-independent and shared with the x86 kernels.
/// GFNI has no NEON equivalent (SVE2's GF ops have no .NET API and no Apple hardware), so the TBL nibble-split kernel is the ARM64 ceiling for now.
/// </remarks>
internal static partial class EccBinaryEncoder
{
    /// <summary>
    /// Entry point for the NEON kernel.
    /// Caller guarantees AdvSimd.Arm64.IsSupported and eccCount ≤ 32 (QR maximum is 30).
    /// </summary>
    internal static void CalculateEccAdvSimd(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
    {
        if (eccCount <= 16)
        {
            AdvSimdCore128(data, ecc, eccCount);
        }
        else
        {
            AdvSimdCoreDual128(data, ecc, eccCount);
        }
    }

    private static void AdvSimdCore128(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
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
        var zero = Vector128<byte>.Zero;

        var reg = Vector128<byte>.Zero; // remainder register, ecc[0] = byte 0

        var i = 0;
        if (data.Length >= 4)
        {
            // First quad's factors come straight from the data (register is zero).
            var d0 = Unsafe.ReadUnaligned<uint>(ref dataRef);
            var f = QuadLookup(ref qf, d0);

            for (i = 4; i + 3 < data.Length; i += 4)
            {
                // Next quad's input from the PRE-update register (software pipelining):
                // x_next = d_next ^ reg[4..7] ^ U(f), and by GF(2)-linearity
                // T(x_next) = T(d_next ^ reg[4..7]) ^ (T∘U)(f).
                var z = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref dataRef, i)) ^ reg.AsUInt32().GetElement(1);
                var y = QuadLookup(ref qf, z);

                ref var t0 = ref Unsafe.Add(ref mulBase, (nint)((f & 0xFF) * 32));
                ref var t1 = ref Unsafe.Add(ref mulBase, (nint)(((f >> 8) & 0xFF) * 32));
                ref var t2 = ref Unsafe.Add(ref mulBase, (nint)(((f >> 16) & 0xFF) * 32));
                ref var t3 = ref Unsafe.Add(ref mulBase, (nint)((f >> 24) * 32));

                // reg = (reg >> 4 bytes) ^ (gen>>3)·f0 ^ (gen>>2)·f1 ^ (gen>>1)·f2 ^ gen·f3
                reg = AdvSimd.ExtractVector128(reg, zero, 4)
                    ^ AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref t0), genS3Lo) ^ AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref t0, 16), genS3Hi)
                    ^ AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref t1), genS2Lo) ^ AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref t1, 16), genS2Hi)
                    ^ AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref t2), genS1Lo) ^ AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref t2, 16), genS1Hi)
                    ^ AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref t3), genLo) ^ AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref t3, 16), genHi);

                f = QuadLookup(ref tu, f) ^ y;
            }

            // Final full quad (its factors were prepared by the last loop iteration).
            {
                ref var t0 = ref Unsafe.Add(ref mulBase, (nint)((f & 0xFF) * 32));
                ref var t1 = ref Unsafe.Add(ref mulBase, (nint)(((f >> 8) & 0xFF) * 32));
                ref var t2 = ref Unsafe.Add(ref mulBase, (nint)(((f >> 16) & 0xFF) * 32));
                ref var t3 = ref Unsafe.Add(ref mulBase, (nint)((f >> 24) * 32));

                reg = AdvSimd.ExtractVector128(reg, zero, 4)
                    ^ AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref t0), genS3Lo) ^ AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref t0, 16), genS3Hi)
                    ^ AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref t1), genS2Lo) ^ AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref t1, 16), genS2Hi)
                    ^ AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref t2), genS1Lo) ^ AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref t2, 16), genS1Hi)
                    ^ AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref t3), genLo) ^ AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref t3, 16), genHi);
            }
        }

        // 0..3 trailing bytes, single-step
        for (; i < data.Length; i++)
        {
            var f = (uint)(Unsafe.Add(ref dataRef, i) ^ reg.ToScalar());
            ref var t = ref Unsafe.Add(ref mulBase, (nint)(f * 32));
            reg = AdvSimd.ExtractVector128(reg, zero, 1)
                ^ AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref t), genLo)
                ^ AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref t, 16), genHi);
        }

        Span<byte> tmp = stackalloc byte[16];
        reg.StoreUnsafe(ref MemoryMarshal.GetReference(tmp));
        tmp.Slice(0, eccCount).CopyTo(ecc);
    }

    private static void AdvSimdCoreDual128(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
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
        var zero = Vector128<byte>.Zero;

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

                // Shift the 32-byte register left by 4 array positions:
                // newLo = lo[4..15] ++ hi[0..3], newHi = hi[4..15] ++ 0000.
                var newLo = AdvSimd.ExtractVector128(lo, hi, 4);
                var newHi = AdvSimd.ExtractVector128(hi, zero, 4);

                lo = newLo
                    ^ AdvSimd.Arm64.VectorTableLookup(tblLo0, genS3LoA) ^ AdvSimd.Arm64.VectorTableLookup(tblHi0, genS3HiA)
                    ^ AdvSimd.Arm64.VectorTableLookup(tblLo1, genS2LoA) ^ AdvSimd.Arm64.VectorTableLookup(tblHi1, genS2HiA)
                    ^ AdvSimd.Arm64.VectorTableLookup(tblLo2, genS1LoA) ^ AdvSimd.Arm64.VectorTableLookup(tblHi2, genS1HiA)
                    ^ AdvSimd.Arm64.VectorTableLookup(tblLo3, genLoA) ^ AdvSimd.Arm64.VectorTableLookup(tblHi3, genHiA);
                hi = newHi
                    ^ AdvSimd.Arm64.VectorTableLookup(tblLo0, genS3LoB) ^ AdvSimd.Arm64.VectorTableLookup(tblHi0, genS3HiB)
                    ^ AdvSimd.Arm64.VectorTableLookup(tblLo1, genS2LoB) ^ AdvSimd.Arm64.VectorTableLookup(tblHi1, genS2HiB)
                    ^ AdvSimd.Arm64.VectorTableLookup(tblLo2, genS1LoB) ^ AdvSimd.Arm64.VectorTableLookup(tblHi2, genS1HiB)
                    ^ AdvSimd.Arm64.VectorTableLookup(tblLo3, genLoB) ^ AdvSimd.Arm64.VectorTableLookup(tblHi3, genHiB);

                f = QuadLookup(ref tu, f) ^ y;
            }

            // Final full quad
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

                var newLo = AdvSimd.ExtractVector128(lo, hi, 4);
                var newHi = AdvSimd.ExtractVector128(hi, zero, 4);

                lo = newLo
                    ^ AdvSimd.Arm64.VectorTableLookup(tblLo0, genS3LoA) ^ AdvSimd.Arm64.VectorTableLookup(tblHi0, genS3HiA)
                    ^ AdvSimd.Arm64.VectorTableLookup(tblLo1, genS2LoA) ^ AdvSimd.Arm64.VectorTableLookup(tblHi1, genS2HiA)
                    ^ AdvSimd.Arm64.VectorTableLookup(tblLo2, genS1LoA) ^ AdvSimd.Arm64.VectorTableLookup(tblHi2, genS1HiA)
                    ^ AdvSimd.Arm64.VectorTableLookup(tblLo3, genLoA) ^ AdvSimd.Arm64.VectorTableLookup(tblHi3, genHiA);
                hi = newHi
                    ^ AdvSimd.Arm64.VectorTableLookup(tblLo0, genS3LoB) ^ AdvSimd.Arm64.VectorTableLookup(tblHi0, genS3HiB)
                    ^ AdvSimd.Arm64.VectorTableLookup(tblLo1, genS2LoB) ^ AdvSimd.Arm64.VectorTableLookup(tblHi1, genS2HiB)
                    ^ AdvSimd.Arm64.VectorTableLookup(tblLo2, genS1LoB) ^ AdvSimd.Arm64.VectorTableLookup(tblHi2, genS1HiB)
                    ^ AdvSimd.Arm64.VectorTableLookup(tblLo3, genLoB) ^ AdvSimd.Arm64.VectorTableLookup(tblHi3, genHiB);
            }
        }

        for (; i < data.Length; i++)
        {
            var f = (uint)(Unsafe.Add(ref dataRef, i) ^ lo.ToScalar());
            ref var t = ref Unsafe.Add(ref mulBase, (nint)(f * 32));
            var tblLo = Vector128.LoadUnsafe(ref t);
            var tblHi = Vector128.LoadUnsafe(ref t, 16);
            var newLo = AdvSimd.ExtractVector128(lo, hi, 1);
            var newHi = AdvSimd.ExtractVector128(hi, zero, 1);
            lo = newLo ^ AdvSimd.Arm64.VectorTableLookup(tblLo, genLoA) ^ AdvSimd.Arm64.VectorTableLookup(tblHi, genHiA);
            hi = newHi ^ AdvSimd.Arm64.VectorTableLookup(tblLo, genLoB) ^ AdvSimd.Arm64.VectorTableLookup(tblHi, genHiB);
        }

        Span<byte> tmp = stackalloc byte[32];
        lo.StoreUnsafe(ref MemoryMarshal.GetReference(tmp));
        hi.StoreUnsafe(ref MemoryMarshal.GetReference(tmp), 16);
        tmp.Slice(0, eccCount).CopyTo(ecc);
    }
}
#endif // NET8_0_OR_GREATER
