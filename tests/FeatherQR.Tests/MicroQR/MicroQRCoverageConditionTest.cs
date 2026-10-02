using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.MicroQR;

namespace FeatherQR.Tests;

/// <summary>
/// Micro QR's own condition for reading a grid again by coverage (<see cref="MicroQRImageDecoder.BothWays{TGrid}"/>): an
/// orientation that got past its format information read that word exactly. Getting past it is not enough: texture reads a word
/// within 3 bits about half the time, and an exact one about 1 in 1,000.
/// </summary>
public class MicroQRCoverageConditionTest
{
    private const int QuietZone = 2;
    private const int PixelsPerModule = 6;

    /// <summary>
    /// An M4 whose data from row and column 10 on is inverted past what Reed-Solomon corrects, drawn as sampled or transposed, its
    /// format word exact or one module off; the grid sampled in the image's own orientation.
    /// </summary>
    [Test]
    [Arguments(false, false, true)]
    [Arguments(false, true, false)]
    [Arguments(true, false, true)]
    [Arguments(true, true, false)]
    public async Task MayReadByCoverage_OnlyWhenTheFormatWordReadExactly(bool mirrored, bool formatModuleOff, bool expected)
    {
        var data = MicroQRCodeGenerator.Create("MICRO QR M4 TEST", MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M4, QuietZoneSize = QuietZone });
        var size = data.Size - 2 * QuietZone;
        bool IsDark(int row, int column)
        {
            var (r, c) = mirrored ? (column - QuietZone, row - QuietZone) : (row - QuietZone, column - QuietZone);
            var dark = data[r + QuietZone, c + QuietZone];
            if (r >= 10 && c >= 10 && r < size && c < size)
                dark = !dark;
            // A format module of row 8, next to the finder
            if (formatModuleOff && r == 8 && c == 1)
                dark = !dark;
            return dark;
        }

        var side = data.Size * PixelsPerModule;
        var luminance = new byte[side * side];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
                luminance[y * side + x] = IsDark(y / PixelsPerModule, x / PixelsPerModule) ? (byte)0 : (byte)255;
        }
        var modules = new byte[size * size];
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
                modules[row * size + column] = IsDark(row + QuietZone, column + QuietZone) ? (byte)1 : (byte)0;
        }

        // Premise, of the orientation drawn alone: past its format information, failing at its data, its word exact or not as
        // drawn; the other orientation does not get past its format information, so the case asks the drawn one's condition
        var (drawn, other) = mirrored
            ? (Orientation(modules, new TransposedModules<MatrixModules>(new MatrixModules(size)), size), Orientation(modules, new MatrixModules(size), size))
            : (Orientation(modules, new MatrixModules(size), size), Orientation(modules, new TransposedModules<MatrixModules>(new MatrixModules(size)), size));
        await Assert.That(drawn).IsEqualTo((DecodeStatus.DataUncorrectable, !formatModuleOff))
            .Because("premise: the orientation drawn gets past its format information, its word exact unless a module is off");
        await Assert.That(other.Status).IsEqualTo(DecodeStatus.FormatInformationInvalid)
            .Because("premise: the other orientation does not get past its format information");

        var image = new ImageView(luminance, side, side, 128, default);
        const float origin = QuietZone * PixelsPerModule;
        var grid = new MicroQRImageDecoder.BothWays<MicroQRImageDecoder.AffineGrid>(new MicroQRImageDecoder.AffineGrid(origin, origin, PixelsPerModule, 0, 0, PixelsPerModule), size);
        var result = new SearchResult<MicroQRCodeDecodeInfo>(ReportRule.Furthest, default);
        grid.Decode(modules, image, new char[64], out _, out _, ref result);

        await Assert.That(grid.MayReadByCoverage(modules)).IsEqualTo(expected);
    }

    private static (DecodeStatus Status, bool ExactFormat) Orientation<TModules>(byte[] modules, TModules view, int size)
        where TModules : struct, IMicroQRModules
    {
        var status = MicroQRMatrixDecoder.DecodeMatrix(modules, view, size, new char[64], out _, out _);
        return (status, MicroQRMatrixDecoder.HasExactFormat(modules, view, size));
    }
}
