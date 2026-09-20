using FeatherQR.SkiaSharp;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// rMQR reads its version from the finder-side format copy through the finder's own frame,
/// before anything else refines it. The builder snaps every module to whole pixels, and
/// below 2 px/module a finder can measure 3-6 % over the symbol's pitch: at column 11 that is
/// most of a pixel, the copy does not read, and the symbol is lost before the sub-finder can
/// correct the scale. The decoder tries the scales nearest the measured one while the frames
/// built on them fail.
/// </summary>
public class RmQRFormatScaleTest
{
    /// <summary>R7x43-M holds 7 alphanumerics; every other version takes the longer payload.</summary>
    private static string ContentFor(RmQRVersion version) => version == RmQRVersion.R7x43 ? "RMQR 43" : "RMQR 12345";

    /// <summary>
    /// Builder renders at about 1.7 px/module whose finder measures 3.5-6 % over the symbol's
    /// pitch, so the finder-side format copy does not read at the measured scale, and R11x77 at
    /// 140 px, which reads its copy exactly at a scale a few percent off even upright; every
    /// right angle.
    /// </summary>
    [Test]
    [Arguments(RmQRVersion.R11x77, 137)]
    [Arguments(RmQRVersion.R13x99, 178)]
    [Arguments(RmQRVersion.R15x99, 178)]
    [Arguments(RmQRVersion.R11x77, 140)]
    public async Task Decode_SnappedModuleWidths_Decodes(RmQRVersion version, int sizePx)
    {
        var content = ContentFor(version);
        var data = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });
        using var bitmap = new RmQRCodeImageBuilder(data).WithSize(sizePx, sizePx).ToBitmap();

        foreach (var degrees in new[] { 0, 90, 180, 270 })
        {
            using var rotated = Rotate(bitmap, degrees);
            var success = RmQRCodeDecoder.TryDecode(rotated, out var text, out var info);

            await Assert.That(success).IsTrue().Because($"{version} at {sizePx} px, {degrees} deg: {info.Status}");
            await Assert.That(text).IsEqualTo(content);
            await Assert.That(info.Version).IsEqualTo(version);
        }
    }

    public static IEnumerable<(RmQRVersion, int)> FractionalSizes()
    {
        foreach (var version in new[] { RmQRVersion.R7x43, RmQRVersion.R11x77, RmQRVersion.R13x99, RmQRVersion.R15x99, RmQRVersion.R17x139 })
        {
            var modules = int.Parse(version.ToString().Split('x')[1]) + 4;
            for (var px = modules * 3 / 2; px <= modules * 5 / 2; px += 3)
                yield return (version, px);
        }
    }

    /// <summary>
    /// Builder sizes from 1.5 to 2.5 px/module, every third pixel, in all four right-angle
    /// rotations. The finder's scale error depends on which of its modules the snapping widened,
    /// so a rotation can make the copy read exactly at a wrong scale (R13x99 at 211 px turned
    /// 180° and 270°); the search has to go on while the frames fail.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(FractionalSizes))]
    public async Task Decode_FractionalBuilderSize_Decodes(RmQRVersion version, int sizePx)
    {
        var content = ContentFor(version);
        var data = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });
        using var bitmap = new RmQRCodeImageBuilder(data).WithSize(sizePx, sizePx).ToBitmap();

        foreach (var degrees in new[] { 0, 90, 180, 270 })
        {
            using var rotated = Rotate(bitmap, degrees);
            var success = RmQRCodeDecoder.TryDecode(rotated, out var text, out var info);

            await Assert.That(success).IsTrue().Because($"{version} at {sizePx} px, {degrees} deg: {info.Status}");
            await Assert.That(text).IsEqualTo(content);
        }
    }

    private static SKBitmap Rotate(SKBitmap source, int degrees)
    {
        var swap = degrees % 180 != 0;
        var rotated = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height);
        using var canvas = new SKCanvas(rotated);
        canvas.Clear(SKColors.White);
        canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);
        return rotated;
    }

    /// <summary>
    /// A finder with random modules where the format copy would be, and nothing else. Within the
    /// usual 3 bits, about a quarter of random reads match a format word, so over the searched
    /// scales one nearly always would, and the decoder would go on to sample a symbol that is not
    /// there. A corrected scale has to read an exact codeword; these seeds read one only within
    /// 1-3 bits.
    /// </summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(5)]
    public async Task Decode_FinderBesideRandomFormatBits_IsNotDetected(int seed)
    {
        var (luminance, width, height) = FinderBesideRandomFormatBits(seed, pixelsPerModule: 2);

        var success = RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out _, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.NotDetected);
    }

    /// <summary>
    /// From 6 px/module only the measured scale is read. A 7 px/module finder with a valid format
    /// word beside it, drawn at 78 % of the finder's pitch: the word reads exactly only at ×0.92, so
    /// a read at all means the search ran where it should not have.
    /// </summary>
    [Test]
    [Arguments(RmQRVersion.R11x77, RmQREccLevel.M)]
    [Arguments(RmQRVersion.R17x139, RmQREccLevel.H)]
    public async Task Decode_FormatWordOffScaleAtSevenPixels_IsNotRead(RmQRVersion version, RmQREccLevel eccLevel)
    {
        const int pixelsPerModule = 7, quietZone = 2, columns = 139, rows = 17;
        const float pitch = 0.78f;
        var word = FeatherQR.Internals.RmQR.RmQRConstants.GetFormatBits(version, eccLevel, subFinderSide: false);
        var dark = new bool[rows, columns];
        for (var r = 0; r < 7; r++)
        {
            for (var c = 0; c < 7; c++)
                dark[r, c] = r is 0 or 6 || c is 0 or 6 || (r is >= 2 and <= 4 && c is >= 2 and <= 4);
        }
        // The finder-side copy: bit c·5 + r at column 8 + c, row 1 + r; bits 15-17 at column 11, rows 1-3
        for (var bit = 0; bit < 18; bit++)
        {
            var (row, column) = bit < 15 ? (1 + bit % 5, 8 + bit / 5) : (1 + bit - 15, 11);
            dark[row, column] = (word >> bit & 1) != 0;
        }
        var width = (columns + 2 * quietZone) * pixelsPerModule;
        var height = (rows + 2 * quietZone) * pixelsPerModule;
        var luminance = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var r = y / pixelsPerModule - quietZone;
                var u = (float)x / pixelsPerModule - quietZone;
                var c = u < 7f ? (int)Math.Floor(u) : 7 + (int)((u - 7f) / pitch);
                luminance[y * width + x] = r >= 0 && c >= 0 && r < rows && c < columns && dark[r, c] ? (byte)0 : (byte)255;
            }
        }

        var success = RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out _, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.NotDetected);
    }

    /// <summary>
    /// A clean render whose sub-finder is painted out, so every frame is the unrefined one and the
    /// symbol reads only at a corrected scale. At 5.1 px/module it pins the cutoff from below: with
    /// the earlier 3 px cutoff this render does not decode.
    /// </summary>
    [Test]
    [Arguments(RmQRVersion.R7x59, 5.1f)]
    [Arguments(RmQRVersion.R7x77, 4.9f)]
    public async Task Decode_SubFinderPaintedOut_ReadsAtACorrectedScale(RmQRVersion version, float pixelsPerModule)
    {
        var data = RmQRCodeGenerator.Create("RMQR", RmQREccLevel.H, new RmQRCodeGeneratorOptions { Version = version, QuietZoneSize = 2 });
        int columns = data.Width, rows = data.Height;
        var dark = new bool[rows, columns];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
                dark[r, c] = data[r, c];
        }
        // The 5 × 5 sub-finder sits in the bottom-right corner of the module area
        for (var r = rows - 7; r < rows - 2; r++)
        {
            for (var c = columns - 7; c < columns - 2; c++)
                dark[r, c] = false;
        }
        int width = (int)(columns * pixelsPerModule), height = (int)(rows * pixelsPerModule);
        var luminance = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int r = (int)(y / pixelsPerModule), c = (int)(x / pixelsPerModule);
                luminance[y * width + x] = r < rows && c < columns && dark[r, c] ? (byte)0 : (byte)255;
            }
        }

        var success = RmQRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"{version} at {pixelsPerModule} px/module: {info.Status}");
        await Assert.That(text).IsEqualTo("RMQR");
    }

    private static (byte[] Luminance, int Width, int Height) FinderBesideRandomFormatBits(int seed, int pixelsPerModule)
    {
        const int quietZone = 2, columns = 139, rows = 17;
        var dark = new bool[rows, columns];
        for (var r = 0; r < 7; r++)
        {
            for (var c = 0; c < 7; c++)
                dark[r, c] = r is 0 or 6 || c is 0 or 6 || (r is >= 2 and <= 4 && c is >= 2 and <= 4);
        }
        var random = new Random(seed);
        for (var r = 0; r < 7; r++)
        {
            for (var c = 8; c < 13; c++)
                dark[r, c] = random.Next(2) == 0;
        }
        var width = (columns + 2 * quietZone) * pixelsPerModule;
        var height = (rows + 2 * quietZone) * pixelsPerModule;
        var luminance = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var r = y / pixelsPerModule - quietZone;
                var c = x / pixelsPerModule - quietZone;
                luminance[y * width + x] = r >= 0 && c >= 0 && r < rows && c < columns && dark[r, c] ? (byte)0 : (byte)255;
            }
        }
        return (luminance, width, height);
    }
}
