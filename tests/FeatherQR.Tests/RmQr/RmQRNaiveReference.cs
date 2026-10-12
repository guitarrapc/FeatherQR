namespace FeatherQR.Tests;

/// <summary>
/// Independent, deliberately naive rMQR reference helpers for tests: a
/// function-pattern painter, the fixed data mask, the two-column zigzag walk, the
/// two format-information regions, and block deinterleaving. Written from the
/// symbol description in specs/rmqr-encoder.md, NOT from the production code, so
/// tests that pair them with <c>RmQRConstants</c> (and later the placer / decoder)
/// catch disagreements between two independent readings of ISO/IEC 23941.
/// </summary>
internal static class RmQRNaiveReference
{
    /// <summary>The 32 versions (height, width) in ISO index order (height-major).</summary>
    public static readonly (int Height, int Width)[] Versions =
    [
        (7, 43), (7, 59), (7, 77), (7, 99), (7, 139),
        (9, 43), (9, 59), (9, 77), (9, 99), (9, 139),
        (11, 27), (11, 43), (11, 59), (11, 77), (11, 99), (11, 139),
        (13, 27), (13, 43), (13, 59), (13, 77), (13, 99), (13, 139),
        (15, 43), (15, 59), (15, 77), (15, 99), (15, 139),
        (17, 43), (17, 59), (17, 77), (17, 99), (17, 139),
    ];

    /// <summary>Vertical timing / alignment column positions per width (0-based columns).</summary>
    public static int[] AlignmentColumns(int width) => width switch
    {
        27 => [],
        43 => [21],
        59 => [19, 39],
        77 => [25, 51],
        99 => [23, 49, 75],
        139 => [27, 55, 83, 111],
        _ => throw new ArgumentOutOfRangeException(nameof(width)),
    };

    /// <summary>
    /// Paints the function-module map (true = function module, i.e. not a data
    /// module): finder + separators, sub-finder, four edge timing patterns, the two
    /// corner patterns, vertical timing columns with 3×3 alignment patterns at both
    /// ends, and both 18-bit format regions.
    /// </summary>
    public static bool[] FunctionModuleMap(int height, int width)
    {
        var map = new bool[height * width];
        void Set(int r, int c) => map[r * width + c] = true;

        for (var r = 0; r < 7; r++)
            for (var c = 0; c < 7; c++)
                Set(r, c);
        for (var r = 0; r < Math.Min(8, height); r++)
            Set(r, 7);
        if (height > 7)
            for (var c = 0; c <= 7; c++)
                Set(7, c);

        for (var r = height - 5; r < height; r++)
            for (var c = width - 5; c < width; c++)
                Set(r, c);

        for (var c = 0; c < width; c++) { Set(0, c); Set(height - 1, c); }
        for (var r = 0; r < height; r++) { Set(r, 0); Set(r, width - 1); }
        Set(1, width - 2);
        Set(height - 2, 1);

        foreach (var c in AlignmentColumns(width))
        {
            for (var r = 0; r < height; r++)
                Set(r, c);
            foreach (var r in new[] { 0, 1, 2, height - 3, height - 2, height - 1 })
            {
                Set(r, c - 1);
                Set(r, c + 1);
            }
        }

        // Format information, finder side: rows 1-5 × cols 8-10, plus col 11 rows 1-3.
        for (var r = 1; r <= 5; r++)
            for (var c = 8; c <= 10; c++)
                Set(r, c);
        for (var r = 1; r <= 3; r++)
            Set(r, 11);

        // Format information, sub-finder side: rows h-6..h-2 × cols w-8..w-6, plus row h-6 cols w-5..w-3.
        for (var r = height - 6; r <= height - 2; r++)
            for (var c = width - 8; c <= width - 6; c++)
                Set(r, c);
        for (var c = width - 5; c <= width - 3; c++)
            Set(height - 6, c);

        return map;
    }

    /// <summary>The single rMQR data mask: dark when ((row / 2) + (col / 3)) is even.</summary>
    public static bool MaskBit(int row, int col) => ((row / 2) + (col / 3)) % 2 == 0;

    /// <summary>
    /// Reads the 18-bit format information copies from a matrix (bit i of the
    /// returned value = module listed at position i of the region: five rows × three
    /// columns column-major, then the three extra modules for bits 15-17).
    /// </summary>
    public static (int FinderSide, int SubFinderSide) ReadFormatRegions(ReadOnlySpan<byte> modules, int height, int width)
    {
        var left = 0;
        for (var c = 0; c < 3; c++)
            for (var r = 0; r < 5; r++)
                if (modules[(r + 1) * width + (c + 8)] != 0)
                    left |= 1 << (c * 5 + r);
        for (var k = 0; k < 3; k++)
            if (modules[(k + 1) * width + 11] != 0)
                left |= 1 << (15 + k);

        var right = 0;
        for (var c = 0; c < 3; c++)
            for (var r = 0; r < 5; r++)
                if (modules[(height - 6 + r) * width + (width - 8 + c)] != 0)
                    right |= 1 << (c * 5 + r);
        for (var k = 0; k < 3; k++)
            if (modules[(height - 6) * width + (width - 5 + k)] != 0)
                right |= 1 << (15 + k);

        return (left, right);
    }

    /// <summary>
    /// Naive BCH(18,6): data (6 bits) followed by the remainder of data·x^12 modulo
    /// the generator 0x1F25.
    /// </summary>
    public static int Bch18(int data)
    {
        var value = data << 12;
        for (var bit = 17; bit >= 12; bit--)
        {
            if ((value & (1 << bit)) != 0)
                value ^= 0x1F25 << (bit - 12);
        }
        return (data << 12) | value;
    }

    public const int FormatXorFinderSide = 0x1FAB2;
    public const int FormatXorSubFinderSide = 0x20A7B;

    /// <summary>
    /// Walks the two-column zigzag over the non-function modules (starting at the
    /// column pair left of the right-edge timing column, upward first, right column
    /// first) and returns the unmasked data bits in placement order, MSB-first packed
    /// into bytes (the interleaved codeword stream followed by remainder bits).
    /// </summary>
    public static byte[] ExtractInterleavedStream(ReadOnlySpan<byte> modules, int height, int width, out int bitCount)
    {
        var function = FunctionModuleMap(height, width);
        var bits = new List<bool>();
        var upward = true;
        for (var col = width - 2; col >= 1; col -= 2)
        {
            for (var step = 0; step < height; step++)
            {
                var row = upward ? height - 1 - step : step;
                foreach (var c in new[] { col, col - 1 })
                {
                    if (function[row * width + c])
                        continue;
                    var dark = modules[row * width + c] != 0;
                    bits.Add(dark ^ MaskBit(row, c));
                }
            }
            upward = !upward;
        }

        bitCount = bits.Count;
        var bytes = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            if (bits[i])
                bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
        }
        return bytes;
    }

    /// <summary>
    /// Undoes Standard-QR-style block interleaving of the data codewords: blocks
    /// have <paramref name="shortBlockCount"/> blocks of <paramref name="shortLength"/>
    /// codewords followed by the remaining blocks with one more codeword. Returns the
    /// data codewords of block 0.
    /// </summary>
    public static byte[] DeinterleaveFirstBlock(ReadOnlySpan<byte> stream, int blockCount, int shortBlockCount, int shortLength)
        => DeinterleaveData(stream, blockCount, shortBlockCount, shortLength).AsSpan(0, shortLength).ToArray();

    /// <summary>
    /// Undoes Standard-QR-style block interleaving of the data codewords and returns
    /// ALL data codewords concatenated in block order (block 0 first).
    /// </summary>
    public static byte[] DeinterleaveData(ReadOnlySpan<byte> stream, int blockCount, int shortBlockCount, int shortLength)
    {
        var lengths = new int[blockCount];
        var total = 0;
        for (var b = 0; b < blockCount; b++)
        {
            lengths[b] = b < shortBlockCount ? shortLength : shortLength + 1;
            total += lengths[b];
        }

        var blocks = new byte[blockCount][];
        for (var b = 0; b < blockCount; b++)
            blocks[b] = new byte[lengths[b]];

        var k = 0;
        for (var i = 0; i < shortLength + 1; i++)
        {
            for (var b = 0; b < blockCount; b++)
            {
                if (i >= lengths[b])
                    continue;
                blocks[b][i] = stream[k++];
            }
        }

        var result = new byte[total];
        var offset = 0;
        foreach (var block in blocks)
        {
            block.CopyTo(result, offset);
            offset += block.Length;
        }
        return result;
    }

    /// <summary>
    /// Independent naive rMQR data-codeword reference for a multi-segment stream:
    /// one optional ECI prefix, then per run a mode indicator, its count indicator
    /// and the payload, then the shared terminator / padding tail. Built as a bit
    /// string on purpose, exactly like the single-segment reference.
    /// </summary>
    public static byte[] NaiveSegmentedDataCodewords(int dataCodewordCount, (string Mode, string Data, int CountIndicatorBits)[] segments, bool utf8, EciMode eciMode = EciMode.Default)
    {
        var bits = new System.Text.StringBuilder();
        void Append(int value, int count)
        {
            for (var b = count - 1; b >= 0; b--)
                bits.Append(((value >> b) & 1) == 1 ? '1' : '0');
        }

        if (eciMode != EciMode.Default)
        {
            Append(0b111, 3);
            Append((int)eciMode, 8);
        }

        foreach (var (mode, text, countIndicatorBits) in segments)
        {
            switch (mode)
            {
                case "Numeric":
                    Append(0b001, 3);
                    Append(text.Length, countIndicatorBits);
                    for (var i = 0; i < text.Length; i += 3)
                    {
                        var take = Math.Min(3, text.Length - i);
                        Append(int.Parse(text.Substring(i, take)), take == 3 ? 10 : take == 2 ? 7 : 4);
                    }
                    break;
                case "Alphanumeric":
                    Append(0b010, 3);
                    Append(text.Length, countIndicatorBits);
                    const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
                    for (var i = 0; i < text.Length; i += 2)
                    {
                        if (i + 1 < text.Length)
                            Append(alphabet.IndexOf(text[i]) * 45 + alphabet.IndexOf(text[i + 1]), 11);
                        else
                            Append(alphabet.IndexOf(text[i]), 6);
                    }
                    break;
                default:
                    Append(0b011, 3);
                    var bytes = utf8 ? System.Text.Encoding.UTF8.GetBytes(text) : System.Text.Encoding.Latin1.GetBytes(text);
                    Append(bytes.Length, countIndicatorBits);
                    foreach (var b in bytes)
                        Append(b, 8);
                    break;
            }
        }

        var capacity = dataCodewordCount * 8;
        if (bits.Length > capacity)
            throw new InvalidOperationException($"payload does not fit: {bits.Length} > {capacity} bits");
        for (var t = 0; t < 3 && bits.Length < capacity; t++)
            bits.Append('0');
        while (bits.Length % 8 != 0)
            bits.Append('0');

        var result = new byte[dataCodewordCount];
        for (var i = 0; i < bits.Length / 8; i++)
            result[i] = Convert.ToByte(bits.ToString(i * 8, 8), 2);
        for (var i = bits.Length / 8; i < dataCodewordCount; i++)
            result[i] = (i - bits.Length / 8) % 2 == 0 ? (byte)0xEC : (byte)0x11;
        return result;
    }

    /// <summary>
    /// Independent naive rMQR data-codeword reference: mode indicator (3 bits),
    /// count indicator, payload bits, terminator (up to 3 zero bits, shortened at
    /// capacity), zero bits to the byte boundary, then alternating 0xEC / 0x11 pad
    /// codewords up to the data codeword count. Built as a bit string on purpose.
    /// </summary>
    public static byte[] NaiveDataCodewords(string text, int dataCodewordCount, int modeIndicator, int countIndicatorBits, string mode, bool utf8, EciMode eciMode = EciMode.Default)
    {
        var bits = new System.Text.StringBuilder();
        void Append(int value, int count)
        {
            for (var b = count - 1; b >= 0; b--)
                bits.Append(((value >> b) & 1) == 1 ? '1' : '0');
        }

        if (eciMode != EciMode.Default)
        {
            Append(0b111, 3);
            Append((int)eciMode, 8);
        }
        Append(modeIndicator, 3);
        switch (mode)
        {
            case "Numeric":
                Append(text.Length, countIndicatorBits);
                for (var i = 0; i < text.Length; i += 3)
                {
                    var take = Math.Min(3, text.Length - i);
                    var value = int.Parse(text.Substring(i, take));
                    Append(value, take == 3 ? 10 : take == 2 ? 7 : 4);
                }
                break;
            case "Alphanumeric":
                Append(text.Length, countIndicatorBits);
                const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
                for (var i = 0; i < text.Length; i += 2)
                {
                    if (i + 1 < text.Length)
                        Append(alphabet.IndexOf(text[i]) * 45 + alphabet.IndexOf(text[i + 1]), 11);
                    else
                        Append(alphabet.IndexOf(text[i]), 6);
                }
                break;
            default:
                var bytes = utf8 ? System.Text.Encoding.UTF8.GetBytes(text) : System.Text.Encoding.Latin1.GetBytes(text);
                Append(bytes.Length, countIndicatorBits);
                foreach (var b in bytes)
                    Append(b, 8);
                break;
        }

        var capacity = dataCodewordCount * 8;
        if (bits.Length > capacity)
            throw new InvalidOperationException($"payload does not fit: {bits.Length} > {capacity} bits");
        for (var t = 0; t < 3 && bits.Length < capacity; t++)
            bits.Append('0');
        while (bits.Length % 8 != 0)
            bits.Append('0');

        var result = new byte[dataCodewordCount];
        for (var i = 0; i < bits.Length / 8; i++)
            result[i] = Convert.ToByte(bits.ToString(i * 8, 8), 2);
        for (var i = bits.Length / 8; i < dataCodewordCount; i++)
            result[i] = (i - bits.Length / 8) % 2 == 0 ? (byte)0xEC : (byte)0x11;
        return result;
    }

}
