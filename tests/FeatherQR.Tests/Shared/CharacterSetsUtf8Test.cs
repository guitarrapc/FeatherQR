using System.Text;
using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The library's UTF-8 count and transcodes against <see cref="Encoding.UTF8"/> on text full of surrogates, paired and not. The count reuses
/// a fallback buffer of the thread's on .NET 8 and later and netstandard2.1, and the transcode replaces lone surrogates itself, so each
/// has to agree with <see cref="Encoding.UTF8"/> on what a lone surrogate becomes. The netstandard builds' own loops are held by <see cref="NetStandardBuildParityTest"/>.
/// </summary>
public class CharacterSetsUtf8Test
{
    // Both halves of a surrogate beside chars of each UTF-8 width, so that a random draw makes pairs, lone halves, reversed pairs and lone
    // halves at either end.
    private static readonly char[] Alphabet = ['a', 'é', 'あ', '\uD800', '\uDBFF', '\uDC00', '\uDFFF', '\uD83D', '\uDE00'];

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
    /// A count kept in an encoder that did not flush would pair a text's trailing high surrogate with the next text's low surrogate. Each
    /// half is a lone surrogate of its own text, three bytes of U+FFFD beside the one byte of its letter.
    /// </summary>
    [Test]
    public async Task Count_CarriesNothingFromOneTextToTheNext()
    {
        var counts = new[] { "a\uD83D", "\uDE00b", "a\uD83D", "a", "\uDE00" }.Select(text => CharacterSets.GetUtf8ByteCount(text));

        await Assert.That(string.Join(",", counts)).IsEqualTo("4,4,4,1,3");
    }

    /// <summary>
    /// A call that throws can leave the thread's fallback buffer in the middle of a replacement: here a destination too short for a
    /// replacement after the first. The next call on the thread gets that buffer, so it must start clean and write U+FFFD for each lone
    /// surrogate, as the runtime's UTF-8 does, and '?' for each char above U+00FF, as the library's Latin-1 does.
    /// </summary>
    [Test]
    public async Task FallbackBuffer_AfterACallThatThrew_StartsClean()
    {
        var high = (char)0xD800;
        var kana = (char)0x3042;
        var utf8Threw = Throws(() => ThreadBufferFallback.Utf8.GetBytes(new string(high, 3).AsSpan(), new byte[4]));
        var utf8 = new byte[16];
        var utf8Text = "a" + high + "b";
        var utf8Written = ThreadBufferFallback.Utf8.GetBytes(utf8Text.AsSpan(), utf8);

        var latin1Threw = Throws(() => ThreadBufferFallback.Latin1.GetBytes(new string(kana, 3).AsSpan(), new byte[2]));
        var latin1 = new byte[16];
        var latin1Written = ThreadBufferFallback.Latin1.GetBytes(("a" + kana + "b").AsSpan(), latin1);

        await Assert.That(utf8Threw && latin1Threw).IsTrue().Because("each short destination throws inside a replacement");
        await Assert.That(Convert.ToHexString(utf8, 0, utf8Written)).IsEqualTo(Convert.ToHexString(Encoding.UTF8.GetBytes(utf8Text)));
        await Assert.That(Convert.ToHexString(latin1, 0, latin1Written)).IsEqualTo("613F62");
    }

    private static bool Throws(Action call)
    {
        try
        {
            call();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
