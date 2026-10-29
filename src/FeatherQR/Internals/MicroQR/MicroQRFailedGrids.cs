using System.Runtime.InteropServices;

namespace FeatherQR.Internals.MicroQR;

/// <summary>
/// The last grids of one grid search whose modules alone decided their failure (<see cref="MicroQRMatrixDecoder.DecodeSampledGrid"/>), packed a bit a module, so the search can skip a grid that has the modules of one of them.
/// </summary>
/// <remarks>
/// Such a grid fails the same wherever it was sampled, so its decode would only repeat the failure.
/// The scale and perspective searches around one frame often sample the same modules again, where a step moves no module centre into another pixel.
/// Only the last <see cref="Capacity"/> are kept: a repeat is mostly of a recent grid.
/// </remarks>
internal ref struct MicroQRFailedGrids
{
    /// <summary>The grids kept, the oldest dropped first.</summary>
    public const int Capacity = 64;

    /// <summary>The words a grid packs into: M4's 17 × 17 modules, and its size in the top byte of the last word.</summary>
    public const int Words = 5;

    private readonly Span<ulong> _keys;
    private readonly Span<ulong> _prints;
    private int _count;
    private int _next;
    private ulong _print;

    /// <param name="keys">Room for <c>(Capacity + 1) · Words</c> words: the grids kept, then the grid <see cref="Seen"/> packed last.</param>
    /// <param name="prints">Room for <see cref="Capacity"/> words.</param>
    public MicroQRFailedGrids(Span<ulong> keys, Span<ulong> prints)
    {
        _keys = keys.Slice(0, (Capacity + 1) * Words);
        _prints = prints.Slice(0, Capacity);
        _count = 0;
        _next = 0;
        _print = 0;
    }

    /// <summary>Packs the first <paramref name="size"/> × <paramref name="size"/> modules, and returns whether a grid kept has that size and those modules.</summary>
    public bool Seen(ReadOnlySpan<byte> modules, int size)
    {
        var packed = _keys.Slice(Capacity * Words, Words);
        Pack(modules, size, packed);
        var print = 0UL;
        foreach (var word in packed)
            print = (print ^ word) * 0x9E3779B97F4A7C15UL;
        _print = print;
        for (var i = 0; i < _count; i++)
        {
            if (_prints[i] == print && _keys.Slice(i * Words, Words).SequenceEqual(packed))
                return true;
        }
        return false;
    }

    /// <summary>Keeps the grid <see cref="Seen"/> packed last, in place of the oldest once full.</summary>
    public void Remember()
    {
        _keys.Slice(Capacity * Words, Words).CopyTo(_keys.Slice(_next * Words, Words));
        _prints[_next] = _print;
        _next = _next == Capacity - 1 ? 0 : _next + 1;
        if (_count < Capacity)
            _count++;
    }

    /// <summary>One bit a module, set where the module is not 0, eight modules a step.</summary>
    internal static void Pack(ReadOnlySpan<byte> modules, int size, Span<ulong> packed)
    {
        packed.Clear();
        var count = size * size;
        var i = 0;
        for (; i + 8 <= count; i += 8)
        {
            var eight = MemoryMarshal.Read<ulong>(modules.Slice(i));
            // A byte's top bit set where the byte is not 0, with no carry into the next byte
            var nonZero = (((eight & 0x7F7F7F7F7F7F7F7FUL) + 0x7F7F7F7F7F7F7F7FUL) | eight) & 0x8080808080808080UL;
            // Byte k's bit lands on bit 56 + k of the product, and no two terms meet
            packed[i >> 6] |= ((nonZero >> 7) * 0x0102040810204080UL >> 56) << (i & 63);
        }
        for (; i < count; i++)
        {
            if (modules[i] != 0)
                packed[i >> 6] |= 1UL << (i & 63);
        }
        packed[Words - 1] |= (ulong)size << 56;
    }
}
