using System.Text;
using FeatherQR.Internals;

namespace FeatherQR.Tests;

public class ModeSegmenterCostRunParityTest
{
    private const int Unreachable = int.MaxValue / 4;
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    public static IEnumerable<(string Text, int Offset, int Length)> Cases()
    {
        foreach (var text in new[] { "", "x", "1234", "ABCDEF", "あいうえお", "éλ", "\uFEFF", "123456789\uFEFF", "123456789x\uFEFF", "\uFEFF\uFEFF1234", "1234\uFEFF\uFEFFx5678", "\uD800", "\uDC00", "\uD800\uD800\uDC00\uDC00", "1234📷🎉\uFEFFABCDEF" })
            yield return (text, 0, text.Length);
        foreach (var length in new[] { 1, 2, 7, 8, 9, 15, 16, 17, 31, 32, 33, 255, 256, 257, 4097 })
        {
            foreach (var run in new[] { new string('x', length), new string('あ', length), new string('\uFEFF', length) })
            {
                yield return (run, 0, run.Length);
                var text = "1234567890" + run + "ABCDEFGHIJKL1234567890";
                yield return (text, 0, text.Length);
            }
        }
        // The surrounding storage must not pair a surrogate outside the borrowed span.
        const string sliced = "\uD800\uDC00あx12345\uD800\uDC00";
        yield return (sliced, 1, sliced.Length - 1);
        yield return (sliced, 0, sliced.Length - 1);
        yield return (sliced, 1, sliced.Length - 2);
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task CostsGeneral_ByteRunsMatchAllStates(string text, int offset, int length)
    {
        Check(text.AsSpan(offset, length));
        await Assert.That(true).IsTrue();
    }

    [Test]
    [Arguments(7)]
    [Arguments(42)]
    [Arguments(20260920)]
    public async Task CostsGeneral_RandomRunsMatchAllStates(int seed)
    {
        var random = new Random(seed);
        const string samples = "0AZ xéλあ\uFEFF\uD800\uDC00";
        var buffer = new char[1025];
        foreach (var length in new[] { 0, 1, 7, 8, 9, 127, 128, 129, 1025 })
        {
            for (var round = 0; round < 20; round++)
            {
                for (var i = 0; i < length; i++)
                    buffer[i] = round % 2 == 0 ? samples[random.Next(samples.Length)] : (char)random.Next(65536);
                Check(buffer.AsSpan(0, length));
            }
        }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task CostsGeneral_AllShortClassSequencesMatchAllStates()
    {
        var buffer = new char[6];
        var combinations = 1;
        for (var length = 0; length <= buffer.Length; length++)
        {
            for (var code = 0; code < combinations; code++)
            {
                var rest = code;
                for (var i = 0; i < length; i++) { buffer[i] = "0Ax\uFEFF"[rest % 4]; rest /= 4; }
                Check(buffer.AsSpan(0, length));
            }
            combinations *= 4;
        }
        await Assert.That(true).IsTrue();
    }

    private static void Check(ReadOnlySpan<char> text)
    {
        foreach (var (mode, numeric, alnum, bytes) in new[] { (4, 10, 9, 8), (4, 12, 11, 16), (4, 14, 13, 16), (0, 3, 0, 0), (1, 4, 3, 0), (2, 5, 4, 4), (3, 6, 5, 5) })
        foreach (var (allowAlnum, allowByte) in new[] { (true, true), (false, true), (true, false), (false, false) })
        foreach (var charset in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
        {
            var expected = Reference(text, charset, mode, numeric, alnum, bytes, allowAlnum, allowByte, out var expectedState);
            // General is selected for UTF-8 or when Byte is disabled. Latin-with-Byte has its own loop.
            if (charset == EciMode.Utf8 || !allowByte)
            {
                var actual = ModeSegmenter.CostsGeneral(text, charset, mode + numeric + 4, mode + alnum + 6, mode + bytes, allowAlnum, allowByte, out var state);
                Equal(actual, state, expected, expectedState);
            }
            var cost = ModeSegmenter.ComputeCosts(text, charset, mode, numeric, alnum, bytes, default, out var finalState, allowAlnum, allowByte);
            Equal(cost, finalState, expected, expectedState);
        }
    }

    private static void Equal(int cost, int state, int expected, int expectedState)
    {
        if (cost != expected || state != expectedState)
            throw new InvalidOperationException($"Expected ({expected}, {expectedState}), got ({cost}, {state}).");
    }

    // Independent reference: relax every transition from every reachable state.
    // UTF-8 byte counts come from the encoding implementation, not the production width helper.
    private static int Reference(ReadOnlySpan<char> text, EciMode charset, int mode, int numeric, int alnum, int bytes, bool allowAlnum, bool allowByte, out int finalState)
    {
        Span<int> previous = stackalloc int[7];
        Span<int> current = stackalloc int[7];
        previous.Fill(Unreachable);
        previous[6] = 0;
        for (var i = 0; i < text.Length; i++)
        {
            current.Fill(Unreachable);
            var c = text[i];
            var byteCount = 1;
            if (charset == EciMode.Utf8)
            {
                byteCount = i > 0 && char.IsSurrogatePair(text[i - 1], c) ? 0
                    : Encoding.UTF8.GetByteCount(text.Slice(i, i + 1 < text.Length && char.IsSurrogatePair(c, text[i + 1]) ? 2 : 1));
            }
            for (var from = 0; from < 7; from++)
            {
                var basis = previous[from];
                if (basis >= Unreachable) continue;
                if (c is >= '0' and <= '9')
                {
                    var target = from < 3 ? (from + 1) % 3 : 1;
                    var added = from < 3 ? (from == 0 ? 4 : 3) : mode + numeric + 4;
                    current[target] = Math.Min(current[target], basis + added);
                }
                if (allowAlnum && Alphabet.IndexOf(c) >= 0)
                {
                    var target = from == 4 ? 3 : 4;
                    var added = from is 3 or 4 ? (from == 3 ? 6 : 5) : mode + alnum + 6;
                    current[target] = Math.Min(current[target], basis + added);
                }
                if (allowByte && !(charset == EciMode.Utf8 && c == '\uFEFF' && i > 0 && from != 5))
                    current[5] = Math.Min(current[5], basis + byteCount * 8 + (from == 5 ? 0 : mode + bytes));
            }
            current.CopyTo(previous);
        }
        var best = Unreachable;
        finalState = 5;
        for (var state = 0; state < 6; state++)
            if (previous[state] < best) { best = previous[state]; finalState = state; }
        return best;
    }
}
