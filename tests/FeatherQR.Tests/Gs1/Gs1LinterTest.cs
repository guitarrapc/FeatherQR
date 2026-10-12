using FeatherQR.Gs1;
using FeatherQR.Gs1.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// Every linter the dictionary names, and the four character-set checks, against the reference linters' own
/// unit tests as imported under Fixtures/Gs1/syntax-dictionary/linters/: the managed rule gives the reference's
/// verdict on every vector. The two-digit-year dates are checked with a fixed year, since their leap-day rule
/// resolves YY through a window around the current year (General Specifications 7.12).
/// </summary>
public class Gs1LinterTest
{
    /// <summary>The year the reference's vectors were written for. Their verdicts do not change before 2050 (the earliest year at which a YY of 00 leaves the window).</summary>
    private const int Year = 2026;

    public static IEnumerable<Func<string>> Linters() => Enum.GetNames<Gs1Linter>().Select(l => (Func<string>)(() => l));

    [Test]
    [MethodDataSource(nameof(Linters))]
    public async Task Linter_GivesTheReferenceVerdictOnEveryVector(string name)
    {
        var linter = Enum.Parse<Gs1Linter>(name);
        var file = Gs1FixtureLoader.LoadLinter(linter.ToString().ToLowerInvariant());
        // The two GCP linters build every test value in a variable, so their files import no literal vector (PROVENANCE.md); Gcp_Rule covers them.
        if (linter is not (Gs1Linter.Gcppos1 or Gs1Linter.Gcppos2))
            await Assert.That(file.Cases.Length).IsGreaterThan(0);
        var wrong = new List<string>();
        foreach (var vector in file.Cases)
        {
            var kind = Gs1Linters.Check(linter, vector.Value, Year, out _, out _);
            if ((kind == Gs1ErrorKind.None) != vector.Pass)
                wrong.Add($"{Escape(vector.Value)}: reference {(vector.Pass ? "passes" : vector.Error)}, this {kind}");
        }
        await Assert.That(wrong).IsEmpty().Because($"{linter}: {wrong.Count} of {file.Cases.Length} vectors differ");
    }

    /// <summary>The GCP linters: at least four digits from the first (gcppos1) or the second (gcppos2) character, the shortest prefix GS1 allocates.</summary>
    [Test]
    [Arguments("Gcppos1", "1234", true)]
    [Arguments("Gcppos1", "9521234ABC", true)]
    [Arguments("Gcppos1", "123", false)]
    [Arguments("Gcppos1", "12A4", false)]
    [Arguments("Gcppos1", "", false)]
    [Arguments("Gcppos2", "01234", true)]
    [Arguments("Gcppos2", "A1234", true)]
    [Arguments("Gcppos2", "0123", false)]
    [Arguments("Gcppos2", "012A4", false)]
    public async Task Gcp_Rule(string name, string value, bool valid)
    {
        await Assert.That(Gs1Linters.Check(Enum.Parse<Gs1Linter>(name), value, Year, out _, out _) == Gs1ErrorKind.None).IsEqualTo(valid);
    }

    /// <summary>A failing linter names the kind its failure maps to: check digits, dates and times, or a value rule.</summary>
    [Test]
    [Arguments("Csum", "12345678901232", Gs1ErrorKind.InvalidCheckDigit)]
    [Arguments("Csumalpha", "1987654Ad4X4bL5ttr2310c2X", Gs1ErrorKind.InvalidCheckDigit)]
    [Arguments("Yymmd0", "251331", Gs1ErrorKind.InvalidDate)]
    [Arguments("Yymmdd", "251200", Gs1ErrorKind.InvalidDate)]
    [Arguments("Yyyymmdd", "20250229", Gs1ErrorKind.InvalidDate)]
    [Arguments("Hhmi", "2400", Gs1ErrorKind.InvalidDate)]
    [Arguments("Hh", "24", Gs1ErrorKind.InvalidDate)]
    [Arguments("Mi", "60", Gs1ErrorKind.InvalidDate)]
    [Arguments("Ss", "60", Gs1ErrorKind.InvalidDate)]
    [Arguments("Iso3166", "987", Gs1ErrorKind.InvalidValue)]
    [Arguments("Iso4217", "987", Gs1ErrorKind.InvalidValue)]
    [Arguments("Yesno", "2", Gs1ErrorKind.InvalidValue)]
    [Arguments("Iban", "BE71096123456760", Gs1ErrorKind.InvalidValue)]
    [Arguments("Couponcode", "a12345612345611110123", Gs1ErrorKind.InvalidValue)]
    public async Task Linter_FailureKind(string name, string value, Gs1ErrorKind expected)
    {
        var linter = Enum.Parse<Gs1Linter>(name);
        await Assert.That(Gs1Linters.Check(linter, value, Year, out _, out _)).IsEqualTo(expected);
    }

    /// <summary>A failing linter points inside the value: the offset and length lie within it.</summary>
    [Test]
    [MethodDataSource(nameof(Linters))]
    public async Task Linter_FailurePointsInsideTheValue(string name)
    {
        var linter = Enum.Parse<Gs1Linter>(name);
        var file = Gs1FixtureLoader.LoadLinter(linter.ToString().ToLowerInvariant());
        foreach (var vector in file.Cases.Where(v => !v.Pass))
        {
            var kind = Gs1Linters.Check(linter, vector.Value, Year, out var offset, out var length);
            if (kind == Gs1ErrorKind.None)
                continue;
            await Assert.That(offset).IsGreaterThanOrEqualTo(0).Because(Escape(vector.Value));
            await Assert.That(length).IsGreaterThanOrEqualTo(0).Because(Escape(vector.Value));
            await Assert.That(offset + length).IsLessThanOrEqualTo(Math.Max(vector.Value.Length, 1)).Because(Escape(vector.Value));
        }
    }

    /// <summary>The leap-day rule follows the window: YY resolves to the century that puts it within -49 to +50 years of the given year.</summary>
    [Test]
    [Arguments("000229", 2026, true)]   // 2000, a leap year
    [Arguments("000229", 2051, false)]  // 2100, not a leap year
    [Arguments("240229", 2026, true)]
    [Arguments("250229", 2026, false)]
    [Arguments("960229", 2026, true)]   // 1996
    [Arguments("760229", 2026, true)]   // 2076
    [Arguments("770228", 2026, true)]
    public async Task Yymmdd_LeapDayFollowsTheYearWindow(string value, int year, bool valid)
    {
        await Assert.That(Gs1Linters.Check(Gs1Linter.Yymmdd, value, year, out _, out _) == Gs1ErrorKind.None).IsEqualTo(valid);
        await Assert.That(Gs1Linters.Check(Gs1Linter.Yymmd0, value, year, out _, out _) == Gs1ErrorKind.None).IsEqualTo(valid);
    }

    [Test]
    [Arguments("cset82", "Cset82")]
    [Arguments("cset39", "Cset39")]
    [Arguments("cset64", "Cset64")]
    [Arguments("csetnumeric", "Numeric")]
    public async Task CharacterSet_GivesTheReferenceVerdictOnEveryVector(string file, string typeName)
    {
        var type = Enum.Parse<Gs1ComponentType>(typeName);
        var vectors = Gs1FixtureLoader.LoadLinter(file).Cases;
        var wrong = new List<string>();
        foreach (var vector in vectors)
        {
            var offset = Gs1Charsets.FirstInvalid(type, vector.Value, out _);
            if ((offset < 0) != vector.Pass)
                wrong.Add($"{Escape(vector.Value)}: reference {(vector.Pass ? "passes" : vector.Error)}, this {(offset < 0 ? "passes" : $"fails at {offset}")}");
        }
        await Assert.That(wrong).IsEmpty().Because($"{file}: {wrong.Count} of {vectors.Length} vectors differ");
    }

    private static string Escape(string value) => value.Replace("\u001D", "<GS>").Replace("\n", "\\n").Replace("\r", "\\r");
}
