using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

public class PerspectiveTransformUnitTest
{
    [Test]
    public async Task QuadToQuad_MapsCorrespondencePoints()
    {
        // Map a square onto a keystone quad. All four correspondences must hold.
        var transform = PerspectiveTransform.QuadrilateralToQuadrilateral(
            0, 0, 10, 0, 10, 10, 0, 10,
            2, 1, 11, 3, 9, 12, 1, 9);

        await AssertMaps(transform, 0, 0, 2, 1);
        await AssertMaps(transform, 10, 0, 11, 3);
        await AssertMaps(transform, 10, 10, 9, 12);
        await AssertMaps(transform, 0, 10, 1, 9);
    }

    [Test]
    public async Task QuadToQuad_AffineCase_MapsMidpoints()
    {
        // Pure affine mapping (parallelogram): midpoints must map to midpoints
        var transform = PerspectiveTransform.QuadrilateralToQuadrilateral(
            0, 0, 4, 0, 4, 4, 0, 4,
            10, 20, 18, 22, 20, 30, 12, 28);

        await AssertMaps(transform, 2, 2, 15, 25);
        await AssertMaps(transform, 0, 2, 11, 24);
    }

    [Test]
    public async Task QuadToQuad_Identity_IsExact()
    {
        var transform = PerspectiveTransform.QuadrilateralToQuadrilateral(
            3.5f, 3.5f, 21.5f, 3.5f, 18.5f, 18.5f, 3.5f, 21.5f,
            3.5f, 3.5f, 21.5f, 3.5f, 18.5f, 18.5f, 3.5f, 21.5f);

        await AssertMaps(transform, 12.5f, 7.25f, 12.5f, 7.25f);
    }

    [Test]
    public async Task QuadToQuad_PerspectiveRoundTrip()
    {
        // Forward then inverse mapping returns the original points
        var forward = PerspectiveTransform.QuadrilateralToQuadrilateral(
            0, 0, 100, 0, 100, 100, 0, 100,
            10, 15, 105, 5, 95, 110, 5, 95);
        var inverse = PerspectiveTransform.QuadrilateralToQuadrilateral(
            10, 15, 105, 5, 95, 110, 5, 95,
            0, 0, 100, 0, 100, 100, 0, 100);

        for (var y = 10; y <= 90; y += 40)
        {
            for (var x = 10; x <= 90; x += 40)
            {
                forward.Transform(x, y, out var fx, out var fy);
                inverse.Transform(fx, fy, out var bx, out var by);
                await Assert.That(bx).IsEqualTo(x).Within(2f);
                await Assert.That(by).IsEqualTo(y).Within(2f);
            }
        }
    }

    [Test]
    public async Task FromLocalFrame_PreservesAnchorAndDerivatives()
    {
        const float gridX = 3.5f;
        const float gridY = 3.5f;
        const float imageX = 42f;
        const float imageY = 27f;
        const float derivativeUx = 7.2f;
        const float derivativeUy = 1.1f;
        const float derivativeVx = -0.8f;
        const float derivativeVy = 6.6f;
        const float step = 0.001f;

        var transform = PerspectiveTransform.FromLocalFrame(
            gridX, gridY,
            imageX, imageY,
            derivativeUx, derivativeUy,
            derivativeVx, derivativeVy,
            perspectiveX: 0.004f,
            perspectiveY: -0.003f);

        await AssertMaps(transform, gridX, gridY, imageX, imageY);

        transform.Transform(gridX + step, gridY, out var uX, out var uY);
        transform.Transform(gridX, gridY + step, out var vX, out var vY);
        await Assert.That((uX - imageX) / step).IsEqualTo(derivativeUx).Within(0.02f);
        await Assert.That((uY - imageY) / step).IsEqualTo(derivativeUy).Within(0.02f);
        await Assert.That((vX - imageX) / step).IsEqualTo(derivativeVx).Within(0.02f);
        await Assert.That((vY - imageY) / step).IsEqualTo(derivativeVy).Within(0.02f);
    }

    private static async Task AssertMaps(PerspectiveTransform transform, float x, float y, float expectedX, float expectedY)
    {
        transform.Transform(x, y, out var actualX, out var actualY);
        await Assert.That(actualX).IsEqualTo(expectedX).Within(0.01f);
        await Assert.That(actualY).IsEqualTo(expectedY).Within(0.01f);
    }
}
