using System.Runtime.CompilerServices;

namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// The pixel a coordinate falls in, for the scalar samplers: its floor, clamped into the image, with NaN at 0.
/// </summary>
internal static class PixelIndex
{
    /// <summary>The column (or row) holding <paramref name="coordinate"/> in an image <paramref name="limit"/> pixels across.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Clamp(float coordinate, int limit)
    {
        // The far edge is taken before the conversion, so it need not saturate, which not every runtime's cast does;
        // NaN and the near side convert to 0 or below. The native conversion is one instruction on x64, where the cast
        // saturates in several; on WebAssembly the cast is the faster of the two.
#if NET9_0_OR_GREATER
        var pixel = coordinate >= limit ? limit - 1
            : OperatingSystem.IsBrowser() ? (int)coordinate : float.ConvertToIntegerNative<int>(coordinate);
#else
        var pixel = coordinate >= limit ? limit - 1 : (int)coordinate;
#endif
        if (pixel < 0)
            pixel = 0;
        return pixel;
    }
}
