using System.Buffers;

namespace FeatherQR.Tests;

/// <summary>
/// <c>GetRawData(IBufferWriter)</c> on the three data types writes the bytes of the array form, and refuses a null writer or one
/// that breaks the contract before it writes a byte.
/// </summary>
public class RawDataWriterTest
{
    /// <summary>A symbol's <c>GetRawData(IBufferWriter)</c> and the bytes of its array form.</summary>
    public sealed record Symbol(string Name, Func<IBufferWriter<byte>, int> Write, byte[] Bytes)
    {
        public int Size => Bytes.Length;
        public override string ToString() => Name;
    }

    public static IEnumerable<Func<Symbol>> Symbols()
    {
        yield return () =>
        {
            var data = QRCodeGenerator.Create("raw", QREccLevel.M);
            return new Symbol("Standard QR", data.GetRawData, data.GetRawData());
        };
        yield return () =>
        {
            var data = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L);
            return new Symbol("Micro QR", data.GetRawData, data.GetRawData());
        };
        yield return () =>
        {
            var data = RmQRCodeGenerator.Create("raw", RmQREccLevel.M);
            return new Symbol("rMQR", data.GetRawData, data.GetRawData());
        };
    }

    /// <summary>The writer's first span is 16 bytes longer than asked, so the count must come from the data and not from the span.</summary>
    [Test]
    [MethodDataSource(nameof(Symbols))]
    public async Task AWriter_GetsTheBytesOfTheArrayForm(Symbol symbol)
    {
        var writer = new ArrayBufferWriter<byte>(symbol.Size + 16);

        var written = symbol.Write(writer);

        await Assert.That(written).IsEqualTo(symbol.Size);
        await Assert.That(writer.WrittenSpan.ToArray()).IsEquivalentTo(symbol.Bytes, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>
    /// One byte short leaves room for the header, so a write that checks nothing first puts the header into the span before it
    /// fails. An empty span leaves room for nothing.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Symbols))]
    public async Task AWriterWithTooShortASpan_IsRefusedBeforeAByteIsWritten(Symbol symbol)
    {
        foreach (var shortBy in new[] { 1, symbol.Size })
        {
            var writer = new ShortSpanWriter(shortBy);

            await Assert.That(() => symbol.Write(writer)).Throws<InvalidOperationException>();
            await Assert.That(writer.Handed.Length).IsEqualTo(symbol.Size - shortBy);
            await Assert.That(writer.Handed.All(b => b == ShortSpanWriter.Unwritten)).IsTrue();
            await Assert.That(writer.Advanced).IsEqualTo(0);
        }
    }

    [Test]
    [MethodDataSource(nameof(Symbols))]
    public async Task ANullWriter_Throws(Symbol symbol)
    {
        await Assert.That(() => symbol.Write(null!)).Throws<ArgumentNullException>();
    }
}
