using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FeatherQR.Internals.BinaryDecoders;

namespace FeatherQR.Internals.RmQr;

/// <summary>
/// rMQR matrix → payload (ISO/IEC 23941, the inverse of the encode pipeline): the
/// version comes from the physical width × height, the two format-information
/// copies name the ECC level (only copies naming that version count, the closer
/// wins), then inverse zigzag + fixed unmask (reusing
/// the placer's own predicate and mask so both sides always agree), block
/// deinterleave, per-block Reed-Solomon correction capped at the block's correction
/// capacity, and the bit-stream decode. Allocation-free: fixed stack budgets sized by
/// the largest version.
/// </summary>
/// <remarks>
/// The capacity cap is the same post-correction shape as
/// <see cref="MicroQR.MicroQRMatrixDecoder"/>, but on rMQR it can never fire: every
/// <see cref="RmQRConstants.GetErrorCorrectionCapacity"/> entry equals the full
/// Reed-Solomon strength ⌊ecc/2⌋, and <see cref="EccBinaryDecoder.TryCorrect"/> only
/// reports a success at or below that. It is applied anyway so both symbologies read
/// the rule from their constants table at the same point in the pipeline, and so a
/// future ISO/IEC 23941 Table 8 that reserves misdecode-protection codewords p on some
/// row (as ISO/IEC 18004 Table 9 does for Micro QR) is a table edit alone. Micro QR
/// has a false-positive damage class for its cap; rMQR's is empty by construction,
/// which is why no test exercises this branch.
/// </remarks>
internal static partial class RmQRMatrixDecoder
{
    internal const int MaxTotalCodewords = 232;  // R17x139
    internal const int MaxDataCodewords = 152;   // R17x139-M
    internal const int MaxBlockCodewords = 74;  // R15x59-M: 48 data + 26 ECC in one block (pinned by RmQRCodeDecoderRoundTripTest)

    public static QRCodeDecodeStatus DecodeMatrix(ReadOnlySpan<byte> modules, int width, int height, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info)
    {
        charsWritten = 0;

        // 1. Version from the physical dimensions
        if (!RmQRConstants.TryGetVersion(height, width, out var version) || modules.Length < width * height)
        {
            info = new RmQRCodeDecodeInfo(QRCodeDecodeStatus.InvalidMatrix, 0, default, 0);
            return QRCodeDecodeStatus.InvalidMatrix;
        }

        // 2. Format information (both copies) → ECC; only copies that agree with the
        //    dimension-derived version count, so a copy miscorrected toward another
        //    version's word cannot veto the valid one.
        ReadFormatCopies(modules, width, height, out var finderSideRaw, out var subFinderSideRaw);
        if (!RmQRFormatInformationDecoder.TryDecode(finderSideRaw, subFinderSideRaw, version, out var eccLevel, out _))
        {
            info = new RmQRCodeDecodeInfo(QRCodeDecodeStatus.FormatInformationInvalid, version, default, 0);
            return QRCodeDecodeStatus.FormatInformationInvalid;
        }

        var eccInfo = RmQRConstants.GetEccInfo(version, eccLevel);
        var totalCodewords = RmQRConstants.GetTotalCodewordCount(version);
        var blocks = eccInfo.BlocksInGroup1 + eccInfo.BlocksInGroup2;

        // 3. Extract the interleaved codeword stream (inverse zigzag + unmask)
        Span<byte> stream = stackalloc byte[MaxTotalCodewords];
        stream = stream.Slice(0, totalCodewords);

        ExtractCodewords(modules, width, height, version, stream);

        // 4. Deinterleave block by block, correct, and collect the data codewords
        Span<byte> block = stackalloc byte[MaxBlockCodewords];
        Span<byte> data = stackalloc byte[MaxDataCodewords];
        data = data.Slice(0, eccInfo.TotalDataCodewords);
        var errorsCorrected = 0;
        var correctionCapacity = RmQRConstants.GetErrorCorrectionCapacity(version, eccLevel);
        var dataOffset = 0;
        for (var b = 0; b < blocks; b++)
        {
            var dataLength = b < eccInfo.BlocksInGroup1 ? eccInfo.CodewordsInGroup1 : eccInfo.CodewordsInGroup2;
            var blockSpan = block.Slice(0, dataLength + eccInfo.ECCPerBlock);

            // Data codewords: round-robin rows across blocks; the extra codeword of the
            // long blocks follows all short-length rows.
            for (var k = 0; k < dataLength; k++)
            {
                var index = k < eccInfo.CodewordsInGroup1
                    ? k * blocks + b
                    : eccInfo.CodewordsInGroup1 * blocks + (b - eccInfo.BlocksInGroup1);
                blockSpan[k] = stream[index];
            }
            // ECC codewords: round-robin after all data.
            for (var e = 0; e < eccInfo.ECCPerBlock; e++)
            {
                blockSpan[dataLength + e] = stream[eccInfo.TotalDataCodewords + e * blocks + b];
            }

            // The capacity cap is unreachable while every table entry equals ⌊ecc/2⌋
            // (see the remarks on this class); it is the seam a reserved p would use.
            if (!EccBinaryDecoder.TryCorrect(blockSpan, eccInfo.ECCPerBlock, out var blockErrors)
                || blockErrors > correctionCapacity)
            {
                info = new RmQRCodeDecodeInfo(QRCodeDecodeStatus.DataUncorrectable, version, eccLevel, errorsCorrected + blockErrors);
                return QRCodeDecodeStatus.DataUncorrectable;
            }
            errorsCorrected += blockErrors;

            blockSpan.Slice(0, dataLength).CopyTo(data.Slice(dataOffset, dataLength));
            dataOffset += dataLength;
        }

        // 5. Bit stream → text
        var status = RmQRBinaryDecoder.DecodeBitStream(data, eccInfo.TotalDataCodewords * 8, version, destination, out charsWritten);
        info = new RmQRCodeDecodeInfo(status, version, eccLevel, errorsCorrected);
        return status;
    }

    /// <summary>
    /// Upper bound on decoded characters for a version across ECC levels and modes:
    /// numeric packs 3 digits into 10 bits, so one data codeword (8 bits) yields at
    /// most 2.4 characters; 3× the M-level data codewords is a safe bound.
    /// </summary>
    public static int GetMaxCharCount(RmQRVersion version)
        => RmQRConstants.GetDataCodewordCount(version, RmQREccLevel.M) * 3;

    /// <summary>Reads both 18-bit copies (positions mirror <see cref="RmQRModulePlacer.PlaceFormat"/> exactly).</summary>
    private static void ReadFormatCopies(ReadOnlySpan<byte> modules, int width, int height, out int finderSide, out int subFinderSide)
    {
        finderSide = 0;
        subFinderSide = 0;
        for (var c = 0; c < 3; c++)
        {
            for (var r = 0; r < 5; r++)
            {
                var bit = c * 5 + r;
                if (modules[(r + 1) * width + (c + 8)] != 0)
                    finderSide |= 1 << bit;
                if (modules[(height - 6 + r) * width + (width - 8 + c)] != 0)
                    subFinderSide |= 1 << bit;
            }
        }
        for (var k = 0; k < 3; k++)
        {
            if (modules[(k + 1) * width + 11] != 0)
                finderSide |= 1 << (15 + k);
            if (modules[(height - 6) * width + (width - 5 + k)] != 0)
                subFinderSide |= 1 << (15 + k);
        }
    }

    /// <summary>
    /// Inverse of <see cref="RmQRModulePlacer.PlaceData"/>: the same walk, unmasked
    /// on the fly. Every byte of <paramref name="stream"/> is written; modules past
    /// the codeword stream (remainder) are ignored.
    /// </summary>
    /// <remarks>
    /// Everything the walk derives from the version alone (which modules are function
    /// modules, their order, the mask bit at each one) is hoisted into lazily built
    /// per-version tables, exactly as the placer does for the encode direction. Two
    /// tiers consume those tables: a bit-plane kernel on x64 with AVX2 and fast BMI2
    /// (see RmQRMatrixDecoder.Simd.cs) and a portable table walk everywhere else.
    /// Measured 16-64x and 8-12x respectively over the per-module reference walk
    /// across R7x43..R17x139 (see the decoder kernel parity tests for equivalence).
    /// ARM64 has a third tier (RmQRMatrixDecoder.Simd.Arm.cs) built on pair-interleaved
    /// planes instead, because NEON has no PEXT/PDEP; it is 1.1-3.3x the portable tier.
    /// </remarks>
    private static void ExtractCodewords(ReadOnlySpan<byte> modules, int width, int height, RmQRVersion version, Span<byte> stream)
        => ExtractCodewords(modules, width, height, version, stream, ExtractKernel.Auto);

    /// <summary>Whether the x64 bit-plane tier runs on this machine (parity tests skip it otherwise).</summary>
    internal static bool IsBitPlaneTierSupported =>
#if NET8_0_OR_GREATER
        System.Runtime.Intrinsics.X86.Avx2.IsSupported && HardwareCapabilities.HasFastPext;
#else
        false;
#endif

    /// <summary>Whether the ARM64 pair-plane tier runs on this machine (parity tests skip it otherwise).</summary>
    internal static bool IsPairPlaneTierSupported =>
#if NET8_0_OR_GREATER
        System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported;
#else
        false;
#endif

    /// <summary>Which extraction kernel to run; anything but <see cref="ExtractKernel.Auto"/> is for parity tests.</summary>
    internal enum ExtractKernel
    {
        /// <summary>The fastest tier this machine supports.</summary>
        Auto,
        /// <summary>The portable table walk, on every target.</summary>
        Scalar,
        /// <summary>x64 column bit planes + PEXT/PDEP.</summary>
        BitPlanes,
        /// <summary>ARM64 pair-interleaved planes + run compression.</summary>
        PairPlanes,
    }

    /// <summary>
    /// Kernel-selecting entry; <paramref name="kernel"/> pins one tier for parity tests.
    /// Pinning a tier that cannot run here throws rather than falling through to the
    /// portable walk: a parity test that silently compares the portable kernel against
    /// itself stays green while the tier it names goes unexercised.
    /// </summary>
    internal static void ExtractCodewords(ReadOnlySpan<byte> modules, int width, int height, RmQRVersion version, Span<byte> stream, ExtractKernel kernel)
    {
        if (kernel is ExtractKernel.BitPlanes or ExtractKernel.PairPlanes)
        {
            var supported = kernel == ExtractKernel.BitPlanes ? IsBitPlaneTierSupported : IsPairPlaneTierSupported;
            if (!supported)
                throw new PlatformNotSupportedException($"{nameof(ExtractKernel)}.{kernel} was pinned, but that tier does not run on this machine. Guard the call with {nameof(IsBitPlaneTierSupported)} / {nameof(IsPairPlaneTierSupported)}.");
            var expected = RmQRConstants.GetTotalCodewordCount(version);
            if (stream.Length != expected)
                throw new ArgumentException($"{nameof(ExtractKernel)}.{kernel} emits whole words off a per-version table, so the stream must be exactly {expected} bytes for {version}; got {stream.Length}.", nameof(stream));
        }

        var layout = GetExtractLayout(version);
#if NET8_0_OR_GREATER
        // Both vector kernels emit whole words off a per-version table, so their output
        // length is fixed by the version rather than by the span they are handed. The
        // portable tier reads stream.Length and truncates. Only dispatch to a kernel
        // when those two agree; every production caller sizes the span exactly.
        var exact = stream.Length == RmQRConstants.GetTotalCodewordCount(version);
        if (exact && kernel != ExtractKernel.Scalar)
        {
            if ((kernel == ExtractKernel.Auto || kernel == ExtractKernel.BitPlanes) && IsBitPlaneTierSupported)
            {
                ExtractCodewordsBitPlanes(modules, width, height, layout.Pairs, stream);
                return;
            }
            if ((kernel == ExtractKernel.Auto || kernel == ExtractKernel.PairPlanes) && IsPairPlaneTierSupported)
            {
                ExtractCodewordsPairPlanes(modules, width, height, layout.PairPlanes!, stream);
                return;
            }
        }
#endif
        // The portable tier reads the geometry out of the walk-order table instead.
        ExtractCodewordsScalar(modules, layout.Order, stream);
    }

    /// <summary>
    /// Portable tier: one gather per stream bit through the walk-order table, the
    /// output byte accumulated in a register so each is stored once.
    /// </summary>
    private static void ExtractCodewordsScalar(ReadOnlySpan<byte> modules, ushort[] order, Span<byte> stream)
    {
        ref var o = ref MemoryMarshal.GetReference(order.AsSpan());
        ref var src = ref MemoryMarshal.GetReference(modules);
        ref var dst = ref MemoryMarshal.GetReference(stream);
        var codewords = stream.Length;

        nint i = 0;
        for (nint k = 0; k < codewords; k++, i += 8)
        {
            ref var p = ref Unsafe.Add(ref o, i);
            var b = Bit(ref src, p) << 7
                  | Bit(ref src, Unsafe.Add(ref p, 1)) << 6
                  | Bit(ref src, Unsafe.Add(ref p, 2)) << 5
                  | Bit(ref src, Unsafe.Add(ref p, 3)) << 4
                  | Bit(ref src, Unsafe.Add(ref p, 4)) << 3
                  | Bit(ref src, Unsafe.Add(ref p, 5)) << 2
                  | Bit(ref src, Unsafe.Add(ref p, 6)) << 1
                  | Bit(ref src, Unsafe.Add(ref p, 7));
            Unsafe.Add(ref dst, k) = (byte)b;
        }

        // Modules are "0 = light, non-zero = dark", so the test is != 0, not a bit test.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Bit(ref byte src, ushort entry)
            => (Unsafe.Add(ref src, entry & CoreIndexMask) != 0 ? 1 : 0) ^ (entry >> MaskBitShift);
    }

    // ---------------------------------------------------------------
    // Per-version extraction tables (built once from the placer's own predicates, so
    // they are correct by construction; published with a volatile write - a benign
    // race builds identical tables twice). Memory per version: 2 bytes per stream bit
    // plus 24 bytes per column pair, and on ARM64 the pair-plane run tables on top of
    // that. Measured: R17x139 is 5,368 B on x64/portable (3,712 B of order plus 69
    // column pairs) and 7,368 B on ARM64; decoding all 32 versions costs about 68 KB
    // and about 100 KB respectively.
    // ---------------------------------------------------------------

    /// <summary>Low bits of an <see cref="ExtractLayout.Order"/> entry: the core module index.</summary>
    private const int CoreIndexMask = 0x7FFF;

    /// <summary>Top bit of an <see cref="ExtractLayout.Order"/> entry: the data mask at that module.</summary>
    private const int MaskBitShift = 15;

    /// <summary>
    /// Column stride between the two bit planes. The widest symbol is 139 columns and the
    /// transpose stores 16 at a time, so the last store ends at 128 + 16 = 144; rounded
    /// to 160 so writing the last column group cannot run past the first plane into the
    /// second, with margin.
    /// </summary>
    private const int PlaneStride = 160;

    /// <summary>
    /// Everything the ARM64 pair-plane kernel derives from the version alone. Unlike the
    /// x64 form, a lane here is a whole column PAIR: the transpose interleaves the two
    /// columns as it goes, so the plane word already is the pair's output field with the
    /// function modules still in it, and the kernel only has to compress it. The
    /// compression is described as runs of consecutive data bits, because function
    /// modules come from rectangular blocks and not from scattered modules (a pair
    /// averages 1.0-2.3 runs, worst case 5-11).
    /// </summary>
    internal sealed class PairPlaneLayout
    {
        /// <summary>8-column transpose blocks: ceil(width / 8), each holding 4 pairs.</summary>
        public readonly int Blocks;

        /// <summary>Bits a full pair contributes before compression: 2 * (height - 2).</summary>
        public readonly int FieldBits;

        /// <summary>Data mask in plane coordinates, indexed by pair index; XORed into the plane once per block.</summary>
        public readonly uint[] PlaneXor;

        /// <summary>
        /// Every run of every pair in walk order, 3 words each: the lane the pair occupies
        /// in its block, the run's bits in place, and source shift | length &lt;&lt; 16.
        /// </summary>
        public readonly uint[] Runs;

        /// <summary>End offset in <see cref="Runs"/> of each block's runs; blocks are walked in descending order.</summary>
        public readonly uint[] BlockRunEnd;

        /// <summary>All-ones in the lanes whose pair walks downward, so needs the row-reversed word.</summary>
        public readonly uint[] DownwardLanes;

        public PairPlaneLayout(int blocks, int fieldBits, uint[] planeXor, uint[] runs, uint[] blockRunEnd, uint[] downwardLanes)
        {
            Blocks = blocks;
            FieldBits = fieldBits;
            PlaneXor = planeXor;
            Runs = runs;
            BlockRunEnd = blockRunEnd;
            DownwardLanes = downwardLanes;
        }
    }

    /// <summary>
    /// Replays the same walk in the pair-plane shape. Plane word for the pair whose right
    /// column is <c>col</c> holds, for every data row, bit <c>2j+1</c> = module(row, col)
    /// and <c>2j</c> = module(row, col-1), where j counts rows in WALK order from the
    /// FIRST row the pair visits (row height-2 walking up, row 1 walking down), so the
    /// pair's earliest bits sit in the word's high bits and the run extraction reads it
    /// from bit 31 down. The walk alternates between the two columns of a pair on every
    /// row, so those bits are already in stream order.
    /// </summary>
    private static PairPlaneLayout BuildPairPlaneLayout(RmQRVersion version, int width, int height, int bitCount)
    {
        var blocks = (width + 7) / 8;
        var fieldBits = 2 * (height - 2);
        var topK = (width - 3) / 2;
        // Padded to whole 32-column steps (4 blocks): the transpose reads mask vectors
        // four blocks at a time, and the padding pairs carry no runs.
        var slots = (blocks + 3) / 4 * 16;

        var selects = new uint[slots];
        var planeXor = new uint[slots];
        var downward = new bool[slots];

        var walked = 0;
        var upward = true;
        for (var col = width - 2; col >= 1; col -= 2)
        {
            var k = col >> 1;
            downward[k] = !upward;

            uint select = 0, mask = 0;
            var contributed = 0;
            for (var step = 0; step < height; step++)
            {
                var row = upward ? height - 1 - step : step;
                if (row < 1 || row > height - 2) continue; // rows 0 and h-1 are timing patterns
                var j = upward ? height - 2 - row : row - 1;
                var group = fieldBits - 2 - 2 * j;

                for (var c = col; c >= col - 1; c--)
                {
                    if (RmQRModulePlacer.IsFunctionModule(version, row, c)) continue;
                    if (walked + contributed >= bitCount) continue; // truncated tail of the walk
                    var bit = group + (c == col ? 1 : 0);
                    select |= 1u << bit;
                    if (RmQRModulePlacer.GetMaskBit(row, c)) mask |= 1u << bit;
                    contributed++;
                }
            }

            selects[k] = select;
            planeXor[k] = mask;
            walked += contributed;
            upward = !upward;
        }

        // A block covers four consecutive pairs and the walk runs down the pair index, so
        // walking the blocks backwards visits the runs in exactly stream order.
        var runs = new List<uint>();
        var blockRunEnd = new uint[slots / 4];
        var atBlock = slots / 4 - 1;
        for (var k = slots - 1; k >= 0; k--)
        {
            while (atBlock > k >> 2)
            {
                blockRunEnd[atBlock] = (uint)runs.Count;
                atBlock--;
            }
            if (k > topK) continue;

            var bit = 31;
            while (bit >= 0)
            {
                if ((selects[k] & (1u << bit)) == 0) { bit--; continue; }
                var low = bit;
                while (low > 0 && (selects[k] & (1u << (low - 1))) != 0) low--;
                var length = bit - low + 1;
                runs.Add((uint)(k & 3));
                runs.Add(((1u << length) - 1) << low);
                runs.Add((uint)low | ((uint)length << 16));
                bit = low - 1;
            }
        }
        blockRunEnd[0] = (uint)runs.Count;

        var lanes = new uint[4];
        for (var lane = 0; lane < 4; lane++)
            lanes[lane] = downward[lane] ? uint.MaxValue : 0u; // a block holds 4 pairs, so the parity pattern repeats

        return new PairPlaneLayout(blocks, fieldBits, planeXor, runs.ToArray(), blockRunEnd, lanes);
    }

    /// <summary>Everything the extraction walk derives from the version alone.</summary>
    private sealed class ExtractLayout
    {
        /// <summary>One entry per stream bit, in walk order: core index | mask bit &lt;&lt; 15.</summary>
        public readonly ushort[] Order;

        /// <summary>
        /// Column-pair descriptors for the bit-plane kernel, 6 words per pair:
        /// plane index | count &lt;&lt; 16, then extract/deposit for column <c>col</c>,
        /// extract/deposit for column <c>col-1</c>, then the data mask of the pair.
        /// </summary>
        public readonly uint[] Pairs;

        /// <summary>Tables for the ARM64 pair-plane kernel; null when that tier cannot run here.</summary>
        public readonly PairPlaneLayout? PairPlanes;

        public ExtractLayout(ushort[] order, uint[] pairs, PairPlaneLayout? pairPlanes)
        {
            Order = order;
            Pairs = pairs;
            PairPlanes = pairPlanes;
        }
    }

    private static readonly ExtractLayout?[] extractLayouts = new ExtractLayout?[RmQRConstants.VersionCount + 1];

    private static ExtractLayout GetExtractLayout(RmQRVersion version)
    {
        ref var slot = ref extractLayouts[(int)version];
        var layout = Volatile.Read(ref slot);
        if (layout is not null) return layout;
        layout = BuildExtractLayout(version);
        Volatile.Write(ref slot, layout);
        return layout;
    }

    /// <summary>
    /// Replays the reference walk once per version and records it two ways.
    /// <para>
    /// The bit-plane form needs the column planes to be readable by PEXT: plane bit
    /// <c>row-1</c> holds row <c>row</c> for an upward pair, and plane bit
    /// <c>height-2-row</c> for a downward one (the row-reversed copy). With that
    /// layout the plane bit index and the deposit position both fall as the walk
    /// advances, so PEXT's packing order matches PDEP's scatter order and one pair of
    /// masks serves both walk directions. The walk is truncated to the codeword
    /// stream here, so neither kernel needs a remainder check.
    /// </para>
    /// </summary>
    private static ExtractLayout BuildExtractLayout(RmQRVersion version)
    {
        var height = RmQRConstants.GetHeight(version);
        var width = RmQRConstants.GetWidth(version);
        var bitCount = RmQRConstants.GetTotalCodewordCount(version) * 8;

        var order = new ushort[bitCount];
        var pairs = new List<uint>();
        var walkPositions = new List<(int Row, int Col)>();
        var walked = 0;
        var upward = true;

        for (var col = width - 2; col >= 1 && walked < bitCount; col -= 2)
        {
            walkPositions.Clear();
            for (var step = 0; step < height; step++)
            {
                var row = upward ? height - 1 - step : step;
                for (var c = col; c >= col - 1; c--)
                {
                    if (!RmQRModulePlacer.IsFunctionModule(version, row, c))
                        walkPositions.Add((row, c));
                }
            }

            var count = Math.Min(walkPositions.Count, bitCount - walked);
            uint extractRight = 0, depositRight = 0, extractLeft = 0, depositLeft = 0, mask = 0;
            for (var p = 0; p < count; p++)
            {
                var (row, c) = walkPositions[p];
                var planeBit = upward ? row - 1 : height - 2 - row;
                var depositBit = count - 1 - p; // the walk fills the pair's field MSB first
                var masked = RmQRModulePlacer.GetMaskBit(row, c);

                if (c == col)
                {
                    extractRight |= 1u << planeBit;
                    depositRight |= 1u << depositBit;
                }
                else
                {
                    extractLeft |= 1u << planeBit;
                    depositLeft |= 1u << depositBit;
                }
                if (masked)
                    mask |= 1u << depositBit;

                order[walked + p] = (ushort)((row * width + c) | (masked ? 1 << MaskBitShift : 0));
            }

            pairs.Add((uint)(col + (upward ? 0 : PlaneStride)) | ((uint)count << 16));
            pairs.Add(extractRight);
            pairs.Add(depositRight);
            pairs.Add(extractLeft);
            pairs.Add(depositLeft);
            pairs.Add(mask);

            walked += count;
            upward = !upward;
        }

        return new ExtractLayout(order, pairs.ToArray(), IsPairPlaneTierSupported ? BuildPairPlaneLayout(version, width, height, bitCount) : null);
    }
}
