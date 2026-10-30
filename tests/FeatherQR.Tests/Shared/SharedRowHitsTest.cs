using System.Runtime.InteropServices;
using FeatherQR.Internals.ImageDecoders;
using FeatherQR.Internals.MicroQR;
using FeatherQR.Internals.RmQR;
using FeatherQR.Internals.StandardQR;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The strided rows' hits the Standard QR read keeps for the single-finder scan (<see cref="SharedRowHits"/>): each reading's rows are the rows of its own stride with their true hits, so a sweep merging them is the full sweep bit for bit, and the scan given them reads what it reads scanning for itself.
/// </summary>
public class SharedRowHitsTest
{
    private const int HitRoom = 1024;

    /// <summary>A sheet with grey levels and light that falls off, so that every one of the four readings runs and the regional ones move pixels.</summary>
    private static (byte[] Luminance, int Width, int Height) Sheet(MixedSymbolsDecodeTest.Mix mix, int across, int down, float degrees)
    {
        var (luminance, width, height) = MixedSymbolsDecodeTest.RenderGrid(mix, across, down, degrees);
        UnevenLightingRenderer.Light(luminance, width, height, 4f, UnevenLight.Ramp, 30f, 0.5f);
        return (luminance, width, height);
    }

    /// <summary>Rooms as the mixed read rents them. The rows are a ref struct over them, made where they are used, and read what a read kept.</summary>
    private sealed class Rooms(int height)
    {
        public readonly FinderPattern[] Hits = new FinderPattern[SharedRowHits.Readings * HitRoom];
        public readonly int[] RowEnds = new int[SharedRowHits.Readings * FinderPatternFinder.StridedRowCount(height, FinderPatternFinder.RowStride(height))];
        public readonly int[] Strides = new int[SharedRowHits.Readings];

        public SharedRowHits Over() => new(Hits, RowEnds, Strides);
    }

    private static int[] Histogram(byte[] luminance)
    {
        var histogram = new int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(luminance, histogram);
        return histogram;
    }

    /// <summary>The four readings as both reads make them: the image and its thresholds, in the order of <see cref="SharedRowHits"/>.</summary>
    private static List<(string Name, byte[] Image, byte Threshold, GreyLevels Grey)> Readings(byte[] luminance, int width, int height)
    {
        var readings = new List<(string, byte[], byte, GreyLevels)>();
        var histogram = Histogram(luminance);
        var positiveThreshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var positiveGrey);
        readings.Add(("positive", luminance, positiveThreshold, positiveGrey));

        var inverted = new byte[luminance.Length];
        LuminanceInverter.Invert(luminance, inverted);
        Binarizer.InvertHistogram(histogram);
        var negativeThreshold = Binarizer.ComputeOtsuThresholdFromHistogram(histogram, out var negativeGrey);
        readings.Add(("negative", inverted, negativeThreshold, negativeGrey));

        var scratch = new int[LocalBinarizer.ScratchLength(width, height)];
        var measured = false;
        for (var polarity = 0; polarity < 2; polarity++)
        {
            var negative = polarity == 1;
            var binarized = new byte[luminance.Length];
            if (!LocalBinarizer.TryBinarize(luminance, width, height, negative, negative ? negativeThreshold : positiveThreshold, binarized, scratch, ref measured, out var darkCount))
                throw new InvalidOperationException("the regional pass moved no pixel: the sheet is not the one this test needs");
            var two = new int[Binarizer.HistogramBins];
            two[LocalBinarizer.Dark] = darkCount;
            two[LocalBinarizer.Light] = binarized.Length - darkCount;
            var threshold = Binarizer.ComputeOtsuThresholdFromHistogram(two, out var grey);
            readings.Add((negative ? "regional negative" : "regional positive", binarized, threshold, grey));
        }
        return readings;
    }

    private static void Read(byte[] luminance, int width, int height, QRCodeDecodeResult[] room, int seeded, Rooms rooms)
    {
        var rows = rooms.Over();
        QRImageDecoder.DecodeSymbolsBeside(luminance, width, height, Histogram(luminance), room, seeded, ref rows);
    }

    private static int ScanGiven<TDecoder, TInfo, TResult>(ref TDecoder decoder, byte[] luminance, int width, int height, TResult[] symbols, Rooms rooms)
        where TDecoder : struct, ISymbolsDecoder<TInfo, TResult>
    {
        var rows = rooms.Over();
        return CandidateScan.DecodeSymbols<TDecoder, TInfo, TResult>(ref decoder, luminance, width, height, symbols, rows);
    }

    private static TResult[] ScanGivenArray<TDecoder, TInfo, TResult>(ref TDecoder decoder, byte[] luminance, int width, int height, Rooms rooms)
        where TDecoder : struct, ISymbolsDecoder<TInfo, TResult>
    {
        var rows = rooms.Over();
        return CandidateScan.DecodeSymbols<TDecoder, TInfo, TResult>(ref decoder, luminance, width, height, rows, CancellationToken.None);
    }

    /// <summary>The bytes of the full sweep of a reading merging the rows kept for it.</summary>
    private static byte[] SweepFromKeptRows(Rooms rooms, int reading, byte[] image, int width, int height, byte threshold, in GreyLevels grey)
    {
        var rows = rooms.Over();
        if (!rows.TryTake(reading, height, out var given))
            throw new InvalidOperationException($"reading {reading} was not kept");
        var swept = new FinderPattern[4096];
        var sweptCount = FinderPatternFinder.FindCandidatesFullSweepReplaying(image, width, height, threshold, grey, swept, own: default, given);
        return MemoryMarshal.AsBytes(swept.AsSpan(0, sweptCount)).ToArray();
    }

    /// <summary>
    /// The Standard QR read keeps each of the four readings' rows at its own stride, and a sweep merging them in their places is the full sweep of that reading bit for bit. The readings of the symbols' polarity, the positive ones of a sheet and the negative ones of the sheet inverted, find finders and keep rows with hits.
    /// </summary>
    [Test]
    [Arguments(MixedSymbolsDecodeTest.Mix.StandardAndRmQR, 2, 2, 0f, false)]
    [Arguments(MixedSymbolsDecodeTest.Mix.AllThree, 3, 2, 20f, false)]
    [Arguments(MixedSymbolsDecodeTest.Mix.AllThree, 3, 2, 20f, true)]
    public async Task StandardQRRead_KeepsEachReadingsRows(MixedSymbolsDecodeTest.Mix mix, int across, int down, float degrees, bool inverted)
    {
        var (luminance, width, height) = Sheet(mix, across, down, degrees);
        if (inverted)
            LuminanceInverter.Invert(luminance, luminance);
        var rooms = new Rooms(height);
        var room = new QRCodeDecodeResult[16];
        Read(luminance, width, height, room, seeded: 0, rooms);

        var readings = Readings(luminance, width, height);
        for (var reading = 0; reading < SharedRowHits.Readings; reading++)
        {
            var (name, image, threshold, grey) = readings[reading];
            await Assert.That(rooms.Strides[reading]).IsEqualTo(FinderPatternFinder.RowStride(height)).Because($"{name}: kept at Standard QR's own stride");

            var expected = new FinderPattern[4096];
            var count = FinderPatternFinder.FindCandidatesFullSweep(image, width, height, threshold, expected, grey);
            var swept = SweepFromKeptRows(rooms, reading, image, width, height, threshold, grey);
            await Assert.That(swept).IsEquivalentTo(MemoryMarshal.AsBytes(expected.AsSpan(0, count)).ToArray(), CollectionOrdering.Matching).Because($"{name}: the sweep from the kept rows is the full sweep, every candidate bit for bit");

            var symbolsPolarity = (reading == SharedRowHits.Negative || reading == SharedRowHits.RegionalNegative) == inverted;
            if (!symbolsPolarity)
                continue;
            await Assert.That(count).IsGreaterThan(0).Because($"{name}: the premise, a finder is found");
            // Rows with hits were kept, or the sweep above scanned every row itself
            var rowEnds = rooms.RowEnds.AsSpan(reading * (rooms.RowEnds.Length / SharedRowHits.Readings), FinderPatternFinder.StridedRowCount(height, rooms.Strides[reading]));
            var last = 0;
            foreach (var end in rowEnds)
                last = Math.Max(last, end);
            await Assert.That(last).IsGreaterThan(0).Because($"{name}: hits were kept");
        }
    }

    /// <summary>A read into a room the seeds fill keeps nothing, and the scan given that scans for itself.</summary>
    [Test]
    public async Task StandardQRRead_WithNoRoomLeft_KeepsNothing()
    {
        var (luminance, width, height) = Sheet(MixedSymbolsDecodeTest.Mix.StandardAndRmQR, 2, 2, 0f);
        var rooms = new Rooms(height);
        var room = new QRCodeDecodeResult[2];
        Read(luminance, width, height, room, seeded: 2, rooms);

        await Assert.That(rooms.Strides).IsEquivalentTo([0, 0, 0, 0], CollectionOrdering.Matching);
    }

    /// <summary>Rooms too small for the readings' rows keep nothing: the scan that follows would otherwise read past them.</summary>
    [Test]
    public async Task StandardQRRead_WithRoomsTooSmall_KeepsNothing()
    {
        var (luminance, width, height) = Sheet(MixedSymbolsDecodeTest.Mix.StandardAndRmQR, 2, 2, 0f);
        var hits = new FinderPattern[SharedRowHits.Readings * HitRoom];
        var rowEnds = new int[SharedRowHits.Readings * (FinderPatternFinder.StridedRowCount(height, FinderPatternFinder.RowStride(height)) - 1)];
        var strides = new int[SharedRowHits.Readings];
        var rows = SharedRowHits.OverEmptyRooms(hits, rowEnds, strides);
        QRImageDecoder.DecodeSymbolsBeside(luminance, width, height, Histogram(luminance), new QRCodeDecodeResult[16], seeded: 0, ref rows);

        await Assert.That(strides).IsEquivalentTo([0, 0, 0, 0], CollectionOrdering.Matching);
    }

    /// <summary>The single-finder scan given the rows reads each symbol it reads scanning for itself, at the same corners, through both forms, with every reading kept and with one left out.</summary>
    [Test]
    [Arguments(MixedSymbolsDecodeTest.Mix.MicroAndRmQR, 3, 2, 0f, -1)]
    [Arguments(MixedSymbolsDecodeTest.Mix.MicroAndRmQR, 3, 2, 35f, -1)]
    [Arguments(MixedSymbolsDecodeTest.Mix.MicroAndRmQR, 3, 2, 0f, SharedRowHits.RegionalPositive)]
    [Arguments(MixedSymbolsDecodeTest.Mix.AllThree, 3, 3, 10f, SharedRowHits.Negative)]
    public async Task Scan_GivenTheRows_ReadsAsItDoesScanning(MixedSymbolsDecodeTest.Mix mix, int across, int down, float degrees, int leftOut)
    {
        var (luminance, width, height) = Sheet(mix, across, down, degrees);
        var rooms = new Rooms(height);
        var room = new QRCodeDecodeResult[64];
        Read(luminance, width, height, room, seeded: 0, rooms);
        // The premise: every reading was kept
        await Assert.That(rooms.Strides.All(static stride => stride > 0)).IsTrue();
        if (leftOut >= 0)
            rooms.Strides[leftOut] = 0;

        var micro = new MicroQRImageDecoder.CandidateDecoder();
        var microAlone = new MicroQRCodeDecodeResult[64];
        var microGiven = new MicroQRCodeDecodeResult[64];
        var aloneCount = CandidateScan.DecodeSymbols<MicroQRImageDecoder.CandidateDecoder, MicroQRCodeDecodeInfo, MicroQRCodeDecodeResult>(ref micro, luminance, width, height, microAlone);
        var givenCount = ScanGiven<MicroQRImageDecoder.CandidateDecoder, MicroQRCodeDecodeInfo, MicroQRCodeDecodeResult>(ref micro, luminance, width, height, microGiven, rooms);
        await Assert.That(aloneCount).IsGreaterThan(0).Because("the premise, Micro QR reads something");
        await Assert.That(microGiven.Take(givenCount)).IsEquivalentTo(microAlone.Take(aloneCount), CollectionOrdering.Matching);
        var microArray = ScanGivenArray<MicroQRImageDecoder.CandidateDecoder, MicroQRCodeDecodeInfo, MicroQRCodeDecodeResult>(ref micro, luminance, width, height, rooms);
        await Assert.That(microArray).IsEquivalentTo(microAlone.Take(aloneCount), CollectionOrdering.Matching);

        var rmqr = new RmQRImageDecoder.CandidateDecoder();
        var rmqrAlone = new RmQRCodeDecodeResult[64];
        var rmqrGiven = new RmQRCodeDecodeResult[64];
        aloneCount = CandidateScan.DecodeSymbols<RmQRImageDecoder.CandidateDecoder, RmQRCodeDecodeInfo, RmQRCodeDecodeResult>(ref rmqr, luminance, width, height, rmqrAlone);
        givenCount = ScanGiven<RmQRImageDecoder.CandidateDecoder, RmQRCodeDecodeInfo, RmQRCodeDecodeResult>(ref rmqr, luminance, width, height, rmqrGiven, rooms);
        await Assert.That(aloneCount).IsGreaterThan(0).Because("the premise, rMQR reads something");
        await Assert.That(rmqrGiven.Take(givenCount)).IsEquivalentTo(rmqrAlone.Take(aloneCount), CollectionOrdering.Matching);
        var rmqrArray = ScanGivenArray<RmQRImageDecoder.CandidateDecoder, RmQRCodeDecodeInfo, RmQRCodeDecodeResult>(ref rmqr, luminance, width, height, rooms);
        await Assert.That(rmqrArray).IsEquivalentTo(rmqrAlone.Take(aloneCount), CollectionOrdering.Matching);
    }
}
