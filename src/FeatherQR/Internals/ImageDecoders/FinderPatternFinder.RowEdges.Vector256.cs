#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals.ImageDecoders;

internal static partial class FinderPatternFinder
{
    /// <summary>Sixteen windows with 256-bit vectors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClassifyWindows16(ref short starts, ref short ends, nuint k, out uint strict, out uint near, out uint crisp)
    {
        // How the strict ratio becomes integer arithmetic. t is the total of the five runs, r one run.
        //
        // 1. The float check takes one module as m = t / 7 and allows half a module either way:
        //      side run   |m - r|  < m / 2
        //      centre     |3m - r| < 3m / 2
        //    Multiplied by 14 the fractions go:
        //      side run   |2t - 14r| < t
        //      centre     |6t - 14r| < 3t
        //    For t = 21 that accepts side runs of 2 to 4 pixels (1.5 < r < 4.5) and a centre of 5 to 13 (4.5 < r < 13.5).
        //
        // 2. The verdict is the float form's on every input, not nearly always. A run sits exactly on an end of its band only when
        //    14r = t or 3t (3t or 9t for the centre), which makes t a multiple of 14, and then t / 7f is an integer and exact: both
        //    forms refuse the equality. Everywhere else an integer run is at least 1/14 pixel from the end of its band, far more
        //    than the rounding of t / 7f.
        //
        // 3. Both ratio checks are functions of one difference a run, d = t - 7r (3t - 7r for the centre):
        //      strict      2·|d| < t        (3t for the centre)
        //      near miss   |d| <= 10        the scalar code's (uint)(t - 7r + 10) <= 20
        //
        // 4. 2·|d| can leave a signed sixteen-bit lane, so strict is written |d| < (t + 1) >> 1, the same condition on integers
        //    (for t = 5 both say |d| <= 2). With that, 7r and 3t are the largest values a lane holds, and they fit for rows shorter
        //    than EdgeListWidthLimit: that is where the limit comes from.
        //
        //    runs 3, 3, 9, 3, 3    t = 21, every d is 0                                          accepted
        //    runs 9, 3, 3, 2, 2    t = 19, first run d = 19 - 63 = -44, 44 >= (19 + 1) >> 1 = 10   refused
        //
        // In scalar code this form is no faster than the float one, which leaves on its first failed compare. It is here because it
        // is what sixteen lanes can do at once without a branch, a division or a conversion.

        var s0 = Vector256.LoadUnsafe(ref starts, k);
        var s1 = Vector256.LoadUnsafe(ref starts, k + 1);
        var s2 = Vector256.LoadUnsafe(ref starts, k + 2);
        var e0 = Vector256.LoadUnsafe(ref ends, k);
        var e1 = Vector256.LoadUnsafe(ref ends, k + 1);
        var e2 = Vector256.LoadUnsafe(ref ends, k + 2);
        // The five runs of each window: dark, light, dark, light, dark
        var r0 = e0 - s0;
        var r1 = s1 - e0;
        var r2 = e1 - s1;
        var r3 = s2 - e1;
        var r4 = e2 - s2;
        var total = e2 - s0;
        var total3 = total + total + total;

        // |d| a run: d = t - 7r on the sides, 3t - 7r on the centre, which is expected to be three modules
        var seven = Vector256.Create((short)7);
        var one = Vector256.Create((short)1);
        var d0 = Vector256.Abs(total - r0 * seven);
        var d1 = Vector256.Abs(total - r1 * seven);
        var d2 = Vector256.Abs(total3 - r2 * seven);
        var d3 = Vector256.Abs(total - r3 * seven);
        var d4 = Vector256.Abs(total - r4 * seven);
        // Seven modules need seven pixels at least; both scalar checks refuse a shorter window first
        var enough = Vector256.GreaterThanOrEqual(total, seven);
        var halfTotal = (total + one) >> 1;
        var halfTotal3 = (total3 + one) >> 1;

        // Strict: 2·|d| < t, as |d| < (t + 1) >> 1 so the doubling cannot overflow a lane
        strict = (enough
            & Vector256.LessThan(d0, halfTotal)
            & Vector256.LessThan(d1, halfTotal)
            & Vector256.LessThan(d2, halfTotal3)
            & Vector256.LessThan(d3, halfTotal)
            & Vector256.LessThan(d4, halfTotal)).ExtractMostSignificantBits();

        // Near miss: |d| <= 10 sevenths of a pixel, a signed compare because |d| is never negative
        var nearLimit = Vector256.Create((short)(NearMissSevenths + 1));
        near = (enough
            & Vector256.LessThan(d0, nearLimit)
            & Vector256.LessThan(d1, nearLimit)
            & Vector256.LessThan(d2, nearLimit)
            & Vector256.LessThan(d3, nearLimit)
            & Vector256.LessThan(d4, nearLimit)).ExtractMostSignificantBits();

        // Small crisp runs: sides of 1 or 2 pixels, a centre of 3 to 5. x - low <= span as unsigned is low <= x <= low + span in one compare
        var oneUnsigned = Vector256.Create((ushort)1);
        crisp = (Vector256.LessThanOrEqual((r2 - Vector256.Create((short)3)).AsUInt16(), Vector256.Create((ushort)2))
            & Vector256.LessThanOrEqual((r0 - one).AsUInt16(), oneUnsigned)
            & Vector256.LessThanOrEqual((r1 - one).AsUInt16(), oneUnsigned)
            & Vector256.LessThanOrEqual((r3 - one).AsUInt16(), oneUnsigned)
            & Vector256.LessThanOrEqual((r4 - one).AsUInt16(), oneUnsigned)).ExtractMostSignificantBits();
    }
}
#endif
