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
/// What a build runs is printed from that build (tests/FeatherQR.AotAnalysis does it for NativeAOT) instead of read off the code, and SimdTiersTest keeps this table and the code together:
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
}
