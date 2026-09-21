using FeatherQR;
using FeatherQR.SkiaSharp;
using SkiaSharp;

namespace QRImageDecodeSweep;

internal static class NativeRenderers
{
    private static Rendered FromBitmap(SKBitmap bitmap, float ppm)
    {
        var (lum, w, h) = Pixels.ToLuminance(bitmap);
        return new Rendered(lum, w, h, ppm);
    }

    // FeatherQR's builder at a fixed pixel size: 1.5 to 4 px/module, modules snapped to whole pixels
    public static Rendered? FeatherQrStandard(CaseDefinition d, Symbol _, Random random)
    {
        var qr = QRCodeGenerator.Create(d.Text, Enum.Parse<QREccLevel>(d.Ecc));
        var ppm = 1.5f + (float)random.NextDouble() * 2.5f;
        var px = (int)MathF.Round(qr.Size * ppm);
        using var bitmap = new QRCodeImageBuilder(qr).WithSize(px, px).ToBitmap();
        return FromBitmap(bitmap, (float)px / qr.Size);
    }

    public static Rendered? FeatherQrMicro(CaseDefinition d, Symbol _, Random random)
    {
        var qr = MicroQRCodeGenerator.Create(d.Text, Enum.Parse<MicroQREccLevel>(d.Ecc), new MicroQRCodeGeneratorOptions { Version = (MicroQRVersion)d.Version });
        var ppm = 1.5f + (float)random.NextDouble() * 2.5f;
        var px = (int)MathF.Round(qr.Size * ppm);
        using var bitmap = new MicroQRCodeImageBuilder(qr).WithSize(px, px).ToBitmap();
        return FromBitmap(bitmap, (float)px / qr.Size);
    }

    public static Rendered? FeatherQrRmqr(CaseDefinition d, Symbol _, Random random)
    {
        var qr = RmQRCodeGenerator.Create(d.Text, Enum.Parse<RmQREccLevel>(d.Ecc), new RmQRCodeGeneratorOptions { Version = (RmQRVersion)d.Version });
        var ppm = 1.5f + (float)random.NextDouble() * 2.5f;
        var widthPx = (int)MathF.Round(qr.Width * ppm);
        var heightPx = (int)MathF.Round(qr.Height * ppm);
        using var bitmap = new RmQRCodeImageBuilder(qr).WithSize(widthPx, heightPx).ToBitmap();
        return FromBitmap(bitmap, (float)widthPx / qr.Width);
    }

    // ZXing.Net's renderer: a requested pixel size, filled at the largest whole multiple and padded
    public static Rendered? ZXingNet(CaseDefinition d, Symbol s, Random random)
    {
        var level = d.Ecc switch
        {
            "L" => ZXing.QrCode.Internal.ErrorCorrectionLevel.L,
            "M" => ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
            "Q" => ZXing.QrCode.Internal.ErrorCorrectionLevel.Q,
            _ => ZXing.QrCode.Internal.ErrorCorrectionLevel.H,
        };
        var ppm = 1f + (float)random.NextDouble() * 5f;
        var px = (int)MathF.Round((s.Width + 8) * ppm);
        var writer = new ZXing.SkiaSharp.BarcodeWriter
        {
            Format = ZXing.BarcodeFormat.QR_CODE,
            Options = new ZXing.QrCode.QrCodeEncodingOptions { Width = px, Height = px, Margin = 4, ErrorCorrection = level },
        };
        using var bitmap = writer.Write(d.Text);
        return FromBitmap(bitmap, MathF.Floor(ppm));
    }

    public static Rendered? QrCoder(CaseDefinition d, Symbol _, Random random)
    {
        using var data = Encoders.QrCoderData(d);
        var ppm = 1 + random.Next(6);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(ppm);
        using var bitmap = SKBitmap.Decode(png);
        return FromBitmap(bitmap, ppm);
    }

    public static Rendered? QrtoolPng(CaseDefinition d, Symbol _, Random random)
    {
        var ppm = 1 + random.Next(6);
        var pngFile = Path.Combine(Path.GetTempPath(), "qr-sweep-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            Qrtool.RunWithPayload(d.Text, payload => $"encode {Qrtool.Arguments(d)} --size {ppm} --type png --output \"{pngFile}\" --read-from \"{payload}\"");
            using var bitmap = SKBitmap.Decode(pngFile);
            return FromBitmap(bitmap, ppm);
        }
        finally
        {
            try { File.Delete(pngFile); } catch (IOException) { }
        }
    }
}
