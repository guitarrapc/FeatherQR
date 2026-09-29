using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// Where a sampler reads when a module's coordinate is not a pixel: every tier takes the same pixel, whatever the runtime's cast
/// does with NaN or a value past the int range (CoreCLR saturates from .NET 9 on; .NET 8 on x64 and the WebAssembly interpreter
/// write <see cref="int.MinValue"/>). Past the right edge reads the last column, anything left of it or NaN the first.
/// </summary>
/// <remarks>
/// Every module of a grid maps to the same x (the transform ignores the grid), and the image is dark in exactly the column the
/// sampler must read, so any other column shows as a light module.
/// </remarks>
public class PixelCoordinateEdgeTest
{
    private const int Width = 16;
    private const int Height = 4;

    public static IEnumerable<(float X, int Column)> Coordinates() =>
    [
        (float.NaN, 0),
        (float.NegativeInfinity, 0),
        (-1e20f, 0),
        (-2147483904f, 0),
        (-3.5f, 0),
        (-0.5f, 0),
        (0f, 0),
        (3.7f, 3),
        (15.99f, 15),
        (16f, 15),
        (1e6f, 15),
        (2147483648f, 15),
        (1e20f, 15),
        (float.PositiveInfinity, 15),
    ];

    private static byte[] DarkColumn(int column)
    {
        var luminance = new byte[Width * Height];
        luminance.AsSpan().Fill(255);
        for (var y = 0; y < Height; y++)
            luminance[y * Width + column] = 0;
        return luminance;
    }

    [Test]
    [MethodDataSource(nameof(Coordinates))]
    public async Task StandardQRSamplers_ReadTheEdgeColumn(float x, int column)
    {
        var luminance = DarkColumn(column);
        var transform = PerspectiveTransform.FromCoefficients(0f, 0f, x, 0f, 0f, 1.5f, 0f, 0f, 1f);
        const int Dimension = 21;

        var scalar = new byte[Dimension * Dimension];
        QRImageDecoder.SampleGridScalar(luminance, Width, Height, 128, transform, Dimension, scalar);
        await Assert.That(scalar.All(m => m == 1)).IsTrue().Because($"scalar, x = {x:R}");
#if NET8_0_OR_GREATER
        if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            var vector = new byte[Dimension * Dimension];
            QRImageDecoder.SampleGridSimd128(luminance, Width, Height, 128, transform, Dimension, vector);
            await Assert.That(vector.All(m => m == 1)).IsTrue().Because($"128-bit, x = {x:R}");
        }
        if (System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated)
        {
            var vector = new byte[Dimension * Dimension];
            QRImageDecoder.SampleGridSimd(luminance, Width, Height, 128, transform, Dimension, vector);
            await Assert.That(vector.All(m => m == 1)).IsTrue().Because($"256-bit, x = {x:R}");
        }
#endif
    }

    [Test]
    [MethodDataSource(nameof(Coordinates))]
    public async Task MicroQRSamplers_ReadTheEdgeColumn(float x, int column)
    {
        var luminance = DarkColumn(column);
        const int Size = 11;

        var scalar = new byte[Size * Size];
        MicroQRImageDecoder.SampleGridScalar(luminance, Width, Height, 128, x, 1.5f, 0f, 0f, 0f, 0f, Size, scalar);
        await Assert.That(scalar.All(m => m == 1)).IsTrue().Because($"scalar, x = {x:R}");
#if NET8_0_OR_GREATER
        if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            var vector = new byte[Size * Size];
            MicroQRImageDecoder.SampleGridVector128(luminance, Width, Height, 128, x, 1.5f, 0f, 0f, 0f, 0f, Size, vector);
            await Assert.That(vector.All(m => m == 1)).IsTrue().Because($"128-bit, x = {x:R}");
        }
#endif
    }

    [Test]
    [MethodDataSource(nameof(Coordinates))]
    public async Task RmQRSamplers_ReadTheEdgeColumn(float x, int column)
    {
        var luminance = DarkColumn(column);
        const int Columns = 27;
        const int Rows = 7;

        // Projective (a33 other than 1) and affine: the 128-bit sampler has a variant for each
        foreach (var transform in new[]
        {
            PerspectiveTransform.FromCoefficients(0f, 0f, x, 0f, 0f, 1.5f, 0f, 0f, 1f),
            PerspectiveTransform.FromCoefficients(0f, 0f, x * 2f, 0f, 0f, 3f, 0f, 0f, 2f),
        })
        {
            var scalar = new byte[Columns * Rows];
            RmQRImageDecoder.SampleGridScalar(luminance, Width, Height, 128, transform, Columns, Rows, scalar);
            await Assert.That(scalar.All(m => m == 1)).IsTrue().Because($"scalar, x = {x:R}, a33 = {transform.a33}");
#if NET8_0_OR_GREATER
            if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
            {
                var vector = new byte[Columns * Rows];
                RmQRImageDecoder.SampleGridSimd128(luminance, Width, Height, 128, transform, Columns, Rows, vector);
                await Assert.That(vector.All(m => m == 1)).IsTrue().Because($"128-bit, x = {x:R}, a33 = {transform.a33}");
            }
#endif
        }
    }

    /// <summary>A lattice point at NaN is neither inside nor outside the image: it is skipped, not read, on every tier.</summary>
    [Test]
    public async Task SubFinderLattice_SkipsNaNPoints()
    {
        var luminance = DarkColumn(0);
        var dark = new ulong[RmQRImageDecoder.MaxSubFinderScreenRows];
        var light = new ulong[RmQRImageDecoder.MaxSubFinderScreenRows];

        RmQRImageDecoder.ClassifySubFinderLatticeScalar(luminance, Width, Height, 128, float.NaN, 1.5f, 1f, 0f, 0f, 1f, 9, 0.1f, 0.1f, dark, light);
        await Assert.That(dark.All(b => b == 0) && light.All(b => b == 0)).IsTrue().Because("scalar");
#if NET8_0_OR_GREATER
        if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            Array.Clear(dark);
            Array.Clear(light);
            RmQRImageDecoder.ClassifySubFinderLatticeVector128(luminance, Width, Height, 128, float.NaN, 1.5f, 1f, 0f, 0f, 1f, 9, 0.1f, 0.1f, dark, light);
            await Assert.That(dark.All(b => b == 0) && light.All(b => b == 0)).IsTrue().Because("128-bit");
        }
#endif
    }
}
