using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;
using Symbology = FeatherQR.Tests.UnevenLightingDecodeTest.Symbology;

namespace FeatherQR.Tests;

/// <summary>
/// The allocation-free decode overloads of the three decoders allocate nothing once warm: at the matrix level with and without a quiet zone and on a symbol damaged past correction, and at the image level on each path it reads through and on images it rejects.
/// A change to how the decoders are put together is held to this class: a stage moved, shared or given a buffer of its own cannot put an allocation on any of these paths unnoticed.
/// </summary>
/// <remarks>
/// <para>
/// Release only: an unoptimized build allocates where an optimized one does not, a span initialized from a list of <see cref="int"/> or <see cref="float"/> values among others.
/// </para>
/// <para>
/// Runs alone. The decoders rent from <see cref="System.Buffers.ArrayPool{T}"/>, whose thread slot holds one array a size; a second array of that size, which Standard QR's grids take while the first is out, comes from the per-core stacks every thread shares, and a test running beside this one can empty them.
/// With this class's tests running beside each other, the Standard QR decoder allocated 4,120 B on noise in every round in one of six runs on .NET 8; alone it allocated nothing in every run.
/// </para>
/// <para>
/// Where another test pins an image to the path it reads through, the image here is that one, drawn the same way, and its scene names the test.
/// The earlier zero-allocation tests (the matrix overloads in <see cref="QRCodeDecoderRoundTripTest"/> and <see cref="RmQRCodeDecoderRoundTripTest"/>, the rMQR images in <see cref="RmQRCodeDecoderImageTest"/> and <see cref="RmQRStrongKeystoneDecodeTest"/>) stay where they are.
/// </para>
/// </remarks>
[NotInParallel]
public class DecodeAllocationTest
{
#if !DEBUG
    /// <summary>
    /// The bytes <paramref name="call"/> allocates once warm: the JIT has compiled it, the static tables are built and the pools hold its buffers.
    /// </summary>
    /// <remarks>
    /// The fewest over a few rounds of calls: tests run in parallel, and the shared array pool can drop a buffer when a collection runs meanwhile, so one round can see a rent allocate once.
    /// A call that allocates every time allocates in every round.
    /// </remarks>
    private static long SteadyStateBytes(Action call)
    {
        const int WarmUp = 3;
        const int Rounds = 3;
        const int CallsPerRound = 4;

        for (var i = 0; i < WarmUp; i++)
            call();

        var fewest = long.MaxValue;
        for (var round = 0; round < Rounds; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < CallsPerRound; i++)
                call();
            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return fewest;
    }

    private static readonly int MaxStandardQRChars = QRCodeDecoder.GetMaxDecodedLength(40);
    private static readonly int MaxMicroQRChars = Enum.GetValues<MicroQRVersion>().Max(MicroQRCodeDecoder.GetMaxDecodedLength);
    private static readonly int MaxRmQRChars = Enum.GetValues<RmQRVersion>().Max(RmQRCodeDecoder.GetMaxDecodedLength);

    #region Matrix level

    /// <summary>A module buffer from the generator's span overload, the smallest version or a large one, quiet zone as given; and which modules of its core are function modules.</summary>
    private static (byte[] Modules, int Width, int Height, string Content, Func<int, int, bool> IsFunction) Matrix(Symbology symbology, bool large, int quietZone)
    {
        switch (symbology)
        {
            case Symbology.StandardQR:
                {
                    // Version 10 is past the matrix decoder's stack budget for its codewords, so its work buffer is rented
                    var (content, version) = large ? ("FQR 2.0 MATRIX LEVEL 0123456789", 10) : ("FQR 2.0", 1);
                    var options = new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = quietZone };
                    var calculated = Sizing.Required(content, QREccLevel.M, options);
                    var buffer = new byte[calculated.BufferSize];
                    QRCodeGenerator.Create(content, QREccLevel.M, buffer, options);
                    // The encoder's blocked mask: function patterns and the reserved format and version areas
                    var layout = ModulePlacer.GetLayout(version);
                    return (buffer, calculated.Size, calculated.Size, content, (row, column) => (layout.BlockedMask[(row * layout.Size + column) >> 3] & (1 << ((row * layout.Size + column) & 7))) != 0);
                }
            case Symbology.MicroQR:
                {
                    var (content, version, level) = large ? ("MICRO QR M4 TEST", MicroQRVersion.M4, MicroQREccLevel.M) : ("12345", MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly);
                    var options = new MicroQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone };
                    var calculated = Sizing.Required(content, level, options);
                    var buffer = new byte[calculated.BufferSize];
                    MicroQRCodeGenerator.Create(content, level, buffer, options);
                    return (buffer, calculated.Size, calculated.Size, content, MicroQRModulePlacer.IsFunctionModule);
                }
            default:
                {
                    var (content, version) = large ? ("RMQR IMAGE 123", RmQRVersion.R17x139) : ("1234", RmQRVersion.R7x43);
                    var options = new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = quietZone };
                    var calculated = Sizing.Required(content, RmQREccLevel.M, options);
                    var buffer = new byte[calculated.BufferSize];
                    RmQRCodeGenerator.Create(content, RmQREccLevel.M, buffer, options);
                    return (buffer, calculated.Width, calculated.Height, content, (row, column) => RmQRModulePlacer.IsFunctionModule(version, row, column));
                }
        }
    }

    private static bool DecodeMatrix(Symbology symbology, byte[] modules, int width, int height, char[] destination, out int charsWritten, out DecodeStatus status)
    {
        bool success;
        switch (symbology)
        {
            case Symbology.StandardQR:
                success = QRCodeDecoder.TryDecode(modules, width, destination, out charsWritten, out var info);
                status = info.Status;
                break;
            case Symbology.MicroQR:
                success = MicroQRCodeDecoder.TryDecode(modules, width, destination, out charsWritten, out var microInfo);
                status = microInfo.Status;
                break;
            default:
                success = RmQRCodeDecoder.TryDecode(modules, width, height, destination, out charsWritten, out var rmqrInfo);
                status = rmqrInfo.Status;
                break;
        }
        return success;
    }

    private static char[] Destination(Symbology symbology)
        => new char[symbology switch { Symbology.StandardQR => MaxStandardQRChars, Symbology.MicroQR => MaxMicroQRChars, _ => MaxRmQRChars }];

    /// <summary>A quiet zone makes the overload copy the core out of the bordered input before it decodes.</summary>
    [Test]
    [Arguments(Symbology.StandardQR, false, 0)]
    [Arguments(Symbology.StandardQR, true, 0)]
    [Arguments(Symbology.StandardQR, false, 4)]
    [Arguments(Symbology.StandardQR, true, 4)]
    [Arguments(Symbology.MicroQR, false, 0)]
    [Arguments(Symbology.MicroQR, true, 0)]
    [Arguments(Symbology.MicroQR, false, 2)]
    [Arguments(Symbology.MicroQR, true, 2)]
    [Arguments(Symbology.RmQR, false, 0)]
    [Arguments(Symbology.RmQR, true, 0)]
    [Arguments(Symbology.RmQR, false, 2)]
    [Arguments(Symbology.RmQR, true, 2)]
    public async Task Matrix_ReadsWithoutAllocating(Symbology symbology, bool large, int quietZone)
    {
        var (modules, width, height, content, _) = Matrix(symbology, large, quietZone);
        var destination = Destination(symbology);

        var success = DecodeMatrix(symbology, modules, width, height, destination, out var charsWritten, out var status);
        await Assert.That(success).IsTrue().Because(status.ToString());
        await Assert.That(new string(destination, 0, charsWritten)).IsEqualTo(content);

        await Assert.That(SteadyStateBytes(() => DecodeMatrix(symbology, modules, width, height, destination, out _, out _))).IsEqualTo(0L);
    }

    /// <summary>
    /// Every data module in the lower half of the core inverted, function modules and the format and version information left as drawn: more codewords damaged than any level corrects, so the decode fails in the Reed-Solomon stage, past every buffer the matrix level takes.
    /// </summary>
    [Test]
    [Arguments(Symbology.StandardQR, false, 0)]
    [Arguments(Symbology.StandardQR, true, 4)]
    [Arguments(Symbology.MicroQR, false, 0)]
    [Arguments(Symbology.MicroQR, true, 2)]
    [Arguments(Symbology.RmQR, false, 0)]
    [Arguments(Symbology.RmQR, true, 2)]
    public async Task Matrix_DamagedPastCorrection_FailsWithoutAllocating(Symbology symbology, bool large, int quietZone)
    {
        var (modules, width, height, _, isFunction) = Matrix(symbology, large, quietZone);
        var coreWidth = width - 2 * quietZone;
        var coreHeight = height - 2 * quietZone;
        for (var row = coreHeight / 2; row < coreHeight; row++)
        {
            for (var column = 0; column < coreWidth; column++)
            {
                if (!isFunction(row, column))
                    modules[(row + quietZone) * width + column + quietZone] ^= 1;
            }
        }
        var destination = Destination(symbology);

        var success = DecodeMatrix(symbology, modules, width, height, destination, out _, out var status);
        await Assert.That(success).IsFalse();
        await Assert.That(status).IsEqualTo(DecodeStatus.DataUncorrectable);

        await Assert.That(SteadyStateBytes(() => DecodeMatrix(symbology, modules, width, height, destination, out _, out _))).IsEqualTo(0L);
    }

    #endregion

    #region Image level: the scenes

    private readonly record struct Scene(byte[] Luminance, int Width, int Height, string Content);

    private static Scene Crisp(Func<int, int, bool> isDark, int columns, int rows, string content, float pixelsPerModule = 4f, float offset = 0f)
    {
        var (luminance, width, height) = NearestNeighbourRenderer.Render(isDark, columns, rows, pixelsPerModule, offset, offset);
        return new Scene(luminance, width, height, content);
    }

    private static Scene Mirrored(Scene scene)
    {
        var (luminance, width, height) = NearestNeighbourRenderer.Turn(scene.Luminance, scene.Width, scene.Height, quarterTurns: 1, mirror: true);
        return scene with { Luminance = luminance, Width = width, Height = height };
    }

    private static Scene Negated(Scene scene)
    {
        var luminance = scene.Luminance.ToArray();
        for (var i = 0; i < luminance.Length; i++)
            luminance[i] = (byte)(255 - luminance[i]);
        return scene with { Luminance = luminance };
    }

    private static Scene Supersampled(Func<int, int, bool> isDark, int columns, int rows, string content, float pixelsPerModule, float degrees, float keystone = 0f)
    {
        var (luminance, width, height) = SupersampledRenderer.Render(isDark, columns, rows, pixelsPerModule, degrees, keystone);
        return new Scene(luminance, width, height, content);
    }

    /// <summary>The lighting <see cref="UnevenLightingDecodeTest"/> pins to the regional pass for every symbology: a shadow across the middle.</summary>
    private static Scene Shadowed(Func<int, int, bool> isDark, int columns, int rows, string content)
    {
        var (luminance, width, height) = UnevenLightingRenderer.Render(isDark, columns, rows, 4, UnevenLight.Shadow, 0f, 0.55f);
        return new Scene(luminance, width, height, content);
    }

    /// <summary>What the image level does on each kind of input it reads, in the order the pipeline reaches for it.</summary>
    public enum ImageScene
    {
        /// <summary>Crisp and upright at 4 px/module. Standard QR: version 1, the finders' parallelogram with no alignment pattern.</summary>
        Upright,
        /// <summary>Supersampled (grey edges) and turned: finder centres and module sizes measured on grey levels. Standard QR: version 3, the alignment-anchored four-point transform. Micro QR: the orientation sweep.</summary>
        Turned,
        /// <summary>Mirrored and turned a quarter: the transposed read.</summary>
        Mirrored,
        /// <summary>Supersampled under keystone. Standard QR: <see cref="LargeVersionGridOrderTest.AnchoredIntactSymbol_ReadsThroughTheFourPointTransform"/> (version 14, 4 px/module, 20°, 0.16), the finders' frame and the four-point transform.</summary>
        Keystone,
        /// <summary>Standard QR only: <see cref="KeystoneFinderDecodeTest.FalseCandidateInTheSelectedTriple_TheNextConfirmedTripleDecodes"/> (version 40, 4.11 px/module, 268.9°, 0.444), the selected triple failing and the next one by confirmation reading.</summary>
        NextTriple,
        /// <summary>Standard QR only: <see cref="LargeVersionGridOrderTest.LostAlignmentPattern_ReadsThroughTheMeshFirst"/> (version 35, 5 px/module, 200°), the mesh when nothing anchors the fourth corner.</summary>
        MeshFirst,
        /// <summary>Standard QR only: <see cref="LargeVersionGridOrderTest.BowedSymbol_ReadsThroughTheMeshAfterTheAnchoredTransform"/> (version 14, 5 px/module, 0°, bowed 1 module), the mesh after the anchored grid fails.</summary>
        MeshAfterAnchoredGrid,
        /// <summary>Micro QR only: crisp at 1.4 px/module, where whole-pixel modules put the finder's size a few percent off: the frame measured on the timing patterns.</summary>
        SnappedToPixels,
        /// <summary>Crisp between 1 and 1.5 px/module (1.15 for Micro QR, where 1.4 still reads through the timing frame; 1.4 for the others), under the density a grid extrapolated from the finders holds: the module boundaries.</summary>
        LowDensity,
        /// <summary>rMQR only: <see cref="RmQRPerimeterTraceTest.Decode_LowDensityTraceFails_AnchoredGridStillReads"/> (R17x99 anti-aliased at 1.31 px/module), the traced grid failing in the data and the anisotropic grid reading.</summary>
        TracedGridFails,
        /// <summary>rMQR only: the case of <see cref="RmQRStrongKeystoneDecodeTest.DegradedCases"/> read by the perspective search behind the trace.</summary>
        PerspectiveSearch,
        /// <summary>Light modules on a dark ground: the inverted pass, the global one having failed.</summary>
        LightOnDark,
        /// <summary>A shadow across the middle: the regional pass, both global ones having failed.</summary>
        UnevenLight,
    }

    public static IEnumerable<ImageScene> StandardQRScenes()
        => [ImageScene.Upright, ImageScene.Turned, ImageScene.Mirrored, ImageScene.Keystone, ImageScene.NextTriple, ImageScene.MeshFirst, ImageScene.MeshAfterAnchoredGrid, ImageScene.LowDensity, ImageScene.LightOnDark, ImageScene.UnevenLight];

    public static IEnumerable<ImageScene> MicroQRScenes()
        => [ImageScene.Upright, ImageScene.Turned, ImageScene.Mirrored, ImageScene.Keystone, ImageScene.SnappedToPixels, ImageScene.LowDensity, ImageScene.LightOnDark, ImageScene.UnevenLight];

    public static IEnumerable<ImageScene> RmQRScenes()
        => [ImageScene.Upright, ImageScene.Turned, ImageScene.Mirrored, ImageScene.Keystone, ImageScene.LowDensity, ImageScene.TracedGridFails, ImageScene.PerspectiveSearch, ImageScene.LightOnDark, ImageScene.UnevenLight];

    private static QRCodeData StandardQR(string content, int version, int quietZone = 4)
        => QRCodeGenerator.Create(content, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = quietZone });

    /// <summary><see cref="LargeVersionGridOrderTest"/>'s symbol, so its pinned renders are the same images.</summary>
    private static QRCodeData GridOrderSymbol(int version)
        => StandardQR($"FQR MESH ORDER v{version} 0123456789", version, quietZone: 0);

    private static Scene Render(ImageScene scene)
    {
        const string Content = "FQR 2.0";
        switch (scene)
        {
            case ImageScene.Upright:
                {
                    var qr = StandardQR(Content, 1);
                    return Crisp((row, column) => qr[row, column], qr.Size, qr.Size, Content);
                }
            case ImageScene.Turned:
                {
                    var qr = StandardQR(Content, 3, quietZone: 0);
                    return Supersampled((row, column) => qr[row, column], qr.Size, qr.Size, Content, 4f, 10f);
                }
            case ImageScene.Mirrored:
                {
                    var qr = StandardQR(Content, 3);
                    return Mirrored(Crisp((row, column) => qr[row, column], qr.Size, qr.Size, Content));
                }
            case ImageScene.Keystone:
                {
                    var qr = GridOrderSymbol(14);
                    return Supersampled((row, column) => qr[row, column], qr.Size, qr.Size, $"FQR MESH ORDER v14 0123456789", 4f, 20f, 0.16f);
                }
            case ImageScene.NextTriple:
                {
                    var (luminance, width, height, _, _) = KeystoneFinderDecodeTest.Render(40, 4.11f, 268.9f, 0.444f);
                    return new Scene(luminance, width, height, KeystoneFinderDecodeTest.Content);
                }
            case ImageScene.MeshFirst:
                {
                    var qr = GridOrderSymbol(35);
                    return Supersampled(LargeVersionGridOrderTest.WithoutBottomRightAlignment(qr), qr.Size, qr.Size, $"FQR MESH ORDER v35 0123456789", 5f, 200f, 0.004f);
                }
            case ImageScene.MeshAfterAnchoredGrid:
                {
                    var qr = GridOrderSymbol(14);
                    var (luminance, side) = BowedRenderer.Render(qr, 5f, 0f, 1f);
                    return new Scene(luminance, side, side, $"FQR MESH ORDER v14 0123456789");
                }
            case ImageScene.LowDensity:
                {
                    var qr = StandardQR(Content, 3);
                    return Crisp((row, column) => qr[row, column], qr.Size, qr.Size, Content, 1.4f, 0.3f);
                }
            case ImageScene.LightOnDark:
                {
                    var qr = StandardQR(Content, 3);
                    return Negated(Crisp((row, column) => qr[row, column], qr.Size, qr.Size, Content));
                }
            case ImageScene.UnevenLight:
                {
                    // UnevenLightingDecodeTest's symbol
                    var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = 3 });
                    return Shadowed((row, column) => qr[row, column], qr.Size, qr.Size, Content);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(scene), scene, "not a Standard QR scene");
        }
    }

    private static Scene RenderMicroQR(ImageScene scene)
    {
        const string Content = "MICRO QR M4 TEST";
        switch (scene)
        {
            case ImageScene.Upright:
                {
                    var qr = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M2 });
                    return Crisp((row, column) => qr[row, column], qr.Size, qr.Size, "12345");
                }
            case ImageScene.Turned:
                {
                    var qr = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
                    return Supersampled((row, column) => qr[row, column], qr.Size, qr.Size, Content, 5f, 30f);
                }
            case ImageScene.Mirrored:
                {
                    var qr = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.M);
                    return Mirrored(Crisp((row, column) => qr[row, column], qr.Size, qr.Size, Content));
                }
            case ImageScene.Keystone:
                {
                    var qr = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
                    return Supersampled((row, column) => qr[row, column], qr.Size, qr.Size, Content, 5f, 0f, 0.08f);
                }
            case ImageScene.SnappedToPixels:
                {
                    var qr = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.M);
                    return Crisp((row, column) => qr[row, column], qr.Size, qr.Size, Content, 1.4f, 0.3f);
                }
            case ImageScene.LowDensity:
                {
                    var qr = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.M);
                    return Crisp((row, column) => qr[row, column], qr.Size, qr.Size, Content, 1.15f, 0.25f);
                }
            case ImageScene.LightOnDark:
                {
                    var qr = MicroQRCodeGenerator.Create(Content, MicroQREccLevel.M);
                    return Negated(Crisp((row, column) => qr[row, column], qr.Size, qr.Size, Content));
                }
            case ImageScene.UnevenLight:
                {
                    // UnevenLightingDecodeTest's symbol
                    var qr = MicroQRCodeGenerator.Create("FQR 2.0", MicroQREccLevel.L);
                    return Shadowed((row, column) => qr[row, column], qr.Size, qr.Size, "FQR 2.0");
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(scene), scene, "not a Micro QR scene");
        }
    }

    private static Scene RenderRmQR(ImageScene scene)
    {
        const string Content = "RMQR IMAGE 123";
        switch (scene)
        {
            case ImageScene.Upright:
                {
                    var qr = RmQRCodeGenerator.Create(Content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x77 });
                    return Crisp((row, column) => qr[row, column], qr.Width, qr.Height, Content);
                }
            case ImageScene.Turned:
                {
                    var qr = RmQRCodeGenerator.Create(Content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R13x99, QuietZoneSize = 0 });
                    return Supersampled((row, column) => qr[row, column], qr.Width, qr.Height, Content, 4f, 20f);
                }
            case ImageScene.Mirrored:
                {
                    var qr = RmQRCodeGenerator.Create(Content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x77 });
                    return Mirrored(Crisp((row, column) => qr[row, column], qr.Width, qr.Height, Content));
                }
            case ImageScene.Keystone:
                {
                    var qr = RmQRCodeGenerator.Create(Content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139, QuietZoneSize = 0 });
                    return Supersampled((row, column) => qr[row, column], qr.Width, qr.Height, Content, 4.5f, 17f, 0.1f);
                }
            case ImageScene.LowDensity:
                {
                    var qr = RmQRCodeGenerator.Create(Content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x77 });
                    return Crisp((row, column) => qr[row, column], qr.Width, qr.Height, Content, 1.4f, 0.3f);
                }
            case ImageScene.TracedGridFails:
                {
                    // RmQRPerimeterTraceTest's render, drawn the same way
                    const string TracedContent = "QKDRSVtoGExL/eYnEKj3I1=9/UUlX_8fPY??-e?=a?AW3wZznDEAkCG5MKW&aiIMNr?nh1uANl";
                    var qr = RmQRCodeGenerator.Create(TracedContent, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x99, QuietZoneSize = 2 });
                    var (luminance, width, height) = AntiAliasedRenderer.Render((row, column) => qr[row, column], qr.Width, qr.Height, 1.3149171f, 0.9328947f, 0.05425163f);
                    return new Scene(luminance, width, height, TracedContent);
                }
            case ImageScene.PerspectiveSearch:
                {
                    // RmQRStrongKeystoneDecodeTest's case "the perspective search behind the trace"
                    const string SearchedContent = "PYQ4UW%7%C1/N- EQ+XQ56UHJK*X5VZDW:BEPXOF10RP 7FY7O:CQEGI";
                    var qr = RmQRCodeGenerator.Create(SearchedContent, RmQREccLevel.H, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R13x139, QuietZoneSize = 0 });
                    var (luminance, side) = RmQRStrongKeystoneDecodeTest.Degraded(qr, 118804, 3f, 6f, 0f, RmQRStrongKeystoneDecodeTest.Degradation.LowContrastNoise);
                    return new Scene(luminance, side, side, SearchedContent);
                }
            case ImageScene.LightOnDark:
                {
                    var qr = RmQRCodeGenerator.Create(Content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x77 });
                    return Negated(Crisp((row, column) => qr[row, column], qr.Width, qr.Height, Content));
                }
            case ImageScene.UnevenLight:
                {
                    // UnevenLightingDecodeTest's symbol
                    var qr = RmQRCodeGenerator.Create("FQR 2.0", RmQREccLevel.M);
                    return Shadowed((row, column) => qr[row, column], qr.Width, qr.Height, "FQR 2.0");
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(scene), scene, "not an rMQR scene");
        }
    }

    /// <summary>The scene's premise where the pipeline's own passes decide its path: the passes before the one it is about do not read it.</summary>
    private static async Task AssertEarlierPassesFail(Symbology symbology, ImageScene scene, Scene image)
    {
        if (scene == ImageScene.LightOnDark)
        {
            var histogram = new int[Binarizer.HistogramBins];
            Binarizer.FillHistogram(image.Luminance, histogram);
            var destination = new char[MaxStandardQRChars];
            var positive = symbology switch
            {
                Symbology.StandardQR => QRImageDecoder.DecodeLuminanceCore(image.Luminance, histogram, image.Width, image.Height, destination, out _, out _),
                Symbology.MicroQR => MicroQRImageDecoder.DecodeLuminanceCore(image.Luminance, histogram, image.Width, image.Height, destination, out _, out _),
                _ => RmQRImageDecoder.DecodeLuminanceCore(image.Luminance, histogram, image.Width, image.Height, destination, out _, out _),
            };
            await Assert.That(positive).IsNotEqualTo(DecodeStatus.Success).Because("the global positive pass must not read it");
        }
        else if (scene == ImageScene.UnevenLight)
        {
            await Assert.That(UnevenLightingDecodeTest.GlobalAttemptsRead(symbology, image.Luminance, image.Width, image.Height)).IsFalse().Because("neither global pass may read it");
        }
    }

    #endregion

    #region Image level: reads

    [Test]
    [MethodDataSource(nameof(StandardQRScenes))]
    public async Task StandardQR_Image_ReadsWithoutAllocating(ImageScene scene)
    {
        var image = Render(scene);
        await AssertEarlierPassesFail(Symbology.StandardQR, scene, image);
        var destination = new char[MaxStandardQRChars];

        var success = QRCodeDecoder.TryDecodeImage(image.Luminance, image.Width, image.Height, destination, out var charsWritten, out var info);
        await Assert.That(success).IsTrue().Because($"{scene}: {info.Status}");
        await Assert.That(new string(destination, 0, charsWritten)).IsEqualTo(image.Content);

        await Assert.That(SteadyStateBytes(() => QRCodeDecoder.TryDecodeImage(image.Luminance, image.Width, image.Height, destination, out _, out _))).IsEqualTo(0L);
    }

    [Test]
    [MethodDataSource(nameof(MicroQRScenes))]
    public async Task MicroQR_Image_ReadsWithoutAllocating(ImageScene scene)
    {
        var image = RenderMicroQR(scene);
        await AssertEarlierPassesFail(Symbology.MicroQR, scene, image);
        var destination = new char[MaxMicroQRChars];

        var success = MicroQRCodeDecoder.TryDecodeImage(image.Luminance, image.Width, image.Height, destination, out var charsWritten, out var info);
        await Assert.That(success).IsTrue().Because($"{scene}: {info.Status}");
        await Assert.That(new string(destination, 0, charsWritten)).IsEqualTo(image.Content);

        await Assert.That(SteadyStateBytes(() => MicroQRCodeDecoder.TryDecodeImage(image.Luminance, image.Width, image.Height, destination, out _, out _))).IsEqualTo(0L);
    }

    [Test]
    [MethodDataSource(nameof(RmQRScenes))]
    public async Task RmQR_Image_ReadsWithoutAllocating(ImageScene scene)
    {
        var image = RenderRmQR(scene);
        await AssertEarlierPassesFail(Symbology.RmQR, scene, image);
        var destination = new char[MaxRmQRChars];

        var success = RmQRCodeDecoder.TryDecodeImage(image.Luminance, image.Width, image.Height, destination, out var charsWritten, out var info);
        await Assert.That(success).IsTrue().Because($"{scene}: {info.Status}");
        await Assert.That(new string(destination, 0, charsWritten)).IsEqualTo(image.Content);

        await Assert.That(SteadyStateBytes(() => RmQRCodeDecoder.TryDecodeImage(image.Luminance, image.Width, image.Height, destination, out _, out _))).IsEqualTo(0L);
    }

    #endregion

    #region Image level: rejections

    /// <summary>An image a decoder must not read: every pass runs, and each candidate it finds is tried to the end.</summary>
    public enum RejectedImage
    {
        /// <summary>White: no finder, and no grey level for a regional pass.</summary>
        Blank,
        /// <summary>Seeded uniform noise: grey levels everywhere, so the regional pass runs, and Standard QR's failure path from a triple of false candidates to its other grids.</summary>
        Noise,
        /// <summary>
        /// A supersampled, turned Standard QR symbol: finders the other decoders try and refuse.
        /// This and the other symbologies' symbols, not noise, are what take Micro QR and rMQR to their midpoint pass.
        /// </summary>
        StandardQRSymbol,
        /// <summary>A supersampled, turned Micro QR symbol.</summary>
        MicroQRSymbol,
        /// <summary>A supersampled, turned rMQR symbol.</summary>
        RmQRSymbol,
    }

    public static IEnumerable<RejectedImage> RejectedByStandardQR() => [RejectedImage.Blank, RejectedImage.Noise, RejectedImage.MicroQRSymbol, RejectedImage.RmQRSymbol];

    public static IEnumerable<RejectedImage> RejectedByMicroQR() => [RejectedImage.Blank, RejectedImage.Noise, RejectedImage.StandardQRSymbol, RejectedImage.RmQRSymbol];

    public static IEnumerable<RejectedImage> RejectedByRmQR() => [RejectedImage.Blank, RejectedImage.Noise, RejectedImage.StandardQRSymbol, RejectedImage.MicroQRSymbol];

    private static (byte[] Luminance, int Width, int Height) Render(RejectedImage image)
    {
        switch (image)
        {
            case RejectedImage.Blank:
                {
                    var luminance = new byte[160 * 160];
                    Array.Fill(luminance, (byte)255);
                    return (luminance, 160, 160);
                }
            case RejectedImage.Noise:
                {
                    var luminance = new byte[240 * 240];
                    new Random(20260929).NextBytes(luminance);
                    return (luminance, 240, 240);
                }
            case RejectedImage.StandardQRSymbol:
                {
                    var qr = StandardQR("FQR 2.0", 3, quietZone: 0);
                    return SupersampledRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, 4f, 10f);
                }
            case RejectedImage.MicroQRSymbol:
                {
                    var qr = MicroQRCodeGenerator.Create("MICRO QR M4 TEST", MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { QuietZoneSize = 0 });
                    return SupersampledRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, 5f, 10f);
                }
            default:
                {
                    var qr = RmQRCodeGenerator.Create("RMQR IMAGE 123", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R13x99, QuietZoneSize = 0 });
                    return SupersampledRenderer.Render((row, column) => qr[row, column], qr.Width, qr.Height, 4f, 10f);
                }
        }
    }

    [Test]
    [MethodDataSource(nameof(RejectedByStandardQR))]
    public async Task StandardQR_Image_RejectsWithoutAllocating(RejectedImage rejected)
    {
        var (luminance, width, height) = Render(rejected);
        var destination = new char[MaxStandardQRChars];

        await Assert.That(QRCodeDecoder.TryDecodeImage(luminance, width, height, destination, out _, out _)).IsFalse();

        await Assert.That(SteadyStateBytes(() => QRCodeDecoder.TryDecodeImage(luminance, width, height, destination, out _, out _))).IsEqualTo(0L);
    }

    [Test]
    [MethodDataSource(nameof(RejectedByMicroQR))]
    public async Task MicroQR_Image_RejectsWithoutAllocating(RejectedImage rejected)
    {
        var (luminance, width, height) = Render(rejected);
        var destination = new char[MaxMicroQRChars];

        await Assert.That(MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, destination, out _, out _)).IsFalse();

        await Assert.That(SteadyStateBytes(() => MicroQRCodeDecoder.TryDecodeImage(luminance, width, height, destination, out _, out _))).IsEqualTo(0L);
    }

    [Test]
    [MethodDataSource(nameof(RejectedByRmQR))]
    public async Task RmQR_Image_RejectsWithoutAllocating(RejectedImage rejected)
    {
        var (luminance, width, height) = Render(rejected);
        var destination = new char[MaxRmQRChars];

        await Assert.That(RmQRCodeDecoder.TryDecodeImage(luminance, width, height, destination, out _, out _)).IsFalse();

        await Assert.That(SteadyStateBytes(() => RmQRCodeDecoder.TryDecodeImage(luminance, width, height, destination, out _, out _))).IsEqualTo(0L);
    }

    #endregion

    #region Standard QR: one module buffer at a time

    /// <summary>The most module buffers the Standard QR decoder had out at once while it decoded the image.</summary>
    private static int ModuleBuffersPeak(byte[] luminance, int width, int height)
    {
        QRImageDecoder.ModuleBuffersPeak = 0;
        QRCodeDecoder.TryDecodeImage(luminance, width, height, new char[MaxStandardQRChars], out _, out _);
        return QRImageDecoder.ModuleBuffersPeak;
    }

    /// <summary>
    /// Every read holds one module buffer at a time. The thread's slot of the array pool holds one array a size, so a second buffer of that size would come from the per-core stacks every thread shares, where a thread decoding beside this one can take it and the rent allocates.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(StandardQRScenes))]
    public async Task StandardQR_Image_ReadHoldsOneModuleBufferAtATime(ImageScene scene)
    {
        var image = Render(scene);

        await Assert.That(ModuleBuffersPeak(image.Luminance, image.Width, image.Height)).IsEqualTo(1);
    }

    /// <summary>A rejected image holds at most one: noise samples the grids of false triples, and the other images find no triple at all.</summary>
    [Test]
    [MethodDataSource(nameof(RejectedByStandardQR))]
    public async Task StandardQR_Image_RejectionHoldsOneModuleBufferAtATime(RejectedImage rejected)
    {
        var (luminance, width, height) = Render(rejected);

        await Assert.That(ModuleBuffersPeak(luminance, width, height)).IsLessThanOrEqualTo(rejected == RejectedImage.Noise ? 1 : 0);
    }

    #endregion
#endif
}
