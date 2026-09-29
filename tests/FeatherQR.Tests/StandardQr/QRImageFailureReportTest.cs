using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// Which failure a Standard QR image decode reports: at each level of its search the main path's attempt, unless another settles, so the version and level of a failure are those of the grid the symbol's own measurements gave.
/// These hold it on whole images; <see cref="FinderTripleCornersTest"/> holds it between the corners and triples of a search.
/// </summary>
/// <remarks>
/// Micro QR and rMQR report the failure that got furthest instead. Standard QR does not (D5, declined 2026-09-30, decode-pipeline-structure-plan.md): its format word names no version, so a grid sampled at a guessed dimension gets past it about half the time and fails at Reed-Solomon with that dimension's version.
/// Under the furthest rule every one of these symbols reported <see cref="DecodeStatus.DataUncorrectable"/> one version too small.
/// </remarks>
public class QRImageFailureReportTest
{
    /// <summary>
    /// Every grid of a symbol whose format information is destroyed fails at it: the estimate's grid, with the symbol's own version, is reported, not a guessed dimension's grid that got past the format word by chance.
    /// On an image with grey levels each grid is read again by coverage, and a crisp render samples the same modules both ways, so that re-read is skipped; a skip is no attempt, and is not reported either.
    /// </summary>
    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(7, false)]
    [Arguments(7, true)]
    public async Task FormatInformationDestroyed_ReportsTheFormatFailure_WithTheVersion(int version, bool blurred)
    {
        var size = 17 + 4 * version;
        var qr = QRCodeGenerator.Create("FORMAT GONE", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
        var modules = new byte[size * size];
        for (var row = 0; row < size; row++)
            for (var col = 0; col < size; col++)
                modules[row * size + col] = qr[row, col] ? (byte)1 : (byte)0;
        ModulePlacer.PlaceFormat(modules, size, FarFromEveryFormatWord());

        var luminance = Render(modules, size, pixelsPerModule: 6, quietZone: 4, blurred, out var side);
        var ok = QRCodeDecoder.TryDecodeImage(luminance, side, side, out _, out var info);

        await Assert.That(ok).IsFalse();
        await Assert.That((info.Status, info.Version)).IsEqualTo((DecodeStatus.FormatInformationInvalid, version));
    }

    /// <summary>A 15-bit word more than three bits from every masked format word, which BCH(15,5) cannot correct to any.</summary>
    private static ushort FarFromEveryFormatWord()
    {
        for (var word = 0; word < 0x8000; word++)
        {
            var nearest = int.MaxValue;
            for (var level = 0; level < 4; level++)
                for (var mask = 0; mask < 8; mask++)
                    nearest = Math.Min(nearest, System.Numerics.BitOperations.PopCount((uint)(word ^ QRCodeConstants.GetFormatBits((QREccLevel)level, mask))));
            if (nearest > 3)
                return (ushort)word;
        }
        throw new InvalidOperationException("No word beyond the correction distance");
    }

    /// <summary>Dark 0 and light 255, square modules; blurred, each pixel is the mean of the 3 × 3 around it, which gives the image grey levels at every edge.</summary>
    private static byte[] Render(byte[] modules, int size, int pixelsPerModule, int quietZone, bool blurred, out int side)
    {
        side = (size + 2 * quietZone) * pixelsPerModule;
        var crisp = new byte[side * side];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var row = y / pixelsPerModule - quietZone;
                var col = x / pixelsPerModule - quietZone;
                var dark = row >= 0 && row < size && col >= 0 && col < size && modules[row * size + col] != 0;
                crisp[y * side + x] = dark ? (byte)0 : (byte)255;
            }
        }
        if (!blurred)
            return crisp;

        var blur = new byte[side * side];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var sum = 0;
                var count = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var yy = y + dy;
                        var xx = x + dx;
                        if (yy < 0 || yy >= side || xx < 0 || xx >= side)
                            continue;
                        sum += crisp[yy * side + xx];
                        count++;
                    }
                }
                blur[y * side + x] = (byte)(sum / count);
            }
        }
        return blur;
    }
}
