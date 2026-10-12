namespace FeatherQR.Gs1.Internals;

/// <summary>Where one element sits in its element string: the AI, the value, and the table entry when the AI is known.</summary>
internal readonly struct Gs1SplitElement
{
    public Gs1SplitElement(int aiStart, int aiLength, int valueStart, int valueLength, bool known, Gs1AiTable.Gs1AiEntry entry)
    {
        AiStart = aiStart;
        AiLength = aiLength;
        ValueStart = valueStart;
        ValueLength = valueLength;
        Known = known;
        Entry = entry;
    }

    public int AiStart { get; }
    public int AiLength { get; }
    public int ValueStart { get; }
    public int ValueLength { get; }
    /// <summary>Whether the table has the AI. When it does not, the value ran to the next separator.</summary>
    public bool Known { get; }
    public Gs1AiTable.Gs1AiEntry Entry { get; }

    public int End => ValueStart + ValueLength;
}

/// <summary>
/// The split of an element string, shared by every reader and check: the AI's length comes from its first
/// two digits (Table 7-5), an AI of predefined length (Table 7-6, known to the table or not) takes exactly
/// its length, every other value runs to the next separator, and one separator after a value is consumed
/// whether or not the AI needed it (General Specifications 7.8.6.3 asks a processing routine to tolerate it).
/// </summary>
internal static class Gs1Splitter
{
    public const char Separator = '\u001D';

    /// <summary>
    /// Reads the element at <paramref name="position"/> and moves <paramref name="position"/> past it and its
    /// separator. <c>false</c> with the error when no element can start there: a separator (at the start or
    /// doubled), digits that start no AI, an AI cut short, or a fixed-length value cut short.
    /// </summary>
    public static bool TryNext(ReadOnlySpan<char> text, ref int position, out Gs1SplitElement element, out Gs1Error error)
    {
        element = default;
        error = default;
        var start = position;
        var length = text.Length;
        if (start >= length)
            return false;

        if (text[start] == Separator)
        {
            error = new Gs1Error(Gs1ErrorKind.UnexpectedSeparator, start, 1);
            return false;
        }

        var remaining = length - start;
        if (remaining < 2 || !IsDigit(text[start]) || !IsDigit(text[start + 1]))
        {
            error = new Gs1Error(Gs1ErrorKind.UnknownAi, start, Math.Min(remaining, 2));
            return false;
        }

        var aiLength = Gs1AiTable.AiLength(text[start], text[start + 1]);
        if (aiLength == 0)
        {
            error = new Gs1Error(Gs1ErrorKind.UnknownAi, start, 2);
            return false;
        }
        if (aiLength > remaining)
        {
            error = new Gs1Error(Gs1ErrorKind.UnknownAi, start, remaining);
            return false;
        }
        for (var i = 2; i < aiLength; i++)
        {
            if (!IsDigit(text[start + i]))
            {
                error = new Gs1Error(Gs1ErrorKind.UnknownAi, start, aiLength);
                return false;
            }
        }

        var ai = text.Slice(start, aiLength);
        var known = Gs1AiTable.TryFind(ai, out var entry);
        var valueStart = start + aiLength;
        int valueLength;
        // Only an element string of predefined length (Table 7-6) is read by its length, and it is whether the table
        // knows the AI or not, since that table is frozen (7.8.3): a known AI gives it through its components, an
        // unknown one through its prefix. Any other fixed-length AI must be followed by a separator (7.8.6.2), so
        // its value runs to the separator and its length is checked after. A separator inside the window cuts the
        // value short, as the engine reads it, rather than becoming part of it.
        bool predefined;
        var fixedLength = 0;
        if (known)
            predefined = entry.IsPredefinedLength && TryGetFixedLength(entry, out fixedLength);
        else
            predefined = Gs1AiTable.TryGetPredefinedLength(text[start], text[start + 1], out fixedLength);
        if (predefined)
        {
            if (!known)
                fixedLength -= aiLength;
            var window = text.Slice(valueStart, Math.Min(fixedLength, length - valueStart));
            var cut = window.IndexOf(Separator);
            if (cut >= 0 || window.Length < fixedLength)
            {
                error = new Gs1Error(Gs1ErrorKind.ValueTooShort, valueStart, cut >= 0 ? cut : window.Length);
                return false;
            }
            valueLength = fixedLength;
        }
        else
        {
            var separator = text.Slice(valueStart).IndexOf(Separator);
            valueLength = separator < 0 ? length - valueStart : separator;
        }

        var next = valueStart + valueLength;
        if (next < length && text[next] == Separator)
            next++;

        element = new Gs1SplitElement(start, aiLength, valueStart, valueLength, known, entry);
        position = next;
        return true;
    }

    /// <summary>Whether a builder must put a separator after this AI's value when another element follows: every AI but one of predefined length (7.8.6.2).</summary>
    public static bool NeedsSeparator(in Gs1AiTable.Gs1AiEntry entry) => !(entry.IsPredefinedLength && TryGetFixedLength(entry, out _));

    /// <summary>The value length of an entry whose components are all fixed and mandatory, which is the only case a reader can take without a separator.</summary>
    public static bool TryGetFixedLength(in Gs1AiTable.Gs1AiEntry entry, out int length)
    {
        length = 0;
        for (var i = 0; i < entry.ComponentCount; i++)
        {
            var component = entry.Component(i);
            if (component.IsVariable || component.IsOptional)
            {
                length = 0;
                return false;
            }
            length += component.MaxLength;
        }
        return true;
    }

    private static bool IsDigit(char c) => (uint)(c - '0') <= 9;
}
