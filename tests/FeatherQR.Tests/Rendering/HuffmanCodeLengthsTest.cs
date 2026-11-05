using FeatherQR.Internals.ImageWriters;

namespace FeatherQR.Tests;

/// <summary>
/// The code lengths the PNG writer's deflate builds from symbol counts: complete, within the format's limit, and as short as a Huffman code
/// when the limit does not bind. Deflate caps the literal/length and distance codes at 15 bits and the code-length code at 7.
/// </summary>
public class HuffmanCodeLengthsTest
{
    [Test]
    [Arguments(286, 15)]
    [Arguments(30, 15)]
    [Arguments(19, 7)]
    public async Task RandomCounts_GiveAnOptimalCompleteCode(int symbols, int limit)
    {
        var rng = new Random(symbols * 31 + limit);
        for (var trial = 0; trial < 300; trial++)
        {
            var counts = new long[symbols];
            var used = rng.Next(2, symbols + 1);
            for (var k = 0; k < used; k++)
                counts[rng.Next(symbols)] = rng.Next(1, 1000);
            var lengths = new byte[symbols];

            HuffmanCodeLengths.Build(counts, limit, lengths);

            await AssertComplete(counts, lengths, limit);
            var optimal = OptimalCost(counts);
            // The limit binds only for skewed counts; otherwise the code must be a Huffman code.
            if (optimal.MaxLength <= limit)
                await Assert.That(Cost(counts, lengths)).IsEqualTo(optimal.Cost);
        }
    }

    /// <summary>
    /// Fibonacci counts make a Huffman tree as deep as it can be, one symbol a level, so the limit has to bind.
    /// </summary>
    [Test]
    [Arguments(30, 15)]
    [Arguments(19, 7)]
    [Arguments(286, 15)]
    public async Task FibonacciCounts_StayWithinTheLimit(int symbols, int limit)
    {
        var counts = new long[symbols];
        long a = 1, b = 1;
        for (var k = 0; k < symbols; k++)
        {
            counts[k] = a;
            (a, b) = (b, Math.Min(a + b, 1L << 40));
        }
        var lengths = new byte[symbols];

        HuffmanCodeLengths.Build(counts, limit, lengths);

        await Assert.That(OptimalCost(counts).MaxLength).IsGreaterThan(limit).Because("the premise: an unlimited code would pass the limit");
        await AssertComplete(counts, lengths, limit);
        // A more frequent symbol never gets a longer code.
        for (var i = 0; i < symbols; i++)
        {
            for (var j = 0; j < symbols; j++)
            {
                if (counts[i] > counts[j] && lengths[i] > lengths[j])
                    throw new InvalidOperationException($"count {counts[i]} got {lengths[i]} bits, longer than count {counts[j]} at {lengths[j]}");
            }
        }
    }

    /// <summary>One used symbol, or none, still gives a complete code: two one-bit codes, which every inflater accepts.</summary>
    [Test]
    public async Task OneOrNoSymbol_GivesTwoOneBitCodes()
    {
        var one = new long[30];
        one[7] = 5;
        var lengths = new byte[30];
        HuffmanCodeLengths.Build(one, 15, lengths);
        await Assert.That(lengths[7]).IsEqualTo((byte)1);
        await Assert.That(lengths.Count(l => l == 1)).IsEqualTo(2);
        await Assert.That(lengths.Count(l => l != 0)).IsEqualTo(2);

        HuffmanCodeLengths.Build(new long[30], 15, lengths);
        await Assert.That(lengths.Count(l => l == 1)).IsEqualTo(2);
        await Assert.That(lengths.Count(l => l != 0)).IsEqualTo(2);
    }

    private static async Task AssertComplete(long[] counts, byte[] lengths, int limit)
    {
        long kraft = 0;
        for (var k = 0; k < counts.Length; k++)
        {
            if (counts[k] != 0 && lengths[k] == 0)
                throw new InvalidOperationException($"symbol {k} is counted and has no code");
            if (lengths[k] > limit)
                throw new InvalidOperationException($"symbol {k} has {lengths[k]} bits, past the limit of {limit}");
            if (lengths[k] != 0)
                kraft += 1L << (limit - lengths[k]);
        }
        await Assert.That(kraft).IsEqualTo(1L << limit).Because("a complete prefix code fills the code space exactly");
    }

    private static long Cost(long[] counts, byte[] lengths) => counts.Select((c, k) => c * lengths[k]).Sum();

    /// <summary>An unlimited Huffman code by repeatedly merging the two lightest nodes, for its cost and its depth.</summary>
    private static (long Cost, int MaxLength) OptimalCost(long[] counts)
    {
        var queue = new PriorityQueue<(long Weight, int Depth), long>();
        foreach (var c in counts.Where(c => c > 0))
            queue.Enqueue((c, 0), c);
        if (queue.Count < 2)
            return (counts.Sum(), 1);
        long cost = 0;
        while (queue.Count > 1)
        {
            var a = queue.Dequeue();
            var b = queue.Dequeue();
            var merged = (a.Weight + b.Weight, Math.Max(a.Depth, b.Depth) + 1);
            cost += merged.Item1;
            queue.Enqueue(merged, merged.Item1);
        }
        return (cost, queue.Dequeue().Depth);
    }
}
