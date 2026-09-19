using System.Text;
using FeatherQR.Internals.StandardQR;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics.Arm;
#endif

namespace FeatherQR.Tests;

public class StructuredAppendScanParityTest
{
    [Test]
    public void ModeBoundaries_AllCharactersAndVectorEdges_MatchReference()
    {
        // Every UTF-16 value must classify correctly, including values whose low byte
        // is a digit or a member of the QR alphabet after narrowing.
        for (var value = 0; value <= char.MaxValue; value++)
        {
            CheckBoundaries(new string((char)value, 33));
            CheckBoundaries(new string('A', 17) + (char)value + new string('A', 17));
            CheckBoundaries(new string('0', 17) + (char)value + new string('0', 17));
        }
        foreach (var length in new[] { 0, 1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 127, 7089 })
        {
            foreach (var prefix in new[] { '0', 'A', ':', '日' })
                foreach (var stop in new[] { '\0', '/', '@', '[', 'a', '\u0130', '\uD800', '\uDC00', '\uFFFF' })
                    CheckBoundaries(new string(prefix, length) + stop + "0ABC");
        }
    }

    [Test]
    public void Utf8Prefix_BudgetsAndSurrogateBoundaries_MatchEncoding()
    {
        foreach (var length in new[] { 0, 1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 127 })
            foreach (var ch in new[] { '\0', 'a', '\u007F', '\u0080', '\u07FF', '\u0800', '日', '\uFFFF', '\uD800', '\uDC00' })
                CheckUtf8(new string(ch, length));
        foreach (var special in new[] { "\uD800\uDC00", "\uDBFF\uDFFF", "\uD800", "\uDC00", "\uD800\uD800\uDC00\uDC00", "\uFEFF" })
            for (var offset = 0; offset <= 33; offset++)
                CheckUtf8(new string('a', offset) + special + new string('日', 35));
        foreach (var seed in new[] { 0, 7, 42 })
        {
            var random = new Random(seed);
            for (var length = 0; length < 80; length++)
            {
                var text = new char[length];
                for (var i = 0; i < length; i++)
                    text[i] = (char)random.Next(0x10000);
                CheckUtf8(new string(text));
                CheckBoundaries(new string(text));
            }
        }
        CheckUtf8(new string('日', 7089), allBudgets: false);
        CheckUtf8(new string('a', 7089), allBudgets: false);
    }

    private static void CheckBoundaries(string text)
    {
        var digits = 0;
        while (digits < text.Length && text[digits] is >= '0' and <= '9')
            digits++;
        var alnum = digits;
        while (alnum < text.Length && "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:".Contains(text[alnum]))
            alnum++;
        StructuredAppendScanner.ModeBoundaries(text, out var d, out var a);
        if (d != digits || a != alnum)
            throw new InvalidOperationException($"Dispatch: length {text.Length}, expected {digits}/{alnum}, got {d}/{a}");
        StructuredAppendScanner.ModeBoundariesScalar(text, out d, out a);
        if (d != digits || a != alnum)
            throw new InvalidOperationException($"Scalar: length {text.Length}, expected {digits}/{alnum}, got {d}/{a}");
#if NET8_0_OR_GREATER
        if (AdvSimd.Arm64.IsSupported)
        {
            StructuredAppendScanner.ModeBoundariesAdvSimd(text, out d, out a);
            if (d != digits || a != alnum)
                throw new InvalidOperationException($"NEON: length {text.Length}, expected {digits}/{alnum}, got {d}/{a}");
        }
#endif
    }

    private static void CheckUtf8(string text, bool allBudgets = true)
    {
        var totalBytes = Encoding.UTF8.GetByteCount(text);
        if (allBudgets)
            for (var budget = -1; budget <= totalBytes + 1; budget++)
                CheckBudget(text, budget);
        foreach (var budget in new[] { int.MinValue, -1, 0, 1, 7, 8, 15, 16, 23, 24, 31, 32, 47, 48, 49, 2300, totalBytes, int.MaxValue })
            CheckBudget(text, budget);
    }

    private static void CheckBudget(string text, int budget)
    {
        var expected = 0;
        var remaining = budget;
        while (expected < text.Length)
        {
            var length = char.IsHighSurrogate(text[expected]) && expected + 1 < text.Length && char.IsLowSurrogate(text[expected + 1]) ? 2 : 1;
            var bytes = Encoding.UTF8.GetByteCount(text.AsSpan(expected, length));
            if (bytes > remaining)
                break;
            remaining -= bytes;
            expected += length;
        }
        var actual = StructuredAppendScanner.Utf8PrefixLength(text, budget);
        if (actual != expected || StructuredAppendScanner.Utf8PrefixLengthScalar(text, budget) != expected)
            throw new InvalidOperationException($"UTF-8 length {text.Length}, budget {budget}, expected {expected}, got {actual}");
#if NET8_0_OR_GREATER
        if (AdvSimd.Arm64.IsSupported && StructuredAppendScanner.Utf8PrefixLengthAdvSimd(text, budget) != expected)
            throw new InvalidOperationException($"NEON UTF-8 length {text.Length}, budget {budget}, expected {expected}");
#endif
    }
}
