using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The grid read the decoders that read around a single finder share (<see cref="GridRead"/>), over a scripted grid: when a grid
/// is read again by coverage, what that re-read samples, and which of the two decodes is returned. The decoders' own grids are
/// held through their images by <see cref="DestinationContractTest"/>.
/// </summary>
public class GridReadTest
{
    private const int Side = 2;

    /// <summary>The decodes a scripted grid returns in turn, and what it records of each.</summary>
    private sealed class Script
    {
        public List<(DecodeStatus Status, bool PastFormat)> Decodes { get; } = [];
        public bool MayReadByCoverage { get; set; } = true;
        public List<byte[]> ModulesDecoded { get; } = [];
    }

    /// <summary>A 2 × 2 grid whose module centres map to the image's pixel centres, decoding as its script says.</summary>
    private struct ScriptedGrid(Script script) : IGridRead<int>
    {
        private bool _pastFormat;

        public readonly int Columns => Side;

        public readonly int Rows => Side;

        public readonly void Map(float u, float v, out float x, out float y)
        {
            x = u * 4;
            y = v * 4;
        }

        public DecodeStatus Decode(ReadOnlySpan<byte> modules, in ImageView image, Span<char> destination, out int charsWritten, out int info, ref SearchResult<int> result)
        {
            var call = script.ModulesDecoded.Count;
            script.ModulesDecoded.Add(modules.ToArray());
            var (status, pastFormat) = script.Decodes[call];
            _pastFormat = pastFormat;
            info = call;
            charsWritten = status == DecodeStatus.Success ? 5 + call : 0;
            if (status != DecodeStatus.Success)
                result.Other(status, 0, info);
            return status;
        }

        public readonly bool PastFormat => _pastFormat;

        public readonly bool MayReadByCoverage(ReadOnlySpan<byte> modules) => script.MayReadByCoverage;
    }

    /// <summary>An 8 × 8 image, every pixel <paramref name="luminance"/>, with grey levels or without.</summary>
    private static (byte[] Luminance, GreyLevels Grey) Image(byte luminance, bool grey)
    {
        var pixels = Enumerable.Repeat(luminance, 64).ToArray();
        var histogram = new int[256];
        for (var i = 0; i < 256; i++)
            histogram[i] = 1;
        return (pixels, grey ? GreyLevels.FromHistogram(histogram, 128) : default);
    }

    private static (DecodeStatus Status, int CharsWritten, int Info, bool PastFormat, bool ReadByCoverage, byte[] Modules) Run(Script script, byte[] sampled, byte luminance = 20, bool grey = true)
    {
        var (pixels, levels) = Image(luminance, grey);
        var image = new ImageView(pixels, 8, 8, 128, levels);
        var grid = new ScriptedGrid(script);
        var result = new SearchResult<int>(ReportRule.Furthest, -1);
        var modules = sampled.ToArray();
        var status = GridRead.Decode<ScriptedGrid, int>(ref grid, image, modules, new char[8], out var charsWritten, out var info, ref result, out var pastFormat, out var readByCoverage);
        return (status, charsWritten, info, pastFormat, readByCoverage, modules);
    }

    private static readonly byte[] Light = [0, 0, 0, 0];
    private static readonly byte[] Dark = [1, 1, 1, 1];

    [Test]
    public async Task Premise_TheImageReadByCoverage_IsDark()
    {
        var (pixels, grey) = Image(20, grey: true);
        await Assert.That(grey.IsEnabled).IsTrue();
        await Assert.That(LuminanceSampler.Bilinear(pixels, 8, 8, 2f, 2f) < grey.Midpoint).IsTrue();
    }

    /// <summary>A read, and a read too long for the destination, are the symbol's: read again, the same grid reads the same text on a real image, and on a crafted one another, which a sized call never returns.</summary>
    [Test]
    [Arguments(DecodeStatus.Success)]
    [Arguments(DecodeStatus.DestinationTooSmall)]
    public async Task Decode_TerminalResult_IsNotReadAgainByCoverage(DecodeStatus terminal)
    {
        var script = new Script();
        script.Decodes.Add((terminal, true));
        script.Decodes.Add((DecodeStatus.Success, true));

        var (status, _, info, _, readByCoverage, modules) = Run(script, Light);

        await Assert.That((status, info, readByCoverage, script.ModulesDecoded.Count)).IsEqualTo((terminal, 0, false, 1));
        await Assert.That(modules).IsEquivalentTo(Light, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>Each condition of the gate alone keeps a failed grid from being read again.</summary>
    [Test]
    [Arguments(false, true, true)]
    [Arguments(true, false, true)]
    [Arguments(true, true, false)]
    public async Task Decode_GateNotMet_IsNotReadAgainByCoverage(bool grey, bool pastFormat, bool decoderAllows)
    {
        var script = new Script { MayReadByCoverage = decoderAllows };
        script.Decodes.Add((DecodeStatus.DataUncorrectable, pastFormat));
        script.Decodes.Add((DecodeStatus.Success, true));

        var (status, _, _, reportedPastFormat, readByCoverage, _) = Run(script, Light, grey: grey);

        await Assert.That((status, reportedPastFormat, readByCoverage, script.ModulesDecoded.Count)).IsEqualTo((DecodeStatus.DataUncorrectable, pastFormat, false, 1));
    }

    [Test]
    public async Task Decode_FailurePastFormat_IsReadAgainByCoverage_AndReturnsItsRead()
    {
        var script = new Script();
        script.Decodes.Add((DecodeStatus.DataUncorrectable, true));
        script.Decodes.Add((DecodeStatus.Success, false));

        var (status, charsWritten, info, pastFormat, readByCoverage, modules) = Run(script, Light);

        await Assert.That((status, charsWritten, info, pastFormat, readByCoverage)).IsEqualTo((DecodeStatus.Success, 6, 1, true, true));
        await Assert.That(script.ModulesDecoded[1]).IsEquivalentTo(Dark, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(modules).IsEquivalentTo(Dark, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>The grid that already failed decodes the same way again, so a re-read that changes no module is not decoded; it still spent its sampling.</summary>
    [Test]
    public async Task Decode_ReadByCoverageChangingNoModule_IsNotDecodedAgain()
    {
        var script = new Script();
        script.Decodes.Add((DecodeStatus.DataUncorrectable, true));

        var (status, _, info, _, readByCoverage, _) = Run(script, Dark);

        await Assert.That((status, info, readByCoverage, script.ModulesDecoded.Count)).IsEqualTo((DecodeStatus.DataUncorrectable, 0, true, 1));
    }

    /// <summary>The re-read's result is returned when it went further, a read that does not fit included; otherwise the grid's.</summary>
    [Test]
    [Arguments(DecodeStatus.DataUncorrectable, DecodeStatus.UnmappedCharacter, DecodeStatus.UnmappedCharacter, 1)]
    [Arguments(DecodeStatus.DataUncorrectable, DecodeStatus.DestinationTooSmall, DecodeStatus.DestinationTooSmall, 1)]
    [Arguments(DecodeStatus.UnmappedCharacter, DecodeStatus.DataUncorrectable, DecodeStatus.UnmappedCharacter, 0)]
    [Arguments(DecodeStatus.DataUncorrectable, DecodeStatus.FormatInformationInvalid, DecodeStatus.DataUncorrectable, 0)]
    [Arguments(DecodeStatus.DataUncorrectable, DecodeStatus.DataUncorrectable, DecodeStatus.DataUncorrectable, 0)]
    public async Task Decode_ReadByCoverage_ReturnsTheDecodeThatWentFurther(DecodeStatus sampled, DecodeStatus byCoverage, DecodeStatus expected, int expectedInfo)
    {
        var script = new Script();
        script.Decodes.Add((sampled, true));
        script.Decodes.Add((byCoverage, true));

        var (status, _, info, _, _, _) = Run(script, Light);

        await Assert.That((status, info)).IsEqualTo((expected, expectedInfo));
    }
}
