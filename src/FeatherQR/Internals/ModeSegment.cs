using System.Diagnostics;

namespace FeatherQR.Internals;

/// <summary>
/// One encoding-mode run of a planned mixed-mode bit stream: a contiguous slice of the source text plus the value its character count indicator carries.
/// Shared by the Standard QR, Micro QR and rMQR planners, whose plans differ in header widths and (Micro QR) mode availability, not in run structure.
/// Packed small and reference-free on purpose — plans live in a caller-lent <see cref="Span{T}"/> (stack for short content, pooled for long), so a run must never own storage.
/// </summary>
internal readonly struct ModeSegment
{
    /// <summary>Character offset of the run in the source text.</summary>
    public readonly ushort Start;

    /// <summary>Character length of the run.</summary>
    public readonly ushort Length;

    /// <summary>
    /// Character count indicator value: digits for Numeric, characters for Alphanumeric, encoded byte count for Byte.
    /// Kept alongside <see cref="Length"/> so the planner and the encoder agree on the bit budget.
    /// </summary>
    public readonly ushort UnitCount;

    /// <summary>Dense mode index: 0 Numeric, 1 Alphanumeric, 2 Byte.</summary>
    public readonly byte ModeIndex;

    public ModeSegment(int modeIndex, int start, int length, int unitCount)
    {
        // The fields are packed to keep a plan on the stack, so the ranges the packing
        // assumes are asserted rather than left to a silent truncation. The longest
        // plannable content of either symbology stays far below ushort.MaxValue.
        Debug.Assert((uint)modeIndex <= 2, "mode index must be Numeric, Alphanumeric or Byte");
        Debug.Assert((uint)start <= ushort.MaxValue && (uint)length <= ushort.MaxValue && (uint)unitCount <= ushort.MaxValue);
        ModeIndex = (byte)modeIndex;
        Start = (ushort)start;
        Length = (ushort)length;
        UnitCount = (ushort)unitCount;
    }

    public EncodingMode Mode => ModeIndex switch
    {
        0 => EncodingMode.Numeric,
        1 => EncodingMode.Alphanumeric,
        _ => EncodingMode.Byte,
    };
}
