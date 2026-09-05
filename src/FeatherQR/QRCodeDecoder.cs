using System.Buffers;
using FeatherQR.Internals.StandardQr;

namespace FeatherQR;

/// <summary>
/// QR code decoder based on ISO/IEC 18004 standard.
/// Decodes QR module matrices back into text, including Reed-Solomon error correction.
/// </summary>
/// <remarks>
/// <para>
/// Supported content: Numeric, Alphanumeric and Byte mode segments (ISO-8859-1 and
/// UTF-8, with or without ECI headers), the full version range 1-40 and all ECC levels.
/// Kanji mode is decoded as JIS X 0208; the generator never emits it, so Kanji is a
/// read-only mode here. A Kanji cell outside the JIS X 0208 repertoire (most visibly
/// the NEC row 13 characters CP932 adds, such as circled digits) fails the WHOLE
/// symbol with <see cref="QRCodeDecodeStatus.UnmappedCharacter"/> rather than
/// substituting a replacement character; that status is distinct from
/// <see cref="QRCodeDecodeStatus.UnsupportedContent"/> so a caller can tell "a CP932
/// reader would read this" from "this uses a feature we do not implement". FNC1 and
/// Structured Append are the latter.
/// </para>
/// <para>
/// Byte segments without an ECI header have no declared charset. The decoder uses
/// UTF-8 when the payload is valid UTF-8 (or carries a BOM) and ISO-8859-1 otherwise.
/// </para>
/// </remarks>
public static class QRCodeDecoder
{
    /// <summary>
    /// Decodes the text content from QR code data.
    /// </summary>
    /// <param name="data">The QR code data to decode.</param>
    /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
    /// <returns>True when decoding succeeded.</returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static bool TryDecode(QRCodeData data, out string text)
        => TryDecode(data, out text, out _);

    /// <summary>
    /// Decodes the text content from QR code data, with diagnostic information.
    /// </summary>
    /// <param name="data">The QR code data to decode.</param>
    /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
    /// <param name="info">Diagnostic information (status, version, ECC level, mask pattern, corrected errors).</param>
    /// <returns>True when decoding succeeded.</returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static bool TryDecode(QRCodeData data, out string text, out QRCodeDecodeInfo info)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));

        var size = data.GetCoreSize();
        var rented = ArrayPool<byte>.Shared.Rent(size * size);
        try
        {
            var modules = rented.AsSpan(0, size * size);
            data.GetCoreData(modules);
            return TryDecodeCore(modules, size, out text, out info);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary>
    /// Decodes the text content from a module matrix.
    /// </summary>
    /// <param name="modules">
    /// Module matrix, one byte per module (0 = light, non-zero = dark), flat row-major
    /// order, the format produced by <see cref="QRCodeGenerator.CreateQrCode(ReadOnlySpan{char}, ECCLevel, Span{byte}, in QRCodeGeneratorOptions)"/>.
    /// A light quiet zone border is detected and skipped automatically.
    /// </param>
    /// <param name="size">Matrix size in modules per side (including quiet zone if present).</param>
    /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
    /// <param name="info">Diagnostic information (status, version, ECC level, mask pattern, corrected errors).</param>
    /// <returns>True when decoding succeeded.</returns>
    /// <exception cref="ArgumentException"></exception>
    public static bool TryDecode(ReadOnlySpan<byte> modules, int size, out string text, out QRCodeDecodeInfo info)
    {
        // long arithmetic: size is caller-controlled and size² overflows int at 46341
        if (size < 1 || modules.Length < (long)size * size)
            throw new ArgumentException($"Module buffer too small: required {(long)size * size}, got {modules.Length}", nameof(modules));

        if (!TryLocateCore(modules, size, out var top, out var left, out var coreSize))
        {
            text = string.Empty;
            info = new QRCodeDecodeInfo(QRCodeDecodeStatus.InvalidMatrix, 0, default, -1, 0);
            return false;
        }

        if (top == 0 && left == 0 && coreSize == size)
            return TryDecodeCore(modules.Slice(0, size * size), coreSize, out text, out info);

        var rented = ArrayPool<byte>.Shared.Rent(coreSize * coreSize);
        try
        {
            var core = rented.AsSpan(0, coreSize * coreSize);
            CopyCoreWindow(modules, size, top, left, coreSize, core);
            return TryDecodeCore(core, coreSize, out text, out info);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary>
    /// Decodes the text content from a module matrix into a caller-provided buffer
    /// without per-call heap allocation.
    /// </summary>
    /// <param name="modules">
    /// Module matrix, one byte per module (0 = light, non-zero = dark), flat row-major order.
    /// A light quiet zone border is detected and skipped automatically.
    /// </param>
    /// <param name="size">Matrix size in modules per side (including quiet zone if present).</param>
    /// <param name="destination">Destination buffer for decoded characters. Use <see cref="GetMaxDecodedLength"/> to size it.</param>
    /// <param name="charsWritten">Number of characters written to <paramref name="destination"/>.</param>
    /// <param name="info">Diagnostic information (status, version, ECC level, mask pattern, corrected errors).</param>
    /// <returns>True when decoding succeeded.</returns>
    /// <exception cref="ArgumentException"></exception>
    public static bool TryDecode(ReadOnlySpan<byte> modules, int size, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
    {
        // long arithmetic: size is caller-controlled and size² overflows int at 46341
        if (size < 1 || modules.Length < (long)size * size)
            throw new ArgumentException($"Module buffer too small: required {(long)size * size}, got {modules.Length}", nameof(modules));

        if (!TryLocateCore(modules, size, out var top, out var left, out var coreSize))
        {
            charsWritten = 0;
            info = new QRCodeDecodeInfo(QRCodeDecodeStatus.InvalidMatrix, 0, default, -1, 0);
            return false;
        }

        if (top == 0 && left == 0 && coreSize == size)
            return QRMatrixDecoder.DecodeMatrix(modules.Slice(0, size * size), coreSize, destination, out charsWritten, out info) == QRCodeDecodeStatus.Success;

        var rented = ArrayPool<byte>.Shared.Rent(coreSize * coreSize);
        try
        {
            var core = rented.AsSpan(0, coreSize * coreSize);
            CopyCoreWindow(modules, size, top, left, coreSize, core);
            return QRMatrixDecoder.DecodeMatrix(core, coreSize, destination, out charsWritten, out info) == QRCodeDecodeStatus.Success;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary>
    /// Detects and decodes a QR code from grayscale image pixels.
    /// </summary>
    /// <param name="luminance">Grayscale pixels (0 = black, 255 = white), flat row-major order, width × height bytes. Transparent source pixels must be composited against white before conversion: the quiet zone is white by definition, and a symbol composited against black is not detected.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
    /// <param name="info">Diagnostic information (status, version, ECC level, mask pattern, corrected errors).</param>
    /// <returns>True when a QR code was detected and decoded.</returns>
    /// <exception cref="ArgumentException"></exception>
    public static bool TryDecodeImage(ReadOnlySpan<byte> luminance, int width, int height, out string text, out QRCodeDecodeInfo info)
    {
        char[]? rentedChars = null;
        try
        {
            // Version is unknown until detection completes, so size for the maximum
            var maxChars = QRMatrixDecoder.GetMaxCharCount(40);
            rentedChars = ArrayPool<char>.Shared.Rent(maxChars);

            var success = TryDecodeImage(luminance, width, height, rentedChars.AsSpan(0, maxChars), out var charsWritten, out info);
            text = success ? rentedChars.AsSpan(0, charsWritten).ToString() : string.Empty;
            return success;
        }
        finally
        {
            if (rentedChars is not null)
                ArrayPool<char>.Shared.Return(rentedChars, clearArray: false);
        }
    }

    /// <summary>
    /// Detects and decodes a QR code from grayscale image pixels into a
    /// caller-provided buffer without per-call heap allocation.
    /// </summary>
    /// <param name="luminance">Grayscale pixels (0 = black, 255 = white), flat row-major order, width × height bytes. Transparent source pixels must be composited against white before conversion: the quiet zone is white by definition, and a symbol composited against black is not detected.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="destination">Destination buffer for decoded characters. Use <see cref="GetMaxDecodedLength"/> to size it.</param>
    /// <param name="charsWritten">Number of characters written to <paramref name="destination"/>.</param>
    /// <param name="info">Diagnostic information (status, version, ECC level, mask pattern, corrected errors).</param>
    /// <returns>True when a QR code was detected and decoded.</returns>
    /// <exception cref="ArgumentException"></exception>
    public static bool TryDecodeImage(ReadOnlySpan<byte> luminance, int width, int height, Span<char> destination, out int charsWritten, out QRCodeDecodeInfo info)
    {
        // long arithmetic: dimensions are caller-controlled and width·height can overflow int
        if (width < 1 || height < 1 || luminance.Length < (long)width * height)
            throw new ArgumentException($"Luminance buffer too small: required {(long)width * height}, got {luminance.Length}", nameof(luminance));

        return QRImageDecoder.DecodeLuminance(luminance, width, height, destination, out charsWritten, out info) == QRCodeDecodeStatus.Success;
    }

    /// <summary>
    /// Calculates the maximum possible decoded character count for a QR code version,
    /// across all ECC levels and encoding modes. Use to size the destination buffer
    /// for the allocation-free <see cref="TryDecode(ReadOnlySpan{byte}, int, Span{char}, out int, out QRCodeDecodeInfo)"/> overload.
    /// </summary>
    /// <param name="version">QR code version (1-40).</param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public static int GetMaxDecodedLength(int version)
    {
        if (version is < 1 or > 40)
            throw new ArgumentOutOfRangeException(nameof(version), $"Version must be 1-40, but was {version}");

        return QRMatrixDecoder.GetMaxCharCount(version);
    }

    private static bool TryDecodeCore(ReadOnlySpan<byte> core, int coreSize, out string text, out QRCodeDecodeInfo info)
    {
        char[]? rentedChars = null;
        try
        {
            // Version is known from the matrix size, so the exact character bound is too
            var version = (coreSize - 21) / 4 + 1;
            var maxChars = coreSize >= 21 && coreSize <= 177 && (coreSize - 21) % 4 == 0
                ? QRMatrixDecoder.GetMaxCharCount(version)
                : 0;

            Span<char> chars = maxChars == 0
                ? default
                : (rentedChars = ArrayPool<char>.Shared.Rent(maxChars)).AsSpan(0, maxChars);

            var status = QRMatrixDecoder.DecodeMatrix(core, coreSize, chars, out var charsWritten, out info);
            text = status == QRCodeDecodeStatus.Success ? chars.Slice(0, charsWritten).ToString() : string.Empty;
            return status == QRCodeDecodeStatus.Success;
        }
        finally
        {
            if (rentedChars is not null)
                ArrayPool<char>.Shared.Return(rentedChars, clearArray: false);
        }
    }

    /// <summary>
    /// Locates the core matrix inside an input that may carry a light quiet zone
    /// border. A valid QR code has dark finder-pattern corners, so the bounding box
    /// of dark modules is exactly the core area.
    /// </summary>
    private static bool TryLocateCore(ReadOnlySpan<byte> modules, int size, out int top, out int left, out int coreSize)
    {
        top = -1;
        left = size;
        coreSize = 0;
        var bottom = -1;
        var right = -1;

        // Bounding box of dark modules
        for (var y = 0; y < size; y++)
        {
            var row = modules.Slice(y * size, size);

            var rowLeft = -1;
            for (var x = 0; x < size; x++)
            {
                if (row[x] != 0)
                {
                    rowLeft = x;
                    break;
                }
            }
            if (rowLeft < 0)
                continue;

            var rowRight = rowLeft;
            for (var x = size - 1; x > rowLeft; x--)
            {
                if (row[x] != 0)
                {
                    rowRight = x;
                    break;
                }
            }

            if (top < 0)
                top = y;
            bottom = y;
            if (rowLeft < left)
                left = rowLeft;
            if (rowRight > right)
                right = rowRight;
        }

        if (top < 0)
            return false; // all light

        var width = right - left + 1;
        var height = bottom - top + 1;
        if (width != height || width < 21 || width > 177 || (width - 21) % 4 != 0)
            return false;

        coreSize = width;
        return true;
    }

    /// <summary>
    /// Copies the core window (rows are not contiguous inside the bordered input)
    /// into a contiguous buffer the matrix decoder can walk.
    /// </summary>
    private static void CopyCoreWindow(ReadOnlySpan<byte> modules, int size, int top, int left, int coreSize, Span<byte> destination)
    {
        for (var y = 0; y < coreSize; y++)
        {
            modules.Slice((top + y) * size + left, coreSize).CopyTo(destination.Slice(y * coreSize, coreSize));
        }
    }
}
