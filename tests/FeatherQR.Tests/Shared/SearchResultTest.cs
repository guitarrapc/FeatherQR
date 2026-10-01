using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// The result a search of many decode attempts reports (<see cref="SearchResult{TInfo}"/>), under each of its two rules, and the status classes the image decoders read results by (<see cref="AttemptStatus"/>).
/// </summary>
public class SearchResultTest
{
    /// <summary>Every status the matrix level can report, in the order of how far an attempt got.</summary>
    private static readonly DecodeStatus[] ByProgress =
    [
        DecodeStatus.NotDetected,
        DecodeStatus.InvalidMatrix,
        DecodeStatus.FormatInformationInvalid,
        DecodeStatus.DataUncorrectable,
        DecodeStatus.InvalidBitstream,
        DecodeStatus.UnmappedCharacter,
        DecodeStatus.UnsupportedContent,
        DecodeStatus.DestinationTooSmall,
        DecodeStatus.Success,
    ];

    [Test]
    public async Task ByProgress_CoversEveryStatus()
        => await Assert.That(ByProgress.Order().ToArray()).IsEquivalentTo(Enum.GetValues<DecodeStatus>().Order().ToArray());

    #region Status classes

    [Test]
    [Arguments(DecodeStatus.Success, true, true, false, true)]
    [Arguments(DecodeStatus.DestinationTooSmall, true, true, false, true)]
    [Arguments(DecodeStatus.UnmappedCharacter, false, true, true, true)]
    [Arguments(DecodeStatus.UnsupportedContent, false, true, true, true)]
    [Arguments(DecodeStatus.DataUncorrectable, false, false, false, true)]
    [Arguments(DecodeStatus.InvalidBitstream, false, false, false, true)]
    [Arguments(DecodeStatus.FormatInformationInvalid, false, false, false, false)]
    [Arguments(DecodeStatus.InvalidMatrix, false, false, false, false)]
    [Arguments(DecodeStatus.NotDetected, false, false, false, false)]
    public async Task Classes(DecodeStatus status, bool terminal, bool settled, bool verdict, bool pastFormat)
    {
        await Assert.That(AttemptStatus.IsTerminal(status)).IsEqualTo(terminal);
        await Assert.That(AttemptStatus.IsSettled(status)).IsEqualTo(settled);
        await Assert.That(AttemptStatus.IsContentVerdict(status)).IsEqualTo(verdict);
        await Assert.That(AttemptStatus.IsPastFormat(status)).IsEqualTo(pastFormat);
    }

    /// <summary>
    /// How far an attempt got: nothing, then a matrix or format information refused, then past the format information, then a verdict on the content, then a read too long for the destination, then a read.
    /// Statuses of one step rank equal.
    /// </summary>
    [Test]
    public async Task Progress_OrdersTheSteps()
    {
        int[] expected = [0, 1, 1, 2, 2, 3, 3, 4, 5];
        for (var i = 0; i < ByProgress.Length; i++)
            await Assert.That(AttemptStatus.Progress(ByProgress[i])).IsEqualTo(expected[i]).Because(ByProgress[i].ToString());
    }

    #endregion

    private static SearchResult<int> New(ReportRule rule) => new(rule, -1);

    #region MainPath: Standard QR

    [Test]
    public async Task MainPath_Nothing_ReportsNotDetected()
    {
        var result = New(ReportRule.MainPath);

        var status = result.Report(out var charsWritten, out var info);

        await Assert.That(status).IsEqualTo(DecodeStatus.NotDetected);
        await Assert.That(info).IsEqualTo(-1);
        await Assert.That(charsWritten).IsEqualTo(0);
    }

    /// <summary>The main path's failure is reported over any other's, one before it or after it, however much further the other got.</summary>
    [Test]
    [Arguments(DecodeStatus.NotDetected, DecodeStatus.DataUncorrectable)]
    [Arguments(DecodeStatus.FormatInformationInvalid, DecodeStatus.InvalidBitstream)]
    [Arguments(DecodeStatus.DataUncorrectable, DecodeStatus.NotDetected)]
    public async Task MainPath_MainFailure_IsReportedOverOthers(DecodeStatus main, DecodeStatus other)
    {
        foreach (var otherFirst in new[] { false, true })
        {
            var result = New(ReportRule.MainPath);
            if (otherFirst)
                await Assert.That(result.Other(other, 3, 2)).IsFalse();
            await Assert.That(result.Main(main, 5, 1)).IsFalse();
            if (!otherFirst)
                await Assert.That(result.Other(other, 3, 2)).IsFalse();

            var status = result.Report(out var charsWritten, out var info);

            await Assert.That(status).IsEqualTo(main).Because(otherFirst ? "other first" : "main first");
            await Assert.That(info).IsEqualTo(1);
            await Assert.That(charsWritten).IsEqualTo(5);
        }
    }

    /// <summary>With no main path, the first other attempt is reported.</summary>
    [Test]
    public async Task MainPath_NoMain_ReportsTheFirstOther()
    {
        var result = New(ReportRule.MainPath);
        result.Other(DecodeStatus.FormatInformationInvalid, 0, 1);
        result.Other(DecodeStatus.DataUncorrectable, 0, 2);

        await Assert.That(result.Report(out _, out var info)).IsEqualTo(DecodeStatus.FormatInformationInvalid);
        await Assert.That(info).IsEqualTo(1);
    }

    /// <summary>A settled result from any attempt is taken, and says so, so the caller can stop there.</summary>
    [Test]
    [Arguments(DecodeStatus.Success)]
    [Arguments(DecodeStatus.DestinationTooSmall)]
    [Arguments(DecodeStatus.UnmappedCharacter)]
    [Arguments(DecodeStatus.UnsupportedContent)]
    public async Task MainPath_SettledOther_IsTaken(DecodeStatus settled)
    {
        var result = New(ReportRule.MainPath);
        result.Main(DecodeStatus.DataUncorrectable, 0, 1);

        await Assert.That(result.Other(settled, 9, 2)).IsTrue();
        var status = result.Report(out var charsWritten, out var info);

        await Assert.That(status).IsEqualTo(settled);
        await Assert.That(info).IsEqualTo(2);
        await Assert.That(charsWritten).IsEqualTo(9);
    }

    [Test]
    public async Task MainPath_SettledMain_SaysSo()
    {
        var result = New(ReportRule.MainPath);
        result.Other(DecodeStatus.DataUncorrectable, 0, 2);

        await Assert.That(result.Main(DecodeStatus.Success, 4, 1)).IsTrue();
        await Assert.That(result.Report(out var charsWritten, out var info)).IsEqualTo(DecodeStatus.Success);
        await Assert.That((info, charsWritten)).IsEqualTo((1, 4));
    }

    /// <summary>The first settled result stays: a caller that goes on past one does not lose it to a later attempt.</summary>
    [Test]
    public async Task MainPath_FirstSettled_Stays()
    {
        var result = New(ReportRule.MainPath);
        result.Other(DecodeStatus.UnmappedCharacter, 0, 1);
        result.Main(DecodeStatus.DataUncorrectable, 0, 2);
        result.Other(DecodeStatus.Success, 5, 3);

        await Assert.That(result.Report(out _, out var info)).IsEqualTo(DecodeStatus.UnmappedCharacter);
        await Assert.That(info).IsEqualTo(1);
    }

    #endregion

    #region Furthest: Micro QR and rMQR

    [Test]
    public async Task Furthest_Nothing_ReportsNotDetected()
    {
        var result = New(ReportRule.Furthest);

        await Assert.That(result.Report(out var charsWritten, out var info)).IsEqualTo(DecodeStatus.NotDetected);
        await Assert.That((info, charsWritten)).IsEqualTo((-1, 0));
    }

    /// <summary>
    /// Of every pair of statuses, in either order, the one that got further is reported, the first on a tie; main or not makes no difference.
    /// Not detected gets nowhere, so a pair of them reports the initial diagnostics.
    /// </summary>
    [Test]
    public async Task Furthest_ReportsTheFurthestFirstOnATie()
    {
        foreach (var first in ByProgress)
        {
            foreach (var second in ByProgress)
            {
                foreach (var mainFirst in new[] { false, true })
                {
                    var result = New(ReportRule.Furthest);
                    if (mainFirst)
                        result.Main(first, 1, 1);
                    else
                        result.Other(first, 1, 1);
                    result.Other(second, 2, 2);

                    var expected = AttemptStatus.Progress(second) > AttemptStatus.Progress(first) ? 2 : AttemptStatus.Progress(first) > 0 ? 1 : -1;
                    var status = result.Report(out var charsWritten, out var info);

                    await Assert.That(info).IsEqualTo(expected).Because($"{first} then {second}");
                    await Assert.That(status).IsEqualTo(expected == 2 ? second : first);
                    await Assert.That(charsWritten).IsEqualTo(Math.Max(expected, 0));
                }
            }
        }
    }

    /// <summary>A not-detected result is not taken over the initial one, so the decoder's own not-detected diagnostics stay.</summary>
    [Test]
    public async Task Furthest_NotDetected_KeepsTheInitialDiagnostics()
    {
        var result = New(ReportRule.Furthest);
        result.Other(DecodeStatus.NotDetected, 0, 7);

        await Assert.That(result.Report(out _, out var info)).IsEqualTo(DecodeStatus.NotDetected);
        await Assert.That(info).IsEqualTo(-1);
    }

    [Test]
    [Arguments(DecodeStatus.Success, true)]
    [Arguments(DecodeStatus.UnmappedCharacter, true)]
    [Arguments(DecodeStatus.DataUncorrectable, false)]
    public async Task Furthest_SaysWhetherTheResultSettles(DecodeStatus status, bool settled)
    {
        var result = New(ReportRule.Furthest);

        await Assert.That(result.Other(status, 0, 1)).IsEqualTo(settled);
    }

    #endregion
}
