using System.Text;
using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The library's UTF-8 count and transcodes against <see cref="Encoding.UTF8"/> on text full of surrogates, paired and not. The count keeps
/// an encoder for the thread on .NET 8 and later, and the transcode replaces lone surrogates itself, so each has to agree with
/// <see cref="Encoding.UTF8"/> on what a lone surrogate becomes. The netstandard builds' own loops are held by <see cref="NetStandardBuildParityTest"/>.
/// </summary>
public class CharacterSetsUtf8Test
{
    // Both halves of a surrogate beside chars of each UTF-8 width, so that a random draw makes pairs, lone halves, reversed pairs and lone
    // halves at either end.
    private static readonly char[] Alphabet = ['a', 'é', 'あ', '\uD800', '\uDBFF', '\uDC00', '\uDFFF', '\uD83D', '\uDE00'];

    // From empty to past the callers' stack budgets for one run.
    public static IEnumerable<int> Lengths() => [0, 1, 2, 3, 7, 40, 53, 54, 64, 85, 86, 255, 256, 257, 1000];

    [Test]
    [MethodDataSource(nameof(Lengths))]
    public async Task CountAndTranscodes_AreEncodingUtf8s(int length)
    {
        var random = new Random(length);
        string? mismatch = null;
        for (var round = 0; round < 200 && mismatch is null; round++)
        {
            var chars = new char[length];
            for (var i = 0; i < length; i++)
                chars[i] = Alphabet[random.Next(Alphabet.Length)];
            var text = new string(chars);
            var expected = Encoding.UTF8.GetBytes(text);

            // A destination of exactly the UTF-8's length: the callers size theirs by the count.
            var written = new byte[expected.Length];
            var looped = new byte[expected.Length];
            if (CharacterSets.GetUtf8ByteCount(text) != expected.Length)
                mismatch = $"round {round}: counts {CharacterSets.GetUtf8ByteCount(text)} bytes";
            else if (CharacterSets.GetUtf8Bytes(text, written) != expected.Length || !written.AsSpan().SequenceEqual(expected))
                mismatch = $"round {round}: GetUtf8Bytes writes other bytes";
            else if (CharacterSets.EncodeUtf8(text, looped) != expected.Length || !looped.AsSpan().SequenceEqual(expected))
                mismatch = $"round {round}: EncodeUtf8 writes other bytes";
        }

        await Assert.That(mismatch).IsNull().Because($"length {length}: the count and both transcodes give what Encoding.UTF8 gives");
    }

    /// <summary>
    /// The count's encoder outlives a call, so a text that ends in a high surrogate must not pair with the next text's low surrogate. Each
    /// half is a lone surrogate of its own text, three bytes of U+FFFD beside the one byte of its letter.
    /// </summary>
    [Test]
    public async Task Count_CarriesNothingFromOneTextToTheNext()
    {
        var counts = new[] { "a\uD83D", "\uDE00b", "a\uD83D", "a", "\uDE00" }.Select(text => CharacterSets.GetUtf8ByteCount(text));

        await Assert.That(string.Join(",", counts)).IsEqualTo("4,4,4,1,3");
    }
}
