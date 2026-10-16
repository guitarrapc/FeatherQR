using System.Text.RegularExpressions;
using FeatherQR.Gs1;
using FeatherQR.Gs1.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The (AI) text conversions against the GS1 corpus: <see cref="Gs1ElementString.TryFromHri"/> builds the element
/// string the Syntax Engine built from every bracketed input it accepted (which pins the separator placement) and
/// refuses every input it refused, <see cref="Gs1ElementString.TryToHri"/> formats every accepted element string as
/// the engine's HRI and every symbol's element string as zxing-cpp's <c>TextMode.HRI</c> text where that reader's
/// table knows the AIs, the two round-trip, and the span forms and their length twins agree with the string forms.
/// </summary>
public class Gs1HriTest
{
    private const int Year = 2026;

    private static readonly Regex EngineHriLine = new(@"^\((\d{2,4})\) ", RegexOptions.Compiled);

    public static IEnumerable<Func<(string Id, string Input, bool Valid, string? ElementString)>> BracketedCases()
    {
        foreach (var c in Gs1FixtureLoader.EnumerateCases())
        {
            if (c.InputKind != Gs1InputKind.Bracketed)
                continue;
            var captured = (c.Id, c.Input, c.SyntaxEngine.Valid, c.SyntaxEngine.ElementString);
            yield return () => captured;
        }
    }

    public static IEnumerable<Func<(string Id, string ElementString, string[] Hri)>> AcceptedCases()
    {
        foreach (var c in Gs1FixtureLoader.EnumerateCases())
        {
            if (!c.SyntaxEngine.Valid || c.SyntaxEngine.ElementString is null || c.SyntaxEngine.Hri is null || c.SyntaxEngine.Hri.Length == 0)
                continue;
            if (c.InputKind is not (Gs1InputKind.ElementString or Gs1InputKind.Bracketed))
                continue;
            var captured = (c.Id, c.SyntaxEngine.ElementString, c.SyntaxEngine.Hri);
            yield return () => captured;
        }
    }

    public static IEnumerable<Func<(string Id, string Writer, string ElementString, string Hri)>> ReaderCases()
    {
        foreach (var c in Gs1FixtureLoader.EnumerateCases())
        {
            if (c.Reader is null)
                continue;
            var captured = (c.Id, c.Reader.Writer, c.Reader.ElementString, c.Reader.Hri);
            yield return () => captured;
        }
    }

    [Test]
    [MethodDataSource(nameof(BracketedCases))]
    public async Task TryFromHri_BuildsTheSyntaxEngineElementString((string Id, string Input, bool Valid, string? ElementString) c)
    {
        var built = Gs1Hri.TryFromHri(c.Input, Year, out var elementString, out var error);
        await Assert.That(built).IsEqualTo(c.Valid).Because($"{c.Id}: {(built ? "built" : error.ToString())}");
        if (c.Valid)
        {
            await Assert.That(elementString).IsEqualTo(c.ElementString).Because(c.Id);
            await Assert.That(error).IsEqualTo(default(Gs1Error));
        }
        else
        {
            await Assert.That(error.Kind).IsNotEqualTo(Gs1ErrorKind.None);
            await Assert.That(error.Offset + error.Length).IsLessThanOrEqualTo(Math.Max(c.Input.Length, 1)).Because($"{c.Id}: {error}");
        }
    }

    [Test]
    [MethodDataSource(nameof(AcceptedCases))]
    public async Task TryToHri_FormatsAsTheSyntaxEngineHri((string Id, string ElementString, string[] Hri) c)
    {
        var expected = string.Concat(c.Hri.Select(line => EngineHriLine.Replace(line, "($1)")));
        await Assert.That(Gs1ElementString.TryToHri(c.ElementString, out var hri, out var error)).IsTrue().Because($"{c.Id}: {error}");
        await Assert.That(Unescape(hri)).IsEqualTo(expected).Because(c.Id);
    }

    /// <summary>zxing-cpp's (AI) text of a symbol's element string, where its hand-maintained table could split it (it falls back to escaped text otherwise).</summary>
    [Test]
    [MethodDataSource(nameof(ReaderCases))]
    public async Task TryToHri_MatchesZxingCppHri((string Id, string Writer, string ElementString, string Hri) c)
    {
        // zxing-cpp falls back to the raw text, and keeps a raw separator in its (AI) text, where its own table could not split.
        if (!c.Hri.StartsWith("(", StringComparison.Ordinal) || c.Hri.Contains("<GS>", StringComparison.Ordinal) || c.Hri.Contains(Gs1TestText.Gs))
            return;
        var ours = Gs1ElementString.TryToHri(c.ElementString, out var hri, out var error);
        await Assert.That(ours).IsTrue().Because($"{c.Id}: {error}");
        await Assert.That(Unescape(hri)).IsEqualTo(c.Hri).Because($"{c.Id} ({c.Writer})");
    }

    /// <summary>libzint's separator placement equals this library's, except where libzint treats AI 235 as predefined length (the fixture spec).</summary>
    [Test]
    [MethodDataSource(nameof(BracketedCases))]
    public async Task TryFromHri_PlacesSeparatorsAsLibzintDoes((string Id, string Input, bool Valid, string? ElementString) c)
    {
        var fixture = Gs1FixtureLoader.EnumerateCases().First(f => f.Id == c.Id);
        if (!c.Valid || fixture.Reader is not { Writer: "zint-libzint" } || c.Input.Contains("(235)", StringComparison.Ordinal))
            return;
        await Assert.That(Gs1Hri.TryFromHri(c.Input, Year, out var elementString, out _)).IsTrue();
        await Assert.That(elementString).IsEqualTo(fixture.Reader.ElementString).Because(c.Id);
    }

    [Test]
    [MethodDataSource(nameof(AcceptedCases))]
    public async Task RoundTrip_ElementStringThroughHri((string Id, string ElementString, string[] Hri) c)
    {
        await Assert.That(Gs1ElementString.TryToHri(c.ElementString, out var hri, out _)).IsTrue();
        await Assert.That(Gs1Hri.TryFromHri(hri, Year, out var back, out var error)).IsTrue().Because($"{c.Id}: {error}");
        // A trailing or redundant separator is dropped on the way, so compare the strings with those removed.
        await Assert.That(back).IsEqualTo(Canonical(c.ElementString)).Because(c.Id);
    }

    [Test]
    [MethodDataSource(nameof(BracketedCases))]
    public async Task RoundTrip_HriThroughElementString((string Id, string Input, bool Valid, string? ElementString) c)
    {
        if (!c.Valid)
            return;
        await Assert.That(Gs1Hri.TryFromHri(c.Input, Year, out var elementString, out _)).IsTrue();
        await Assert.That(Gs1ElementString.TryToHri(elementString, out var hri, out _)).IsTrue();
        await Assert.That(hri).IsEqualTo(c.Input).Because(c.Id);
    }

    [Test]
    [Arguments("(01)09520123456788(10)ABC(17)251231", "0109520123456788" + "10ABC\u001D" + "17251231")]
    [Arguments("(01)09520123456788(17)251231(10)ABC", "0109520123456788" + "17251231" + "10ABC")]
    [Arguments("(10)ABC(01)09520123456788", "10ABC\u001D" + "0109520123456788")]
    [Arguments("(01)09520123456788(8005)000365(10)ABC", "0109520123456788" + "8005000365\u001D" + "10ABC")]
    [Arguments("(01)09520123456788(10)A\\(1)B", "0109520123456788" + "10A(1)B")]
    [Arguments("(01)09520123456788(10)A)B", "0109520123456788" + "10A)B")]
    public async Task TryFromHri_EquivalenceClasses(string hri, string elementString)
    {
        await Assert.That(Gs1Hri.TryFromHri(hri, Year, out var built, out var error)).IsTrue().Because(error.ToString());
        await Assert.That(built).IsEqualTo(elementString);
    }

    [Test]
    [Arguments("", Gs1ErrorKind.InvalidText, 0, 0)]
    [Arguments("(01", Gs1ErrorKind.InvalidText, 0, 3)]
    [Arguments("01)09520123456788", Gs1ErrorKind.InvalidText, 0, 1)]
    [Arguments("(0A)09520123456788", Gs1ErrorKind.InvalidText, 1, 2)]
    [Arguments("()09520123456788", Gs1ErrorKind.InvalidText, 1, 0)]
    [Arguments("(12345)1", Gs1ErrorKind.InvalidText, 1, 5)]
    [Arguments(" (01)09520123456788", Gs1ErrorKind.InvalidText, 0, 1)]
    [Arguments("(89)ABC", Gs1ErrorKind.UnknownAi, 1, 2)]
    [Arguments("(01)09520123456788(" + Gs1UnassignedAi.Ai7050 + ")ABC", Gs1ErrorKind.UnknownAi, 19, 4)]
    [Arguments("(01)09520123456788(10)", Gs1ErrorKind.EmptyValue, 22, 0)]
    [Arguments("(01)0952012345678", Gs1ErrorKind.ValueTooShort, 4, 13)]
    [Arguments("(01) 09520123456788", Gs1ErrorKind.InvalidCharacter, 4, 1)]
    [Arguments("(01)09520123456789", Gs1ErrorKind.InvalidCheckDigit, 17, 1)]
    [Arguments("(01)09520123456788(17)251331", Gs1ErrorKind.InvalidDate, 24, 2)]
    [Arguments("(10)ABC", Gs1ErrorKind.MissingRequiredAi, 0, 7)]
    [Arguments("(01)09520123456788(21)A(235)B", Gs1ErrorKind.ExcludedAiPair, 23, 6)]
    [Arguments("(01)09520123456788(10)A\\(1)B(10)A\\(2)B", Gs1ErrorKind.RepeatedAi, 28, 10)]
    [Arguments("(01)09520123456788(10)A\u001DB", Gs1ErrorKind.InvalidCharacter, 23, 1)]
    [Arguments("(01)09520123456788(10)A\\(B C", Gs1ErrorKind.InvalidCharacter, 26, 1)]
    // A backslash is written \\, so a value ending in one cannot swallow the next pair; a lone backslash before another character stays as it is.
    [Arguments("(90)\\\\(91)x", Gs1ErrorKind.InvalidCharacter, 4, 2)]
    [Arguments("(01)09520123456788(10)A\\\\B", Gs1ErrorKind.InvalidCharacter, 23, 2)]
    [Arguments("(01)09520123456788(10)A\\xB", Gs1ErrorKind.InvalidCharacter, 23, 1)]
    [Arguments("(01)0952012345678\u001D(10)ABC", Gs1ErrorKind.InvalidCharacter, 17, 1)]
    [Arguments("(01)09520123456788(10)A(B)C", Gs1ErrorKind.InvalidText, 24, 1)]
    public async Task TryFromHri_RefusesWithThePlaceInTheText(string hri, Gs1ErrorKind kind, int offset, int length)
    {
        await Assert.That(Gs1Hri.TryFromHri(hri, Year, out _, out var error)).IsFalse();
        await Assert.That((error.Kind, error.Offset, error.Length)).IsEqualTo((kind, offset, length));
    }

    [Test]
    public async Task TryToHri_EscapesAParenthesisAndReadsUnknownAis()
    {
        await Assert.That(Gs1ElementString.TryToHri("0109520123456788" + "10A(1)B", out var hri, out _)).IsTrue();
        await Assert.That(hri).IsEqualTo("(01)09520123456788(10)A\\(1)B");
        await Assert.That(Gs1ElementString.TryToHri("0109520123456788" + Gs1UnassignedAi.Ai234 + "XYZ\u001D" + "10ABC\u001D", out hri, out _)).IsTrue();
        await Assert.That(hri).IsEqualTo("(01)09520123456788(" + Gs1UnassignedAi.Ai234 + ")XYZ(10)ABC");
        await Assert.That(Gs1ElementString.TryToHri("0109520123456788\u001D17251231", out hri, out _)).IsTrue();
        await Assert.That(hri).IsEqualTo("(01)09520123456788(17)251231");

        // A backslash, which no GS1 character set holds but a decoded symbol can carry, is escaped too, so the text never reads back as other data.
        await Assert.That(Gs1ElementString.TryToHri("90\\" + "\u001D" + "91x", out hri, out _)).IsTrue();
        await Assert.That(hri).IsEqualTo("(90)\\\\(91)x");
        await Assert.That(Gs1ElementString.TryToHri("0109520123456788" + "10A\\(B", out hri, out _)).IsTrue();
        await Assert.That(hri).IsEqualTo("(01)09520123456788(10)A\\\\\\(B");
        await Assert.That(Gs1ElementString.TryGetToHriLength("90\\" + "\u001D" + "91x", out var length, out _)).IsTrue();
        await Assert.That(length).IsEqualTo(11);
    }

    [Test]
    [Arguments("", Gs1ErrorKind.InvalidText)]
    [Arguments("\u001D01", Gs1ErrorKind.UnexpectedSeparator)]
    [Arguments("0109520123456788\u001D\u001D10A", Gs1ErrorKind.UnexpectedSeparator)]
    [Arguments("0109520123456788" + "89ABC", Gs1ErrorKind.UnknownAi)]
    [Arguments("010952012345", Gs1ErrorKind.ValueTooShort)]
    public async Task TryToHri_RefusesWhatItCannotSplit(string elementString, Gs1ErrorKind kind)
    {
        await Assert.That(Gs1ElementString.TryToHri(elementString, out var hri, out var error)).IsFalse();
        await Assert.That(hri).IsEqualTo("");
        await Assert.That(error.Kind).IsEqualTo(kind);
    }

    /// <summary>The span forms and their length twins: exactly the length fits and writes the string form's text, one short fails with nothing written.</summary>
    [Test]
    [Arguments("(01)09520123456788(10)A\\(1)B(17)251231")]
    [Arguments("(8013)1987654Ad4X4bL5ttr2310c2K")]
    public async Task SpanForms_AgreeWithTheStringForms(string hri)
    {
        await Assert.That(Gs1Hri.TryFromHri(hri, Year, out var elementString, out _)).IsTrue();
        await Assert.That(Gs1Hri.TryGetFromHriLength(hri, Year, out var esLength, out _)).IsTrue();
        await Assert.That(esLength).IsEqualTo(elementString.Length);
        var exact = new char[esLength];
        await Assert.That(Gs1Hri.TryFromHri(hri, Year, exact, out var written, out _)).IsTrue();
        await Assert.That((written, new string(exact))).IsEqualTo((esLength, elementString));
        var short1 = new char[esLength - 1];
        await Assert.That(Gs1Hri.TryFromHri(hri, Year, short1, out written, out var error)).IsFalse();
        await Assert.That((written, error.Kind)).IsEqualTo((0, Gs1ErrorKind.DestinationTooSmall));

        await Assert.That(Gs1ElementString.TryToHri(elementString, out var back, out _)).IsTrue();
        await Assert.That(back).IsEqualTo(hri);
        await Assert.That(Gs1ElementString.TryGetToHriLength(elementString, out var hriLength, out _)).IsTrue();
        await Assert.That(hriLength).IsEqualTo(hri.Length);
        var exactHri = new char[hriLength];
        await Assert.That(Gs1ElementString.TryToHri(elementString, exactHri, out written, out _)).IsTrue();
        await Assert.That((written, new string(exactHri))).IsEqualTo((hriLength, hri));
        var shortHri = new char[hriLength - 1];
        await Assert.That(Gs1ElementString.TryToHri(elementString, shortHri, out written, out error)).IsFalse();
        await Assert.That((written, error.Kind)).IsEqualTo((0, Gs1ErrorKind.DestinationTooSmall));
    }

    /// <summary>Inputs longer than the stack scratch buffer (512 characters) go through the rented buffer in every form, on success and on refusal.</summary>
    [Test]
    public async Task LongInputs_UseTheRentedBuffer()
    {
        var longText = "(01)09520123456788" + string.Concat(Enumerable.Range(91, 9).Select(ai => "(" + ai + ")" + new string('A', 90)));
        await Assert.That(longText.Length).IsGreaterThan(512);
        await Assert.That(Gs1Hri.TryFromHri(longText, Year, out var elementString, out var error)).IsTrue().Because(error.ToString());
        await Assert.That(elementString.Length).IsGreaterThan(512);
        await Assert.That(Gs1Hri.TryGetFromHriLength(longText, Year, out var length, out _)).IsTrue();
        await Assert.That(length).IsEqualTo(elementString.Length);
        var exact = new char[length];
        await Assert.That(Gs1Hri.TryFromHri(longText, Year, exact, out var written, out _)).IsTrue();
        await Assert.That((written, new string(exact))).IsEqualTo((length, elementString));
        await Assert.That(Gs1Hri.TryFromHri(longText, Year, new char[length - 1], out written, out error)).IsFalse();
        await Assert.That((written, error.Kind)).IsEqualTo((0, Gs1ErrorKind.DestinationTooSmall));

        await Assert.That(Gs1ElementString.TryToHri(elementString, out var hri, out _)).IsTrue();
        await Assert.That(hri).IsEqualTo(longText);
        await Assert.That(Gs1ElementString.TryGetToHriLength(elementString, out length, out _)).IsTrue();
        await Assert.That(length).IsEqualTo(longText.Length);

        // Refused after the rent: the error comes back and nothing is written.
        var tooLong = "(01)09520123456788(91)" + new string('A', 600);
        await Assert.That(Gs1Hri.TryFromHri(tooLong, Year, out elementString, out error)).IsFalse();
        await Assert.That((elementString, error.Kind, error.Offset)).IsEqualTo(("", Gs1ErrorKind.ValueTooLong, 22 + 90));
        await Assert.That(Gs1Hri.TryGetFromHriLength(tooLong, Year, out length, out _)).IsFalse();
        await Assert.That(length).IsEqualTo(0);

        // Formatting reads an over-long value as it is.
        var longElementString = "0109520123456788" + "91" + new string('A', 600);
        await Assert.That(Gs1ElementString.TryToHri(longElementString, out hri, out _)).IsTrue();
        await Assert.That(hri).IsEqualTo("(01)09520123456788(91)" + new string('A', 600));
    }

    /// <summary>
    /// (AI) text of 511, 512 and 513 characters from an element string about half as long, so the text crosses the
    /// 512-character stack buffer where the input does not. The expected text is built here, apart from the formatter.
    /// </summary>
    [Test]
    [Arguments(511)]
    [Arguments(512)]
    [Arguments(513)]
    public async Task StringForm_AtTheStackBufferEdge_AgreesWithTheSpanForm(int hriLength)
    {
        // "(01)09520123456788(91)" is 22 characters, a '(' in the value is written "\(" and an 'A' as it is
        var escaped = (hriLength - 22) / 2;
        var plain = (hriLength - 22) % 2;
        var elementString = "0109520123456788" + "91" + new string('(', escaped) + new string('A', plain);
        var expected = "(01)09520123456788(91)" + string.Concat(Enumerable.Repeat("\\(", escaped)) + new string('A', plain);
        await Assert.That(expected.Length).IsEqualTo(hriLength);
        await Assert.That(elementString.Length).IsLessThan(300);

        await Assert.That(Gs1ElementString.TryToHri(elementString, out var hri, out var error)).IsTrue().Because(error.ToString());
        await Assert.That(hri).IsEqualTo(expected);
        await Assert.That(Gs1ElementString.TryRead(elementString, Fnc1Mode.FirstPosition, out var read, out _)).IsTrue();
        await Assert.That(read).IsEqualTo(expected);
        await Assert.That(Gs1ElementString.TryGetToHriLength(elementString, out var length, out _)).IsTrue();
        await Assert.That(length).IsEqualTo(hriLength);
        var exact = new char[hriLength];
        await Assert.That(Gs1ElementString.TryToHri(elementString, exact, out var written, out _)).IsTrue();
        await Assert.That((written, new string(exact))).IsEqualTo((hriLength, expected));
    }

    /// <summary>
    /// An element the splitter cannot read after the text has outgrown the stack buffer: the string form gives the
    /// length twin's error and no text, from an element string under 512 characters, which is written into the stack
    /// buffer as it is counted, and from one over 512, which is counted first.
    /// </summary>
    [Test]
    [Arguments(300)]
    [Arguments(600)]
    public async Task StringForm_RefusedAfterTheStackBufferFills(int parentheses)
    {
        // the first two elements alone format to more than 600 characters
        var elementString = "0109520123456788" + "91" + new string('(', parentheses) + "\u001D" + "89ABC";
        await Assert.That(elementString.Length < 512).IsEqualTo(parentheses < 400);
        await Assert.That(Gs1ElementString.TryGetToHriLength(elementString, out _, out var twin)).IsFalse();
        await Assert.That(twin.Kind).IsEqualTo(Gs1ErrorKind.UnknownAi);
        await Assert.That(Gs1ElementString.TryToHri(elementString, out var hri, out var error)).IsFalse();
        await Assert.That((hri, error)).IsEqualTo(("", twin));
        await Assert.That(Gs1ElementString.TryRead(elementString, Fnc1Mode.FirstPosition, out var read, out error)).IsFalse();
        await Assert.That((read, error)).IsEqualTo(("", twin));
    }

    /// <summary>A refused input yields the error, not a length or a write, from every form.</summary>
    [Test]
    public async Task EveryForm_RefusesTheSameInputTheSameWay()
    {
        var bad = "(01)09520123456789";
        await Assert.That(Gs1Hri.TryFromHri(bad, Year, out var es, out var e1)).IsFalse();
        await Assert.That(es).IsEqualTo("");
        await Assert.That(Gs1Hri.TryGetFromHriLength(bad, Year, out var length, out var e2)).IsFalse();
        await Assert.That(length).IsEqualTo(0);
        await Assert.That(Gs1Hri.TryFromHri(bad, Year, new char[64], out var written, out var e3)).IsFalse();
        await Assert.That(written).IsEqualTo(0);
        await Assert.That(e1).IsEqualTo(e2);
        await Assert.That(e2).IsEqualTo(e3);
        await Assert.That(e1.Kind).IsEqualTo(Gs1ErrorKind.InvalidCheckDigit);

        var unsplittable = "0109520123456788" + "89ABC";
        await Assert.That(Gs1ElementString.TryGetToHriLength(unsplittable, out length, out var e4)).IsFalse();
        await Assert.That((length, e4.Kind)).IsEqualTo((0, Gs1ErrorKind.UnknownAi));
        await Assert.That(Gs1ElementString.TryToHri(unsplittable, new char[64], out written, out var e5)).IsFalse();
        await Assert.That((written, e5)).IsEqualTo((0, e4));
    }

    /// <summary>The public forms use the current year and otherwise are the internal ones.</summary>
    [Test]
    public async Task PublicForms()
    {
        await Assert.That(Gs1ElementString.TryFromHri("(01)09520123456788(10)ABC", out var es, out _)).IsTrue();
        await Assert.That(es).IsEqualTo("0109520123456788" + "10ABC");
        await Assert.That(Gs1ElementString.TryGetFromHriLength("(01)09520123456788(10)ABC", out var length, out _)).IsTrue();
        await Assert.That(length).IsEqualTo(21);
        var buffer = new char[21];
        await Assert.That(Gs1ElementString.TryFromHri("(01)09520123456788(10)ABC", buffer, out var written, out _)).IsTrue();
        await Assert.That(written).IsEqualTo(21);
    }

    private static string Unescape(string hri) => hri.Replace("\\(", "(");

    /// <summary>The element string without the separators a reader tolerates: a trailing one and one after a predefined-length element.</summary>
    private static string Canonical(string elementString)
    {
        var sb = new System.Text.StringBuilder();
        var pairs = new List<(string Ai, string Value)>();
        foreach (var element in Gs1ElementString.EnumerateElements(elementString))
            pairs.Add((element.Ai.ToString(), element.Value.ToString()));
        return Gs1TestText.ToElementString(pairs)!;
    }
}
