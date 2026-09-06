using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Table-driven placement for Standard QR: everything the placer derives from the version alone is built once per version by the reference painters and cached (<see cref="PlacementLayout"/>): the painted function-module template, the blocked-module bitmask, the zigzag walk over free modules as core indices, and the walk segmented into runs of rows where both strip columns are free.
/// </summary>
/// <remarks>
/// Benchmark-driven (kernel 4.5-9x over the per-call painters + acc64 walk at v40..v1, see the Performance section of specs/standardqr-encoder.md); the reference implementations (<see cref="QRCodeGenerator.PlaceFunctionModulesReference"/>, <see cref="PlaceDataWords(Span{byte}, int, ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>) remain the source of truth: they build the tables and the parity tests hold the fast paths to them byte for byte.
/// Memory per version used: size² template + size²/8 mask + 2 B per free module + a few hundred ops (≤ 95 KB at v40, ~1.3 MB if all 40 versions were ever used).
/// </remarks>
internal static partial class ModulePlacer
{
    /// <summary>A walk segment: a run of rows where both strip modules are free (one 16-bit store per row) or a scatter range through <see cref="PlacementLayout.Index"/>.</summary>
    internal readonly struct PlacementOp
    {
        public readonly int Start;   // first stream bit of the segment
        public readonly int Count;   // run: rows; scatter: modules
        public readonly int Core;    // run: core index of the LEFT module (x-1) on the first row
        public readonly int RowStep; // run: +size (downward) / -size (upward)
        public readonly bool IsRun;

        public PlacementOp(bool isRun, int start, int count, int core, int rowStep)
        {
            IsRun = isRun;
            Start = start;
            Count = count;
            Core = core;
            RowStep = rowStep;
        }
    }

    internal sealed class PlacementLayout
    {
        public readonly int Size;
        public readonly byte[] Template;    // function modules painted (finder / separators / alignment / timing / dark), everything else 0
        public readonly byte[] BlockedMask; // QRCodeGenerator.PlaceFunctionModules bitmask (function + reserved format/version areas)
        public readonly ushort[] Index;     // core index per stream bit position (free modules in zigzag order)
        public readonly PlacementOp[] Ops;  // the same walk as runs + scatter ranges

        public int FreeModules => Index.Length;

        public PlacementLayout(int size, byte[] template, byte[] blockedMask, ushort[] index, PlacementOp[] ops)
        {
            Size = size;
            Template = template;
            BlockedMask = blockedMask;
            Index = index;
            Ops = ops;
        }
    }

    private static readonly PlacementLayout?[] layouts = new PlacementLayout?[41];

    /// <summary>Per-version placement tables (built on first use, published with a volatile write).</summary>
    internal static PlacementLayout GetLayout(int version)
    {
        if (version < 1 || version > 40)
            throw new ArgumentOutOfRangeException(nameof(version), $"Version must be 1-40, got {version}");
        ref var slot = ref layouts[version];
        var layout = Volatile.Read(ref slot);
        if (layout is not null) return layout;
        layout = BuildLayout(version);
        Volatile.Write(ref slot, layout);
        return layout;
    }

    private static PlacementLayout BuildLayout(int version)
    {
        var size = QRCodeData.SizeFromVersion(version);
        var template = new byte[size * size];
        var blockedMask = new byte[(size * size + 7) / 8];
        QRCodeGenerator.PlaceFunctionModulesReference(template, size, version, blockedMask);

        // The same walk as PlaceDataWords (2-column strips from the right, column 6
        // skipped, direction alternating), recording free modules in stream order and
        // segmenting rows where both modules are free into runs.
        var index = new List<ushort>();
        var ops = new List<PlacementOp>();
        var up = true;
        // size is odd, so x walks the even columns down to 8, then (after the column-6
        // skip) the odd columns 5, 3, 1: the pair (x, x-1) always exists, hence x > 0.
        for (var x = size - 1; x > 0; x -= 2)
        {
            if (x == 6) x--;
            var b = up ? (size - 1) * size + x : x;
            var step = up ? -size : size;
            var scatterStart = -1;
            var runRows = 0;
            var runCore = 0;
            var runStart = 0;
            for (var rows = 0; rows < size; rows++, b += step)
            {
                var rightFree = !IsModuleBlocked(blockedMask, b);
                var leftFree = !IsModuleBlocked(blockedMask, b - 1);
                if (rightFree && leftFree)
                {
                    if (scatterStart >= 0)
                    {
                        ops.Add(new PlacementOp(false, scatterStart, index.Count - scatterStart, 0, 0));
                        scatterStart = -1;
                    }
                    if (runRows == 0)
                    {
                        runCore = b - 1;
                        runStart = index.Count;
                    }
                    runRows++;
                    index.Add((ushort)b);
                    index.Add((ushort)(b - 1));
                }
                else
                {
                    if (runRows > 0)
                    {
                        ops.Add(new PlacementOp(true, runStart, runRows, runCore, step));
                        runRows = 0;
                    }
                    if (rightFree || leftFree)
                    {
                        if (scatterStart < 0) scatterStart = index.Count;
                        if (rightFree) index.Add((ushort)b);
                        if (leftFree) index.Add((ushort)(b - 1));
                    }
                }
            }
            if (runRows > 0) ops.Add(new PlacementOp(true, runStart, runRows, runCore, step));
            if (scatterStart >= 0) ops.Add(new PlacementOp(false, scatterStart, index.Count - scatterStart, 0, 0));
            up = !up;
        }

        return new PlacementLayout(size, template, blockedMask, index.ToArray(), ops.ToArray());
    }

    /// <summary>
    /// Fast data placement (same result as <see cref="PlaceDataWords(Span{byte}, int, ReadOnlySpan{byte}, ReadOnlySpan{byte})"/> with the version's canonical blocked mask): the stream is expanded to one byte per bit (AVX2 / SSSE3, scalar otherwise), runs of both-free rows are written as one byte-swapped 16-bit store per row, the remaining free modules through the index table.
    /// Modules beyond the stream end are not written (the caller's buffer already holds the template zeros there).
    /// </summary>
    /// <param name="buffer">Core matrix (size × size bytes) with the function template in place.</param>
    /// <param name="layout">The version's placement tables.</param>
    /// <param name="interleavedData">Interleaved data + ECC codewords (+ remainder byte); bits beyond the free modules are ignored.</param>
    public static void PlaceDataWords(Span<byte> buffer, PlacementLayout layout, ReadOnlySpan<byte> interleavedData)
    {
        var size = layout.Size;
        if (buffer.Length < size * size)
            throw new ArgumentException($"buffer too small: required {size * size}, got {buffer.Length}", nameof(buffer));

        var free = layout.FreeModules;
        var streamBits = Math.Min(interleavedData.Length * 8, free);
        if (streamBits == 0) return;
        var byteCount = (streamBits + 7) / 8;

        // Bit-per-module scratch for the stream: fixed stack budget (versions 1-2 with
        // the vector store slack), pool rental above it.
        if (free + VectorSlack <= StackBitBudget)
        {
            Span<byte> bits = stackalloc byte[StackBitBudget];
            ExpandBits(interleavedData, byteCount, bits);
            PlaceExpanded(ref MemoryMarshal.GetReference(buffer), ref MemoryMarshal.GetReference(bits), layout, streamBits);
        }
        else
        {
            var rented = ArrayPool<byte>.Shared.Rent(free + VectorSlack);
            try
            {
                ExpandBits(interleavedData, byteCount, rented);
                PlaceExpanded(ref MemoryMarshal.GetReference(buffer), ref MemoryMarshal.GetReference(rented.AsSpan()), layout, streamBits);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private const int StackBitBudget = 512;
    private const int VectorSlack = 32; // the AVX2 expand step stores 32 bytes per 4 stream bytes

    /// <summary>bits[8k + j] = bit (7 - j) of message[k] for k &lt; byteCount (MSB first).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ExpandBits(ReadOnlySpan<byte> message, int byteCount, Span<byte> bits)
    {
        ref var src = ref MemoryMarshal.GetReference(message);
        ref var dst = ref MemoryMarshal.GetReference(bits);
        var k = 0;
#if NET8_0_OR_GREATER
        if (Avx2.IsSupported)
        {
            // 4 message bytes -> 32 module bytes per step: broadcast the 4 bytes, in-lane
            // shuffle replicates byte j over lanes 8j..8j+7, AND with the per-lane bit
            // mask + compare-equal yields 0/1 bytes
            var sel = Vector256.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3);
            var bitm = Vector256.Create((byte)128, 64, 32, 16, 8, 4, 2, 1, 128, 64, 32, 16, 8, 4, 2, 1, 128, 64, 32, 16, 8, 4, 2, 1, 128, 64, 32, 16, 8, 4, 2, 1);
            var one = Vector256.Create((byte)1);
            for (; k + 4 <= byteCount; k += 4)
            {
                var v = Vector256.Create(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, k))).AsByte();
                var m = Avx2.Shuffle(v, sel) & bitm;
                (Vector256.Equals(m, bitm) & one).StoreUnsafe(ref dst, (nuint)(k * 8));
            }
        }
        if (Ssse3.IsSupported)
        {
            var sel = Vector128.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1);
            var bitm = Vector128.Create((byte)128, 64, 32, 16, 8, 4, 2, 1, 128, 64, 32, 16, 8, 4, 2, 1);
            var one = Vector128.Create((byte)1);
            for (; k + 2 <= byteCount; k += 2)
            {
                var v = Vector128.Create(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref src, k))).AsByte();
                var m = Ssse3.Shuffle(v, sel) & bitm;
                (Vector128.Equals(m, bitm) & one).StoreUnsafe(ref dst, (nuint)(k * 8));
            }
        }
#endif
        for (; k < byteCount; k++)
        {
            int b = Unsafe.Add(ref src, k);
            ref var d = ref Unsafe.Add(ref dst, k * 8);
            d = (byte)((b >> 7) & 1);
            Unsafe.Add(ref d, 1) = (byte)((b >> 6) & 1);
            Unsafe.Add(ref d, 2) = (byte)((b >> 5) & 1);
            Unsafe.Add(ref d, 3) = (byte)((b >> 4) & 1);
            Unsafe.Add(ref d, 4) = (byte)((b >> 3) & 1);
            Unsafe.Add(ref d, 5) = (byte)((b >> 2) & 1);
            Unsafe.Add(ref d, 6) = (byte)((b >> 1) & 1);
            Unsafe.Add(ref d, 7) = (byte)(b & 1);
        }
    }

    /// <summary>Store pass over the walk segments; stops at the stream end (segments are in stream order).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PlaceExpanded(ref byte dest, ref byte src, PlacementLayout layout, int streamBits)
    {
        ref var idx = ref MemoryMarshal.GetReference(layout.Index.AsSpan());
        var ops = layout.Ops;
        for (var p = 0; p < ops.Length; p++)
        {
            var op = ops[p];
            if (op.Start >= streamBits) break;
            if (op.IsRun)
            {
                var rows = op.Count;
                if (op.Start + 2 * rows > streamBits) rows = (streamBits - op.Start) / 2; // run cut by the stream end
                var k = (nuint)op.Start;
                ref var d = ref Unsafe.Add(ref dest, op.Core);
                var stride = op.RowStep;
                for (var r = 0; r < rows; r++)
                {
                    // walk order: right module (bit k) then left (bit k + 1); memory order is left, right
                    Unsafe.WriteUnaligned(ref d, SwapPair(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref src, k))));
                    k += 2;
                    d = ref Unsafe.Add(ref d, stride);
                }
                // stream ends between the right and left module of a run row
                var placed = op.Start + 2 * rows;
                if (placed < streamBits && placed < op.Start + 2 * op.Count)
                    Unsafe.Add(ref dest, Unsafe.Add(ref idx, placed)) = Unsafe.Add(ref src, placed);
            }
            else
            {
                var end = Math.Min(op.Start + op.Count, streamBits);
                for (var i = op.Start; i < end; i++)
                    Unsafe.Add(ref dest, Unsafe.Add(ref idx, i)) = Unsafe.Add(ref src, i);
            }
        }
    }

    // The bit array holds (right module, left module) in walk order; memory wants the
    // left module first. Read and write go through the same host endianness, so a
    // ReverseEndianness in between always swaps the two BYTES in memory order,
    // whatever the host is — the swap is unconditional by design.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort SwapPair(ushort v) => BinaryPrimitives.ReverseEndianness(v);
}
