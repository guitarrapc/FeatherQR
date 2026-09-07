using TUnit.Assertions.Enums;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// All three <see cref="RmQRMatrixDecoder.ExtractCodewords(ReadOnlySpan{byte}, int, int, RmQRVersion, Span{byte}, RmQRMatrixDecoder.ExtractKernel)"/>
/// tiers (the x64 bit-plane kernel, the ARM64 pair-plane kernel and the portable
/// table walk) against
/// <see cref="RmQRNaiveReference.ExtractInterleavedStream"/>, byte for byte, for
/// every version over module grids that pin the contract corners: all light, all
/// dark written as 1 / 0xFF / 2 (the API is "0 = light, non-zero = dark", so a
/// kernel that bit-tests instead of comparing against zero fails here), several
/// pseudo-random grids.
/// </summary>
/// <remarks>
/// The reference walks the zigzag per module with its own independently written
/// function-module map, so it shares no code with either kernel.
/// </remarks>
public class RmQRExtractCodewordsParityTest
{
    public static IEnumerable<RmQRVersion> AllVersions() => Enum.GetValues<RmQRVersion>();

    /// <summary>
    /// A stream shorter than the version's codeword count is legal and truncates: the
    /// vector tiers emit whole words off a per-version table and cannot honour it, so
    /// the dispatcher must fall back to the portable walk. Nothing else pins that
    /// guard, and deleting it would let a vector kernel run off the end of the span.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task ShortStream_FallsBackToTheWalk_AndTruncates(RmQRVersion version)
    {
        var width = RmQRConstants.GetWidth(version);
        var height = RmQRConstants.GetHeight(version);
        var total = RmQRConstants.GetTotalCodewordCount(version);
        // Shape 4 (pseudo-random), not a uniform fill: a uniform grid is invariant under
        // the kernels' transpose, row reversal and run compression, so every tier would
        // agree with every other no matter how broken it was.
        var grid = Grid(width * height, 4, (int)version + 41);

        var full = new byte[total];
        RmQRMatrixDecoder.ExtractCodewords(grid, width, height, version, full, RmQRMatrixDecoder.ExtractKernel.Auto);

        foreach (var length in new[] { 1, total / 2, total - 1 })
        {
            var backing = new byte[total];
            backing.AsSpan().Fill(0xA5);
            RmQRMatrixDecoder.ExtractCodewords(grid, width, height, version, backing.AsSpan(0, length), RmQRMatrixDecoder.ExtractKernel.Auto);

            await Assert.That(backing.AsSpan(0, length).ToArray()).IsEquivalentTo(full.AsSpan(0, length).ToArray())
                .Because($"{version}: a {length}-byte stream must be the full stream's prefix");
            for (var i = length; i < total; i++)
            {
                await Assert.That(backing[i]).IsEqualTo((byte)0xA5)
                    .Because($"{version}: wrote {i - length + 1} byte(s) past a {length}-byte stream");
            }
        }
    }

    private static byte[] Grid(int length, int shape, int seed)
    {
        var grid = new byte[length];
        switch (shape)
        {
            case 0:
                break; // all light
            case 1:
                grid.AsSpan().Fill(1);
                break;
            case 2:
                grid.AsSpan().Fill(0xFF);
                break;
            case 3:
                grid.AsSpan().Fill(2); // dark, but neither 1 nor 0xFF
                break;
            default:
                var state = (uint)seed * 2654435761u + 7u;
                for (var i = 0; i < length; i++)
                {
                    state = state * 1664525u + 1013904223u;
                    var r = state >> 16;
                    // Mix in non-1 dark values so the != 0 contract stays covered.
                    grid[i] = (r & 1) == 0 ? (byte)0 : (byte)((r & 6) == 0 ? 2 : 1);
                }
                break;
        }
        return grid;
    }

    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task EveryTier_MatchesNaiveReference_EveryGridShape(RmQRVersion version)
    {
        var width = RmQRConstants.GetWidth(version);
        var height = RmQRConstants.GetHeight(version);
        var totalCodewords = RmQRConstants.GetTotalCodewordCount(version);

        for (var shape = 0; shape < 8; shape++)
        {
            var modules = Grid(width * height, shape, (int)version * 31 + shape);
            var expected = RmQRNaiveReference.ExtractInterleavedStream(modules, height, width, out _)
                .AsSpan(0, totalCodewords)
                .ToArray();

            // Poison the destination so a kernel that fails to write a byte is caught.
            var scalar = new byte[totalCodewords];
            scalar.AsSpan().Fill(0xA5);
            RmQRMatrixDecoder.ExtractCodewords(modules, width, height, version, scalar, RmQRMatrixDecoder.ExtractKernel.Scalar);
            await Assert.That(scalar).IsEquivalentTo(expected, CollectionOrdering.Matching)
                .Because($"scalar tier, version {version} ({width}x{height}), grid shape {shape}");

            if (RmQRMatrixDecoder.IsBitPlaneTierSupported)
            {
                var bitPlanes = new byte[totalCodewords];
                bitPlanes.AsSpan().Fill(0xA5);
                RmQRMatrixDecoder.ExtractCodewords(modules, width, height, version, bitPlanes, RmQRMatrixDecoder.ExtractKernel.BitPlanes);
                await Assert.That(bitPlanes).IsEquivalentTo(expected, CollectionOrdering.Matching)
                    .Because($"bit-plane tier, version {version} ({width}x{height}), grid shape {shape}");
            }

            if (RmQRMatrixDecoder.IsPairPlaneTierSupported)
            {
                var pairPlanes = new byte[totalCodewords];
                pairPlanes.AsSpan().Fill(0xA5);
                RmQRMatrixDecoder.ExtractCodewords(modules, width, height, version, pairPlanes, RmQRMatrixDecoder.ExtractKernel.PairPlanes);
                await Assert.That(pairPlanes).IsEquivalentTo(expected, CollectionOrdering.Matching)
                    .Because($"pair-plane tier, version {version} ({width}x{height}), grid shape {shape}");
            }
        }
    }

    /// <summary>
    /// Both vector transposes deliberately run past the end of a row (16 bytes at a time
    /// on x64, up to 32 on ARM64), relying on there always being a row below rows
    /// 1..h-2. This pins that neither kernel reads outside the caller's span: the grid is
    /// followed by bytes of the opposite polarity that the kernel must not need.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(AllVersions))]
    public async Task VectorTiers_StayInsideTheModuleSpan(RmQRVersion version)
    {
        if (!RmQRMatrixDecoder.IsBitPlaneTierSupported && !RmQRMatrixDecoder.IsPairPlaneTierSupported)
        {
            Skip.Test("no vector extraction tier on this machine");
            return;
        }

        var width = RmQRConstants.GetWidth(version);
        var height = RmQRConstants.GetHeight(version);
        var totalCodewords = RmQRConstants.GetTotalCodewordCount(version);
        var length = width * height;

        var modules = Grid(length, 4, (int)version * 7 + 3);
        var expected = RmQRNaiveReference.ExtractInterleavedStream(modules, height, width, out _)
            .AsSpan(0, totalCodewords)
            .ToArray();

        // Same grid, but the span ends exactly at the last module and is followed by
        // bytes of the opposite polarity: an overread past width*height would change
        // the result.
        var padded = new byte[length + 64];
        padded.AsSpan().Fill(0xFF);
        modules.CopyTo(padded, 0);

        if (RmQRMatrixDecoder.IsBitPlaneTierSupported)
        {
            var actual = new byte[totalCodewords];
            RmQRMatrixDecoder.ExtractCodewords(padded.AsSpan(0, length), width, height, version, actual, RmQRMatrixDecoder.ExtractKernel.BitPlanes);
            await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching)
                .Because($"bit-plane tier, version {version} ({width}x{height}) must not depend on bytes past width*height");
        }

        if (RmQRMatrixDecoder.IsPairPlaneTierSupported)
        {
            var actual = new byte[totalCodewords];
            RmQRMatrixDecoder.ExtractCodewords(padded.AsSpan(0, length), width, height, version, actual, RmQRMatrixDecoder.ExtractKernel.PairPlanes);
            await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching)
                .Because($"pair-plane tier, version {version} ({width}x{height}) must not depend on bytes past width*height");
        }
    }
}
