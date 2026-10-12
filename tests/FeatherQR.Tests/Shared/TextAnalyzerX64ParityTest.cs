using FeatherQR.Internals;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR.Tests;

/// <summary>
/// Verifies that the x64 text analysis tiers (TextAnalyzer.AnalyzeAvx2, 16 chars per
/// step, and TextAnalyzer.AnalyzeSse2, 8 chars per step) return the same
/// TextAnalysisResult as the scalar reference. The ARM64 counterpart lives in
/// TextAnalyzerAdvSimdParityTest. Both call the tier entry points directly rather than
/// through Analyze, because Analyze picks exactly one tier per machine and the SSE2 tier
/// is unreachable on any CPU that also has AVX2.
/// </summary>
/// <remarks>
/// The regression these pin: both tiers compared UTF-16 code units as Int16, so every
/// char at or above U+8000 (surrogate halves, U+FFFD, CJK compatibility) read as
/// negative and passed the "&gt; 127" and "&gt; 255" range tests. Auto-detection then
/// declared Latin-1 for text ISO-8859-1 cannot represent, and the Byte-mode Latin-1
/// writer truncated (or, on its SSE2 tier, saturated) the char.
/// <para>
/// The offending code units are written as \u escapes on purpose: what is under test is
/// the code unit value at a position, not the glyph.
/// </para>
/// </remarks>
public class TextAnalyzerX64ParityTest
{
    public static IEnumerable<string> RepresentativeTexts =>
    [
        "0",
        "0123456789",                                                   // Numeric, below/at vector grain
        "01234567890123456789012345678901234567890123456789",           // Numeric, multi-block
        "HELLO WORLD $%*+-./:",                                         // Alphanumeric incl. all specials
        "TICKET-2026/07 GATE A SEAT 42 PRICE $35.00 :*+",               // Alphanumeric, realistic
        "https://github.com/guitarrapc/FeatherQR?tab=readme#qr", // Byte + ASCII (lowercase)
        "café au lait été",                              // Byte + ISO-8859-1
        "QRコード日本語",                       // Byte + UTF-8
        "PHOTO 📷 GALLERY 2026 OPEN",                         // Byte + surrogate pair, past the block grain
        "0123456789012345\uFFFD",                                       // above U+7FFF, scalar tail only
        "\uFFFD0123456789012345",                                       // above U+7FFF, first block only
    ];

    // Boundary chars adjacent to every SIMD range check, plus the code units the signed
    // comparison used to swallow: U+8000 is the first negative Int16, U+D83D a surrogate
    // half, U+FFFD the top of the code unit range.
    public static IEnumerable<char> BoundaryChars =>
    [
        '/', '0', '9', ':', ';', '@', 'A', 'Z', '[', '`', 'z', '{',
        ' ', '!', '#', '$', '%', '&', ')', '*', '+', ',', '-', '.',
        '\u007F', '\u0080', 'ÿ', '\u0100', 'あ',
        '\u7FFF', '\u8000', '\uD83D', '\uFFFD',
    ];

    [Test]
    [MethodDataSource(nameof(RepresentativeTexts))]
    public async Task AnalyzeAvx2_MatchesScalar(string text)
    {
        if (!Avx2.IsSupported)
        {
            Skip.Test("AVX2 not supported on this machine");
            return;
        }

        foreach (var eciMode in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
        {
            var expected = TextAnalyzer.AnalyzeScalar(text, eciMode);
            var actual = TextAnalyzer.AnalyzeAvx2(text, eciMode);

            await Assert.That(actual).IsEqualTo(expected);
        }
    }

    [Test]
    [MethodDataSource(nameof(RepresentativeTexts))]
    public async Task AnalyzeSse2_MatchesScalar(string text)
    {
        if (!Sse2.IsSupported)
        {
            Skip.Test("SSE2 not supported on this machine");
            return;
        }

        foreach (var eciMode in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
        {
            var expected = TextAnalyzer.AnalyzeScalar(text, eciMode);
            var actual = TextAnalyzer.AnalyzeSse2(text, eciMode);

            await Assert.That(actual).IsEqualTo(expected);
        }
    }

    [Test]
    [MethodDataSource(nameof(BoundaryChars))]
    public async Task AnalyzeX64_BoundaryChars_AllPositionsAndLengths_MatchScalar(char c)
    {
        if (!Sse2.IsSupported)
        {
            Skip.Test("SSE2 not supported on this machine");
            return;
        }

        // Lengths crossing the 8/16-char vector grain. The char alone, repeated,
        // leading a digit run, and trailing a digit run (tail-only flag flips).
        foreach (var length in new[] { 1, 7, 8, 9, 15, 16, 17, 24 })
        {
            var digits = "012345678901234567890123".AsSpan(0, length);
            string[] inputs =
            [
                new string(c, length),
                string.Concat(c.ToString(), digits),
                string.Concat(digits, c.ToString()),
            ];

            foreach (var input in inputs)
            {
                var expected = TextAnalyzer.AnalyzeScalar(input, EciMode.Default);

                await Assert.That(TextAnalyzer.AnalyzeSse2(input, EciMode.Default)).IsEqualTo(expected);

                if (Avx2.IsSupported)
                {
                    await Assert.That(TextAnalyzer.AnalyzeAvx2(input, EciMode.Default)).IsEqualTo(expected);
                }
            }
        }
    }

    /// <summary>
    /// The Latin-1 precondition from TextAnalyzerAdvSimdParityTest, aimed at the two x64
    /// tiers directly: on an AVX2 machine the dispatcher never reaches the SSE2 tier, so
    /// its half of the bug would survive a green run of the dispatch-level test.
    /// </summary>
    [Test]
    public async Task AnalyzeX64_NeverDeclaresLatin1_ForCharsAboveFF()
    {
        if (!Sse2.IsSupported)
        {
            Skip.Test("SSE2 not supported on this machine");
            return;
        }

        char[] offenders = ['\u0100', '\u07FF', 'あ', '\u7FFF', '\u8000', '\uD83D', '\uFFFD'];
        int[] lengths = [1, 2, 7, 8, 9, 15, 16, 17, 31, 32, 33, 47, 64, 65];

        foreach (var offender in offenders)
        {
            foreach (var length in lengths)
            {
                for (var position = 0; position < length; position++)
                {
                    // A Latin-1 filler, so the ONLY reason to pick UTF-8 is the offender.
                    var chars = new char[length];
                    chars.AsSpan().Fill('é');
                    chars[position] = offender;
                    var text = new string(chars);

                    await Assert.That(TextAnalyzer.AnalyzeSse2(text, EciMode.Default).EciMode).IsEqualTo(EciMode.Utf8)
                        .Because($"SSE2 tier: U+{(int)offender:X4} at {position}/{length} is not representable in ISO-8859-1");

                    if (Avx2.IsSupported)
                    {
                        await Assert.That(TextAnalyzer.AnalyzeAvx2(text, EciMode.Default).EciMode).IsEqualTo(EciMode.Utf8)
                            .Because($"AVX2 tier: U+{(int)offender:X4} at {position}/{length} is not representable in ISO-8859-1");
                    }
                }
            }
        }
    }

    /// <summary>
    /// The complement: text that IS representable must not be pushed to UTF-8, or the
    /// invariant above would hold trivially by never choosing Latin-1 at all.
    /// </summary>
    [Test]
    public async Task AnalyzeX64_StillDeclaresLatin1_AtTheBoundaryChar()
    {
        if (!Sse2.IsSupported)
        {
            Skip.Test("SSE2 not supported on this machine");
            return;
        }

        foreach (var length in new[] { 1, 8, 16, 17, 33, 64 })
        {
            var chars = new char[length];
            chars.AsSpan().Fill('A');
            chars[length - 1] = 'ÿ'; // the last char ISO-8859-1 can represent
            var text = new string(chars);

            await Assert.That(TextAnalyzer.AnalyzeSse2(text, EciMode.Default).EciMode).IsEqualTo(EciMode.Iso8859_1)
                .Because($"SSE2 tier: U+00FF at {length - 1}/{length} is representable in ISO-8859-1");

            if (Avx2.IsSupported)
            {
                await Assert.That(TextAnalyzer.AnalyzeAvx2(text, EciMode.Default).EciMode).IsEqualTo(EciMode.Iso8859_1)
                    .Because($"AVX2 tier: U+00FF at {length - 1}/{length} is representable in ISO-8859-1");
            }
        }
    }
}
