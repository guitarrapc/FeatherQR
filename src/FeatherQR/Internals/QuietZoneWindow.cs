using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FeatherQR.Internals;

/// <summary>
/// A core matrix centred in a byte-per-module destination with a quiet zone around it, written in the destination itself: no second
/// buffer, no clear of the whole destination. A placer that writes the core into the strided window needs only the margins cleared
/// (Micro QR, rMQR); one that needs a contiguous core builds it at the destination's start, and <see cref="CenterCore"/> moves it
/// into the window (Standard QR).
/// </summary>
/// <remarks>
/// Between two core rows the margins are one gap: the right margin of one row and the left margin of the next, 2q bytes. A gap of up
/// to 16 bytes is zeroed as a clear of a constant 8 or 16 bytes that ends where the gap ends, which the JIT writes as one store. A clear
/// of the 2q bytes themselves, a length known only at run time, is a call: with one per row, moving a small symbol's core in place
/// took longer than clearing the whole destination and copying the rows. The constant clear also zeroes up to 6 bytes in front of the
/// gap, the end of the row before it, so that row is written after it; every core is wider than that.
/// </remarks>
internal static class QuietZoneWindow
{
    /// <summary>
    /// Moves the contiguous <paramref name="width"/> × <paramref name="height"/> core at the start of <paramref name="target"/> into the
    /// window a quiet zone of <paramref name="quietZoneSize"/> modules leaves, and clears the margins, so every byte of
    /// <paramref name="target"/> (exactly the matrix, quiet zone included) is written.
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
        Debug.Assert(quietZoneSize > 0 && width >= 11 && height > 0, "a quiet zone, and a core at least as wide as M1, which the gap clears' bounds assume");
        var totalWidth = width + 2 * quietZoneSize;
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
    /// Clears every byte of <paramref name="target"/> outside the window: the rows above and below, and the margins beside each core row.
    /// It is called before the window is written, since it may zero the last bytes of every window row but the last.
    /// </summary>
    public static void ClearMargins(Span<byte> target, int width, int height, int quietZoneSize)
    {
        Debug.Assert(quietZoneSize > 0 && width >= 11 && height > 0, "a quiet zone, and a core at least as wide as M1, which the gap clears' bounds assume");
        var totalWidth = width + 2 * quietZoneSize;
        var first = quietZoneSize * totalWidth + quietZoneSize;
        var gap = 2 * quietZoneSize;
        target.Slice(0, first).Clear();                                                  // the top rows and the first row's left margin
        for (var row = 1; row < height; row++)
            ClearGap(target, first + row * totalWidth, gap);                             // row - 1's right margin and this row's left
        target.Slice(first + (height - 1) * totalWidth + width).Clear();                  // the last row's right margin and the bottom rows
    }

    /// <summary>Zeroes the <paramref name="gap"/> bytes ending at <paramref name="gapEnd"/>; a gap of up to 16 bytes also zeroes up to 6 bytes in front of it.</summary>
    // Called once a row. Until it was marked for inlining, it cost the quiet-zone encodes of Micro QR and rMQR 5 to 15 % on the
    // WebAssembly interpreter, where the JIT had inlined it anyway.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClearGap(Span<byte> target, int gapEnd, int gap)
    {
        // A constant length, which the JIT writes as one store.
        if (gap <= 8)
            target.Slice(gapEnd - 8, 8).Clear();
        else if (gap <= 16)
            target.Slice(gapEnd - 16, 16).Clear();
        else
            target.Slice(gapEnd - gap, gap).Clear();
    }
}
