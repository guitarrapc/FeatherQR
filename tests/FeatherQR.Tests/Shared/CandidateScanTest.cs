using FeatherQR.Internals.ImageDecoders;
using TUnit.Assertions.Enums;

namespace FeatherQR.Tests;

/// <summary>
/// The scan of the decoders that read around a single finder (<see cref="CandidateScan"/>), over ranked candidates and a scripted
/// decoder: which candidates it decodes, what it keeps of each one's result, and what it reports. The decoders' own rules are
/// held through their images by <see cref="DestinationContractTest"/>.
/// </summary>
public class CandidateScanTest
{
    /// <summary>What the scripted decoder returns for a candidate: its status, and the corners of the symbol it read.</summary>
    private readonly record struct Outcome(DecodeStatus Status, SymbolCorners Corners);

    /// <summary>A scripted decoder's diagnostics: the candidate it came from, the status, and the corners, if any.</summary>
    private readonly record struct ScriptedInfo(int Candidate, DecodeStatus Status, SymbolCorners Corners);

    /// <summary>
    /// Returns each candidate's scripted outcome as a decoder does, keeping it in the candidate's result, and records the
    /// candidates it was asked for and whether each one's result started empty.
    /// </summary>
    private readonly struct ScriptedDecoder(Dictionary<float, Outcome> outcomes, List<int> calls, List<bool> freshResults, int moduleBufferLength, List<int> moduleLengths) : ICandidateDecoder<ScriptedInfo>
    {
        public ScriptedInfo NotDetected => new(-1, DecodeStatus.NotDetected, default);

        public int ModuleBufferLength => moduleBufferLength;

        public SymbolCorners Corners(in ScriptedInfo info) => info.Corners;

        public ScriptedInfo WithoutCorners(in ScriptedInfo info) => info with { Corners = default };

        public DecodeStatus DecodeCandidate(in ImageView image, FinderPattern candidate, Span<byte> modules, Span<char> destination, out int charsWritten, out ScriptedInfo info, ref SearchResult<ScriptedInfo> result)
        {
            var index = (int)candidate.X;
            calls.Add(index);
            freshResults.Add(result.Status == DecodeStatus.NotDetected);
            moduleLengths.Add(modules.Length);
            var outcome = outcomes.TryGetValue(candidate.X, out var scripted) ? scripted : new Outcome(DecodeStatus.FormatInformationInvalid, default);
            info = new ScriptedInfo(index, outcome.Status, outcome.Corners);
            charsWritten = outcome.Status == DecodeStatus.Success ? 3 : 0;
            if (outcome.Status != DecodeStatus.Success)
                result.Other(outcome.Status, 0, info);
            return outcome.Status;
        }
    }

    private sealed class Scan
    {
        public Dictionary<float, Outcome> Outcomes { get; } = [];
        public List<int> Calls { get; } = [];
        public List<bool> FreshResults { get; } = [];
        public List<int> ModuleLengths { get; } = [];
        public int ModuleBufferLength { get; init; } = 64;

        public (DecodeStatus Status, int CharsWritten, ScriptedInfo Info, int[] Tried) Run(int candidateCount, params int[] skip)
        {
            // Candidate i sits at (i, 0), ranked in index order
            var ranked = Enumerable.Range(0, candidateCount).Select(static i => Candidate(i)).ToArray();
            var skipped = skip.Select(static i => Candidate(i)).ToArray();
            var tried = new FinderPattern[CandidateScan.MaxCandidatesToTry];
            var decoder = new ScriptedDecoder(Outcomes, Calls, FreshResults, ModuleBufferLength, ModuleLengths);
            var destination = new char[16];
            var status = CandidateScan.DecodeRanked<ScriptedDecoder, ScriptedInfo>(ref decoder, default, ranked, destination, out var charsWritten, out var info, skipped, tried, out var triedCount);
            return (status, charsWritten, info, tried.Take(triedCount).Select(static c => (int)c.X).ToArray());
        }

        private static FinderPattern Candidate(int index) => new() { X = index, Y = 0, ModuleSize = 1, Count = 10 };
    }

    /// <summary>A square around candidates <paramref name="first"/> to <paramref name="last"/>, strictly containing them.</summary>
    private static SymbolCorners Around(int first, int last)
        => new(new ImagePoint(first - 0.5f, -0.5f), new ImagePoint(last + 0.5f, -0.5f), new ImagePoint(last + 0.5f, 0.5f), new ImagePoint(first - 0.5f, 0.5f));

    [Test]
    public async Task DecodeRanked_Success_EndsTheScan_WithTheReadsCorners()
    {
        var scan = new Scan();
        scan.Outcomes[0] = new Outcome(DecodeStatus.Success, Around(0, 0));

        var (status, written, info, tried) = scan.Run(3);

        await Assert.That((status, written, info.Candidate, info.Corners)).IsEqualTo((DecodeStatus.Success, 3, 0, Around(0, 0)));
        await Assert.That(scan.Calls).IsEquivalentTo(new[] { 0 }, CollectionOrdering.Matching);
        await Assert.That(tried).IsEquivalentTo(new[] { 0 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task DecodeRanked_ReadThatDoesNotFit_SkipsTheCandidatesInsideIt_AndReportsItWithoutCorners()
    {
        var scan = new Scan();
        scan.Outcomes[0] = new Outcome(DecodeStatus.DestinationTooSmall, Around(0, 2));

        var (status, written, info, tried) = scan.Run(5);

        await Assert.That(scan.Calls).IsEquivalentTo(new[] { 0, 3, 4 }, CollectionOrdering.Matching);
        await Assert.That(tried).IsEquivalentTo(new[] { 0, 3, 4 }, CollectionOrdering.Matching);
        await Assert.That((status, written, info)).IsEqualTo((DecodeStatus.DestinationTooSmall, 0, new ScriptedInfo(0, DecodeStatus.DestinationTooSmall, default)));
    }

    [Test]
    public async Task DecodeRanked_EveryReadThatDoesNotFit_KeepsItsCorners()
    {
        var scan = new Scan();
        scan.Outcomes[0] = new Outcome(DecodeStatus.DestinationTooSmall, Around(4, 5));
        scan.Outcomes[1] = new Outcome(DecodeStatus.DestinationTooSmall, Around(6, 7));

        scan.Run(8);

        await Assert.That(scan.Calls).IsEquivalentTo(new[] { 0, 1, 2, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task DecodeRanked_ReadThatDoesNotFit_SkipsNothingOutsideIt()
    {
        var scan = new Scan();
        scan.Outcomes[0] = new Outcome(DecodeStatus.DestinationTooSmall, Around(5, 6));

        scan.Run(4);

        await Assert.That(scan.Calls).IsEquivalentTo(new[] { 0, 1, 2, 3 }, CollectionOrdering.Matching);
    }

    /// <summary>A read that does not fit without corners, and any other failure, carry no symbol to skip, whatever their info holds.</summary>
    [Test]
    [Arguments(DecodeStatus.DestinationTooSmall, false)]
    [Arguments(DecodeStatus.DataUncorrectable, true)]
    [Arguments(DecodeStatus.UnmappedCharacter, true)]
    [Arguments(DecodeStatus.FormatInformationInvalid, true)]
    public async Task DecodeRanked_ResultWithoutASymbolToSkip_SkipsNothing(DecodeStatus status, bool withCorners)
    {
        var scan = new Scan();
        scan.Outcomes[0] = new Outcome(status, withCorners ? Around(0, 3) : default);

        scan.Run(4);

        await Assert.That(scan.Calls).IsEquivalentTo(new[] { 0, 1, 2, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task DecodeRanked_TriesTheFirstEightOnly()
    {
        var scan = new Scan();

        scan.Run(10);

        await Assert.That(scan.Calls).IsEquivalentTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, CollectionOrdering.Matching);
    }

    /// <summary>A candidate skipped, as one a strided scan already tried, or one inside a read, is not replaced by the next in rank.</summary>
    [Test]
    public async Task DecodeRanked_SkippedCandidates_AreNotReplacedByTheNextInRank()
    {
        var scan = new Scan();
        scan.Outcomes[0] = new Outcome(DecodeStatus.DestinationTooSmall, Around(0, 1));

        var (_, _, _, tried) = scan.Run(10, skip: 2);

        await Assert.That(scan.Calls).IsEquivalentTo(new[] { 0, 3, 4, 5, 6, 7 }, CollectionOrdering.Matching);
        await Assert.That(tried).IsEquivalentTo(new[] { 0, 3, 4, 5, 6, 7 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task DecodeRanked_EachCandidate_StartsFromAResultOfItsOwn()
    {
        var scan = new Scan();
        scan.Outcomes[0] = new Outcome(DecodeStatus.DestinationTooSmall, Around(5, 5));
        scan.Outcomes[1] = new Outcome(DecodeStatus.DataUncorrectable, default);

        scan.Run(3);

        await Assert.That(scan.FreshResults).IsEquivalentTo(new[] { true, true, true }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task DecodeRanked_ReportsTheFailureThatWentFurthest_TheFirstOnATie()
    {
        var scan = new Scan();
        scan.Outcomes[1] = new Outcome(DecodeStatus.DataUncorrectable, default);
        scan.Outcomes[2] = new Outcome(DecodeStatus.DataUncorrectable, default);

        var (status, written, info, _) = scan.Run(4);

        await Assert.That((status, written, info.Candidate)).IsEqualTo((DecodeStatus.DataUncorrectable, 0, 1));
    }

    [Test]
    public async Task DecodeRanked_NoCandidate_ReportsNotDetected()
    {
        var scan = new Scan();

        var (status, written, info, tried) = scan.Run(0);

        await Assert.That((status, written, info.Candidate, tried.Length)).IsEqualTo((DecodeStatus.NotDetected, 0, -1, 0));
    }

    /// <summary>
    /// One module buffer for every candidate of the scan, of the length the decoder asks, at Micro QR's size and rMQR's. Whether it
    /// came from the stack or the pool is not visible here.
    /// </summary>
    [Test]
    [Arguments(289)]
    [Arguments(17 * 139)]
    public async Task DecodeRanked_GivesEachCandidateTheModuleBufferTheDecoderAsks(int length)
    {
        var scan = new Scan { ModuleBufferLength = length };

        scan.Run(3);

        await Assert.That(scan.ModuleLengths).IsEquivalentTo(new[] { length, length, length }, CollectionOrdering.Matching);
    }

    /// <summary>
    /// Returns the outcome scripted for each call in turn, whatever the candidate, keeping a failure in the candidate's result as
    /// a decoder does, and records each candidate's centre.
    /// </summary>
    private readonly struct QueuedDecoder(Queue<DecodeStatus> outcomes, List<(float X, float Y)> calls) : ICandidateDecoder<ScriptedInfo>
    {
        public ScriptedInfo NotDetected => new(-1, DecodeStatus.NotDetected, default);

        public int ModuleBufferLength => 64;

        public SymbolCorners Corners(in ScriptedInfo info) => info.Corners;

        public ScriptedInfo WithoutCorners(in ScriptedInfo info) => info with { Corners = default };

        public DecodeStatus DecodeCandidate(in ImageView image, FinderPattern candidate, Span<byte> modules, Span<char> destination, out int charsWritten, out ScriptedInfo info, ref SearchResult<ScriptedInfo> result)
        {
            calls.Add((candidate.X, candidate.Y));
            var status = outcomes.Count > 0 ? outcomes.Dequeue() : DecodeStatus.FormatInformationInvalid;
            info = new ScriptedInfo(calls.Count - 1, status, default);
            charsWritten = status == DecodeStatus.Success ? 3 : 0;
            if (status != DecodeStatus.Success)
                result.Other(status, 0, info);
            return status;
        }
    }

    /// <summary>
    /// A light image with one finder pattern, its modules <paramref name="moduleSize"/> pixels with its top-left corner at
    /// (<paramref name="left"/>, <paramref name="top"/>), or none.
    /// </summary>
    private static byte[] FinderImage(int width, int height, int moduleSize, int left, int top, bool withFinder = true)
    {
        var luminance = Enumerable.Repeat((byte)255, width * height).ToArray();
        if (!withFinder)
            return luminance;
        for (var row = 0; row < 7; row++)
        {
            for (var column = 0; column < 7; column++)
            {
                var ring = Math.Min(Math.Min(row, column), Math.Min(6 - row, 6 - column));
                if (ring == 1)
                    continue;
                for (var y = 0; y < moduleSize; y++)
                {
                    for (var x = 0; x < moduleSize; x++)
                        luminance[(top + row * moduleSize + y) * width + left + column * moduleSize + x] = 0;
                }
            }
        }
        return luminance;
    }

    /// <summary>A 64 × 64 image whose finder, at 4 px/module, the strided scan finds.</summary>
    private static (byte[] Luminance, int Width, int Height) StridedFinder() => (FinderImage(64, 64, 4, 16, 16), 64, 64);

    /// <summary>
    /// A 32 × 24 image whose finder, at 1 px/module, only the sweep finds: rows 4n are the strided scan's, and the finder's centre
    /// band, rows top + 2 to top + 4, misses them with a top of 4n + 3.
    /// </summary>
    private static (byte[] Luminance, int Width, int Height) SweepOnlyFinder() => (FinderImage(32, 24, 1, 12, 7), 32, 24);

    /// <summary>Premise: how many candidates the strided scan and the sweep find in the image.</summary>
    private static async Task AssertCandidates((byte[] Luminance, int Width, int Height) image, int strided, int swept)
    {
        var (luminance, width, height) = image;
        var candidates = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);
        var found = (FinderPatternFinder.FindCandidates(luminance, width, height, threshold, candidates, grey), FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, candidates, grey));
        await Assert.That(found).IsEqualTo((strided, swept)).Because("premise: the candidates the strided scan and the sweep find");
    }

    private static (DecodeStatus Status, ScriptedInfo Info, bool NoFinder, List<(float X, float Y)> Calls) Decode((byte[] Luminance, int Width, int Height) image, params DecodeStatus[] outcomes)
    {
        var (luminance, width, height) = image;
        var calls = new List<(float X, float Y)>();
        var decoder = new QueuedDecoder(new Queue<DecodeStatus>(outcomes), calls);
        var histogram = new int[Binarizer.HistogramBins];
        Binarizer.FillHistogram(luminance, histogram);
        var status = CandidateScan.Decode<QueuedDecoder, ScriptedInfo>(ref decoder, luminance, histogram, width, height, new char[16], out _, out var info, out var noFinder, out _, out _);
        return (status, info, noFinder, calls);
    }

    /// <summary>A candidate the strided scan tried without settling decodes the same way in the sweep, so the sweep does not try it again.</summary>
    [Test]
    public async Task Decode_StridedScanUnsettled_SweepSkipsTheCandidateItTried()
    {
        var image = StridedFinder();
        await AssertCandidates(image, strided: 1, swept: 1);

        var (status, _, noFinder, calls) = Decode(image, DecodeStatus.DataUncorrectable);

        await Assert.That((status, noFinder, calls.Count)).IsEqualTo((DecodeStatus.DataUncorrectable, false, 1));
    }

    /// <summary>
    /// A strided scan that settled on a verdict leaves every candidate to the sweep, the verdict's own included, so the sweep's
    /// report is that symbol's unless another reads. The script gives the candidate a read the second time, to show the sweep
    /// decoded it again.
    /// </summary>
    [Test]
    public async Task Decode_StridedScanSettledOnAVerdict_SweepTriesEveryCandidateAgain()
    {
        var image = StridedFinder();
        await AssertCandidates(image, strided: 1, swept: 1);

        var (status, _, _, calls) = Decode(image, DecodeStatus.UnmappedCharacter, DecodeStatus.Success);

        await Assert.That((status, calls.Count)).IsEqualTo((DecodeStatus.Success, 2));
        await Assert.That(calls[1]).IsEqualTo(calls[0]);
    }

    /// <summary>
    /// A read, or a read too long for the destination, in the strided scan ends the symbol pass: no sweep runs. For a read too long
    /// this is a residual (qrcode-symbologies.md, single-finder candidate scan): a symbol that fits, which only the sweep would find,
    /// is not looked for, the price of keeping a probe for the destination's size as cheap as a sized call.
    /// </summary>
    [Test]
    [Arguments(DecodeStatus.Success)]
    [Arguments(DecodeStatus.DestinationTooSmall)]
    public async Task Decode_StridedScanTerminal_RunsNoSweep(DecodeStatus terminal)
    {
        var image = StridedFinder();
        await AssertCandidates(image, strided: 1, swept: 1);

        var (status, _, _, calls) = Decode(image, terminal, DecodeStatus.Success);

        await Assert.That((status, calls.Count)).IsEqualTo((terminal, 1));
    }

    /// <summary>
    /// When only the sweep finds a candidate, the midpoint pass, which is for a polarity whose scans found no finder at all, is not
    /// owed. Both scans failed, so the report is the strided scan's, the one a caller saw before the sweep existed.
    /// </summary>
    [Test]
    public async Task Decode_OnlyTheSweepFindsACandidate_ReportsAFinder()
    {
        var image = SweepOnlyFinder();
        await AssertCandidates(image, strided: 0, swept: 1);

        var (status, _, noFinder, calls) = Decode(image, DecodeStatus.DataUncorrectable);

        await Assert.That((status, noFinder, calls.Count)).IsEqualTo((DecodeStatus.NotDetected, false, 1));
    }

    /// <summary>
    /// When the sweep is the scan that finds the symbol, its read too long for the destination is the answer: reported as the
    /// strided scan's nothing, a call probing for its buffer size would never grow it.
    /// </summary>
    [Test]
    public async Task Decode_OnlyTheSweepReadsTooLong_ReportsTheReadTooLong()
    {
        var image = SweepOnlyFinder();
        await AssertCandidates(image, strided: 0, swept: 1);

        var (status, _, noFinder, calls) = Decode(image, DecodeStatus.DestinationTooSmall);

        await Assert.That((status, noFinder, calls.Count)).IsEqualTo((DecodeStatus.DestinationTooSmall, false, 1));
    }

    [Test]
    public async Task Decode_NoCandidateInEitherScan_ReportsNoFinder()
    {
        var image = (FinderImage(32, 24, 1, 0, 0, withFinder: false), 32, 24);
        await AssertCandidates(image, strided: 0, swept: 0);

        var (status, info, noFinder, calls) = Decode(image);

        await Assert.That((status, info.Candidate, noFinder, calls.Count)).IsEqualTo((DecodeStatus.NotDetected, -1, true, 0));
    }
}
