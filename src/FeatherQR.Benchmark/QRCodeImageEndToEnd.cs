using System.Text;

/// <summary>
/// End-to-end PNG image generation through the public API (QRCodeImageBuilder.GetPngBytes).
/// QRCodeData is pre-generated in setup so the measurement covers the Skia render + PNG encode path only, not the QR encoding itself.
///
/// Scenarios cover the version/pixel spread:
///   Small : version 1 matrix (few modules, per-image overhead dominated)
///   Large : version 40 matrix (~31k modules, per-module draw calls dominated)
/// each rendered at 512px and 2048px output sizes.
/// </summary>
public class QRCodeImageEndToEnd
{
    private QRCodeData _small = default!;
    private QRCodeData _large = default!;

    [GlobalSetup]
    public void Setup()
    {
        _small = QRCodeGenerator.Create("HELLO WORLD 2026", QREccLevel.M); // version 1-2
        _large = QRCodeGenerator.Create(BuildDeterministicText(2900), QREccLevel.L); // version 40
    }

    [Benchmark]
    public byte[] Small_512px() => QRCodeImageBuilder.GetPngBytes(_small, 512);

    [Benchmark]
    public byte[] Small_2048px() => QRCodeImageBuilder.GetPngBytes(_small, 2048);

    [Benchmark]
    public byte[] Large_512px() => QRCodeImageBuilder.GetPngBytes(_large, 512);

    [Benchmark]
    public byte[] Large_2048px() => QRCodeImageBuilder.GetPngBytes(_large, 2048);

    private static string BuildDeterministicText(int length)
    {
        var sb = new StringBuilder(length);
        var rng = new Random(42);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,:/?&=-_";
        for (var i = 0; i < length; i++)
        {
            sb.Append(alphabet[rng.Next(alphabet.Length)]);
        }
        return sb.ToString();
    }
}
