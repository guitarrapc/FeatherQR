#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals.ImageWriters;

internal sealed partial class PngEncoder
{
    /// <summary>
    /// 256-bit tier of <see cref="MarkStarts"/>: <see cref="MarkStartsVector128"/>'s step over thirty-two positions, two steps a word.
    /// The last loads of a scanline run up to thirty-one bytes into its slack.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void MarkStartsVector256(ReadOnlySpan<byte> slot, ReadOnlySpan<byte> aboveSlot, int stride, bool hasAbove, Span<ulong> before, Span<ulong> equal, Span<ulong> starts)
    {
        var words = (stride + 63) >> 6;
        AssertSlots(slot, aboveSlot, stride, hasAbove, before, equal, starts);
        ref var r = ref Unsafe.Add(ref MemoryMarshal.GetReference(slot), SlotLead);
        ref var back = ref Unsafe.Add(ref MemoryMarshal.GetReference(slot), SlotLead - 1);
        ref var a = ref Unsafe.Add(ref MemoryMarshal.GetReference(aboveSlot), SlotLead);
        for (var k = 0; k < words; k++)
        {
            ulong backBits = 0, aboveBits = 0;
            for (var j = 0; j < 64; j += 32)
            {
                var q = (k << 6) + j;
                if (q >= stride)
                    break;
                var current = Vector256.LoadUnsafe(ref r, (nuint)q);
                backBits |= (ulong)Vector256.Equals(current, Vector256.LoadUnsafe(ref back, (nuint)q)).ExtractMostSignificantBits() << j;
                if (hasAbove)
                    aboveBits |= (ulong)Vector256.Equals(current, Vector256.LoadUnsafe(ref a, (nuint)q)).ExtractMostSignificantBits() << j;
            }
            before[k] = backBits;
            equal[k] = aboveBits;
        }
        FinishStarts(before, equal, starts, stride, hasAbove);
    }
}
#endif
