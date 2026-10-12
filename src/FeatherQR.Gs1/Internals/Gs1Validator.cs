namespace FeatherQR.Gs1.Internals;

/// <summary>
/// Every check on an element string (the strength the builders and <c>TryValidate</c> use): the split, each
/// element's components against their lengths, character sets and linters, then the rules between elements
/// (a repeated AI with different values, the requisite and exclusive pairings). Two passes over the text and
/// no allocation: the pairing checks re-split the string instead of keeping a list of elements.
/// </summary>
internal static class Gs1Validator
{
    /// <summary><paramref name="year"/> resolves two-digit years, so the checks can be run for a fixed year in tests.</summary>
    public static bool TryValidate(ReadOnlySpan<char> text, int year, out Gs1Error error)
    {
        error = default;
        if (text.Length == 0)
        {
            error = new Gs1Error(Gs1ErrorKind.InvalidText, 0, 0);
            return false;
        }

        var position = 0;
        while (position < text.Length)
        {
            if (!Gs1Splitter.TryNext(text, ref position, out var element, out error))
                return false;
            if (!element.Known)
            {
                error = new Gs1Error(Gs1ErrorKind.UnknownAi, element.AiStart, element.AiLength);
                return false;
            }
            if (!CheckValue(text, element, year, out error))
                return false;
        }

        return CheckPairings(text, out error);
    }

    /// <summary>One element's value against its entry: component by component, lengths, then character set, then linters.</summary>
    private static bool CheckValue(ReadOnlySpan<char> text, in Gs1SplitElement element, int year, out Gs1Error error)
    {
        error = default;
        var entry = element.Entry;
        var value = text.Slice(element.ValueStart, element.ValueLength);
        if (value.Length == 0)
        {
            error = new Gs1Error(Gs1ErrorKind.EmptyValue, element.ValueStart, 0);
            return false;
        }

        var consumed = 0;
        for (var i = 0; i < entry.ComponentCount; i++)
        {
            var component = entry.Component(i);
            var rest = value.Slice(consumed);
            if (rest.Length == 0)
            {
                if (component.IsOptional)
                    break;
                error = new Gs1Error(Gs1ErrorKind.ValueTooShort, element.ValueStart, element.ValueLength);
                return false;
            }

            int take;
            if (component.IsVariable)
            {
                take = rest.Length;
                if (take > component.MaxLength)
                {
                    error = new Gs1Error(Gs1ErrorKind.ValueTooLong, element.ValueStart + consumed + component.MaxLength, take - component.MaxLength);
                    return false;
                }
            }
            else
            {
                take = component.MaxLength;
                if (rest.Length < take)
                {
                    error = new Gs1Error(Gs1ErrorKind.ValueTooShort, element.ValueStart, element.ValueLength);
                    return false;
                }
            }

            var part = rest.Slice(0, take);
            var partStart = element.ValueStart + consumed;
            var invalid = Gs1Charsets.FirstInvalid(component.Type, part, out var invalidLength);
            if (invalid >= 0)
            {
                error = new Gs1Error(Gs1ErrorKind.InvalidCharacter, partStart + invalid, invalidLength);
                return false;
            }

            if (component.Linters != 0)
            {
                for (var bit = 0; bit < 64; bit++)
                {
                    if ((component.Linters & (1UL << bit)) == 0)
                        continue;
                    var kind = Gs1Linters.Check((Gs1Linter)bit, part, year, out var offset, out var length);
                    if (kind != Gs1ErrorKind.None)
                    {
                        error = new Gs1Error(kind, partStart + offset, length);
                        return false;
                    }
                }
            }
            consumed += take;
        }

        if (consumed < value.Length)
        {
            error = new Gs1Error(Gs1ErrorKind.ValueTooLong, element.ValueStart + consumed, value.Length - consumed);
            return false;
        }
        return true;
    }

    /// <summary>
    /// The rules between elements, over the split that <see cref="TryValidate"/> has already accepted, in the
    /// order the Syntax Engine reports them: a repeated AI with another value, then every exclusion, then every
    /// requisite. Each is a pass over the elements.
    /// </summary>
    private static bool CheckPairings(ReadOnlySpan<char> text, out Gs1Error error)
    {
        error = default;
        var position = 0;
        while (position < text.Length)
        {
            if (!Gs1Splitter.TryNext(text, ref position, out var element, out error))
                return false;
            var ai = text.Slice(element.AiStart, element.AiLength);
            var value = text.Slice(element.ValueStart, element.ValueLength);
            var later = position;
            while (later < text.Length)
            {
                if (!Gs1Splitter.TryNext(text, ref later, out var other, out error))
                    return false;
                if (text.Slice(other.AiStart, other.AiLength).SequenceEqual(ai) && !text.Slice(other.ValueStart, other.ValueLength).SequenceEqual(value))
                {
                    error = new Gs1Error(Gs1ErrorKind.RepeatedAi, other.AiStart, other.End - other.AiStart);
                    return false;
                }
            }
        }

        position = 0;
        while (position < text.Length)
        {
            if (!Gs1Splitter.TryNext(text, ref position, out var element, out error))
                return false;
            var entry = element.Entry;
            for (var x = 0; x < entry.ExclusionCount; x++)
            {
                if (FindMatch(text, element, entry.Exclusion(x), out var excluded))
                {
                    var last = excluded.AiStart > element.AiStart ? excluded : element;
                    error = new Gs1Error(Gs1ErrorKind.ExcludedAiPair, last.AiStart, last.End - last.AiStart);
                    return false;
                }
            }
        }

        position = 0;
        while (position < text.Length)
        {
            if (!Gs1Splitter.TryNext(text, ref position, out var element, out error))
                return false;
            var entry = element.Entry;
            for (var g = 0; g < entry.RequisiteGroupCount; g++)
            {
                if (!IsSatisfied(text, element, entry.RequisiteGroup(g)))
                {
                    error = new Gs1Error(Gs1ErrorKind.MissingRequiredAi, element.AiStart, element.End - element.AiStart);
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// A requisite group holds when one of its alternatives does: every member present as another element. In a
    /// group that has a compound alternative (<c>01+21</c>), a single-member alternative that names a key with an
    /// optional serial component is satisfied only when that component is present, which is how the dictionary
    /// expresses "a serialised key" for AI (8030).
    /// </summary>
    private static bool IsSatisfied(ReadOnlySpan<char> text, in Gs1SplitElement element, in Gs1AiTable.Gs1RequisiteGroup group)
    {
        var hasCompound = false;
        for (var a = 0; a < group.AlternativeCount; a++)
        {
            if (group.Alternative(a).MemberCount > 1)
            {
                hasCompound = true;
                break;
            }
        }

        for (var a = 0; a < group.AlternativeCount; a++)
        {
            var alternative = group.Alternative(a);
            var all = true;
            for (var m = 0; m < alternative.MemberCount && all; m++)
            {
                if (!FindMatch(text, element, alternative.Member(m), out var match))
                {
                    all = false;
                }
                else if (hasCompound && alternative.MemberCount == 1 && !HasOptionalComponentPresent(text, match))
                {
                    all = false;
                }
            }
            if (all)
                return true;
        }
        return false;
    }

    /// <summary>Whether an element's value reaches into an optional component of its entry (true also for an entry that has none).</summary>
    private static bool HasOptionalComponentPresent(ReadOnlySpan<char> text, in Gs1SplitElement element)
    {
        if (!element.Known)
            return true;
        var mandatory = 0;
        var hasOptional = false;
        for (var i = 0; i < element.Entry.ComponentCount; i++)
        {
            var component = element.Entry.Component(i);
            if (component.IsOptional)
                hasOptional = true;
            else
                mandatory += component.MaxLength;
        }
        return !hasOptional || element.ValueLength > mandatory;
    }

    /// <summary>Another element (not this one, and not its AI) whose AI matches the pattern.</summary>
    private static bool FindMatch(ReadOnlySpan<char> text, in Gs1SplitElement self, in Gs1AiTable.Gs1AiPattern pattern, out Gs1SplitElement match)
    {
        match = default;
        var selfAi = text.Slice(self.AiStart, self.AiLength);
        var position = 0;
        while (position < text.Length)
        {
            if (!Gs1Splitter.TryNext(text, ref position, out var element, out _))
                return false;
            var ai = text.Slice(element.AiStart, element.AiLength);
            if (ai.SequenceEqual(selfAi))
                continue;
            if (pattern.Matches(ai))
            {
                match = element;
                return true;
            }
        }
        return false;
    }
}
