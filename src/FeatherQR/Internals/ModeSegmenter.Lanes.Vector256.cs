#if NET8_0_OR_GREATER
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace FeatherQR.Internals;

internal static partial class ModeSegmenter
{
    private static void RunLanes<TBytes>(ReadOnlySpan<char> text, ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths, EciMode charset, int openNumeric, int openAlnum, int openByte, Span<byte> table, Span<int> costs, Span<int> finalStates) where TBytes : ILaneBytes
    {
        const int U = UnreachableKey;
        var lanes = starts.Length;

        var longest = 0;
        for (var lane = 1; lane < lanes; lane++)
        {
            if (lengths[lane] > lengths[longest])
                longest = lane;
        }
        Debug.Assert(lengths[longest] <= MaxTrackedChars);

        // Where each lane reads: its own piece, or the longest for a lane with no piece or none left.
        Span<int> readStart = stackalloc int[Lanes];
        Span<int> readLength = stackalloc int[Lanes];
        Span<int> keys = stackalloc int[3 * Lanes];
        for (var lane = 0; lane < Lanes; lane++)
        {
            var source = lane < lanes ? lane : longest;
            readStart[lane] = starts[source];
            readLength[lane] = lengths[source];
            OpenFromStart(text.Slice(readStart[lane], readLength[lane]), charset, openNumeric, openAlnum, openByte, allowAlnum: true, allowByte: true, table.Slice(lane * ParentBytesPerChar), out keys[lane], out keys[Lanes + lane], out keys[2 * Lanes + lane]);
        }

        var u0 = Vector256.Create(U | StateNumeric0);
        var u1 = Vector256.Create(U | StateNumeric1);
        var u2 = Vector256.Create(U | StateNumeric2);
        var u3 = Vector256.Create(U | StateAlnum0);
        var u4 = Vector256.Create(U | StateAlnum1);
        var n0 = u0;
        var n1 = Vector256.Create<int>(keys.Slice(0, Lanes));
        var n2 = u2;
        var a0 = u3;
        var a1 = Vector256.Create<int>(keys.Slice(Lanes, Lanes));
        var b = Vector256.Create<int>(keys.Slice(2 * Lanes, Lanes));

        var vOpenNumeric = Vector256.Create(openNumeric);
        var vOpenAlnum = Vector256.Create(openAlnum);
        var vOpenByte = Vector256.Create(openByte);
        var stateBits = Vector256.Create(7);
        var costBits = Vector256.Create(~7);

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

            int s0 = readStart[0], s1 = readStart[1], s2 = readStart[2], s3 = readStart[3], s4 = readStart[4], s5 = readStart[5], s6 = readStart[6], s7 = readStart[7];
            for (; i < end; i++)
            {
                char c0 = text[s0 + i], c1 = text[s1 + i], c2 = text[s2 + i], c3 = text[s3 + i], c4 = text[s4 + i], c5 = text[s5 + i], c6 = text[s6 + i], c7 = text[s7 + i];
                var cls = Vector256.Create(ClassOf(c0), ClassOf(c1), ClassOf(c2), ClassOf(c3), ClassOf(c4), ClassOf(c5), ClassOf(c6), ClassOf(c7));

                var byteBits = Vector256.Create(64);
                var noOpen = Vector256<int>.Zero;
                if (TBytes.Utf8)
                {
                    var chars = Vector256.Create((int)c0, c1, c2, c3, c4, c5, c6, c7);
                    // A lane at a byte order mark only continues its Byte run; past its first character it has one.
                    noOpen = Vector256.Equals(chars, Vector256.Create((int)ByteOrderMark)) & Vector256.Create(U);
                    if (Vector256.EqualsAny(chars & Vector256.Create(0xF800), Vector256.Create(0xD800)))
                    {
                        // A surrogate in some lane: its length depends on its neighbour, asked lane by lane.
                        byteBits = Vector256.Create(
                            LaneByteCost(text, s0, readLength[0], i), LaneByteCost(text, s1, readLength[1], i), LaneByteCost(text, s2, readLength[2], i), LaneByteCost(text, s3, readLength[3], i),
                            LaneByteCost(text, s4, readLength[4], i), LaneByteCost(text, s5, readLength[5], i), LaneByteCost(text, s6, readLength[6], i), LaneByteCost(text, s7, readLength[7], i)) << 6;
                    }
                    else
                    {
                        // One byte, one more above U+007F, one more above U+07FF; a compare is all ones, so it takes one off.
                        byteBits = (Vector256.Create(1) - Vector256.GreaterThan(chars, Vector256.Create(0x7F)) - Vector256.GreaterThan(chars, Vector256.Create(0x7FF))) << 6;
                    }
                }

                var isDigit = Vector256.Equals(cls, Vector256.Create(ClassDigit));
                var isAlnum = Vector256.GreaterThan(cls, Vector256<int>.Zero);

                // The steps of TrackedLatin and TrackedGeneral, every lane taking every branch and keeping what its class allows.
                var numeric = Vector256.Min(Vector256.Min(n0, n1), n2);
                var alnum = Vector256.Min(a0, a1);
                var numericKey = Vector256.Min(n0 + Vector256.Create(32), Vector256.Min(alnum, b) + vOpenNumeric);
                var alnumKey = Vector256.Min(Vector256.Min(numeric, b) + vOpenAlnum, a0 + Vector256.Create(48));
                var openKey = Vector256.Min(numeric, alnum) + vOpenByte;
                if (TBytes.Utf8)
                    openKey = Vector256.Max(openKey, noOpen);
                var byteKey = Vector256.Min(openKey, b) + byteBits;

                // Both bytes of every lane's entry in one store.
                var parents = ((byteKey & stateBits) | ((numericKey & stateBits) << 8) | ((alnumKey & stateBits) << 11)).AsUInt32();
                Vector256.Narrow(parents, parents).GetLower().AsByte().CopyTo(table.Slice(i * LaneTableBytesPerChar, LaneTableBytesPerChar));

                var nn1 = Vector256.ConditionalSelect(isDigit, (numericKey & costBits) | Vector256.Create(StateNumeric1), u1);
                var nn2 = Vector256.ConditionalSelect(isDigit, n1 + Vector256.Create(25), u2);
                n0 = Vector256.ConditionalSelect(isDigit, n2 + Vector256.Create(22), u0);
                n1 = nn1;
                n2 = nn2;
                var na1 = Vector256.ConditionalSelect(isAlnum, (alnumKey & costBits) | Vector256.Create(StateAlnum1), u4);
                a0 = Vector256.ConditionalSelect(isAlnum, a1 + Vector256.Create(39), u3);
                a1 = na1;
                b = (byteKey & costBits) | Vector256.Create(StateByte);
            }

            var best = Vector256.Min(Vector256.Min(Vector256.Min(n0, n1), Vector256.Min(n2, a0)), Vector256.Min(a1, b));
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
}
#endif
