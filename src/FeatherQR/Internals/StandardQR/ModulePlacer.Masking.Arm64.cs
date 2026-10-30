#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace FeatherQR.Internals.StandardQR;

/// <summary>
/// Vectorized mask pattern selection for ARM64 with AdvSimd (NEON).
/// Selected at runtime by <see cref="ModulePlacer.MaskCode"/>. It produces byte-identical matrices and identical pattern selections to the scalar bit-packed implementation in ModulePlacer.Masking.cs (verified by ModulePlacerMaskAdvSimdParityTest).
///
/// Two tiers, split at 64 modules as <see cref="MaskCodeAdvSimd"/> does, both on the 128-bit tier's scorers:
/// - Versions 1-11: the lane-per-pattern scorer (<see cref="MaskCode64Vector128"/> in ModulePlacer.Masking.Simd.cs), two candidates a
///   vector and four groups a call, with that tier's per-version pre-masked templates, format overlays and checkpoint.
/// - Versions 12-40: the transposed scorer (the design is in ModulePlacer.Masking.Transposed.cs). It masks and scores with the 128-bit tier's
///   code (ModulePlacer.Masking.Transposed.Vector128.cs).
/// What this file adds to them:
/// - Byte&lt;-&gt;bit edges of 16 modules a step in both tiers. The 128-bit tier packs with a compare and movemask, which ARM64 has no
///   instruction for, and unpacks eight modules a step. Packing gathers per-byte bit weights (cmeq+bic) and reduces with a uaddlp chain.
///   Unpacking broadcasts the 16-bit delta chunk and replicates bytes with tbl + cmtst (the same sequence as MicroQRModulePlacer.Unpack16).
///   The single-word tier takes them from inside <see cref="PackRows64Vector128"/> and <see cref="MaskCode64Vector128"/>.
/// - The popcount both scorers accumulate is cnt and uadalp, in <see cref="AddPopCount"/>.
///
/// This file only executes under AdvSimd.Arm64.IsSupported, so memory order is always little-endian and the SWAR tail reads skip endianness normalization.
/// </summary>
internal static partial class ModulePlacer
{
    /// <summary>Entry point for the ARM64 tiers. Caller guarantees AdvSimd.Arm64.IsSupported.</summary>
    internal static int MaskCodeAdvSimd(Span<byte> buffer, int size, int version, ReadOnlySpan<byte> blockedMask, QREccLevel eccLevel)
    {
        return size <= 64
            ? MaskCode64Vector128(buffer, size, version, blockedMask, eccLevel)
            : MaskCodeTransposedAdvSimd(buffer, size, version, blockedMask, eccLevel);
    }

    // ---------------------------------
    // Shared NEON pieces
    // ---------------------------------

    /// <summary>Per-byte bit weights [1,2,4,...,128] repeated: byte i of a 16-module chunk contributes bit i.</summary>
    private static readonly Vector128<byte> PackBitSel16 = Vector128.Create(
        (byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);

    /// <summary>Byte-replicate table indices: delta byte 0 -&gt; output bytes 0..7, delta byte 1 -&gt; bytes 8..15 (for tbl).</summary>
    private static readonly Vector128<byte> UnpackSel16 = Vector128.Create(
        (byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1);

    /// <summary>
    /// Packs 16 module bytes (0/1) into 16 bits: non-zero bytes select their bit weight (cmeq+bic), then a uaddlp chain sums each 8-byte half into the low and high result bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Pack16(Vector128<byte> v)
    {
        var zero = Vector128.Equals(v, Vector128<byte>.Zero);
        // The cross-platform helper: Vector128.AndNot(left, right) is left & ~right, which the JIT emits as bic.
        var bits = Vector128.AndNot(PackBitSel16, zero);
        var s = AdvSimd.AddPairwiseWidening(AdvSimd.AddPairwiseWidening(AdvSimd.AddPairwiseWidening(bits)));
        return s.GetElement(0) | (s.GetElement(1) << 8);
    }

    /// <summary>
    /// Packs a row of 0/1 module bytes into bits, 16 modules per SIMD step, then an 8-module SWAR step and a scalar tail.
    /// Bit c = row[c], same contract as PackRowBits64.
    /// </summary>
    internal static ulong PackRowBits64AdvSimd(ReadOnlySpan<byte> row)
    {
        ulong w = 0;
        var c = 0;
        ref var r = ref MemoryMarshal.GetReference(row);
        for (; c + 16 <= row.Length; c += 16)
        {
            w |= Pack16(Vector128.LoadUnsafe(ref r, (nuint)c)) << c;
        }
        for (; c + 8 <= row.Length; c += 8)
        {
            // ARM64 only (AdvSimd-guarded), so the load is little-endian by definition.
            var u = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, c));
            w |= ((u * 0x0102040810204080UL) >> 56) << c;
        }
        for (; c < row.Length; c++)
        {
            if (row[c] != 0)
            {
                w |= 1ul << c;
            }
        }
        return w;
    }

    /// <summary>
    /// Triple-word NEON row packer.
    /// The 16-module chunks are 16-aligned so they never straddle a word boundary. The 8-module SWAR steps stay within a word for the same reason.
    /// </summary>
    private static Row192 PackRowBits192AdvSimd(ReadOnlySpan<byte> row)
    {
        ulong w0 = 0, w1 = 0, w2 = 0;
        var c = 0;
        ref var r = ref MemoryMarshal.GetReference(row);
        for (; c + 16 <= row.Length; c += 16)
        {
            var bits = Pack16(Vector128.LoadUnsafe(ref r, (nuint)c));
            if (c < 64) w0 |= bits << c;
            else if (c < 128) w1 |= bits << (c - 64);
            else w2 |= bits << (c - 128);
        }
        for (; c + 8 <= row.Length; c += 8)
        {
            var u = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, c));
            var bits = (u * 0x0102040810204080UL) >> 56;
            if (c < 64) w0 |= bits << c;
            else if (c < 128) w1 |= bits << (c - 64);
            else w2 |= bits << (c - 128);
        }
        for (; c < row.Length; c++)
        {
            if (row[c] != 0)
            {
                if (c < 64) w0 |= 1ul << c;
                else if (c < 128) w1 |= 1ul << (c - 64);
                else w2 |= 1ul << (c - 128);
            }
        }
        return new Row192(w0, w1, w2);
    }

    /// <summary>Expands a 16-bit delta chunk to 16 bytes of 0/1 (tbl byte-replicate + cmtst bit test).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Unpack16(ushort bits16)
    {
        var src = Vector128.Create(bits16).AsByte();
        var repl = AdvSimd.Arm64.VectorTableLookup(src, UnpackSel16);
        return AdvSimd.CompareTest(repl, PackBitSel16) & Vector128.Create((byte)1);
    }

    /// <summary>XORs a packed 0/1 delta into a byte row, 16 modules per SIMD step, SWAR + scalar tails.</summary>
    internal static void XorUnpackRow64AdvSimd(Span<byte> row, ulong delta)
    {
        var c = 0;
        ref var r = ref MemoryMarshal.GetReference(row);
        for (; c + 16 <= row.Length; c += 16)
        {
            var bits01 = Unpack16((ushort)(delta >> c));
            var cur = Vector128.LoadUnsafe(ref r, (nuint)c);
            (cur ^ bits01).StoreUnsafe(ref r, (nuint)c);
        }
        for (; c + 8 <= row.Length; c += 8)
        {
            var b = (delta >> c) & 0xFF;
            var spread = (b * 0x0101010101010101UL) & 0x8040201008040201UL;
            spread |= spread >> 4;
            spread |= spread >> 2;
            spread |= spread >> 1;
            spread &= 0x0101010101010101UL;
            ref var p = ref Unsafe.Add(ref r, c);
            var cur = Unsafe.ReadUnaligned<ulong>(ref p);
            Unsafe.WriteUnaligned(ref p, cur ^ spread);
        }
        for (; c < row.Length; c++)
        {
            if (((delta >> c) & 1) != 0)
            {
                row[c] ^= 1;
            }
        }
    }

    /// <summary>Triple-word NEON delta unpack (16-aligned chunks never straddle words), SWAR + scalar tails.</summary>
    private static void XorUnpackRow192AdvSimd(ref byte rowRef, int len, in Row192 delta)
    {
        var c = 0;
        for (; c + 16 <= len; c += 16)
        {
            var bits01 = Unpack16((ushort)(delta.WordAt(c >> 6) >> (c & 63)));
            ref var p = ref Unsafe.Add(ref rowRef, c);
            var cur = Vector128.LoadUnsafe(ref p);
            (cur ^ bits01).StoreUnsafe(ref p);
        }
        for (; c + 8 <= len; c += 8)
        {
            var b = (delta.WordAt(c >> 6) >> (c & 63)) & 0xFF;
            var spread = (b * 0x0101010101010101UL) & 0x8040201008040201UL;
            spread |= spread >> 4;
            spread |= spread >> 2;
            spread |= spread >> 1;
            spread &= 0x0101010101010101UL;
            ref var p = ref Unsafe.Add(ref rowRef, c);
            var cur = Unsafe.ReadUnaligned<ulong>(ref p);
            Unsafe.WriteUnaligned(ref p, cur ^ spread);
        }
        for (; c < len; c++)
        {
            if (((delta.WordAt(c >> 6) >> (c & 63)) & 1) != 0)
            {
                Unsafe.Add(ref rowRef, c) ^= 1;
            }
        }
    }

    // ---------------------------------
    // Transposed tier (versions 12-40)
    // ---------------------------------

    /// <summary>Mask selection for versions 12-40 on ARM64: scores all eight candidates transposed, applies the winner to <paramref name="buffer"/>, returns it.</summary>
    internal static int MaskCodeTransposedAdvSimd(Span<byte> buffer, int size, int version, ReadOnlySpan<byte> blockedMask, QREccLevel eccLevel)
    {
        // The per-version tables come from the version's canonical blocked mask, and every production caller passes that mask.
        var layout = GetTransposedLayout(version, size);
        System.Diagnostics.Debug.Assert(blockedMask.SequenceEqual(GetLayout(version).BlockedMask), "MaskCodeTransposedAdvSimd requires the version's canonical blocked mask");
        if (buffer.Length < size * size)
            throw new ArgumentException($"buffer too small: required {size * size}, got {buffer.Length}", nameof(buffer));
        if ((uint)eccLevel > 3)
            throw new ArgumentOutOfRangeException(nameof(eccLevel), eccLevel, "QREccLevel was out of range");

        var plane = layout.Words * layout.Stride;
        var rent = System.Buffers.ArrayPool<ulong>.Shared.Rent(4 * plane + WorkLength(layout));
        try
        {
            var all = rent.AsSpan();
            var r = all.Slice(0, plane);
            var c = all.Slice(plane, plane);
            var rp = all.Slice(2 * plane, plane);
            var cp = all.Slice(3 * plane, plane);
            var work = all.Slice(4 * plane, WorkLength(layout));

            PackTransposedAdvSimd(buffer, layout, version, r, c);
            var bestPattern = 0;
            var bestScore = int.MaxValue;
            for (var pattern = 0; pattern < 8; pattern++)
            {
                MaskCandidateTransposedVector128(r, c, layout, pattern, eccLevel, rp, cp);
                var score = ScoreTransposedVector128(rp, cp, layout, work, bestScore);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestPattern = pattern;
                }
            }

            ApplyWinnerTransposedAdvSimd(buffer, layout, bestPattern);
            return bestPattern;
        }
        finally
        {
            System.Buffers.ArrayPool<ulong>.Shared.Return(rent);
        }
    }

    /// <summary>
    /// Every candidate's score of the unmasked <paramref name="buffer"/> with one abort bound for all, the buffer untouched: the scores
    /// <see cref="MaskCodeTransposedAdvSimd"/> compares, for the parity tests.
    /// </summary>
    internal static void ScoreCandidatesTransposedAdvSimd(ReadOnlySpan<byte> buffer, int version, QREccLevel eccLevel, int abortAbove, Span<int> scores)
    {
        var size = QRCodeData.SizeFromVersion(version);
        var layout = GetTransposedLayout(version, size);
        var plane = layout.Words * layout.Stride;
        var rent = System.Buffers.ArrayPool<ulong>.Shared.Rent(4 * plane + WorkLength(layout));
        try
        {
            var all = rent.AsSpan();
            var r = all.Slice(0, plane);
            var c = all.Slice(plane, plane);
            var rp = all.Slice(2 * plane, plane);
            var cp = all.Slice(3 * plane, plane);
            var work = all.Slice(4 * plane, WorkLength(layout));

            PackTransposedAdvSimd(buffer, layout, version, r, c);
            for (var pattern = 0; pattern < 8; pattern++)
            {
                MaskCandidateTransposedVector128(r, c, layout, pattern, eccLevel, rp, cp);
                scores[pattern] = ScoreTransposedVector128(rp, cp, layout, work, abortAbove);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<ulong>.Shared.Return(rent);
        }
    }

    /// <summary>
    /// Packs the unmasked symbol into the row planes sixteen modules a step (padding rows zero), adds the version information, and
    /// transposes it into the column planes with the 128-bit tier's 64x64 block transpose.
    /// </summary>
    private static void PackTransposedAdvSimd(ReadOnlySpan<byte> buffer, TransposedLayout layout, int version, Span<ulong> r, Span<ulong> c)
    {
        var size = layout.Size;
        var stride = layout.Stride;
        var words = layout.Words;
        for (var y = 0; y < size; y++)
        {
            var row = PackRowBits192AdvSimd(buffer.Slice(y * size, size));
            r[y] = row.W0;
            r[stride + y] = row.W1;
            if (words == 3) r[2 * stride + y] = row.W2;
        }
        FinishRowPlanes(r, layout, version);

        for (var kr = 0; kr < words; kr++)
        {
            for (var kc = 0; kc < words; kc++)
            {
                var block = c.Slice(kr * stride + 64 * kc, 64);
                r.Slice(kc * stride + 64 * kr, 64).CopyTo(block);
                Transpose64Vector128(ref MemoryMarshal.GetReference(block));
            }
            c.Slice(kr * stride + 64 * words, stride - 64 * words).Clear();
        }
    }

    /// <summary>Applies the winning pattern's packed XOR delta to the byte buffer, sixteen modules a step.</summary>
    private static void ApplyWinnerTransposedAdvSimd(Span<byte> buffer, TransposedLayout layout, int bestPattern)
    {
        var size = layout.Size;
        ref var bufRef = ref MemoryMarshal.GetReference(buffer);
        for (int y = 0, tplRow = 0; y < size; y++)
        {
            XorUnpackRow192AdvSimd(ref Unsafe.Add(ref bufRef, y * size), size, WinnerDelta(layout, bestPattern, y, tplRow));
            if (++tplRow == 12) tplRow = 0;
        }
    }

    /// <summary><see cref="ApplyMaskPattern"/> unpacking 16 modules a step, for both row widths.</summary>
    internal static void ApplyMaskPatternAdvSimd(Span<byte> buffer, int size, ReadOnlySpan<byte> blockedMask, int patternIndex)
    {
        // As in ApplyMaskPatternScalar: the unblocked rows are bit slices of a padded copy of the blocked bitmask.
        Span<byte> padded = stackalloc byte[blockedMask.Length + 32];
        blockedMask.CopyTo(padded);
        padded.Slice(blockedMask.Length).Clear();

        var tplBase = patternIndex * 12;
        if (size <= 64)
        {
            var rowMask = size == 64 ? ulong.MaxValue : (1ul << size) - 1;
            for (int y = 0, tplRow = 0; y < size; y++)
            {
                XorUnpackRow64AdvSimd(buffer.Slice(y * size, size), _maskTemplates64[tplBase + tplRow] & AllowedRow64(padded, y * size, rowMask));
                if (++tplRow == 12) tplRow = 0;
            }
            return;
        }

        var rowMask192 = Row192.MaskLow(size);
        ref var bufRef = ref MemoryMarshal.GetReference(buffer);
        for (int y = 0, tplRow = 0; y < size; y++)
        {
            XorUnpackRow192AdvSimd(ref Unsafe.Add(ref bufRef, y * size), size, _maskTemplates[tplBase + tplRow] & Row192.FromBitSlice(padded, y * size).AndNot(rowMask192));
            if (++tplRow == 12) tplRow = 0;
        }
    }
}
#endif
