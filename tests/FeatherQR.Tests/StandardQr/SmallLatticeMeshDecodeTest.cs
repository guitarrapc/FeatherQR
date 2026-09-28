using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;
using FeatherQR.SkiaSharp.Internals;
using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// Versions 7 to 13 bowed off the plane: no single projective map reads them, and the mesh over their 3 × 3 alignment lattice does.
/// </summary>
public class SmallLatticeMeshDecodeTest
{
    private static QRCodeData Symbol(int version)
        => QRCodeGenerator.Create($"FQR LATTICE v{version} 0123456789", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });

    /// <summary>
    /// One render a version; each fails on the commit before and reads under nudges of ±0.05 px/module, ±0.5° and ±0.05 module of bow.
    /// </summary>
    [Test]
    [Arguments(7, 5.953f, 202.83f, 1.439f)]
    [Arguments(8, 4.521f, 308.46f, 0.987f)]
    [Arguments(9, 4.972f, 131.75f, 1.393f)]
    [Arguments(10, 5.597f, 198.55f, 1.263f)]
    [Arguments(11, 3.222f, 265.36f, 1.132f)]
    [Arguments(12, 4.615f, 127.48f, 1.217f)]
    [Arguments(13, 5.241f, 194.28f, 1.087f)]
    public async Task BowedSymbol_ReadsThroughTheLatticeMesh(int version, float pixelsPerModule, float degrees, float bowModules)
    {
        var qr = Symbol(version);
        var (luminance, side) = BowedRenderer.Render(qr, pixelsPerModule, degrees, bowModules);

        // Premise: the transform through the true finder centres and the true bottom-right alignment centre does not read it
        var d = qr.Size;
        var (x0, y0) = BowedRenderer.ToPixel(d, pixelsPerModule, degrees, bowModules, side, 3.5f, 3.5f);
        var (x1, y1) = BowedRenderer.ToPixel(d, pixelsPerModule, degrees, bowModules, side, d - 3.5f, 3.5f);
        var (x2, y2) = BowedRenderer.ToPixel(d, pixelsPerModule, degrees, bowModules, side, d - 6.5f, d - 6.5f);
        var (x3, y3) = BowedRenderer.ToPixel(d, pixelsPerModule, degrees, bowModules, side, 3.5f, d - 3.5f);
        var fourPoint = PerspectiveTransform.QuadrilateralToQuadrilateral(3.5f, 3.5f, d - 3.5f, 3.5f, d - 6.5f, d - 6.5f, 3.5f, d - 3.5f, x0, y0, x1, y1, x2, y2, x3, y3);
        var threshold = Binarizer.ComputeOtsuThreshold(luminance);
        var modules = new byte[d * d];
        QRImageDecoder.SampleGridScalar(luminance, side, side, threshold, fourPoint, d, modules);
        await Assert.That(QRMatrixDecoder.DecodeMatrix(modules, d, new char[QRCodeDecoder.GetMaxDecodedLength(13)], out _, out _)).IsNotEqualTo(DecodeStatus.Success);

        var success = QRCodeDecoder.TryDecodeImage(luminance, side, side, out var text, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo($"FQR LATTICE v{version} 0123456789");
    }

    /// <summary>
    /// Each node is searched close to where the frame puts it before further out: on this symbol of dots shifted off their module centres, the tight window finds all four nodes and the wider one alone finds none.
    /// Reads under nudges of ±0.05 px/module, ±0.5° and ±0.02 module of shift; with the wider window alone it fails at 8 of those 9.
    /// </summary>
    [Test]
    public async Task DottedSymbol_ReadsWithTheTightWindowFirst()
    {
        const string Content = "FQR DOTS 00563";
        var qr = QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(7) });
        var (luminance, side) = DottedRenderer.Render(qr, 7.751f, 213.85f, 0.575f, -0.077f, -0.284f);

        var success = QRCodeDecoder.TryDecodeImage(luminance, side, side, out var text, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(Content);
    }

    /// <summary>The mesh is one more grid, not a looser read: a bowed symbol whose data is destroyed, alignment lattice kept, does not decode.</summary>
    [Test]
    [Arguments(7)]
    [Arguments(10)]
    public async Task BowedSymbolWithItsDataDestroyed_DoesNotDecode(int version)
    {
        var qr = Symbol(version);
        var lattice = new List<int>();
        foreach (var value in QRCodeConstants.AlignmentPatternBaseValues.Slice((version - 1) * 7, 7))
        {
            if (value != 0)
                lattice.Add(value);
        }
        bool NearLattice(int row, int column) => lattice.Exists(r => Math.Abs(row - r) <= 2) && lattice.Exists(c => Math.Abs(column - c) <= 2);
        // Every module between the finders' rows and columns but the alignment patterns, clear of both format and both version copies
        bool IsDark(int row, int column) => row >= 9 && column >= 9 && row < qr.Size - 11 && column < qr.Size - 11 && !NearLattice(row, column) ? !qr[row, column] : qr[row, column];
        var (luminance, side) = BowedRenderer.Render(IsDark, qr.Size, 5f, 200f, 1.2f);

        var success = QRCodeDecoder.TryDecodeImage(luminance, side, side, out var text, out var info);

        await Assert.That(success).IsFalse().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(string.Empty);
    }

    /// <summary>A version 7 symbol photographed bent off the plane, from the real-image fixtures (Apache-2.0), at every right angle.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RealImageBentOffThePlane_Decodes(int quarterTurns)
    {
        var path = Path.Combine(FixtureLoader.FixtureRoot, "RealImages", "zxing-cpp-samples", "qrcode-3", "22.webp");
        var expected = File.ReadAllText(Path.ChangeExtension(path, ".txt"));
        using var bitmap = SKBitmap.Decode(path);
        var luminance = new byte[bitmap.Width * bitmap.Height];
        BitmapLuminanceConverter.Convert(bitmap, luminance);
        var (turned, width, height) = NearestNeighbourRenderer.Turn(luminance, bitmap.Width, bitmap.Height, quarterTurns, mirror: false);

        var success = QRCodeDecoder.TryDecodeImage(turned, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because($"turned {quarterTurns * 90}°: {info.Status}");
        await Assert.That(text).IsEqualTo(expected);
    }
}
