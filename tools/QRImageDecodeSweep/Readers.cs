using System.Security.Cryptography;
using FeatherQR;

namespace QRImageDecodeSweep;

/// <summary>The three readers, each given the same grayscale buffer and told the symbology.</summary>
internal static class Readers
{
    /// <summary>This library's image decoder: the text when it decodes, and the status either way.</summary>
    public static (string? Text, string Status) FeatherQr(string symbology, Rendered image)
    {
        bool success;
        string text;
        DecodeStatus status;
        switch (symbology)
        {
            case Symbologies.StandardQr:
                {
                    success = QRCodeDecoder.TryDecodeImage(image.Luminance, image.Width, image.Height, out text, out var info);
                    status = info.Status;
                    break;
                }
            case Symbologies.MicroQr:
                {
                    success = MicroQRCodeDecoder.TryDecodeImage(image.Luminance, image.Width, image.Height, out text, out var info);
                    status = info.Status;
                    break;
                }
            default:
                {
                    success = RmQRCodeDecoder.TryDecodeImage(image.Luminance, image.Width, image.Height, out text, out var info);
                    status = info.Status;
                    break;
                }
        }
        return success ? (text, "Success") : (null, status.ToString());
    }

    /// <summary>zxing-cpp with its defaults and <c>TryHarder</c>; every symbol it reports.</summary>
    public static string[] ZXingCpp(string symbology, Rendered image)
    {
        var format = symbology switch
        {
            Symbologies.StandardQr => global::ZXingCpp.BarcodeFormat.QRCode,
            Symbologies.MicroQr => global::ZXingCpp.BarcodeFormat.MicroQRCode,
            _ => global::ZXingCpp.BarcodeFormat.RMQRCode,
        };
        return ZXingCpp(format, image.Luminance, image.Width, image.Height, static r => r.Text);
    }

    /// <summary>
    /// zxing-cpp with <c>TryHarder</c> over a grayscale buffer, each symbol it reports through <paramref name="select"/>.
    /// Its wrapper keeps a raw pointer to the pixels and frees each object's native half in a finalizer, so the buffer is pinned and every object is held until the results are taken.
    /// Without that a collection during the call moves a small buffer or frees the options, and the reads vary from run to run.
    /// </summary>
    public static T[] ZXingCpp<T>(global::ZXingCpp.BarcodeFormat format, byte[] luminance, int width, int height, Func<global::ZXingCpp.Barcode, T> select)
    {
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(luminance, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var view = new global::ZXingCpp.ImageView(pin.AddrOfPinnedObject(), width, height, global::ZXingCpp.ImageFormat.Lum);
            var reader = new global::ZXingCpp.BarcodeReader { Formats = format, TryHarder = true };
            var results = reader.From(view);
            T[] selected = [.. results.Select(select)];
            GC.KeepAlive(results);
            GC.KeepAlive(reader);
            GC.KeepAlive(view);
            return selected;
        }
        catch
        {
            return [];
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>ZXing.Net with <c>TryHarder</c> and <c>AutoRotate</c>. It reads Standard QR only.</summary>
    public static string? ZXingNet(Rendered image)
    {
        try
        {
            var reader = new ZXing.BarcodeReaderGeneric { AutoRotate = true };
            reader.Options.TryHarder = true;
            reader.Options.PossibleFormats = [ZXing.BarcodeFormat.QR_CODE];
            var source = new ZXing.RGBLuminanceSource(image.Luminance, image.Width, image.Height, ZXing.RGBLuminanceSource.BitmapFormat.Gray8);
            return reader.Decode(source)?.Text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>All three on one image, scored against the text the image carries.</summary>
    public static ResultRow Read(string[] key, string symbology, Rendered image, string expected)
    {
        var (text, status) = FeatherQr(symbology, image);
        var misread = text is not null && text != expected;
        var cpp = Array.IndexOf(ZXingCpp(symbology, image), expected) >= 0;
        var net = symbology == Symbologies.StandardQr && ZXingNet(image) == expected;
        return new ResultRow(key, Digest(image), misread ? "Misread" : status, text == expected, misread, cpp, net, misread ? Difference(expected, text!) : null);
    }

    /// <summary>The pixels only; the size is in the key.</summary>
    private static string Digest(Rendered image) => Convert.ToHexString(SHA256.HashData(image.Luminance), 0, 8);

    private static string Difference(string expected, string actual)
    {
        var at = 0;
        while (at < expected.Length && at < actual.Length && expected[at] == actual[at])
            at++;
        static string Show(string s, int from) => string.Concat(s.Skip(Math.Max(0, from - 8)).Take(24).Select(static c => c < 0x20 || c == 0x7F ? $"<{(int)c:X2}>" : c.ToString()));
        return $"lengths {expected.Length} / {actual.Length}, first difference at {at}: expected \"{Show(expected, at)}\", got \"{Show(actual, at)}\"";
    }
}
