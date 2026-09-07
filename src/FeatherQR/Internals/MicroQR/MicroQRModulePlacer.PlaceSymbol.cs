using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace FeatherQR.Internals.MicroQR;

/// <summary>
/// Fused fast-path placement pipeline for Micro QR (<see cref="PlaceSymbol"/>): function patterns, data placement, mask selection/application and format information in one pass over bit-packed rows, unpacked to the byte matrix once at the end.
///
/// Produces matrices byte-identical to the per-module reference methods in MicroQRModulePlacer.cs (guarded by MicroQRModulePlacerParityTest).
/// Four runtime tiers: BMI2+AVX2 (placement as a static per-row PEXT/PDEP permutation, 32-module unpack; gated on fast-PEXT hardware, Intel or AMD Zen 3+), SSSE3 and ARM64 NEON (serial packed placement, 16-module unpack sharing one pipeline, only the bit-expand idiom differs), and portable scalar (SWAR spreads).
/// Measured 3.6-8.4x over the reference across M1-M4 on x64, zero allocations (see the micro-optimization findings log):
/// <list type="bullet">
/// <item>The transmission stream is at most 192 bits (M4), so it is packed once
/// into three ulongs and consumed MSB-first from a register accumulator instead of per-bit indexed loads with a data/ecc branch.</item>
/// <item>Column pairs are (even, odd) and never straddle the finder boundary:
/// <c>right &gt;= 10</c> frees rows 1..size-1, <c>right &lt;= 8</c> frees rows 9..size-1, no per-module function-region predicate, and the stream fills the free modules exactly (ISO capacity tables, validated up front) so no remaining-bits guard is needed.
/// Every segment's row count is even for all four sizes, so placement runs 2 rows (4 stream bits) per iteration.</item>
/// <item>Mask scoring reads only the two symbol edges (ISO 7.8.3), taken
/// straight from the packed rows; every candidate's SUM1/SUM2 is one XOR + popcount against a static per-(size, mask) edge table.</item>
/// <item>The mask apply (a 12-row-periodic template AND the data-region mask)
/// is fused into the unpack pass instead of a separate XOR pass over the rows; format bits are poked into the packed rows beforehand and live entirely outside the data-region mask, so the fused XOR cannot touch them.</item>
/// <item>The unpack expands 16 modules per step with the SSSE3
/// broadcast/shuffle/compare idiom when available, falling back to scalar SWAR spreads (8 modules per step) otherwise; a single code path serves all four sizes (the scalar phase's size-11 byte-domain dispatch lost its advantage once the unpack was vectorized and the placement unrolled, measured).</item>
/// <item>All tables are small static arrays built by ordinary code from the
/// reference implementations (NativeAOT/trimming-safe).</item>
/// </list>
/// </summary>
internal static partial class MicroQRModulePlacer
{
    /// <summary>
    /// Runs the full placement pipeline into a zeroed core matrix: function patterns, data/ECC codeword placement, mask selection and application, and format information.
    /// Returns the selected mask pattern (0-3).
    /// </summary>
    /// <param name="matrix">Zeroed core matrix, at least size*size bytes.</param>
    /// <param name="size">Core side length in modules (11/13/15/17).</param>
    /// <param name="dataCodewords">Data codewords including padding.</param>
    /// <param name="eccCodewords">Error correction codewords.</param>
    /// <param name="dataBitCount">Number of DATA bits to emit, for M1/M3 this stops after the high nibble of the final (4-bit) data codeword; its forced-zero low nibble is never placed.</param>
    /// <param name="version">Micro QR version (drives the format information).</param>
    /// <param name="eccLevel">ECC level (drives the format information).</param>
    /// <param name="forcedMask">Pinned mask pattern (0-3), or -1 for edge-score selection. The fused pipeline is mask-index-driven throughout (templates, format bits), so a pinned pattern rides the same tiers.</param>
    public static int PlaceSymbol(Span<byte> matrix, int size, ReadOnlySpan<byte> dataCodewords, ReadOnlySpan<byte> eccCodewords, int dataBitCount, MicroQRVersion version, MicroQREccLevel eccLevel, int forcedMask = -1)
    {
        ValidateArguments(matrix, size, dataCodewords, eccCodewords, dataBitCount, forcedMask);

        Span<ulong> stream = stackalloc ulong[3];
        PackStream(stream, dataCodewords, eccCodewords, dataBitCount);

#if NET8_0_OR_GREATER
        if (Avx2.IsSupported && HardwareCapabilities.HasFastPext)
        {
            return PlaceCoreBmi2(matrix, size, stream, version, eccLevel, forcedMask);
        }
        if (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported)
        {
            return PlaceCoreVector(matrix, size, stream, version, eccLevel, forcedMask);
        }
#endif
        return PlaceCoreScalar(matrix, size, stream, version, eccLevel, forcedMask);
    }

    /// <summary>
    /// Scalar-unpack variant of <see cref="PlaceSymbol"/>, the code path taken at runtime when neither SSSE3 nor NEON is available, exposed as a named entry point so parity tests exercise it on SIMD-capable machines too.
    /// </summary>
    internal static int PlaceSymbolScalar(Span<byte> matrix, int size, ReadOnlySpan<byte> dataCodewords, ReadOnlySpan<byte> eccCodewords, int dataBitCount, MicroQRVersion version, MicroQREccLevel eccLevel, int forcedMask = -1)
    {
        ValidateArguments(matrix, size, dataCodewords, eccCodewords, dataBitCount, forcedMask);

        Span<ulong> stream = stackalloc ulong[3];
        PackStream(stream, dataCodewords, eccCodewords, dataBitCount);

        return PlaceCoreScalar(matrix, size, stream, version, eccLevel, forcedMask);
    }

#if NET8_0_OR_GREATER
    /// <summary>
    /// BMI2+AVX2 fast-path variant of <see cref="PlaceSymbol"/>, exposed as a named entry point for parity tests (the public dispatch additionally requires <see cref="HardwareCapabilities.HasFastPext"/>).
    /// </summary>
    internal static int PlaceSymbolBmi2(Span<byte> matrix, int size, ReadOnlySpan<byte> dataCodewords, ReadOnlySpan<byte> eccCodewords, int dataBitCount, MicroQRVersion version, MicroQREccLevel eccLevel, int forcedMask = -1)
    {
        ValidateArguments(matrix, size, dataCodewords, eccCodewords, dataBitCount, forcedMask);

        Span<ulong> stream = stackalloc ulong[3];
        PackStream(stream, dataCodewords, eccCodewords, dataBitCount);

        return PlaceCoreBmi2(matrix, size, stream, version, eccLevel, forcedMask);
    }

    /// <summary>
    /// SSSE3 mid-tier variant of <see cref="PlaceSymbol"/>, the code path taken at runtime when BMI2/AVX2 (or fast PEXT) are absent, exposed as a named entry point so it stays covered on machines whose dispatch prefers the BMI2 kernel.
    /// </summary>
    internal static int PlaceSymbolSsse3(Span<byte> matrix, int size, ReadOnlySpan<byte> dataCodewords, ReadOnlySpan<byte> eccCodewords, int dataBitCount, MicroQRVersion version, MicroQREccLevel eccLevel, int forcedMask = -1)
    {
        ValidateArguments(matrix, size, dataCodewords, eccCodewords, dataBitCount, forcedMask);

        Span<ulong> stream = stackalloc ulong[3];
        PackStream(stream, dataCodewords, eccCodewords, dataBitCount);

        return PlaceCoreVector(matrix, size, stream, version, eccLevel, forcedMask);
    }

    /// <summary>
    /// ARM64 NEON mid-tier variant of <see cref="PlaceSymbol"/>, the code path taken at runtime on ARM64 (same <see cref="PlaceCoreVector"/> pipeline as the SSSE3 tier; only the 16-module unpack idiom differs inside <see cref="WriteExpand16"/>), exposed as a named entry point for parity tests.
    /// Caller must ensure AdvSimd.Arm64 support.
    /// </summary>
    internal static int PlaceSymbolAdvSimd(Span<byte> matrix, int size, ReadOnlySpan<byte> dataCodewords, ReadOnlySpan<byte> eccCodewords, int dataBitCount, MicroQRVersion version, MicroQREccLevel eccLevel, int forcedMask = -1)
    {
        ValidateArguments(matrix, size, dataCodewords, eccCodewords, dataBitCount, forcedMask);

        Span<ulong> stream = stackalloc ulong[3];
        PackStream(stream, dataCodewords, eccCodewords, dataBitCount);

        return PlaceCoreVector(matrix, size, stream, version, eccLevel, forcedMask);
    }
#endif

    private static void ValidateArguments(Span<byte> matrix, int size, ReadOnlySpan<byte> dataCodewords, ReadOnlySpan<byte> eccCodewords, int dataBitCount, int forcedMask)
    {
        // The fast paths below index by arithmetic the JIT cannot bounds-prove
        // (flat zigzag indices, 8/16-byte unaligned unpack writes), so the
        // whole traversal domain is validated here instead of per access.
        if (MicroQRConstants.VersionFromSize(size) == 0)
            throw new ArgumentOutOfRangeException(nameof(size), $"size must be a Micro QR size (11/13/15/17), got {size}");
        // 4-7 would merge into the (symbolNumber << 2) | mask format-table index
        // and silently write another symbol number's format information.
        if (forcedMask is < -1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(forcedMask), $"Mask pattern must be 0-3, or -1 for automatic selection, but was {forcedMask}");
        if (matrix.Length < size * size)
            throw new ArgumentException($"matrix too small: required {size * size}, got {matrix.Length}", nameof(matrix));
        if (dataBitCount < 0 || dataCodewords.Length * 8 < dataBitCount)
            throw new ArgumentException($"dataCodewords too small: required {dataBitCount} bits, got {dataCodewords.Length * 8}", nameof(dataCodewords));

        // Free data modules: size^2 minus row 0 (size), the rest of column 0
        // (size-1) and rows 1..8 x cols 1..8 (64). The ISO capacity tables make
        // the stream fill this exactly; the placement loops rely on it.
        var totalBits = dataBitCount + eccCodewords.Length * 8;
        var freeModules = size * size - 2 * size - 63;
        if (totalBits != freeModules)
            throw new ArgumentException($"stream length mismatch: {totalBits} bits for {freeModules} free modules (size {size})", nameof(eccCodewords));
    }

    /// <summary>
    /// Packs the transmission stream (data bits MSB-first, cut at <paramref name="dataBitCount"/>, then ECC bits) into three MSB-aligned ulongs.
    /// The stream is byte-aligned except after an M1/M3 half codeword, where the remaining bytes are merged with a 4-bit carry.
    /// </summary>
    private static void PackStream(Span<ulong> stream, ReadOnlySpan<byte> dataCodewords, ReadOnlySpan<byte> eccCodewords, int dataBitCount)
    {
        Span<byte> tmp = stackalloc byte[24];
        tmp.Clear();
        var fullDataBytes = dataBitCount >> 3;
        dataCodewords.Slice(0, fullDataBytes).CopyTo(tmp);
        if ((dataBitCount & 7) != 0)
        {
            var t = fullDataBytes;
            var carry = (byte)(dataCodewords[fullDataBytes] & 0xF0);
            for (var i = 0; i < eccCodewords.Length; i++)
            {
                tmp[t++] = (byte)(carry | (eccCodewords[i] >> 4));
                carry = (byte)(eccCodewords[i] << 4);
            }
            tmp[t] = carry;
        }
        else
        {
            eccCodewords.CopyTo(tmp.Slice(fullDataBytes));
        }
        stream[0] = BinaryPrimitives.ReadUInt64BigEndian(tmp);
        stream[1] = BinaryPrimitives.ReadUInt64BigEndian(tmp.Slice(8));
        stream[2] = BinaryPrimitives.ReadUInt64BigEndian(tmp.Slice(16));
    }

    // ---------------------------------------------------------------
    // Packed pipeline (all sizes)
    // ---------------------------------------------------------------

    /// <summary>
    /// Runs placement, scoring, format poking on packed rows and returns the selected mask; the caller-selected unpack pass then materializes the byte matrix with the mask template XORed in on the fly.
    /// </summary>
    private static int BuildPackedRows(Span<ulong> rows, int size, ReadOnlySpan<ulong> stream, MicroQRVersion version, MicroQREccLevel eccLevel, int forcedMask)
    {
        FuncPackedRows(size).CopyTo(rows);

        var acc = stream[0];
        var w1 = stream[1];
        var w2 = stream[2];
        var accBits = 64;

        // Data placement, 2 rows (4 stream bits) per iteration: bits land at
        // columns (right, right-1) of two consecutive rows. Segment row counts
        // (size-1 and size-9) are even for every Micro QR size, and 64 % 4 == 0
        // keeps the refill boundary exact.
        var upward = true;
        for (var right = size - 1; right >= 2; right -= 2)
        {
            var rowStart = right >= 10 ? 1 : 9;
            var pairs = (size - rowStart) >> 1;
            var row = upward ? size - 1 : rowStart;
            var step = upward ? -1 : 1;
            var shift = right - 1;

            for (var i = 0; i < pairs; i++)
            {
                if (accBits == 0)
                {
                    acc = w1;
                    w1 = w2;
                    accBits = 64;
                }
                var quad = (acc >> 60) & 0xF;
                rows[row] |= (quad >> 2) << shift;
                rows[row + step] |= (quad & 3) << shift;
                acc <<= 4;
                accBits -= 4;
                row += 2 * step;
            }
            upward = !upward;
        }

        // Scoring: edges straight out of the packed rows (bit 0 = the column-0
        // function module, excluded from both edge sums).
        var last = size - 1;
        ulong colDark = 0;
        for (var i = 1; i < size; i++)
        {
            colDark |= ((rows[i] >> last) & 1) << i;
        }
        var rowDark = rows[last] & ~1ul;

        var mask = forcedMask >= 0 ? forcedMask : SelectMaskFromEdges(colDark, rowDark, size);

        // Format information: row 8 cols 1..8 carry bits 14..7, col 8 rows 7..1
        // carry bits 6..0 (same coordinates as PlaceFormat). These modules lie
        // outside the data-region mask, so the fused apply cannot flip them.
        // Row 8's cols 1..8 hold format bits 14..7 in descending order, the
        // bit-reversed high byte of (formatBits >> 7) shifted to bit 1, one
        // masked insert instead of eight.
        var formatBits = _formatBitsTable[(MicroQRConstants.GetSymbolNumber(version, eccLevel) << 2) | mask];
        rows[8] = (rows[8] & ~0x1FEul) | ((ulong)_reverseByte[(formatBits >> 7) & 0xFF] << 1);
        for (var row = 7; row >= 1; row--)
        {
            var bit = (ulong)((formatBits >> (row - 1)) & 1);
            rows[row] = (rows[row] & ~(1ul << 8)) | (bit << 8);
        }

        return mask;
    }

    /// <summary>Masked mask-template row for the fused apply: zero for row 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong MaskDelta(int mask, int row, int size)
    {
        if (row == 0)
        {
            return 0;
        }
        var colStart = row <= 8 ? 9 : 1;
        var allowed = ((1ul << size) - 1) & ~((1ul << colStart) - 1);
        var tplRow = row < 12 ? row : row - 12;
        return _maskTemplates12[mask * 12 + tplRow] & allowed;
    }

    private static int PlaceCoreScalar(Span<byte> matrix, int size, ReadOnlySpan<ulong> stream, MicroQRVersion version, MicroQREccLevel eccLevel, int forcedMask)
    {
        Span<ulong> rows = stackalloc ulong[17];
        rows = rows.Slice(0, size);
        var mask = BuildPackedRows(rows, size, stream, version, eccLevel, forcedMask);

        // Unpack with the mask applied on the fly: full 8-byte write steps; a
        // tail of >= 4 bytes becomes one overlapped spread ending at the row
        // edge (write-mode overlap is idempotent, unlike XOR), a tail of <= 3
        // bytes is cheaper as scalar stores. ValidateArguments guaranteed
        // matrix.Length >= size*size, so every write stays inside the matrix.
        ref var buf = ref MemoryMarshal.GetReference(matrix);
        var rowOffset = 0;
        for (var y = 0; y < size; y++, rowOffset += size)
        {
            var bits = rows[y] ^ MaskDelta(mask, y, size);
            var c = 0;
            for (; c + 8 <= size; c += 8)
            {
                WriteSpread8(ref Unsafe.Add(ref buf, rowOffset + c), bits >> c);
            }
            if (size - c >= 4)
            {
                var c2 = size - 8;
                WriteSpread8(ref Unsafe.Add(ref buf, rowOffset + c2), bits >> c2);
            }
            else
            {
                for (; c < size; c++)
                {
                    matrix[rowOffset + c] = (byte)((bits >> c) & 1);
                }
            }
        }

        return mask;
    }

#if NET8_0_OR_GREATER
    private static int PlaceCoreVector(Span<byte> matrix, int size, ReadOnlySpan<ulong> stream, MicroQRVersion version, MicroQREccLevel eccLevel, int forcedMask)
    {
        Span<ulong> rows = stackalloc ulong[17];
        rows = rows.Slice(0, size);
        var mask = BuildPackedRows(rows, size, stream, version, eccLevel, forcedMask);

        // Unpack with the mask applied on the fly, 16 modules per SIMD step
        // (SSSE3 or NEON, see WriteExpand16).
        // Rows 0..size-2 may overrun into the following row: bits >= size are
        // zero and rows unpack in ascending order, so the overwritten zeros are
        // immediately replaced by that row's own unpack. The last row (buffer
        // edge) uses the scalar-safe tail instead.
        ref var buf = ref MemoryMarshal.GetReference(matrix);
        var last = size - 1;
        var rowOffset = 0;
        for (var y = 0; y < last; y++, rowOffset += size)
        {
            var bits = rows[y] ^ MaskDelta(mask, y, size);
            for (var c = 0; c < size; c += 16)
            {
                WriteExpand16(ref Unsafe.Add(ref buf, rowOffset + c), (uint)((bits >> c) & 0xFFFF));
            }
        }
        {
            var bits = rows[last] ^ MaskDelta(mask, last, size);
            var c = 0;
            for (; c + 8 <= size; c += 8)
            {
                WriteSpread8(ref Unsafe.Add(ref buf, rowOffset + c), bits >> c);
            }
            if (size - c >= 4)
            {
                var c2 = size - 8;
                WriteSpread8(ref Unsafe.Add(ref buf, rowOffset + c2), bits >> c2);
            }
            else
            {
                for (; c < size; c++)
                {
                    matrix[rowOffset + c] = (byte)((bits >> c) & 1);
                }
            }
        }

        return mask;
    }

    /// <summary>
    /// Expands 16 bits to 16 (0/1) module bytes: broadcast the 16-bit lane, shuffle byte 0/1 across the halves, then per-lane bit test (byte k = 1 iff bit k set).
    /// Caller (via <see cref="PlaceCoreVector"/> dispatch) guarantees SSSE3 or AdvSimd.Arm64; IsSupported is a JIT constant so the untaken branch is eliminated. x86: PSHUFB + PAND + PCMPEQB, beat the GFNI bit-expand in the kernel benchmark loop (GFNI's matrix-operand preparation outweighs its single-instruction expand).
    /// ARM64: TBL + CMTST, CMTST (compare-test: 0xFF iff (a &amp; b) != 0) fuses the AND+CMEQ pair; x86 has no per-lane bit-test compare.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteExpand16(ref byte p, uint bits16)
    {
        var src = Vector128.Create((ushort)bits16).AsByte();
        var sel = Vector128.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1);
        var bitm = Vector128.Create((byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);
        Vector128<byte> ones;
        if (Ssse3.IsSupported)
        {
            var m = Ssse3.Shuffle(src, sel) & bitm;
            ones = Vector128.Equals(m, bitm) & Vector128.Create((byte)1);
        }
        else
        {
            var repl = AdvSimd.Arm64.VectorTableLookup(src, sel);
            ones = AdvSimd.CompareTest(repl, bitm) & Vector128.Create((byte)1);
        }
        ones.StoreUnsafe(ref p);
    }

    /// <summary>
    /// BMI2+AVX2 pipeline: placement is a static per-row PEXT/PDEP permutation of the packed stream words (the zigzag is a fixed bit permutation per size, so each row's data bits are gathered/scattered branch-free with no cross-row dependency), the stream word count is dispatched per size (M1 fits one word, M2 two), and the unpack expands 32 modules per AVX2 step.
    /// Measured over the SSSE3 pipeline: M4 -29%, M3 -22%, M2 -10%, M1 -5% (kernel benchmark rounds 8-11).
    /// </summary>
    private static int PlaceCoreBmi2(Span<byte> matrix, int size, ReadOnlySpan<ulong> stream, MicroQRVersion version, MicroQREccLevel eccLevel, int forcedMask)
    {
        var w0 = stream[0];
        var w1 = stream[1];
        var w2 = stream[2];

        Span<ulong> rows = stackalloc ulong[17];
        rows = rows.Slice(0, size);
        FuncPackedRows(size).CopyTo(rows);

        // Zero extract masks would make unused words no-ops, but the loads and
        // BMI ops are not free: sizes 11/13 skip the guaranteed-zero words.
        var tbl = _pextPlaceTables[(size - 11) >> 1];
        ref var te = ref MemoryMarshal.GetArrayDataReference(tbl);
        if (size >= 15)
        {
            for (var r = 1; r < size; r++)
            {
                ref var e = ref Unsafe.Add(ref te, r * 6);
                rows[r] |= Bmi2.X64.ParallelBitDeposit(Bmi2.X64.ParallelBitExtract(w0, e), Unsafe.Add(ref e, 1))
                         | Bmi2.X64.ParallelBitDeposit(Bmi2.X64.ParallelBitExtract(w1, Unsafe.Add(ref e, 2)), Unsafe.Add(ref e, 3))
                         | Bmi2.X64.ParallelBitDeposit(Bmi2.X64.ParallelBitExtract(w2, Unsafe.Add(ref e, 4)), Unsafe.Add(ref e, 5));
            }
        }
        else if (size == 13)
        {
            for (var r = 1; r < 13; r++)
            {
                ref var e = ref Unsafe.Add(ref te, r * 6);
                rows[r] |= Bmi2.X64.ParallelBitDeposit(Bmi2.X64.ParallelBitExtract(w0, e), Unsafe.Add(ref e, 1))
                         | Bmi2.X64.ParallelBitDeposit(Bmi2.X64.ParallelBitExtract(w1, Unsafe.Add(ref e, 2)), Unsafe.Add(ref e, 3));
            }
        }
        else
        {
            for (var r = 1; r < 11; r++)
            {
                ref var e = ref Unsafe.Add(ref te, r * 6);
                rows[r] |= Bmi2.X64.ParallelBitDeposit(Bmi2.X64.ParallelBitExtract(w0, e), Unsafe.Add(ref e, 1));
            }
        }

        // Scoring and format information, identical to BuildPackedRows.
        var last = size - 1;
        ulong colDark = 0;
        for (var i = 1; i < size; i++)
        {
            colDark |= ((rows[i] >> last) & 1) << i;
        }
        var rowDark = rows[last] & ~1ul;

        var mask = forcedMask >= 0 ? forcedMask : SelectMaskFromEdges(colDark, rowDark, size);

        var formatBits = _formatBitsTable[(MicroQRConstants.GetSymbolNumber(version, eccLevel) << 2) | mask];
        rows[8] = (rows[8] & ~0x1FEul) | ((ulong)_reverseByte[(formatBits >> 7) & 0xFF] << 1);
        for (var row = 7; row >= 1; row--)
        {
            var bit = (ulong)((formatBits >> (row - 1)) & 1);
            rows[row] = (rows[row] & ~(1ul << 8)) | (bit << 8);
        }

        // Unpack with the mask applied on the fly: one 32-module AVX2 step per
        // row while the store fits inside size*size (overrun into following
        // rows is self-healing in ascending order), 16-module steps for the
        // last rows, scalar-safe tail for the final row.
        ref var buf = ref MemoryMarshal.GetReference(matrix);
        var sizeSq = size * size;
        var rowOffset = 0;
        for (var y = 0; y < last; y++, rowOffset += size)
        {
            var bits = rows[y] ^ MaskDelta(mask, y, size);
            if (rowOffset + 32 <= sizeSq)
            {
                WriteExpand32(ref Unsafe.Add(ref buf, rowOffset), (uint)bits);
            }
            else
            {
                for (var c = 0; c < size; c += 16)
                {
                    WriteExpand16(ref Unsafe.Add(ref buf, rowOffset + c), (uint)((bits >> c) & 0xFFFF));
                }
            }
        }
        {
            var bits = rows[last] ^ MaskDelta(mask, last, size);
            var c = 0;
            for (; c + 8 <= size; c += 8)
            {
                WriteSpread8(ref Unsafe.Add(ref buf, rowOffset + c), bits >> c);
            }
            if (size - c >= 4)
            {
                var c2 = size - 8;
                WriteSpread8(ref Unsafe.Add(ref buf, rowOffset + c2), bits >> c2);
            }
            else
            {
                for (; c < size; c++)
                {
                    matrix[rowOffset + c] = (byte)((bits >> c) & 1);
                }
            }
        }

        return mask;
    }

    /// <summary>
    /// Expands 32 bits to 32 (0/1) module bytes with AVX2: broadcast the 32-bit lane to all uint lanes (each 128-bit lane then holds all four source bytes), in-lane shuffle spreads byte 0/1 across the low lane and byte 2/3 across the high lane, AND with per-byte bit masks, compare-equal.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteExpand32(ref byte p, uint bits32)
    {
        var src = Vector256.Create(bits32).AsByte();
        var sel = Vector256.Create(
            (byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
            2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3);
        var bitm = Vector256.Create(
            (byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128,
            1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);
        var m = Avx2.Shuffle(src, sel) & bitm;
        var ones = Vector256.Equals(m, bitm) & Vector256.Create((byte)1);
        ones.StoreUnsafe(ref p);
    }

    /// <summary>
    /// Per-(size, row) PEXT/PDEP masks, 6 ulongs per row: [extract0, deposit0, extract1, deposit1, extract2, deposit2].
    /// Built by walking the reference zigzag and recording, for every stream bit p placed at (row, col): word p&gt;&gt;6, source bit 63-(p&amp;63) (MSB-first stream), deposit bit col.
    /// Within a word, ascending source bit means descending stream position, which the zigzag maps to strictly ascending columns, so PEXT's packing order matches PDEP's deposit order (guarded by MicroQRModulePlacerParityTest).
    /// </summary>
    private static readonly ulong[][] _pextPlaceTables = BuildPextPlaceTables();

    private static ulong[][] BuildPextPlaceTables()
    {
        var tables = new ulong[4][];
        for (var sizeIndex = 0; sizeIndex < 4; sizeIndex++)
        {
            var size = 11 + 2 * sizeIndex;
            var tbl = new ulong[size * 6];
            var p = 0;
            var upward = true;
            for (var right = size - 1; right >= 2; right -= 2)
            {
                var rowStart = right >= 10 ? 1 : 9;
                var row = upward ? size - 1 : rowStart;
                var stepDir = upward ? -1 : 1;
                var count = size - rowStart;
                for (var i = 0; i < count; i++, row += stepDir)
                {
                    for (var side = 0; side < 2; side++, p++)
                    {
                        var col = right - side;
                        var w = p >> 6;
                        tbl[row * 6 + w * 2] |= 1ul << (63 - (p & 63));
                        tbl[row * 6 + w * 2 + 1] |= 1ul << col;
                    }
                }
                upward = !upward;
            }
            tables[sizeIndex] = tbl;
        }
        return tables;
    }
#endif

    /// <summary>
    /// Scores the four mask patterns from the packed symbol edges (ISO/IEC 18004 Section 7.8.3: SUM1 = right edge dark count, SUM2 = lower edge dark count, score = min*16 + max, HIGHEST wins, ties keep the lowest pattern number).
    /// Each candidate is one XOR + popcount per edge against the static edge tables; bit 0 of both inputs must be clear (row/column 0 are excluded from the edge sums).
    /// </summary>
    private static int SelectMaskFromEdges(ulong colDark, ulong rowDark, int size)
    {
        var edgeBase = ((size - 11) >> 1) * 4;
        var bestMask = 0;
        var bestScore = -1;
        for (var mask = 0; mask < 4; mask++)
        {
            var sum1 = PopCount(colDark ^ _edgeColBits[edgeBase + mask]);
            var sum2 = PopCount(rowDark ^ _edgeRowBits[edgeBase + mask]);

            var score = sum1 <= sum2 ? sum1 * 16 + sum2 : sum2 * 16 + sum1;
            if (score > bestScore)
            {
                bestScore = score;
                bestMask = mask;
            }
        }

        return bestMask;
    }

    /// <summary>
    /// Writes 8 modules at once: byte k = bit k of the low byte of <paramref name="bits"/> (multiply-replicate to the per-byte diagonal, OR-cascade down to bit 0, the ModulePlacer.Masking SWAR unpack).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteSpread8(ref byte p, ulong bits)
    {
        var spread = ((bits & 0xFF) * 0x0101010101010101UL) & 0x8040201008040201UL;
        spread |= spread >> 4;
        spread |= spread >> 2;
        spread |= spread >> 1;
        spread &= 0x0101010101010101UL;
        Unsafe.WriteUnaligned(ref p, NormalizeEndianness(spread));
    }

    // ---------------------------------------------------------------
    // Static tables (all built by ordinary code from the reference
    // implementations in MicroQRModulePlacer.cs, NativeAOT/trimming-safe)
    // ---------------------------------------------------------------

    /// <summary>
    /// All 32 masked format words, index = symbolNumber(0-7) * 4 + mask(0-3), built from <see cref="MicroQRConstants.GetFormatBits"/> (the same shape as the decoder-side candidate table) so the per-symbol BCH remainder loop never runs during placement.
    /// </summary>
    private static readonly ushort[] _formatBitsTable = BuildFormatBitsTable();

    private static ushort[] BuildFormatBitsTable()
    {
        var table = new ushort[32];
        for (var symbolNumber = 0; symbolNumber < 8; symbolNumber++)
        {
            MicroQRConstants.GetVersionAndEccFromSymbolNumber(symbolNumber, out var version, out var eccLevel);
            for (var mask = 0; mask < 4; mask++)
            {
                table[symbolNumber * 4 + mask] = MicroQRConstants.GetFormatBits(version, eccLevel, mask);
            }
        }
        return table;
    }

    /// <summary>Bit-reversed bytes, for the row-8 format information insert.</summary>
    private static readonly byte[] _reverseByte = BuildReverseByte();

    private static byte[] BuildReverseByte()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var v = i;
            var r = 0;
            for (var b = 0; b < 8; b++)
            {
                r = (r << 1) | (v & 1);
                v >>= 1;
            }
            table[i] = (byte)r;
        }
        return table;
    }

    /// <summary>Full function pattern as packed rows, [sizeIndex][row].</summary>
    private static readonly ulong[][] _funcPackedRows = BuildFuncPackedRows();

    private static ReadOnlySpan<ulong> FuncPackedRows(int size) => _funcPackedRows[(size - 11) >> 1];

    private static ulong[][] BuildFuncPackedRows()
    {
        var tables = new ulong[4][];
        for (var sizeIndex = 0; sizeIndex < 4; sizeIndex++)
        {
            var size = 11 + 2 * sizeIndex;
            var matrix = new byte[size * size];
            PlaceFunctionModules(matrix, size);
            var rows = new ulong[size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (matrix[y * size + x] != 0)
                    {
                        rows[y] |= 1ul << x;
                    }
                }
            }
            tables[sizeIndex] = rows;
        }
        return tables;
    }

    /// <summary>
    /// Mask darkness along the scored edges, [sizeIndex * 4 + mask]: bit i of the column table = GetMaskBit(mask, i, size-1), bit i of the row table = GetMaskBit(mask, size-1, i).
    /// Bit 0 stays clear (column 0 / row 0 are function modules and excluded from the edge sums).
    /// </summary>
    private static readonly ulong[] _edgeColBits = BuildEdgeBits(column: true);
    private static readonly ulong[] _edgeRowBits = BuildEdgeBits(column: false);

    private static ulong[] BuildEdgeBits(bool column)
    {
        var table = new ulong[4 * 4];
        for (var sizeIndex = 0; sizeIndex < 4; sizeIndex++)
        {
            var size = 11 + 2 * sizeIndex;
            for (var mask = 0; mask < 4; mask++)
            {
                ulong bits = 0;
                for (var i = 1; i < size; i++)
                {
                    var hit = column ? GetMaskBit(mask, i, size - 1) : GetMaskBit(mask, size - 1, i);
                    if (hit)
                    {
                        bits |= 1ul << i;
                    }
                }
                table[sizeIndex * 4 + mask] = bits;
            }
        }
        return table;
    }

    /// <summary>
    /// Packed mask template rows, [mask * 12 + (row % 12)]: every Micro QR mask formula is periodic in the row with period dividing 12 (row%2, row/2%2, row%3) and evaluated exactly per column bit (columns never exceed 16).
    /// Bits beyond the data region are removed by the caller's allowed mask.
    /// </summary>
    private static readonly ulong[] _maskTemplates12 = BuildMaskTemplates12();

    private static ulong[] BuildMaskTemplates12()
    {
        var table = new ulong[4 * 12];
        for (var mask = 0; mask < 4; mask++)
        {
            for (var r = 0; r < 12; r++)
            {
                ulong bits = 0;
                for (var c = 0; c < 64; c++)
                {
                    if (GetMaskBit(mask, r, c))
                    {
                        bits |= 1ul << c;
                    }
                }
                table[mask * 12 + r] = bits;
            }
        }
        return table;
    }

    // ---------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Maps between machine byte order and the logical little-endian order the SWAR unpack constant assumes (memory offset k = ulong byte k).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong NormalizeEndianness(ulong value)
        => BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PopCount(ulong value)
    {
#if NET8_0_OR_GREATER
        return System.Numerics.BitOperations.PopCount(value);
#else
        // SWAR popcount for targets without System.Numerics.BitOperations.
        value -= (value >> 1) & 0x5555555555555555UL;
        value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
        value = (value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
        return (int)((value * 0x0101010101010101UL) >> 56);
#endif
    }
}
