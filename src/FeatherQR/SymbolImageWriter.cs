using System.Buffers;
using FeatherQR.Internals;
using FeatherQR.Internals.ImageWriters;

namespace FeatherQR;

/// <summary>
/// Writes Standard QR, Micro QR and rMQR symbols as SVG and PNG images and as raw pixels, with no graphics library.
/// </summary>
/// <remarks>
/// <para>
/// Every module is a sharp square of <see cref="SymbolImageOptions.ModulePixelSize"/> pixels, and the quiet zone is the one the symbol was made with.
/// The same symbol and options give the same bytes on every target framework.
/// </para>
/// <para>
/// For module shapes, gradients, icons or an image of a set size in pixels, use the FeatherQR.SkiaSharp package.
/// </para>
/// </remarks>
public static class SymbolImageWriter
{
    /// <summary>
    /// Writes the QR code as an SVG document.
    /// </summary>
    /// <param name="data">The QR code.</param>
    /// <param name="options">The module size and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <returns>The document. It has no XML declaration, so it also goes into HTML as it is.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the image would be wider or taller than <see cref="int.MaxValue"/> pixels.</exception>
    public static string GetSvgString(QRCodeData data, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return SvgWriter.GetString(new StandardQrMatrixView(data), in options);
    }

    /// <inheritdoc cref="GetSvgString(QRCodeData, in SymbolImageOptions)"/>
    /// <param name="data">The Micro QR code.</param>
    /// <param name="options">The module size and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    public static string GetSvgString(MicroQRCodeData data, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return SvgWriter.GetString(new MicroQRMatrixView(data), in options);
    }

    /// <inheritdoc cref="GetSvgString(QRCodeData, in SymbolImageOptions)"/>
    /// <param name="data">The rMQR code.</param>
    /// <param name="options">The module size and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    public static string GetSvgString(RmQRCodeData data, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return SvgWriter.GetString(new RmQRMatrixView(data), in options);
    }

    /// <summary>
    /// Writes the QR code as an SVG document in UTF-8, after whatever the writer already holds.
    /// </summary>
    /// <param name="data">The QR code.</param>
    /// <param name="writer">Where to write the bytes.</param>
    /// <param name="options">The module size and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <returns>The number of bytes written.</returns>
    /// <remarks>
    /// The bytes are those of <see cref="GetSvgString(QRCodeData, in SymbolImageOptions)"/>, and nothing is allocated beyond what the writer hands out.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> or <paramref name="writer"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the image would be wider or taller than <see cref="int.MaxValue"/> pixels.</exception>
    public static int WriteSvg(QRCodeData data, IBufferWriter<byte> writer, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (writer is null)
            throw new ArgumentNullException(nameof(writer));
        return SvgWriter.Write(new StandardQrMatrixView(data), writer, in options);
    }

    /// <inheritdoc cref="WriteSvg(QRCodeData, IBufferWriter{byte}, in SymbolImageOptions)"/>
    /// <param name="data">The Micro QR code.</param>
    /// <param name="writer">Where to write the bytes.</param>
    /// <param name="options">The module size and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <remarks>
    /// The bytes are those of <see cref="GetSvgString(MicroQRCodeData, in SymbolImageOptions)"/>, and nothing is allocated beyond what the writer hands out.
    /// </remarks>
    public static int WriteSvg(MicroQRCodeData data, IBufferWriter<byte> writer, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (writer is null)
            throw new ArgumentNullException(nameof(writer));
        return SvgWriter.Write(new MicroQRMatrixView(data), writer, in options);
    }

    /// <inheritdoc cref="WriteSvg(QRCodeData, IBufferWriter{byte}, in SymbolImageOptions)"/>
    /// <param name="data">The rMQR code.</param>
    /// <param name="writer">Where to write the bytes.</param>
    /// <param name="options">The module size and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <remarks>
    /// The bytes are those of <see cref="GetSvgString(RmQRCodeData, in SymbolImageOptions)"/>, and nothing is allocated beyond what the writer hands out.
    /// </remarks>
    public static int WriteSvg(RmQRCodeData data, IBufferWriter<byte> writer, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (writer is null)
            throw new ArgumentNullException(nameof(writer));
        return SvgWriter.Write(new RmQRMatrixView(data), writer, in options);
    }

    /// <summary>
    /// Writes the QR code as a PNG file, one bit a pixel.
    /// </summary>
    /// <param name="data">The QR code.</param>
    /// <param name="options">The module size and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <returns>The file.</returns>
    /// <remarks>
    /// Black on white is greyscale. Any other pair is a two-colour palette, with the transparency of a colour whose alpha is below 255.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the image would be wider or taller than <see cref="int.MaxValue"/> pixels, or the file longer than the largest array.</exception>
    public static byte[] GetPngBytes(QRCodeData data, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return PngWriter.GetBytes(new StandardQrMatrixView(data), in options);
    }

    /// <inheritdoc cref="GetPngBytes(QRCodeData, in SymbolImageOptions)"/>
    /// <param name="data">The Micro QR code.</param>
    /// <param name="options">The module size and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    public static byte[] GetPngBytes(MicroQRCodeData data, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return PngWriter.GetBytes(new MicroQRMatrixView(data), in options);
    }

    /// <inheritdoc cref="GetPngBytes(QRCodeData, in SymbolImageOptions)"/>
    /// <param name="data">The rMQR code.</param>
    /// <param name="options">The module size and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    public static byte[] GetPngBytes(RmQRCodeData data, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return PngWriter.GetBytes(new RmQRMatrixView(data), in options);
    }

    /// <summary>
    /// Writes the QR code as a PNG file, after whatever the writer already holds.
    /// </summary>
    /// <param name="data">The QR code.</param>
    /// <param name="writer">Where to write the bytes.</param>
    /// <param name="options">The module size and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <returns>The number of bytes written.</returns>
    /// <remarks>
    /// The bytes are those of <see cref="GetPngBytes(QRCodeData, in SymbolImageOptions)"/>. The writer is asked for exactly their length, and nothing else is allocated.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> or <paramref name="writer"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the image would be wider or taller than <see cref="int.MaxValue"/> pixels, or the file longer than the largest array.</exception>
    public static int WritePng(QRCodeData data, IBufferWriter<byte> writer, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (writer is null)
            throw new ArgumentNullException(nameof(writer));
        return PngWriter.Write(new StandardQrMatrixView(data), writer, in options);
    }

    /// <inheritdoc cref="WritePng(QRCodeData, IBufferWriter{byte}, in SymbolImageOptions)"/>
    /// <param name="data">The Micro QR code.</param>
    /// <param name="writer">Where to write the bytes.</param>
    /// <param name="options">The module size and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <remarks>
    /// The bytes are those of <see cref="GetPngBytes(MicroQRCodeData, in SymbolImageOptions)"/>. The writer is asked for exactly their length, and nothing else is allocated.
    /// </remarks>
    public static int WritePng(MicroQRCodeData data, IBufferWriter<byte> writer, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (writer is null)
            throw new ArgumentNullException(nameof(writer));
        return PngWriter.Write(new MicroQRMatrixView(data), writer, in options);
    }

    /// <inheritdoc cref="WritePng(QRCodeData, IBufferWriter{byte}, in SymbolImageOptions)"/>
    /// <param name="data">The rMQR code.</param>
    /// <param name="writer">Where to write the bytes.</param>
    /// <param name="options">The module size and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <remarks>
    /// The bytes are those of <see cref="GetPngBytes(RmQRCodeData, in SymbolImageOptions)"/>. The writer is asked for exactly their length, and nothing else is allocated.
    /// </remarks>
    public static int WritePng(RmQRCodeData data, IBufferWriter<byte> writer, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (writer is null)
            throw new ArgumentNullException(nameof(writer));
        return PngWriter.Write(new RmQRMatrixView(data), writer, in options);
    }

    /// <summary>
    /// The length in bytes of the QR code's pixel buffer: its width times its height in pixels times the bytes of one pixel.
    /// </summary>
    /// <param name="data">The QR code.</param>
    /// <param name="layout">The channel order and depth.</param>
    /// <param name="options">The module size and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <returns>The length <see cref="TryWritePixels(QRCodeData, Span{byte}, PixelLayout, out int, out int, in SymbolImageOptions)"/> needs.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="layout"/> is not one of the four, or the buffer would be longer than the largest array.</exception>
    public static int GetPixelBufferSize(QRCodeData data, PixelLayout layout, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return PixelWriter.BufferSize(new StandardQrMatrixView(data), layout, in options);
    }

    /// <inheritdoc cref="GetPixelBufferSize(QRCodeData, PixelLayout, in SymbolImageOptions)"/>
    /// <param name="data">The Micro QR code.</param>
    /// <param name="layout">The channel order and depth.</param>
    /// <param name="options">The module size and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <returns>The length <see cref="TryWritePixels(MicroQRCodeData, Span{byte}, PixelLayout, out int, out int, in SymbolImageOptions)"/> needs.</returns>
    public static int GetPixelBufferSize(MicroQRCodeData data, PixelLayout layout, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return PixelWriter.BufferSize(new MicroQRMatrixView(data), layout, in options);
    }

    /// <inheritdoc cref="GetPixelBufferSize(QRCodeData, PixelLayout, in SymbolImageOptions)"/>
    /// <param name="data">The rMQR code.</param>
    /// <param name="layout">The channel order and depth.</param>
    /// <param name="options">The module size and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <returns>The length <see cref="TryWritePixels(RmQRCodeData, Span{byte}, PixelLayout, out int, out int, in SymbolImageOptions)"/> needs.</returns>
    public static int GetPixelBufferSize(RmQRCodeData data, PixelLayout layout, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return PixelWriter.BufferSize(new RmQRMatrixView(data), layout, in options);
    }

    /// <summary>
    /// Writes the QR code's pixels into the buffer you provide, rows packed with no padding between them.
    /// </summary>
    /// <param name="data">The QR code.</param>
    /// <param name="destination">The buffer. Size it with <see cref="GetPixelBufferSize(QRCodeData, PixelLayout, in SymbolImageOptions)"/>. Bytes past the image are left as they were.</param>
    /// <param name="layout">The channel order and depth.</param>
    /// <param name="width">The image's width in pixels, or 0 when the buffer is too short.</param>
    /// <param name="height">The image's height in pixels, or 0 when the buffer is too short.</param>
    /// <param name="options">The module size and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <returns><c>false</c> only when <paramref name="destination"/> is too short, in which case nothing is written.</returns>
    /// <remarks>
    /// Alpha is not premultiplied, and <see cref="PixelLayout.Rgb888x"/>'s fourth byte is 255.
    /// <see cref="PixelLayout.Gray8"/> holds each colour's luminance as the decoders compute it: an alpha below 255 composited over white,
    /// then BT.601 weights. A Gray8 buffer is the luminance input the decoders' <c>TryDecodeImage</c> calls take.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="layout"/> is not one of the four, or the buffer would be longer than the largest array.</exception>
    public static bool TryWritePixels(QRCodeData data, Span<byte> destination, PixelLayout layout, out int width, out int height, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return PixelWriter.TryWrite(new StandardQrMatrixView(data), destination, layout, out width, out height, in options);
    }

    /// <inheritdoc cref="TryWritePixels(QRCodeData, Span{byte}, PixelLayout, out int, out int, in SymbolImageOptions)"/>
    /// <param name="data">The Micro QR code.</param>
    /// <param name="destination">The buffer. Size it with <see cref="GetPixelBufferSize(MicroQRCodeData, PixelLayout, in SymbolImageOptions)"/>. Bytes past the image are left as they were.</param>
    /// <param name="layout">The channel order and depth.</param>
    /// <param name="width">The image's width in pixels, or 0 when the buffer is too short.</param>
    /// <param name="height">The image's height in pixels, or 0 when the buffer is too short.</param>
    /// <param name="options">The module size and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    public static bool TryWritePixels(MicroQRCodeData data, Span<byte> destination, PixelLayout layout, out int width, out int height, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return PixelWriter.TryWrite(new MicroQRMatrixView(data), destination, layout, out width, out height, in options);
    }

    /// <inheritdoc cref="TryWritePixels(QRCodeData, Span{byte}, PixelLayout, out int, out int, in SymbolImageOptions)"/>
    /// <param name="data">The rMQR code.</param>
    /// <param name="destination">The buffer. Size it with <see cref="GetPixelBufferSize(RmQRCodeData, PixelLayout, in SymbolImageOptions)"/>. Bytes past the image are left as they were.</param>
    /// <param name="layout">The channel order and depth.</param>
    /// <param name="width">The image's width in pixels, or 0 when the buffer is too short.</param>
    /// <param name="height">The image's height in pixels, or 0 when the buffer is too short.</param>
    /// <param name="options">The module size and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    public static bool TryWritePixels(RmQRCodeData data, Span<byte> destination, PixelLayout layout, out int width, out int height, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return PixelWriter.TryWrite(new RmQRMatrixView(data), destination, layout, out width, out height, in options);
    }
}
