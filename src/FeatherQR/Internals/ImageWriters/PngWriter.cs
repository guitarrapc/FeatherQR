using System.Buffers;

namespace FeatherQR.Internals.ImageWriters;

/// <summary>
/// PNG for a module matrix, one bit a pixel: greyscale for opaque black on opaque white, otherwise a two-colour palette with the dark colour,
/// painted over the light one, first. When the light colour has an alpha, the palette carries the transparency of each.
/// </summary>
/// <remarks>
/// <para>
/// The image data is one zlib stream of one deflate block with dynamic Huffman codes, written by a matcher made for this image.
/// Every module row repeats its first scanline once for each pixel of the module's height, so only the first scanline is searched,
/// for runs of one byte and for copies of the scanline above, and each repeat is a copy of the scanline above, written without comparing.
/// A copy that reaches the end of a module row runs on into the next row's first scanline while that matches.
/// A scanline past a copy's reach is searched for runs alone, and its symbols are written again for each repeat.
/// </para>
/// <para>
/// At 1, 2, 4 and 8 pixels a module, each literal byte stands for one or more whole modules, and runs and copies of the scanline above
/// are all the search looks for. At any other size a module takes more than one byte or shares a byte with its neighbours, so the search
/// also looks each position up by a hash of its next four to eight bytes among the first scanlines in reach. That finds the same few
/// modules again at the same bit phase, in an earlier row or further along this one.
/// </para>
/// <para>
/// The symbols are counted before any is written, so the codes come from the image's own counts and the length of the file is known
/// before a byte of it is written. The counting pass records the symbols and the writing pass replays them. An image past a copy's reach,
/// or with more symbols than the record holds, is matched a second time instead. The output is written straight into its destination,
/// the same bytes on every target framework. Scanlines are packed from the stored module bits, light 1 and dark 0, and padded with light.
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
            encoder.Write(in view, BufferWriterSpan.Get(writer, length));
            writer.Advance(length);
            return length;
        }
        finally
        {
            PngEncoder.Return(encoder);
        }
    }
}
