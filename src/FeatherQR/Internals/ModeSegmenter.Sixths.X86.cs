#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR.Internals;

internal static partial class ModeSegmenter
{
    /// <summary>
    /// The bound 32 characters a step: the characters saturated to bytes (a character above 255 lands on 0 or 255 through the signed saturation, neither in a class), each byte's row and column
    /// of the alphanumeric alphabet looked up by nibble shuffles, and the discounts widened and summed in 16-bit lanes. Then one 16-lane block by range compares
    /// and the overlapped last block.
    /// </summary>
    internal static int CheapestSixthsAvx2(ReadOnlySpan<char> text, EciMode charset)
    {
        // The overlapped last block reads the 16 characters before the end, so a shorter text would be read from before the span, and the
        // tail mask would hide that from a parity test. The dispatch keeps such texts away, and the kernel checks for itself in every build.
        if (text.Length < Vector256<ushort>.Count)
            return CheapestSixthsScalar(text, charset);
        ref var start = ref MemoryMarshal.GetReference(text);
        var length = text.Length;
        // Row membership of each low nibble as bits 2 to 5 for rows 0x2 to 0x5 (space $ % * + - . /, digits and colon, A-O, P-Z), and the row bit of each high nibble.
        // A byte is in the alphabet where the two agree on a row. The tables live in locals: a helper taking them was not inlined.
        var loBits = Vector256.Create(
            (byte)0x2C, 0x38, 0x38, 0x38, 0x3C, 0x3C, 0x38, 0x38, 0x38, 0x38, 0x3C, 0x14, 0x10, 0x14, 0x14, 0x14,
            0x2C, 0x38, 0x38, 0x38, 0x3C, 0x3C, 0x38, 0x38, 0x38, 0x38, 0x3C, 0x14, 0x10, 0x14, 0x14, 0x14);
        var hiBits = Vector256.Create(
            (byte)0x00, 0x00, 0x04, 0x08, 0x10, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x04, 0x08, 0x10, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
        var lowNibble = Vector256.Create((byte)0x0F);
        var digitWeight = Vector256.Create((byte)(SixthsPerByte - SixthsPerDigit));
        var alnumWeight = Vector256.Create((byte)(SixthsPerByte - SixthsPerAlnum));
        var discounts = Vector256<ushort>.Zero;
        var extraBytes = Vector256<ushort>.Zero;
        var scalar = 0;
        var vectored = 0;
        var i = 0;
        var utf8 = charset == EciMode.Utf8;
        // A lane is 16 bits wide and sums at most 56 of discount a block, so the lanes are added into the totals every 1,000 blocks before they can wrap. The horizontal sum of a ushort vector is a ushort too, so the lanes are widened first.
        var total = 0;
        var extraTotal = 0;
        var flushAt = 1000 * 2 * Vector256<ushort>.Count;
        for (; i + 2 * Vector256<ushort>.Count <= length; i += 2 * Vector256<ushort>.Count)
        {
            if (i == flushAt)
            {
                total += (int)Vector256.Sum(Vector256.WidenLower(discounts) + Vector256.WidenUpper(discounts));
                extraTotal += (int)Vector256.Sum(Vector256.WidenLower(extraBytes) + Vector256.WidenUpper(extraBytes));
                discounts = Vector256<ushort>.Zero;
                extraBytes = Vector256<ushort>.Zero;
                flushAt += 1000 * 2 * Vector256<ushort>.Count;
            }
            var lo = Vector256.LoadUnsafe(ref Unsafe.As<char, ushort>(ref Unsafe.Add(ref start, i)));
            var hi = Vector256.LoadUnsafe(ref Unsafe.As<char, ushort>(ref Unsafe.Add(ref start, i + Vector256<ushort>.Count)));
            if (utf8)
            {
                if (AnySurrogate(lo) || AnySurrogate(hi))
                {
                    scalar += Utf8Sixths(text, i, i + 2 * Vector256<ushort>.Count);
                    continue;
                }
                extraBytes -= ExtraUtf8Bytes(lo) + ExtraUtf8Bytes(hi);
            }
            vectored += 2 * Vector256<ushort>.Count;
            // The pack permutes lanes within each 128-bit half, which a sum does not mind.
            var bytes = Avx2.PackUnsignedSaturate(lo.AsInt16(), hi.AsInt16());
            var inRow = Avx2.Shuffle(loBits, bytes & lowNibble) & Avx2.Shuffle(hiBits, Avx2.ShiftRightLogical(bytes.AsUInt16(), 4).AsByte() & lowNibble);
            var alnum = Vector256.GreaterThan(inRow, Vector256<byte>.Zero);
            // A digit shares the colon's row and is told apart by its low nibble.
            var digit = Vector256.Equals(bytes & Vector256.Create((byte)0xF0), Vector256.Create((byte)0x30)) & Vector256.LessThanOrEqual(bytes, Vector256.Create((byte)0x39));
            var weights = Vector256.ConditionalSelect(digit, digitWeight, alnum & alnumWeight);
            discounts += Avx2.UnpackLow(weights, Vector256<byte>.Zero).AsUInt16() + Avx2.UnpackHigh(weights, Vector256<byte>.Zero).AsUInt16();
        }
        if (i + Vector256<ushort>.Count <= length)
        {
            var c = Vector256.LoadUnsafe(ref Unsafe.As<char, ushort>(ref Unsafe.Add(ref start, i)));
            if (utf8 && AnySurrogate(c))
            {
                scalar += Utf8Sixths(text, i, i + Vector256<ushort>.Count);
            }
            else
            {
                if (utf8)
                    extraBytes -= ExtraUtf8Bytes(c);
                vectored += Vector256<ushort>.Count;
                discounts += (Vector256.LessThanOrEqual(c - Vector256.Create((ushort)'0'), Vector256.Create((ushort)9)) & Vector256.Create((ushort)(SixthsPerByte - SixthsPerDigit)))
                | ((Vector256.LessThanOrEqual(c - Vector256.Create((ushort)'A'), Vector256.Create((ushort)('Z' - 'A')))
                    | Vector256.LessThanOrEqual(c - Vector256.Create((ushort)'$'), Vector256.Create((ushort)1))
                    | Vector256.LessThanOrEqual(c - Vector256.Create((ushort)'*'), Vector256.Create((ushort)1))
                    | Vector256.LessThanOrEqual(c - Vector256.Create((ushort)'-'), Vector256.Create((ushort)2))
                    | Vector256.Equals(c, Vector256.Create((ushort)' ')) | Vector256.Equals(c, Vector256.Create((ushort)':')))
                    & Vector256.Create((ushort)(SixthsPerByte - SixthsPerAlnum)));
            }
            i += Vector256<ushort>.Count;
        }
        if (i < length)
        {
            // The last 16 characters, the first of them priced already and masked out. The sum is not idempotent, so an overlapped read needs the mask.
            var skip = Vector256<ushort>.Count - (length - i);
            var c = Vector256.LoadUnsafe(ref Unsafe.As<char, ushort>(ref Unsafe.Add(ref start, length - Vector256<ushort>.Count)));
            if (utf8 && AnySurrogate(c))
            {
                scalar += Utf8Sixths(text, i, length);
            }
            else
            {
                var keep = Vector256.GreaterThanOrEqual(Vector256.Create((ushort)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15), Vector256.Create((ushort)skip));
                if (utf8)
                    extraBytes -= ExtraUtf8Bytes(c) & keep;
                vectored += length - i;
                discounts += ((Vector256.LessThanOrEqual(c - Vector256.Create((ushort)'0'), Vector256.Create((ushort)9)) & Vector256.Create((ushort)(SixthsPerByte - SixthsPerDigit)))
                | ((Vector256.LessThanOrEqual(c - Vector256.Create((ushort)'A'), Vector256.Create((ushort)('Z' - 'A')))
                    | Vector256.LessThanOrEqual(c - Vector256.Create((ushort)'$'), Vector256.Create((ushort)1))
                    | Vector256.LessThanOrEqual(c - Vector256.Create((ushort)'*'), Vector256.Create((ushort)1))
                    | Vector256.LessThanOrEqual(c - Vector256.Create((ushort)'-'), Vector256.Create((ushort)2))
                    | Vector256.Equals(c, Vector256.Create((ushort)' ')) | Vector256.Equals(c, Vector256.Create((ushort)':')))
                    & Vector256.Create((ushort)(SixthsPerByte - SixthsPerAlnum)))) & keep;
            }
        }

        return (vectored + extraTotal + (int)Vector256.Sum(Vector256.WidenLower(extraBytes) + Vector256.WidenUpper(extraBytes))) * SixthsPerByte - total - (int)Vector256.Sum(Vector256.WidenLower(discounts) + Vector256.WidenUpper(discounts)) + scalar;
    }

    /// <summary>Whether any of the 16 characters is a surrogate half.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AnySurrogate(Vector256<ushort> c)
        => Vector256.LessThanOrEqualAny(c - Vector256.Create((ushort)0xD800), Vector256.Create((ushort)0x7FF));

    /// <summary>The UTF-8 bytes each character takes beyond its first, one above 0x7F and one more above 0x7FF, as negated lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ushort> ExtraUtf8Bytes(Vector256<ushort> c)
        => Vector256.GreaterThan(c, Vector256.Create((ushort)0x7F)) + Vector256.GreaterThan(c, Vector256.Create((ushort)0x7FF));

}
#endif
