using FeatherQR.Gs1;

namespace FeatherQR.Tests;

/// <summary>
/// No <c>Try</c> form throws for any input text (gs1-support-plan.md, "What has to stay true"): mutations of
/// every corpus input (characters inserted, deleted and replaced, separators and non-ASCII among them) and
/// random strings go through <see cref="Gs1ElementString.TryValidate"/>, <see cref="Gs1ElementString.EnumerateElements"/>
/// the (AI) text and Digital Link conversions in both directions and <see cref="Gs1ElementString.TryRead"/>
/// without an exception, an accepted string always enumerates at least one element, an accepted string formats
/// and its text reads back, a URI that reads gives an element string that splits, and a built URI reads back.
/// </summary>
public class Gs1FuzzTest
{
    private const int Seed = 20261012;
    private const int MutationsPerInput = 12;
    private const int RandomStrings = 5000;

    private static readonly char[] Alphabet = ['0', '1', '2', '9', 'A', 'z', '%', '(', ')', '\\', '\u001D', ' ', 'é', 'あ', '\0', '￿', '=', '/', '-'];

    [Test]
    public async Task TryValidate_NeverThrows_OnMutatedCorpus()
    {
        var random = new Random(Seed);
        var inputs = Gs1FixtureLoader.EnumerateCases().Select(c => c.Input).Distinct().ToArray();
        var checkedCount = 0;
        foreach (var input in inputs)
        {
            for (var i = 0; i < MutationsPerInput; i++)
            {
                var text = Mutate(input, random);
                Exercise(text);
                checkedCount++;
            }
        }
        await Assert.That(checkedCount).IsGreaterThan(10_000);
    }

    [Test]
    public async Task TryValidate_NeverThrows_OnRandomStrings()
    {
        var random = new Random(Seed + 1);
        for (var i = 0; i < RandomStrings; i++)
        {
            var length = random.Next(0, 60);
            var chars = new char[length];
            for (var j = 0; j < length; j++)
                chars[j] = random.Next(4) == 0 ? Alphabet[random.Next(Alphabet.Length)] : (char)('0' + random.Next(10));
            Exercise(new string(chars));
        }
        await Assert.That(RandomStrings).IsGreaterThan(0);
    }

    private static void Exercise(string text)
    {
        var valid = Gs1ElementString.TryValidate(text, out var error);
        if (valid && error.Kind != Gs1ErrorKind.None)
            throw new InvalidOperationException($"accepted with an error: \"{text}\"");
        if (!valid && error.Kind == Gs1ErrorKind.None)
            throw new InvalidOperationException($"refused without an error: \"{text}\"");
        var elements = 0;
        foreach (var element in Gs1ElementString.EnumerateElements(text))
        {
            if (element.Ai.Length is < 2 or > 4 || element.Offset < 0 || element.Offset + element.Ai.Length + element.Value.Length > text.Length)
                throw new InvalidOperationException($"an element outside its text: \"{text}\"");
            elements++;
        }
        if (valid && elements == 0)
            throw new InvalidOperationException($"accepted but no element: \"{text}\"");
        _ = error.ToString();

        // The (AI) text conversions never throw either, in both directions, and an accepted string formats.
        var formats = Gs1ElementString.TryToHri(text, out var hri, out var hriError);
        if (valid && !formats)
            throw new InvalidOperationException($"accepted but not formatted: \"{text}\" ({hriError})");
        if (formats && !Gs1ElementString.TryFromHri(hri, out var back, out _) && valid)
            throw new InvalidOperationException($"accepted, formatted, and the text does not read back: \"{text}\"");
        _ = Gs1ElementString.TryFromHri(text, out _, out _);
        Gs1ElementString.TryGetToHriLength(text, out _, out _);
        Gs1ElementString.TryGetFromHriLength(text, out _, out _);

        // The Digital Link conversions and the decode-side read never throw either, and a URI read gives an element string that splits.
        if (Gs1ElementString.TryFromDigitalLink(text, out var fromUri, out _))
        {
            var count = 0;
            foreach (var element in Gs1ElementString.EnumerateElements(fromUri))
                count++;
            if (count == 0 || !Gs1ElementString.TryToHri(fromUri, out _, out var fromUriError))
                throw new InvalidOperationException($"a URI read into an element string that does not split: \"{text}\" -> \"{fromUri}\"");
        }
        Gs1ElementString.TryGetFromDigitalLinkLength(text, out _, out _);
        var toUri = Gs1ElementString.TryToDigitalLink(text, out var uri, out _);
        if (toUri && !Gs1ElementString.TryFromDigitalLink(uri, out _, out var backError))
            throw new InvalidOperationException($"a built URI does not read: \"{text}\" -> \"{uri}\" ({backError})");
        Gs1ElementString.TryGetToDigitalLinkLength(text, out _, out _);
        Gs1ElementString.TryRead(text, Fnc1Mode.FirstPosition, out _, out _);
        Gs1ElementString.TryRead(text, default, out _, out _);
        Gs1ElementString.TryGetReadLength(text, default, out _, out _);
    }

    private static string Mutate(string input, Random random)
    {
        var chars = input.ToList();
        var edits = 1 + random.Next(3);
        for (var e = 0; e < edits; e++)
        {
            var at = random.Next(chars.Count + 1);
            switch (random.Next(3))
            {
                case 0:
                    chars.Insert(at, Alphabet[random.Next(Alphabet.Length)]);
                    break;
                case 1 when chars.Count > 0:
                    chars.RemoveAt(Math.Min(at, chars.Count - 1));
                    break;
                default:
                    if (chars.Count > 0)
                        chars[Math.Min(at, chars.Count - 1)] = Alphabet[random.Next(Alphabet.Length)];
                    break;
            }
        }
        return new string(chars.ToArray());
    }
}
