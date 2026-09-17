using FeatherQR.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The segmentation program against an independent reference that always walks all seven states,
/// so the fast path taken through runs no mode but Byte encodes is held to the cost and the plan
/// the state machine would have produced. The reference shares only the cost model's numbers with
/// the production code, not its control flow.
/// </summary>
public class ModeSegmenterByteRunParityTest
{
    private const int ModeIndicatorBits = 4;

    private const int StateNumeric0 = 0;
    private const int StateNumeric1 = 1;
    private const int StateNumeric2 = 2;
    private const int StateAlnum0 = 3;
    private const int StateAlnum1 = 4;
    private const int StateByte = 5;
    private const int StateStart = 6;
    private const int States = 7;
    private const int Unreachable = int.MaxValue / 4;

    public static IEnumerable<(int CciNumeric, int CciAlnum, int CciByte)> Bands() =>
    [
        (10, 9, 8),
        (12, 11, 16),
        (14, 13, 16),
    ];

    public static IEnumerable<(string Name, string Text)> Corpus() =>
    [
        ("byte-run-short", "abc"),
        ("byte-run-long", new string('a', 200)),
        ("prose", "The quick brown fox jumps over the lazy dog. "),
        ("prose-long", string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 6))),
        ("digits", "01234567890123456789"),
        ("alnum", "HELLO WORLD $%*+-./:"),
        ("mixed-runs", "order 20260915 item 0000123456 qty 42 "),
        ("alternating", string.Concat(Enumerable.Repeat("a7", 40))),
        ("byte-then-digits", new string('z', 40) + "1234567890123456"),
        ("digits-then-byte", "1234567890123456" + new string('z', 40)),
        ("latin1", "Crème brûlée à la carte, jalapeño, naïve café."),
        ("japanese", "こんにちは世界、QRコードのテストです。"),
        ("surrogates", "photo 📷 gallery 🎉🎊🎈 end"),
        ("surrogate-run", string.Concat(Enumerable.Repeat("🎉", 30))),
        ("lone-surrogate", "ab\uD83Ccd"),
        ("single-byte-char", "z"),
        ("single-digit", "7"),
    ];

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task ComputeCosts_MatchesTheAllStatesReference(string name, string text)
    {
        foreach (var charset in new[] { EciMode.Default, EciMode.Iso8859_1, EciMode.Utf8 })
        {
            foreach (var (cciNumeric, cciAlnum, cciByte) in Bands())
            {
                foreach (var (allowAlnum, allowByte) in new[] { (true, true), (false, true), (true, false) })
                {
                    var expectedCost = Reference(text, charset, cciNumeric, cciAlnum, cciByte, allowAlnum, allowByte, out var expectedState);

                    var parents = new byte[text.Length * ModeSegmenter.ParentBytesPerChar];
                    var actualCost = ModeSegmenter.ComputeCosts(text, charset, ModeIndicatorBits, cciNumeric, cciAlnum, cciByte, parents, out var actualState, allowAlnum, allowByte);

                    var because = $"{name} in {charset} at ({cciNumeric}, {cciAlnum}, {cciByte}) alnum={allowAlnum} byte={allowByte}";
                    await Assert.That(actualCost).IsEqualTo(expectedCost).Because(because);
                    if (expectedCost < Unreachable)
                        await Assert.That(actualState).IsEqualTo(expectedState).Because(because);
                }
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Corpus))]
    public async Task ReconstructedPlan_CostsWhatTheProgramSaid(string name, string text)
    {
        if (text.Length == 0)
            return;

        foreach (var charset in new[] { EciMode.Default, EciMode.Utf8 })
        {
            foreach (var (cciNumeric, cciAlnum, cciByte) in Bands())
            {
                var parents = new byte[text.Length * ModeSegmenter.ParentBytesPerChar];
                var cost = ModeSegmenter.ComputeCosts(text, charset, ModeIndicatorBits, cciNumeric, cciAlnum, cciByte, parents, out var finalState);
                var segments = new ModeSegment[text.Length];

                await Assert.That(ModeSegmenter.Reconstruct(text, parents, finalState, segments, out var count)).IsTrue();
                ModeSegmenter.FillUnitCounts(text, charset, segments.AsSpan(0, count));

                var measured = 0;
                var covered = 0;
                for (var i = 0; i < count; i++)
                {
                    var mode = segments[i].ModeIndex switch { 0 => EncodingMode.Numeric, 1 => EncodingMode.Alphanumeric, _ => EncodingMode.Byte };
                    var cci = segments[i].ModeIndex switch { 0 => cciNumeric, 1 => cciAlnum, _ => cciByte };
                    measured += ModeIndicatorBits + cci + ModeSegmenter.PayloadBits(mode, segments[i].UnitCount);
                    covered += segments[i].Length;
                }

                var because = $"{name} in {charset} at ({cciNumeric}, {cciAlnum}, {cciByte})";
                await Assert.That(covered).IsEqualTo(text.Length).Because(because);
                await Assert.That(measured).IsEqualTo(cost).Because(because);
            }
        }
    }

    /// <summary>Independent program: every state relaxed from every state, every character, no shortcuts.</summary>
    private static int Reference(string text, EciMode charset, int cciNumeric, int cciAlnum, int cciByte, bool allowAlnum, bool allowByte, out int finalState)
    {
        var prev = new int[States];
        for (var s = 0; s < States; s++)
            prev[s] = Unreachable;
        prev[StateStart] = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var cur = new int[States];
            for (var s = 0; s < States; s++)
                cur[s] = Unreachable;

            var c = text[i];
            var isNumeric = c is >= '0' and <= '9';
            var isAlnum = IsAlnum(c);
            var byteBits = 8 * ByteCost(text, i, charset);

            for (var from = 0; from < States; from++)
            {
                var basis = prev[from];
                if (basis >= Unreachable)
                    continue;

                if (isNumeric)
                {
                    var target = from <= StateNumeric2 ? (from == StateNumeric2 ? StateNumeric0 : from + 1) : StateNumeric1;
                    var cost = from <= StateNumeric2 ? basis + (from == StateNumeric0 ? 4 : 3) : basis + ModeIndicatorBits + cciNumeric + 4;
                    if (cost < cur[target])
                        cur[target] = cost;
                }

                if (isAlnum && allowAlnum)
                {
                    var pair = from is StateAlnum0 or StateAlnum1;
                    var target = pair ? (from == StateAlnum0 ? StateAlnum1 : StateAlnum0) : StateAlnum1;
                    var cost = pair ? basis + (from == StateAlnum0 ? 6 : 5) : basis + ModeIndicatorBits + cciAlnum + 6;
                    if (cost < cur[target])
                        cur[target] = cost;
                }

                if (allowByte)
                {
                    var cost = from == StateByte ? basis + byteBits : basis + ModeIndicatorBits + cciByte + byteBits;
                    if (cost < cur[StateByte])
                        cur[StateByte] = cost;
                }
            }

            prev = cur;
        }

        var best = Unreachable;
        finalState = StateByte;
        for (var s = 0; s <= StateByte; s++)
        {
            if (prev[s] < best)
            {
                best = prev[s];
                finalState = s;
            }
        }
        return best;
    }

    private static bool IsAlnum(char c)
        => (c is >= '0' and <= '9') || (c is >= 'A' and <= 'Z') || c is ' ' or '$' or '%' or '*' or '+' or '-' or '.' or '/' or ':';

    private static int ByteCost(string text, int index, EciMode charset)
    {
        if (charset != EciMode.Utf8)
            return 1;
        var c = text[index];
        if (c < 0x80)
            return 1;
        if (c < 0x800)
            return 2;
        if (char.IsHighSurrogate(c))
            return index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 4 : 3;
        if (char.IsLowSurrogate(c))
            return index > 0 && char.IsHighSurrogate(text[index - 1]) ? 0 : 3;
        return 3;
    }
}
