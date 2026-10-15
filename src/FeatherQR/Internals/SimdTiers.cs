using static FeatherQR.Internals.SimdTier;
#if NET8_0_OR_GREATER
using Arm = System.Runtime.Intrinsics.Arm;
using Wasm = System.Runtime.Intrinsics.Wasm;
using X86 = System.Runtime.Intrinsics.X86;
#endif

namespace FeatherQR.Internals;

/// <summary>
/// The instruction set a kernel's vector path needs from the build and the CPU.
/// </summary>
/// <remarks>
/// A tier is named for what it needs, not for a vector width. Which tiers a process has depends on the build as much as on the CPU: a default NativeAOT publish on x64 has no AVX, so every AVX2 and 256-bit tier is off there on any CPU.
/// </remarks>
internal enum SimdTier : byte
{
    /// <summary>No vector tier runs. The portable loop does.</summary>
    Scalar,
    /// <summary>Portable 128-bit vectors: SSE2 on x64, AdvSimd on ARM64, PackedSimd on WebAssembly.</summary>
    Vector128,
    /// <summary>Portable 256-bit vectors: AVX2 on x64, never on ARM64 or WebAssembly.</summary>
    Vector256,
    /// <summary>x64 SSE2.</summary>
    Sse2,
    /// <summary>x64 SSSE3.</summary>
    Ssse3,
    /// <summary>x64 SSE4.1, which includes SSSE3.</summary>
    Sse41,
    /// <summary>x64 AVX2.</summary>
    Avx2,
    /// <summary>x64 AVX2 with BMI2 PEXT/PDEP on a core that runs them in hardware (<c>HardwareCapabilities.HasFastPext</c>).</summary>
    Avx2Pext,
    /// <summary>x64 GFNI on 128-bit vectors (.NET 10 and later), which includes SSE4.1.</summary>
    Gfni,
    /// <summary>x64 GFNI on 256-bit vectors (.NET 10 and later), which needs AVX.</summary>
    GfniV256,
    /// <summary>ARM64 AdvSimd (NEON), the ARM64 baseline.</summary>
    AdvSimd,
    /// <summary>ARM64 AdvSimd with the ARMv8.2 dot product, which Cortex-A53/A72-class cores lack.</summary>
    AdvSimdDp,
    /// <summary>WebAssembly PackedSimd, where a portable 128-bit tier takes an operation WebAssembly has and the portable form lowers badly.</summary>
    PackedSimd,
}

/// <summary>
/// What a build and the CPUs it runs on give a process, as far as <see cref="SimdTiers.Expected"/> is concerned: the instruction sets it always has, those it never has, and those it leaves to the CPU (<see cref="SimdTiers.Definition"/>).
/// </summary>
internal enum SimdBuildClass : byte
{
    /// <summary>x64 without AVX: a default NativeAOT publish, or the JIT under <c>DOTNET_EnableAVX=0</c>. GFNI is left to the CPU.</summary>
    X64Sse,
    /// <summary>x64 with AVX2: the JIT on an AVX2 CPU, or a NativeAOT publish for <c>x86-64-v3</c>. GFNI and fast PEXT are left to the CPU.</summary>
    X64Avx2,
    /// <summary>ARM64: the JIT, or a default NativeAOT publish. The dot product is left to the CPU.</summary>
    Arm64,
    /// <summary>WebAssembly with its SIMD proposal, the default (<c>WasmEnableSIMD</c>): 128-bit vectors through PackedSimd, interpreted or AOT-compiled alike.</summary>
    Wasm,
}

/// <summary>One kernel's row of <see cref="SimdTiers.Expected"/>.</summary>
internal sealed class SimdExpectation(string kernel, SimdTier[] x64Sse, SimdTier[] x64Avx2, SimdTier[] arm64, SimdTier[] wasm)
{
    /// <summary>The kernel, as <see cref="SimdKernel.Name"/> names it.</summary>
    internal string Kernel { get; } = kernel;

    /// <summary>The cell for a build class: the tier the kernel takes there or, where the CPU decides, the tiers it takes, most preferred first.</summary>
    internal SimdTier[] For(SimdBuildClass buildClass) => buildClass switch
    {
        SimdBuildClass.X64Sse => x64Sse,
        SimdBuildClass.X64Avx2 => x64Avx2,
        SimdBuildClass.Arm64 => arm64,
        SimdBuildClass.Wasm => wasm,
        _ => throw new ArgumentOutOfRangeException(nameof(buildClass), buildClass, "Unknown build class."),
    };
}

/// <summary>One kernel's tiers in the order its dispatch prefers them, each with whether it can run in this process.</summary>
internal sealed class SimdKernel
{
    internal SimdKernel(string name, params (SimdTier Tier, bool Runs)[] tiers)
    {
        Name = name;
        Tiers = tiers;
    }

    /// <summary>The kernel, named for the code whose dispatch it describes.</summary>
    internal string Name { get; }

    /// <summary>Every tier the kernel has besides the scalar one, most preferred first.</summary>
    internal (SimdTier Tier, bool Runs)[] Tiers { get; }

    /// <summary>The first tier that can run here, the one the dispatch prefers, or <see cref="SimdTier.Scalar"/> when none can.</summary>
    internal SimdTier Active
    {
        get
        {
            foreach (var (tier, runs) in Tiers)
            {
                if (runs)
                    return tier;
            }
            return SimdTier.Scalar;
        }
    }
}

/// <summary>
/// Every SIMD tier the library dispatches to, per kernel: which tiers each kernel has, in the order its dispatch prefers them, and which of them this process can run.
/// </summary>
/// <remarks>
/// <para>
/// What a build runs is printed from that build (tests/FeatherQR.AotAnalysis for native builds, tests/FeatherQR.WasmReport for WebAssembly) instead of read off the code, and SimdTiersTest keeps this table and the code together:
/// every instruction-set flag a kernel's files read must be the condition of a tier declared here for that kernel, and every tier declared here must be read there.
/// </para>
/// <para>
/// The kernels do not read this table. Each dispatch keeps its own <c>IsSupported</c> reads:
/// the JIT inlines a dispatch into its caller only when the dispatch reads <c>IsSupported</c> itself, not when the flag sits behind a property, an aggressively inlined property or a <see langword="static"/> <see langword="readonly"/> field.
/// </para>
/// <para>
/// A lower tier also finishes the tail of a higher one (<c>ModuleBitPacker</c>), takes inputs too small for it (<c>PerspectiveGridSampler.Sample</c>) or takes inputs it does not pay on (<c>RmQRMatrixDecoder.ExtractCodewords</c> on WebAssembly, under 44 stream bits per eight columns), so a tier listed as runnable is one the dispatch can take, not the only one it takes. <see cref="SimdKernel.Active"/> is the most preferred of them.
/// </para>
/// </remarks>
internal static class SimdTiers
{
    /// <summary>The instruction-set condition each tier is named for.</summary>
    private static class Isa
    {
#if NET8_0_OR_GREATER
        internal static bool Vector128 => System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated;
        internal static bool Vector256 => System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated;
        internal static bool Sse2 => X86.Sse2.IsSupported;
        internal static bool Ssse3 => X86.Ssse3.IsSupported;
        internal static bool Sse41 => X86.Sse41.IsSupported;
        internal static bool Avx2 => X86.Avx2.IsSupported;
        internal static bool Avx2Pext => X86.Avx2.IsSupported && HardwareCapabilities.HasFastPext;
#if NET10_0_OR_GREATER
        internal static bool Gfni => X86.Gfni.IsSupported;
        internal static bool GfniV256 => X86.Gfni.V256.IsSupported;
#else
        internal static bool Gfni => false;
        internal static bool GfniV256 => false;
#endif
        internal static bool AdvSimd => Arm.AdvSimd.Arm64.IsSupported;
        internal static bool AdvSimdDp => Arm.Dp.IsSupported && Arm.AdvSimd.Arm64.IsSupported;
        internal static bool PackedSimd => Wasm.PackedSimd.IsSupported;
#else
        internal static bool Vector128 => false;
        internal static bool Vector256 => false;
        internal static bool Sse2 => false;
        internal static bool Ssse3 => false;
        internal static bool Sse41 => false;
        internal static bool Avx2 => false;
        internal static bool Avx2Pext => false;
        internal static bool Gfni => false;
        internal static bool GfniV256 => false;
        internal static bool AdvSimd => false;
        internal static bool AdvSimdDp => false;
        internal static bool PackedSimd => false;
#endif
    }

    /// <summary>Each tier's instruction-set condition in this process.</summary>
    internal static (SimdTier Tier, bool Available)[] IsaReport() =>
    [
        (SimdTier.Vector128, Isa.Vector128),
        (SimdTier.Vector256, Isa.Vector256),
        (SimdTier.Sse2, Isa.Sse2),
        (SimdTier.Ssse3, Isa.Ssse3),
        (SimdTier.Sse41, Isa.Sse41),
        (SimdTier.Avx2, Isa.Avx2),
        (SimdTier.Avx2Pext, Isa.Avx2Pext),
        (SimdTier.Gfni, Isa.Gfni),
        (SimdTier.GfniV256, Isa.GfniV256),
        (SimdTier.AdvSimd, Isa.AdvSimd),
        (SimdTier.AdvSimdDp, Isa.AdvSimdDp),
        (SimdTier.PackedSimd, Isa.PackedSimd),
    ];

    /// <summary>
    /// Every kernel with its tiers, most preferred first, and whether each can run in this process.
    /// A tier's condition is its <see cref="Isa"/> condition, with what else the kernel's dispatch asks where it asks more.
    /// </summary>
    internal static SimdKernel[] Report() =>
    [
        // ---- Shared across symbologies ----

        // TextAnalyzer.Analyze: the mode and charset scan of the input text
        new("TextAnalyzer", (SimdTier.Avx2, Isa.Avx2), (SimdTier.Sse2, Isa.Sse2), (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.PackedSimd, Isa.PackedSimd)),
        // ModuleBitPacker.Pack / Unpack: byte-per-module to MSB-first bits and back. The SSSE3 / AdvSimd step also finishes what the AVX2 step leaves.
        new("ModuleBitPacker", (SimdTier.Avx2, Isa.Avx2), (SimdTier.Ssse3, Isa.Ssse3), (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.PackedSimd, Isa.PackedSimd)),
        // ModeSegmenter.ComputeCostsLanes: the mixed-mode cost walk over eight pieces at once. The 128-bit tier's four-lane groups narrow their parent entries with SSE2 on x64 and PackedSimd on WebAssembly.
        new("ModeSegmenterLanes", (SimdTier.Vector256, Isa.Vector256), (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.Sse2, Isa.Vector128 && Isa.Sse2), (SimdTier.PackedSimd, Isa.Vector128 && Isa.PackedSimd), (SimdTier.Vector128, Isa.Vector128)),
        // EccBinaryEncoder.CalculateEcc / CalculateECCBlocks: Reed-Solomon remainder, a group's blocks side by side in one loop on x86 (from .NET 8 two with SSSE3 up to 16 codewords, and on .NET 10 up to four with GFNI, above 16 codewords only with 256-bit GFNI and AVX2). GFNI runs inside the SSSE3 entry, and its 256-bit form, for blocks over 16 codewords, also asks for AVX2. WebAssembly runs the NEON kernel with its swizzle for the table lookup.
        new("EccBinaryEncoder", (SimdTier.GfniV256, Isa.GfniV256 && Isa.Avx2), (SimdTier.Gfni, Isa.Gfni), (SimdTier.Ssse3, Isa.Ssse3), (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.PackedSimd, Isa.PackedSimd)),
        // EccBinaryDecoder.ComputeSyndromes: the syndrome pass. The 128-bit tier multiplies by each lane's constant through its bit planes, with no platform instruction.
        new("EccBinaryDecoder", (SimdTier.GfniV256, Isa.GfniV256), (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.Vector128, Isa.Vector128)),
        // LuminanceConverter.ConvertRgba: RGBA / BGRA pixels to 8-bit luminance. The 128-bit tier's 16-bit dot product and narrowing are SSE2 on x64 and PackedSimd on WebAssembly.
        new("LuminanceConverter", (SimdTier.Avx2, Isa.Avx2), (SimdTier.AdvSimdDp, Isa.AdvSimdDp), (SimdTier.Sse2, Isa.Vector128 && Isa.Sse2), (SimdTier.PackedSimd, Isa.Vector128 && Isa.PackedSimd), (SimdTier.Vector128, Isa.Vector128)),
        // LuminanceInverter: the negative image for the light-on-dark pass
        new("LuminanceInverter", (SimdTier.Vector256, Isa.Vector256), (SimdTier.Vector128, Isa.Vector128)),
        // LuminanceHalver: the reduced-scale search's halving, each pixel the mean of a two by two block. WebAssembly shifts and narrows with its own instructions.
        new("LuminanceHalver", (SimdTier.Vector256, Isa.Vector256), (SimdTier.PackedSimd, Isa.Vector128 && Isa.PackedSimd), (SimdTier.Vector128, Isa.Vector128)),
        // Binarizer.FillHistogram: the luminance histogram behind the global threshold
        new("Binarizer", (SimdTier.Vector256, Isa.Vector256), (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.Vector128, Isa.Vector128)),
        // LocalBinarizer: the block statistics of the regional retry, and its rows written and counted against one threshold a pixel. The 256-bit tier also sums and divides the block thresholds.
        new("LocalBinarizer", (SimdTier.Vector256, Isa.Vector256), (SimdTier.Vector128, Isa.Vector128)),
        // FinderPatternFinder.ScanRowMask: a row's dark bitmask for the finder search's mask walk
        new("FinderRowMask", (SimdTier.Vector256, Isa.Vector256), (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.Vector128, Isa.Vector128)),
        // FinderPatternFinder.ScanRowEdges: the finder search's edge-list row kernel, sixteen windows a step on 256-bit vectors and eight on 128-bit ones. ARM64 folds its rows into words.
        new("FinderRowEdges", (SimdTier.Vector256, Isa.Vector256), (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.Vector128, Isa.Vector128)),
        // PerspectiveGridSampler.Sample: a square grid through one projective transform (Standard QR's four-point, parallelogram and frame grids, Micro QR's perspective search). The 128-bit tier also takes grids too small for the 256-bit one, and converts coordinates with SSE2 on x64 and PackedSimd on WebAssembly (VectorCast).
        new("PerspectiveGridSampler", (SimdTier.Vector256, Isa.Vector256), (SimdTier.Sse2, Isa.Vector128 && Isa.Sse2), (SimdTier.PackedSimd, Isa.Vector128 && Isa.PackedSimd), (SimdTier.Vector128, Isa.Vector128)),

        // ---- Standard QR ----

        // ModulePlacer.ExpandBits: message bits to module bytes. The SSSE3 step also finishes what the AVX2 step leaves.
        new("ModulePlacerExpandBits", (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.Avx2, Isa.Avx2), (SimdTier.Ssse3, Isa.Ssse3)),
        // ModulePlacer.MaskCode: mask scoring and selection. At 12-40 the AVX2 tier's automatic selection also places the interleaved stream (TryMaskCodeFromStream). The 128-bit tier scores every version (12-40 transposed), its popcount SSSE3 on x64 and PackedSimd on WebAssembly. ARM64 scores 12-40 with the same transposed tier on its own popcount, packing and unpack.
        new("ModulePlacerMaskCode", (SimdTier.Avx2, Isa.Avx2), (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.Ssse3, Isa.Vector128 && Isa.Ssse3), (SimdTier.PackedSimd, Isa.Vector128 && Isa.PackedSimd), (SimdTier.Vector128, Isa.Vector128)),
        // QRBinaryEncoder.WriteAlphanumericData: the Alphanumeric payload writer, sixteen characters a step (then one step of eight on x64 and ARM64). The portable writer takes the rest of a run.
        new("QRAlphanumericWriter", (SimdTier.Sse41, Isa.Sse41 && Isa.Ssse3), (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.PackedSimd, Isa.PackedSimd)),
        // QRBinaryEncoder.WriteNumericData: the Numeric payload writer, twelve digits a step on x64 and fifteen on ARM64 for a run of 40 digits or more. The portable writer takes shorter runs and the rest of a run.
        new("QRNumericWriter", (SimdTier.Ssse3, Isa.Ssse3), (SimdTier.AdvSimd, Isa.AdvSimd)),
        // AlignmentPatternFinder.ScanRowMask: a row's dark bitmask for the alignment search
        new("AlignmentRowMask", (SimdTier.Vector256, Isa.Vector256), (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.Vector128, Isa.Vector128)),
        // QRImageDecoder.SampleGridPiecewise: the piecewise mesh sampler. The 128-bit tier converts coordinates with SSE2 on x64 and PackedSimd on WebAssembly (VectorCast).
        new("QRSampleGridPiecewise", (SimdTier.Avx2, Isa.Avx2), (SimdTier.Sse2, Isa.Vector128 && Isa.Sse2), (SimdTier.PackedSimd, Isa.Vector128 && Isa.PackedSimd), (SimdTier.Vector128, Isa.Vector128)),
        // StructuredAppendPlanner.TryNarrowWithLanes / WalkLanes: the chunk-budget walks over eight budgets at once. The 128-bit tier's saturating add is SSE2 on x64 and PackedSimd on WebAssembly.
        new("StructuredAppendLanes", (SimdTier.Vector256, Isa.Vector256), (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.Sse2, Isa.Vector128 && Isa.Sse2), (SimdTier.PackedSimd, Isa.Vector128 && Isa.PackedSimd), (SimdTier.Vector128, Isa.Vector128)),
        // StructuredAppendPlanner.Parity: the XOR of the message's encoded bytes
        new("StructuredAppendParity", (SimdTier.AdvSimd, Isa.AdvSimd)),
        // StructuredAppendScanner.ModeBoundaries / Utf8PrefixLength: the character boundaries of the single-mode cost model
        new("StructuredAppendScanner", (SimdTier.AdvSimd, Isa.AdvSimd)),

        // ---- Micro QR ----

        // MicroQRBinaryEncoder, Byte mode: the Latin-1 check and the narrowing of the payload
        new("MicroQRByteSegment", (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.Sse2, Isa.Sse2)),
        // MicroQRModulePlacer.PlaceSymbol: placement, masking and mask selection
        new("MicroQRModulePlacer", (SimdTier.Avx2Pext, Isa.Avx2Pext), (SimdTier.Ssse3, Isa.Ssse3), (SimdTier.AdvSimd, Isa.AdvSimd)),
        // MicroQRImageDecoder.SampleGrid: the affine module-centre sampler of the grid searches. Coordinates are converted as PerspectiveGridSampler's.
        new("MicroQRSampleGrid", (SimdTier.Sse2, Isa.Vector128 && Isa.Sse2), (SimdTier.PackedSimd, Isa.Vector128 && Isa.PackedSimd), (SimdTier.Vector128, Isa.Vector128)),

        // ---- rMQR ----

        // RmQRBinaryEncoder.WriteNumeric / WriteAlphanumeric: the value kernels, which ask for SSSE3 beside SSE4.1
        new("RmQRValueSegments", (SimdTier.Sse41, Isa.Sse41 && Isa.Ssse3)),
        // RmQRBinaryEncoder.WriteLatin1: the Byte segment's narrowing
        new("RmQRLatin1Segment", (SimdTier.Sse2, Isa.Sse2), (SimdTier.Vector128, Isa.Vector128)),
        // RmQRModulePlacer: masked bit expansion (the SSSE3 step also finishes what the AVX2 step leaves) and, on ARM64, the block and run stores
        new("RmQRModulePlacer", (SimdTier.Avx2, Isa.Avx2), (SimdTier.Ssse3, Isa.Ssse3), (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.PackedSimd, Isa.PackedSimd)),
        // RmQRMatrixDecoder.ExtractCodewords: codeword extraction, x64 bit planes or ARM64 pair planes. The 128-bit tier is the pair planes on portable vectors, which WebAssembly takes only for symbols of 44 stream bits or more per eight columns.
        new("RmQRExtractCodewords", (SimdTier.Avx2Pext, Isa.Avx2Pext), (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.PackedSimd, Isa.Vector128 && Isa.PackedSimd), (SimdTier.Vector128, Isa.Vector128)),
        // RmQRImageDecoder.ClassifySubFinderLattice: the sub-finder lattice classification. Coordinates are converted as PerspectiveGridSampler's.
        new("RmQRSubFinderLattice", (SimdTier.Sse2, Isa.Vector128 && Isa.Sse2), (SimdTier.PackedSimd, Isa.Vector128 && Isa.PackedSimd), (SimdTier.Vector128, Isa.Vector128)),
        // RmQRImageDecoder.SampleGrid: the perspective sampler. Coordinates are converted as PerspectiveGridSampler's.
        new("RmQRSampleGrid", (SimdTier.Sse2, Isa.Vector128 && Isa.Sse2), (SimdTier.PackedSimd, Isa.Vector128 && Isa.PackedSimd), (SimdTier.Vector128, Isa.Vector128)),
    ];

    /// <summary>
    /// The instruction sets a build class always has, those it never has, and those it leaves to the CPU, in groups a CPU has or lacks together
    /// (with AVX present, a CPU with GFNI has its 256-bit form too).
    /// </summary>
    internal static (SimdTier[] Present, SimdTier[] Absent, SimdTier[][] LeftToCpu) Definition(SimdBuildClass buildClass) => buildClass switch
    {
        SimdBuildClass.X64Sse => ([Vector128, Sse2, Ssse3, Sse41], [Vector256, Avx2, Avx2Pext, GfniV256, AdvSimd, AdvSimdDp, PackedSimd], [[Gfni]]),
        SimdBuildClass.X64Avx2 => ([Vector128, Vector256, Sse2, Ssse3, Sse41, Avx2], [AdvSimd, AdvSimdDp, PackedSimd], [[Gfni, GfniV256], [Avx2Pext]]),
        SimdBuildClass.Arm64 => ([Vector128, AdvSimd], [Vector256, Sse2, Ssse3, Sse41, Avx2, Avx2Pext, Gfni, GfniV256, PackedSimd], [[AdvSimdDp]]),
        SimdBuildClass.Wasm => ([Vector128, PackedSimd], [Vector256, Sse2, Ssse3, Sse41, Avx2, Avx2Pext, Gfni, GfniV256, AdvSimd, AdvSimdDp], []),
        _ => throw new ArgumentOutOfRangeException(nameof(buildClass), buildClass, "Unknown build class."),
    };

    /// <summary>
    /// Which tier each kernel takes per build class: the answer to "which kernel runs which tier on which build", and CI holds every build to it (<c>--simd-class</c> of tests/FeatherQR.AotAnalysis and tests/FeatherQR.WasmReport). The spec .github/docs/specs/qrcode-symbologies.md says what keeps it true, and SimdTiersDocTest renders it into .github/docs/specs/qrcode-simd-tiers.md.
    /// A cell with one tier is that tier. A cell with more lists what the CPU decides between, most preferred first: the first whose instruction set the process has is expected, and the last when it has none of them.
    /// </summary>
    internal static SimdExpectation[] Expected() =>
    [
        //  kernel                     x64, no AVX      x64, AVX2             ARM64                 WebAssembly

        // ---- Shared across symbologies ----
        new("TextAnalyzer",            [Sse2],          [Avx2],               [AdvSimd],            [PackedSimd]),
        new("ModuleBitPacker",         [Ssse3],         [Avx2],               [AdvSimd],            [PackedSimd]),
        new("ModeSegmenterLanes",      [Sse2],          [Vector256],          [AdvSimd],            [PackedSimd]),
        new("EccBinaryEncoder",        [Gfni, Ssse3],   [GfniV256, Ssse3],    [AdvSimd],            [PackedSimd]),
        new("EccBinaryDecoder",        [Vector128],     [GfniV256, Vector128], [AdvSimd],           [Vector128]),
        new("LuminanceConverter",      [Sse2],          [Avx2],               [AdvSimdDp, Vector128], [PackedSimd]),
        new("LuminanceInverter",       [Vector128],     [Vector256],          [Vector128],          [Vector128]),
        new("LuminanceHalver",         [Vector128],     [Vector256],          [Vector128],          [PackedSimd]),
        // WebAssembly keeps the 128-bit tier though AOT-compiled it counts dense input (soft, noise, a gradient) slower than scalar: a decode of such input does not move, and rendered symbols count several times faster
        new("Binarizer",               [Vector128],     [Vector256],          [AdvSimd],            [Vector128]),
        new("LocalBinarizer",          [Vector128],     [Vector256],          [Vector128],          [Vector128]),
        new("FinderRowMask",           [Vector128],     [Vector256],          [AdvSimd],            [Vector128]),
        new("FinderRowEdges",          [Vector128],     [Vector256],          [AdvSimd],            [Vector128]),
        new("PerspectiveGridSampler",  [Sse2],          [Vector256],          [Vector128],          [PackedSimd]),

        // ---- Standard QR ----
        // WebAssembly stays scalar: the expand is too small a share of an encode there
        new("ModulePlacerExpandBits",  [Ssse3],         [Avx2],               [AdvSimd],            [Scalar]),
        new("ModulePlacerMaskCode",    [Ssse3],         [Avx2],               [AdvSimd],            [PackedSimd]),
        // x64 with AVX2 keeps the SSE4.1 tier: 32 characters a step gain only on long runs, no more than holding the writer in registers gives the sixteen-character loop, and lose on short ones
        new("QRAlphanumericWriter",    [Sse41],         [Sse41],              [AdvSimd],            [PackedSimd]),
        // x64 with AVX2 keeps the SSSE3 tier: 24 digits a step are no faster. WebAssembly stays scalar: twelve digits a step on its SIMD gain little AOT-compiled and lose interpreted, and one flag gates both
        new("QRNumericWriter",         [Ssse3],         [Ssse3],              [AdvSimd],            [Scalar]),
        // WebAssembly keeps the 128-bit tier though the interpreter runs it slower than scalar: AOT-compiled it is faster, and the search is a small share of a decode there
        new("AlignmentRowMask",        [Vector128],     [Vector256],          [AdvSimd],            [Vector128]),
        // ARM64 takes the 128-bit tier: a tier of its own intrinsics ran no faster on either JIT and 2 % slower as NativeAOT code
        new("QRSampleGridPiecewise",   [Sse2],          [Avx2],               [Vector128],          [PackedSimd]),
        new("StructuredAppendLanes",   [Sse2],          [Vector256],          [AdvSimd],            [PackedSimd]),
        // x64 and WebAssembly stay scalar: the parity pass is too small a share of a Structured Append encode there
        new("StructuredAppendParity",  [Scalar],        [Scalar],             [AdvSimd],            [Scalar]),
        // x64 and WebAssembly stay scalar: the scanner is too small a share of a Structured Append encode there
        new("StructuredAppendScanner", [Scalar],        [Scalar],             [AdvSimd],            [Scalar]),

        // ---- Micro QR ----
        // WebAssembly stays scalar: a vector step for the 8 to 15 chars saves too little of an M4 Byte encode
        new("MicroQRByteSegment",      [Sse2],          [Sse2],               [AdvSimd],            [Scalar]),
        // WebAssembly stays scalar: the 16-module vector unpack gains little AOT-compiled and loses interpreted, and one flag gates both
        new("MicroQRModulePlacer",     [Ssse3],         [Avx2Pext, Ssse3],    [AdvSimd],            [Scalar]),
        new("MicroQRSampleGrid",       [Sse2],          [Sse2],               [Vector128],          [PackedSimd]),

        // ---- rMQR ----
        // ARM64 and WebAssembly stay scalar: a vector step gains a few percent of an encode at most, and inside the encode's switch, which the
        // three writers share, it slows the others and loses end to end (the rMQR encoder record)
        new("RmQRValueSegments",       [Sse41],         [Sse41],              [Scalar],             [Scalar]),
        new("RmQRLatin1Segment",       [Sse2],          [Sse2],               [Vector128],          [Vector128]),
        new("RmQRModulePlacer",        [Ssse3],         [Avx2],               [AdvSimd],            [PackedSimd]),
        new("RmQRExtractCodewords",    [Vector128],     [Avx2Pext, Vector128], [AdvSimd],           [PackedSimd]),
        new("RmQRSubFinderLattice",    [Sse2],          [Sse2],               [Vector128],          [PackedSimd]),
        new("RmQRSampleGrid",          [Sse2],          [Sse2],               [Vector128],          [PackedSimd]),
    ];

    /// <summary>What in this process disagrees with <see cref="Expected"/> for <paramref name="buildClass"/>. Empty when nothing does.</summary>
    internal static List<string> Check(SimdBuildClass buildClass) => Check(buildClass, Report(), IsaReport());

    /// <summary>
    /// <see cref="Check(SimdBuildClass)"/> over the given kernels and instruction sets.
    /// A process that is not of the build class, one with an instruction set the class never has or without one it always has, is reported as that alone: every kernel would only repeat it.
    /// </summary>
    internal static List<string> Check(SimdBuildClass buildClass, SimdKernel[] kernels, (SimdTier Tier, bool Available)[] isa)
    {
        var problems = new List<string>();
        var (present, absent, _) = Definition(buildClass);
        foreach (var (tier, available) in isa)
        {
            if (!available && Array.IndexOf(present, tier) >= 0)
                problems.Add($"{buildClass} always has {tier}, and this process does not");
            else if (available && Array.IndexOf(absent, tier) >= 0)
                problems.Add($"{buildClass} never has {tier}, and this process does");
        }
        if (problems.Count > 0)
            return problems;

        var rows = Expected();
        foreach (var kernel in kernels)
        {
            var row = Array.Find(rows, r => r.Kernel == kernel.Name);
            if (row is null)
            {
                problems.Add($"{kernel.Name} has no row in the table");
                continue;
            }
            var cell = row.For(buildClass);
            var expected = cell[cell.Length - 1];
            for (var i = 0; i < cell.Length - 1; i++)
            {
                if (IsAvailable(isa, cell[i]))
                {
                    expected = cell[i];
                    break;
                }
            }
            if (kernel.Active != expected)
                problems.Add($"{kernel.Name} takes {kernel.Active}, and the table expects {expected} on {buildClass}");
        }
        return problems;
    }

    private static bool IsAvailable((SimdTier Tier, bool Available)[] isa, SimdTier tier)
    {
        foreach (var (candidate, available) in isa)
        {
            if (candidate == tier)
                return available;
        }
        return false;
    }
}
