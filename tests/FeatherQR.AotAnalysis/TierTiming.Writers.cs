using FeatherQR.Internals;
using FeatherQR.Internals.BinaryEncoders;
using FeatherQR.Internals.StandardQR;

/// <summary>
/// The Standard QR Alphanumeric and Numeric payload writers (<c>--shape kernel/AlnumWriter</c>, <c>kernel/NumericWriter</c>): each tier
/// entered directly beside the writer they replaced (<c>-old</c>), on runs of the lengths callers write, 13 bits into a word as after a
/// mode and count indicator.
/// </summary>
internal static partial class TierTiming
{
    internal delegate void PayloadWriter(ref BitWriter writer, ReadOnlySpan<char> chars);

    private static Shape[] WriterShapes() =>
    [
        .. new[] { 4296, 300, 40, 16 }.SelectMany(n => WriterTiers("AlnumWriter", n, Pick(n, AlphanumericAlphabet), OldAlphanumeric,
            QRBinaryEncoder.WriteAlphanumericScalar, QRBinaryEncoder.WriteAlphanumericSsse3, AlphanumericSsse3Runs, QRBinaryEncoder.WriteAlphanumericPackedSimd)),
        .. new[] { 7089, 500, 40, 12 }.SelectMany(n => WriterTiers("NumericWriter", n, Pick(n, "0123456789"), OldNumeric,
            QRBinaryEncoder.WriteNumericScalar, QRBinaryEncoder.WriteNumericSsse3, NumericSsse3Runs, null)),
    ];

    // each as its dispatch asks: the Alphanumeric step blends with SSE4.1, the Numeric step needs SSSE3 alone
    private static bool AlphanumericSsse3Runs => System.Runtime.Intrinsics.X86.Ssse3.IsSupported && System.Runtime.Intrinsics.X86.Sse41.IsSupported;
    private static bool NumericSsse3Runs => System.Runtime.Intrinsics.X86.Ssse3.IsSupported;

    private static Shape[] WriterTiers(string name, int length, string text, PayloadWriter old, PayloadWriter scalar, PayloadWriter ssse3, bool ssse3Runs, PayloadWriter? wasm) =>
    [
        new($"kernel/{name}-{length}-old", () => TimedWriter(text, old)),
        new($"kernel/{name}-{length}-scalar", () => TimedWriter(text, scalar)),
        new($"kernel/{name}-{length}-ssse3", () => TimedWriter(text, ssse3), ssse3Runs),
        new($"kernel/{name}-{length}-wasm", () => TimedWriter(text, wasm!), wasm is not null && System.Runtime.Intrinsics.Wasm.PackedSimd.IsSupported),
    ];

    private static Func<int> TimedWriter(string text, PayloadWriter write)
    {
        var buffer = new byte[(text.Length * 6 + 64) / 8 + 16];
        return () =>
        {
            var writer = new BitWriter(buffer);
            writer.Write(0x1555, 13);
            write(ref writer, text);
            return writer.BitPosition;
        };
    }

    /// <summary>The Alphanumeric writer the tiers replaced (QRBinaryEncoder.WriteAlphanumericData, 2026-10-05), verbatim: the reference.</summary>
    internal static void OldAlphanumeric(ref BitWriter writer, ReadOnlySpan<char> chars)
    {
        var length = chars.Length;
        var i = 0;
        while (i + 1 < length)
        {
            var value = CharacterSets.GetAlphanumericValue(chars[i]) * 45
                + CharacterSets.GetAlphanumericValue(chars[i + 1]);
            writer.Write(value, 11);
            i += 2;
        }
        if (i < length)
        {
            var value = CharacterSets.GetAlphanumericValue(chars[i]);
            writer.Write(value, 6);
        }
    }

    /// <summary>The Numeric writer the tiers replaced (QRBinaryEncoder.WriteNumericData, 2026-10-05), verbatim: the reference.</summary>
    internal static void OldNumeric(ref BitWriter writer, ReadOnlySpan<char> digits)
    {
        var i = 0;
        var length = digits.Length;
        while (i + 2 < length)
        {
            var value = (digits[i] - '0') * 100 + (digits[i + 1] - '0') * 10 + (digits[i + 2] - '0');
            writer.Write(value, 10);
            i += 3;
        }
        if (i + 1 < length)
        {
            var value = (digits[i] - '0') * 10 + (digits[i + 1] - '0');
            writer.Write(value, 7);
            i += 2;
        }
        if (i < length)
        {
            var value = digits[i] - '0';
            writer.Write(value, 4);
        }
    }
}
