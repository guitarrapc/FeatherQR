#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics.X86;

namespace FeatherQR.Internals;

/// <summary>
/// Runtime CPU properties that the <c>IsSupported</c> flags of the hardware intrinsic classes do not express. net8.0+ only: every consumer is inside a SIMD tier.
/// </summary>
internal static class HardwareCapabilities
{
    /// <summary>
    /// PDEP/PEXT are microcoded on AMD before Zen 3 (hundreds of cycles per instruction), which would turn a BMI2 kernel into a large regression there: true only on vendors without that lineage, or AMD family 0x19 (Zen 3) and later.
    /// Implies <see cref="Bmi2.X64.IsSupported"/>, so callers do not repeat that check.
    /// </summary>
    internal static readonly bool HasFastPext = DetectFastPext();

    private static bool DetectFastPext()
    {
        if (!Bmi2.X64.IsSupported)
        {
            return false;
        }
        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        // Hygon Dhyana is a Zen 1 derivative and inherits its microcoded PDEP/PEXT, so
        // it is gated with AMD rather than with the vendors that implement them in
        // hardware. Its family (0x18) is below the Zen 3 cutoff, so the same test covers it.
        var isAmdLineage = (ebx == 0x68747541 && edx == 0x69746E65 && ecx == 0x444D4163)  // "AuthenticAMD"
                        || (ebx == 0x6F677948 && edx == 0x6E65476E && ecx == 0x656E6975); // "HygonGenuine"
        if (!isAmdLineage)
        {
            return true;
        }
        var (eax, _, _, _) = X86Base.CpuId(1, 0);
        var baseFamily = (eax >> 8) & 0xF;
        var family = baseFamily == 0xF ? baseFamily + ((eax >> 20) & 0xFF) : baseFamily;
        return family >= 0x19;
    }
}
#endif
