#if NET8_0_OR_GREATER
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace FeatherQR.Internals;

/// <summary>
/// The program with parents over several pieces of one text at once, one vector lane a piece: the symbols of a Structured Append set, whose plans have nothing to do with each other.
/// </summary>
/// <remarks>
/// The program is a serial recurrence, so one piece cannot be vectorised; eight pieces can, each lane at its own character. The keyed form of <see cref="ModeSegmenter"/> is what makes that cheap: the minimum of keys is one instruction a lane and the predecessor falls out of it, with no compare-and-blend chain for the tie-break.
/// The table is the single piece's, <see cref="ParentBytesPerChar"/> bytes a character, interleaved by lane so that a step is one store; <see cref="ReconstructLane"/> walks one lane of it back by stride.
/// Lanes end at different characters: the loop runs to the nearest end, the result of every lane that ends there is taken, and the lane goes on reading the longest piece so that it keeps reading text that exists; what it writes past its own end is never read.
/// Accelerated <c>Vector256</c> handles eight pieces; ARM64 NEON handles groups of four 32-bit keys. Without either capability the caller plans piece by piece.
/// </remarks>
internal static partial class ModeSegmenter
{
    /// <summary>Pieces planned at once.</summary>
    internal const int Lanes = 8;

    /// <summary>Bytes of the interleaved table per character of the longest piece.</summary>
    internal const int LaneTableBytesPerChar = Lanes * ParentBytesPerChar;

    /// <summary>Whether the lanes are worth taking on this machine.</summary>
    internal static bool LanesAccelerated => Vector256.IsHardwareAccelerated || AdvSimd.Arm64.IsSupported;

    private interface ILaneBytes
    {
        static abstract bool Utf8 { get; }
    }

    private readonly struct OneByteLanes : ILaneBytes
    {
        public static bool Utf8 => false;
    }

    private readonly struct Utf8Lanes : ILaneBytes
    {
        public static bool Utf8 => true;
    }

    /// <summary>
    /// <see cref="ComputeCosts"/> with parents for the pieces of <paramref name="text"/> that begin at <paramref name="starts"/> and are <paramref name="lengths"/> long (at most <see cref="Lanes"/>, none empty), every mode allowed.
    /// <paramref name="table"/> is <see cref="LaneTableBytesPerChar"/> bytes a character of the longest piece; each lane's cost and final state come back in <paramref name="costs"/> and <paramref name="finalStates"/>.
    /// </summary>
    internal static void ComputeCostsLanes(ReadOnlySpan<char> text, ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths, EciMode charset, int modeIndicatorBits, int cciNumeric, int cciAlnum, int cciByte, Span<byte> table, Span<int> costs, Span<int> finalStates)
    {
        var openNumeric = (modeIndicatorBits + cciNumeric + 4) << 3;
        var openAlnum = (modeIndicatorBits + cciAlnum + 6) << 3;
        var openByte = (modeIndicatorBits + cciByte) << 3;
        // A lane is running until its state is taken.
        finalStates.Slice(0, starts.Length).Fill(-1);
        if (AdvSimd.Arm64.IsSupported)
        {
            ComputeCostsLanesAdvSimd(text, starts, lengths, charset, openNumeric, openAlnum, openByte, table, costs, finalStates);
            return;
        }
        if (charset == EciMode.Utf8)
            RunLanes<Utf8Lanes>(text, starts, lengths, charset, openNumeric, openAlnum, openByte, table, costs, finalStates);
        else
            RunLanes<OneByteLanes>(text, starts, lengths, charset, openNumeric, openAlnum, openByte, table, costs, finalStates);
    }

    /// <summary><see cref="Reconstruct"/> for one lane of the table <see cref="ComputeCostsLanes"/> filled.</summary>
    internal static bool ReconstructLane(int length, ReadOnlySpan<byte> table, int lane, int finalState, Span<ModeSegment> segments, out int segmentCount)
        => WalkBack(length, table.Slice(lane * ParentBytesPerChar), LaneTableBytesPerChar, finalState, segments, materialise: true, out segmentCount, out _, out _, out _);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LaneByteCost(ReadOnlySpan<char> text, int start, int length, int i) => ByteCost(text.Slice(start, length), i, EciMode.Utf8);
}
#endif
