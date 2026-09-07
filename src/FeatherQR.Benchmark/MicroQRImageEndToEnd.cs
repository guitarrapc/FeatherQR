/// <summary>
/// End-to-end PNG image generation through the public Micro QR API (MicroQRCodeImageBuilder.GetPngBytes).
/// MicroQRCodeData is pre-generated in setup so the measurement covers the Skia render + PNG encode path only, not the Micro QR encoding itself.
///
/// Micro QR matrices are tiny (11-17 core modules), so per-image overhead dominates; scenarios cover the smallest and largest versions at the default 512px output plus a small 128px output typical for inline display.
/// </summary>
public class MicroQRImageEndToEnd
{
    private MicroQRCodeData _m2 = default!;
    private MicroQRCodeData _m4 = default!;
    private byte[] _m4Luminance = default!;
    private int _m4ImageSide;
    private char[] _decodeBuffer = default!;

    [GlobalSetup]
    public void Setup()
    {
        _m2 = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.L); // M2, 13x13 core
        _m4 = MicroQRCodeGenerator.Create("MICRO QR M4 BENCH", MicroQREccLevel.M); // M4, 17x17 core

        // Grayscale of an 8px/module render for the image-decode scenario
        using var bitmap = new MicroQRCodeImageBuilder(_m4).WithModulePixelSize(8).ToBitmap();
        _m4ImageSide = bitmap.Width;
        _m4Luminance = new byte[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                _m4Luminance[y * bitmap.Width + x] = bitmap.GetPixel(x, y).Red;
            }
        }
        _decodeBuffer = new char[MicroQRCodeDecoder.GetMaxDecodedLength(MicroQRVersion.M4)];
    }

    [Benchmark]
    public byte[] M2_512px() => MicroQRCodeImageBuilder.GetPngBytes(_m2, 512);

    [Benchmark]
    public byte[] M4_512px() => MicroQRCodeImageBuilder.GetPngBytes(_m4, 512);

    [Benchmark]
    public byte[] M4_128px() => MicroQRCodeImageBuilder.GetPngBytes(_m4, 128);

    [Benchmark]
    public bool M4_ImageDecode_Span() => MicroQRCodeDecoder.TryDecodeImage(_m4Luminance, _m4ImageSide, _m4ImageSide, _decodeBuffer, out _, out _);
}
