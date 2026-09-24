namespace FeatherQR.Internals.MicroQR;

/// <summary>
/// How much error correction a grid sampled from an image may use, from how much of the symbol's known structure it shows.
/// </summary>
/// <remarks>
/// Evidence is counted in bits: the negative log of the chance that a grid sampled on anything other than this symbol comes out at least this close.
/// Reed-Solomon gives <c>8·ecc − log2 V(n, e)</c> for a read with <c>e</c> corrections, <c>V</c> the number of words within <c>e</c> codewords of one codeword;
/// the format word, among the words within 3 bits of a candidate, the share within <c>d</c>; the timing patterns the tail of their mismatch count at even odds;
/// and the quiet-zone line the tail of its dark count at <see cref="QuietZoneDarkShare"/>, lower than even because texture beside a finder is often margin.
/// A read is kept when the sum reaches <see cref="ThresholdBits"/>, so a grid that shows its structure may use every correction its level allows, and one that does not may use fewer or none.
/// </remarks>
internal static class MicroQRGridEvidence
{
    /// <summary>
    /// A failing image sends about 2^14 grids through Reed-Solomon, so 33 bits leave about 2^-19 an image before the bitstream's own validity.
    /// </summary>
    internal const int ThresholdBits = 33;

    internal const double QuietZoneDarkShare = 0.3;

    /// <summary>Passed for a quiet zone not read: it earns nothing.</summary>
    public const int QuietZoneUnread = -1;

    /// <summary>Set by a test around a decode on its own thread, to see what the decode reads without the rule: the premise of a test that the rule refuses an image.</summary>
    [ThreadStatic]
    internal static bool SuspendedOnThisThread;

    private const int UnitsPerBit = 256;
    private const int MaxTimingModules = 2 * (17 - 8);
    private const int MaxQuietZoneModules = 2 * 17 + 1;

    private const int Threshold = ThresholdBits * UnitsPerBit;
    private static readonly int[] reedSolomon = BuildReedSolomon();
    private static readonly int[] format = BuildFormat();
    private static readonly int[] timing = BuildTail(static size => 2 * (size - 8), MaxTimingModules, 0.5);
    private static readonly int[] quietZone = BuildTail(static size => 2 * size + 1, MaxQuietZoneModules, QuietZoneDarkShare);

    /// <summary>The most corrections a read may use, or -1 when the grid cannot be kept at any count; the quiet zone counts only when read (not <see cref="QuietZoneUnread"/>).</summary>
    public static int MaxCorrections(MicroQRVersion version, MicroQREccLevel eccLevel, int formatDistance, int timingMismatches, int quietZoneDark)
    {
        var v = (int)version - 1;
        var structure = format[formatDistance] + timing[v * (MaxTimingModules + 1) + timingMismatches];
        if (quietZoneDark != QuietZoneUnread)
            structure += quietZone[v * (MaxQuietZoneModules + 1) + quietZoneDark];

        var row = MicroQRConstants.GetSymbolNumber(version, eccLevel) * 8;
        var e = MicroQRConstants.GetErrorCorrectionCapacity(version, eccLevel);
        while (e >= 0 && reedSolomon[row + e] + structure < Threshold)
            e--;
        return e;
    }

    private static int[] BuildReedSolomon()
    {
        var table = new int[8 * 8];
        for (var symbolNumber = 0; symbolNumber < 8; symbolNumber++)
        {
            MicroQRConstants.GetVersionAndEccFromSymbolNumber(symbolNumber, out var version, out var eccLevel);
            var ecc = MicroQRConstants.GetEccCodewordCount(version, eccLevel);
            var total = MicroQRConstants.GetDataCodewordCount(version, eccLevel) + ecc;
            for (var e = 0; e <= MicroQRConstants.GetErrorCorrectionCapacity(version, eccLevel); e++)
            {
                // V(n, e) = Σ C(n, i)·255^i
                double ball = 0, choose = 1;
                for (var i = 0; i <= e; i++)
                {
                    ball += choose * Math.Pow(255, i);
                    choose = choose * (total - i) / (i + 1);
                }
                table[symbolNumber * 8 + e] = Units(8.0 * ecc - Math.Log(ball, 2));
            }
        }
        return table;
    }

    private static int[] BuildFormat()
    {
        // Words within d bits of a candidate: Σ C(15, i)
        ReadOnlySpan<int> within = [1, 16, 121, 576];
        var table = new int[4];
        for (var d = 0; d < 4; d++)
            table[d] = Units(Math.Log(576.0 / within[d], 2));
        return table;
    }

    /// <summary>Per version, <c>−log2 P(at most k of n modules wrong)</c> with each wrong at probability <paramref name="p"/>.</summary>
    private static int[] BuildTail(Func<int, int> modules, int maxModules, double p)
    {
        var table = new int[4 * (maxModules + 1)];
        for (var v = 0; v < 4; v++)
        {
            var n = modules(11 + 2 * v);
            double tail = 0, choose = 1;
            for (var k = 0; k <= n; k++)
            {
                tail += choose * Math.Pow(p, k) * Math.Pow(1 - p, n - k);
                choose = choose * (n - k) / (k + 1);
                table[v * (maxModules + 1) + k] = Units(-Math.Log(Math.Min(1.0, tail), 2));
            }
        }
        return table;
    }

    // Floored, with room for a last-place error where a count is a whole number of bits
    private static int Units(double bits) => Math.Max(0, (int)Math.Floor(bits * UnitsPerBit + 1e-6));
}
