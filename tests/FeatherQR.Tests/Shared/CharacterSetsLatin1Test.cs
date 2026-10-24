using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The check that refuses a forced ISO-8859-1 over text it cannot hold, against the runtime's range search. A wrong answer either refuses
/// Latin-1 text or lets a char above U+00FF reach the Latin-1 writer.
/// </summary>
public class CharacterSetsLatin1Test
{
    // Up to 70 chars with at most one above U+00FF put that char at every place of the 16-char blocks, the 4-char steps and the tail. The
    // chars beside it are all U+00FF and below, half of them U+00FD to U+00FF, next to the bound a wrong mask would move.
    [Test]
    public async Task IsValidISO88591_AnswersAsTheRuntimesSearch()
    {
        var random = new Random(256);
        string? mismatch = null;
        for (var round = 0; round < 20_000 && mismatch is null; round++)
        {
            var chars = new char[random.Next(71)];
            for (var i = 0; i < chars.Length; i++)
                chars[i] = (char)(random.Next(2) == 0 ? 0xFF - random.Next(3) : random.Next(0x100));
            if (chars.Length > 0 && random.Next(2) == 0)
                chars[random.Next(chars.Length)] = (char)(random.Next(3) switch { 0 => 0x100, 1 => 0xFFFF, _ => random.Next(0x100, 0x10000) });

            var expected = !chars.AsSpan().ContainsAnyExceptInRange((char)0, (char)0xFF);
            if (CharacterSets.IsValidISO88591(chars) != expected)
                mismatch = $"round {round}: {chars.Length} chars, the runtime answers {expected}";
        }

        await Assert.That(mismatch).IsNull();
    }

    /// <summary>
    /// A byte offset kept in an int wraps past 2^30 chars, so the read lands about 2 GiB before the text. That ends the process or gives a
    /// wrong answer, depending on what memory is there. It runs only when a filter names it, since its text takes 2 GiB.
    /// </summary>
    [Test]
    [Explicit]
    [NotInParallel]
    public async Task IsValidISO88591_PastTwoToTheThirtyChars_ReadsInsideTheSpan()
    {
        var chars = new char[(1 << 30) + 64];
        var latin1 = CharacterSets.IsValidISO88591(chars);
        chars[(1 << 30) + 10] = (char)0x100;
        var above = CharacterSets.IsValidISO88591(chars);

        await Assert.That(latin1).IsTrue();
        await Assert.That(above).IsFalse();
    }
}
