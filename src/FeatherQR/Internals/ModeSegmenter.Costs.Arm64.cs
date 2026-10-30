#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace FeatherQR.Internals;

internal static partial class ModeSegmenter
{
    /// <summary>Whether the cost run of a one-byte charset with Byte allowed, costs only, takes the NEON lane form on this machine and text.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TakesLaneCostsAdvSimd(int length) => AdvSimd.Arm64.IsSupported && length <= MaxLaneCostChars;

    /// <summary>
    /// <see cref="CostsLatinSse41"/> in NEON, on the same lanes and continuation table: tbl moves the states to the lanes they continue into, a saturating add a class, one more for the
    /// openings from the cheapest state, and a lane minimum. NEON's minimum across a vector (uminv) has no index, so the cheapest state is carried as a broadcast and taken from the old states:
    /// the cheapest opened lane is the old cheapest plus the class's smallest opening, and the new cheapest is the smaller of that and the minimum of the continued lanes, a reduction that
    /// runs beside each character's chain instead of on it. Only the final state needs the index. Same answer as <see cref="CostsLatin"/>, including the lowest state among equal final costs.
    /// </summary>
    /// <remarks>
    /// There is no tracked form. The parents need the cheapest state's index every character, which makes it slower than the scalar loop on alternating text.
    /// </remarks>
    internal static int CostsLatinAdvSimd(ReadOnlySpan<char> text, int openNumeric, int openAlnum, int openByte, bool allowAlnum, out int finalState)
    {
        const ushort F = 0xFFFF;
        var oN = (ushort)openNumeric;
        var oA = allowAlnum ? (ushort)openAlnum : F;
        var oB = (ushort)openByte;
        var contOther = Vector128.Create(F, F, F, F, F, (ushort)8, F, F);
        var contAlnum = allowAlnum ? Vector128.Create(F, F, F, (ushort)5, (ushort)6, (ushort)8, F, F) : contOther;
        var contDigit = allowAlnum ? Vector128.Create((ushort)3, (ushort)4, (ushort)3, (ushort)5, (ushort)6, (ushort)8, F, F) : Vector128.Create((ushort)3, (ushort)4, (ushort)3, F, F, (ushort)8, F, F);
        var openOther = Vector128.Create(F, F, F, F, F, oB, F, F);
        var openAlnumV = Vector128.Create(F, F, F, F, oA, oB, F, F);
        var openDigit = Vector128.Create(F, oN, F, F, oA, oB, F, F);
        // The smallest opening of each class, the lowest of its open lanes.
        var minOpenOther = Vector128.Create(oB);
        var minOpenAlnum = Vector128.Create(Math.Min(oA, oB));
        var minOpenDigit = Vector128.Create(Math.Min(oN, Math.Min(oA, oB)));
        var continueMask = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(continueLanes));
        var allF = Vector128.Create(F);

        var s = allF;
        var cheapest = Vector128<ushort>.Zero;
        var length = text.Length;
        for (var i = 0; i < length; i++)
        {
            var cls = ClassOf(text[i]);
            Vector128<ushort> cont, open, minOpen;
            if (cls == ClassDigit)
            {
                cont = contDigit;
                open = openDigit;
                minOpen = minOpenDigit;
            }
            else if (cls != ClassOther)
            {
                cont = contAlnum;
                open = openAlnumV;
                minOpen = minOpenAlnum;
            }
            else if (i + 1 < length && ClassOf(text[i + 1]) == ClassOther)
            {
                var run = 2;
                i++;
                while (i + 1 < length && ClassOf(text[i + 1]) == ClassOther)
                {
                    run++;
                    i++;
                }
                var b = Math.Min(Math.Min(s.GetElement(5) + 8 * run, cheapest.GetElement(0) + openByte + 8 * (run - 1)), F);
                s = AdvSimd.Insert(allF, 5, (ushort)b);
                cheapest = Vector128.Create((ushort)b);
                continue;
            }
            else
            {
                cont = contOther;
                open = openOther;
                minOpen = minOpenOther;
            }

            var continued = AdvSimd.AddSaturate(AdvSimd.Arm64.VectorTableLookup(s.AsByte(), continueMask).AsUInt16(), cont);
            s = AdvSimd.Min(continued, AdvSimd.AddSaturate(cheapest, open));
            cheapest = AdvSimd.Min(AdvSimd.DuplicateSelectedScalarToVector128(AdvSimd.Arm64.MinAcross(continued), 0), AdvSimd.AddSaturate(cheapest, minOpen));
        }

        var m = AdvSimd.Arm64.MinAcross(s);
        int cost = m.ToScalar();
        if (cost == F)
        {
            finalState = StateByte;
            return Unreachable;
        }
        // The lowest lane holding the minimum: a lane that does not hold it reads 0xFFFF, one that does its number.
        var laneNumbers = Vector128.Create((ushort)0, 1, 2, 3, 4, 5, 6, 7);
        finalState = AdvSimd.Arm64.MinAcross(AdvSimd.OrNot(laneNumbers, AdvSimd.CompareEqual(s, AdvSimd.DuplicateSelectedScalarToVector128(m, 0)))).ToScalar();
        return cost;
    }
}
#endif
