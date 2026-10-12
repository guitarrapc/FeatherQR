using System.Buffers;
using System.Text;
using FeatherQR.Internals;
using FeatherQR.Internals.BinaryDecoders;
using FeatherQR.Internals.BinaryEncoders;

namespace FeatherQR.Tests;

/// <summary>
/// What <see cref="SegmentDecoders.DecodeBytePayload"/> makes of a byte segment, against the rule
/// written out here from the specification of the behaviour: ISO-8859-1 widens; a declared UTF-8
/// segment, or one that opens with a BOM, decodes as UTF-8 and substitutes what is invalid; a
/// declared Shift_JIS segment is read through the JIS X 0208 cells and refused where it is not well
/// formed or holds a cell outside them; an undeclared one is UTF-8 when every sequence is well
/// formed, else Shift_JIS when the guess written out below holds, and ISO-8859-1 otherwise.
/// </summary>
/// <remarks>
/// <para>
/// The decoder has two implementations of that rule: a hand-written validator followed by the
/// framework's transcoder, and on net8.0 and later the framework's one-pass transcoder deciding
/// validity itself. The oracle below uses neither (<see cref="Rune.DecodeFromUtf8"/> one scalar at a
/// time), so both are held to the same answer.
/// </para>
/// <para>
/// <see cref="SegmentDecoders.ResolvesToUtf8WhenUnspecified"/> and
/// <see cref="SegmentDecoders.ResolvesToIso8859_1WhenUnspecified"/> are checked in the same pass: the
/// Micro QR generator and its planner ask what the decoder will do, so a rule that moves in one and
/// not the other turns into a symbol the decoder misreads.
/// </para>
/// </remarks>
public class ByteSegmentCharsetParityTest
{
    private static readonly ByteSegmentCharset[] charsets = [ByteSegmentCharset.Unspecified, ByteSegmentCharset.Iso8859_1, ByteSegmentCharset.Utf8, ByteSegmentCharset.ShiftJis];

    private static bool IsWellFormed(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            if (Rune.DecodeFromUtf8(bytes, out _, out var consumed) != OperationStatus.Done)
                return false;
            bytes = bytes.Slice(consumed);
        }
        return true;
    }

    private static string Latin1(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
            chars[i] = (char)bytes[i];
        return new string(chars);
    }

    private enum Token
    {
        Ascii,
        Control,
        Kana,
        Pair,
        Malformed,
    }

    /// <summary>The bytes as Shift_JIS reads them: ASCII, a half-width katakana byte, a lead and trail pair, or a byte that is none of these where it stands.</summary>
    private static List<(Token Kind, int Code)> ShiftJisTokens(byte[] payload)
    {
        var tokens = new List<(Token, int)>();
        for (var i = 0; i < payload.Length; i++)
        {
            int b = payload[i];
            if (b <= 0x7F)
                tokens.Add((b < 0x20 && b is not ('\t' or '\r' or '\n') ? Token.Control : Token.Ascii, b));
            else if (b is >= 0xA1 and <= 0xDF)
                tokens.Add((Token.Kana, b));
            else if (b is (>= 0x81 and <= 0x9F) or (>= 0xE0 and <= 0xEF) && i + 1 < payload.Length && payload[i + 1] is >= 0x40 and <= 0xFC and not 0x7F)
                tokens.Add((Token.Pair, b << 8 | payload[++i]));
            else
                tokens.Add((Token.Malformed, b));
        }
        return tokens;
    }

    private static int LongestRun(List<(Token Kind, int Code)> tokens, Token kind)
    {
        int longest = 0, current = 0;
        foreach (var token in tokens)
        {
            current = token.Kind == kind ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }
        return longest;
    }

    /// <summary>The guess for an undeclared segment that is not UTF-8: well formed Shift_JIS with no control character but tab, CR and LF, and a run of three half-width katakana or a byte ISO-8859-1 text never holds.</summary>
    private static bool GuessesShiftJis(byte[] payload)
    {
        var tokens = ShiftJisTokens(payload);
        if (tokens.Any(static t => t.Kind is Token.Malformed or Token.Control))
            return false;
        return LongestRun(tokens, Token.Kana) >= 3 || payload.Any(static b => b is >= 0x80 and <= 0x9F);
    }

    private static (DecodeStatus Status, string Text) ShiftJis(byte[] payload)
    {
        var tokens = ShiftJisTokens(payload);
        if (tokens.Any(static t => t.Kind == Token.Malformed))
            return (DecodeStatus.InvalidBitstream, "");

        var text = new StringBuilder();
        foreach (var (kind, code) in tokens)
        {
            if (kind == Token.Kana)
            {
                text.Append((char)(0xFF61 + code - 0xA1));
            }
            else if (kind == Token.Pair)
            {
                // The Kanji-mode cell of the pair: rows of 0xC0 from lead 0x81, and from 0xE0 after the gap, as far as the table goes (0xEBBF)
                int lead = code >> 8, trail = code & 0xFF;
                var row = lead <= 0x9F ? lead - 0x81 : lead - 0xC1;
                var index = row * 0xC0 + (trail - 0x40);
                var cell = index < ShiftJisKanjiTable.IndexCount ? ShiftJisKanjiTable.Lookup(index) : '\0';
                if (cell == '\0')
                    return (DecodeStatus.UnmappedCharacter, "");
                text.Append(cell);
            }
            else
            {
                text.Append((char)code);
            }
        }
        return (DecodeStatus.Success, text.ToString());
    }

    private static (DecodeStatus Status, string Text) Expected(byte[] payload, ByteSegmentCharset charset)
    {
        if (charset == ByteSegmentCharset.Iso8859_1)
            return (DecodeStatus.Success, Latin1(payload));
        if (charset == ByteSegmentCharset.ShiftJis)
            return ShiftJis(payload);
        var hasBom = payload.Length >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF;
        if (hasBom)
            return (DecodeStatus.Success, Encoding.UTF8.GetString(payload, 3, payload.Length - 3));
        if (charset == ByteSegmentCharset.Utf8 || IsWellFormed(payload))
            return (DecodeStatus.Success, Encoding.UTF8.GetString(payload));
        return GuessesShiftJis(payload) ? ShiftJis(payload) : (DecodeStatus.Success, Latin1(payload));
    }

    /// <summary>
    /// Every one- and two-byte string, the boundary bytes of every three- and four-byte form
    /// (overlongs, surrogates, the first value past U+10FFFF, leads that do not exist, truncated
    /// tails), each also behind a BOM and inside ASCII, plus well-formed text long enough for a
    /// vector path.
    /// </summary>
    private static IEnumerable<byte[]> Corpus()
    {
        yield return [];
        for (var a = 0; a < 256; a++)
        {
            yield return [(byte)a];
            for (var b = 0; b < 256; b++)
                yield return [(byte)a, (byte)b];
        }

        byte[] seconds = [0x7F, 0x80, 0x8F, 0x90, 0x9F, 0xA0, 0xBF, 0xC0];
        byte[] tails = [0x7F, 0x80, 0xBF, 0xC0];
        foreach (var lead in new byte[] { 0xE0, 0xE1, 0xEC, 0xED, 0xEE, 0xEF })
        {
            foreach (var second in seconds)
            {
                foreach (var third in tails)
                    yield return [lead, second, third];
            }
        }
        foreach (var lead in new byte[] { 0xF0, 0xF1, 0xF3, 0xF4, 0xF5, 0xF7, 0xF8, 0xFF })
        {
            foreach (var second in seconds)
            {
                foreach (var third in tails)
                {
                    foreach (var fourth in tails)
                        yield return [lead, second, third, fourth];
                }
            }
        }

        var prose = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox, こんにちは世界, naïve café, 𝄞 clef. ", 12)));
        yield return prose;
        // one bad byte at each end and in the middle of text long enough to be read in vector steps
        foreach (var at in new[] { 0, prose.Length / 2, prose.Length - 1 })
        {
            var broken = (byte[])prose.Clone();
            broken[at] = 0xFF;
            yield return broken;
        }
        // a sequence cut short by the end of the segment
        yield return prose.AsSpan(0, prose.Length - 2).ToArray();

        // Shift_JIS: runs of two and three of each kind, a control character, and prose
        byte[][] units = [[0x41], [0x0D], [0x09], [0xB1], [0xDF], [0xE9, 0x61], [0xE0, 0xFC], [0x83, 0x82], [0x87, 0x40], [0xEC, 0x40], [0x80], [0xFD]];
        foreach (var a in units)
        {
            foreach (var b in units)
            {
                foreach (var c in units)
                {
                    yield return [.. a, .. b, .. c];
                    yield return [.. a, .. b, .. c, .. c];
                }
            }
        }
        var japanese = Encoding.GetEncoding(932).GetBytes(string.Concat(Enumerable.Repeat("Google モバイル\r\nﾃﾞｻﾞｲﾝQR 価格は100円です。", 6)));
        yield return japanese;
        yield return japanese.AsSpan(0, japanese.Length - 1).ToArray();
    }

    private static IEnumerable<byte[]> CorpusWithFramings()
    {
        foreach (var payload in Corpus())
        {
            yield return payload;
            if (payload.Length is > 0 and <= 4)
            {
                yield return [0xEF, 0xBB, 0xBF, .. payload];
                yield return [(byte)'a', .. payload, (byte)'z'];
            }
        }
    }

    private static (DecodeStatus Status, string Text) Decode(byte[] payload, ByteSegmentCharset charset, int leadingBits, int destinationLength)
    {
        // leadingBits of junk first, so the payload starts at every alignment inside a byte
        var stream = new byte[payload.Length + 2];
        var bit = 0;
        for (; bit < leadingBits; bit++)
            stream[bit >> 3] |= (byte)(0x80 >> (bit & 7));
        foreach (var value in payload)
        {
            for (var k = 7; k >= 0; k--, bit++)
            {
                if ((value >> k & 1) != 0)
                    stream[bit >> 3] |= (byte)(0x80 >> (bit & 7));
            }
        }

        var reader = new BitReader(stream);
        if (leadingBits > 0)
            reader.Reads(leadingBits);
        var destination = new char[destinationLength];
        var charsWritten = 0;
        var status = SegmentDecoders.DecodeBytePayload(ref reader, leadingBits + payload.Length * 8, payload.Length, charset, new byte[payload.Length + 8], destination, ref charsWritten);
        return (status, new string(destination, 0, charsWritten));
    }

    [Test]
    public async Task DecodeBytePayload_MatchesTheRule_OverTheCorpus()
    {
        var mismatches = new List<string>();
        var cases = 0;
        foreach (var payload in CorpusWithFramings())
        {
            foreach (var charset in charsets)
            {
                var (expectedStatus, expected) = Expected(payload, charset);
                // A refused segment gets room for any reading, so that the refusal is not for want of it
                var (status, text) = Decode(payload, charset, cases % 8, expectedStatus == DecodeStatus.Success ? expected.Length : payload.Length);
                if (status != expectedStatus || (status == DecodeStatus.Success && text != expected))
                    mismatches.Add($"{charset} {Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 8)))} ({payload.Length} B): {status}, expected {expectedStatus}");
                cases++;
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    /// <summary>
    /// One char short of what the rule produces is <c>DestinationTooSmall</c>, whichever reading
    /// applies. The one-pass transcoder meets a short destination before it has seen whether the
    /// rest of the segment is well formed, so this is where the two implementations could part.
    /// </summary>
    [Test]
    public async Task DecodeBytePayload_DestinationOneShort_IsDestinationTooSmall_OverTheCorpus()
    {
        var mismatches = new List<string>();
        foreach (var payload in CorpusWithFramings())
        {
            foreach (var charset in charsets)
            {
                var (expectedStatus, expected) = Expected(payload, charset);
                if (expectedStatus != DecodeStatus.Success || expected.Length == 0)
                    continue;
                var (status, _) = Decode(payload, charset, 0, expected.Length - 1);
                if (status != DecodeStatus.DestinationTooSmall)
                    mismatches.Add($"{charset} {Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 8)))} ({payload.Length} B): {status}");
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task ResolvesToUtf8WhenUnspecified_AgreesWithTheDecoder_OverTheCorpus()
    {
        var mismatches = new List<string>();
        foreach (var payload in CorpusWithFramings())
        {
            var hasBom = payload.Length >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF;
            if (SegmentDecoders.ResolvesToUtf8WhenUnspecified(payload) != (hasBom || IsWellFormed(payload)))
                mismatches.Add(Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 8))));
        }

        await Assert.That(mismatches).IsEmpty();
    }

    /// <summary>The Micro QR generator asks this before it writes ISO-8859-1 bytes with no ECI to declare them, so it has to say what the decoder does.</summary>
    [Test]
    public async Task ResolvesToIso8859_1WhenUnspecified_AgreesWithTheDecoder_OverTheCorpus()
    {
        var mismatches = new List<string>();
        foreach (var payload in CorpusWithFramings())
        {
            var hasBom = payload.Length >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF;
            var latin1 = !hasBom && !IsWellFormed(payload) && !GuessesShiftJis(payload);
            if (SegmentDecoders.ResolvesToIso8859_1WhenUnspecified(payload) != latin1)
                mismatches.Add(Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 8))));
        }

        await Assert.That(mismatches).IsEmpty();
    }
}
