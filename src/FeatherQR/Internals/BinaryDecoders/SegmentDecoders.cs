#if NET8_0_OR_GREATER
// OperationStatus, for the one-pass UTF-8 transcode.
using System.Buffers;
using System.Text.Unicode;
#elif !NETSTANDARD2_1_OR_GREATER
// ArrayPool is only reached from the netstandard2.0 branch of DecodeUtf8.
using System.Buffers;
#endif
#if !NET8_0_OR_GREATER
using System.Text;
#endif
using System.Runtime.CompilerServices;
using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Internals.BinaryDecoders;

/// <summary>
/// Effective charset of a Byte mode segment.
/// Standard QR and rMQR can pin it via an ECI header; Micro QR has no ECI, so it is always <see cref="Unspecified"/> there.
/// </summary>
internal enum ByteSegmentCharset
{
    /// <summary>No declared charset: UTF-8 when the payload validates as UTF-8, else Shift_JIS when the bytes give it away, else ISO-8859-1.</summary>
    Unspecified,
    Iso8859_1,
    Utf8,

    /// <summary>ECI 20. Read through the JIS X 0208 cells Kanji mode reads, plus JIS X 0201's half-width katakana.</summary>
    ShiftJis,
}

/// <summary>
/// Segment payload decoders shared by the symbology bitstream decoders (Standard QR, Micro QR, rMQR).
/// The mode/count indicator layout differs per symbology and stays in the caller; the payload bit groups (ISO/IEC 18004 7.4.3-7.4.5: numeric 10/7/4, alphanumeric 11/6, byte 8·count) and the byte charset heuristics are identical.
/// </summary>
internal static class SegmentDecoders
{
    // Value → character table for alphanumeric mode (ISO/IEC 18004 Table 5),
    // the inverse of CharacterSets.GetAlphanumericValue.
    private const string AlphanumericChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    // ISO/IEC 18004 8.4.5: each Kanji character is one 13-bit compacted Shift_JIS value.
    private const int KanjiBitsPerCharacter = 13;

    /// <summary>Decodes a numeric segment payload of <paramref name="count"/> digits.</summary>
    public static DecodeStatus DecodeNumericPayload(ref BitReader reader, int totalBits, int count, Span<char> destination, ref int charsWritten)
    {
        // Bitstream sufficiency before destination sufficiency, as byte mode already
        // does. A count read off the wire can exceed what the remaining bits could
        // possibly encode, and such a stream is malformed whatever buffer the caller
        // passed; reporting DestinationTooSmall for it would tell a caller sizing its
        // buffer to grow, and — because callers treat that status as "the symbol was
        // read" — would stop the image decoder looking for the real symbol.
        // 3 digits per 10 bits, then 2 per 7 and 1 per 4: 10·(n/3) + the remainder's cost.
        if (totalBits - reader.BitPosition < 10 * (count / 3) + ((count % 3) switch { 2 => 7, 1 => 4, _ => 0 }))
            return DecodeStatus.InvalidBitstream;

        if (destination.Length - charsWritten < count)
            return DecodeStatus.DestinationTooSmall;

        // Groups of 3 digits (10 bits), then 2 digits (7 bits) or 1 digit (4 bits)
        while (count >= 3)
        {
            if (totalBits - reader.BitPosition < 10)
                return DecodeStatus.InvalidBitstream;
            var value = reader.Reads(10);
            if (value > 999)
                return DecodeStatus.InvalidBitstream;
            destination[charsWritten++] = (char)('0' + value / 100);
            destination[charsWritten++] = (char)('0' + value / 10 % 10);
            destination[charsWritten++] = (char)('0' + value % 10);
            count -= 3;
        }
        if (count == 2)
        {
            if (totalBits - reader.BitPosition < 7)
                return DecodeStatus.InvalidBitstream;
            var value = reader.Reads(7);
            if (value > 99)
                return DecodeStatus.InvalidBitstream;
            destination[charsWritten++] = (char)('0' + value / 10);
            destination[charsWritten++] = (char)('0' + value % 10);
        }
        else if (count == 1)
        {
            if (totalBits - reader.BitPosition < 4)
                return DecodeStatus.InvalidBitstream;
            var value = reader.Reads(4);
            if (value > 9)
                return DecodeStatus.InvalidBitstream;
            destination[charsWritten++] = (char)('0' + value);
        }

        return DecodeStatus.Success;
    }

    /// <summary>Decodes an alphanumeric segment payload of <paramref name="count"/> characters.</summary>
    public static DecodeStatus DecodeAlphanumericPayload(ref BitReader reader, int totalBits, int count, Span<char> destination, ref int charsWritten)
    {
        // Bitstream sufficiency first; see DecodeNumericPayload for why the order matters.
        // 2 characters per 11 bits, then 6 bits for an odd one.
        if (totalBits - reader.BitPosition < 11 * (count / 2) + (count % 2) * 6)
            return DecodeStatus.InvalidBitstream;

        if (destination.Length - charsWritten < count)
            return DecodeStatus.DestinationTooSmall;

        // Pairs of characters (11 bits), then a single character (6 bits)
        while (count >= 2)
        {
            if (totalBits - reader.BitPosition < 11)
                return DecodeStatus.InvalidBitstream;
            var value = reader.Reads(11);
            if (value >= 45 * 45)
                return DecodeStatus.InvalidBitstream;
            destination[charsWritten++] = AlphanumericChars[value / 45];
            destination[charsWritten++] = AlphanumericChars[value % 45];
            count -= 2;
        }
        if (count == 1)
        {
            if (totalBits - reader.BitPosition < 6)
                return DecodeStatus.InvalidBitstream;
            var value = reader.Reads(6);
            if (value >= 45)
                return DecodeStatus.InvalidBitstream;
            destination[charsWritten++] = AlphanumericChars[value];
        }

        return DecodeStatus.Success;
    }

    /// <summary>
    /// Decodes a Kanji segment payload of <paramref name="count"/> characters (ISO/IEC 18004 8.4.5): 13 bits per character, JIS X 0208 via <see cref="ShiftJisKanjiTable"/>.
    /// </summary>
    /// <remarks>
    /// It reads Kanji segments from any encoder, this library's included (written with <c>AllowKanji</c>).
    /// Unmapped cells fail the segment rather than yielding a replacement character, because a Kanji segment carries no redundancy of its own and a guessed character is indistinguishable from a correct one.
    /// </remarks>
    public static DecodeStatus DecodeKanjiPayload(ref BitReader reader, int totalBits, int count, Span<char> destination, ref int charsWritten)
    {
        // Bitstream sufficiency first; see DecodeNumericPayload for why the order matters.
        if (totalBits - reader.BitPosition < count * KanjiBitsPerCharacter)
            return DecodeStatus.InvalidBitstream;

        if (destination.Length - charsWritten < count)
            return DecodeStatus.DestinationTooSmall;

        for (var i = 0; i < count; i++)
        {
            var value = reader.Reads(KanjiBitsPerCharacter);
            var mapped = ShiftJisKanjiTable.Lookup(value);
            if (mapped == '\0')
            {
                // A value no Shift_JIS pair can express is corruption; a well-formed
                // value outside the JIS X 0208 repertoire is a character we cannot map.
                // The two get different statuses because they call for different things
                // from the caller: discard the symbol, or hand it to a CP932 reader.
                return ShiftJisKanjiTable.IsStructurallyValid(value)
                    ? DecodeStatus.UnmappedCharacter
                    : DecodeStatus.InvalidBitstream;
            }

            destination[charsWritten++] = mapped;
        }

        return DecodeStatus.Success;
    }

    /// <summary>
    /// Decodes a byte segment payload of <paramref name="count"/> bytes, resolving the effective charset (UTF-8 heuristic / BOM handling / ISO-8859-1 widening).
    /// </summary>
    /// <param name="reader">Reader positioned at the first byte of the segment. It is advanced past the segment.</param>
    /// <param name="totalBits">Length of the whole bitstream, in bits; used to reject a segment that runs past the end.</param>
    /// <param name="count">Number of bytes in this segment.</param>
    /// <param name="charset">The charset declared for the segment, or <see cref="ByteSegmentCharset.Unspecified"/> to infer it from the bytes.</param>
    /// <param name="byteBuffer">Scratch buffer for the segment bytes; must hold at least <paramref name="count"/> bytes.</param>
    /// <param name="destination">Receives the decoded text.</param>
    /// <param name="charsWritten">How many characters <paramref name="destination"/> already holds. This call advances the count.</param>
    public static DecodeStatus DecodeBytePayload(ref BitReader reader, int totalBits, int count, ByteSegmentCharset charset, byte[] byteBuffer, Span<char> destination, ref int charsWritten)
    {
        if (totalBits - reader.BitPosition < count * 8)
            return DecodeStatus.InvalidBitstream;
        var buffer = byteBuffer.AsSpan(0, count);
        reader.ReadBytes(buffer);

        // Converted once, here: a conversion at a rarely run call below stays a call and costs every decode its frame
        ReadOnlySpan<byte> bytes = buffer;

        // Declared Shift_JIS is not tried as UTF-8 first, and EF BB BF in it is no byte order mark
        if (charset == ByteSegmentCharset.ShiftJis)
            return DecodeShiftJis(bytes, destination, ref charsWritten);

#if NET8_0_OR_GREATER
        // A UTF-8 BOM (the encoder can emit one with utf8BOM: true) is consumed, not decoded,
        // but an explicit ECI ISO-8859-1 declaration wins over it: there, EF BB BF is the
        // legitimate Latin-1 text "ï»¿".
        if (charset != ByteSegmentCharset.Iso8859_1)
        {
            var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

            // One pass: the transcoder validates as it writes. A declared charset or a BOM
            // substitutes what is invalid; undeclared, an invalid sequence means ISO-8859-1.
            var status = Utf8.ToUtf16(hasBom ? bytes.Slice(3) : bytes, destination.Slice(charsWritten), out _, out var written, replaceInvalidSequences: hasBom || charset == ByteSegmentCharset.Utf8);
            if (status == OperationStatus.Done)
            {
                charsWritten += written;
                return DecodeStatus.Success;
            }

            // Too small for the UTF-8 reading is too small for the ISO-8859-1 one, which needs a char per byte,
            // so this holds before knowing whether the rest of the segment is well formed.
            // A Shift_JIS reading needs a char per pair, so a segment that turns out to be one is counted below.
            if (status == OperationStatus.DestinationTooSmall
                && (hasBom || charset == ByteSegmentCharset.Utf8 || IsValidUtf8(bytes) || !GuessesShiftJis(bytes)))
                return DecodeStatus.DestinationTooSmall;
        }
#else
        // Resolve the effective charset. A UTF-8 BOM (the encoder can emit one with
        // utf8BOM: true) is consumed, not decoded, but an explicit ECI ISO-8859-1
        // declaration wins over the BOM heuristic: there, EF BB BF is the legitimate
        // Latin-1 text "ï»¿".
        var useUtf8 = charset switch
        {
            ByteSegmentCharset.Utf8 => true,
            ByteSegmentCharset.Iso8859_1 => false,
            _ => IsValidUtf8(bytes),
        };
        if (charset != ByteSegmentCharset.Iso8859_1
            && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            useUtf8 = true;
            bytes = bytes.Slice(3);
        }

        if (useUtf8)
            return DecodeUtf8(byteBuffer, bytes.Length == count ? 0 : 3, bytes.Length, destination, ref charsWritten);
#endif

        // Undeclared and not UTF-8: encoders that write Shift_JIS here leave the ECI header out
        if (charset == ByteSegmentCharset.Unspecified && GuessesShiftJis(bytes))
            return DecodeShiftJis(bytes, destination, ref charsWritten);

        // ISO-8859-1 → UTF-16 is a pure widening cast
        if (destination.Length - charsWritten < bytes.Length)
            return DecodeStatus.DestinationTooSmall;
        for (var i = 0; i < bytes.Length; i++)
        {
            destination[charsWritten + i] = (char)bytes[i];
        }
        charsWritten += bytes.Length;
        return DecodeStatus.Success;
    }

    /// <summary>
    /// Whether the charset resolution above would read a byte segment as UTF-8 when no charset is declared: the validity heuristic, or a leading BOM (which is consumed).
    /// The first half of <see cref="ResolvesToIso8859_1WhenUnspecified"/>.
    /// </summary>
    public static bool ResolvesToUtf8WhenUnspecified(ReadOnlySpan<byte> bytes)
        => IsValidUtf8(bytes)
            || (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

    /// <summary>
    /// Whether the charset resolution above reads an undeclared byte segment as ISO-8859-1, which is neither of its other two readings.
    /// The Micro QR generator and its mixed-mode planner ask this before they write ISO-8859-1 bytes with no ECI header to declare them, the whole text or a run a split isolates from its disambiguating neighbours.
    /// It must mirror <see cref="DecodeBytePayload"/> exactly, which is why it lives here rather than beside them.
    /// </summary>
    public static bool ResolvesToIso8859_1WhenUnspecified(ReadOnlySpan<byte> bytes)
        => !ResolvesToUtf8WhenUnspecified(bytes) && !GuessesShiftJis(bytes);

    /// <summary>
    /// Whether an undeclared segment that is not UTF-8 is read as Shift_JIS: its bytes are well formed Shift_JIS with no control character but tab, CR and LF,
    /// and hold a run of three half-width katakana, or a byte from 0x80 to 0x9F, which ISO-8859-1 text does not.
    /// </summary>
    private static bool GuessesShiftJis(ReadOnlySpan<byte> bytes)
    {
        var katakanaRun = 0;
        var telling = false;
        for (var i = 0; i < bytes.Length; i++)
        {
            int b = bytes[i];
            if (b < 0x80)
            {
                // Binary data, not text
                if (b < 0x20 && b != '\t' && b != '\r' && b != '\n')
                    return false;
                katakanaRun = 0;
            }
            else if (b is >= 0xA1 and <= 0xDF)
            {
                telling |= ++katakanaRun >= 3;
            }
            else
            {
                if (!IsShiftJisPair(bytes, i))
                    return false;
                katakanaRun = 0;

                // Pairs alone tell nothing: é before a letter is one, and French has three in a row
                telling |= b <= 0x9F || bytes[i + 1] is >= 0x80 and <= 0x9F;
                i++;
            }
        }
        return telling;
    }

    /// <summary>A lead byte at <paramref name="index"/> with a trail byte after it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsShiftJisPair(ReadOnlySpan<byte> bytes, int index)
        => bytes[index] is (>= 0x81 and <= 0x9F) or (>= 0xE0 and <= 0xEF)
            && index + 1 < bytes.Length
            && bytes[index + 1] is >= 0x40 and <= 0xFC and not 0x7F;

    /// <summary>
    /// Shift_JIS to UTF-16: ASCII as it stands, JIS X 0201's half-width katakana, and pairs through the JIS X 0208 cells Kanji mode reads.
    /// A segment that is not well formed is <see cref="DecodeStatus.InvalidBitstream"/>, and a pair with no cell is <see cref="DecodeStatus.UnmappedCharacter"/>, as an unmapped Kanji value is.
    /// </summary>
    private static DecodeStatus DecodeShiftJis(ReadOnlySpan<byte> bytes, Span<char> destination, ref int charsWritten)
    {
        var characters = 0;
        for (var i = 0; i < bytes.Length; i++, characters++)
        {
            int b = bytes[i];
            if (b < 0x80 || b is >= 0xA1 and <= 0xDF)
                continue;
            if (!IsShiftJisPair(bytes, i))
                return DecodeStatus.InvalidBitstream;
            i++;
        }
        if (destination.Length - charsWritten < characters)
            return DecodeStatus.DestinationTooSmall;

        for (var i = 0; i < bytes.Length; i++)
        {
            int b = bytes[i];
            if (b < 0x80)
            {
                destination[charsWritten++] = (char)b;
            }
            else if (b <= 0xDF && b >= 0xA1)
            {
                destination[charsWritten++] = (char)(b + (0xFF61 - 0xA1));
            }
            else
            {
                // The pair's Kanji-mode value (ISO/IEC 18004 8.4.5). Pairs past 0xEBBF have none
                var pair = b << 8 | bytes[++i];
                var folded = pair - (pair <= 0x9FFC ? 0x8140 : 0xC140);
                var mapped = pair <= 0xEBBF ? ShiftJisKanjiTable.Lookup((folded >> 8) * 0xC0 + (folded & 0xFF)) : '\0';
                if (mapped == '\0')
                    return DecodeStatus.UnmappedCharacter;
                destination[charsWritten++] = mapped;
            }
        }
        return DecodeStatus.Success;
    }

#if !NET8_0_OR_GREATER
    // Two passes (count, then transcode); net8.0 and later transcode once in DecodeBytePayload.
    private static DecodeStatus DecodeUtf8(byte[] byteBuffer, int offset, int byteCount, Span<char> destination, ref int charsWritten)
    {
        if (byteCount == 0)
            return DecodeStatus.Success;

#if NETSTANDARD2_1_OR_GREATER
        var bytes = byteBuffer.AsSpan(offset, byteCount);
        if (destination.Length - charsWritten < Encoding.UTF8.GetCharCount(bytes))
            return DecodeStatus.DestinationTooSmall;
        charsWritten += Encoding.UTF8.GetChars(bytes, destination.Slice(charsWritten));
        return DecodeStatus.Success;
#else
        // netstandard2.0 has no span-based Encoding APIs; byteBuffer is already an
        // array, so only the char side needs a temporary rented array.
        var charCount = Encoding.UTF8.GetCharCount(byteBuffer, offset, byteCount);
        if (destination.Length - charsWritten < charCount)
            return DecodeStatus.DestinationTooSmall;

        var rentedChars = ArrayPool<char>.Shared.Rent(charCount);
        try
        {
            var written = Encoding.UTF8.GetChars(byteBuffer, offset, byteCount, rentedChars, 0);
            rentedChars.AsSpan(0, written).CopyTo(destination.Slice(charsWritten));
            charsWritten += written;
            return DecodeStatus.Success;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rentedChars, clearArray: false);
        }
#endif
    }
#endif

    /// <summary>
    /// Strict UTF-8 validation (RFC 3629): rejects overlongs, surrogates and values above U+10FFFF, so ISO-8859-1 payloads with high bytes fall through to the Latin-1 path instead of being mangled.
    /// </summary>
    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        var i = 0;
        while (i < bytes.Length)
        {
            var b = bytes[i];
            if (b < 0x80)
            {
                i++;
                continue;
            }

            int continuations;
            int codepoint;
            if ((b & 0xE0) == 0xC0)
            {
                continuations = 1;
                codepoint = b & 0x1F;
            }
            else if ((b & 0xF0) == 0xE0)
            {
                continuations = 2;
                codepoint = b & 0x0F;
            }
            else if ((b & 0xF8) == 0xF0)
            {
                continuations = 3;
                codepoint = b & 0x07;
            }
            else
            {
                return false;
            }

            if (i + continuations >= bytes.Length)
                return false;

            for (var j = 1; j <= continuations; j++)
            {
                var c = bytes[i + j];
                if ((c & 0xC0) != 0x80)
                    return false;
                codepoint = (codepoint << 6) | (c & 0x3F);
            }

            // Overlong encodings, UTF-16 surrogates and out-of-range values
            if (continuations == 1 && codepoint < 0x80)
                return false;
            if (continuations == 2 && (codepoint < 0x800 || (codepoint >= 0xD800 && codepoint <= 0xDFFF)))
                return false;
            if (continuations == 3 && (codepoint < 0x10000 || codepoint > 0x10FFFF))
                return false;

            i += continuations + 1;
        }

        return true;
    }

    /// <summary>
    /// Reads an ECI assignment number (ISO/IEC 18004 7.4.2, identical in ISO/IEC 23941): 1-3 bytes, length signaled by the leading bits (0xxxxxxx = 8 bits, 10xxxxxx = 16, 110xxxxx = 24).
    /// Lifted from the Standard QR bitstream decoder when rMQR became the second consumer.
    /// </summary>
    public static DecodeStatus ReadEciDesignator(ref BitReader reader, int totalBits, out int eciValue)
    {
        eciValue = 0;
        if (totalBits - reader.BitPosition < 8)
            return DecodeStatus.InvalidBitstream;

        var first = reader.Reads(8);
        if ((first & 0x80) == 0)
        {
            eciValue = first;
        }
        else if ((first & 0xC0) == 0x80)
        {
            if (totalBits - reader.BitPosition < 8)
                return DecodeStatus.InvalidBitstream;
            eciValue = ((first & 0x3F) << 8) | reader.Reads(8);
        }
        else if ((first & 0xE0) == 0xC0)
        {
            if (totalBits - reader.BitPosition < 16)
                return DecodeStatus.InvalidBitstream;
            eciValue = ((first & 0x1F) << 16) | reader.Reads(16);
        }
        else
        {
            return DecodeStatus.InvalidBitstream;
        }

        return DecodeStatus.Success;
    }
}
