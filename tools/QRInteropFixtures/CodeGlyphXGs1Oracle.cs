using CodeGlyphX;
using CodeGlyphX.Gs1Data;

namespace QRInteropFixtures;

/// <summary>
/// CodeGlyphX 2.1.0 as a GS1 data oracle: its validator takes bracketed syntax and raw element strings
/// (lengths, character sets, its linters, <c>req</c> / <c>ex</c> pairings by default), its Digital Link
/// parser and canonical builder follow URI Syntax 1.6.0, and its table is generated from the Syntax
/// Dictionary release it names. It has no scan-data path.
/// </summary>
public static class CodeGlyphXGs1Oracle
{
    public static string Version => Fnc1Writers.CodeGlyphXVersion;

    public static string DictionaryRelease => Gs1ApplicationIdentifierCatalog.Release;

    public static Gs1CodeGlyphXVerdict? Run(Gs1InputKind kind, string input)
    {
        try
        {
            return kind switch
            {
                Gs1InputKind.Bracketed or Gs1InputKind.ElementString => Validate(input),
                Gs1InputKind.DigitalLink => ParseDigitalLink(input),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            return new Gs1CodeGlyphXVerdict { Valid = false, Issues = [$"{ex.GetType().Name}: {FirstLine(ex.Message)}"] };
        }
    }

    private static Gs1CodeGlyphXVerdict Validate(string input)
    {
        var result = Gs1Validator.Validate(input);
        var issues = result.Issues.Select(Describe).ToArray();
        if (!result.IsValid)
            return new Gs1CodeGlyphXVerdict { Valid = false, Issues = issues };

        var elements = result.Elements;
        var (dl, dlError) = BuildDigitalLink(elements);
        return new Gs1CodeGlyphXVerdict
        {
            Valid = true,
            ElementString = Gs1Validator.ToElementString(elements),
            Hri = string.Concat(elements.Select(e => $"({e.Ai}){e.Data}")),
            DigitalLink = dl,
            DigitalLinkError = dlError,
            Issues = issues,
        };
    }

    private static Gs1CodeGlyphXVerdict ParseDigitalLink(string uri)
    {
        var result = Gs1DigitalLink.Validate(uri);
        var issues = result.Issues.Select(i => i.Component is null ? i.Code.ToString() : $"{i.Code}({i.Component})").ToArray();
        if (!result.IsValid || result.Value is null)
            return new Gs1CodeGlyphXVerdict { Valid = false, Issues = issues };

        var value = result.Value;
        return new Gs1CodeGlyphXVerdict
        {
            Valid = true,
            ElementString = value.ToElementString(),
            Hri = string.Concat(value.Elements.Select(e => $"({e.Ai}){e.Data}")),
            DigitalLink = value.CanonicalUri?.ToString(),
            Issues = issues,
        };
    }

    private static (string? Uri, string? Error) BuildDigitalLink(IReadOnlyList<Gs1Element> elements)
    {
        try
        {
            return (Gs1DigitalLink.BuildCanonical(elements.ToArray()).ToString(), null);
        }
        catch (Exception ex)
        {
            return (null, FirstLine(ex.Message));
        }
    }

    private static string Describe(Gs1ValidationIssue issue)
        => issue.Ai is null ? issue.Code.ToString() : $"{issue.Code}({issue.Ai})";

    private static string FirstLine(string message) => message.Split('\n')[0].TrimEnd('\r');
}
