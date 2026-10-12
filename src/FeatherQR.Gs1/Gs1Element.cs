using FeatherQR.Gs1.Internals;

namespace FeatherQR.Gs1;

/// <summary>One element of an element string as spans into it: the AI, its value and the AI's offset. The AI is 2 to 4 digits, the value excludes the separator.</summary>
public readonly ref struct Gs1Element
{
    internal Gs1Element(ReadOnlySpan<char> ai, ReadOnlySpan<char> value, int offset)
    {
        Ai = ai;
        Value = value;
        Offset = offset;
    }

    /// <summary>The application identifier, 2 to 4 digits.</summary>
    public ReadOnlySpan<char> Ai { get; }

    /// <summary>The value, without the separator that may follow it.</summary>
    public ReadOnlySpan<char> Value { get; }

    /// <summary>Where the AI starts in the element string.</summary>
    public int Offset { get; }
}

/// <summary>
/// Enumerates the elements of an element string without allocating, usable in <c>foreach</c>. It splits as a
/// reader does (<see cref="Gs1ElementString.EnumerateElements"/>): a known AI of fixed length takes its length,
/// any other value runs to the next separator, an unknown AI with a known length is read, a redundant or
/// trailing separator is skipped, and the enumeration stops at the first element it cannot split.
/// <see cref="Gs1ElementString.TryValidate"/> says why.
/// </summary>
public ref struct Gs1ElementEnumerator
{
    private readonly ReadOnlySpan<char> _text;
    private int _position;
    private int _aiStart;
    private int _aiLength;
    private int _valueStart;
    private int _valueLength;

    internal Gs1ElementEnumerator(ReadOnlySpan<char> text)
    {
        _text = text;
        _position = 0;
        _aiStart = 0;
        _aiLength = 0;
        _valueStart = 0;
        _valueLength = 0;
    }

    /// <summary>Returns this enumerator, for <c>foreach</c>.</summary>
    public readonly Gs1ElementEnumerator GetEnumerator() => this;

    /// <summary>The element last read by <see cref="MoveNext"/>.</summary>
    public readonly Gs1Element Current => new(_text.Slice(_aiStart, _aiLength), _text.Slice(_valueStart, _valueLength), _aiStart);

    /// <summary>Advances to the next element: <c>false</c> at the end of the text, and at the first element that cannot be split.</summary>
    public bool MoveNext()
    {
        if (_position >= _text.Length)
            return false;
        if (!Gs1Splitter.TryNext(_text, ref _position, out var element, out _))
        {
            _position = _text.Length;
            return false;
        }
        _aiStart = element.AiStart;
        _aiLength = element.AiLength;
        _valueStart = element.ValueStart;
        _valueLength = element.ValueLength;
        return true;
    }
}
