using System.Buffers;
using System.Text;

namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// SVG for a module matrix: a rectangle in the light colour behind everything, and one path that strokes each horizontal run of dark modules along its centre line.
/// </summary>
/// <remarks>
/// <para>
/// A stroke one module wide over a run is a move and a length, against a move and three lengths for a filled rectangle, so the path is about half the size.
/// Browsers draw the two to the same pixels at every size of a pixel a module and over.
/// The default stroke width of 1 and the default fill, which paints nothing on a line, are not written.
/// </para>
/// <para>
/// Coordinates are in modules. The first move carries the only half, to put the stroke on the row's centre line, and every later move is relative and whole.
/// The runs come from <see cref="ModuleRunEnumerator{TView}"/>, so the strokes cover exactly what <c>GetModuleRectangles</c> reports.
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
        var runs = new ModuleRunEnumerator<TView>(view, skipFinderPatterns: false);
        if (dark.A != 0 && runs.MoveNext())
        {
            // Both axes share one quiet zone.
            var quietZone = (width - view.CoreWidth) / 2;
            w.Raw("<path stroke=\""u8);
            w.Color(dark);
            w.Byte((byte)'"');
            if (dark.A != 255)
            {
                w.Raw(" stroke-opacity=\""u8);
                w.Opacity(dark.A);
                w.Byte((byte)'"');
            }

            var x = runs.RunStart + quietZone;
            var y = runs.RunRow + quietZone;
            w.Raw(" d=\"M"u8);
            w.Int(x);
            w.Byte((byte)' ');
            w.Int(y);
            w.Raw(".5h"u8);
            w.Int(runs.RunLength);
            x += runs.RunLength;

            while (runs.MoveNext())
            {
                var start = runs.RunStart + quietZone;
                var row = runs.RunRow + quietZone;
                // Rows only go down, so the second number is never negative and needs the space.
                w.Byte((byte)'m');
                w.Int(start - x);
                w.Byte((byte)' ');
                w.Int(row - y);
                w.Byte((byte)'h');
                w.Int(runs.RunLength);
                x = start + runs.RunLength;
                y = row;
            }
            w.Raw("\"/>"u8);
        }

        w.Raw("</svg>"u8);
        return w.Position;
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
