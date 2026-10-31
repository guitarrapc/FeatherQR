using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace FeatherQR.Gs1.Internals;

/// <summary>
/// The GS1 Application Identifier table: every AI of the pinned GS1 Barcode Syntax Dictionary release
/// (<see cref="DictionaryRelease"/>) with its components, linters, pairing rules and Digital Link
/// role, plus the two tables of the General Specifications that are frozen by its own statement: AI
/// length by the first two digits (Table 7-5) and the predefined element string lengths (Table 7-6).
/// </summary>
/// <remarks>
/// <para>
/// The data lives in <c>Gs1AiTable.Generated.cs</c> as byte span literals (RVA data, no allocation, no
/// static constructor), rendered by <c>Gs1AiTableGeneratorTest</c> from the dictionary file committed
/// under the test fixtures. The dictionary is Apache-2.0; see THIRD-PARTY-NOTICES.md.
/// </para>
/// <para>
/// An AI is keyed by <c>length * 10000 + value</c>, which orders the entries by length and then by
/// value, so a range such as 3100-3105 is one entry and a lookup is a binary search over
/// <see cref="EntryCount"/> keys. A pairing pattern (<c>31nn</c>) is a key with a wildcard mask over its
/// digit positions.
/// </para>
/// </remarks>
internal static partial class Gs1AiTable
{
    private const int EntrySize = 16;
    private const int ComponentSize = 11;
    private const int PatternSize = 3;

    /// <summary>Finds the entry of a 2- to 4-digit AI, <c>false</c> for anything the release does not assign.</summary>
    public static bool TryFind(ReadOnlySpan<char> ai, out Gs1AiEntry entry)
    {
        entry = default;
        if (!TryKey(ai, out var key))
            return false;

        var low = 0;
        var high = EntryCount - 1;
        while (low <= high)
        {
            var middle = (low + high) >> 1;
            var first = FirstKey(middle);
            if (key < first)
            {
                high = middle - 1;
            }
            else if (key > LastKey(middle))
            {
                low = middle + 1;
            }
            else
            {
                entry = new Gs1AiEntry(middle);
                return true;
            }
        }
        return false;
    }

    /// <summary>The entry at a position in key order, for enumeration.</summary>
    public static Gs1AiEntry EntryAt(int index)
    {
        if ((uint)index >= EntryCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return new Gs1AiEntry(index);
    }

    /// <summary>
    /// The length of every AI that starts with these two digits (General Specifications 7.8.2, Table 7-5),
    /// or 0 for a prefix the table does not list. The table says it is fixed, so an AI newer than the
    /// dictionary still gets its length from here.
    /// </summary>
    public static int AiLength(char digit0, char digit1)
    {
        if (!IsDigit(digit0) || !IsDigit(digit1))
            return 0;
        return AiLengths[(digit0 - '0') * 10 + (digit1 - '0')];
    }

    /// <summary>
    /// Whether every element string whose AI starts with these two digits has a predefined total length
    /// (AI and value, General Specifications 7.8.3, Table 7-6), and that length. The table says it will
    /// remain unchanged, and it lists four reserved prefixes (04, 14, 18, 19) that no AI uses yet.
    /// </summary>
    public static bool TryGetPredefinedLength(char digit0, char digit1, out int totalLength)
    {
        totalLength = 0;
        if (!IsDigit(digit0) || !IsDigit(digit1))
            return false;
        totalLength = PredefinedLengths[(digit0 - '0') * 10 + (digit1 - '0')];
        return totalLength != 0;
    }

    // Table 7-5: AI length by the first two digits. Index = first digit * 10 + second digit.
    private static ReadOnlySpan<byte> AiLengths =>
    [
        2, 2, 2, 2, 0, 0, 0, 0, 0, 0, // 00-09
        2, 2, 2, 2, 0, 2, 2, 2, 0, 0, // 10-19
        2, 2, 2, 3, 3, 3, 0, 0, 0, 0, // 20-29
        2, 4, 4, 4, 4, 4, 4, 2, 0, 4, // 30-39
        3, 3, 3, 4, 0, 0, 0, 0, 0, 0, // 40-49
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 50-59
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 60-69
        4, 3, 4, 0, 0, 0, 0, 0, 0, 0, // 70-79
        4, 4, 4, 0, 0, 0, 0, 0, 0, 0, // 80-89
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, // 90-99
    ];

    // Table 7-6: predefined total length (AI and value) by the first two digits, 0 where none. Index as above.
    private static ReadOnlySpan<byte> PredefinedLengths =>
    [
        20, 16, 16, 16, 18, 0, 0, 0, 0, 0, // 00-09
        0, 8, 8, 8, 8, 8, 8, 8, 8, 8, // 10-19
        4, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 20-29
        0, 10, 10, 10, 10, 10, 10, 0, 0, 0, // 30-39
        0, 16, 0, 0, 0, 0, 0, 0, 0, 0, // 40-49
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 50-59
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 60-69
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 70-79
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 80-89
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 90-99
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDigit(char c) => (uint)(c - '0') <= 9;

    /// <summary>The table key of a 2- to 4-digit AI: <c>length * 10000 + value</c>.</summary>
    internal static bool TryKey(ReadOnlySpan<char> ai, out int key)
    {
        key = 0;
        if (ai.Length < 2 || ai.Length > 4)
            return false;
        var value = 0;
        foreach (var c in ai)
        {
            if (!IsDigit(c))
                return false;
            value = value * 10 + (c - '0');
        }
        key = ai.Length * 10000 + value;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FirstKey(int index) => BinaryPrimitives.ReadUInt16LittleEndian(Entries.Slice(index * EntrySize, 2));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LastKey(int index) => BinaryPrimitives.ReadUInt16LittleEndian(Entries.Slice(index * EntrySize + 2, 2));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> EntryBytes(int index) => Entries.Slice(index * EntrySize, EntrySize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadUInt16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));

    /// <summary>One entry of the table: one AI or a range of AIs with one specification.</summary>
    internal readonly struct Gs1AiEntry
    {
        private const byte PredefinedFlag = 1;
        private const byte DigitalLinkAttributeFlag = 2;
        private const byte DigitalLinkKeyFlag = 4;

        /// <summary>Position in key order, 0 to <see cref="EntryCount"/> - 1.</summary>
        public int Index { get; }

        internal Gs1AiEntry(int index)
        {
            Index = index;
        }

        private ReadOnlySpan<byte> Bytes => EntryBytes(Index);

        /// <summary>The first AI the entry covers (the only one, unless it is a range).</summary>
        public Gs1AiPattern FirstAi => new((ushort)ReadUInt16(Bytes, 0), 0);

        public Gs1AiPattern LastAi => new((ushort)ReadUInt16(Bytes, 2), 0);

        /// <summary>The dictionary's <c>*</c>: the element string has a predefined length and takes no separator (Table 7-6).</summary>
        public bool IsPredefinedLength => (Bytes[4] & PredefinedFlag) != 0;

        /// <summary>The dictionary's <c>?</c>: the AI may be a Digital Link data attribute.</summary>
        public bool IsDigitalLinkAttribute => (Bytes[4] & DigitalLinkAttributeFlag) != 0;

        /// <summary>The dictionary's <c>dlpkey</c>: the AI may be the primary key of a Digital Link URI.</summary>
        public bool IsDigitalLinkKey => (Bytes[4] & DigitalLinkKeyFlag) != 0;

        public int ComponentCount => Bytes[5];

        public Gs1Component Component(int index)
        {
            if ((uint)index >= (uint)ComponentCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return new Gs1Component(Components.Slice(ReadUInt16(Bytes, 6) + index * ComponentSize, ComponentSize));
        }

        /// <summary>The number of <c>req=</c> attributes, each a group of alternatives of which one must hold.</summary>
        public int RequisiteGroupCount => Bytes[10];

        public Gs1RequisiteGroup RequisiteGroup(int index)
        {
            if ((uint)index >= (uint)RequisiteGroupCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            var offset = ReadUInt16(Bytes, 8);
            for (var i = 0; i < index; i++)
                offset = Gs1RequisiteGroup.End(offset);
            return new Gs1RequisiteGroup(offset);
        }

        /// <summary>The number of AIs or patterns the dictionary's <c>ex=</c> forbids beside this one.</summary>
        public int ExclusionCount => Bytes[13];

        public Gs1AiPattern Exclusion(int index)
        {
            if ((uint)index >= (uint)ExclusionCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return Gs1AiPattern.Read(Attributes, ReadUInt16(Bytes, 11) + index * PatternSize);
        }

        /// <summary>The number of qualifier sequences a Digital Link URI may put after this key (<c>dlpkey=22,10,21|235</c> has two), 0 for a key without qualifiers and for an AI that is no key.</summary>
        public int QualifierSequenceCount => IsDigitalLinkKey ? Attributes[ReadUInt16(Bytes, 14)] : 0;

        public Gs1QualifierSequence QualifierSequence(int index)
        {
            if ((uint)index >= (uint)QualifierSequenceCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            var offset = ReadUInt16(Bytes, 14) + 1;
            for (var i = 0; i < index; i++)
                offset = Gs1QualifierSequence.End(offset);
            return new Gs1QualifierSequence(offset);
        }
    }

    /// <summary>One component of an entry's data field.</summary>
    internal readonly struct Gs1Component
    {
        private const byte OptionalFlag = 0x80;

        private readonly byte _type;
        private readonly byte _min;
        private readonly byte _max;
        private readonly ulong _linters;

        internal Gs1Component(ReadOnlySpan<byte> bytes)
        {
            _type = bytes[0];
            _min = bytes[1];
            _max = bytes[2];
            _linters = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(3, 8));
        }

        public Gs1ComponentType Type => (Gs1ComponentType)(_type & 0x7F);

        /// <summary>Bracketed in the dictionary: the component may be absent, and every later one too.</summary>
        public bool IsOptional => (_type & OptionalFlag) != 0;

        public int MinLength => _min;

        public int MaxLength => _max;

        /// <summary><c>N..20</c> rather than <c>N6</c>: the length is a range, and only the last component may be one.</summary>
        public bool IsVariable => _min != _max;

        /// <summary>One bit per <see cref="Gs1Linter"/>.</summary>
        public ulong Linters => _linters;

        public bool Has(Gs1Linter linter) => (_linters & (1UL << (int)linter)) != 0;
    }

    /// <summary>An AI, or an AI pattern such as <c>31nn</c> where <c>n</c> stands for any digit, as a key and a wildcard mask.</summary>
    internal readonly struct Gs1AiPattern
    {
        private readonly ushort _key;
        private readonly byte _wildcards;

        internal Gs1AiPattern(ushort key, byte wildcards)
        {
            _key = key;
            _wildcards = wildcards;
        }

        internal static Gs1AiPattern Read(ReadOnlySpan<byte> bytes, int offset)
            => new((ushort)ReadUInt16(bytes, offset), bytes[offset + 2]);

        /// <summary>The number of digits, 2 to 4.</summary>
        public int Length => _key / 10000;

        /// <summary>The smallest key the pattern matches, which is its own: a wildcard digit is stored as 0.</summary>
        public int FirstKey => _key;

        /// <summary>
        /// The largest key the pattern matches, with 9 for each wildcard digit. Where the wildcards are the last
        /// digits, the keys the pattern matches are every key from <see cref="FirstKey"/> to this one.
        /// </summary>
        public int LastKey
        {
            get
            {
                var last = (int)_key;
                var unit = 1;
                for (var position = Length - 1; position >= 0; position--)
                {
                    if ((_wildcards & (1 << position)) != 0)
                        last += 9 * unit;
                    unit *= 10;
                }
                return last;
            }
        }

        /// <summary>The digit at a position, or <c>'n'</c> where the pattern takes any digit.</summary>
        public char this[int position]
        {
            get
            {
                if ((uint)position >= (uint)Length)
                    throw new ArgumentOutOfRangeException(nameof(position));
                if ((_wildcards & (1 << position)) != 0)
                    return 'n';
                var value = _key % 10000;
                for (var i = Length - 1; i > position; i--)
                    value /= 10;
                return (char)('0' + value % 10);
            }
        }

        /// <summary>Whether an AI matches: the same length, and every non-wildcard digit equal.</summary>
        public bool Matches(ReadOnlySpan<char> ai)
        {
            if (ai.Length != Length)
                return false;
            for (var i = 0; i < ai.Length; i++)
            {
                var expected = this[i];
                if (expected != 'n' && ai[i] != expected)
                    return false;
            }
            return true;
        }

        /// <summary>The digits, with <c>n</c> for a wildcard. Allocates, for diagnostics and tests.</summary>
        public override string ToString()
        {
            Span<char> chars = stackalloc char[4];
            for (var i = 0; i < Length; i++)
                chars[i] = this[i];
            return chars.Slice(0, Length).ToString();
        }
    }

    /// <summary>One <c>req=</c> attribute: alternatives of which one must hold on the item.</summary>
    internal readonly struct Gs1RequisiteGroup
    {
        private readonly int _offset;

        internal Gs1RequisiteGroup(int offset)
        {
            _offset = offset;
        }

        public int AlternativeCount => Attributes[_offset];

        public Gs1AiAlternative Alternative(int index)
        {
            if ((uint)index >= (uint)AlternativeCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            var offset = _offset + 1;
            for (var i = 0; i < index; i++)
                offset = Gs1AiAlternative.End(offset);
            return new Gs1AiAlternative(offset);
        }

        /// <summary>The offset just past the group that starts at <paramref name="offset"/>.</summary>
        internal static int End(int offset)
        {
            var alternatives = Attributes[offset];
            offset++;
            for (var i = 0; i < alternatives; i++)
                offset = Gs1AiAlternative.End(offset);
            return offset;
        }
    }

    /// <summary>One alternative of a requisite group: the AIs that must all be present (<c>01+21</c>), usually one.</summary>
    internal readonly struct Gs1AiAlternative
    {
        private readonly int _offset;

        internal Gs1AiAlternative(int offset)
        {
            _offset = offset;
        }

        public int MemberCount => Attributes[_offset];

        public Gs1AiPattern Member(int index)
        {
            if ((uint)index >= (uint)MemberCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return Gs1AiPattern.Read(Attributes, _offset + 1 + index * PatternSize);
        }

        internal static int End(int offset) => offset + 1 + Attributes[offset] * PatternSize;
    }

    /// <summary>One ordered sequence of key qualifiers a Digital Link URI may carry after a primary key.</summary>
    internal readonly struct Gs1QualifierSequence
    {
        private readonly int _offset;

        internal Gs1QualifierSequence(int offset)
        {
            _offset = offset;
        }

        public int Count => Attributes[_offset];

        public Gs1AiPattern Qualifier(int index)
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return new Gs1AiPattern((ushort)ReadUInt16(Attributes, _offset + 1 + index * 2), 0);
        }

        internal static int End(int offset) => offset + 1 + Attributes[offset] * 2;
    }
}
