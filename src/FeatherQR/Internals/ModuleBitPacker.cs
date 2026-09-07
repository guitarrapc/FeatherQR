using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace FeatherQR.Internals;

/// <summary>
/// Conversion between the byte-per-module matrix (0 = light, non-zero = dark) and the MSB-first bit-packed storage of the Micro QR / rMQR data models: bit 7 of byte 0 is module 0, the padding bits of the final byte are zero.
/// </summary>
/// <remarks>
/// Both directions run 16 (Vector128) / 32 (Vector256) modules per step on .NET 8+ — pack: non-zero compare, lane reversal within each byte group (pshufb / tbl), move-mask; unpack: per-lane byte broadcast, bit mask, compare — with a SWAR / unrolled scalar tail, and a portable scalar path on netstandard.
/// Kept behind byte parity with a naive reference by <c>ModuleBitPackerParityTest</c>.
/// </remarks>
internal static class ModuleBitPacker
{
    /// <summary>Packs <paramref name="modules"/> into <paramref name="bits"/> (at least ceil(n / 8) bytes; exactly that many are written).</summary>
    public static void Pack(ReadOnlySpan<byte> modules, Span<byte> bits)
    {
        var count = modules.Length;
        var byteCount = (count + 7) >> 3;
        if (bits.Length < byteCount)
            throw new ArgumentException($"Bit buffer too small: required {byteCount} bytes for {count} modules, got {bits.Length}.", nameof(bits));

        ref var src = ref MemoryMarshal.GetReference(modules);
        ref var dst = ref MemoryMarshal.GetReference(bits);
        var i = 0;

#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && count >= 32)
        {
            // dark lanes -> 0xFF; reverse the lanes inside each 8-lane group so the
            // move-mask (lane i -> bit i) yields MSB-first bytes; the 32-bit mask's
            // little-endian bytes are exactly modules 0-7, 8-15, 16-23, 24-31
            var reverse = Vector256.Create((byte)7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8, 23, 22, 21, 20, 19, 18, 17, 16, 31, 30, 29, 28, 27, 26, 25, 24);
            for (; i + 32 <= count; i += 32)
            {
                var dark = ~Vector256.Equals(Vector256.LoadUnsafe(ref src, (nuint)i), Vector256<byte>.Zero);
                var mask = Avx2.Shuffle(dark, reverse).ExtractMostSignificantBits();
                WriteLittleEndian(ref Unsafe.Add(ref dst, i >> 3), mask);
            }
        }
        if ((Ssse3.IsSupported || AdvSimd.Arm64.IsSupported) && count - i >= 16)
        {
            // explicit byte-shuffle intrinsics (pshufb / tbl): the portable Vector128.Shuffle
            // only lowers to them when the JIT sees a constant index operand at import, which
            // the .NET 8 JIT does not for a hoisted local (it emits a per-lane software loop)
            var reverse = Vector128.Create((byte)7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8);
            for (; i + 16 <= count; i += 16)
            {
                var dark = ~Vector128.Equals(Vector128.LoadUnsafe(ref src, (nuint)i), Vector128<byte>.Zero);
                var reversed = Ssse3.IsSupported ? Ssse3.Shuffle(dark, reverse) : AdvSimd.Arm64.VectorTableLookup(dark, reverse);
                var mask = (ushort)reversed.ExtractMostSignificantBits();
                WriteLittleEndian(ref Unsafe.Add(ref dst, i >> 3), mask);
            }
        }
#endif
        if (BitConverter.IsLittleEndian)
        {
            // SWAR: 8 modules per 64-bit load. Fold any non-zero byte to 0x01, then
            // gather the eight low bits MSB-first: multiplying by 0x8040201008040201
            // places module i (byte i) at bit 63 - i, no carries between the partial
            // products because every byte is 0 or 1.
            for (; i + 8 <= count; i += 8)
            {
                var x = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, i));
                x |= x >> 4;
                x |= x >> 2;
                x |= x >> 1;
                x &= 0x0101010101010101UL;
                Unsafe.Add(ref dst, i >> 3) = (byte)((x * 0x8040201008040201UL) >> 56);
            }
        }
        for (; i < count; i += 8)
        {
            var b = 0;
            var end = Math.Min(8, count - i);
            for (var k = 0; k < end; k++)
            {
                if (Unsafe.Add(ref src, i + k) != 0)
                    b |= 1 << (7 - k);
            }
            Unsafe.Add(ref dst, i >> 3) = (byte)b;
        }
    }

    /// <summary>Unpacks <paramref name="modules"/>.Length modules (0 / 1) from <paramref name="bits"/> (at least ceil(n / 8) bytes).</summary>
    public static void Unpack(ReadOnlySpan<byte> bits, Span<byte> modules)
    {
        var count = modules.Length;
        var byteCount = (count + 7) >> 3;
        if (bits.Length < byteCount)
            throw new ArgumentException($"Bit buffer too small: required {byteCount} bytes for {count} modules, got {bits.Length}.", nameof(bits));

        ref var src = ref MemoryMarshal.GetReference(bits);
        ref var dst = ref MemoryMarshal.GetReference(modules);
        var i = 0;

#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && count >= 32)
        {
            // 4 source bytes -> 32 modules: broadcast each byte over 8 lanes, keep its
            // lane's bit (128 .. 1), compare-equal -> 0/1
            var sel = Vector256.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3);
            var bitm = Vector256.Create((byte)128, 64, 32, 16, 8, 4, 2, 1, 128, 64, 32, 16, 8, 4, 2, 1, 128, 64, 32, 16, 8, 4, 2, 1, 128, 64, 32, 16, 8, 4, 2, 1);
            var one = Vector256.Create((byte)1);
            for (; i + 32 <= count; i += 32)
            {
                var v = Vector256.Create(ReadLittleEndianUInt32(ref Unsafe.Add(ref src, i >> 3))).AsByte();
                var m = Avx2.Shuffle(v, sel) & bitm;
                (Vector256.Equals(m, bitm) & one).StoreUnsafe(ref dst, (nuint)i);
            }
        }
        if ((Ssse3.IsSupported || AdvSimd.Arm64.IsSupported) && count - i >= 16)
        {
            var sel = Vector128.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1);
            var bitm = Vector128.Create((byte)128, 64, 32, 16, 8, 4, 2, 1, 128, 64, 32, 16, 8, 4, 2, 1);
            var one = Vector128.Create((byte)1);
            for (; i + 16 <= count; i += 16)
            {
                var v = Vector128.Create(ReadLittleEndianUInt16(ref Unsafe.Add(ref src, i >> 3))).AsByte();
                var m = (Ssse3.IsSupported ? Ssse3.Shuffle(v, sel) : AdvSimd.Arm64.VectorTableLookup(v, sel)) & bitm;
                (Vector128.Equals(m, bitm) & one).StoreUnsafe(ref dst, (nuint)i);
            }
        }
#endif
        for (; i + 8 <= count; i += 8)
        {
            int b = Unsafe.Add(ref src, i >> 3);
            ref var d = ref Unsafe.Add(ref dst, i);
            d = (byte)((b >> 7) & 1);
            Unsafe.Add(ref d, 1) = (byte)((b >> 6) & 1);
            Unsafe.Add(ref d, 2) = (byte)((b >> 5) & 1);
            Unsafe.Add(ref d, 3) = (byte)((b >> 4) & 1);
            Unsafe.Add(ref d, 4) = (byte)((b >> 3) & 1);
            Unsafe.Add(ref d, 5) = (byte)((b >> 2) & 1);
            Unsafe.Add(ref d, 6) = (byte)((b >> 1) & 1);
            Unsafe.Add(ref d, 7) = (byte)(b & 1);
        }
        for (; i < count; i++)
        {
            Unsafe.Add(ref dst, i) = (byte)((Unsafe.Add(ref src, i >> 3) >> (7 - (i & 7))) & 1);
        }
    }

#if NET8_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteLittleEndian(ref byte dst, uint value)
        => Unsafe.WriteUnaligned(ref dst, BitConverter.IsLittleEndian ? value : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteLittleEndian(ref byte dst, ushort value)
        => Unsafe.WriteUnaligned(ref dst, BitConverter.IsLittleEndian ? value : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value));

    // little-endian read: byte k of the source lands in lane group k of the broadcast
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadLittleEndianUInt32(ref byte src)
    {
        var v = Unsafe.ReadUnaligned<uint>(ref src);
        return BitConverter.IsLittleEndian ? v : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ReadLittleEndianUInt16(ref byte src)
    {
        var v = Unsafe.ReadUnaligned<ushort>(ref src);
        return BitConverter.IsLittleEndian ? v : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    }
#endif
}
