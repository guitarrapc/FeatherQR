using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// The format reads that stop early against the full read they replace: <see cref="RmQRImageDecoder.TryReadExactFormatCopy"/> where only a valid word is taken,
/// and <see cref="RmQRImageDecoder.IsFormatCopyWithinABit"/> where the copy has to name the version within a bit. The full read through an affine frame is checked against the frame's general transform.
/// </summary>
/// <remarks>
/// Each copy the early reads are checked on is drawn module by module, so the full read returns the drawn bits and both reads see the same pixels.
/// The copies are valid words with up to three bits flipped, where the early exits decide, and random bits, which are mostly far from every word.
/// </remarks>
public class RmQRFormatCopyEarlyExitTest
{
    private const int Module = 4;
    // Margins that differ across and down, so the frame's two offsets differ and a sample that takes one for the other misreads
    private const int MarginX = 2;
    private const int MarginY = 3;

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LowBits_NameOneWordEach(bool subFinderSide)
    {
        var lowBits = new HashSet<int>();
        for (var index = 0; index < 64; index++)
        {
            var word = RmQRConstants.GetFormatBits((RmQRVersion)((index & 31) + 1), (RmQREccLevel)(index >> 5), subFinderSide);
            lowBits.Add(word & 63);
            await Assert.That(RmQRFormatInformationDecoder.WordOfLowBits(word, subFinderSide)).IsEqualTo(word);
            await Assert.That(RmQRFormatInformationDecoder.Word((RmQRVersion)((index & 31) + 1), (RmQREccLevel)(index >> 5), subFinderSide)).IsEqualTo(word);
        }
        await Assert.That(lowBits.Count).IsEqualTo(64);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExactRead_IsTheFullReadOfAValidWord(bool subFinderSide)
    {
        var random = new Random(subFinderSide ? 11 : 10);
        var taken = 0;
        var refused = 0;
        foreach (var (raw, version) in Copies(random, subFinderSide))
        {
            var (luminance, width, height, transform, symbolWidth, symbolHeight) = Draw(raw, version, subFinderSide, random);
            var full = RmQRImageDecoder.ReadFormatCopy(luminance, width, height, 128, transform, subFinderSide, symbolWidth, symbolHeight);
            // The premise: the drawing reads back as drawn
            await Assert.That(full).IsEqualTo(raw);

            var exact = RmQRFormatInformationDecoder.TryDecodeCopy(full, subFinderSide, out _, out _, out var distance) && distance == 0;
            var read = RmQRImageDecoder.TryReadExactFormatCopy(luminance, width, height, 128, transform, subFinderSide, symbolWidth, symbolHeight, out var got);
            await Assert.That(read).IsEqualTo(exact).Because($"copy {raw:X5}");
            if (read)
            {
                await Assert.That(got).IsEqualTo(full);
                taken++;
            }
            else
            {
                refused++;
            }
        }

        // Both outcomes have to occur, a refusal past the sixth bit among them, or one side of the rule went untested
        await Assert.That(taken).IsGreaterThan(50);
        await Assert.That(refused).IsGreaterThan(500);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WithinABit_IsTheFullReadNamingTheVersionWithinABit(bool subFinderSide)
    {
        var random = new Random(subFinderSide ? 21 : 20);
        var within = 0;
        var beyond = 0;
        foreach (var (raw, version) in Copies(random, subFinderSide))
        {
            var (luminance, width, height, transform, symbolWidth, symbolHeight) = Draw(raw, version, subFinderSide, random);
            var full = RmQRImageDecoder.ReadFormatCopy(luminance, width, height, 128, transform, subFinderSide, symbolWidth, symbolHeight);
            await Assert.That(full).IsEqualTo(raw);

            var asked = random.Next(2) == 0 ? version : (RmQRVersion)random.Next(1, 33);
            var want = RmQRFormatInformationDecoder.TryDecodeCopy(full, subFinderSide, out var named, out _, out var distance) && named == asked && distance <= 1;
            var got = RmQRImageDecoder.IsFormatCopyWithinABit(luminance, width, height, 128, transform, subFinderSide, symbolWidth, symbolHeight, asked);
            await Assert.That(got).IsEqualTo(want).Because($"copy {raw:X5}, version {asked}");
            if (want)
                within++;
            else
                beyond++;
        }

        await Assert.That(within).IsGreaterThan(50);
        await Assert.That(beyond).IsGreaterThan(500);
    }

    /// <summary>
    /// A copy read through a turned affine frame with fractional coefficients samples the pixels the frame's general transform names, bit for bit.
    /// </summary>
    /// <remarks>
    /// Each frame is shifted so that one of its samples lands within a few roundings of a pixel edge, where a sum rounded any other way than the transform rounds it falls in the next pixel.
    /// The image is noise, so a sample one pixel off reads the other way about half the time.
    /// </remarks>
    [Test]
    public async Task AffineFrame_ReadsThePixelsItsTransformNames()
    {
        const int Size = 96;
        var random = new Random(31);
        var luminance = new byte[Size * Size];
        for (var i = 0; i < luminance.Length; i++)
            luminance[i] = random.Next(2) == 0 ? (byte)20 : (byte)230;
        var version = RmQRVersion.R11x43;
        var symbolWidth = RmQRConstants.GetWidth(version);
        var symbolHeight = RmQRConstants.GetHeight(version);

        var onEdge = 0;
        var mismatches = 0;
        var first = "";
        for (var n = 0; n < 4000; n++)
        {
            var subFinderSide = (n & 1) != 0;
            var angle = random.NextDouble() * 2 * Math.PI;
            var scale = 1.1 + random.NextDouble() * 0.8;
            float uX = (float)(scale * Math.Cos(angle)), uY = (float)(scale * Math.Sin(angle));
            var centerX = 30f + (float)random.NextDouble() * 36f;
            var centerY = 30f + (float)random.NextDouble() * 36f;
            RmQRConstants.GetFormatModule(random.Next(18), subFinderSide, symbolHeight, symbolWidth, out var edgeRow, out var edgeCol);
            // Half the frames put the sample on a column edge and half on a row edge, so each coordinate's sum is tested
            var acrossEdge = (n & 2) == 0;
            var transform = PerspectiveTransform.FromLocalFrame(3.5f, 3.5f, centerX, centerY, uX, uY, -uY, uX, 0f, 0f);
            transform.Transform(edgeCol + 0.5f, edgeRow + 0.5f, out var edgeX, out var edgeY);
            if (acrossEdge)
                centerX += MathF.Round(edgeX) - edgeX;
            else
                centerY += MathF.Round(edgeY) - edgeY;
            transform = PerspectiveTransform.FromLocalFrame(3.5f, 3.5f, centerX, centerY, uX, uY, -uY, uX, 0f, 0f);
            transform.Transform(edgeCol + 0.5f, edgeRow + 0.5f, out edgeX, out edgeY);
            var edge = acrossEdge ? edgeX : edgeY;
            var inside = edgeX >= 1f && edgeX < Size - 1 && edgeY >= 1f && edgeY < Size - 1;
            if (transform.a13 == 0f && transform.a23 == 0f && transform.a33 == 1f && inside && MathF.Abs(edge - MathF.Round(edge)) < 1e-3f)
                onEdge++;

            var expected = 0;
            for (var bit = 0; bit < 18; bit++)
            {
                RmQRConstants.GetFormatModule(bit, subFinderSide, symbolHeight, symbolWidth, out var row, out var col);
                transform.Transform(col + 0.5f, row + 0.5f, out var x, out var y);
                var px = Math.Clamp((int)x, 0, Size - 1);
                var py = Math.Clamp((int)y, 0, Size - 1);
                if (luminance[py * Size + px] < 128)
                    expected |= 1 << bit;
            }
            var read = RmQRImageDecoder.ReadFormatCopy(luminance, Size, Size, 128, transform, subFinderSide, symbolWidth, symbolHeight);
            if (read != expected && mismatches++ == 0)
                first = $"frame {n}: read {read:X5} where the transform names {expected:X5}";
        }

        await Assert.That(mismatches).IsEqualTo(0).Because(first);
        // The frames have to be affine and put a sample on a pixel edge inside the image, where it is not clamped, or the shortcut and its rounding go untested
        await Assert.That(onEdge).IsGreaterThan(2500);
    }

    /// <summary>Every valid word with 0 to 3 of its bits flipped, a few times each, and random copies.</summary>
    private static IEnumerable<(int Raw, RmQRVersion Version)> Copies(Random random, bool subFinderSide)
    {
        for (var index = 0; index < 64; index++)
        {
            var version = (RmQRVersion)((index & 31) + 1);
            var word = RmQRConstants.GetFormatBits(version, (RmQREccLevel)(index >> 5), subFinderSide);
            for (var flips = 0; flips <= 3; flips++)
            {
                for (var n = 0; n < 4; n++)
                {
                    var raw = word;
                    for (var f = 0; f < flips; f++)
                        raw ^= 1 << random.Next(18);
                    yield return (raw, version);
                }
            }
        }
        for (var n = 0; n < 400; n++)
            yield return (random.Next(1 << 18), (RmQRVersion)random.Next(1, 33));
    }

    /// <summary>The copy drawn at <see cref="Module"/> pixels a module through an axis-aligned frame, the other modules light or dark at random.</summary>
    private static (byte[] Luminance, int Width, int Height, PerspectiveTransform Transform, int SymbolWidth, int SymbolHeight) Draw(int raw, RmQRVersion version, bool subFinderSide, Random random)
    {
        var symbolWidth = RmQRConstants.GetWidth(version);
        var symbolHeight = RmQRConstants.GetHeight(version);
        var width = (symbolWidth + 2 * MarginX) * Module;
        var height = (symbolHeight + 2 * MarginY) * Module;
        var luminance = new byte[width * height];
        var modules = new bool[symbolHeight, symbolWidth];
        for (var row = 0; row < symbolHeight; row++)
        {
            for (var col = 0; col < symbolWidth; col++)
                modules[row, col] = random.Next(2) == 0;
        }
        for (var bit = 0; bit < 18; bit++)
        {
            RmQRConstants.GetFormatModule(bit, subFinderSide, symbolHeight, symbolWidth, out var row, out var col);
            modules[row, col] = (raw >> bit & 1) != 0;
        }
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var row = y / Module - MarginY;
                var col = x / Module - MarginX;
                var dark = row >= 0 && col >= 0 && row < symbolHeight && col < symbolWidth && modules[row, col];
                luminance[y * width + x] = dark ? (byte)20 : (byte)230;
            }
        }
        // Grid (3.5, 3.5), the finder's centre, at its pixel centre
        var transform = PerspectiveTransform.FromLocalFrame(3.5f, 3.5f, (MarginX + 3.5f) * Module, (MarginY + 3.5f) * Module, Module, 0f, 0f, Module, 0f, 0f);
        return (luminance, width, height, transform, symbolWidth, symbolHeight);
    }
}
