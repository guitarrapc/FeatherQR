#if NET8_0_OR_GREATER
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Wasm;
using System.Runtime.Intrinsics.X86;

namespace FeatherQR.Internals;

internal static partial class ModeSegmenter
{
    private const int Vector128PlanLanes = 4;

    /// <summary><see cref="ComputeCostsLanes"/> on this tier on any 128-bit machine: the dispatch takes it only without 256-bit vectors, so its parity test enters here.</summary>
    internal static void ComputeCostsLanesVector128(ReadOnlySpan<char> text, ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths, EciMode charset, int modeIndicatorBits, int cciNumeric, int cciAlnum, int cciByte, Span<byte> table, Span<int> costs, Span<int> finalStates)
    {
        finalStates.Slice(0, starts.Length).Fill(-1);
        ComputeCostsGroupsVector128(text, starts, lengths, charset, (modeIndicatorBits + cciNumeric + 4) << 3, (modeIndicatorBits + cciAlnum + 6) << 3, (modeIndicatorBits + cciByte) << 3, table, costs, finalStates);
    }

    /// <summary>
    /// The ARM64 tier's four-lane groups on portable vectors, for the targets with neither accelerated 256-bit vectors nor NEON: x64 without
    /// AVX and WebAssembly. A group whose useful character count is less than 1.5 times its longest piece takes the scalar keyed loops, as there.
    /// </summary>
    private static void ComputeCostsGroupsVector128(ReadOnlySpan<char> text, ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths, EciMode charset, int openNumeric, int openAlnum, int openByte, Span<byte> table, Span<int> costs, Span<int> finalStates)
    {
        for (var first = 0; first < starts.Length; first += Vector128PlanLanes)
        {
            var count = Math.Min(Vector128PlanLanes, starts.Length - first);
            var longest = 0;
            var total = 0;
            for (var lane = first; lane < first + count; lane++)
            {
                Debug.Assert(lengths[lane] <= MaxTrackedChars);
                longest = Math.Max(longest, lengths[lane]);
                total += lengths[lane];
            }
            if (total * 2 < 3 * longest)
            {
                for (var lane = first; lane < first + count; lane++)
                {
                    var chunk = text.Slice(starts[lane], lengths[lane]);
                    var parents = table.Slice(lane * ParentBytesPerChar);
                    OpenFromStart(chunk, charset, openNumeric, openAlnum, openByte, true, true, parents, out var n1, out var a1, out var b);
                    var best = charset == EciMode.Utf8
                        ? TrackedGeneral(chunk, charset, n1, a1, b, openNumeric, openAlnum, openByte, true, true, parents, LaneTableBytesPerChar)
                        : TrackedLatin(chunk, n1, a1, b, openNumeric, openAlnum, openByte + 64, true, parents, LaneTableBytesPerChar);
                    costs[lane] = FromKey(best, out finalStates[lane]);
                }
                continue;
            }
            if (charset == EciMode.Utf8)
                RunLanesVector128<Utf8Lanes>(text, starts.Slice(first, count), lengths.Slice(first, count), charset, openNumeric, openAlnum, openByte, table.Slice(first * ParentBytesPerChar), costs.Slice(first), finalStates.Slice(first));
            else
                RunLanesVector128<OneByteLanes>(text, starts.Slice(first, count), lengths.Slice(first, count), charset, openNumeric, openAlnum, openByte, table.Slice(first * ParentBytesPerChar), costs.Slice(first), finalStates.Slice(first));
        }
    }

    private static void RunLanesVector128<TBytes>(ReadOnlySpan<char> text, ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths, EciMode charset, int openNumeric, int openAlnum, int openByte, Span<byte> table, Span<int> costs, Span<int> finalStates) where TBytes : ILaneBytes
    {
        const int U = UnreachableKey;
        var lanes = starts.Length;
        const int Width = Vector128PlanLanes;

        var longest = 0;
        for (var lane = 1; lane < lanes; lane++)
        {
            if (lengths[lane] > lengths[longest])
                longest = lane;
        }
        Debug.Assert(lengths[longest] <= MaxTrackedChars);

        // Where each lane reads: its own piece, or the longest for a lane with no piece or none left.
        Span<int> readStart = stackalloc int[Width];
        Span<int> readLength = stackalloc int[Width];
        Span<int> keys = stackalloc int[3 * Width];
        for (var lane = 0; lane < Width; lane++)
        {
            var source = lane < lanes ? lane : longest;
            readStart[lane] = starts[source];
            readLength[lane] = lengths[source];
            OpenFromStart(text.Slice(readStart[lane], readLength[lane]), charset, openNumeric, openAlnum, openByte, allowAlnum: true, allowByte: true, table.Slice(lane * ParentBytesPerChar), out keys[lane], out keys[Width + lane], out keys[2 * Width + lane]);
        }

        var u0 = Vector128.Create(U | StateNumeric0);
        var u1 = Vector128.Create(U | StateNumeric1);
        var u2 = Vector128.Create(U | StateNumeric2);
        var u3 = Vector128.Create(U | StateAlnum0);
        var u4 = Vector128.Create(U | StateAlnum1);
        var n0 = u0;
        var n1 = Vector128.Create<int>(keys.Slice(0, Width));
        var n2 = u2;
        var a0 = u3;
        var a1 = Vector128.Create<int>(keys.Slice(Width, Width));
        var b = Vector128.Create<int>(keys.Slice(2 * Width, Width));

        var vOpenNumeric = Vector128.Create(openNumeric);
        var vOpenAlnum = Vector128.Create(openAlnum);
        var vOpenByte = Vector128.Create(openByte);
        var stateBits = Vector128.Create(7);
        var costBits = Vector128.Create(~7);

        var i = 1;
        var remaining = lanes;
        while (remaining > 0)
        {
            // To the nearest end among the lanes still running.
            var end = int.MaxValue;
            for (var lane = 0; lane < lanes; lane++)
            {
                if (lengths[lane] >= i && lengths[lane] < end && finalStates[lane] < 0)
                    end = lengths[lane];
            }

            int s0 = readStart[0], s1 = readStart[1], s2 = readStart[2], s3 = readStart[3];
            for (; i < end; i++)
            {
                char c0 = text[s0 + i], c1 = text[s1 + i], c2 = text[s2 + i], c3 = text[s3 + i];
                var cls = Vector128.Create(ClassOf(c0), ClassOf(c1), ClassOf(c2), ClassOf(c3));

                var byteBits = Vector128.Create(64);
                var noOpen = Vector128<int>.Zero;
                if (TBytes.Utf8)
                {
                    var chars = Vector128.Create((int)c0, c1, c2, c3);
                    // A lane at a byte order mark only continues its Byte run; past its first character it has one.
                    noOpen = Vector128.Equals(chars, Vector128.Create((int)ByteOrderMark)) & Vector128.Create(U);
                    if (Vector128.EqualsAny(chars & Vector128.Create(0xF800), Vector128.Create(0xD800)))
                    {
                        // A surrogate in some lane: its length depends on its neighbour, asked lane by lane.
                        byteBits = Vector128.Create(
                            LaneByteCost(text, s0, readLength[0], i), LaneByteCost(text, s1, readLength[1], i), LaneByteCost(text, s2, readLength[2], i), LaneByteCost(text, s3, readLength[3], i)) << 6;
                    }
                    else
                    {
                        // One byte, one more above U+007F, one more above U+07FF; a compare is all ones, so it takes one off.
                        byteBits = (Vector128.Create(1) - Vector128.GreaterThan(chars, Vector128.Create(0x7F)) - Vector128.GreaterThan(chars, Vector128.Create(0x7FF))) << 6;
                    }
                }

                var isDigit = Vector128.Equals(cls, Vector128.Create(ClassDigit));
                var isAlnum = Vector128.GreaterThan(cls, Vector128<int>.Zero);

                // The steps of TrackedLatin and TrackedGeneral, every lane taking every branch and keeping what its class allows.
                var numeric = Vector128.Min(Vector128.Min(n0, n1), n2);
                var alnum = Vector128.Min(a0, a1);
                var numericKey = Vector128.Min(n0 + Vector128.Create(32), Vector128.Min(alnum, b) + vOpenNumeric);
                var alnumKey = Vector128.Min(Vector128.Min(numeric, b) + vOpenAlnum, a0 + Vector128.Create(48));
                var openKey = Vector128.Min(numeric, alnum) + vOpenByte;
                if (TBytes.Utf8)
                    openKey = Vector128.Max(openKey, noOpen);
                var byteKey = Vector128.Min(openKey, b) + byteBits;

                // Both bytes of every lane's entry in one 8-byte store; an entry is below 0x4000, so the signed narrowing is exact.
                var parents = (byteKey & stateBits) | ((numericKey & stateBits) << 8) | ((alnumKey & stateBits) << 11);
                MemoryMarshal.Write(table.Slice(i * LaneTableBytesPerChar, Width * ParentBytesPerChar), NarrowEntries(parents).AsUInt64().ToScalar());

                var nn1 = Vector128.ConditionalSelect(isDigit, (numericKey & costBits) | Vector128.Create(StateNumeric1), u1);
                var nn2 = Vector128.ConditionalSelect(isDigit, n1 + Vector128.Create(25), u2);
                n0 = Vector128.ConditionalSelect(isDigit, n2 + Vector128.Create(22), u0);
                n1 = nn1;
                n2 = nn2;
                var na1 = Vector128.ConditionalSelect(isAlnum, (alnumKey & costBits) | Vector128.Create(StateAlnum1), u4);
                a0 = Vector128.ConditionalSelect(isAlnum, a1 + Vector128.Create(39), u3);
                a1 = na1;
                b = (byteKey & costBits) | Vector128.Create(StateByte);
            }

            var best = Vector128.Min(Vector128.Min(Vector128.Min(n0, n1), Vector128.Min(n2, a0)), Vector128.Min(a1, b));
            for (var lane = 0; lane < lanes; lane++)
            {
                if (lengths[lane] != end || finalStates[lane] >= 0)
                    continue;
                costs[lane] = FromKey(best.GetElement(lane), out finalStates[lane]);
                remaining--;
                readStart[lane] = starts[longest];
                readLength[lane] = lengths[longest];
            }
        }
    }

    /// <summary>The four entries as 16-bit lanes in the low half: SSE2's packssdw and WebAssembly's i16x8.narrow_i32x4_s, a portable narrowing elsewhere.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> NarrowEntries(Vector128<int> entries)
    {
        if (Sse2.IsSupported)
            return Sse2.PackSignedSaturate(entries, entries);
        if (PackedSimd.IsSupported)
            return PackedSimd.ConvertNarrowingSaturateSigned(entries, entries);
        return Vector128.Narrow(entries, entries);
    }
}
#endif
