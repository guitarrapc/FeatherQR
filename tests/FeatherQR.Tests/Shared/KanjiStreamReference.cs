using System.Text;
using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// Data codeword streams written from the standards' definitions, for checking the three Kanji writers.
/// Nothing here comes from the library's tables or writers: the Shift_JIS pair of a character comes from .NET's CP932 encoder, which agrees with JIS X 0208 on every encoder cell (the seven cells where the two disagree have none), the 13-bit value is computed from that pair as ISO/IEC 18004 8.4.5 states it, and the count indicator widths are transcriptions of the published tables.
/// </summary>
internal static class KanjiStreamReference
{
    /// <summary>
    /// One run of a stream: <c>N</c>umeric, <c>A</c>lphanumeric, <c>B</c>yte (ASCII / Latin-1, one byte a character), <c>U</c> (Byte mode carrying the text's UTF-8 bytes), <c>K</c>anji, or <c>E</c>, an ECI header whose text is the assignment number (Standard QR and rMQR only).
    /// </summary>
    public readonly record struct Run(char Mode, string Text)
    {
        /// <summary>The mode its header names: a UTF-8 run is a Byte run.</summary>
        public char Header => Mode == 'U' ? 'B' : Mode;

        /// <summary>What its count indicator carries: UTF-8 bytes for a UTF-8 run, characters otherwise.</summary>
        public int Units => Mode == 'U' ? Encoding.UTF8.GetByteCount(Text) : Text.Length;
    }

    private const string AlphanumericAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    /// <summary>
    /// Taken from the provider directly rather than registered: a registration is process-wide, and ZXing.Net, which other tests in this process use to decode, guesses Byte-segment charsets from what <see cref="Encoding"/> offers.
    /// </summary>
    private static readonly Encoding Cp932 = CodePagesEncodingProvider.Instance.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
        ?? throw new InvalidOperationException("CP932 is not available from CodePagesEncodingProvider.");

    /// <summary>
    /// ISO/IEC 18004 8.4.5: subtract 0x8140 (0x8140-0x9FFC) or 0xC140 (0xE040-0xEBBF) from the Shift_JIS pair, multiply the high byte by 0xC0 and add the low byte.
    /// </summary>
    public static int Value13(char c)
    {
        var bytes = Cp932.GetBytes([c]);
        if (bytes.Length != 2)
            throw new ArgumentException($"U+{(int)c:X4} is not a double-byte CP932 character.", nameof(c));

        var sjis = (bytes[0] << 8) | bytes[1];
        var shifted = sjis switch
        {
            >= 0x8140 and <= 0x9FFC => sjis - 0x8140,
            >= 0xE040 and <= 0xEBBF => sjis - 0xC140,
            _ => throw new ArgumentException($"U+{(int)c:X4} is Shift_JIS 0x{sjis:X4}, outside the Kanji-mode ranges.", nameof(c)),
        };
        return (shifted >> 8) * 0xC0 + (shifted & 0xFF);
    }

    /// <summary>
    /// Every character with an encoder cell, in a fixed shuffled order, so that a small symbol filled from the front of the list carries kana, kanji and symbols rather than one script.
    /// The set comes from the library's reverse table, whose membership <c>ShiftJisKanjiReverseTableUnitTest</c> pins; the values the reference writes for them do not.
    /// </summary>
    public static readonly char[] EncoderCells = BuildEncoderCells();

    private static char[] BuildEncoderCells()
    {
        var cells = new List<char>(ShiftJisKanjiReverseTable.EncoderCellCount);
        for (var c = 0; c < 65536; c++)
        {
            if (ShiftJisKanjiReverseTable.Lookup((char)c) >= 0)
                cells.Add((char)c);
        }

        var random = new Random(20260929);
        return cells.OrderBy(_ => random.Next()).ToArray();
    }

    /// <summary><paramref name="count"/> encoder cells taken cyclically from <paramref name="start"/>.</summary>
    public static string Cells(int start, int count)
    {
        var chars = new char[count];
        for (var i = 0; i < count; i++)
            chars[i] = EncoderCells[(start + i) % EncoderCells.Length];
        return new string(chars);
    }

    // ---- Standard QR (ISO/IEC 18004 Tables 2 and 3) --------------------------------

    public static int StandardQrCountBits(char mode, int version) => mode switch
    {
        'N' => version < 10 ? 10 : version < 27 ? 12 : 14,
        'A' => version < 10 ? 9 : version < 27 ? 11 : 13,
        'B' => version < 10 ? 8 : 16,
        'K' => version < 10 ? 8 : version < 27 ? 10 : 12,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static int StandardQrModeIndicator(char mode) => mode switch
    {
        'N' => 0b0001,
        'A' => 0b0010,
        'B' => 0b0100,
        'K' => 0b1000,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>The runs' bits without the terminator and padding.</summary>
    public static string StandardQrBits(int version, IEnumerable<Run> runs)
    {
        var bits = new StringBuilder();
        foreach (var run in runs)
        {
            if (run.Mode == 'E')
            {
                // ECI: 0111 and the assignment number, one byte for the assignments below 128.
                Append(bits, 0b0111, 4);
                Append(bits, int.Parse(run.Text), 8);
                continue;
            }
            Append(bits, StandardQrModeIndicator(run.Header), 4);
            Append(bits, run.Units, StandardQrCountBits(run.Header, version));
            AppendPayload(bits, run);
        }
        return bits.ToString();
    }

    public static byte[] StandardQrStream(int version, int dataCodewords, IEnumerable<Run> runs)
        => Finish(StandardQrBits(version, runs), terminatorLength: 4, dataCodewords * 8, dataCodewords);

    /// <summary>The smallest version whose data capacity at <paramref name="ecc"/> holds the runs, or 0; widths follow each version's count band.</summary>
    public static int SmallestStandardQrVersion(Run[] runs, QREccLevel ecc)
    {
        for (var version = 1; version <= 40; version++)
        {
            if (StandardQrBits(version, runs).Length <= Internals.StandardQR.QRCodeConstants.GetEccInfo(version, ecc).TotalDataCodewords * 8)
                return version;
        }
        return 0;
    }

    // ---- Micro QR (ISO/IEC 18004 Tables 2 and 3) ------------------------------------

    public static int MicroQrCountBits(char mode, int version) => mode switch
    {
        'N' => version + 2,
        'A' or 'B' => version + 1,
        'K' => version,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static int MicroQrModeIndicator(char mode) => mode switch
    {
        'N' => 0,
        'A' => 1,
        'B' => 2,
        'K' => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static string MicroQrBits(int version, IEnumerable<Run> runs)
    {
        var bits = new StringBuilder();
        foreach (var run in runs)
        {
            if (version > 1)
                Append(bits, MicroQrModeIndicator(run.Header), version - 1);
            Append(bits, run.Units, MicroQrCountBits(run.Header, version));
            AppendPayload(bits, run);
        }
        return bits.ToString();
    }

    /// <summary>M1 and M3 end on a 4-bit codeword, stored in the high nibble of the last byte.</summary>
    public static byte[] MicroQrStream(int version, int capacityBits, int dataCodewords, IEnumerable<Run> runs)
        => Finish(MicroQrBits(version, runs), terminatorLength: 2 * version + 1, capacityBits, dataCodewords);

    // ---- rMQR (ISO/IEC 23941 Tables 2 and 3) ----------------------------------------

    /// <summary>
    /// Count indicator widths per version (ISO index order, height-major), transcribed from the table the English Wikipedia article on rMQR reproduces from ISO/IEC 23941 Table 3, and in agreement with the rmqrcode Python library's own transcription where it was checked.
    /// Columns: Numeric, Alphanumeric, Byte, Kanji.
    /// </summary>
    private static readonly int[,] RmQrCountBitsTable =
    {
        { 4, 3, 3, 2 }, { 5, 5, 4, 3 }, { 6, 5, 5, 4 }, { 7, 6, 5, 5 }, { 7, 6, 6, 5 },
        { 5, 5, 4, 3 }, { 6, 5, 5, 4 }, { 7, 6, 5, 5 }, { 7, 6, 6, 5 }, { 8, 7, 6, 6 },
        { 4, 4, 3, 2 }, { 6, 5, 5, 4 }, { 7, 6, 5, 5 }, { 7, 6, 6, 5 }, { 8, 7, 6, 6 }, { 8, 7, 7, 6 },
        { 5, 5, 4, 3 }, { 6, 6, 5, 5 }, { 7, 6, 6, 5 }, { 7, 7, 6, 6 }, { 8, 7, 7, 6 }, { 8, 8, 7, 7 },
        { 7, 6, 6, 5 }, { 7, 7, 6, 5 }, { 8, 7, 7, 6 }, { 8, 7, 7, 6 }, { 9, 8, 7, 7 },
        { 7, 6, 6, 5 }, { 8, 7, 6, 6 }, { 8, 7, 7, 6 }, { 8, 8, 7, 6 }, { 9, 8, 8, 7 },
    };

    public static int RmQrCountBits(char mode, int versionIndex) => RmQrCountBitsTable[versionIndex, mode switch
    {
        'N' => 0,
        'A' => 1,
        'B' => 2,
        'K' => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    }];

    private static int RmQrModeIndicator(char mode) => mode switch
    {
        'N' => 0b001,
        'A' => 0b010,
        'B' => 0b011,
        'K' => 0b100,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static string RmQrBits(int versionIndex, IEnumerable<Run> runs)
    {
        var bits = new StringBuilder();
        foreach (var run in runs)
        {
            if (run.Mode == 'E')
            {
                // ECI: 111 and the assignment number, one byte for the assignments below 128.
                Append(bits, 0b111, 3);
                Append(bits, int.Parse(run.Text), 8);
                continue;
            }
            Append(bits, RmQrModeIndicator(run.Header), 3);
            Append(bits, run.Units, RmQrCountBits(run.Header, versionIndex));
            AppendPayload(bits, run);
        }
        return bits.ToString();
    }

    public static byte[] RmQrStream(int versionIndex, int dataCodewords, IEnumerable<Run> runs)
        => Finish(RmQrBits(versionIndex, runs), terminatorLength: 3, dataCodewords * 8, dataCodewords);

    // ---- shared -------------------------------------------------------------------

    private static void Append(StringBuilder bits, int value, int width)
    {
        if (value < 0 || value >= 1 << width)
            throw new ArgumentOutOfRangeException(nameof(value), $"{value} does not fit {width} bits.");
        for (var bit = width - 1; bit >= 0; bit--)
            bits.Append(((value >> bit) & 1) == 1 ? '1' : '0');
    }

    private static void AppendPayload(StringBuilder bits, Run run)
    {
        var text = run.Text;
        switch (run.Mode)
        {
            case 'N':
                for (var i = 0; i < text.Length; i += 3)
                {
                    var group = text.Substring(i, Math.Min(3, text.Length - i));
                    Append(bits, int.Parse(group), group.Length switch { 3 => 10, 2 => 7, _ => 4 });
                }
                break;
            case 'A':
                for (var i = 0; i < text.Length; i += 2)
                {
                    if (i + 1 < text.Length)
                        Append(bits, AlphanumericAlphabet.IndexOf(text[i]) * 45 + AlphanumericAlphabet.IndexOf(text[i + 1]), 11);
                    else
                        Append(bits, AlphanumericAlphabet.IndexOf(text[i]), 6);
                }
                break;
            case 'B':
                foreach (var c in text)
                    Append(bits, checked((byte)c), 8);
                break;
            case 'U':
                foreach (var b in Encoding.UTF8.GetBytes(text))
                    Append(bits, b, 8);
                break;
            case 'K':
                foreach (var c in text)
                    Append(bits, Value13(c), 13);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(run));
        }
    }

    /// <summary>
    /// Terminator (shortened at capacity), zero bits to the byte boundary, then 0xEC / 0x11 pad codewords over the whole bytes the capacity holds; a capacity that ends on half a byte leaves its last 4-bit codeword zero.
    /// </summary>
    private static byte[] Finish(string data, int terminatorLength, int capacityBits, int dataCodewords)
    {
        if (data.Length > capacityBits)
            throw new ArgumentException($"{data.Length} bits exceed the capacity of {capacityBits}.", nameof(data));

        var bits = new StringBuilder(data);
        bits.Append('0', Math.Min(terminatorLength, capacityBits - bits.Length));
        bits.Append('0', Math.Min((8 - bits.Length % 8) % 8, dataCodewords * 8 - bits.Length));
        var pad = 0;
        while (bits.Length + 8 <= capacityBits / 8 * 8)
            Append(bits, pad++ % 2 == 0 ? 0xEC : 0x11, 8);
        bits.Append('0', dataCodewords * 8 - bits.Length);

        var bytes = new byte[dataCodewords];
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i] == '1')
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        }
        return bytes;
    }
}
