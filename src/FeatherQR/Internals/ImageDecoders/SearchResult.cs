namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// How the image decoders read a decode attempt's status: whether it ends the search for its symbol, and how far it got.
/// </summary>
internal static class AttemptStatus
{
    /// <summary>A read, or a read too long for the destination, which only a larger destination changes.</summary>
    public static bool IsTerminal(DecodeStatus status)
        => status is DecodeStatus.Success or DecodeStatus.DestinationTooSmall;

    /// <summary>
    /// Whether a failed decode read the symbol and failed on its content: a verdict the caller can act on. <see cref="DecodeStatus.DataUncorrectable"/> and <see cref="DecodeStatus.InvalidBitstream"/> are not, since noise reaches them.
    /// </summary>
    public static bool IsContentVerdict(DecodeStatus status)
        => status is DecodeStatus.UnmappedCharacter or DecodeStatus.UnsupportedContent;

    /// <summary>A result no other grid or pass for the same symbol improves on: read, too long for the destination, or a verdict on its content, which like a read comes only once every Reed-Solomon block has corrected.</summary>
    public static bool IsSettled(DecodeStatus status)
        => IsTerminal(status) || IsContentVerdict(status);

    /// <summary>
    /// Whether the attempt got past the format information. A grid sampled in the wrong place overwhelmingly fails before it, so one that got past it almost certainly lies on the symbol, and is worth refining.
    /// </summary>
    public static bool IsPastFormat(DecodeStatus status)
        => status is not DecodeStatus.NotDetected
            and not DecodeStatus.InvalidMatrix
            and not DecodeStatus.FormatInformationInvalid;

    /// <summary>
    /// How far an attempt got: 0 nothing, 1 a matrix or format information refused, 2 past the format information, 3 a verdict on the content, 4 a read too long for the destination, 5 a read.
    /// </summary>
    /// <remarks>
    /// A verdict comes after error correction too, so a wrong grid's correction failure tried before the right one cannot mask it.
    /// A read too long for the destination ranks above a verdict, one on another symbol included, since a larger destination reads that symbol and the verdict holds at any size.
    /// </remarks>
    public static int Progress(DecodeStatus status) => status switch
    {
        DecodeStatus.NotDetected => 0,
        DecodeStatus.InvalidMatrix or DecodeStatus.FormatInformationInvalid => 1,
        DecodeStatus.UnmappedCharacter or DecodeStatus.UnsupportedContent => 3,
        DecodeStatus.DestinationTooSmall => 4,
        DecodeStatus.Success => 5,
        _ => 2,
    };
}

/// <summary>Which result a search of many decode attempts reports when none of them reads the symbol.</summary>
internal enum ReportRule
{
    /// <summary>
    /// The main path's result, unless another attempt settles: Standard QR, whose main path at each level is the attempt it would make alone (the corner the triangle's shape names, the estimated dimension, the first grid, the grid as sampled).
    /// </summary>
    MainPath,

    /// <summary>The result that got furthest (<see cref="AttemptStatus.Progress"/>), the first on a tie: Micro QR and rMQR, which try many grids of one finder with no one of them first in any sense.</summary>
    Furthest,
}

/// <summary>
/// The result a search of many decode attempts reports, under one of two rules (<see cref="ReportRule"/>).
/// </summary>
/// <remarks>
/// It decides which result is reported, not where the search stops: each method returns whether the result it was given settles the symbol (<see cref="AttemptStatus.IsSettled"/>), and the caller decides whether that ends its search.
/// Standard QR stops at a settled result; Micro QR's and rMQR's scans go on past everything but a read, since another symbol in the image may read.
/// </remarks>
/// <typeparam name="TInfo">The decoder's diagnostic record.</typeparam>
internal struct SearchResult<TInfo>
{
    private readonly ReportRule _rule;
    private bool _hasResult;

    /// <summary>The status reported so far.</summary>
    public DecodeStatus Status { readonly get; private set; }

    /// <summary>The characters written by the attempt reported so far.</summary>
    public int CharsWritten { readonly get; private set; }

    /// <summary>The diagnostics reported so far.</summary>
    public TInfo Info { readonly get; private set; }

    /// <param name="rule">Which result is reported.</param>
    /// <param name="notDetected">The decoder's diagnostics of an image not decoded at all, reported until an attempt is taken.</param>
    public SearchResult(ReportRule rule, in TInfo notDetected)
    {
        _rule = rule;
        _hasResult = false;
        Status = DecodeStatus.NotDetected;
        CharsWritten = 0;
        Info = notDetected;
    }

    /// <summary>
    /// The result of the main path's attempt. Under <see cref="ReportRule.MainPath"/> it is reported unless a settled result was taken before it; under <see cref="ReportRule.Furthest"/> it is one more attempt.
    /// </summary>
    /// <returns>Whether the result settles the symbol.</returns>
    public bool Main(DecodeStatus status, int charsWritten, in TInfo info)
    {
        if (_rule == ReportRule.Furthest)
            return Other(status, charsWritten, info);

        if (!_hasResult || !AttemptStatus.IsSettled(Status))
            Take(status, charsWritten, info);
        return AttemptStatus.IsSettled(status);
    }

    /// <summary>
    /// The result of any other attempt. Under <see cref="ReportRule.MainPath"/> it is reported when it settles, or when no attempt came before it; under <see cref="ReportRule.Furthest"/> when it got further than every attempt before it.
    /// </summary>
    /// <returns>Whether the result settles the symbol.</returns>
    public bool Other(DecodeStatus status, int charsWritten, in TInfo info)
    {
        var settled = AttemptStatus.IsSettled(status);
        var take = _rule == ReportRule.Furthest
            ? AttemptStatus.Progress(status) > AttemptStatus.Progress(Status)
            : !_hasResult || (settled && !AttemptStatus.IsSettled(Status));
        if (take)
            Take(status, charsWritten, info);
        return settled;
    }

    /// <summary>The result to report.</summary>
    public readonly DecodeStatus Report(out int charsWritten, out TInfo info)
    {
        charsWritten = CharsWritten;
        info = Info;
        return Status;
    }

    private void Take(DecodeStatus status, int charsWritten, in TInfo info)
    {
        _hasResult = true;
        Status = status;
        CharsWritten = charsWritten;
        Info = info;
    }
}
