using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// SVG for a module matrix: a rectangle in the light colour behind everything, and one path that strokes each horizontal run of dark modules along its centre line.
/// </summary>
/// <remarks>
/// <para>
/// A stroke one module wide over a run is a move and a length, against a move and three lengths for a filled rectangle, so the path is shorter and draws like filled runs.
/// The default stroke width of 1 and the default fill, which paints nothing on a line, are not written.
/// </para>
/// <para>
/// Coordinates are in modules. The first move carries the only half, to put the stroke on the row's centre line, and every later move is relative and whole.
/// The runs are read from the packed rows a 64-bit word at a time, two leading-zero counts a run, where <see cref="ModuleRunEnumerator{TView}"/>
/// reads a module a step. Tests hold the strokes to the runs <c>GetModuleRectangles</c> reports.
/// </para>
/// <para>
/// At a set size the root's width and height are the canvas and the path is unchanged: the view box and the background grow by the light
/// around the symbol, in modules to three decimals, and the renderer scales the drawing and snaps it to its pixels.
/// </para>
/// <para>
/// A styled image, with shaped or scaled modules or shaped finder patterns, is the other partial file's: one shape defined once and used
/// at each dark module, the finder patterns as their own elements, and the stroked runs kept for whatever is still a square.
/// </para>
/// </remarks>
internal static partial class SvgWriter
{
    // Room for the markup, up to twelve numbers of up to ten digits and three decimals, two colours and two opacities.
    private const int MarkupBound = 768;

    public static string GetString<TView>(in TView view, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        var layout = ImageLayout.Create(in view, in options);
        var icon = IconArea.Get(in view, in options);
        var rented = ArrayPool<byte>.Shared.Rent(MaxLength(in view, in options, in layout));
        try
        {
            var length = Write(in view, in options, in layout, in icon, rented);
            return Encoding.ASCII.GetString(rented, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static int Write<TView>(in TView view, IBufferWriter<byte> writer, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        var layout = ImageLayout.Create(in view, in options);
        var icon = IconArea.Get(in view, in options);
        // Written in full first, so the caller's writer is asked for the exact length and not for the bound.
        var rented = ArrayPool<byte>.Shared.Rent(MaxLength(in view, in options, in layout));
        try
        {
            var length = Write(in view, in options, in layout, in icon, rented);
            rented.AsSpan(0, length).CopyTo(BufferWriterSpan.Get(writer, length));
            writer.Advance(length);
            return length;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int Write<TView>(in TView view, in SymbolImageOptions options, in ImageLayout layout, in IconArea icon, Span<byte> destination)
        where TView : struct, IModuleMatrixView
    {
        var w = new AsciiWriter(destination);
        int width = view.Width, height = view.Height;
        var fitted = !layout.Uniform;

        w.Raw("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\""u8);
        if (fitted)
        {
            w.Modules(-layout.Left, in layout);
            w.Byte((byte)' ');
            w.Modules(-layout.Top, in layout);
            w.Byte((byte)' ');
            w.Modules(layout.Width, in layout);
            w.Byte((byte)' ');
            w.Modules(layout.Height, in layout);
        }
        else
        {
            w.Raw("0 0 "u8);
            w.Int(width);
            w.Byte((byte)' ');
            w.Int(height);
        }
        w.Raw("\" width=\""u8);
        w.Int(layout.Width);
        w.Raw("\" height=\""u8);
        w.Int(layout.Height);
        // A styled document may draw curves, which come out jagged when crisp, so it leaves the renderer its anti-aliasing.
        var curved = options.ModuleStyle != ModuleStyle.Square || options.FinderPatternStyle != FinderPatternStyle.Square;
        w.Raw(curved ? "\">"u8 : "\" shape-rendering=\"crispEdges\">"u8);

        var light = options.LightColor;
        if (light.A != 0)
        {
            if (fitted)
            {
                // The background is the view box, which the pad around the symbol has grown.
                w.Raw("<rect x=\""u8);
                w.Modules(-layout.Left, in layout);
                w.Raw("\" y=\""u8);
                w.Modules(-layout.Top, in layout);
                w.Raw("\" width=\""u8);
                w.Modules(layout.Width, in layout);
                w.Raw("\" height=\""u8);
                w.Modules(layout.Height, in layout);
                w.Raw("\" fill=\""u8);
            }
            else
            {
                w.Raw("<rect width=\"100%\" height=\"100%\" fill=\""u8);
            }
            w.Color(light);
            w.Byte((byte)'"');
            if (light.A != 255)
            {
                w.Raw(" fill-opacity=\""u8);
                w.Opacity(light.A);
                w.Byte((byte)'"');
            }
            w.Raw("/>"u8);
        }

        var dark = options.DarkColor;
        // Both axes share one quiet zone.
        var quietZone = (width - view.CoreWidth) / 2;
        if (dark.A == 0)
        {
            WriteIconImage(ref w, in options, in icon, quietZone);
            w.Raw("</svg>"u8);
            return w.Position;
        }
        var styled = options.IsStyled;
        var squareModules = options.ModuleStyle == ModuleStyle.Square && options.ModuleScalePercent == 100;
        if (styled)
            OpenStyled(ref w, in view, in options, in icon, quietZone, squareModules);
        if (squareModules)
        {
            // The runs come from the packed rows, or from a copy with the icon area's modules and, under a finder style, the finder patterns' cleared.
            var excludeFinders = styled && options.FinderPatternStyle != FinderPatternStyle.Square;
            var bits = icon.Present || excludeFinders ? MaskedBits(in view, in icon, excludeFinders) : view.CoreBits;
            // The scan sits in this method, where the writer is a local: as a method of its own, or inlined behind a reference to the writer, it ran slower.
            int coreWidth = view.CoreWidth, coreHeight = view.CoreHeight;
            // The scan's state is plain locals of this loop rather than an enumerator's fields, so it stays in registers.
            // The word holds the row's next bits from the column on, and held says how many of them belong to the row.
            int row = 0, column = 0, held = 0;
            ulong word = 0;
            int at = 0, x = 0, y = 0;
            var first = true;
            // MaxLength leaves room past any document, so the path's writes skip bounds checks.
            ref var d = ref MemoryMarshal.GetReference(destination);
            while (row < coreHeight)
            {
                if (held == 0)
                {
                    if (column >= coreWidth)
                    {
                        row++;
                        column = 0;
                        continue;
                    }
                    var bit = (long)row * coreWidth + column;
                    held = Math.Min(64 - (int)(bit & 7), coreWidth - column);
                    word = PackedRuns.Window(bits, bit, held);
                }
                var lightModules = PackedRuns.LeadingZeros(word);
                if (lightModules >= held)
                {
                    column += held;
                    held = 0;
                    continue;
                }
                word <<= lightModules;
                column += lightModules;
                held -= lightModules;

                var runStart = column;
                while (true)
                {
                    var darkModules = PackedRuns.LeadingZeros(~word);
                    if (darkModules < held)
                    {
                        word <<= darkModules;
                        held -= darkModules;
                        column += darkModules;
                        break;
                    }
                    // The run reaches the end of the word, and goes on if the next bits of the row start dark.
                    column += held;
                    held = 0;
                    if (column >= coreWidth)
                        break;
                    var bit = (long)row * coreWidth + column;
                    held = Math.Min(64 - (int)(bit & 7), coreWidth - column);
                    word = PackedRuns.Window(bits, bit, held);
                    if ((long)word >= 0)
                        break;
                }

                var runLength = column - runStart;
                var start = runStart + quietZone;
                var rowAt = row + quietZone;
                if (first)
                {
                    w.Raw("<path stroke=\""u8);
                    w.Color(dark);
                    w.Byte((byte)'"');
                    if (dark.A != 255)
                    {
                        w.Raw(" stroke-opacity=\""u8);
                        w.Opacity(dark.A);
                        w.Byte((byte)'"');
                    }
                    w.Raw(" d=\"M"u8);
                    w.Int(start);
                    w.Byte((byte)' ');
                    w.Int(rowAt);
                    w.Raw(".5h"u8);
                    w.Int(runLength);
                    at = w.Position;
                    first = false;
                }
                else
                {
                    Unsafe.Add(ref d, at++) = (byte)'m';
                    at = PutInt(ref d, at, start - x);
                    if (rowAt == y)
                    {
                        // Most runs share the last one's row, so the step down of 0 goes with its space and "h" in one store.
                        PutUInt32(ref d, at, 0x00_68_30_20u);
                        at += 3;
                    }
                    else
                    {
                        // Rows only go down, so the second number is never negative and needs the space.
                        Unsafe.Add(ref d, at++) = (byte)' ';
                        at = PutInt(ref d, at, rowAt - y);
                        Unsafe.Add(ref d, at++) = (byte)'h';
                    }
                    at = PutInt(ref d, at, runLength);
                }
                x = start + runLength;
                y = rowAt;
            }

            if (!first)
            {
                "\"/>"u8.CopyTo(destination.Slice(at));
                w.Seek(at + 3);
            }
        }
        if (styled)
            CloseStyled(ref w, in view, in options, quietZone, dark);
        WriteIconImage(ref w, in options, in icon, quietZone);
        w.Raw("</svg>"u8);
        return w.Position;
    }

    // The packed rows of the last masked document on this thread, kept so that a masked document rents and returns nothing around its scan.
    [ThreadStatic]
    private static byte[]? maskedBits;

    /// <summary>
    /// A copy of the packed rows with the icon area's modules and, when asked, the finder patterns' cleared, so the scan has no case to test.
    /// </summary>
    private static ReadOnlySpan<byte> MaskedBits<TView>(in TView view, in IconArea icon, bool excludeFinders)
        where TView : struct, IModuleMatrixView
    {
        var bits = view.CoreBits;
        var buffer = maskedBits;
        if (buffer == null || buffer.Length < bits.Length)
            maskedBits = buffer = new byte[bits.Length];
        var masked = buffer.AsSpan(0, bits.Length);
        bits.CopyTo(masked);
        for (var row = 0; row < view.CoreHeight; row++)
        {
            RowMasks(in view, in icon, row, excludeFinders, out var from0, out var to0, out var from1, out var to1, out var from2, out var to2);
            var start = (long)row * view.CoreWidth;
            ClearBits(masked, start + from0, start + to0);
            ClearBits(masked, start + from1, start + to1);
            ClearBits(masked, start + from2, start + to2);
        }
        return masked;
    }


    /// <summary>The column ranges of a core row that the icon area and, when asked, the finder patterns cover. An empty range has its end at or before its start.</summary>
    private static void RowMasks<TView>(in TView view, in IconArea icon, int row, bool excludeFinders, out int from0, out int to0, out int from1, out int to1, out int from2, out int to2)
        where TView : struct, IModuleMatrixView
    {
        from0 = to0 = from1 = to1 = from2 = to2 = 0;
        if (icon.CoversRow(row))
        {
            from0 = icon.Column;
            to0 = icon.Column + icon.Size;
        }
        if (!excludeFinders)
            return;
        // At most two finder patterns share a row: the top two, or the bottom-left one alone.
        var found = 0;
        for (var i = 0; i < view.FinderPatternCount; i++)
        {
            view.GetFinderPatternOrigin(i, out var finderRow, out var finderColumn);
            if ((uint)(row - finderRow) >= 7)
                continue;
            if (found++ == 0)
            {
                from1 = finderColumn;
                to1 = finderColumn + 7;
            }
            else
            {
                from2 = finderColumn;
                to2 = finderColumn + 7;
            }
        }
    }

    /// <summary>Clears the packed bits from <paramref name="from"/> up to <paramref name="to"/>, each byte's first bit its high one. Nothing for an empty range.</summary>
    private static void ClearBits(Span<byte> bits, long from, long to)
    {
        for (var bit = from; bit < to; bit++)
            bits[(int)(bit >> 3)] &= (byte)~(0x80 >> (int)(bit & 7));
    }

    /// <summary>The caller's image over the icon area, stretched to fill it, as a data URI. Nothing without an image.</summary>
    private static void WriteIconImage(ref AsciiWriter w, in SymbolImageOptions options, in IconArea icon, int quietZone)
    {
        if (!icon.Present || options.IconImage.IsEmpty)
            return;
        w.Raw("<image x=\""u8);
        w.Int(quietZone + icon.IconColumn);
        w.Raw("\" y=\""u8);
        w.Int(quietZone + icon.IconRow);
        w.Raw("\" width=\""u8);
        w.Int(icon.IconSize);
        w.Raw("\" height=\""u8);
        w.Int(icon.IconSize);
        w.Raw("\" preserveAspectRatio=\"none\" href=\"data:"u8);
        w.Ascii(string.IsNullOrEmpty(options.IconMediaType) ? "image/png" : options.IconMediaType!);
        w.Raw(";base64,"u8);
        w.Base64(options.IconImage.Span);
        w.Raw("\"/>"u8);
    }

    /// <summary>
    /// A number of the path at <paramref name="at"/>, returning the position after it.
    /// A store of 4 bytes writes up to 3 past the number, which the next write covers and MaxLength has room for.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PutInt(ref byte destination, int at, int value)
    {
        if (value < 0)
        {
            Unsafe.Add(ref destination, at++) = (byte)'-';
            value = -value;
        }
        // Values below 256 come from a table rather than a division by 10 a digit.
        if ((uint)value < (uint)DigitTable.Length)
        {
            var entry = DigitTable[value];
            PutUInt32(ref destination, at, entry);
            return at + (int)(entry >> 24);
        }
        var end = at + Digits(value);
        for (var i = end - 1; i >= at; i--)
        {
            Unsafe.Add(ref destination, i) = (byte)('0' + value % 10);
            value /= 10;
        }
        return end;
    }

    /// <summary>Four bytes in the order of their significance from the low byte up, on either byte order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PutUInt32(ref byte destination, int at, uint value)
        => Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, at), BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value));

    /// <summary>Each value below 256 as its ASCII digits from the low byte up, and how many there are in the top byte.</summary>
    private static readonly uint[] DigitTable = BuildDigitTable();

    private static uint[] BuildDigitTable()
    {
        var table = new uint[256];
        for (var value = 0; value < table.Length; value++)
        {
            uint ones = (uint)('0' + value % 10), tens = (uint)('0' + value / 10 % 10), hundreds = (uint)('0' + value / 100);
            table[value] = value < 10 ? ones | 1u << 24
                : value < 100 ? tens | ones << 8 | 2u << 24
                : hundreds | tens << 8 | ones << 16 | 3u << 24;
        }
        return table;
    }

    /// <summary>
    /// The longest document the matrix can give.
    /// A relative move stays inside the core, so its numbers have no more digits than the core's sides.
    /// </summary>
    internal static int MaxLength<TView>(in TView view, in SymbolImageOptions options, in ImageLayout layout)
        where TView : struct, IModuleMatrixView
    {
        int coreWidth = view.CoreWidth, coreHeight = view.CoreHeight;
        // "m", the sign and digits of the step across, a space, the step down, "h" and the length.
        var perRun = 4 + 2 * Digits(coreWidth) + Digits(coreHeight);
        // The first move is absolute, quiet zone included, and carries the ".5".
        var firstMove = 4 + Digits(view.Width) + Digits(view.Height);
        // In a long, since an icon image of over 1.6 GB alone passes an int.
        long length = MarkupBound + firstMove + (long)ModuleRunScanner.GetMaxRunCount(coreWidth, coreHeight) * perRun;
        if (!options.IconImage.IsEmpty)
        {
            // The image element, its media type and the bytes in base64.
            length += 160 + (options.IconMediaType?.Length ?? 0) + (options.IconImage.Length + 2L) / 3 * 4;
        }
        if (options.IsStyled)
        {
            // The group, the shape and up to three finder patterns on top of the runs.
            length += StyledMarkupBound;
            // A use element for each dark module where the modules have a shape of their own. A bound of every module would pass the
            // 1 MiB some runtimes' shared pool keeps at the larger versions, and the rent would then allocate.
            if (options.ModuleStyle != ModuleStyle.Square || options.ModuleScalePercent != 100)
                length += DarkModules(view.CoreBits) * (StyledUseBound + Digits(view.Width) + Digits(view.Height));
        }
        ImageSize.ThrowIfLongerThanAnArray(length, layout.Width, layout.Height);
        return (int)length;
    }

    private static long DarkModules(ReadOnlySpan<byte> bits)
    {
        long count = 0;
        foreach (var b in bits)
        {
            var v = b - ((b >> 1) & 0x55);
            v = (v & 0x33) + ((v >> 2) & 0x33);
            count += (v + (v >> 4)) & 0x0F;
        }
        return count;
    }

    private static int Digits(int value)
    {
        var digits = 1;
        while (value >= 10)
        {
            value /= 10;
            digits++;
        }
        return digits;
    }

    private ref struct AsciiWriter(Span<byte> destination)
    {
        private readonly Span<byte> _destination = destination;

        public int Position { get; private set; }

        /// <summary>The whole destination, for writes that skip the bounds checks.</summary>
        public readonly Span<byte> Destination => _destination;

        /// <summary>Continues after bytes written straight into the destination.</summary>
        public void Seek(int position) => Position = position;

        public void Byte(byte value) => _destination[Position++] = value;

        public void Ascii(string text)
        {
            foreach (var c in text)
                _destination[Position++] = (byte)c;
        }

        public void Base64(ReadOnlySpan<byte> bytes)
        {
            System.Buffers.Text.Base64.EncodeToUtf8(bytes, _destination.Slice(Position), out _, out var written);
            Position += written;
        }

        public void Raw(ReadOnlySpan<byte> text)
        {
            text.CopyTo(_destination.Slice(Position));
            Position += text.Length;
        }

        public void Int(int value)
        {
            if (value < 0)
            {
                Byte((byte)'-');
                value = -value;
            }
            var end = Position + Digits(value);
            for (var i = end - 1; i >= Position; i--)
            {
                _destination[i] = (byte)('0' + value % 10);
                value /= 10;
            }
            Position = end;
        }

        /// <summary>
        /// A length in pixels as modules, to three decimals with trailing zeros dropped.
        /// A thousandth of a module is well under a pixel at any scale a symbol reads at, and whole values stay whole.
        /// </summary>
        public void Modules(long pixels, in ImageLayout layout)
        {
            var negative = pixels < 0;
            var scaled = Math.Abs(pixels) * layout.Denominator;
            var whole = scaled / layout.Numerator;
            var rest = (scaled % layout.Numerator * 1000 + layout.Numerator / 2) / layout.Numerator;
            var thousandths = whole * 1000 + rest;
            Thousandths(negative ? -thousandths : thousandths);
        }

        /// <summary>A number of thousandths as a decimal, trailing zeros dropped, so a whole value stays whole.</summary>
        public void Thousandths(long thousandths)
        {
            if (thousandths < 0)
            {
                Byte((byte)'-');
                thousandths = -thousandths;
            }
            Int((int)(thousandths / 1000));
            var rest = (int)(thousandths % 1000);
            if (rest == 0)
                return;
            Byte((byte)'.');
            Byte((byte)('0' + rest / 100));
            if (rest % 100 == 0)
                return;
            Byte((byte)('0' + rest / 10 % 10));
            if (rest % 10 == 0)
                return;
            Byte((byte)('0' + rest % 10));
        }

        /// <summary><c>#rrggbb</c>, or <c>#rgb</c> when each channel's two digits are the same.</summary>
        public void Color(SymbolColor color)
        {
            Byte((byte)'#');
            if (color.R >> 4 == (color.R & 15) && color.G >> 4 == (color.G & 15) && color.B >> 4 == (color.B & 15))
            {
                Byte(Hex(color.R & 15));
                Byte(Hex(color.G & 15));
                Byte(Hex(color.B & 15));
                return;
            }
            Byte(Hex(color.R >> 4));
            Byte(Hex(color.R & 15));
            Byte(Hex(color.G >> 4));
            Byte(Hex(color.G & 15));
            Byte(Hex(color.B >> 4));
            Byte(Hex(color.B & 15));
        }

        /// <summary>
        /// An alpha from 1 to 254 as an opacity with three decimals, trailing zeros dropped.
        /// Neighbouring alphas are 1/255 apart, so three decimals keep them apart and scale back to the same byte.
        /// </summary>
        public void Opacity(byte alpha)
        {
            var thousandths = (alpha * 1000 + 127) / 255;
            Raw("0."u8);
            Byte((byte)('0' + thousandths / 100));
            if (thousandths % 100 == 0)
                return;
            Byte((byte)('0' + thousandths / 10 % 10));
            if (thousandths % 10 == 0)
                return;
            Byte((byte)('0' + thousandths % 10));
        }

        private static byte Hex(int nibble) => (byte)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
    }
}
