using FeatherQR;

namespace QRImageDecodeSweep;

internal static class Encoders
{
    public static Encoder[] For(string symbology)
    {
        var list = new List<Encoder>();
        switch (symbology)
        {
            case Symbologies.StandardQr:
                list.Add(new Encoder("FeatherQR", FeatherQrStandard, NativeRenderers.FeatherQrStandard));
                list.Add(new Encoder("ZXing.Net", ZXingNet, NativeRenderers.ZXingNet));
                list.Add(new Encoder("QRCoder", QrCoder, NativeRenderers.QrCoder));
                list.Add(new Encoder("QrCodeGenerator", Nayuki, null));
                list.Add(new Encoder("CodeGlyphX", CodeGlyph, null));
                list.Add(new Encoder(Libzint.Name, Libzint.Encode, Libzint.NativeRender));
                if (Qrtool.Available)
                    list.Add(new Encoder("qrtool", Qrtool.Encode, NativeRenderers.QrtoolPng));
                break;
            case Symbologies.MicroQr:
                list.Add(new Encoder("FeatherQR", FeatherQrMicro, NativeRenderers.FeatherQrMicro));
                list.Add(new Encoder(Libzint.Name, Libzint.Encode, Libzint.NativeRender));
                if (Qrtool.Available)
                    list.Add(new Encoder("qrtool", Qrtool.Encode, NativeRenderers.QrtoolPng));
                break;
            default:
                list.Add(new Encoder("FeatherQR", FeatherQrRmqr, NativeRenderers.FeatherQrRmqr));
                list.Add(new Encoder(Libzint.Name, Libzint.Encode, Libzint.NativeRender));
                if (Qrtool.Available)
                    list.Add(new Encoder("qrtool", Qrtool.Encode, NativeRenderers.QrtoolPng));
                break;
        }
        return [.. list];
    }

    private static Symbol FeatherQrStandard(CaseDefinition d)
    {
        var qr = QRCodeGenerator.Create(d.Text, Enum.Parse<QREccLevel>(d.Ecc), new QRCodeGeneratorOptions { QuietZoneSize = 0 });
        var size = qr.Size;
        var modules = new bool[size * size];
        for (var r = 0; r < size; r++)
            for (var c = 0; c < size; c++)
                modules[r * size + c] = qr[r, c];
        return new Symbol(modules, size, size, "v" + (size - 17) / 4);
    }

    private static Symbol FeatherQrMicro(CaseDefinition d)
    {
        var qr = MicroQRCodeGenerator.Create(d.Text, Enum.Parse<MicroQREccLevel>(d.Ecc), new MicroQRCodeGeneratorOptions { QuietZoneSize = 0, Version = (MicroQRVersion)d.Version });
        var size = qr.Size;
        var modules = new bool[size * size];
        for (var r = 0; r < size; r++)
            for (var c = 0; c < size; c++)
                modules[r * size + c] = qr[r, c];
        return new Symbol(modules, size, size, "M" + (size - 9) / 2);
    }

    private static Symbol FeatherQrRmqr(CaseDefinition d)
    {
        var qr = RmQRCodeGenerator.Create(d.Text, Enum.Parse<RmQREccLevel>(d.Ecc), new RmQRCodeGeneratorOptions { QuietZoneSize = 0, Version = (RmQRVersion)d.Version });
        var modules = new bool[qr.Width * qr.Height];
        for (var r = 0; r < qr.Height; r++)
            for (var c = 0; c < qr.Width; c++)
                modules[r * qr.Width + c] = qr[r, c];
        return new Symbol(modules, qr.Width, qr.Height, $"R{qr.Height}x{qr.Width}");
    }

    private static Symbol ZXingNet(CaseDefinition d)
    {
        var level = d.Ecc switch
        {
            "L" => ZXing.QrCode.Internal.ErrorCorrectionLevel.L,
            "M" => ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
            "Q" => ZXing.QrCode.Internal.ErrorCorrectionLevel.Q,
            _ => ZXing.QrCode.Internal.ErrorCorrectionLevel.H,
        };
        var qr = ZXing.QrCode.Internal.Encoder.encode(d.Text, level);
        var matrix = qr.Matrix;
        var size = matrix.Width;
        var modules = new bool[size * size];
        var array = matrix.Array;
        for (var r = 0; r < size; r++)
            for (var c = 0; c < size; c++)
                modules[r * size + c] = array[r][c] != 0;
        return new Symbol(modules, size, size, "v" + (size - 17) / 4);
    }

    private static QRCoder.QRCodeGenerator.ECCLevel QrCoderLevel(string ecc) => ecc switch
    {
        "L" => QRCoder.QRCodeGenerator.ECCLevel.L,
        "M" => QRCoder.QRCodeGenerator.ECCLevel.M,
        "Q" => QRCoder.QRCodeGenerator.ECCLevel.Q,
        _ => QRCoder.QRCodeGenerator.ECCLevel.H,
    };

    public static QRCoder.QRCodeData QrCoderData(CaseDefinition d) => QRCoder.QRCodeGenerator.GenerateQrCode(d.Text, QrCoderLevel(d.Ecc));

    private static Symbol QrCoder(CaseDefinition d)
    {
        using var data = QrCoderData(d);
        var matrix = data.ModuleMatrix; // includes a 4-module quiet zone
        var size = matrix.Count - 8;
        var modules = new bool[size * size];
        for (var r = 0; r < size; r++)
            for (var c = 0; c < size; c++)
                modules[r * size + c] = matrix[r + 4][c + 4];
        return new Symbol(modules, size, size, "v" + (size - 17) / 4);
    }

    private static Symbol Nayuki(CaseDefinition d)
    {
        var level = d.Ecc switch
        {
            "L" => Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Low,
            "M" => Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Medium,
            "Q" => Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Quartile,
            _ => Net.Codecrete.QrCodeGenerator.QrCode.Ecc.High,
        };
        var qr = Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(d.Text, level);
        var size = qr.Size;
        var modules = new bool[size * size];
        for (var r = 0; r < size; r++)
            for (var c = 0; c < size; c++)
                modules[r * size + c] = qr.GetModule(c, r);
        return new Symbol(modules, size, size, "v" + (size - 17) / 4);
    }

    private static Symbol CodeGlyph(CaseDefinition d)
    {
        var level = d.Ecc switch
        {
            "L" => CodeGlyphX.QrErrorCorrectionLevel.L,
            "M" => CodeGlyphX.QrErrorCorrectionLevel.M,
            "Q" => CodeGlyphX.QrErrorCorrectionLevel.Q,
            _ => CodeGlyphX.QrErrorCorrectionLevel.H,
        };
        var qr = CodeGlyphX.QrCodeEncoder.EncodeText(d.Text, level);
        var size = qr.Size;
        var modules = new bool[size * size];
        for (var r = 0; r < size; r++)
            for (var c = 0; c < size; c++)
                modules[r * size + c] = qr.Modules[c, r];
        return new Symbol(modules, size, size, "v" + (size - 17) / 4);
    }
}
