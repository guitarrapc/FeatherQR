#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// The Alphanumeric and Numeric payload writers on ARM64 AdvSimd (NEON).
/// </summary>
/// <remarks>
/// <para>
/// Alphanumeric, sixteen characters a step and then one step of eight, as the SSE4.1 tier (QRBinaryEncoder.X86.cs): the chars narrow with
/// unsigned saturation (a char past 0xFF to 0xFF), and one TBL over 64 bytes, indexed by the byte less 0x20, gives the value plus one, and
/// 0 for a character outside the alphabet, including any whose index is past 63. A step with a 0 leaves the run to the portable writer,
/// which checks it again and throws there. MLA forms the pairs in 16-bit lanes, and USRA two pairs in 22 bits and two of those in 44, so a
/// step of sixteen is two 44-bit appends.
/// </para>
/// <para>
/// Numeric, fifteen digits from sixteen chars, the portable writer's 50-bit append: the low bytes of the chars, three TBLs laying out the
/// hundreds, tens and ones of five groups, UMULL / UMLAL / UADDW the groups, and USRA the pairs and the 40 bits of the first four, joined with
/// the fifth in scalar.
/// </para>
/// <para>
/// A run long enough for a step runs its steps on a local copy of the writer, stored back before anything else writes: reached through its
/// reference, the Alphanumeric step took 0.52 of the portable writer's time at 4,296 characters, against 0.41 with the copy (Apple M2,
/// 2026-10-05). The copy stays in registers only on .NET 10. The .NET 8 JIT and NativeAOT 8 keep it on the stack (BitWriter is a span and three
/// more fields, which .NET 8 does not promote), so every append there goes through memory and the steps gain less: from 300 characters up 0.60 to
/// 0.64 of the portable writer's time against 0.40 to 0.43, and from 500 digits 0.70 to 0.82 against 0.57 to 0.61. The dispatch therefore
/// enters each step from a length of its own per runtime (<see cref="AlphanumericAdvSimdMinimum"/>, <see cref="NumericAdvSimdMinimum"/>).
/// </para>
/// </remarks>
internal ref partial struct QRBinaryEncoder
{
#if NET10_0_OR_GREATER
    /// <summary>
    /// The shortest run the Alphanumeric step is entered for: every run the dispatch passes on .NET 10, where the step took 0.85 to 1.00 of
    /// the portable writer's time at 8 to 15 characters and 0.59 to 0.77 at 16 to 40, on the JIT and NativeAOT (Apple M2, 2026-10-05).
    /// </summary>
    private const int AlphanumericAdvSimdMinimum = 8;

    /// <summary>
    /// The shortest run the Numeric step is entered for on .NET 10. A step reads sixteen chars to write fifteen, so below 40 digits it runs
    /// once or twice: there it took 0.85 to 1.06 of the portable writer's time (1.06 at 28 and 30), 0.80 to 1.00 at 40 to 64 digits and
    /// 0.57 to 0.61 from 500.
    /// </summary>
    private const int NumericAdvSimdMinimum = 40;
#else
    /// <summary>
    /// The shortest run the Alphanumeric step is entered for on .NET 8: a run of 15 or 31 characters, which ends in the step of eight and a
    /// tail of seven, took 1.05 to 1.10 of the portable writer's time there, and from 32 characters the step took 0.60 to 0.96.
    /// </summary>
    private const int AlphanumericAdvSimdMinimum = 32;

    /// <summary>
    /// The shortest run the Numeric step is entered for on .NET 8: it took 1.03 to 1.45 of the portable writer's time at 16 to 48 digits,
    /// 0.89 to 1.02 at 64 to 128 and 0.70 to 0.90 from 160.
    /// </summary>
    private const int NumericAdvSimdMinimum = 160;
#endif

    /// <summary>The value plus one of each character 0x20-0x5F, 0 outside the alphabet.</summary>
    private static ReadOnlySpan<byte> AlphanumericValuesPlusOne =>
    [
        37, 0, 0, 0, 38, 39, 0, 0, 0, 0, 40, 41, 0, 42, 43, 44,     // ' ' '$' '%' '*' '+' '-' '.' '/'
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 45, 0, 0, 0, 0, 0,           // '0'-'9' ':'
        0, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25,
        26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 0, 0, 0, 0, 0,  // 'A'-'Z'
    ];

    /// <summary>The Alphanumeric writer on AdvSimd: sixteen characters a step, then one step of eight, then the portable writer.</summary>
    internal static void WriteAlphanumericAdvSimd(ref BitWriter writer, ReadOnlySpan<char> chars)
    {
        ref var t = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(chars));
        ref var v = ref MemoryMarshal.GetReference(AlphanumericValuesPlusOne);
        var table = (Vector128.LoadUnsafe(ref v), Vector128.LoadUnsafe(ref v, 16), Vector128.LoadUnsafe(ref v, 32), Vector128.LoadUnsafe(ref v, 48));
        var i = 0;

        if (chars.Length >= 16)
        {
            var local = writer;
            for (; i + 16 <= chars.Length; i += 16)
            {
                var bytes = AdvSimd.ExtractNarrowingSaturateUpper(
                    AdvSimd.ExtractNarrowingSaturateLower(Vector128.LoadUnsafe(ref t, (nuint)i)), Vector128.LoadUnsafe(ref t, (nuint)(i + 8)));
                var looked = AdvSimd.Arm64.VectorTableLookup(table, bytes - Vector128.Create((byte)0x20));
                if (AdvSimd.Arm64.MinAcross(looked).ToScalar() == 0)
                    break;
                var fields = AlphanumericFieldsAdvSimd((looked - Vector128<byte>.One).AsUInt16());
                local.WriteWide(fields.ToScalar(), 44);
                local.WriteWide(fields.GetElement(1), 44);
            }
            writer = local;
        }

        if (i + 8 <= chars.Length)
        {
            var looked = AdvSimd.VectorTableLookup(table, AdvSimd.ExtractNarrowingSaturateLower(Vector128.LoadUnsafe(ref t, (nuint)i)) - Vector64.Create((byte)0x20));
            if (AdvSimd.Arm64.MinAcross(looked).ToScalar() != 0)
            {
                var values = (looked - Vector64<byte>.One).AsUInt16();
                var pairs = AdvSimd.MultiplyAdd(values >> 8, values & Vector64.Create((ushort)0xFF), Vector64.Create((ushort)45)).AsUInt32();
                // the last USRA on the 128-bit form, as the step of sixteen takes it: the .NET 8 reference bounds the 64-bit scalar form's shift to 1-8 (CA1857)
                var quads = AdvSimd.ShiftRightLogicalAdd(pairs >> 16, pairs << 16, 5).AsUInt64().ToVector128Unsafe();
                writer.WriteWide(AdvSimd.ShiftRightLogicalAdd(quads >> 32, quads << 32, 10).ToScalar(), 44);
                i += 8;
            }
        }
        WriteAlphanumericScalar(ref writer, chars, i);
    }

    /// <summary>
    /// Sixteen values, two to a 16-bit lane (first in the low byte), as two 44-bit fields: first * 45 + second in each lane by MLA, two
    /// pairs in 22 bits and two of those in 44 by USRA.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> AlphanumericFieldsAdvSimd(Vector128<ushort> values)
    {
        var pairs = AdvSimd.MultiplyAdd(values >> 8, values & Vector128.Create((ushort)0xFF), Vector128.Create((ushort)45)).AsUInt32();
        var quads = AdvSimd.ShiftRightLogicalAdd(pairs >> 16, pairs << 16, 5).AsUInt64();
        return AdvSimd.ShiftRightLogicalAdd(quads >> 32, quads << 32, 10);
    }

    /// <summary>The Numeric writer on AdvSimd: fifteen digits a step, then the portable writer.</summary>
    internal static void WriteNumericAdvSimd(ref BitWriter writer, ReadOnlySpan<char> digits)
    {
        ref var t = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(digits));
        var i = 0;

        if (digits.Length >= 16)
        {
            // the five groups' digits in lanes 0-3 and 5, so the pairs are (g0 g1, g2 g3, g4, 0) and the fields (40 bits, g4 << 20)
            var hundreds = Vector64.Create((byte)0, 3, 6, 9, 0xFF, 12, 0xFF, 0xFF);
            var tens = Vector64.Create((byte)1, 4, 7, 10, 0xFF, 13, 0xFF, 0xFF);
            var ones = Vector64.Create((byte)2, 5, 8, 11, 0xFF, 14, 0xFF, 0xFF);
            var local = writer;
            // a step reads sixteen chars and writes fifteen, so a sixteenth must exist
            for (; i + 15 < digits.Length; i += 15)
            {
                var bytes = AdvSimd.Arm64.UnzipEven(Vector128.LoadUnsafe(ref t, (nuint)i).AsByte(), Vector128.LoadUnsafe(ref t, (nuint)(i + 8)).AsByte())
                    - Vector128.Create((byte)'0');
                var groups = AdvSimd.AddWideningLower(
                    AdvSimd.MultiplyWideningLowerAndAdd(
                        AdvSimd.MultiplyWideningLower(AdvSimd.VectorTableLookup(bytes, hundreds), Vector64.Create((byte)100)),
                        AdvSimd.VectorTableLookup(bytes, tens), Vector64.Create((byte)10)),
                    AdvSimd.VectorTableLookup(bytes, ones)).AsUInt32();
                var pairs = AdvSimd.ShiftRightLogicalAdd(groups >> 16, groups << 16, 6).AsUInt64();
                var fields = AdvSimd.ShiftRightLogicalAdd(pairs >> 32, pairs << 32, 12);
                local.WriteWide((fields.ToScalar() << 10) | (fields.GetElement(1) >> 20), 50);
            }
            writer = local;
        }
        WriteNumericScalar(ref writer, digits, i);
    }
}
#endif
