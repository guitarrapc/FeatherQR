#if NET8_0_OR_GREATER
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals.ImageDecoders;

internal static partial class LuminanceHalver
{
    /// <summary>
    /// 256-bit tier: 64 pixels of each row a step into 32. Each row's bytes are read as 16-bit lanes, so a lane holds a horizontal pair, the two rows add, and the means narrow back to bytes.
    /// A step loads both rows before it stores, so a row halved in place reads nothing an earlier step wrote; the row ends on the scalar tier, not on a last step laid over pixels already written.
    /// The spans are sized as <see cref="Halve"/> checks them.
    /// </summary>
    internal static void HalveVector256(ReadOnlySpan<byte> source, int width, int height, Span<byte> destination)
    {
        int halfWidth = width / 2, halfHeight = height / 2;
        Debug.Assert(source.Length >= (long)width * height && destination.Length >= (long)halfWidth * halfHeight, "the entry point checks the lengths");
        var lowBytes = Vector256.Create((ushort)0x00FF);
        var round = Vector256.Create((ushort)2);
        for (var y = 0; y < halfHeight; y++)
        {
            ref var top = ref Unsafe.Add(ref MemoryMarshal.GetReference(source), 2 * y * width);
            ref var bottom = ref Unsafe.Add(ref top, width);
            ref var row = ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), y * halfWidth);
            var x = 0;
            for (; x <= halfWidth - 32; x += 32)
            {
                var t0 = Vector256.LoadUnsafe(ref top, (nuint)(2 * x)).AsUInt16();
                var t1 = Vector256.LoadUnsafe(ref top, (nuint)(2 * x + 32)).AsUInt16();
                var b0 = Vector256.LoadUnsafe(ref bottom, (nuint)(2 * x)).AsUInt16();
                var b1 = Vector256.LoadUnsafe(ref bottom, (nuint)(2 * x + 32)).AsUInt16();
                // Each lane: the block's four pixels and the rounding, 1,022 at most
                var sums0 = (t0 & lowBytes) + Vector256.ShiftRightLogical(t0, 8) + (b0 & lowBytes) + Vector256.ShiftRightLogical(b0, 8) + round;
                var sums1 = (t1 & lowBytes) + Vector256.ShiftRightLogical(t1, 8) + (b1 & lowBytes) + Vector256.ShiftRightLogical(b1, 8) + round;
                Vector256.Narrow(Vector256.ShiftRightLogical(sums0, 2), Vector256.ShiftRightLogical(sums1, 2)).StoreUnsafe(ref row, (nuint)x);
            }
            HalveRowFrom(source.Slice(2 * y * width, 2 * halfWidth), source.Slice((2 * y + 1) * width, 2 * halfWidth), destination.Slice(y * halfWidth, halfWidth), x);
        }
    }
}
#endif
