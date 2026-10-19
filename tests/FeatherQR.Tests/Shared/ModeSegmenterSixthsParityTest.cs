using System.Runtime.Intrinsics;
using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The trivial lower bound's kernels against an independent pricing of each character: the dispatch, the scalar loop and each vector tier
/// the machine runs, over every alphabet mix the callers can pass, every length across the block and tail boundaries, and the three charsets.
/// </summary>
/// <remarks>
/// The reference prices a character by the rules alone: a digit at the Numeric rate, the rest of the alphanumeric alphabet at the Alphanumeric
/// rate, anything else a byte at a time, where a byte is one under a one-byte charset and under UTF-8 the character's encoded length, a surrogate
/// pair four bytes between its two halves and a lone half three. The vector tiers price 32 or 8 characters a step, the AVX2 tier 16 in its last blocks, read the last block
/// overlapped with a mask, and send a block holding a surrogate to the scalar loop, so the pairs placed across those boundaries below are
/// where a wrong tier would differ.
/// </remarks>
public class ModeSegmenterSixthsParityTest
{
    private const int SixthsPerDigit = 20;
    private const int SixthsPerAlnum = 33;
    private const int SixthsPerByte = 48;
    private const string Alphanumeric = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    private delegate int Kernel(ReadOnlySpan<char> text, EciMode charset);

    /// <summary>Every kernel the machine runs, each called at every length: a vector tier routes a text under its block to the scalar loop itself.</summary>
    private static IEnumerable<(string Name, Kernel Run)> Kernels()
    {
        yield return ("dispatch", ModeSegmenter.CheapestSixths);
        yield return ("scalar", ModeSegmenter.CheapestSixthsScalar);
#if NET8_0_OR_GREATER
        if (Vector128.IsHardwareAccelerated)
            yield return ("Vector128", ModeSegmenter.CheapestSixthsVector128);
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
            yield return ("Avx2", ModeSegmenter.CheapestSixthsAvx2);
#endif
    }

    public static IEnumerable<string> Alphabets() =>
    [
        "0123456789",
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
        " $%*+-./:",
        "abcdefghijklmnopqrstuvwxyz",
        "!\"#&'(),;<=>?@[\\]^_`{|}~\u0000\u001D\u007F",
        "0123456789ABCDEFGHIJ $%*+-./:abcxyz!?",
        "éèêàçñüß ÿ",
        "Āǿ߿",
        "日本語漢字テストࠀ￿",
        "😀🎉👍",
        "😀𐀀􏿿",
        "𐀀a\uDC00\uD800z",
        "a日1😀Z\uD800é:7",
    ];

    [Test]
    [MethodDataSource(nameof(Alphabets))]
    public async Task EveryKernel_MatchesTheReference_AtEveryLengthAndCharset(string alphabet)
    {
        var random = new Random(alphabet.Length * 7919 + 20261018);
        // 7,089 is the most digits one symbol holds, past where the horizontal sum of 16-bit lanes would wrap. The last three lengths are
        // Structured Append inputs, the longest past what a set holds and priced whole before it is refused, all past where a lane itself would wrap.
        foreach (var length in Enumerable.Range(0, 71).Concat([100, 120, 180, 300, 361, 1000, 7_089, 40_000, 100_000, 700_000]))
        {
            for (var seed = 0; seed < (length > 1000 ? 1 : 3); seed++)
            {
                var chars = new char[length];
                for (var i = 0; i < length; i++)
                    chars[i] = alphabet[random.Next(alphabet.Length)];
                await CheckAll(new string(chars));
            }
        }
    }

    /// <summary>A surrogate pair laid across each block boundary and each masked tail's cut, with the tail holding 0 to 17 further characters.</summary>
    [Test]
    public async Task EveryKernel_MatchesTheReference_WithPairsAcrossTheBlockBoundaries()
    {
        foreach (var cut in new[] { 7, 8, 15, 16, 23, 24, 31, 32, 47, 48, 63, 64 })
        {
            foreach (var tail in new[] { 0, 1, 2, 7, 8, 9, 15, 16, 17 })
            {
                await CheckAll(new string('a', cut - 1) + "😀" + new string('b', tail));
                await CheckAll(new string('7', cut) + "😀" + new string('Z', tail));
                await CheckAll(new string('a', cut - 1) + "\uDC00" + new string('b', tail));
                await CheckAll(new string('a', cut - 1) + "\uD800" + new string('b', tail));
                await CheckAll(new string('é', cut - 1) + "😀" + new string('日', tail));
            }
        }
    }

    private static async Task CheckAll(string text)
    {
        foreach (var charset in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
        {
            var expected = Reference(text, charset);
            foreach (var (name, run) in Kernels())
                await Assert.That(run(text, charset)).IsEqualTo(expected).Because($"{name} on {charset} text of {text.Length} chars 「{Show(text)}」");
        }
    }

    private static int Reference(string text, EciMode charset)
    {
        var sixths = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is >= '0' and <= '9')
                sixths += SixthsPerDigit;
            else if (Alphanumeric.Contains(c))
                sixths += SixthsPerAlnum;
            else
                sixths += SixthsPerByte * Utf8Bytes(text, i, charset);
        }
        return sixths;
    }

    private static int Utf8Bytes(string text, int i, EciMode charset)
    {
        if (charset != EciMode.Utf8)
            return 1;
        var c = text[i];
        if (c < 0x80)
            return 1;
        if (c < 0x800)
            return 2;
        if (char.IsHighSurrogate(c))
            return i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 4 : 3;
        if (char.IsLowSurrogate(c))
            return i > 0 && char.IsHighSurrogate(text[i - 1]) ? 0 : 3;
        return 3;
    }

    private static string Show(string text)
        => string.Concat(text.Take(40).Select(c => c < 0x20 || c >= 0x7F ? $"\\u{(int)c:X4}" : c.ToString())) + (text.Length > 40 ? "…" : "");
}
