using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Internals.StandardQR;

internal ref struct QRBinaryEncoder
{
    // stackalloc threshold for temporary buffers. Avoids stackoverflow for large inputs.
    // if input length exceeds this, ArrayPool<byte> is used instead.
    private const int StackAllocThreshold = 256;

    private BitWriter _writer;

    /// <summary>
    /// Gets the current bit position (actual number of bits written).
    /// </summary>
    public int BitPosition => _writer.BitPosition;

    /// <summary>
    /// Gets the number of bytes written (rounded up to nearest byte)
    /// </summary>
    public int ByteCount => _writer.ByteCount;

    public QRBinaryEncoder(Span<byte> buffer)
    {
        _writer = new BitWriter(buffer);
    }

    /// <summary>
    /// Writes mode indicator (4 bits) and optional ECI header (12 bits).
    /// </summary>
    /// <param name="encoding">Encoding mode.</param>
    /// <param name="eci">ECI mode for character encoding.</param>
    public void WriteMode(EncodingMode encoding, EciMode eci)
    {
        // ECI mode requires special header before mode indicator
        if (eci != EciMode.Default)
        {
            // ECI mode indicator: 0111 (4 bits)
            _writer.Write((int)EncodingMode.ECI, 4);
            // ECI assignment number (8 bits for 00000000-000000FF)
            _writer.Write((int)eci, 8);
        }

        // Standard mode indicator (4 bits)
        _writer.Write((int)encoding, 4);
    }

    /// <summary>
    /// Writes character count indicator (8-16 bits depending on version and mode)
    /// </summary>
    /// <param name="count">Number of input characters</param>
    /// <param name="bitsLength"></param>
    public void WriteCharacterCount(int count, int bitsLength)
    {
        _writer.Write(count, bitsLength);
    }

    /// <summary>
    /// Writes terminator (up to 4 bits), byte alignment (0-7 bits) and pad bytes (0-16 bits) to reach target capacity
    /// </summary>
    /// <param name="targetBitCount">Target total bit count</param>
    /// <remarks>
    /// Padding structure:
    /// 1. Terminator: 0000 (up to 4 bits)
    /// 2. Byte alignment: 0-7 bits to align to byte boundary
    /// 3. Pad bytes: Alternating 11101100 (0xEC) and 00010001 (0x11)
    /// </remarks>
    public void WritePadding(int targetBitCount)
    {
        var currentBits = _writer.BitPosition;
        var remaining = targetBitCount - currentBits;

        // Target already reached (or exceeded): nothing to pad. Only drain pending
        // bits so callers can read the raw buffer; the position does not advance.
        if (remaining <= 0)
        {
            _writer.Flush();
            return;
        }

        // 1. Terminator (up to 4 bits)
        var terminatorBits = Math.Min(remaining, 4);
        _writer.Write(0, terminatorBits);

        // 2. Byte boundary alignment (0-7 bits)
        var alignmentBits = (8 - _writer.BitPosition % 8) % 8;
        if (alignmentBits > 0)
        {
            _writer.Write(0, alignmentBits);
        }

        // 3. Alternating pad bytes (0xEC, 0x11, 0xEC, 0x11, ...) until target length.
        // The position is byte-aligned here, so the bytes are filled directly.
        // Callers read the raw buffer after WritePadding, so both branches drain
        // every pending bit to the buffer.
        var padBytes = (targetBitCount - _writer.BitPosition) / 8;
        if (padBytes > 0)
        {
            _writer.WritePadBytes(padBytes);
        }
        else
        {
            _writer.Flush();
        }
    }

    /// <summary>
    /// Writes encoded data byte based on mode
    /// </summary>
    /// <param name="textSpan">Input text to encode</param>
    /// <param name="encoding">Encoding mode</param>
    /// <param name="eci">ECI mode for character encoding</param>
    /// <param name="utf8Bom">Whether to include UTF-8 BOM</param>
    public void WriteData(ReadOnlySpan<char> textSpan, EncodingMode encoding, EciMode eci, bool utf8Bom)
    {
        switch (encoding)
        {
            case EncodingMode.Numeric:
                WriteNumericData(textSpan);
                break;
            case EncodingMode.Alphanumeric:
                WriteAlphanumericData(textSpan);
                break;
            case EncodingMode.Byte:
                EncodeByte(textSpan, eci, utf8Bom);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(encoding), "Invalid encoding mode");
        }
    }

    /// <summary>
    /// Writes a planned mixed-mode data stream (<see cref="QRSegmentation.Optimal"/>): one optional ECI prefix, then per run a mode indicator, count indicator and payload.
    /// Same bit grammar as the single-segment path, repeated per planned run (a Standard QR decoder carries the declared charset across the runs that follow the ECI header).
    /// Never writes a UTF-8 BOM: the BOM is a stream-level prefix, and the planner falls back to the single-mode stream when one is requested.
    /// </summary>
    /// <param name="text">Content the plan indexes into.</param>
    /// <param name="segments">Planned runs, in order, covering the whole content.</param>
    /// <param name="version">Target version (decides the count indicator widths).</param>
    /// <param name="eci">Effective charset: Default / ISO-8859-1 narrow, UTF-8 transcode.</param>
    public void WriteSegments(ReadOnlySpan<char> text, ReadOnlySpan<ModeSegment> segments, int version, EciMode eci)
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

        var expectedStart = 0;
        foreach (var segment in segments)
        {
            if (segment.Start != expectedStart || segment.Length == 0 || segment.Start + segment.Length > text.Length)
                throw new ArgumentException("Segment plan must cover the content in order with non-empty runs.", nameof(segments));
            expectedStart = segment.Start + segment.Length;

            var chars = text.Slice(segment.Start, segment.Length);
            var mode = segment.Mode;
            var countBits = mode.GetCountIndicatorLength(version);

            // The count indicator width always covers the largest unit count a version
            // can hold, and a run holds no more than that, so this cannot bind; assert
            // it anyway because overflowing it would corrupt the mode indicator above.
            Debug.Assert(segment.UnitCount < (1 << countBits), "run length must fit the character count indicator");

            // The bit budget the caller cleared comes from UnitCount, but the payload
            // below is written from the run's characters. A plan whose two disagree
            // would clear the budget and then overrun, so this is a runtime check, not
            // an assert. Byte mode under UTF-8 is the exception: its unit count is only
            // knowable after transcoding, so WriteUtf8Segment verifies it there instead.
            if ((mode != EncodingMode.Byte || eci != EciMode.Utf8) && segment.UnitCount != segment.Length)
                throw new ArgumentException($"Segment plan gives a {segment.Length}-character run a unit count of {segment.UnitCount}; they must agree outside UTF-8 Byte mode.", nameof(segments));

            _writer.Write((int)mode, 4);
            _writer.Write(segment.UnitCount, countBits);
            switch (mode)
            {
                case EncodingMode.Numeric:
                    WriteNumericData(chars);
                    break;
                case EncodingMode.Alphanumeric:
                    WriteAlphanumericData(chars);
                    break;
                default:
                    if (eci == EciMode.Utf8)
                        WriteUtf8Segment(chars, segment.UnitCount);
                    else
                        WriteLatin1Data(chars);
                    break;
            }
        }

        if (expectedStart != text.Length)
            throw new ArgumentException("Segment plan must cover the content in order with non-empty runs.", nameof(segments));
    }

    /// <summary>
    /// Transcodes one Byte-mode run and writes it.
    /// <paramref name="expectedBytes"/> is what the plan budgeted; a mismatch means the plan and the transcoder disagree, which would silently produce an unreadable symbol.
    /// </summary>
    private void WriteUtf8Segment(ReadOnlySpan<char> chars, int expectedBytes)
    {
        var maxByteCount = chars.Length * 4;
        if (maxByteCount <= StackAllocThreshold)
        {
            Span<byte> buffer = stackalloc byte[maxByteCount];
            var length = GetUtf8Data(chars, utf8BOM: false, buffer);
            if (length != expectedBytes)
                throw new ArgumentException($"Segment plan budgeted {expectedBytes} UTF-8 bytes but the run encodes to {length}.");
            WriteByteData(buffer.Slice(0, length));
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(maxByteCount);
            try
            {
                var length = GetUtf8Data(chars, utf8BOM: false, buffer);
                if (length != expectedBytes)
                    throw new ArgumentException($"Segment plan budgeted {expectedBytes} UTF-8 bytes but the run encodes to {length}.");
                WriteByteData(buffer.AsSpan(0, length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Writes byte mode data based on ECI mode.
    /// </summary>
    private void EncodeByte(ReadOnlySpan<char> textSpan, EciMode eci, bool utf8Bom)
    {
        if (eci is EciMode.Default or EciMode.Iso8859_1)
        {
            // ISO-8859-1 is a pure narrowing cast for chars <= 0xFF (validated upstream by TextAnalyzer)
            WriteLatin1Data(textSpan);
            return;
        }

        if (eci != EciMode.Utf8)
            throw new ArgumentOutOfRangeException(nameof(eci), "Unsupported ECI mode for Byte encoding");

        // UTF-8: encode into a temporary buffer, then bulk-write (+3 reserves room for the UTF-8 BOM)
        var maxByteCount = textSpan.Length * 4 + (utf8Bom ? 3 : 0);
        if (maxByteCount <= StackAllocThreshold)
        {
            Span<byte> buffer = stackalloc byte[maxByteCount];
            var length = GetUtf8Data(textSpan, utf8Bom, buffer);
            WriteByteData(buffer.Slice(0, length));
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(maxByteCount);
            try
            {
                var length = GetUtf8Data(textSpan, utf8Bom, buffer);
                WriteByteData(buffer.AsSpan(0, length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Writes numeric data (groups of 3 digits as 10 bits, 2 digits as 7 bits, 1 digit as 4 bits)
    /// </summary>
    /// <param name="digits"></param>
    private void WriteNumericData(ReadOnlySpan<char> digits)
    {
        var i = 0;
        var length = digits.Length;

        // Process 3 digits at a time (10 bits)
        while (i + 2 < length)
        {
            var value = (digits[i] - '0') * 100 + (digits[i + 1] - '0') * 10 + (digits[i + 2] - '0');
            _writer.Write(value, 10);
            i += 3;
        }

        // Process remaining 2 digits (7 bits)
        if (i + 1 < length)
        {
            var value = (digits[i] - '0') * 10 + (digits[i + 1] - '0');
            _writer.Write(value, 7);
            i += 2;
        }

        // Process remaining 1 digit (4 bits)
        if (i < length)
        {
            var value = digits[i] - '0';
            _writer.Write(value, 4);
        }
    }

    /// <summary>
    /// Encodes alphanumeric data (0-9, A-Z, space, $, %, *, +, -, ., /, :)
    /// </summary>
    /// <param name="chars"></param>
    /// <remarks>
    /// Encoding: Groups of 2 chars → 11 bits, 1 char → 6 bits.
    /// </remarks>
    private void WriteAlphanumericData(ReadOnlySpan<char> chars)
    {
        var length = chars.Length;
        var i = 0;

        // Pairs of chars = 11bits
        // Single chars = 6bits

        // Process 2 characters at a time
        while (i + 1 < length)
        {
            var value = CharacterSets.GetAlphanumericValue(chars[i]) * 45
                + CharacterSets.GetAlphanumericValue(chars[i + 1]);
            _writer.Write(value, 11);
            i += 2;
        }

        // Process remaining 1 character
        if (i < length)
        {
            var value = CharacterSets.GetAlphanumericValue(chars[i]);
            _writer.Write(value, 6);
        }
    }

    /// <summary>
    /// Writes ISO-8859-1 (Latin-1) byte data directly from chars.
    /// </summary>
    /// <remarks>
    /// Chars are pre-validated as ISO-8859-1 (&lt;= 0xFF), so encoding is a narrowing cast.
    /// </remarks>
    private void WriteLatin1Data(ReadOnlySpan<char> textSpan)
    {
#if NET5_0_OR_GREATER
        // Encoding.Latin1 narrows chars with SIMD; then bulk-write 8 bytes per store
        if (textSpan.Length <= StackAllocThreshold)
        {
            Span<byte> latin1 = stackalloc byte[textSpan.Length];
            Encoding.Latin1.GetBytes(textSpan, latin1);
            WriteByteData(latin1);
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(textSpan.Length);
            try
            {
                var bytesWritten = Encoding.Latin1.GetBytes(textSpan, buffer);
                WriteByteData(buffer.AsSpan(0, bytesWritten));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
#else
        // Scalar fallback: pack 8 narrowed chars into one big-endian ulong per store
        var i = 0;
        for (; i + 8 <= textSpan.Length; i += 8)
        {
            var v = ((ulong)(byte)textSpan[i] << 56)
                | ((ulong)(byte)textSpan[i + 1] << 48)
                | ((ulong)(byte)textSpan[i + 2] << 40)
                | ((ulong)(byte)textSpan[i + 3] << 32)
                | ((ulong)(byte)textSpan[i + 4] << 24)
                | ((ulong)(byte)textSpan[i + 5] << 16)
                | ((ulong)(byte)textSpan[i + 6] << 8)
                | (byte)textSpan[i + 7];
            _writer.Write64(v);
        }
        for (; i < textSpan.Length; i++)
        {
            _writer.Write((byte)textSpan[i], 8);
        }
#endif
    }

    /// <summary>
    /// Writes byte data (each byte should be 8-bit), 8 bytes per store where possible.
    /// </summary>
    /// <param name="data"></param>
    private void WriteByteData(scoped ReadOnlySpan<byte> data)
    {
        var i = 0;
        for (; i + 8 <= data.Length; i += 8)
        {
            _writer.Write64(BinaryPrimitives.ReadUInt64BigEndian(data.Slice(i)));
        }
        for (; i < data.Length; i++)
        {
            _writer.Write(data[i], 8);
        }
    }

    /// <summary>
    /// Gets the encoded data.
    /// </summary>
    /// <returns></returns>
    public ReadOnlySpan<byte> GetEncodedData() => _writer.GetData();

    /// <summary>
    /// Gets UTF-8 byte data (with optional BOM) for Byte mode encoding.
    /// </summary>
    /// <param name="textSpan">Text to encode.</param>
    /// <param name="utf8BOM">Whether to include UTF-8 BOM.</param>
    /// <param name="buffer">Buffer to use for encoding.</param>
    /// <returns>Number of bytes written to the buffer.</returns>
    private static int GetUtf8Data(ReadOnlySpan<char> textSpan, bool utf8BOM, Span<byte> buffer)
    {
        var offset = 0;
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        if (utf8BOM)
        {
            var preamble = Encoding.UTF8.GetPreamble();
            preamble.CopyTo(buffer);
            offset += preamble.Length;

            return offset + Encoding.UTF8.GetBytes(textSpan, buffer.Slice(offset));
        }
        else
        {
            return Encoding.UTF8.GetBytes(textSpan, buffer);
        }
#else
        if (utf8BOM)
        {
            var preamble = Encoding.UTF8.GetPreamble();
            preamble.CopyTo(buffer);
            offset += preamble.Length;

            var input = textSpan.ToString();
            ReadOnlySpan<byte> utf8bytes = Encoding.UTF8.GetBytes(input);
            utf8bytes.CopyTo(buffer.Slice(offset));
            return offset + utf8bytes.Length;
        }
        else
        {
            var input = textSpan.ToString();
            ReadOnlySpan<byte> utf8bytes = Encoding.UTF8.GetBytes(input);
            utf8bytes.CopyTo(buffer);
            return utf8bytes.Length;
        }
#endif
    }
}
