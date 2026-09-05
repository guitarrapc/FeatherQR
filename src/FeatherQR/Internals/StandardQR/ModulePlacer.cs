using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Static utility class for placing QR code modules (patterns and data).
/// Handles all matrix manipulation operations during QR code generation.
/// Mask pattern selection lives in ModulePlacer.Masking.cs (bit-packed implementation).
/// </summary>
internal static partial class ModulePlacer
{
    /// <summary>
    /// Places encoded data and ECC words into the QR code matrix.
    /// Fills modules in zigzag pattern from bottom-right to top-left.
    /// </summary>
    /// <param name="buffer">QR code data structure to populate.</param>
    /// <param name="size">Side length of the matrix in modules, quiet zone excluded.</param>
    /// <param name="interleavedData">Interleaved data and ECC bytes.</param>
    /// <param name="blockedMask">blocked mask bytes.</param>
    /// <remarks>
    /// Bits are read MSB-first (most significant bit first) from the byte array.
    ///
    /// Data placement pattern (ISO/IEC 18004 Section 7.7.3):
    /// - Start from bottom-right corner
    /// - Move upwards in a 2-column strips (zigzag pattern).
    /// - Alternate direction at top/bottom edges.
    /// - Skip timing pattern column (column 6)
    /// - Fill out non-blocked modules
    ///
    /// Performance (see the micro-optimization findings log, ~2x over the
    /// per-module implementation at every version, zero allocations):
    /// - The stream is consumed strictly sequentially, so the next up-to-64 bits
    ///   are kept MSB-aligned in a ulong register and refilled 8 bytes at a time,
    ///   replacing an indexed byte load + variable shift per module.
    /// - The two modules of a strip row sit at adjacent bit indices (b, b-1) in
    ///   the blocked mask and ~90% of rows have both free: one 2-bit mask window
    ///   read + one 2-bit stream consume handles the pair on the fast path.
    /// - Once the stream is exhausted nothing can be written again, so the
    ///   remainder modules end the walk via return instead of a per-module guard.
    /// - Buffer/mask accesses go through refs so the JIT emits no bounds checks
    ///   in the row loop (measured ~15% at version 10); the index range is
    ///   validated once up front instead.
    /// </remarks>
    public static void PlaceDataWords(Span<byte> buffer, int size, ReadOnlySpan<byte> interleavedData, ReadOnlySpan<byte> blockedMask)
    {
        // Ref-based access below elides per-module bounds checks, so validate the
        // whole traversal range here. size >= 7 keeps the strip column x >= 1
        // (real QR sizes are 21..177), so b-1 never goes negative.
        // The module count is computed in long: an int size*size would overflow
        // for size >= 46341 and could wrap below the actual span lengths, letting
        // the unchecked writes run out of bounds. With long, any size whose square
        // exceeds int range can never satisfy the length checks and throws here;
        // for every size that passes, the int index arithmetic below is exact.
        if (size < 7)
            throw new ArgumentOutOfRangeException(nameof(size), $"size must be >= 7, got {size}");
        var totalModules = (long)size * size;
        if (buffer.Length < totalModules)
            throw new ArgumentException($"buffer too small: required {totalModules}, got {buffer.Length}", nameof(buffer));
        if (blockedMask.Length < (totalModules + 7) / 8)
            throw new ArgumentException($"blockedMask too small: required {(totalModules + 7) / 8}, got {blockedMask.Length}", nameof(blockedMask));

        var up = true;

        // Next up-to-64 stream bits, MSB-aligned; accBits of them are valid.
        var acc = 0ul;
        var accBits = 0;
        var bytePos = 0;

        ref var buf = ref MemoryMarshal.GetReference(buffer);
        ref var mask = ref MemoryMarshal.GetReference(blockedMask);

        for (var x = size - 1; x >= 0; x -= 2)
        {
            // Skip timing pattern column
            if (x == 6)
                x--;

            // Walk the strip via the flat index: right module at b = y * size + x,
            // left at b - 1; direction alternates per strip.
            var b = up ? (size - 1) * size + x : x;
            var step = up ? -size : size;

            for (var rows = 0; rows < size; rows++, b += step)
            {
                // rightmodule = b, leftmodule = b - 1
                // b tracks the right module's flat index directly; stepping by ±size avoids per-row y computation and re-multiplication.

                // Blocked bits for the row pair: w bit1 = blocked(b), bit0 = blocked(b-1).
                // |    `w` |  right `b` | left `b-1` |
                // | -----: | ---------: | ---------: |
                // | `0b00` |       free |       free |
                // | `0b01` |       free |    blocked |
                // | `0b10` |    blocked |       free |
                // | `0b11` |    blocked |    blocked |
                var byteIdx = b >> 3;
                var sh = b & 7;
                var w = sh == 0
                    ? (uint)(((Unsafe.Add(ref mask, byteIdx) & 1) << 1) | (Unsafe.Add(ref mask, byteIdx - 1) >> 7))
                    : ((uint)Unsafe.Add(ref mask, byteIdx) >> (sh - 1)) & 3u;

                if (w == 0 && accBits >= 2)
                {
                    // Fast path: both modules free AND >= 2 stream bits buffered.
                    // Taken for ~90% of rows: blocked regions (finder/timing/alignment/format)
                    // cover only ~10-15% of the matrix, and the accumulator dips below 2 bits
                    // at most once per 64 consumed bits.
                    // 1. w == 0, both right and left modules are free, so we can consume 2 bits from the accumulator.
                    // 2. accBits >= 2, we have at least 2 bits in the accumulator to write.

                    Unsafe.Add(ref buf, b) = (byte)(acc >> 63); // place right module (b) with the MSB of acc
                    Unsafe.Add(ref buf, b - 1) = (byte)((acc >> 62) & 1); // place left module (b-1) with the next bit of acc
                    acc <<= 2;
                    accBits -= 2;
                }
                else
                {
                    // Slow path: a module is blocked, or fewer than 2 bits are buffered.
                    // Handle each module independently; refill happens only when the
                    // accumulator is empty (a leftover single bit is consumed first).
                    if ((w & 2) == 0)
                    {
                        // w bit1 == 0, right module is free
                        if (accBits == 0 && !RefillAccumulator(interleavedData, ref bytePos, ref acc, ref accBits))
                            return;
                        Unsafe.Add(ref buf, b) = (byte)(acc >> 63);
                        acc <<= 1;
                        accBits--;
                    }
                    if ((w & 1) == 0)
                    {
                        // w bit0 == 0, left module is free. Same as above, but for the left module (b-1)
                        if (accBits == 0 && !RefillAccumulator(interleavedData, ref bytePos, ref acc, ref accBits))
                            return;
                        Unsafe.Add(ref buf, b - 1) = (byte)(acc >> 63);
                        acc <<= 1;
                        accBits--;
                    }
                }
            }

            // Alternate direction for zigzag pattern
            up = !up;
        }
    }

    /// <summary>
    /// Loads the next up-to-8 stream bytes MSB-aligned into the accumulator.
    /// Returns false when the stream is exhausted.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool RefillAccumulator(ReadOnlySpan<byte> data, ref int bytePos, ref ulong acc, ref int accBits)
    {
        var remaining = data.Length - bytePos;
        if (remaining >= 8)
        {
            acc = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(bytePos));
            accBits = 64;
            bytePos += 8;
            return true;
        }
        if (remaining == 0)
            return false;

        ulong v = 0;
        for (var i = 0; i < remaining; i++)
        {
            v = (v << 8) | data[bytePos + i];
        }
        acc = v << (64 - remaining * 8);
        accBits = remaining * 8;
        bytePos = data.Length;
        return true;
    }

    /// <summary>
    /// Reserves separator areas (white borders) around finder patterns.
    /// 1-module wide white border separates finder patterns from data area.
    /// </summary>
    /// <param name="size"></param>
    /// <param name="blockedModules"></param>
    /// <param name="blockedCount"></param>
    public static void ReserveSeparatorAreas(int size, Span<Rectangle> blockedModules, ref int blockedCount)
    {
        blockedModules[blockedCount++] = new Rectangle(7, 0, 1, 8);
        blockedModules[blockedCount++] = new Rectangle(0, 7, 7, 1);
        blockedModules[blockedCount++] = new Rectangle(0, size - 8, 8, 1);
        blockedModules[blockedCount++] = new Rectangle(7, size - 7, 1, 7);
        blockedModules[blockedCount++] = new Rectangle(size - 8, 0, 1, 8);
        blockedModules[blockedCount++] = new Rectangle(size - 7, 7, 7, 1);
    }

    /// <summary>
    /// Reserves areas for format and version information.
    /// These areas are filled later with actual format/version data.
    /// </summary>
    /// <param name="size"></param>
    /// <param name="version"></param>
    /// <param name="blockedModules"></param>
    /// <param name="blockedCount"></param>
    public static void ReserveVersionAreas(int size, int version, Span<Rectangle> blockedModules, ref int blockedCount)
    {
        blockedModules[blockedCount++] = new Rectangle(8, 0, 1, 6);
        blockedModules[blockedCount++] = new Rectangle(8, 7, 1, 1);
        blockedModules[blockedCount++] = new Rectangle(0, 8, 6, 1);
        blockedModules[blockedCount++] = new Rectangle(7, 8, 2, 1);
        blockedModules[blockedCount++] = new Rectangle(size - 8, 8, 8, 1);
        blockedModules[blockedCount++] = new Rectangle(8, size - 7, 1, 7);

        if (version >= 7)
        {
            blockedModules[blockedCount++] = new Rectangle(size - 11, 0, 3, 6);
            blockedModules[blockedCount++] = new Rectangle(0, size - 11, 6, 3);
        }
    }

    /// <summary>
    /// Places three finder patterns (position detection patterns).
    /// 7×7 patterns located at top-left, top-right, and bottom-left corners.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="size"></param>
    /// <param name="blockedModules"></param>
    /// <param name="blockedCount"></param>
    public static void PlaceFinderPatterns(Span<byte> buffer, int size, Span<Rectangle> blockedModules, ref int blockedCount)
    {
        ReadOnlySpan<Point> locations = stackalloc Point[3]
        {
            new Point(0, 0),
            new Point(size - 7, 0),
            new Point(0, size - 7),
        };

        foreach (var location in locations)
        {
            for (var x = 0; x < 7; x++)
            {
                for (var y = 0; y < 7; y++)
                {
                    if (!(((x == 1 || x == 5) && y > 0 && y < 6) || (x > 0 && x < 6 && (y == 1 || y == 5))))
                    {
                        var row = y + location.Y;
                        var col = x + location.X;
                        buffer[row * size + col] = 1;
                    }
                }
            }
            blockedModules[blockedCount++] = new Rectangle(location.X, location.Y, 7, 7);
        }
    }

    /// <summary>
    /// Places alignment patterns throughout the QR code.
    /// Number and positions vary by version (version 2+).
    /// 5×5 patterns help with image recognition and distortion correction.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="size"></param>
    /// <param name="alignmentPatternLocations"></param>
    /// <param name="blockedModules"></param>
    /// <param name="blockedCount"></param>
    public static void PlaceAlignmentPatterns(Span<byte> buffer, int size, List<Point> alignmentPatternLocations, Span<Rectangle> blockedModules, ref int blockedCount)
    {
        foreach (var loc in alignmentPatternLocations)
        {
            var alignmentPatternRect = new Rectangle(loc.X, loc.Y, 5, 5);
            var blocked = false;
            foreach (var blockedRect in blockedModules)
            {
                if (Intersects(alignmentPatternRect, blockedRect))
                {
                    blocked = true;
                    break;
                }
            }
            if (blocked)
            {
                continue;
            }

            for (var x = 0; x < 5; x++)
            {
                for (var y = 0; y < 5; y++)
                {
                    if (y == 0 || y == 4 || x == 0 || x == 4 || (x == 2 && y == 2))
                    {
                        var row = loc.Y + y;
                        var col = loc.X + x;
                        buffer[row * size + col] = 1;
                    }
                }
            }
            blockedModules[blockedCount++] = new Rectangle(loc.X, loc.Y, 5, 5);
        }
    }

    /// <summary>
    /// Places timing patterns (alternating dark/light modules).
    /// Horizontal and vertical lines at row 6 and column 6.
    /// Used for module coordinate mapping during decoding.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="size"></param>
    /// <param name="blockedModules"></param>
    /// <param name="blockedCount"></param>
    public static void PlaceTimingPatterns(Span<byte> buffer, int size, Span<Rectangle> blockedModules, ref int blockedCount)
    {
        for (var i = 8; i < size - 8; i++)
        {
            if (i % 2 == 0)
            {
                buffer[6 * size + i] = 1; // row 6
                buffer[i * size + 6] = 1; // col 6
            }
        }
        blockedModules[blockedCount++] = new Rectangle(6, 8, 1, size - 16);
        blockedModules[blockedCount++] = new Rectangle(8, 6, size - 16, 1);
    }

    /// <summary>
    /// Places the dark module (always dark/black).
    /// Located at position (8, 4*version + 9).
    /// Required by QR code specification for all versions.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="size"></param>
    /// <param name="version"></param>
    /// <param name="blockedModules"></param>
    /// <param name="blockedCount"></param>
    public static void PlaceDarkModule(Span<byte> buffer, int size, int version, Span<Rectangle> blockedModules, ref int blockedCount)
    {
        buffer[(4 * version + 9) * size + 8] = 1;
        blockedModules[blockedCount++] = new Rectangle(8, 4 * version + 9, 1, 1);
    }

    /// <summary>
    /// Places format information patterns around finder patterns.
    /// Contains error correction level and mask pattern information.
    /// Two identical 15-bit sequences for redundancy.
    /// </summary>
    /// <param name="buffer">QR code matrix buffer.</param>
    /// <param name="size">Side length of the matrix in modules, quiet zone excluded.</param>
    /// <param name="formatBits">15-bit format information (LSB first).</param>
    /// <remarks>
    /// Places two identical copies for redundancy (ISO/IEC 18004 Section 7.9):
    /// <code>
    /// - Copy 1: Around top-left and top-right finder patterns
    /// - Copy 2: Around top-left and bottom-left finder patterns
    /// </code>
    /// Bits are placed from LSB (bit 0) to MSB (bit 14).
    /// </remarks>
    public static void PlaceFormat(Span<byte> buffer, int size, ushort formatBits)
    {
        // Stack allocation: 15 tuples × 4 ints × 4 bytes = 240 bytes
        ReadOnlySpan<(int x1, int y1, int x2, int y2)> positions = stackalloc (int, int, int, int)[15]
        {
            ( 8, 0, size - 1, 8 ),
            ( 8, 1, size - 2, 8 ),
            ( 8, 2, size - 3, 8 ),
            ( 8, 3, size - 4, 8 ),
            ( 8, 4, size - 5, 8 ),
            ( 8, 5, size - 6, 8 ),
            ( 8, 7, size - 7, 8 ),
            ( 8, 8, size - 8, 8 ),
            ( 7, 8, 8, size - 7 ),
            ( 5, 8, 8, size - 6 ),
            ( 4, 8, 8, size - 5 ),
            ( 3, 8, 8, size - 4 ),
            ( 2, 8, 8, size - 3 ),
            ( 1, 8, 8, size - 2 ),
            ( 0, 8, 8, size - 1 ),
        };

        for (var i = 0; i < 15; i++)
        {
            var bit = (byte)((formatBits & (1 << i)) != 0 ? 1 : 0);
            var (x1, y1, x2, y2) = positions[i];
            buffer[y1 * size + x1] = bit;
            buffer[y2 * size + x2] = bit;
        }
    }

    /// <summary>
    /// Places version information patterns (version 7+ only).
    /// Two identical 3×6 patterns placed at top-right and bottom-left corners.
    /// </summary>
    /// <param name="buffer">QR code matrix buffer</param>
    /// <param name="size">QR code size in modules</param>
    /// <param name="versionBits">18-bit version information (LSB first)</param>
    /// <remarks>
    /// Places two identical 3×6 patterns (ISO/IEC 18004 Section 7.10):
    /// <code>
    /// - Pattern 1: Bottom-left corner (vertical)
    /// - Pattern 2: Top-right corner (horizontal)
    /// </code>
    /// Bits are placed from LSB (bit 0) to MSB (bit 17) in reading order.
    /// </remarks>
    public static void PlaceVersion(Span<byte> buffer, int size, uint versionBits)
    {
        for (var x = 0; x < 6; x++)
        {
            for (var y = 0; y < 3; y++)
            {
                var bitIndex = x * 3 + y;
                var bit = (byte)((versionBits & (1 << bitIndex)) != 0 ? 1 : 0);
                buffer[(y + size - 11) * size + x] = bit;
                buffer[x * size + (y + size - 11)] = bit;
            }
        }
    }

    /// <summary>
    /// Checks if two rectangles intersect.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Intersects(Rectangle r1, Rectangle r2)
    {
        return r2.X < r1.X + r1.Width
            && r1.X < r2.X + r2.Width
            && r2.Y < r1.Y + r1.Height
            && r1.Y < r2.Y + r2.Height;
    }

    /// <summary>
    /// Check if a module at given bit index is blocked using a bitmask.
    /// </summary>
    /// <param name="mask">Bitmask buffer</param>
    /// <param name="bitIndex">Linear index of module</param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsModuleBlocked(ReadOnlySpan<byte> mask, int bitIndex)
    {
        // Performance: O(1) constant-time lookup using bitwise operations.
        //
        // Bit extraction:
        // - Byte index = bitIndex >> 3 (equivalent to bitIndex / 8)
        // - Bit position = bitIndex & 7 (equivalent to bitIndex % 8)
        // - Mask = 1 << bit_position
        // - Result = (byte & mask) != 0
        //
        // Example (bitIndex = 10):
        // - Byte index: 10 >> 3 = 1 (second byte)
        // - Bit position: 10 & 7 = 2 (third bit from LSB)
        // - Mask: 1 << 2 = 0b00000100

        return (mask[bitIndex >> 3] & (1 << (bitIndex & 7))) != 0;
    }

    // private class/strusts

    /// <summary>
    /// Mask pattern implementations and scoring algorithm using pre-calculated modulo/division values.
    /// ISO/IEC 18004:2015 defines 8 mask patterns (0-7) to improve QR code readability.
    /// </summary>
    /// <remarks>
    /// Performance optimization strategy:
    /// <code>
    /// 1. Pre-calculate modulo and division values for rows/columns
    /// 2. Use bitwise operations (&amp;, ^) instead of modulo where possible
    /// 3. Avoid repeated Math.Floor calculations
    /// </code>
    ///
    /// Pre-calculated values (shared across all patterns):
    /// <code>
    /// - rMod2[i] = i % 2  (row modulo 2)
    /// - rDiv2[i] = i / 2  (row integer division by 2)
    /// - rMod3[i] = i % 3  (row modulo 3)
    /// - cMod2[i] = i % 2  (column modulo 2)
    /// - cDiv3[i] = i / 3  (column integer division by 3)
    /// - cMod3[i] = i % 3  (column modulo 3)
    /// </code>
    ///
    /// For mathematical background, see:
    /// <code>
    /// - ISO/IEC 18004:2015 Section 7.8.2 (Data Masking)
    /// - MaskPattern class for readable reference implementations
    /// </code>
    /// </remarks>
    private static class MaskPattern
    {
        /// <summary>
        /// Pattern 0: Checkerboard pattern
        /// </summary>
        /// <param name="rowMod2">Pre-calculated row modulo 2 value.</param>
        /// <param name="colMod2">Pre-calculated column modulo 2 value.</param>
        /// <remarks>
        /// <para>Formula</para>
        /// <code>
        /// (x + y) % 2 == 0
        /// </code>
        ///
        /// Visual Pattern (8×8 sample, ■=dark, □=light):
        /// <code>
        /// ■□■□■□■□
        /// □■□■□■□■
        /// ■□■□■□■□
        /// □■□■□■□■
        /// ■□■□■□■□
        /// □■□■□■□■
        /// ■□■□■□■□
        /// □■□■□■□■
        /// </code>
        ///
        /// Mathematical Properties:
        /// <code>
        /// (a % 2) + (b % 2) ≡ (a + b) % 2
        /// (a % 2) ⊕ (b % 2) == 0 ⟺ (a + b) % 2 == 0  (XOR equivalence)
        /// </code>
        ///
        /// Optimization:
        /// <code>
        /// Instead of: (row + col) % 2 == 0
        /// Use XOR:    (row % 2) ^ (col % 2) == 0
        /// Benefit:    XOR is faster than addition + modulo
        /// </code>
        ///
        /// Example:
        /// <code>
        /// (0,0): (0^0)==0 → true  ■
        /// (0,1): (0^1)==0 → false □
        /// (1,0): (1^0)==0 → false □
        /// (1,1): (1^1)==0 → true  ■
        /// </code>
        /// </remarks>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Pattern0(byte rowMod2, byte colMod2) => (rowMod2 ^ colMod2) == 0;

        /// <summary>
        /// Pattern 1: Horizontal stripes
        /// </summary>
        /// <param name="rowMod2">Pre-calculated row modulo 2 value.</param>
        /// <remarks>
        /// Formula:
        /// <code>
        /// y % 2 == 0
        /// </code>
        ///
        /// Visual Pattern (8×8 sample, ■=dark, □=light):
        /// <code>
        /// ■■■■■■■■
        /// □□□□□□□□
        /// ■■■■■■■■
        /// □□□□□□□□
        /// ■■■■■■■■
        /// □□□□□□□□
        /// ■■■■■■■■
        /// □□□□□□□□
        /// </code>
        ///
        /// Characteristics:
        /// <code>
        /// - Alternating horizontal rows
        /// - Independent of x coordinate
        /// - Period: 2 rows
        /// </code>
        ///
        /// Example:
        /// <code>
        /// Row 0: 0%2==0 → true  ■■■■
        /// Row 1: 1%2==0 → false □□□□
        /// Row 2: 2%2==0 → true  ■■■■
        /// Row 3: 3%2==0 → false □□□□
        /// </code>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Pattern1(byte rowMod2) => rowMod2 == 0;

        /// <summary>
        /// Pattern 2: Vertical stripes
        /// </summary>
        /// <param name="colMod3">Pre-calculated column modulo 3 value.</param>
        /// <remarks>
        /// Formula:
        /// <code>
        /// x % 3 == 0
        /// </code>
        ///
        /// Visual Pattern (9×8 sample, ■=dark, □=light):
        /// <code>
        /// ■□□■□□■□□
        /// ■□□■□□■□□
        /// ■□□■□□■□□
        /// ■□□■□□■□□
        /// ■□□■□□■□□
        /// ■□□■□□■□□
        /// ■□□■□□■□□
        /// ■□□■□□■□□
        /// </code>
        ///
        /// Characteristics:
        /// <code>
        /// - Alternating vertical columns
        /// - Independent of y coordinate
        /// - Period: 3 columns (wider than Pattern 1)
        /// </code>
        ///
        /// Example:
        /// <code>
        /// Col 0: 0%3==0 → true  ■
        /// Col 1: 1%3==0 → false □
        /// Col 2: 2%3==0 → false □
        /// Col 3: 3%3==0 → true  ■
        /// </code>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Pattern2(byte colMod3) => colMod3 == 0;

        // same as: public static bool Pattern3(byte rowMod3, byte colMod3) => (rowMod3 + colMod3) % 3 == 0;
        /// <summary>
        /// Pattern 3: Diagonal stripes
        /// </summary>
        /// <param name="rowMod3">Pre-calculated row modulo 3 value.</param>
        /// <param name="colMod3">Pre-calculated column modulo 3 value.</param>
        /// <remarks>
        /// Formula:
        /// <code>
        /// (x + y) % 3 == 0
        /// </code>
        ///
        /// Visual Pattern (9×9 sample, ■=dark, □=light):
        /// <code>
        /// ■□□■□□■□□
        /// □□■□□■□□■
        /// □■□□■□□■□
        /// ■□□■□□■□□
        /// □□■□□■□□■
        /// □■□□■□□■□
        /// ■□□■□□■□□
        /// □□■□□■□□■
        /// □■□□■□□■□
        /// </code>
        ///
        /// Mathematical Properties:
        /// <code>
        /// (a % 3) + (b % 3) can exceed 3
        /// Example: (2 % 3) + (2 % 3) = 2 + 2 = 4
        /// Therefore: Must apply modulo to sum: (2 + 2) % 3 = 1
        /// </code>
        ///
        /// Optimization - Avoid Modulo:
        /// <code>
        /// rowMod3 ∈ {0, 1, 2}
        /// colMod3 ∈ {0, 1, 2}
        /// sum = rowMod3 + colMod3 ∈ {0, 1, 2, 3, 4}
        ///
        /// sum % 3 == 0 ⟺ sum ∈ {0, 3}
        ///
        /// Instead of: (rowMod3 + colMod3) % 3 == 0
        /// Use:        sum == 0 || sum == 3
        /// Benefit:    Eliminates modulo operation
        /// </code>
        ///
        /// Diagonal Pattern:
        /// <code>
        /// Slope: -1/1 (45° diagonal)
        /// Period: 3 modules
        /// </code>
        ///
        /// Example:
        /// <code>
        /// (0,0): 0+0=0 → true  ■
        /// (0,1): 0+1=1 → false □
        /// (0,2): 0+2=2 → false □
        /// (1,2): 1+2=3 → true  ■
        /// (2,1): 2+1=3 → true  ■
        /// </code>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Pattern3(byte rowMod3, byte colMod3)
        {
            var sum = rowMod3 + colMod3;
            return sum == 0 || sum == 3;
        }

        /// <summary>
        /// Pattern 4: Combined horizontal and vertical grid
        /// </summary>
        /// <param name="rowDiv2">Pre-calculated row division 2 value.</param>
        /// <param name="colDiv3">Pre-calculated column division 3 value.</param>
        /// <remarks>
        /// Formula:
        /// <code>
        /// (⌊y/2⌋ + ⌊x/3⌋) % 2 == 0
        /// </code>
        ///
        /// Visual Pattern (12×12 sample, ■=dark, □=light):
        /// <code>
        /// ■■■□□□■■■□□□
        /// ■■■□□□■■■□□□
        /// □□□■■■□□□■■■
        /// □□□■■■□□□■■■
        /// ■■■□□□■■■□□□
        /// ■■■□□□■■■□□□
        /// □□□■■■□□□■■■
        /// □□□■■■□□□■■■
        /// ■■■□□□■■■□□□
        /// ■■■□□□■■■□□□
        /// □□□■■■□□□■■■
        /// □□□■■■□□□■■■
        /// </code>
        ///
        /// Mathematical Optimization:
        /// <code>
        /// ⌊y/2⌋ = y >> 1     (for non-negative integers)
        /// ⌊x/3⌋ = x / 3      (integer division)
        /// (a + b) % 2 ≡ (a + b) &amp; 1  (bitwise optimization)
        /// </code>
        ///
        /// Block Size:
        /// <code>
        /// Vertical blocks:   2 rows
        /// Horizontal blocks: 3 columns
        /// </code>
        ///
        /// Example:
        /// <code>
        /// (0,0): (0/2 + 0/3)%2 = (0+0)%2 = 0 → true  ■
        /// (3,0): (3/2 + 0/3)%2 = (1+0)%2 = 1 → false □
        /// (0,2): (0/2 + 2/3)%2 = (0+0)%2 = 0 → true  ■
        /// (3,2): (3/2 + 2/3)%2 = (1+0)%2 = 1 → false □
        /// </code>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Pattern4(byte rowDiv2, byte colDiv3) => ((rowDiv2 + colDiv3) & 1) == 0;

        // same as: public static bool Pattern5(int row, int col) => ((row * col) % 2) + ((row * col) % 3) == 0;
        /// <summary>
        /// Pattern 5: Complex grid pattern
        /// </summary>
        /// <param name="rowMod2">Pre-calculated row modulo 2 value.</param>
        /// <param name="rowMod3">Pre-calculated row modulo 3 value.</param>
        /// <param name="colMod2">Pre-calculated column modulo 2 value.</param>
        /// <param name="colMod3">Pre-calculated column modulo 3 value.</param>
        /// <remarks>
        /// Formula:
        /// <code>
        /// ((x·y) % 2) + ((x·y) % 3) == 0
        /// </code>
        ///
        /// Visual Pattern (12×12 sample, ■=dark, □=light):
        /// <code>
        /// ■■■■■■■■■■■■
        /// ■□■□■□■□■□■□
        /// ■■□□□□■■□□□□
        /// ■□□■□■□□□■□■
        /// ■■□□□□■■□□□□
        /// ■□■□■□■□■□■□
        /// ■■■■■■□□□□□□
        /// ■□■□■□□■□■□■
        /// ■■□□□□□□□□□□
        /// ■□□■□■□■□■□■
        /// ■■□□□□□□□□□□
        /// ■□■□■□□■□■□■
        /// </code>
        ///
        /// Mathematical Properties:
        /// <code>
        /// ((x·y) % 2) + ((x·y) % 3) == 0
        /// ⟺ (x·y) % 2 == 0 AND (x·y) % 3 == 0
        /// ⟺ (x·y) % lcm(2,3) == 0
        /// ⟺ (x·y) % 6 == 0
        /// </code>
        ///
        /// Optimization - Avoid Multiplication:
        /// <code>
        /// (x·y) % 2 == 0  ⟺  (x % 2 == 0) OR (y % 2 == 0)  (either is even)
        /// (x·y) % 3 == 0  ⟺  (x % 3 == 0) OR (y % 3 == 0)  (either is divisible by 3)
        ///
        /// Therefore:
        /// ((x·y) % 2) + ((x·y) % 3) == 0
        /// ⟺ (rowMod2 == 0 OR colMod2 == 0) AND (rowMod3 == 0 OR colMod3 == 0)
        ///
        /// Benefit: Eliminates multiplication and modulo operations entirely
        /// </code>
        ///
        /// Example:
        /// <code>
        /// # Basic
        /// (0,0): 0%2 + 0%3 = 0+0 = 0 → true  ■
        /// (2,3): 6%2 + 6%3 = 0+0 = 0 → true  ■
        /// (1,1): 1%2 + 1%3 = 1+1 = 2 → false □
        /// (2,2): 4%2 + 4%3 = 0+1 = 1 → false □
        ///
        /// # Optimized:
        /// (0,0): (0==0 OR 0==0) AND (0==0 OR 0==0) → true  ■
        /// (2,3): (0==0 OR 1==0) AND (2==0 OR 0==0) → true  ■
        /// (1,1): (1==0 OR 1==0) AND (1==0 OR 1==0) → false □
        /// (2,2): (0==0 OR 0==0) AND (2==0 OR 2==0) → false □
        /// </code>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Pattern5(byte rowMod2, byte colMod2, byte rowMod3, byte colMod3) => (rowMod2 == 0 || colMod2 == 0) && (rowMod3 == 0 || colMod3 == 0);

        // same as: public static bool Pattern6(int row, int col) => (((row * col) % 2) + ((row * col) % 3) & 1) == 0;
        /// <summary>
        /// Pattern 6: Alternating complex pattern
        /// </summary>
        /// <param name="rowMod2">Pre-calculated row modulo 2 value.</param>
        /// <param name="rowMod3">Pre-calculated row modulo 3 value.</param>
        /// <param name="colMod2">Pre-calculated column modulo 2 value.</param>
        /// <param name="colMod3">Pre-calculated column modulo 3 value.</param>
        /// <remarks>
        /// Formula:
        /// <code>
        /// (((x·y) % 2) + ((x·y) % 3)) % 2 == 0
        /// </code>
        ///
        /// Visual Pattern (12×12 sample, ■=dark, □=light):
        /// <code>
        /// ■■■■■■■■■■■■
        /// ■□■□■□■□■□■□
        /// ■■■■□□■■■■□□
        /// ■□□■□■■□□■□■
        /// ■■□□■■■■□□■■
        /// ■□■□■□■□■□■□
        /// ■■■■■■■■■■■■
        /// ■□■□■□□■□■□■
        /// ■■■■□□□□■■□□
        /// ■□□■□■□■□■□■
        /// ■■□□■■□□■■■■
        /// ■□■□■□□■□■□■
        /// </code>
        ///
        /// Mathematical Properties:
        /// <code>
        /// Similar to Pattern 5, but checks parity of sum
        /// Sum can be: 0, 1, 2, or 3
        /// Pattern is dark when sum is even (0 or 2)
        /// </code>
        ///
        /// Optimization - Avoid Multiplication and Modulo:
        /// <code>
        /// Break down into two components:
        ///
        /// 1. (x·y) % 2 parity:
        ///    - Product is odd only when both x and y are odd
        ///    - rowMod2 &amp; colMod2  (bitwise AND gives 1 only if both are 1)
        ///
        /// 2. (x·y) % 3 parity (using 3×3 lookup table):
        ///    - rowMod3 ∈ {0,1,2}, colMod3 ∈ {0,1,2}
        ///    - Product table:
        ///      ×   0  1  2
        ///      0   0  0  0  (all even → parity 0)
        ///      1   0  1  2  (1 odd, 2 even)
        ///      2   0  2  4  (all even → parity 0)
        ///    - Parity is odd (1) only when: (1,1) or (2,2)
        ///    - Condition: rowMod3 != 0 AND rowMod3 == colMod3
        ///
        /// Final result:
        ///    ((p2) + (p3)) % 2 == 0  ⟺  p2 ^ p3 == 0  (XOR for parity)
        ///
        /// Benefit: Eliminates multiplication and modulo operations entirely
        /// </code>
        ///
        /// Example:
        /// <code>
        /// # Basic formula:
        /// (0,0): (0%2 + 0%3)%2 = (0+0)%2 = 0 → true  ■
        /// (1,1): (1%2 + 1%3)%2 = (1+1)%2 = 0 → true  ■
        /// (2,1): (2%2 + 2%3)%2 = (0+2)%2 = 0 → true  ■
        /// (1,2): (2%2 + 2%3)%2 = (0+2)%2 = 0 → true  ■
        ///
        /// # Optimized:
        /// (0,0): p2=0 &amp; 0=0, p3=(0!=0 &amp;&amp; 0==0)=0 → 0^0=0 → true  ■
        /// (1,1): p2=1 &amp; 1=1, p3=(1!=0 &amp;&amp; 1==1)=1 → 1^1=0 → true  ■
        /// (2,1): p2=0 &amp; 1=0, p3=(2!=0 &amp;&amp; 2==1)=0 → 0^0=0 → true  ■
        /// (1,2): p2=1 &amp; 0=0, p3=(1!=0 &amp;&amp; 1==2)=0 → 0^0=0 → true  ■
        /// </code>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Pattern6(byte rowMod2, byte colMod2, byte rowMod3, byte colMod3)
        {
            // (xy) % 2 parity: odd only when both x and y are odd
            var p2 = rowMod2 & colMod2;

            // (xy) % 3 parity: odd only when (1,1) or (2,2)
            // Using truth table instead of multiplication:
            //   0×0=0(even), 0×1=0(even), 0×2=0(even)
            //   1×0=0(even), 1×1=1(odd),  1×2=2(even)
            //   2×0=0(even), 2×1=2(even), 2×2=4(even → 4%3=1 odd)
            var p3Odd = (rowMod3 != 0 && rowMod3 == colMod3) ? 1 : 0;

            // Sum parity: even when p2 and p3Odd have the same parity (XOR == 0)
            return (p2 ^ p3Odd) == 0;
        }

        /// <summary>
        /// Pattern 7: Checkerboard and grid combination
        /// </summary>
        /// <param name="rowMod2">Pre-calculated row modulo 2 value.</param>
        /// <param name="colMod2">Pre-calculated column modulo 2 value.</param>
        /// <param name="row">Row index.</param>
        /// <param name="col">Column index.</param>
        /// <remarks>
        /// Formula:
        /// <code>
        /// (((x+y) % 2) + ((x·y) % 3)) % 2 == 0
        /// </code>
        ///
        /// Visual Pattern (12×12 sample, ■=dark, □=light):
        /// <code>
        /// ■□■□■□■□■□■□
        /// □■■■□■□■■■□■
        /// ■■□■■■■□■■■■
        /// □■■□■□□■■□■□
        /// ■□■■□■■□■■□■
        /// □■■■■■□■■■■■
        /// ■□■□■□■□■□■□
        /// □■■■□■□□■■□□
        /// ■■□■■■■□□■■■
        /// □■■□■□□■■□□■
        /// ■□■■□■■□■■□■
        /// □■■■■■□■■■■□
        /// </code>
        ///
        /// Hybrid Pattern:
        /// <code>
        /// Combines two components:
        /// 1. Checkerboard: (x+y) % 2
        /// 2. Product grid: (x·y) % 3
        /// Result is dark when their sum is even
        /// </code>
        ///
        /// Optimization Strategy:
        /// <code>
        /// (x+y) % 2: Use pre-calculated rowMod2 + colMod2
        /// (x·y) % 3: Calculate at runtime (cannot pre-calculate)
        ///
        /// Mathematical property: (a%2) + (b%2) ∈ {0, 1, 2}
        /// Final check: ((sum) + (product%3)) &amp; 1 == 0
        /// </code>
        ///
        /// Example:
        /// <code>
        /// (0,0): ((0+0)%2 + 0%3)%2 = (0+0)%2 = 0 → true  ■
        /// (0,1): ((0+1)%2 + 0%3)%2 = (1+0)%2 = 1 → false □
        /// (1,1): ((1+1)%2 + 1%3)%2 = (0+1)%2 = 1 → false □
        /// (2,3): ((2+3)%2 + 6%3)%2 = (1+0)%2 = 1 → false □
        /// </code>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Pattern7(byte rowMod2, byte colMod2, int row, int col) => (((rowMod2 + colMod2) + ((row * col) % 3)) & 1) == 0;
    }
}
