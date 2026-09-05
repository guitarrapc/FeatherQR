using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FeatherQR.Internals.RmQr;

/// <summary>
/// Multi-segment data-codeword stream for <see cref="RmQRSegmentation.Optimal"/>.
/// Same bit grammar as the single-segment writer, repeated per planned run: mode
/// indicator, count indicator, payload; one optional ECI prefix ahead of the first
/// run (an rMQR decoder carries the declared charset across the runs that follow),
/// then the shared terminator / padding tail.
/// </summary>
/// <remarks>
/// The cold half of the encoder, reusing the hot path's segment writers so the bits
/// are identical run for run. It re-derives the planned bit cost up front because
/// those writers store without per-flush bounds checks: a plan that does not fit must
/// be rejected before the first store, not discovered as a buffer overrun.
/// </remarks>
internal static partial class RmQRBinaryEncoder
{
    /// <summary>
    /// Writes the data codewords for a planned mixed-mode split and returns the
    /// number of codewords written (always the version and ECC data codeword count).
    /// </summary>
    /// <param name="text">Content the plan indexes into.</param>
    /// <param name="version">Target version.</param>
    /// <param name="eccLevel">Target ECC level.</param>
    /// <param name="charset">Effective charset: Default / ISO-8859-1 narrow, UTF-8 transcode.</param>
    /// <param name="segments">Planned runs, in order, covering the whole content.</param>
    /// <param name="destination">At least the data codeword count in size.</param>
    public static int EncodeDataCodewordsSegmented(ReadOnlySpan<char> text, RmQRVersion version, RmQREccLevel eccLevel, EciMode charset, ReadOnlySpan<ModeSegment> segments, Span<byte> destination)
    {
        var codewordCount = RmQRConstants.GetDataCodewordCount(version, eccLevel);
        var capacityBits = codewordCount * 8;
        if (destination.Length < codewordCount)
            throw new ArgumentException($"Destination too small: {codewordCount} data codewords required, got {destination.Length} bytes.", nameof(destination));
        if (segments.Length == 0)
            throw new ArgumentException("A segmented rMQR stream needs at least one segment.", nameof(segments));
        if (charset is not (EciMode.Default or EciMode.Iso8859_1 or EciMode.Utf8))
            throw new ArgumentOutOfRangeException(nameof(charset), $"Unsupported charset {charset} for rMQR.");

        var eciBits = charset == EciMode.Default ? 0 : EciHeaderBits;
        var plannedBits = RmQRSegmentPlanner.MeasurePlan(version, segments) + eciBits;
        if (plannedBits > capacityBits)
            throw new ArgumentException($"Segment plan does not fit rMQR {version} at ECC level {eccLevel}: {plannedBits} bits required, {capacityBits} available.", nameof(segments));

        ref var dest = ref MemoryMarshal.GetReference(destination);
        ulong acc = 0;
        var accBits = 0;
        var bytePos = 0;

        if (charset != EciMode.Default)
            WriteEciHeader(ref dest, ref acc, ref accBits, ref bytePos, charset);

        // Hoisted out of the loop on purpose: a stackalloc inside it would accumulate
        // per iteration. Byte-mode capacity tops out at 150 bytes, so any run of a
        // plan that passed the bit check above transcodes into this budget.
        Span<byte> utf8 = stackalloc byte[StackByteBudget];
        var expectedStart = 0;

        foreach (var segment in segments)
        {
            if (segment.Start != expectedStart || segment.Length == 0 || segment.Start + segment.Length > text.Length)
                throw new ArgumentException("Segment plan must cover the content in order with non-empty runs.", nameof(segments));
            expectedStart = segment.Start + segment.Length;

            var chars = text.Slice(segment.Start, segment.Length);
            var mode = segment.Mode;
            var countBits = RmQRConstants.GetCountIndicatorLength(version, mode);

            // The count indicator width always covers the largest unit count a version
            // can hold, and a run holds no more than that, so this cannot bind; assert
            // it anyway because overflowing it would corrupt the mode indicator above.
            Debug.Assert(segment.UnitCount < (1 << countBits), "run length must fit the character count indicator");

            // The budget checked above comes from UnitCount, but the payload below is
            // written from the run's characters. A plan whose two disagree would clear
            // the budget and then overrun, so this is a runtime check, not an assert.
            // Byte mode under UTF-8 is the exception: its unit count is only knowable
            // after transcoding, so WriteUtf8Segment verifies it there instead.
            if ((mode != EncodingMode.Byte || charset != EciMode.Utf8) && segment.UnitCount != segment.Length)
                throw new ArgumentException($"Segment plan gives a {segment.Length}-character run a unit count of {segment.UnitCount}; they must agree outside UTF-8 Byte mode.", nameof(segments));

            switch (mode)
            {
                case EncodingMode.Numeric:
                    Append(ref dest, ref acc, ref accBits, ref bytePos, (0b001 << countBits) | segment.UnitCount, RmQRConstants.ModeIndicatorLength + countBits);
                    WriteNumeric(ref dest, ref acc, ref accBits, ref bytePos, chars, vectorized: true);
                    break;
                case EncodingMode.Alphanumeric:
                    Append(ref dest, ref acc, ref accBits, ref bytePos, (0b010 << countBits) | segment.UnitCount, RmQRConstants.ModeIndicatorLength + countBits);
                    WriteAlphanumeric(ref dest, ref acc, ref accBits, ref bytePos, chars, vectorized: true);
                    break;
                default:
                    Append(ref dest, ref acc, ref accBits, ref bytePos, (0b011 << countBits) | segment.UnitCount, RmQRConstants.ModeIndicatorLength + countBits);
                    if (charset == EciMode.Utf8)
                        WriteUtf8Segment(ref dest, ref acc, ref accBits, ref bytePos, chars, segment.UnitCount, utf8);
                    else
                        WriteLatin1(ref dest, ref acc, ref accBits, ref bytePos, chars, vectorized: true);
                    break;
            }
        }

        if (expectedStart != text.Length)
            throw new ArgumentException("Segment plan must cover the content in order with non-empty runs.", nameof(segments));

        Finish(ref dest, acc, accBits, bytePos, codewordCount, capacityBits);
        return codewordCount;
    }

    /// <summary>
    /// Transcodes one Byte-mode run and writes it. <paramref name="expectedBytes"/>
    /// is what the plan budgeted; a mismatch means the plan and the transcoder
    /// disagree, which would silently produce an unreadable symbol.
    /// </summary>
    private static void WriteUtf8Segment(ref byte dest, ref ulong acc, ref int accBits, ref int bytePos, ReadOnlySpan<char> chars, int expectedBytes, Span<byte> scratch)
    {
        if (expectedBytes <= scratch.Length)
        {
            var length = GetUtf8Bytes(chars, scratch);
            if (length != expectedBytes)
                throw new ArgumentException($"Segment plan budgeted {expectedBytes} UTF-8 bytes but the run encodes to {length}.");
            WriteBytes(ref dest, ref acc, ref accBits, ref bytePos, scratch.Slice(0, length));
            return;
        }

        // Unreachable for a plan that passed the capacity check; kept so a caller
        // that hand-builds a plan cannot overrun the stack budget.
        var rented = ArrayPool<byte>.Shared.Rent(chars.Length * 3);
        try
        {
            var length = GetUtf8Bytes(chars, rented);
            if (length != expectedBytes)
                throw new ArgumentException($"Segment plan budgeted {expectedBytes} UTF-8 bytes but the run encodes to {length}.");
            WriteBytes(ref dest, ref acc, ref accBits, ref bytePos, rented.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }
}
