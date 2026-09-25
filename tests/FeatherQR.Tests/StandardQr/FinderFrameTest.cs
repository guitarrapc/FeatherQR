using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The frame the three finder centres and the module sizes along the two finder lines determine: the plane itself when the symbol is a plane in perspective, the parallelogram when the sizes cannot tell perspective from measurement, and refused when no plane in front of the camera gives those sizes.
/// </summary>
public class FinderFrameTest
{
    private const float PixelsPerModule = 4f;

    public static IEnumerable<(int, float, float)> Planes()
    {
        foreach (var dimension in new[] { 21, 57, 177 })
        {
            foreach (var (keystone, degrees) in new[] { (0.1f, 0f), (0.2f, 37f), (0.3f, 200f), (-0.2f, 290f) })
                yield return (dimension, keystone, degrees);
        }
    }

    /// <summary>The frame through the centres and their sizes is the plane's own map, where the parallelogram of the centres is a module or more off.</summary>
    [Test]
    [MethodDataSource(nameof(Planes))]
    public async Task Frame_OfAPlaneInPerspective_MapsEveryModuleCentreWhereThePlaneDoes(int dimension, float keystone, float degrees)
    {
        var plane = Plane(dimension, keystone, degrees);
        var (topLeft, topRight, bottomLeft) = Centres(plane, dimension);
        var sizes = new QRImageDecoder.FinderModuleSizes(
            SizeAlong(plane, 3.5f, 3.5f, 1f, 0f),
            SizeAlong(plane, 3.5f, 3.5f, 0f, 1f),
            SizeAlong(plane, dimension - 3.5f, 3.5f, 1f, 0f),
            SizeAlong(plane, 3.5f, dimension - 3.5f, 0f, 1f),
            PixelsPerModule,
            SubPixelAlongU: true, SubPixelAlongV: true);

        var frame = QRImageDecoder.FinderFrame.Create(topLeft, topRight, bottomLeft, sizes);
        var transform = frame.Transform(dimension);
        var parallelogram = QRImageDecoder.BuildParallelogramTransform(topLeft, topRight, bottomLeft, dimension);

        var worstFrame = 0f;
        var worstTransform = 0f;
        var worstParallelogram = 0f;
        for (var v = 0; v < dimension; v++)
        {
            for (var u = 0; u < dimension; u++)
            {
                plane.Transform(u + 0.5f, v + 0.5f, out var x, out var y);
                frame.Map(u + 0.5f, v + 0.5f, dimension, out var frameX, out var frameY);
                transform.Transform(u + 0.5f, v + 0.5f, out var transformX, out var transformY);
                parallelogram.Transform(u + 0.5f, v + 0.5f, out var parallelogramX, out var parallelogramY);
                worstFrame = Math.Max(worstFrame, Distance(x, y, frameX, frameY));
                worstTransform = Math.Max(worstTransform, Distance(x, y, transformX, transformY));
                worstParallelogram = Math.Max(worstParallelogram, Distance(x, y, parallelogramX, parallelogramY));
            }
        }

        await Assert.That(worstParallelogram).IsGreaterThan(PixelsPerModule).Because("the plane has to be one the parallelogram does not fit");
        await Assert.That(worstFrame).IsLessThan(0.01f * PixelsPerModule);
        await Assert.That(worstTransform).IsLessThan(0.01f * PixelsPerModule);
    }

    [Test]
    public async Task Frame_OfEqualSizes_IsTheParallelogramBitForBit()
    {
        var plane = Plane(57, 0f, 23f);
        var (topLeft, topRight, bottomLeft) = Centres(plane, 57);
        var sizes = new QRImageDecoder.FinderModuleSizes(4f, 4f, 4f, 4f, 4f, SubPixelAlongU: true, SubPixelAlongV: true);

        var frame = QRImageDecoder.FinderFrame.Create(topLeft, topRight, bottomLeft, sizes);
        var transform = frame.Transform(57);
        var parallelogram = QRImageDecoder.BuildParallelogramTransform(topLeft, topRight, bottomLeft, 57);

        await Assert.That(frame.IsAffine).IsTrue();
        float[] actual = [transform.a11, transform.a12, transform.a13, transform.a21, transform.a22, transform.a23, transform.a31, transform.a32, transform.a33];
        float[] expected = [parallelogram.a11, parallelogram.a12, parallelogram.a13, parallelogram.a21, parallelogram.a22, parallelogram.a23, parallelogram.a31, parallelogram.a32, parallelogram.a33];
        await Assert.That(actual).IsEquivalentTo(expected);
    }

    /// <summary>
    /// Weights of 0.55 on both lines put the vanishing line across the far corner of a version 1 symbol, which no plane in front of the camera does; 0.62 keeps it outside.
    /// </summary>
    [Test]
    [Arguments(0.55f, true)]
    [Arguments(0.62f, false)]
    public async Task Frame_SizesPuttingTheHorizonAcrossTheSymbol_AreTheParallelogram(float weight, bool refused)
    {
        var plane = Plane(21, 0f, 0f);
        var (topLeft, topRight, bottomLeft) = Centres(plane, 21);
        var far = 4f / (weight * weight);
        var sizes = new QRImageDecoder.FinderModuleSizes(4f, 4f, far, far, 4f, SubPixelAlongU: true, SubPixelAlongV: true);

        var frame = QRImageDecoder.FinderFrame.Create(topLeft, topRight, bottomLeft, sizes);

        await Assert.That(frame.IsAffine).IsEqualTo(refused);
    }

    /// <summary>Whole-pixel runs give sizes in twelfths of a pixel, each within two twelfths, so ends that differ by up to four twelfths may be one size.</summary>
    [Test]
    [Arguments(52f / 12f, true)]
    [Arguments(53f / 12f, false)]
    public async Task Frame_WholePixelRunsWithinTheirResolution_AreTheParallelogram(float far, bool flat)
    {
        var plane = Plane(57, 0f, 0f);
        var (topLeft, topRight, bottomLeft) = Centres(plane, 57);
        var sizes = new QRImageDecoder.FinderModuleSizes(4f, 4f, far, 4f, 4f, SubPixelAlongU: false, SubPixelAlongV: false);

        var frame = QRImageDecoder.FinderFrame.Create(topLeft, topRight, bottomLeft, sizes);

        await Assert.That(frame.IsAffine).IsEqualTo(flat);
    }

    /// <summary>
    /// Under a strong keystone the parallelogram of the centres puts the timing lines off the timing patterns toward the far finders, and the frame keeps them on: the dimension the timing modules name is found through the frame alone.
    /// The finders are placed where the render put them, so only the frame is under test.
    /// </summary>
    [Test]
    [Arguments(20, 0.3f, 0f, 4f)]
    [Arguments(36, 0.4f, 110f, 3f)]
    [Arguments(40, 0.45f, 200f, 4f)]
    public async Task TimingMatch_ThroughTheFrame_NamesTheDimensionTheParallelogramMisses(int version, float keystone, float degrees, float pixelsPerModule)
    {
        var (luminance, width, height, dimension, frame, parallelogram, _, sizes, equalSizes) = RenderWithTrueFrames(version, keystone, degrees, pixelsPerModule);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);

        var throughParallelogram = QRImageDecoder.MatchTimingDimension(luminance, width, height, threshold, grey, frame.TopLeft, frame.TopRight, frame.BottomLeft, equalSizes, parallelogram);
        var throughFrame = QRImageDecoder.MatchTimingDimension(luminance, width, height, threshold, grey, frame.TopLeft, frame.TopRight, frame.BottomLeft, sizes, frame);

        await Assert.That(throughParallelogram).IsNotEqualTo(dimension).Because("the parallelogram has to miss for the frame to be what finds it");
        await Assert.That(throughFrame).IsEqualTo(dimension);
    }

    /// <summary>
    /// The parallelogram predicts the bottom-right alignment pattern a lattice step or more off under a strong keystone, and a search there anchors on a neighbouring pattern; the frame predicts it where it is.
    /// </summary>
    [Test]
    [Arguments(32, 0.2f, 227f, 3.7f)]
    [Arguments(40, 0.19f, 38f, 3.7f)]
    [Arguments(25, 0.18f, 213f, 4.3f)]
    public async Task AlignmentSearch_WhereTheFrameExpectsIt_AnchorsOnTheBottomRightPattern(int version, float keystone, float degrees, float pixelsPerModule)
    {
        var (luminance, width, height, dimension, frame, parallelogram, moduleSize, _, _) = RenderWithTrueFrames(version, keystone, degrees, pixelsPerModule);
        var truth = SupersampledGeometry.GridToPixel(dimension, dimension, pixelsPerModule, degrees, keystone);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);

        var fromParallelogram = QRImageDecoder.BuildGridTransform(luminance, width, height, threshold, grey, parallelogram, dimension, moduleSize, out var parallelogramAnchored);
        var fromFrame = QRImageDecoder.BuildGridTransform(luminance, width, height, threshold, grey, frame, dimension, moduleSize, out var frameAnchored);

        await Assert.That(parallelogramAnchored && ModulesOff(truth, fromParallelogram, dimension - 6.5f) < 1f).IsFalse().Because("the parallelogram's search has to miss the pattern for the frame to be what finds it");
        await Assert.That(frameAnchored).IsTrue();
        await Assert.That(ModulesOff(truth, fromFrame, dimension - 6.5f)).IsLessThan(0.25f);
    }

    /// <summary>
    /// On grey edges all four sizes come from sub-pixel edges and match the render within 2 %, along the image axes as well as turned: the walk reaches the dark ring's outer edge 3.5 modules out on a finder whose row-scan size is the module itself.
    /// </summary>
    [Test]
    [Arguments(0f, 2.5f)]
    [Arguments(0f, 4f)]
    [Arguments(45f, 2.5f)]
    [Arguments(200f, 4f)]
    public async Task ModuleSizes_OnGreyEdges_AreSubPixelAlongEachLine(float degrees, float pixelsPerModule)
    {
        var qr = QRCodeGenerator.Create("FQR KEYSTONE 0123", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(5), QuietZoneSize = 0 });
        var dimension = qr.Size;
        var (luminance, width, height) = SupersampledRenderer.Render((row, column) => qr[row, column], dimension, dimension, pixelsPerModule, degrees, 0.15f);
        var truth = SupersampledGeometry.GridToPixel(dimension, dimension, pixelsPerModule, degrees, 0.15f);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var (topLeft, topRight, bottomLeft) = Centres(truth, dimension);
        topLeft.ModuleSize = SizeAlong(truth, 3.5f, 3.5f, 1f, 0f);
        topRight.ModuleSize = SizeAlong(truth, dimension - 3.5f, 3.5f, 1f, 0f);
        bottomLeft.ModuleSize = SizeAlong(truth, 3.5f, dimension - 3.5f, 1f, 0f);

        var sizes = QRImageDecoder.MeasureModuleSizes(luminance, width, height, threshold, grey, topLeft, topRight, bottomLeft);

        await Assert.That(sizes.SubPixelAlongU).IsTrue();
        await Assert.That(sizes.SubPixelAlongV).IsTrue();
        await Assert.That(Math.Abs(sizes.TopLeftAlongU / SizeAlong(truth, 3.5f, 3.5f, 1f, 0f) - 1f)).IsLessThan(0.02f);
        await Assert.That(Math.Abs(sizes.TopLeftAlongV / SizeAlong(truth, 3.5f, 3.5f, 0f, 1f) - 1f)).IsLessThan(0.02f);
        await Assert.That(Math.Abs(sizes.TopRight / SizeAlong(truth, dimension - 3.5f, 3.5f, 1f, 0f) - 1f)).IsLessThan(0.02f);
        await Assert.That(Math.Abs(sizes.BottomLeft / SizeAlong(truth, 3.5f, dimension - 3.5f, 0f, 1f) - 1f)).IsLessThan(0.02f);
    }

    /// <summary>
    /// A line whose far end cannot be measured to sub-pixel edges takes both of its sizes from whole-pixel runs, so its change of size is never compared across two resolutions; the other line keeps its sub-pixel sizes.
    /// The far finder's row-scan size is given as half the module, which stops its sub-pixel walk before the dark ring ends and leaves the runs, which have no such limit, to measure it.
    /// </summary>
    [Test]
    [Arguments(37f, 4f)]
    [Arguments(200f, 4f)]
    public async Task ModuleSizes_WhenOneEndIsNotSubPixel_TakeThatWholeLineFromRuns(float degrees, float pixelsPerModule)
    {
        var qr = QRCodeGenerator.Create("FQR KEYSTONE 0123", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(5), QuietZoneSize = 0 });
        var dimension = qr.Size;
        var (luminance, width, height) = SupersampledRenderer.Render((row, column) => qr[row, column], dimension, dimension, pixelsPerModule, degrees, 0.15f);
        var truth = SupersampledGeometry.GridToPixel(dimension, dimension, pixelsPerModule, degrees, 0.15f);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var (topLeft, topRight, bottomLeft) = Centres(truth, dimension);
        topLeft.ModuleSize = SizeAlong(truth, 3.5f, 3.5f, 1f, 0f);
        topRight.ModuleSize = SizeAlong(truth, dimension - 3.5f, 3.5f, 1f, 0f) / 2f;
        bottomLeft.ModuleSize = SizeAlong(truth, 3.5f, dimension - 3.5f, 1f, 0f);

        var sizes = QRImageDecoder.MeasureModuleSizes(luminance, width, height, threshold, grey, topLeft, topRight, bottomLeft);

        var length = Distance(topLeft.X, topLeft.Y, topRight.X, topRight.Y);
        var dirX = (topRight.X - topLeft.X) / length;
        var dirY = (topRight.Y - topLeft.Y) / length;
        await Assert.That(sizes.SubPixelAlongU).IsFalse();
        await Assert.That(sizes.TopLeftAlongU).IsEqualTo(FinderAxisEstimator.MeasureAxis(luminance, width, height, threshold, topLeft.X, topLeft.Y, dirX, dirY));
        await Assert.That(sizes.TopRight).IsEqualTo(FinderAxisEstimator.MeasureAxis(luminance, width, height, threshold, topRight.X, topRight.Y, -dirX, -dirY));
        await Assert.That(sizes.SubPixelAlongV).IsTrue();
        await Assert.That(Math.Abs(sizes.TopLeftAlongV / SizeAlong(truth, 3.5f, 3.5f, 0f, 1f) - 1f)).IsLessThan(0.02f);
        await Assert.That(Math.Abs(sizes.BottomLeft / SizeAlong(truth, 3.5f, dimension - 3.5f, 0f, 1f) - 1f)).IsLessThan(0.02f);
    }

    /// <summary>
    /// The count along each finder line from its own two ends is the plane's: the far end at weight w measures 1/w² of the near one, and the distance over their geometric mean is the modules between the centres.
    /// The mean of all four sizes, the estimate before, is short by more than a version here.
    /// </summary>
    [Test]
    [Arguments(97, 0.45f, 0f)]
    [Arguments(117, 0.4f, 35f)]
    [Arguments(177, 0.5f, 200f)]
    [Arguments(177, 0.35f, 290f)]
    public async Task Estimate_OfAPlaneInPerspective_CountsEachLineByItsOwnEnds(int dimension, float keystone, float degrees)
    {
        var plane = Plane(dimension, keystone, degrees);
        var (topLeft, topRight, bottomLeft) = Centres(plane, dimension);
        var sizes = TrueSizes(plane, dimension);
        var frame = QRImageDecoder.FinderFrame.Create(topLeft, topRight, bottomLeft, sizes);

        var estimate = QRImageDecoder.EstimateModules(topLeft, topRight, bottomLeft, sizes, frame);

        var byMean = (Distance(topLeft.X, topLeft.Y, topRight.X, topRight.Y) + Distance(topLeft.X, topLeft.Y, bottomLeft.X, bottomLeft.Y)) / 2f / sizes.Mean + 7f;
        await Assert.That(dimension - byMean).IsGreaterThan(4f).Because("the mean of the four sizes has to miss for the lines' own sizes to be what counts right");
        await Assert.That(Math.Abs(estimate - dimension)).IsLessThan(0.5f);
    }

    /// <summary>
    /// A line whose whole-pixel runs are one size beside a line the frame foreshortens counts by its own sizes. The mean of all four held the other line's far end, 40 % large here, and put a version 40 two versions short.
    /// </summary>
    [Test]
    public async Task Estimate_FlatLineBesideAForeshortenedOne_CountsByItsOwnSizes()
    {
        var topLeft = new FinderPattern { X = 100f, Y = 100f, ModuleSize = 3.5f, Count = 2 };
        var topRight = new FinderPattern { X = 100f + 560f, Y = 100f, ModuleSize = 3.5f, Count = 2 };
        var bottomLeft = new FinderPattern { X = 100f, Y = 100f + 600f, ModuleSize = 5f, Count = 2 };
        var sizes = new QRImageDecoder.FinderModuleSizes(3.5f, 3.5f, 3.5f, 5f, 3.875f, SubPixelAlongU: false, SubPixelAlongV: false);
        var frame = QRImageDecoder.FinderFrame.Create(topLeft, topRight, bottomLeft, sizes);

        var estimate = QRImageDecoder.EstimateModules(topLeft, topRight, bottomLeft, sizes, frame);

        await Assert.That(frame.IsAffine).IsFalse().Because("the left line's runs differ past their resolution");
        var expected = ((560f / 3.5f + 7f) + (600f / MathF.Sqrt(3.5f * 5f) + 7f)) / 2f;
        await Assert.That(Math.Abs(estimate - expected)).IsLessThan(1e-3f);
    }

    /// <summary>Where the frame is the parallelogram, the estimate divides both lines by the mean of all four sizes, as it did before any line counted by its own.</summary>
    [Test]
    public async Task Estimate_OfTheParallelogram_IsTheMeanOfFourBitForBit()
    {
        var plane = Plane(97, 0f, 23f);
        var (topLeft, topRight, bottomLeft) = Centres(plane, 97);
        // Whole-pixel runs within their resolution of one another: the frame takes both lines as flat
        var sizes = new QRImageDecoder.FinderModuleSizes(4f, 4f + 1f / 12f, 4f - 2f / 12f, 4f + 2f / 12f, 4f + 1f / 48f, SubPixelAlongU: false, SubPixelAlongV: false);
        var frame = QRImageDecoder.FinderFrame.Create(topLeft, topRight, bottomLeft, sizes);

        var estimate = QRImageDecoder.EstimateModules(topLeft, topRight, bottomLeft, sizes, frame);

        await Assert.That(frame.IsAffine).IsTrue();
        var widthModules = Distance(topLeft.X, topLeft.Y, topRight.X, topRight.Y) / sizes.Mean + 7f;
        var heightModules = Distance(topLeft.X, topLeft.Y, bottomLeft.X, bottomLeft.Y) / sizes.Mean + 7f;
        await Assert.That(estimate).IsEqualTo((widthModules + heightModules) / 2f);
    }

    /// <summary>
    /// The timing match gives a dimension up once its wrong modules are as many as the best so far allows, which has to name what counting every module names: through true frames, through the parallelogram of the same centres, and through triples with a corner moved off its finder, where nothing reads.
    /// </summary>
    [Test]
    public async Task TimingMatch_GivingUpADimensionEarly_NamesWhatTheFullCountNames()
    {
        var named = 0;
        var refused = 0;
        foreach (var version in new[] { 2, 7, 20, 32, 40 })
        {
            foreach (var (keystone, degrees) in new[] { (0f, 0f), (0.3f, 77f), (0.45f, 200f) })
            {
                var (luminance, width, height, _, frame, parallelogram, _, sizes, equalSizes) = RenderWithTrueFrames(version, keystone, degrees, 4f);
                var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
                foreach (var shift in new[] { 0f, 3f, 11f })
                {
                    var topRight = frame.TopRight;
                    topRight.X += shift * topRight.ModuleSize;
                    foreach (var (triple, tripleSizes) in new[] { (frame, sizes), (parallelogram, equalSizes) })
                    {
                        var moved = QRImageDecoder.FinderFrame.Create(triple.TopLeft, topRight, triple.BottomLeft, tripleSizes);
                        var actual = QRImageDecoder.MatchTimingDimension(luminance, width, height, threshold, grey, triple.TopLeft, topRight, triple.BottomLeft, tripleSizes, moved);
                        var reference = MatchTimingDimensionCountingEveryModule(luminance, width, height, threshold, grey, triple.TopLeft, topRight, triple.BottomLeft, tripleSizes, moved);
                        await Assert.That(actual).IsEqualTo(reference).Because($"version {version}, keystone {keystone}, turned {degrees}°, top-right moved {shift} modules");
                        if (reference == 0)
                            refused++;
                        else
                            named++;
                    }
                }
            }
        }

        // Both outcomes have to occur, or the comparison held nothing
        await Assert.That(named).IsGreaterThan(10);
        await Assert.That(refused).IsGreaterThan(10);
    }

    /// <summary>The timing match as it was before it gave a dimension up early: every timing module of every dimension within four versions counted.</summary>
    private static int MatchTimingDimensionCountingEveryModule(byte[] luminance, int width, int height, byte threshold, GreyLevels grey, FinderPattern topLeft, FinderPattern topRight, FinderPattern bottomLeft, QRImageDecoder.FinderModuleSizes sizes, QRImageDecoder.FinderFrame frame)
    {
        var estimate = QRImageDecoder.EstimateModules(topLeft, topRight, bottomLeft, sizes, frame);
        var best = 0;
        var bestWrong = 1f / 8f;
        for (var version = 1; version <= 40; version++)
        {
            var dimension = 17 + 4 * version;
            if (Math.Abs(dimension - estimate) > 16f)
                continue;
            var transform = frame.Transform(dimension);
            var wrong = 0;
            for (var i = 8; i < dimension - 8; i++)
            {
                var dark = (i & 1) == 0;
                if (IsDarkAt(transform, i + 0.5f, 6.5f) != dark)
                    wrong++;
                if (IsDarkAt(transform, 6.5f, i + 0.5f) != dark)
                    wrong++;
            }
            var share = wrong / (2f * (dimension - 16));
            if (share < bestWrong)
            {
                bestWrong = share;
                best = dimension;
            }
        }
        return best;

        bool IsDarkAt(in PerspectiveTransform transform, float u, float v)
        {
            transform.Transform(u, v, out var x, out var y);
            if (grey.IsEnabled)
                return LuminanceSampler.Bilinear(luminance, width, height, x, y) < grey.Midpoint;
            var px = x < 0f ? 0 : x >= width ? width - 1 : (int)x;
            var py = y < 0f ? 0 : y >= height ? height - 1 : (int)y;
            return luminance[py * width + px] < threshold;
        }
    }

    /// <summary>The plane's own sizes along each finder line at its finder centres, as sub-pixel measurements.</summary>
    private static QRImageDecoder.FinderModuleSizes TrueSizes(in PerspectiveTransform plane, int dimension)
    {
        var topLeftU = SizeAlong(plane, 3.5f, 3.5f, 1f, 0f);
        var topLeftV = SizeAlong(plane, 3.5f, 3.5f, 0f, 1f);
        var topRight = SizeAlong(plane, dimension - 3.5f, 3.5f, 1f, 0f);
        var bottomLeft = SizeAlong(plane, 3.5f, dimension - 3.5f, 0f, 1f);
        return new QRImageDecoder.FinderModuleSizes(topLeftU, topLeftV, topRight, bottomLeft, (topLeftU + topLeftV + topRight + bottomLeft) / 4f, SubPixelAlongU: true, SubPixelAlongV: true);
    }

    /// <summary>A render of a version's symbol, the frame of its true finder centres and true sizes beside the parallelogram of the same centres, the sizes' mean, the module size the decoder searches with, and the two sets of sizes.</summary>
    private static (byte[] Luminance, int Width, int Height, int Dimension, QRImageDecoder.FinderFrame Frame, QRImageDecoder.FinderFrame Parallelogram, float ModuleSize, QRImageDecoder.FinderModuleSizes Sizes, QRImageDecoder.FinderModuleSizes EqualSizes) RenderWithTrueFrames(int version, float keystone, float degrees, float pixelsPerModule)
    {
        var qr = QRCodeGenerator.Create("FQR KEYSTONE 0123", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
        var dimension = qr.Size;
        var (luminance, width, height) = SupersampledRenderer.Render((row, column) => qr[row, column], dimension, dimension, pixelsPerModule, degrees, keystone);
        var truth = SupersampledGeometry.GridToPixel(dimension, dimension, pixelsPerModule, degrees, keystone);
        var (topLeft, topRight, bottomLeft) = Centres(truth, dimension);
        var topLeftSizeU = SizeAlong(truth, 3.5f, 3.5f, 1f, 0f);
        var topLeftSizeV = SizeAlong(truth, 3.5f, 3.5f, 0f, 1f);
        var topRightSize = SizeAlong(truth, dimension - 3.5f, 3.5f, 1f, 0f);
        var bottomLeftSize = SizeAlong(truth, 3.5f, dimension - 3.5f, 0f, 1f);
        var mean = (topLeftSizeU + topLeftSizeV + topRightSize + bottomLeftSize) / 4f;
        var sizes = new QRImageDecoder.FinderModuleSizes(topLeftSizeU, topLeftSizeV, topRightSize, bottomLeftSize, mean, SubPixelAlongU: true, SubPixelAlongV: true);
        var equal = new QRImageDecoder.FinderModuleSizes(mean, mean, mean, mean, mean, SubPixelAlongU: true, SubPixelAlongV: true);
        return (luminance, width, height, dimension, QRImageDecoder.FinderFrame.Create(topLeft, topRight, bottomLeft, sizes), QRImageDecoder.FinderFrame.Create(topLeft, topRight, bottomLeft, equal), mean, sizes, equal);
    }

    /// <summary>How far the transform puts grid point (<paramref name="at"/>, <paramref name="at"/>) from where the render drew it, in modules there.</summary>
    private static float ModulesOff(in PerspectiveTransform truth, in PerspectiveTransform transform, float at)
    {
        truth.Transform(at, at, out var x, out var y);
        transform.Transform(at, at, out var actualX, out var actualY);
        return Distance(x, y, actualX, actualY) / SizeAlong(truth, at, at, 1f, 0f);
    }

    /// <summary>A symbol's grid, quiet zone included, drawn onto a quadrilateral whose top edge is <paramref name="keystone"/> shorter (longer when negative), turned about its centre.</summary>
    private static PerspectiveTransform Plane(int dimension, float keystone, float degrees)
    {
        var half = (dimension + 8) * PixelsPerModule / 2f;
        var top = half * (1f - keystone);
        float[] corners = [-top, -half, top, -half, half, half, -half, half];
        var radians = degrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        for (var i = 0; i < 8; i += 2)
        {
            var x = corners[i];
            var y = corners[i + 1];
            corners[i] = 1000f + x * cos - y * sin;
            corners[i + 1] = 1000f + x * sin + y * cos;
        }
        return PerspectiveTransform.QuadrilateralToQuadrilateral(
            -4f, -4f, dimension + 4f, -4f, dimension + 4f, dimension + 4f, -4f, dimension + 4f,
            corners[0], corners[1], corners[2], corners[3], corners[4], corners[5], corners[6], corners[7]);
    }

    private static (FinderPattern TopLeft, FinderPattern TopRight, FinderPattern BottomLeft) Centres(in PerspectiveTransform plane, int dimension)
        => (At(plane, 3.5f, 3.5f), At(plane, dimension - 3.5f, 3.5f), At(plane, 3.5f, dimension - 3.5f));

    private static FinderPattern At(in PerspectiveTransform plane, float u, float v)
    {
        plane.Transform(u, v, out var x, out var y);
        return new FinderPattern { X = x, Y = y, ModuleSize = PixelsPerModule, Count = 2 };
    }

    /// <summary>The plane's pixels per module at (<paramref name="u"/>, <paramref name="v"/>) along the grid direction given.</summary>
    private static float SizeAlong(in PerspectiveTransform plane, float u, float v, float du, float dv)
    {
        plane.Transform(u - 0.5f * du, v - 0.5f * dv, out var x0, out var y0);
        plane.Transform(u + 0.5f * du, v + 0.5f * dv, out var x1, out var y1);
        return Distance(x0, y0, x1, y1);
    }

    private static float Distance(float x0, float y0, float x1, float y1)
        => MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
}
