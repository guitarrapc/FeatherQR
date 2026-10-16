using System.Buffers;

namespace FeatherQR.Gs1.Internals;

/// <summary>
/// A GS1 Digital Link URI (URI Syntax 1.7.0, uncompressed) from an element string and back.
/// </summary>
/// <remarks>
/// <para>
/// Building takes the first element whose AI is a primary key, then the key qualifier sequence of that key that
/// matches the most elements among the sequences that leave only data attributes outside the path (failing that,
/// the first sequence that matches the most), writes the key and its present qualifiers in the sequence's order as
/// <c>/AI/value</c>, and every other element as a query parameter in the lexical order of the AIs, each AI once.
/// Every character outside the URI's unreserved set is percent-encoded. The result is the canonical form of
/// section 4.12 under the canonical stem.
/// </para>
/// <para>
/// Reading follows the Syntax Engine's parser: an <c>http</c> or <c>https</c> scheme (case-insensitive, after RFC
/// 3986), a host, then the path walked backwards in <c>/AI/value</c> pairs until one whose AI is a primary key, so
/// any stem may come before it. The pairs after the key must be an ordered subsequence of one of the key's
/// qualifier sequences. Query parameters are percent-decoded (<c>+</c> is a blank there), a numeric key must be an
/// AI the table knows and a data attribute, a key that is not numeric is ignored, and a key the path or the query
/// already holds, equal values included, or that belongs in the path, is refused. Values are read as
/// they are, except what the element string needs to split back into the same elements: a predefined-length AI's
/// value has its length, and an escape never decodes to a control character.
/// </para>
/// </remarks>
internal static class Gs1DigitalLink
{
    public const string CanonicalStem = "https://id.gs1.org";

    private const int StackLimit = 512;
    private const string ReservedUriCharacters = ":/?#[]@!$&'()*+,;=%";
    private const string BadHostCharacters = "_~?#@!$&'()*+,;=%";

    // ----- the stem -----

    /// <summary>The stem to build with: the canonical one for <c>null</c>, else the caller's with one trailing slash removed. Throws for anything but an http or https URI prefix with a host and no query or fragment.</summary>
    public static ReadOnlySpan<char> ValidateStem(string? stem)
    {
        if (stem is null)
            return CanonicalStem.AsSpan();

        var text = stem.AsSpan();
        var valid = TryReadScheme(text, out var hostStart);
        if (valid)
        {
            var slash = text.Slice(hostStart).IndexOf('/');
            var hostEnd = slash < 0 ? text.Length : hostStart + slash;
            valid = IsAuthority(text.Slice(hostStart, hostEnd - hostStart));
            for (var i = 0; valid && i < text.Length; i++)
                valid = IsUriCharacter(text[i]) && text[i] != '?' && text[i] != '#';
        }
        if (!valid)
            throw new ArgumentException("The stem must be an http or https URI prefix with a host and no query or fragment, such as https://id.gs1.org.", nameof(stem));

        return text[text.Length - 1] == '/' ? text.Slice(0, text.Length - 1) : text;
    }

    /// <summary>
    /// Whether a stem's authority is a host with an optional port (RFC 3986 section 3.2): a bracketed IPv6
    /// address of hexadecimal digits, colons and dots, or a registered name or IPv4 address of letters, digits,
    /// dots and hyphens, then nothing or a colon and one or more digits. Stricter than the reader, which follows
    /// the Syntax Engine and refuses only a short list of characters, because the stem is the caller's option and
    /// the contract promises a host.
    /// </summary>
    private static bool IsAuthority(ReadOnlySpan<char> authority)
    {
        if (authority.Length == 0)
            return false;

        ReadOnlySpan<char> rest;
        if (authority[0] == '[')
        {
            var close = authority.IndexOf(']');
            if (close < 2)
                return false;
            for (var i = 1; i < close; i++)
            {
                var c = authority[i];
                if (HexValue(c) < 0 && c != ':' && c != '.')
                    return false;
            }
            rest = authority.Slice(close + 1);
        }
        else
        {
            var colon = authority.IndexOf(':');
            var host = colon < 0 ? authority : authority.Slice(0, colon);
            if (host.Length == 0)
                return false;
            for (var i = 0; i < host.Length; i++)
            {
                var c = host[i];
                if (!((uint)(c - 'a') <= 25 || (uint)(c - 'A') <= 25 || (uint)(c - '0') <= 9 || c == '.' || c == '-'))
                    return false;
            }
            rest = colon < 0 ? default : authority.Slice(colon);
        }

        if (rest.Length == 0)
            return true;
        if (rest[0] != ':' || rest.Length < 2)
            return false;
        return IsAllDigits(rest.Slice(1));
    }

    // ----- element string -> URI -----

    public static bool TryToDigitalLink(ReadOnlySpan<char> elementString, int year, string? stem, out string uri, out Gs1Error error)
    {
        // Build writes what fits and counts the whole URI, so a URI that fits the stack buffer needs no second pass.
        // An element string of StackLimit characters or more seldom has a URI that fits, so counting it first saves
        // a write that would be thrown away. Each of its elements is written at least as long as it stands there,
        // and only an AI repeated with one value, which the URI writes once, can make the URI fit.
        var stemText = ValidateStem(stem);
        uri = "";
        int length;
        if (elementString.Length < StackLimit)
        {
            Span<char> buffer = stackalloc char[StackLimit];
            if (!Build(elementString, year, stemText, buffer, out length, out error))
                return false;
            if (length <= StackLimit)
            {
                uri = buffer.Slice(0, length).ToString();
                return true;
            }
        }
        else if (!Build(elementString, year, stemText, Span<char>.Empty, out length, out error))
        {
            return false;
        }

        var rented = ArrayPool<char>.Shared.Rent(length);
        Build(elementString, year, stemText, rented, out _, out _);
        uri = new string(rented, 0, length);
        ArrayPool<char>.Shared.Return(rented);
        return true;
    }

    public static bool TryToDigitalLink(ReadOnlySpan<char> elementString, int year, string? stem, Span<char> destination, out int charsWritten, out Gs1Error error)
        => TryBuild(elementString, year, ValidateStem(stem), destination, out charsWritten, out error);

    /// <summary>The span form under a stem <see cref="ValidateStem"/> already returned, for a caller that sized its destination from that stem.</summary>
    public static bool TryBuild(ReadOnlySpan<char> elementString, int year, ReadOnlySpan<char> validatedStem, Span<char> destination, out int charsWritten, out Gs1Error error)
    {
        charsWritten = 0;
        if (!Build(elementString, year, validatedStem, destination, out var length, out error))
            return false;
        if (length > destination.Length)
        {
            error = new Gs1Error(Gs1ErrorKind.DestinationTooSmall, 0, 0);
            return false;
        }
        charsWritten = length;
        return true;
    }

    public static bool TryGetToDigitalLinkLength(ReadOnlySpan<char> elementString, int year, string? stem, out int length, out Gs1Error error)
        => Build(elementString, year, ValidateStem(stem), Span<char>.Empty, out length, out error);

    /// <summary>Writes the URI where it fits in <paramref name="destination"/> and counts it in full either way, after every check on the data and on what a URI can carry.</summary>
    private static bool Build(ReadOnlySpan<char> elementString, int year, ReadOnlySpan<char> stem, Span<char> destination, out int length, out Gs1Error error)
    {
        length = 0;
        if (!Gs1Validator.TryValidate(elementString, year, out error))
            return false;

        if (!FindKey(elementString, out var key))
        {
            error = new Gs1Error(Gs1ErrorKind.MissingPrimaryKey, 0, elementString.Length);
            return false;
        }
        var sequence = ChooseSequence(elementString, key);

        var position = 0;
        while (position < elementString.Length)
        {
            Gs1Splitter.TryNext(elementString, ref position, out var element, out _);
            if (!IsInPath(elementString, element, key, sequence) && !element.Entry.IsDigitalLinkAttribute)
            {
                error = new Gs1Error(Gs1ErrorKind.AiNotAllowedInUri, element.AiStart, element.AiLength);
                return false;
            }
        }

        var writer = new Writer(destination);
        writer.Put(stem);
        PutPathElement(ref writer, elementString, key);
        if (sequence >= 0)
        {
            var qualifiers = key.Entry.QualifierSequence(sequence);
            for (var i = 0; i < qualifiers.Count; i++)
            {
                if (FindElement(elementString, qualifiers.Qualifier(i), out var qualifier))
                    PutPathElement(ref writer, elementString, qualifier);
            }
        }

        ReadOnlySpan<char> previous = default;
        var first = true;
        while (FindNextAttribute(elementString, key, sequence, previous, out var attribute))
        {
            writer.Put(first ? '?' : '&');
            first = false;
            var ai = elementString.Slice(attribute.AiStart, attribute.AiLength);
            writer.Put(ai);
            writer.Put('=');
            PutEscaped(ref writer, elementString.Slice(attribute.ValueStart, attribute.ValueLength));
            previous = ai;
        }

        length = writer.Count;
        return true;
    }

    /// <summary>The first element whose AI is a Digital Link primary key.</summary>
    private static bool FindKey(ReadOnlySpan<char> elementString, out Gs1SplitElement key)
    {
        var position = 0;
        while (position < elementString.Length)
        {
            Gs1Splitter.TryNext(elementString, ref position, out key, out _);
            if (key.Entry.IsDigitalLinkKey)
                return true;
        }
        key = default;
        return false;
    }

    /// <summary>
    /// The key's qualifier sequence for the path, as an index into the entry's sequences or -1 for the key alone:
    /// among the choices that leave only data attributes outside the path, the one with the most qualifiers present,
    /// and failing any such choice, the one with the most qualifiers present. The first wins a tie, which is what
    /// the Syntax Engine does throughout.
    /// </summary>
    private static int ChooseSequence(ReadOnlySpan<char> elementString, in Gs1SplitElement key)
    {
        var best = -1;
        var bestPresent = 0;
        var bestViable = IsViable(elementString, key, -1);
        for (var s = 0; s < key.Entry.QualifierSequenceCount; s++)
        {
            var qualifiers = key.Entry.QualifierSequence(s);
            var present = 0;
            for (var i = 0; i < qualifiers.Count; i++)
            {
                if (FindElement(elementString, qualifiers.Qualifier(i), out _))
                    present++;
            }
            var viable = IsViable(elementString, key, s);
            if ((viable && !bestViable) || (viable == bestViable && present > bestPresent))
            {
                best = s;
                bestPresent = present;
                bestViable = viable;
            }
        }
        return best;
    }

    /// <summary>Whether every element outside the path this sequence gives is a data attribute.</summary>
    private static bool IsViable(ReadOnlySpan<char> elementString, in Gs1SplitElement key, int sequence)
    {
        var position = 0;
        while (position < elementString.Length)
        {
            Gs1Splitter.TryNext(elementString, ref position, out var element, out _);
            if (!IsInPath(elementString, element, key, sequence) && !element.Entry.IsDigitalLinkAttribute)
                return false;
        }
        return true;
    }

    /// <summary>Whether an element goes into the path: it is the key (or repeats its AI), or its AI is a qualifier of the chosen sequence.</summary>
    private static bool IsInPath(ReadOnlySpan<char> elementString, in Gs1SplitElement element, in Gs1SplitElement key, int sequence)
    {
        var ai = elementString.Slice(element.AiStart, element.AiLength);
        if (ai.SequenceEqual(elementString.Slice(key.AiStart, key.AiLength)))
            return true;
        if (sequence < 0)
            return false;
        var qualifiers = key.Entry.QualifierSequence(sequence);
        for (var i = 0; i < qualifiers.Count; i++)
        {
            if (qualifiers.Qualifier(i).Matches(ai))
                return true;
        }
        return false;
    }

    /// <summary>The first element whose AI matches the pattern.</summary>
    private static bool FindElement(ReadOnlySpan<char> elementString, in Gs1AiTable.Gs1AiPattern pattern, out Gs1SplitElement match)
    {
        var position = 0;
        while (position < elementString.Length)
        {
            Gs1Splitter.TryNext(elementString, ref position, out match, out _);
            if (pattern.Matches(elementString.Slice(match.AiStart, match.AiLength)))
                return true;
        }
        match = default;
        return false;
    }

    /// <summary>The attribute with the smallest AI above <paramref name="previous"/> (any AI when it is empty), so the query comes out in lexical order with each AI once.</summary>
    private static bool FindNextAttribute(ReadOnlySpan<char> elementString, in Gs1SplitElement key, int sequence, ReadOnlySpan<char> previous, out Gs1SplitElement next)
    {
        next = default;
        var found = false;
        var position = 0;
        while (position < elementString.Length)
        {
            Gs1Splitter.TryNext(elementString, ref position, out var element, out _);
            if (IsInPath(elementString, element, key, sequence))
                continue;
            var ai = elementString.Slice(element.AiStart, element.AiLength);
            if (previous.Length != 0 && ai.SequenceCompareTo(previous) <= 0)
                continue;
            if (found && ai.SequenceCompareTo(elementString.Slice(next.AiStart, next.AiLength)) >= 0)
                continue;
            next = element;
            found = true;
        }
        return found;
    }

    private static void PutPathElement(ref Writer writer, ReadOnlySpan<char> elementString, in Gs1SplitElement element)
    {
        writer.Put('/');
        writer.Put(elementString.Slice(element.AiStart, element.AiLength));
        writer.Put('/');
        PutEscaped(ref writer, elementString.Slice(element.ValueStart, element.ValueLength));
    }

    /// <summary>The value with every character outside the unreserved set percent-encoded, as UTF-8 bytes.</summary>
    private static void PutEscaped(ref Writer writer, ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (IsUnreserved(c))
            {
                writer.Put(c);
                continue;
            }

            int codePoint = c;
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                codePoint = char.ConvertToUtf32(c, value[i + 1]);
                i++;
            }
            if (codePoint < 0x80)
            {
                PutPercent(ref writer, codePoint);
            }
            else if (codePoint < 0x800)
            {
                PutPercent(ref writer, 0xC0 | (codePoint >> 6));
                PutPercent(ref writer, 0x80 | (codePoint & 0x3F));
            }
            else if (codePoint < 0x10000)
            {
                PutPercent(ref writer, 0xE0 | (codePoint >> 12));
                PutPercent(ref writer, 0x80 | ((codePoint >> 6) & 0x3F));
                PutPercent(ref writer, 0x80 | (codePoint & 0x3F));
            }
            else
            {
                PutPercent(ref writer, 0xF0 | (codePoint >> 18));
                PutPercent(ref writer, 0x80 | ((codePoint >> 12) & 0x3F));
                PutPercent(ref writer, 0x80 | ((codePoint >> 6) & 0x3F));
                PutPercent(ref writer, 0x80 | (codePoint & 0x3F));
            }
        }
    }

    private static void PutPercent(ref Writer writer, int value)
    {
        const string Hex = "0123456789ABCDEF";
        writer.Put('%');
        writer.Put(Hex[(value >> 4) & 0xF]);
        writer.Put(Hex[value & 0xF]);
    }

    /// <summary>Writes where it fits and counts everything, so one pass serves the length twin and the span form.</summary>
    private ref struct Writer
    {
        private readonly Span<char> _destination;

        public Writer(Span<char> destination)
        {
            _destination = destination;
            Count = 0;
        }

        public int Count { get; private set; }

        public void Put(char c)
        {
            if (Count < _destination.Length)
                _destination[Count] = c;
            Count++;
        }

        public void Put(ReadOnlySpan<char> text)
        {
            for (var i = 0; i < text.Length; i++)
                Put(text[i]);
        }
    }

    // ----- URI -> element string -----

    public static bool TryFromDigitalLink(ReadOnlySpan<char> uri, out string elementString, out Gs1Error error)
    {
        elementString = "";
        if (uri.Length == 0)
        {
            error = new Gs1Error(Gs1ErrorKind.NotAUri, 0, 0);
            return false;
        }

        char[]? rented = null;
        Span<char> buffer = uri.Length <= StackLimit ? stackalloc char[StackLimit] : (rented = ArrayPool<char>.Shared.Rent(uri.Length));
        var read = Read(uri, buffer, out var length, out error);
        if (read)
            elementString = buffer.Slice(0, length).ToString();
        if (rented is not null)
            ArrayPool<char>.Shared.Return(rented);
        return read;
    }

    public static bool TryFromDigitalLink(ReadOnlySpan<char> uri, Span<char> destination, out int charsWritten, out Gs1Error error)
    {
        charsWritten = 0;
        if (uri.Length == 0)
        {
            error = new Gs1Error(Gs1ErrorKind.NotAUri, 0, 0);
            return false;
        }

        // The element string is never longer than its URI, so a destination at least that long is read into directly.
        if (destination.Length >= uri.Length)
        {
            if (!Read(uri, destination, out charsWritten, out error))
                charsWritten = 0;
            return charsWritten > 0;
        }

        char[]? rented = null;
        Span<char> buffer = uri.Length <= StackLimit ? stackalloc char[StackLimit] : (rented = ArrayPool<char>.Shared.Rent(uri.Length));
        var read = Read(uri, buffer, out var length, out error);
        if (read)
        {
            if (length <= destination.Length)
            {
                buffer.Slice(0, length).CopyTo(destination);
                charsWritten = length;
            }
            else
            {
                error = new Gs1Error(Gs1ErrorKind.DestinationTooSmall, 0, 0);
                read = false;
            }
        }
        if (rented is not null)
            ArrayPool<char>.Shared.Return(rented);
        return read;
    }

    public static bool TryGetFromDigitalLinkLength(ReadOnlySpan<char> uri, out int length, out Gs1Error error)
    {
        length = 0;
        if (uri.Length == 0)
        {
            error = new Gs1Error(Gs1ErrorKind.NotAUri, 0, 0);
            return false;
        }

        char[]? rented = null;
        Span<char> buffer = uri.Length <= StackLimit ? stackalloc char[StackLimit] : (rented = ArrayPool<char>.Shared.Rent(uri.Length));
        var read = Read(uri, buffer, out length, out error);
        if (rented is not null)
            ArrayPool<char>.Shared.Return(rented);
        return read;
    }

    /// <summary>Reads a non-empty URI into <paramref name="buffer"/>, which is at least as long as the URI (the remarks on the class give the rules).</summary>
    public static bool Read(ReadOnlySpan<char> uri, Span<char> buffer, out int length, out Gs1Error error)
    {
        length = 0;
        error = default;

        if (!TryReadScheme(uri, out var hostStart))
        {
            error = new Gs1Error(Gs1ErrorKind.NotAUri, 0, uri.Length);
            return false;
        }
        for (var i = hostStart; i < uri.Length; i++)
        {
            if (!IsUriCharacter(uri[i]))
            {
                error = new Gs1Error(Gs1ErrorKind.NotAUri, i, 1);
                return false;
            }
        }
        var slash = uri.Slice(hostStart).IndexOf('/');
        if (slash < 0)
        {
            error = new Gs1Error(Gs1ErrorKind.NotAUri, uri.Length, 0);
            return false;
        }
        if (slash == 0)
        {
            error = new Gs1Error(Gs1ErrorKind.NotAUri, hostStart, 0);
            return false;
        }
        for (var i = hostStart; i < hostStart + slash; i++)
        {
            if (IsBadHostCharacter(uri[i]))
            {
                error = new Gs1Error(Gs1ErrorKind.NotAUri, i, 1);
                return false;
            }
        }

        var pathStart = hostStart + slash;
        var fragment = uri.Slice(pathStart).IndexOf('#');
        var end = fragment < 0 ? uri.Length : pathStart + fragment;
        var question = uri.Slice(pathStart, end - pathStart).IndexOf('?');
        var pathEnd = question < 0 ? end : pathStart + question;
        var queryStart = question < 0 ? end : pathEnd + 1;

        // The key: the path walked backwards in /AI/value pairs until one whose AI is a primary key.
        var keyStart = -1;
        var r = pathEnd;
        while (true)
        {
            var valueSlash = LastSlash(uri, pathStart, r);
            if (valueSlash < 0)
                break;
            var aiSlash = LastSlash(uri, pathStart, valueSlash);
            if (aiSlash < 0)
                break;
            if (!Gs1AiTable.TryFind(uri.Slice(aiSlash + 1, valueSlash - aiSlash - 1), out var entry))
                break;
            if (entry.IsDigitalLinkKey)
            {
                keyStart = aiSlash;
                break;
            }
            r = aiSlash;
        }
        if (keyStart < 0)
        {
            var lastSlash = LastSlash(uri, pathStart, pathEnd);
            var segmentStart = lastSlash + 1;
            if (lastSlash >= 0 && LooksCompressed(uri.Slice(segmentStart, pathEnd - segmentStart)))
                error = new Gs1Error(Gs1ErrorKind.CompressedUri, segmentStart, pathEnd - segmentStart);
            else
                error = new Gs1Error(Gs1ErrorKind.MissingPrimaryKey, pathStart, pathEnd - pathStart);
            return false;
        }

        // The path: the key, then its qualifiers, which must follow one of the key's sequences in order.
        var written = 0;
        var needsSeparator = false;
        var position = keyStart;
        Gs1AiTable.TryFind(uri.Slice(keyStart + 1, uri.Slice(keyStart + 1).IndexOf('/')), out var key);
        var sequenceCount = key.QualifierSequenceCount;
        Span<int> reached = stackalloc int[Math.Max(sequenceCount, 1)];
        Span<bool> alive = stackalloc bool[Math.Max(sequenceCount, 1)];
        for (var s = 0; s < sequenceCount; s++)
        {
            reached[s] = -1;
            alive[s] = true;
        }
        var isKey = true;
        while (position < pathEnd)
        {
            var aiStart = position + 1;
            var aiEnd = aiStart + uri.Slice(aiStart).IndexOf('/');
            var valueStart = aiEnd + 1;
            var next = uri.Slice(valueStart, pathEnd - valueStart).IndexOf('/');
            var valueEnd = next < 0 ? pathEnd : valueStart + next;
            var ai = uri.Slice(aiStart, aiEnd - aiStart);
            Gs1AiTable.TryFind(ai, out var entry);

            if (!isKey)
            {
                var anyAlive = false;
                for (var s = 0; s < sequenceCount; s++)
                {
                    if (!alive[s])
                        continue;
                    var qualifiers = key.QualifierSequence(s);
                    var matched = -1;
                    for (var q = reached[s] + 1; q < qualifiers.Count && matched < 0; q++)
                    {
                        if (qualifiers.Qualifier(q).Matches(ai))
                            matched = q;
                    }
                    if (matched < 0)
                        alive[s] = false;
                    else
                    {
                        reached[s] = matched;
                        anyAlive = true;
                    }
                }
                if (!anyAlive)
                {
                    error = new Gs1Error(Gs1ErrorKind.InvalidKeyQualifierSequence, aiStart, ai.Length);
                    return false;
                }
            }
            isKey = false;

            if (!WriteElement(buffer, ref written, ref needsSeparator, ai, entry, uri.Slice(valueStart, valueEnd - valueStart), valueStart, query: false, out error))
                return false;
            position = valueEnd;
        }
        // The query: numeric keys are AIs, in URI order, none repeating an AI the path or the query already holds.
        position = queryStart;
        while (position < end)
        {
            var amp = uri.Slice(position, end - position).IndexOf('&');
            var parameterEnd = amp < 0 ? end : position + amp;
            var parameter = uri.Slice(position, parameterEnd - position);
            var parameterStart = position;
            position = parameterEnd + 1;
            if (parameter.Length == 0)
                continue;
            var equals = parameter.IndexOf('=');
            if (equals < 0)
                continue;
            var ai = parameter.Slice(0, equals);
            // A key that is not numeric is an extension and is ignored. An empty key counts as numeric, as the engine counts it, and is no AI.
            if (ai.Length != 0 && !IsAllDigits(ai))
                continue;
            if (!Gs1AiTable.TryFind(ai, out var entry))
            {
                error = new Gs1Error(Gs1ErrorKind.UnknownAi, parameterStart, ai.Length);
                return false;
            }
            if (HoldsAi(buffer.Slice(0, written), ai))
            {
                error = new Gs1Error(Gs1ErrorKind.RepeatedAi, parameterStart, ai.Length);
                return false;
            }
            if (!entry.IsDigitalLinkAttribute)
            {
                error = new Gs1Error(Gs1ErrorKind.AiNotAllowedInUri, parameterStart, ai.Length);
                return false;
            }
            for (var s = 0; s < sequenceCount; s++)
            {
                if (!alive[s])
                    continue;
                var qualifiers = key.QualifierSequence(s);
                for (var q = 0; q < qualifiers.Count; q++)
                {
                    if (qualifiers.Qualifier(q).Matches(ai))
                    {
                        error = new Gs1Error(Gs1ErrorKind.InvalidKeyQualifierSequence, parameterStart, ai.Length);
                        return false;
                    }
                }
            }

            if (!WriteElement(buffer, ref written, ref needsSeparator, ai, entry, parameter.Slice(equals + 1), parameterStart + equals + 1, query: true, out error))
                return false;
        }

        length = written;
        return true;
    }

    /// <summary>One element into the buffer: a separator when the previous needs one, the AI, the value decoded, and the value's length checked when the AI is of predefined length.</summary>
    private static bool WriteElement(Span<char> buffer, ref int written, ref bool needsSeparator, ReadOnlySpan<char> ai, in Gs1AiTable.Gs1AiEntry entry, ReadOnlySpan<char> rawValue, int valueOffset, bool query, out Gs1Error error)
    {
        error = default;
        if (rawValue.Length == 0)
        {
            error = new Gs1Error(Gs1ErrorKind.EmptyValue, valueOffset, 0);
            return false;
        }

        if (needsSeparator)
            buffer[written++] = Gs1Splitter.Separator;
        ai.CopyTo(buffer.Slice(written));
        written += ai.Length;

        if (!Decode(rawValue, buffer.Slice(written), query, out var valueLength, out var badOffset, out var badLength))
        {
            error = new Gs1Error(Gs1ErrorKind.InvalidPercentEncoding, valueOffset + badOffset, badLength);
            return false;
        }
        if (entry.IsPredefinedLength && Gs1Splitter.TryGetFixedLength(entry, out var fixedLength) && valueLength != fixedLength)
        {
            error = new Gs1Error(valueLength < fixedLength ? Gs1ErrorKind.ValueTooShort : Gs1ErrorKind.ValueTooLong, valueOffset, rawValue.Length);
            return false;
        }
        written += valueLength;
        needsSeparator = Gs1Splitter.NeedsSeparator(entry);
        return true;
    }

    /// <summary>
    /// Percent-decodes a path element or query value. An escape must be <c>%</c> and two hexadecimal digits, a
    /// sequence of them must be UTF-8, and no escape may give a control character (U+0000 to U+001F, U+007F), since
    /// the separator and the null among them would change how the element string splits. A <c>+</c> in a query value
    /// is a blank (RFC 1866), and anything in a path element.
    /// </summary>
    private static bool Decode(ReadOnlySpan<char> raw, Span<char> destination, bool query, out int written, out int badOffset, out int badLength)
    {
        written = 0;
        badOffset = 0;
        badLength = 0;
        var i = 0;
        while (i < raw.Length)
        {
            var c = raw[i];
            if (c != '%')
            {
                destination[written++] = query && c == '+' ? ' ' : c;
                i++;
                continue;
            }

            if (!TryHexByte(raw, i, out var lead))
            {
                badOffset = i;
                badLength = 1;
                return false;
            }
            var start = i;
            i += 3;
            if (lead < 0x80)
            {
                if (lead < 0x20 || lead == 0x7F)
                {
                    badOffset = start;
                    badLength = 3;
                    return false;
                }
                destination[written++] = (char)lead;
                continue;
            }

            int continuations, codePoint;
            if ((lead & 0xE0) == 0xC0)
            {
                continuations = 1;
                codePoint = lead & 0x1F;
            }
            else if ((lead & 0xF0) == 0xE0)
            {
                continuations = 2;
                codePoint = lead & 0x0F;
            }
            else if ((lead & 0xF8) == 0xF0)
            {
                continuations = 3;
                codePoint = lead & 0x07;
            }
            else
            {
                badOffset = start;
                badLength = 3;
                return false;
            }
            for (var k = 0; k < continuations; k++)
            {
                if (i >= raw.Length || raw[i] != '%' || !TryHexByte(raw, i, out var next) || (next & 0xC0) != 0x80)
                {
                    badOffset = start;
                    badLength = 3;
                    return false;
                }
                codePoint = (codePoint << 6) | (next & 0x3F);
                i += 3;
            }
            var minimum = continuations == 1 ? 0x80 : continuations == 2 ? 0x800 : 0x10000;
            if (codePoint < minimum || codePoint > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
            {
                badOffset = start;
                badLength = 3;
                return false;
            }
            if (codePoint >= 0x10000)
            {
                codePoint -= 0x10000;
                destination[written++] = (char)(0xD800 + (codePoint >> 10));
                destination[written++] = (char)(0xDC00 + (codePoint & 0x3FF));
            }
            else
            {
                destination[written++] = (char)codePoint;
            }
        }
        return true;
    }

    private static bool TryHexByte(ReadOnlySpan<char> text, int percent, out int value)
    {
        value = 0;
        if (percent + 2 >= text.Length)
            return false;
        var high = HexValue(text[percent + 1]);
        var low = HexValue(text[percent + 2]);
        if (high < 0 || low < 0)
            return false;
        value = (high << 4) | low;
        return true;
    }

    private static int HexValue(char c)
    {
        if ((uint)(c - '0') <= 9)
            return c - '0';
        if ((uint)(c - 'A') <= 5)
            return c - 'A' + 10;
        if ((uint)(c - 'a') <= 5)
            return c - 'a' + 10;
        return -1;
    }

    /// <summary>Whether the elements already in the buffer, from the path and the query so far, hold this AI.</summary>
    private static bool HoldsAi(ReadOnlySpan<char> elements, ReadOnlySpan<char> ai)
    {
        var position = 0;
        while (position < elements.Length && Gs1Splitter.TryNext(elements, ref position, out var element, out _))
        {
            if (elements.Slice(element.AiStart, element.AiLength).SequenceEqual(ai))
                return true;
        }
        return false;
    }

    /// <summary>The index of the last <c>/</c> in [<paramref name="from"/>, <paramref name="to"/>), or -1.</summary>
    private static int LastSlash(ReadOnlySpan<char> uri, int from, int to)
    {
        var index = uri.Slice(from, to - from).LastIndexOf('/');
        return index < 0 ? -1 : from + index;
    }

    /// <summary>
    /// Whether a final path segment looks like the compressed form (Digital Link 1.1, frozen): at least ten
    /// characters of the base64url alphabet with letters of both cases. A serial number can look the same, so
    /// the heuristic runs only after no primary key was found in the path, where no valid URI reaches it.
    /// </summary>
    private static bool LooksCompressed(ReadOnlySpan<char> segment)
    {
        if (segment.Length < 10)
            return false;
        var upper = false;
        var lower = false;
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if ((uint)(c - 'A') <= 25)
                upper = true;
            else if ((uint)(c - 'a') <= 25)
                lower = true;
            else if (!((uint)(c - '0') <= 9 || c == '-' || c == '_'))
                return false;
        }
        return upper && lower;
    }

    /// <summary>An <c>http://</c> or <c>https://</c> prefix in either ASCII case (RFC 3986 makes the scheme case-insensitive).</summary>
    private static bool TryReadScheme(ReadOnlySpan<char> text, out int afterScheme)
    {
        afterScheme = StartsWithAsciiIgnoreCase(text, "https://") ? 8 : StartsWithAsciiIgnoreCase(text, "http://") ? 7 : 0;
        return afterScheme > 0;
    }

    /// <summary>A prefix test that folds ASCII letters only and allocates on no target framework (the netstandard2.0 span comparison with <c>OrdinalIgnoreCase</c> allocates once it meets a non-ASCII character).</summary>
    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<char> text, string lowerPrefix)
    {
        if (text.Length < lowerPrefix.Length)
            return false;
        for (var i = 0; i < lowerPrefix.Length; i++)
        {
            var c = text[i];
            if ((uint)(c - 'A') <= 25)
                c = (char)(c + 32);
            if (c != lowerPrefix[i])
                return false;
        }
        return true;
    }

    private static bool IsAllDigits(ReadOnlySpan<char> text)
    {
        if (text.Length == 0)
            return false;
        for (var i = 0; i < text.Length; i++)
        {
            if (!Gs1Charsets.IsDigit(text[i]))
                return false;
        }
        return true;
    }

    /// <summary>RFC 3986's unreserved set, which needs no escaping.</summary>
    private static bool IsUnreserved(char c)
        => (uint)(c - 'a') <= 25 || (uint)(c - 'A') <= 25 || (uint)(c - '0') <= 9 || c == '-' || c == '.' || c == '_' || c == '~';

    /// <summary>The characters a URI may hold: unreserved, reserved and the percent (the Syntax Engine's set).</summary>
    private static bool IsUriCharacter(char c) => IsUnreserved(c) || ReservedUriCharacters.IndexOf(c) >= 0;

    private static bool IsBadHostCharacter(char c) => BadHostCharacters.IndexOf(c) >= 0;
}
