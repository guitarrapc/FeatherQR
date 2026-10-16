using FeatherQR.Gs1;

namespace FeatherQR.Tests;

/// <summary>
/// No <c>Try</c> form throws for any input text (qrcode-gs1-data.md, "The data layer"): mutations of
/// every corpus input (characters inserted, deleted and replaced, separators and non-ASCII among them) and
/// random strings go through <see cref="Gs1ElementString.TryValidate"/>, <see cref="Gs1ElementString.EnumerateElements"/>
/// the (AI) text and Digital Link conversions in both directions and <see cref="Gs1ElementString.TryRead"/>
/// without an exception, an accepted string always enumerates at least one element, an accepted string formats
/// and its text reads back, a URI that reads gives an element string that splits, and a built URI reads back.
/// The generator entry points get the same texts: their size forms throw nothing, their <c>Create</c> forms
/// throw the data layer's error in a <see cref="Gs1FormatException"/> or what <c>Create</c> throws for content
/// that does not fit and nothing else, each succeeds exactly where its size form says yes, and they take exactly
/// the data the data layer takes. No text here is longer than the entry points' stack buffers, no text that is
/// taken has a URI bound past them, and none that is taken is too long for a symbol. So nothing is written into
/// a pooled buffer here, and content that does not fit is not reached.
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

        ExerciseEntryPoints(text);
    }

    /// <summary>
    /// The generator entry points on one text, against the data layer's own verdicts: the element string of (AI)
    /// text or the text itself, and that element string's URI.
    /// </summary>
    private static void ExerciseEntryPoints(string text)
    {
        var isAiText = text.Length > 0 && text[0] == '(';
        string elementString;
        Gs1Error dataError;
        var taken = isAiText ? Gs1ElementString.TryFromHri(text, out elementString, out dataError) : Gs1ElementString.TryValidate(elementString = text, out dataError);
        var linkError = dataError;
        var linked = taken && Gs1ElementString.TryToDigitalLink(elementString, out _, out linkError);

        // Standard QR: content that does not fit version 40 is an InvalidOperationException.
        var sized = QRCodeGenerator.TryGetRequiredBufferSizeGs1(text, QREccLevel.M, out var size);
        var thrown = Run(() => QRCodeGenerator.CreateGs1(text, QREccLevel.M).Size, typeof(InvalidOperationException), out var written);
        Check("QRCodeGenerator.CreateGs1", text, taken, dataError, exactPlace: true, sized, size.Size, thrown, written);

        sized = QRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text, QREccLevel.M, out size);
        thrown = Run(() => QRCodeGenerator.CreateGs1DigitalLink(text, QREccLevel.M).Size, typeof(InvalidOperationException), out written);
        Check("QRCodeGenerator.CreateGs1DigitalLink", text, linked, linkError, exactPlace: !taken, sized, size.Size, thrown, written);

        // rMQR: content that does not fit any symbol is an ArgumentException.
        sized = RmQRCodeGenerator.TryGetRequiredBufferSizeGs1(text, RmQREccLevel.M, out var rmqrSize);
        thrown = Run(() => RmQRCodeGenerator.CreateGs1(text, RmQREccLevel.M).Width, typeof(ArgumentException), out written);
        Check("RmQRCodeGenerator.CreateGs1", text, taken, dataError, exactPlace: true, sized, rmqrSize.Width, thrown, written);

        sized = RmQRCodeGenerator.TryGetRequiredBufferSizeGs1DigitalLink(text, RmQREccLevel.M, out rmqrSize);
        thrown = Run(() => RmQRCodeGenerator.CreateGs1DigitalLink(text, RmQREccLevel.M).Width, typeof(ArgumentException), out written);
        Check("RmQRCodeGenerator.CreateGs1DigitalLink", text, linked, linkError, exactPlace: !taken, sized, rmqrSize.Width, thrown, written);
    }

    /// <summary>Runs a <c>Create</c> form. An exception other than the data's and the one named for content that does not fit goes through, and fails the test.</summary>
    private static Exception? Run(Func<int> create, Type doesNotFit, out int written)
    {
        written = 0;
        try
        {
            written = create();
            return null;
        }
        catch (Exception e) when (e is Gs1FormatException || e.GetType() == doesNotFit)
        {
            return e;
        }
    }

    private static void Check(string name, string text, bool taken, Gs1Error dataError, bool exactPlace, bool sized, int size, Exception? thrown, int written)
    {
        if (!taken)
        {
            // Data the data layer refuses: its error, at its place in the text where the data layer reports one for this text.
            if (thrown is not Gs1FormatException refused || refused.Error.Kind != dataError.Kind || (exactPlace && refused.Error != dataError))
                throw new InvalidOperationException($"{name}: refused data is not reported as {dataError}: \"{text}\" ({thrown?.GetType().Name ?? "no exception"} {(thrown as Gs1FormatException)?.Error})");
            if (refused.Error.Offset < 0 || refused.Error.Offset + refused.Error.Length > Math.Max(text.Length, 1))
                throw new InvalidOperationException($"{name}: an error outside its text: \"{text}\" ({refused.Error})");
            if (sized)
                throw new InvalidOperationException($"{name}: refused data was sized: \"{text}\"");
            return;
        }

        if (thrown is Gs1FormatException unexpected)
            throw new InvalidOperationException($"{name}: data the data layer takes was refused: \"{text}\" ({unexpected.Error})");
        if (sized != (thrown is null) || (sized && size != written))
            throw new InvalidOperationException($"{name}: the size form and the Create form disagree: \"{text}\" (sized {sized} as {size}, {thrown?.GetType().Name ?? "created"} as {written})");
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
