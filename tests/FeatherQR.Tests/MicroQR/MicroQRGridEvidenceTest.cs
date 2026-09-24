using FeatherQR.Internals.MicroQR;

namespace FeatherQR.Tests;

/// <summary>
/// The corrections a sampled grid may use, against the evidence written out in exact arithmetic here: Reed-Solomon's ball, the format word's share, and the tails of the timing and quiet-zone counts.
/// </summary>
public class MicroQRGridEvidenceTest
{
    private const double Threshold = 33;
    private const double QuietZoneDarkShare = 0.3;

    public static IEnumerable<(MicroQRVersion Version, MicroQREccLevel Level)> Levels()
    {
        yield return (MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly);
        yield return (MicroQRVersion.M2, MicroQREccLevel.L);
        yield return (MicroQRVersion.M2, MicroQREccLevel.M);
        yield return (MicroQRVersion.M3, MicroQREccLevel.L);
        yield return (MicroQRVersion.M3, MicroQREccLevel.M);
        yield return (MicroQRVersion.M4, MicroQREccLevel.L);
        yield return (MicroQRVersion.M4, MicroQREccLevel.M);
        yield return (MicroQRVersion.M4, MicroQREccLevel.Q);
    }

    /// <summary>Every format distance, timing count and quiet-zone count: the table agrees with the exact sum wherever the sum is not within a hundredth of a bit of the threshold.</summary>
    [Test]
    [MethodDataSource(nameof(Levels))]
    public async Task MaxCorrections_MatchesTheExactEvidence(MicroQRVersion version, MicroQREccLevel level)
    {
        var size = 9 + 2 * (int)version;
        var disagreements = new List<string>();
        for (var d = 0; d <= 3; d++)
        {
            for (var k = 0; k <= 2 * (size - 8); k++)
            {
                for (var q = MicroQRGridEvidence.QuietZoneUnread; q <= 2 * size + 1; q++)
                {
                    var (expected, margin) = Reference(version, level, d, k, q);
                    var actual = MicroQRGridEvidence.MaxCorrections(version, level, d, k, q);
                    if (actual != expected && margin > 0.01)
                        disagreements.Add($"d={d} k={k} q={q}: {actual}, expected {expected}");
                }
            }
        }

        await Assert.That(disagreements).IsEmpty();
    }

    /// <summary>A grid that shows all its structure may use every correction its level allows.</summary>
    [Test]
    [MethodDataSource(nameof(Levels))]
    public async Task CleanGrid_UsesEveryCorrection(MicroQRVersion version, MicroQREccLevel level)
    {
        await Assert.That(MicroQRGridEvidence.MaxCorrections(version, level, 0, 0, 0)).IsEqualTo(MicroQRConstants.GetErrorCorrectionCapacity(version, level));
    }

    /// <summary>M1 detects errors and corrects none, and its format word and timing modules are not enough on their own: it is kept only with its quiet zone.</summary>
    [Test]
    public async Task M1_NeedsItsQuietZone()
    {
        await Assert.That(MicroQRGridEvidence.MaxCorrections(MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly, 0, 0, MicroQRGridEvidence.QuietZoneUnread)).IsEqualTo(-1);
        await Assert.That(MicroQRGridEvidence.MaxCorrections(MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly, 0, 0, 0)).IsEqualTo(0);
    }

    /// <summary>A grid on texture (a format word 3 bits off, half its timing modules wrong, no quiet zone read) keeps none of M3-M's and M4-M's limit, where texture was read before, but M4-Q's limit is enough on its own.</summary>
    [Test]
    [Arguments(MicroQRVersion.M1, MicroQREccLevel.ErrorDetectionOnly, -1)]
    [Arguments(MicroQRVersion.M2, MicroQREccLevel.M, 1)]
    [Arguments(MicroQRVersion.M3, MicroQREccLevel.L, 1)]
    [Arguments(MicroQRVersion.M3, MicroQREccLevel.M, 2)]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.M, 4)]
    [Arguments(MicroQRVersion.M4, MicroQREccLevel.Q, 7)]
    public async Task TextureLikeGrid_KeepsFewerCorrections(MicroQRVersion version, MicroQREccLevel level, int expected)
    {
        var size = 9 + 2 * (int)version;

        var actual = MicroQRGridEvidence.MaxCorrections(version, level, 3, size - 8, MicroQRGridEvidence.QuietZoneUnread);

        await Assert.That(actual).IsEqualTo(expected);
    }

    /// <summary>
    /// A real M4-M symbol at its correction limit whose format word is a bit off and whose timing modules all read the other way: Reed-Solomon reads it, the structure does not earn that, and a clean quiet zone does.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SampledGrid_ReadAtTheLimit_KeptOnlyWhenItsStructureEarnsIt(bool mirrored)
    {
        var (grid, size) = Matrix("12345", MicroQREccLevel.M, MicroQRVersion.M4);
        // Five data modules in five codewords: M4-M's limit
        foreach (var column in new[] { 16, 14, 12, 10, 8 })
            grid[16 * size + column] ^= 1;
        for (var i = 8; i < size; i++)
        {
            grid[i] ^= 1;
            grid[i * size] ^= 1;
        }
        // One format bit (row 8, column 1)
        grid[8 * size + 1] ^= 1;
        if (mirrored)
            grid = Transpose(grid, size);

        var damaged = Decode(grid, size, quietZoneDark: 2 * size + 1, mirrored);
        var clean = Decode(grid, size, quietZoneDark: 0, mirrored);
        MicroQRGridEvidence.SuspendedOnThisThread = true;
        (DecodeStatus Status, MicroQRCodeDecodeInfo Info, string Text) suspended;
        try
        {
            suspended = Decode(grid, size, quietZoneDark: 2 * size + 1, mirrored);
        }
        finally
        {
            MicroQRGridEvidence.SuspendedOnThisThread = false;
        }

        await Assert.That(suspended.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(suspended.Info.ErrorsCorrected).IsEqualTo(5);
        await Assert.That(damaged.Status).IsEqualTo(DecodeStatus.DataUncorrectable);
        await Assert.That(damaged.Info.ErrorsCorrected).IsEqualTo(5);
        await Assert.That(clean.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(clean.Text).IsEqualTo("12345");
    }

    /// <summary>A clean M1 grid reads with a light quiet zone and not with a dark one: its format word and timing modules are not enough on their own.</summary>
    [Test]
    public async Task SampledGrid_M1_ReadOnlyWithItsQuietZone()
    {
        var (grid, size) = Matrix("12345", MicroQREccLevel.ErrorDetectionOnly, MicroQRVersion.M1);

        var light = Decode(grid, size, quietZoneDark: 0, mirrored: false);
        var dark = Decode(grid, size, quietZoneDark: 2 * size + 1, mirrored: false);

        await Assert.That(light.Status).IsEqualTo(DecodeStatus.Success);
        await Assert.That(light.Text).IsEqualTo("12345");
        await Assert.That(dark.Status).IsEqualTo(DecodeStatus.DataUncorrectable);
    }

    /// <summary>A matrix handed to the decoder is not held to the rule: only a grid sampled from an image is.</summary>
    [Test]
    public async Task Matrix_NotHeldToTheRule()
    {
        var (grid, size) = Matrix("12345", MicroQREccLevel.ErrorDetectionOnly, MicroQRVersion.M1);
        for (var i = 8; i < size; i++)
            grid[i] ^= 1;

        var status = MicroQRMatrixDecoder.DecodeMatrix(grid, size, new char[16], out _, out _);

        await Assert.That(status).IsEqualTo(DecodeStatus.Success);
    }

    private static (byte[] Grid, int Size) Matrix(string text, MicroQREccLevel level, MicroQRVersion version)
    {
        var qr = MicroQRCodeGenerator.Create(text, level, new MicroQRCodeGeneratorOptions { Version = version });
        var size = qr.Size - 4;
        var grid = new byte[size * size];
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
                grid[row * size + column] = qr[row + 2, column + 2] ? (byte)1 : (byte)0;
        }
        return (grid, size);
    }

    private static byte[] Transpose(byte[] grid, int size)
    {
        var transposed = new byte[grid.Length];
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
                transposed[column * size + row] = grid[row * size + column];
        }
        return transposed;
    }

    private static (DecodeStatus Status, MicroQRCodeDecodeInfo Info, string Text) Decode(byte[] grid, int size, int quietZoneDark, bool mirrored)
    {
        var destination = new char[64];
        var status = mirrored
            ? MicroQRMatrixDecoder.DecodeSampledGrid(grid, new TransposedModules<MatrixModules>(new MatrixModules(size)), size, default, 0, 0, 0, new CountedQuietZone(quietZoneDark), destination, out var charsWritten, out var info)
            : MicroQRMatrixDecoder.DecodeSampledGrid(grid, new MatrixModules(size), size, default, 0, 0, 0, new CountedQuietZone(quietZoneDark), destination, out charsWritten, out info);
        return (status, info, new string(destination, 0, charsWritten));
    }

    /// <summary>The most corrections the exact evidence allows, and how far the deciding sum lies from the threshold.</summary>
    private static (int MaxCorrections, double Margin) Reference(MicroQRVersion version, MicroQREccLevel level, int formatDistance, int timingMismatches, int quietZoneDark)
    {
        var size = 9 + 2 * (int)version;
        var within = formatDistance switch { 0 => 1, 1 => 16, 2 => 121, _ => 576 };
        var structure = Math.Log2(576.0 / within) + Tail(2 * (size - 8), timingMismatches, 0.5);
        if (quietZoneDark >= 0)
            structure += Tail(2 * size + 1, quietZoneDark, QuietZoneDarkShare);

        var ecc = MicroQRConstants.GetEccCodewordCount(version, level);
        var total = MicroQRConstants.GetDataCodewordCount(version, level) + ecc;
        var margin = double.MaxValue;
        for (var e = MicroQRConstants.GetErrorCorrectionCapacity(version, level); e >= 0; e--)
        {
            double ball = 0;
            for (var i = 0; i <= e; i++)
                ball += Choose(total, i) * Math.Pow(255, i);
            var sum = 8.0 * ecc - Math.Log2(ball) + structure;
            margin = Math.Min(margin, Math.Abs(sum - Threshold));
            if (sum >= Threshold)
                return (e, margin);
        }
        return (-1, margin);
    }

    private static double Tail(int n, int k, double p)
    {
        double sum = 0;
        for (var i = 0; i <= k; i++)
            sum += Choose(n, i) * Math.Pow(p, i) * Math.Pow(1 - p, n - i);
        return -Math.Log2(Math.Min(1.0, sum));
    }

    private static double Choose(int n, int k)
    {
        double c = 1;
        for (var i = 0; i < k; i++)
            c = c * (n - i) / (i + 1);
        return c;
    }
}
