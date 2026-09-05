using System.Buffers;
using FeatherQR.Internals.MicroQR;

namespace FeatherQR;

/// <summary>
/// Micro QR code decoder based on ISO/IEC 18004.
/// Decodes Micro QR module matrices back into text, including Reed-Solomon error correction.
/// </summary>
/// <remarks>
/// <para>
/// Supported content: Numeric, Alphanumeric and Byte mode segments (ISO-8859-1 and
/// UTF-8), all versions M1-M4 and all legal ECC levels, plus Kanji mode segments in
/// M3 and M4 (decoded as JIS X 0208; this library never emits them). A Kanji cell
/// outside the JIS X 0208 repertoire fails the whole symbol with
/// <see cref="DecodeStatus.UnmappedCharacter"/> rather than substituting a
/// replacement character. Micro QR has no
/// ECI mode; byte segments use UTF-8 when the payload validates as UTF-8 and
/// ISO-8859-1 otherwise (matching this library's encoder).
/// </para>
/// <para>
/// Inputs are module matrices (<see cref="MicroQRCodeData"/> or byte-per-module
/// buffers as produced by <see cref="MicroQRCodeGenerator"/>; a uniform light quiet
/// zone border is detected and skipped automatically) or images via the
/// <see cref="TryDecodeImage(ReadOnlySpan{byte}, int, int, out string, out MicroQRCodeDecodeInfo)"/>
/// overloads. Image detection targets clean, screen-rendered or scanned images:
/// arbitrary rotation, mirroring, reflectance reversal, uniform or non-uniform
/// scaling, translation and mild perspective distortion are supported. Micro QR
/// image scanning is a separate, explicitly-typed entry point —
/// <see cref="QRCodeDecoder"/> continues to scan Standard QR only.
/// </para>
/// </remarks>
public static class MicroQRCodeDecoder
{
    /// <summary>
    /// Decodes the text content from Micro QR code data.
    /// </summary>
    /// <param name="data">The Micro QR code data to decode.</param>
    /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
    /// <returns>True when decoding succeeded.</returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static bool TryDecode(MicroQRCodeData data, out string text)
        => TryDecode(data, out text, out _);

    /// <summary>
    /// Decodes the text content from Micro QR code data, with diagnostic information.
    /// </summary>
    /// <param name="data">The Micro QR code data to decode.</param>
    /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
    /// <param name="info">Diagnostic information (status, version, ECC level, mask pattern, corrected errors).</param>
    /// <returns>True when decoding succeeded.</returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static bool TryDecode(MicroQRCodeData data, out string text, out MicroQRCodeDecodeInfo info)
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
    /// order, the format produced by <see cref="MicroQRCodeGenerator.Create(ReadOnlySpan{char}, MicroQREccLevel, Span{byte}, in MicroQRCodeGeneratorOptions)"/>.
    /// A uniform light quiet zone border is detected and skipped automatically.
    /// </param>
    /// <param name="size">Matrix size in modules per side (including quiet zone if present).</param>
    /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
    /// <param name="info">Diagnostic information (status, version, ECC level, mask pattern, corrected errors).</param>
    /// <returns>True when decoding succeeded.</returns>
    /// <exception cref="ArgumentException"></exception>
    public static bool TryDecode(ReadOnlySpan<byte> modules, int size, out string text, out MicroQRCodeDecodeInfo info)
    {
        // long arithmetic: size is caller-controlled and size² overflows int at 46341
        if (size < 1 || modules.Length < (long)size * size)
            throw new ArgumentException($"Module buffer too small: required {(long)size * size}, got {modules.Length}", nameof(modules));

        if (!TryLocateCore(modules, size, out var origin, out var coreSize))
        {
            text = string.Empty;
            info = new MicroQRCodeDecodeInfo(DecodeStatus.InvalidMatrix, 0, default, -1, 0);
            return false;
        }

        if (origin == 0 && coreSize == size)
            return TryDecodeCore(modules.Slice(0, size * size), coreSize, out text, out info);

        var rented = ArrayPool<byte>.Shared.Rent(coreSize * coreSize);
        try
        {
            var core = rented.AsSpan(0, coreSize * coreSize);
            CopyCoreWindow(modules, size, origin, coreSize, core);
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
    /// A uniform light quiet zone border is detected and skipped automatically.
    /// </param>
    /// <param name="size">Matrix size in modules per side (including quiet zone if present).</param>
    /// <param name="destination">Destination buffer for decoded characters. Use <see cref="GetMaxDecodedLength"/> to size it.</param>
    /// <param name="charsWritten">Number of characters written to <paramref name="destination"/>.</param>
    /// <param name="info">Diagnostic information (status, version, ECC level, mask pattern, corrected errors).</param>
    /// <returns>True when decoding succeeded.</returns>
    /// <exception cref="ArgumentException"></exception>
    public static bool TryDecode(ReadOnlySpan<byte> modules, int size, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
    {
        // long arithmetic: size is caller-controlled and size² overflows int at 46341
        if (size < 1 || modules.Length < (long)size * size)
            throw new ArgumentException($"Module buffer too small: required {(long)size * size}, got {modules.Length}", nameof(modules));

        if (!TryLocateCore(modules, size, out var origin, out var coreSize))
        {
            charsWritten = 0;
            info = new MicroQRCodeDecodeInfo(DecodeStatus.InvalidMatrix, 0, default, -1, 0);
            return false;
        }

        if (origin == 0 && coreSize == size)
            return MicroQRMatrixDecoder.DecodeMatrix(modules.Slice(0, size * size), coreSize, destination, out charsWritten, out info) == DecodeStatus.Success;

        // Micro QR cores are at most 17×17 = 289 modules, small enough for the stack.
        Span<byte> core = stackalloc byte[17 * 17].Slice(0, coreSize * coreSize);
        CopyCoreWindow(modules, size, origin, coreSize, core);
        return MicroQRMatrixDecoder.DecodeMatrix(core, coreSize, destination, out charsWritten, out info) == DecodeStatus.Success;
    }

    /// <summary>
    /// Detects and decodes a Micro QR code from grayscale image pixels.
    /// </summary>
    /// <param name="luminance">Grayscale pixels (0 = black, 255 = white), flat row-major order, width × height bytes. Transparent source pixels must be composited against white before conversion: the quiet zone is white by definition, and a symbol composited against black is not detected.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
    /// <param name="info">Diagnostic information (status, version, ECC level, mask pattern, corrected errors).</param>
    /// <returns>True when a Micro QR code was detected and decoded.</returns>
    /// <exception cref="ArgumentException"></exception>
    public static bool TryDecodeImage(ReadOnlySpan<byte> luminance, int width, int height, out string text, out MicroQRCodeDecodeInfo info)
    {
        char[]? rentedChars = null;
        try
        {
            // Version is unknown until detection completes, so size for the maximum
            var maxChars = MicroQRMatrixDecoder.GetMaxCharCount(MicroQRVersion.M4);
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
    /// Detects and decodes a Micro QR code from grayscale image pixels into a
    /// caller-provided buffer without per-call heap allocation.
    /// </summary>
    /// <param name="luminance">Grayscale pixels (0 = black, 255 = white), flat row-major order, width × height bytes. Transparent source pixels must be composited against white before conversion: the quiet zone is white by definition, and a symbol composited against black is not detected.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="destination">Destination buffer for decoded characters. Use <see cref="GetMaxDecodedLength"/> to size it.</param>
    /// <param name="charsWritten">Number of characters written to <paramref name="destination"/>.</param>
    /// <param name="info">Diagnostic information (status, version, ECC level, mask pattern, corrected errors).</param>
    /// <returns>True when a Micro QR code was detected and decoded.</returns>
    /// <exception cref="ArgumentException"></exception>
    public static bool TryDecodeImage(ReadOnlySpan<byte> luminance, int width, int height, Span<char> destination, out int charsWritten, out MicroQRCodeDecodeInfo info)
    {
        // long arithmetic: dimensions are caller-controlled and width·height can overflow int
        if (width < 1 || height < 1 || luminance.Length < (long)width * height)
            throw new ArgumentException($"Luminance buffer too small: required {(long)width * height}, got {luminance.Length}", nameof(luminance));

        return MicroQRImageDecoder.DecodeLuminance(luminance, width, height, destination, out charsWritten, out info) == DecodeStatus.Success;
    }

    /// <summary>
    /// Calculates the maximum possible decoded character count for a Micro QR version,
    /// across all ECC levels and encoding modes. Use to size the destination buffer
    /// for the allocation-free <see cref="TryDecode(ReadOnlySpan{byte}, int, Span{char}, out int, out MicroQRCodeDecodeInfo)"/> overload.
    /// </summary>
    /// <param name="version">Micro QR version (M1-M4).</param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public static int GetMaxDecodedLength(MicroQRVersion version)
    {
        if ((uint)((int)version - 1) > 3)
            throw new ArgumentOutOfRangeException(nameof(version), $"Invalid Micro QR version: {version}");

        return MicroQRMatrixDecoder.GetMaxCharCount(version);
    }

    private static bool TryDecodeCore(ReadOnlySpan<byte> core, int coreSize, out string text, out MicroQRCodeDecodeInfo info)
    {
        char[]? rentedChars = null;
        try
        {
            var version = MicroQRConstants.VersionFromSize(coreSize);
            var maxChars = version == 0 ? 0 : MicroQRMatrixDecoder.GetMaxCharCount(version);

            Span<char> chars = maxChars == 0
                ? default
                : (rentedChars = ArrayPool<char>.Shared.Rent(maxChars)).AsSpan(0, maxChars);

            var status = MicroQRMatrixDecoder.DecodeMatrix(core, coreSize, chars, out var charsWritten, out info);
            text = status == DecodeStatus.Success ? chars.Slice(0, charsWritten).ToString() : string.Empty;
            return status == DecodeStatus.Success;
        }
        finally
        {
            if (rentedChars is not null)
                ArrayPool<char>.Shared.Return(rentedChars, clearArray: false);
        }
    }

    /// <summary>
    /// Locates the core matrix inside an input that may carry a light quiet zone
    /// border. Micro QR has a single finder pattern, so unlike Standard QR the
    /// right/bottom edges carry data and are not guaranteed dark, the dark
    /// bounding box cannot size the core. Instead the top-left dark module is the
    /// finder corner (core origin); a uniform border implies
    /// <c>coreSize = size − 2·origin</c>.
    /// </summary>
    private static bool TryLocateCore(ReadOnlySpan<byte> modules, int size, out int origin, out int coreSize)
    {
        origin = -1;
        coreSize = 0;

        // First row containing a dark module, and the minimum dark column.
        // Until the first dark module is found, left == size, so the full row is
        // scanned; afterwards only columns left of the current minimum matter.
        var top = -1;
        var left = size;
        for (var y = 0; y < size; y++)
        {
            var row = modules.Slice(y * size, size);
            for (var x = 0; x < left; x++)
            {
                if (row[x] != 0)
                {
                    if (top < 0)
                        top = y;
                    left = x;
                    break;
                }
            }
            if (left == 0 && top >= 0)
                break; // origin cannot get smaller
        }

        if (top < 0)
            return false; // all light

        // Core row 0 / column 0 are timing patterns starting dark at the finder
        // corner, so the first dark row and column meet at the core origin.
        if (top != left)
            return false;

        var candidate = size - 2 * top;
        if (MicroQRConstants.VersionFromSize(candidate) == 0)
            return false;

        origin = top;
        coreSize = candidate;
        return true;
    }

    /// <summary>
    /// Copies the core window (rows are not contiguous inside the bordered input)
    /// into a contiguous buffer the matrix decoder can walk.
    /// </summary>
    private static void CopyCoreWindow(ReadOnlySpan<byte> modules, int size, int origin, int coreSize, Span<byte> destination)
    {
        for (var y = 0; y < coreSize; y++)
        {
            modules.Slice((origin + y) * size + origin, coreSize).CopyTo(destination.Slice(y * coreSize, coreSize));
        }
    }
}
