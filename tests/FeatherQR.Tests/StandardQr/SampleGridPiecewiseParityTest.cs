using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The piecewise mesh sampler's fast tiers (<see cref="QRImageDecoder.SampleGridPiecewiseColumnTable"/>,
/// the portable one, <c>SampleGridPiecewiseAvx2</c> and <c>SampleGridPiecewiseAdvSimd</c>) and the dispatcher
/// (<see cref="QRImageDecoder.SampleGridPiecewise"/>) against the per-module loop
/// (<see cref="QRImageDecoder.SampleGridPiecewiseScalar"/>), module for module.
/// </summary>
/// <remarks>
/// A sampler is right only when it picks the same pixel, so "close" is a failure: one ulp of
/// difference in a coordinate can truncate into the neighbouring pixel, and on a module boundary that
/// is a different bit. The tiers reach identity by doing the reference's float operations in the
/// reference's order (no reciprocal, no fused multiply-add) and moving only where they happen: the
/// fraction across a cell once a column, a cell's start and span once a row.
/// The scenes are chosen by what can go wrong rather than by what decodes: every version that has a
/// mesh (the row tail is 1 or 5 modules, and which 8-module steps straddle two cells differs per
/// version), a mesh bent by rotation and keystone, nodes moved off their predictions, symbols pushed
/// past each image edge so all four clamps fire inside a vector step, and nodes no detector would
/// produce (NaN, infinities, magnitudes past int range), which is where a vector float-to-int
/// conversion and the scalar cast part ways unless the tier is written for it.
/// </remarks>
public class SampleGridPiecewiseParityTest
{
    private const byte Threshold = 128;

    private delegate void Sampler(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, ReadOnlySpan<float> gridCoords, ReadOnlySpan<float> nodeXs, ReadOnlySpan<float> nodeYs, int meshSize, int dimension, Span<byte> modules);

    private sealed record Scene(byte[] Luminance, int Width, int Height, float[] GridCoords, float[] NodeXs, float[] NodeYs, int MeshSize, int Dimension);

    public static IEnumerable<int> MeshVersions() => Enumerable.Range(14, 27);

    private static IEnumerable<(string Name, Sampler Fn)> Tiers()
    {
        yield return ("dispatcher", QRImageDecoder.SampleGridPiecewise);
        yield return ("column table", QRImageDecoder.SampleGridPiecewiseColumnTable);
#if NET8_0_OR_GREATER
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
            yield return ("AVX2", QRImageDecoder.SampleGridPiecewiseAvx2);
        if (System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            yield return ("AdvSimd", QRImageDecoder.SampleGridPiecewiseAdvSimd);
#endif
    }

    /// <summary>
    /// Alignment centres from the library's own Annex E table, a homography for the node positions,
    /// each node moved by up to <paramref name="jitter"/> pixels, and a two-valued image of blocks
    /// (the content only decides the compare; the geometry decides which pixel is read).
    /// </summary>
    private static Scene BuildScene(int version, float pixelsPerModule, bool bent, float shiftModules, float jitter, int seed)
    {
        var dimension = 17 + 4 * version;
        var baseValues = QRCodeConstants.AlignmentPatternBaseValues.Slice((version - 1) * 7, 7);
        var gridCoords = new List<float>();
        foreach (var value in baseValues)
        {
            if (value != 0)
                gridCoords.Add(value + 0.5f);
        }
        var meshSize = gridCoords.Count;
        var rng = new Random(seed);

        var angle = bent ? 17.0 * Math.PI / 180.0 : 0.0;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var keystone = bent ? 0.00035 : 0.0;
        var side = (int)Math.Ceiling((dimension + 8) * pixelsPerModule * (Math.Abs(cos) + Math.Abs(sin)));
        var centre = side / 2.0 - shiftModules * pixelsPerModule;

        var nodeXs = new float[meshSize * meshSize];
        var nodeYs = new float[meshSize * meshSize];
        for (var j = 0; j < meshSize; j++)
        {
            for (var i = 0; i < meshSize; i++)
            {
                var gx = (gridCoords[i] - dimension / 2.0) * pixelsPerModule;
                var gy = (gridCoords[j] - dimension / 2.0) * pixelsPerModule;
                var w = 1.0 + keystone * gx + keystone * 0.5 * gy;
                nodeXs[j * meshSize + i] = (float)(centre + (gx * cos - gy * sin) / w + (rng.NextDouble() - 0.5) * 2 * jitter);
                nodeYs[j * meshSize + i] = (float)(centre + (gx * sin + gy * cos) / w + (rng.NextDouble() - 0.5) * 2 * jitter);
            }
        }

        // Not square, so a clamp against the wrong side's limit shows; and the blocks take the values either side of
        // the threshold as well as the extremes, so a compare that is off by one shows.
        var height = side + 37;
        var luminance = new byte[side * height];
        ReadOnlySpan<byte> levels = [0, Threshold - 1, Threshold, Threshold + 1, 255];
        var block = Math.Max(1, (int)pixelsPerModule);
        var blocksPerRow = side / block + 1;
        var blocksPerColumn = height / block + 1;
        var level = new byte[blocksPerRow * blocksPerColumn];
        for (var i = 0; i < level.Length; i++)
            level[i] = levels[rng.Next(levels.Length)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < side; x++)
                luminance[y * side + x] = level[y / block * blocksPerRow + x / block];
        }

        return new Scene(luminance, side, height, gridCoords.ToArray(), nodeXs, nodeYs, meshSize, dimension);
    }

    private static string? FirstMismatch(Scene scene, string label)
    {
        var expected = new byte[scene.Dimension * scene.Dimension];
        QRImageDecoder.SampleGridPiecewiseScalar(scene.Luminance, scene.Width, scene.Height, Threshold, scene.GridCoords, scene.NodeXs, scene.NodeYs, scene.MeshSize, scene.Dimension, expected);

        foreach (var (name, fn) in Tiers())
        {
            // dirty, and longer than the grid: a tier has to store every module and nothing after them
            var actual = new byte[expected.Length + 16];
            actual.AsSpan().Fill(0xA5);
            fn(scene.Luminance, scene.Width, scene.Height, Threshold, scene.GridCoords, scene.NodeXs, scene.NodeYs, scene.MeshSize, scene.Dimension, actual);

            var at = actual.AsSpan(0, expected.Length).CommonPrefixLength(expected);
            if (at != expected.Length)
                return $"{name}, {label}: module {at} (row {at / scene.Dimension}, column {at % scene.Dimension})";
            if (actual.AsSpan(expected.Length).IndexOfAnyExcept((byte)0xA5) >= 0)
                return $"{name}, {label}: wrote past the grid";
        }
        return null;
    }

    [Test]
    [MethodDataSource(nameof(MeshVersions))]
    public async Task EveryTier_PicksTheReferencePixel_UprightAndBent(int version)
    {
        var mismatches = new List<string>();
        foreach (var pixels in new[] { 1f, 2.4f, 3f, 8f })
        {
            foreach (var bent in new[] { false, true })
            {
                // jitter 0 = every node left at its prediction, 0.3 = nodes the alignment finder moved
                foreach (var jitter in new[] { 0f, 0.3f })
                {
                    var label = $"v{version} {pixels} px bent={bent} jitter={jitter}";
                    if (FirstMismatch(BuildScene(version, pixels, bent, 0f, jitter, version * 7 + 1), label) is { } mismatch)
                        mismatches.Add(mismatch);
                }
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    /// <summary>
    /// The symbol pushed past the top-left (positive shift) and the bottom-right (negative) of its
    /// image, by a whole number of modules and by a fraction, so each clamp fires across whole rows
    /// and part-way through an 8-module step.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(MeshVersions))]
    public async Task EveryTier_PicksTheReferencePixel_WhenSamplesClampAtEachImageEdge(int version)
    {
        var mismatches = new List<string>();
        foreach (var shift in new[] { 40f, 93.3f, -40f, -93.3f })
        {
            foreach (var bent in new[] { false, true })
            {
                var label = $"v{version} shift={shift} bent={bent}";
                if (FirstMismatch(BuildScene(version, 3f, bent, shift, 0.3f, version + 100), label) is { } mismatch)
                    mismatches.Add(mismatch);
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    /// <summary>
    /// What the reference's cast makes of these depends on the runtime: from .NET 9 it saturates and
    /// maps NaN to 0, so +∞ is the far edge of the image; on net8.0 it is the raw x64 conversion,
    /// INT_MIN for every one of them, which the clamp takes to 0. A raw vector conversion gives the
    /// net8.0 answer on both, so the vector tier has a form per runtime, and on the saturating side
    /// which operand of the float minimum holds the limit decides where NaN lands.
    /// Both target frameworks have to run this: the first port passed on net10.0 and failed on net8.0.
    /// </summary>
    [Test]
    [Arguments(float.NaN)]
    [Arguments(float.PositiveInfinity)]
    [Arguments(float.NegativeInfinity)]
    [Arguments(1e12f)]
    [Arguments(-1e12f)]
    [Arguments(2147483648f)]
    [Arguments(-2147483904f)]
    public async Task EveryTier_PicksTheReferencePixel_ForANodeNoDetectorWouldProduce(float poison)
    {
        var mismatches = new List<string>();
        foreach (var version in new[] { 14, 20, 40 })
        {
            var nodes = BuildScene(version, 3f, true, 0f, 0.3f, 7).MeshSize;
            foreach (var node in new[] { 0, nodes + 1, nodes * nodes - 1 })
            {
                foreach (var onX in new[] { true, false })
                {
                    var scene = BuildScene(version, 3f, true, 0f, 0.3f, 7);
                    (onX ? scene.NodeXs : scene.NodeYs)[node] = poison;
                    if (FirstMismatch(scene, $"v{version} node {node} {(onX ? "x" : "y")} = {poison}") is { } mismatch)
                        mismatches.Add(mismatch);
                }
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    /// <summary>
    /// The vector tier builds a step from at most two cells, which holds for every Annex E lattice
    /// (cells are at least 20 modules wide). Handed a lattice with cells three modules wide, where an
    /// 8-module step crosses three, it has to notice and leave the grid to a tier that does not assume it.
    /// </summary>
    [Test]
    public async Task EveryTier_PicksTheReferencePixel_WhenCellsAreNarrowerThanAVectorStep()
    {
        var scene = BuildScene(14, 3f, true, 0f, 0.3f, 3);
        var meshSize = 7;
        var gridCoords = new float[meshSize];
        for (var i = 0; i < meshSize; i++)
            gridCoords[i] = 6.5f + 3 * i;
        var rng = new Random(11);
        var nodeXs = new float[meshSize * meshSize];
        var nodeYs = new float[meshSize * meshSize];
        for (var j = 0; j < meshSize; j++)
        {
            for (var i = 0; i < meshSize; i++)
            {
                nodeXs[j * meshSize + i] = 20f + gridCoords[i] * 3f + (float)rng.NextDouble();
                nodeYs[j * meshSize + i] = 20f + gridCoords[j] * 3f + (float)rng.NextDouble();
            }
        }
        var narrow = scene with { GridCoords = gridCoords, NodeXs = nodeXs, NodeYs = nodeYs, MeshSize = meshSize };

        await Assert.That(FirstMismatch(narrow, "cells 3 modules wide")).IsNull();
    }

    /// <summary>
    /// The fast tiers store through unchecked references, so the two lengths they rely on are checked
    /// once at the top; a short buffer is an exception there, not a write past the span.
    /// </summary>
    [Test]
    public async Task EveryTier_RejectsBuffersItCannotIndex()
    {
        var scene = BuildScene(14, 3f, false, 0f, 0f, 1);
        var modules = new byte[scene.Dimension * scene.Dimension];

        foreach (var (name, fn) in Tiers())
        {
            await Assert.That(() => fn(scene.Luminance.AsSpan(0, scene.Luminance.Length - 1), scene.Width, scene.Height, Threshold, scene.GridCoords, scene.NodeXs, scene.NodeYs, scene.MeshSize, scene.Dimension, modules))
                .Throws<ArgumentException>().Because($"{name}: luminance one byte short");
            await Assert.That(() => fn(scene.Luminance, scene.Width, scene.Height, Threshold, scene.GridCoords, scene.NodeXs, scene.NodeYs, scene.MeshSize, scene.Dimension, modules.AsSpan(0, modules.Length - 1)))
                .Throws<ArgumentException>().Because($"{name}: modules one byte short");
        }
    }
}
