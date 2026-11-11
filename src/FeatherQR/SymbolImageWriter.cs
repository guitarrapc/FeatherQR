using System.Buffers;
using FeatherQR.Internals;
using FeatherQR.Internals.ImageWriters;

namespace FeatherQR;

/// <summary>
/// Writes Standard QR, Micro QR and rMQR symbols as SVG and PNG images, as raw pixels and as text for a terminal, with no graphics library.
/// </summary>
/// <remarks>
/// <para>
/// Every module is a sharp square of <see cref="SymbolImageOptions.ModulePixelSize"/> pixels, or with <see cref="SymbolImageOptions.Width"/> or
/// <see cref="SymbolImageOptions.Height"/> set the largest whole number of pixels that canvas allows. <see cref="SymbolImageOptions.FillCanvas"/>
/// fills the canvas instead, each module still whole pixels. The quiet zone is the one the symbol was made with.
/// The same symbol and options give the same bytes on every target framework.
/// </para>
/// <para>
/// <see cref="SymbolImageOptions.ModuleStyle"/>, <see cref="SymbolImageOptions.ModuleScalePercent"/> and <see cref="SymbolImageOptions.FinderPatternStyle"/>
/// draw the modules and the finder patterns as circles or rounded squares, with soft edges. <see cref="SymbolImageOptions.IconSizeModules"/> leaves an icon area
/// at the centre of a Standard QR code, which <see cref="GetIconRectangle"/> locates and the SVG fills with <see cref="SymbolImageOptions.IconImage"/>.
/// For gradients, or an icon drawn into the PNG, use the FeatherQR.SkiaSharp package.
/// </para>
/// </remarks>
public static class SymbolImageWriter
{
    // SVG

    /// <summary>
    /// Writes the QR code as an SVG document.
    /// </summary>
    /// <param name="data">The QR code.</param>
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <returns>The document. It has no XML declaration, so it also goes into HTML as it is.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the image would be wider or taller than <see cref="int.MaxValue"/> pixels or the document longer than the largest array, a set size gives a module under one pixel, or an icon area does not fit the symbol or its symbology, or an icon image comes without one.</exception>
    public static string GetSvgString(QRCodeData data, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return SvgWriter.GetString(new StandardQrMatrixView(data), in options);
    }

    /// <inheritdoc cref="GetSvgString(QRCodeData, in SymbolImageOptions)"/>
    /// <param name="data">The Micro QR code.</param>
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    public static string GetSvgString(MicroQRCodeData data, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return SvgWriter.GetString(new MicroQRMatrixView(data), in options);
    }

    /// <inheritdoc cref="GetSvgString(QRCodeData, in SymbolImageOptions)"/>
    /// <param name="data">The rMQR code.</param>
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
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
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <returns>The number of bytes written.</returns>
    /// <remarks>
    /// The bytes are those of <see cref="GetSvgString(QRCodeData, in SymbolImageOptions)"/>, and once warm nothing is allocated beyond what the writer hands out.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> or <paramref name="writer"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the image would be wider or taller than <see cref="int.MaxValue"/> pixels or the document longer than the largest array, a set size gives a module under one pixel, or an icon area does not fit the symbol or its symbology, or an icon image comes without one.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="writer"/> hands out a span shorter than the document, against the <see cref="IBufferWriter{T}"/> contract.</exception>
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
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <remarks>
    /// The bytes are those of <see cref="GetSvgString(MicroQRCodeData, in SymbolImageOptions)"/>, and once warm nothing is allocated beyond what the writer hands out.
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
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <remarks>
    /// The bytes are those of <see cref="GetSvgString(RmQRCodeData, in SymbolImageOptions)"/>, and once warm nothing is allocated beyond what the writer hands out.
    /// </remarks>
    public static int WriteSvg(RmQRCodeData data, IBufferWriter<byte> writer, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (writer is null)
            throw new ArgumentNullException(nameof(writer));
        return SvgWriter.Write(new RmQRMatrixView(data), writer, in options);
    }

    // PNG

    /// <summary>
    /// Writes the QR code as a PNG file, one bit a pixel, or four with a module style, a module scale below 100 percent or a finder pattern style.
    /// </summary>
    /// <param name="data">The QR code.</param>
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <returns>The file.</returns>
    /// <remarks>
    /// Black on white is greyscale. Any other pair is a palette, the dark colour painted over the light one as in the SVG: two entries,
    /// or sixteen levels between the colours with a style, whose shapes have soft edges.
    /// The palette carries transparency when the light colour's alpha is below 255.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the image would be wider or taller than <see cref="int.MaxValue"/> pixels or the file longer than the largest array, a set size gives a module under one pixel, or an icon area does not fit the symbol or its symbology, or an icon image comes without one.</exception>
    public static byte[] GetPngBytes(QRCodeData data, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return PngWriter.GetBytes(new StandardQrMatrixView(data), in options);
    }

    /// <inheritdoc cref="GetPngBytes(QRCodeData, in SymbolImageOptions)"/>
    /// <param name="data">The Micro QR code.</param>
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    public static byte[] GetPngBytes(MicroQRCodeData data, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return PngWriter.GetBytes(new MicroQRMatrixView(data), in options);
    }

    /// <inheritdoc cref="GetPngBytes(QRCodeData, in SymbolImageOptions)"/>
    /// <param name="data">The rMQR code.</param>
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
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
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <returns>The number of bytes written.</returns>
    /// <remarks>
    /// The bytes are those of <see cref="GetPngBytes(QRCodeData, in SymbolImageOptions)"/>. The writer is asked for exactly their length, and once warm nothing else is allocated.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> or <paramref name="writer"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the image would be wider or taller than <see cref="int.MaxValue"/> pixels or the file longer than the largest array, a set size gives a module under one pixel, or an icon area does not fit the symbol or its symbology, or an icon image comes without one.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="writer"/> hands out a span shorter than the file, against the <see cref="IBufferWriter{T}"/> contract.</exception>
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
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <remarks>
    /// The bytes are those of <see cref="GetPngBytes(MicroQRCodeData, in SymbolImageOptions)"/>. The writer is asked for exactly their length, and once warm nothing else is allocated.
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
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <remarks>
    /// The bytes are those of <see cref="GetPngBytes(RmQRCodeData, in SymbolImageOptions)"/>. The writer is asked for exactly their length, and once warm nothing else is allocated.
    /// </remarks>
    public static int WritePng(RmQRCodeData data, IBufferWriter<byte> writer, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (writer is null)
            throw new ArgumentNullException(nameof(writer));
        return PngWriter.Write(new RmQRMatrixView(data), writer, in options);
    }

    // Image size

    /// <summary>
    /// The size in pixels of the QR code's image: the size the SVG names, the PNG holds and the pixel buffer is written at.
    /// </summary>
    /// <param name="data">The QR code.</param>
    /// <param name="options">The module size or the image's size. <c>default</c> is 8 pixels a module.</param>
    /// <returns>The image's width and height in pixels.</returns>
    /// <remarks>
    /// Ask it before writing, to make the image the pixels go into. The size is the same for every format and every <see cref="PixelLayout"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the image would be wider or taller than <see cref="int.MaxValue"/> pixels, a set size gives a module under one pixel, or an icon area does not fit the symbol or its symbology, or an icon image comes without one.</exception>
    public static ImageSize GetImageSize(QRCodeData data, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return ImageSizeOf(new StandardQrMatrixView(data), in options);
    }

    /// <inheritdoc cref="GetImageSize(QRCodeData, in SymbolImageOptions)"/>
    /// <param name="data">The Micro QR code.</param>
    /// <param name="options">The module size or the image's size. <c>default</c> is 8 pixels a module.</param>
    public static ImageSize GetImageSize(MicroQRCodeData data, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return ImageSizeOf(new MicroQRMatrixView(data), in options);
    }

    /// <inheritdoc cref="GetImageSize(QRCodeData, in SymbolImageOptions)"/>
    /// <param name="data">The rMQR code.</param>
    /// <param name="options">The module size or the image's size. <c>default</c> is 8 pixels a module.</param>
    public static ImageSize GetImageSize(RmQRCodeData data, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return ImageSizeOf(new RmQRMatrixView(data), in options);
    }

    private static ImageSize ImageSizeOf<TView>(in TView view, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        var layout = ImageLayout.Create(in view, in options);
        // Refused here too, so a caller making an image of this size learns of an icon that does not fit before writing.
        IconArea.Get(in view, in options);
        return new ImageSize(layout.Width, layout.Height);
    }

    // Pixel buffer

    /// <summary>
    /// The length in bytes of the QR code's pixel buffer: its width times its height in pixels times the bytes of one pixel.
    /// </summary>
    /// <param name="data">The QR code.</param>
    /// <param name="layout">The channel order and depth.</param>
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <returns>The length <see cref="TryWritePixels(QRCodeData, Span{byte}, PixelLayout, out int, out int, in SymbolImageOptions)"/> needs.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="layout"/> is not one of the seven, the buffer would be longer than the largest array, a set size gives a module under one pixel, or an icon area does not fit the symbol or its symbology, or an icon image comes without one.</exception>
    public static int GetPixelBufferSize(QRCodeData data, PixelLayout layout, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return PixelWriter.BufferSize(new StandardQrMatrixView(data), layout, in options);
    }

    /// <inheritdoc cref="GetPixelBufferSize(QRCodeData, PixelLayout, in SymbolImageOptions)"/>
    /// <param name="data">The Micro QR code.</param>
    /// <param name="layout">The channel order and depth.</param>
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
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
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
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
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <returns><c>false</c> only when <paramref name="destination"/> is too short, in which case nothing is written.</returns>
    /// <remarks>
    /// Alpha is not premultiplied. <see cref="PixelLayout.Gray8"/>, <see cref="PixelLayout.Rgb888x"/>, <see cref="PixelLayout.Bgr888x"/>, <see cref="PixelLayout.Rgb24"/>
    /// and <see cref="PixelLayout.Bgr24"/> have no alpha, so they hold each colour composited over white, and the fourth byte of Rgb888x and Bgr888x is 255. Gray8 then holds its luminance with BT.601 weights,
    /// as the decoders compute it, so a Gray8 buffer is the luminance input the decoders' <c>TryDecodeImage</c> calls take.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="layout"/> is not one of the seven, the buffer would be longer than the largest array, a set size gives a module under one pixel, or an icon area does not fit the symbol or its symbology, or an icon image comes without one.</exception>
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
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
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
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    public static bool TryWritePixels(RmQRCodeData data, Span<byte> destination, PixelLayout layout, out int width, out int height, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return PixelWriter.TryWrite(new RmQRMatrixView(data), destination, layout, out width, out height, in options);
    }

    /// <summary>
    /// Writes the QR code's pixels into the buffer you provide, rows <paramref name="rowBytes"/> bytes apart: into an image another library holds, or into a region of a larger buffer.
    /// </summary>
    /// <param name="data">The QR code.</param>
    /// <param name="destination">The buffer, from the image's first pixel. It needs <paramref name="rowBytes"/> bytes for every row but the last, and the last row's pixels. The bytes between a row's last pixel and the next row, and those past the last row, are left as they were.</param>
    /// <param name="rowBytes">The stride: the bytes from the start of one row to the start of the next. At least the image's width × the bytes of one pixel. <see cref="GetImageSize(QRCodeData, in SymbolImageOptions)"/> gives the width before writing.</param>
    /// <param name="layout">The channel order and depth.</param>
    /// <param name="width">The image's width in pixels, or 0 when the buffer is too short.</param>
    /// <param name="height">The image's height in pixels, or 0 when the buffer is too short.</param>
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <returns><c>false</c> only when <paramref name="destination"/> is too short, in which case nothing is written.</returns>
    /// <remarks>
    /// The pixels are those of <see cref="TryWritePixels(QRCodeData, Span{byte}, PixelLayout, out int, out int, in SymbolImageOptions)"/>, which is this call at a stride of one row.
    /// For a region of a larger image, slice <paramref name="destination"/> at the region's first pixel and pass that image's stride.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="layout"/> is not one of the seven, <paramref name="rowBytes"/> is shorter than one row, the image's pixels would be longer than the largest array, a set size gives a module under one pixel, or an icon area does not fit the symbol or its symbology, or an icon image comes without one.</exception>
    public static bool TryWritePixels(QRCodeData data, Span<byte> destination, int rowBytes, PixelLayout layout, out int width, out int height, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return PixelWriter.TryWrite(new StandardQrMatrixView(data), destination, rowBytes, layout, out width, out height, in options);
    }

    /// <inheritdoc cref="TryWritePixels(QRCodeData, Span{byte}, int, PixelLayout, out int, out int, in SymbolImageOptions)"/>
    /// <param name="data">The Micro QR code.</param>
    /// <param name="destination">The buffer, from the image's first pixel. It needs <paramref name="rowBytes"/> bytes for every row but the last, and the last row's pixels. The bytes between a row's last pixel and the next row, and those past the last row, are left as they were.</param>
    /// <param name="rowBytes">The stride: the bytes from the start of one row to the start of the next. At least the image's width × the bytes of one pixel. <see cref="GetImageSize(MicroQRCodeData, in SymbolImageOptions)"/> gives the width before writing.</param>
    /// <param name="layout">The channel order and depth.</param>
    /// <param name="width">The image's width in pixels, or 0 when the buffer is too short.</param>
    /// <param name="height">The image's height in pixels, or 0 when the buffer is too short.</param>
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <remarks>
    /// The pixels are those of <see cref="TryWritePixels(MicroQRCodeData, Span{byte}, PixelLayout, out int, out int, in SymbolImageOptions)"/>, which is this call at a stride of one row.
    /// For a region of a larger image, slice <paramref name="destination"/> at the region's first pixel and pass that image's stride.
    /// </remarks>
    public static bool TryWritePixels(MicroQRCodeData data, Span<byte> destination, int rowBytes, PixelLayout layout, out int width, out int height, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return PixelWriter.TryWrite(new MicroQRMatrixView(data), destination, rowBytes, layout, out width, out height, in options);
    }

    /// <inheritdoc cref="TryWritePixels(QRCodeData, Span{byte}, int, PixelLayout, out int, out int, in SymbolImageOptions)"/>
    /// <param name="data">The rMQR code.</param>
    /// <param name="destination">The buffer, from the image's first pixel. It needs <paramref name="rowBytes"/> bytes for every row but the last, and the last row's pixels. The bytes between a row's last pixel and the next row, and those past the last row, are left as they were.</param>
    /// <param name="rowBytes">The stride: the bytes from the start of one row to the start of the next. At least the image's width × the bytes of one pixel. <see cref="GetImageSize(RmQRCodeData, in SymbolImageOptions)"/> gives the width before writing.</param>
    /// <param name="layout">The channel order and depth.</param>
    /// <param name="width">The image's width in pixels, or 0 when the buffer is too short.</param>
    /// <param name="height">The image's height in pixels, or 0 when the buffer is too short.</param>
    /// <param name="options">The module size or the image's size, and the colours. <c>default</c> is 8 pixels a module, black on white.</param>
    /// <remarks>
    /// The pixels are those of <see cref="TryWritePixels(RmQRCodeData, Span{byte}, PixelLayout, out int, out int, in SymbolImageOptions)"/>, which is this call at a stride of one row.
    /// For a region of a larger image, slice <paramref name="destination"/> at the region's first pixel and pass that image's stride.
    /// </remarks>
    public static bool TryWritePixels(RmQRCodeData data, Span<byte> destination, int rowBytes, PixelLayout layout, out int width, out int height, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return PixelWriter.TryWrite(new RmQRMatrixView(data), destination, rowBytes, layout, out width, out height, in options);
    }

    // Symbol area

    /// <summary>
    /// Where the QR code lands in the image the options describe, in pixels: its modules, quiet zone excluded.
    /// </summary>
    /// <param name="data">The QR code.</param>
    /// <param name="options">The options the image is written with. <c>default</c> is 8 pixels a module.</param>
    /// <returns>The symbol's left, top, width and height in pixels.</returns>
    /// <remarks>
    /// <para>
    /// Use it to draw a frame around the symbol or to check how large its modules came out. With <see cref="SymbolImageOptions.Width"/> or
    /// <see cref="SymbolImageOptions.Height"/> set, the symbol is centred and its edges fall where the writers round them, so read them here
    /// rather than scaling the canvas yourself. The quiet zone and any canvas left around it are the light colour. An image decoder reading the
    /// image back reports its estimate of these corners in <see cref="SymbolCorners"/>.
    /// </para>
    /// <para>
    /// A module's size is the image's width over the symbol's modules across, quiet zone included, or its height over the modules down where that is
    /// smaller: <c>Size</c> both ways on Standard QR and Micro QR, <c>Width</c> and <c>Height</c> on rMQR, with the image's size from
    /// <see cref="GetImageSize(QRCodeData, in SymbolImageOptions)"/>. Every module is that size rounded down, or with <see cref="SymbolImageOptions.FillCanvas"/>
    /// rounded down or up. Without a set size it is <see cref="SymbolImageOptions.ModulePixelSize"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the image would be wider or taller than <see cref="int.MaxValue"/> pixels, a set size gives a module under one pixel, or an icon area does not fit the symbol or its symbology, or an icon image comes without one.</exception>
    public static ImageRect GetSymbolRectangle(QRCodeData data, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return SymbolRectangleOf(new StandardQrMatrixView(data), in options);
    }

    /// <inheritdoc cref="GetSymbolRectangle(QRCodeData, in SymbolImageOptions)"/>
    /// <param name="data">The Micro QR code.</param>
    /// <param name="options">The options the image is written with. <c>default</c> is 8 pixels a module.</param>
    public static ImageRect GetSymbolRectangle(MicroQRCodeData data, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return SymbolRectangleOf(new MicroQRMatrixView(data), in options);
    }

    /// <inheritdoc cref="GetSymbolRectangle(QRCodeData, in SymbolImageOptions)"/>
    /// <param name="data">The rMQR code.</param>
    /// <param name="options">The options the image is written with. <c>default</c> is 8 pixels a module.</param>
    public static ImageRect GetSymbolRectangle(RmQRCodeData data, in SymbolImageOptions options = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return SymbolRectangleOf(new RmQRMatrixView(data), in options);
    }

    private static ImageRect SymbolRectangleOf<TView>(in TView view, in SymbolImageOptions options)
        where TView : struct, IModuleMatrixView
    {
        var layout = ImageLayout.Create(in view, in options);
        // Options the writers refuse are refused here too, as the image's size refuses them.
        IconArea.Get(in view, in options);
        var quietZone = (view.Width - view.CoreWidth) / 2;
        var x = layout.ColumnStart(quietZone);
        var y = layout.RowStart(quietZone);
        return new ImageRect(x, y, layout.ColumnStart(quietZone + view.CoreWidth) - x, layout.RowStart(quietZone + view.CoreHeight) - y);
    }

    // Icon area

    /// <summary>
    /// Where the icon lands in the image the options describe, in pixels, for you to draw it there: the square <see cref="SymbolImageOptions.IconSizeModules"/>
    /// names, inside the light area the writers leave for it.
    /// </summary>
    /// <param name="data">The QR code.</param>
    /// <param name="options">The options the image is written with, with an icon area.</param>
    /// <returns>The icon's left, top, width and height in pixels, on the module grid.</returns>
    /// <remarks>
    /// Draw into the pixel buffer at it, or over the PNG in whatever decodes your icon.
    /// The SVG draws <see cref="SymbolImageOptions.IconImage"/> over the same modules itself.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the options have no icon area, or one that does not fit the symbol, or the image's size is out of range.</exception>
    public static ImageRect GetIconRectangle(QRCodeData data, in SymbolImageOptions options)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        var view = new StandardQrMatrixView(data);
        var layout = ImageLayout.Create(in view, in options);
        var icon = IconArea.Get(in view, in options);
        if (!icon.Present)
            throw new ArgumentOutOfRangeException(nameof(options), "No icon area is set: set IconSizeModules.");
        var quietZone = (view.Width - view.CoreWidth) / 2;
        var x = layout.ColumnStart(quietZone + icon.IconColumn);
        var y = layout.RowStart(quietZone + icon.IconRow);
        return new ImageRect(x, y, layout.ColumnStart(quietZone + icon.IconColumn + icon.IconSize) - x, layout.RowStart(quietZone + icon.IconRow + icon.IconSize) - y);
    }

    // Text output for terminal

    /// <summary>
    /// Writes the QR code as text for a terminal, two module rows a line in half blocks.
    /// </summary>
    /// <param name="data">The QR code.</param>
    /// <param name="invert"><c>false</c> draws the dark modules, for a terminal with a light background. <c>true</c> draws the light modules, for a dark background.</param>
    /// <returns>The text. Every line ends with <c>\n</c>.</returns>
    /// <remarks>
    /// Each character shows two modules, one above the other: a space, U+2580 (upper half), U+2584 (lower half) or U+2588 (both).
    /// A monospace font's cell is about twice as tall as it is wide, so the halves are close to square. The text carries no colour
    /// escapes, and the quiet zone is the one the symbol was made with.
    /// The console has to show those three characters. A Windows console on a Chinese, Japanese or Korean code page prints some of them
    /// as <c>?</c>, so set <c>Console.OutputEncoding</c> to UTF-8 first.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
    public static string GetTerminalText(QRCodeData data, bool invert = false)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return HalfBlockText.Get(new StandardQrMatrixView(data), invert);
    }

    /// <inheritdoc cref="GetTerminalText(QRCodeData, bool)"/>
    /// <param name="data">The Micro QR code.</param>
    /// <param name="invert"><c>false</c> draws the dark modules, for a terminal with a light background. <c>true</c> draws the light modules, for a dark background.</param>
    public static string GetTerminalText(MicroQRCodeData data, bool invert = false)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return HalfBlockText.Get(new MicroQRMatrixView(data), invert);
    }

    /// <inheritdoc cref="GetTerminalText(QRCodeData, bool)"/>
    /// <param name="data">The rMQR code.</param>
    /// <param name="invert"><c>false</c> draws the dark modules, for a terminal with a light background. <c>true</c> draws the light modules, for a dark background.</param>
    public static string GetTerminalText(RmQRCodeData data, bool invert = false)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        return HalfBlockText.Get(new RmQRMatrixView(data), invert);
    }
}
