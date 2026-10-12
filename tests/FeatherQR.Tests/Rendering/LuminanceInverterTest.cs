using TUnit.Assertions.Enums;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="LuminanceInverter"/> against the per-byte reference, over the lengths
/// that separate its paths: below one vector, exactly one, and every scalar-tail length
/// in between. The 128-bit tier is selected by hardware, not by length, so on an AVX2
/// machine these lengths exercise the 256-bit loop and the tail only.
/// </summary>
public class LuminanceInverterTest
{
    private static byte[] Reference(ReadOnlySpan<byte> source)
    {
        var expected = new byte[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            expected[i] = (byte)(255 - source[i]);
        }
        return expected;
    }

    private static byte[] Sample(int length, int seed)
    {
        var source = new byte[length];
        var state = (uint)(seed * 2654435761u + 1);
        for (var i = 0; i < length; i++)
        {
            state = state * 1664525u + 1013904223u;
            // Pin the extremes explicitly: 255 - 0 and 255 - 255 are the two values a
            // wrong-width or saturating implementation is most likely to get right by
            // accident on random data alone.
            source[i] = i switch
            {
                0 => (byte)0,
                1 => (byte)255,
                _ => (byte)(state >> 24),
            };
        }
        return source;
    }

    [Test]
    public async Task Invert_MatchesTheReference_AtEveryLengthAcrossThePathBoundaries()
    {
        // 0 and 1 byte, every length up to two 256-bit vectors, and the sizes either
        // side of the 128-bit and 256-bit thresholds.
        for (var length = 0; length <= 96; length++)
        {
            var source = Sample(length, length);
            var expected = Reference(source);

            var actual = new byte[length];
            actual.AsSpan().Fill(0xA5); // poison, so a skipped byte is caught
            LuminanceInverter.Invert(source, actual);

            await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching)
                .Because($"length {length}");
        }
    }

    [Test]
    [Arguments(127)]
    [Arguments(128)]
    [Arguments(129)]
    [Arguments(1023)]
    [Arguments(1024)]
    [Arguments(4097)]
    [Arguments(1144 * 168)]
    public async Task Invert_MatchesTheReference_AtDecoderSizedLengths(int length)
    {
        var source = Sample(length, length);
        var expected = Reference(source);

        var actual = new byte[length];
        actual.AsSpan().Fill(0xA5);
        LuminanceInverter.Invert(source, actual);

        await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>
    /// The three image decoders invert a buffer onto a rented one of the same length.
    /// Exact aliasing is the degenerate case of that and must still be a plain negate.
    /// </summary>
    [Test]
    public async Task Invert_InPlace_NegatesEveryByteOnce()
    {
        var source = Sample(1000, 7);
        var expected = Reference(source);

        var buffer = (byte[])source.Clone();
        LuminanceInverter.Invert(buffer, buffer);

        await Assert.That(buffer).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>
    /// The vector paths address both spans through unchecked offsets bounded by the
    /// destination, so a short source is an out-of-bounds read rather than a silent
    /// short write. It has to be rejected, not tolerated.
    /// </summary>
    [Test]
    public async Task Invert_ShorterSourceThanDestination_Throws()
    {
        var source = new byte[64];
        var destination = new byte[65];

        await Assert.That(() =>
        {
            LuminanceInverter.Invert(source, destination);
            return Task.CompletedTask;
        }).Throws<ArgumentException>();
    }

    /// <summary>A longer source is allowed: only the destination's length is written.</summary>
    [Test]
    public async Task Invert_LongerSource_WritesOnlyTheDestinationLength()
    {
        var source = Sample(200, 3);
        var destination = new byte[100];
        LuminanceInverter.Invert(source, destination);

        var expected = Reference(source.AsSpan(0, 100));
        await Assert.That(destination).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }
}
