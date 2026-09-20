using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace FeatherQR.Internals.StandardQR;

internal static partial class ModulePlacer
{
    /// <summary>bits[8k + j] = bit (7 - j) of message[k] for k &lt; byteCount (MSB first).</summary>
    /// <remarks>The caller supplies disjoint spans, 0 &lt;= byteCount &lt;= message.Length, and at least byteCount * 8 destination bytes. No vector slack is required.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ExpandBits(ReadOnlySpan<byte> message, int byteCount, Span<byte> bits)
    {
#if NET8_0_OR_GREATER
        if (AdvSimd.Arm64.IsSupported)
        {
            ExpandBitsAdvSimd(message, byteCount, bits);
            return;
        }
#endif
        ref var src = ref MemoryMarshal.GetReference(message);
        ref var dst = ref MemoryMarshal.GetReference(bits);
        var k = 0;
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported)
        {
            // 4 message bytes -> 32 module bytes per step: broadcast the 4 bytes, in-lane
            // shuffle replicates byte j over lanes 8j..8j+7, AND with the per-lane bit
            // mask + compare-equal yields 0/1 bytes
            var sel = Vector256.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3);
            var bitm = Vector256.Create((byte)128, 64, 32, 16, 8, 4, 2, 1, 128, 64, 32, 16, 8, 4, 2, 1, 128, 64, 32, 16, 8, 4, 2, 1, 128, 64, 32, 16, 8, 4, 2, 1);
            var one = Vector256.Create((byte)1);
            for (; k + 4 <= byteCount; k += 4)
            {
                var v = Vector256.Create(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, k))).AsByte();
                var m = Avx2.Shuffle(v, sel) & bitm;
                (Vector256.Equals(m, bitm) & one).StoreUnsafe(ref dst, (nuint)(k * 8));
            }
        }
        if (Ssse3.IsSupported)
        {
            var sel = Vector128.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1);
            var bitm = Vector128.Create((byte)128, 64, 32, 16, 8, 4, 2, 1, 128, 64, 32, 16, 8, 4, 2, 1);
            var one = Vector128.Create((byte)1);
            for (; k + 2 <= byteCount; k += 2)
            {
                var v = Vector128.Create(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref src, k))).AsByte();
                var m = Ssse3.Shuffle(v, sel) & bitm;
                (Vector128.Equals(m, bitm) & one).StoreUnsafe(ref dst, (nuint)(k * 8));
            }
        }
#endif
        if (k < byteCount)
            ExpandBitsScalar(message.Slice(k), byteCount - k, bits.Slice(k * 8));
    }

    /// <summary>Portable expansion: multiplication for short streams, four independent table lookups per step otherwise.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ExpandBitsScalar(ReadOnlySpan<byte> message, int byteCount, Span<byte> bits)
    {
        ref var src = ref MemoryMarshal.GetReference(message);
        ref var dst = ref MemoryMarshal.GetReference(bits);
        nint k = 0;
        if (byteCount < 16)
        {
            for (; k < byteCount; k++)
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, k * 8), ExpandByte(Unsafe.Add(ref src, k)));
            return;
        }

        ref var table = ref MemoryMarshal.GetReference(ExpandedByteTable.Values.AsSpan());
        for (; k + 4 <= byteCount; k += 4)
        {
            ref var d = ref Unsafe.Add(ref dst, k * 8);
            Unsafe.WriteUnaligned(ref d, Unsafe.Add(ref table, Unsafe.Add(ref src, k)));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 8), Unsafe.Add(ref table, Unsafe.Add(ref src, k + 1)));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 16), Unsafe.Add(ref table, Unsafe.Add(ref src, k + 2)));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 24), Unsafe.Add(ref table, Unsafe.Add(ref src, k + 3)));
        }
        for (; k < byteCount; k++)
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, k * 8), Unsafe.Add(ref table, Unsafe.Add(ref src, k)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ExpandByte(byte value)
    {
        // Replicate the input bits onto the top bit of each byte, then normalize to 0/1.
        // The least significant result byte holds input bit 7, matching MSB-first stores.
        var expanded = (unchecked((ulong)value * 0x8040201008040201UL) & 0x8080808080808080UL) >> 7;
        return BitConverter.IsLittleEndian ? expanded : BinaryPrimitives.ReverseEndianness(expanded);
    }

    private static class ExpandedByteTable
    {
        // Keep the 2 KiB scalar table separate from the placement cache and SIMD path.
        internal static readonly ulong[] Values;

        static ExpandedByteTable()
        {
            Values = new ulong[256];
            for (var i = 0; i < Values.Length; i++)
                Values[i] = ExpandByte((byte)i);
        }
    }
}
