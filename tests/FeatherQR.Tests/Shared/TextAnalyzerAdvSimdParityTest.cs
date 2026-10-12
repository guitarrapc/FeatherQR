using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// Verifies that the ARM64 NEON text analysis (TextAnalyzer.AnalyzeAdvSimd)
/// returns the same TextAnalysisResult (EncodingMode, EciMode, DataLength) as
/// the scalar reference for every character class and block boundary. Analyze
/// dispatches by hardware capability, so these tests pin BOTH sides explicitly:
/// AnalyzeScalar is called directly, and the NEON entry point is called
/// directly (skipped on machines without AdvSimd.Arm64).
/// </summary>
public class TextAnalyzerAdvSimdParityTest
{
    // Representative payloads: one per encoding-mode/ECI equivalence class,
    // plus flag flips isolated to the first vector block vs the scalar tail.
    public static IEnumerable<string> RepresentativeTexts =>
    [
        "0",
        "0123456789",                                                   // Numeric, below/at vector grain
        "01234567890123456789012345678901234567890123456789",           // Numeric, multi-block
        "HELLO WORLD $%*+-./:",                                         // Alphanumeric incl. all specials
        "TICKET-2026/07 GATE A SEAT 42 PRICE $35.00 :*+",               // Alphanumeric, realistic
        "https://github.com/guitarrapc/FeatherQR?tab=readme#qr", // Byte + ASCII (lowercase)
        "café au lait été",                                             // Byte + ISO-8859-1
        "QRコード日本語テスト",                                          // Byte + UTF-8
        "0123456789012345X",                                            // Numeric flips only in scalar tail
        "X0123456789012345",                                            // Numeric flips only in first block
        "0123456é0123456789",                                           // non-ASCII inside first block
        "01234567890123456789é",                                        // non-ASCII only in scalar tail
    ];

    /// <summary>
    /// The precondition the Latin-1 segment writers rely on: auto-detection must never
    /// declare Default or ISO-8859-1 for text containing a character above U+00FF.
    /// </summary>
    /// <remarks>
    /// This is the invariant, tested where it is actually decided. `RmQRBinaryEncoder`
    /// routes Byte-mode text to `WriteLatin1` on `EciMode.Default or Iso8859_1`, and that
    /// writer truncates chars to a byte. On x64 its SSE2 tier SATURATES instead, so a
    /// mis-detection would produce a different payload per architecture rather than a
    /// uniformly wrong one. The explicit-ECI half of the precondition is enforced by
    /// `CharacterSets.IsValidISO88591` and covered by RmQRCodeGeneratorUnitTest. This is
    /// the auto-detect half.
    ///
    /// Position matters as much as the character: every vector tier tests a whole block
    /// at a time and finishes with an overlapping or scalar tail, so an offending char is
    /// placed at the first index, inside the first block, on each block boundary, and in
    /// the tail.
    /// </remarks>
    [Test]
    public async Task AutoDetect_NeverDeclaresLatin1_ForCharsAboveFF()
    {
        char[] offenders = ['\u0100', '\u07FF', 'あ', '\uFFFD'];
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

                    var analysis = TextAnalyzer.Analyze(text.AsSpan(), EciMode.Default);
                    await Assert.That(analysis.EciMode).IsEqualTo(EciMode.Utf8)
                        .Because($"U+{(int)offender:X4} at {position}/{length} is not representable in ISO-8859-1");

                    var scalar = TextAnalyzer.AnalyzeScalar(text.AsSpan(), EciMode.Default);
                    await Assert.That(scalar.EciMode).IsEqualTo(EciMode.Utf8)
                        .Because($"scalar tier: U+{(int)offender:X4} at {position}/{length}");
                }
            }
        }
    }

    /// <summary>
    /// The complement: text that IS representable must not be pushed to UTF-8, or the
    /// invariant above would hold trivially by never choosing Latin-1 at all.
    /// </summary>
    [Test]
    public async Task AutoDetect_StillDeclaresLatin1_AtTheBoundaryChar()
    {
        foreach (var length in new[] { 1, 8, 16, 17, 33, 64 })
        {
            var chars = new char[length];
            chars.AsSpan().Fill('A');
            chars[length - 1] = '\u00FF'; // the last char ISO-8859-1 can represent
            var analysis = TextAnalyzer.Analyze(new string(chars).AsSpan(), EciMode.Default);
            await Assert.That(analysis.EciMode).IsEqualTo(EciMode.Iso8859_1)
                .Because($"U+00FF at {length - 1}/{length} is representable in ISO-8859-1");
        }
    }

    // Boundary chars adjacent to every SIMD range check:
    // '/'(0x2F) and ':'(0x3A) are IN the alphanumeric set, ';' '@' '[' '`' '{' are OUT;
    // 0x7F/0x80 straddle ASCII, 0xFF/0x100 straddle ISO-8859-1.
    public static IEnumerable<char> BoundaryChars =>
    [
        '/', '0', '9', ':', ';', '@', 'A', 'Z', '[', '`', 'z', '{',
        ' ', '!', '#', '$', '%', '&', ')', '*', '+', ',', '-', '.',
        '\u007F', '\u0080', 'ÿ', 'Ā', 'あ',
    ];

    [Test]
    [MethodDataSource(nameof(RepresentativeTexts))]
    public async Task AnalyzeAdvSimd_MatchesScalar(string text)
    {
        if (!System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
        {
            Skip.Test("AdvSimd.Arm64 not supported on this machine");
            return;
        }

        foreach (var eciMode in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
        {
            var expected = TextAnalyzer.AnalyzeScalar(text, eciMode);
            var actual = TextAnalyzer.AnalyzeAdvSimd(text, eciMode);

            await Assert.That(actual).IsEqualTo(expected);
        }
    }

    [Test]
    [MethodDataSource(nameof(BoundaryChars))]
    public async Task AnalyzeAdvSimd_BoundaryChars_AllPositionsAndLengths_MatchScalar(char c)
    {
        if (!System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
        {
            Skip.Test("AdvSimd.Arm64 not supported on this machine");
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
                var actual = TextAnalyzer.AnalyzeAdvSimd(input, EciMode.Default);

                await Assert.That(actual).IsEqualTo(expected);
            }
        }
    }

    [Test]
    public async Task Analyze_Dispatch_MatchesScalar_OnThisMachine()
    {
        // Public dispatch parity: whatever tier Analyze picks on this machine
        // must agree with the scalar reference.
        foreach (var text in RepresentativeTexts)
        {
            var expected = TextAnalyzer.AnalyzeScalar(text, EciMode.Default);
            var actual = TextAnalyzer.Analyze(text, EciMode.Default);

            await Assert.That(actual).IsEqualTo(expected);
        }
    }
}
