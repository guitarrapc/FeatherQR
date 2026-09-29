#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Wasm;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// Float lanes to pixel indices for the samplers and the sub-finder lattice.
/// </summary>
/// <remarks>
/// <see cref="Vector128.ConvertToInt32(Vector128{float})"/> saturates from .NET 9 on, which a default NativeAOT publish on x64 (portable
/// vectors compiled for SSE2 alone) and WebAssembly lower to a loop slower than converting lane by lane. Here the conversion only meets
/// values it takes exactly, or lanes the caller does not read, so the plain instruction does.
/// </remarks>
internal static class VectorCast
{
    /// <summary>
    /// The pixel each lane falls in, as <see cref="PixelIndex.Clamp(float, int)"/> finds it: truncated and clamped to [0, <paramref name="last"/>],
    /// NaN at 0. <paramref name="last"/> is the last column (or row), a whole number.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> ToPixel(Vector128<float> coordinate, Vector128<float> last)
    {
        // SSE2 has no unsigned conversion: both edges are clamped first, and the max's operand order sends NaN to 0
        if (Sse2.IsSupported)
            return Sse2.ConvertToVector128Int32WithTruncation(Sse.Min(Sse.Max(coordinate, Vector128<float>.Zero), last));
        // The unsigned conversion takes NaN and the near side to 0 itself; the min caps the far side and keeps NaN
        if (PackedSimd.IsSupported)
            return PackedSimd.ConvertToUInt32Saturate(PackedSimd.PseudoMin(coordinate, last)).AsInt32();
        return Vector128.ConvertToUInt32(Vector128.Min(coordinate, last)).AsInt32();
    }

    /// <summary>
    /// Truncated toward zero, exact from -1 (exclusive) up to 2^31; outside that range or at NaN the lane depends on the platform,
    /// for callers that do not read such lanes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> ToInt32Native(Vector128<float> value)
    {
        if (Sse2.IsSupported)
            return Sse2.ConvertToVector128Int32WithTruncation(value);
        if (PackedSimd.IsSupported)
            return PackedSimd.ConvertToInt32Saturate(value);
        return Vector128.ConvertToInt32(value);
    }
}
#endif
