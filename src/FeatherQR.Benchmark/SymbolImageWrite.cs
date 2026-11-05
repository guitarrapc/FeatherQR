using System.Buffers;
using System.Text;

/// <summary>
/// SVG and PNG from the core with no graphics library (SymbolImageWriter), the symbol made in setup so only the writer is measured.
///
///   Small : Standard QR version 1-2, where the per-call work dominates
///   Large : Standard QR version 40, about 31k modules, where the scan of the matrix dominates
///   Micro : Micro QR M4
///   RmQR  : rMQR R17x139, the widest
///
/// The string and array forms allocate their result. The writer forms write into a buffer reused across calls, so they allocate nothing.
/// Large_Png_3px is a module size that straddles bytes, the case the PNG writer's matcher compresses least.
/// </summary>
public class SymbolImageWrite
{
    private QRCodeData _small = default!;
    private QRCodeData _large = default!;
    private MicroQRCodeData _micro = default!;
    private RmQRCodeData _rmqr = default!;
    private readonly ArrayBufferWriter<byte> _writer = new(1 << 20);
    private static readonly SymbolImageOptions ThreePixels = new() { ModulePixelSize = 3 };

    [GlobalSetup]
    public void Setup()
    {
        _small = QRCodeGenerator.Create("HELLO WORLD 2026", QREccLevel.M);
        _large = QRCodeGenerator.Create(BuildDeterministicText(2900), QREccLevel.L);
        _micro = MicroQRCodeGenerator.Create("HELLO2026", MicroQREccLevel.L, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M4 });
        _rmqr = RmQRCodeGenerator.Create(BuildDeterministicText(100), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 });
    }

    [Benchmark]
    public string Small_Svg() => SymbolImageWriter.GetSvgString(_small);

    [Benchmark]
    public string Large_Svg() => SymbolImageWriter.GetSvgString(_large);

    [Benchmark]
    public int Large_SvgWriter()
    {
        _writer.ResetWrittenCount();
        return SymbolImageWriter.WriteSvg(_large, _writer);
    }

    [Benchmark]
    public string Micro_Svg() => SymbolImageWriter.GetSvgString(_micro);

    [Benchmark]
    public string RmQR_Svg() => SymbolImageWriter.GetSvgString(_rmqr);

    [Benchmark]
    public byte[] Small_Png() => SymbolImageWriter.GetPngBytes(_small);

    [Benchmark]
    public byte[] Large_Png() => SymbolImageWriter.GetPngBytes(_large);

    [Benchmark]
    public int Large_PngWriter()
    {
        _writer.ResetWrittenCount();
        return SymbolImageWriter.WritePng(_large, _writer);
    }

    [Benchmark]
    public byte[] Large_Png_3px() => SymbolImageWriter.GetPngBytes(_large, ThreePixels);

    [Benchmark]
    public byte[] Micro_Png() => SymbolImageWriter.GetPngBytes(_micro);

    [Benchmark]
    public byte[] RmQR_Png() => SymbolImageWriter.GetPngBytes(_rmqr);

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
