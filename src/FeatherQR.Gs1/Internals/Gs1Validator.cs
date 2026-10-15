using System.Runtime.CompilerServices;

namespace FeatherQR.Gs1.Internals;

/// <summary>
/// Every check on an element string (the strength the builders and <c>TryValidate</c> use): the split, each
/// element's components against their lengths, character sets and linters, then the rules between elements
/// (the exclusive and requisite pairings, a repeated AI with different values, the serialised key a digital
/// signature needs). No allocation, and time in proportion to the length of the text: the rules between elements
/// ask a set of the AIs present, kept as bits on the stack, where a scan of the text for each element would cost
/// the square of the number of elements.
/// </summary>
internal static class Gs1Validator
{
    // A set of AIs has one bit for each number of two, three and four digits, assigned or not: 100, then 1,000, then
    // 10,000, in 174 words of 64 bits. After them come three words more, with a bit for each of those that holds an
    // AI, so that counting the AIs below a word reads only the words a text uses.
    private const int ThreeDigitBits = 100;
    private const int FourDigitBits = ThreeDigitBits + 1000;
    private const int AiSetWords = (FourDigitBits + 10000 + 63) / 64;
    private const int SetWords = AiSetWords + (AiSetWords + 63) / 64;
    private const int DigitalSignatureBit = FourDigitBits + 8030;

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
        if (!TryNextChecked(text, ref position, year, out var element, out error))
            return false;
        // The set is cleared only now, so that text refused at its first element does not pay for it.
        Span<ulong> present = stackalloc ulong[SetWords];
        var repeats = false;
        while (true)
        {
            repeats |= Add(present, AiBit(text, element));
            if (position >= text.Length)
                break;
            if (!TryNextChecked(text, ref position, year, out element, out error))
                return false;
        }

        return CheckPairings(text, present, repeats, out error);
    }

    /// <summary>The next element, refused unless the table knows its AI and its value passes the checks of its entry.</summary>
    private static bool TryNextChecked(ReadOnlySpan<char> text, ref int position, int year, out Gs1SplitElement element, out Gs1Error error)
    {
        if (!Gs1Splitter.TryNext(text, ref position, out element, out error))
            return false;
        if (!element.Known)
        {
            error = new Gs1Error(Gs1ErrorKind.UnknownAi, element.AiStart, element.AiLength);
            return false;
        }
        return CheckValue(text.Slice(element.ValueStart, element.ValueLength), element.ValueStart, element.Entry, year, out error);
    }

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
    /// The rules between elements, in the order the Syntax Engine runs its validations: every exclusion, then every
    /// requisite, then a repeated AI with another value, then the digital signature's serialised key. Within a rule
    /// the elements are asked in the order of the text. Given text that does not split it gives the splitter's
    /// error, since it builds the set of the text's AIs first. Its one caller in the library, the (AI) text builder,
    /// passes text that splits.
    /// </summary>
    public static bool CheckPairings(ReadOnlySpan<char> text, out Gs1Error error)
    {
        error = default;
        Span<ulong> present = stackalloc ulong[SetWords];
        var repeats = false;
        var position = 0;
        while (position < text.Length)
        {
            if (!Gs1Splitter.TryNext(text, ref position, out var element, out error))
                return false;
            repeats |= Add(present, AiBit(text, element));
        }
        return CheckPairings(text, present, repeats, out error);
    }

    /// <summary>The rules between elements, given the set of the AIs in the text and whether any AI is there twice.</summary>
    private static bool CheckPairings(ReadOnlySpan<char> text, ReadOnlySpan<ulong> present, bool repeats, out Gs1Error error)
    {
        error = default;
        // An exclusion anywhere in the text is reported before a missing requisite. So the first element that
        // misses one is only noted, and reported when every element has been asked for its exclusions.
        var missing = default(Gs1SplitElement);
        var isMissing = false;
        var position = 0;
        while (position < text.Length)
        {
            if (!Gs1Splitter.TryNext(text, ref position, out var element, out error))
                return false;
            var entry = element.Entry;
            var self = AiBit(text, element);
            for (var x = 0; x < entry.ExclusionCount; x++)
            {
                var pattern = entry.Exclusion(x);
                if (!HasOther(present, pattern, self))
                    continue;
                // The set says that an excluded AI is there and not where. The place takes one scan, once a text.
                FindMatch(text, element, pattern, out var excluded);
                var last = excluded.AiStart > element.AiStart ? excluded : element;
                error = new Gs1Error(Gs1ErrorKind.ExcludedAiPair, last.AiStart, last.End - last.AiStart);
                return false;
            }
            for (var g = 0; g < entry.RequisiteGroupCount && !isMissing; g++)
            {
                if (!IsSatisfied(present, self, entry.RequisiteGroup(g)))
                {
                    missing = element;
                    isMissing = true;
                }
            }
        }
        if (isMissing)
        {
            error = new Gs1Error(Gs1ErrorKind.MissingRequiredAi, missing.AiStart, missing.End - missing.AiStart);
            return false;
        }

        if (repeats && !CheckRepeats(text, present, out error))
            return false;
        return !Contains(present, DigitalSignatureBit) || CheckSerialisedKey(text, out error);
    }

    /// <summary>
    /// An AI may repeat with one value only. Of the AIs that repeat with another, the refusal is for the one whose
    /// first element comes first in the text, at its first element with another value: the place where comparing
    /// each element with every later one would stop.
    /// </summary>
    /// <remarks>
    /// One pass, with each AI's first value in a table indexed by the AI's rank in the set: the number of AIs of the
    /// text below it. A table indexed by the AI itself would need a slot for every number of the set's range, and
    /// this one has a slot for each AI the text holds.
    /// </remarks>
    private static bool CheckRepeats(ReadOnlySpan<char> text, ReadOnlySpan<ulong> present, out Gs1Error error)
    {
        error = default;
        // The AIs below each word that holds one, so that a rank takes one count within one word. The words are
        // taken from the set's last three, lowest first, so that a text of few AIs counts few words.
        Span<ushort> below = stackalloc ushort[AiSetWords];
        var count = 0;
        for (var summary = AiSetWords; summary < present.Length; summary++)
        {
            for (var words = present[summary]; words != 0; words &= words - 1)
            {
                var word = (summary - AiSetWords) * 64 + PopCount((words & (~words + 1)) - 1);
                below[word] = (ushort)count;
                count += PopCount(present[word]);
            }
        }

        // For each AI by rank, where its first value starts, plus one so that 0 is an AI not met yet, and its length.
        Span<int> firsts = stackalloc int[2 * count];
        var refusedFirst = int.MaxValue;
        var refused = default(Gs1SplitElement);
        var position = 0;
        while (position < text.Length)
        {
            if (!Gs1Splitter.TryNext(text, ref position, out var element, out error))
                return false;
            var bit = AiBit(text, element);
            var slot = 2 * (below[bit >> 6] + PopCount(present[bit >> 6] & ((1UL << (bit & 63)) - 1)));
            var firstStart = firsts[slot] - 1;
            if (firstStart < 0)
            {
                firsts[slot] = element.ValueStart + 1;
                firsts[slot + 1] = element.ValueLength;
                continue;
            }
            // An AI whose first element comes after that of the AI found so far cannot be the answer, nor can a
            // later element of the AI found, so only the first element with another value of each AI is kept.
            if (firstStart >= refusedFirst)
                continue;
            if (!text.Slice(element.ValueStart, element.ValueLength).SequenceEqual(text.Slice(firstStart, firsts[slot + 1])))
            {
                refusedFirst = firstStart;
                refused = element;
            }
        }

        if (refusedFirst == int.MaxValue)
            return true;
        error = new Gs1Error(Gs1ErrorKind.RepeatedAi, refused.AiStart, refused.End - refused.AiStart);
        return false;
    }

    /// <summary>
    /// The Syntax Engine's one rule beside the dictionary's pairings (its <c>validateDigSigRequiresSerialisedKey</c>,
    /// which cannot be turned off): a digital signature (8030) signs a serialised key, so with (8030) present every
    /// (253), (255) and (8003) must carry its optional serial component, whichever requisite alternative let
    /// (8030) in. Reported at the key element, where the engine reports it too ("Serial component must be present
    /// for AI (253) when used with AI (8030)"), as a missing requisite, the kind the test corpus maps that message to.
    /// The caller runs it for a text that holds (8030).
    /// </summary>
    private static bool CheckSerialisedKey(ReadOnlySpan<char> text, out Gs1Error error)
    {
        error = default;
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

    /// <summary>A requisite group holds when one of its alternatives does: every member present as another AI.</summary>
    private static bool IsSatisfied(ReadOnlySpan<ulong> present, int self, in Gs1AiTable.Gs1RequisiteGroup group)
    {
        for (var a = 0; a < group.AlternativeCount; a++)
        {
            var alternative = group.Alternative(a);
            var all = true;
            for (var m = 0; m < alternative.MemberCount && all; m++)
                all = HasOther(present, alternative.Member(m), self);
            if (all)
                return true;
        }
        return false;
    }

    /// <summary>Whether an element's value reaches into an optional component of its entry (true also for an entry that has none).</summary>
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

    /// <summary>The first element whose AI matches the pattern and is not this element's AI.</summary>
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

    /// <summary>
    /// Whether the set holds an AI that the pattern matches and that is not the AI of <paramref name="self"/>. The
    /// keys a pattern matches are taken as the range from its first to its last, which they are while the
    /// wildcards of every pattern in the table are its last digits.
    /// </summary>
    private static bool HasOther(ReadOnlySpan<ulong> present, in Gs1AiTable.Gs1AiPattern pattern, int self)
    {
        var first = KeyBit(pattern.FirstKey);
        var last = KeyBit(pattern.LastKey);
        for (var word = first >> 6; word <= last >> 6; word++)
        {
            var bits = present[word];
            if (word == first >> 6)
                bits &= ulong.MaxValue << (first & 63);
            if (word == last >> 6)
                bits &= ulong.MaxValue >> (63 - (last & 63));
            if (word == self >> 6)
                bits &= ~(1UL << (self & 63));
            if (bits != 0)
                return true;
        }
        return false;
    }

    /// <summary>The bit of an element's AI in a set of AIs.</summary>
    private static int AiBit(ReadOnlySpan<char> text, in Gs1SplitElement element)
    {
        var value = 0;
        for (var i = 0; i < element.AiLength; i++)
            value = value * 10 + (text[element.AiStart + i] - '0');
        return KeyBit(element.AiLength * 10000 + value);
    }

    /// <summary>The bit of a table key (<c>length * 10000 + value</c>) in a set of AIs.</summary>
    private static int KeyBit(int key)
        => key < 30000 ? key - 20000 : key < 40000 ? key - 30000 + ThreeDigitBits : key - 40000 + FourDigitBits;

    /// <summary>Adds an AI to a set, notes its word among the words that hold an AI, and says whether it was there already.</summary>
    // Inlined on request: it runs for each element, and the note of the word takes it past the size the JIT inlines
    // on its own.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Add(Span<ulong> set, int bit)
    {
        var word = bit >> 6;
        ref var bits = ref set[word];
        var mask = 1UL << (bit & 63);
        var was = (bits & mask) != 0;
        bits |= mask;
        set[AiSetWords + (word >> 6)] |= 1UL << (word & 63);
        return was;
    }

    private static bool Contains(ReadOnlySpan<ulong> set, int bit) => (set[bit >> 6] & (1UL << (bit & 63))) != 0;

    /// <summary>The number of bits set. Counted here, since the runtime's count is missing from the netstandard targets.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PopCount(ulong value)
    {
        value -= (value >> 1) & 0x5555555555555555UL;
        value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
        value = (value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
        return (int)((value * 0x0101010101010101UL) >> 56);
    }
}
