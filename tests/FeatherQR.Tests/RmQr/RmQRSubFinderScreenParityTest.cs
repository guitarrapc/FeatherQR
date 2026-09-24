using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.RmQR;

namespace FeatherQR.Tests;

/// <summary>
/// The sub-finder search with its lattice screen against the search without it: the same answer, bit for bit, and a screen that only ever drops positions the template cannot accept.
/// </summary>
/// <remarks>
/// The reference is the search as it was before the screen, transcribed: three leans, rings outward, the row-wise early exit, the tie-break by distance and the refinement.
/// The screen samples each lattice point once where the search samples it once per position, in another float expression, so the two can land in different pixels next to a pixel edge.
/// That is what the margin is for, and the cases here put the prediction at large coordinates, where the difference is widest.
/// </remarks>
public class RmQRSubFinderScreenParityTest
{
    private const float FinderCenter = 3.5f;
    private const int SubFinderMinScore = 24;

    public static IEnumerable<int> Seeds() => Enumerable.Range(0, 24);

    [Test]
    [MethodDataSource(nameof(Seeds))]
    public async Task Locate_MatchesTheUnscreenedSearch(int seed)
    {
        var random = new Random(seed);
        var located = 0;
        for (var i = 0; i < 60; i++)
        {
            var scene = Scene.Build(random, i % 5);
            var frame = scene.Frame;
            var got = RmQRImageDecoder.TryLocateSubFinder(scene.Luminance, scene.Width, scene.Height, scene.Threshold, frame.Candidate, frame.UX, frame.UY, frame.VX, frame.VY, frame.SymbolWidth, frame.SymbolHeight, out var gotX, out var gotY);
            var want = ReferenceLocate(scene.Luminance, scene.Width, scene.Height, scene.Threshold, frame.Candidate, frame.UX, frame.UY, frame.VX, frame.VY, frame.SymbolWidth, frame.SymbolHeight, out var wantX, out var wantY);

            await Assert.That(got).IsEqualTo(want).Because($"seed {seed} case {i} ({scene.Kind})");
            await Assert.That(BitConverter.SingleToInt32Bits(gotX)).IsEqualTo(BitConverter.SingleToInt32Bits(wantX)).Because($"seed {seed} case {i} ({scene.Kind}): x {gotX} against {wantX}");
            await Assert.That(BitConverter.SingleToInt32Bits(gotY)).IsEqualTo(BitConverter.SingleToInt32Bits(wantY)).Because($"seed {seed} case {i} ({scene.Kind}): y {gotY} against {wantY}");
            if (want)
                located++;
        }

        // The rendered symbols have to be found, or the cases only compare failures
        await Assert.That(located).IsGreaterThanOrEqualTo(12).Because($"seed {seed}");
    }

    /// <summary>A position the screen drops scores under the acceptance floor when sampled the way the search samples it.</summary>
    [Test]
    [MethodDataSource(nameof(Seeds))]
    public async Task Screen_DropsOnlyPositionsTheTemplateCannotAccept(int seed)
    {
        var random = new Random(1000 + seed);
        var survivors = new ulong[RmQRImageDecoder.MaxSubFinderScreenRows];
        for (var i = 0; i < 20; i++)
        {
            var scene = Scene.Build(random, i % 5);
            var frame = scene.Frame;
            var radius = 12 + frame.SymbolWidth / 10;
            var predictedX = frame.Candidate.X + (frame.SymbolWidth - 2.5f - FinderCenter) * frame.UX + (frame.SymbolHeight - 2.5f - FinderCenter) * frame.VX;
            var predictedY = frame.Candidate.Y + (frame.SymbolWidth - 2.5f - FinderCenter) * frame.UY + (frame.SymbolHeight - 2.5f - FinderCenter) * frame.VY;
            foreach (var degrees in new[] { 0f, 12f, -12f })
            {
                var radians = degrees * (Math.PI / 180d);
                var leanCos = (float)Math.Cos(radians);
                Rotate(frame.VX / leanCos, frame.VY / leanCos, leanCos, (float)Math.Sin(radians), out var svX, out var svY);
                var screened = RmQRImageDecoder.TryScreenSubFinderPositions(scene.Luminance, scene.Width, scene.Height, scene.Threshold, predictedX, predictedY, frame.UX, frame.UY, svX, svY, radius, survivors);
                await Assert.That(screened).IsTrue().Because($"seed {seed} case {i}: every radius here fits the screen");

                for (var ov = -radius; ov <= radius; ov++)
                {
                    for (var ou = -radius; ou <= radius; ou++)
                    {
                        if ((survivors[ov + radius] >> (ou + radius) & 1) != 0)
                            continue;
                        var score = FullScore(scene.Luminance, scene.Width, scene.Height, scene.Threshold, predictedX, predictedY, frame.UX, frame.UY, svX, svY, ou, ov);
                        await Assert.That(score).IsLessThan(SubFinderMinScore).Because($"seed {seed} case {i} ({scene.Kind}) lean {degrees}: dropped ({ou}, {ov}) scores {score}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Each lattice point the classification calls certain is the pixel the search reads there, for every position and template cell that samples it.
    /// A one-pixel checkerboard far out on a wide image makes every pixel edge count, where the two float expressions differ most.
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task Lattice_CertainPointsAreThePixelsTheSearchReads(int seed)
    {
        const int Width = 32_768;
        const int Height = 256;
        var luminance = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
                luminance[y * Width + x] = ((x + y) & 1) == 0 ? (byte)0 : (byte)255;
        }

        var random = new Random(seed);
        var ifDark = new ulong[RmQRImageDecoder.MaxSubFinderScreenRows];
        var ifLight = new ulong[RmQRImageDecoder.MaxSubFinderScreenRows];
        var checkedPoints = 0L;
        var disagreements = 0;
        var firstDisagreement = "";
        for (var frame = 0; frame < 12; frame++)
        {
            var module = 1f + (float)random.NextDouble() * 4f;
            var angle = (float)(random.NextDouble() * 2 * Math.PI);
            var uX = module * MathF.Cos(angle);
            var uY = module * MathF.Sin(angle);
            var svX = -module * 1.07f * MathF.Sin(angle + 0.03f);
            var svY = module * 1.07f * MathF.Cos(angle + 0.03f);
            // Next to the right edge half the time, so the border is certain too
            var predictedX = frame % 2 == 0 ? Width - (float)random.NextDouble() * 40f : 16_384f + (float)random.NextDouble() * 16_000f;
            var predictedY = (float)random.NextDouble() * Height;
            const int Radius = 25;
            if (!RmQRImageDecoder.TryClassifySubFinderLattice(luminance, Width, Height, 128, predictedX, predictedY, uX, uY, svX, svY, Radius, ifDark, ifLight))
            {
                disagreements++;
                firstDisagreement = $"frame {frame} not classified";
                continue;
            }

            for (var ov = -Radius; ov <= Radius; ov++)
            {
                for (var ou = -Radius; ou <= Radius; ou++)
                {
                    var cx = predictedX + ou * 0.5f * uX + ov * 0.5f * svX;
                    var cy = predictedY + ou * 0.5f * uY + ov * 0.5f * svY;
                    for (var j = -2; j <= 2; j++)
                    {
                        for (var i = -2; i <= 2; i++)
                        {
                            var a = ou + 2 * i + Radius + 4;
                            var b = ov + 2 * j + Radius + 4;
                            var mismatchIfDark = (ifDark[b] >> a & 1) != 0;
                            var mismatchIfLight = (ifLight[b] >> a & 1) != 0;
                            if (!mismatchIfDark && !mismatchIfLight)
                                continue;

                            checkedPoints++;
                            var px = (int)(cx + i * uX + j * svX);
                            var py = (int)(cy + i * uY + j * svY);
                            var outside = (uint)px >= Width || (uint)py >= Height;
                            var agrees = mismatchIfDark && mismatchIfLight
                                ? outside
                                : !outside && (luminance[py * Width + px] < 128) == mismatchIfLight;
                            if (!agrees && disagreements++ == 0)
                                firstDisagreement = $"frame {frame} position ({ou}, {ov}) cell ({i}, {j}) pixel ({px}, {py})";
                        }
                    }
                }
            }
        }

        await Assert.That(disagreements).IsEqualTo(0).Because(firstDisagreement);
        await Assert.That(checkedPoints).IsGreaterThan(500_000L);
    }

    /// <summary>The vector classification against the scalar one, mask for mask, with the lattice inside, across and wholly outside the image.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task Lattice_Vector128AndScalar_AreIdentical(int seed)
    {
#if NET8_0_OR_GREATER
        if (!System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            Skip.Test("Vector128 not accelerated on this machine");
            return;
        }

        var random = new Random(seed);
        var mismatches = 0;
        var first = "";
        for (var i = 0; i < 400; i++)
        {
            var width = random.Next(8, 400);
            var height = random.Next(8, 300);
            var luminance = new byte[width * height];
            random.NextBytes(luminance);
            var module = 0.5f + (float)random.NextDouble() * 8f;
            var angle = (float)(random.NextDouble() * 2 * Math.PI);
            var uX = module * MathF.Cos(angle);
            var uY = module * MathF.Sin(angle);
            var svX = -module * 1.1f * MathF.Sin(angle - 0.2f);
            var svY = module * 1.1f * MathF.Cos(angle - 0.2f);
            var predictedX = (float)(random.NextDouble() * 2 - 0.5) * width;
            var predictedY = (float)(random.NextDouble() * 2 - 0.5) * height;
            var side = 2 * random.Next(0, 28) + 9;
            var marginX = (float)random.NextDouble() * 0.3f;
            var marginY = (float)random.NextDouble() * 0.3f;
            var threshold = (byte)random.Next(1, 256);

            var scalarDark = new ulong[RmQRImageDecoder.MaxSubFinderScreenRows];
            var scalarLight = new ulong[RmQRImageDecoder.MaxSubFinderScreenRows];
            var vectorDark = new ulong[RmQRImageDecoder.MaxSubFinderScreenRows];
            var vectorLight = new ulong[RmQRImageDecoder.MaxSubFinderScreenRows];
            RmQRImageDecoder.ClassifySubFinderLatticeScalar(luminance, width, height, threshold, predictedX, predictedY, uX, uY, svX, svY, side, marginX, marginY, scalarDark, scalarLight);
            RmQRImageDecoder.ClassifySubFinderLatticeVector128(luminance, width, height, threshold, predictedX, predictedY, uX, uY, svX, svY, side, marginX, marginY, vectorDark, vectorLight);
            if (!scalarDark.AsSpan().SequenceEqual(vectorDark) || !scalarLight.AsSpan().SequenceEqual(vectorLight))
            {
                if (mismatches++ == 0)
                    first = $"case {i}: {width}x{height}, side {side}, prediction ({predictedX}, {predictedY})";
            }
        }

        await Assert.That(mismatches).IsEqualTo(0).Because(first);
#else
        await Task.CompletedTask;
#endif
    }

    /// <summary>On noise the screen leaves few positions to score, which is all it is for.</summary>
    [Test]
    public async Task Screen_DropsMostPositionsOnNoise()
    {
        var random = new Random(7);
        var width = 400;
        var height = 300;
        var luminance = new byte[width * height];
        random.NextBytes(luminance);
        var survivors = new ulong[RmQRImageDecoder.MaxSubFinderScreenRows];
        const int Radius = 25;
        var screened = RmQRImageDecoder.TryScreenSubFinderPositions(luminance, width, height, 128, 200.3f, 150.7f, 3.1f, 0.4f, -0.4f, 3.1f, Radius, survivors);
        await Assert.That(screened).IsTrue();

        var kept = 0;
        for (var row = 0; row <= 2 * Radius; row++)
            kept += System.Numerics.BitOperations.PopCount(survivors[row]);
        var positions = (2 * Radius + 1) * (2 * Radius + 1);
        await Assert.That(kept * 20).IsLessThan(positions).Because($"{kept} of {positions} positions kept");
    }

    /// <summary>A radius whose lattice is wider than a mask is not screened, and the search scores every position.</summary>
    [Test]
    public async Task Screen_RefusesARadiusWiderThanAMask()
    {
        var luminance = new byte[100 * 100];
        var survivors = new ulong[RmQRImageDecoder.MaxSubFinderScreenRows];
        var screened = RmQRImageDecoder.TryScreenSubFinderPositions(luminance, 100, 100, 128, 50f, 50f, 1f, 0f, 0f, 1f, 28, survivors);
        await Assert.That(screened).IsFalse();
    }

    /// <summary>Every sample of a position, no early exit: the score the template gives it.</summary>
    private static int FullScore(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float predictedX, float predictedY, float uX, float uY, float svX, float svY, int ou, int ov)
    {
        var offU = ou * 0.5f;
        var offV = ov * 0.5f;
        var cx = predictedX + offU * uX + offV * svX;
        var cy = predictedY + offU * uY + offV * svY;
        var score = 0;
        for (var j = -2; j <= 2; j++)
        {
            for (var i = -2; i <= 2; i++)
            {
                var expectedDark = i == -2 || i == 2 || j == -2 || j == 2 || (i == 0 && j == 0);
                var px = (int)(cx + i * uX + j * svX);
                var py = (int)(cy + i * uY + j * svY);
                if ((uint)px >= (uint)width || (uint)py >= (uint)height)
                    continue;
                if (luminance[py * width + px] < threshold == expectedDark)
                    score++;
            }
        }
        return score;
    }

    /// <summary>The search before the screen, transcribed.</summary>
    private static bool ReferenceLocate(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in FinderPattern candidate, float uX, float uY, float vX, float vY, int symbolWidth, int symbolHeight, out float centerX, out float centerY)
    {
        var dX = symbolWidth - 2.5f - FinderCenter;
        var dY = symbolHeight - 2.5f - FinderCenter;
        var predictedX = candidate.X + dX * uX + dY * vX;
        var predictedY = candidate.Y + dX * uY + dY * vY;
        var radius = 12 + symbolWidth / 10 * 1;

        var reach = radius * 0.5f + 2f;
        var extentX = reach * (Math.Abs(uX) + Math.Abs(vX) + Math.Abs(vY)) + 1f;
        var extentY = reach * (Math.Abs(uY) + Math.Abs(vX) + Math.Abs(vY)) + 1f;
        if (predictedX + extentX < 0f || predictedX - extentX >= width
            || predictedY + extentY < 0f || predictedY - extentY >= height)
        {
            centerX = 0f;
            centerY = 0f;
            return false;
        }

        var bestScore = -1;
        var bestDistance = int.MaxValue;
        var bestX = 0f;
        var bestY = 0f;
        var bestVX = vX;
        var bestVY = vY;

        foreach (var degrees in new[] { 0f, 12f, -12f })
        {
            var radians = degrees * (Math.PI / 180d);
            var leanCos = (float)Math.Cos(radians);
            Rotate(vX / leanCos, vY / leanCos, leanCos, (float)Math.Sin(radians), out var svX, out var svY);
            for (var ring = 0; ring <= radius && bestScore < 25; ring++)
            {
                for (var ov = -ring; ov <= ring; ov++)
                {
                    var offV = ov * 0.5f;
                    var onVerticalEdge = ov == -ring || ov == ring;
                    for (var ou = -ring; ou <= ring; ou++)
                    {
                        if (!onVerticalEdge && ou != -ring && ou != ring)
                            continue;

                        var offU = ou * 0.5f;
                        var cx = predictedX + offU * uX + offV * svX;
                        var cy = predictedY + offU * uY + offV * svY;
                        var score = 0;
                        var remaining = 25;
                        for (var j = -2; j <= 2; j++)
                        {
                            for (var i = -2; i <= 2; i++)
                            {
                                var expectedDark = i == -2 || i == 2 || j == -2 || j == 2 || (i == 0 && j == 0);
                                var px = (int)(cx + i * uX + j * svX);
                                var py = (int)(cy + i * uY + j * svY);
                                if ((uint)px >= (uint)width || (uint)py >= (uint)height)
                                    continue;
                                var dark = luminance[py * width + px] < threshold;
                                if (dark == expectedDark)
                                    score++;
                            }
                            remaining -= 5;
                            if (score + remaining < SubFinderMinScore)
                                break;
                        }

                        var distance = ou * ou + ov * ov;
                        if (score > bestScore || (score == bestScore && distance < bestDistance))
                        {
                            bestScore = score;
                            bestDistance = distance;
                            bestX = cx;
                            bestY = cy;
                            bestVX = svX;
                            bestVY = svY;
                        }
                    }
                }
            }

            if (bestScore == 25)
                break;
        }

        if (bestScore < SubFinderMinScore)
        {
            centerX = 0f;
            centerY = 0f;
            return false;
        }

        centerX = bestX;
        centerY = bestY;
        var uLength = (float)Math.Sqrt(uX * uX + uY * uY);
        var vLength = (float)Math.Sqrt(bestVX * bestVX + bestVY * bestVY);
        if (uLength > 0f && vLength > 0f)
        {
            RefineAlongAxis(luminance, width, height, threshold, ref centerX, ref centerY, uX / uLength, uY / uLength, uLength);
            RefineAlongAxis(luminance, width, height, threshold, ref centerX, ref centerY, bestVX / vLength, bestVY / vLength, vLength);
        }
        return true;
    }

    private static void RefineAlongAxis(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, ref float x, ref float y, float dirX, float dirY, float moduleLength)
    {
        var maxRun = moduleLength * 1.6f;
        var forward = DarkRun(luminance, width, height, threshold, x, y, dirX, dirY, maxRun);
        var backward = DarkRun(luminance, width, height, threshold, x, y, -dirX, -dirY, maxRun);
        if (float.IsNaN(forward) || float.IsNaN(backward))
            return;
        var shift = (forward - backward) / 2f;
        x += dirX * shift;
        y += dirY * shift;
    }

    private static float DarkRun(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, float startX, float startY, float dirX, float dirY, float maxRun)
    {
        for (var step = 0.5f; step <= maxRun; step += 0.5f)
        {
            var px = (int)(startX + dirX * step);
            var py = (int)(startY + dirY * step);
            if ((uint)px >= (uint)width || (uint)py >= (uint)height)
                return float.NaN;
            if (luminance[py * width + px] >= threshold)
                return step - 0.25f;
        }
        return float.NaN;
    }

    private static void Rotate(float x, float y, float cos, float sin, out float rx, out float ry)
    {
        rx = cos * x - sin * y;
        ry = sin * x + cos * y;
    }

    private readonly record struct Frame(FinderPattern Candidate, float UX, float UY, float VX, float VY, int SymbolWidth, int SymbolHeight);

    private sealed record Scene(string Kind, byte[] Luminance, int Width, int Height, byte Threshold, Frame Frame)
    {
        private static readonly RmQRVersion[] Versions = Enum.GetValues<RmQRVersion>();

        /// <summary>
        /// Kinds: 0 a symbol turned and keystoned a little, framed near its finder; 1 the same far out along a wide image, where coordinates are large;
        /// 2 two-level noise and 3 8-bit noise, framed anywhere; 4 a symbol with a frame a module or two off.
        /// </summary>
        public static Scene Build(Random random, int kind)
        {
            var version = Versions[random.Next(Versions.Length)];
            if (kind is 2 or 3)
            {
                var width = random.Next(40, 700);
                var height = random.Next(30, 500);
                var luminance = new byte[width * height];
                random.NextBytes(luminance);
                if (kind == 2)
                {
                    for (var p = 0; p < luminance.Length; p++)
                        luminance[p] = luminance[p] < 128 ? (byte)0 : (byte)255;
                }
                var module = 1f + (float)random.NextDouble() * 8f;
                var angle = (float)(random.NextDouble() * 2 * Math.PI);
                var aspect = 0.85f + (float)random.NextDouble() * 0.3f;
                var candidate = new FinderPattern { X = (float)random.NextDouble() * width, Y = (float)random.NextDouble() * height, ModuleSize = module, Count = 3 };
                var frame = new Frame(candidate, module * MathF.Cos(angle), module * MathF.Sin(angle), -module * aspect * MathF.Sin(angle), module * aspect * MathF.Cos(angle), RmQRConstants.GetWidth(version), RmQRConstants.GetHeight(version));
                return new Scene(kind == 2 ? "two-level noise" : "8-bit noise", luminance, width, height, 128, frame);
            }

            var data = RmQRCodeGenerator.Create("12345", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });
            var ppm = kind == 1 ? 1.5f + (float)random.NextDouble() * 1.5f : 1.5f + (float)random.NextDouble() * 6f;
            var turn = kind == 1 ? (float)(random.NextDouble() - 0.5) * 0.1f : (float)(random.NextDouble() * 2 * Math.PI);
            var keystone = (float)(random.NextDouble() - 0.5) * 0.08f;
            var cos = MathF.Cos(turn);
            var sin = MathF.Sin(turn);
            var span = (data.Width + 8) * ppm;
            var imageWidth = kind == 1 ? 10_000 + random.Next(10_000) : (int)(span * 1.2f) + random.Next(20);
            var imageHeight = (int)(span * (kind == 1 ? 0.2f : 1.2f) + (data.Height + 8) * ppm) + random.Next(10);
            // Symbol centre in the image; grid point (gx, gy) maps to centre + R·(gx - w/2, gy - h/2)·ppm with a keystone on the row axis
            var centreX = kind == 1 ? imageWidth - span * 0.6f - random.Next(200) + 0.37f : imageWidth / 2f + (float)random.NextDouble();
            var centreY = imageHeight / 2f + (float)random.NextDouble();

            void ToImage(float gx, float gy, out float x, out float y)
            {
                var lx = (gx - data.Width / 2f) * ppm * (1f + keystone * (gy - data.Height / 2f) / data.Height);
                var ly = (gy - data.Height / 2f) * ppm;
                x = centreX + cos * lx - sin * ly;
                y = centreY + sin * lx + cos * ly;
            }

            var lum = new byte[imageWidth * imageHeight];
            Array.Fill(lum, (byte)220);
            // Inverse map per pixel near the symbol only
            ToImage(0f, 0f, out var x0, out var y0);
            ToImage(data.Width, 0f, out var x1, out var y1);
            ToImage(data.Width, data.Height, out var x2, out var y2);
            ToImage(0f, data.Height, out var x3, out var y3);
            var minX = Math.Max(0, (int)Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)) - 2);
            var maxX = Math.Min(imageWidth - 1, (int)Math.Max(Math.Max(x0, x1), Math.Max(x2, x3)) + 2);
            var minY = Math.Max(0, (int)Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)) - 2);
            var maxY = Math.Min(imageHeight - 1, (int)Math.Max(Math.Max(y0, y1), Math.Max(y2, y3)) + 2);
            for (var py = minY; py <= maxY; py++)
            {
                for (var px = minX; px <= maxX; px++)
                {
                    var dx = px + 0.5f - centreX;
                    var dy = py + 0.5f - centreY;
                    var lx = cos * dx + sin * dy;
                    var ly = -sin * dx + cos * dy;
                    var gy = ly / ppm + data.Height / 2f;
                    var gx = lx / (ppm * (1f + keystone * (gy - data.Height / 2f) / data.Height)) + data.Width / 2f;
                    if (gx >= 0 && gy >= 0 && gx < data.Width && gy < data.Height && data[(int)gy, (int)gx])
                        lum[py * imageWidth + px] = 30;
                }
            }

            ToImage(FinderCenter, FinderCenter, out var fx, out var fy);
            var jitter = kind == 4 ? 2.5f * ppm : 0.4f;
            var scale = kind == 4 ? 0.97f + (float)random.NextDouble() * 0.06f : 0.995f + (float)random.NextDouble() * 0.01f;
            var skew = (float)(random.NextDouble() - 0.5) * 0.02f;
            var frameCandidate = new FinderPattern
            {
                X = fx + (float)(random.NextDouble() - 0.5) * 2 * jitter,
                Y = fy + (float)(random.NextDouble() - 0.5) * 2 * jitter,
                ModuleSize = ppm,
                Count = 5,
            };
            var m = ppm * scale;
            var result = new Frame(frameCandidate, m * MathF.Cos(turn), m * MathF.Sin(turn), -m * MathF.Sin(turn + skew), m * MathF.Cos(turn + skew), data.Width, data.Height);
            return new Scene(kind switch { 1 => "far out on a wide image", 4 => "framed off", _ => "turned symbol" }, lum, imageWidth, imageHeight, 128, result);
        }
    }
}
