using System.Diagnostics;

namespace FeatherQR.Internals.MicroQR;

/// <summary>
/// Multi-segment data-codeword stream for <see cref="MicroQRSegmentation.Optimal"/>.
/// Same bit grammar as the single-segment writer, repeated per planned run: mode indicator (version − 1 bits; M1 never reaches here, plans need mode switches), count indicator, payload, then the shared terminator / padding tail.
/// </summary>
/// <remarks>
/// The cold half of the encoder, reusing the hot path's 128-bit accumulator and <c>FinishAndStore</c> so the tail (terminator, alignment, 0xEC/0x11 padding, the M1/M3 half codeword) is bit-identical to the single-segment writer.
/// It re-derives the planned bit cost up front because the accumulator asserts rather than bounds- checks: a plan that does not fit must be rejected before the first append.
/// </remarks>
internal static partial class MicroQRBinaryEncoder
{
    /// <summary>
    /// Writes the data codewords for a planned mixed-mode split and returns the number of codewords written (always the version and ECC data codeword count).
    /// </summary>
    /// <param name="text">Content the plan indexes into.</param>
    /// <param name="version">Target version (M2-M4; decides indicator widths and available modes).</param>
    /// <param name="eccLevel">Target ECC level (valid for the version).</param>
    /// <param name="charset">Effective charset: Default / ISO-8859-1 narrow, UTF-8 transcode.</param>
    /// <param name="segments">Planned runs, in order, covering the whole content.</param>
    /// <param name="destination">At least the data codeword count in size.</param>
    public static int EncodeDataCodewordsSegmented(ReadOnlySpan<char> text, MicroQRVersion version, MicroQREccLevel eccLevel, EciMode charset, ReadOnlySpan<ModeSegment> segments, Span<byte> destination)
    {
        var capacityBits = MicroQRConstants.GetDataBitCapacity(version, eccLevel);
        var codewordCount = MicroQRConstants.GetDataCodewordCount(version, eccLevel);
        if (destination.Length < codewordCount)
            throw new ArgumentException($"Destination too small: {codewordCount} data codewords required, got {destination.Length} bytes.", nameof(destination));
        if (segments.Length == 0)
            throw new ArgumentException("A segmented Micro QR stream needs at least one segment.", nameof(segments));

        var plannedBits = MicroQRSegmentPlanner.MeasurePlan(version, segments);
        if (plannedBits > capacityBits)
            throw new ArgumentException($"Segment plan does not fit Micro QR {version} at ECC level {eccLevel}: {plannedBits} bits required, {capacityBits} available.", nameof(segments));

        ulong hi = 0, lo = 0;
        var pos = 0;
        var modeBits = MicroQRConstants.GetModeIndicatorLength(version);
        var expectedStart = 0;

        // Hoisted out of the loop on purpose: a stackalloc inside it would
        // accumulate per iteration. Worst-case UTF-8 expansion of a 15-char run
        // stays within 45 <= 64 bytes, matching the single-segment writer's budget.
        Span<byte> utf8 = stackalloc byte[64];

        foreach (var segment in segments)
        {
            if (segment.Start != expectedStart || segment.Length == 0 || segment.Start + segment.Length > text.Length)
                throw new ArgumentException("Segment plan must cover the content in order with non-empty runs.", nameof(segments));
            expectedStart = segment.Start + segment.Length;

            var mode = segment.Mode;
            if (!MicroQRConstants.IsModeSupported(version, mode))
                throw new ArgumentException($"Micro QR version {version} does not offer {mode} mode; the plan cannot be emitted.", nameof(segments));

            var chars = text.Slice(segment.Start, segment.Length);
            var countBits = MicroQRConstants.GetCountIndicatorLength(version, mode);
            Debug.Assert(segment.UnitCount < (1 << countBits), "run length must fit the character count indicator");

            // The bit budget checked above comes from UnitCount, but the payload is
            // written from the run's characters; a plan whose two disagree would
            // clear the budget and then overrun the accumulator. Byte mode under
            // UTF-8 is the exception: its unit count is only knowable after
            // transcoding, verified below.
            if ((mode != EncodingMode.Byte || charset != EciMode.Utf8) && segment.UnitCount != segment.Length)
                throw new ArgumentException($"Segment plan gives a {segment.Length}-character run a unit count of {segment.UnitCount}; they must agree outside UTF-8 Byte mode.", nameof(segments));

            var header = (MicroQRConstants.GetModeIndicatorValue(mode) << countBits) | segment.UnitCount;
            Append(ref hi, ref lo, ref pos, header, modeBits + countBits);

            switch (mode)
            {
                case EncodingMode.Numeric:
                    WriteNumericData(ref hi, ref lo, ref pos, chars);
                    break;
                case EncodingMode.Alphanumeric:
                    WriteAlphanumericData(ref hi, ref lo, ref pos, chars);
                    break;
                default:
                    if (charset == EciMode.Utf8)
                    {
                        var n = EncodeUtf8(chars, utf8);
                        if (n != segment.UnitCount)
                            throw new ArgumentException($"Segment plan budgeted {segment.UnitCount} UTF-8 bytes but the run encodes to {n}.");
                        for (var i = 0; i < n; i++)
                            Append(ref hi, ref lo, ref pos, utf8[i], 8);
                    }
                    else
                    {
                        // ISO-8859-1 narrows to one byte per char (validated upstream).
                        for (var i = 0; i < chars.Length; i++)
                            Append(ref hi, ref lo, ref pos, (byte)chars[i], 8);
                    }
                    break;
            }
        }

        if (expectedStart != text.Length)
            throw new ArgumentException("Segment plan must cover the content in order with non-empty runs.", nameof(segments));

        Debug.Assert(pos == plannedBits, "the emitted stream must cost exactly what the plan measured");
        FinishAndStore(hi, lo, pos, version, capacityBits, codewordCount, destination);
        return codewordCount;
    }
}
