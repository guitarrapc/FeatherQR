using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace FeatherQR.Internals.RmQr;

/// <summary>
/// rMQR module placement (ISO/IEC 23941 6.3, 7.7-7.9): function patterns, both
/// format-information copies, and the two-column zigzag data placement with the
/// single fixed data mask. Writes a byte-per-module core matrix (0 light, 1 dark,
/// row-major over the symbol width, quiet zone excluded).
/// </summary>
/// <remarks>
/// Two implementations live here and are held to byte parity by
/// <c>RmQRModulePlacerParityTest</c>:
/// <list type="bullet">
/// <item><see cref="PlaceSymbolReference"/> — the readable per-module reference
/// (<see cref="PlaceFunctionModules"/>, <see cref="PlaceFormat"/>,
/// <see cref="PlaceData"/>). The function-module predicate
/// (<see cref="IsFunctionModule"/>) and the mask (<see cref="GetMaskBit"/>) are the
/// single source of truth the matrix decoder reuses, so both sides always agree.</item>
/// <item><see cref="PlaceSymbol(Span{byte}, RmQRVersion, RmQREccLevel, ReadOnlySpan{byte})"/>
/// is the fast path (benchmark-driven, 16-27x over the reference). Everything derived
/// from the version alone is built once and cached
/// (<see cref="Layout"/>); placement is then a template copy, one vector pass that
/// expands the message bits and XORs the masks, and a store pass. The store pass is
/// documented on <see cref="ScatterPairs"/>, its ARM64 tier in
/// RmQRModulePlacer.Simd.Arm.cs. Zero allocations after the one-time tables.</item>
/// </list>
///
/// Geometry (0-based, h = height, w = width):
/// finder 7×7 at (0,0) with light separators col 7 (rows 0-7) and row 7 (cols 0-7);
/// sub-finder 5×5 at (h-5, w-5); timing patterns on rows 0 / h-1 (dark at even
/// columns) and cols 0 / w-1 (dark at even rows); corner patterns (0,w-2), (1,w-1)
/// dark with (1,w-2) light and (h-2,0), (h-1,1) dark with (h-2,1) light (on height 9
/// the separator row 7 overrides (h-2,0) to light); vertical
/// timing columns (RmQRConstants.GetAlignmentColumns) dark at even rows with a 3×3
/// alignment pattern (dark ring, light center) at rows 0-2 and h-3..h-1; format
/// copy 1 in rows 1-5 × cols 8-10 (bit = col-major index) plus col 11 rows 1-3
/// (bits 15-17); format copy 2 in rows h-6..h-2 × cols w-8..w-6 plus row h-6 cols
/// w-5..w-3. Data walks column pairs from (w-2, w-3) leftward, upward first, right
/// column first, skipping function modules; bits beyond the final message are
/// remainder bits and are light before masking.
/// </remarks>
internal static partial class RmQRModulePlacer
{
    /// <summary>The single rMQR data mask (ISO/IEC 23941 7.8): dark when ((row / 2) + (col / 3)) is even.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool GetMaskBit(int row, int col) => (((row >> 1) + col / 3) & 1) == 0;

    /// <summary>
    /// Whether (row, col) is a function module (finder, separators, sub-finder,
    /// timing, corners, alignment / vertical timing, or format information) for the
    /// version; everything else is a data or remainder module.
    /// </summary>
    public static bool IsFunctionModule(RmQRVersion version, int row, int col)
    {
        var height = RmQRConstants.GetHeight(version);
        var width = RmQRConstants.GetWidth(version);

        // Edge timing patterns (row 0 / h-1, col 0 / w-1) include the corner cells.
        if (row == 0 || row == height - 1 || col == 0 || col == width - 1)
            return true;
        // Finder (7×7) with its separators: col 7 rows 0-7 and row 7 cols 0-7.
        if (row <= 7 && col <= 7)
            return true;
        // Sub-finder 5×5, bottom-right.
        if (row >= height - 5 && col >= width - 5)
            return true;
        // Corner patterns' inner light modules.
        if ((row == 1 && col == width - 2) || (row == height - 2 && col == 1))
            return true;
        // Format information, finder side.
        if (row >= 1 && row <= 5 && col >= 8 && col <= 10)
            return true;
        if (row >= 1 && row <= 3 && col == 11)
            return true;
        // Format information, sub-finder side.
        if (row >= height - 6 && row <= height - 2 && col >= width - 8 && col <= width - 6)
            return true;
        if (row == height - 6 && col >= width - 5 && col <= width - 3)
            return true;
        // Vertical timing columns and their 3×3 alignment patterns at both ends.
        var alignment = RmQRConstants.GetAlignmentColumns(version);
        for (var i = 0; i < alignment.Length; i++)
        {
            var c = alignment[i];
            if (col == c)
                return true;
            if ((row <= 2 || row >= height - 3) && (col == c - 1 || col == c + 1))
                return true;
        }

        return false;
    }


    /// <summary>
    /// Writes the complete symbol: function patterns, both format copies, and the
    /// masked final message (data + ECC + remainder) into <paramref name="core"/>
    /// (byte per module, row-major over the version's width; every module is written).
    /// Fast path; see the class remarks.
    /// </summary>
    /// <param name="core">At least width × height bytes.</param>
    /// <param name="version">Symbol version.</param>
    /// <param name="eccLevel">ECC level (format information only; the message is already ECC-encoded).</param>
    /// <param name="finalMessage">Interleaved final message from <see cref="RmQRCodewordEncoder"/> (at least total codewords bytes).</param>
    public static void PlaceSymbol(Span<byte> core, RmQRVersion version, RmQREccLevel eccLevel, ReadOnlySpan<byte> finalMessage)
    {
        var width = RmQRConstants.GetWidth(version);
        var height = RmQRConstants.GetHeight(version);
        if (core.Length < width * height)
            throw new ArgumentException($"Core buffer too small: required {width * height} bytes ({width}x{height}), got {core.Length}.", nameof(core));
        PlaceSymbol(core, width, version, eccLevel, finalMessage);
    }

    /// <summary>
    /// Strided variant: writes the symbol into a wider matrix whose rows are
    /// <paramref name="stride"/> bytes apart (e.g. a quiet-zoned destination, with
    /// <paramref name="destination"/> starting at the top-left core module). Only the
    /// width × height core modules are written; the bytes between rows are untouched.
    /// </summary>
    /// <param name="destination">At least (height − 1) × stride + width bytes.</param>
    /// <param name="stride">Row pitch in bytes, at least the symbol width.</param>
    /// <param name="version">Symbol version.</param>
    /// <param name="eccLevel">ECC level (format information only).</param>
    /// <param name="finalMessage">Interleaved final message (at least total codewords bytes).</param>
    public static void PlaceSymbol(Span<byte> destination, int stride, RmQRVersion version, RmQREccLevel eccLevel, ReadOnlySpan<byte> finalMessage)
        => PlaceSymbol(destination, stride, version, eccLevel, finalMessage, PlaceKernel.Auto);

    private static void PlaceSymbolCore(Span<byte> destination, int stride, RmQRVersion version, RmQREccLevel eccLevel, ReadOnlySpan<byte> finalMessage, PlaceKernel kernel)
    {
        var height = RmQRConstants.GetHeight(version);
        var width = RmQRConstants.GetWidth(version);
        var totalCodewords = RmQRConstants.GetTotalCodewordCount(version);
        if (stride < width)
            throw new ArgumentException($"Stride {stride} is narrower than the symbol width {width}.", nameof(stride));
        var required = (height - 1) * stride + width;
        if (destination.Length < required)
            throw new ArgumentException($"Destination buffer too small: required {required} bytes ({width}x{height}, stride {stride}), got {destination.Length}.", nameof(destination));
        if (finalMessage.Length < totalCodewords)
            throw new ArgumentException($"Final message too short: required {totalCodewords} codewords, got {finalMessage.Length}.", nameof(finalMessage));

        var layout = GetLayout(version);
        var count = layout.DataModuleCount;
        var template = layout.GetTemplate(version, eccLevel);
        var message = finalMessage.Slice(0, totalCodewords);
        destination = destination.Slice(0, required);

        // Bit array scratch: one byte per data module (message bits, then the light
        // remainder bits), mask already applied. Fixed stack budget, pool fallback.
        if (count <= StackBitBudget)
        {
            Span<byte> bits = stackalloc byte[StackBitBudget + VectorSlack];
            PlaceCore(destination, stride, layout, template, height, width, message, bits, kernel);
        }
        else
        {
            var rented = ArrayPool<byte>.Shared.Rent(count + VectorSlack);
            try
            {
                PlaceCore(destination, stride, layout, template, height, width, message, rented, kernel);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    // 512 modules covers every version with <= 63 total codewords; larger symbols rent.
    // Both paths carry VectorSlack bytes past the module count: the AVX2 expand step
    // writes 32 bytes per 4 message bytes, and the ARM64 store tier reads a fixed 32
    // bytes per column pair, so the last pair of the last transpose block reads past
    // the module count. The measured worst case is 20 bytes of over-read (R7x43;
    // 22 in theory, at rows == 5), so 32 leaves a 10-byte margin, not a large one —
    // re-measure before shrinking it. Garbage in the slack cannot reach the output:
    // the UZP/TBL/ZIP chain is a pure permutation and every stored byte comes from a
    // lane below `rows`.
    private const int StackBitBudget = 512;
    private const int VectorSlack = 32;

    /// <summary>
    /// Reference composition (per-module painters), the oracle the fast path is
    /// parity-tested against; also builds the cached tables.
    /// </summary>
    internal static void PlaceSymbolReference(Span<byte> core, RmQRVersion version, RmQREccLevel eccLevel, ReadOnlySpan<byte> finalMessage)
    {
        var height = RmQRConstants.GetHeight(version);
        var width = RmQRConstants.GetWidth(version);
        var totalCodewords = RmQRConstants.GetTotalCodewordCount(version);
        if (core.Length < width * height)
            throw new ArgumentException($"Core buffer too small: required {width * height} bytes ({width}x{height}), got {core.Length}.", nameof(core));
        if (finalMessage.Length < totalCodewords)
            throw new ArgumentException($"Final message too short: required {totalCodewords} codewords, got {finalMessage.Length}.", nameof(finalMessage));

        core = core.Slice(0, width * height);
        PlaceFunctionModules(core, version, height, width);
        PlaceFormat(core, version, eccLevel, height, width);
        PlaceData(core, version, height, width, finalMessage.Slice(0, totalCodewords));
    }

    // ---------------------------------------------------------------
    // Fast path
    // ---------------------------------------------------------------

    /// <summary>Whether the ARM64 block/run store tier runs on this machine (parity tests skip it otherwise).</summary>
    internal static bool IsNeonTierSupported =>
#if NET8_0_OR_GREATER
        System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported;
#else
        false;
#endif

    /// <summary>Which store kernel to run; anything but <see cref="PlaceKernel.Auto"/> is for parity tests.</summary>
    internal enum PlaceKernel
    {
        /// <summary>The fastest tier this machine supports.</summary>
        Auto,
        /// <summary>
        /// The whole portable path — SWAR bit expansion plus pair stores and index
        /// scatter — on every target, exactly what netstandard2.0/2.1 and non-SIMD
        /// CPUs run.
        /// </summary>
        Portable,
        /// <summary>ARM64 register transpose blocks + row runs + single scatter.</summary>
        Neon,
    }

    /// <summary>
    /// Kernel-selecting entry; <paramref name="kernel"/> pins one tier for parity tests.
    /// Pinning a tier this machine cannot run throws rather than falling back: a parity
    /// test that silently compares the portable kernel against itself is worse than no
    /// test at all, because it stays green while the tier it names goes unexercised.
    /// </summary>
    internal static void PlaceSymbol(Span<byte> destination, int stride, RmQRVersion version, RmQREccLevel eccLevel, ReadOnlySpan<byte> finalMessage, PlaceKernel kernel)
    {
        if (kernel == PlaceKernel.Neon && !IsNeonTierSupported)
            throw new PlatformNotSupportedException($"{nameof(PlaceKernel)}.{nameof(PlaceKernel.Neon)} was pinned, but the ARM64 store tier does not run on this machine. Guard the call with {nameof(IsNeonTierSupported)}.");
        PlaceSymbolCore(destination, stride, version, eccLevel, finalMessage, kernel);
    }

    private static void PlaceCore(Span<byte> destination, int stride, Layout layout, byte[] template, int height, int width, ReadOnlySpan<byte> finalMessage, Span<byte> bits, PlaceKernel kernel)
    {
        if (stride == width)
        {
            template.AsSpan().CopyTo(destination);
        }
        else
        {
            for (var row = 0; row < height; row++)
                template.AsSpan(row * width, width).CopyTo(destination.Slice(row * stride, width));
        }
        ExpandBitsMasked(finalMessage, layout.Masks, layout.DataModuleCount, bits, kernel);
#if NET8_0_OR_GREATER
        if (IsNeonTierSupported && kernel != PlaceKernel.Portable)
        {
            // Literal, not `stride != width`: ScatterPairs below is AggressiveInlining
            // and branches on `strided` inside its per-pair loop, so a constant argument
            // folds those branches away, and both call sites are written the same way.
            // ScatterNeon is deliberately NOT inlined (it is far too large) and tests
            // `strided` once, outside its loops, so there the literal buys only symmetry.
            if (stride == width)
                ScatterNeon(destination, bits, layout, height, width, strided: false);
            else
                ScatterNeon(destination, bits, layout, height, stride, strided: true);
            return;
        }
#endif
        ref var dest = ref MemoryMarshal.GetReference(destination);
        ref var src = ref MemoryMarshal.GetReference(bits);
        if (stride == width)
            ScatterPairs(ref dest, ref src, layout, height, width, strided: false);
        else
            ScatterPairs(ref dest, ref src, layout, height, stride, strided: true);
    }

    /// <summary>
    /// bits[i] = message bit i (MSB first) XOR mask[i] for i &lt; 8 × message length;
    /// remainder positions up to <paramref name="count"/> get the mask only (light).
    /// One vector pass: pshufb replicates each message byte over 8 lanes, an AND with
    /// the per-lane bit mask + compare-equal yields 0/1 bytes, XOR with the mask table.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ExpandBitsMasked(ReadOnlySpan<byte> message, byte[] masks, int count, Span<byte> bits, PlaceKernel kernel)
    {
        var byteCount = message.Length;
        ref var src = ref MemoryMarshal.GetReference(message);
        ref var msk = ref MemoryMarshal.GetReference(masks.AsSpan());
        ref var dst = ref MemoryMarshal.GetReference(bits);
        var k = 0;
#if NET8_0_OR_GREATER
        // One predictable compare per placement, not per iteration. It buys the SWAR
        // tail below real coverage: on every machine the suite runs on, some vector
        // tier consumes the whole message, so without this the portable expand — which
        // is the WHOLE loop on netstandard2.0/2.1 and non-SIMD targets — is unreachable
        // from any test.
        if (kernel != PlaceKernel.Portable)
        {
            if (Avx2.IsSupported)
            {
                // 4 message bytes -> 32 module bytes per step; the uint broadcast puts the
                // same 4 bytes in both 128-bit lanes, and the in-lane shuffle picks byte 0..3
                var sel = Vector256.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3);
                var bitm = Vector256.Create((byte)128, 64, 32, 16, 8, 4, 2, 1, 128, 64, 32, 16, 8, 4, 2, 1, 128, 64, 32, 16, 8, 4, 2, 1, 128, 64, 32, 16, 8, 4, 2, 1);
                var one = Vector256.Create((byte)1);
                for (; k + 4 <= byteCount; k += 4)
                {
                    var v = Vector256.Create(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, k))).AsByte();
                    var m = Avx2.Shuffle(v, sel) & bitm;
                    ((Vector256.Equals(m, bitm) & one) ^ Vector256.LoadUnsafe(ref msk, (nuint)(k * 8))).StoreUnsafe(ref dst, (nuint)(k * 8));
                }
            }
            if (Ssse3.IsSupported)
            {
                var sel = Vector128.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1);
                var bitm = Vector128.Create((byte)128, 64, 32, 16, 8, 4, 2, 1, 128, 64, 32, 16, 8, 4, 2, 1);
                var one = Vector128.Create((byte)1);
                for (; k + 2 <= byteCount; k += 2)
                {
                    var v = Vector128.Create(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref src, k))).AsByte();
                    var m = Ssse3.Shuffle(v, sel) & bitm;
                    ((Vector128.Equals(m, bitm) & one) ^ Vector128.LoadUnsafe(ref msk, (nuint)(k * 8))).StoreUnsafe(ref dst, (nuint)(k * 8));
                }
            }
            if (AdvSimd.Arm64.IsSupported)
            {
                // Same 16 modules per step as SSSE3, but CMTST (0xFF where (a & b) != 0)
                // is the per-lane bit test x86 lacks, so the AND + compare-equal pair is
                // one instruction; the broadcast is a load-replicate straight from memory.
                var sel = Vector128.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1);
                var bitm = Vector128.Create((byte)128, 64, 32, 16, 8, 4, 2, 1, 128, 64, 32, 16, 8, 4, 2, 1);
                var one = Vector128.Create((byte)1);
                for (; k + 2 <= byteCount; k += 2)
                {
                    var v = Vector128.Create(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref src, k))).AsByte();
                    var repl = AdvSimd.Arm64.VectorTableLookup(v, sel);
                    ((AdvSimd.CompareTest(repl, bitm) & one) ^ Vector128.LoadUnsafe(ref msk, (nuint)(k * 8))).StoreUnsafe(ref dst, (nuint)(k * 8));
                }
            }
        }
#endif
        // Portable tail (whole message on netstandard and non-SIMD targets): the
        // multiply spreads bit 7-j of the byte to bit 8j+7 of the product, one source
        // bit per product bit so no carries can occur, and the shift + mask leaves the
        // eight module bytes in one register — one multiply and one 8-byte store
        // instead of eight loads, shifts and byte stores. Little-endian only: the
        // product's byte order is the module order.
        if (BitConverter.IsLittleEndian)
        {
            for (; k < byteCount; k++)
            {
                ulong b = Unsafe.Add(ref src, k);
                var expanded = ((b * 0x8040201008040201UL) >> 7) & 0x0101010101010101UL;
                ref var d = ref Unsafe.Add(ref dst, k * 8);
                Unsafe.WriteUnaligned(ref d, expanded ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref msk, k * 8)));
            }
        }
        for (; k < byteCount; k++)
        {
            int b = Unsafe.Add(ref src, k);
            ref var d = ref Unsafe.Add(ref dst, k * 8);
            ref var m = ref Unsafe.Add(ref msk, k * 8);
            d = (byte)(((b >> 7) & 1) ^ m);
            Unsafe.Add(ref d, 1) = (byte)(((b >> 6) & 1) ^ Unsafe.Add(ref m, 1));
            Unsafe.Add(ref d, 2) = (byte)(((b >> 5) & 1) ^ Unsafe.Add(ref m, 2));
            Unsafe.Add(ref d, 3) = (byte)(((b >> 4) & 1) ^ Unsafe.Add(ref m, 3));
            Unsafe.Add(ref d, 4) = (byte)(((b >> 3) & 1) ^ Unsafe.Add(ref m, 4));
            Unsafe.Add(ref d, 5) = (byte)(((b >> 2) & 1) ^ Unsafe.Add(ref m, 5));
            Unsafe.Add(ref d, 6) = (byte)(((b >> 1) & 1) ^ Unsafe.Add(ref m, 6));
            Unsafe.Add(ref d, 7) = (byte)((b & 1) ^ Unsafe.Add(ref m, 7));
        }
        // remainder bits (0..7 per version): light before masking -> mask only
        for (var i = byteCount * 8; i < count; i++)
            Unsafe.Add(ref dst, i) = Unsafe.Add(ref msk, i);
    }

    /// <summary>
    /// Store pass. Clean pairs: the walk visits (row, col) then (row, col-1), so the two
    /// consecutive bit-array bytes land at core[row, col-1..col] byte-swapped, one
    /// 16-bit store per row walking the rows in the pair's direction. Other pairs:
    /// scatter through the index table (core offsets when the destination pitch is
    /// the symbol width, row/col codes × the pitch otherwise; <paramref name="strided"/>
    /// is a JIT-time constant at both call sites).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScatterPairs(ref byte dest, ref byte src, Layout layout, int height, int stride, bool strided)
    {
        ref var idx = ref MemoryMarshal.GetReference(layout.Index.AsSpan());
        ref var rowCol = ref MemoryMarshal.GetReference(layout.RowCol.AsSpan());
        var pairs = layout.Pairs;
        var rows = (nuint)(height - 2);
        var pitch = (nuint)stride;
        for (var p = 0; p < pairs.Length; p++)
        {
            var seg = pairs[p];
            if (seg.Clean)
            {
                var k = (nuint)seg.Start;
                if (seg.Upward)
                {
                    ref var d = ref Unsafe.Add(ref dest, (nuint)((height - 2) * stride + seg.Col - 1));
                    for (nuint r = 0; r < rows; r++)
                    {
                        Unsafe.WriteUnaligned(ref d, SwapPair(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref src, k))));
                        k += 2;
                        d = ref Unsafe.Subtract(ref d, pitch);
                    }
                }
                else
                {
                    ref var d = ref Unsafe.Add(ref dest, (nuint)(stride + seg.Col - 1));
                    for (nuint r = 0; r < rows; r++)
                    {
                        Unsafe.WriteUnaligned(ref d, SwapPair(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref src, k))));
                        k += 2;
                        d = ref Unsafe.Add(ref d, pitch);
                    }
                }
            }
            else
            {
                var end = (nuint)(seg.Start + seg.Count);
                if (strided)
                {
                    for (var i = (nuint)seg.Start; i < end; i++)
                    {
                        uint code = Unsafe.Add(ref rowCol, i);
                        Unsafe.Add(ref dest, (nuint)(code >> 8) * pitch + (code & 0xFF)) = Unsafe.Add(ref src, i);
                    }
                }
                else
                {
                    for (var i = (nuint)seg.Start; i < end; i++)
                    {
                        Unsafe.Add(ref dest, Unsafe.Add(ref idx, i)) = Unsafe.Add(ref src, i);
                    }
                }
            }
        }
    }

    // The bit array holds (col module, col-1 module) in walk order; memory wants
    // col-1 first. Read and write go through the same host endianness, so a
    // ReverseEndianness in between always swaps the two BYTES in memory order,
    // whatever the host is — the swap is unconditional by design.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort SwapPair(ushort v) => BinaryPrimitives.ReverseEndianness(v);

    // ---------------------------------------------------------------
    // Per-version tables (built once from the reference painters, so they are
    // correct by construction; published with a volatile write — a benign race
    // builds identical tables twice). Memory per version: w×h bytes per ECC template
    // used + 5 bytes per data module (core index, row/col code, mask) + a few pair
    // descriptors, plus the ARM64 block/run/single segmentation where it is built.
    // Measured for R17x139: about 12 KB, and 784 B more on ARM64; across every version
    // and ECC about 150 KB, and about 16 KB more on ARM64.
    // ---------------------------------------------------------------

    /// <summary>A column pair (col, col-1) of the zigzag walk.</summary>
    private readonly struct PairSegment
    {
        public readonly int Col;
        public readonly int Start;   // first walk position of the pair
        public readonly int Count;   // data modules in the pair
        public readonly bool Upward;
        public readonly bool Clean;  // rows 1..h-2 are data in both columns (Count == 2 * (h - 2))

        public PairSegment(int col, int start, int count, bool upward, bool clean)
        {
            Col = col;
            Start = start;
            Count = count;
            Upward = upward;
            Clean = clean;
        }
    }

    /// <summary>
    /// Four consecutive clean column pairs — eight consecutive columns — that the ARM64
    /// tier transposes in registers. Consecutive clean pairs occupy consecutive walk
    /// positions, so the first pair's start locates all four.
    /// </summary>
    private readonly struct BlockSegment
    {
        public readonly int LeftCol;   // leftmost of the eight columns
        public readonly int StartBit;  // first walk position of the block's first (rightmost) pair
        public readonly bool Upward;   // direction of that first pair; they alternate

        public BlockSegment(int leftCol, int startBit, bool upward)
        {
            LeftCol = leftCol;
            StartBit = startBit;
            Upward = upward;
        }
    }

    /// <summary>
    /// A stretch of consecutive rows of one column pair where BOTH columns are data,
    /// so the two modules of every row are adjacent walk positions and can be written
    /// with one byte-swapped 16-bit store. Unlike <see cref="PairSegment.Clean"/> this
    /// survives function modules elsewhere in the same pair.
    /// </summary>
    private readonly struct RunSegment
    {
        public readonly int StartBit;  // walk position of the run's first row IN WALK ORDER
        public readonly int RowCount;
        public readonly int FirstRow;  // symbol row of that first walk position
        public readonly int LeftCol;   // the pair's left column (col - 1)
        public readonly bool Upward;

        public RunSegment(int startBit, int rowCount, int firstRow, int leftCol, bool upward)
        {
            StartBit = startBit;
            RowCount = rowCount;
            FirstRow = firstRow;
            LeftCol = leftCol;
            Upward = upward;
        }
    }

    private sealed class Layout
    {
        public readonly ushort[] Index;      // core offset (row * width + col) per walk position
        public readonly ushort[] RowCol;     // row << 8 | col per walk position (strided destinations; row <= 16, col <= 138 by geometry)
        public readonly byte[] Masks;        // mask bit per walk position
        public readonly int DataModuleCount; // 8 * total codewords + remainder bits
        public readonly PairSegment[] Pairs;
        // ARM64 store tier (RmQRModulePlacer.Simd.Arm.cs); empty on other targets.
        public readonly BlockSegment[] Blocks;   // 4 consecutive clean pairs = 8 consecutive columns
        public readonly RunSegment[] Runs;       // stretches of rows where both columns of a pair are data
        public readonly int[] Singles;           // walk positions no block or run covers
        public readonly byte[] ReverseIndex;     // TBL index flipping the first (h-2) lanes into row order
        private readonly byte[] _functionTemplate; // function modules painted, format modules 0
        private byte[]? _templateM;
        private byte[]? _templateH;

        public Layout(byte[] functionTemplate, ushort[] index, ushort[] rowCol, byte[] masks, PairSegment[] pairs, BlockSegment[] blocks, RunSegment[] runs, int[] singles, byte[] reverseIndex)
        {
            _functionTemplate = functionTemplate;
            Index = index;
            RowCol = rowCol;
            Masks = masks;
            DataModuleCount = index.Length;
            Pairs = pairs;
            Blocks = blocks;
            Runs = runs;
            Singles = singles;
            ReverseIndex = reverseIndex;
        }

        /// <summary>Function template with the ECC level's two format copies painted.</summary>
        public byte[] GetTemplate(RmQRVersion version, RmQREccLevel eccLevel)
        {
            ref var slot = ref (eccLevel == RmQREccLevel.M ? ref _templateM : ref _templateH);
            var t = Volatile.Read(ref slot);
            if (t is not null) return t;
            t = (byte[])_functionTemplate.Clone();
            PlaceFormat(t, version, eccLevel, RmQRConstants.GetHeight(version), RmQRConstants.GetWidth(version));
            Volatile.Write(ref slot, t);
            return t;
        }
    }

    private static readonly Layout?[] layouts = new Layout?[RmQRConstants.VersionCount + 1];

    private static Layout GetLayout(RmQRVersion version)
    {
        ref var slot = ref layouts[(int)version];
        var layout = Volatile.Read(ref slot);
        if (layout is not null) return layout;
        layout = BuildLayout(version);
        Volatile.Write(ref slot, layout);
        return layout;
    }

    private static Layout BuildLayout(RmQRVersion version)
    {
        var height = RmQRConstants.GetHeight(version);
        var width = RmQRConstants.GetWidth(version);
        var functionTemplate = new byte[width * height];
        PlaceFunctionModules(functionTemplate, version, height, width);

        // The same walk as PlaceData, recording core offset, row/col code and mask per
        // data position and the per-pair shape.
        var index = new List<ushort>();
        var rowCol = new List<ushort>();
        var masks = new List<byte>();
        var pairs = new List<PairSegment>();
        var upward = true;
        for (var col = width - 2; col >= 1; col -= 2)
        {
            var start = index.Count;
            var clean = true;
            for (var step = 0; step < height; step++)
            {
                var row = upward ? height - 1 - step : step;
                for (var c = col; c >= col - 1; c--)
                {
                    var isFunction = IsFunctionModule(version, row, c);
                    if (isFunction)
                    {
                        if (row >= 1 && row <= height - 2) clean = false;
                        continue;
                    }
                    index.Add((ushort)(row * width + c));
                    // 8-bit fields: rMQR geometry caps height at 17 and width at 139 (both < 256)
                    Debug.Assert(row < 256 && c < 256, "row/col code needs 8-bit fields");
                    rowCol.Add((ushort)((row << 8) | c));
                    masks.Add((byte)(GetMaskBit(row, c) ? 1 : 0));
                }
            }
            pairs.Add(new PairSegment(col, start, index.Count - start, upward, clean));
            upward = !upward;
        }

        var (blocks, runs, singles, reverseIndex) = BuildNeonTables(version, height, width, pairs, index.Count);
        return new Layout(functionTemplate, index.ToArray(), rowCol.ToArray(), masks.ToArray(), pairs.ToArray(), blocks, runs, singles, reverseIndex);
    }

    /// <summary>
    /// Segmentation the ARM64 store tier consumes: transpose blocks, then the row runs
    /// the blocks did not take, then the isolated modules. Built only where that tier
    /// can run (<c>AdvSimd.Arm64.IsSupported</c> is a JIT constant, so other targets
    /// neither build nor carry these tables).
    /// </summary>
    private static (BlockSegment[] Blocks, RunSegment[] Runs, int[] Singles, byte[] ReverseIndex) BuildNeonTables(RmQRVersion version, int height, int width, List<PairSegment> pairs, int positionCount)
    {
        if (!IsNeonTierSupported)
            return ([], [], [], []);

        // Blocks: runs of consecutive clean pairs, cut into groups of four.
        var covered = new bool[positionCount];
        var blocks = new List<BlockSegment>();
        var p = 0;
        while (p < pairs.Count)
        {
            var run = 0;
            while (p + run < pairs.Count && pairs[p + run].Clean) run++;
            for (var b = 0; b + 4 <= run; b += 4)
            {
                var first = pairs[p + b];
                blocks.Add(new BlockSegment(pairs[p + b + 3].Col - 1, first.Start, first.Upward));
                for (var j = 0; j < 4; j++)
                {
                    var seg = pairs[p + b + j];
                    for (var k = seg.Start; k < seg.Start + seg.Count; k++) covered[k] = true;
                }
            }
            p += run == 0 ? 1 : run;
        }

        // The same walk again, now recording which uncovered rows have both columns.
        var runs = new List<RunSegment>();
        var singles = new List<int>();
        var pos = 0;
        var upward = true;
        for (var col = width - 2; col >= 1; col -= 2)
        {
            var runStart = -1;
            var runRows = 0;
            var runFirstRow = 0;
            for (var step = 0; step < height; step++)
            {
                var row = upward ? height - 1 - step : step;
                var right = IsFunctionModule(version, row, col) ? -1 : pos++;
                var left = IsFunctionModule(version, row, col - 1) ? -1 : pos++;
                if (right >= 0 && left >= 0 && !covered[right] && !covered[left])
                {
                    if (runStart < 0)
                    {
                        runStart = right;
                        runRows = 0;
                        runFirstRow = row;
                    }
                    runRows++;
                    continue;
                }
                if (runStart >= 0)
                {
                    runs.Add(new RunSegment(runStart, runRows, runFirstRow, col - 1, upward));
                    runStart = -1;
                }
                if (right >= 0 && !covered[right]) singles.Add(right);
                if (left >= 0 && !covered[left]) singles.Add(left);
            }
            if (runStart >= 0)
                runs.Add(new RunSegment(runStart, runRows, runFirstRow, col - 1, upward));
            upward = !upward;
        }
        Debug.Assert(pos == positionCount, "the neon table walk must visit the same positions as the layout walk");

        // Lane i of an upward pair's column vector is row (h - 2) - 1 - i.
        var reverseIndex = new byte[16];
        for (var i = 0; i < 16; i++)
            reverseIndex[i] = (byte)(height - 3 - i); // wraps out of TBL range for i >= h-2, which yields 0

        return (blocks.ToArray(), runs.ToArray(), singles.ToArray(), reverseIndex);
    }

    // ---------------------------------------------------------------
    // Reference painters (per module)
    // ---------------------------------------------------------------

    /// <summary>Paints every function module (dark or light) for the version.</summary>
    internal static void PlaceFunctionModules(Span<byte> core, RmQRVersion version, int height, int width)
    {
        // Edge timing patterns first; finder, sub-finder, corners and alignment
        // patterns overwrite their cells afterwards.
        for (var col = 0; col < width; col++)
        {
            var dark = (byte)((col & 1) == 0 ? 1 : 0);
            core[col] = dark;
            core[(height - 1) * width + col] = dark;
        }
        for (var row = 0; row < height; row++)
        {
            var dark = (byte)((row & 1) == 0 ? 1 : 0);
            core[row * width] = dark;
            core[row * width + width - 1] = dark;
        }

        // Vertical timing columns with a 3×3 alignment pattern (dark ring, light center) at both ends.
        var alignment = RmQRConstants.GetAlignmentColumns(version);
        for (var i = 0; i < alignment.Length; i++)
        {
            int c = alignment[i];
            for (var row = 0; row < height; row++)
                core[row * width + c] = (byte)((row & 1) == 0 ? 1 : 0);
            for (var dr = 0; dr < 3; dr++)
            {
                for (var dc = -1; dc <= 1; dc++)
                {
                    var dark = (byte)(dr == 1 && dc == 0 ? 0 : 1);
                    core[dr * width + c + dc] = dark;
                    core[(height - 3 + dr) * width + c + dc] = dark;
                }
            }
        }

        // Corner patterns: top-right and bottom-left. Painted BEFORE the finder
        // separators: on height 9 the bottom-left corner cell (h-2, 0) = (7, 0) lies on
        // separator row 7, and the separator (light) wins there (both external lineages agree).
        core[width - 2] = 1;
        core[width - 1] = 1;
        core[width + width - 1] = 1;
        core[width + width - 2] = 0;
        core[(height - 1) * width] = 1;
        core[(height - 1) * width + 1] = 1;
        core[(height - 2) * width] = 1;
        core[(height - 2) * width + 1] = 0;

        // Finder 7×7: dark border, light ring, dark 3×3 center; light separators.
        for (var row = 0; row < 7; row++)
        {
            for (var col = 0; col < 7; col++)
            {
                var ring = Math.Max(Math.Abs(row - 3), Math.Abs(col - 3));
                core[row * width + col] = (byte)(ring == 2 ? 0 : 1);
            }
        }
        for (var row = 0; row < Math.Min(8, height); row++)
            core[row * width + 7] = 0;
        if (height > 7)
        {
            for (var col = 0; col <= 7; col++)
                core[7 * width + col] = 0;
        }

        // Sub-finder 5×5: dark border, light ring, dark center.
        for (var row = 0; row < 5; row++)
        {
            for (var col = 0; col < 5; col++)
            {
                var ring = Math.Max(Math.Abs(row - 2), Math.Abs(col - 2));
                core[(height - 5 + row) * width + width - 5 + col] = (byte)(ring == 1 ? 0 : 1);
            }
        }
    }

    /// <summary>Writes both 18-bit format-information copies.</summary>
    internal static void PlaceFormat(Span<byte> core, RmQRVersion version, RmQREccLevel eccLevel, int height, int width)
    {
        var finderSide = RmQRConstants.GetFormatBits(version, eccLevel, subFinderSide: false);
        var subFinderSide = RmQRConstants.GetFormatBits(version, eccLevel, subFinderSide: true);

        // Bits 0-14: five rows × three columns, column-major (bit = col * 5 + row).
        for (var c = 0; c < 3; c++)
        {
            for (var r = 0; r < 5; r++)
            {
                var bit = c * 5 + r;
                core[(r + 1) * width + (c + 8)] = (byte)((finderSide >> bit) & 1);
                core[(height - 6 + r) * width + (width - 8 + c)] = (byte)((subFinderSide >> bit) & 1);
            }
        }
        // Bits 15-17.
        for (var k = 0; k < 3; k++)
        {
            core[(k + 1) * width + 11] = (byte)((finderSide >> (15 + k)) & 1);
            core[(height - 6) * width + (width - 5 + k)] = (byte)((subFinderSide >> (15 + k)) & 1);
        }
    }

    /// <summary>
    /// Two-column zigzag data placement with the fixed mask: column pairs from
    /// (w-2, w-3) leftward, upward first, right column first, function modules
    /// skipped; bits beyond the message are remainder bits (light before masking).
    /// </summary>
    internal static void PlaceData(Span<byte> core, RmQRVersion version, int height, int width, ReadOnlySpan<byte> finalMessage)
    {
        var bitIndex = 0;
        var bitCount = finalMessage.Length * 8;
        var upward = true;
        for (var col = width - 2; col >= 1; col -= 2)
        {
            for (var step = 0; step < height; step++)
            {
                var row = upward ? height - 1 - step : step;
                for (var c = col; c >= col - 1; c--)
                {
                    if (IsFunctionModule(version, row, c))
                        continue;

                    var bit = 0;
                    if (bitIndex < bitCount)
                    {
                        bit = (finalMessage[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1;
                    }
                    bitIndex++;

                    core[row * width + c] = (byte)(bit ^ (GetMaskBit(row, c) ? 1 : 0));
                }
            }
            upward = !upward;
        }
    }
}
