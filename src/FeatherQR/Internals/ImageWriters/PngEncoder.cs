using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET5_0_OR_GREATER
using System.Numerics;
#endif

namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// The state of one PNG: the image's geometry, the symbol counts and codes, and the three scanlines the matcher reads.
/// One instance a thread is kept, so a call allocates nothing beyond its output once warm.
/// </summary>
internal sealed class PngEncoder
{
    [ThreadStatic]
    private static PngEncoder? cached;

    // Runs and copies shorter than these cost more than their literals: on a 1-bit image a literal is one of few byte values and its code is short.
    private const int MinRun = 4;
    private const int MinRowCopy = 8;
    // Each scanline's slot runs this far past it, so packing and matching read and write whole words up to its end.
    private const int Slack = 8;

    private readonly long[] _literalCounts = new long[Deflate.LiteralLengthSymbols];
    private readonly long[] _distanceCounts = new long[Deflate.DistanceSymbols];
    private readonly long[] _codeLengthCounts = new long[Deflate.CodeLengthSymbols];
    private readonly byte[] _literalLengths = new byte[Deflate.LiteralLengthSymbols];
    private readonly byte[] _distanceLengths = new byte[Deflate.DistanceSymbols];
    private readonly byte[] _codeLengthLengths = new byte[Deflate.CodeLengthSymbols];
    private readonly uint[] _literalCodes = new uint[Deflate.LiteralLengthSymbols];
    private readonly uint[] _distanceCodes = new uint[Deflate.DistanceSymbols];
    private readonly uint[] _codeLengthCodes = new uint[Deflate.CodeLengthSymbols];
    private readonly byte[] _lengths = new byte[Deflate.LiteralLengthSymbols + Deflate.DistanceSymbols];
    // A run-length coded code length: the code-length symbol in the low byte, its extra bits above.
    private readonly int[] _lengthRuns = new int[Deflate.LiteralLengthSymbols + Deflate.DistanceSymbols];

    // Set by Measure and read by Write, which must write the file Measure sized.
    private int _pixels;
    private int _quietZone;
    private int _coreWidth;
    private int _coreHeight;
    private int _moduleRows;
    private int _widthPixels;
    private int _stride;
    private bool _rowCopies;
    private int _literalSymbols;
    private int _distanceSymbols;
    private int _codeLengthSymbols;
    private int _lengthRunCount;
    private int _deflateLength;
    private SymbolColor _dark;
    private SymbolColor _light;

    // The scanlines of module rows m - 1, m and m + 1, at slot m % 3, rented for each call.
    private byte[] _rows = [];
    private readonly int[] _slotRow = new int[3];

    // Eight modules' light-1 bits at _expansionPixels pixels a module, for each byte of the core (dark 1, MSB first).
    private readonly ulong[] _expansion = new ulong[256];
    private int _expansionPixels;

    // Bit p of _starts marks a position of the scanline where a run or a copy may start. The others are the equal bytes they come from.
    private ulong[] _starts = [];
    private ulong[] _equalBefore = [];
    private ulong[] _equalAbove = [];

    // The counting pass's symbols, each with how many times it repeats in a row, so the writing pass replays them instead of matching
    // again. A literal is its byte. A match is 1 << 31, then length - 3 from bit 16 and distance - 1 below. A file with more entries than
    // MaxRecorded is matched again instead, which bounds the memory.
    private const int MaxRecorded = 1 << 20;
    private ulong[] _recorded = [];
    private int _recordedCount;
    private bool _recordComplete;
    private uint _adler;

    public static PngEncoder Take()
    {
        var encoder = cached ?? new PngEncoder();
        cached = null;
        return encoder;
    }

    public static void Return(PngEncoder encoder)
    {
        if (encoder._rows.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(encoder._rows);
            encoder._rows = [];
        }
        if (encoder._recorded.Length != 0)
        {
            ArrayPool<ulong>.Shared.Return(encoder._recorded);
            encoder._recorded = [];
        }
        cached = encoder;
    }

    private bool Greyscale => _dark == new SymbolColor(0, 0, 0) && _light == new SymbolColor(255, 255, 255);

    // The dark colour is painted over the light one, so it can have an alpha only when the light colour has one.
    private bool Transparent => _light.A != 255;

    /// <summary>Counts the image's symbols, builds the codes from them, and returns the length of the file.</summary>
    public int Measure<TView>(in TView view, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        _pixels = options.ModulePixelSize;
        _dark = options.DarkColor.Over(options.LightColor);
        _light = options.LightColor;
        _quietZone = (view.Width - view.CoreWidth) / 2;
        _coreWidth = view.CoreWidth;
        _coreHeight = view.CoreHeight;
        _moduleRows = view.Height;
        _widthPixels = view.Width * _pixels;
        // In a long, since a width within 7 of int.MaxValue passes the side check.
        _stride = (int)((_widthPixels + 7L) / 8) + 1;
        _rowCopies = _stride <= Deflate.MaxDistance;

        // A match covers at most 258 bytes and costs at least two bits, a length code and a distance code. A literal costs at least a bit for one byte.
        // That bounds the output from below before anything is counted.
        var rawLength = (long)_stride * view.Height * _pixels;
        ImageSize.ThrowIfLongerThanAnArray(rawLength * 2 / (Deflate.MaxMatch * 8), in options);

        Array.Clear(_literalCounts, 0, _literalCounts.Length);
        Array.Clear(_distanceCounts, 0, _distanceCounts.Length);
        var nowhere = new DeflateBitWriter(default);
        RentRows();
        _recordedCount = 0;
        _recordComplete = true;
        _adler = Tokenize(in view, ref nowhere, write: false);
        _literalCounts[Deflate.EndOfBlock] = 1;

        HuffmanCodeLengths.Build(_literalCounts, 15, _literalLengths);
        HuffmanCodeLengths.Build(_distanceCounts, 15, _distanceLengths);
        Deflate.BuildCodes(_literalLengths, _literalCodes);
        Deflate.BuildCodes(_distanceLengths, _distanceCodes);

        _literalSymbols = Deflate.LiteralLengthSymbols;
        while (_literalSymbols > 257 && _literalLengths[_literalSymbols - 1] == 0)
            _literalSymbols--;
        _distanceSymbols = Deflate.DistanceSymbols;
        while (_distanceSymbols > 1 && _distanceLengths[_distanceSymbols - 1] == 0)
            _distanceSymbols--;
        _literalLengths.AsSpan(0, _literalSymbols).CopyTo(_lengths);
        _distanceLengths.AsSpan(0, _distanceSymbols).CopyTo(_lengths.AsSpan(_literalSymbols));
        _lengthRunCount = RunLengths(_lengths.AsSpan(0, _literalSymbols + _distanceSymbols));

        Array.Clear(_codeLengthCounts, 0, _codeLengthCounts.Length);
        for (var k = 0; k < _lengthRunCount; k++)
            _codeLengthCounts[_lengthRuns[k] & 0xFF]++;
        HuffmanCodeLengths.Build(_codeLengthCounts, 7, _codeLengthLengths);
        Deflate.BuildCodes(_codeLengthLengths, _codeLengthCodes);
        _codeLengthSymbols = Deflate.CodeLengthSymbols;
        while (_codeLengthSymbols > 4 && _codeLengthLengths[Deflate.CodeLengthOrder[_codeLengthSymbols - 1]] == 0)
            _codeLengthSymbols--;

        // The block header, then every symbol at its code length and extra bits.
        long bits = 3 + 5 + 5 + 4 + 3 * _codeLengthSymbols;
        for (var k = 0; k < _lengthRunCount; k++)
        {
            var symbol = _lengthRuns[k] & 0xFF;
            bits += _codeLengthLengths[symbol] + (symbol == 16 ? 2 : symbol == 17 ? 3 : symbol == 18 ? 7 : 0);
        }
        for (var symbol = 0; symbol <= Deflate.EndOfBlock; symbol++)
            bits += _literalCounts[symbol] * _literalLengths[symbol];
        for (var code = 0; code < 29; code++)
            bits += _literalCounts[257 + code] * (_literalLengths[257 + code] + Deflate.LengthExtraBits[code]);
        for (var code = 0; code < Deflate.DistanceSymbols; code++)
            bits += _distanceCounts[code] * (_distanceLengths[code] + Deflate.DistanceExtraBits[code]);

        var deflateLength = (bits + 7) / 8;
        // Signature, IHDR, PLTE and tRNS when used, IDAT with the zlib header and Adler-32, IEND.
        var length = 8 + 25 + (Greyscale ? 0 : 12 + 6) + (Transparent ? 12 + 2 : 0) + 12 + 2 + deflateLength + 4 + 12;
        ImageSize.ThrowIfLongerThanAnArray(length, in options);
        _deflateLength = (int)deflateLength;
        return (int)length;
    }

    /// <summary>Writes the file <see cref="Measure"/> sized into a destination of exactly that length.</summary>
    public void Write<TView>(in TView view, Span<byte> png)
        where TView : struct, IModuleMatrixView
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(png);
        var position = 8;

        var header = BeginChunk(png, ref position, "IHDR"u8, 13);
        BinaryPrimitives.WriteInt32BigEndian(png.Slice(position), _widthPixels);
        BinaryPrimitives.WriteInt32BigEndian(png.Slice(position + 4), _moduleRows * _pixels);
        png[position + 8] = 1;
        png[position + 9] = (byte)(Greyscale ? 0 : 3);
        png[position + 10] = 0;
        png[position + 11] = 0;
        png[position + 12] = 0;
        position += 13;
        EndChunk(png, ref position, header);

        if (!Greyscale)
        {
            var palette = BeginChunk(png, ref position, "PLTE"u8, 6);
            png[position++] = _dark.R;
            png[position++] = _dark.G;
            png[position++] = _dark.B;
            png[position++] = _light.R;
            png[position++] = _light.G;
            png[position++] = _light.B;
            EndChunk(png, ref position, palette);
        }

        if (Transparent)
        {
            var alphas = BeginChunk(png, ref position, "tRNS"u8, 2);
            png[position++] = _dark.A;
            png[position++] = _light.A;
            EndChunk(png, ref position, alphas);
        }

        var data = BeginChunk(png, ref position, "IDAT"u8, 2 + _deflateLength + 4);
        // Deflate with a 32 KB window, no preset dictionary, and a check that makes the two bytes a multiple of 31.
        png[position++] = 0x78;
        png[position++] = 0x9C;
        var block = png.Slice(position, _deflateLength);
        var w = new DeflateBitWriter(block);
        WriteBlockHeader(ref w);
        var adler = _adler;
        if (_recordComplete)
            Replay(ref w);
        else
            adler = Tokenize(in view, ref w, write: true);
        w.Put(_literalCodes[Deflate.EndOfBlock]);
        if (w.Flush() != _deflateLength)
            throw new InvalidOperationException("The PNG's writing pass diverged from its counting pass; this is a bug in PngEncoder.");
        position += _deflateLength;
        BinaryPrimitives.WriteUInt32BigEndian(png.Slice(position), adler);
        position += 4;
        EndChunk(png, ref position, data);

        var end = BeginChunk(png, ref position, "IEND"u8, 0);
        EndChunk(png, ref position, end);
    }

    private static int BeginChunk(Span<byte> png, ref int position, ReadOnlySpan<byte> type, int length)
    {
        BinaryPrimitives.WriteInt32BigEndian(png.Slice(position), length);
        type.CopyTo(png.Slice(position + 4));
        var start = position + 4;
        position += 8;
        return start;
    }

    // The CRC covers the chunk type and data.
    private static void EndChunk(Span<byte> png, ref int position, int start)
    {
        BinaryPrimitives.WriteUInt32BigEndian(png.Slice(position), PngCrc.Compute(png.Slice(start, position - start)));
        position += 4;
    }

    private void WriteBlockHeader(ref DeflateBitWriter w)
    {
        // The last block, with dynamic codes.
        w.Put(1, 1);
        w.Put(2, 2);
        w.Put((uint)(_literalSymbols - 257), 5);
        w.Put((uint)(_distanceSymbols - 1), 5);
        w.Put((uint)(_codeLengthSymbols - 4), 4);
        for (var k = 0; k < _codeLengthSymbols; k++)
            w.Put(_codeLengthLengths[Deflate.CodeLengthOrder[k]], 3);
        for (var k = 0; k < _lengthRunCount; k++)
        {
            var symbol = _lengthRuns[k] & 0xFF;
            var extra = (uint)(_lengthRuns[k] >> 8);
            w.Put(_codeLengthCodes[symbol]);
            if (symbol == 16)
                w.Put(extra, 2);
            else if (symbol == 17)
                w.Put(extra, 3);
            else if (symbol == 18)
                w.Put(extra, 7);
        }
    }

    /// <summary>
    /// The code lengths in deflate's run-length form: 16 repeats the previous length 3 to 6 times, 17 writes 3 to 10 zeros, 18 writes 11 to 138.
    /// </summary>
    private int RunLengths(ReadOnlySpan<byte> lengths)
    {
        var count = 0;
        var i = 0;
        while (i < lengths.Length)
        {
            var length = lengths[i];
            var run = 1;
            while (i + run < lengths.Length && lengths[i + run] == length)
                run++;
            i += run;
            if (length == 0)
            {
                while (run >= 11)
                {
                    var zeros = Math.Min(run, 138);
                    _lengthRuns[count++] = 18 | (zeros - 11) << 8;
                    run -= zeros;
                }
                if (run >= 3)
                {
                    _lengthRuns[count++] = 17 | (run - 3) << 8;
                    run = 0;
                }
            }
            else
            {
                _lengthRuns[count++] = length;
                run--;
                while (run >= 3)
                {
                    var repeats = Math.Min(run, 6);
                    _lengthRuns[count++] = 16 | (repeats - 3) << 8;
                    run -= repeats;
                }
            }
            while (run-- > 0)
                _lengthRuns[count++] = length;
        }
        return count;
    }

    // ---- the matcher ----------------------------------------------------------------------

    private void RentRows()
    {
        var slots = _rowCopies ? 3 : 1;
        var length = slots * (_stride + Slack);
        if (_rows.Length < length)
        {
            if (_rows.Length != 0)
                ArrayPool<byte>.Shared.Return(_rows);
            _rows = ArrayPool<byte>.Shared.Rent(length);
        }
        if (_rowCopies)
        {
            var words = (_stride + 63) >> 6;
            if (_starts.Length <= words)
            {
                _starts = new ulong[words + 1];
                _equalBefore = new ulong[words + 1];
                _equalAbove = new ulong[words + 1];
            }
        }
    }

    /// <summary>
    /// Emits the image's symbols, counting them or writing them, and returns the Adler-32 of the scanlines.
    /// </summary>
    private uint Tokenize<TView>(in TView view, ref DeflateBitWriter w, bool write)
        where TView : struct, IModuleMatrixView
    {
        _slotRow[0] = _slotRow[1] = _slotRow[2] = -1;
        var stride = _stride;
        var repeatLength = (long)(_pixels - 1) * stride;
        var adler = new Adler32();
        var carry = 0;
        // The record's array and position are locals between the calls that read them from the fields.
        var recorded = _recorded;
        var count = _recordedCount;

        for (var m = 0; m < _moduleRows; m++)
        {
            var row = Row(in view, m);
            adler.AddRepeated(row, _pixels);

            if (!_rowCopies)
            {
                // The scanline above is out of a copy's reach, so every scanline is searched for runs alone.
                _recordedCount = count;
                for (var repeat = 0; repeat < _pixels; repeat++)
                    TokenizeRuns(row, ref w, write);
                recorded = _recorded;
                count = _recordedCount;
                continue;
            }

            var above = m > 0 ? Row(in view, m - 1) : default;
            MarkStarts(row, above, m > 0);
            ref var rowStart = ref MemoryMarshal.GetReference(row);
            ref var aboveStart = ref MemoryMarshal.GetReference(above);
            var i = carry;
            while (i < stride)
            {
                // Every position before the next mark is a literal.
                var next = NextStart(i, stride);
                if (next > i)
                {
                    if (write)
                    {
                        for (; i < next; i++)
                            w.Put(_literalCodes[row[i]]);
                    }
                    else if (count + (next - i) <= recorded.Length)
                    {
                        // Unchecked: the record's room is checked just above, and a byte indexes inside the 286 counts.
                        ref var counts = ref MemoryMarshal.GetReference(_literalCounts.AsSpan());
                        ref var entries = ref MemoryMarshal.GetReference(recorded.AsSpan());
                        for (; i < next; i++)
                        {
                            var value = Unsafe.Add(ref rowStart, i);
                            Unsafe.Add(ref counts, value)++;
                            // Literals rarely repeat in a row, so they are appended without looking at the entry before.
                            Unsafe.Add(ref entries, count++) = (ulong)value << 32 | 1;
                        }
                    }
                    else
                    {
                        _recordedCount = count;
                        for (; i < next; i++)
                            Literal(ref w, write, row[i]);
                        recorded = _recorded;
                        count = _recordedCount;
                    }
                    if (i >= stride)
                        break;
                }

                var max = Math.Min(Deflate.MaxMatch, stride - i);

                // A run repeats the byte before it, which is the scanline above's last byte at the start.
                var run = 0;
                if (i > 0)
                {
                    if (MayRun(row, i))
                        run = Prefix(ref Unsafe.Add(ref rowStart, i), ref Unsafe.Add(ref rowStart, i - 1), max);
                }
                else if (m > 0 && row[0] == above[stride - 1])
                {
                    run = 1 + CommonPrefix(row.Slice(1, max - 1), row.Slice(0, max - 1));
                }

                var length = 0;
                var distance = 0;
                // A short match that ends the scanline is taken: the alternative is literals up to the end anyway.
                if (run >= Deflate.MinMatch && (run >= MinRun || run == stride - i))
                {
                    length = run;
                    distance = 1;
                }

                if (m > 0 && MayCopy(row, above, i))
                {
                    long copy = Prefix(ref Unsafe.Add(ref rowStart, i), ref Unsafe.Add(ref aboveStart, i), max);
                    if (copy == stride - i)
                        copy = Extend(in view, m, copy + repeatLength, row);
                    if (copy > length && copy >= Deflate.MinMatch && (copy >= MinRowCopy || i + copy > stride))
                    {
                        length = (int)copy;
                        distance = stride;
                    }
                }

                _recordedCount = count;
                if (distance != 0)
                {
                    Match(ref w, write, length, distance);
                    i += length;
                }
                else
                {
                    Literal(ref w, write, row[i]);
                    i++;
                }
                recorded = _recorded;
                count = _recordedCount;
            }

            // The repeats, as copies of the scanline above, the last one running on into the next row's first scanline.
            _recordedCount = count;
            long offset = i - stride;
            while (offset < repeatLength)
            {
                var length = Extend(in view, m, repeatLength - offset, row);
                if (length < Deflate.MinMatch)
                {
                    Literal(ref w, write, row[(int)(offset % stride)]);
                    offset++;
                    continue;
                }
                Match(ref w, write, length, stride);
                offset += length;
            }
            recorded = _recorded;
            count = _recordedCount;
            carry = (int)(offset - repeatLength);
        }
        _recordedCount = count;
        return adler.Value;
    }

    /// <summary>
    /// A copy one scanline back that has reached the end of row <paramref name="m"/>'s scanlines continues into the next row's first scanline
    /// while that matches this row. At most 258 bytes.
    /// </summary>
    private int Extend<TView>(in TView view, int m, long length, ReadOnlySpan<byte> row)
        where TView : struct, IModuleMatrixView
    {
        if (length >= Deflate.MaxMatch)
            return Deflate.MaxMatch;
        if (m + 1 >= _moduleRows)
            return (int)length;
        var reach = Math.Min(Deflate.MaxMatch - (int)length, _stride);
        var next = Row(in view, m + 1);
        return (int)length + CommonPrefix(next.Slice(0, reach), row.Slice(0, reach));
    }

    /// <summary>One scanline searched for runs alone, for images too wide for a copy of the scanline above.</summary>
    private void TokenizeRuns(scoped ReadOnlySpan<byte> row, ref DeflateBitWriter w, bool write)
    {
        Literal(ref w, write, row[0]);
        var i = 1;
        while (i < row.Length)
        {
            var max = Math.Min(Deflate.MaxMatch, row.Length - i);
            var run = MayRun(row, i) ? CommonPrefix(row.Slice(i, max), row.Slice(i - 1, max)) : 0;
            if (run >= Deflate.MinMatch && (run >= MinRun || run == row.Length - i))
            {
                Match(ref w, write, run, 1);
                i += run;
            }
            else
            {
                Literal(ref w, write, row[i]);
                i++;
            }
        }
    }

    /// <summary>
    /// Whether a run at <paramref name="i"/> can be long enough to take: the shortest one taken there repeats its byte <see cref="MinRun"/> times,
    /// unless it would end the scanline, so one compare of that many bytes rules most positions out. Exact, never a refusal of a run the full compare would take.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MayRun(ReadOnlySpan<byte> row, int i)
        => i + MinRun <= row.Length
            ? BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(i)) == BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(i - 1))
            : row[i] == row[i - 1];

    /// <summary>The same for a copy of the scanline above, whose shortest one taken is <see cref="MinRowCopy"/> bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MayCopy(ReadOnlySpan<byte> row, ReadOnlySpan<byte> above, int i)
        => i + MinRowCopy <= row.Length
            ? BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(i)) == BinaryPrimitives.ReadUInt64LittleEndian(above.Slice(i))
            : row[i] == above[i];

    /// <summary>How many leading bytes two spans share. They may overlap, as a run's do.</summary>
    private static int CommonPrefix(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
#if NET7_0_OR_GREATER
        return a.CommonPrefixLength(b);
#else
        var length = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i + 8 <= length && MemoryMarshal.Read<ulong>(a.Slice(i)) == MemoryMarshal.Read<ulong>(b.Slice(i)))
            i += 8;
        while (i < length && a[i] == b[i])
            i++;
        return i;
#endif
    }

    /// <summary>
    /// How many leading bytes from two places in the scanline slots match, at most <paramref name="max"/>, eight a step.
    /// The last step reads up to seven bytes past <paramref name="max"/>, which the slots' slack holds.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Prefix(ref byte a, ref byte b, int max)
    {
        var n = 0;
        while (n < max)
        {
            var x = LittleEndian(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, n))) ^ LittleEndian(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, n)));
            if (x != 0)
                return Math.Min(max, n + (TrailingZeros(x) >> 3));
            n += 8;
        }
        return max;
    }

    // ---- where a run or a copy may start ------------------------------------------------------

    /// <summary>
    /// Sets bit p of <see cref="_starts"/> where the search may take a run or a copy at p: four bytes from p equal to the four before them
    /// (<see cref="MayRun"/>), or eight equal to the scanline above (<see cref="MayCopy"/>). Position 0 and the last seven, where those compare
    /// fewer bytes, are always set, so the search decides them itself.
    /// </summary>
    /// <remarks>
    /// The equal bytes are found eight positions a compare. The bytes one back come from the word before, shifted, so both masks of a word
    /// are built in a register. Bytes past the scanline are read from its slot's slack and are noise, but they reach only positions that are
    /// always set or cleared.
    /// </remarks>
    private void MarkStarts(ReadOnlySpan<byte> row, ReadOnlySpan<byte> above, bool hasAbove)
    {
        var stride = row.Length;
        var words = (stride + 63) >> 6;
        var before = _equalBefore.AsSpan(0, words + 1);
        var equal = _equalAbove.AsSpan(0, words + 1);
        var starts = _starts.AsSpan(0, words + 1);
        ref var r = ref MemoryMarshal.GetReference(row);
        ref var a = ref MemoryMarshal.GetReference(above);
        ulong previous = 0;
        for (var k = 0; k < words; k++)
        {
            ulong backBits = 0, aboveBits = 0;
            for (var j = 0; j < 64; j += 8)
            {
                var q = (k << 6) + j;
                if (q >= stride)
                    break;
                var current = LittleEndian(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, q)));
                var back = (current << 8) | (previous >> 56);
                previous = current;
                backBits |= ZeroBytes(current ^ back) << j;
                if (hasAbove)
                    aboveBits |= ZeroBytes(current ^ LittleEndian(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, q)))) << j;
            }
            before[k] = backBits;
            equal[k] = aboveBits;
        }
        before[words] = 0;
        equal[words] = 0;

        // A run needs an equal byte at p, p + 1, p + 2 and p + 3, a copy eight from p.
        AndShifted(before, 1);
        AndShifted(before, 2);
        if (hasAbove)
        {
            AndShifted(equal, 1);
            AndShifted(equal, 2);
            AndShifted(equal, 4);
        }
        for (var k = 0; k < words; k++)
            starts[k] = before[k] | equal[k];
        starts[0] |= 1;
        for (var p = Math.Max(0, stride - 7); p < stride; p++)
            starts[p >> 6] |= 1UL << (p & 63);
        if ((stride & 63) != 0)
            starts[words - 1] &= ~0UL >> (64 - (stride & 63));
        starts[words] = 0;
    }

    /// <summary>Bit k set where byte k of <paramref name="x"/> is zero. Exact: no borrow crosses a byte.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ZeroBytes(ulong x)
    {
        var y = ~(((x & 0x7F7F7F7F7F7F7F7FUL) + 0x7F7F7F7F7F7F7F7FUL) | x | 0x7F7F7F7F7F7F7F7FUL);
        // Byte k's top bit to bit k.
        return ((y >> 7) * 0x0102040810204080UL) >> 56;
    }

    /// <summary>Bit p becomes bit p and bit p + s, for s from 1 to 63, across words. The last word stays zero.</summary>
    private static void AndShifted(Span<ulong> bits, int s)
    {
        for (var k = 0; k + 1 < bits.Length; k++)
            bits[k] &= (bits[k] >> s) | (bits[k + 1] << (64 - s));
    }

    /// <summary>The first marked position from <paramref name="i"/>, or the scanline's end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int NextStart(int i, int stride)
    {
        var starts = _starts;
        var k = i >> 6;
        var word = starts[k] & (~0UL << (i & 63));
        while (word == 0)
        {
            if (++k >= starts.Length)
                return stride;
            word = starts[k];
        }
        return Math.Min(stride, (k << 6) + TrailingZeros(word));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong LittleEndian(ulong value) => BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int TrailingZeros(ulong value)
    {
#if NET5_0_OR_GREATER
        return BitOperations.TrailingZeroCount(value);
#else
        return value == 0 ? 64 : 63 - PackedRuns.LeadingZeros(value & (0 - value));
#endif
    }

    // ---- symbols ------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Literal(ref DeflateBitWriter w, bool write, byte value)
    {
        if (write)
        {
            w.Put(_literalCodes[value]);
            return;
        }
        _literalCounts[value]++;
        // Literals rarely repeat in a row, so they are appended without looking at the entry before.
        var count = _recordedCount;
        if (count < _recorded.Length)
        {
            _recorded[count] = (ulong)value << 32 | 1;
            _recordedCount = count + 1;
            return;
        }
        Record(value);
    }

    private void Match(ref DeflateBitWriter w, bool write, int length, int distance)
    {
        var lengthCode = Deflate.LengthCode(length);
        var distanceCode = Deflate.DistanceCode(distance);
        if (!write)
        {
            _literalCounts[257 + lengthCode]++;
            _distanceCounts[distanceCode]++;
            Record(0x8000_0000u | (uint)(length - Deflate.MinMatch) << 16 | (uint)(distance - 1));
            return;
        }
        w.Put(_literalCodes[257 + lengthCode]);
        w.Put((uint)(length - Deflate.LengthBase(lengthCode)), Deflate.LengthExtraBits[lengthCode]);
        w.Put(_distanceCodes[distanceCode]);
        w.Put((uint)(distance - Deflate.DistanceBase(distanceCode)), Deflate.DistanceExtraBits[distanceCode]);
    }

    /// <summary>Appends a symbol of the counting pass, or adds one to the last entry when it is the same symbol.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Record(uint symbol)
    {
        if (!_recordComplete)
            return;
        var count = _recordedCount;
        if (count > 0 && (uint)(_recorded[count - 1] >> 32) == symbol && (uint)_recorded[count - 1] != uint.MaxValue)
        {
            _recorded[count - 1]++;
            return;
        }
        if (count == _recorded.Length && !GrowRecorded())
        {
            // Full: the writing pass matches again. The count stays at the end so a literal's quick append fails from here on.
            _recordComplete = false;
            return;
        }
        _recorded[count] = (ulong)symbol << 32 | 1;
        _recordedCount = count + 1;
    }

    private bool GrowRecorded()
    {
        if (_recorded.Length >= MaxRecorded)
            return false;
        var grown = ArrayPool<ulong>.Shared.Rent(Math.Max(4096, _recorded.Length * 2));
        _recorded.AsSpan(0, _recordedCount).CopyTo(grown);
        if (_recorded.Length != 0)
            ArrayPool<ulong>.Shared.Return(_recorded);
        _recorded = grown;
        return true;
    }

    /// <summary>Writes the symbols the counting pass recorded.</summary>
    /// <remarks>
    /// The bit writer's state is carried in locals. Through the writer, every code would load and store it. The entries and the literal
    /// codes are read unchecked, since the count is within the record and a literal is a byte.
    /// </remarks>
    private void Replay(ref DeflateBitWriter w)
    {
        var destination = w.Destination;
        w.Save(out var position, out var held, out var count);
        ref var recorded = ref MemoryMarshal.GetReference(_recorded.AsSpan());
        ref var literals = ref MemoryMarshal.GetReference(_literalCodes.AsSpan());
        var literalCodes = _literalCodes;
        var distanceCodes = _distanceCodes;
        for (var k = 0; k < _recordedCount; k++)
        {
            var item = Unsafe.Add(ref recorded, k);
            var symbol = (uint)(item >> 32);
            var times = (uint)item;
            if ((symbol & 0x8000_0000u) == 0)
            {
                var entry = Unsafe.Add(ref literals, (int)symbol);
                var code = entry & 0xFFFF;
                var bits = (int)(entry >> 16);
                for (var t = 0u; t < times; t++)
                {
                    held |= (ulong)code << count;
                    count += bits;
                    if (count >= 32)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(position), (uint)held);
                        position += 4;
                        held >>= 32;
                        count -= 32;
                    }
                }
                continue;
            }
            var length = (int)(symbol >> 16 & 0xFF) + Deflate.MinMatch;
            var distance = (int)(symbol & 0xFFFF) + 1;
            var lengthCode = Deflate.LengthCode(length);
            var distanceCode = Deflate.DistanceCode(distance);
            // A match's four fields: the length code and its extra bits, the distance code and its extra bits.
            var lengthEntry = literalCodes[257 + lengthCode];
            var distanceEntry = distanceCodes[distanceCode];
            uint lengthBits = lengthEntry & 0xFFFF, lengthExtra = (uint)(length - Deflate.LengthBase(lengthCode));
            uint distanceBits = distanceEntry & 0xFFFF, distanceExtra = (uint)(distance - Deflate.DistanceBase(distanceCode));
            int lengthSize = (int)(lengthEntry >> 16), lengthExtraSize = Deflate.LengthExtraBits[lengthCode];
            int distanceSize = (int)(distanceEntry >> 16), distanceExtraSize = Deflate.DistanceExtraBits[distanceCode];
            for (var t = 0u; t < times; t++)
            {
                held |= (ulong)lengthBits << count;
                count += lengthSize;
                if (count >= 32)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(position), (uint)held);
                    position += 4;
                    held >>= 32;
                    count -= 32;
                }
                held |= (ulong)lengthExtra << count;
                count += lengthExtraSize;
                if (count >= 32)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(position), (uint)held);
                    position += 4;
                    held >>= 32;
                    count -= 32;
                }
                held |= (ulong)distanceBits << count;
                count += distanceSize;
                if (count >= 32)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(position), (uint)held);
                    position += 4;
                    held >>= 32;
                    count -= 32;
                }
                held |= (ulong)distanceExtra << count;
                count += distanceExtraSize;
                if (count >= 32)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(position), (uint)held);
                    position += 4;
                    held >>= 32;
                    count -= 32;
                }
            }
        }
        w.Resume(position, held, count);
    }

    // ---- scanlines --------------------------------------------------------------------------

    /// <summary>The first scanline of module row <paramref name="m"/>, packed on first use into its slot of the three.</summary>
    private ReadOnlySpan<byte> Row<TView>(in TView view, int m)
        where TView : struct, IModuleMatrixView
    {
        var slot = _rowCopies ? m % 3 : 0;
        var start = slot * (_stride + Slack);
        if (_slotRow[slot] != m)
        {
            PackScanline(in view, m, _rows.AsSpan(start, _stride + Slack));
            _slotRow[slot] = m;
        }
        return _rows.AsSpan(start, _stride);
    }

    /// <summary>
    /// Filter byte 0, then a bit a pixel, light 1 and dark 0, MSB first, padded with light, into a slot <see cref="Slack"/> bytes longer
    /// than the scanline.
    /// </summary>
    /// <remarks>
    /// Up to 8 pixels a module, a table turns eight modules into their bits at once, and up to 32 each module goes in alone. Run by run, the
    /// runs of one to three modules each cost a run length and a fill. The bits gather in a word that is stored eight bytes at a time, which
    /// writes into the slack. Past 32 pixels the runs are long and the packing goes run by run.
    /// </remarks>
    private void PackScanline<TView>(in TView view, int m, Span<byte> slot)
        where TView : struct, IModuleMatrixView
    {
        var stride = _stride;
        slot[0] = 0;
        var coreRow = m - _quietZone;
        if ((uint)coreRow >= (uint)_coreHeight)
        {
            slot.Slice(1, stride - 1).Fill(0xFF);
            return;
        }
        var pixels = _pixels;
        if (pixels > 32)
        {
            PackRuns(in view, coreRow, slot.Slice(0, stride));
            return;
        }

        var line = slot.Slice(1);
        var position = 0;
        ulong held = 0;
        var count = 0;
        var quietZone = (long)_quietZone * pixels;
        for (var light = quietZone; light > 0; light -= 56)
        {
            var k = (int)Math.Min(light, 56);
            held = (held << k) | (~0UL >> (64 - k));
            count += k;
            BinaryPrimitives.WriteUInt64BigEndian(line.Slice(position), held << (64 - count));
            position += count >> 3;
            count &= 7;
        }

        var core = view.CoreBits;
        var start = (long)coreRow * _coreWidth;
        var coreWidth = _coreWidth;
        var column = 0;
        if (pixels <= 8)
        {
            var table = Expansion(pixels);
            // Four modules a step, at most 32 bits, so the held bits never pass 39.
            var half = 4 * pixels;
            var halfMask = ~0UL >> (64 - half);
            for (; column + 8 <= coreWidth; column += 8)
            {
                var bits = table[(int)(PackedRuns.Window(core, start + column, 8) >> 56)];
                held = (held << half) | (bits >> half);
                count += half;
                BinaryPrimitives.WriteUInt64BigEndian(line.Slice(position), held << (64 - count));
                position += count >> 3;
                count &= 7;
                held = (held << half) | (bits & halfMask);
                count += half;
                BinaryPrimitives.WriteUInt64BigEndian(line.Slice(position), held << (64 - count));
                position += count >> 3;
                count &= 7;
            }
            var rest = coreWidth - column;
            if (rest > 0)
            {
                // Only the first modules of the byte are this row's, and their bits are the top of the entry.
                var bits = table[(int)(PackedRuns.Window(core, start + column, rest) >> 56)] >> ((8 - rest) * pixels);
                var k = rest * pixels;
                if (k > 32)
                {
                    held = (held << (k - 32)) | (bits >> 32);
                    count += k - 32;
                    BinaryPrimitives.WriteUInt64BigEndian(line.Slice(position), held << (64 - count));
                    position += count >> 3;
                    count &= 7;
                    bits &= 0xFFFF_FFFF;
                    k = 32;
                }
                held = (held << k) | bits;
                count += k;
                BinaryPrimitives.WriteUInt64BigEndian(line.Slice(position), held << (64 - count));
                position += count >> 3;
                count &= 7;
            }
        }
        else
        {
            var mask = ~0UL >> (64 - pixels);
            while (column < coreWidth)
            {
                var take = Math.Min(8, coreWidth - column);
                var word = PackedRuns.Window(core, start + column, take);
                for (var j = 0; j < take; j++)
                {
                    // Dark is 1 in the core and 0 in the scanline.
                    held = (held << pixels) | ((long)word < 0 ? 0 : mask);
                    word <<= 1;
                    count += pixels;
                    BinaryPrimitives.WriteUInt64BigEndian(line.Slice(position), held << (64 - count));
                    position += count >> 3;
                    count &= 7;
                }
                column += take;
            }
        }

        for (var light = quietZone; light > 0; light -= 56)
        {
            var k = (int)Math.Min(light, 56);
            held = (held << k) | (~0UL >> (64 - k));
            count += k;
            BinaryPrimitives.WriteUInt64BigEndian(line.Slice(position), held << (64 - count));
            position += count >> 3;
            count &= 7;
        }
        if (count != 0)
        {
            // Padded with light to the byte.
            held = (held << (8 - count)) | (~0UL >> (56 + count));
            BinaryPrimitives.WriteUInt64BigEndian(line.Slice(position), held << 56);
        }
    }

    /// <summary>For each byte of eight modules (dark 1, MSB first), their light-1 bits at <paramref name="pixels"/> a module, the first module highest.</summary>
    private ulong[] Expansion(int pixels)
    {
        if (_expansionPixels != pixels)
        {
            var mask = ~0UL >> (64 - pixels);
            for (var b = 0; b < 256; b++)
            {
                ulong bits = 0;
                for (var j = 7; j >= 0; j--)
                    bits = (bits << pixels) | (((b >> j) & 1) != 0 ? 0 : mask);
                _expansion[b] = bits;
            }
            _expansionPixels = pixels;
        }
        return _expansion;
    }

    /// <summary>The scanline run by run, for modules wider than 32 pixels.</summary>
    private void PackRuns<TView>(in TView view, int coreRow, Span<byte> row)
        where TView : struct, IModuleMatrixView
    {
        var bits = new ScanlineBits(row.Slice(1));
        var pixels = _pixels;
        bits.Add(light: true, (long)_quietZone * pixels);
        var core = view.CoreBits;
        var start = (long)coreRow * _coreWidth;
        var column = 0;
        var dark = false;
        while (column < _coreWidth)
        {
            var length = PackedRuns.Length(core, start + column, _coreWidth - column, dark);
            bits.Add(!dark, (long)length * pixels);
            column += length;
            dark = !dark;
        }
        bits.Add(light: true, (long)_quietZone * pixels);
        bits.PadWithLight();
    }

    /// <summary>Bits into a scanline from the high end of each byte, a run of one colour at a time.</summary>
    private ref struct ScanlineBits(Span<byte> line)
    {
        private readonly Span<byte> _line = line;
        private int _position;
        private int _held;
        private byte _byte;

        public void Add(bool light, long count)
        {
            if (count == 0)
                return;
            if (_held != 0)
            {
                var take = (int)Math.Min(8 - _held, count);
                if (light)
                    _byte |= (byte)((0xFF >> _held) ^ (0xFF >> (_held + take)));
                _held += take;
                count -= take;
                if (_held < 8)
                    return;
                _line[_position++] = _byte;
                _byte = 0;
                _held = 0;
            }
            var whole = (int)(count >> 3);
            _line.Slice(_position, whole).Fill(light ? (byte)0xFF : (byte)0);
            _position += whole;
            _held = (int)(count & 7);
            _byte = light && _held != 0 ? (byte)(0xFF << (8 - _held)) : (byte)0;
        }

        public void PadWithLight()
        {
            if (_held != 0)
                _line[_position++] = (byte)(_byte | (0xFF >> _held));
        }
    }
}
