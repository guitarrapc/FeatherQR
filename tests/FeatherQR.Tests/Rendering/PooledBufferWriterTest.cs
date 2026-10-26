using FeatherQR.SkiaSharp.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The buffer the async writes and the stream reads fill: it grows by doubling and stops at its limit.
/// </summary>
public class PooledBufferWriterTest
{
    [Test]
    public async Task GrowingToItsLimit_TakesRoomOfTheLimitAlone()
    {
        // A limit just past a power of two, which the pool would round up to twice the limit
        const int Limit = 2 * 1024 * 1024 + 1;
        using var buffer = new PooledBufferWriter(64 * 1024, Limit);
        while (buffer.WrittenCount < Limit - 1)
            buffer.Advance(Math.Min(buffer.GetSpan(1).Length, Limit - 1 - buffer.WrittenCount));

        // The room for the last byte is what is left of an array of the limit's size
        await Assert.That(buffer.GetSpan(1).Length).IsEqualTo(1);
    }

    [Test]
    public async Task WrittenBytes_SurviveEachGrowth()
    {
        using var buffer = new PooledBufferWriter(4, 1000);
        for (var i = 0; i < 1000; i++)
        {
            buffer.GetSpan(1)[0] = (byte)i;
            buffer.Advance(1);
        }

        var expected = Enumerable.Range(0, 1000).Select(static i => (byte)i).ToArray();
        await Assert.That(buffer.WrittenSpan.SequenceEqual(expected)).IsTrue();
    }
}
