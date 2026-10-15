using System.Diagnostics;
using FeatherQR.Gs1;
using FeatherQR.Gs1.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="Gs1ElementString.TryValidate"/> against the Syntax Engine's verdicts in the GS1 corpus: every element
/// string the engine accepts is accepted, every one it refuses is refused with an error of the same class, and the
/// plan's equivalence classes come out as the API promises (unknown AIs refused, a redundant or trailing separator
/// tolerated, a leading or doubled one refused, the pairings per symbol). Bracketed fixtures are read through the
/// engine's own element string where it accepted them and through a test-side join where it refused them.
/// </summary>
public class Gs1ElementStringValidateTest
{
    private const int Year = 2026;

    /// <summary>The engine's error message classes and the kind this library reports for them.</summary>
    private static readonly (string Prefix, Gs1ErrorKind[] Kinds)[] KindMap =
    [
        ("Required AIs for AI", [Gs1ErrorKind.MissingRequiredAi]),
        ("Serial component must be present", [Gs1ErrorKind.MissingRequiredAi]),
        ("It is invalid to pair AI", [Gs1ErrorKind.ExcludedAiPair]),
        ("Multiple instances of AI", [Gs1ErrorKind.RepeatedAi]),
        ("Unrecognised AI", [Gs1ErrorKind.UnknownAi]),
        ("No known AI is a prefix of", [Gs1ErrorKind.UnknownAi, Gs1ErrorKind.UnexpectedSeparator, Gs1ErrorKind.EmptyValue]),
        ("AI (", [Gs1ErrorKind.ValueTooShort, Gs1ErrorKind.ValueTooLong, Gs1ErrorKind.EmptyValue, Gs1ErrorKind.InvalidCharacter, Gs1ErrorKind.InvalidCheckDigit, Gs1ErrorKind.InvalidDate, Gs1ErrorKind.InvalidValue]),
    ];

    /// <summary>
    /// The cases as element strings. A bracketed input the engine refused is joined on the test side, and kept
    /// only where the join reads back into the same AIs (a value of the wrong length for a predefined AI would
    /// otherwise split into other elements and test nothing). The engine's error text describes the bracketed
    /// form, so the error class is compared for element string inputs only.
    /// </summary>
    public static IEnumerable<Func<(string Id, string ElementString, bool Valid, string? EngineError, bool CompareKind)>> Cases()
    {
        foreach (var c in Gs1FixtureLoader.EnumerateCases())
        {
            string? elementString;
            var compareKind = false;
            switch (c.InputKind)
            {
                case Gs1InputKind.ElementString:
                    elementString = c.Input;
                    compareKind = true;
                    break;
                case Gs1InputKind.Bracketed when c.SyntaxEngine.Valid:
                    elementString = c.SyntaxEngine.ElementString;
                    break;
                case Gs1InputKind.Bracketed:
                    var pairs = Gs1TestText.ParseBracketed(c.Input);
                    elementString = pairs is null ? null : Gs1TestText.ToElementString(pairs);
                    if (elementString is not null && !ReadsBackAs(elementString, pairs!))
                        elementString = null;
                    break;
                default:
                    continue;
            }
            if (elementString is null)
                continue;
            var captured = (c.Id, elementString, c.SyntaxEngine.Valid, c.SyntaxEngine.Error, compareKind);
            yield return () => captured;
        }
    }

    private static bool ReadsBackAs(string elementString, List<(string Ai, string Value)> pairs)
    {
        var ais = new List<string>();
        foreach (var element in Gs1ElementString.EnumerateElements(elementString))
            ais.Add(element.Ai.ToString());
        return ais.SequenceEqual(pairs.Select(p => p.Ai));
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task TryValidate_GivesTheSyntaxEngineVerdict((string Id, string ElementString, bool Valid, string? EngineError, bool CompareKind) c)
    {
        var valid = Gs1Validator.TryValidate(c.ElementString, Year, out var error);
        await Assert.That(valid).IsEqualTo(c.Valid).Because($"{c.Id}: engine {(c.Valid ? "accepts" : c.EngineError)}, this {(valid ? "accepts" : error.ToString())}");
        if (valid)
        {
            await Assert.That(error).IsEqualTo(default(Gs1Error));
            return;
        }
        await Assert.That(error.Kind).IsNotEqualTo(Gs1ErrorKind.None);
        await Assert.That(error.Offset).IsGreaterThanOrEqualTo(0);
        await Assert.That(error.Offset + error.Length).IsLessThanOrEqualTo(Math.Max(c.ElementString.Length, 1));
        if (!c.CompareKind)
            return;
        var map = KindMap.FirstOrDefault(m => c.EngineError!.StartsWith(m.Prefix, StringComparison.Ordinal));
        if (map.Kinds is not null)
            await Assert.That(map.Kinds).Contains(error.Kind).Because($"{c.Id}: engine says {c.EngineError}, this {error}");
    }

    [Test]
    public async Task Corpus_CoversEveryKindButDestinationTooSmall()
    {
        var seen = new HashSet<Gs1ErrorKind>();
        foreach (var make in Cases())
        {
            var c = make();
            if (!Gs1Validator.TryValidate(c.ElementString, Year, out var error))
                seen.Add(error.Kind);
        }
        var expected = new[]
        {
            Gs1ErrorKind.UnexpectedSeparator, Gs1ErrorKind.UnknownAi, Gs1ErrorKind.EmptyValue, Gs1ErrorKind.ValueTooShort, Gs1ErrorKind.ValueTooLong,
            Gs1ErrorKind.InvalidCharacter, Gs1ErrorKind.InvalidCheckDigit, Gs1ErrorKind.InvalidDate, Gs1ErrorKind.InvalidValue,
            Gs1ErrorKind.MissingRequiredAi, Gs1ErrorKind.ExcludedAiPair, Gs1ErrorKind.RepeatedAi,
        };
        await Assert.That(expected.Where(k => !seen.Contains(k))).IsEmpty();
    }

    [Test]
    [Arguments("", false, Gs1ErrorKind.InvalidText)]
    [Arguments("0109520123456788", true, Gs1ErrorKind.None)]
    [Arguments("0109520123456788\u001D", true, Gs1ErrorKind.None)]
    [Arguments("0109520123456788\u001D17251231", true, Gs1ErrorKind.None)]
    [Arguments("\u001D0109520123456788", false, Gs1ErrorKind.UnexpectedSeparator)]
    [Arguments("010952012345678810ABC\u001D\u001D17251231", false, Gs1ErrorKind.UnexpectedSeparator)]
    [Arguments("010952012345678810ABC\u001D", true, Gs1ErrorKind.None)]
    [Arguments("010952012345678810ABC17251231", true, Gs1ErrorKind.None)]
    // Prefix 40 gives three-digit AIs (Table 7-5), so this is (400) with the value 0ABC, not an unknown (4000).
    [Arguments("0109520123456788" + "4000ABC", true, Gs1ErrorKind.None)]
    [Arguments("0109520123456788" + "89ABC", false, Gs1ErrorKind.UnknownAi)]
    [Arguments("0109520123456788" + Gs1UnassignedAi.Ai234 + "ABC", false, Gs1ErrorKind.UnknownAi)]
    [Arguments("0109520123456788" + "10", false, Gs1ErrorKind.EmptyValue)]
    [Arguments("01095201234567", false, Gs1ErrorKind.ValueTooShort)]
    [Arguments("010952012345678A", false, Gs1ErrorKind.InvalidCharacter)]
    [Arguments("0109520123456789", false, Gs1ErrorKind.InvalidCheckDigit)]
    [Arguments("0109520123456788" + "17251331", false, Gs1ErrorKind.InvalidDate)]
    [Arguments("0109520123456788" + "10ABCDEFGHIJKLMNOPQRSTU", false, Gs1ErrorKind.ValueTooLong)]
    [Arguments("10ABC", false, Gs1ErrorKind.MissingRequiredAi)]
    [Arguments("0109520123456788" + "21ABC\u001D" + "235XYZ", false, Gs1ErrorKind.ExcludedAiPair)]
    [Arguments("0109520123456788" + "10ABC\u001D" + "10DEF", false, Gs1ErrorKind.RepeatedAi)]
    [Arguments("0109520123456788" + "10ABC\u001D" + "10ABC", true, Gs1ErrorKind.None)]
    [Arguments("0109520123456788" + "8040123456789012345", false, Gs1ErrorKind.MissingRequiredAi)]
    [Arguments("0109520123456788" + "21ABC\u001D" + "8040123456789012345", true, Gs1ErrorKind.None)]
    [Arguments("25395260640000280000011\u001D" + "8030QUJD", true, Gs1ErrorKind.None)]
    [Arguments("2539526064000028\u001D" + "8030QUJD", false, Gs1ErrorKind.MissingRequiredAi)]
    [Arguments("0109520123456788" + "10Aé", false, Gs1ErrorKind.InvalidCharacter)]
    [Arguments("1", false, Gs1ErrorKind.UnknownAi)]
    [Arguments("0", false, Gs1ErrorKind.UnknownAi)]
    // A digital signature (8030) needs its key serialised whichever requisite alternative holds (the engine's own rule, validateDigSigRequiresSerialisedKey).
    [Arguments("0109520123456788" + "21ABC\u001D" + "2539526064000028\u001D" + "8030QUJD", false, Gs1ErrorKind.MissingRequiredAi)]
    [Arguments("0109520123456788" + "21ABC\u001D" + "25395260640000280000011\u001D" + "8030QUJD", true, Gs1ErrorKind.None)]
    [Arguments("00195201234567891232" + "2559526064000028\u001D" + "8030QUJD", false, Gs1ErrorKind.MissingRequiredAi)]
    [Arguments("0109520123456788" + "21ABC\u001D" + "800309520123456788\u001D" + "8030QUJD", false, Gs1ErrorKind.MissingRequiredAi)]
    // An IBAN of ten characters is too short (lint_iban.c refuses data_len <= 10); a real one passes.
    [Arguments("4159526064000028" + "8020REF1\u001D" + "8007GB18000000", false, Gs1ErrorKind.InvalidValue)]
    [Arguments("4159526064000028" + "8020REF1\u001D" + "8007GB82WEST12345698765432", true, Gs1ErrorKind.None)]
    // A separator inside a predefined-length window cuts the value short, as the engine reads it.
    [Arguments("01" + "0952012345678\u001D" + "10ABC", false, Gs1ErrorKind.ValueTooShort)]
    // The engine's order between the pairing rules: exclusions, then requisites, then repeats.
    [Arguments("0109520123456788" + "10A\u001D" + "10B\u001D" + "3712", false, Gs1ErrorKind.ExcludedAiPair)]
    [Arguments("10A\u001D" + "10B", false, Gs1ErrorKind.MissingRequiredAi)]
    public async Task TryValidate_EquivalenceClasses(string elementString, bool valid, Gs1ErrorKind kind)
    {
        await Assert.That(Gs1Validator.TryValidate(elementString, Year, out var error)).IsEqualTo(valid);
        await Assert.That(error.Kind).IsEqualTo(kind);
    }

    /// <summary>The case list passes through <see cref="ReadsBackAs"/>, which uses the split under test, so its size is pinned: a split regression that dropped cases would otherwise pass in silence.</summary>
    [Test]
    public async Task Cases_CountIsPinned()
    {
        var cases = Cases().Select(make => make()).ToList();
        await Assert.That(cases.Count).IsEqualTo(962);
        await Assert.That(cases.Count(c => c.CompareKind)).IsEqualTo(96);
    }

    /// <summary>The error points at the element or character at fault, in the input's coordinates.</summary>
    [Test]
    public async Task TryValidate_ErrorPointsAtTheFault()
    {
        Gs1Validator.TryValidate("0109520123456788" + "17251331", Year, out var date);
        await Assert.That((date.Kind, date.Offset, date.Length)).IsEqualTo((Gs1ErrorKind.InvalidDate, 20, 2));

        Gs1Validator.TryValidate("0109520123456788" + "10AB CD", Year, out var space);
        await Assert.That((space.Kind, space.Offset, space.Length)).IsEqualTo((Gs1ErrorKind.InvalidCharacter, 20, 1));

        Gs1Validator.TryValidate("0109520123456788" + "10ABC\u001D" + "10DEF", Year, out var repeated);
        await Assert.That((repeated.Kind, repeated.Offset, repeated.Length)).IsEqualTo((Gs1ErrorKind.RepeatedAi, 22, 5));

        // The same repeat before another element: whether an AI stands twice is kept over every element, not the last.
        Gs1Validator.TryValidate("0109520123456788" + "10ABC\u001D" + "10DEF\u001D" + "17251231", Year, out var repeatedBefore);
        await Assert.That((repeatedBefore.Kind, repeatedBefore.Offset, repeatedBefore.Length)).IsEqualTo((Gs1ErrorKind.RepeatedAi, 22, 5));

        Gs1Validator.TryValidate("0109520123456789", Year, out var check);
        await Assert.That((check.Kind, check.Offset, check.Length)).IsEqualTo((Gs1ErrorKind.InvalidCheckDigit, 15, 1));

        Gs1Validator.TryValidate("01" + "0952012345678\u001D" + "10ABC", Year, out var cut);
        await Assert.That((cut.Kind, cut.Offset, cut.Length)).IsEqualTo((Gs1ErrorKind.ValueTooShort, 2, 13));

        Gs1Validator.TryValidate("0109520123456788" + "310", Year, out var aiCut);
        await Assert.That((aiCut.Kind, aiCut.Offset, aiCut.Length)).IsEqualTo((Gs1ErrorKind.UnknownAi, 16, 3));

        Gs1Validator.TryValidate("0109520123456788" + "31A3000001", Year, out var aiLetter);
        await Assert.That((aiLetter.Kind, aiLetter.Offset, aiLetter.Length)).IsEqualTo((Gs1ErrorKind.UnknownAi, 16, 4));

        // An AI the table does not know is refused at its digits alone, whether it comes first or between known
        // elements. The first one's value would pass the checks of (00), which an unknown element's entry falls back to.
        Gs1Validator.TryValidate(Gs1UnassignedAi.Ai234 + "195201234567891232\u001D" + "0109520123456788", Year, out var unknownFirst);
        await Assert.That((unknownFirst.Kind, unknownFirst.Offset, unknownFirst.Length)).IsEqualTo((Gs1ErrorKind.UnknownAi, 0, 3));
        Gs1Validator.TryValidate("0109520123456788" + Gs1UnassignedAi.Ai234 + "ABC\u001D" + "17251231", Year, out var unknownBetween);
        await Assert.That((unknownBetween.Kind, unknownBetween.Offset, unknownBetween.Length)).IsEqualTo((Gs1ErrorKind.UnknownAi, 16, 3));

        // An unknown AI under a predefined-length prefix is read by that length (Table 7-6), so a short one is cut, not unknown.
        Gs1Validator.TryValidate(Gs1UnassignedAi.Ai3106 + "12345", Year, out var unknownCut);
        await Assert.That((unknownCut.Kind, unknownCut.Offset, unknownCut.Length)).IsEqualTo((Gs1ErrorKind.ValueTooShort, 4, 5));

        // The key that lacks its serial component is the place, where the engine points too.
        Gs1Validator.TryValidate("0109520123456788" + "21ABC\u001D" + "2539526064000028\u001D" + "8030QUJD", Year, out var serial);
        await Assert.That((serial.Kind, serial.Offset, serial.Length)).IsEqualTo((Gs1ErrorKind.MissingRequiredAi, 22, 16));
    }

    /// <summary>
    /// A two-digit year is read in the century the year given puts it in. 29 February of year 00 is a date of 2000,
    /// a leap year, when the checks run in 2026, and of 2100, which is not one, when they run in 2051. The two
    /// answers differ, so a check that took its year from the clock, the same for both calls, would get one wrong.
    /// The date comes second in one text and first in the other, since the first element is checked by a call of its
    /// own, before the loop over the rest.
    /// </summary>
    [Test]
    [Arguments("0109520123456788" + "17000229", 22)]
    [Arguments("17000229" + "0109520123456788", 6)]
    public async Task TryValidate_TwoDigitYear_IsReadAgainstTheYearGiven(string text, int dayOffset)
    {
        await Assert.That(Gs1Validator.TryValidate(text, 2026, out var accepted)).IsTrue().Because(accepted.ToString());
        await Assert.That(Gs1Validator.TryValidate(text, 2051, out var error)).IsFalse();
        await Assert.That((error.Kind, error.Offset, error.Length)).IsEqualTo((Gs1ErrorKind.InvalidDate, dayOffset, 2));
    }

    /// <summary>
    /// Valid data and its (AI) text, in five shapes: an AI that repeats its value, a key with exclusions before an
    /// attribute that requires it, a price whose requisite names the measures by pattern, the attribute again with
    /// its key only at the very end, all of 40,000 elements, and two AIs that each repeat, the second only after
    /// 40,000 elements of the first, 80,000 in all.
    /// </summary>
    private static (string ElementString, string Hri, int Elements) ManyElements(string shape)
    {
        const string Gtin = "09520123456788";
        return shape switch
        {
            "one AI repeating" => (
                "01" + Gtin + string.Concat(Enumerable.Repeat("91A\u001D", 39_999)),
                "(01)" + Gtin + string.Concat(Enumerable.Repeat("(91)A", 39_999)), 40_000),
            "a key and its batch" => (
                string.Concat(Enumerable.Repeat("01" + Gtin + "10ABC\u001D", 20_000)),
                string.Concat(Enumerable.Repeat("(01)" + Gtin + "(10)ABC", 20_000)), 40_000),
            "a weighed label" => (
                string.Concat(Enumerable.Repeat("01" + Gtin + "3103000195" + "39220299\u001D" + "17251231", 10_000)),
                string.Concat(Enumerable.Repeat("(01)" + Gtin + "(3103)000195" + "(3922)0299" + "(17)251231", 10_000)), 40_000),
            "the key last" => (
                string.Concat(Enumerable.Repeat("10ABC\u001D", 39_999)) + "01" + Gtin,
                string.Concat(Enumerable.Repeat("(10)ABC", 39_999)) + "(01)" + Gtin, 40_000),
            "one AI after another" => (
                "01" + Gtin + string.Concat(Enumerable.Repeat("91A\u001D", 40_000)) + string.Concat(Enumerable.Repeat("92B\u001D", 39_999)),
                "(01)" + Gtin + string.Concat(Enumerable.Repeat("(91)A", 40_000)) + string.Concat(Enumerable.Repeat("(92)B", 39_999)), 80_000),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown shape"),
        };
    }

    /// <summary>
    /// The checks take time in proportion to the number of elements, in each form that runs them: the element
    /// string, the (AI) text, and the element string on its way to a URI. A rule that scans the text for each element,
    /// for an AI it needs or for an earlier value to compare with, reads more than a billion elements in one of these
    /// shapes at least, which takes several times the five seconds allowed. A scan for a requisite meets the key late
    /// only when the key comes last, and a scan for a first value meets it late only for the second of two AIs, which
    /// is why that shape is twice as long. The test runs apart from the other tests of its process, since the bound
    /// is on the clock.
    /// </summary>
    [Test]
    [NotInParallel]
    [Arguments("one AI repeating")]
    [Arguments("a key and its batch")]
    [Arguments("a weighed label")]
    [Arguments("the key last")]
    [Arguments("one AI after another")]
    public async Task Checks_OnManyElements_TakeUnderFiveSeconds(string name)
    {
        var shape = ManyElements(name);
        var elements = 0;
        foreach (var _ in Gs1ElementString.EnumerateElements(shape.ElementString))
            elements++;
        await Assert.That(elements).IsEqualTo(shape.Elements);

        var elementString = new char[shape.ElementString.Length];
        var uri = new char[256];
        var clock = Stopwatch.StartNew();
        var valid = Gs1ElementString.TryValidate(shape.ElementString, out var error);
        var validated = clock.Elapsed;
        clock.Restart();
        var read = Gs1ElementString.TryFromHri(shape.Hri, elementString, out var elementStringLength, out var hriError);
        var readFromHri = clock.Elapsed;
        clock.Restart();
        var built = Gs1ElementString.TryToDigitalLink(shape.ElementString, uri, out var uriLength, out var uriError);
        var builtUri = clock.Elapsed;

        await Assert.That(valid).IsTrue().Because(error.ToString());
        await Assert.That(read).IsTrue().Because(hriError.ToString());
        await Assert.That(new string(elementString, 0, elementStringLength)).IsEqualTo(shape.ElementString.TrimEnd('\u001D'));
        await Assert.That(built).IsTrue().Because(uriError.ToString());
        await Assert.That(new string(uri, 0, uriLength)).StartsWith("https://id.gs1.org/01/09520123456788");
        var bound = TimeSpan.FromSeconds(5);
        var times = $"TryValidate {validated.TotalMilliseconds:F0} ms, TryFromHri {readFromHri.TotalMilliseconds:F0} ms, TryToDigitalLink {builtUri.TotalMilliseconds:F0} ms";
        await Assert.That(validated).IsLessThan(bound).Because(times);
        await Assert.That(readFromHri).IsLessThan(bound).Because(times);
        await Assert.That(builtUri).IsLessThan(bound).Because(times);
    }

    /// <summary>The public form uses the current year and otherwise is the internal one.</summary>
    [Test]
    public async Task TryValidate_PublicForm()
    {
        await Assert.That(Gs1ElementString.TryValidate("0109520123456788" + "10ABC", out var error)).IsTrue();
        await Assert.That(error).IsEqualTo(default(Gs1Error));
        await Assert.That(Gs1ElementString.TryValidate("10ABC", out error)).IsFalse();
        await Assert.That(error.Kind).IsEqualTo(Gs1ErrorKind.MissingRequiredAi);
        await Assert.That(error.ToString()).Contains("MissingRequiredAi at 0");
    }
}
