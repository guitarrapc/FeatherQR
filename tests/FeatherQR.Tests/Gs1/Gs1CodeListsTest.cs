using FeatherQR.Gs1.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The code lists generated from the Syntax Dictionary's reference linters (<see cref="Gs1CodeLists"/>), checked
/// against those linters' own unit tests as imported under Fixtures/Gs1/syntax-dictionary/linters/: for every
/// well-formed value the list gives the reference's verdict, and a malformed value is never in a list.
/// </summary>
public class Gs1CodeListsTest
{
    [Test]
    public async Task Iso3166Numeric_MatchesTheReferenceLinter()
    {
        var vectors = Gs1FixtureLoader.LoadLinter("iso3166").Cases;
        var wellFormed = vectors.Where(v => v.Value.Length == 3 && v.Value.All(char.IsAsciiDigit)).ToArray();
        await Assert.That(wellFormed.Select(v => v.Value).Distinct().Count()).IsEqualTo(1000);
        foreach (var v in wellFormed)
            await Assert.That(Gs1CodeLists.IsIso3166Numeric(int.Parse(v.Value))).IsEqualTo(v.Pass).Because(v.Value);
        await Assert.That(Enumerable.Range(0, 1000).Count(Gs1CodeLists.IsIso3166Numeric)).IsEqualTo(249);
        await Assert.That(Gs1CodeLists.IsIso3166Numeric(-1)).IsFalse();
        await Assert.That(Gs1CodeLists.IsIso3166Numeric(1000)).IsFalse();
    }

    [Test]
    public async Task Iso3166Alpha2_MatchesTheReferenceLinter()
    {
        var vectors = Gs1FixtureLoader.LoadLinter("iso3166alpha2").Cases;
        var wellFormed = vectors.Where(v => v.Value.Length == 2 && v.Value.All(char.IsAsciiLetterUpper)).ToArray();
        await Assert.That(wellFormed.Select(v => v.Value).Distinct().Count()).IsEqualTo(676);
        foreach (var v in wellFormed)
            await Assert.That(Gs1CodeLists.IsIso3166Alpha2(v.Value[0], v.Value[1])).IsEqualTo(v.Pass).Because(v.Value);
        await Assert.That(Gs1CodeLists.IsIso3166Alpha2('d', 'e')).IsFalse();
        await Assert.That(Gs1CodeLists.IsIso3166Alpha2('D', '1')).IsFalse();
    }

    [Test]
    public async Task Iso4217_MatchesTheReferenceLinter()
    {
        var vectors = Gs1FixtureLoader.LoadLinter("iso4217").Cases;
        var wellFormed = vectors.Where(v => v.Value.Length == 3 && v.Value.All(char.IsAsciiDigit)).ToArray();
        await Assert.That(wellFormed.Select(v => v.Value).Distinct().Count()).IsEqualTo(1000);
        foreach (var v in wellFormed)
            await Assert.That(Gs1CodeLists.IsIso4217(int.Parse(v.Value))).IsEqualTo(v.Pass).Because(v.Value);
        await Assert.That(Gs1CodeLists.IsIso4217(978)).IsTrue();
        await Assert.That(Gs1CodeLists.IsIso4217(1000)).IsFalse();
    }

    [Test]
    public async Task MediaType_MatchesTheReferenceLinter()
    {
        var vectors = Gs1FixtureLoader.LoadLinter("mediatype").Cases;
        var wellFormed = vectors.Where(v => v.Value.Length == 2 && v.Value.All(char.IsAsciiDigit)).ToArray();
        await Assert.That(wellFormed.Select(v => v.Value).Distinct().Count()).IsEqualTo(100);
        foreach (var v in wellFormed)
            await Assert.That(Gs1CodeLists.IsMediaType(int.Parse(v.Value))).IsEqualTo(v.Pass).Because(v.Value);
        await Assert.That(Gs1CodeLists.IsMediaType(100)).IsFalse();
    }

    [Test]
    public async Task PackageType_MatchesTheReferenceLinter()
    {
        var vectors = Gs1FixtureLoader.LoadLinter("packagetype").Cases;
        await Assert.That(vectors.Length).IsEqualTo(442);
        foreach (var v in vectors)
            await Assert.That(Gs1CodeLists.IsPackageType(v.Value)).IsEqualTo(v.Pass).Because(v.Value);
        var packageTypeCount = Gs1CodeLists.PackageTypeCount;
        await Assert.That(packageTypeCount).IsEqualTo(431);
        await Assert.That(Gs1CodeLists.IsPackageType("1A")).IsTrue();
        await Assert.That(Gs1CodeLists.IsPackageType("1a")).IsFalse();
        await Assert.That(Gs1CodeLists.IsPackageType("")).IsFalse();
        await Assert.That(Gs1CodeLists.IsPackageType("ZZZZZ")).IsFalse();
    }
}
