using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// One symbol carrying two texts, for a test of what a grid read again by coverage returns. In the modules where the texts
/// differ, the pixel under the module's centre is a little to the first text's side of the middle grey and the rest of the
/// module fully on the second's: a grid thresholded at the module centres reads the first text, and one read by coverage, whose
/// bilinear sample near that pixel's corner leans to the rest of the module, the second. Upright, every module centre sits
/// <c>offset</c> pixels from its pixel's corner; turned, each sits where the turn puts it, and the premise has to be asserted.
/// </summary>
internal sealed class TwoTextRenderer
{
    private const int Subsamples = 8;

    private readonly int _columns;
    private readonly int _rows;
    private readonly int _pixelsPerModule;
    private readonly double _cos;
    private readonly double _sin;
    private readonly double _centreX;
    private readonly double _centreY;

    /// <param name="columns">The symbol's width in modules, quiet zone included.</param>
    /// <param name="rows">The symbol's height in modules, quiet zone included.</param>
    /// <param name="offset">How far each module centre sits from its pixel's corner, upright.</param>
    /// <param name="degrees">The turn about the symbol's centre.</param>
    public TwoTextRenderer(int columns, int rows, int pixelsPerModule, float offset, double degrees)
    {
        _columns = columns;
        _rows = rows;
        _pixelsPerModule = pixelsPerModule;
        _cos = Math.Cos(degrees * Math.PI / 180);
        _sin = Math.Sin(degrees * Math.PI / 180);
        var origin = offset - (0.5 * pixelsPerModule - Math.Floor(0.5 * pixelsPerModule));
        var halfWidth = columns * pixelsPerModule / 2.0;
        var halfHeight = rows * pixelsPerModule / 2.0;
        var extentX = Math.Abs(_cos) * halfWidth + Math.Abs(_sin) * halfHeight;
        var extentY = Math.Abs(_sin) * halfWidth + Math.Abs(_cos) * halfHeight;
        _centreX = extentX + origin;
        _centreY = extentY + origin;
        Width = (int)Math.Ceiling(2 * extentX + origin) + 1;
        Height = (int)Math.Ceiling(2 * extentY + origin) + 1;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The image: light outside the symbol, each module of <paramref name="first"/> and <paramref name="second"/> by row and column.</summary>
    public byte[] Render(Func<int, int, bool> first, Func<int, int, bool> second)
    {
        var luminance = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var sum = 0.0;
                for (var j = 0; j < Subsamples; j++)
                {
                    for (var i = 0; i < Subsamples; i++)
                    {
                        var (u, v) = ToModules(x + (i + 0.5) / Subsamples, y + (j + 0.5) / Subsamples);
                        var column = (int)Math.Floor(u);
                        var row = (int)Math.Floor(v);
                        if (column < 0 || row < 0 || column >= _columns || row >= _rows)
                            sum += 255;
                        else if (first(row, column) == second(row, column))
                            sum += first(row, column) ? 0 : 255;
                        else if (IsUnderCentre(x, y, column, row))
                            sum += first(row, column) ? 108 : 148;
                        else
                            sum += first(row, column) ? 255 : 0;
                    }
                }
                luminance[y * Width + x] = (byte)Math.Round(sum / (Subsamples * Subsamples));
            }
        }
        return luminance;
    }

    /// <summary>
    /// The grids inside the quiet zone, dark as 1, sampled at the module centres: thresholded as the decoder's grids are, and
    /// read by coverage as its re-read does, at the image's own threshold and grey levels.
    /// </summary>
    public (byte[] Thresholded, byte[] ReadByCoverage) SampleGrids(byte[] luminance, int quietZone)
    {
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var columns = _columns - 2 * quietZone;
        var rows = _rows - 2 * quietZone;
        var thresholded = new byte[columns * rows];
        var coverage = new byte[columns * rows];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var (x, y) = ToImage(column + quietZone + 0.5, row + quietZone + 0.5);
                thresholded[row * columns + column] = luminance[(int)y * Width + (int)x] < threshold ? (byte)1 : (byte)0;
                coverage[row * columns + column] = LuminanceSampler.Bilinear(luminance, Width, Height, (float)x, (float)y) < grey.Midpoint ? (byte)1 : (byte)0;
            }
        }
        return (thresholded, coverage);
    }

    private bool IsUnderCentre(int x, int y, int column, int row)
    {
        var (centreX, centreY) = ToImage(column + 0.5, row + 0.5);
        return x == (int)Math.Floor(centreX) && y == (int)Math.Floor(centreY);
    }

    private (double X, double Y) ToImage(double u, double v)
    {
        var dx = u * _pixelsPerModule - _columns * _pixelsPerModule / 2.0;
        var dy = v * _pixelsPerModule - _rows * _pixelsPerModule / 2.0;
        return (_centreX + _cos * dx - _sin * dy, _centreY + _sin * dx + _cos * dy);
    }

    private (double U, double V) ToModules(double x, double y)
    {
        var dx = x - _centreX;
        var dy = y - _centreY;
        return ((_cos * dx + _sin * dy + _columns * _pixelsPerModule / 2.0) / _pixelsPerModule,
            (-_sin * dx + _cos * dy + _rows * _pixelsPerModule / 2.0) / _pixelsPerModule);
    }
}
