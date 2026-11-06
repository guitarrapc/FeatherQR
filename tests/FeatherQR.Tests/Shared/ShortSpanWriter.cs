using System.Buffers;

namespace FeatherQR.Tests;

/// <summary>
/// A writer that hands out a span shorter than asked, against the contract. It keeps the last span it handed out, filled with
/// <see cref="Unwritten"/>, so a test can see whether anything was written into it.
/// </summary>
internal sealed class ShortSpanWriter(int shortBy = 1) : IBufferWriter<byte>
{
    public const byte Unwritten = 0xEE;

    public byte[] Handed { get; private set; } = [];
    public int Advanced { get; private set; }

    public void Advance(int count) => Advanced += count;
    public Memory<byte> GetMemory(int sizeHint = 0) => Hand(sizeHint);
    public Span<byte> GetSpan(int sizeHint = 0) => Hand(sizeHint);

    private byte[] Hand(int sizeHint)
    {
        Handed = new byte[Math.Max(0, sizeHint - shortBy)];
        Handed.AsSpan().Fill(Unwritten);
        return Handed;
    }
}
