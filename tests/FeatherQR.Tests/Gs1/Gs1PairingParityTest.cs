using System.Diagnostics;
using System.Text;
using FeatherQR.Gs1;
using FeatherQR.Gs1.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="Gs1Validator.CheckPairings"/> against <see cref="Gs1PairingReference"/>: on every text that splits, the
/// same answer, and for a refusal the same kind at the same place. On text that does not split the library gives the
/// splitter's first error, which the tests take as the expected answer there. The rules read the split, the AIs,
/// whether two values of one AI are equal and how long a key's value is, so the values here are filler of one
/// repeated character: <c>1</c> everywhere, and <c>2</c> where a value has to differ.
/// </summary>
public class Gs1PairingParityTest
{
    private const int Seed = 20261014;

    /// <summary>Every AI of the table, in key order.</summary>
    private static readonly string[] Ais = AllAis();

    /// <summary>AIs that the pairing rules of the common labels name, so that texts drawn from them meet and miss each rule often.</summary>
    private static readonly string[] RulePool =
    [
        "00", "01", "02", "10", "21", "22", "37", "30", "3103", "3104", "3922", "253", "255", "8003", "8030", "8006", "8026", "8017", "8018", "8020", "415", "8004", "91", "17",
    ];

    /// <summary>Value lengths for an AI that is not of predefined length. 13 and 14 are the mandatory lengths of the keys the serialised key rule reads, so both sides of that rule occur.</summary>
    private static readonly int[] Lengths = [1, 13, 14, 15];

    private static readonly Lazy<string[]> Compatible = new(BuildCompatibleAis);

    /// <summary>The AIs of <see cref="Compatible"/> that need no AI beside them but (00) and (01), so any of them may follow those two.</summary>
    private static readonly Lazy<string[]> BesideTheKeys = new(() => Compatible.Value.Where(ai => Gs1PairingReference.CheckPairings(Keys + Element(ai), out _)).ToArray());

    private static string Keys => Element("00") + Element("01");

    private static string[] AllAis()
    {
        var ais = new List<string>();
        for (var i = 0; i < Gs1AiTable.EntryCount; i++)
        {
            var entry = Gs1AiTable.EntryAt(i);
            var first = entry.FirstAi.ToString();
            var last = int.Parse(entry.LastAi.ToString());
            for (var value = int.Parse(first); value <= last; value++)
                ais.Add(value.ToString().PadLeft(first.Length, '0'));
        }
        return ais.ToArray();
    }

    /// <summary>One element as the splitter reads it: the AI, then a value of its predefined length, or a value of <paramref name="length"/> characters and a separator.</summary>
    private static string Element(string ai, char fill = '1', int length = 1)
    {
        if (!Gs1AiTable.TryFind(ai, out var entry))
            throw new InvalidOperationException($"({ai}) is not in the table");
        if (entry.IsPredefinedLength && Gs1Splitter.TryGetFixedLength(entry, out var fixedLength))
            return ai + new string(fill, fixedLength);
        return ai + new string(fill, length) + Gs1TestText.Gs;
    }

    /// <summary>
    /// A large set of AIs that may stand together: one AI of each entry, less the element the reference refuses,
    /// until it refuses none. Only a set like this reaches the check of repeated AIs with many AIs in the text,
    /// since an exclusion or a missing requisite is reported first.
    /// </summary>
    private static string[] BuildCompatibleAis()
    {
        var ais = new List<string>();
        for (var i = 0; i < Gs1AiTable.EntryCount; i++)
            ais.Add(Gs1AiTable.EntryAt(i).FirstAi.ToString());
        while (true)
        {
            var text = string.Concat(ais.Select(ai => Element(ai)));
            if (Gs1PairingReference.CheckPairings(text, out var error))
                return ais.ToArray();
            // Every refusal of these rules starts at the AI of the element it refuses.
            ais.Remove(text.Substring(error.Offset, Gs1AiTable.AiLength(text[error.Offset], text[error.Offset + 1])));
        }
    }

    private static string Show(string text) => text.Replace(Gs1TestText.Gs, '|');

    /// <summary>The splitter's first error in the text, or <c>null</c> where the whole text splits.</summary>
    private static Gs1Error? SplitError(string text)
    {
        var position = 0;
        while (position < text.Length)
        {
            if (!Gs1Splitter.TryNext(text, ref position, out _, out var error))
                return error;
        }
        return null;
    }

    /// <summary><c>null</c> where the library gives the expected answer, and what each said where it does not.</summary>
    private static string? Difference(string text) => Difference(text, out _);

    /// <summary>As <see cref="Difference(string)"/>, with the expected error (<see cref="Gs1ErrorKind.None"/> where the text is accepted).</summary>
    private static string? Difference(string text, out Gs1Error expectedError)
    {
        bool expected;
        if (SplitError(text) is { } splitError)
        {
            expected = false;
            expectedError = splitError;
        }
        else
        {
            expected = Gs1PairingReference.CheckPairings(text, out expectedError);
        }
        var actual = Gs1Validator.CheckPairings(text, out var actualError);
        if (expected == actual && expectedError.Equals(actualError))
            return null;
        return $"\"{Show(text)}\": reference {(expected ? "accepts" : expectedError.ToString())}, library {(actual ? "accepts" : actualError.ToString())}";
    }

    private static string Counts(Dictionary<Gs1ErrorKind, int> outcomes) => string.Join(", ", outcomes.OrderBy(o => o.Key).Select(o => $"{o.Key} {o.Value}"));

    [Test]
    public async Task Ais_AreEveryAiOfTheTable()
    {
        await Assert.That(Ais.Length).IsEqualTo(Gs1AiTable.AiCount);
        await Assert.That(Ais.Distinct().Count()).IsEqualTo(Ais.Length);
        await Assert.That(RulePool.Where(ai => !Ais.Contains(ai))).IsEmpty();
    }

    /// <summary>
    /// Every input of the corpus, whatever its kind, and every element string the corpus tests are run on. The
    /// corpus reaches each pairing rule, and many of its texts do not split, so both answers are held.
    /// </summary>
    [Test]
    public async Task CheckPairings_MatchesReference_OnTheCorpus()
    {
        var texts = Gs1FixtureLoader.EnumerateCases().Select(c => c.Input)
            .Concat(Gs1ElementStringValidateTest.Cases().Select(make => make().ElementString))
            .Distinct().ToArray();
        var differences = new List<string>();
        var kinds = new HashSet<Gs1ErrorKind>();
        var accepted = 0;
        var unsplit = 0;
        foreach (var text in texts)
        {
            if (Difference(text, out var error) is { } difference)
                differences.Add(difference);
            if (SplitError(text) is not null)
                unsplit++;
            else if (error.Kind == Gs1ErrorKind.None)
                accepted++;
            else
                kinds.Add(error.Kind);
        }

        await Assert.That(differences).IsEmpty();
        await Assert.That(texts.Length).IsGreaterThan(900);
        await Assert.That(unsplit).IsGreaterThan(100);
        await Assert.That(accepted).IsGreaterThan(100);
        await Assert.That(kinds).Contains(Gs1ErrorKind.ExcludedAiPair);
        await Assert.That(kinds).Contains(Gs1ErrorKind.MissingRequiredAi);
        await Assert.That(kinds).Contains(Gs1ErrorKind.RepeatedAi);
    }

    /// <summary>
    /// Text that stops splitting after an excluded pair. The reference reports the pair, since it asks each element's
    /// exclusions before it reads on to the fault. The library reports the fault, since it reads every AI into its
    /// set first. No library path gives <c>CheckPairings</c> such text: its one caller in the library, the (AI) text
    /// builder, passes the element string it built, which always splits, and <see cref="Gs1Validator.TryValidate"/>
    /// refuses the fault itself before any rule between elements.
    /// </summary>
    [Test]
    [Arguments("0109520123456788371\u001D\u001D", Gs1ErrorKind.UnexpectedSeparator, 20, 1)]
    [Arguments("0109520123456788371\u001D9", Gs1ErrorKind.UnknownAi, 20, 1)]
    [Arguments("0109520123456788" + "0209520123456788" + "011", Gs1ErrorKind.ValueTooShort, 34, 1)]
    public async Task CheckPairings_TextThatStopsSplittingAfterAnExcludedPair_GivesTheSplittersError(string text, Gs1ErrorKind kind, int offset, int length)
    {
        var splitterError = new Gs1Error(kind, offset, length);
        await Assert.That(SplitError(text)).IsEqualTo(splitterError);
        await Assert.That(Gs1Validator.CheckPairings(text, out var error)).IsFalse();
        await Assert.That(error).IsEqualTo(splitterError);
        await Assert.That(Gs1Validator.TryValidate(text, 2026, out var validated)).IsFalse();
        await Assert.That(validated).IsEqualTo(splitterError);
        await Assert.That(Gs1PairingReference.CheckPairings(text, out var reference)).IsFalse();
        await Assert.That(reference.Kind).IsEqualTo(Gs1ErrorKind.ExcludedAiPair);
    }

    /// <summary>
    /// Each AI alone and every ordered pair of AIs, the second with another value, so a pair of one AI is a repeat
    /// with a value that differs. This asks every exclusion and every requisite of the table of each AI it can
    /// name, in both orders of the two elements.
    /// </summary>
    [Test]
    public async Task CheckPairings_MatchesReference_OnEveryPairOfAis()
    {
        var differences = new List<string>();
        var outcomes = new Dictionary<Gs1ErrorKind, int>();
        foreach (var first in Ais)
        {
            if (Difference(Element(first)) is { } alone)
                differences.Add(alone);
            var head = Element(first);
            foreach (var second in Ais)
            {
                var text = head + Element(second, '2');
                if (Difference(text, out var error) is { } difference && differences.Count < 20)
                    differences.Add(difference);
                outcomes[error.Kind] = outcomes.GetValueOrDefault(error.Kind) + 1;
            }
        }

        await Assert.That(differences).IsEmpty();
        await Assert.That(outcomes.GetValueOrDefault(Gs1ErrorKind.None)).IsGreaterThan(1000).Because(Counts(outcomes));
        await Assert.That(outcomes.GetValueOrDefault(Gs1ErrorKind.ExcludedAiPair)).IsGreaterThan(1000).Because(Counts(outcomes));
        await Assert.That(outcomes.GetValueOrDefault(Gs1ErrorKind.MissingRequiredAi)).IsGreaterThan(1000).Because(Counts(outcomes));
        await Assert.That(outcomes.GetValueOrDefault(Gs1ErrorKind.RepeatedAi)).IsGreaterThan(10).Because(Counts(outcomes));
    }

    /// <summary>
    /// Each AI whose rule names others by a pattern with wildcards, beside each AI the pattern matches and each AI
    /// within a hundred keys of them: after a GTIN, and in the other order without one. A pattern such as
    /// <c>31nn</c> matches a hundred keys, which lie in more than one word of the validator's set, so each AI in
    /// the range has to be seen there and each AI beside the range not. The GTIN meets the other requisite of the
    /// AIs that have two, so that the one with the pattern decides.
    /// </summary>
    [Test]
    public async Task CheckPairings_MatchesReference_OnEveryAiAPatternNames()
    {
        var differences = new List<string>();
        var outcomes = new Dictionary<Gs1ErrorKind, int>();
        var owners = 0;
        foreach (var owner in Ais)
        {
            Gs1AiTable.TryFind(owner, out var entry);
            var patterns = new List<Gs1AiTable.Gs1AiPattern>();
            for (var x = 0; x < entry.ExclusionCount; x++)
                patterns.Add(entry.Exclusion(x));
            for (var g = 0; g < entry.RequisiteGroupCount; g++)
            {
                var group = entry.RequisiteGroup(g);
                for (var a = 0; a < group.AlternativeCount; a++)
                {
                    for (var m = 0; m < group.Alternative(a).MemberCount; m++)
                        patterns.Add(group.Alternative(a).Member(m));
                }
            }
            patterns.RemoveAll(pattern => pattern.FirstKey == pattern.LastKey);
            if (patterns.Count == 0)
                continue;
            owners++;

            foreach (var named in Ais)
            {
                var key = named.Length * 10000 + int.Parse(named);
                if (!patterns.Any(pattern => key >= pattern.FirstKey - 100 && key <= pattern.LastKey + 100))
                    continue;
                foreach (var text in new[] { Element("01") + Element(owner) + Element(named), Element(named) + Element(owner) })
                {
                    if (Difference(text, out var error) is { } difference && differences.Count < 20)
                        differences.Add(difference);
                    outcomes[error.Kind] = outcomes.GetValueOrDefault(error.Kind) + 1;
                }
            }
        }

        await Assert.That(differences).IsEmpty();
        await Assert.That(owners).IsGreaterThan(300).Because(Counts(outcomes));
        await Assert.That(outcomes.GetValueOrDefault(Gs1ErrorKind.None)).IsGreaterThan(1000).Because(Counts(outcomes));
        await Assert.That(outcomes.GetValueOrDefault(Gs1ErrorKind.ExcludedAiPair)).IsGreaterThan(1000).Because(Counts(outcomes));
        await Assert.That(outcomes.GetValueOrDefault(Gs1ErrorKind.MissingRequiredAi)).IsGreaterThan(1000).Because(Counts(outcomes));
    }

    /// <summary>
    /// Random element strings of one to sixteen elements drawn from four sources: the whole table, the AIs the
    /// common rules name, the AIs that may follow (00) and (01), and six of those picked for the text. The first two
    /// mostly end at an exclusion or a requisite. Texts of the last two start with (00) and (01) before the drawn
    /// elements and so get past both, and six AIs in up to sixteen drawn elements repeat. Each outcome is counted to
    /// show that the run reached it.
    /// </summary>
    [Test]
    public async Task CheckPairings_MatchesReference_OnRandomElementStrings()
    {
        var random = new Random(Seed);
        var beside = BesideTheKeys.Value;
        await Assert.That(beside.Length).IsGreaterThan(100);
        var differences = new List<string>();
        var outcomes = new Dictionary<Gs1ErrorKind, int>();
        for (var i = 0; i < 40_000; i++)
        {
            var pool = (i % 4) switch { 0 => Ais, 1 => RulePool, 2 => beside, _ => Enumerable.Range(0, 6).Select(_ => beside[random.Next(beside.Length)]).ToArray() };
            var count = random.Next(1, 17);
            var sb = new StringBuilder(i % 4 >= 2 ? Keys : "");
            for (var e = 0; e < count; e++)
                sb.Append(Element(pool[random.Next(pool.Length)], random.Next(8) == 0 ? '2' : '1', Lengths[random.Next(Lengths.Length)]));
            var text = sb.ToString();
            if (Difference(text, out var error) is { } difference && differences.Count < 20)
                differences.Add(difference);
            outcomes[error.Kind] = outcomes.GetValueOrDefault(error.Kind) + 1;
        }

        await Assert.That(differences).IsEmpty();
        await Assert.That(outcomes.GetValueOrDefault(Gs1ErrorKind.None)).IsGreaterThan(1000).Because(Counts(outcomes));
        await Assert.That(outcomes.GetValueOrDefault(Gs1ErrorKind.ExcludedAiPair)).IsGreaterThan(1000).Because(Counts(outcomes));
        await Assert.That(outcomes.GetValueOrDefault(Gs1ErrorKind.MissingRequiredAi)).IsGreaterThan(1000).Because(Counts(outcomes));
        await Assert.That(outcomes.GetValueOrDefault(Gs1ErrorKind.RepeatedAi)).IsGreaterThan(1000).Because(Counts(outcomes));
    }

    /// <summary>
    /// The digital signature's rule: with (8030) in the text, a key without its serial component is refused at the
    /// key and the same key one character longer is taken, wherever (8030) stands. A key's mandatory part is 13
    /// digits for (253) and (255) and 14 for (8003).
    /// </summary>
    [Test]
    [Arguments("253", 13)]
    [Arguments("255", 13)]
    [Arguments("8003", 14)]
    public async Task CheckPairings_SerialisedKey_MatchesReference(string key, int mandatory)
    {
        var bare = Element(key, '1', mandatory);
        var serialised = Element(key, '1', mandatory + 1);
        var signature = Element("8030");

        foreach (var text in new[] { bare + signature, signature + bare })
        {
            await Assert.That(Difference(text)).IsNull();
            await Assert.That(Gs1Validator.CheckPairings(text, out var error)).IsFalse();
            await Assert.That(error).IsEqualTo(new Gs1Error(Gs1ErrorKind.MissingRequiredAi, text.IndexOf(bare, StringComparison.Ordinal), bare.Length - 1));
        }
        foreach (var text in new[] { serialised + signature, signature + serialised, bare, serialised })
        {
            await Assert.That(Difference(text)).IsNull();
            await Assert.That(Gs1Validator.CheckPairings(text, out _)).IsTrue().Because(Show(text));
        }
    }

    /// <summary>
    /// The set the tests of repeats are built from holds AIs of two, three and four digits. It holds more than 128,
    /// more than two words of the validator's set can hold, so the ranks that index the table of first values are
    /// counted across more than two words. The reference takes the set whole.
    /// </summary>
    [Test]
    public async Task CompatibleAis_AreManyAndOfEveryLength()
    {
        var compatible = Compatible.Value;
        await Assert.That(compatible.Length).IsGreaterThan(128);
        await Assert.That(compatible.Select(ai => ai.Length).Distinct().Order()).IsEquivalentTo(new[] { 2, 3, 4 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(compatible.Distinct().Count()).IsEqualTo(compatible.Length);
        await Assert.That(Gs1PairingReference.CheckPairings(string.Concat(compatible.Select(ai => Element(ai))), out _)).IsTrue();
    }

    /// <summary>Every AI two and three times with one value is valid data, in the order of the first round and in reverse.</summary>
    [Test]
    public async Task CheckPairings_ManyAisRepeatedWithOneValue_IsAccepted()
    {
        var compatible = Compatible.Value;
        var once = string.Concat(compatible.Select(ai => Element(ai)));
        var reversed = string.Concat(Enumerable.Reverse(compatible).Select(ai => Element(ai)));
        foreach (var text in new[] { once + once, once + reversed, once + reversed + once })
        {
            await Assert.That(Difference(text)).IsNull();
            await Assert.That(Gs1Validator.CheckPairings(text, out var error)).IsTrue().Because(error.ToString());
        }
    }

    /// <summary>
    /// One AI of many repeats with another value, after every AI has stood once: the refusal is at that last
    /// element, whichever AI it is and so whatever its rank in the set. Every third AI is tried, and the last.
    /// </summary>
    [Test]
    public async Task CheckPairings_OneOfManyAisRepeatsWithAnotherValue_IsRefusedAtThatElement()
    {
        var compatible = Compatible.Value;
        var once = string.Concat(compatible.Select(ai => Element(ai)));
        var failures = new List<string>();
        var tried = 0;
        for (var changed = 0; changed < compatible.Length; changed++)
        {
            if (changed % 3 != 0 && changed != compatible.Length - 1)
                continue;
            tried++;
            var repeat = Element(compatible[changed], '2');
            var text = once + repeat;
            var expected = new Gs1Error(Gs1ErrorKind.RepeatedAi, once.Length, repeat.TrimEnd(Gs1TestText.Gs).Length);
            if (Difference(text) is { } difference)
                failures.Add(difference);
            else if (Gs1Validator.CheckPairings(text, out var error) || !error.Equals(expected))
                failures.Add($"({compatible[changed]}) repeated: expected {expected}, got {error}");
        }
        await Assert.That(failures).IsEmpty();
        await Assert.That(tried).IsGreaterThan(compatible.Length / 3);
    }

    /// <summary>
    /// Two AIs repeat with another value, and the one whose first element comes later is repeated first. The refusal
    /// is at the repeat of the AI that comes first, which a check that stops at the first repeat it meets gets
    /// wrong. The two AIs are taken from the start, the middle and the end of the first round, both near each other
    /// and far apart.
    /// </summary>
    [Test]
    public async Task CheckPairings_TwoAisRepeatWithAnotherValue_IsRefusedAtTheAiThatComesFirst()
    {
        var compatible = Compatible.Value;
        var once = string.Concat(compatible.Select(ai => Element(ai)));
        var n = compatible.Length;
        var places = new[] { 0, 1, 2, n / 3, n / 3 + 1, n / 2, 2 * n / 3, n - 3, n - 2, n - 1 };
        var failures = new List<string>();
        var pairs = 0;
        foreach (var early in places)
        {
            foreach (var late in places)
            {
                if (early >= late)
                    continue;
                pairs++;
                var lateRepeat = Element(compatible[late], '2');
                var earlyRepeat = Element(compatible[early], '2');
                var text = once + lateRepeat + earlyRepeat;
                var expected = new Gs1Error(Gs1ErrorKind.RepeatedAi, once.Length + lateRepeat.Length, earlyRepeat.TrimEnd(Gs1TestText.Gs).Length);
                if (Difference(text) is { } difference)
                    failures.Add(difference);
                else if (Gs1Validator.CheckPairings(text, out var error) || !error.Equals(expected))
                    failures.Add($"({compatible[early]}) and ({compatible[late]}): expected {expected}, got {error}");
            }
        }
        await Assert.That(failures).IsEmpty();
        await Assert.That(pairs).IsEqualTo(45);
    }

    /// <summary>
    /// The large set of AIs that may stand together in a random order, each once or twice. In three texts of four a
    /// value in sixteen differs, so such a text holds several repeats with another value, in any order of their first
    /// elements.
    /// </summary>
    [Test]
    public async Task CheckPairings_MatchesReference_OnShuffledRepeats()
    {
        var random = new Random(Seed + 1);
        var compatible = Compatible.Value;
        var differences = new List<string>();
        var refused = 0;
        var accepted = 0;
        for (var i = 0; i < 24; i++)
        {
            // Every fourth text has no value that differs, so the check also runs to its end on many AIs.
            var differing = i % 4 == 0 ? 0 : 16;
            var elements = new List<string>();
            foreach (var ai in compatible)
            {
                var times = random.Next(1, 3);
                for (var t = 0; t < times; t++)
                    elements.Add(Element(ai, differing != 0 && random.Next(differing) == 0 ? '2' : '1'));
            }
            for (var e = elements.Count - 1; e > 0; e--)
            {
                var other = random.Next(e + 1);
                (elements[e], elements[other]) = (elements[other], elements[e]);
            }
            var text = string.Concat(elements);
            if (Difference(text, out var error) is { } difference && differences.Count < 5)
                differences.Add(difference);
            if (error.Kind == Gs1ErrorKind.None)
                accepted++;
            else if (error.Kind == Gs1ErrorKind.RepeatedAi)
                refused++;
        }

        await Assert.That(differences).IsEmpty();
        await Assert.That(accepted).IsEqualTo(6);
        await Assert.That(refused).IsEqualTo(18);
    }

    /// <summary>
    /// The large set of AIs that may stand together, in turn, for close to 160,000 elements, all accepted, so every
    /// element after the first round repeats an AI. A repeat check that takes more passes over the text as it grows,
    /// rather than as its AIs grow, reads hundreds of millions of elements here and takes longer than the five seconds
    /// allowed. The test runs apart from the other tests of its process, since the bound is on the clock.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task CheckPairings_OnManyAisInTurn_TakesUnderFiveSeconds()
    {
        var round = string.Concat(Compatible.Value.Select(ai => Element(ai)));
        var rounds = 160_000 / Compatible.Value.Length;
        var text = new StringBuilder(round.Length * rounds).Insert(0, round, rounds).ToString();

        var clock = Stopwatch.StartNew();
        var accepted = Gs1Validator.CheckPairings(text, out var error);
        var elapsed = clock.Elapsed;

        await Assert.That(accepted).IsTrue().Because(error.ToString());
        await Assert.That(rounds * Compatible.Value.Length).IsGreaterThan(159_000);
        await Assert.That(elapsed).IsLessThan(TimeSpan.FromSeconds(5)).Because($"{elapsed.TotalMilliseconds:F0} ms");
    }
}
