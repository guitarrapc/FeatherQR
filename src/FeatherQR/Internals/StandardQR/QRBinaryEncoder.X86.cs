#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// The Alphanumeric and Numeric payload writers on SSSE3 and SSE4.1: x64 with or without AVX2, a default NativeAOT publish included.
/// </summary>
/// <remarks>
/// <para>
/// Alphanumeric, sixteen characters a step (the rMQR writer's form, RmQRBinaryEncoder.WriteAlphanumeric, on one pack of two loads): the
/// value is the character plus an offset chosen by its row, from two pshufb tables over the low nibble and a constant for the letters;
/// pmaddubsw (45, 1) forms the pairs and pmaddwd (2048, 1) two pairs in 22 bits, so the step is two 44-bit appends. A membership table
/// over the low nibble, ANDed with the row's bit, marks a character outside the alphabet; such a step leaves the run to the portable
/// writer, which checks it again and throws there. A character past 0xFF saturates to 0xFF or to 0 in the pack, neither in the alphabet.
/// </para>
/// <para>
/// Numeric, twelve digits from one pack of sixteen chars: pshufb lays them out as four groups of (d0, d1, d2, 0), pmaddubsw
/// (100, 10, 1, 0) and pmaddwd (1, 1) give the four groups, packssdw and pmaddwd (1024, 1) two pairs of them in 20 bits each, so the step
/// is one 40-bit append.
/// </para>
/// <para>
/// A run long enough for a step runs its steps on a local copy of the writer, stored back before anything else writes: with its fields in
/// registers the steps took 0.86 to 0.93 of their time at 300 to 7,089 characters, against about a nanosecond more for the copy, which a
/// run too short for a step does not pay (2026-10-05).
/// </para>
/// </remarks>
internal ref partial struct QRBinaryEncoder
{
    /// <summary>The Alphanumeric writer on SSSE3 and SSE4.1: sixteen characters a step, then one step of eight, then the portable writer.</summary>
    internal static void WriteAlphanumericSsse3(ref BitWriter writer, ReadOnlySpan<char> chars)
    {
        ref var t = ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(chars));
        var pairWeights = Vector128.Create((short)(1 << 8 | 45)).AsSByte();   // bytes 45, 1
        var quadWeights = Vector128.Create(1 << 16 | 2048).AsInt16();         // shorts 2048, 1
        var i = 0;

        if (chars.Length >= 16)
        {
            var local = writer;
            for (; i + 16 <= chars.Length; i += 16)
            {
                var values = AlphanumericValuesSsse3(Sse2.PackUnsignedSaturate(Vector128.LoadUnsafe(ref t, (nuint)i), Vector128.LoadUnsafe(ref t, (nuint)(i + 8))), out var outside);
                if (Sse2.MoveMask(outside) != 0)
                    break;
                var quads = Sse2.MultiplyAddAdjacent(Ssse3.MultiplyAddAdjacent(values, pairWeights), quadWeights).AsUInt64();
                local.WriteWide(TwoQuads(quads.ToScalar()), 44);
                local.WriteWide(TwoQuads(quads.GetElement(1)), 44);
            }
            writer = local;
        }

        if (i + 8 <= chars.Length)
        {
            var v = Vector128.LoadUnsafe(ref t, (nuint)i);
            var values = AlphanumericValuesSsse3(Sse2.PackUnsignedSaturate(v, v), out var outside);
            if ((Sse2.MoveMask(outside) & 0xFF) == 0)
            {
                writer.WriteWide(TwoQuads(Sse2.MultiplyAddAdjacent(Ssse3.MultiplyAddAdjacent(values, pairWeights), quadWeights).AsUInt64().ToScalar()), 44);
                i += 8;
            }
        }
        WriteAlphanumericScalar(ref writer, chars, i);
    }

    /// <summary>The Numeric writer on SSSE3: twelve digits a step, then the portable writer.</summary>
    internal static void WriteNumericSsse3(ref BitWriter writer, ReadOnlySpan<char> digits)
    {
        ref var t = ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(digits));
        var zero = Vector128.Create((byte)'0');
        var layout = Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);
        var groupWeights = Vector128.Create(1 << 16 | 10 << 8 | 100).AsSByte();   // bytes 100, 10, 1, 0
        var ones = Vector128.Create((short)1);
        var pairWeights = Vector128.Create(1 << 16 | 1024).AsInt16();              // shorts 1024, 1
        var i = 0;

        // a step reads sixteen chars and writes twelve
        if (digits.Length >= 16)
        {
            var local = writer;
            for (; i + 16 <= digits.Length; i += 12)
            {
                var bytes = Sse2.PackUnsignedSaturate(Vector128.LoadUnsafe(ref t, (nuint)i), Vector128.LoadUnsafe(ref t, (nuint)(i + 8))) - zero;
                var groups = Sse2.MultiplyAddAdjacent(Ssse3.MultiplyAddAdjacent(Ssse3.Shuffle(bytes, layout), groupWeights), ones);
                var lane = Sse2.MultiplyAddAdjacent(Sse2.PackSignedSaturate(groups, groups), pairWeights).AsUInt64().ToScalar();
                local.WriteWide(((lane & 0xFFFFF) << 20) | (lane >> 32), 40);
            }
            writer = local;
        }
        WriteNumericScalar(ref writer, digits, i);
    }

    /// <summary>A 64-bit lane of pmaddwd's output, two 22-bit pairs of pairs (low and high 32 bits), as the 44 bits they are written as.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong TwoQuads(ulong lane) => ((lane & 0x3FFFFF) << 22) | (lane >> 32);

    /// <summary>
    /// The values of sixteen character bytes, and in <paramref name="outside"/> all ones where a byte is not a character of the alphabet.
    /// </summary>
    /// <remarks>
    /// Value = byte + offset by row: row 2 (' ' +4, '$' '%' +1, '*' '+' -3, '-' '.' '/' -4) and row 3 ('0'-'9' -48, ':' -14) from tables over
    /// the low nibble, rows 4 and 5 ('A'-'Z') -55. Membership: by low nibble, the rows 2, 3, 4, 5 (bits 0-3) where that nibble is in the
    /// alphabet, ANDed with the row's own bit.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> AlphanumericValuesSsse3(Vector128<byte> bytes, out Vector128<byte> outside)
    {
        var lowNibble = Vector128.Create((byte)0x0F);
        var three = Vector128.Create((byte)3);
        var low = bytes & lowNibble;
        var row = (bytes.AsUInt16() >> 4).AsByte() & lowNibble;
        var offset = Ssse3.Shuffle(Vector128.Create((sbyte)4, 0, 0, 0, 1, 1, 0, 0, 0, 0, -3, -3, 0, -4, -4, -4), low.AsSByte());
        offset = Sse41.BlendVariable(offset, Ssse3.Shuffle(Vector128.Create((sbyte)-48, -48, -48, -48, -48, -48, -48, -48, -48, -48, -14, 0, 0, 0, 0, 0), low.AsSByte()), Sse2.CompareEqual(row, three).AsSByte());
        offset = Sse41.BlendVariable(offset, Vector128.Create((sbyte)-55), Sse2.CompareGreaterThan(row.AsSByte(), three.AsSByte()));
        var rows = Ssse3.Shuffle(Vector128.Create((byte)0x0B, 0x0E, 0x0E, 0x0E, 0x0F, 0x0F, 0x0E, 0x0E, 0x0E, 0x0E, 0x0F, 0x05, 0x04, 0x05, 0x05, 0x05), low);
        var rowBit = Ssse3.Shuffle(Vector128.Create((byte)0, 0, 1, 2, 4, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), row);
        outside = Sse2.CompareEqual(rows & rowBit, Vector128<byte>.Zero);
        return (bytes.AsSByte() + offset).AsByte();
    }
}
#endif
