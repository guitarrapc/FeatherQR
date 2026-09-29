#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Wasm;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// Float lanes to 32-bit integers for pixel coordinates: truncated toward zero from -1 (exclusive) up to 2^31; below that range or NaN,
/// 0 or less; above it, at least 2,147,483,520.
/// </summary>
/// <remarks>
/// <see cref="Vector128.ConvertToInt32(Vector128{float})"/> saturates from .NET 9 on, but two builds lower it to a loop slower than
/// converting lane by lane: a default NativeAOT publish on x64, which compiles portable vectors for SSE2 alone, and WebAssembly. Both
/// take their own instruction here instead. Outside the range the lanes differ by platform; callers clamp such lanes into the image
/// or skip them, as the scalar tiers do.
/// </remarks>
internal static class VectorCast
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> ToInt32(Vector128<float> value)
    {
        if (Sse2.IsSupported)
        {
            // cvttps2dq writes int.MinValue for anything out of range: the positive side flips to int.MaxValue
            var truncated = Sse2.ConvertToVector128Int32WithTruncation(value);
            return Sse2.Xor(truncated, Sse.CompareGreaterThanOrEqual(value, Vector128.Create(2147483648f)).AsInt32());
        }
        if (PackedSimd.IsSupported)
        {
            // The instruction saturates, but an AOT-compiled caller has been seen to get int.MinValue past 2^31, so it is only given
            // values it converts exactly: pmax(0, v) sends NaN and the negative side to 0, pmin caps the top at the largest float under 2^31
            return PackedSimd.ConvertToInt32Saturate(PackedSimd.PseudoMin(PackedSimd.PseudoMax(Vector128<float>.Zero, value), Vector128.Create(2147483520f)));
        }
        return Vector128.ConvertToInt32(value);
    }
}
#endif
