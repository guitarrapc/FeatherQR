#if !NET10_0_OR_GREATER
using System.Buffers.Binary;
#endif
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FeatherQR.Internals;

/// <summary>
/// A core matrix centred in a byte-per-module destination with a quiet zone around it, written in the destination itself: no second
/// buffer, no clear of the whole destination. A placer that writes the core into the strided window needs only the margins cleared
/// (Micro QR, rMQR); one that needs a contiguous core builds it at the destination's start, and <see cref="CenterCore"/> moves it
/// into the window (Standard QR on every build but netstandard2.0).
/// </summary>
/// <remarks>
/// Between two core rows the margins are one gap: the right margin of one row and the left margin of the next, 2q bytes. A gap of up
/// to 16 bytes is zeroed as the 8 or 16 bytes that end where the gap ends, one or two stores, and a wider one as 16-byte stores back
/// from its end, the last at its start (see <see cref="ClearGap"/>). A clear of the 2q bytes themselves, a length known only at run
/// time, is a call: with one per row, moving a small symbol's core in place took longer than clearing the whole destination and copying
/// the rows. The stores of a gap of up to 16 bytes also zero up to 6 bytes in front of it, the end of the row before it, so that row is
/// written after them; every core is wider than that.
/// </remarks>
internal static partial class QuietZoneWindow
{
    /// <summary>
    /// Moves the contiguous <paramref name="width"/> × <paramref name="height"/> core at the start of <paramref name="target"/> into the
    /// window a quiet zone of <paramref name="quietZoneSize"/> modules leaves, and clears the margins, so every byte of
    /// <paramref name="target"/>, which is exactly the matrix, quiet zone included, is written.
    /// </summary>
    /// <remarks>
    /// The last row moves first. Row r lands at (q + r) × (width + 2q) + q, past r × width, where the rows not yet moved end, so no
    /// row is overwritten before it moves; a row overlapping its own place moves as a memmove does. The gap in front of a row is
    /// zeroed once the row has moved. What that zeroes reaches back from the row's place at most 16 bytes, or the gap when it is
    /// wider, and the rows not yet moved end q × (width + 2q) + 3q or more bytes before it, which is no nearer for a core of 11 or
    /// more modules, so the bytes zeroed in front of the gap are in the window of the row that moves next. The rows above the first
    /// core row are cleared last, since the core starts there.
    /// </remarks>
    public static void CenterCore(Span<byte> target, int width, int height, int quietZoneSize)
    {
        var totalWidth = width + 2 * quietZoneSize;
        Debug.Assert(quietZoneSize > 0 && width >= 11 && height > 0, "a quiet zone, and a core at least as wide as M1, which the gap stores' bounds assume");
        Debug.Assert(target.Length == totalWidth * (height + 2 * quietZoneSize), "target is exactly the matrix: the bottom rows are cleared to its end");
        var first = quietZoneSize * totalWidth + quietZoneSize;
        var gap = 2 * quietZoneSize;
        target.Slice(first + (height - 1) * totalWidth + width).Clear();                  // the last row's right margin and the bottom rows
        for (var row = height - 1; row > 0; row--)
        {
            var start = first + row * totalWidth;
            target.Slice(row * width, width).CopyTo(target.Slice(start, width));
            ClearGap(target, start, gap);                                                // row - 1's right margin and this row's left
        }
        target.Slice(0, width).CopyTo(target.Slice(first, width));
        target.Slice(0, first).Clear();                                                  // the top rows and the first row's left margin
    }

    /// <summary>
    /// Clears every byte of <paramref name="target"/>, which is exactly the matrix, outside the window: the rows above and below, and
    /// the margins beside each core row. It is called before the window is written, since it may zero the last bytes of every window
    /// row but the last.
    /// </summary>
    public static void ClearMargins(Span<byte> target, int width, int height, int quietZoneSize)
    {
        var totalWidth = width + 2 * quietZoneSize;
        Debug.Assert(quietZoneSize > 0 && width >= 11 && height > 0, "a quiet zone, and a core at least as wide as M1, which the gap stores' bounds assume");
        Debug.Assert(target.Length == totalWidth * (height + 2 * quietZoneSize), "target is exactly the matrix: the bottom rows are cleared to its end");
        var first = quietZoneSize * totalWidth + quietZoneSize;
        var gap = 2 * quietZoneSize;
        target.Slice(0, first).Clear();                                                  // the top rows and the first row's left margin
        for (var row = 1; row < height; row++)
            ClearGap(target, first + row * totalWidth, gap);                             // row - 1's right margin and this row's left
        target.Slice(first + (height - 1) * totalWidth + width).Clear();                  // the last row's right margin and the bottom rows
    }

    /// <summary>Zeroes the <paramref name="gap"/> bytes ending at <paramref name="gapEnd"/>; a gap of up to 16 bytes also zeroes up to 6 bytes in front of it.</summary>
    /// <remarks>
    /// .NET 10 writes a clear of a constant 8 or 16 bytes as one store whichever form its profile saw run. .NET 8 leaves the form the
    /// profile did not see as a call, so a process that encoded with the default quiet zones and then with one of 5 to 8 paid a call a
    /// row. The builds before .NET 10 write 8-byte stores instead, which .NET 8 inlines in either form. .NET Framework 4.8 inlines them
    /// too but calls <c>MemoryMarshal.GetReference</c> for each (2026-10-06).
    /// <para>
    /// A wider gap, from quiet zone 9, is 16-byte stores back from its end, the last at its start, so nothing in front of it is zeroed: two
    /// stores up to quiet zone 16. Its clear was a call a row, and the stores took the span encodes at quiet zones 9, 12 and 16 to 0.88 to
    /// 0.91 of their time for Micro QR, 0.95 to 0.99 for Standard QR and 0.96 to 0.99 for rMQR on .NET 8, and to 0.94 to 0.96, 0.96 to 0.99
    /// and 0.96 to 0.98 on .NET 10, where the rows they do not reach read 0.97 to 1.03 (2026-10-07). The browser keeps the clear: with the
    /// stores the WebAssembly interpreter took 1.01 to 1.05 times as long at quiet zones 9 and 16 and AOT-compiled WebAssembly 0.90 to 0.98,
    /// and one test serves both WebAssembly builds. The netstandard builds keep it too, not timed with the stores.
    /// </para>
    /// </remarks>
    // Called once a row. Until it was marked for inlining, the WebAssembly interpreter called it, and in one run the quiet-zone encodes
    // timed there (Micro QR M2 to M4, rMQR R7x43, R11x59 and R17x139, Standard QR versions 1 and 6) took 1.04 to 1.19 times as long as
    // with it inlined and 1.03 to 1.15 times as long as the code before it. The JIT inlined it without the attribute.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClearGap(Span<byte> target, int gapEnd, int gap)
    {
        if (gap <= 8)
        {
#if NET10_0_OR_GREATER
            target.Slice(gapEnd - 8, 8).Clear();
#else
            BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(gapEnd - 8), 0);
#endif
        }
        else if (gap <= 16)
        {
#if NET10_0_OR_GREATER
            target.Slice(gapEnd - 16, 16).Clear();
#else
            BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(gapEnd - 16), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(gapEnd - 8), 0);
#endif
        }
        else
        {
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsBrowser())
            {
                var start = gapEnd - gap;
                for (var end = gapEnd; end - 16 > start; end -= 16)
                    Clear16(target, end - 16);
                Clear16(target, start);
                return;
            }
#endif
            target.Slice(gapEnd - gap, gap).Clear();
        }
    }
}
