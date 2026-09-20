using FeatherQR.Internals;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

public class StructuredAppendPlanHelpTest
{
    [Test]
    [Arguments("", false, true)]
    [Arguments("ab12cd", false, true)]
    [Arguments("ab123cd", false, false)]
    [Arguments("ab1234cd", true, false)]
    [Arguments("xABCDEx", false, true)]
    [Arguments("xABCDEFx", true, false)]
    [Arguments("123xx12", false, false)]
    [Arguments("123xx1234", true, false)]
    [Arguments("123xxABCDEF", true, false)]
    [Arguments("A12BC", false, true)]
    [Arguments("12AB34", true, false)]
    [Arguments("123\uFEFF4", false, false)]
    [Arguments("ABC\uFEFFDEF", false, true)]
    [Arguments("１２３４５６", false, true)]
    [Arguments("123\uD800\uDC004", false, false)]
    [Arguments("ABC\uD800DEF\uDC00", false, true)]
    public async Task ByteContent_PreservesPayingAndTyingThresholds(string text, bool expectedHelp, bool expectedOneRun)
    {
        var help = StructuredAppendPlanner.CanPlanHelp(text, EncodingMode.Byte, out var oneRun);
        await Assert.That(help).IsEqualTo(expectedHelp);
        await Assert.That(oneRun).IsEqualTo(expectedOneRun);
    }

    [Test]
    public async Task EveryClassSequenceThroughNineCharacters_MatchesFullScan()
    {
        var text = new char[9];
        var combinations = 1;
        for (var length = 0; length <= text.Length; length++)
        {
            for (var value = 0; value < combinations; value++)
            {
                var digits = value;
                for (var i = 0; i < length; i++)
                {
                    text[i] = "0Ax"[digits % 3];
                    digits /= 3;
                }
                await Check(new string(text, 0, length));
            }
            combinations *= 3;
        }
    }

    [Test]
    public async Task EveryCodeUnit_AtThresholds_MatchesFullScan()
    {
        foreach (var (prefix, suffix) in new[] { ("12", "3x"), ("ABCD", "Ax"), ("123x", "yz") })
            for (var value = 0; value <= char.MaxValue; value++)
                await Check(prefix + (char)value + suffix);
    }

    [Test]
    public async Task LateRunsAndSpanBoundaries_MatchFullScan()
    {
        foreach (var length in new[] { 0, 1, 7, 8, 9, 15, 16, 17, 127, 128, 129, 65_537 })
        {
            var gap = new string('x', length);
            foreach (var dense in new[] { "12", "123", "1234", "ABCDE", "ABCDEF" })
            {
                await Check(dense + gap);
                await Check(gap + dense);
                await Check("123x" + gap + dense);
                var surrounded = "123ABC" + gap + dense + "DEF456";
                await Check(surrounded, 6, gap.Length + dense.Length);
            }
        }
        foreach (var seed in new[] { 7, 42, 20260920 })
        {
            var random = new Random(seed);
            const string alphabet = "09AZ $%*+-./:az\0\u007F\u0080日\uFEFF\uD800\uDC00";
            var text = new char[513];
            for (var i = 0; i < text.Length; i++)
                text[i] = alphabet[random.Next(alphabet.Length)];
            await Check(new string(text));
        }
    }

    private static async Task Check(string text, int start = 0, int length = -1)
    {
        if (length < 0)
            length = text.Length - start;
        // Independent alphabet and full scan: no production classifier or thresholds.
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
        int digits = 0, alnum = 0, longestDigits = 0, longestAlnum = 0;
        for (var i = start; i < start + length; i++)
        {
            var index = alphabet.IndexOf(text[i]);
            digits = index >= 0 && index < 10 ? digits + 1 : 0;
            alnum = index >= 0 ? alnum + 1 : 0;
            longestDigits = Math.Max(longestDigits, digits);
            longestAlnum = Math.Max(longestAlnum, alnum);
        }
        foreach (var mode in new[] { EncodingMode.Numeric, EncodingMode.Alphanumeric, EncodingMode.Byte })
        {
            var expectedHelp = mode != EncodingMode.Numeric && (longestDigits >= 4 || longestAlnum >= 6);
            var expectedOneRun = mode == EncodingMode.Byte && longestDigits < 3 && longestAlnum < 6;
            var help = StructuredAppendPlanner.CanPlanHelp(text.AsSpan(start, length), mode, out var oneRun);
            await Assert.That(help).IsEqualTo(expectedHelp);
            await Assert.That(oneRun).IsEqualTo(expectedOneRun);
        }
    }
}
