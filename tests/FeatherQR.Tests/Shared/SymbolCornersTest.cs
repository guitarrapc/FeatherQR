using FeatherQR.SkiaSharp;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// <c>Corners</c> on the three decode results: the outer corners of the symbol's
/// module area (quiet zone excluded), in continuous image coordinates, ordered from
/// the symbol's own top-left, populated when and only when the decode succeeded.
/// </summary>
/// <remarks>
/// <para>
/// Every scene is built the same way: a symbol is rendered flat at a known pixel size
/// with no quiet zone of its own, then drawn onto a white canvas through a matrix. The
/// expected corners are that matrix applied to the flat symbol's corners, so the oracle
/// is the scene, not the decoder, and rotation, mirroring and keystone are one test with
/// different matrices.
/// </para>
/// <para>
/// Continuous coordinates: (0, 0) is the top-left corner of the top-left pixel, so a
/// symbol drawn from pixel 48 reports 48, not 48.5, and the values plot directly
/// with SkiaSharp. The order carries orientation: seen on screen, a non-mirrored symbol's
/// corners run clockwise (positive cross product in y-down coordinates) and a mirrored
/// capture's run counter-clockwise.
/// </para>
/// </remarks>
public class SymbolCornersTest
{
    private const int PixelsPerModule = 8;
    private const int Margin = 6 * PixelsPerModule;

    // ---- Standard QR --------------------------------------------------------------------

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(7)]
    public async Task QR_AxisAligned_CornersAreTheDrawnRectangle(int version)
    {
        using var flat = RenderQr("CORNERS", version);
        var matrix = SKMatrix.CreateTranslation(Margin, Margin);
        using var scene = Compose(flat, matrix);

        await Assert.That(QRCodeImageDecoder.TryDecode(scene, out _, out var info)).IsTrue().Because($"status={info.Status}");
        await AssertCorners(info.Corners, Expected(matrix, flat), PixelsPerModule / 2f, mirrored: false);
    }

    /// <summary>
    /// The coordinate convention, pinned to a tenth of a pixel where nothing but the
    /// finder centres anchors the geometry: a symbol drawn from pixel 48 reports 48.0, not
    /// 48.5 (the first implementation added half a pixel, hidden by the half-module
    /// tolerance every other case needs). Standard QR version 1 and Micro QR are anchored
    /// on finders alone, so all four corners are held; rMQR's top-left is finder-anchored
    /// while its right-hand corners come from the sub-finder refinement and carry their own
    /// sub-pixel residual, so only the top-left is held here.
    /// </summary>
    [Test]
    public async Task AxisAligned_FinderAnchoredCorners_LandOnTheDrawnEdgeToATenthOfAPixel()
    {
        var matrix = SKMatrix.CreateTranslation(Margin, Margin);

        using var qr = RenderQr("CORNERS", version: 1);
        using var qrScene = Compose(qr, matrix);
        await Assert.That(QRCodeImageDecoder.TryDecode(qrScene, out _, out var qrInfo)).IsTrue();
        await AssertCorners(qrInfo.Corners, Expected(matrix, qr), 0.1f, mirrored: false);

        using var micro = RenderMicro("MICRO CORNERS", MicroQRVersion.M4);
        using var microScene = Compose(micro, matrix);
        await Assert.That(MicroQRCodeImageDecoder.TryDecode(microScene, out _, out var microInfo)).IsTrue();
        await AssertCorners(microInfo.Corners, Expected(matrix, micro), 0.1f, mirrored: false);

        using var rm = RenderRm("RMQR CORNERS", RmQRVersion.R11x59);
        using var rmScene = Compose(rm, matrix);
        await Assert.That(RmQRCodeImageDecoder.TryDecode(rmScene, out _, out var rmInfo)).IsTrue();
        await AssertNear(rmInfo.Corners.TopLeft, Expected(matrix, rm).TopLeft, 0.15f, "TopLeft");
    }

    /// <summary>Version 1, with no alignment pattern, is held for rotation too; its only gap is keystone.</summary>
    [Test]
    [Arguments(2, 90)]
    [Arguments(2, 180)]
    [Arguments(2, 270)]
    [Arguments(2, 17)]
    [Arguments(1, 90)]
    [Arguments(1, 17)]
    [Arguments(1, 60)]
    public async Task QR_Rotated_CornersFollowTheSymbolNotTheImage(int version, int degrees)
    {
        using var flat = RenderQr("CORNERS", version);
        var matrix = Rotation(flat, degrees);
        using var scene = Compose(flat, matrix);

        await Assert.That(QRCodeImageDecoder.TryDecode(scene, out _, out var info)).IsTrue().Because($"status={info.Status}");
        await AssertCorners(info.Corners, Expected(matrix, flat), PixelsPerModule / 2f, mirrored: false);
    }

    /// <summary>
    /// A mirrored capture decodes on the transposed retry, and the corner order has to
    /// carry that on both mappings: the global fit (version 2) and the piecewise mesh
    /// (version 20 under keystone), which reaches the mesh's own attach site with its own
    /// transposed flag. Review finding: without the second row a mesh site that dropped
    /// the flag passed the suite.
    /// </summary>
    [Test]
    [Arguments(2, 0f)]
    [Arguments(20, 0.06f)]
    public async Task QR_Mirrored_WindingReverses(int version, float tilt)
    {
        using var flat = RenderQr("CORNERS UNDER PERSPECTIVE", version);
        var matrix = Keystone(flat, tilt).PreConcat(MirrorInPlace(flat));
        using var scene = Compose(flat, matrix);

        await Assert.That(QRCodeImageDecoder.TryDecode(scene, out _, out var info)).IsTrue().Because($"status={info.Status}");
        await AssertCorners(info.Corners, Expected(matrix, flat), PixelsPerModule / 2f, mirrored: true);
    }

    /// <summary>
    /// Under perspective the corners come from the fitted geometry, so a version with an
    /// alignment pattern (a true four-point fit) is held to half a module at the tilts
    /// below (12 % on versions 2 and 5, 6 to 8 % on 7 and 10, 5 to 6 % from 14 up); this
    /// scene (8 px per module, no quiet zone) decodes further on most of them (versions 2
    /// and 5 to 15 to 17 %, 7 and 30 to 10 %) and holds the bound there too. From version
    /// 14 the decoder can sample through the piecewise mesh instead of the global fit, when
    /// it detects enough of the mesh's nodes; the 14 row stays on the global fit on this
    /// scene, while the cases at 20, 25 and 30 take the mesh and are ones where the global
    /// fit's bottom-right anchor locks onto a neighbouring alignment pattern (18 to 30
    /// modules off, review finding), so they pin that the corners follow whatever mapping
    /// actually decoded the symbol.
    /// Version 1 has no alignment pattern and is covered by the flat and rotated cases only.
    /// </summary>
    [Test]
    [Arguments(2, 0.08f)]
    [Arguments(2, 0.12f)]
    [Arguments(5, 0.12f)]
    [Arguments(7, 0.06f)]
    [Arguments(10, 0.08f)]
    [Arguments(14, 0.05f)]
    [Arguments(20, 0.06f)]
    [Arguments(25, 0.06f)]
    [Arguments(30, 0.05f)]
    public async Task QR_Keystone_CornersAreTheWarpedQuadrilateral(int version, float tilt)
    {
        using var flat = RenderQr("CORNERS UNDER PERSPECTIVE", version);
        var matrix = Keystone(flat, tilt);
        using var scene = Compose(flat, matrix);

        await Assert.That(QRCodeImageDecoder.TryDecode(scene, out _, out var info)).IsTrue().Because($"status={info.Status}");
        await AssertCorners(info.Corners, Expected(matrix, flat), PixelsPerModule / 2f, mirrored: false);
    }

    // ---- Micro QR -----------------------------------------------------------------------

    [Test]
    [Arguments(MicroQRVersion.M2, "12345")]
    [Arguments(MicroQRVersion.M4, "MICRO CORNERS")]
    public async Task MicroQR_AxisAligned_CornersAreTheDrawnRectangle(MicroQRVersion version, string content)
    {
        using var flat = RenderMicro(content, version);
        var matrix = SKMatrix.CreateTranslation(Margin, Margin);
        using var scene = Compose(flat, matrix);

        await Assert.That(MicroQRCodeImageDecoder.TryDecode(scene, out _, out var info)).IsTrue().Because($"status={info.Status}");
        await AssertCorners(info.Corners, Expected(matrix, flat), PixelsPerModule / 2f, mirrored: false);
    }

    /// <summary>
    /// Right angles are exact for Micro QR (the axis-aligned fast path). Oblique angles go
    /// through the single-finder axis sweep, whose far corners carry the residual: a 1°
    /// sweep at 8 px per module measured 0.2 to 0.9 modules depending on the angle (review
    /// finding), so the oblique cases are held to one module, which is the bound the
    /// contract states for the single-finder symbologies.
    /// </summary>
    [Test]
    [Arguments(90, 0.5f)]
    [Arguments(180, 0.5f)]
    [Arguments(8, 1f)]
    [Arguments(20, 1f)]
    [Arguments(60, 1f)]
    [Arguments(75, 1f)]
    public async Task MicroQR_Rotated_CornersFollowTheSymbol(int degrees, float toleranceModules)
    {
        using var flat = RenderMicro("MICRO CORNERS", MicroQRVersion.M4);
        var matrix = Rotation(flat, degrees);
        using var scene = Compose(flat, matrix);

        await Assert.That(MicroQRCodeImageDecoder.TryDecode(scene, out _, out var info)).IsTrue().Because($"status={info.Status}");
        await AssertCorners(info.Corners, Expected(matrix, flat), PixelsPerModule * toleranceModules, mirrored: false);
    }

    [Test]
    [Arguments(0, 0.5f)]
    [Arguments(30, 1f)]
    public async Task MicroQR_Mirrored_WindingReverses(int degrees, float toleranceModules)
    {
        using var flat = RenderMicro("MICRO CORNERS", MicroQRVersion.M4);
        var matrix = Rotation(flat, degrees).PreConcat(MirrorInPlace(flat));
        using var scene = Compose(flat, matrix);

        await Assert.That(MicroQRCodeImageDecoder.TryDecode(scene, out _, out var info)).IsTrue().Because($"status={info.Status}");
        await AssertCorners(info.Corners, Expected(matrix, flat), PixelsPerModule * toleranceModules, mirrored: true);
    }

    /// <summary>
    /// Micro QR's far corners are held to a full module rather than half. The decoder
    /// absorbs a mild keystone in its affine paths (2 % on the axis-aligned fast path, 4 %
    /// through the scale variants) and only goes to its bounded single-finder perspective
    /// search from about 5 %; the 5 % and 8 % rows are the ones that reach that search's
    /// attach sites, plain and mirrored (review finding: without them those two sites were
    /// unexercised). The mirrored 8 % row sits at 1.03 modules on this scene, past the
    /// documented 4 % envelope, and is held to a module and a half.
    /// </summary>
    [Test]
    [Arguments(0.02f, false, 1f)]
    [Arguments(0.04f, false, 1f)]
    [Arguments(0.05f, false, 1f)]
    [Arguments(0.08f, false, 1f)]
    [Arguments(0.08f, true, 1.5f)]
    public async Task MicroQR_Keystone_CornersAreTheWarpedQuadrilateral(float tilt, bool mirrored, float toleranceModules)
    {
        using var flat = RenderMicro("MICRO CORNERS", MicroQRVersion.M4);
        var matrix = mirrored ? Keystone(flat, tilt).PreConcat(MirrorInPlace(flat)) : Keystone(flat, tilt);
        using var scene = Compose(flat, matrix);

        await Assert.That(MicroQRCodeImageDecoder.TryDecode(scene, out _, out var info)).IsTrue().Because($"status={info.Status}");
        await AssertCorners(info.Corners, Expected(matrix, flat), PixelsPerModule * toleranceModules, mirrored);
    }

    // ---- rMQR ---------------------------------------------------------------------------

    [Test]
    [Arguments(RmQRVersion.R7x43, "RMQR")]
    [Arguments(RmQRVersion.R13x77, "RMQR CORNERS ARE RECTANGULAR")]
    public async Task RmQR_AxisAligned_CornersAreTheDrawnRectangle(RmQRVersion version, string content)
    {
        using var flat = RenderRm(content, version);
        var matrix = SKMatrix.CreateTranslation(Margin, Margin);
        using var scene = Compose(flat, matrix);

        await Assert.That(RmQRCodeImageDecoder.TryDecode(scene, out _, out var info)).IsTrue().Because($"status={info.Status}");
        await AssertCorners(info.Corners, Expected(matrix, flat), PixelsPerModule / 2f, mirrored: false);
    }

    [Test]
    [Arguments(90)]
    [Arguments(180)]
    [Arguments(8)]
    public async Task RmQR_Rotated_CornersFollowTheSymbol(int degrees)
    {
        using var flat = RenderRm("RMQR CORNERS", RmQRVersion.R11x59);
        var matrix = Rotation(flat, degrees);
        using var scene = Compose(flat, matrix);

        await Assert.That(RmQRCodeImageDecoder.TryDecode(scene, out _, out var info)).IsTrue().Because($"status={info.Status}");
        await AssertCorners(info.Corners, Expected(matrix, flat), PixelsPerModule / 2f, mirrored: false);
    }

    [Test]
    public async Task RmQR_Mirrored_WindingReverses()
    {
        using var flat = RenderRm("RMQR CORNERS", RmQRVersion.R11x59);
        var matrix = Mirror(flat);
        using var scene = Compose(flat, matrix);

        await Assert.That(RmQRCodeImageDecoder.TryDecode(scene, out _, out var info)).IsTrue().Because($"status={info.Status}");
        await AssertCorners(info.Corners, Expected(matrix, flat), PixelsPerModule / 2f, mirrored: true);
    }

    /// <summary>
    /// rMQR anchors its far end on the sub-finder and recovers the remaining perspective
    /// by a bounded search that under-recovers the lean of the column axis on short
    /// symbols, so the two far corners carry the residual; which is worse depends on the
    /// version and tilt. Measured at 8 px per module (review finding): on R11x59 0.9
    /// modules at 2 % (top-right), 1.24 at 3 % and 1.22 at 4 % (bottom-left); on R7x43
    /// 1.04 at 4 % (top-right, with even the anchors at 0.9 and 0.7); R17x139 stays under
    /// 0.65 and a left-shrunk keystone under 0.4 everywhere. The contract states one module
    /// at 2 % and about a module and a half from 3 % up on the short symbols, and that is
    /// what is held here; the scene decodes on to 8 % on R11x59 with the bound still holding.
    /// </summary>
    [Test]
    [Arguments(RmQRVersion.R11x59, "RMQR CORNERS", 0.02f, 1f)]
    [Arguments(RmQRVersion.R11x59, "RMQR CORNERS", 0.04f, 1.5f)]
    [Arguments(RmQRVersion.R7x43, "RMQR", 0.04f, 1.5f)]
    [Arguments(RmQRVersion.R17x139, "RMQR CORNERS ARE RECTANGULAR", 0.04f, 1f)]
    public async Task RmQR_Keystone_CornersAreTheWarpedQuadrilateral(RmQRVersion version, string content, float tilt, float toleranceModules)
    {
        using var flat = RenderRm(content, version);
        var matrix = Keystone(flat, tilt);
        using var scene = Compose(flat, matrix);

        await Assert.That(RmQRCodeImageDecoder.TryDecode(scene, out _, out var info)).IsTrue().Because($"status={info.Status}");
        await AssertCorners(info.Corners, Expected(matrix, flat), PixelsPerModule * toleranceModules, mirrored: false);
    }

    // ---- when there is nothing to report ------------------------------------------------

    /// <summary>
    /// A matrix-level decode has no image, so it has no corners; the value is the default
    /// and says so.
    /// </summary>
    [Test]
    public async Task MatrixDecode_HasNoCorners()
    {
        QRCodeDecoder.TryDecode(QRCodeGenerator.Create("CORNERS", QREccLevel.M), out _, out var qr);
        MicroQRCodeDecoder.TryDecode(MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L), out _, out var micro);
        RmQRCodeDecoder.TryDecode(RmQRCodeGenerator.Create("RMQR", RmQREccLevel.M), out _, out var rm);

        await Assert.That(qr.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(qr.Corners.IsEmpty).IsTrue();
        await Assert.That(micro.Corners.IsEmpty).IsTrue();
        await Assert.That(rm.Corners.IsEmpty).IsTrue();
        await Assert.That(qr.Corners).IsEqualTo(default(SymbolCorners));
    }

    /// <summary>
    /// Corners are reported for a success only. A failed image decode has tried some
    /// geometry, and reporting that would hand the caller the position of a symbol that
    /// was not read.
    /// </summary>
    [Test]
    public async Task FailedImageDecode_HasNoCorners()
    {
        using var blank = new SKBitmap(new SKImageInfo(200, 200, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(blank))
        {
            canvas.Clear(SKColors.White);
        }

        await Assert.That(QRCodeImageDecoder.TryDecode(blank, out _, out var qr)).IsFalse();
        await Assert.That(MicroQRCodeImageDecoder.TryDecode(blank, out _, out var micro)).IsFalse();
        await Assert.That(RmQRCodeImageDecoder.TryDecode(blank, out _, out var rm)).IsFalse();

        await Assert.That(qr.Corners.IsEmpty).IsTrue();
        await Assert.That(micro.Corners.IsEmpty).IsTrue();
        await Assert.That(rm.Corners.IsEmpty).IsTrue();
    }

    /// <summary>
    /// "Success only" has two negative classes that reach the geometry, unlike a blank
    /// image: a symbol that was located and fully read but did not fit the caller's buffer
    /// (<see cref="DecodeStatus.DestinationTooSmall"/>), and one that was located but could
    /// not be read (<see cref="DecodeStatus.DataUncorrectable"/> after damage). Both leave
    /// the corners empty, on all three symbologies; a guard that widened to "terminal" would
    /// fail the first, and one that attached corners before checking the status the second.
    /// </summary>
    [Test]
    public async Task LocatedButNotDecoded_HasNoCorners()
    {
        var matrix = SKMatrix.CreateTranslation(Margin, Margin);
        var tiny = new char[1];

        using var qr = RenderQr("CORNERS", version: 2);
        using var qrScene = Compose(qr, matrix);
        var qrLuminance = Luminance(qrScene);
        await Assert.That(QRCodeDecoder.TryDecodeImage(qrLuminance, qrScene.Width, qrScene.Height, tiny, out _, out var qrShort)).IsFalse();
        await Assert.That(qrShort.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(qrShort.Corners.IsEmpty).IsTrue();

        using var micro = RenderMicro("MICRO CORNERS", MicroQRVersion.M4);
        using var microScene = Compose(micro, matrix);
        var microLuminance = Luminance(microScene);
        await Assert.That(MicroQRCodeDecoder.TryDecodeImage(microLuminance, microScene.Width, microScene.Height, tiny, out _, out var microShort)).IsFalse();
        await Assert.That(microShort.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(microShort.Corners.IsEmpty).IsTrue();

        using var rm = RenderRm("RMQR CORNERS", RmQRVersion.R11x59);
        using var rmScene = Compose(rm, matrix);
        var rmLuminance = Luminance(rmScene);
        await Assert.That(RmQRCodeDecoder.TryDecodeImage(rmLuminance, rmScene.Width, rmScene.Height, tiny, out _, out var rmShort)).IsFalse();
        await Assert.That(rmShort.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(rmShort.Corners.IsEmpty).IsTrue();

        // Damage: a block of data modules painted over, well away from every function
        // pattern (for rMQR that means clear of the finder-side format columns 8-10).
        Damage(qrScene, moduleX: 10, moduleY: 10, modules: 12);
        await Assert.That(QRCodeImageDecoder.TryDecode(qrScene, out _, out var qrDamaged)).IsFalse();
        await Assert.That(qrDamaged.Status).IsEqualTo(DecodeStatus.DataUncorrectable);
        await Assert.That(qrDamaged.Corners.IsEmpty).IsTrue();

        Damage(microScene, moduleX: 9, moduleY: 9, modules: 7);
        await Assert.That(MicroQRCodeImageDecoder.TryDecode(microScene, out _, out var microDamaged)).IsFalse();
        await Assert.That(microDamaged.Status).IsEqualTo(DecodeStatus.DataUncorrectable);
        await Assert.That(microDamaged.Corners.IsEmpty).IsTrue();

        Damage(rmScene, moduleX: 30, moduleY: 2, modules: 7);
        await Assert.That(RmQRCodeImageDecoder.TryDecode(rmScene, out _, out var rmDamaged)).IsFalse();
        await Assert.That(rmDamaged.Status).IsEqualTo(DecodeStatus.DataUncorrectable);
        await Assert.That(rmDamaged.Corners.IsEmpty).IsTrue();
    }

    /// <summary>
    /// The luminance overload reports the same corners as the bitmap overload: the
    /// bitmap path converts pixels one to one, so both are the input's pixel space.
    /// </summary>
    [Test]
    public async Task LuminanceOverload_ReportsTheSameCorners()
    {
        using var flat = RenderQr("CORNERS", version: 2);
        var matrix = Rotation(flat, 30);
        using var scene = Compose(flat, matrix);

        await Assert.That(QRCodeImageDecoder.TryDecode(scene, out _, out var fromBitmap)).IsTrue();

        var luminance = Luminance(scene);
        await Assert.That(QRCodeDecoder.TryDecodeImage(luminance, scene.Width, scene.Height, out _, out var fromLuminance)).IsTrue();

        await Assert.That(fromLuminance.Corners).IsEqualTo(fromBitmap.Corners);
    }

    private static byte[] Luminance(SKBitmap scene)
    {
        var luminance = new byte[scene.Width * scene.Height];
        for (var y = 0; y < scene.Height; y++)
            for (var x = 0; x < scene.Width; x++)
                luminance[y * scene.Width + x] = scene.GetPixel(x, y).Red;
        return luminance;
    }

    /// <summary>Paints a <paramref name="modules"/>-module square of noise over the symbol drawn at <see cref="Margin"/>, starting at module (<paramref name="moduleX"/>, <paramref name="moduleY"/>).</summary>
    private static void Damage(SKBitmap scene, int moduleX, int moduleY, int modules)
    {
        using var canvas = new SKCanvas(scene);
        using var paint = new SKPaint();
        var random = new Random(42);
        for (var v = 0; v < modules; v++)
        {
            for (var u = 0; u < modules; u++)
            {
                paint.Color = random.Next(2) == 0 ? SKColors.Black : SKColors.White;
                canvas.DrawRect(SKRect.Create(Margin + (moduleX + u) * PixelsPerModule, Margin + (moduleY + v) * PixelsPerModule, PixelsPerModule, PixelsPerModule), paint);
            }
        }
        canvas.Flush();
    }

    // ---- scenes -------------------------------------------------------------------------

    private static SKBitmap RenderQr(string content, int version)
    {
        var data = QRCodeGenerator.Create(content, QREccLevel.M, new QRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 });
        return RenderFlat(data.Size, data.Size, (canvas, rect) => SymbolRenderer.Render(canvas, rect, data, SKColors.Black, SKColors.White));
    }

    private static SKBitmap RenderMicro(string content, MicroQRVersion version)
    {
        var data = MicroQRCodeGenerator.Create(content, MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 });
        return RenderFlat(data.Size, data.Size, (canvas, rect) => SymbolRenderer.Render(canvas, rect, data, SKColors.Black, SKColors.White));
    }

    private static SKBitmap RenderRm(string content, RmQRVersion version)
    {
        var data = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 });
        return RenderFlat(data.Width, data.Height, (canvas, rect) => SymbolRenderer.Render(canvas, rect, data, SKColors.Black, SKColors.White));
    }

    /// <summary>The symbol alone, module area only, at <see cref="PixelsPerModule"/>.</summary>
    private static SKBitmap RenderFlat(int modulesWide, int modulesHigh, Action<SKCanvas, SKRect> render)
    {
        var bitmap = new SKBitmap(new SKImageInfo(modulesWide * PixelsPerModule, modulesHigh * PixelsPerModule, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        render(canvas, SKRect.Create(0, 0, bitmap.Width, bitmap.Height));
        canvas.Flush();
        return bitmap;
    }

    /// <summary>Draws the flat symbol onto a white canvas through <paramref name="matrix"/>, with room around it.</summary>
    private static SKBitmap Compose(SKBitmap flat, SKMatrix matrix)
    {
        var side = 2 * Margin + 2 * Math.Max(flat.Width, flat.Height);
        var scene = new SKBitmap(new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(scene);
        canvas.Clear(SKColors.White);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(flat, 0, 0, SKSamplingOptions.Default);
        canvas.Flush();
        return scene;
    }

    /// <summary>Rotation about the scene centre, symbol centred: the symbol never leaves the canvas.</summary>
    private static SKMatrix Rotation(SKBitmap flat, float degrees)
    {
        var side = 2 * Margin + 2 * Math.Max(flat.Width, flat.Height);
        var centre = side / 2f;
        var toCentre = SKMatrix.CreateTranslation(centre - flat.Width / 2f, centre - flat.Height / 2f);
        var rotation = SKMatrix.CreateRotationDegrees(degrees, centre, centre);
        return rotation.PreConcat(toCentre);
    }

    /// <summary>Horizontal flip at <see cref="Margin"/>: a front-camera capture.</summary>
    private static SKMatrix Mirror(SKBitmap flat)
        => SKMatrix.CreateTranslation(Margin + flat.Width, Margin).PreConcat(SKMatrix.CreateScale(-1, 1));

    /// <summary>Horizontal flip about the flat symbol's own centre, to compose with <see cref="Rotation"/>.</summary>
    private static SKMatrix MirrorInPlace(SKBitmap flat)
        => SKMatrix.CreateTranslation(flat.Width, 0).PreConcat(SKMatrix.CreateScale(-1, 1));

    /// <summary>Keystone: the top edge shrunk by <paramref name="tilt"/> of the width per side.</summary>
    private static SKMatrix Keystone(SKBitmap flat, float tilt)
    {
        float w = flat.Width, h = flat.Height;
        var shrink = tilt * w;
        return SquareToQuad(
            w, h,
            new SKPoint(Margin + shrink, Margin),
            new SKPoint(Margin + w - shrink, Margin),
            new SKPoint(Margin + w, Margin + h),
            new SKPoint(Margin, Margin + h));
    }

    /// <summary>
    /// The flat symbol's corners through the scene matrix, in the symbol's own order.
    /// Projected by hand so a perspective matrix is honoured whatever <c>MapPoint</c> does.
    /// </summary>
    private static (SKPoint TopLeft, SKPoint TopRight, SKPoint BottomRight, SKPoint BottomLeft) Expected(SKMatrix m, SKBitmap flat)
        => (Project(m, 0, 0), Project(m, flat.Width, 0), Project(m, flat.Width, flat.Height), Project(m, 0, flat.Height));

    private static SKPoint Project(SKMatrix m, float x, float y)
    {
        var w = m.Persp0 * x + m.Persp1 * y + m.Persp2;
        return new SKPoint((m.ScaleX * x + m.SkewX * y + m.TransX) / w, (m.SkewY * x + m.ScaleY * y + m.TransY) / w);
    }

    /// <summary>Homography mapping the (0,0)-(w,h) rectangle onto the quadrilateral (top-left, top-right, bottom-right, bottom-left).</summary>
    private static SKMatrix SquareToQuad(float w, float h, SKPoint tl, SKPoint tr, SKPoint br, SKPoint bl)
    {
        var dx3 = tl.X - tr.X + br.X - bl.X;
        var dy3 = tl.Y - tr.Y + br.Y - bl.Y;
        float a13, a23, a11, a21, a12, a22;
        if (dx3 == 0f && dy3 == 0f)
        {
            a11 = tr.X - tl.X;
            a21 = br.X - tr.X;
            a12 = tr.Y - tl.Y;
            a22 = br.Y - tr.Y;
            a13 = 0f;
            a23 = 0f;
        }
        else
        {
            var dx1 = tr.X - br.X;
            var dx2 = bl.X - br.X;
            var dy1 = tr.Y - br.Y;
            var dy2 = bl.Y - br.Y;
            var denominator = dx1 * dy2 - dx2 * dy1;
            a13 = (dx3 * dy2 - dx2 * dy3) / denominator;
            a23 = (dx1 * dy3 - dx3 * dy1) / denominator;
            a11 = tr.X - tl.X + a13 * tr.X;
            a21 = bl.X - tl.X + a23 * bl.X;
            a12 = tr.Y - tl.Y + a13 * tr.Y;
            a22 = bl.Y - tl.Y + a23 * bl.Y;
        }

        // Unit square → quad, then scale (w, h) → unit square.
        var unit = new SKMatrix(a11, a21, tl.X, a12, a22, tl.Y, a13, a23, 1f);
        return unit.PreConcat(SKMatrix.CreateScale(1f / w, 1f / h));
    }

    // ---- assertions ---------------------------------------------------------------------

    private static async Task AssertCorners(SymbolCorners actual, (SKPoint TopLeft, SKPoint TopRight, SKPoint BottomRight, SKPoint BottomLeft) expected, float tolerance, bool mirrored)
    {
        await Assert.That(actual.IsEmpty).IsFalse();

        await AssertNear(actual.TopLeft, expected.TopLeft, tolerance, "TopLeft");
        await AssertNear(actual.TopRight, expected.TopRight, tolerance, "TopRight");
        await AssertNear(actual.BottomRight, expected.BottomRight, tolerance, "BottomRight");
        await AssertNear(actual.BottomLeft, expected.BottomLeft, tolerance, "BottomLeft");

        // Winding in y-down coordinates: (TR − TL) × (BL − TL) is positive for a symbol
        // seen as printed and negative for a mirrored capture.
        var cross = (actual.TopRight.X - actual.TopLeft.X) * (actual.BottomLeft.Y - actual.TopLeft.Y)
                  - (actual.TopRight.Y - actual.TopLeft.Y) * (actual.BottomLeft.X - actual.TopLeft.X);
        await Assert.That(cross < 0).IsEqualTo(mirrored).Because($"winding must {(mirrored ? "reverse" : "stay")} (cross={cross})");
    }

    private static async Task AssertNear(ImagePoint actual, SKPoint expected, float tolerance, string corner)
    {
        var dx = actual.X - expected.X;
        var dy = actual.Y - expected.Y;
        var distance = MathF.Sqrt(dx * dx + dy * dy);
        await Assert.That(distance).IsLessThanOrEqualTo(tolerance)
            .Because($"{corner}: got ({actual.X:F2}, {actual.Y:F2}), expected ({expected.X:F2}, {expected.Y:F2}), off by {distance:F2}px");
    }
}
