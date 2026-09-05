using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FeatherQR.Internals.BinaryEncoders;

/// <summary>
/// Reed-Solomon block interleaving (ISO/IEC 18004 7.6, ISO/IEC 23941 7.6): data
/// codewords round-robin across blocks, then ECC codewords round-robin, then zero
/// remainder bits. Shared across symbologies: the layout depends only on the
/// <see cref="ECCInfo"/> block structure, never on a symbology-specific version
/// (Standard QR and rMQR interleave identically; Micro QR has one block and no
/// interleaving stage). Lifted from Internals.StandardQR when rMQR became the
/// second consumer.
/// </summary>
internal static class BinaryInterleaver
{
    /// <summary>
    /// Calculates the final interleaved data capacity including data codewords, ECC codewords, and remainder bits.
    /// </summary>
    /// <param name="data">Array of data codewords to interleave.</param>
    /// <param name="ecc">Array of ECC codewords to interleave.</param>
    /// <param name="output">Output buffer for interleaved bits.</param>
    /// <param name="eccInfo">ECC information for the QR code version.</param>
    /// <returns></returns>
    public static void InterleaveCodewords(ReadOnlySpan<byte> data, ReadOnlySpan<byte> ecc, Span<byte> output, in ECCInfo eccInfo)
    {
        // The interleaving process distributes data across blocks to improve error resilience:
        // 1. Data codewords are interleaved in round-robin order from all blocks
        // 2. ECC codewords are interleaved in round-robin order from all blocks
        // 3. Final sequence follows QR code specification for optimal error correction
        //
        // Writing the output sequentially makes the source access strided (block length);
        // the reverse (sequential reads, strided writes) measured consistently slower in benchmarks.
        //
        // Image
        // data:  [D1 D2 D3 | D4 D5 D6]
        // ↑ stride=3, row0: D1, D4
        // ↑ stride=3, row1: D2, D5
        // ↑ stride=3, row2: D3, D6
        // output: D1 D4 D2 D5 D3 D6 | (Same for ECC)

        var g1Blocks = eccInfo.BlocksInGroup1;
        var g1Cw = eccInfo.CodewordsInGroup1;
        var g2Blocks = eccInfo.BlocksInGroup2;
        var g2Cw = g2Blocks > 0 ? eccInfo.CodewordsInGroup2 : 0;
        var group2Offset = g1Blocks * g1Cw;
        var totalBlocks = g1Blocks + g2Blocks;
        var eccPerBlock = eccInfo.ECCPerBlock;

        // Upfront validation so the ref arithmetic below cannot leave the buffers
        var dataLen = group2Offset + g2Blocks * g2Cw;
        var eccLen = totalBlocks * eccPerBlock;
        if (data.Length < dataLen)
            throw new ArgumentException($"Data buffer too small: required {dataLen}, got {data.Length}", nameof(data));
        if (ecc.Length < eccLen)
            throw new ArgumentException($"ECC buffer too small: required {eccLen}, got {ecc.Length}", nameof(ecc));
        if (output.Length < dataLen + eccLen)
            throw new ArgumentException($"Output buffer too small: required {dataLen + eccLen}, got {output.Length}", nameof(output));

        // Zero the remainder-bits tail (at most 1 byte for CalculateInterleavedSize-sized
        // buffers) so the output is deterministic even if the caller passes an
        // uninitialized buffer (SkipLocalsInit stackalloc, pooled arrays). The remainder
        // bits are consumed by ModulePlacer.PlaceDataWords and must be 0 per ISO/IEC 18004.
        if (output.Length > dataLen + eccLen)
            output.Slice(dataLen + eccLen).Clear();

        // Single block: interleaving is the identity permutation (versions 1-2 at most
        // ECC levels, v5L, ...), two sequential copies, ~5x faster than the loop
        if (totalBlocks == 1)
        {
            data.Slice(0, dataLen).CopyTo(output);
            ecc.Slice(0, eccLen).CopyTo(output.Slice(dataLen));
            return;
        }

        ref var src = ref MemoryMarshal.GetReference(data);
        ref var dst = ref MemoryMarshal.GetReference(output);

        // Rows where every block contributes: group 1 blocks then group 2 blocks,
        // walking each column with an additive stride (the block length)
        var common = g2Blocks > 0 ? Math.Min(g1Cw, g2Cw) : g1Cw;
        var i = 0;
        for (; i < common; i++)
        {
            var idx = i;
            for (var b = 0; b < g1Blocks; b++)
            {
                dst = Unsafe.Add(ref src, idx);
                dst = ref Unsafe.Add(ref dst, 1);
                idx += g1Cw;
            }
            idx = group2Offset + i;
            for (var b = 0; b < g2Blocks; b++)
            {
                dst = Unsafe.Add(ref src, idx);
                dst = ref Unsafe.Add(ref dst, 1);
                idx += g2Cw;
            }
        }

        // Tail rows: only the group with longer blocks still has codewords
        // (in real QR patterns g2Cw == g1Cw + 1, so at most one row remains)
        for (; i < g1Cw; i++)
        {
            var idx = i;
            for (var b = 0; b < g1Blocks; b++)
            {
                dst = Unsafe.Add(ref src, idx);
                dst = ref Unsafe.Add(ref dst, 1);
                idx += g1Cw;
            }
        }
        for (var t = common; t < g2Cw; t++)
        {
            var idx = group2Offset + t;
            for (var b = 0; b < g2Blocks; b++)
            {
                dst = Unsafe.Add(ref src, idx);
                dst = ref Unsafe.Add(ref dst, 1);
                idx += g2Cw;
            }
        }

        // Interleave ECC codewords: all blocks have exactly eccPerBlock codewords
        ref var eccSrc = ref MemoryMarshal.GetReference(ecc);
        for (var e = 0; e < eccPerBlock; e++)
        {
            var idx = e;
            for (var b = 0; b < totalBlocks; b++)
            {
                dst = Unsafe.Add(ref eccSrc, idx);
                dst = ref Unsafe.Add(ref dst, 1);
                idx += eccPerBlock;
            }
        }

        // Remainder bits were zeroed upfront (tail Clear after validation).
    }

    /// <summary>
    /// Calculates the required buffer size for interleaved data.
    /// </summary>
    /// <param name="eccInfo">ECC information for the QR code version.</param>
    /// <param name="remainderBits">Symbology-specific remainder bits after the last codeword (0-7).</param>
    /// <remarks>
    /// Buffer includes:
    /// - Data codewords (TotalDataCodewords bytes)
    /// - ECC codewords (ECCPerBlock * TotalBlocks bytes)
    /// - Remainder bits space (0-7 bits, rounded up to nearest byte)
    /// </remarks>
    /// <returns></returns>
    public static int CalculateInterleavedSize(in ECCInfo eccInfo, int remainderBits)
    {
        // -----------------------------------------------------
        // QR Code Interleaved data structure (ISO/IEC 18004):
        // -----------------------------------------------------
        //
        // ┌──────────────────────────────────────────────────┐
        // │ Interleaved Data Codewords                       │
        // │ (TotalDataCodewords × 8 bits)                    │
        // ├──────────────────────────────────────────────────┤
        // │ Interleaved ECC Codewords                        │
        // │ (ECCPerBlock × TotalBlocks × 8 bits)             │
        // ├──────────────────────────────────────────────────┤
        // │ Remainder Bits                                   │
        // │ (0-7 bits, version dependent)                    │
        // └──────────────────────────────────────────────────┘
        //
        // -----------------------------------------------------
        // // ECCInfo for Version 5, Q
        // eccInfo = {
        //     Version = 5,
        //     ErrorCorrectionLevel = Q,
        //     TotalDataCodewords = 62,       // Data capacity
        //     ECCPerBlock = 18,              // ECC Word count per block
        //     BlocksInGroup1 = 2,            // Group 1 block count
        //     CodewordsInGroup1 = 15,        // Group 1 data word count per block
        //     BlocksInGroup2 = 2,            // Group 2 block count
        //     CodewordsInGroup2 = 16,        // Group 2 data word count per block
        // };
        //
        // // Calculation:
        // var dataCodewordsBits = 62 * 8 = 496 bits
        // var eccCodewordsBits = 18 * (2 + 2) * 8 = 576 bits
        // var remainderBits = GetRemainderBits(5) = 7 bits
        //
        // Total = (496 + 576 + 7 + 7) / 8 = 135 bytes
        // -----------------------------------------------------

        var totalDataBytes = eccInfo.TotalDataCodewords;
        var totalEccBytes = (eccInfo.BlocksInGroup1 + eccInfo.BlocksInGroup2) * eccInfo.ECCPerBlock;

        // Calculate total size in bits, then round up to bytes
        var totalBits = totalDataBytes * 8 + totalEccBytes * 8 + remainderBits;
        return (totalBits + 7) / 8;
    }

}
