#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals.BinaryDecoders;

/// <summary>
/// 128-bit syndrome kernel for the targets with neither GFNI nor NEON: x64 without the GFNI tier (no 256-bit GFNI, or .NET 8), and WebAssembly. The ARM64 kernel's
/// structure (two 16-lane accumulator groups, the same step tables for the data terms, eight bytes a step as a tree), with its one
/// multiply that has no portable instruction done by linearity.
/// </summary>
/// <remarks>
/// Every multiply here is by a per-lane constant K, and a·K is GF(2)-linear in a: the XOR, over the set bits b of a, of K·x^b.
/// <see cref="MultiplierPlanes"/> holds K·x^b for the three multipliers, so a multiply is eight rounds of a sign-bit mask, an AND and
/// an XOR, the next bit brought up by adding a to itself, with no reduction step.
/// </remarks>
internal static partial class EccBinaryDecoder
{
    // Plane (m, b), lane i = α^(s·i) · x^b for the steps s = 1, 4, 8 (m = 0, 1, 2), 32 lanes a plane.
    private const int PlaneBytes = SyndromeLanes;
    private const int MultiplierBytes = 8 * PlaneBytes;
    // Built on first use, as the step tables are: a static initializer would give the class a static constructor on every target.
    private static byte[]? multiplierPlanes;

    private static byte[] MultiplierPlanes => Volatile.Read(ref multiplierPlanes) ?? BuildMultiplierPlanes();

    private static byte[] BuildMultiplierPlanes()
    {
        // Benign race, as for the step tables: a reader never sees a partly filled array.
        var planes = new byte[3 * MultiplierBytes];
        ReadOnlySpan<int> steps = [1, 4, 8];
        for (var m = 0; m < steps.Length; m++)
        {
            for (var b = 0; b < 8; b++)
            {
                for (var i = 0; i < SyndromeLanes; i++)
                {
                    planes[m * MultiplierBytes + b * PlaneBytes + i] = GaloisField.Exp[(steps[m] * i + b) % 255];
                }
            }
        }
        Volatile.Write(ref multiplierPlanes, planes);
        return planes;
    }

    /// <summary>
    /// Computes the syndromes for one block, the contract of <see cref="ComputeSyndromesAdvSimd"/>: <paramref name="syndromes"/> has room
    /// for <see cref="SyndromeLanes"/> bytes, and lanes past <paramref name="eccCount"/> hold syndromes of unused roots.
    /// </summary>
    internal static bool ComputeSyndromesVector128(ReadOnlySpan<byte> codeword, int eccCount, Span<byte> syndromes)
    {
        ref var planes = ref MemoryMarshal.GetArrayDataReference(MultiplierPlanes);
        ref var alpha1 = ref planes;
        ref var alpha4 = ref Unsafe.Add(ref planes, MultiplierBytes);
        ref var alpha8 = ref Unsafe.Add(ref planes, 2 * MultiplierBytes);

        ref var tables = ref MemoryMarshal.GetArrayDataReference(AlphaTables);
        ref var t1 = ref tables;
        ref var t2 = ref Unsafe.Add(ref tables, StepTableStride);
        ref var t3 = ref Unsafe.Add(ref tables, 2 * StepTableStride);
        ref var cw = ref MemoryMarshal.GetReference(codeword);
        var length = codeword.Length;

        var accLow = Vector128<byte>.Zero;
        nint j = 0;

        if (eccCount <= 16)
        {
            for (; j + 8 <= length; j += 8)
            {
                var d0 = DataTerm(ref t1, ref t2, ref t3, ref cw, j, 0);
                var d1 = DataTerm(ref t1, ref t2, ref t3, ref cw, j + 4, 0);
                accLow = MultiplyByPlanes(accLow, ref alpha8) ^ MultiplyByPlanes(d0, ref alpha4) ^ d1;
            }
            for (; j + 4 <= length; j += 4)
            {
                accLow = MultiplyByPlanes(accLow, ref alpha4) ^ DataTerm(ref t1, ref t2, ref t3, ref cw, j, 0);
            }
            for (; j < length; j++)
            {
                accLow = MultiplyByPlanes(accLow, ref alpha1) ^ Vector128.Create(Unsafe.Add(ref cw, j));
            }

            return StoreSyndromes(accLow, Vector128<byte>.Zero, eccCount, syndromes);
        }

        ref var alpha1High = ref Unsafe.Add(ref alpha1, 16);
        ref var alpha4High = ref Unsafe.Add(ref alpha4, 16);
        ref var alpha8High = ref Unsafe.Add(ref alpha8, 16);
        var accHigh = Vector128<byte>.Zero;

        for (; j + 8 <= length; j += 8)
        {
            var d0 = DataTerm(ref t1, ref t2, ref t3, ref cw, j, 0);
            var d1 = DataTerm(ref t1, ref t2, ref t3, ref cw, j + 4, 0);
            var e0 = DataTerm(ref t1, ref t2, ref t3, ref cw, j, 16);
            var e1 = DataTerm(ref t1, ref t2, ref t3, ref cw, j + 4, 16);
            accLow = MultiplyByPlanes(accLow, ref alpha8) ^ MultiplyByPlanes(d0, ref alpha4) ^ d1;
            accHigh = MultiplyByPlanes(accHigh, ref alpha8High) ^ MultiplyByPlanes(e0, ref alpha4High) ^ e1;
        }
        for (; j + 4 <= length; j += 4)
        {
            accLow = MultiplyByPlanes(accLow, ref alpha4) ^ DataTerm(ref t1, ref t2, ref t3, ref cw, j, 0);
            accHigh = MultiplyByPlanes(accHigh, ref alpha4High) ^ DataTerm(ref t1, ref t2, ref t3, ref cw, j, 16);
        }
        for (; j < length; j++)
        {
            var c = Vector128.Create(Unsafe.Add(ref cw, j));
            accLow = MultiplyByPlanes(accLow, ref alpha1) ^ c;
            accHigh = MultiplyByPlanes(accHigh, ref alpha1High) ^ c;
        }

        return StoreSyndromes(accLow, accHigh, eccCount, syndromes);
    }

    /// <summary>a·K per lane, K given as its eight planes K·x^b (b = 0 first, <see cref="PlaneBytes"/> apart): bit 7 of a first, then each lower bit brought up by a + a.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> MultiplyByPlanes(Vector128<byte> a, ref byte planes)
    {
        var t = a;
        var p7 = Vector128.LessThan(t.AsSByte(), Vector128<sbyte>.Zero).AsByte() & Vector128.LoadUnsafe(ref planes, 7 * PlaneBytes);
        t += t;
        var p6 = Vector128.LessThan(t.AsSByte(), Vector128<sbyte>.Zero).AsByte() & Vector128.LoadUnsafe(ref planes, 6 * PlaneBytes);
        t += t;
        var p5 = Vector128.LessThan(t.AsSByte(), Vector128<sbyte>.Zero).AsByte() & Vector128.LoadUnsafe(ref planes, 5 * PlaneBytes);
        t += t;
        var p4 = Vector128.LessThan(t.AsSByte(), Vector128<sbyte>.Zero).AsByte() & Vector128.LoadUnsafe(ref planes, 4 * PlaneBytes);
        t += t;
        var p3 = Vector128.LessThan(t.AsSByte(), Vector128<sbyte>.Zero).AsByte() & Vector128.LoadUnsafe(ref planes, 3 * PlaneBytes);
        t += t;
        var p2 = Vector128.LessThan(t.AsSByte(), Vector128<sbyte>.Zero).AsByte() & Vector128.LoadUnsafe(ref planes, 2 * PlaneBytes);
        t += t;
        var p1 = Vector128.LessThan(t.AsSByte(), Vector128<sbyte>.Zero).AsByte() & Vector128.LoadUnsafe(ref planes, 1 * PlaneBytes);
        t += t;
        var p0 = Vector128.LessThan(t.AsSByte(), Vector128<sbyte>.Zero).AsByte() & Vector128.LoadUnsafe(ref planes);
        return ((p7 ^ p6) ^ (p5 ^ p4)) ^ ((p3 ^ p2) ^ (p1 ^ p0));
    }
}
#endif
