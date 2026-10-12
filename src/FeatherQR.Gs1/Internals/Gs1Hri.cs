using System.Buffers;

namespace FeatherQR.Gs1.Internals;

/// <summary>
/// The (AI) text of an element string and back. The text is <c>(AI)value</c> pairs with nothing between them,
/// the AI in parentheses and a literal parenthesis in a value written <c>\(</c>, which is the GS1 HRI form
/// (General Specifications 4.14) minus the blank the specification allows after the closing parenthesis.
/// Formatting runs only what splitting needs. Building runs every check: each value is checked against its
/// entry as it is copied, then the pairing rules run over the element string built. Errors point into the
/// text the caller gave, escapes included.
/// </summary>
internal static class Gs1Hri
{
    private const char Escape = '\\';
    private const char Open = '(';
    private const char Close = ')';

    /// <summary>Above this many characters a scratch buffer is rented instead of taken from the stack.</summary>
    private const int StackLimit = 512;

    // ----- element string -> (AI) text -----

    public static bool TryToHri(ReadOnlySpan<char> elementString, out string hri, out Gs1Error error)
    {
        hri = "";
        if (!Format(elementString, Span<char>.Empty, out var length, out error))
            return false;
        if (length == 0)
            return true;

        char[]? rented = null;
        Span<char> buffer = length <= StackLimit ? stackalloc char[StackLimit] : (rented = ArrayPool<char>.Shared.Rent(length));
        Format(elementString, buffer, out _, out _);
        hri = buffer.Slice(0, length).ToString();
        if (rented is not null)
            ArrayPool<char>.Shared.Return(rented);
        return true;
    }

    public static bool TryToHri(ReadOnlySpan<char> elementString, Span<char> destination, out int charsWritten, out Gs1Error error)
    {
        charsWritten = 0;
        if (!Format(elementString, destination, out var length, out error))
            return false;
        if (length > destination.Length)
        {
            error = new Gs1Error(Gs1ErrorKind.DestinationTooSmall, 0, 0);
            return false;
        }
        charsWritten = length;
        return true;
    }

    public static bool TryGetToHriLength(ReadOnlySpan<char> elementString, out int length, out Gs1Error error)
        => Format(elementString, Span<char>.Empty, out length, out error);

    /// <summary>
    /// Writes the text where it fits in <paramref name="destination"/> and counts it in full either way, so the
    /// same pass serves the length twin and the span form. <c>false</c> with the error, and no length, at the
    /// first element the splitter cannot read.
    /// </summary>
    private static bool Format(ReadOnlySpan<char> elementString, Span<char> destination, out int length, out Gs1Error error)
    {
        length = 0;
        error = default;
        if (elementString.Length == 0)
        {
            error = new Gs1Error(Gs1ErrorKind.InvalidText, 0, 0);
            return false;
        }

        var written = 0;
        var position = 0;
        while (position < elementString.Length)
        {
            if (!Gs1Splitter.TryNext(elementString, ref position, out var element, out error))
                return false;
            Put(destination, ref written, Open);
            for (var i = 0; i < element.AiLength; i++)
                Put(destination, ref written, elementString[element.AiStart + i]);
            Put(destination, ref written, Close);
            for (var i = 0; i < element.ValueLength; i++)
            {
                var c = elementString[element.ValueStart + i];
                if (c == Open)
                    Put(destination, ref written, Escape);
                Put(destination, ref written, c);
            }
        }
        length = written;
        return true;
    }

    private static void Put(Span<char> destination, ref int written, char c)
    {
        if (written < destination.Length)
            destination[written] = c;
        written++;
    }

    // ----- (AI) text -> element string -----

    public static bool TryFromHri(ReadOnlySpan<char> hri, int year, out string elementString, out Gs1Error error)
    {
        elementString = "";
        if (hri.Length == 0)
        {
            error = new Gs1Error(Gs1ErrorKind.InvalidText, 0, 0);
            return false;
        }

        char[]? rented = null;
        Span<char> buffer = hri.Length <= StackLimit ? stackalloc char[StackLimit] : (rented = ArrayPool<char>.Shared.Rent(hri.Length));
        var built = Build(hri, year, buffer, out var length, out error);
        if (built)
            elementString = buffer.Slice(0, length).ToString();
        if (rented is not null)
            ArrayPool<char>.Shared.Return(rented);
        return built;
    }

    public static bool TryFromHri(ReadOnlySpan<char> hri, int year, Span<char> destination, out int charsWritten, out Gs1Error error)
    {
        charsWritten = 0;
        if (hri.Length == 0)
        {
            error = new Gs1Error(Gs1ErrorKind.InvalidText, 0, 0);
            return false;
        }

        // The element string is never longer than its text, so a destination at least that long is built in directly.
        if (destination.Length >= hri.Length)
        {
            if (!Build(hri, year, destination, out charsWritten, out error))
                charsWritten = 0;
            return charsWritten > 0;
        }

        char[]? rented = null;
        Span<char> buffer = hri.Length <= StackLimit ? stackalloc char[StackLimit] : (rented = ArrayPool<char>.Shared.Rent(hri.Length));
        var built = Build(hri, year, buffer, out var length, out error);
        if (built)
        {
            if (length <= destination.Length)
            {
                buffer.Slice(0, length).CopyTo(destination);
                charsWritten = length;
            }
            else
            {
                error = new Gs1Error(Gs1ErrorKind.DestinationTooSmall, 0, 0);
                built = false;
            }
        }
        if (rented is not null)
            ArrayPool<char>.Shared.Return(rented);
        return built;
    }

    public static bool TryGetFromHriLength(ReadOnlySpan<char> hri, int year, out int length, out Gs1Error error)
    {
        length = 0;
        if (hri.Length == 0)
        {
            error = new Gs1Error(Gs1ErrorKind.InvalidText, 0, 0);
            return false;
        }

        char[]? rented = null;
        Span<char> buffer = hri.Length <= StackLimit ? stackalloc char[StackLimit] : (rented = ArrayPool<char>.Shared.Rent(hri.Length));
        var built = Build(hri, year, buffer, out length, out error);
        if (rented is not null)
            ArrayPool<char>.Shared.Return(rented);
        return built;
    }

    /// <summary>
    /// Builds the element string of a non-empty text into <paramref name="buffer"/>, which is at least as long
    /// as the text. Each pair is read, its AI looked up, its value copied with escapes resolved and checked
    /// against the entry, and a separator put after every value whose AI is not of predefined length when
    /// another follows (7.8.6.2). The pairing rules then run over the result and their errors, which name an
    /// element of the element string, are mapped back to its pair in the text.
    /// </summary>
    private static bool Build(ReadOnlySpan<char> hri, int year, Span<char> buffer, out int length, out Gs1Error error)
    {
        length = 0;
        var position = 0;
        var written = 0;
        var needsSeparator = false;
        while (position < hri.Length)
        {
            if (!TryNextPair(hri, ref position, out var pair, out error))
                return false;
            var ai = hri.Slice(pair.AiStart, pair.AiLength);
            if (!Gs1AiTable.TryFind(ai, out var entry))
            {
                error = new Gs1Error(Gs1ErrorKind.UnknownAi, pair.AiStart, pair.AiLength);
                return false;
            }
            if (pair.ValueLength == 0)
            {
                error = new Gs1Error(Gs1ErrorKind.EmptyValue, pair.ValueStart, 0);
                return false;
            }

            if (needsSeparator)
                buffer[written++] = Gs1Splitter.Separator;
            ai.CopyTo(buffer.Slice(written));
            written += pair.AiLength;
            var hriValue = hri.Slice(pair.ValueStart, pair.ValueLength);
            var valueStart = written;
            written += Unescape(hriValue, buffer.Slice(written));

            if (!Gs1Validator.CheckValue(buffer.Slice(valueStart, written - valueStart), 0, entry, year, out var valueError))
            {
                var from = IndexInText(hriValue, valueError.Offset);
                var to = IndexInText(hriValue, valueError.Offset + valueError.Length);
                error = new Gs1Error(valueError.Kind, pair.ValueStart + from, to - from);
                return false;
            }
            needsSeparator = !(entry.IsPredefinedLength && Gs1Splitter.TryGetFixedLength(entry, out _));
        }

        if (!Gs1Validator.CheckPairings(buffer.Slice(0, written), out var pairingError))
        {
            error = MapElement(hri, pairingError);
            return false;
        }
        length = written;
        error = default;
        return true;
    }

    /// <summary>One <c>(AI)value</c> pair of the text: the AI's digits and the value as written, escapes included.</summary>
    private readonly struct Pair
    {
        public Pair(int aiStart, int aiLength, int valueStart, int valueLength)
        {
            AiStart = aiStart;
            AiLength = aiLength;
            ValueStart = valueStart;
            ValueLength = valueLength;
        }

        public int AiStart { get; }
        public int AiLength { get; }
        public int ValueStart { get; }
        public int ValueLength { get; }
    }

    /// <summary>
    /// Reads the pair at <paramref name="position"/> and moves it past the value, which runs to the next
    /// unescaped opening parenthesis or the end. <c>false</c> with <see cref="Gs1ErrorKind.InvalidText"/> when
    /// the text is not a pair there: no opening parenthesis, no closing one, or an AI that is not 2 to 4 digits.
    /// </summary>
    private static bool TryNextPair(ReadOnlySpan<char> hri, ref int position, out Pair pair, out Gs1Error error)
    {
        pair = default;
        error = default;
        var start = position;
        if (hri[start] != Open)
        {
            error = new Gs1Error(Gs1ErrorKind.InvalidText, start, 1);
            return false;
        }
        var close = hri.Slice(start + 1).IndexOf(Close);
        if (close < 0)
        {
            error = new Gs1Error(Gs1ErrorKind.InvalidText, start, hri.Length - start);
            return false;
        }

        var aiStart = start + 1;
        var aiLength = close;
        if (aiLength < 2 || aiLength > 4)
        {
            error = new Gs1Error(Gs1ErrorKind.InvalidText, aiStart, aiLength);
            return false;
        }
        for (var i = 0; i < aiLength; i++)
        {
            if (!Gs1Charsets.IsDigit(hri[aiStart + i]))
            {
                error = new Gs1Error(Gs1ErrorKind.InvalidText, aiStart, aiLength);
                return false;
            }
        }

        var valueStart = aiStart + aiLength + 1;
        var end = valueStart;
        while (end < hri.Length)
        {
            if (hri[end] == Escape && end + 1 < hri.Length && hri[end + 1] == Open)
            {
                end += 2;
                continue;
            }
            if (hri[end] == Open)
                break;
            end++;
        }

        pair = new Pair(aiStart, aiLength, valueStart, end - valueStart);
        position = end;
        return true;
    }

    /// <summary>Copies a value as written to its element string form (<c>\(</c> becomes <c>(</c>) and returns the length written.</summary>
    private static int Unescape(ReadOnlySpan<char> hriValue, Span<char> destination)
    {
        var written = 0;
        for (var i = 0; i < hriValue.Length; i++)
        {
            var c = hriValue[i];
            if (c == Escape && i + 1 < hriValue.Length && hriValue[i + 1] == Open)
            {
                c = Open;
                i++;
            }
            destination[written++] = c;
        }
        return written;
    }

    /// <summary>The place in a value as written of the character at <paramref name="index"/> of its element string form (its length when <paramref name="index"/> is at the end).</summary>
    private static int IndexInText(ReadOnlySpan<char> hriValue, int index)
    {
        var count = 0;
        var i = 0;
        while (i < hriValue.Length)
        {
            if (count == index)
                return i;
            i += hriValue[i] == Escape && i + 1 < hriValue.Length && hriValue[i + 1] == Open ? 2 : 1;
            count++;
        }
        return hriValue.Length;
    }

    /// <summary>
    /// The pair in the text of an error that names an element of the element string built from it: the pairs
    /// are walked again with the same separator rule to find the one whose element holds the error's offset, and
    /// the whole pair, parentheses included, is the place. The error stays as it is when no pair holds it.
    /// </summary>
    private static Gs1Error MapElement(ReadOnlySpan<char> hri, Gs1Error error)
    {
        var position = 0;
        var written = 0;
        var needsSeparator = false;
        while (position < hri.Length)
        {
            if (!TryNextPair(hri, ref position, out var pair, out _))
                break;
            if (needsSeparator)
                written++;
            var elementStart = written;
            written += pair.AiLength + UnescapedLength(hri.Slice(pair.ValueStart, pair.ValueLength));
            if (error.Offset >= elementStart && error.Offset < written)
            {
                var textStart = pair.AiStart - 1;
                return new Gs1Error(error.Kind, textStart, pair.ValueStart + pair.ValueLength - textStart);
            }
            needsSeparator = !(Gs1AiTable.TryFind(hri.Slice(pair.AiStart, pair.AiLength), out var entry) && entry.IsPredefinedLength && Gs1Splitter.TryGetFixedLength(entry, out _));
        }
        return error;
    }

    private static int UnescapedLength(ReadOnlySpan<char> hriValue)
    {
        var length = 0;
        for (var i = 0; i < hriValue.Length; i++)
        {
            if (hriValue[i] == Escape && i + 1 < hriValue.Length && hriValue[i + 1] == Open)
                i++;
            length++;
        }
        return length;
    }
}
