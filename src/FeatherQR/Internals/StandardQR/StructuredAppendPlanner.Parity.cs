using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
#endif

namespace FeatherQR.Internals.StandardQR;

internal static partial class StructuredAppendPlanner
{
    /// <summary>
    /// XOR of the message's encoded bytes, including the UTF-8 byte order mark when one is written.
    /// </summary>
    public static byte Parity(ReadOnlySpan<char> text, EciMode charset, bool utf8Bom)
    {
#if NET8_0_OR_GREATER
        if (AdvSimd.Arm64.IsSupported && text.Length >= (charset == EciMode.Utf8 ? 9 : 4))
            return ParityAdvSimd(text, charset, utf8Bom);
#endif
        return ParityScalar(text, charset, utf8Bom);
    }

    /// <summary>The portable parity kernel; UTF-8 is folded per code point without a temporary byte buffer.</summary>
    internal static byte ParityScalar(ReadOnlySpan<char> text, EciMode charset, bool utf8Bom)
    {
        var parity = utf8Bom ? 0xEF ^ 0xBB ^ 0xBF : 0;
        var i = 0;
        if (charset != EciMode.Utf8)
        {
            // Independent accumulators shorten the XOR dependency chain.
            int p1 = 0, p2 = 0, p3 = 0;
            for (; i <= text.Length - 4; i += 4)
            {
                parity ^= Latin1ParityValue(text[i]);
                p1 ^= Latin1ParityValue(text[i + 1]);
                p2 ^= Latin1ParityValue(text[i + 2]);
                p3 ^= Latin1ParityValue(text[i + 3]);
            }
            for (; i < text.Length; i++)
                parity ^= Latin1ParityValue(text[i]);
            return (byte)(parity ^ p1 ^ p2 ^ p3);
        }

        for (; i < text.Length; i++)
            parity ^= Utf8ParityValue(text, ref i);
        return (byte)parity;
    }

    // Match the Byte writer: modern targets replace each unrepresentable code unit,
    // while the downlevel writer narrows it. Only the low byte contributes to the result.
    private static int Latin1ParityValue(char c)
#if NET5_0_OR_GREATER
        => c <= 0xFF ? c : '?';
#else
        => c;
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Utf8ParityValue(ReadOnlySpan<char> text, ref int i)
    {
        int c = text[i];
        if (c < 0x80)
            return c;
        if (c < 0x800)
            return 0x40 ^ (c >> 6) ^ (c & 0x3F);
        if ((uint)(c - 0xD800) < 0x800)
        {
            if (c < 0xDC00 && i + 1 < text.Length && (uint)(text[i + 1] - 0xDC00) < 0x400)
            {
                c = ((c - 0xD800) << 10) + text[++i] - 0xDC00 + 0x10000;
                return 0x70 ^ (c >> 18) ^ ((c >> 12) & 0x3F) ^ ((c >> 6) & 0x3F) ^ (c & 0x3F);
            }
            c = 0xFFFD;
        }
        return 0xE0 ^ (c >> 12) ^ ((c >> 6) & 0x3F) ^ (c & 0x3F);
    }

#if NET8_0_OR_GREATER
    /// <summary>NEON parity kernel. The caller checks <see cref="AdvSimd.Arm64.IsSupported"/>.</summary>
    internal static byte ParityAdvSimd(ReadOnlySpan<char> text, EciMode charset, bool utf8Bom)
        => charset == EciMode.Utf8 ? Utf8ParityAdvSimd(text, utf8Bom) : Latin1ParityAdvSimd(text, utf8Bom);

    private static byte Latin1ParityAdvSimd(ReadOnlySpan<char> text, bool utf8Bom)
    {
        var vectors = MemoryMarshal.Cast<char, Vector128<ushort>>(text);
        var p0 = Vector128<ushort>.Zero;
        var p1 = p0;
        var p2 = p0;
        var p3 = p0;
        var v = 0;
        var parity = utf8Bom ? 0xEF ^ 0xBB ^ 0xBF : 0;
        for (; v <= vectors.Length - 4; v += 4)
        {
            var c0 = vectors[v];
            var c1 = vectors[v + 1];
            var c2 = vectors[v + 2];
            var c3 = vectors[v + 3];
            p0 ^= Vector128.ConditionalSelect(Vector128.GreaterThan(c0, Vector128.Create((ushort)0xFF)), Vector128.Create((ushort)'?'), c0);
            p1 ^= Vector128.ConditionalSelect(Vector128.GreaterThan(c1, Vector128.Create((ushort)0xFF)), Vector128.Create((ushort)'?'), c1);
            p2 ^= Vector128.ConditionalSelect(Vector128.GreaterThan(c2, Vector128.Create((ushort)0xFF)), Vector128.Create((ushort)'?'), c2);
            p3 ^= Vector128.ConditionalSelect(Vector128.GreaterThan(c3, Vector128.Create((ushort)0xFF)), Vector128.Create((ushort)'?'), c3);
        }
        if (v <= vectors.Length - 2)
        {
            var c0 = vectors[v];
            var c1 = vectors[v + 1];
            p0 ^= Vector128.ConditionalSelect(Vector128.GreaterThan(c0, Vector128.Create((ushort)0xFF)), Vector128.Create((ushort)'?'), c0);
            p1 ^= Vector128.ConditionalSelect(Vector128.GreaterThan(c1, Vector128.Create((ushort)0xFF)), Vector128.Create((ushort)'?'), c1);
            v += 2;
        }
        if (v < vectors.Length)
        {
            var c = vectors[v++];
            p0 ^= Vector128.ConditionalSelect(Vector128.GreaterThan(c, Vector128.Create((ushort)0xFF)), Vector128.Create((ushort)'?'), c);
        }
        var combined = p0 ^ p1 ^ p2 ^ p3;
        var folded = combined.GetLower() ^ combined.GetUpper();
        var i = v * 8;
        // A half-width block also covers short sets and avoids a four-character scalar tail.
        if (i <= text.Length - 4)
        {
            var c = MemoryMarshal.Cast<char, Vector64<ushort>>(text.Slice(i))[0];
            folded ^= Vector64.ConditionalSelect(Vector64.GreaterThan(c, Vector64.Create((ushort)0xFF)), Vector64.Create((ushort)'?'), c);
            i += 4;
        }
        for (; i < text.Length; i++)
            parity ^= Latin1ParityValue(text[i]);
        var bits = folded.AsUInt64().ToScalar();
        bits ^= bits >> 32;
        bits ^= bits >> 16;
        return (byte)(parity ^ (int)bits);
    }

    private static byte Utf8ParityAdvSimd(ReadOnlySpan<char> text, bool utf8Bom)
    {
        var vectors = MemoryMarshal.Cast<char, Vector128<ushort>>(text);
        // The shifted span supplies the next code unit, even across a vector boundary.
        // Its length also leaves the last 1..8 characters for the scalar tail.
        var nextVectors = MemoryMarshal.Cast<char, Vector128<ushort>>(text.IsEmpty ? text : text.Slice(1));
        var raw = Vector128<ushort>.Zero;
        var nonAscii = raw;
        var threeByte = raw;
        var pairHigh = raw;
        var pairLow = raw;
        var v = 0;
        var parity = utf8Bom ? 0xEF ^ 0xBB ^ 0xBF : 0;
        for (; v < nextVectors.Length; v++)
        {
            var c = vectors[v];
            var maximum = AdvSimd.Arm64.MaxAcross(c).ToScalar();
            if (maximum < 0x80)
            {
                raw ^= c;
                continue;
            }
            if (maximum >= 0xD800)
            {
                var surrogate = Vector128.Equals(c & Vector128.Create((ushort)0xF800), Vector128.Create((ushort)0xD800));
                if (!Vector128.EqualsAll(surrogate, Vector128<ushort>.Zero))
                {
                    var next = nextVectors[v];
                    var pair = Vector128.Equals(c & Vector128.Create((ushort)0xFC00), Vector128.Create((ushort)0xD800))
                        & Vector128.Equals(next & Vector128.Create((ushort)0xFC00), Vector128.Create((ushort)0xDC00));
                    // Every surrogate contributes U+FFFD first. The two replacements of a
                    // valid pair cancel, so only that pair's real byte XOR must be added.
                    // q = (high - D800) + 64, lo = low - DC00: code point = (q << 10) | lo.
                    // Bit 15 of q is free and records the number of pairs modulo two.
                    var q = c - Vector128.Create((ushort)0xD7C0);
                    pairHigh ^= (q | Vector128.Create((ushort)0x8000)) & pair;
                    pairLow ^= (next & Vector128.Create((ushort)0x03FF)) & pair;
                    c = Vector128.ConditionalSelect(surrogate, Vector128.Create((ushort)0xFFFD), c);
                }
            }
            raw ^= c;
            // UTF-8 payload shifts/masks are linear under XOR; apply them once after the
            // loop. These groups need bits 6..11 and 12..15 respectively, leaving bit 0
            // free to record their count parity for the encoded-byte prefix constants.
            var tagged = c | Vector128.Create((ushort)1);
            nonAscii ^= tagged & Vector128.GreaterThan(c, Vector128.Create((ushort)0x7F));
            threeByte ^= tagged & Vector128.GreaterThan(c, Vector128.Create((ushort)0x07FF));
        }

        // A low surrogate paired with the last vector's high half is deliberately read
        // as U+FFFD here: it cancels that high half's replacement, whose true pair parity
        // was already accumulated. All pairs wholly inside the tail are priced normally.
        for (var i = v * 8; i < text.Length; i++)
            parity ^= Utf8ParityValue(text, ref i);

        int r = XorLanes(raw), d = XorLanes(nonAscii), e = XorLanes(threeByte);
        int high = XorLanes(pairHigh), low = XorLanes(pairLow);
        var pairs = ((high >> 15) * 0x70) ^ ((high >> 8) & 7) ^ ((high >> 2) & 0x3F)
            ^ ((high & 3) << 4) ^ (low >> 6) ^ (low & 0x3F);
        return (byte)(parity ^ pairs ^ r ^ (d & 0xC0) ^ ((d >> 6) & 0x3F)
            ^ ((d & 1) << 6) ^ (e >> 12) ^ ((e & 1) * 0xA0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int XorLanes(Vector128<ushort> value)
    {
        var folded = value.AsUInt64().GetElement(0) ^ value.AsUInt64().GetElement(1);
        folded ^= folded >> 32;
        folded ^= folded >> 16;
        return (ushort)folded;
    }
#endif
}
