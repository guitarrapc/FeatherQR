using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

public class StructuredAppendByteCostParityTest
{
    [Test]
    public async Task EveryCodeUnit_MatchesRuneEncoding()
    {
        var text = new char[3];
        foreach (var neighbours in new[] { ('x', 'y'), ('\uD800', '\uDC00'), ('\uDBFF', '\uDFFF'), ('\uDC00', '\uD800') })
        {
            text[0] = neighbours.Item1;
            text[2] = neighbours.Item2;
            for (var value = 0; value <= char.MaxValue; value++)
            {
                text[1] = (char)value;
                await Check(new string(text));
            }
        }
    }

    [Test]
    public async Task SlicesAndLongOffsets_KeepPairsInsideTheSuppliedSpan()
    {
        var random = new Random(20260919);
        var alphabet = new[] { '\0', '9', 'A', 'x', '\u007F', '\u0080', '\u07FF', '\u0800', '\uD7FF', '\uD800', '\uDBFF', '\uDC00', '\uDFFF', '\uE000', '\uFEFF', '\uFFFF' };
        foreach (var length in new[] { 1, 2, 3, 7, 8, 9, 15, 16, 17, 127, 128, 129, 65_537 })
        {
            var text = new char[length + 2];
            for (var i = 0; i < text.Length; i++)
                text[i] = alphabet[random.Next(alphabet.Length)];
            text[0] = '\uD800';
            text[1] = '\uDC00';
            text[^2] = '\uD800';
            text[^1] = '\uDC00';
            var storage = new string(text);
            await Check(storage);
            await Check(storage, 1, length);
        }
    }

    private static async Task Check(string storage, int start = 0, int length = -1)
    {
        if (length < 0)
            length = storage.Length - start;
        var text = storage.Substring(start, length);
        var expected = new int[text.Length];
        var position = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            expected[position] = rune.Utf8SequenceLength;
            position += rune.Utf16SequenceLength;
        }
        foreach (var charset in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
        {
            for (var i = 0; i < text.Length; i++)
            {
                var cost = charset == EciMode.Utf8 ? expected[i] : 1;
                await Assert.That(StructuredAppendPlanner.LaneByteCost(storage.AsSpan(start, length), i, charset)).IsEqualTo(cost);
            }
        }
    }
}
