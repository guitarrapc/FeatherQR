using System.Text.RegularExpressions;
using FeatherQR.Gs1;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="Gs1ElementString.TryRead"/>, the decode-side entry: a decoder's text with its <see cref="Fnc1Mode"/>
/// comes back as (AI) text, from the element string under FNC1 in first position and from the Digital Link URI
/// otherwise. Held to the Syntax Engine's scan-data vectors (<c>]Q3</c> element strings and <c>]Q1</c> URIs or plain
/// text) and to its reading of every URI fixture.
/// </summary>
public class Gs1ReadTest
{
    private static readonly Regex EngineHriLine = new(@"^\((\d{2,4})\) ", RegexOptions.Compiled);

    public static IEnumerable<Func<(string Id, string Data, bool Valid, string? ElementString, string[]? Hri)>> QrScanCases()
    {
        foreach (var c in Gs1FixtureLoader.EnumerateCases())
        {
            if (c.InputKind != Gs1InputKind.ScanData || !(c.Input.StartsWith("]Q1", StringComparison.Ordinal) || c.Input.StartsWith("]Q3", StringComparison.Ordinal)))
                continue;
            var captured = (c.Id, c.Input, c.SyntaxEngine.Valid, c.SyntaxEngine.ElementString, c.SyntaxEngine.Hri);
            yield return () => captured;
        }
    }

    public static IEnumerable<Func<(string Id, string Input, Gs1InputKind Kind, string ElementString)>> AcceptedCases()
    {
        foreach (var c in Gs1FixtureLoader.EnumerateCases())
        {
            if (!c.SyntaxEngine.Valid || string.IsNullOrEmpty(c.SyntaxEngine.ElementString))
                continue;
            if (c.InputKind is not (Gs1InputKind.ElementString or Gs1InputKind.DigitalLink))
                continue;
            if (Gs1DigitalLinkTest.ReadDifferenceIds.Contains(c.Id))
                continue;
            var captured = (c.Id, c.Input, c.InputKind, c.SyntaxEngine.ElementString!);
            yield return () => captured;
        }
    }

    /// <summary>The engine's scan-data reading: the text after <c>]Q3</c> is an element string, the text after <c>]Q1</c> is a URI or plain data.</summary>
    [Test]
    [MethodDataSource(nameof(QrScanCases))]
    public async Task TryRead_ReadsScanDataAsTheSyntaxEngine((string Id, string Data, bool Valid, string? ElementString, string[]? Hri) c)
    {
        var fnc1 = c.Data.StartsWith("]Q3", StringComparison.Ordinal) ? Fnc1Mode.FirstPosition : default;
        var text = c.Data.Substring(3);
        var read = Gs1ElementString.TryRead(text, fnc1, out var hri, out var error);
        if (c.Id == "scan-data-014")
        {
            // A mixed-case scheme: the engine takes the text for plain data, this library reads the URI (RFC 3986 makes the scheme case-insensitive).
            await Assert.That((read, hri)).IsEqualTo((true, "(01)12312312312333(99)TEST"));
            return;
        }
        if (c.Valid && !string.IsNullOrEmpty(c.ElementString))
        {
            await Assert.That(read).IsTrue().Because($"{c.Id}: {error}");
            await Assert.That(Gs1ElementString.TryToHri(c.ElementString, out var expected, out _)).IsTrue();
            await Assert.That(hri).IsEqualTo(expected).Because(c.Id);
            if (c.Hri is { Length: > 0 })
                await Assert.That(hri.Replace("\\(", "(")).IsEqualTo(string.Concat(c.Hri.Select(line => EngineHriLine.Replace(line, "($1)")))).Because(c.Id);
            return;
        }
        await Assert.That(read).IsFalse().Because($"{c.Id}: read as {hri}");
        await Assert.That(hri).IsEqualTo("");
        await Assert.That(error.Kind).IsNotEqualTo(Gs1ErrorKind.None);
        await Assert.That(error.Offset + error.Length).IsLessThanOrEqualTo(Math.Max(text.Length, 1)).Because($"{c.Id}: {error}");
    }

    /// <summary>Every element string and URI the engine read gives the (AI) text of the engine's element string, under the carrier's <see cref="Fnc1Mode"/>.</summary>
    [Test]
    [MethodDataSource(nameof(AcceptedCases))]
    public async Task TryRead_GivesTheTextOfTheEnginesElementString((string Id, string Input, Gs1InputKind Kind, string ElementString) c)
    {
        var fnc1 = c.Kind == Gs1InputKind.ElementString ? Fnc1Mode.FirstPosition : default;
        await Assert.That(Gs1ElementString.TryRead(c.Input, fnc1, out var hri, out var error)).IsTrue().Because($"{c.Id}: {error}");
        await Assert.That(Gs1ElementString.TryToHri(c.ElementString, out var expected, out _)).IsTrue();
        await Assert.That(hri).IsEqualTo(expected).Because(c.Id);
    }

    [Test]
    public async Task TryRead_EquivalenceClasses()
    {
        var elementString = "0109520123456788" + "10ABC\u001D" + "17251231";
        var uri = "https://id.gs1.org/01/09520123456788/10/ABC?17=251231";
        var hri = "(01)09520123456788(10)ABC(17)251231";

        await Assert.That(Gs1ElementString.TryRead(elementString, Fnc1Mode.FirstPosition, out var read, out _)).IsTrue();
        await Assert.That(read).IsEqualTo(hri);
        await Assert.That(Gs1ElementString.TryRead(uri, default, out read, out _)).IsTrue();
        await Assert.That(read).IsEqualTo(hri);

        // An element string without FNC1 is plain text, and a URI under FNC1 is an element string that does not split.
        await Assert.That(Gs1ElementString.TryRead(elementString, default, out read, out var error)).IsFalse();
        await Assert.That((read, error.Kind)).IsEqualTo(("", Gs1ErrorKind.NotAUri));
        await Assert.That(Gs1ElementString.TryRead(uri, Fnc1Mode.FirstPosition, out read, out error)).IsFalse();
        await Assert.That((read, error.Kind)).IsEqualTo(("", Gs1ErrorKind.UnknownAi));

        // FNC1 in second position is not a GS1 carrier: the text is tried as a URI.
        await Assert.That(Gs1ElementString.TryRead("37" + elementString, Fnc1Mode.SecondPosition(37), out read, out error)).IsFalse();
        await Assert.That(error.Kind).IsEqualTo(Gs1ErrorKind.NotAUri);

        // Reading checks what splitting needs and no more: a bad check digit reads, an unknown AI in a URI does not.
        await Assert.That(Gs1ElementString.TryRead("https://id.gs1.org/01/09520123456789", default, out read, out _)).IsTrue();
        await Assert.That(read).IsEqualTo("(01)09520123456789");
        await Assert.That(Gs1ElementString.TryRead("https://id.gs1.org/01/09520123456788?4000=X", default, out _, out error)).IsFalse();
        await Assert.That((error.Kind, error.Offset, error.Length)).IsEqualTo((Gs1ErrorKind.UnknownAi, 37, 4));

        // The text is escaped as TryToHri escapes it, from either carrier.
        await Assert.That(Gs1ElementString.TryRead("https://id.gs1.org/01/09520123456788/10/A%281%29", default, out read, out _)).IsTrue();
        await Assert.That(read).IsEqualTo("(01)09520123456788(10)A\\(1)");

        await Assert.That(Gs1ElementString.TryRead("", Fnc1Mode.FirstPosition, out _, out error)).IsFalse();
        await Assert.That(error.Kind).IsEqualTo(Gs1ErrorKind.InvalidText);
        await Assert.That(Gs1ElementString.TryRead("", default, out _, out error)).IsFalse();
        await Assert.That(error.Kind).IsEqualTo(Gs1ErrorKind.NotAUri);
    }

    [Test]
    [Arguments("0109520123456788" + "10A(B\u001D" + "17251231", true)]
    [Arguments("https://id.gs1.org/01/09520123456788/10/A%28B?17=251231", false)]
    public async Task SpanForms_AgreeWithTheStringForms(string text, bool firstPosition)
    {
        var fnc1 = firstPosition ? Fnc1Mode.FirstPosition : default;
        await Assert.That(Gs1ElementString.TryRead(text, fnc1, out var hri, out _)).IsTrue();
        await Assert.That(hri).IsEqualTo("(01)09520123456788(10)A\\(B(17)251231");
        await Assert.That(Gs1ElementString.TryGetReadLength(text, fnc1, out var length, out _)).IsTrue();
        await Assert.That(length).IsEqualTo(hri.Length);
        await Assert.That(length).IsLessThanOrEqualTo(2 * text.Length);
        var exact = new char[length];
        await Assert.That(Gs1ElementString.TryRead(text, fnc1, exact, out var written, out _)).IsTrue();
        await Assert.That((written, new string(exact))).IsEqualTo((length, hri));
        await Assert.That(Gs1ElementString.TryRead(text, fnc1, new char[length - 1], out written, out var error)).IsFalse();
        await Assert.That((written, error.Kind)).IsEqualTo((0, Gs1ErrorKind.DestinationTooSmall));

        var bad = firstPosition ? "0109520123456788" + "89X" : "https://id.gs1.org/01/09520123456788?89=X";
        await Assert.That(Gs1ElementString.TryRead(bad, fnc1, out hri, out var e1)).IsFalse();
        await Assert.That(hri).IsEqualTo("");
        await Assert.That(Gs1ElementString.TryGetReadLength(bad, fnc1, out length, out var e2)).IsFalse();
        await Assert.That(length).IsEqualTo(0);
        await Assert.That(Gs1ElementString.TryRead(bad, fnc1, new char[256], out written, out var e3)).IsFalse();
        await Assert.That(written).IsEqualTo(0);
        await Assert.That((e1.Kind, e2, e3)).IsEqualTo((Gs1ErrorKind.UnknownAi, e1, e1));
    }
}
