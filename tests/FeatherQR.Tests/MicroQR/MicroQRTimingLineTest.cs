using FeatherQR.Internals.MicroQR;

namespace FeatherQR.Tests;

/// <summary>
/// The timing-line fit behind Micro QR's timing frame, on synthetic lines: a line reads only when
/// it looks like the finder's edge row followed by a timing pattern that ends where a Micro QR
/// symbol can end. Every refusal leaves the frame to the finder alone.
/// </summary>
public class MicroQRTimingLineTest
{
    private const int PixelsPerModule = 4;
    private const int QuietZone = 3;

    [Test]
    [Arguments(11)]
    [Arguments(13)]
    [Arguments(15)]
    [Arguments(17)]
    public async Task Fit_TimingPattern_ReadsSizePitchAndStart(int size)
    {
        var fitted = Fit(TimingRow(size), out var start, out var pitch, out var fittedSize);

        await Assert.That(fitted).IsTrue();
        await Assert.That(fittedSize).IsEqualTo(size);
        await Assert.That(pitch).IsEqualTo(PixelsPerModule).Within(0.05f);
        await Assert.That(start).IsEqualTo(-3.5f * PixelsPerModule).Within(0.5f);
    }

    /// <summary>A symbol touching the image edge: the edge ends the timing pattern like a quiet zone.</summary>
    [Test]
    public async Task Fit_EndsAtTheImageEdge_Reads()
    {
        var fitted = Fit(TimingRow(15, trailingQuietZone: 0), out _, out _, out var size);

        await Assert.That(fitted).IsTrue();
        await Assert.That(size).IsEqualTo(15);
    }

    /// <summary>
    /// A timing pattern longer than any Micro QR symbol, as the timing row of a larger symbol would
    /// read. Refused, which also keeps the frame inside the 17 × 17 sampling buffer.
    /// </summary>
    [Test]
    [Arguments(19)]
    [Arguments(21)]
    public async Task Fit_LongerThanM4_IsRefused(int size)
        => await Assert.That(Fit(TimingRow(size), out _, out _, out _)).IsFalse();

    /// <summary>Three dark modules in a row, as a data row would give: not a timing pattern.</summary>
    [Test]
    public async Task Fit_RunLongerThanAModule_IsRefused()
    {
        var modules = TimingRow(15);
        modules[QuietZone + 9] = true;

        await Assert.That(Fit(modules, out _, out _, out _)).IsFalse();
    }

    /// <summary>
    /// Every run within the per-run tolerance of the given module, but the fitted pitch 0.65 of it:
    /// the line does not belong to this finder.
    /// </summary>
    [Test]
    public async Task Fit_PitchFarFromTheFinders_IsRefused()
        => await Assert.That(Fit(TimingRow(15), out _, out _, out _, module: PixelsPerModule / 0.65f)).IsFalse();

    /// <summary>
    /// The walk starts on the finder's dark edge row; a light start is not that row. One light
    /// pixel under the start and the finder dark around it, so every later check would pass.
    /// </summary>
    [Test]
    public async Task Fit_StartsOnLight_IsRefused()
        => await Assert.That(Fit(TimingRow(15), out _, out _, out _, lightPixelAtStart: true)).IsFalse();

    /// <summary>A dark run reaching more than five modules back from the finder's centre is no finder.</summary>
    [Test]
    public async Task Fit_FinderRunTooLong_IsRefused()
    {
        var modules = TimingRow(15);
        for (var i = 0; i < QuietZone; i++)
            modules[i] = true;

        await Assert.That(Fit(modules, out _, out _, out _)).IsFalse();
    }

    /// <summary>A quiet zone, the finder's seven dark modules, then light and dark alternating from column 7 to <paramref name="size"/> − 1.</summary>
    private static bool[] TimingRow(int size, int trailingQuietZone = 4)
    {
        var modules = new bool[QuietZone + size + trailingQuietZone];
        for (var column = 0; column < size; column++)
            modules[QuietZone + column] = column < 7 || column % 2 == 0;
        return modules;
    }

    /// <summary>Renders the row three pixels tall and walks it from the finder's centre column.</summary>
    private static bool Fit(bool[] modules, out float start, out float pitch, out int size, float module = PixelsPerModule, bool lightPixelAtStart = false)
    {
        const int height = 3;
        var width = modules.Length * PixelsPerModule;
        var lineX = (QuietZone + 3.5f) * PixelsPerModule;
        var luminance = new byte[width * height];
        for (var x = 0; x < width; x++)
        {
            var value = modules[x / PixelsPerModule] && !(lightPixelAtStart && x == (int)lineX) ? (byte)0 : (byte)255;
            for (var y = 0; y < height; y++)
                luminance[y * width + x] = value;
        }
        return MicroQRImageDecoder.TryFitTimingLine(luminance, width, height, 128, lineX, 1.5f, 1f, 0f, module, out start, out pitch, out size);
    }
}
