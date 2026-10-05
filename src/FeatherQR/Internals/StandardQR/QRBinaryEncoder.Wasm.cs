#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Wasm;

using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// The Alphanumeric payload writer on WebAssembly SIMD: the SSE4.1 tier's step of sixteen characters (QRBinaryEncoder.X86.cs), without
/// its one step of eight, so the dispatch enters it from sixteen characters. Swizzle does its nibble tables, the saturating narrow its
/// pack, and the i32x4 dot product of widened lanes its multiply-adds. The Numeric writer has no WebAssembly tier (see WriteNumericData).
/// </summary>
internal ref partial struct QRBinaryEncoder
{
    /// <summary>The Alphanumeric writer on WebAssembly SIMD: sixteen characters a step, then the portable writer.</summary>
    internal static void WriteAlphanumericPackedSimd(ref BitWriter writer, ReadOnlySpan<char> chars)
    {
        ref var t = ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(chars));
        var pairWeights = Vector128.Create(1 << 16 | 45).AsInt16();     // shorts 45, 1
        var quadWeights = Vector128.Create(1 << 16 | 2048).AsInt16();   // shorts 2048, 1
        var i = 0;

        if (chars.Length >= 16)
        {
            var local = writer;
            for (; i + 16 <= chars.Length; i += 16)
            {
                var bytes = PackedSimd.ConvertNarrowingSaturateUnsigned(Vector128.LoadUnsafe(ref t, (nuint)i), Vector128.LoadUnsafe(ref t, (nuint)(i + 8)));
                var values = AlphanumericValuesPackedSimd(bytes, out var outside);
                // Bitmask, not AnyTrue: with AnyTrue the interpreter's trace compiler (jiterpreter, .NET 10.0.11) emitted an invalid module
                // for this method ("v128.store expected type v128, found v128.any_true of type i32") and left it interpreted.
                if (PackedSimd.Bitmask(outside) != 0)
                    break;
                var pairs = PackedSimd.ConvertNarrowingSaturateSigned(
                    PackedSimd.Dot(PackedSimd.ZeroExtendWideningLower(values).AsInt16(), pairWeights),
                    PackedSimd.Dot(PackedSimd.ZeroExtendWideningUpper(values).AsInt16(), pairWeights));
                var quads = PackedSimd.Dot(pairs, quadWeights).AsUInt64();
                local.WriteWide(TwoQuadsLanes(quads.ToScalar()), 44);
                local.WriteWide(TwoQuadsLanes(quads.GetElement(1)), 44);
            }
            writer = local;
        }
        WriteAlphanumericScalar(ref writer, chars, i);
    }

    /// <summary>A 64-bit lane of two 32-bit dot products, two 22-bit pairs of pairs (low and high 32 bits), as the 44 bits they are written as.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong TwoQuadsLanes(ulong lane) => ((lane & 0x3FFFFF) << 22) | (lane >> 32);

    /// <summary>The values of sixteen character bytes and the bytes outside the alphabet (all ones), as the SSSE3 tier takes them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> AlphanumericValuesPackedSimd(Vector128<byte> bytes, out Vector128<byte> outside)
    {
        var three = Vector128.Create((byte)3);
        var low = bytes & Vector128.Create((byte)0x0F);
        var row = PackedSimd.ShiftRightLogical(bytes, 4);
        var offset = PackedSimd.Swizzle(Vector128.Create((sbyte)4, 0, 0, 0, 1, 1, 0, 0, 0, 0, -3, -3, 0, -4, -4, -4).AsByte(), low);
        offset = PackedSimd.BitwiseSelect(PackedSimd.Swizzle(Vector128.Create((sbyte)-48, -48, -48, -48, -48, -48, -48, -48, -48, -48, -14, 0, 0, 0, 0, 0).AsByte(), low), offset, PackedSimd.CompareEqual(row, three));
        offset = PackedSimd.BitwiseSelect(Vector128.Create(unchecked((byte)-55)), offset, PackedSimd.CompareGreaterThan(row, three));
        var rows = PackedSimd.Swizzle(Vector128.Create((byte)0x0B, 0x0E, 0x0E, 0x0E, 0x0F, 0x0F, 0x0E, 0x0E, 0x0E, 0x0E, 0x0F, 0x05, 0x04, 0x05, 0x05, 0x05), low);
        var rowBit = PackedSimd.Swizzle(Vector128.Create((byte)0, 0, 1, 2, 4, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), row);
        outside = PackedSimd.CompareEqual(rows & rowBit, Vector128<byte>.Zero);
        return bytes + offset;
    }
}
#endif
