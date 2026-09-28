namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// The grid-to-image homography that fits point correspondences best in the least-squares sense, accumulated one point at a time with nothing stored.
/// </summary>
/// <remarks>
/// Each point gives two equations linear in the eight coefficients once the denominator is multiplied out, and the normal equations of all of them are sums of 23 moments of the points.
/// The image side is moved and scaled to unit size first, about a point and a span the caller names: raw, the sums grow with the fourth power of the pixel coordinates, and a symbol 4,000 px from the origin already leaves them singular in double.
/// Grid coordinates stay as they are; within a symbol they are small enough.
/// </remarks>
internal struct ProjectiveFit
{
    private readonly double _imageX, _imageY, _imageScale;

    // Σ over the points of the products the normal equations need: p = (u, v, 1), x and y normalized, r² = x² + y²
    private double _uu, _uv, _vv, _u, _v, _n;
    private double _xuu, _xuv, _xvv, _xu, _xv, _x;
    private double _yuu, _yuv, _yvv, _yu, _yv, _y;
    private double _ruu, _ruv, _rvv, _ru, _rv;

    /// <param name="imageX">Image point the image side is centred on.</param>
    /// <param name="imageY">Image point the image side is centred on.</param>
    /// <param name="imageSpan">Image distance scaled to 1.</param>
    public ProjectiveFit(double imageX, double imageY, double imageSpan)
    {
        _imageX = imageX;
        _imageY = imageY;
        _imageScale = 1d / imageSpan;
    }

    /// <summary>Grid point (<paramref name="u"/>, <paramref name="v"/>) is seen at image point (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void Add(float u, float v, float x, float y)
    {
        double gu = u;
        double gv = v;
        var ix = (x - _imageX) * _imageScale;
        var iy = (y - _imageY) * _imageScale;
        var uu = gu * gu;
        var uv = gu * gv;
        var vv = gv * gv;
        var r = ix * ix + iy * iy;
        _uu += uu;
        _uv += uv;
        _vv += vv;
        _u += gu;
        _v += gv;
        _n += 1d;
        _xuu += ix * uu;
        _xuv += ix * uv;
        _xvv += ix * vv;
        _xu += ix * gu;
        _xv += ix * gv;
        _x += ix;
        _yuu += iy * uu;
        _yuv += iy * uv;
        _yvv += iy * vv;
        _yu += iy * gu;
        _yv += iy * gv;
        _y += iy;
        _ruu += r * uu;
        _ruv += r * uv;
        _rvv += r * vv;
        _ru += r * gu;
        _rv += r * gv;
    }

    /// <summary>
    /// The fitted transform, grid to image. False when the points do not determine one: fewer than four, or all but collinear.
    /// </summary>
    public readonly bool TrySolve(out PerspectiveTransform transform)
    {
        transform = default;

        // Unknowns h0..h7: x = (h0·u + h1·v + h2) / (h6·u + h7·v + 1), y = (h3·u + h4·v + h5) / (the same)
        Span<double> m = stackalloc double[8 * 9];
        Set(m, 0, 0, _uu); Set(m, 0, 1, _uv); Set(m, 0, 2, _u);
        Set(m, 1, 1, _vv); Set(m, 1, 2, _v);
        Set(m, 2, 2, _n);
        Set(m, 3, 3, _uu); Set(m, 3, 4, _uv); Set(m, 3, 5, _u);
        Set(m, 4, 4, _vv); Set(m, 4, 5, _v);
        Set(m, 5, 5, _n);
        Set(m, 0, 6, -_xuu); Set(m, 0, 7, -_xuv);
        Set(m, 1, 6, -_xuv); Set(m, 1, 7, -_xvv);
        Set(m, 2, 6, -_xu); Set(m, 2, 7, -_xv);
        Set(m, 3, 6, -_yuu); Set(m, 3, 7, -_yuv);
        Set(m, 4, 6, -_yuv); Set(m, 4, 7, -_yvv);
        Set(m, 5, 6, -_yu); Set(m, 5, 7, -_yv);
        Set(m, 6, 6, _ruu); Set(m, 6, 7, _ruv);
        Set(m, 7, 7, _rvv);
        m[0 * 9 + 8] = _xu;
        m[1 * 9 + 8] = _xv;
        m[2 * 9 + 8] = _x;
        m[3 * 9 + 8] = _yu;
        m[4 * 9 + 8] = _yv;
        m[5 * 9 + 8] = _y;
        m[6 * 9 + 8] = -_ru;
        m[7 * 9 + 8] = -_rv;

        Span<double> h = stackalloc double[8];
        if (!TrySolveLinear(m, h))
            return false;

        // Back to pixels: x = x' / scale + imageX over the same denominator
        var s = 1d / _imageScale;
        transform = PerspectiveTransform.FromCoefficients(
            (float)(h[0] * s + _imageX * h[6]), (float)(h[1] * s + _imageX * h[7]), (float)(h[2] * s + _imageX),
            (float)(h[3] * s + _imageY * h[6]), (float)(h[4] * s + _imageY * h[7]), (float)(h[5] * s + _imageY),
            (float)h[6], (float)h[7], 1f);
        return true;
    }

    /// <summary>Both halves of a symmetric entry.</summary>
    private static void Set(Span<double> m, int row, int column, double value)
    {
        m[row * 9 + column] = value;
        m[column * 9 + row] = value;
    }

    /// <summary>Gaussian elimination with partial pivoting on the 8 × 9 augmented matrix.</summary>
    private static bool TrySolveLinear(Span<double> m, Span<double> solution)
    {
        // Pivots under this, relative to the largest entry, leave the system singular in double
        var largest = 0d;
        for (var i = 0; i < 72; i++)
            largest = Math.Max(largest, Math.Abs(m[i]));
        var tiny = largest * 1e-12;
        for (var column = 0; column < 8; column++)
        {
            var pivot = column;
            for (var row = column + 1; row < 8; row++)
            {
                if (Math.Abs(m[row * 9 + column]) > Math.Abs(m[pivot * 9 + column]))
                    pivot = row;
            }
            if (!(Math.Abs(m[pivot * 9 + column]) > tiny))
                return false;
            if (pivot != column)
            {
                for (var k = column; k < 9; k++)
                    (m[column * 9 + k], m[pivot * 9 + k]) = (m[pivot * 9 + k], m[column * 9 + k]);
            }
            var inverse = 1d / m[column * 9 + column];
            for (var row = column + 1; row < 8; row++)
            {
                var factor = m[row * 9 + column] * inverse;
                if (factor == 0d)
                    continue;
                for (var k = column; k < 9; k++)
                    m[row * 9 + k] -= factor * m[column * 9 + k];
            }
        }
        for (var row = 7; row >= 0; row--)
        {
            var sum = m[row * 9 + 8];
            for (var k = row + 1; k < 8; k++)
                sum -= m[row * 9 + k] * solution[k];
            solution[row] = sum / m[row * 9 + row];
        }
        return true;
    }
}
