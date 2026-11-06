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
/// </remarks>
internal static class SvgWriter
{
    // Room for the markup, four numbers of up to ten digits, two colours and two opacities.
    private const int MarkupBound = 512;

    public static string GetString<TView>(in TView view, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        ImageSize.ThrowIfTooLarge(in view, in options);
        var rented = ArrayPool<byte>.Shared.Rent(MaxLength(in view));
        try
        {
            var length = Write(in view, in options, rented);
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
        ImageSize.ThrowIfTooLarge(in view, in options);
        // Written in full first, so the caller's writer is asked for the exact length and not for the bound.
        var rented = ArrayPool<byte>.Shared.Rent(MaxLength(in view));
        try
        {
            var length = Write(in view, in options, rented);
            rented.AsSpan(0, length).CopyTo(writer.GetSpan(length));
            writer.Advance(length);
            return length;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int Write<TView>(in TView view, in SymbolImageOptions options, Span<byte> destination)
        where TView : struct, IModuleMatrixView
    {
        var w = new AsciiWriter(destination);
        int width = view.Width, height = view.Height, pixels = options.ModulePixelSize;

        w.Raw("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 "u8);
        w.Int(width);
        w.Byte((byte)' ');
        w.Int(height);
        w.Raw("\" width=\""u8);
        w.Int(width * pixels);
        w.Raw("\" height=\""u8);
        w.Int(height * pixels);
        w.Raw("\" shape-rendering=\"crispEdges\">"u8);

        var light = options.LightColor;
        if (light.A != 0)
        {
            w.Raw("<rect width=\"100%\" height=\"100%\" fill=\""u8);
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
        if (dark.A == 0)
        {
            w.Raw("</svg>"u8);
            return w.Position;
        }

        var bits = view.CoreBits;
        int coreWidth = view.CoreWidth, coreHeight = view.CoreHeight;
        // Both axes share one quiet zone.
        var quietZone = (width - coreWidth) / 2;
        // The scan's state is plain locals. As an enumerator's fields it spilled to the stack on every run.
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

        if (first)
        {
            w.Raw("</svg>"u8);
            return w.Position;
        }
        "\"/></svg>"u8.CopyTo(destination.Slice(at));
        return at + 9;
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
        // Values below 256 come from a table. Dividing by 10 a digit at a time was most of the path's cost.
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
    private static int MaxLength<TView>(in TView view)
        where TView : struct, IModuleMatrixView
    {
        int coreWidth = view.CoreWidth, coreHeight = view.CoreHeight;
        // "m", the sign and digits of the step across, a space, the step down, "h" and the length.
        var perRun = 4 + 2 * Digits(coreWidth) + Digits(coreHeight);
        // The first move is absolute, quiet zone included, and carries the ".5".
        var firstMove = 4 + Digits(view.Width) + Digits(view.Height);
        return MarkupBound + firstMove + ModuleRunScanner.GetMaxRunCount(coreWidth, coreHeight) * perRun;
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

        public void Byte(byte value) => _destination[Position++] = value;

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
