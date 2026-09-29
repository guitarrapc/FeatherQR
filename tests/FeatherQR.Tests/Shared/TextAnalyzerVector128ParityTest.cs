using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The portable 128-bit text analysis (TextAnalyzer.AnalyzeVector128, WebAssembly's tier) against the scalar reference,
/// entered directly so it runs on every machine with 128-bit vectors, not only where the dispatch picks it.
/// </summary>
/// <remarks>
/// The tier narrows a block to bytes only once no char in it is above U+00FF, so the chars that would break a narrowing
/// that drops the high byte are the ones whose low byte is a digit or in the alphanumeric set (U+0130 would read as '0').
/// Each is placed at every position of every length across the 8- and 16-char blocks, alone and among digits and
/// alphanumerics, so it lands in the main loop, the 8-char block, the overlapped last block and the scalar tail.
/// </remarks>
public class TextAnalyzerVector128ParityTest
{
    public static IEnumerable<string> RepresentativeTexts =>
    [
        "0123456789",
        "01234567890123456789012345678901234567890123456789",
        "HELLO WORLD $%*+-./:",
        "TICKET-2026/07 GATE A SEAT 42 PRICE $35.00 :*+",
        "https://github.com/guitarrapc/FeatherQR?tab=readme#qr",
        "café au lait été",
        "QRコード日本語テスト",
        "0123456789012345X",
        "X0123456789012345",
        "0123456é0123456789",
        "01234567890123456789é",
        "HELLO WORLD HELLOİWORLD HELLO",
    ];

    /// <summary>
    /// Chars next to every range the tier checks, and above U+00FF the ones whose low byte is a digit, an alphanumeric or
    /// 0xFF, with the Latin-1 and the 16-bit sign boundaries.
    /// </summary>
    public static IEnumerable<char> BoundaryChars =>
    [
        '/', '0', '9', ':', ';', '@', 'A', 'Z', '[', '`', 'z', '{',
        ' ', '!', '#', '$', '%', '&', ')', '*', '+', ',', '-', '.',
        '\u007F', '\u0080', 'ÿ',
        'Ā', 'Ġ', 'Ĥ', 'Ī', 'ĭ', 'İ', 'Ĺ', 'ĺ', 'Ł', 'Ś', 'ǿ',
        '翿', '耀', '耰', 'あ', '�', '￿',
    ];

    [Test]
    [MethodDataSource(nameof(RepresentativeTexts))]
    public async Task AnalyzeVector128_MatchesScalar(string text)
    {
        if (!System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            Skip.Test("Vector128 not accelerated on this machine");
            return;
        }

        foreach (var eciMode in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
        {
            if (text.Length < 8)
                continue;
            await Assert.That(TextAnalyzer.AnalyzeVector128(text, eciMode)).IsEqualTo(TextAnalyzer.AnalyzeScalar(text, eciMode)).Because($"{text}, {eciMode}");
        }
    }

    [Test]
    [MethodDataSource(nameof(BoundaryChars))]
    public async Task AnalyzeVector128_BoundaryChars_AllPositionsAndLengths_MatchScalar(char c)
    {
        if (!System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            Skip.Test("Vector128 not accelerated on this machine");
            return;
        }

        const string Fillers = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
        var mismatches = new List<string>();
        foreach (var filler in new[] { '7', 'Q', 'é' })
        {
            // Lengths 8 (the tier's floor) to 40 cross the 16-char steps, the 8-char block and the overlapped last block
            for (var length = 8; length <= 40; length++)
            {
                for (var position = 0; position < length; position++)
                {
                    var chars = new char[length];
                    chars.AsSpan().Fill(filler);
                    chars[position] = c;
                    Check(new string(chars));

                    // The same among varied alphanumerics, so each range check is exercised beside it
                    for (var k = 0; k < length; k++)
                        chars[k] = Fillers[(k * 7 + length) % Fillers.Length];
                    chars[position] = c;
                    Check(new string(chars));
                }
            }
        }

        await Assert.That(mismatches).IsEmpty().Because(string.Join("; ", mismatches.Take(8)));

        void Check(string input)
        {
            foreach (var eciMode in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
            {
                var expected = TextAnalyzer.AnalyzeScalar(input, eciMode);
                var actual = TextAnalyzer.AnalyzeVector128(input, eciMode);
                if (actual != expected)
                    mismatches.Add($"U+{(int)c:X4} in \"{input}\" ({eciMode}): {actual}, scalar {expected}");
            }
        }
    }
}
