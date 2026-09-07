using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FeatherQR.Internals.MicroQR;

/// <summary>
/// Places Micro QR function patterns, data modules, masking and format information into a byte-per-module core matrix (ISO/IEC 18004).
/// </summary>
/// <remarks>
/// Micro QR layout: a single finder pattern at the top-left, separators along its right/bottom edges, timing patterns along row 0 and column 0, and 15 format modules adjacent to the finder.
/// The entire function region reduces to the predicate <c>row == 0 || col == 0 || (row &lt;= 8 &amp;&amp; col &lt;= 8)</c>: no alignment patterns, no dark module, no version information.
/// The fused fast-path pipeline lives in MicroQRModulePlacer.PlaceSymbol.cs; the per-module implementations in this file are the readable reference.
/// </remarks>
internal static partial class MicroQRModulePlacer
{
    /// <summary>True when (row, col) belongs to a function pattern or reserved area.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFunctionModule(int row, int col) => row == 0 || col == 0 || (row <= 8 && col <= 8);

    /// <summary>
    /// Draws the finder pattern and timing patterns into a zeroed core matrix.
    /// Separators and reserved format modules stay light (zero) at this stage.
    /// </summary>
    public static void PlaceFunctionModules(Span<byte> matrix, int size)
    {
        // Finder pattern (7×7 at top-left): dark outer ring + dark 3×3 center.
        for (var i = 0; i < 7; i++)
        {
            matrix[i] = 1;                  // row 0
            matrix[6 * size + i] = 1;       // row 6
            matrix[i * size] = 1;           // col 0
            matrix[i * size + 6] = 1;       // col 6
        }
        for (var row = 2; row <= 4; row++)
        {
            for (var col = 2; col <= 4; col++)
            {
                matrix[row * size + col] = 1;
            }
        }

        // Timing patterns: row 0 and column 0 from index 8 to the edge, dark on
        // even coordinates (Micro QR runs timing along the symbol edges instead
        // of Standard QR's row/column 6).
        for (var i = 8; i < size; i += 2)
        {
            matrix[i] = 1;
            matrix[i * size] = 1;
        }
    }

    /// <summary>
    /// Places the codeword bit stream into the data region using the two-column zigzag (bottom-right start, upward first, alternating per column pair).
    /// </summary>
    /// <param name="matrix">Core matrix with function patterns already placed.</param>
    /// <param name="size">Core side length in modules.</param>
    /// <param name="dataCodewords">Data codewords including padding.</param>
    /// <param name="eccCodewords">Error correction codewords.</param>
    /// <param name="dataBitCount">Number of DATA bits to emit, for M1/M3 this stops after the high nibble of the final (4-bit) data codeword; its forced-zero low nibble is never placed.</param>
    public static void PlaceDataCodewords(Span<byte> matrix, int size, ReadOnlySpan<byte> dataCodewords, ReadOnlySpan<byte> eccCodewords, int dataBitCount)
    {
        var totalBits = dataBitCount + eccCodewords.Length * 8;
        var bitIndex = 0;
        var upward = true;

        // Column pairs (size-1, size-2) … (2, 1); column 0 is all function modules.
        for (var right = size - 1; right >= 2; right -= 2)
        {
            for (var step = 0; step < size; step++)
            {
                var row = upward ? size - 1 - step : step;
                var rowOffset = row * size;

                for (var side = 0; side < 2; side++)
                {
                    var col = right - side;
                    if (IsFunctionModule(row, col))
                        continue;

                    if (bitIndex < totalBits)
                    {
                        matrix[rowOffset + col] = GetStreamBit(bitIndex, dataCodewords, eccCodewords, dataBitCount);
                        bitIndex++;
                    }
                }
            }
            upward = !upward;
        }

        Debug.Assert(bitIndex == totalBits, $"data module count mismatch: placed {bitIndex}, stream {totalBits}");
    }

    /// <summary>
    /// Reads bit <paramref name="bitIndex"/> of the transmission stream: data bits first (MSB-first per codeword; naturally covers only the high nibble of a final half codeword because <paramref name="dataBitCount"/> stops there), then ECC bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte GetStreamBit(int bitIndex, ReadOnlySpan<byte> dataCodewords, ReadOnlySpan<byte> eccCodewords, int dataBitCount)
    {
        if (bitIndex < dataBitCount)
        {
            return (byte)((dataCodewords[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1);
        }

        var eccBit = bitIndex - dataBitCount;
        return (byte)((eccCodewords[eccBit >> 3] >> (7 - (eccBit & 7))) & 1);
    }

    /// <summary>
    /// Scores the four Micro QR mask patterns and applies the winner to the data region.
    /// Returns the selected mask pattern (0-3).
    /// </summary>
    /// <remarks>
    /// ISO/IEC 18004 Micro QR evaluation: count dark modules on the right edge (SUM1) and lower edge (SUM2), both excluding row/column 0; score is <c>min·16 + max</c> and the HIGHEST score wins (more dark edge modules make the symbol easier to distinguish from its quiet zone).
    /// Ties keep the lowest pattern number.
    /// Scoring reads only the two edges with the candidate mask applied on the fly, so no trial matrices are materialized.
    /// </remarks>
    public static int SelectAndApplyMask(Span<byte> matrix, int size)
    {
        var bestMask = 0;
        var bestScore = -1;

        for (var mask = 0; mask < 4; mask++)
        {
            var sum1 = 0; // right edge: column size-1, rows 1..size-1
            var sum2 = 0; // lower edge: row size-1, columns 1..size-1
            var lastRowOffset = (size - 1) * size;

            for (var i = 1; i < size; i++)
            {
                if ((matrix[i * size + size - 1] != 0) ^ GetMaskBit(mask, i, size - 1))
                {
                    sum1++;
                }
                if ((matrix[lastRowOffset + i] != 0) ^ GetMaskBit(mask, size - 1, i))
                {
                    sum2++;
                }
            }

            var score = sum1 <= sum2 ? sum1 * 16 + sum2 : sum2 * 16 + sum1;
            if (score > bestScore)
            {
                bestScore = score;
                bestMask = mask;
            }
        }

        // Apply the winning mask to every data module.
        for (var row = 1; row < size; row++)
        {
            var rowOffset = row * size;
            var colStart = row <= 8 ? 9 : 1;
            for (var col = colStart; col < size; col++)
            {
                if (GetMaskBit(bestMask, row, col))
                {
                    matrix[rowOffset + col] ^= 1;
                }
            }
        }

        return bestMask;
    }

    /// <summary>
    /// Micro QR mask conditions (ISO/IEC 18004 Table 10; they correspond to Standard QR patterns 1, 4, 6 and 7 respectively).
    /// True = flip the module.
    /// Shared with the decoder (<see cref="MicroQRMatrixDecoder"/>) so both sides always agree on the mask templates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool GetMaskBit(int mask, int row, int col) => mask switch
    {
        0 => row % 2 == 0,
        1 => (row / 2 + col / 3) % 2 == 0,
        2 => (row * col % 2 + row * col % 3) % 2 == 0,
        _ => ((row + col) % 2 + row * col % 3) % 2 == 0,
    };

    /// <summary>
    /// Places the 15 format information bits: bit 14 … 8 along row 8 columns 1-7, bit 7 at (8,8), bits 6 … 0 down column 8 rows 7-1 (ISO/IEC 18004).
    /// </summary>
    public static void PlaceFormat(Span<byte> matrix, int size, ushort formatBits)
    {
        var bit = 14;
        var row8Offset = 8 * size;
        for (var col = 1; col <= 8; col++, bit--)
        {
            matrix[row8Offset + col] = (byte)((formatBits >> bit) & 1);
        }
        for (var row = 7; row >= 1; row--, bit--)
        {
            matrix[row * size + 8] = (byte)((formatBits >> bit) & 1);
        }
    }
}
