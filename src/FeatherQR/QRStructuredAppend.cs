namespace FeatherQR;

/// <summary>
/// The Structured Append header a Standard QR symbol carries when it is one of a set of up to sixteen symbols that together hold one message (ISO/IEC 18004 Structured Append). Micro QR and rMQR do not define it.
/// </summary>
/// <remarks>
/// <para>
/// Each symbol of a set decodes on its own and yields its own part of the text; the header says where that part goes. To reassemble a set: every symbol must report the same <see cref="Count"/> and the same <see cref="Parity"/>; the indices <c>0</c> to <c>Count - 1</c> must each be present exactly once; the parts are concatenated in <see cref="Index"/> order.
/// </para>
/// <para>
/// <see cref="Parity"/> identifies the set, not the content. It is the XOR of the bytes of the whole message as the encoder wrote them, in whatever charset the set carries, so a reader can tell that two symbols belong to different sets; it is not a checksum a caller can recompute from the reassembled text.
/// </para>
/// <para>
/// Reported only when the decode succeeded. A symbol with no header, or a failed decode, leaves the value at its default, which <see cref="IsEmpty"/> reports.
/// </para>
/// </remarks>
public readonly record struct QRStructuredAppend
{
    internal QRStructuredAppend(int index, int count, byte parity)
    {
        Index = index;
        Count = count;
        Parity = parity;
    }

    /// <summary>This symbol's position in the set, 0-based, as on the wire. Less than <see cref="Count"/> whenever the value is not empty; both are 0 on the empty value.</summary>
    public int Index { get; }

    /// <summary>How many symbols the set has, 1 to 16.</summary>
    public int Count { get; }

    /// <summary>The set's parity byte; the same on every symbol of one set.</summary>
    public byte Parity { get; }

    /// <summary><c>true</c> when the symbol carried no Structured Append header, or the decode did not succeed.</summary>
    public bool IsEmpty => Count == 0;
}
