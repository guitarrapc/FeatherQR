using System.Buffers;

namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// PNG for a module matrix, one bit a pixel: greyscale for opaque black on opaque white, otherwise a two-colour palette with the dark colour
/// first and, when either colour has an alpha, the transparency of each.
/// </summary>
/// <remarks>
/// <para>
/// The image data is one zlib stream of one deflate block with dynamic Huffman codes, written by a matcher made for this image.
/// Every module row repeats its first scanline once for each pixel of the module's height, so only the first scanline is searched,
/// for runs of one byte and for copies of the scanline above, and each repeat is a copy of the scanline above, written without comparing.
/// A copy that reaches the end of a module row runs on into the next row's first scanline while that matches.
/// </para>
/// <para>
/// The symbols are counted before any is written, so the codes come from the image's own counts and the length of the file is known
/// before a byte of it is written. The counting pass records the symbols and the writing pass replays them. An image with more symbols
/// than the record holds is matched a second time instead. The output is written straight into its destination, the same bytes on
/// every target framework. Scanlines are packed from the stored module bits, light 1 and dark 0, and padded with light.
/// </para>
/// </remarks>
internal static class PngWriter
{
    public static byte[] GetBytes<TView>(in TView view, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        ImageSize.ThrowIfTooLarge(in view, in options);
        var encoder = PngEncoder.Take();
        try
        {
            var length = encoder.Measure(in view, in options);
            var png = new byte[length];
            encoder.Write(in view, png);
            return png;
        }
        finally
        {
            PngEncoder.Return(encoder);
        }
    }

    public static int Write<TView>(in TView view, IBufferWriter<byte> writer, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        ImageSize.ThrowIfTooLarge(in view, in options);
        var encoder = PngEncoder.Take();
        try
        {
            var length = encoder.Measure(in view, in options);
            encoder.Write(in view, writer.GetSpan(length).Slice(0, length));
            writer.Advance(length);
            return length;
        }
        finally
        {
            PngEncoder.Return(encoder);
        }
    }
}
