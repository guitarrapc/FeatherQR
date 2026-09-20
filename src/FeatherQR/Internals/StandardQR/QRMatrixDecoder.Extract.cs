using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FeatherQR.Internals.StandardQR;

internal static partial class QRMatrixDecoder
{
    /// <summary>
    /// Reads the codeword stream through the encoder's placement runs (<see cref="ModulePlacer.PlacementLayout.Ops"/>), unmasked.
    /// Same result as <see cref="ExtractCodewordsReference"/> over a cleared output, without a per-module test: four rows of a run are one output byte, and the mask under that byte comes from <see cref="RunMask"/>.
    /// </summary>
    /// <remarks>
    /// Writes every byte of <paramref name="output"/> the free modules reach, so a full-length output needs no clear; an output shorter than the stream is its prefix, and a longer one receives the remainder bits and keeps its tail.
    /// Reads no per-version table beyond the ones <see cref="ModulePlacer.GetLayout"/> already holds for the encoder.
    /// </remarks>
    /// <param name="modules">Core module matrix, one byte per module (0 = light, non-zero = dark), row-major, no quiet zone.</param>
    /// <param name="layout">The version's placement tables.</param>
    /// <param name="maskPattern">Mask pattern 0-7.</param>
    /// <param name="output">Receives the interleaved data and ECC codewords.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maskPattern"/> is not 0-7.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="modules"/> is smaller than the layout's matrix.</exception>
    internal static void ExtractCodewords(ReadOnlySpan<byte> modules, ModulePlacer.PlacementLayout layout, int maskPattern, Span<byte> output)
    {
        // The walk below indexes both without bounds checks; these two are what make that safe.
        if ((uint)maskPattern >= RunMask.Patterns)
            throw new ArgumentOutOfRangeException(nameof(maskPattern), $"Mask pattern must be 0-7, got {maskPattern}");
        var size = layout.Size;
        if (modules.Length < size * size)
            throw new ArgumentException($"modules too small: required {size * size}, got {modules.Length}", nameof(modules));

        var ops = layout.Ops;
        var index = layout.Index;
        ref var m = ref MemoryMarshal.GetReference(modules);
        ref var dst = ref MemoryMarshal.GetReference(output);
        ref var mask = ref Unsafe.Add(ref MemoryMarshal.GetReference(RunMask.Table.AsSpan()), maskPattern * RunMask.PerPattern);

        var totalBits = output.Length * 8;
        ulong acc = 0; // low nbits are pending
        var nbits = 0;
        nuint o = 0;

        for (var p = 0; p < ops.Length; p++)
        {
            ref readonly var op = ref ops[p];
            if (op.Start >= totalBits)
                break;

            if (op.IsRun)
            {
                var rows = op.Count;
                // A run cut by the stream end may append one bit too many; it never completes a byte.
                if (op.Start + 2 * rows > totalBits)
                    rows = (totalBits - op.Start + 1) / 2;

                var step = op.RowStep;
                var up = step < 0;
                var y = op.Core / size;
                var x = op.Core - y * size + 1; // Core is the left module; phases follow the right one
                ref var t = ref Unsafe.Add(ref mask, ((up ? 0 : RunMask.ColumnPhases) + x % RunMask.ColumnPhases) * RunMask.RowPhases);
                var ym = y % RunMask.RowPhases;
                ref var s = ref Unsafe.Add(ref m, op.Core);

                var r = 0;
                for (; r + 4 <= rows; r += 4)
                {
                    // (left, right) pairs load with right in the high byte, which is stream order top down
                    var q = (ulong)LoadPair(ref s) << 48
                          | (ulong)LoadPair(ref Unsafe.Add(ref s, step)) << 32
                          | (ulong)LoadPair(ref Unsafe.Add(ref s, 2 * step)) << 16
                          | LoadPair(ref Unsafe.Add(ref s, 3 * step));
                    // non-zero byte -> 1, then the byte k from the top -> bit 7 - k
                    q = (((q & 0x7F7F7F7F7F7F7F7FUL) + 0x7F7F7F7F7F7F7F7FUL | q) & 0x8080808080808080UL) >> 7;
                    var b = (uint)(q * 0x0102040810204080UL >> 56) ^ Unsafe.Add(ref t, ym);
                    acc = acc << 8 | b;
                    Unsafe.Add(ref dst, o++) = (byte)(acc >> nbits);

                    s = ref Unsafe.Add(ref s, 4 * step);
                    ym += up ? RunMask.RowPhases - 4 : 4;
                    if (ym >= RunMask.RowPhases)
                        ym -= RunMask.RowPhases;
                }
                for (; r < rows; r++)
                {
                    var pair = (uint)LoadPair(ref s);
                    var two = (pair >> 8 != 0 ? 2u : 0u) | ((pair & 0xFF) != 0 ? 1u : 0u);
                    // the entry's top two bits are its first row
                    acc = acc << 2 | (two ^ (uint)Unsafe.Add(ref t, ym) >> 6);
                    nbits += 2;
                    if (nbits >= 8)
                    {
                        nbits -= 8;
                        Unsafe.Add(ref dst, o++) = (byte)(acc >> nbits);
                    }

                    s = ref Unsafe.Add(ref s, step);
                    ym += up ? RunMask.RowPhases - 1 : 1;
                    if (ym >= RunMask.RowPhases)
                        ym -= RunMask.RowPhases;
                }
            }
            else
            {
                var end = Math.Min(op.Start + op.Count, totalBits);
                for (var i = op.Start; i < end; i++)
                {
                    int core = index[i];
                    var y = core / size;
                    var dark = Unsafe.Add(ref m, core) != 0;
                    acc = acc << 1 | (dark != GetMaskBit(maskPattern, y, core - y * size) ? 1u : 0u);
                    if (++nbits == 8)
                    {
                        nbits = 0;
                        Unsafe.Add(ref dst, o++) = (byte)acc;
                    }
                }
            }
        }

        // remainder bits, when the output has room for them
        if (nbits > 0 && o < (nuint)output.Length)
            Unsafe.Add(ref dst, o) = (byte)(acc << (8 - nbits));
    }

    /// <summary>The run mask byte for a run whose current row is <paramref name="y"/> and whose right column is <paramref name="x"/>.</summary>
    internal static byte GetRunMask(int maskPattern, bool up, int x, int y)
        => RunMask.Table[((maskPattern * 2 + (up ? 0 : 1)) * RunMask.ColumnPhases + x % RunMask.ColumnPhases) * RunMask.RowPhases + y % RunMask.RowPhases];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort LoadPair(ref byte left)
    {
        var pair = Unsafe.ReadUnaligned<ushort>(ref left);
        return BitConverter.IsLittleEndian ? pair : BinaryPrimitives.ReverseEndianness(pair);
    }

    /// <summary>
    /// The eight mask bits under one output byte of a run, for every mask pattern.
    /// Every predicate of ISO/IEC 18004 7.8.2 repeats every 12 rows and every 6 columns, so those bits depend on the pattern, the walk direction, the column phase and the row phase only: one table serves all 40 versions.
    /// </summary>
    /// <remarks>
    /// Entry [((pattern · 2 + direction) · 6 + x mod 6) · 12 + y mod 12], direction 0 = up: rows y, y ± 1, y ± 2, y ± 3, each as (right = x, left = x − 1), MSB first.
    /// </remarks>
    private static class RunMask
    {
        internal const int Patterns = 8;
        internal const int ColumnPhases = 6;
        internal const int RowPhases = 12;
        internal const int PerPattern = 2 * ColumnPhases * RowPhases;

        internal static readonly byte[] Table = Build();

        private static byte[] Build()
        {
            var table = new byte[Patterns * PerPattern];
            var i = 0;
            for (var pattern = 0; pattern < Patterns; pattern++)
            {
                for (var direction = 0; direction < 2; direction++)
                {
                    var step = direction == 0 ? -1 : 1;
                    for (var xm = 0; xm < ColumnPhases; xm++)
                    {
                        for (var ym = 0; ym < RowPhases; ym++)
                        {
                            // one period up and across keeps row and column non-negative without moving a phase
                            var x = xm + ColumnPhases;
                            var bits = 0;
                            for (var k = 0; k < 4; k++)
                            {
                                var y = ym + RowPhases + step * k;
                                bits = bits << 1 | (GetMaskBit(pattern, y, x) ? 1 : 0);
                                bits = bits << 1 | (GetMaskBit(pattern, y, x - 1) ? 1 : 0);
                            }
                            table[i++] = (byte)bits;
                        }
                    }
                }
            }
            return table;
        }
    }
}
