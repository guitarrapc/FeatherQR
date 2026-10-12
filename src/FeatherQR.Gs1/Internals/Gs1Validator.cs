namespace FeatherQR.Gs1.Internals;

/// <summary>
/// Every check on an element string (the strength the builders and <c>TryValidate</c> use): the split, each
/// element's components against their lengths, character sets and linters, then the rules between elements
/// (the exclusive and requisite pairings, a repeated AI with different values, the serialised key a digital
/// signature needs). One pass over the components, then a pass or two per pairing rule, and no allocation: the
/// pairing checks re-split the string instead of keeping a list of elements.
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

    private static bool CheckValue(ReadOnlySpan<char> text, in Gs1SplitElement element, int year, out Gs1Error error)
        => CheckValue(text.Slice(element.ValueStart, element.ValueLength), element.ValueStart, element.Entry, year, out error);

    /// <summary>
    /// One element's value against its entry: component by component, lengths, then character set, then linters.
    /// The error's offset is <paramref name="valueOffset"/> plus the place in <paramref name="value"/>, so a
    /// builder that holds the value apart from its text can map it back.
    /// </summary>
    public static bool CheckValue(ReadOnlySpan<char> value, int valueOffset, in Gs1AiTable.Gs1AiEntry entry, int year, out Gs1Error error)
    {
        error = default;
        if (value.Length == 0)
        {
            error = new Gs1Error(Gs1ErrorKind.EmptyValue, valueOffset, 0);
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
                error = new Gs1Error(Gs1ErrorKind.ValueTooShort, valueOffset, value.Length);
                return false;
            }

            int take;
            if (component.IsVariable)
            {
                take = rest.Length;
                if (take > component.MaxLength)
                {
                    error = new Gs1Error(Gs1ErrorKind.ValueTooLong, valueOffset + consumed + component.MaxLength, take - component.MaxLength);
                    return false;
                }
            }
            else
            {
                take = component.MaxLength;
                if (rest.Length < take)
                {
                    error = new Gs1Error(Gs1ErrorKind.ValueTooShort, valueOffset, value.Length);
                    return false;
                }
            }

            var part = rest.Slice(0, take);
            var partStart = valueOffset + consumed;
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
            error = new Gs1Error(Gs1ErrorKind.ValueTooLong, valueOffset + consumed, value.Length - consumed);
            return false;
        }
        return true;
    }

    /// <summary>
    /// The rules between elements, over the split that <see cref="TryValidate"/> has already accepted, in the
    /// order the Syntax Engine runs its validations: every exclusion, then every requisite, then a repeated AI
    /// with another value, then the digital signature's serialised key. Each is a pass over the elements.
    /// </summary>
    public static bool CheckPairings(ReadOnlySpan<char> text, out Gs1Error error)
    {
        error = default;
        var position = 0;
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

        position = 0;
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

        return CheckSerialisedKey(text, out error);
    }

    /// <summary>
    /// The Syntax Engine's one rule beside the dictionary's pairings (its <c>validateDigSigRequiresSerialisedKey</c>,
    /// which cannot be turned off): a digital signature (8030) signs a serialised key, so with (8030) present every
    /// (253), (255) and (8003) must carry its optional serial component, whichever requisite alternative let
    /// (8030) in. Reported at the key element, where the engine reports it too ("Serial component must be present
    /// for AI (253) when used with AI (8030)"), as a missing requisite, the kind the test corpus maps that message to.
    /// </summary>
    private static bool CheckSerialisedKey(ReadOnlySpan<char> text, out Gs1Error error)
    {
        error = default;
        if (!FindAi(text, "8030".AsSpan(), out _))
            return true;
        var position = 0;
        while (position < text.Length)
        {
            if (!Gs1Splitter.TryNext(text, ref position, out var element, out error))
                return false;
            var ai = text.Slice(element.AiStart, element.AiLength);
            var isKey = ai.SequenceEqual("253".AsSpan()) || ai.SequenceEqual("255".AsSpan()) || ai.SequenceEqual("8003".AsSpan());
            if (isKey && !HasOptionalComponentPresent(text, element))
            {
                error = new Gs1Error(Gs1ErrorKind.MissingRequiredAi, element.AiStart, element.End - element.AiStart);
                return false;
            }
        }
        return true;
    }

    /// <summary>A requisite group holds when one of its alternatives does: every member present as another element.</summary>
    private static bool IsSatisfied(ReadOnlySpan<char> text, in Gs1SplitElement element, in Gs1AiTable.Gs1RequisiteGroup group)
    {
        for (var a = 0; a < group.AlternativeCount; a++)
        {
            var alternative = group.Alternative(a);
            var all = true;
            for (var m = 0; m < alternative.MemberCount && all; m++)
                all = FindMatch(text, element, alternative.Member(m), out _);
            if (all)
                return true;
        }
        return false;
    }

    /// <summary>The first element with this AI.</summary>
    private static bool FindAi(ReadOnlySpan<char> text, ReadOnlySpan<char> ai, out Gs1SplitElement match)
    {
        var position = 0;
        while (position < text.Length && Gs1Splitter.TryNext(text, ref position, out match, out _))
        {
            if (text.Slice(match.AiStart, match.AiLength).SequenceEqual(ai))
                return true;
        }
        match = default;
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
