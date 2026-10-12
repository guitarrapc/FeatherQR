using FeatherQR.Gs1;
using FeatherQR.Gs1.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The Digital Link conversions against the GS1 corpus: <see cref="Gs1ElementString.TryFromDigitalLink"/> reads every
/// URI the Syntax Engine read to the same element string and refuses, or reads into data <c>TryValidate</c> refuses,
/// every URI the engine refused (the listed deliberate differences apart); <see cref="Gs1ElementString.TryToDigitalLink"/>
/// builds the engine's URI with its query in the canonical order, which is CodeGlyphX's URI where both built one; the
/// two round-trip; and the span forms, their length twins and the stem argument behave as the plan says.
/// </summary>
public class Gs1DigitalLinkTest
{
    private const int Year = 2026;
    private const string Gtin = "0109520123456788";

    /// <summary>URIs the engine read whose element string this library does not produce, by cause (the fixture spec records them).</summary>
    private static readonly Dictionary<string, Gs1ErrorKind> ReadDifferences = new()
    {
        ["parse-dl-uri-098"] = Gs1ErrorKind.ValueTooShort,         // the engine pads a 13-digit GTIN in the query to 14; URI Syntax 1.7.0 section 4.1 says a GTIN is 14 digits
        ["eq-dl-percent-malformed"] = Gs1ErrorKind.InvalidPercentEncoding, // the engine keeps a malformed escape as text
    };

    internal static IEnumerable<string> ReadDifferenceIds => ReadDifferences.Keys;

    /// <summary>URIs the engine refused that this library reads into data every check passes, by cause.</summary>
    private static readonly HashSet<string> AcceptDifferences =
    [
        "parse-dl-uri-012",     // a mixed-case scheme: RFC 3986 makes the scheme case-insensitive, the engine takes it for plain data
    ];

    /// <summary>Element strings the engine could not put in a URI that this library can, by cause.</summary>
    private static readonly HashSet<string> BuildDifferences =
    [
        "generate-dl-uri-020",  // (01)(235)(10): the engine takes the first longest qualifier sequence and strands (235); this library takes the sequence that leaves only data attributes behind
        "parse-ai-data-010",    // the same data
        "process-ai-data-058",  // the same data
    ];

    /// <summary>The engine's refusal messages for a URI and the kinds this library may report for them. A message not listed here is a value fault, which the library reads and <c>TryValidate</c> refuses.</summary>
    private static readonly (string Fragment, Gs1ErrorKind[] Kinds)[] UriKindMap =
    [
        ("should be in the path info", [Gs1ErrorKind.InvalidKeyQualifierSequence]),
        ("not a valid key-qualifier sequence", [Gs1ErrorKind.InvalidKeyQualifierSequence]),
        ("is duplicated", [Gs1ErrorKind.RepeatedAi]),
        ("is not a valid DL URI data attribute", [Gs1ErrorKind.AiNotAllowedInUri]),
        ("element is empty", [Gs1ErrorKind.EmptyValue]),
        ("illegal null character", [Gs1ErrorKind.InvalidPercentEncoding]),
        ("illegal characters", [Gs1ErrorKind.NotAUri]),
        ("must contain a domain and path info", [Gs1ErrorKind.NotAUri]),
        ("No GS1 DL keys found", [Gs1ErrorKind.MissingPrimaryKey, Gs1ErrorKind.CompressedUri]),
        ("Unknown AI", [Gs1ErrorKind.UnknownAi]),
        ("value is too short", [Gs1ErrorKind.ValueTooShort]),
        ("value is too long", [Gs1ErrorKind.ValueTooLong]),
    ];

    public static IEnumerable<Func<(string Id, string Uri, bool Valid, string? ElementString, string? Error)>> UriCases()
    {
        foreach (var c in Gs1FixtureLoader.EnumerateCases())
        {
            if (c.InputKind != Gs1InputKind.DigitalLink)
                continue;
            var captured = (c.Id, c.Input, c.SyntaxEngine.Valid, c.SyntaxEngine.ElementString, c.SyntaxEngine.Error);
            yield return () => captured;
        }
    }

    public static IEnumerable<Func<(string Id, string ElementString, string? Uri, string? UriError, string? CodeGlyphXUri)>> BuildCases()
    {
        foreach (var c in Gs1FixtureLoader.EnumerateCases())
        {
            if (!c.SyntaxEngine.Valid || string.IsNullOrEmpty(c.SyntaxEngine.ElementString))
                continue;
            if (c.InputKind is not (Gs1InputKind.ElementString or Gs1InputKind.Bracketed))
                continue;
            if (c.SyntaxEngine.DigitalLink is null && c.SyntaxEngine.DigitalLinkError is null)
                continue;
            var cgx = c.CodeGlyphX?.DigitalLink;
            var captured = (c.Id, c.SyntaxEngine.ElementString!, c.SyntaxEngine.DigitalLink, c.SyntaxEngine.DigitalLinkError, cgx is not null && cgx.StartsWith("https://", StringComparison.Ordinal) ? cgx : null);
            yield return () => captured;
        }
    }

    [Test]
    [MethodDataSource(nameof(UriCases))]
    public async Task TryFromDigitalLink_ReadsAsTheSyntaxEngine((string Id, string Uri, bool Valid, string? ElementString, string? Error) c)
    {
        var read = Gs1ElementString.TryFromDigitalLink(c.Uri, out var elementString, out var error);
        if (ReadDifferences.TryGetValue(c.Id, out var differentKind))
        {
            await Assert.That((read, error.Kind)).IsEqualTo((false, differentKind)).Because(c.Id);
            return;
        }
        if (c.Valid && !string.IsNullOrEmpty(c.ElementString))
        {
            await Assert.That(read).IsTrue().Because($"{c.Id}: {error}");
            await Assert.That(elementString).IsEqualTo(c.ElementString).Because(c.Id);
            await Assert.That(error).IsEqualTo(default(Gs1Error));
            return;
        }
        if (AcceptDifferences.Contains(c.Id))
        {
            await Assert.That(read).IsTrue().Because($"{c.Id}: {error}");
            await Assert.That(Gs1Validator.TryValidate(elementString, Year, out error)).IsTrue().Because($"{c.Id}: {error}");
            return;
        }
        if (c.Valid)
        {
            // The engine took the text for plain data, not a URI.
            await Assert.That((read, error.Kind)).IsEqualTo((false, Gs1ErrorKind.NotAUri)).Because(c.Id);
            return;
        }
        // The engine refused: either the URI's structure is wrong, in which case the kind follows the engine's message, or the data it carries fails a check.
        if (read)
        {
            await Assert.That(Gs1Validator.TryValidate(elementString, Year, out error)).IsFalse().Because($"{c.Id}: read as {elementString}");
            return;
        }
        var allowed = UriKindMap.FirstOrDefault(m => c.Error!.Contains(m.Fragment, StringComparison.Ordinal)).Kinds;
        await Assert.That(allowed).IsNotNull().Because($"{c.Id}: the engine's \"{c.Error}\" is a value fault, which this library should read");
        await Assert.That(allowed!).Contains(error.Kind).Because($"{c.Id}: {c.Error} -> {error}");
        await Assert.That(error.Offset + error.Length).IsLessThanOrEqualTo(Math.Max(c.Uri.Length, 1)).Because($"{c.Id}: {error}");
    }

    [Test]
    [Arguments("HTTPS://ID.GS1.ORG/01/09520123456788", Gtin)]
    [Arguments("Https://id.gs1.org/01/09520123456788", Gtin)]
    [Arguments("http://a/01/09520123456788", Gtin)]
    [Arguments("https://brand.example.com/some/path/01/09520123456788/22/2A/10/ABC/21/123?17=251231&3103=000195&linkType=gs1:pip&x", Gtin + "222A\u001D" + "10ABC\u001D" + "21123\u001D" + "17251231" + "3103000195")]
    [Arguments("https://id.gs1.org/01/09520123456788/235/XYZ?10=ABC", Gtin + "235XYZ\u001D" + "10ABC")]
    [Arguments("https://id.gs1.org/01/09520123456788?99=A%2FB+C&98=%41", Gtin + "99A/B C\u001D" + "98A")]
    [Arguments("https://id.gs1.org/01/09520123456788/10/A+B%2fC#frag", Gtin + "10A+B/C")]
    [Arguments("https://id.gs1.org/00/faux/00/195201234567891232", "00195201234567891232")]
    [Arguments("https://id.gs1.org/01/09520123456788?", Gtin)]
    [Arguments("https://id.gs1.org/01/09520123456788?&&99=A&&", Gtin + "99A")]
    [Arguments("https://id.gs1.org/01/09520123456788?99=%E3%81%82", Gtin + "99あ")]
    [Arguments("https://id.gs1.org/01/09520123456789", "0109520123456789")]
    [Arguments("https://id.gs1.org/414/9520123456788/7040/1ABC", "4149520123456788" + "70401ABC")]
    [Arguments("https://[2001:db8::1]:8443/01/09520123456788", Gtin)]
    [Arguments("https://id.gs1.org/01/09520123456788/10/A%F0%9F%98%80", Gtin + "10A\U0001F600")]
    public async Task TryFromDigitalLink_EquivalenceClasses(string uri, string elementString)
    {
        await Assert.That(Gs1ElementString.TryFromDigitalLink(uri, out var read, out var error)).IsTrue().Because(error.ToString());
        await Assert.That(read).IsEqualTo(elementString);
    }

    [Test]
    [Arguments("", Gs1ErrorKind.NotAUri, 0, 0)]
    [Arguments("id.gs1.org/01/09520123456788", Gs1ErrorKind.NotAUri, 0, 28)]
    [Arguments("ftp://a/01/09520123456788", Gs1ErrorKind.NotAUri, 0, 25)]
    [Arguments("http://", Gs1ErrorKind.NotAUri, 7, 0)]
    [Arguments("http:///", Gs1ErrorKind.NotAUri, 7, 0)]
    [Arguments("http://a", Gs1ErrorKind.NotAUri, 8, 0)]
    [Arguments("https://a$/01/09520123456788", Gs1ErrorKind.NotAUri, 9, 1)]
    [Arguments("https://a/01/09520123456788<x", Gs1ErrorKind.NotAUri, 27, 1)]
    [Arguments("https://a/01/09520123456788?99=a b", Gs1ErrorKind.NotAUri, 32, 1)]
    [Arguments("https://id.gs1.org/10/ABC", Gs1ErrorKind.MissingPrimaryKey, 18, 7)]
    [Arguments("https://id.gs1.org/", Gs1ErrorKind.MissingPrimaryKey, 18, 1)]
    [Arguments("https://id.gs1.org/01/09520123456788/", Gs1ErrorKind.MissingPrimaryKey, 18, 19)]
    [Arguments("https://id.gs1.org/01/09520123456788/89/ABC", Gs1ErrorKind.MissingPrimaryKey, 18, 25)]
    [Arguments("https://id.gs1.org/gtin/09520123456788", Gs1ErrorKind.MissingPrimaryKey, 18, 20)]
    [Arguments("https://id.gs1.org/DBHKVAdpQgqrAg", Gs1ErrorKind.CompressedUri, 19, 14)]
    [Arguments("https://id.gs1.org/01/09520123456788/DBHKVAdpQgqrAg", Gs1ErrorKind.CompressedUri, 37, 14)]
    [Arguments("https://id.gs1.org/01/09520123456788/10/ABC/22/2A", Gs1ErrorKind.InvalidKeyQualifierSequence, 44, 2)]
    [Arguments("https://id.gs1.org/01/09520123456788/22/2A/235/X", Gs1ErrorKind.InvalidKeyQualifierSequence, 43, 3)]
    [Arguments("https://id.gs1.org/01/09520123456788/99/ABC", Gs1ErrorKind.InvalidKeyQualifierSequence, 37, 2)]
    [Arguments("https://id.gs1.org/00/195201234567891232/10/ABC", Gs1ErrorKind.InvalidKeyQualifierSequence, 41, 2)]
    [Arguments("https://id.gs1.org/01/09520123456788?10=ABC", Gs1ErrorKind.InvalidKeyQualifierSequence, 37, 2)]
    [Arguments("https://id.gs1.org/01/09520123456788?8200=x", Gs1ErrorKind.AiNotAllowedInUri, 37, 4)]
    [Arguments("https://id.gs1.org/01/09520123456788/21/ABC?235=XYZ", Gs1ErrorKind.AiNotAllowedInUri, 44, 3)]
    [Arguments("https://id.gs1.org/01/09520123456788?8040=123456789012345", Gs1ErrorKind.AiNotAllowedInUri, 37, 4)]
    [Arguments("https://id.gs1.org/01/09520123456788?" + Gs1UnassignedAi.Ai7050 + "=ABC", Gs1ErrorKind.UnknownAi, 37, 4)]
    [Arguments("https://id.gs1.org/01/09520123456788?17=", Gs1ErrorKind.EmptyValue, 40, 0)]
    [Arguments("https://id.gs1.org/01/09520123456788/10/", Gs1ErrorKind.EmptyValue, 40, 0)]
    [Arguments("https://id.gs1.org/01/09520123456788/10/ABC?10=ABC", Gs1ErrorKind.RepeatedAi, 44, 2)]
    [Arguments("https://id.gs1.org/01/09520123456788?01=09520123456788", Gs1ErrorKind.RepeatedAi, 37, 2)]
    [Arguments("https://id.gs1.org/01/09520123456788?17=251231&17=261231", Gs1ErrorKind.RepeatedAi, 47, 2)]
    [Arguments("https://id.gs1.org/01/09520123456788?99=X&99=X", Gs1ErrorKind.RepeatedAi, 42, 2)]
    [Arguments("https://id.gs1.org/01/0952012345678", Gs1ErrorKind.ValueTooShort, 22, 13)]
    [Arguments("https://id.gs1.org/01/095201234567881", Gs1ErrorKind.ValueTooLong, 22, 15)]
    [Arguments("https://id.gs1.org/01/09520123456788?17=25123", Gs1ErrorKind.ValueTooShort, 40, 5)]
    [Arguments("https://id.gs1.org/01/09520123456788/10/A%2", Gs1ErrorKind.InvalidPercentEncoding, 41, 1)]
    [Arguments("https://id.gs1.org/01/09520123456788/10/A%2G", Gs1ErrorKind.InvalidPercentEncoding, 41, 1)]
    [Arguments("https://id.gs1.org/01/09520123456788/10/A%00B", Gs1ErrorKind.InvalidPercentEncoding, 41, 3)]
    [Arguments("https://id.gs1.org/01/09520123456788/10/A%1DB", Gs1ErrorKind.InvalidPercentEncoding, 41, 3)]
    [Arguments("https://id.gs1.org/01/09520123456788/10/A%C3%28", Gs1ErrorKind.InvalidPercentEncoding, 41, 3)]
    [Arguments("https://id.gs1.org/01/09520123456788/10/A%C3", Gs1ErrorKind.InvalidPercentEncoding, 41, 3)]
    [Arguments("https://id.gs1.org/01/09520123456788/10/A%C0%80", Gs1ErrorKind.InvalidPercentEncoding, 41, 3)]
    [Arguments("https://id.gs1.org/01/09520123456788/10/A%ED%A0%80", Gs1ErrorKind.InvalidPercentEncoding, 41, 3)]
    [Arguments("https://id.gs1.org/01/09520123456788/10/A%F4%90%80%80", Gs1ErrorKind.InvalidPercentEncoding, 41, 3)]
    [Arguments("https://id.gs1.org/01/09520123456788/10/A%80", Gs1ErrorKind.InvalidPercentEncoding, 41, 3)]
    [Arguments("https://id.gs1.org/01/09520123456788/10/A%FF", Gs1ErrorKind.InvalidPercentEncoding, 41, 3)]
    [Arguments("https://id.gs1.org/01/09520123456788?=x", Gs1ErrorKind.UnknownAi, 37, 0)]
    [Arguments("https://id.gs1.org/01/09520123456788/22/2A?10=ABC", Gs1ErrorKind.InvalidKeyQualifierSequence, 43, 2)]
    public async Task TryFromDigitalLink_RefusesWithTheKindAndPlace(string uri, Gs1ErrorKind kind, int offset, int length)
    {
        await Assert.That(Gs1ElementString.TryFromDigitalLink(uri, out var elementString, out var error)).IsFalse();
        await Assert.That(elementString).IsEqualTo("");
        await Assert.That((error.Kind, error.Offset, error.Length)).IsEqualTo((kind, offset, length));
    }

    [Test]
    [MethodDataSource(nameof(BuildCases))]
    public async Task TryToDigitalLink_BuildsTheSyntaxEngineUri((string Id, string ElementString, string? Uri, string? UriError, string? CodeGlyphXUri) c)
    {
        var built = Gs1DigitalLink.TryToDigitalLink(c.ElementString, Year, null, out var uri, out var error);
        if (c.Uri is not null)
        {
            await Assert.That(built).IsTrue().Because($"{c.Id}: {error}");
            await Assert.That(uri).IsEqualTo(Canonical(c.Uri)).Because(c.Id);
            if (c.CodeGlyphXUri is not null)
                await Assert.That(uri).IsEqualTo(c.CodeGlyphXUri).Because($"{c.Id} (CodeGlyphX)");
            return;
        }
        if (BuildDifferences.Contains(c.Id))
        {
            await Assert.That(built).IsTrue().Because($"{c.Id}: {error}");
            await Assert.That(Gs1ElementString.TryFromDigitalLink(uri, out var back, out _)).IsTrue();
            await Assert.That(Pairs(back)).IsEquivalentTo(Pairs(c.ElementString));
            return;
        }
        await Assert.That(built).IsFalse().Because(c.Id);
        var expected = c.UriError!.Contains("without a primary key", StringComparison.Ordinal) ? Gs1ErrorKind.MissingPrimaryKey : Gs1ErrorKind.AiNotAllowedInUri;
        await Assert.That(error.Kind).IsEqualTo(expected).Because($"{c.Id}: {c.UriError}");
    }

    /// <summary>A URI the engine read, read here and built again, is the engine's canonical URI of that data.</summary>
    [Test]
    [MethodDataSource(nameof(UriCases))]
    public async Task RoundTrip_UriThroughElementString((string Id, string Uri, bool Valid, string? ElementString, string? Error) c)
    {
        if (!c.Valid || string.IsNullOrEmpty(c.ElementString) || ReadDifferences.ContainsKey(c.Id))
            return;
        var fixture = Gs1FixtureLoader.EnumerateCases().First(f => f.Id == c.Id);
        await Assert.That(Gs1ElementString.TryFromDigitalLink(c.Uri, out var elementString, out _)).IsTrue();
        await Assert.That(Gs1DigitalLink.TryToDigitalLink(elementString, Year, null, out var uri, out var error)).IsTrue().Because($"{c.Id}: {error}");
        await Assert.That(uri).IsEqualTo(Canonical(fixture.SyntaxEngine.DigitalLink!)).Because(c.Id);
    }

    /// <summary>An element string a URI can carry comes back with the same (AI, value) pairs, in the URI's order.</summary>
    [Test]
    [MethodDataSource(nameof(BuildCases))]
    public async Task RoundTrip_ElementStringThroughUri((string Id, string ElementString, string? Uri, string? UriError, string? CodeGlyphXUri) c)
    {
        if (c.Uri is null)
            return;
        await Assert.That(Gs1DigitalLink.TryToDigitalLink(c.ElementString, Year, null, out var uri, out _)).IsTrue();
        await Assert.That(Gs1ElementString.TryFromDigitalLink(uri, out var back, out var error)).IsTrue().Because($"{c.Id}: {error}");
        await Assert.That(Pairs(back)).IsEquivalentTo(Pairs(c.ElementString)).Because(c.Id);
        await Assert.That(Gs1Validator.TryValidate(back, Year, out error)).IsTrue().Because($"{c.Id}: {error}");
    }

    [Test]
    [Arguments(Gtin, null, "https://id.gs1.org/01/09520123456788")]
    [Arguments(Gtin, "https://brand.example.com", "https://brand.example.com/01/09520123456788")]
    [Arguments(Gtin, "https://brand.example.com/", "https://brand.example.com/01/09520123456788")]
    [Arguments(Gtin, "http://x/y/", "http://x/y/01/09520123456788")]
    [Arguments(Gtin, "HTTPS://Brand.Example.com:8443/path", "HTTPS://Brand.Example.com:8443/path/01/09520123456788")]
    [Arguments(Gtin, "https://[2001:db8::1]:8443", "https://[2001:db8::1]:8443/01/09520123456788")]
    [Arguments(Gtin, "https://[::ffff:192.0.2.1]", "https://[::ffff:192.0.2.1]/01/09520123456788")]
    [Arguments(Gtin, "http://192.0.2.1:80", "http://192.0.2.1:80/01/09520123456788")]
    [Arguments(Gtin, "https://xn--fsq.xn--0zwm56d/", "https://xn--fsq.xn--0zwm56d/01/09520123456788")]
    [Arguments(Gtin + "21123\u001D" + "10ABC\u001D" + "222A", null, "https://id.gs1.org/01/09520123456788/22/2A/10/ABC/21/123")]
    [Arguments(Gtin + "3922" + "0299\u001D" + "17251231" + "3103000195", null, "https://id.gs1.org/01/09520123456788?17=251231&3103=000195&3922=0299")]
    [Arguments(Gtin + "00195201234567891232", null, "https://id.gs1.org/01/09520123456788?00=195201234567891232")]
    [Arguments("00195201234567891232" + "0209520123456788" + "375\u001D" + "10ABC", null, "https://id.gs1.org/00/195201234567891232?02=09520123456788&10=ABC&37=5")]
    [Arguments(Gtin + "235XYZ\u001D" + "10ABC", null, "https://id.gs1.org/01/09520123456788/235/XYZ?10=ABC")]
    [Arguments(Gtin + "10A+B/C!\u001D" + "99a.b_-", null, "https://id.gs1.org/01/09520123456788/10/A%2BB%2FC%21?99=a.b_-")]
    [Arguments(Gtin + "10ABC\u001D" + "10ABC\u001D" + "99X\u001D" + "99X", null, "https://id.gs1.org/01/09520123456788/10/ABC?99=X")]
    [Arguments("4149520123456788" + "70401ABC", null, "https://id.gs1.org/414/9520123456788/7040/1ABC")]
    [Arguments("8017795260646688514634\u001D" + "80191", null, "https://id.gs1.org/8017/795260646688514634/8019/1")]
    public async Task TryToDigitalLink_EquivalenceClasses(string elementString, string? stem, string uri)
    {
        await Assert.That(Gs1DigitalLink.TryToDigitalLink(elementString, Year, stem, out var built, out var error)).IsTrue().Because(error.ToString());
        await Assert.That(built).IsEqualTo(uri);
    }

    [Test]
    [Arguments("", Gs1ErrorKind.InvalidText, 0, 0)]
    [Arguments("10ABC", Gs1ErrorKind.MissingRequiredAi, 0, 5)]
    [Arguments("0109520123456789", Gs1ErrorKind.InvalidCheckDigit, 15, 1)]
    [Arguments("90ABC", Gs1ErrorKind.MissingPrimaryKey, 0, 5)]
    [Arguments(Gtin + "8200http://x", Gs1ErrorKind.AiNotAllowedInUri, 16, 4)]
    [Arguments(Gtin + "235TPX\u001D" + "222A", Gs1ErrorKind.AiNotAllowedInUri, 16, 3)]
    [Arguments(Gtin + "222A\u001D" + "10DEF\u001D" + "235TPX", Gs1ErrorKind.AiNotAllowedInUri, 27, 3)]
    [Arguments(Gtin + "21ABC\u001D" + "8040123456789012345", Gs1ErrorKind.AiNotAllowedInUri, 22, 4)]
    public async Task TryToDigitalLink_RefusesWithTheKindAndPlace(string elementString, Gs1ErrorKind kind, int offset, int length)
    {
        await Assert.That(Gs1DigitalLink.TryToDigitalLink(elementString, Year, null, out var uri, out var error)).IsFalse();
        await Assert.That(uri).IsEqualTo("");
        await Assert.That((error.Kind, error.Offset, error.Length)).IsEqualTo((kind, offset, length));
    }

    [Test]
    [Arguments("")]
    [Arguments("ftp://a")]
    [Arguments("https://")]
    [Arguments("https:///path")]
    [Arguments("https://a?x")]
    [Arguments("https://a#x")]
    [Arguments("https://a/b c")]
    [Arguments("a/b")]
    // An authority that is not a host with an optional port.
    [Arguments("https://:")]
    [Arguments("https://:8443")]
    [Arguments("https://[]")]
    [Arguments("https://[::1")]
    [Arguments("https://[zz::1]")]
    [Arguments("https://a:")]
    [Arguments("https://a:x")]
    [Arguments("https://a:1:2")]
    [Arguments("https://a_b")]
    [Arguments("https://a%20b")]
    public async Task TryToDigitalLink_ThrowsForAStemThatIsNotAUriPrefix(string stem)
    {
        await Assert.That(() => Gs1ElementString.TryToDigitalLink(Gtin, out _, out _, stem)).Throws<ArgumentException>();
        await Assert.That(() => Gs1ElementString.TryToDigitalLink(Gtin, new char[128], out _, out _, stem)).Throws<ArgumentException>();
        await Assert.That(() => Gs1ElementString.TryGetToDigitalLinkLength(Gtin, out _, out _, stem)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments(Gtin + "222A\u001D" + "10A/B\u001D" + "21123\u001D" + "17251231", "https://brand.example.com/x")]
    [Arguments("8017795260646688514634\u001D" + "99000001\u001D" + "2539526064000028000001", null)]
    public async Task SpanForms_AgreeWithTheStringForms(string elementString, string? stem)
    {
        await Assert.That(Gs1DigitalLink.TryToDigitalLink(elementString, Year, stem, out var uri, out _)).IsTrue();
        await Assert.That(Gs1DigitalLink.TryGetToDigitalLinkLength(elementString, Year, stem, out var uriLength, out _)).IsTrue();
        await Assert.That(uriLength).IsEqualTo(uri.Length);
        await Assert.That(uriLength).IsLessThanOrEqualTo((stem ?? "https://id.gs1.org").Length + 3 * elementString.Length + 2);
        var exact = new char[uriLength];
        await Assert.That(Gs1DigitalLink.TryToDigitalLink(elementString, Year, stem, exact, out var written, out _)).IsTrue();
        await Assert.That((written, new string(exact))).IsEqualTo((uriLength, uri));
        await Assert.That(Gs1DigitalLink.TryToDigitalLink(elementString, Year, stem, new char[uriLength - 1], out written, out var error)).IsFalse();
        await Assert.That((written, error.Kind)).IsEqualTo((0, Gs1ErrorKind.DestinationTooSmall));

        await Assert.That(Gs1ElementString.TryFromDigitalLink(uri, out var back, out _)).IsTrue();
        await Assert.That(Gs1ElementString.TryGetFromDigitalLinkLength(uri, out var esLength, out _)).IsTrue();
        await Assert.That(esLength).IsEqualTo(back.Length);
        await Assert.That(esLength).IsLessThanOrEqualTo(uri.Length);
        var exactEs = new char[esLength];
        await Assert.That(Gs1ElementString.TryFromDigitalLink(uri, exactEs, out written, out _)).IsTrue();
        await Assert.That((written, new string(exactEs))).IsEqualTo((esLength, back));
        await Assert.That(Gs1ElementString.TryFromDigitalLink(uri, new char[esLength - 1], out written, out error)).IsFalse();
        await Assert.That((written, error.Kind)).IsEqualTo((0, Gs1ErrorKind.DestinationTooSmall));
    }

    /// <summary>Inputs longer than the stack scratch buffer (512 characters) go through the rented buffer in every form, on success and on refusal.</summary>
    [Test]
    public async Task LongInputs_UseTheRentedBuffer()
    {
        var longElementString = Gtin + string.Join("\u001D", Enumerable.Range(91, 9).Select(ai => ai + new string('A', 90)));
        await Assert.That(longElementString.Length).IsGreaterThan(512);
        await Assert.That(Gs1DigitalLink.TryToDigitalLink(longElementString, Year, null, out var uri, out var error)).IsTrue().Because(error.ToString());
        await Assert.That(uri.Length).IsGreaterThan(512);
        await Assert.That(Gs1DigitalLink.TryGetToDigitalLinkLength(longElementString, Year, null, out var length, out _)).IsTrue();
        await Assert.That(length).IsEqualTo(uri.Length);

        await Assert.That(Gs1ElementString.TryFromDigitalLink(uri, out var back, out error)).IsTrue().Because(error.ToString());
        await Assert.That(Pairs(back)).IsEquivalentTo(Pairs(longElementString));
        await Assert.That(Gs1ElementString.TryGetFromDigitalLinkLength(uri, out length, out _)).IsTrue();
        await Assert.That(length).IsEqualTo(back.Length);
        var exact = new char[length];
        await Assert.That(Gs1ElementString.TryFromDigitalLink(uri, exact, out var written, out _)).IsTrue();
        await Assert.That((written, new string(exact))).IsEqualTo((length, back));
        await Assert.That(Gs1ElementString.TryFromDigitalLink(uri, new char[length - 1], out written, out error)).IsFalse();
        await Assert.That((written, error.Kind)).IsEqualTo((0, Gs1ErrorKind.DestinationTooSmall));

        await Assert.That(Gs1ElementString.TryRead(uri, default, out var hri, out _)).IsTrue();
        await Assert.That(Gs1ElementString.TryToHri(back, out var expectedHri, out _)).IsTrue();
        await Assert.That(hri).IsEqualTo(expectedHri);
        await Assert.That(Gs1ElementString.TryGetReadLength(uri, default, out length, out _)).IsTrue();
        await Assert.That(length).IsEqualTo(hri.Length);
        var exactHri = new char[length];
        await Assert.That(Gs1ElementString.TryRead(uri, default, exactHri, out written, out _)).IsTrue();
        await Assert.That((written, new string(exactHri))).IsEqualTo((length, hri));

        // Refused after the rent: the error comes back and nothing is written.
        var bad = "https://id.gs1.org/01/09520123456788/10/" + new string('A', 600) + "%2";
        await Assert.That(Gs1ElementString.TryFromDigitalLink(bad, out back, out error)).IsFalse();
        await Assert.That((back, error.Kind, error.Offset)).IsEqualTo(("", Gs1ErrorKind.InvalidPercentEncoding, 40 + 600));
        await Assert.That(Gs1ElementString.TryGetFromDigitalLinkLength(bad, out length, out _)).IsFalse();
        await Assert.That(length).IsEqualTo(0);
        await Assert.That(Gs1ElementString.TryRead(bad, default, out hri, out error)).IsFalse();
        await Assert.That((hri, error.Kind)).IsEqualTo(("", Gs1ErrorKind.InvalidPercentEncoding));
    }

    /// <summary>A refused input yields the data's error from every form, a short destination included.</summary>
    [Test]
    public async Task EveryForm_RefusesTheSameInputTheSameWay()
    {
        var noKey = "90ABC";
        await Assert.That(Gs1DigitalLink.TryToDigitalLink(noKey, Year, null, out var uri, out var e1)).IsFalse();
        await Assert.That(uri).IsEqualTo("");
        await Assert.That(Gs1DigitalLink.TryGetToDigitalLinkLength(noKey, Year, null, out var length, out var e2)).IsFalse();
        await Assert.That(length).IsEqualTo(0);
        await Assert.That(Gs1DigitalLink.TryToDigitalLink(noKey, Year, null, new char[2], out var written, out var e3)).IsFalse();
        await Assert.That(written).IsEqualTo(0);
        await Assert.That((e1, e2, e3)).IsEqualTo((e1, e1, e1));
        await Assert.That(e1.Kind).IsEqualTo(Gs1ErrorKind.MissingPrimaryKey);

        var badUri = "https://id.gs1.org/01/09520123456788?" + Gs1UnassignedAi.Ai7050 + "=ABC";
        await Assert.That(Gs1ElementString.TryGetFromDigitalLinkLength(badUri, out length, out var e4)).IsFalse();
        await Assert.That((length, e4.Kind)).IsEqualTo((0, Gs1ErrorKind.UnknownAi));
        await Assert.That(Gs1ElementString.TryFromDigitalLink(badUri, new char[2], out written, out var e5)).IsFalse();
        await Assert.That((written, e5)).IsEqualTo((0, e4));
    }

    [Test]
    public async Task PublicForms()
    {
        await Assert.That(Gs1ElementString.TryToDigitalLink(Gtin + "10ABC", out var uri, out _)).IsTrue();
        await Assert.That(uri).IsEqualTo("https://id.gs1.org/01/09520123456788/10/ABC");
        await Assert.That(Gs1ElementString.TryToDigitalLink(Gtin + "10ABC", out uri, out _, "https://example.com")).IsTrue();
        await Assert.That(uri).IsEqualTo("https://example.com/01/09520123456788/10/ABC");
        await Assert.That(Gs1ElementString.TryGetToDigitalLinkLength(Gtin + "10ABC", out var length, out _)).IsTrue();
        await Assert.That(length).IsEqualTo(43);
        var buffer = new char[43];
        await Assert.That(Gs1ElementString.TryToDigitalLink(Gtin + "10ABC", buffer, out var written, out _)).IsTrue();
        await Assert.That(written).IsEqualTo(43);
    }

    /// <summary>The engine's URI with its query parameters in the canonical order (URI Syntax 1.7.0 section 4.12), which the engine does not apply.</summary>
    internal static string Canonical(string uri)
    {
        var q = uri.IndexOf('?');
        if (q < 0)
            return uri;
        var parameters = uri.Substring(q + 1).Split('&').Select(p => (Key: p.Substring(0, p.IndexOf('=')), Text: p)).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Text);
        return uri.Substring(0, q + 1) + string.Join("&", parameters);
    }

    private static List<(string Ai, string Value)> Pairs(string elementString)
    {
        var pairs = new List<(string, string)>();
        foreach (var element in Gs1ElementString.EnumerateElements(elementString))
            pairs.Add((element.Ai.ToString(), element.Value.ToString()));
        return pairs.Distinct().OrderBy(p => p.Item1, StringComparer.Ordinal).ThenBy(p => p.Item2, StringComparer.Ordinal).ToList();
    }
}
