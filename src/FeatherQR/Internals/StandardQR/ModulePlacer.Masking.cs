using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Bit-packed mask pattern selection.
///
/// QR modules are 1-bit values, so the whole evaluation pipeline operates on rows packed into ulongs instead of one byte per module: a row is 1 word for matrices up to 64 modules (versions 1-11) or 3 words (a <see cref="Row192"/>) for larger ones.
/// Per pattern, applying the mask is a handful of XOR/AND word operations per row and all four ISO/IEC 18004 penalty rules are computed bit-parallel with shifts and popcounts.
///
/// Measured against the previous byte-per-module implementation (see the micro-optimization findings log): version 1 ~8x, version 10 ~44x, version 40 ~30-40x, zero allocations.
/// Bit-packing beat both Parallel.For over the 8 patterns (which allocates and still loses at every size) and early-terminating the score (5-15%): the serial 8-pattern loop was never the problem, the per-pattern representation was.
/// </summary>
internal static partial class ModulePlacer
{
    /// <summary>
    /// Applies mask pattern to data area and selects optimal pattern.
    /// Tests all 8 mask patterns and selects one with lowest penalty score.
    /// </summary>
    /// <param name="buffer">Original QR code data without mask applied.</param>
    /// <param name="size">QR code size in modules.</param>
    /// <param name="version">QR code version (1-40).</param>
    /// <param name="blockedMask">Blocked mask bytes.</param>
    /// <param name="eccLevel">Error correction level.</param>
    /// <returns>Index of the best mask pattern number (0-7).</returns>
    /// <remarks>
    /// Scoring evaluates each candidate exactly as a decoder would see it: mask XOR over the data area, format bits for (eccLevel, pattern), and version bits (version 7+).
    /// Version bits live in blocked areas, so they are pattern-invariant and packed once per call; only the 30 format-bit modules are re-poked per pattern.
    /// </remarks>
    public static int MaskCode(Span<byte> buffer, int size, int version, ReadOnlySpan<byte> blockedMask, QREccLevel eccLevel)
    {
#if NET8_0_OR_GREATER
        // Vectorized tiers (lane-per-row scorer + SIMD byte<->bit conversion),
        // see ModulePlacer.Masking.Simd.cs. Measured 1.3-2x over the scalar
        // bit-packed paths below (findings log, round 5).
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            return MaskCodeSimd(buffer, size, version, blockedMask, eccLevel);
        }
        // ARM64 NEON port of the same tiers (Vector128 lane-per-row scorer),
        // see ModulePlacer.Masking.Simd.Arm.cs. Measured 2.4-3x (versions 1-11)
        // and 1.15-1.2x (12-40) over the scalar paths below on Apple M2
        // (MaskCodeArm findings log).
        if (System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
        {
            return MaskCodeAdvSimd(buffer, size, version, blockedMask, eccLevel);
        }
#endif
        // Versions 1-11 (size <= 61) fit a whole row in one ulong.
        return size <= 64
            ? MaskCode64(buffer, size, version, blockedMask, eccLevel)
            : MaskCode192(buffer, size, version, blockedMask, eccLevel);
    }

    /// <summary>
    /// Applies one specific mask pattern to the data area, for a caller-pinned pattern (<see cref="QRCodeGeneratorOptions.MaskPattern"/>).
    /// No scoring: the whole 8-pattern evaluation is skipped, so a plain per-module loop is already far cheaper than the selection path it replaces.
    /// </summary>
    /// <param name="buffer">QR matrix with data placed and no mask applied; masked in place.</param>
    /// <param name="size">QR code size in modules.</param>
    /// <param name="blockedMask">Blocked-module bitmask (function patterns and format/version areas).</param>
    /// <param name="patternIndex">Mask pattern number (0-7).</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="patternIndex"/> is not 0-7. Without the guard an out-of-range pattern would silently apply no mask (GetMaskBit's default arm) while the caller still writes format information claiming it.</exception>
    public static void ApplyMaskPattern(Span<byte> buffer, int size, ReadOnlySpan<byte> blockedMask, int patternIndex)
    {
        if ((uint)patternIndex > 7)
            throw new ArgumentOutOfRangeException(nameof(patternIndex), $"Mask pattern must be 0-7, but was {patternIndex}");

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var index = y * size + x;
                if (IsModuleBlocked(blockedMask, index))
                    continue;
                if (GetMaskBit(patternIndex, y, x))
                    buffer[index] ^= 1;
            }
        }
    }

    /// <summary>
    /// Mask predicates (ISO/IEC 18004 7.8.2), row = y, col = x.
    /// Must agree with the packed mask templates below and with QRMatrixDecoder.GetMaskBit.
    /// </summary>
    private static bool GetMaskBit(int pattern, int row, int col) => pattern switch
    {
        0 => ((row + col) & 1) == 0,
        1 => (row & 1) == 0,
        2 => col % 3 == 0,
        3 => (row + col) % 3 == 0,
        4 => ((row / 2 + col / 3) & 1) == 0,
        5 => (row * col) % 2 + (row * col) % 3 == 0,
        6 => ((row * col % 2 + row * col % 3) & 1) == 0,
        7 => (((row + col) % 2 + row * col % 3) & 1) == 0,
        _ => false,
    };

    // ---------------------------------
    // Single-word path (versions 1-11)
    // ---------------------------------

    internal static int MaskCode64(Span<byte> buffer, int size, int version, ReadOnlySpan<byte> blockedMask, QREccLevel eccLevel)
    {
        Span<ulong> packed = stackalloc ulong[64];
        Span<ulong> allowed = stackalloc ulong[64];
        Span<ulong> masked = stackalloc ulong[64];
        Span<ulong> nmasked = stackalloc ulong[64];
        packed = packed[..size];
        allowed = allowed[..size];
        masked = masked[..size];
        nmasked = nmasked[..size];

        for (var y = 0; y < size; y++)
        {
            packed[y] = PackRowBits64(buffer.Slice(y * size, size));
        }

        // Version bits sit in blocked areas, hence identical for every pattern.
        if (version >= 7)
        {
            var versionBits = QRCodeConstants.GetVersionBits(version);
            for (var x = 0; x < 6; x++)
            {
                for (var y = 0; y < 3; y++)
                {
                    var bit = (versionBits & (1u << (x * 3 + y))) != 0;
                    packed[y + size - 11] = WithBit64(packed[y + size - 11], x, bit);
                    packed[x] = WithBit64(packed[x], y + size - 11, bit);
                }
            }
        }

        // Each allowed row (~blocked) is a contiguous bit slice of the blocked
        // bitmask; a padded copy makes the two 8-byte slice reads always legal.
        Span<byte> padded = stackalloc byte[blockedMask.Length + 16];
        padded.Clear();
        blockedMask.CopyTo(padded);
        var rowMask = size == 64 ? ulong.MaxValue : (1ul << size) - 1;
        for (var y = 0; y < size; y++)
        {
            var bitOffset = y * size;
            var byteOff = bitOffset >> 3;
            var sh = bitOffset & 7;
            var u0 = NormalizeEndianness(MemoryMarshal.Read<ulong>(padded.Slice(byteOff)));
            var u1 = NormalizeEndianness(MemoryMarshal.Read<ulong>(padded.Slice(byteOff + 8)));
            var blocked = sh == 0 ? u0 : (u0 >> sh) | (u1 << (64 - sh));
            allowed[y] = ~blocked & rowMask;
        }

        var templates = _maskTemplates64;
        var bestPatternIndex = 0;
        var bestScore = int.MaxValue;
        for (var patternIndex = 0; patternIndex < 8; patternIndex++)
        {
            var tplBase = patternIndex * 12;
            for (int y = 0, tplRow = 0; y < size; y++)
            {
                masked[y] = packed[y] ^ (templates[tplBase + tplRow] & allowed[y]);
                if (++tplRow == 12) tplRow = 0;
            }
            PokeFormatBits64(masked, size, QRCodeConstants.GetFormatBits(eccLevel, patternIndex));

            var score = CalculateScore64(masked, nmasked, size, bestScore);
            if (score < bestScore)
            {
                bestPatternIndex = patternIndex;
                bestScore = score;
            }
        }

        // Apply the winner to the byte buffer: unpack the XOR delta 8 modules at a time.
        {
            var tplBase = bestPatternIndex * 12;
            for (int y = 0, tplRow = 0; y < size; y++)
            {
                XorUnpackRow64(buffer.Slice(y * size, size), _maskTemplates64[tplBase + tplRow] & allowed[y]);
                if (++tplRow == 12) tplRow = 0;
            }
        }

        return bestPatternIndex;
    }

    /// <summary>
    /// Bit-parallel penalty scoring for single-word rows.
    /// See <see cref="CalculateScorePacked"/> for the rule derivations.
    /// </summary>
    /// <remarks>
    /// Terminates early (returning int.MaxValue) once the running sum of rules 1-3 exceeds <paramref name="abortAbove"/>: penalty sub-scores only ever accumulate, so a pattern whose partial sum already exceeds the best total can never be selected, the result is provably identical.
    /// Measured ~5-10% on this single-word path; the triple-word scorer intentionally has no abort because no win was measurable there (see the findings log).
    /// </remarks>
#if NET6_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    private static int CalculateScore64(ReadOnlySpan<ulong> rows, Span<ulong> nrows, int size, int abortAbove)
    {
        var rowMask = size == 64 ? ulong.MaxValue : (1ul << size) - 1;
        var startMaskP3 = (1ul << (size - 10)) - 1;
        var maskN1 = (1ul << (size - 1)) - 1;

        var score1 = 0;
        var score2 = 0;
        var score3 = 0;
        var blackModules = 0;

        for (var y = 0; y < size; y++)
        {
            nrows[y] = ~rows[y] & rowMask;
        }

        // Row-direction rules 1 and 3, rule 2, rule 4
        for (var y = 0; y < size; y++)
        {
            var x = rows[y];
            var nx = nrows[y];

            blackModules += PopCount(x);

            score1 += ScoreRuns64(x) + ScoreRuns64(nx);

            // Rule 3 forward window [0,0,0,0,1,0,1,1,1,0,1], backward is its reverse.
            var mf = nx & (nx >> 1) & (nx >> 2) & (nx >> 3)
                   & (x >> 4) & (nx >> 5) & (x >> 6) & (x >> 7)
                   & (x >> 8) & (nx >> 9) & (x >> 10) & startMaskP3;
            var mb = x & (nx >> 1) & (x >> 2) & (x >> 3)
                   & (x >> 4) & (nx >> 5) & (x >> 6) & (nx >> 7)
                   & (nx >> 8) & (nx >> 9) & (nx >> 10) & startMaskP3;
            score3 += 40 * (PopCount(mf) + PopCount(mb));

            if (y < size - 1)
            {
                var next = rows[y + 1];
                var eqv = ~(x ^ next);
                var eqh = ~(x ^ (x >> 1));
                var m = eqh & eqv & (eqv >> 1) & maskN1;
                score2 += 3 * PopCount(m);
            }

            if (score1 + score2 + score3 > abortAbove)
            {
                return int.MaxValue;
            }
        }

        // Column-direction rules 1 and 3
        ulong eq1 = 0, eq2 = 0, eq3 = 0, prevV5 = 0;
        for (var y = 1; y < size; y++)
        {
            var eq0 = ~(rows[y] ^ rows[y - 1]) & rowMask;
            if (y >= 4)
            {
                var v5 = eq0 & eq1 & eq2 & eq3;
                score1 += PopCount(v5) + 2 * PopCount(v5 & ~prevV5);
                prevV5 = v5;
            }
            eq3 = eq2;
            eq2 = eq1;
            eq1 = eq0;

            if (y >= 10)
            {
                var b = y - 10;
                var mf = nrows[b] & nrows[b + 1] & nrows[b + 2] & nrows[b + 3]
                       & rows[b + 4] & nrows[b + 5] & rows[b + 6] & rows[b + 7]
                       & rows[b + 8] & nrows[b + 9] & rows[b + 10];
                var mb = rows[b] & nrows[b + 1] & rows[b + 2] & rows[b + 3]
                       & rows[b + 4] & nrows[b + 5] & rows[b + 6] & nrows[b + 7]
                       & nrows[b + 8] & nrows[b + 9] & nrows[b + 10];
                score3 += 40 * (PopCount(mf) + PopCount(mb));

                if (score1 + score2 + score3 > abortAbove)
                {
                    return int.MaxValue;
                }
            }
        }

        return score1 + score2 + score3 + CalculateBalanceScore(blackModules, size);
    }

    /// <summary>
    /// Penalty-1 contribution of one color for a single row: sum over runs of length L >= 5 of (3 + (L - 5)). y5 marks every position where 5 consecutive set bits start, so a run of length L contributes popcount L-4 plus 2 per run (isolated via the run's lowest y5 bit), totalling the required L-2.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScoreRuns64(ulong x)
    {
        var y2 = x & (x >> 1);
        var y4 = y2 & (y2 >> 2);
        var y5 = y4 & (x >> 4);
        var starts = y5 & ~(y5 << 1);
        return PopCount(y5) + 2 * PopCount(starts);
    }

    /// <summary>
    /// Format-bit module coordinates around the top-left finder (copy 1), same positions as PlaceFormat.
    /// Static data spans: no per-call table build, and no temporary array in unoptimized builds (stackalloc initializers allocate a heap copy there).
    /// Copy 2 coordinates are size-relative and computed inline: bit i &lt; 8 sits at (x = size-1-i, y = 8), bit i &gt;= 8 at (x = 8, y = size-15+i).
    /// </summary>
    private static ReadOnlySpan<byte> FormatXs1 => new byte[15] { 8, 8, 8, 8, 8, 8, 8, 8, 7, 5, 4, 3, 2, 1, 0 };
    private static ReadOnlySpan<byte> FormatYs1 => new byte[15] { 0, 1, 2, 3, 4, 5, 7, 8, 8, 8, 8, 8, 8, 8, 8 };

    private static void PokeFormatBits64(Span<ulong> rows, int size, ushort formatBits)
    {
        for (var i = 0; i < 15; i++)
        {
            var bit = (formatBits & (1 << i)) != 0;
            var x2 = i < 8 ? size - 1 - i : 8;
            var y2 = i < 8 ? 8 : size - 15 + i;
            rows[FormatYs1[i]] = WithBit64(rows[FormatYs1[i]], FormatXs1[i], bit);
            rows[y2] = WithBit64(rows[y2], x2, bit);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong WithBit64(ulong w, int x, bool value)
    {
        var bit = 1ul << x;
        return value ? w | bit : w & ~bit;
    }

    /// <summary>
    /// Packs a row of 0/1 module bytes into bits (bit c = row[c]) using the multiply-gather trick: for a ulong holding eight 0/1 bytes, u * 0x0102040810204080 collects byte k into bit 56+k, so one multiply plus a shift packs 8 modules.
    /// </summary>
    private static ulong PackRowBits64(ReadOnlySpan<byte> row)
    {
        ulong w = 0;
        var c = 0;
        for (; c + 8 <= row.Length; c += 8)
        {
            var u = NormalizeEndianness(MemoryMarshal.Read<ulong>(row.Slice(c)));
            w |= ((u * 0x0102040810204080UL) >> 56) << c;
        }
        for (; c < row.Length; c++)
        {
            if (row[c] != 0)
            {
                w |= 1ul << c;
            }
        }
        return w;
    }

    /// <summary>
    /// XORs a packed 0/1 delta into a byte row, 8 modules per step: the delta byte is spread so bit k lands in byte k (multiply-replicate, mask to the per-byte diagonal, then OR-cascade down to bit 0 of each byte).
    /// </summary>
    private static void XorUnpackRow64(Span<byte> row, ulong delta)
    {
        ref var rowRef = ref MemoryMarshal.GetReference(row);
        var c = 0;
        for (; c + 8 <= row.Length; c += 8)
        {
            var spread = (((delta >> c) & 0xFF) * 0x0101010101010101UL) & 0x8040201008040201UL;
            spread |= spread >> 4;
            spread |= spread >> 2;
            spread |= spread >> 1;
            spread &= 0x0101010101010101UL;
            ref var p = ref Unsafe.Add(ref rowRef, c);
            // cur round-trips through the same byte order, so only the spread
            // (logical little-endian) needs normalizing before the XOR store.
            var cur = Unsafe.ReadUnaligned<ulong>(ref p);
            Unsafe.WriteUnaligned(ref p, cur ^ NormalizeEndianness(spread));
        }
        for (; c < row.Length; c++)
        {
            if (((delta >> c) & 1) != 0)
            {
                row[c] ^= 1;
            }
        }
    }

    // ---------------------------------
    // Triple-word path (versions 12-40)
    // ---------------------------------

    internal static int MaskCode192(Span<byte> buffer, int size, int version, ReadOnlySpan<byte> blockedMask, QREccLevel eccLevel)
    {
        // One rent, partitioned four ways (packed / allowed / masked / ~masked).
        var rent = ArrayPool<Row192>.Shared.Rent(4 * size);
        try
        {
            var packed = rent.AsSpan(0, size);
            var allowed = rent.AsSpan(size, size);
            var masked = rent.AsSpan(2 * size, size);
            var nmasked = rent.AsSpan(3 * size, size);

            for (var y = 0; y < size; y++)
            {
                packed[y] = Row192.PackRowBits(buffer.Slice(y * size, size));
            }

            // Version bits sit in blocked areas, hence identical for every pattern.
            // This path only serves size > 64, i.e. version >= 12, so always poke.
            var versionBits = QRCodeConstants.GetVersionBits(version);
            for (var x = 0; x < 6; x++)
            {
                for (var y = 0; y < 3; y++)
                {
                    var bit = (versionBits & (1u << (x * 3 + y))) != 0;
                    packed[y + size - 11] = packed[y + size - 11].WithBit(x, bit);
                    packed[x] = packed[x].WithBit(y + size - 11, bit);
                }
            }

            // Padded copy so the four 8-byte slice reads per row never overrun.
            Span<byte> padded = stackalloc byte[blockedMask.Length + 32];
            padded.Clear();
            blockedMask.CopyTo(padded);
            var rowMask = Row192.MaskLow(size);
            for (var y = 0; y < size; y++)
            {
                allowed[y] = Row192.FromBitSlice(padded, y * size).AndNot(rowMask);
            }

            var templates = _maskTemplates;
            var bestPatternIndex = 0;
            var bestScore = int.MaxValue;
            for (var patternIndex = 0; patternIndex < 8; patternIndex++)
            {
                var tplBase = patternIndex * 12;
                for (int y = 0, tplRow = 0; y < size; y++)
                {
                    masked[y] = packed[y] ^ (templates[tplBase + tplRow] & allowed[y]);
                    if (++tplRow == 12) tplRow = 0;
                }
                PokeFormatBits192(masked, size, QRCodeConstants.GetFormatBits(eccLevel, patternIndex));

                var score = CalculateScorePacked(masked, nmasked, size);
                if (score < bestScore)
                {
                    bestPatternIndex = patternIndex;
                    bestScore = score;
                }
            }

            // Apply the winner to the byte buffer via the packed XOR delta.
            {
                var tplBase = bestPatternIndex * 12;
                ref var bufRef = ref MemoryMarshal.GetReference(buffer);
                for (int y = 0, tplRow = 0; y < size; y++)
                {
                    var delta = templates[tplBase + tplRow] & allowed[y];
                    XorUnpackRow192(ref Unsafe.Add(ref bufRef, y * size), size, delta);
                    if (++tplRow == 12) tplRow = 0;
                }
            }

            return bestPatternIndex;
        }
        finally
        {
            ArrayPool<Row192>.Shared.Return(rent);
        }
    }

    // ---------------------------------
    // Basic penalty calculations for reference.
    // The plain byte-per-module formulation of ISO/IEC 18004 Section 8.8.2 that
    // the bit-parallel scorers in this file must reproduce exactly. A runnable
    // copy is kept in tests (ModulePlacerMaskPackedParityTest.ReferenceScore)
    // and gates every change.
    // ---------------------------------
    // // Penalty 1: Consecutive modules (runs of the same color, length >= 5:
    // // +3 at the 5th module, +1 for each module beyond)
    // for (var y = 0; y < size; y++)
    // {
    //     var modInRow = 0;
    //     var modInColumn = 0;
    //     var lastValRow = qrCode[y, 0];
    //     var lastValColumn = qrCode[0, y];
    //     for (var x = 0; x < size; x++)
    //     {
    //         if (qrCode[y, x] == lastValRow)
    //         {
    //             modInRow++;
    //         }
    //         else
    //         {
    //             modInRow = 1;
    //         }
    //         if (modInRow == 5)
    //         {
    //             score1 += 3;
    //         }
    //         else if (modInRow > 5)
    //         {
    //             score1++;
    //         }
    //         lastValRow = qrCode[y, x];
    //
    //         if (qrCode[x, y] == lastValColumn)
    //         {
    //             modInColumn++;
    //         }
    //         else
    //         {
    //             modInColumn = 1;
    //         }
    //         if (modInColumn == 5)
    //         {
    //             score1 += 3;
    //         }
    //         else if (modInColumn > 5)
    //         {
    //             score1++;
    //         }
    //         lastValColumn = qrCode[x, y];
    //     }
    // }
    //
    // // Penalty 2: Block patterns (each 2x2 block of one color: +3)
    // for (var y = 0; y < size - 1; y++)
    // {
    //     for (var x = 0; x < size - 1; x++)
    //     {
    //         if (qrCode[y, x] == qrCode[y, x + 1] &&
    //             qrCode[y, x] == qrCode[y + 1, x] &&
    //             qrCode[y, x] == qrCode[y + 1, x + 1])
    //         {
    //             score2 += 3;
    //         }
    //     }
    // }
    //
    // // Penalty 3: Finder-like patterns (sliding 11-bit window, +40 per match)
    // // pattern: 1011101 (dark-light-dark-dark-dark-light-dark = 1:1:3:1:1)
    // // preceded (forward) or followed (backward) by 4 light modules
    // const uint PATTERN_FORWARD = 0b_0000_1011101; // 4 light modules + pattern
    // const uint PATTERN_BACKWARD = 0b_1011101_0000; // pattern + 4 light modules
    // const uint MASK_11BIT = 0b_111_1111_1111; // guarantee 11 bits
    // for (var y = 0; y < size; y++)
    // {
    //     var rowOffset = y * size;
    //     uint rowBits = 0;
    //     uint colBits = 0;
    //     for (var x = 0; x < size; x++)
    //     {
    //         // Build row/col bits (shift left and OR with current bit == add new bit)
    //         rowBits = ((rowBits << 1) | (buffer[rowOffset + x] != 0 ? 1u : 0u)) & MASK_11BIT;
    //         colBits = ((colBits << 1) | (buffer[x * size + y] != 0 ? 1u : 0u)) & MASK_11BIT;
    //
    //         // 11 bits ready, check for pattern
    //         if (x >= 10)
    //         {
    //             // Check row bits
    //             if (rowBits == PATTERN_FORWARD || rowBits == PATTERN_BACKWARD)
    //                 score3 += 40;
    //
    //             // Check column bits
    //             if (colBits == PATTERN_FORWARD || colBits == PATTERN_BACKWARD)
    //                 score3 += 40;
    //         }
    //     }
    // }
    //
    // // Penalty 4: Dark/light balance
    // double blackModules = 0;
    // for (var row = 0; row < size; row++)
    // {
    //     for (var col = 0; col < size; col++)
    //     {
    //         if (qrCode[row, col])
    //         {
    //             blackModules++;
    //         }
    //     }
    // }
    //
    // // Calculate percentage of dark modules
    // var percent = (blackModules / (size * size)) * 100;
    //
    // // ISO/IEC 18004:2015 Section 8.8.2: Score = (|closest multiple of 5 - 50| / 5) x 10
    // // Evaluate the multiples of 5 on both sides of the percentage and take the
    // // smaller deviation from 50%. (An earlier version of this reference used
    // // `Floor(percent / 5) * 5 - 45` for the upper side, which is wrong when the
    // // percentage is an exact multiple of 5, e.g. percent = 45 scored 0 instead
    // // of 10. Ceiling keeps prev == next there, matching the shipped code.)
    // var prevMultipleOf5 = Math.Abs((int)Math.Floor(percent / 5) * 5 - 50) / 5;
    // var nextMultipleOf5 = Math.Abs((int)Math.Ceiling(percent / 5) * 5 - 50) / 5;
    // score4 = Math.Min(prevMultipleOf5, nextMultipleOf5) * 10;

    /// <summary>
    /// Bit-parallel implementation of all four ISO/IEC 18004 Section 8.8.2 penalty rules over packed rows.
    /// Produces scores identical to the plain byte-per-module formulation (see the reference block above and ModulePlacerMaskPackedParityTest).
    ///
    /// Rule 1 (runs >= 5, rows): y5 = x &amp; (x>>1) &amp; ... &amp; (x>>4) marks every
    ///   position where 5 consecutive equal bits start; a run of length L
    ///   contributes popcount L-4 and its total penalty is 3+(L-5) = L-2, so
    ///   score = popcount(y5) + 2 * runs, runs isolated via y5 &amp; ~(y5&lt;&lt;1).
    ///   Computed for dark bits and for light bits (~x within the row).
    /// Rule 1 (columns): eq[y] = ~(row[y] ^ row[y-1]) marks columns whose
    ///   vertical run continues at row y; v5[y] = eq[y] &amp; eq[y-1] &amp; eq[y-2] &amp; eq[y-3]
    ///   is the vertical analog of y5, kept in a 4-deep rolling window; vertical
    ///   run starts are isolated against the previous v5.
    /// Rule 2 (2x2 blocks): all-equal(a[x], a[x+1], b[x], b[x+1]) ⟺
    ///   eqh_a[x] &amp; eqv[x] &amp; eqv[x+1], eqh = ~(a ^ (a>>1)), eqv = ~(a ^ b).
    /// Rule 3 (finder windows): bit-sliced 11-wide pattern match, AND together
    ///   shifted rows, taking x for required-dark offsets {4,6,7,8,10} and ~x for
    ///   required-light offsets {0,1,2,3,5,9} (forward; backward is the reverse);
    ///   the column direction applies the same selection over the last 11 packed
    ///   rows. Matches counted per start position, same as the sliding-window scan.
    /// Rule 4 (balance): popcount per row, then the shared closest-multiple-of-5
    ///   deviation formula.
    /// </summary>
#if NET6_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    private static int CalculateScorePacked(ReadOnlySpan<Row192> rows, Span<Row192> nrows, int size)
    {
        var rowMask = Row192.MaskLow(size);
        var startMaskP3 = Row192.MaskLow(size - 10); // rule-3 window starts: 0..n-11
        var maskN1 = Row192.MaskLow(size - 1);       // rule-2 block positions: 0..n-2

        var score1 = 0;
        var score2 = 0;
        var score3 = 0;
        var blackModules = 0;

        for (var y = 0; y < size; y++)
        {
            nrows[y] = rows[y].AndNot(rowMask); // ~rows[y] & rowMask
        }

        for (var y = 0; y < size; y++)
        {
            var x = rows[y];
            var nx = nrows[y];

            blackModules += x.PopCount();

            score1 += ScoreRuns(x) + ScoreRuns(nx);

            var mf = nx & nx.ShiftRight(1) & nx.ShiftRight(2) & nx.ShiftRight(3)
                   & x.ShiftRight(4) & nx.ShiftRight(5) & x.ShiftRight(6) & x.ShiftRight(7)
                   & x.ShiftRight(8) & nx.ShiftRight(9) & x.ShiftRight(10) & startMaskP3;
            var mb = x & nx.ShiftRight(1) & x.ShiftRight(2) & x.ShiftRight(3)
                   & x.ShiftRight(4) & nx.ShiftRight(5) & x.ShiftRight(6) & nx.ShiftRight(7)
                   & nx.ShiftRight(8) & nx.ShiftRight(9) & nx.ShiftRight(10) & startMaskP3;
            score3 += 40 * (mf.PopCount() + mb.PopCount());

            if (y < size - 1)
            {
                var eqv = x.Xnor(rows[y + 1]);
                var eqh = x.Xnor(x.ShiftRight(1));
                var m = eqh & eqv & eqv.ShiftRight(1) & maskN1;
                score2 += 3 * m.PopCount();
            }
        }

        var eq1 = default(Row192);
        var eq2 = default(Row192);
        var eq3 = default(Row192);
        var prevV5 = default(Row192);
        for (var y = 1; y < size; y++)
        {
            var eq0 = rows[y].Xnor(rows[y - 1]) & rowMask;
            if (y >= 4)
            {
                var v5 = eq0 & eq1 & eq2 & eq3;
                score1 += v5.PopCount() + 2 * v5.AndNotWith(prevV5).PopCount();
                prevV5 = v5;
            }
            eq3 = eq2;
            eq2 = eq1;
            eq1 = eq0;

            if (y >= 10)
            {
                var b = y - 10;
                var mf = nrows[b] & nrows[b + 1] & nrows[b + 2] & nrows[b + 3]
                       & rows[b + 4] & nrows[b + 5] & rows[b + 6] & rows[b + 7]
                       & rows[b + 8] & nrows[b + 9] & rows[b + 10];
                var mb = rows[b] & nrows[b + 1] & rows[b + 2] & rows[b + 3]
                       & rows[b + 4] & nrows[b + 5] & rows[b + 6] & nrows[b + 7]
                       & nrows[b + 8] & nrows[b + 9] & nrows[b + 10];
                score3 += 40 * (mf.PopCount() + mb.PopCount());
            }
        }

        return score1 + score2 + score3 + CalculateBalanceScore(blackModules, size);
    }

    /// <summary>Penalty-1 contribution of one color (see <see cref="ScoreRuns64"/> for the derivation).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScoreRuns(in Row192 x)
    {
        var y2 = x & x.ShiftRight(1);
        var y4 = y2 & y2.ShiftRight(2);
        var y5 = y4 & x.ShiftRight(4);
        var starts = y5.AndNotWith(y5.ShiftLeft1());
        return y5.PopCount() + 2 * starts.PopCount();
    }

    private static void PokeFormatBits192(Span<Row192> rows, int size, ushort formatBits)
    {
        // Same coordinate scheme as PokeFormatBits64 (see FormatXs1/FormatYs1).
        for (var i = 0; i < 15; i++)
        {
            var bit = (formatBits & (1 << i)) != 0;
            var x2 = i < 8 ? size - 1 - i : 8;
            var y2 = i < 8 ? 8 : size - 15 + i;
            rows[FormatYs1[i]] = rows[FormatYs1[i]].WithBit(FormatXs1[i], bit);
            rows[y2] = rows[y2].WithBit(x2, bit);
        }
    }

    /// <summary>Ref-based packed delta unpack: one word load per 64 columns, 8-module SWAR XOR steps, scalar tail.</summary>
    private static void XorUnpackRow192(ref byte rowRef, int len, in Row192 delta)
    {
        var c = 0;
        for (var w = 0; w < 3 && c < len; w++)
        {
            var bits = delta.WordAt(w);
            var wordEnd = (w + 1) * 64;
            if (wordEnd > len) wordEnd = len;
            for (; c + 8 <= wordEnd; c += 8)
            {
                var spread = (((bits >> (c & 63)) & 0xFF) * 0x0101010101010101UL) & 0x8040201008040201UL;
                spread |= spread >> 4;
                spread |= spread >> 2;
                spread |= spread >> 1;
                spread &= 0x0101010101010101UL;
                ref var p = ref Unsafe.Add(ref rowRef, c);
                // cur round-trips through the same byte order, so only the spread
                // (logical little-endian) needs normalizing before the XOR store.
                var cur = Unsafe.ReadUnaligned<ulong>(ref p);
                Unsafe.WriteUnaligned(ref p, cur ^ NormalizeEndianness(spread));
            }
            for (; c < wordEnd; c++)
            {
                if (((bits >> (c & 63)) & 1) != 0)
                {
                    Unsafe.Add(ref rowRef, c) ^= 1;
                }
            }
        }
    }

    // ---------------------------------
    // Shared pieces
    // ---------------------------------

    /// <summary>
    /// Rule 4: deviation of the dark-module share from 50%, rounded to the closest multiple of 5 (ISO/IEC 18004:2015 Section 8.8.2).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CalculateBalanceScore(int blackModules, int size)
    {
        // Simple calculation `(int)(Math.Abs(percent - 50.0) / 5.0) * 10` does not
        // honor the 'round to nearest multiple of 5' requirement of the spec.
        var percent = (blackModules / (double)(size * size)) * 100;
        var prevMultipleOf5 = Math.Abs((int)Math.Floor(percent / 5) * 5 - 50) / 5;
        var nextMultipleOf5 = Math.Abs((int)Math.Ceiling(percent / 5) * 5 - 50) / 5;
        return Math.Min(prevMultipleOf5, nextMultipleOf5) * 10;
    }

    /// <summary>
    /// Maps between machine byte order and the logical little-endian order the SWAR pack/unpack constants assume (memory offset k = ulong byte k).
    /// The same swap works for both reads and writes; BitConverter.IsLittleEndian is a JIT-time constant, so little-endian codegen is unaffected.
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

    /// <summary>
    /// Packed mask template rows: [pattern * 12 + (row % 12)].
    /// Every mask formula depends on the row only via row%2, row%3 or (row/2)%2, periodic in lcm(2,3,4) = 12, and on the column with period 6, so 12 rows of 192 bits per pattern cover every matrix size (bits beyond a row's length are removed by ANDing with the allowed mask).
    /// </summary>
    private static readonly Row192[] _maskTemplates = BuildMaskTemplates();

    /// <summary>Low words of <see cref="_maskTemplates"/> for the single-word path.</summary>
    private static readonly ulong[] _maskTemplates64 = BuildMaskTemplates64();

    private static Row192[] BuildMaskTemplates()
    {
        var templates = new Row192[8 * 12];
        for (var p = 0; p < 8; p++)
        {
            for (var r = 0; r < 12; r++)
            {
                var rm2 = (byte)(r & 1);
                var rm3 = (byte)(r % 3);
                var rd2 = (byte)((r >> 1) & 1); // only the parity of row/2 matters (Pattern4)
                ulong w0 = 0, w1 = 0, w2 = 0;
                for (var c = 0; c < 192; c++)
                {
                    var cm2 = (byte)(c & 1);
                    var cm3 = (byte)(c % 3);
                    var cd3 = (byte)(c / 3);
                    var hit = p switch
                    {
                        0 => MaskPattern.Pattern0(rm2, cm2),
                        1 => MaskPattern.Pattern1(rm2),
                        2 => MaskPattern.Pattern2(cm3),
                        3 => MaskPattern.Pattern3(rm3, cm3),
                        4 => MaskPattern.Pattern4(rd2, cd3),
                        5 => MaskPattern.Pattern5(rm2, cm2, rm3, cm3),
                        6 => MaskPattern.Pattern6(rm2, cm2, rm3, cm3),
                        7 => MaskPattern.Pattern7(rm2, cm2, r, c),
                        _ => false
                    };
                    if (hit)
                    {
                        if (c < 64) w0 |= 1ul << c;
                        else if (c < 128) w1 |= 1ul << (c - 64);
                        else w2 |= 1ul << (c - 128);
                    }
                }
                templates[p * 12 + r] = new Row192(w0, w1, w2);
            }
        }
        return templates;
    }

    private static ulong[] BuildMaskTemplates64()
    {
        var templates = _maskTemplates;
        var result = new ulong[templates.Length];
        for (var i = 0; i < templates.Length; i++)
        {
            result[i] = templates[i].W0;
        }
        return result;
    }

    /// <summary>
    /// 192-bit row register (3 ulongs, LSB = column 0).
    /// Sized for the largest QR matrix (version 40, 177 modules); the fixed width keeps every operation branch-free regardless of the actual size.
    /// </summary>
    private readonly struct Row192
    {
        public readonly ulong W0, W1, W2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Row192(ulong w0, ulong w1, ulong w2)
        {
            W0 = w0;
            W1 = w1;
            W2 = w2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Row192 operator &(in Row192 a, in Row192 b) => new(a.W0 & b.W0, a.W1 & b.W1, a.W2 & b.W2);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Row192 operator ^(in Row192 a, in Row192 b) => new(a.W0 ^ b.W0, a.W1 ^ b.W1, a.W2 ^ b.W2);

        /// <summary>~(this ^ other), equality bits. High garbage must be masked by the caller.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Row192 Xnor(in Row192 other) => new(~(W0 ^ other.W0), ~(W1 ^ other.W1), ~(W2 ^ other.W2));

        /// <summary>~this &amp; mask.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Row192 AndNot(in Row192 mask) => new(~W0 & mask.W0, ~W1 & mask.W1, ~W2 & mask.W2);

        /// <summary>this &amp; ~other.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Row192 AndNotWith(in Row192 other) => new(W0 & ~other.W0, W1 & ~other.W1, W2 & ~other.W2);

        /// <summary>Logical shift right by k bits (1..63 only), pulling zeros in at the top.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Row192 ShiftRight(int k)
            => new((W0 >> k) | (W1 << (64 - k)), (W1 >> k) | (W2 << (64 - k)), W2 >> k);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Row192 ShiftLeft1()
            => new(W0 << 1, (W1 << 1) | (W0 >> 63), (W2 << 1) | (W1 >> 63));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int PopCount()
            => ModulePlacer.PopCount(W0) + ModulePlacer.PopCount(W1) + ModulePlacer.PopCount(W2);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong WordAt(int i) => i == 0 ? W0 : i == 1 ? W1 : W2;

        /// <summary>Returns a copy with bit x set to the given value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Row192 WithBit(int x, bool value)
        {
            var w0 = W0;
            var w1 = W1;
            var w2 = W2;
            var bit = 1ul << (x & 63);
            if (x < 64)
            {
                w0 = value ? w0 | bit : w0 & ~bit;
            }
            else if (x < 128)
            {
                w1 = value ? w1 | bit : w1 & ~bit;
            }
            else
            {
                w2 = value ? w2 | bit : w2 & ~bit;
            }
            return new Row192(w0, w1, w2);
        }

        /// <summary>Mask with bits 0..n-1 set (n in 0..192).</summary>
        public static Row192 MaskLow(int n)
        {
            var w0 = n >= 64 ? ulong.MaxValue : n <= 0 ? 0ul : (1ul << n) - 1;
            var w1 = n >= 128 ? ulong.MaxValue : n <= 64 ? 0ul : (1ul << (n - 64)) - 1;
            var w2 = n >= 192 ? ulong.MaxValue : n <= 128 ? 0ul : (1ul << (n - 128)) - 1;
            return new Row192(w0, w1, w2);
        }

        /// <summary>Packs a row of 0/1 module bytes into bits (see <see cref="PackRowBits64"/>).</summary>
        public static Row192 PackRowBits(ReadOnlySpan<byte> row)
        {
            ulong w0 = 0, w1 = 0, w2 = 0;
            var c = 0;
            for (; c + 8 <= row.Length; c += 8)
            {
                var u = NormalizeEndianness(MemoryMarshal.Read<ulong>(row.Slice(c)));
                var bits = (u * 0x0102040810204080UL) >> 56;
                if (c < 64) w0 |= bits << c;
                else if (c < 128) w1 |= bits << (c - 64);
                else w2 |= bits << (c - 128);
            }
            for (; c < row.Length; c++)
            {
                if (row[c] != 0)
                {
                    if (c < 64) w0 |= 1ul << c;
                    else if (c < 128) w1 |= 1ul << (c - 64);
                    else w2 |= 1ul << (c - 128);
                }
            }
            return new Row192(w0, w1, w2);
        }

        /// <summary>
        /// Reads 192 bits starting at an arbitrary bit offset of a byte buffer (LSB-first within bytes).
        /// The buffer must have at least 32 readable bytes from the offset's byte position (callers pad).
        /// </summary>
        public static Row192 FromBitSlice(ReadOnlySpan<byte> data, int bitOffset)
        {
            var byteOff = bitOffset >> 3;
            var sh = bitOffset & 7;
            var u0 = NormalizeEndianness(MemoryMarshal.Read<ulong>(data.Slice(byteOff)));
            var u1 = NormalizeEndianness(MemoryMarshal.Read<ulong>(data.Slice(byteOff + 8)));
            var u2 = NormalizeEndianness(MemoryMarshal.Read<ulong>(data.Slice(byteOff + 16)));
            if (sh == 0)
            {
                return new Row192(u0, u1, u2);
            }
            var u3 = NormalizeEndianness(MemoryMarshal.Read<ulong>(data.Slice(byteOff + 24)));
            var inv = 64 - sh;
            return new Row192((u0 >> sh) | (u1 << inv), (u1 >> sh) | (u2 << inv), (u2 >> sh) | (u3 << inv));
        }
    }
}
