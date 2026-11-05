using System.Buffers;
using FeatherQR.Internals;
using FeatherQR.Internals.ImageWriters;

namespace FeatherQR;

/// <summary>
/// Writes Standard QR, Micro QR and rMQR symbols as images, with no graphics library.
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
}
