#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals;

internal static partial class ModeSegmenter
{
    /// <summary>
    /// The bound 8 characters a step on the portable 128-bit vectors, the tier of ARM64 and WebAssembly: the discounts by range compares, the last block overlapped and masked, a block holding a surrogate half priced one character at a time.
    /// </summary>
    internal static int CheapestSixthsVector128(ReadOnlySpan<char> text, EciMode charset)
    {
        // The overlapped last block reads the 8 characters before the end, so a shorter text would be read from before the span, and the
        // tail mask would hide that from a parity test. The dispatch keeps such texts away, and the kernel checks for itself in every build.
        if (text.Length < Vector128<ushort>.Count)
            return CheapestSixthsScalar(text, charset);
        ref var start = ref MemoryMarshal.GetReference(text);
        var length = text.Length;
        var discounts = Vector128<ushort>.Zero;
        var extraBytes = Vector128<ushort>.Zero;
        var scalar = 0;
        var vectored = 0;
        var i = 0;
        var utf8 = charset == EciMode.Utf8;
        // A lane is 16 bits wide and sums at most 28 of discount a block, so the lanes are added into the totals every 2,000 blocks before they can wrap. The horizontal sum of a ushort vector is a ushort too, so the lanes are widened first.
        var total = 0;
        var extraTotal = 0;
        var flushAt = 2000 * Vector128<ushort>.Count;
        for (; i + Vector128<ushort>.Count <= length; i += Vector128<ushort>.Count)
        {
            if (i == flushAt)
            {
                total += (int)Vector128.Sum(Vector128.WidenLower(discounts) + Vector128.WidenUpper(discounts));
                extraTotal += (int)Vector128.Sum(Vector128.WidenLower(extraBytes) + Vector128.WidenUpper(extraBytes));
                discounts = Vector128<ushort>.Zero;
                extraBytes = Vector128<ushort>.Zero;
                flushAt += 2000 * Vector128<ushort>.Count;
            }
            var c = Vector128.LoadUnsafe(ref Unsafe.As<char, ushort>(ref Unsafe.Add(ref start, i)));
            if (utf8)
            {
                if (Vector128.LessThanOrEqualAny(c - Vector128.Create((ushort)0xD800), Vector128.Create((ushort)0x7FF)))
                {
                    scalar += Utf8Sixths(text, i, i + Vector128<ushort>.Count);
                    continue;
                }
                extraBytes -= Vector128.GreaterThan(c, Vector128.Create((ushort)0x7F)) + Vector128.GreaterThan(c, Vector128.Create((ushort)0x7FF));
            }
            vectored += Vector128<ushort>.Count;
            discounts += (Vector128.LessThanOrEqual(c - Vector128.Create((ushort)'0'), Vector128.Create((ushort)9)) & Vector128.Create((ushort)(SixthsPerByte - SixthsPerDigit)))
                | ((Vector128.LessThanOrEqual(c - Vector128.Create((ushort)'A'), Vector128.Create((ushort)('Z' - 'A')))
                    | Vector128.LessThanOrEqual(c - Vector128.Create((ushort)'$'), Vector128.Create((ushort)1))
                    | Vector128.LessThanOrEqual(c - Vector128.Create((ushort)'*'), Vector128.Create((ushort)1))
                    | Vector128.LessThanOrEqual(c - Vector128.Create((ushort)'-'), Vector128.Create((ushort)2))
                    | Vector128.Equals(c, Vector128.Create((ushort)' ')) | Vector128.Equals(c, Vector128.Create((ushort)':')))
                    & Vector128.Create((ushort)(SixthsPerByte - SixthsPerAlnum)));
        }
        if (i < length)
        {
            var skip = Vector128<ushort>.Count - (length - i);
            var c = Vector128.LoadUnsafe(ref Unsafe.As<char, ushort>(ref Unsafe.Add(ref start, length - Vector128<ushort>.Count)));
            if (utf8 && Vector128.LessThanOrEqualAny(c - Vector128.Create((ushort)0xD800), Vector128.Create((ushort)0x7FF)))
            {
                scalar += Utf8Sixths(text, i, length);
            }
            else
            {
                var keep = Vector128.GreaterThanOrEqual(Vector128.Create((ushort)0, 1, 2, 3, 4, 5, 6, 7), Vector128.Create((ushort)skip));
                if (utf8)
                    extraBytes -= (Vector128.GreaterThan(c, Vector128.Create((ushort)0x7F)) + Vector128.GreaterThan(c, Vector128.Create((ushort)0x7FF))) & keep;
                vectored += length - i;
                discounts += ((Vector128.LessThanOrEqual(c - Vector128.Create((ushort)'0'), Vector128.Create((ushort)9)) & Vector128.Create((ushort)(SixthsPerByte - SixthsPerDigit)))
                | ((Vector128.LessThanOrEqual(c - Vector128.Create((ushort)'A'), Vector128.Create((ushort)('Z' - 'A')))
                    | Vector128.LessThanOrEqual(c - Vector128.Create((ushort)'$'), Vector128.Create((ushort)1))
                    | Vector128.LessThanOrEqual(c - Vector128.Create((ushort)'*'), Vector128.Create((ushort)1))
                    | Vector128.LessThanOrEqual(c - Vector128.Create((ushort)'-'), Vector128.Create((ushort)2))
                    | Vector128.Equals(c, Vector128.Create((ushort)' ')) | Vector128.Equals(c, Vector128.Create((ushort)':')))
                    & Vector128.Create((ushort)(SixthsPerByte - SixthsPerAlnum)))) & keep;
            }
        }

        return (vectored + extraTotal + (int)Vector128.Sum(Vector128.WidenLower(extraBytes) + Vector128.WidenUpper(extraBytes))) * SixthsPerByte - total - (int)Vector128.Sum(Vector128.WidenLower(discounts) + Vector128.WidenUpper(discounts)) + scalar;
    }

}
#endif
