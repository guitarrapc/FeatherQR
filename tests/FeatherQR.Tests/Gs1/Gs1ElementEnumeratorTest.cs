using FeatherQR.Gs1;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="Gs1ElementString.EnumerateElements"/>: the split of every element string the Syntax Engine accepts
/// equals the engine's own bracketed reading, and the reader's tolerances hold (an unknown AI read to the next
/// separator, a redundant or trailing separator skipped, a stop at the first element that cannot be split).
/// </summary>
public class Gs1ElementEnumeratorTest
{
    public static IEnumerable<Func<(string Id, string ElementString, string Bracketed)>> AcceptedCases()
    {
        foreach (var c in Gs1FixtureLoader.EnumerateCases())
        {
            if (!c.SyntaxEngine.Valid || c.SyntaxEngine.ElementString is null || c.SyntaxEngine.Bracketed is null)
                continue;
            if (c.InputKind is not (Gs1InputKind.ElementString or Gs1InputKind.Bracketed))
                continue;
            var captured = (c.Id, c.SyntaxEngine.ElementString, c.SyntaxEngine.Bracketed);
            yield return () => captured;
        }
    }

    [Test]
    [MethodDataSource(nameof(AcceptedCases))]
    public async Task EnumerateElements_SplitsAsTheSyntaxEngineDoes((string Id, string ElementString, string Bracketed) c)
    {
        var expected = Gs1TestText.ParseBracketed(c.Bracketed);
        await Assert.That(expected).IsNotNull().Because(c.Id);
        var actual = Split(c.ElementString);
        await Assert.That(actual).IsEquivalentTo(expected!, TUnit.Assertions.Enums.CollectionOrdering.Matching).Because(c.Id);
    }

    [Test]
    public async Task EnumerateElements_ReadsAnUnknownAiToTheNextSeparator()
    {
        var elements = Split("0109520123456788" + "234ABC\u001D" + "10DEF");
        await Assert.That(elements).IsEquivalentTo([("01", "09520123456788"), ("234", "ABC"), ("10", "DEF")], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task EnumerateElements_SkipsARedundantAndATrailingSeparator()
    {
        var elements = Split("0109520123456788\u001D" + "17251231" + "10ABC\u001D");
        await Assert.That(elements).IsEquivalentTo([("01", "09520123456788"), ("17", "251231"), ("10", "ABC")], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task EnumerateElements_StopsWhereItCannotSplit()
    {
        await Assert.That(Split("")).IsEmpty();
        await Assert.That(Split("\u001D0109520123456788")).IsEmpty();
        await Assert.That(Split("0109520123456788" + "89ABC")).IsEquivalentTo([("01", "09520123456788")], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Split("010952012345")).IsEmpty();
        await Assert.That(Split("010952012345678810ABC\u001D\u001D17251231")).IsEquivalentTo([("01", "09520123456788"), ("10", "ABC")], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Element_ReportsItsOffset()
    {
        var offsets = new List<(string Ai, int Offset)>();
        foreach (var element in Gs1ElementString.EnumerateElements("0109520123456788" + "10ABC\u001D" + "17251231"))
            offsets.Add((element.Ai.ToString(), element.Offset));
        await Assert.That(offsets).IsEquivalentTo([("01", 0), ("10", 16), ("17", 22)], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    private static List<(string Ai, string Value)> Split(string elementString)
    {
        var list = new List<(string, string)>();
        foreach (var element in Gs1ElementString.EnumerateElements(elementString))
            list.Add((element.Ai.ToString(), element.Value.ToString()));
        return list;
    }
}
