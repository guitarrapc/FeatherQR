using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// The rMQR format information's module positions are stated once, in <see cref="RmQRConstants.GetFormatBlock"/> and
/// <see cref="RmQRConstants.GetFormatTail"/>, which <see cref="RmQRConstants.GetFormatModule"/> composes for one bit and
/// <see cref="RmQRConstants.IsFormatModule"/> for a module. These tests hold them to the naive reference, written from the standard apart from the
/// library, and hold both readers, the matrix decoder's and the image decoder's, which walk the block themselves, to them bit by bit.
/// </summary>
public class RmQRFormatPositionTest
{
    public static IEnumerable<RmQRVersion> AllVersions() => Enum.GetValues<RmQRVersion>();

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task GetFormatModule_IsWhereTheNaiveReaderReadsEachBit(RmQRVersion version)
    {
        var height = RmQRConstants.GetHeight(version);
        var width = RmQRConstants.GetWidth(version);
        foreach (var subFinderSide in new[] { false, true })
        {
            for (var bit = 0; bit < 18; bit++)
            {
                RmQRConstants.GetFormatModule(bit, subFinderSide, height, width, out var row, out var col);
                var modules = new byte[width * height];
                modules[row * width + col] = 1;

                var (finderSideRaw, subFinderSideRaw) = RmQRNaiveReference.ReadFormatRegions(modules, height, width);

                await Assert.That(subFinderSide ? subFinderSideRaw : finderSideRaw).IsEqualTo(1 << bit).Because($"{version}, bit {bit}, sub-finder side {subFinderSide}");
                await Assert.That(subFinderSide ? finderSideRaw : subFinderSideRaw).IsEqualTo(0);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task IsFormatModule_IsExactlyTheModulesOfBothCopies(RmQRVersion version)
    {
        var height = RmQRConstants.GetHeight(version);
        var width = RmQRConstants.GetWidth(version);
        var copies = new HashSet<(int Row, int Col)>();
        foreach (var subFinderSide in new[] { false, true })
        {
            for (var bit = 0; bit < 18; bit++)
            {
                RmQRConstants.GetFormatModule(bit, subFinderSide, height, width, out var row, out var col);
                copies.Add((row, col));
            }
        }

        var marked = new HashSet<(int Row, int Col)>();
        for (var row = 0; row < height; row++)
            for (var col = 0; col < width; col++)
                if (RmQRConstants.IsFormatModule(row, col, height, width))
                    marked.Add((row, col));

        await Assert.That(copies.Count).IsEqualTo(36);
        await Assert.That(marked.SetEquals(copies)).IsTrue();
        foreach (var (row, col) in copies)
            await Assert.That(RmQRModulePlacer.IsFunctionModule(version, row, col)).IsTrue().Because($"{version} ({row},{col})");
    }

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task ReadFormatCopy_ReadsEachBitAtItsModule(RmQRVersion version)
    {
        // Two pixels a module, so each module centre falls inside its module's pixels.
        const int scale = 2;
        var height = RmQRConstants.GetHeight(version);
        var width = RmQRConstants.GetWidth(version);
        var transform = PerspectiveTransform.QuadrilateralToQuadrilateral(
            0, 0, width, 0, width, height, 0, height,
            0, 0, width * scale, 0, width * scale, height * scale, 0, height * scale);
        foreach (var subFinderSide in new[] { false, true })
        {
            for (var bit = 0; bit < 18; bit++)
            {
                RmQRConstants.GetFormatModule(bit, subFinderSide, height, width, out var row, out var col);
                var luminance = new byte[width * scale * height * scale];
                luminance.AsSpan().Fill(255);
                for (var y = 0; y < scale; y++)
                    for (var x = 0; x < scale; x++)
                        luminance[(row * scale + y) * width * scale + col * scale + x] = 0;

                var raw = RmQRImageDecoder.ReadFormatCopy(luminance, width * scale, height * scale, 128, transform, subFinderSide, width, height);

                await Assert.That(raw).IsEqualTo(1 << bit).Because($"{version}, bit {bit}, sub-finder side {subFinderSide}");
            }
        }
    }

    /// <summary>
    /// The matrix decoder's reader, bit by bit. A decode test does not show a module read in the wrong place: either copy
    /// reads the symbol, and the word's error correction absorbs a bit or two in the other.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task ReadFormatCopies_ReadsEachBitAtItsModule(RmQRVersion version)
    {
        var height = RmQRConstants.GetHeight(version);
        var width = RmQRConstants.GetWidth(version);
        foreach (var subFinderSide in new[] { false, true })
        {
            for (var bit = 0; bit < 18; bit++)
            {
                RmQRConstants.GetFormatModule(bit, subFinderSide, height, width, out var row, out var col);
                var modules = new byte[width * height];
                modules[row * width + col] = 1;

                RmQRMatrixDecoder.ReadFormatCopies(modules, width, height, out var finderSideRaw, out var subFinderSideRaw);

                await Assert.That((finderSideRaw, subFinderSideRaw)).IsEqualTo(subFinderSide ? (0, 1 << bit) : (1 << bit, 0)).Because($"{version}, bit {bit}, sub-finder side {subFinderSide}");
            }
        }
    }
}
