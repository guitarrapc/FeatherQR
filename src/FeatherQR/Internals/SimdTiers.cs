using static FeatherQR.Internals.SimdTier;
#if NET8_0_OR_GREATER
using Arm = System.Runtime.Intrinsics.Arm;
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
    /// <summary>No vector tier runs; the portable loop does.</summary>
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

    /// <summary>The first tier that can run here, which the dispatch takes for inputs large enough for it; <see cref="SimdTier.Scalar"/> when none can.</summary>
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
/// The kernels do not read this table; each dispatch keeps its own <c>IsSupported</c> reads. That was measured, not chosen.
/// With every dispatch moved onto <see langword="bool"/> properties declared here, the JIT's machine code for the kernels stayed the same but its inlining did not: <c>ModulePlacer.MaskCode</c> stopped being inlined into its caller, and <c>TextAnalyzer.Analyze</c> inlined 167 methods, not 9, before dropping its dead tiers.
/// A probe put the flag behind a property, an aggressively inlined property and a <see langword="static"/> <see langword="readonly"/> field: the JIT inlined the dispatch into its caller only when the dispatch read <c>IsSupported</c> itself. (ILC compiled all of them alike.)
/// </para>
/// <para>
/// A lower tier also finishes the tail of a higher one (<c>ModuleBitPacker</c>) or takes inputs too small for it (<c>QRImageDecoder.SampleGrid</c>), so a tier listed as runnable is one the dispatch can take, not the only one it takes; <see cref="SimdKernel.Active"/> is the most preferred of them.
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
    ];

    /// <summary>
    /// Every kernel with its tiers, most preferred first, and whether each can run in this process.
    /// A tier's condition is its <see cref="Isa"/> condition, with what else the kernel's dispatch asks where it asks more.
    /// </summary>
    internal static SimdKernel[] Report() =>
    [
        // ---- Shared across symbologies ----

        // TextAnalyzer.Analyze: the mode and charset scan of the input text
        new("TextAnalyzer", (SimdTier.Avx2, Isa.Avx2), (SimdTier.Sse2, Isa.Sse2), (SimdTier.AdvSimd, Isa.AdvSimd)),
        // ModuleBitPacker.Pack / Unpack: byte-per-module to MSB-first bits and back; the SSSE3 / AdvSimd step also finishes what the AVX2 step leaves
        new("ModuleBitPacker", (SimdTier.Avx2, Isa.Avx2), (SimdTier.Ssse3, Isa.Ssse3), (SimdTier.AdvSimd, Isa.AdvSimd)),
        // ModeSegmenter.ComputeCostsLanes: the mixed-mode cost walk over eight pieces at once
        new("ModeSegmenterLanes", (SimdTier.Vector256, Isa.Vector256), (SimdTier.AdvSimd, Isa.AdvSimd)),
        // EccBinaryEncoder.CalculateEcc: Reed-Solomon remainder; GFNI runs inside the SSSE3 entry, and its 256-bit form, for blocks over 16 codewords, also asks for AVX2
        new("EccBinaryEncoder", (SimdTier.GfniV256, Isa.GfniV256 && Isa.Avx2), (SimdTier.Gfni, Isa.Gfni), (SimdTier.Ssse3, Isa.Ssse3), (SimdTier.AdvSimd, Isa.AdvSimd)),
        // EccBinaryDecoder.ComputeSyndromes: the syndrome pass
        new("EccBinaryDecoder", (SimdTier.GfniV256, Isa.GfniV256), (SimdTier.AdvSimd, Isa.AdvSimd)),
        // LuminanceConverter.ConvertRgba: RGBA / BGRA pixels to 8-bit luminance
        new("LuminanceConverter", (SimdTier.Avx2, Isa.Avx2), (SimdTier.AdvSimdDp, Isa.AdvSimdDp)),
        // LuminanceInverter: the negative image for the light-on-dark pass
        new("LuminanceInverter", (SimdTier.Vector256, Isa.Vector256), (SimdTier.Vector128, Isa.Vector128)),
        // Binarizer.FillHistogram: the luminance histogram behind the global threshold
        new("Binarizer", (SimdTier.Vector256, Isa.Vector256), (SimdTier.AdvSimd, Isa.AdvSimd)),
        // LocalBinarizer: block statistics, block thresholds and the dark count of the regional retry
        new("LocalBinarizer", (SimdTier.Vector128, Isa.Vector128)),
        // FinderPatternFinder.ScanRowMask: a row's dark bitmask for the finder search's mask walk
        new("FinderRowMask", (SimdTier.Vector256, Isa.Vector256), (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.Vector128, Isa.Vector128)),
        // FinderPatternFinder.ScanRowEdges: the finder search's edge-list row kernel, sixteen windows a step on 256-bit vectors and eight on ARM64
        new("FinderRowEdges", (SimdTier.Vector256, Isa.Vector256), (SimdTier.AdvSimd, Isa.AdvSimd)),

        // ---- Standard QR ----

        // ModulePlacer.ExpandBits: message bits to module bytes; the SSSE3 step also finishes what the AVX2 step leaves
        new("ModulePlacerExpandBits", (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.Avx2, Isa.Avx2), (SimdTier.Ssse3, Isa.Ssse3)),
        // ModulePlacer.MaskCode: mask scoring and selection
        new("ModulePlacerMaskCode", (SimdTier.Avx2, Isa.Avx2), (SimdTier.AdvSimd, Isa.AdvSimd)),
        // AlignmentPatternFinder.ScanRowMask: a row's dark bitmask for the alignment search
        new("AlignmentRowMask", (SimdTier.Vector256, Isa.Vector256), (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.Vector128, Isa.Vector128)),
        // QRImageDecoder.SampleGrid: the four-point sampler; the 128-bit tier also takes grids too small for the 256-bit one
        new("QRSampleGrid", (SimdTier.Vector256, Isa.Vector256), (SimdTier.Vector128, Isa.Vector128)),
        // QRImageDecoder.SampleGridPiecewise: the piecewise mesh sampler
        new("QRSampleGridPiecewise", (SimdTier.Avx2, Isa.Avx2), (SimdTier.AdvSimd, Isa.AdvSimd)),
        // StructuredAppendPlanner.TryNarrowWithLanes / WalkLanes: the chunk-budget walks over eight budgets at once
        new("StructuredAppendLanes", (SimdTier.Vector256, Isa.Vector256), (SimdTier.AdvSimd, Isa.AdvSimd)),
        // StructuredAppendPlanner.Parity: the XOR of the message's encoded bytes
        new("StructuredAppendParity", (SimdTier.AdvSimd, Isa.AdvSimd)),
        // StructuredAppendScanner.ModeBoundaries / Utf8PrefixLength: the character boundaries of the single-mode cost model
        new("StructuredAppendScanner", (SimdTier.AdvSimd, Isa.AdvSimd)),

        // ---- Micro QR ----

        // MicroQRBinaryEncoder, Byte mode: the Latin-1 check and the narrowing of the payload
        new("MicroQRByteSegment", (SimdTier.AdvSimd, Isa.AdvSimd), (SimdTier.Sse2, Isa.Sse2)),
        // MicroQRModulePlacer.PlaceSymbol: placement, masking and mask selection
        new("MicroQRModulePlacer", (SimdTier.Avx2Pext, Isa.Avx2Pext), (SimdTier.Ssse3, Isa.Ssse3), (SimdTier.AdvSimd, Isa.AdvSimd)),
        // MicroQRImageDecoder.SampleGrid: the affine module-centre sampler of the grid searches
        new("MicroQRSampleGrid", (SimdTier.Vector128, Isa.Vector128)),

        // ---- rMQR ----

        // RmQRBinaryEncoder.WriteNumeric / WriteAlphanumeric: the value kernels, which ask for SSSE3 beside SSE4.1
        new("RmQRValueSegments", (SimdTier.Sse41, Isa.Sse41 && Isa.Ssse3)),
        // RmQRBinaryEncoder.WriteLatin1: the Byte segment's narrowing
        new("RmQRLatin1Segment", (SimdTier.Sse2, Isa.Sse2), (SimdTier.Vector128, Isa.Vector128)),
        // RmQRModulePlacer: masked bit expansion (the SSSE3 step also finishes what the AVX2 step leaves) and, on ARM64, the block and run stores
        new("RmQRModulePlacer", (SimdTier.Avx2, Isa.Avx2), (SimdTier.Ssse3, Isa.Ssse3), (SimdTier.AdvSimd, Isa.AdvSimd)),
        // RmQRMatrixDecoder.ExtractCodewords: codeword extraction, x64 bit planes or ARM64 pair planes
        new("RmQRExtractCodewords", (SimdTier.Avx2Pext, Isa.Avx2Pext), (SimdTier.AdvSimd, Isa.AdvSimd)),
        // RmQRImageDecoder.ClassifySubFinderLattice: the sub-finder lattice classification
        new("RmQRSubFinderLattice", (SimdTier.Vector128, Isa.Vector128)),
        // RmQRImageDecoder.SampleGrid: the perspective sampler
        new("RmQRSampleGrid", (SimdTier.Vector128, Isa.Vector128)),
    ];

    /// <summary>
    /// The instruction sets a build class always has, those it never has, and those it leaves to the CPU, in groups a CPU has or lacks together
    /// (with AVX present, a CPU with GFNI has its 256-bit form too).
    /// </summary>
    internal static (SimdTier[] Present, SimdTier[] Absent, SimdTier[][] LeftToCpu) Definition(SimdBuildClass buildClass) => buildClass switch
    {
        SimdBuildClass.X64Sse => ([Vector128, Sse2, Ssse3, Sse41], [Vector256, Avx2, Avx2Pext, GfniV256, AdvSimd, AdvSimdDp], [[Gfni]]),
        SimdBuildClass.X64Avx2 => ([Vector128, Vector256, Sse2, Ssse3, Sse41, Avx2], [AdvSimd, AdvSimdDp], [[Gfni, GfniV256], [Avx2Pext]]),
        SimdBuildClass.Arm64 => ([Vector128, AdvSimd], [Vector256, Sse2, Ssse3, Sse41, Avx2, Avx2Pext, Gfni, GfniV256], [[AdvSimdDp]]),
        SimdBuildClass.Wasm => ([Vector128], [Vector256, Sse2, Ssse3, Sse41, Avx2, Avx2Pext, Gfni, GfniV256, AdvSimd, AdvSimdDp], []),
        _ => throw new ArgumentOutOfRangeException(nameof(buildClass), buildClass, "Unknown build class."),
    };

    /// <summary>
    /// Which tier each kernel takes per build class: the answer to "which kernel runs which tier on which build", and CI holds every build to it (<c>--simd-class</c> of tests/FeatherQR.AotAnalysis and tests/FeatherQR.WasmReport); .github/docs/specs/qrcode-symbologies.md says what keeps it true.
    /// A cell with one tier is that tier. A cell with more lists what the CPU decides between, most preferred first: the first whose instruction set the process has is expected, and the last when it has none of them.
    /// </summary>
    internal static SimdExpectation[] Expected() =>
    [
        //  kernel                     x64, no AVX      x64, AVX2             ARM64                 WebAssembly

        // ---- Shared across symbologies ----
        new("TextAnalyzer",            [Sse2],          [Avx2],               [AdvSimd],            [Scalar]),
        new("ModuleBitPacker",         [Ssse3],         [Avx2],               [AdvSimd],            [Scalar]),
        new("ModeSegmenterLanes",      [Scalar],        [Vector256],          [AdvSimd],            [Scalar]),
        new("EccBinaryEncoder",        [Gfni, Ssse3],   [GfniV256, Ssse3],    [AdvSimd],            [Scalar]),
        new("EccBinaryDecoder",        [Scalar],        [GfniV256, Scalar],   [AdvSimd],            [Scalar]),
        new("LuminanceConverter",      [Scalar],        [Avx2],               [AdvSimdDp, Scalar],  [Scalar]),
        new("LuminanceInverter",       [Vector128],     [Vector256],          [Vector128],          [Vector128]),
        new("Binarizer",               [Scalar],        [Vector256],          [AdvSimd],            [Scalar]),
        new("LocalBinarizer",          [Vector128],     [Vector128],          [Vector128],          [Vector128]),
        new("FinderRowMask",           [Vector128],     [Vector256],          [AdvSimd],            [Vector128]),
        new("FinderRowEdges",          [Scalar],        [Vector256],          [AdvSimd],            [Scalar]),

        // ---- Standard QR ----
        new("ModulePlacerExpandBits",  [Ssse3],         [Avx2],               [AdvSimd],            [Scalar]),
        new("ModulePlacerMaskCode",    [Scalar],        [Avx2],               [AdvSimd],            [Scalar]),
        new("AlignmentRowMask",        [Vector128],     [Vector256],          [AdvSimd],            [Vector128]),
        new("QRSampleGrid",            [Vector128],     [Vector256],          [Vector128],          [Vector128]),
        new("QRSampleGridPiecewise",   [Scalar],        [Avx2],               [AdvSimd],            [Scalar]),
        new("StructuredAppendLanes",   [Scalar],        [Vector256],          [AdvSimd],            [Scalar]),
        new("StructuredAppendParity",  [Scalar],        [Scalar],             [AdvSimd],            [Scalar]),
        new("StructuredAppendScanner", [Scalar],        [Scalar],             [AdvSimd],            [Scalar]),

        // ---- Micro QR ----
        new("MicroQRByteSegment",      [Sse2],          [Sse2],               [AdvSimd],            [Scalar]),
        new("MicroQRModulePlacer",     [Ssse3],         [Avx2Pext, Ssse3],    [AdvSimd],            [Scalar]),
        new("MicroQRSampleGrid",       [Vector128],     [Vector128],          [Vector128],          [Vector128]),

        // ---- rMQR ----
        new("RmQRValueSegments",       [Sse41],         [Sse41],              [Scalar],             [Scalar]),
        new("RmQRLatin1Segment",       [Sse2],          [Sse2],               [Vector128],          [Vector128]),
        new("RmQRModulePlacer",        [Ssse3],         [Avx2],               [AdvSimd],            [Scalar]),
        new("RmQRExtractCodewords",    [Scalar],        [Avx2Pext, Scalar],   [AdvSimd],            [Scalar]),
        new("RmQRSubFinderLattice",    [Vector128],     [Vector128],          [Vector128],          [Vector128]),
        new("RmQRSampleGrid",          [Vector128],     [Vector128],          [Vector128],          [Vector128]),
    ];

    /// <summary>What in this process disagrees with <see cref="Expected"/> for <paramref name="buildClass"/>; empty when nothing does.</summary>
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
