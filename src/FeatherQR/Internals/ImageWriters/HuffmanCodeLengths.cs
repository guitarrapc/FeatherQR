using System.Buffers;

namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// Code lengths for a prefix code from symbol counts, no longer than a limit: a Huffman code when its depth stays within the limit,
/// and otherwise the same leaves moved within the limit until the code is complete again.
/// </summary>
internal static class HuffmanCodeLengths
{
    /// <summary>
    /// Writes a length for each symbol with a non-zero count and 0 for the rest. The code is always complete: fewer than two counted
    /// symbols still get two one-bit codes, which every inflater accepts.
    /// </summary>
    public static void Build(ReadOnlySpan<long> counts, int limit, Span<byte> lengths)
    {
        lengths.Clear();
        var symbolCount = counts.Length;
        var symbols = ArrayPool<int>.Shared.Rent(symbolCount);
        var weights = ArrayPool<long>.Shared.Rent(2 * symbolCount);
        var parents = ArrayPool<int>.Shared.Rent(2 * symbolCount);
        try
        {
            var used = 0;
            for (var s = 0; s < symbolCount; s++)
            {
                if (counts[s] != 0)
                    symbols[used++] = s;
            }
            if (used < 2)
            {
                var only = used == 1 ? symbols[0] : 0;
                lengths[only] = 1;
                lengths[only == 0 ? 1 : 0] = 1;
                return;
            }

            // Least frequent first, ties by symbol, so the result is the same on every runtime.
            SortByCount(symbols.AsSpan(0, used), counts);

            // Huffman by two queues: the sorted leaves, and the merged nodes in the order they are made, which is also sorted.
            // On a tie a leaf goes first, which keeps the tree as shallow as any optimal one.
            for (var k = 0; k < used; k++)
                weights[k] = counts[symbols[k]];
            int leaf = 0, inner = used;
            var nodes = 2 * used - 1;
            for (var k = used; k < nodes; k++)
            {
                var a = TakeLighter(weights, ref leaf, ref inner, used, k);
                var b = TakeLighter(weights, ref leaf, ref inner, used, k);
                weights[k] = weights[a] + weights[b];
                parents[a] = k;
                parents[b] = k;
            }

            // Depths from the root down. A parent is made after its children, so it is reached first and its slot already holds its depth.
            parents[nodes - 1] = 0;
            var deepest = 0;
            for (var k = nodes - 2; k >= 0; k--)
            {
                parents[k] = parents[parents[k]] + 1;
                if (k < used && parents[k] > deepest)
                    deepest = parents[k];
            }

            if (deepest <= limit)
            {
                for (var k = 0; k < used; k++)
                    lengths[symbols[k]] = (byte)parents[k];
                return;
            }

            LimitDepth(symbols.AsSpan(0, used), parents.AsSpan(0, used), limit, lengths);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(symbols);
            ArrayPool<long>.Shared.Return(weights);
            ArrayPool<int>.Shared.Return(parents);
        }
    }

    private static int TakeLighter(long[] weights, ref int leaf, ref int inner, int leaves, int made)
    {
        if (leaf < leaves && (inner >= made || weights[leaf] <= weights[inner]))
            return leaf++;
        return inner++;
    }

    /// <summary>
    /// Every leaf deeper than the limit goes up to it, which over-fills the code space. Leaves then move down one level at a time from the
    /// deepest level above the limit until the space is no longer over-filled, and up from the deepest level until it is exactly full.
    /// The lengths go to the leaves by count, so a more frequent symbol never gets a longer code.
    /// </summary>
    private static void LimitDepth(ReadOnlySpan<int> sortedSymbols, ReadOnlySpan<int> depths, int limit, Span<byte> lengths)
    {
        Span<int> perLength = stackalloc int[16];
        perLength.Clear();
        foreach (var depth in depths)
            perLength[Math.Min(depth, limit)]++;

        // The code space in units of the shortest step, one code of `limit` bits.
        var full = 1L << limit;
        long filled = 0;
        for (var length = 1; length <= limit; length++)
            filled += (long)perLength[length] << (limit - length);

        while (filled > full)
        {
            var length = limit - 1;
            while (perLength[length] == 0)
                length--;
            perLength[length]--;
            perLength[length + 1]++;
            filled -= 1L << (limit - length - 1);
        }
        while (filled < full)
        {
            var length = limit;
            while (perLength[length] == 0)
                length--;
            perLength[length]--;
            perLength[length - 1]++;
            filled += 1L << (limit - length);
        }

        // The least frequent symbols take the longest codes.
        var next = 0;
        for (var length = limit; length >= 1; length--)
        {
            for (var k = 0; k < perLength[length]; k++)
                lengths[sortedSymbols[next++]] = (byte)length;
        }
    }

    /// <summary>Heapsort by count, then symbol: no allocation, and one order on every runtime since no two keys are equal.</summary>
    private static void SortByCount(Span<int> symbols, ReadOnlySpan<long> counts)
    {
        for (var start = symbols.Length / 2 - 1; start >= 0; start--)
            SiftDown(symbols, counts, start, symbols.Length);
        for (var end = symbols.Length - 1; end > 0; end--)
        {
            (symbols[0], symbols[end]) = (symbols[end], symbols[0]);
            SiftDown(symbols, counts, 0, end);
        }
    }

    private static void SiftDown(Span<int> symbols, ReadOnlySpan<long> counts, int root, int end)
    {
        while (true)
        {
            var child = 2 * root + 1;
            if (child >= end)
                return;
            if (child + 1 < end && Before(symbols[child], symbols[child + 1], counts))
                child++;
            if (!Before(symbols[root], symbols[child], counts))
                return;
            (symbols[root], symbols[child]) = (symbols[child], symbols[root]);
            root = child;
        }
    }

    private static bool Before(int a, int b, ReadOnlySpan<long> counts) => counts[a] < counts[b] || (counts[a] == counts[b] && a < b);
}
