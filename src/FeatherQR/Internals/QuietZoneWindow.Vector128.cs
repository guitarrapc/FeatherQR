#if NET8_0_OR_GREATER
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals;

internal static partial class QuietZoneWindow
{
    /// <summary>
    /// Zeroes <paramref name="gap"/>, 17 to 64 bytes, as two 16-byte stores from its ends, and two more 16 bytes in from them when it
    /// is over 32 bytes, for <see cref="ClearGap"/>.
    /// </summary>
    // Vector stores on every build: .NET 8 leaves a constant clear in a block its profile did not see run as a call (see ClearGap),
    // and 32-bit x86 on .NET 10 compiles a constant 16-byte clear as a call to SpanHelpers.ClearWithoutReferences, where the vector
    // store is one instruction (2026-10-07). The caller's slice checks the gap once and the stores fall inside it. A checked slice and
    // copy a store, four a gap, used up the JIT's inline budget of the .NET 10 span encodes that inline this through ClearMargins and
    // CenterCore, and Micro QR's and Standard QR's then compiled the 8- and 16-byte clears as calls, once a row (2026-10-08).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClearByStores(Span<byte> gap)
    {
        Debug.Assert(gap.Length is > 16 and <= 64);
        ref var start = ref MemoryMarshal.GetReference(gap);
        var last = (nuint)gap.Length - 16;
        Vector128<byte>.Zero.StoreUnsafe(ref start);
        Vector128<byte>.Zero.StoreUnsafe(ref start, last);
        if (gap.Length > 32)
        {
            Vector128<byte>.Zero.StoreUnsafe(ref start, 16);
            Vector128<byte>.Zero.StoreUnsafe(ref start, last - 16);
        }
    }
}
#endif
