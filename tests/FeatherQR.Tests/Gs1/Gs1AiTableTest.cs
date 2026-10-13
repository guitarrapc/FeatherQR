using FeatherQR.Gs1.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The generated GS1 AI table (<see cref="Gs1AiTable"/>) read back through its lookup: the pinned release, the
/// AIs the plan's equivalence classes name (2-, 3- and 4-digit AIs, a range, predefined and variable lengths,
/// optional and multi-component specifications), the pairing rules, the Digital Link keys and qualifier
/// sequences, and the two tables of the General Specifications that are frozen by its own statement (7-5, AI
/// length by the first two digits, and 7-6, the predefined lengths). Every expectation is read from the committed
/// dictionary file or the specification text, not from the generated source.
/// </summary>
public class Gs1AiTableTest
{
    [Test]
    public async Task Table_IsThePinnedRelease()
    {
        // The constants are generated, so they are read into locals: the pin is on the generated output, not on a literal.
        var (release, entries, ais) = (Gs1AiTable.DictionaryRelease, Gs1AiTable.EntryCount, Gs1AiTable.AiCount);
        await Assert.That(release).IsEqualTo("2026-01-27");
        await Assert.That(entries).IsEqualTo(224);
        await Assert.That(ais).IsEqualTo(541);
    }

    [Test]
    public async Task TryFind_Gtin_IsPredefinedWithOneCheckedComponent()
    {
        await Assert.That(Gs1AiTable.TryFind("01", out var entry)).IsTrue();
        await Assert.That(entry.IsPredefinedLength).IsTrue();
        await Assert.That(entry.IsDigitalLinkAttribute).IsTrue();
        await Assert.That(entry.IsDigitalLinkKey).IsTrue();
        await Assert.That(entry.ComponentCount).IsEqualTo(1);
        var component = entry.Component(0);
        await Assert.That(component.Type).IsEqualTo(Gs1ComponentType.Numeric);
        await Assert.That((component.MinLength, component.MaxLength, component.IsOptional)).IsEqualTo((14, 14, false));
        await Assert.That(component.Has(Gs1Linter.Csum)).IsTrue();
        await Assert.That(component.Has(Gs1Linter.Gcppos2)).IsTrue();
        await Assert.That(component.Has(Gs1Linter.Gcppos1)).IsFalse();
    }

    [Test]
    public async Task TryFind_Batch_IsVariableAndNotAKey()
    {
        await Assert.That(Gs1AiTable.TryFind("10", out var entry)).IsTrue();
        await Assert.That(entry.IsPredefinedLength).IsFalse();
        await Assert.That(entry.IsDigitalLinkKey).IsFalse();
        await Assert.That(entry.IsDigitalLinkAttribute).IsTrue();
        var component = entry.Component(0);
        await Assert.That(component.Type).IsEqualTo(Gs1ComponentType.Cset82);
        await Assert.That((component.MinLength, component.MaxLength, component.IsVariable)).IsEqualTo((1, 20, true));
        await Assert.That(component.Linters).IsEqualTo(0UL);
    }

    [Test]
    [Arguments("3100")]
    [Arguments("3103")]
    [Arguments("3105")]
    public async Task TryFind_RangeAi_IsOneEntryForEveryMember(string ai)
    {
        await Assert.That(Gs1AiTable.TryFind(ai, out var entry)).IsTrue();
        await Assert.That(entry.IsPredefinedLength).IsTrue();
        await Assert.That(entry.Component(0).MaxLength).IsEqualTo(6);
        await Assert.That(Gs1AiTable.TryFind("3100", out var first)).IsTrue();
        await Assert.That(entry.Index).IsEqualTo(first.Index);
    }

    [Test]
    public async Task TryFind_ProductionTime_HasTwoOptionalComponents()
    {
        await Assert.That(Gs1AiTable.TryFind("8008", out var entry)).IsTrue();
        await Assert.That(entry.ComponentCount).IsEqualTo(4);
        await Assert.That(entry.Component(0).Has(Gs1Linter.Yymmdd)).IsTrue();
        await Assert.That(entry.Component(1).Has(Gs1Linter.Hh)).IsTrue();
        await Assert.That(entry.Component(2).Has(Gs1Linter.Mi)).IsTrue();
        await Assert.That(entry.Component(3).Has(Gs1Linter.Ss)).IsTrue();
        await Assert.That(new[] { entry.Component(0).IsOptional, entry.Component(1).IsOptional, entry.Component(2).IsOptional, entry.Component(3).IsOptional })
            .IsEquivalentTo([false, false, true, true], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task TryFind_Uic_HasFourSingleCharacterComponents()
    {
        await Assert.That(Gs1AiTable.TryFind("7040", out var entry)).IsTrue();
        await Assert.That(entry.ComponentCount).IsEqualTo(4);
        await Assert.That(entry.Component(0).Type).IsEqualTo(Gs1ComponentType.Numeric);
        await Assert.That(entry.Component(3).Type).IsEqualTo(Gs1ComponentType.Cset82);
        await Assert.That(entry.Component(3).Has(Gs1Linter.Importeridx)).IsTrue();
        await Assert.That(entry.IsDigitalLinkAttribute).IsFalse();
    }

    [Test]
    public async Task TryFind_DigSig_IsBase64Url()
    {
        await Assert.That(Gs1AiTable.TryFind("8030", out var entry)).IsTrue();
        await Assert.That(entry.Component(0).Type).IsEqualTo(Gs1ComponentType.Cset64);
        await Assert.That(entry.Component(0).MaxLength).IsEqualTo(90);
        await Assert.That(Gs1AiTable.TryFind("8010", out var cpid)).IsTrue();
        await Assert.That(cpid.Component(0).Type).IsEqualTo(Gs1ComponentType.Cset39);
    }

    [Test]
    [Arguments("")]
    [Arguments("0")]
    [Arguments("1")]
    [Arguments("010")]
    [Arguments("0A")]
    [Arguments("89")]
    [Arguments(Gs1UnassignedAi.Ai7050)]
    [Arguments(Gs1UnassignedAi.Ai3106)]
    [Arguments("12345")]
    public async Task TryFind_UnknownAi_IsFalse(string ai)
    {
        await Assert.That(Gs1AiTable.TryFind(ai, out _)).IsFalse();
    }

    [Test]
    public async Task Requisites_Batch_HasFiveAlternativesInOneGroup()
    {
        Gs1AiTable.TryFind("10", out var entry);
        await Assert.That(entry.RequisiteGroupCount).IsEqualTo(1);
        var group = entry.RequisiteGroup(0);
        await Assert.That(group.AlternativeCount).IsEqualTo(5);
        var alternatives = Enumerable.Range(0, group.AlternativeCount).Select(i => group.Alternative(i)).ToArray();
        await Assert.That(alternatives.Select(a => a.MemberCount).ToArray()).IsEquivalentTo([1, 1, 1, 1, 1], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(alternatives.Select(a => a.Member(0).ToString()).ToArray()).IsEquivalentTo(["01", "02", "03", "8006", "8026"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(entry.ExclusionCount).IsEqualTo(0);
    }

    [Test]
    public async Task Requisites_Imei_IsOneCompoundAlternative()
    {
        Gs1AiTable.TryFind("8040", out var entry);
        var alternative = entry.RequisiteGroup(0).Alternative(0);
        await Assert.That(entry.RequisiteGroup(0).AlternativeCount).IsEqualTo(1);
        await Assert.That(alternative.MemberCount).IsEqualTo(2);
        await Assert.That(alternative.Member(0).ToString()).IsEqualTo("01");
        await Assert.That(alternative.Member(1).ToString()).IsEqualTo("21");
    }

    [Test]
    public async Task Requisites_Count_HasTwoGroupsThatBothApply()
    {
        Gs1AiTable.TryFind("37", out var entry);
        await Assert.That(entry.RequisiteGroupCount).IsEqualTo(2);
        await Assert.That(entry.RequisiteGroup(0).Alternative(0).Member(0).ToString()).IsEqualTo("00");
        await Assert.That(entry.RequisiteGroup(1).AlternativeCount).IsEqualTo(2);
        await Assert.That(entry.RequisiteGroup(1).Alternative(1).Member(0).ToString()).IsEqualTo("8026");
    }

    [Test]
    public async Task Exclusions_Gtin_AndAPattern()
    {
        Gs1AiTable.TryFind("01", out var gtin);
        await Assert.That(gtin.ExclusionCount).IsEqualTo(2);
        await Assert.That(gtin.Exclusion(0).ToString()).IsEqualTo("255");
        await Assert.That(gtin.Exclusion(1).ToString()).IsEqualTo("37");

        Gs1AiTable.TryFind("3103", out var netWeight);
        var pattern = netWeight.Exclusion(0);
        await Assert.That(pattern.ToString()).IsEqualTo("310n");
        await Assert.That(pattern.Matches("3104")).IsTrue();
        await Assert.That(pattern.Matches("3109")).IsTrue();
        await Assert.That(pattern.Matches("3113")).IsFalse();
        await Assert.That(pattern.Matches("310")).IsFalse();
        await Assert.That(gtin.Exclusion(1).Matches("37")).IsTrue();
        await Assert.That(gtin.Exclusion(1).Matches("370")).IsFalse();
    }

    /// <summary>The first and last keys a pattern matches: a wildcard digit counts as 0 in the first and as 9 in the last, wherever it stands.</summary>
    [Test]
    [Arguments(20001, 0b0000, 20001, 20001)]
    [Arguments(30250, 0b0000, 30250, 30250)]
    [Arguments(43940, 0b1000, 43940, 43949)]
    [Arguments(43100, 0b1100, 43100, 43199)]
    [Arguments(43010, 0b1010, 43010, 43919)]
    [Arguments(40000, 0b1111, 40000, 49999)]
    [Arguments(30200, 0b0100, 30200, 30209)]
    public async Task Pattern_KeyRange(int key, int wildcards, int first, int last)
    {
        var pattern = new Gs1AiTable.Gs1AiPattern((ushort)key, (byte)wildcards);
        await Assert.That(pattern.FirstKey).IsEqualTo(first);
        await Assert.That(pattern.LastKey).IsEqualTo(last);
    }

    /// <summary>
    /// Every pairing pattern of the table matches every key from its first to its last, which is what lets the
    /// validator ask its set of AIs for a pattern by that range. A wildcard before a fixed digit, as in
    /// <c>3n1n</c>, would leave keys in the range that the pattern does not match.
    /// </summary>
    [Test]
    public async Task Patterns_MatchEveryKeyFromTheFirstToTheLast()
    {
        var patterns = new List<Gs1AiTable.Gs1AiPattern>();
        for (var i = 0; i < Gs1AiTable.EntryCount; i++)
        {
            var entry = Gs1AiTable.EntryAt(i);
            for (var x = 0; x < entry.ExclusionCount; x++)
                patterns.Add(entry.Exclusion(x));
            for (var g = 0; g < entry.RequisiteGroupCount; g++)
            {
                var group = entry.RequisiteGroup(g);
                for (var a = 0; a < group.AlternativeCount; a++)
                {
                    var alternative = group.Alternative(a);
                    for (var m = 0; m < alternative.MemberCount; m++)
                        patterns.Add(alternative.Member(m));
                }
            }
        }

        var failures = new List<string>();
        var withWildcards = 0;
        foreach (var pattern in patterns)
        {
            var text = pattern.ToString();
            if (text.Contains('n'))
                withWildcards++;
            if (pattern.FirstKey != text.Length * 10000 + int.Parse(text.Replace('n', '0')) || pattern.LastKey != text.Length * 10000 + int.Parse(text.Replace('n', '9')))
                failures.Add($"{text}: keys {pattern.FirstKey} to {pattern.LastKey}");
            for (var key = pattern.FirstKey; key <= pattern.LastKey; key++)
            {
                var ai = (key % 10000).ToString().PadLeft(text.Length, '0');
                if (!pattern.Matches(ai))
                    failures.Add($"{text} does not match {ai}");
            }
        }

        await Assert.That(failures).IsEmpty();
        await Assert.That(patterns.Count).IsGreaterThan(400);
        await Assert.That(withWildcards).IsGreaterThan(70);
    }

    [Test]
    public async Task DigitalLink_KeysAndQualifierSequences()
    {
        Gs1AiTable.TryFind("01", out var gtin);
        await Assert.That(gtin.QualifierSequenceCount).IsEqualTo(2);
        var first = gtin.QualifierSequence(0);
        await Assert.That(Enumerable.Range(0, first.Count).Select(i => first.Qualifier(i).ToString()).ToArray()).IsEquivalentTo(["22", "10", "21"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(gtin.QualifierSequence(1).Count).IsEqualTo(1);
        await Assert.That(gtin.QualifierSequence(1).Qualifier(0).ToString()).IsEqualTo("235");

        Gs1AiTable.TryFind("00", out var sscc);
        await Assert.That(sscc.IsDigitalLinkKey).IsTrue();
        await Assert.That(sscc.QualifierSequenceCount).IsEqualTo(0);

        Gs1AiTable.TryFind("414", out var gln);
        await Assert.That(gln.QualifierSequenceCount).IsEqualTo(2);
        await Assert.That(gln.QualifierSequence(1).Qualifier(0).ToString()).IsEqualTo("7040");

        Gs1AiTable.TryFind("10", out var batch);
        await Assert.That(batch.IsDigitalLinkKey).IsFalse();
        await Assert.That(batch.QualifierSequenceCount).IsEqualTo(0);
    }

    /// <summary>Table 7-5 of the General Specifications 26.0 (7.8.2), as the text prints it, with an unlisted prefix reading 0.</summary>
    [Test]
    [Arguments("00", 2)]
    [Arguments("01", 2)]
    [Arguments("10", 2)]
    [Arguments("17", 2)]
    [Arguments("22", 2)]
    [Arguments("23", 3)]
    [Arguments("25", 3)]
    [Arguments("30", 2)]
    [Arguments("31", 4)]
    [Arguments("36", 4)]
    [Arguments("37", 2)]
    [Arguments("39", 4)]
    [Arguments("40", 3)]
    [Arguments("41", 3)]
    [Arguments("42", 3)]
    [Arguments("43", 4)]
    [Arguments("70", 4)]
    [Arguments("71", 3)]
    [Arguments("72", 4)]
    [Arguments("80", 4)]
    [Arguments("82", 4)]
    [Arguments("90", 2)]
    [Arguments("99", 2)]
    [Arguments("04", 0)]
    [Arguments("14", 0)]
    [Arguments("38", 0)]
    [Arguments("50", 0)]
    [Arguments("73", 0)]
    [Arguments("89", 0)]
    public async Task AiLength_FollowsTable75(string prefix, int length)
    {
        await Assert.That(Gs1AiTable.AiLength(prefix[0], prefix[1])).IsEqualTo(length);
    }

    /// <summary>Table 7-6 (7.8.3): the predefined total lengths, bracketed reserved prefixes included, and nothing else.</summary>
    [Test]
    [Arguments("00", 20)]
    [Arguments("01", 16)]
    [Arguments("02", 16)]
    [Arguments("03", 16)]
    [Arguments("04", 18)]
    [Arguments("11", 8)]
    [Arguments("12", 8)]
    [Arguments("13", 8)]
    [Arguments("14", 8)]
    [Arguments("15", 8)]
    [Arguments("16", 8)]
    [Arguments("17", 8)]
    [Arguments("18", 8)]
    [Arguments("19", 8)]
    [Arguments("20", 4)]
    [Arguments("31", 10)]
    [Arguments("33", 10)]
    [Arguments("36", 10)]
    [Arguments("41", 16)]
    public async Task PredefinedLength_FollowsTable76(string prefix, int totalLength)
    {
        await Assert.That(Gs1AiTable.TryGetPredefinedLength(prefix[0], prefix[1], out var length)).IsTrue();
        await Assert.That(length).IsEqualTo(totalLength);
    }

    [Test]
    [Arguments("10")]
    [Arguments("21")]
    [Arguments("30")]
    [Arguments("37")]
    [Arguments("39")]
    [Arguments("40")]
    [Arguments("42")]
    [Arguments("80")]
    [Arguments("99")]
    public async Task PredefinedLength_IsFalseOutsideTable76(string prefix)
    {
        await Assert.That(Gs1AiTable.TryGetPredefinedLength(prefix[0], prefix[1], out _)).IsFalse();
    }

    /// <summary>The dictionary's `*` flag and Table 7-6 agree on every entry: a predefined entry's AI plus its fixed components sum to the table's length, and no other entry sits under a predefined prefix.</summary>
    [Test]
    public async Task PredefinedEntries_AgreeWithTable76()
    {
        var disagreements = new List<string>();
        for (var i = 0; i < Gs1AiTable.EntryCount; i++)
        {
            var entry = Gs1AiTable.EntryAt(i);
            var ai = entry.FirstAi.ToString();
            var predefined = Gs1AiTable.TryGetPredefinedLength(ai[0], ai[1], out var total);
            if (predefined != entry.IsPredefinedLength)
                disagreements.Add($"{ai}: dictionary {(entry.IsPredefinedLength ? "*" : "variable")}, Table 7-6 {(predefined ? total.ToString() : "absent")}");
            if (!entry.IsPredefinedLength)
                continue;
            var length = ai.Length;
            for (var c = 0; c < entry.ComponentCount; c++)
            {
                var component = entry.Component(c);
                if (component.IsVariable || component.IsOptional)
                    disagreements.Add($"{ai}: a predefined entry with a variable or optional component");
                length += component.MaxLength;
            }
            if (length != total)
                disagreements.Add($"{ai}: {length} characters, Table 7-6 says {total}");
        }
        await Assert.That(disagreements).IsEmpty();
    }

    /// <summary>Every AI's length is the one Table 7-5 gives its first two digits, and every prefix of Table 7-5 has an AI in this release.</summary>
    [Test]
    public async Task EntryLengths_AgreeWithTable75()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < Gs1AiTable.EntryCount; i++)
        {
            var entry = Gs1AiTable.EntryAt(i);
            var first = entry.FirstAi.ToString();
            var last = entry.LastAi.ToString();
            await Assert.That(last.Length).IsEqualTo(first.Length);
            for (var value = int.Parse(first); value <= int.Parse(last); value++)
            {
                var ai = value.ToString().PadLeft(first.Length, '0');
                await Assert.That(Gs1AiTable.AiLength(ai[0], ai[1])).IsEqualTo(ai.Length).Because($"AI {ai}");
                seen.Add(ai.Substring(0, 2));
            }
        }
        var listed = 0;
        for (var d0 = '0'; d0 <= '9'; d0++)
        {
            for (var d1 = '0'; d1 <= '9'; d1++)
            {
                if (Gs1AiTable.AiLength(d0, d1) == 0)
                    continue;
                listed++;
                await Assert.That(seen.Contains($"{d0}{d1}")).IsTrue().Because($"prefix {d0}{d1} is in Table 7-5 and should have an entry");
            }
        }
        await Assert.That(listed).IsEqualTo(46);
    }
}
