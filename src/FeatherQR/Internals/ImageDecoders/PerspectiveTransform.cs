namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// Projective (perspective) plane-to-plane transform, used to map module grid coordinates onto image pixels when the symbol is captured off-axis.
/// </summary>
/// <remarks>
/// Standard homogeneous 3×3 formulation (the classic ZXing/OpenCV construction): a unit-square-to-quadrilateral transform is built directly from the target quad, its inverse comes from the adjugate matrix (a projective transform needs no normalization, so the adjugate substitutes for the true inverse), and quad-to-quad is the composition of the two.
/// Mild keystone distortion, the Tier-2 target, is exactly representable; the affine case falls out naturally when the fourth point matches the parallelogram estimate.
/// </remarks>
internal readonly struct PerspectiveTransform
{
    internal readonly float a11, a12, a13, a21, a22, a23, a31, a32, a33;

    private PerspectiveTransform(float a11, float a21, float a31, float a12, float a22, float a32, float a13, float a23, float a33)
    {
        this.a11 = a11;
        this.a12 = a12;
        this.a13 = a13;
        this.a21 = a21;
        this.a22 = a22;
        this.a23 = a23;
        this.a31 = a31;
        this.a32 = a32;
        this.a33 = a33;
    }

    /// <summary>
    /// Builds the transform mapping source quadrilateral (x0..x3, y0..y3) onto the destination quadrilateral (x0p..x3p, y0p..y3p).
    /// Point order: the four correspondences are taken pairwise; any consistent order works.
    /// </summary>
    public static PerspectiveTransform QuadrilateralToQuadrilateral(
        float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3,
        float x0p, float y0p, float x1p, float y1p, float x2p, float y2p, float x3p, float y3p)
    {
        // Composition order follows the element convention of Times (row-vector
        // application): squareToQuad ∘ quadToSquare, verified by the corner
        // correspondence unit tests.
        var quadToSquare = SquareToQuadrilateral(x0, y0, x1, y1, x2, y2, x3, y3).BuildAdjoint();
        var squareToQuad = SquareToQuadrilateral(x0p, y0p, x1p, y1p, x2p, y2p, x3p, y3p);
        return squareToQuad.Times(quadToSquare);
    }

    /// <summary>
    /// Builds a grid-to-image homography from a known point, its two local axis derivatives, and the two projective denominator coefficients.
    /// A single Micro QR finder supplies the point and local frame; bounded searches over <paramref name="perspectiveX"/> and <paramref name="perspectiveY"/> recover the remaining two degrees of freedom.
    /// </summary>
    internal static PerspectiveTransform FromLocalFrame(
        float gridX,
        float gridY,
        float imageX,
        float imageY,
        float derivativeUx,
        float derivativeUy,
        float derivativeVx,
        float derivativeVy,
        float perspectiveX,
        float perspectiveY)
    {
        var denominator = perspectiveX * gridX + perspectiveY * gridY + 1f;
        var a11 = derivativeUx * denominator + imageX * perspectiveX;
        var a12 = derivativeUy * denominator + imageY * perspectiveX;
        var a21 = derivativeVx * denominator + imageX * perspectiveY;
        var a22 = derivativeVy * denominator + imageY * perspectiveY;
        var a31 = imageX * denominator - a11 * gridX - a21 * gridY;
        var a32 = imageY * denominator - a12 * gridX - a22 * gridY;
        return new PerspectiveTransform(
            a11, a21, a31,
            a12, a22, a32,
            perspectiveX, perspectiveY, 1f);
    }

    /// <summary>
    /// Transforms (x, y) through the projective map.
    /// </summary>
    public void Transform(float x, float y, out float xOut, out float yOut)
    {
        var denominator = a13 * x + a23 * y + a33;
        xOut = (a11 * x + a21 * y + a31) / denominator;
        yOut = (a12 * x + a22 * y + a32) / denominator;
    }

    /// <summary>
    /// Maps the unit square (0,0)-(1,1) onto the quadrilateral (x0,y0)..(x3,y3) given in the order top-left, top-right, bottom-right, bottom-left.
    /// </summary>
    private static PerspectiveTransform SquareToQuadrilateral(
        float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3)
    {
        var dx3 = x0 - x1 + x2 - x3;
        var dy3 = y0 - y1 + y2 - y3;
        if (dx3 == 0f && dy3 == 0f)
        {
            // Affine case
            return new PerspectiveTransform(
                x1 - x0, x2 - x1, x0,
                y1 - y0, y2 - y1, y0,
                0f, 0f, 1f);
        }

        var dx1 = x1 - x2;
        var dx2 = x3 - x2;
        var dy1 = y1 - y2;
        var dy2 = y3 - y2;
        var denominator = dx1 * dy2 - dx2 * dy1;
        var a13 = (dx3 * dy2 - dx2 * dy3) / denominator;
        var a23 = (dx1 * dy3 - dx3 * dy1) / denominator;
        return new PerspectiveTransform(
            x1 - x0 + a13 * x1, x3 - x0 + a23 * x3, x0,
            y1 - y0 + a13 * y1, y3 - y0 + a23 * y3, y0,
            a13, a23, 1f);
    }

    /// <summary>
    /// Adjugate matrix: for projective transforms (defined up to scale) the adjugate acts as the inverse without needing the determinant division.
    /// </summary>
    private PerspectiveTransform BuildAdjoint()
    {
        return new PerspectiveTransform(
            a22 * a33 - a23 * a32,
            a23 * a31 - a21 * a33,
            a21 * a32 - a22 * a31,
            a13 * a32 - a12 * a33,
            a11 * a33 - a13 * a31,
            a12 * a31 - a11 * a32,
            a12 * a23 - a13 * a22,
            a13 * a21 - a11 * a23,
            a11 * a22 - a12 * a21);
    }

    private PerspectiveTransform Times(in PerspectiveTransform other)
    {
        return new PerspectiveTransform(
            a11 * other.a11 + a21 * other.a12 + a31 * other.a13,
            a11 * other.a21 + a21 * other.a22 + a31 * other.a23,
            a11 * other.a31 + a21 * other.a32 + a31 * other.a33,
            a12 * other.a11 + a22 * other.a12 + a32 * other.a13,
            a12 * other.a21 + a22 * other.a22 + a32 * other.a23,
            a12 * other.a31 + a22 * other.a32 + a32 * other.a33,
            a13 * other.a11 + a23 * other.a12 + a33 * other.a13,
            a13 * other.a21 + a23 * other.a22 + a33 * other.a23,
            a13 * other.a31 + a23 * other.a32 + a33 * other.a33);
    }
}
