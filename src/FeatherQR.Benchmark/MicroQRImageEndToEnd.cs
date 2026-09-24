/// <summary>
/// End-to-end PNG image generation through the public Micro QR API (MicroQRCodeImageBuilder.GetPngBytes), and image decode.
/// MicroQRCodeData is pre-generated in setup so the render measurements cover the Skia render + PNG encode path, not the Micro QR encoding itself.
///
/// Micro QR matrices are tiny (11-17 core modules), so per-image overhead dominates; scenarios cover the smallest and largest versions at the default 512px output plus a small 128px output typical for inline display.
/// </summary>
public class MicroQRImageEndToEnd
{
    private MicroQRCodeData _m2 = default!;
    private MicroQRCodeData _m4 = default!;
    private byte[] _m4Luminance = default!;
    private int _m4ImageSide;
    private byte[] _m1ShadowLuminance = default!;
    private int _m1ShadowSide;
    private byte[] _m1TurnedShadowLuminance = default!;
    private int _m1TurnedShadowSide;
    private byte[] _standardQRLuminance = default!;
    private int _standardQRSide;
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

        // An M1 symbol under a 55 % shadow: only the regional pass reads it
        var m1 = MicroQRCodeGenerator.Create("12345", MicroQREccLevel.ErrorDetectionOnly);
        const int PixelsPerModule = 4;
        _m1ShadowSide = m1.Size * PixelsPerModule;
        _m1ShadowLuminance = new byte[_m1ShadowSide * _m1ShadowSide];
        var softness = 1.5f * PixelsPerModule;
        for (var y = 0; y < _m1ShadowSide; y++)
        {
            for (var x = 0; x < _m1ShadowSide; x++)
            {
                var t = Math.Clamp((x + 0.5f - _m1ShadowSide / 2f + softness) / (2 * softness), 0f, 1f);
                var light = 1f - 0.55f * t * t * (3 - 2 * t);
                _m1ShadowLuminance[y * _m1ShadowSide + x] = (byte)MathF.Round((m1[y / PixelsPerModule, x / PixelsPerModule] ? 30 : 220) * light);
            }
        }

        // Turned 20° and anti-aliased: past the lighting bounds, so the searches find no grid its structure earns
        _m1TurnedShadowLuminance = RenderTurnedUnderShadow(m1, PixelsPerModule, 20f, out _m1TurnedShadowSide);

        // A version 10 Standard QR symbol at 4 px/module: no Micro QR in it, and its finders and texture are what the searches try
        var standard = QRCodeGenerator.Create("MICRO QR DECODER, STANDARD QR IMAGE: A FAILURE PATH BENCHMARK 0123456789", QREccLevel.M, new QRCodeGeneratorOptions { Version = 10 });
        _standardQRSide = standard.Size * PixelsPerModule;
        _standardQRLuminance = new byte[_standardQRSide * _standardQRSide];
        for (var y = 0; y < _standardQRSide; y++)
        {
            for (var x = 0; x < _standardQRSide; x++)
                _standardQRLuminance[y * _standardQRSide + x] = standard[y / PixelsPerModule, x / PixelsPerModule] ? (byte)30 : (byte)220;
        }
    }

    /// <summary>Turned about the image centre, 4 × 4 samples a pixel, under the same shadow.</summary>
    private static byte[] RenderTurnedUnderShadow(MicroQRCodeData symbol, int pixelsPerModule, float turnDegrees, out int side)
    {
        var symbolSide = symbol.Size * pixelsPerModule;
        side = (int)MathF.Ceiling(symbolSide * 1.5f);
        var luminance = new byte[side * side];
        var cos = MathF.Cos(turnDegrees * MathF.PI / 180f);
        var sin = MathF.Sin(turnDegrees * MathF.PI / 180f);
        var centre = side / 2f;
        var softness = 1.5f * pixelsPerModule;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var dark = 0;
                for (var sy = 0; sy < 4; sy++)
                {
                    for (var sx = 0; sx < 4; sx++)
                    {
                        var dx = x + (sx + 0.5f) / 4f - centre;
                        var dy = y + (sy + 0.5f) / 4f - centre;
                        var u = (cos * dx + sin * dy + symbolSide / 2f) / pixelsPerModule;
                        var v = (-sin * dx + cos * dy + symbolSide / 2f) / pixelsPerModule;
                        if (u >= 0 && v >= 0 && u < symbol.Size && v < symbol.Size && symbol[(int)v, (int)u])
                            dark++;
                    }
                }
                var t = Math.Clamp((x + 0.5f - centre + softness) / (2 * softness), 0f, 1f);
                var light = 1f - 0.55f * t * t * (3 - 2 * t);
                var reflectance = (dark * 30 + (16 - dark) * 220) / 16f;
                luminance[y * side + x] = (byte)MathF.Round(reflectance * light);
            }
        }
        return luminance;
    }

    [Benchmark]
    public byte[] M2_512px() => MicroQRCodeImageBuilder.GetPngBytes(_m2, 512);

    [Benchmark]
    public byte[] M4_512px() => MicroQRCodeImageBuilder.GetPngBytes(_m4, 512);

    [Benchmark]
    public byte[] M4_128px() => MicroQRCodeImageBuilder.GetPngBytes(_m4, 128);

    [Benchmark]
    public bool M4_ImageDecode_Span() => MicroQRCodeDecoder.TryDecodeImage(_m4Luminance, _m4ImageSide, _m4ImageSide, _decodeBuffer, out _, out _);

    [Benchmark]
    public bool M1_UnderShadow_ImageDecode() => MicroQRCodeDecoder.TryDecodeImage(_m1ShadowLuminance, _m1ShadowSide, _m1ShadowSide, _decodeBuffer, out _, out _);

    [Benchmark]
    public bool M1_TurnedUnderShadow_ImageDecode_Fails() => MicroQRCodeDecoder.TryDecodeImage(_m1TurnedShadowLuminance, _m1TurnedShadowSide, _m1TurnedShadowSide, _decodeBuffer, out _, out _);

    [Benchmark]
    public bool StandardQRImage_ImageDecode_Fails() => MicroQRCodeDecoder.TryDecodeImage(_standardQRLuminance, _standardQRSide, _standardQRSide, _decodeBuffer, out _, out _);
}
