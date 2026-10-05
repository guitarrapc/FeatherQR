using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Internals.StandardQR;

internal ref partial struct QRBinaryEncoder
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

    /// <summary>ISO/IEC 18004 Structured Append mode indicator; not an <see cref="EncodingMode"/>, since it carries no data.</summary>
    private const int StructuredAppendModeIndicator = 0b0011;

    /// <summary>
    /// Writes the Structured Append header, which precedes every other segment: mode indicator, 4-bit position, 4-bit count minus one, 8-bit parity.
    /// </summary>
    public void WriteStructuredAppend(in QRStructuredAppend header)
    {
        _writer.Write(StructuredAppendModeIndicator, 4);
        _writer.Write(header.Index, 4);
        _writer.Write(header.Count - 1, 4);
        _writer.Write(header.Parity, 8);
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
            case EncodingMode.Kanji:
                if (eci != EciMode.Default)
                    KanjiCells.ThrowKanjiUnderEci(eci);
                WriteKanjiData(textSpan);
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

        // The count widths once for the version, not a mode lookup per run: a Kanji plan of
        // interleaved text has a run every few characters.
        var numericCountBits = EncodingMode.Numeric.GetCountIndicatorLength(version);
        var alnumCountBits = EncodingMode.Alphanumeric.GetCountIndicatorLength(version);
        var byteCountBits = EncodingMode.Byte.GetCountIndicatorLength(version);
        var kanjiCountBits = EncodingMode.Kanji.GetCountIndicatorLength(version);

        var expectedStart = 0;
        foreach (var segment in segments)
        {
            if (segment.Start != expectedStart || segment.Length == 0 || segment.Start + segment.Length > text.Length)
                throw new ArgumentException("Segment plan must cover the content in order with non-empty runs.", nameof(segments));
            expectedStart = segment.Start + segment.Length;

            var chars = text.Slice(segment.Start, segment.Length);
            var mode = segment.Mode;
            var countBits = segment.ModeIndex switch { 0 => numericCountBits, 1 => alnumCountBits, 2 => byteCountBits, _ => kanjiCountBits };

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
            if (mode == EncodingMode.Kanji && eci != EciMode.Default)
                KanjiCells.ThrowKanjiUnderEci(eci);

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
                case EncodingMode.Kanji:
                    WriteKanjiData(chars);
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
    /// Writes numeric data: three digits in 10 bits, a last two in 7, a last one in 4.
    /// </summary>
    /// <remarks>
    /// The digits are '0'-'9', found so by the analysis or the plan, as the writer has always taken them: a writer does not check them,
    /// and a character outside them writes wrong fields, not an exception. Its group can spill into the fields appended with it (the
    /// 30-bit append of nine digits, the 40-bit step of twelve), where the writer of one field a group spoiled only that field. A run
    /// under sixteen digits, at most five groups, is written here (<see cref="WriteNumericTail"/>): a call to a writer cost more than it
    /// saved there, 1.12 times the old loop's time at twelve
    /// digits interpreted on WebAssembly. WebAssembly has no vector tier: twelve digits a step on its SIMD took 0.95 to 0.96 of the portable
    /// writer's time AOT-compiled and 1.10 to 1.65 interpreted at 40 to 7,089 digits, and one flag gates both (2026-10-05).
    /// </remarks>
    private void WriteNumericData(ReadOnlySpan<char> digits)
    {
        if (digits.Length < 16)
        {
            WriteNumericTail(ref _writer, digits, 0);
            return;
        }
#if NET8_0_OR_GREATER
        // The SSSE3 step runs from 40 digits. It took 1.08 to 1.22 of the portable writer's time at 16 to 24 and at 32 digits, 0.80 to
        // 0.93 at 28 and 1.00 to 1.07 at 36, and 0.77 to 1.00 from 40, on the JIT with and without AVX2 and on default NativeAOT (2026-10-05).
        if (System.Runtime.Intrinsics.X86.Ssse3.IsSupported && digits.Length >= 40)
        {
            WriteNumericSsse3(ref _writer, digits);
            return;
        }
        // The NEON step's shortest run depends on the runtime: 40 digits on .NET 10, 160 on .NET 8 (NumericAdvSimdMinimum).
        if (System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported && digits.Length >= NumericAdvSimdMinimum)
        {
            WriteNumericAdvSimd(ref _writer, digits);
            return;
        }
#endif
        WriteNumericScalar(ref _writer, digits);
    }

    /// <summary>The portable Numeric writer: fifteen digits a 50-bit append, then nine a 30-bit one, then the groups left.</summary>
    internal static void WriteNumericScalar(ref BitWriter writer, ReadOnlySpan<char> digits) => WriteNumericScalar(ref writer, digits, 0);

    /// <summary>
    /// The portable writer from <paramref name="start"/>, where a vector tier stopped. Each step of fifteen digits comes from five 8-byte
    /// loads (see <see cref="NumericGroup"/>). Not inlined, as <see cref="WriteAlphanumericScalar(ref BitWriter, ReadOnlySpan{char}, int)"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteNumericScalar(ref BitWriter writer, ReadOnlySpan<char> digits, int start)
    {
        var i = start;
        ref var c = ref MemoryMarshal.GetReference(digits);
        // the last load of a step reads chars i+12..i+15, so a sixteenth must exist
        for (; i + 15 < digits.Length; i += 15)
        {
            writer.WriteWide((NumericGroup(ref c, i) << 40) | (NumericGroup(ref c, i + 3) << 30) | (NumericGroup(ref c, i + 6) << 20)
                | (NumericGroup(ref c, i + 9) << 10) | NumericGroup(ref c, i + 12), 50);
        }
        WriteNumericTail(ref writer, digits, i);
    }

    /// <summary>
    /// The digits from <paramref name="i"/> with no load past the run: nine an append, then the groups left and the 7- or 4-bit rest, the
    /// per-digit '0' folded into one subtract (5328 = '0' * 111, 528 = '0' * 11).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteNumericTail(ref BitWriter writer, ReadOnlySpan<char> digits, int i)
    {
        for (; i + 8 < digits.Length; i += 9)
        {
            var g0 = digits[i] * 100 + digits[i + 1] * 10 + digits[i + 2] - 5328;
            var g1 = digits[i + 3] * 100 + digits[i + 4] * 10 + digits[i + 5] - 5328;
            var g2 = digits[i + 6] * 100 + digits[i + 7] * 10 + digits[i + 8] - 5328;
            writer.Write((g0 << 20) | (g1 << 10) | g2, 30);
        }
        for (; i + 2 < digits.Length; i += 3)
            writer.Write(digits[i] * 100 + digits[i + 1] * 10 + digits[i + 2] - 5328, 10);
        if (i + 1 < digits.Length)
            writer.Write(digits[i] * 10 + digits[i + 1] - 528, 7);
        else if (i < digits.Length)
            writer.Write(digits[i] - '0', 4);
    }

    /// <summary>
    /// Three digits from one 8-byte load of four chars (the fourth must exist): with each char's '0' removed, the multiply by
    /// (100 &lt;&lt; 32 | 10 &lt;&lt; 16 | 1) lands d0 * 100 + d1 * 10 + d2 in bits 32-47, and no lower product carries into them.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong NumericGroup(ref char c, int i)
    {
        if (BitConverter.IsLittleEndian)
        {
            var chunk = Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref c, i)));
            return (((chunk - 0x0030_0030_0030_0030UL) * ((100UL << 32) | (10UL << 16) | 1UL)) >> 32) & 0x3FF;
        }

        // A big-endian runtime lays the chars out the other way within the word, so the group is taken char by char there.
        return (ulong)(Unsafe.Add(ref c, i) * 100 + Unsafe.Add(ref c, i + 1) * 10 + Unsafe.Add(ref c, i + 2) - 5328);
    }

    /// <summary>
    /// Encodes alphanumeric data (0-9, A-Z, space, $, %, *, +, -, ., /, :): two characters in 11 bits (first * 45 + second), a last odd
    /// one in 6.
    /// </summary>
    /// <remarks>
    /// The analysis or the plan has found every character in the alphabet, so the writers check a whole step at once rather than each
    /// character. A step with a character outside the alphabet is not written: the pairs ahead of that character are written a pair at
    /// a time (<see cref="WriteAlphanumericTail"/>), and <see cref="WriteAlphanumericChecked"/> takes the run from the pair that holds it,
    /// and throws at that character as the writer always has. A run under eight characters, shorter than any step, is written here
    /// (<see cref="WriteAlphanumericTail"/>), with no call to a writer.
    /// </remarks>
    private void WriteAlphanumericData(ReadOnlySpan<char> chars)
    {
        if (chars.Length < 8)
        {
            WriteAlphanumericTail(ref _writer, chars, 0);
            return;
        }
#if NET8_0_OR_GREATER
        if (System.Runtime.Intrinsics.X86.Ssse3.IsSupported && System.Runtime.Intrinsics.X86.Sse41.IsSupported)
        {
            WriteAlphanumericSsse3(ref _writer, chars);
            return;
        }
        // The NEON step's shortest run depends on the runtime: every run here on .NET 10, 32 characters on .NET 8 (AlphanumericAdvSimdMinimum).
        if (System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported && chars.Length >= AlphanumericAdvSimdMinimum)
        {
            WriteAlphanumericAdvSimd(ref _writer, chars);
            return;
        }
        // The WebAssembly tier's step is sixteen characters; a shorter run takes the portable writer.
        if (System.Runtime.Intrinsics.Wasm.PackedSimd.IsSupported && chars.Length >= 16)
        {
            WriteAlphanumericPackedSimd(ref _writer, chars);
            return;
        }
#endif
        WriteAlphanumericScalar(ref _writer, chars);
    }

    /// <summary>The portable Alphanumeric writer: four pairs a 44-bit append, then pairs and a last odd character.</summary>
    internal static void WriteAlphanumericScalar(ref BitWriter writer, ReadOnlySpan<char> chars) => WriteAlphanumericScalar(ref writer, chars, 0);

    /// <summary>
    /// The portable writer from <paramref name="start"/>, where a vector tier stopped. A value is one table load, -1 outside the alphabet
    /// (<see cref="CharacterSets.AlphanumericValues"/>), and a step's characters and values are checked by one OR: a character past 0x7F,
    /// or a -1, sets a bit no character or value of the alphabet sets.
    /// Not inlined: inlined into its two-parameter entry, an earlier body, whose throwing path still sat in its loop, ran out of the JIT's
    /// inlining budget before its 8-byte store, which was left a call: 1.64 times the old writer's time at 16 characters, against 0.77 not
    /// inlined. This body inlined reads 0.81 against 0.71 at 16 characters and level from 300 (JIT with AVX2, 2026-10-05).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteAlphanumericScalar(ref BitWriter writer, ReadOnlySpan<char> chars, int start)
    {
        var values = CharacterSets.AlphanumericValues;
        var i = start;
        for (; i + 7 < chars.Length; i += 8)
        {
            int c0 = chars[i], c1 = chars[i + 1], c2 = chars[i + 2], c3 = chars[i + 3];
            int c4 = chars[i + 4], c5 = chars[i + 5], c6 = chars[i + 6], c7 = chars[i + 7];
            int v0 = values[c0 & 0x7F], v1 = values[c1 & 0x7F], v2 = values[c2 & 0x7F], v3 = values[c3 & 0x7F];
            int v4 = values[c4 & 0x7F], v5 = values[c5 & 0x7F], v6 = values[c6 & 0x7F], v7 = values[c7 & 0x7F];
            if ((((c0 | c1 | c2 | c3 | c4 | c5 | c6 | c7) >> 7) | ((v0 | v1 | v2 | v3 | v4 | v5 | v6 | v7) >> 6)) != 0)
                break;
            var high = (ulong)(uint)(((v0 * 45 + v1) << 11) | (v2 * 45 + v3));
            var low = (uint)(((v4 * 45 + v5) << 11) | (v6 * 45 + v7));
            writer.WriteWide((high << 22) | low, 44);
        }
        WriteAlphanumericTail(ref writer, chars, i);
    }

    /// <summary>
    /// The characters from <paramref name="i"/>: pairs and a last odd character, and the rest of the run to
    /// <see cref="WriteAlphanumericChecked"/> at a pair with a character outside the alphabet.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteAlphanumericTail(ref BitWriter writer, ReadOnlySpan<char> chars, int i)
    {
        var values = CharacterSets.AlphanumericValues;
        for (; i + 1 < chars.Length; i += 2)
        {
            int c0 = chars[i], c1 = chars[i + 1];
            int v0 = values[c0 & 0x7F], v1 = values[c1 & 0x7F];
            if ((((c0 | c1) >> 7) | ((v0 | v1) >> 6)) != 0)
            {
                WriteAlphanumericChecked(ref writer, chars.Slice(i));
                return;
            }
            writer.Write(v0 * 45 + v1, 11);
        }
        if (i < chars.Length)
        {
            int c = chars[i];
            int v = values[c & 0x7F];
            if (((c >> 7) | (v >> 6)) != 0)
            {
                WriteAlphanumericChecked(ref writer, chars.Slice(i));
                return;
            }
            writer.Write(v, 6);
        }
    }

    /// <summary>
    /// The writer with a check a character: the path of a run with a character outside the alphabet, which throws there after writing
    /// the pairs ahead of it. Not inlined: inside a writer's loop it took the inlining budget its appends need.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteAlphanumericChecked(ref BitWriter writer, ReadOnlySpan<char> chars)
    {
        var i = 0;
        for (; i + 1 < chars.Length; i += 2)
            writer.Write(CharacterSets.GetAlphanumericValue(chars[i]) * 45 + CharacterSets.GetAlphanumericValue(chars[i + 1]), 11);
        if (i < chars.Length)
            writer.Write(CharacterSets.GetAlphanumericValue(chars[i]), 6);
    }

    /// <summary>
    /// Writes Kanji data: 13 bits per character, the compacted Shift_JIS value (ISO/IEC 18004 8.4.5) that <see cref="ShiftJisKanjiReverseTable"/> holds for it.
    /// </summary>
    /// <remarks>
    /// Two characters share one 26-bit write.
    /// A character without a cell is caught after the loop, from the OR of the lookups (a miss is -1), and refused rather than written as some other character.
    /// </remarks>
    private void WriteKanjiData(ReadOnlySpan<char> chars)
    {
        var misses = 0;
        var i = 0;
        for (; i + 1 < chars.Length; i += 2)
        {
            var first = ShiftJisKanjiReverseTable.Lookup(chars[i]);
            var second = ShiftJisKanjiReverseTable.Lookup(chars[i + 1]);
            misses |= first | second;
            _writer.Write((first << 13) | (second & 0x1FFF), 26);
        }
        if (i < chars.Length)
        {
            var last = ShiftJisKanjiReverseTable.Lookup(chars[i]);
            misses |= last;
            _writer.Write(last, 13);
        }

        if (misses < 0)
            KanjiCells.ThrowCharacterWithoutCell(chars);
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
