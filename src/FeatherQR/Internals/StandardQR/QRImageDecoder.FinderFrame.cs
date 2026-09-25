using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Internals.StandardQR;

internal static partial class QRImageDecoder
{
    /// <summary>
    /// The projective map the three finder centres and the module sizes along the two finder lines determine together: a flat symbol in perspective foreshortens along each line, and one plane fits the three centres and both changes of size.
    /// </summary>
    /// <remarks>
    /// With the centres at barycentric weights 1, <c>w_u</c> and <c>w_v</c>, a grid point at <c>a</c> of the way from the top-left centre to the top-right one and <c>b</c> of the way to the bottom-left one maps to
    /// <c>((1 − a − b)·TL + w_u·a·TR + w_v·b·BL) / ((1 − a − b) + w_u·a + w_v·b)</c>, and along a line the size at one end over the size at the other is the square of the weights' ratio, so
    /// <c>w_u = √(s_TL / s_TR)</c> along the top line and <c>w_v = √(s_TL / s_BL)</c> along the left one. Both weights at 1 is the parallelogram of the three centres.
    /// A size that is a percent or two off reads as perspective that is not there, so the frame predicts where to look and a grid is sampled through it only when nothing anchored the fourth corner and the parallelogram failed.
    /// </remarks>
    internal readonly struct FinderFrame
    {
        public FinderPattern TopLeft { get; }
        public FinderPattern TopRight { get; }
        public FinderPattern BottomLeft { get; }
        private readonly float _uWeight;
        private readonly float _vWeight;

        private FinderFrame(in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, float uWeight, float vWeight)
        {
            TopLeft = topLeft;
            TopRight = topRight;
            BottomLeft = bottomLeft;
            _uWeight = uWeight;
            _vWeight = vWeight;
        }

        /// <summary>True when the frame is the parallelogram of the three centres.</summary>
        public bool IsAffine => _uWeight == 1f && _vWeight == 1f;

        public static FinderFrame Create(in FinderPattern topLeft, in FinderPattern topRight, in FinderPattern bottomLeft, in FinderModuleSizes sizes)
        {
            var uWeight = Weight(sizes.TopLeftAlongU, sizes.TopRight, sizes.SubPixelAlongU);
            var vWeight = Weight(sizes.TopLeftAlongV, sizes.BottomLeft, sizes.SubPixelAlongV);
            // A plane in front of the camera keeps the denominator positive over the whole symbol, whose
            // corners lie at a and b from −0.25 to 1.25 at version 1 and closer in above it; sizes that put
            // the horizon across the symbol were measured wrong
            if (!(Lowest(uWeight) + Lowest(vWeight) > -1f))
                return new FinderFrame(topLeft, topRight, bottomLeft, 1f, 1f);
            return new FinderFrame(topLeft, topRight, bottomLeft, uWeight, vWeight);

            static float Lowest(float weight) => Math.Min((weight - 1f) * -0.25f, (weight - 1f) * 1.25f);
        }

        /// <summary>
        /// The weight of the far end of a line whose sizes are <paramref name="near"/> and <paramref name="far"/>.
        /// Whole-pixel runs place each of their four edges on a half-pixel step, within half a step of the edge, so their sizes are whole twelfths of a pixel, each within two twelfths of its true value: two that differ by up to four twelfths may be one size, and the line is taken as flat.
        /// </summary>
        private static float Weight(float near, float far, bool subPixel)
        {
            // Compared between four and five twelfths, where float rounding cannot move a difference across
            if (!subPixel && !(Math.Abs(near - far) * 12f > 4.5f))
                return 1f;
            return (float)Math.Sqrt(near / far);
        }

        /// <summary>The pixel position of grid point (<paramref name="u"/>, <paramref name="v"/>) of a symbol <paramref name="dimension"/> modules wide, whose finder centres sit at 3.5 and <c>dimension − 3.5</c>.</summary>
        public void Map(float u, float v, int dimension, out float x, out float y)
        {
            var span = (float)(dimension - 7);
            var a = (u - 3.5f) / span;
            var b = (v - 3.5f) / span;
            var origin = 1f - a - b;
            var along = _uWeight * a;
            var down = _vWeight * b;
            var denominator = origin + along + down;
            x = (origin * TopLeft.X + along * TopRight.X + down * BottomLeft.X) / denominator;
            y = (origin * TopLeft.Y + along * TopRight.Y + down * BottomLeft.Y) / denominator;
        }

        /// <summary>The per-module grid axis vectors at grid point (<paramref name="u"/>, <paramref name="v"/>).</summary>
        public void Axes(float u, float v, int dimension, out (float X, float Y) axisX, out (float X, float Y) axisY)
        {
            Map(u - 0.5f, v, dimension, out var leftX, out var leftY);
            Map(u + 0.5f, v, dimension, out var rightX, out var rightY);
            Map(u, v - 0.5f, dimension, out var upX, out var upY);
            Map(u, v + 0.5f, dimension, out var downX, out var downY);
            axisX = (rightX - leftX, rightY - leftY);
            axisY = (downX - upX, downY - upY);
        }

        /// <summary>The grid-to-pixel transform of a symbol <paramref name="dimension"/> modules wide.</summary>
        public PerspectiveTransform Transform(int dimension)
        {
            if (IsAffine)
                return BuildParallelogramTransform(TopLeft, TopRight, BottomLeft, dimension);
            var corner = dimension - 3.5f;
            Map(corner, corner, dimension, out var cornerX, out var cornerY);
            return PerspectiveTransform.QuadrilateralToQuadrilateral(
                3.5f, 3.5f,
                corner, 3.5f,
                corner, corner,
                3.5f, corner,
                TopLeft.X, TopLeft.Y,
                TopRight.X, TopRight.Y,
                cornerX, cornerY,
                BottomLeft.X, BottomLeft.Y);
        }
    }
}
