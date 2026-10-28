using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// The format reads that stop early against the full read they replace: <see cref="RmQRImageDecoder.TryReadExactFormatCopy"/> where only a valid word is taken,
/// and <see cref="RmQRImageDecoder.IsFormatCopyWithinABit"/> where the copy has to name the version within a bit.
/// </summary>
/// <remarks>
/// Each copy is drawn module by module, so the full read returns the drawn bits and both reads see the same pixels.
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

            // The version asked about is the drawn one, or another half the time
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
