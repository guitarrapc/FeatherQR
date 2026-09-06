#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR.Internals.BinaryEncoders;

/// <summary>
/// Vectorized Reed-Solomon kernels for x86/x64.
/// Selected at runtime by <see cref="EccBinaryEncoder.CalculateECC"/>; every kernel produces byte-identical output to <see cref="EccBinaryEncoder.CalculateEccScalar"/>.
/// </summary>
/// <remarks>
/// Shared architecture of both kernels (result of a measured 13-round optimization loop)
///
/// - The ≤32-byte remainder register lives in one (eccCount ≤ 16) or two vector
///   registers for the whole block, no message buffer, no store/load round-trips.
/// - Four data bytes are consumed per iteration.
/// Their division factors are a
///   GF(2)-linear function of the four input bytes, so they come from four parallel
///   256-entry uint table lookups (T) instead of a serial per-byte recurrence.
/// - Iterations are software-pipelined: the next quad's factors are derived from the
///   PRE-update register using composed T∘U tables, which keeps the whole vector
///   update off the critical dependency chain (same idea as multi-bit CRC tables).
/// - The register update multiplies the (shifted) generator vector by each factor:
///   GFNI does this in one gf2p8affineqb per factor; the SSSE3 kernel uses the
///   classic PSHUFB nibble-split multiply (2 shuffles per factor) instead.
///
/// Table cost: 2 KB of GFNI matrices baked into the assembly as constant data (no runtime build), ~8 KB SSSE3 nibble-split table, plus ~8-18 KB per distinct eccCount actually used (QR uses at most 13 values; a typical app touches one or two).
/// All runtime-built tables build lazily on first use, so GFNI machines never pay for the SSSE3 table.
/// </remarks>
internal static partial class EccBinaryEncoder
{
    /// <summary>
    /// Entry point for vectorized kernels.
    /// Caller guarantees Ssse3.IsSupported and eccCount ≤ 32 (QR maximum is 30).
    /// </summary>
    internal static void CalculateEccSimd(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
    {
#if NET10_0_OR_GREATER
        if (Gfni.IsSupported)
        {
            CalculateEccGfni(data, ecc, eccCount);
            return;
        }
#endif
        // Covers all pre-GFNI x86 (AVX2-only CPUs included; wider vectors measured
        // slower than dual 128-bit registers for this kernel).
        CalculateEccSsse3(data, ecc, eccCount);
    }

    // ---------------------------------------------------------------------------
    // SSSE3 kernel (PSHUFB nibble-split GF multiply)
    // ---------------------------------------------------------------------------

    internal static void CalculateEccSsse3(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
    {
        if (eccCount <= 16)
        {
            Ssse3Core128(data, ecc, eccCount);
        }
        else
        {
            Ssse3CoreDual128(data, ecc, eccCount);
        }
    }

    private static void Ssse3Core128(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
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
                reg = Sse2.ShiftRightLogical128BitLane(reg, 4)
                    ^ Ssse3.Shuffle(Vector128.LoadUnsafe(ref t0), genS3Lo) ^ Ssse3.Shuffle(Vector128.LoadUnsafe(ref t0, 16), genS3Hi)
                    ^ Ssse3.Shuffle(Vector128.LoadUnsafe(ref t1), genS2Lo) ^ Ssse3.Shuffle(Vector128.LoadUnsafe(ref t1, 16), genS2Hi)
                    ^ Ssse3.Shuffle(Vector128.LoadUnsafe(ref t2), genS1Lo) ^ Ssse3.Shuffle(Vector128.LoadUnsafe(ref t2, 16), genS1Hi)
                    ^ Ssse3.Shuffle(Vector128.LoadUnsafe(ref t3), genLo) ^ Ssse3.Shuffle(Vector128.LoadUnsafe(ref t3, 16), genHi);

                f = QuadLookup(ref tu, f) ^ y;
            }

            // Final full quad (its factors were prepared by the last loop iteration).
            {
                ref var t0 = ref Unsafe.Add(ref mulBase, (nint)((f & 0xFF) * 32));
                ref var t1 = ref Unsafe.Add(ref mulBase, (nint)(((f >> 8) & 0xFF) * 32));
                ref var t2 = ref Unsafe.Add(ref mulBase, (nint)(((f >> 16) & 0xFF) * 32));
                ref var t3 = ref Unsafe.Add(ref mulBase, (nint)((f >> 24) * 32));

                reg = Sse2.ShiftRightLogical128BitLane(reg, 4)
                    ^ Ssse3.Shuffle(Vector128.LoadUnsafe(ref t0), genS3Lo) ^ Ssse3.Shuffle(Vector128.LoadUnsafe(ref t0, 16), genS3Hi)
                    ^ Ssse3.Shuffle(Vector128.LoadUnsafe(ref t1), genS2Lo) ^ Ssse3.Shuffle(Vector128.LoadUnsafe(ref t1, 16), genS2Hi)
                    ^ Ssse3.Shuffle(Vector128.LoadUnsafe(ref t2), genS1Lo) ^ Ssse3.Shuffle(Vector128.LoadUnsafe(ref t2, 16), genS1Hi)
                    ^ Ssse3.Shuffle(Vector128.LoadUnsafe(ref t3), genLo) ^ Ssse3.Shuffle(Vector128.LoadUnsafe(ref t3, 16), genHi);
            }
        }

        // 0..3 trailing bytes, single-step
        for (; i < data.Length; i++)
        {
            var f = (uint)(Unsafe.Add(ref dataRef, i) ^ reg.ToScalar());
            ref var t = ref Unsafe.Add(ref mulBase, (nint)(f * 32));
            reg = Sse2.ShiftRightLogical128BitLane(reg, 1)
                ^ Ssse3.Shuffle(Vector128.LoadUnsafe(ref t), genLo)
                ^ Ssse3.Shuffle(Vector128.LoadUnsafe(ref t, 16), genHi);
        }

        Span<byte> tmp = stackalloc byte[16];
        reg.StoreUnsafe(ref MemoryMarshal.GetReference(tmp));
        tmp.Slice(0, eccCount).CopyTo(ecc);
    }

    private static void Ssse3CoreDual128(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
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

        // Remainder register as two 128-bit halves: lo = ecc[0..15], hi = ecc[16..31].
        // Measured faster than one 256-bit register (no cross-lane shift, no table
        // broadcast on the dependency chain).
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
                var newLo = Ssse3.AlignRight(hi, lo, 4);
                var newHi = Sse2.ShiftRightLogical128BitLane(hi, 4);

                lo = newLo
                    ^ Ssse3.Shuffle(tblLo0, genS3LoA) ^ Ssse3.Shuffle(tblHi0, genS3HiA)
                    ^ Ssse3.Shuffle(tblLo1, genS2LoA) ^ Ssse3.Shuffle(tblHi1, genS2HiA)
                    ^ Ssse3.Shuffle(tblLo2, genS1LoA) ^ Ssse3.Shuffle(tblHi2, genS1HiA)
                    ^ Ssse3.Shuffle(tblLo3, genLoA) ^ Ssse3.Shuffle(tblHi3, genHiA);
                hi = newHi
                    ^ Ssse3.Shuffle(tblLo0, genS3LoB) ^ Ssse3.Shuffle(tblHi0, genS3HiB)
                    ^ Ssse3.Shuffle(tblLo1, genS2LoB) ^ Ssse3.Shuffle(tblHi1, genS2HiB)
                    ^ Ssse3.Shuffle(tblLo2, genS1LoB) ^ Ssse3.Shuffle(tblHi2, genS1HiB)
                    ^ Ssse3.Shuffle(tblLo3, genLoB) ^ Ssse3.Shuffle(tblHi3, genHiB);

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

                var newLo = Ssse3.AlignRight(hi, lo, 4);
                var newHi = Sse2.ShiftRightLogical128BitLane(hi, 4);

                lo = newLo
                    ^ Ssse3.Shuffle(tblLo0, genS3LoA) ^ Ssse3.Shuffle(tblHi0, genS3HiA)
                    ^ Ssse3.Shuffle(tblLo1, genS2LoA) ^ Ssse3.Shuffle(tblHi1, genS2HiA)
                    ^ Ssse3.Shuffle(tblLo2, genS1LoA) ^ Ssse3.Shuffle(tblHi2, genS1HiA)
                    ^ Ssse3.Shuffle(tblLo3, genLoA) ^ Ssse3.Shuffle(tblHi3, genHiA);
                hi = newHi
                    ^ Ssse3.Shuffle(tblLo0, genS3LoB) ^ Ssse3.Shuffle(tblHi0, genS3HiB)
                    ^ Ssse3.Shuffle(tblLo1, genS2LoB) ^ Ssse3.Shuffle(tblHi1, genS2HiB)
                    ^ Ssse3.Shuffle(tblLo2, genS1LoB) ^ Ssse3.Shuffle(tblHi2, genS1HiB)
                    ^ Ssse3.Shuffle(tblLo3, genLoB) ^ Ssse3.Shuffle(tblHi3, genHiB);
            }
        }

        for (; i < data.Length; i++)
        {
            var f = (uint)(Unsafe.Add(ref dataRef, i) ^ lo.ToScalar());
            ref var t = ref Unsafe.Add(ref mulBase, (nint)(f * 32));
            var tblLo = Vector128.LoadUnsafe(ref t);
            var tblHi = Vector128.LoadUnsafe(ref t, 16);
            var newLo = Ssse3.AlignRight(hi, lo, 1);
            var newHi = Sse2.ShiftRightLogical128BitLane(hi, 1);
            lo = newLo ^ Ssse3.Shuffle(tblLo, genLoA) ^ Ssse3.Shuffle(tblHi, genHiA);
            hi = newHi ^ Ssse3.Shuffle(tblLo, genLoB) ^ Ssse3.Shuffle(tblHi, genHiB);
        }

        Span<byte> tmp = stackalloc byte[32];
        lo.StoreUnsafe(ref MemoryMarshal.GetReference(tmp));
        hi.StoreUnsafe(ref MemoryMarshal.GetReference(tmp), 16);
        tmp.Slice(0, eccCount).CopyTo(ecc);
    }

#if NET10_0_OR_GREATER
    // ---------------------------------------------------------------------------
    // GFNI kernel (gf2p8affineqb: whole-vector GF multiply in one instruction)
    // ---------------------------------------------------------------------------

    internal static void CalculateEccGfni(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
    {
        if (eccCount <= 16)
        {
            GfniCore128(data, ecc, eccCount);
        }
        else if (Gfni.V256.IsSupported && Avx2.IsSupported)
        {
            GfniCore256(data, ecc, eccCount);
        }
        else
        {
            Ssse3CoreDual128(data, ecc, eccCount);
        }
    }

    private static void GfniCore128(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
    {
        ref var blob = ref MemoryMarshal.GetArrayDataReference(GetBlob(eccCount));
        ref var gm = ref MemoryMarshal.GetReference(GfniMatrix);
        ref var dataRef = ref MemoryMarshal.GetReference(data);

        // Raw shifted generator vectors: (gen >> s)[j] = gen[j + s]
        var gen0v = Vector128.LoadUnsafe(ref blob, BlobGen);
        var gen1v = Vector128.LoadUnsafe(ref blob, BlobGen + 1);
        var gen2v = Vector128.LoadUnsafe(ref blob, BlobGen + 2);
        var gen3v = Vector128.LoadUnsafe(ref blob, BlobGen + 3);

        var reg = Vector128<byte>.Zero;

        var i = 0;
        if (data.Length >= 4)
        {
            var d0 = Unsafe.ReadUnaligned<uint>(ref dataRef);
            var f = BlobQuadLookup(ref blob, BlobQf, d0);

            for (i = 4; i + 3 < data.Length; i += 4)
            {
                var z = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref dataRef, i)) ^ reg.AsUInt32().GetElement(1);
                var y = BlobQuadLookup(ref blob, BlobQf, z);

                var m0 = Vector128.Create(ReadMatrix(ref gm, f & 0xFF)).AsByte();
                var m1 = Vector128.Create(ReadMatrix(ref gm, (f >> 8) & 0xFF)).AsByte();
                var m2 = Vector128.Create(ReadMatrix(ref gm, (f >> 16) & 0xFF)).AsByte();
                var m3 = Vector128.Create(ReadMatrix(ref gm, f >> 24)).AsByte();

                reg = Sse2.ShiftRightLogical128BitLane(reg, 4)
                    ^ Gfni.GaloisFieldAffineTransform(gen3v, m0, 0)
                    ^ Gfni.GaloisFieldAffineTransform(gen2v, m1, 0)
                    ^ Gfni.GaloisFieldAffineTransform(gen1v, m2, 0)
                    ^ Gfni.GaloisFieldAffineTransform(gen0v, m3, 0);

                f = BlobQuadLookup(ref blob, BlobTu, f) ^ y;
            }

            // Final full quad
            {
                var m0 = Vector128.Create(ReadMatrix(ref gm, f & 0xFF)).AsByte();
                var m1 = Vector128.Create(ReadMatrix(ref gm, (f >> 8) & 0xFF)).AsByte();
                var m2 = Vector128.Create(ReadMatrix(ref gm, (f >> 16) & 0xFF)).AsByte();
                var m3 = Vector128.Create(ReadMatrix(ref gm, f >> 24)).AsByte();

                reg = Sse2.ShiftRightLogical128BitLane(reg, 4)
                    ^ Gfni.GaloisFieldAffineTransform(gen3v, m0, 0)
                    ^ Gfni.GaloisFieldAffineTransform(gen2v, m1, 0)
                    ^ Gfni.GaloisFieldAffineTransform(gen1v, m2, 0)
                    ^ Gfni.GaloisFieldAffineTransform(gen0v, m3, 0);
            }
        }

        for (; i < data.Length; i++)
        {
            var f = (uint)(Unsafe.Add(ref dataRef, i) ^ reg.ToScalar());
            var m = Vector128.Create(ReadMatrix(ref gm, f)).AsByte();
            reg = Sse2.ShiftRightLogical128BitLane(reg, 1)
                ^ Gfni.GaloisFieldAffineTransform(gen0v, m, 0);
        }

        Span<byte> tmp = stackalloc byte[16];
        reg.StoreUnsafe(ref MemoryMarshal.GetReference(tmp));
        tmp.Slice(0, eccCount).CopyTo(ecc);
    }

    private static void GfniCore256(ReadOnlySpan<byte> data, Span<byte> ecc, int eccCount)
    {
        ref var blob = ref MemoryMarshal.GetArrayDataReference(GetBlob(eccCount));
        ref var gm = ref MemoryMarshal.GetReference(GfniMatrix);
        ref var dataRef = ref MemoryMarshal.GetReference(data);

        var gen0v = Vector256.LoadUnsafe(ref blob, BlobGen);
        var gen1v = Vector256.LoadUnsafe(ref blob, BlobGen + 1);
        var gen2v = Vector256.LoadUnsafe(ref blob, BlobGen + 2);
        var gen3v = Vector256.LoadUnsafe(ref blob, BlobGen + 3);

        var reg = Vector256<byte>.Zero;

        var i = 0;
        if (data.Length >= 4)
        {
            var d0 = Unsafe.ReadUnaligned<uint>(ref dataRef);
            var f = BlobQuadLookup(ref blob, BlobQf, d0);

            for (i = 4; i + 3 < data.Length; i += 4)
            {
                var z = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref dataRef, i)) ^ reg.AsUInt32().GetElement(1);
                var y = BlobQuadLookup(ref blob, BlobQf, z);

                var m0 = Vector256.Create(ReadMatrix(ref gm, f & 0xFF)).AsByte();
                var m1 = Vector256.Create(ReadMatrix(ref gm, (f >> 8) & 0xFF)).AsByte();
                var m2 = Vector256.Create(ReadMatrix(ref gm, (f >> 16) & 0xFF)).AsByte();
                var m3 = Vector256.Create(ReadMatrix(ref gm, f >> 24)).AsByte();

                // 256-bit byte shift right by 4 across the lane boundary
                var carry = Avx2.Permute2x128(reg, reg, 0x81);
                reg = Avx2.AlignRight(carry, reg, 4)
                    ^ Gfni.V256.GaloisFieldAffineTransform(gen3v, m0, 0)
                    ^ Gfni.V256.GaloisFieldAffineTransform(gen2v, m1, 0)
                    ^ Gfni.V256.GaloisFieldAffineTransform(gen1v, m2, 0)
                    ^ Gfni.V256.GaloisFieldAffineTransform(gen0v, m3, 0);

                f = BlobQuadLookup(ref blob, BlobTu, f) ^ y;
            }

            // Final full quad
            {
                var m0 = Vector256.Create(ReadMatrix(ref gm, f & 0xFF)).AsByte();
                var m1 = Vector256.Create(ReadMatrix(ref gm, (f >> 8) & 0xFF)).AsByte();
                var m2 = Vector256.Create(ReadMatrix(ref gm, (f >> 16) & 0xFF)).AsByte();
                var m3 = Vector256.Create(ReadMatrix(ref gm, f >> 24)).AsByte();

                var carry = Avx2.Permute2x128(reg, reg, 0x81);
                reg = Avx2.AlignRight(carry, reg, 4)
                    ^ Gfni.V256.GaloisFieldAffineTransform(gen3v, m0, 0)
                    ^ Gfni.V256.GaloisFieldAffineTransform(gen2v, m1, 0)
                    ^ Gfni.V256.GaloisFieldAffineTransform(gen1v, m2, 0)
                    ^ Gfni.V256.GaloisFieldAffineTransform(gen0v, m3, 0);
            }
        }

        for (; i < data.Length; i++)
        {
            var f = (uint)(Unsafe.Add(ref dataRef, i) ^ reg.ToScalar());
            var m = Vector256.Create(ReadMatrix(ref gm, f)).AsByte();
            var carry = Avx2.Permute2x128(reg, reg, 0x81);
            reg = Avx2.AlignRight(carry, reg, 1)
                ^ Gfni.V256.GaloisFieldAffineTransform(gen0v, m, 0);
        }

        Span<byte> tmp = stackalloc byte[32];
        reg.StoreUnsafe(ref MemoryMarshal.GetReference(tmp));
        tmp.Slice(0, eccCount).CopyTo(ecc);
    }
#endif // NET10_0_OR_GREATER

    // ---------------------------------------------------------------------------
    // Lookup helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// XOR of the four per-byte-position table entries for the packed input <paramref name="x"/>: T0[x0] ^ T1[x1] ^ T2[x2] ^ T3[x3].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint QuadLookup(ref uint table, uint x)
        => Unsafe.Add(ref table, (nint)(x & 0xFF))
         ^ Unsafe.Add(ref table, 256 + (nint)((x >> 8) & 0xFF))
         ^ Unsafe.Add(ref table, 512 + (nint)((x >> 16) & 0xFF))
         ^ Unsafe.Add(ref table, 768 + (nint)(x >> 24));

#if NET10_0_OR_GREATER
    /// <summary>Same as <see cref="QuadLookup"/> against a byte-offset region of the fused blob.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint BlobQuadLookup(ref byte blob, nint regionOffset, uint x)
        => Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref blob, regionOffset + (nint)((x & 0xFF) * 4)))
         ^ Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref blob, regionOffset + 1024 + (nint)(((x >> 8) & 0xFF) * 4)))
         ^ Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref blob, regionOffset + 2048 + (nint)(((x >> 16) & 0xFF) * 4)))
         ^ Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref blob, regionOffset + 3072 + (nint)((x >> 24) * 4)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadMatrix(ref ulong matrices, uint factor)
        => Unsafe.Add(ref matrices, (nint)factor);
#endif

    // ---------------------------------------------------------------------------
    // Table construction (all lazy, per eccCount). Benign races: concurrent builders
    // produce identical arrays and the losing build is garbage. Publication uses
    // Volatile.Write so readers on weakly-ordered hardware can never observe a
    // partially initialized array; plain reads suffice because the element loads are
    // address-dependent on the reference load.
    // ---------------------------------------------------------------------------

    // Normal-domain generator coefficients (generator[1..eccCount], leading 1
    // dropped), zero-padded to 32 bytes. GF-multiplying zero padding contributes 0,
    // so padded lanes are no-ops in the vector kernels.
    private static readonly byte[]?[] _paddedGenCache = new byte[]?[33];

    private static byte[] GetPaddedGenerator(int eccCount)
    {
        var cached = _paddedGenCache[eccCount];
        if (cached is not null) return cached;

        Span<byte> generator = stackalloc byte[eccCount + 1];
        GenerateGeneratorPolynomial(generator, eccCount);

        var padded = new byte[32];
        generator.Slice(1, eccCount).CopyTo(padded);

        Volatile.Write(ref _paddedGenCache[eccCount], padded);
        return padded;
    }

    // PSHUFB nibble-split GF(256) multiplication tables: for each factor c,
    // 32 bytes = TLo (c·n for low nibble n) followed by THi (c·(n<<4) for high
    // nibble n), so c·x = TLo[x & 0xF] ^ THi[x >> 4]. 256 factors × 32 B = 8 KB.
    // Built lazily (not a field initializer) so the GFNI path never constructs it.
    private static byte[]? _nibbleMulTable;

    private static byte[] GetNibbleMulTable()
    {
        var cached = _nibbleMulTable;
        if (cached is not null) return cached;

        var table = new byte[256 * 32];
        for (var c = 0; c < 256; c++)
        {
            for (var n = 0; n < 16; n++)
            {
                table[c * 32 + n] = GaloisField.Multiply((byte)c, (byte)n);
                table[c * 32 + 16 + n] = GaloisField.Multiply((byte)c, (byte)(n << 4));
            }
        }

        Volatile.Write(ref _nibbleMulTable, table);
        return table;
    }

    // Shuffle-ready nibble split of (gen >> s bytes) for s = 0..3, each as
    // [loA|hiA|loB|hiB] 16-byte blocks (A = gen bytes 0..15, B = 16..31).
    // Offsets below; 256 bytes per eccCount.
    private const nuint NibGenLoA = 0;
    private const nuint NibGenHiA = 16;
    private const nuint NibGenLoB = 32;
    private const nuint NibGenHiB = 48;
    private const nuint NibGenS1LoA = 64;
    private const nuint NibGenS1HiA = 80;
    private const nuint NibGenS1LoB = 96;
    private const nuint NibGenS1HiB = 112;
    private const nuint NibGenS2LoA = 128;
    private const nuint NibGenS2HiA = 144;
    private const nuint NibGenS2LoB = 160;
    private const nuint NibGenS2HiB = 176;
    private const nuint NibGenS3LoA = 192;
    private const nuint NibGenS3HiA = 208;
    private const nuint NibGenS3LoB = 224;
    private const nuint NibGenS3HiB = 240;

    private static readonly byte[]?[] _nibbleCache = new byte[]?[33];

    private static byte[] GetNibbleTables(int eccCount)
    {
        var cached = _nibbleCache[eccCount];
        if (cached is not null) return cached;

        var padded = GetPaddedGenerator(eccCount);
        var t = new byte[256];
        for (var shift = 0; shift < 4; shift++)
        {
            var baseOff = shift * 64;
            for (var j = 0; j < 32; j++)
            {
                var idx = j + shift;
                var v = idx < padded.Length ? padded[idx] : (byte)0;
                var half = j >> 4; // 0 = A (bytes 0..15), 1 = B (bytes 16..31)
                var lane = j & 15;
                t[baseOff + half * 32 + lane] = (byte)(v & 0x0F);
                t[baseOff + half * 32 + 16 + lane] = (byte)(v >> 4);
            }
        }

        Volatile.Write(ref _nibbleCache[eccCount], t);
        return t;
    }

    // Quad factor tables T: T_k[b] = packed division factors (f0..f3) produced by
    // input byte b at position k with all other bytes zero. Factors for a quad are
    // the XOR of the four entries (GF(2)-linearity). 4 KB per eccCount.
    private static readonly uint[]?[] _quadFactorCache = new uint[]?[33];

    private static uint[] GetQuadFactorTables(int eccCount)
    {
        var cached = _quadFactorCache[eccCount];
        if (cached is not null) return cached;

        var padded = GetPaddedGenerator(eccCount);
        var g0 = padded[0];
        var g1 = padded[1];
        var g2 = padded[2];

        var t = new uint[4 * 256];
        for (var k = 0; k < 4; k++)
        {
            for (var b = 0; b < 256; b++)
            {
                // Run the factor recurrence with w = b at position k, zeros elsewhere:
                // f0 = w0; f_j = w_j ^ Σ gen[j-1-m]·f_m
                var w0 = k == 0 ? (byte)b : (byte)0;
                var w1 = k == 1 ? (byte)b : (byte)0;
                var w2 = k == 2 ? (byte)b : (byte)0;
                var w3 = k == 3 ? (byte)b : (byte)0;

                var f0 = w0;
                var f1 = (byte)(w1 ^ GaloisField.Multiply(g0, f0));
                var f2 = (byte)(w2 ^ GaloisField.Multiply(g1, f0) ^ GaloisField.Multiply(g0, f1));
                var f3 = (byte)(w3 ^ GaloisField.Multiply(g2, f0) ^ GaloisField.Multiply(g1, f1) ^ GaloisField.Multiply(g0, f2));

                t[k * 256 + b] = f0 | ((uint)f1 << 8) | ((uint)f2 << 16) | ((uint)f3 << 24);
            }
        }

        Volatile.Write(ref _quadFactorCache[eccCount], t);
        return t;
    }

    // Quad update tables U: U_k[b] = bytes 0..3 of factor f_k = b's contribution to
    // the updated remainder register (padded[j + 3 - k]·b packed for j = 0..3).
    private static readonly uint[]?[] _quadUpdateCache = new uint[]?[33];

    private static uint[] GetQuadUpdateTables(int eccCount)
    {
        var cached = _quadUpdateCache[eccCount];
        if (cached is not null) return cached;

        var padded = GetPaddedGenerator(eccCount);
        var t = new uint[4 * 256];
        for (var k = 0; k < 4; k++)
        {
            for (var b = 0; b < 256; b++)
            {
                var v = 0u;
                for (var j = 0; j < 4; j++)
                {
                    v |= (uint)GaloisField.Multiply(padded[j + 3 - k], (byte)b) << (j * 8);
                }
                t[k * 256 + b] = v;
            }
        }

        Volatile.Write(ref _quadUpdateCache[eccCount], t);
        return t;
    }

    // Composed T∘U tables: TU_k[b] = T(U_k[b]), the next quad's factor contribution
    // of current factor f_k = b, folding two dependent lookup rounds into one.
    // Valid because T and U are GF(2)-linear maps.
    private static readonly uint[]?[] _composedTUCache = new uint[]?[33];

    private static uint[] GetComposedTUTables(int eccCount)
    {
        var cached = _composedTUCache[eccCount];
        if (cached is not null) return cached;

        var qf = GetQuadFactorTables(eccCount);
        var uf = GetQuadUpdateTables(eccCount);

        var t = new uint[4 * 256];
        for (var k = 0; k < 4; k++)
        {
            for (var b = 0; b < 256; b++)
            {
                var u = uf[k * 256 + b];
                t[k * 256 + b] = qf[u & 0xFF]
                    ^ qf[256 + (int)((u >> 8) & 0xFF)]
                    ^ qf[512 + (int)((u >> 16) & 0xFF)]
                    ^ qf[768 + (int)(u >> 24)];
            }
        }

        Volatile.Write(ref _composedTUCache[eccCount], t);
        return t;
    }

#if NET10_0_OR_GREATER
    // Fused per-eccCount blob for the GFNI kernel: quad-factor tables, composed TU
    // tables and the padded generator behind a single pointer chase (measured ~13%
    // on the smallest QR block vs separate arrays).
    private const nint BlobQf = 0;      // 4 × 256 uints (4096 bytes)
    private const nint BlobTu = 4096;   // 4 × 256 uints (4096 bytes)
    private const nuint BlobGen = 8192; // 40 bytes: padded generator + shift headroom

    private static readonly byte[]?[] _blobCache = new byte[]?[33];

    private static byte[] GetBlob(int eccCount)
    {
        var cached = _blobCache[eccCount];
        if (cached is not null) return cached;

        var blob = new byte[8192 + 40];
        MemoryMarshal.AsBytes(GetQuadFactorTables(eccCount).AsSpan()).CopyTo(blob.AsSpan((int)BlobQf, 4096));
        MemoryMarshal.AsBytes(GetComposedTUTables(eccCount).AsSpan()).CopyTo(blob.AsSpan((int)BlobTu, 4096));
        GetPaddedGenerator(eccCount).CopyTo(blob.AsSpan((int)BlobGen, 32)); // last 8 bytes stay 0

        Volatile.Write(ref _blobCache[eccCount], blob);
        return blob;
    }

    // GFNI multiply-by-f bit matrices (universal for GF(0x11D), 2 KB), baked into
    // the assembly as constant data so cold start pays no table build.
    // gf2p8affineqb convention: qword byte (7-i) = matrix row for result bit i,
    // row bit k = bit i of f·2^k (identity matrix = 0x0102040810204080).
    // Content is locked to GaloisField.Multiply by GfniMatrixTable_MatchesGaloisFieldMultiply.
    internal static ReadOnlySpan<ulong> GfniMatrix =>
    [
        0x0000000000000000, 0x0102040810204080, 0x8001828488102040, 0x8103868C983060C0,
        0x408041C2C4881020, 0x418245CAD4A850A0, 0xC081C3464C983060, 0xC183C74E5CB870E0,
        0x2040A061E2C48810, 0x2142A469F2E4C890, 0xA04122E56AD4A850, 0xA14326ED7AF4E8D0,
        0x60C0E1A3264C9830, 0x61C2E5AB366CD8B0, 0xE0C16327AE5CB870, 0xE1C3672FBE7CF8F0,
        0x102050B071E2C488, 0x112254B861C28408, 0x9021D234F9F2E4C8, 0x9123D63CE9D2A448,
        0x50A01172B56AD4A8, 0x51A2157AA54A9428, 0xD0A193F63D7AF4E8, 0xD1A397FE2D5AB468,
        0x3060F0D193264C98, 0x3162F4D983060C18, 0xB06172551B366CD8, 0xB163765D0B162C58,
        0x70E0B11357AE5CB8, 0x71E2B51B478E1C38, 0xF0E13397DFBE7CF8, 0xF1E3379FCF9E3C78,
        0x8810A8D83871E2C4, 0x8912ACD02851A244, 0x08112A5CB061C284, 0x09132E54A0418204,
        0xC890E91AFCF9F2E4, 0xC992ED12ECD9B264, 0x48916B9E74E9D2A4, 0x49936F9664C99224,
        0xA85008B9DAB56AD4, 0xA9520CB1CA952A54, 0x28518A3D52A54A94, 0x29538E3542850A14,
        0xE8D0497B1E3D7AF4, 0xE9D24D730E1D3A74, 0x68D1CBFF962D5AB4, 0x69D3CFF7860D1A34,
        0x9830F8684993264C, 0x9932FC6059B366CC, 0x18317AECC183060C, 0x19337EE4D1A3468C,
        0xD8B0B9AA8D1B366C, 0xD9B2BDA29D3B76EC, 0x58B13B2E050B162C, 0x59B33F26152B56AC,
        0xB8705809AB57AE5C, 0xB9725C01BB77EEDC, 0x3871DA8D23478E1C, 0x3973DE853367CE9C,
        0xF8F019CB6FDFBE7C, 0xF9F21DC37FFFFEFC, 0x78F19B4FE7CF9E3C, 0x79F39F47F7EFDEBC,
        0xC488D46C1C3871E2, 0xC58AD0640C183162, 0x448956E8942851A2, 0x458B52E084081122,
        0x840895AED8B061C2, 0x850A91A6C8902142, 0x0409172A50A04182, 0x050B132240800102,
        0xE4C8740DFEFCF9F2, 0xE5CA7005EEDCB972, 0x64C9F68976ECD9B2, 0x65CBF28166CC9932,
        0xA44835CF3A74E9D2, 0xA54A31C72A54A952, 0x2449B74BB264C992, 0x254BB343A2448912,
        0xD4A884DC6DDAB56A, 0xD5AA80D47DFAF5EA, 0x54A90658E5CA952A, 0x55AB0250F5EAD5AA,
        0x9428C51EA952A54A, 0x952AC116B972E5CA, 0x1429479A2142850A, 0x152B43923162C58A,
        0xF4E824BD8F1E3D7A, 0xF5EA20B59F3E7DFA, 0x74E9A639070E1D3A, 0x75EBA231172E5DBA,
        0xB468657F4B962D5A, 0xB56A61775BB66DDA, 0x3469E7FBC3860D1A, 0x356BE3F3D3A64D9A,
        0x4C987CB424499326, 0x4D9A78BC3469D3A6, 0xCC99FE30AC59B366, 0xCD9BFA38BC79F3E6,
        0x0C183D76E0C18306, 0x0D1A397EF0E1C386, 0x8C19BFF268D1A346, 0x8D1BBBFA78F1E3C6,
        0x6CD8DCD5C68D1B36, 0x6DDAD8DDD6AD5BB6, 0xECD95E514E9D3B76, 0xEDDB5A595EBD7BF6,
        0x2C589D1702050B16, 0x2D5A991F12254B96, 0xAC591F938A152B56, 0xAD5B1B9B9A356BD6,
        0x5CB82C0455AB57AE, 0x5DBA280C458B172E, 0xDCB9AE80DDBB77EE, 0xDDBBAA88CD9B376E,
        0x1C386DC69123478E, 0x1D3A69CE8103070E, 0x9C39EF42193367CE, 0x9D3BEB4A0913274E,
        0x7CF88C65B76FDFBE, 0x7DFA886DA74F9F3E, 0xFCF90EE13F7FFFFE, 0xFDFB0AE92F5FBF7E,
        0x3C78CDA773E7CF9E, 0x3D7AC9AF63C78F1E, 0xBC794F23FBF7EFDE, 0xBD7B4B2BEBD7AF5E,
        0xE2C46A368E1C3871, 0xE3C66E3E9E3C78F1, 0x62C5E8B2060C1831, 0x63C7ECBA162C58B1,
        0xA2442BF44A942851, 0xA3462FFC5AB468D1, 0x2245A970C2840811, 0x2347AD78D2A44891,
        0xC284CA576CD8B061, 0xC386CE5F7CF8F0E1, 0x428548D3E4C89021, 0x43874CDBF4E8D0A1,
        0x82048B95A850A041, 0x83068F9DB870E0C1, 0x0205091120408001, 0x03070D193060C081,
        0xF2E43A86FFFEFCF9, 0xF3E63E8EEFDEBC79, 0x72E5B80277EEDCB9, 0x73E7BC0A67CE9C39,
        0xB2647B443B76ECD9, 0xB3667F4C2B56AC59, 0x3265F9C0B366CC99, 0x3367FDC8A3468C19,
        0xD2A49AE71D3A74E9, 0xD3A69EEF0D1A3469, 0x52A51863952A54A9, 0x53A71C6B850A1429,
        0x9224DB25D9B264C9, 0x9326DF2DC9922449, 0x122559A151A24489, 0x13275DA941820409,
        0x6AD4C2EEB66DDAB5, 0x6BD6C6E6A64D9A35, 0xEAD5406A3E7DFAF5, 0xEBD744622E5DBA75,
        0x2A54832C72E5CA95, 0x2B56872462C58A15, 0xAA5501A8FAF5EAD5, 0xAB5705A0EAD5AA55,
        0x4A94628F54A952A5, 0x4B96668744891225, 0xCA95E00BDCB972E5, 0xCB97E403CC993265,
        0x0A14234D90214285, 0x0B16274580010205, 0x8A15A1C9183162C5, 0x8B17A5C108112245,
        0x7AF4925EC78F1E3D, 0x7BF69656D7AF5EBD, 0xFAF510DA4F9F3E7D, 0xFBF714D25FBF7EFD,
        0x3A74D39C03070E1D, 0x3B76D79413274E9D, 0xBA7551188B172E5D, 0xBB7755109B376EDD,
        0x5AB4323F254B962D, 0x5BB63637356BD6AD, 0xDAB5B0BBAD5BB66D, 0xDBB7B4B3BD7BF6ED,
        0x1A3473FDE1C3860D, 0x1B3677F5F1E3C68D, 0x9A35F17969D3A64D, 0x9B37F57179F3E6CD,
        0x264CBE5A92244993, 0x274EBA5282040913, 0xA64D3CDE1A3469D3, 0xA74F38D60A142953,
        0x66CCFF9856AC59B3, 0x67CEFB90468C1933, 0xE6CD7D1CDEBC79F3, 0xE7CF7914CE9C3973,
        0x060C1E3B70E0C183, 0x070E1A3360C08103, 0x860D9CBFF8F0E1C3, 0x870F98B7E8D0A143,
        0x468C5FF9B468D1A3, 0x478E5BF1A4489123, 0xC68DDD7D3C78F1E3, 0xC78FD9752C58B163,
        0x366CEEEAE3C68D1B, 0x376EEAE2F3E6CD9B, 0xB66D6C6E6BD6AD5B, 0xB76F68667BF6EDDB,
        0x76ECAF28274E9D3B, 0x77EEAB20376EDDBB, 0xF6ED2DACAF5EBD7B, 0xF7EF29A4BF7EFDFB,
        0x162C4E8B0102050B, 0x172E4A831122458B, 0x962DCC0F8912254B, 0x972FC807993265CB,
        0x56AC0F49C58A152B, 0x57AE0B41D5AA55AB, 0xD6AD8DCD4D9A356B, 0xD7AF89C55DBA75EB,
        0xAE5C1682AA55AB57, 0xAF5E128ABA75EBD7, 0x2E5D940622458B17, 0x2F5F900E3265CB97,
        0xEEDC57406EDDBB77, 0xEFDE53487EFDFBF7, 0x6EDDD5C4E6CD9B37, 0x6FDFD1CCF6EDDBB7,
        0x8E1CB6E348912347, 0x8F1EB2EB58B163C7, 0x0E1D3467C0810307, 0x0F1F306FD0A14387,
        0xCE9CF7218C193367, 0xCF9EF3299C3973E7, 0x4E9D75A504091327, 0x4F9F71AD142953A7,
        0xBE7C4632DBB76FDF, 0xBF7E423ACB972F5F, 0x3E7DC4B653A74F9F, 0x3F7FC0BE43870F1F,
        0xFEFC07F01F3F7FFF, 0xFFFE03F80F1F3F7F, 0x7EFD8574972F5FBF, 0x7FFF817C870F1F3F,
        0x9E3CE6533973E7CF, 0x9F3EE25B2953A74F, 0x1E3D64D7B163C78F, 0x1F3F60DFA143870F,
        0xDEBCA791FDFBF7EF, 0xDFBEA399EDDBB76F, 0x5EBD251575EBD7AF, 0x5FBF211D65CB972F,
    ];
#endif // NET10_0_OR_GREATER
}
#endif // NET8_0_OR_GREATER
