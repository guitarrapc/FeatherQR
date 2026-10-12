namespace FeatherQR.Internals.StandardQR;

internal ref partial struct QRBinaryEncoder
{
    private const int Fnc1FirstModeIndicator = 0b0101;
    private const int Fnc1SecondModeIndicator = 0b1001;
    private const int PercentValue = 38;
    private const char FieldSeparator = '\u001D';

    /// <summary>
    /// The headers of a stream under FNC1 in the standard's order (ISO/IEC 18004 6.4.8, fnc1-support-plan.md decision 5):
    /// the ECI header when a charset is declared, the FNC1 indicator with its application indicator in second position,
    /// then the data mode indicator. Kept apart from <see cref="WriteMode"/> so the route without FNC1 keeps its code.
    /// </summary>
    public void WriteModeFnc1(EncodingMode encoding, EciMode eci, Fnc1Mode fnc1)
    {
        if (eci != EciMode.Default)
        {
            _writer.Write((int)EncodingMode.ECI, 4);
            _writer.Write((int)eci, 8);
        }
        WriteFnc1(fnc1);
        _writer.Write((int)encoding, 4);
    }

    private void WriteFnc1(Fnc1Mode fnc1)
    {
        if (fnc1.IsFirstPosition)
        {
            _writer.Write(Fnc1FirstModeIndicator, 4);
        }
        else if (fnc1.IsSecondPosition)
        {
            _writer.Write(Fnc1SecondModeIndicator, 4);
            _writer.Write(fnc1.Codeword, 8);
        }
    }

    /// <summary>
    /// The data of a single-mode stream under FNC1: an Alphanumeric run goes through the <c>%</c> rule, every other
    /// mode is written as it is (a Byte run carries the separator as 0x1D, Numeric and Kanji runs hold none).
    /// </summary>
    public void WriteDataFnc1(ReadOnlySpan<char> textSpan, EncodingMode encoding, EciMode eci, bool utf8Bom)
    {
        if (encoding == EncodingMode.Alphanumeric)
            WriteAlphanumericDataFnc1(textSpan);
        else
            WriteData(textSpan, encoding, eci, utf8Bom);
    }

    /// <summary>
    /// The Alphanumeric payload under FNC1 (6.4.8.1): a separator is written as <c>%</c> and a literal <c>%</c> as
    /// <c>%%</c>, and the values pair into 11 bits as in any Alphanumeric run. The caller keeps a separator pair out
    /// of the run (a reader would take the <c>%%</c> first). Scalar: the SIMD writers never see a separator.
    /// </summary>
    public void WriteAlphanumericDataFnc1(ReadOnlySpan<char> chars)
    {
        var pending = -1;
        foreach (var c in chars)
        {
            if (c == FieldSeparator)
            {
                PutAlphanumeric(ref pending, PercentValue);
            }
            else if (c == '%')
            {
                PutAlphanumeric(ref pending, PercentValue);
                PutAlphanumeric(ref pending, PercentValue);
            }
            else
            {
                PutAlphanumeric(ref pending, CharacterSets.GetAlphanumericValue(c));
            }
        }
        if (pending >= 0)
            _writer.Write(pending, 6);
    }

    private void PutAlphanumeric(ref int pending, int value)
    {
        if (pending < 0)
        {
            pending = value;
            return;
        }
        _writer.Write(pending * 45 + value, 11);
        pending = -1;
    }

    /// <summary>
    /// <see cref="WriteSegments"/> under FNC1: the ECI header, the FNC1 indicator, then each run, an Alphanumeric
    /// run through the <c>%</c> rule with the unit count the plan priced (its characters plus its literal percents).
    /// </summary>
    public void WriteSegmentsFnc1(ReadOnlySpan<char> text, ReadOnlySpan<ModeSegment> segments, int version, EciMode eci, Fnc1Mode fnc1)
    {
        if (segments.Length == 0)
            throw new ArgumentException("A segmented QR stream needs at least one segment.", nameof(segments));
        if (eci is not (EciMode.Default or EciMode.Iso8859_1 or EciMode.Utf8))
            throw new ArgumentOutOfRangeException(nameof(eci), $"Unsupported charset {eci} for segmented encoding.");

        if (eci != EciMode.Default)
        {
            _writer.Write((int)EncodingMode.ECI, 4);
            _writer.Write((int)eci, 8);
        }
        WriteFnc1(fnc1);

        var numericCountBits = EncodingMode.Numeric.GetCountIndicatorLength(version);
        var alnumCountBits = EncodingMode.Alphanumeric.GetCountIndicatorLength(version);
        var byteCountBits = EncodingMode.Byte.GetCountIndicatorLength(version);

        var expectedStart = 0;
        foreach (var segment in segments)
        {
            if (segment.Start != expectedStart || segment.Length == 0 || segment.Start + segment.Length > text.Length)
                throw new ArgumentException("Segment plan must cover the content in order with non-empty runs.", nameof(segments));
            expectedStart = segment.Start + segment.Length;

            var chars = text.Slice(segment.Start, segment.Length);
            var mode = segment.Mode;
            switch (mode)
            {
                case EncodingMode.Numeric:
                    if (segment.UnitCount != segment.Length)
                        throw new ArgumentException($"Segment plan gives a {segment.Length}-character run a unit count of {segment.UnitCount}; they must agree in Numeric mode.", nameof(segments));
                    _writer.Write((int)mode, 4);
                    _writer.Write(segment.UnitCount, numericCountBits);
                    WriteNumericData(chars);
                    break;
                case EncodingMode.Alphanumeric:
                    if (segment.UnitCount != ModeSegmenter.AlphanumericUnitsFnc1(chars))
                        throw new ArgumentException($"Segment plan gives a {segment.Length}-character run a unit count of {segment.UnitCount}; under FNC1 an Alphanumeric run counts its literal percents twice.", nameof(segments));
                    _writer.Write((int)mode, 4);
                    _writer.Write(segment.UnitCount, alnumCountBits);
                    WriteAlphanumericDataFnc1(chars);
                    break;
                case EncodingMode.Byte:
                    if (eci != EciMode.Utf8 && segment.UnitCount != segment.Length)
                        throw new ArgumentException($"Segment plan gives a {segment.Length}-character run a unit count of {segment.UnitCount}; they must agree outside UTF-8 Byte mode.", nameof(segments));
                    _writer.Write((int)mode, 4);
                    _writer.Write(segment.UnitCount, byteCountBits);
                    if (eci == EciMode.Utf8)
                        WriteUtf8Segment(chars, segment.UnitCount);
                    else
                        WriteLatin1Data(chars);
                    break;
                default:
                    throw new ArgumentException("A plan under FNC1 holds Numeric, Alphanumeric and Byte runs only.", nameof(segments));
            }
        }

        if (expectedStart != text.Length)
            throw new ArgumentException("Segment plan must cover the content in order with non-empty runs.", nameof(segments));
    }
}
