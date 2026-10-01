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

    /// <summary>One module buffer for every candidate of the scan, of the length the decoder asks: from the stack when small, rented when not.</summary>
    [Test]
    [Arguments(289)]
    [Arguments(17 * 139)]
    public async Task DecodeRanked_GivesEachCandidateTheModuleBufferTheDecoderAsks(int length)
    {
        var scan = new Scan { ModuleBufferLength = length };

        scan.Run(3);

        await Assert.That(scan.ModuleLengths).IsEquivalentTo(new[] { length, length, length }, CollectionOrdering.Matching);
    }
}
