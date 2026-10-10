using System.Runtime.InteropServices;
using ZXingCpp;

namespace QRInteropFixtures;

/// <summary>
/// The calls into the pinned ZXingCpp package that do work in native code, each with what the package's wrapper leaves unheld held through the call.
/// </summary>
/// <remarks>
/// The wrapper gives the native reader a pointer to the caller's pixel array without pinning it, and frees its image view, its options and its barcodes in finalizers, with nothing keeping them through a call.
/// A collection during an unheld call can then move the pixels or free an object native code is using.
/// It showed as a reader's counts changing from run to run of one build, and as a creator dying in native code (qrcode-test-fixtures.md, Lessons learned).
/// </remarks>
internal static class ZXingCppCalls
{
    /// <summary>Every symbol <paramref name="reader"/> finds in <paramref name="pixels"/>.</summary>
    /// <param name="rowStride">Bytes from one row to the next where that is more than the row's pixels, or 0.</param>
    public static Barcode[] Read(BarcodeReader reader, byte[] pixels, int width, int height, ImageFormat format = ImageFormat.Lum, int rowStride = 0)
    {
        // The view built from a pointer checks no length, so the least any format needs is checked here
        var rowBytes = rowStride == 0 ? width : rowStride;
        if (width < 1 || height < 1 || rowBytes < width || pixels.Length < (long)rowBytes * (height - 1) + width)
            throw new ArgumentException($"{pixels.Length} bytes do not hold a {width} x {height} image.", nameof(pixels));

        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var view = new ImageView(pin.AddrOfPinnedObject(), width, height, format, rowStride);
            var results = reader.From(view);
            GC.KeepAlive(view);
            GC.KeepAlive(reader);
            return results;
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>The symbol <paramref name="creator"/> writes for <paramref name="text"/>.</summary>
    public static Barcode Create(BarcodeCreator creator, string text)
    {
        var barcode = creator.From(text);
        GC.KeepAlive(creator);
        return barcode;
    }

    /// <summary>The symbol <paramref name="creator"/> writes for <paramref name="bytes"/>.</summary>
    public static Barcode Create(BarcodeCreator creator, byte[] bytes)
    {
        var barcode = creator.From(bytes);
        GC.KeepAlive(creator);
        return barcode;
    }

    /// <summary><paramref name="barcode"/> drawn as <paramref name="options"/> say.</summary>
    public static Image ToImage(Barcode barcode, WriterOptions options)
    {
        var image = barcode.ToImage(options);
        GC.KeepAlive(options);
        GC.KeepAlive(barcode);
        return image;
    }
}
