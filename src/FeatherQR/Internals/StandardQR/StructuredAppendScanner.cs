using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
#endif

namespace FeatherQR.Internals.StandardQR;

/// <summary>Character boundaries used by the single-mode Structured Append cost model.</summary>
internal static class StructuredAppendScanner
{
    /// <summary>The leading Numeric and Alphanumeric lengths; digits belong to both alphabets.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ModeBoundaries(ReadOnlySpan<char> text, out int digits, out int alnum)
    {
#if NET8_0_OR_GREATER
        if (AdvSimd.Arm64.IsSupported && text.Length >= 16)
        {
            ModeBoundariesAdvSimd(text, out digits, out alnum);
            return;
        }
#endif
        ModeBoundariesScalar(text, out digits, out alnum);
    }

    internal static void ModeBoundariesScalar(ReadOnlySpan<char> text, out int digits, out int alnum)
    {
        var d = 0;
        while (d < text.Length && (uint)(text[d] - '0') <= 9)
            d++;
        var a = d;
        while (a < text.Length && CharacterSets.IsAlphanumeric(text[a]))
            a++;
        digits = d;
        alnum = a;
    }

    /// <summary>Longest UTF-8 prefix within the byte budget, without cutting a surrogate pair.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Utf8PrefixLength(ReadOnlySpan<char> text, int bytes)
    {
#if NET8_0_OR_GREATER
        if (AdvSimd.Arm64.IsSupported && text.Length >= 8 && bytes >= 8)
            return Utf8PrefixLengthAdvSimd(text, bytes);
#endif
        return Utf8PrefixLengthScalar(text, bytes);
    }

    internal static int Utf8PrefixLengthScalar(ReadOnlySpan<char> text, int bytes)
    {
        var used = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            int cost, step = 1;
            if (c < 0x80)
                cost = 1;
            else if (c < 0x800)
                cost = 2;
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                (cost, step) = (4, 2);
            else
                cost = 3; // A BMP character or a lone surrogate written as U+FFFD.
            if (used + cost > bytes)
                break;
            used += cost;
            i += step;
        }
        return i;
    }

#if NET8_0_OR_GREATER
    internal static void ModeBoundariesAdvSimd(ReadOnlySpan<char> text, out int digits, out int alnum)
    {
        ref var origin = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(text));
        var n = text.Length;
        var d = 0;
        // Most Byte chunks leave immediately. Do not prepare vector constants for them.
        if (n > 0 && CharacterSets.IsNumeric(text[0]))
        {
            while (d <= n - 16)
            {
                var lo = Vector128.LoadUnsafe(ref origin, (nuint)d) - Vector128.Create((ushort)'0');
                var hi = Vector128.LoadUnsafe(ref origin, (nuint)(d + 8)) - Vector128.Create((ushort)'0');
                if (AdvSimd.Arm64.MaxAcross(AdvSimd.Max(lo, hi)).ToScalar() > 9)
                    break;
                d += 16;
            }
            while (d < n && CharacterSets.IsNumeric(text[d]))
                d++;
        }

        var a = d;
        if (a < n && CharacterSets.IsAlphanumeric(text[a]))
        {
            // Each byte holds the membership bits of eight ASCII characters. TBL returns
            // zero beyond this 128-character table; saturating narrowing prevents a wide
            // UTF-16 character from aliasing an ASCII member through its low byte.
            var alphabet = Vector128.Create((byte)0, 0, 0, 0, 0x31, 0xEC, 0xFF, 0x07, 0xFE, 0xFF, 0xFF, 0x07, 0, 0, 0, 0);
            while (a <= n - 16)
            {
                var lo = Vector128.LoadUnsafe(ref origin, (nuint)a);
                var hi = Vector128.LoadUnsafe(ref origin, (nuint)(a + 8));
                var chars = AdvSimd.ExtractNarrowingSaturateUpper(AdvSimd.ExtractNarrowingSaturateLower(lo), hi);
                var masks = AdvSimd.Arm64.VectorTableLookup(alphabet, AdvSimd.ShiftRightLogical(chars, 3));
                var bit = AdvSimd.ShiftLogical(Vector128.Create((byte)1), (chars & Vector128.Create((byte)7)).AsSByte());
                if (AdvSimd.Arm64.MinAcross(masks & bit).ToScalar() == 0)
                    break;
                a += 16;
            }
            while (a < n && CharacterSets.IsAlphanumeric(text[a]))
                a++;
        }
        digits = d;
        alnum = a;
    }

    internal static int Utf8PrefixLengthAdvSimd(ReadOnlySpan<char> text, int bytes)
    {
        ref var origin = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(text));
        var i = 0;
        // A BMP block costs at most three bytes per code unit. With that headroom it
        // needs no budget branch after counting. Below it, use smaller blocks and a tail.
        while (i <= text.Length - 16 && bytes >= 48)
        {
            var lo = Vector128.LoadUnsafe(ref origin, (nuint)i);
            var hi = Vector128.LoadUnsafe(ref origin, (nuint)(i + 8));
            var surrogate = Surrogates(lo) | Surrogates(hi);
            if (AdvSimd.Arm64.MaxAcross(surrogate).ToScalar() != 0)
                return i + Utf8Pairs16(text.Slice(i), bytes);
            bytes -= AdvSimd.Arm64.AddAcross(BmpByteCosts(lo) + BmpByteCosts(hi)).ToScalar();
            i += 16;
        }
        while (i <= text.Length - 8 && bytes >= 8)
        {
            var chars = Vector128.LoadUnsafe(ref origin, (nuint)i);
            if (AdvSimd.Arm64.MaxAcross(Surrogates(chars)).ToScalar() != 0)
                return i + Utf8Pairs8(text.Slice(i), bytes);
            var cost = AdvSimd.Arm64.AddAcross(BmpByteCosts(chars)).ToScalar();
            if (cost > bytes)
                break;
            bytes -= cost;
            i += 8;
        }
        return i + Utf8PrefixLengthScalar(text.Slice(i), bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Surrogates(Vector128<ushort> chars)
        => AdvSimd.CompareEqual(chars & Vector128.Create((ushort)0xF800), Vector128.Create((ushort)0xD800));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> BmpByteCosts(Vector128<ushort> chars)
        => Vector128.Create((ushort)1)
            - AdvSimd.CompareGreaterThan(chars, Vector128.Create((ushort)0x7F))
            - AdvSimd.CompareGreaterThan(chars, Vector128.Create((ushort)0x7FF));

    // Separate entry points let each loop use a constant width. Once a surrogate is
    // encountered, count pairs for the remaining suffix rather than repeatedly trying
    // and rejecting the same BMP block as a scalar cursor advances through it.
    private static int Utf8Pairs16(ReadOnlySpan<char> text, int bytes) => Utf8Pairs(text, bytes, 16);
    private static int Utf8Pairs8(ReadOnlySpan<char> text, int bytes) => Utf8Pairs(text, bytes, 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Utf8Pairs(ReadOnlySpan<char> text, int bytes, int width)
    {
        ref var origin = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(text));
        var i = 0;
        while (i <= text.Length - width && bytes >= width)
        {
            var lo = Vector128.LoadUnsafe(ref origin, (nuint)i);
            var previous = AdvSimd.ExtractVector128(Vector128<ushort>.Zero, lo, 7);
            var extraBytes = PairExtraBytes(lo, previous);
            if (width == 16)
            {
                var hi = Vector128.LoadUnsafe(ref origin, (nuint)(i + 8));
                previous = AdvSimd.ExtractVector128(lo, hi, 7);
                extraBytes += PairExtraBytes(hi, previous);
            }
            var cost = width + AdvSimd.Arm64.AddAcross(extraBytes).ToScalar();
            var take = width;
            // The pair correction applies only inside the block. Defer a high surrogate
            // paired with the next block's first character, so no returned prefix cuts it.
            if (char.IsHighSurrogate(text[i + width - 1]) && i + width < text.Length && char.IsLowSurrogate(text[i + width]))
            {
                cost -= 3;
                take--;
            }
            if (cost > bytes)
                break;
            bytes -= cost;
            i += take;
        }
        return i + Utf8PrefixLengthScalar(text.Slice(i), bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> PairExtraBytes(Vector128<ushort> chars, Vector128<ushort> previous)
    {
        // Default every surrogate to the replacement character's three bytes. Each
        // low surrogate preceded by a high one subtracts two: 3 + 3 becomes 4. The
        // mandatory one byte per code unit is added after the horizontal sum.
        var pair = AdvSimd.CompareEqual(chars & Vector128.Create((ushort)0xFC00), Vector128.Create((ushort)0xDC00))
            & AdvSimd.CompareEqual(previous & Vector128.Create((ushort)0xFC00), Vector128.Create((ushort)0xD800));
        return (pair << 1)
            - AdvSimd.CompareGreaterThan(chars, Vector128.Create((ushort)0x7F))
            - AdvSimd.CompareGreaterThan(chars, Vector128.Create((ushort)0x7FF));
    }
#endif
}
