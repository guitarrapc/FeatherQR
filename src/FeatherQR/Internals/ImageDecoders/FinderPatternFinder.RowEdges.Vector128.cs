#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals.ImageDecoders;

internal static partial class FinderPatternFinder
{
    /// <summary>
    /// Eight windows with 128-bit vectors: the same arithmetic as <see cref="ClassifyWindows16"/>, the verdicts left as lanes (0 or all bits) so the caller can test the OR of the three before any becomes bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClassifyWindows8(ref short starts, ref short ends, nuint k, out Vector128<short> strict, out Vector128<short> near, out Vector128<short> crisp)
    {
        // The arithmetic is ClassifyWindows16's on eight lanes; how the checks became integers is at the top of that method.
        // Two things are different here, both chosen on ARM64:
        //
        // 1. The verdicts leave as lanes (0 or all bits), not as bits. Turning a vector of shorts into a bitmask is one
        //    instruction on x64 and a short sequence on ARM64 (LaneBits), and a step would pay it three times while on a
        //    symbol most steps flag nothing. The caller ORs the three verdicts, makes bits of that once, and only when some
        //    lane is flagged makes bits of each verdict.
        //
        // 2. Eight windows a step, not sixteen as two halves: the second half is wasted on every row's last step, and a row
        //    of a small symbol has only a few dozen windows. Measured slower.
        var s0 = Vector128.LoadUnsafe(ref starts, k);
        var s1 = Vector128.LoadUnsafe(ref starts, k + 1);
        var s2 = Vector128.LoadUnsafe(ref starts, k + 2);
        var e0 = Vector128.LoadUnsafe(ref ends, k);
        var e1 = Vector128.LoadUnsafe(ref ends, k + 1);
        var e2 = Vector128.LoadUnsafe(ref ends, k + 2);
        var r0 = e0 - s0;
        var r1 = s1 - e0;
        var r2 = e1 - s1;
        var r3 = s2 - e1;
        var r4 = e2 - s2;
        var total = e2 - s0;
        var total3 = total + total + total;

        var seven = Vector128.Create((short)7);
        var one = Vector128.Create((short)1);
        var d0 = Vector128.Abs(total - r0 * seven);
        var d1 = Vector128.Abs(total - r1 * seven);
        var d2 = Vector128.Abs(total3 - r2 * seven);
        var d3 = Vector128.Abs(total - r3 * seven);
        var d4 = Vector128.Abs(total - r4 * seven);
        var enough = Vector128.GreaterThanOrEqual(total, seven);
        var halfTotal = (total + one) >> 1;
        var halfTotal3 = (total3 + one) >> 1;

        strict = enough
            & Vector128.LessThan(d0, halfTotal)
            & Vector128.LessThan(d1, halfTotal)
            & Vector128.LessThan(d2, halfTotal3)
            & Vector128.LessThan(d3, halfTotal)
            & Vector128.LessThan(d4, halfTotal);

        var nearLimit = Vector128.Create((short)(NearMissSevenths + 1));
        near = enough
            & Vector128.LessThan(d0, nearLimit)
            & Vector128.LessThan(d1, nearLimit)
            & Vector128.LessThan(d2, nearLimit)
            & Vector128.LessThan(d3, nearLimit)
            & Vector128.LessThan(d4, nearLimit);

        var oneUnsigned = Vector128.Create((ushort)1);
        crisp = (Vector128.LessThanOrEqual((r2 - Vector128.Create((short)3)).AsUInt16(), Vector128.Create((ushort)2))
            & Vector128.LessThanOrEqual((r0 - one).AsUInt16(), oneUnsigned)
            & Vector128.LessThanOrEqual((r1 - one).AsUInt16(), oneUnsigned)
            & Vector128.LessThanOrEqual((r3 - one).AsUInt16(), oneUnsigned)
            & Vector128.LessThanOrEqual((r4 - one).AsUInt16(), oneUnsigned)).AsInt16();
    }
}
#endif
