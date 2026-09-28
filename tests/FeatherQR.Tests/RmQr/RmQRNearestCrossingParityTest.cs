using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// The perimeter trace's crossing search, which samples outward from the middle and stops early, against a scan of every sample in order: the same crossing to the bit, or none from both.
/// </summary>
public class RmQRNearestCrossingParityTest
{
    private const int Side = 48;

    /// <summary>Random points, directions, windows and levels over images that cross the level often: blocks of dark and light, blurred, with noise.</summary>
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task TryNearestCrossing_RandomScenes_MatchesTheFullScan(int seed)
    {
        var random = new Random(seed);
        var luminance = Scene(random);
        var mismatches = 0;
        var found = 0;
        for (var i = 0; i < 50_000; i++)
        {
            var x = (float)random.NextDouble() * Side;
            var y = (float)random.NextDouble() * Side;
            var angle = random.NextDouble() * 2 * Math.PI;
            var length = 0.3f + (float)random.NextDouble() * 9f;
            var dx = (float)(Math.Cos(angle) * length);
            var dy = (float)(Math.Sin(angle) * length);
            var half = 0.2f + (float)random.NextDouble() * 1.4f;
            var level = 40f + (float)random.NextDouble() * 170f;
            var toDark = random.Next(2) == 0;

            var expected = FullScan(luminance, Side, Side, level, x, y, dx, dy, half, toDark, out var expectedX, out var expectedY);
            var actual = RmQRImageDecoder.TryNearestCrossing(luminance, Side, Side, level, x, y, dx, dy, half, toDark, out var actualX, out var actualY);
            if (expected != actual || expectedX != actualX || expectedY != actualY)
                mismatches++;
            if (expected)
                found++;
        }

        await Assert.That(mismatches).IsEqualTo(0);
        await Assert.That(found).IsGreaterThan(10_000).Because("the scenes have to cross the level often for the comparison to mean anything");
    }

    /// <summary>
    /// Crossings the same distance either side of the middle, and on a sample exactly: axis-aligned windows whose samples fall on pixel centres, or halfway between two, and a level that some pixels equal.
    /// The one toward the start of the window wins, as in the scan.
    /// </summary>
    [Test]
    public async Task TryNearestCrossing_ExactTies_MatchesTheFullScan()
    {
        var random = new Random(4);
        var luminance = new byte[Side * Side];
        for (var i = 0; i < luminance.Length; i++)
            luminance[i] = (byte)(random.Next(3) switch { 0 => 50, 1 => 128, _ => 200 });

        var mismatches = 0;
        var ties = 0;
        for (var i = 0; i < 50_000; i++)
        {
            var horizontal = random.Next(2) == 0;
            var sign = random.Next(2) == 0 ? 1f : -1f;
            var x = random.Next(8, Side - 8) + 0.5f;
            var y = random.Next(8, Side - 8) + 0.5f;
            // A 4 px module takes a sample a pixel: an even count from a pixel centre, an odd one from halfway between two
            var dx = horizontal ? 4f * sign : 0f;
            var dy = horizontal ? 0f : 4f * sign;
            var half = random.Next(2) == 0 ? random.Next(1, 7) / 4f : (2 * random.Next(1, 7) + 1) / 8f;
            var toDark = random.Next(2) == 0;

            var expected = FullScan(luminance, Side, Side, 128f, x, y, dx, dy, half, toDark, out var expectedX, out var expectedY, out var asNear);
            var actual = RmQRImageDecoder.TryNearestCrossing(luminance, Side, Side, 128f, x, y, dx, dy, half, toDark, out var actualX, out var actualY);
            if (expected != actual || expectedX != actualX || expectedY != actualY)
                mismatches++;
            if (asNear > 1)
                ties++;
        }

        await Assert.That(mismatches).IsEqualTo(0);
        await Assert.That(ties).IsGreaterThan(500).Because("ties are the case the early stop has to get right");
    }

    /// <summary>A window whose ends leave the image, and a direction under half a pixel: both refused.</summary>
    [Test]
    [Arguments(2f, 24f, 1f, 0f, 3f)]
    [Arguments(46f, 24f, 1f, 0f, 3f)]
    [Arguments(24f, 24f, 0.3f, 0.3f, 1f)]
    public async Task TryNearestCrossing_OffTheImageOrTooShort_Refused(float x, float y, float dx, float dy, float half)
    {
        var luminance = Scene(new Random(5));

        await Assert.That(RmQRImageDecoder.TryNearestCrossing(luminance, Side, Side, 128f, x, y, dx, dy, half, toDark: true, out _, out _)).IsFalse();
        await Assert.That(FullScan(luminance, Side, Side, 128f, x, y, dx, dy, half, toDark: true, out _, out _)).IsFalse();
    }

    private static byte[] Scene(Random random)
    {
        var blocks = new byte[Side * Side];
        for (var y = 0; y < Side; y += 3)
        {
            for (var x = 0; x < Side; x += 3)
            {
                var value = (byte)(random.Next(2) == 0 ? 30 : 220);
                for (var dy = 0; dy < 3 && y + dy < Side; dy++)
                    for (var dx = 0; dx < 3 && x + dx < Side; dx++)
                        blocks[(y + dy) * Side + x + dx] = value;
            }
        }
        var scene = new byte[Side * Side];
        for (var y = 0; y < Side; y++)
        {
            for (var x = 0; x < Side; x++)
            {
                var sum = 0;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                        sum += blocks[Math.Clamp(y + dy, 0, Side - 1) * Side + Math.Clamp(x + dx, 0, Side - 1)];
                scene[y * Side + x] = (byte)Math.Clamp(sum / 9 + random.Next(-20, 21), 0, 255);
            }
        }
        return scene;
    }

    private static bool FullScan(ReadOnlySpan<byte> luminance, int width, int height, float level, float x, float y, float dx, float dy, float half, bool toDark, out float crossingX, out float crossingY)
        => FullScan(luminance, width, height, level, x, y, dx, dy, half, toDark, out crossingX, out crossingY, out _);

    /// <summary>Every sample in order, the nearest crossing kept, the first of those as near; <paramref name="asNear"/> counts the crossings that near.</summary>
    private static bool FullScan(ReadOnlySpan<byte> luminance, int width, int height, float level, float x, float y, float dx, float dy, float half, bool toDark, out float crossingX, out float crossingY, out int asNear)
    {
        crossingX = 0f;
        crossingY = 0f;
        asNear = 0;
        var length = (float)Math.Sqrt(dx * dx + dy * dy);
        if (!(length >= 0.5f))
            return false;
        var spacing = Math.Min(1f, 0.34f * length);
        var steps = Math.Max(2, (int)Math.Ceiling(2f * half * length / spacing));
        var startX = x - half * dx;
        var startY = y - half * dy;
        var stepX = 2f * half * dx / steps;
        var stepY = 2f * half * dy / steps;
        if (startX < 0f || startY < 0f || startX >= width || startY >= height
            || startX + steps * stepX < 0f || startY + steps * stepY < 0f || startX + steps * stepX >= width || startY + steps * stepY >= height)
        {
            return false;
        }

        var nearest = float.MaxValue;
        var found = false;
        var previous = LuminanceSampler.Bilinear(luminance, width, height, startX, startY);
        for (var i = 1; i <= steps; i++)
        {
            var value = LuminanceSampler.Bilinear(luminance, width, height, startX + i * stepX, startY + i * stepY);
            if (toDark ? previous >= level && value < level : previous < level && value >= level)
            {
                var position = i - 1 + (previous - level) / (previous - value);
                var offset = Math.Abs(position - steps * 0.5f);
                if (offset == nearest)
                    asNear++;
                if (offset < nearest)
                {
                    nearest = offset;
                    asNear = 1;
                    crossingX = startX + position * stepX;
                    crossingY = startY + position * stepY;
                    found = true;
                }
            }
            previous = value;
        }
        return found;
    }
}
