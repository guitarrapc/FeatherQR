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

    // Match the Byte writer: modern targets replace each unrepresentable code unit, while the downlevel writer narrows it. Only the low byte contributes to the result.
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
}
