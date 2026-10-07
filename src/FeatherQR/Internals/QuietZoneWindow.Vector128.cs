#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals;

internal static partial class QuietZoneWindow
{
    /// <summary>Zeroes the 16 bytes at <paramref name="at"/> in one store, for <see cref="ClearGap"/>'s gaps of 17 to 32 bytes.</summary>
    // A vector store on every build: .NET 8 leaves a constant clear in a block its profile did not see run as a call (see ClearGap),
    // and 32-bit x86 on .NET 10 compiles a constant 16-byte clear as a call to SpanHelpers.ClearWithoutReferences, where the vector
    // store is one instruction (2026-10-07).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Clear16(Span<byte> target, int at) => Vector128<byte>.Zero.CopyTo(target.Slice(at, 16));
}
#endif
