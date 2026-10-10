#if NET8_0_OR_GREATER
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals.ImageDecoders;

internal static partial class LuminanceHalver
{
    /// <summary>
    /// 128-bit tier: <see cref="HalveVector256"/>'s step on two 128-bit loads a row, 32 pixels of each row into 16.
    /// The spans are sized as <see cref="Halve"/> checks them.
    /// </summary>
    internal static void HalveVector128(ReadOnlySpan<byte> source, int width, int height, Span<byte> destination)
    {
        int halfWidth = width / 2, halfHeight = height / 2;
        Debug.Assert(source.Length >= (long)width * height && destination.Length >= (long)halfWidth * halfHeight, "the entry point checks the lengths");
        var lowBytes = Vector128.Create((ushort)0x00FF);
        var round = Vector128.Create((ushort)2);
        for (var y = 0; y < halfHeight; y++)
        {
            ref var top = ref Unsafe.Add(ref MemoryMarshal.GetReference(source), 2 * y * width);
            ref var bottom = ref Unsafe.Add(ref top, width);
            ref var row = ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), y * halfWidth);
            var x = 0;
            for (; x <= halfWidth - 16; x += 16)
            {
                var t0 = Vector128.LoadUnsafe(ref top, (nuint)(2 * x)).AsUInt16();
                var t1 = Vector128.LoadUnsafe(ref top, (nuint)(2 * x + 16)).AsUInt16();
                var b0 = Vector128.LoadUnsafe(ref bottom, (nuint)(2 * x)).AsUInt16();
                var b1 = Vector128.LoadUnsafe(ref bottom, (nuint)(2 * x + 16)).AsUInt16();
                var sums0 = (t0 & lowBytes) + Vector128.ShiftRightLogical(t0, 8) + (b0 & lowBytes) + Vector128.ShiftRightLogical(b0, 8) + round;
                var sums1 = (t1 & lowBytes) + Vector128.ShiftRightLogical(t1, 8) + (b1 & lowBytes) + Vector128.ShiftRightLogical(b1, 8) + round;
                Vector128.Narrow(Vector128.ShiftRightLogical(sums0, 2), Vector128.ShiftRightLogical(sums1, 2)).StoreUnsafe(ref row, (nuint)x);
            }
            HalveRowFrom(source.Slice(2 * y * width, 2 * halfWidth), source.Slice((2 * y + 1) * width, 2 * halfWidth), destination.Slice(y * halfWidth, halfWidth), x);
        }
    }
}
#endif
