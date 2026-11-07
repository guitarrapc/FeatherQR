#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals.ImageWriters;

internal sealed partial class PngEncoder
{
    /// <summary>
    /// 128-bit tier of <see cref="MarkStarts"/>: the equal bytes sixteen positions a compare, each compare's sign bits gathered into the
    /// word. The compare with the byte before loads from one position back, which at position 0 is the slot's lead, and the last loads
    /// of a scanline run into its slack. Both reach only positions the start words force or clear.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void MarkStartsVector128(ReadOnlySpan<byte> slot, ReadOnlySpan<byte> aboveSlot, int stride, bool hasAbove, Span<ulong> before, Span<ulong> equal, Span<ulong> starts)
    {
        var words = (stride + 63) >> 6;
        AssertSlots(slot, aboveSlot, stride, hasAbove, before, equal, starts);
        ref var r = ref Unsafe.Add(ref MemoryMarshal.GetReference(slot), SlotLead);
        ref var back = ref Unsafe.Add(ref MemoryMarshal.GetReference(slot), SlotLead - 1);
        ref var a = ref Unsafe.Add(ref MemoryMarshal.GetReference(aboveSlot), SlotLead);
        for (var k = 0; k < words; k++)
        {
            ulong backBits = 0, aboveBits = 0;
            for (var j = 0; j < 64; j += 16)
            {
                var q = (k << 6) + j;
                if (q >= stride)
                    break;
                var current = Vector128.LoadUnsafe(ref r, (nuint)q);
                backBits |= (ulong)Vector128.Equals(current, Vector128.LoadUnsafe(ref back, (nuint)q)).ExtractMostSignificantBits() << j;
                if (hasAbove)
                    aboveBits |= (ulong)Vector128.Equals(current, Vector128.LoadUnsafe(ref a, (nuint)q)).ExtractMostSignificantBits() << j;
            }
            before[k] = backBits;
            equal[k] = aboveBits;
        }
        FinishStarts(before, equal, starts, stride, hasAbove);
    }
}
#endif
