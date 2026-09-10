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

    // Styled renders leave the merged-run fast path and draw every data module through the shape,
    // and they are also where the finder patterns stop coming from the module loop and get drawn
    // as three rectangles each. Version 40 is where per-module work dominates.
    [Benchmark]
    public byte[] Large_512px_Styled() => new QRCodeImageBuilder(_large)
        .WithSize(512, 512)
        .WithModuleShape(CircleModuleShape.Default, 0.85f)
        .ToByteArray();

    [Benchmark]
    public byte[] Small_512px_Styled() => new QRCodeImageBuilder(_small)
        .WithSize(512, 512)
        .WithModuleShape(CircleModuleShape.Default, 0.85f)
        .ToByteArray();

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
