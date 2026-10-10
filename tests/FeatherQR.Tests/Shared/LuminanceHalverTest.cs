using TUnit.Assertions.Enums;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="LuminanceHalver"/> against its definition, each pixel the rounded mean of the two by two block above it, over the widths that end in every vector tail, odd dimensions, and in place.
/// </summary>
public class LuminanceHalverTest
{
    private static byte[] Reference(ReadOnlySpan<byte> source, int width, int height)
    {
        int halfWidth = width / 2, halfHeight = height / 2;
        var expected = new byte[halfWidth * halfHeight];
        for (var y = 0; y < halfHeight; y++)
        {
            for (var x = 0; x < halfWidth; x++)
            {
                var sum = source[2 * y * width + 2 * x] + source[2 * y * width + 2 * x + 1]
                    + source[(2 * y + 1) * width + 2 * x] + source[(2 * y + 1) * width + 2 * x + 1];
                expected[y * halfWidth + x] = (byte)((sum + 2) / 4);
            }
        }
        return expected;
    }

    /// <summary>Seeded bytes with the extremes in every block of eight, where a sum that wraps or a mean that saturates shows.</summary>
    private static byte[] Sample(int length, int seed)
    {
        var source = new byte[length];
        var state = (uint)(seed * 2654435761u + 1);
        for (var i = 0; i < length; i++)
        {
            state = state * 1664525u + 1013904223u;
            source[i] = (i & 7) switch
            {
                0 => 255,
                1 => 255,
                5 => 0,
                _ => (byte)(state >> 24),
            };
        }
        return source;
    }

    /// <summary>Widths to past four 256-bit steps (32 pixels in, 16 out, each), so every tail length of both vector widths is met, on two and three row pairs.</summary>
    [Test]
    public async Task Halve_MatchesTheDefinition_AtEveryWidth()
    {
        var mismatches = new List<string>();
        for (var width = 2; width <= 140; width++)
        {
            foreach (var height in new[] { 2, 3, 6, 7 })
            {
                var source = Sample(width * height, width * 31 + height);
                var actual = new byte[(width / 2) * (height / 2)];

                LuminanceHalver.Halve(source, width, height, actual);

                if (!actual.AsSpan().SequenceEqual(Reference(source, width, height)))
                    mismatches.Add($"{width}x{height}");
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    /// <summary>The mean of all white is white, and a half rounds up: no 8-bit average of averages gets both.</summary>
    [Test]
    public async Task Halve_RoundsTheMeanOfFour()
    {
        byte[] levels = [0, 1, 2, 127, 128, 253, 254, 255];
        var mismatches = new List<string>();
        // 64 pixels a row, so the blocks go through the vector loops and not only the tail
        const int Width = 64;
        var source = new byte[Width * 2];
        foreach (var a in levels)
        {
            foreach (var b in levels)
            {
                foreach (var c in levels)
                {
                    foreach (var d in levels)
                    {
                        for (var x = 0; x < Width; x += 2)
                        {
                            source[x] = a;
                            source[x + 1] = b;
                            source[Width + x] = c;
                            source[Width + x + 1] = d;
                        }
                        var actual = new byte[Width / 2];

                        LuminanceHalver.Halve(source, Width, 2, actual);

                        var expected = (byte)((a + b + c + d + 2) / 4);
                        if (actual.AsSpan().IndexOfAnyExcept(expected) >= 0)
                            mismatches.Add($"{a} {b} {c} {d}");
                    }
                }
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    /// <summary>An odd width or height drops its last column or row, and nothing is written past the halved image.</summary>
    [Test]
    [Arguments(65, 4)]
    [Arguments(64, 5)]
    [Arguments(33, 3)]
    [Arguments(3, 3)]
    public async Task Halve_OddDimension_DropsTheLastColumnOrRow(int width, int height)
    {
        var source = Sample(width * height, 7);
        var halved = (width / 2) * (height / 2);
        var destination = new byte[halved + 40];
        Array.Fill(destination, (byte)0xA5);

        LuminanceHalver.Halve(source, width, height, destination);

        await Assert.That(destination.AsSpan(0, halved).ToArray()).IsEquivalentTo(Reference(source, width, height), CollectionOrdering.Matching);
        await Assert.That(destination.AsSpan(halved).IndexOfAnyExcept((byte)0xA5)).IsEqualTo(-1);
    }

    /// <summary>
    /// The halved image may be written over the start of its source: the reduced-scale search halves each level into the buffer it is in.
    /// A vector step that stored before it had loaded, or a tail that read pixels again after they were written over, shows on the first rows, where source and destination overlap.
    /// </summary>
    [Test]
    public async Task Halve_InPlace_MatchesTheDefinition()
    {
        var mismatches = new List<string>();
        for (var width = 2; width <= 140; width++)
        {
            foreach (var height in new[] { 2, 3, 6, 9 })
            {
                var source = Sample(width * height, width * 17 + height);
                var expected = Reference(source, width, height);
                var buffer = source.ToArray();

                LuminanceHalver.Halve(buffer, width, height, buffer);

                if (!buffer.AsSpan(0, expected.Length).SequenceEqual(expected))
                    mismatches.Add($"{width}x{height}");
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    /// <summary>An image under two pixels on a side halves to nothing, and nothing is written.</summary>
    [Test]
    [Arguments(1, 8)]
    [Arguments(8, 1)]
    public async Task Halve_UnderTwoPixelsOnASide_WritesNothing(int width, int height)
    {
        var destination = new byte[4];
        Array.Fill(destination, (byte)0xA5);

        LuminanceHalver.Halve(new byte[width * height], width, height, destination);

        await Assert.That(destination.AsSpan().IndexOfAnyExcept((byte)0xA5)).IsEqualTo(-1);
    }

    [Test]
    public async Task Halve_SourceShorterThanItsDimensions_Throws()
    {
        await Assert.That(() => LuminanceHalver.Halve(new byte[63], 8, 8, new byte[16])).Throws<ArgumentException>();
    }

    [Test]
    public async Task Halve_DestinationShorterThanTheHalvedImage_Throws()
    {
        await Assert.That(() => LuminanceHalver.Halve(new byte[64], 8, 8, new byte[15])).Throws<ArgumentException>();
    }
}
