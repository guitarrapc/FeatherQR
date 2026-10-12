namespace FeatherQR.Tests;

/// <summary>
/// Photographs a symbol printed on a card through a pinhole camera.
/// The card is tilted about any axis in its own plane and turned in the image, can be bent round a cylinder whose axis runs along the symbol's columns, and is seen through a lens with radial distortion.
/// Each pixel averages 3 × 3 rays, so edges come out grey, and the image is then blurred and given sensor noise.
/// The canvas holds the card with a small margin, or a larger scene of clutter round it.
/// </summary>
internal static class CameraRenderer
{
    /// <summary>How a photograph is taken.</summary>
    /// <param name="PixelsPerModule">The module size at the symbol's centre, in pixels.</param>
    /// <param name="TiltDegrees">The angle between the card and the image plane.</param>
    /// <param name="TiltAxisDegrees">The direction, in the card, of the axis it is tilted about. 0 runs along the symbol's rows.</param>
    /// <param name="RollDegrees">The turn of the whole image.</param>
    /// <param name="BowModules">How far the card's left and right edges stand off the plane through its centre column, in modules. Positive bends them toward the camera.</param>
    /// <param name="Distortion">Radial distortion where a front-on photograph puts the symbol's corners: −0.1 draws them 10 % nearer the centre than a pinhole would (barrel).</param>
    /// <param name="BlurSigma">Gaussian blur, in pixels.</param>
    /// <param name="NoiseSigma">Gaussian sensor noise, in grey levels.</param>
    /// <param name="SceneScale">The canvas side over the card's. Up to 1 draws the card with a small margin on a plain table, more draws a scene of clutter round it.</param>
    /// <param name="Distance">The camera's distance from the symbol's centre, in symbol sides. Nearer draws stronger perspective.</param>
    /// <param name="Seed">Seeds the noise, the scene and the card's place in it.</param>
    internal readonly record struct Shot(
        float PixelsPerModule,
        float TiltDegrees = 0f,
        float TiltAxisDegrees = 0f,
        float RollDegrees = 0f,
        float BowModules = 0f,
        float Distortion = 0f,
        float BlurSigma = 0f,
        float NoiseSigma = 0f,
        float SceneScale = 1f,
        float Distance = 2.5f,
        int Seed = 0);

    private const float Ink = 35f;
    private const float Paper = 225f;
    private const float Table = 90f;

    /// <summary>Paper beyond the quiet zone, in modules.</summary>
    private const float CardMargin = 1.5f;

    /// <summary>The longest side a scene is drawn at, so a large symbol in a scene stays a size a sweep can afford.</summary>
    private const int MaxSceneSide = 1400;

    /// <summary>A photograph and where it drew the symbol.</summary>
    internal sealed class Photograph
    {
        private readonly Camera camera;
        private readonly double offsetX;
        private readonly double offsetY;

        internal Photograph(byte[] luminance, int width, int height, Camera camera, double offsetX, double offsetY, int columns, int rows)
        {
            Luminance = luminance;
            Width = width;
            Height = height;
            this.camera = camera;
            this.offsetX = offsetX;
            this.offsetY = offsetY;
            Corners = [ToPixel(0, 0), ToPixel(columns, 0), ToPixel(columns, rows), ToPixel(0, rows)];
        }

        public byte[] Luminance { get; }

        public int Width { get; }

        public int Height { get; }

        /// <summary>The corners of the module area (quiet zone excluded) in the symbol's own order: top-left, top-right, bottom-right, bottom-left.</summary>
        public (float X, float Y)[] Corners { get; }

        /// <summary>Where module coordinates (<paramref name="u"/>, <paramref name="v"/>) of the symbol, quiet zone excluded, land in continuous image coordinates.</summary>
        public (float X, float Y) ToPixel(double u, double v)
        {
            var (x, y) = camera.Project(u, v);
            return ((float)(x + offsetX), (float)(y + offsetY));
        }
    }

    /// <param name="isDark">Module colours of the symbol, quiet zone excluded.</param>
    /// <param name="columns">Modules across, quiet zone excluded.</param>
    /// <param name="rows">Modules down, quiet zone excluded.</param>
    /// <param name="quietZone">Light modules round the symbol, part of the card.</param>
    public static Photograph Render(Func<int, int, bool> isDark, int columns, int rows, int quietZone, Shot shot)
    {
        var camera = new Camera(columns, rows, quietZone, shot);
        var (minX, minY, maxX, maxY) = camera.CardBounds();
        var random = new Random(shot.Seed);

        int width, height;
        double offsetX, offsetY;
        float[]? scene = null;
        if (shot.SceneScale <= 1f)
        {
            var margin = 0.06 * Math.Max(maxX - minX, maxY - minY) + 4;
            width = (int)Math.Ceiling(maxX - minX + 2 * margin);
            height = (int)Math.Ceiling(maxY - minY + 2 * margin);
            offsetX = margin - minX;
            offsetY = margin - minY;
        }
        else
        {
            var scale = Math.Min(shot.SceneScale, MaxSceneSide / Math.Max(maxX - minX, maxY - minY));
            width = (int)Math.Ceiling((maxX - minX) * Math.Max(scale, 1.0));
            height = (int)Math.Ceiling((maxY - minY) * Math.Max(scale, 1.0));
            offsetX = random.NextDouble() * (width - (maxX - minX)) - minX;
            offsetY = random.NextDouble() * (height - (maxY - minY)) - minY;
            scene = Scene(width, height, random);
        }

        var image = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var background = scene?[y * width + x] ?? Table;
                var sum = 0f;
                for (var sy = 0; sy < 3; sy++)
                {
                    for (var sx = 0; sx < 3; sx++)
                    {
                        var px = x + (sx + 0.5) / 3 - offsetX;
                        var py = y + (sy + 0.5) / 3 - offsetY;
                        sum += camera.Trace(px, py, isDark) switch
                        {
                            Surface.Ink => Ink,
                            Surface.Paper => Paper,
                            _ => background,
                        };
                    }
                }
                image[y * width + x] = sum / 9f;
            }
        }

        if (shot.BlurSigma > 0f)
            Blur(image, width, height, shot.BlurSigma);

        var luminance = new byte[width * height];
        for (var i = 0; i < luminance.Length; i++)
        {
            var value = image[i];
            if (shot.NoiseSigma > 0f)
                value += shot.NoiseSigma * Gaussian(random);
            luminance[i] = (byte)Math.Clamp(MathF.Round(value), 0f, 255f);
        }
        return new Photograph(luminance, width, height, camera, offsetX, offsetY, columns, rows);
    }

    internal enum Surface
    {
        Miss,
        Paper,
        Ink,
    }

    /// <summary>
    /// The card in its own frame (modules, the module area's centre at the origin, z toward the scene) turned into the camera's (x right, y down, z forward from the lens at z = −distance).
    /// Image coordinates are relative to the principal point.
    /// </summary>
    internal sealed class Camera
    {
        private readonly int columns;
        private readonly int rows;
        private readonly double halfWidth;
        private readonly double halfHeight;
        private readonly double cardHalfWidth;
        private readonly double cardHalfHeight;
        private readonly double distance;
        private readonly double focal;
        private readonly double distortion;
        private readonly double normalisingRadius;
        private readonly double[] rotation;
        private readonly double bowSign;
        private readonly double bowRadius;

        public Camera(int columns, int rows, int quietZone, Shot shot)
        {
            this.columns = columns;
            this.rows = rows;
            halfWidth = columns / 2.0;
            halfHeight = rows / 2.0;
            cardHalfWidth = halfWidth + quietZone + CardMargin;
            cardHalfHeight = halfHeight + quietZone + CardMargin;
            distance = shot.Distance * Math.Max(columns, rows);
            focal = shot.PixelsPerModule * distance;
            distortion = shot.Distortion;
            normalisingRadius = focal * Math.Sqrt(halfWidth * halfWidth + halfHeight * halfHeight) / distance;

            // Tilt about an axis in the card, then roll about the optical axis
            var tilt = shot.TiltDegrees * Math.PI / 180.0;
            var axis = shot.TiltAxisDegrees * Math.PI / 180.0;
            double ax = Math.Cos(axis), ay = Math.Sin(axis), c = Math.Cos(tilt), s = Math.Sin(tilt), t = 1 - c;
            double[] tilted =
            [
                c + ax * ax * t, ax * ay * t, ay * s,
                ax * ay * t, c + ay * ay * t, -ax * s,
                -ay * s, ax * s, c,
            ];
            var roll = shot.RollDegrees * Math.PI / 180.0;
            double rc = Math.Cos(roll), rs = Math.Sin(roll);
            rotation = new double[9];
            for (var j = 0; j < 3; j++)
            {
                rotation[j] = rc * tilted[j] - rs * tilted[3 + j];
                rotation[3 + j] = rs * tilted[j] + rc * tilted[3 + j];
                rotation[6 + j] = tilted[6 + j];
            }

            // The sagitta h at the card's edges fixes the cylinder's radius: (w² + h²) / 2h
            if (shot.BowModules != 0f)
            {
                var h = Math.Abs(shot.BowModules);
                bowSign = Math.Sign(shot.BowModules);
                bowRadius = (cardHalfWidth * cardHalfWidth + h * h) / (2 * h);
            }
        }

        /// <summary>Where the card point at module coordinates (<paramref name="u"/>, <paramref name="v"/>) is drawn.</summary>
        public (double X, double Y) Project(double u, double v)
        {
            var (px, py, pz) = SurfacePoint(u - halfWidth, v - halfHeight);
            var wx = rotation[0] * px + rotation[1] * py + rotation[2] * pz;
            var wy = rotation[3] * px + rotation[4] * py + rotation[5] * pz;
            var wz = rotation[6] * px + rotation[7] * py + rotation[8] * pz + distance;
            var x = focal * wx / wz;
            var y = focal * wy / wz;
            if (distortion == 0)
                return (x, y);
            var r2 = (x * x + y * y) / (normalisingRadius * normalisingRadius);
            var scale = 1 + distortion * r2;
            return (x * scale, y * scale);
        }

        /// <summary>The bounds of the card's outline, sampled along its edges, which the bow and the lens can curve.</summary>
        public (double MinX, double MinY, double MaxX, double MaxY) CardBounds()
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            const int steps = 64;
            double u0 = halfWidth - cardHalfWidth, u1 = halfWidth + cardHalfWidth, v0 = halfHeight - cardHalfHeight, v1 = halfHeight + cardHalfHeight;
            for (var i = 0; i <= steps; i++)
            {
                var f = (double)i / steps;
                foreach (var (u, v) in new[] { (u0 + f * (u1 - u0), v0), (u0 + f * (u1 - u0), v1), (u0, v0 + f * (v1 - v0)), (u1, v0 + f * (v1 - v0)) })
                {
                    var (x, y) = Project(u, v);
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
            return (minX, minY, maxX, maxY);
        }

        /// <summary>What the ray through image point (<paramref name="x"/>, <paramref name="y"/>) meets first.</summary>
        public Surface Trace(double x, double y, Func<int, int, bool> isDark)
        {
            if (distortion != 0 && !Undistort(ref x, ref y))
                return Surface.Miss;

            // The ray from the lens, in the card's frame: the inverse of a rotation is its transpose
            double dx = x / focal, dy = y / focal, dz = 1;
            var ox = rotation[6] * -distance;
            var oy = rotation[7] * -distance;
            var oz = rotation[8] * -distance;
            var rx = rotation[0] * dx + rotation[3] * dy + rotation[6] * dz;
            var ry = rotation[1] * dx + rotation[4] * dy + rotation[7] * dz;
            var rz = rotation[2] * dx + rotation[5] * dy + rotation[8] * dz;

            double u, v;
            if (bowRadius == 0)
            {
                if (Math.Abs(rz) < 1e-12)
                    return Surface.Miss;
                var t = -oz / rz;
                if (t <= 0)
                    return Surface.Miss;
                u = ox + t * rx;
                v = oy + t * ry;
            }
            else if (!HitCylinder(ox, oy, oz, rx, ry, rz, out u, out v))
            {
                return Surface.Miss;
            }

            if (Math.Abs(u) > cardHalfWidth || Math.Abs(v) > cardHalfHeight)
                return Surface.Miss;
            u += halfWidth;
            v += halfHeight;
            if (u < 0 || v < 0 || u >= columns || v >= rows)
                return Surface.Paper;
            return isDark((int)v, (int)u) ? Surface.Ink : Surface.Paper;
        }

        /// <summary>The card bent round a cylinder along its columns: arc length <paramref name="s"/> across, <paramref name="t"/> down.</summary>
        private (double X, double Y, double Z) SurfacePoint(double s, double t)
        {
            if (bowRadius == 0)
                return (s, t, 0);
            var angle = s / bowRadius;
            return (bowRadius * Math.Sin(angle), t, -bowSign * bowRadius * (1 - Math.Cos(angle)));
        }

        /// <summary>The nearest point of the bent card on the ray, as arc length across and distance down from the module area's centre.</summary>
        private bool HitCylinder(double ox, double oy, double oz, double rx, double ry, double rz, out double s, out double t)
        {
            // x² + (z + sign·R)² = R², the card on the sheet where cos θ > 0
            var centre = bowSign * bowRadius;
            var a = rx * rx + rz * rz;
            var b = 2 * (ox * rx + (oz + centre) * rz);
            var c = ox * ox + (oz + centre) * (oz + centre) - bowRadius * bowRadius;
            var discriminant = b * b - 4 * a * c;
            s = t = 0;
            if (a < 1e-12 || discriminant < 0)
                return false;
            var root = Math.Sqrt(discriminant);
            return OnCard((-b - root) / (2 * a), out s, out t) || OnCard((-b + root) / (2 * a), out s, out t);

            bool OnCard(double k, out double arc, out double down)
            {
                arc = down = 0;
                if (k <= 0)
                    return false;
                var x = ox + k * rx;
                var z = oz + k * rz;
                var cos = (z + centre) / centre;
                if (cos <= 0)
                    return false;
                arc = bowRadius * Math.Atan2(x / bowRadius, cos);
                down = oy + k * ry;
                return Math.Abs(arc) <= cardHalfWidth;
            }
        }

        /// <summary>The pinhole point the lens drew at (<paramref name="x"/>, <paramref name="y"/>), or false past the radius the lens can draw.</summary>
        private bool Undistort(ref double x, ref double y)
        {
            var rd = Math.Sqrt(x * x + y * y) / normalisingRadius;
            if (rd == 0)
                return true;
            var ru = rd;
            for (var i = 0; i < 12; i++)
            {
                var slope = 1 + 3 * distortion * ru * ru;
                if (slope <= 0)
                    return false;
                ru -= (ru + distortion * ru * ru * ru - rd) / slope;
            }
            if (Math.Abs(ru + distortion * ru * ru * ru - rd) > 1e-6)
                return false;
            x *= ru / rd;
            y *= ru / rd;
            return true;
        }
    }

    /// <summary>A table under the card with things on it: a gradient, then rectangles and lines of random greys.</summary>
    private static float[] Scene(int width, int height, Random random)
    {
        var scene = new float[width * height];
        float from = 60 + random.Next(80), to = 60 + random.Next(120);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                scene[y * width + x] = from + (to - from) * (x + y) / (width + height);
        }
        var side = Math.Min(width, height);
        for (var n = 0; n < 60; n++)
        {
            var w = (int)(side * (0.02 + 0.2 * random.NextDouble()));
            var h = (int)(side * (0.02 + 0.2 * random.NextDouble()));
            int x0 = random.Next(width), y0 = random.Next(height);
            float grey = 30 + random.Next(211);
            for (var y = y0; y < Math.Min(height, y0 + h); y++)
            {
                for (var x = x0; x < Math.Min(width, x0 + w); x++)
                    scene[y * width + x] = grey;
            }
        }
        for (var n = 0; n < 30; n++)
        {
            double x = random.Next(width), y = random.Next(height), angle = random.NextDouble() * Math.PI;
            var length = side * (0.1 + 0.4 * random.NextDouble());
            var thickness = 1 + random.Next(4);
            float grey = 20 + random.Next(221);
            for (var k = 0; k < length; k++)
            {
                for (var d = 0; d < thickness; d++)
                {
                    var px = (int)(x + k * Math.Cos(angle) - d * Math.Sin(angle));
                    var py = (int)(y + k * Math.Sin(angle) + d * Math.Cos(angle));
                    if (px >= 0 && py >= 0 && px < width && py < height)
                        scene[py * width + px] = grey;
                }
            }
        }
        return scene;
    }

    private static void Blur(float[] image, int width, int height, float sigma)
    {
        var radius = (int)Math.Ceiling(3 * sigma);
        var kernel = new float[2 * radius + 1];
        var total = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            kernel[i + radius] = MathF.Exp(-i * i / (2 * sigma * sigma));
            total += kernel[i + radius];
        }
        for (var i = 0; i < kernel.Length; i++)
            kernel[i] /= total;

        var pass = new float[image.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0f;
                for (var i = -radius; i <= radius; i++)
                    sum += kernel[i + radius] * image[y * width + Math.Clamp(x + i, 0, width - 1)];
                pass[y * width + x] = sum;
            }
        }
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0f;
                for (var i = -radius; i <= radius; i++)
                    sum += kernel[i + radius] * pass[Math.Clamp(y + i, 0, height - 1) * width + x];
                image[y * width + x] = sum;
            }
        }
    }

    private static float Gaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
    }
}
