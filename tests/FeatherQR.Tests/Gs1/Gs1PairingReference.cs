using FeatherQR.Gs1;
using FeatherQR.Gs1.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The rules between the elements of an element string in their plainest form, as the reference the library's
/// <see cref="Gs1Validator.CheckPairings"/> is held to: each exclusion and each requisite is asked of every element
/// by a scan of the whole string, a repeated AI is found by comparing every element with every later one, and the
/// serialised key is one scan for (8030) and one pass. It keeps no state between elements, so its cost is the square
/// of the number of elements, and its answer and the place of its error follow from the order of the scans alone:
/// the first element with an exclusion that is met, then the first element with a requisite that is not, then the
/// first element that a later one repeats with another value, then the digital signature's serialised key.
/// </summary>
internal static class Gs1PairingReference
{
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

    private static bool CheckSerialisedKey(ReadOnlySpan<char> text, out Gs1Error error)
    {
        error = default;
        if (!FindAi(text, "8030".AsSpan()))
            return true;
        var position = 0;
        while (position < text.Length)
        {
            if (!Gs1Splitter.TryNext(text, ref position, out var element, out error))
                return false;
            var ai = text.Slice(element.AiStart, element.AiLength);
            var isKey = ai.SequenceEqual("253".AsSpan()) || ai.SequenceEqual("255".AsSpan()) || ai.SequenceEqual("8003".AsSpan());
            if (isKey && !HasOptionalComponentPresent(element))
            {
                error = new Gs1Error(Gs1ErrorKind.MissingRequiredAi, element.AiStart, element.End - element.AiStart);
                return false;
            }
        }
        return true;
    }

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

    private static bool FindAi(ReadOnlySpan<char> text, ReadOnlySpan<char> ai)
    {
        var position = 0;
        while (position < text.Length && Gs1Splitter.TryNext(text, ref position, out var match, out _))
        {
            if (text.Slice(match.AiStart, match.AiLength).SequenceEqual(ai))
                return true;
        }
        return false;
    }

    private static bool HasOptionalComponentPresent(in Gs1SplitElement element)
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
