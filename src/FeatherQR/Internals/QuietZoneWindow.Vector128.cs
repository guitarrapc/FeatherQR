#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
#if !NET10_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif

namespace FeatherQR.Internals;

internal static partial class QuietZoneWindow
{
    /// <summary>Zeroes the 16 bytes at <paramref name="at"/> in one store, for <see cref="ClearGap"/>'s gaps over 16 bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Clear16(Span<byte> target, int at)
    {
#if NET10_0_OR_GREATER
        target.Slice(at, 16).Clear();
#else
        // A vector store, since .NET 8 leaves a constant clear in a block its profile did not see run as a call (see ClearGap). This one
        // it inlines there too: warmed at quiet zone 8 only, the wide gap's block held its two stores (2026-10-07).
        Vector128<byte>.Zero.CopyTo(target.Slice(at, 16));
#endif
    }
}
#endif
