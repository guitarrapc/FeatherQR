using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.StandardQR;

namespace FeatherQR.Tests;

/// <summary>
/// The Standard QR image decoder searches an image again at reduced scale when nothing reads at full size: a symbol under a texture finer than its modules reads halved, with its corners reported in the image it was given.
/// The texture is a renderer's (<see cref="FineTextureRenderer"/>); what the search reads of photographs is measured by the decode sweep's photograph sets.
/// </summary>
public class ReducedScaleDecodeTest
{
    private const string Content = "FQR 2.0 REDUCED SCALE";
    private const int QuietZone = 4;

    private static QRCodeData Symbol()
        => QRCodeGenerator.Create(Content, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(3), QuietZoneSize = QuietZone });

    private sealed class Log
    {
        public List<(int Width, int Height, DecodeStatus Status)> Calls { get; } = [];
    }

    /// <summary>
    /// The decoder's own symbol pass (<see cref="QRImageDecoder.DecodeLuminanceCore"/>) through the shared passes, with the reduced-scale search on or off, recording what each call was given and found.
    /// Off, it is the decoder as it reads at full size only, which is the premise of every case here.
    /// </summary>
    private readonly struct CorePass(Log log, bool reducedScale) : ISymbolPass<QRCodeDecodeInfo>
    {
        public bool HasMidpointPass => false;

        public bool HasReducedScaleSearch => reducedScale;

        public QRCodeDecodeInfo NotDetected => new(DecodeStatus.NotDetected, 0, default, -1, 0);

        public QRCodeDecodeInfo AtFullScale(in QRCodeDecodeInfo info, int scale) => info;

        public DecodeStatus DecodeGlobal(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info, out bool noFinder, out byte threshold, out GreyLevels grey)
        {
            noFinder = false;
            threshold = 0;
            grey = default;
            return Decode(luminance, histogram, width, height, destination, out charsWritten, out info);
        }

        public DecodeStatus Decode(ReadOnlySpan<byte> luminance, ReadOnlySpan<int> histogram, int width, int height, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
        {
            var status = QRImageDecoder.DecodeLuminanceCore(luminance, histogram, width, height, destination, out charsWritten, out info);
            log.Calls.Add((width, height, status));
            return status;
        }

        public DecodeStatus DecodeAtMidpoint(ReadOnlySpan<byte> luminance, int width, int height, byte threshold, in GreyLevels grey, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
        {
            charsWritten = 0;
            info = NotDetected;
            return DecodeStatus.NotDetected;
        }
    }

    private static (DecodeStatus Status, QRCodeDecodeInfo Info, Log Log) ThroughThePasses(byte[] luminance, int width, int height, bool reducedScale)
    {
        var log = new Log();
        var pass = new CorePass(log, reducedScale);
        var status = ImageDecodePasses.Decode<CorePass, QRCodeDecodeInfo>(ref pass, luminance, width, height, new char[QRCodeDecoder.GetMaxDecodedLength(3)], out _, out var info);
        return (status, info, log);
    }

    /// <summary>
    /// Under a one-pixel texture nothing reads at full size, in either polarity or region by region, and the image halved reads.
    /// Under a two-pixel one the half is still textured and the quarter reads.
    /// </summary>
    [Test]
    [Arguments(8, 1, 2)]
    [Arguments(16, 2, 4)]
    public async Task FineTexture_ReadsAtTheScaleThatAveragesItOut(int pixelsPerModule, int cell, int scale)
    {
        var qr = Symbol();
        var (luminance, width, height) = FineTextureRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule, cell: cell);

        var fullSize = ThroughThePasses(luminance, width, height, reducedScale: false);
        await Assert.That(fullSize.Status).IsNotEqualTo(DecodeStatus.Success).Because("a premise: nothing reads at full size");

        var searched = ThroughThePasses(luminance, width, height, reducedScale: true);
        await Assert.That(searched.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(searched.Log.Calls[^1]).IsEqualTo((width / scale, height / scale, DecodeStatus.Success));
        await Assert.That(searched.Log.Calls.Count(static call => call.Status == DecodeStatus.Success)).IsEqualTo(1);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(Content);
    }

    /// <summary>
    /// A level is read in both polarities, as an image is: the same symbol light on dark, under the same texture, reads at half scale through the negative pass.
    /// </summary>
    [Test]
    public async Task FineTexture_LightOnDark_ReadsAtHalfScaleThroughTheNegative()
    {
        var qr = Symbol();
        var (luminance, width, height) = FineTextureRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, 8);
        for (var i = 0; i < luminance.Length; i++)
            luminance[i] = (byte)(255 - luminance[i]);
        await Assert.That(ThroughThePasses(luminance, width, height, reducedScale: false).Status).IsNotEqualTo(DecodeStatus.Success).Because("a premise: nothing reads at full size");

        var searched = ThroughThePasses(luminance, width, height, reducedScale: true);
        var half = searched.Log.Calls.Where(call => call.Width == width / 2).ToArray();
        await Assert.That(half.Length).IsEqualTo(2);
        await Assert.That(half[0].Status).IsNotEqualTo(DecodeStatus.Success).Because("a premise: the positive of the half does not read it");
        await Assert.That(half[1].Status).IsEqualTo(DecodeStatus.Success);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);
        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(Content);
    }

    /// <summary>
    /// The corners of a symbol read at reduced scale are reported in the pixels of the image given, within half a module.
    /// An odd offset puts the module edges inside the blocks that are averaged, so the reduced image has grey edges and its corners do not land on whole pixels.
    /// </summary>
    [Test]
    [Arguments(8, 1, 0, 0)]
    [Arguments(8, 1, 37, 12)]
    [Arguments(12, 1, 5, 9)]
    [Arguments(16, 2, 0, 0)]
    [Arguments(16, 2, 22, 6)]
    public async Task ReadAtReducedScale_ReportsCornersInTheImageGiven(int pixelsPerModule, int cell, int offsetX, int offsetY)
    {
        var qr = Symbol();
        var (luminance, width, height) = FineTextureRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, pixelsPerModule, offsetX, offsetY, cell);
        await Assert.That(ThroughThePasses(luminance, width, height, reducedScale: false).Status).IsNotEqualTo(DecodeStatus.Success).Because("a premise: nothing reads at full size");

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, out var text, out var info);

        await Assert.That(success).IsTrue().Because(info.Status.ToString());
        await Assert.That(text).IsEqualTo(Content);
        float left = offsetX + QuietZone * pixelsPerModule, top = offsetY + QuietZone * pixelsPerModule;
        float right = offsetX + (qr.Size - QuietZone) * pixelsPerModule, bottom = offsetY + (qr.Size - QuietZone) * pixelsPerModule;
        var tolerance = pixelsPerModule / 2f;
        await AssertNear(info.Corners.TopLeft, left, top, tolerance);
        await AssertNear(info.Corners.TopRight, right, top, tolerance);
        await AssertNear(info.Corners.BottomRight, right, bottom, tolerance);
        await AssertNear(info.Corners.BottomLeft, left, bottom, tolerance);
    }

    private static async Task AssertNear(ImagePoint actual, float x, float y, float tolerance)
    {
        var distance = MathF.Sqrt((actual.X - x) * (actual.X - x) + (actual.Y - y) * (actual.Y - y));
        await Assert.That(distance).IsLessThanOrEqualTo(tolerance).Because($"got ({actual.X:F2}, {actual.Y:F2}), expected ({x:F2}, {y:F2})");
    }

    /// <summary>A symbol that reads at full size is read once: the search is paid only by an image nothing else reads.</summary>
    [Test]
    public async Task CleanSymbol_ReadsAtFullSize_AndNothingIsReadReduced()
    {
        var qr = Symbol();
        var (luminance, width, height) = NearestNeighbourRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, 8f, 0f, 0f);

        var searched = ThroughThePasses(luminance, width, height, reducedScale: true);

        await Assert.That(searched.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(searched.Log.Calls.Count).IsEqualTo(1);
        await Assert.That(searched.Log.Calls[0]).IsEqualTo((width, height, DecodeStatus.Success));
    }

    /// <summary>The symbol with the lower half of its data inverted, which no error correction recovers; the function patterns stay.</summary>
    private static Func<int, int, bool> DamagedPastCorrection(QRCodeData qr)
    {
        var layout = ModulePlacer.GetLayout(3);
        return (row, column) =>
        {
            int coreRow = row - QuietZone, coreColumn = column - QuietZone;
            var inCore = coreRow >= 0 && coreColumn >= 0 && coreRow < layout.Size && coreColumn < layout.Size;
            var isFunction = inCore && (layout.BlockedMask[(coreRow * layout.Size + coreColumn) >> 3] & (1 << ((coreRow * layout.Size + coreColumn) & 7))) != 0;
            return inCore && coreRow >= layout.Size / 2 && !isFunction ? !qr[row, column] : qr[row, column];
        };
    }

    /// <summary>
    /// What is reported for an image nothing reads is what the full-size passes found, not what a level found.
    /// Under the texture the damaged symbol is not detected at full size, and at half scale it is found and fails correction: the report is the first.
    /// </summary>
    [Test]
    public async Task NothingReadsAtAnyScale_ReportsWhatFullSizeFound()
    {
        var qr = Symbol();
        var (luminance, width, height) = FineTextureRenderer.Render(DamagedPastCorrection(qr), qr.Size, qr.Size, 8);

        var fullSize = ThroughThePasses(luminance, width, height, reducedScale: false);
        await Assert.That(fullSize.Status).IsEqualTo(DecodeStatus.NotDetected).Because("a premise: nothing is found at full size");
        var searched = ThroughThePasses(luminance, width, height, reducedScale: true);
        await Assert.That(searched.Log.Calls.Any(call => call.Width == width / 2 && call.Status == DecodeStatus.DataUncorrectable)).IsTrue().Because("a premise: the half finds the symbol and cannot correct it");

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, new char[64], out var charsWritten, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(charsWritten).IsEqualTo(0);
        await Assert.That(info).IsEqualTo(fullSize.Info);
        await Assert.That(info.Corners.IsEmpty).IsTrue();
    }

    /// <summary>
    /// An image of only black and white is read at full size only: a damaged symbol drawn crisp is found, fails correction, and is not searched for again at half scale.
    /// </summary>
    [Test]
    public async Task OnlyBlackAndWhite_IsReadAtFullSizeOnly()
    {
        var qr = Symbol();
        var (luminance, width, height) = NearestNeighbourRenderer.Render(DamagedPastCorrection(qr), qr.Size, qr.Size, 8f, 0f, 0f);
        await Assert.That(luminance.AsSpan().IndexOfAnyExcept((byte)0, (byte)255)).IsEqualTo(-1).Because("a premise: only black and white");

        var searched = ThroughThePasses(luminance, width, height, reducedScale: true);

        await Assert.That(searched.Status).IsEqualTo(DecodeStatus.DataUncorrectable);
        await Assert.That(searched.Log.Calls.All(call => call.Width == width && call.Height == height)).IsTrue();
        await Assert.That(searched.Log.Calls.Count).IsEqualTo(2);
    }

    /// <summary>A read at reduced scale that does not fit the destination says so, as one at full size does, and names the version to size it for.</summary>
    [Test]
    public async Task ReadAtReducedScale_DestinationOneShort_IsDestinationTooSmall()
    {
        var qr = Symbol();
        var (luminance, width, height) = FineTextureRenderer.Render((row, column) => qr[row, column], qr.Size, qr.Size, 8);

        var success = QRCodeDecoder.TryDecodeImage(luminance, width, height, new char[Content.Length - 1], out var charsWritten, out var info);

        await Assert.That(success).IsFalse();
        await Assert.That(info.Status).IsEqualTo(DecodeStatus.DestinationTooSmall);
        await Assert.That(info.Version).IsEqualTo(3);
        await Assert.That(charsWritten).IsEqualTo(0);
    }
}
