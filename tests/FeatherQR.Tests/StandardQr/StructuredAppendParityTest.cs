using System.Runtime.Intrinsics.Arm;
using System.Text;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

public class StructuredAppendParityTest
{
    public static IEnumerable<int> Lengths() => [0, 1, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 129, 1023, 15001, 45001];

    [Test]
    [MethodDataSource(nameof(Lengths))]
    public async Task Parity_AllKernels_MatchEncodedBytes(int length)
    {
        foreach (var seed in new[] { 0, 17, 91 })
        {
            var random = new Random(seed);
            var chars = new char[length];
            for (var i = 0; i < chars.Length; i++)
                chars[i] = (char)random.Next(65536);
            await Check(new string(chars));
        }
        foreach (var c in new[] { '\0', 'A', '\u007F', '\u0080', '\u00FF', '\u0100', '\u07FF', '\u0800', 'あ', '\uD800', '\uDC00', '\uFEFF', '\uFFFF' })
            await Check(new string(c, length));
    }

    [Test]
    public async Task Parity_SurrogatesAcrossBlocks_MatchEncodedBytes()
    {
        for (var position = 0; position < 66; position++)
            foreach (var pair in new[] { "\uD800\uDC00", "\uDBFF\uDFFF", "\uD800A", "\uDC00\uD800", "\uD800\uD800\uDC00", "\uD800\uDC00\uDC00" })
            {
                var prefix = new string('x', position);
                await Check(prefix + pair);
                await Check(prefix + pair + new string('é', 65));
            }
    }

    [Test]
    public async Task Parity_AllCodeUnits_MatchEncodedBytes()
    {
        var chars = new char[65536];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = (char)i;
        await Check(new string(chars));
    }

    [Test]
    public async Task Parity_Slices_RespectTheirOwnBoundaries()
    {
        var text = "abcde\uD800\uDC00 日本語 é \uD800\uDC00" + new string('x', 80) + "\uD800\uDC00";
        for (var offset = 0; offset < 16; offset++)
            foreach (var length in new[] { 0, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65 })
                await Check(text, offset, length);
        // Pair halves outside the slice must not affect its replacement behavior.
        await Check("\uD800\uDC00", 0, 1);
        await Check("\uD800\uDC00", 1, 1);
    }

    [Test]
    public async Task Parity_PairPayloadBitBoundaries_MatchEncodedBytes()
    {
        // Carry boundaries in the high payload and six-bit groups in the low payload.
        for (var high = 0xD800; high <= 0xDBFF; high++)
            foreach (var low in new[] { 0xDC00, 0xDC3F, 0xDC40, 0xDFFF })
                foreach (var position in new[] { 7, 15 })
                    await Check(new string('x', position) + (char)high + (char)low + "日本語 é");
    }

    private static async Task Check(string text, int offset = 0, int length = -1)
    {
        if (length < 0)
            length = text.Length - offset;
        var payload = text.Substring(offset, length);
        await CheckSlice(text, offset, length, payload);
    }

    private static async Task CheckSlice(string text, int offset, int length, string payload)
    {
        foreach (var charset in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
        {
            // Independent reference: encode the bytes, then XOR them. For forced Latin-1,
            // the writer replaces each unrepresentable UTF-16 code unit with '?'.
            var bytes = charset == EciMode.Utf8 ? Encoding.UTF8.GetBytes(payload) : Latin1Bytes(payload);
            byte expected = 0;
            foreach (var b in bytes)
                expected ^= b;
            foreach (var bom in new[] { false, true })
            {
                var value = (byte)(expected ^ (bom ? 0xEF ^ 0xBB ^ 0xBF : 0));
                await Assert.That(StructuredAppendPlanner.Parity(text.AsSpan(offset, length), charset, bom)).IsEqualTo(value);
                await Assert.That(StructuredAppendPlanner.ParityScalar(text.AsSpan(offset, length), charset, bom)).IsEqualTo(value);
                if (AdvSimd.Arm64.IsSupported)
                    await Assert.That(StructuredAppendPlanner.ParityAdvSimd(text.AsSpan(offset, length), charset, bom)).IsEqualTo(value);
            }
        }
    }

    private static byte[] Latin1Bytes(string text)
    {
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
            bytes[i] = text[i] <= 255 ? (byte)text[i] : (byte)'?';
        return bytes;
    }
}
