using System.Buffers;
using FeatherQR.Internals.RmQR;

namespace FeatherQR;

/// <summary>
/// rMQR Code (ISO/IEC 23941) decoder: module matrix → text. Sibling of
/// <see cref="QRCodeDecoder"/> and <see cref="MicroQRCodeDecoder"/>; explicitly
/// typed so Standard QR scanning stays unaffected. Matrix-level and image-level
/// decoding (<see cref="TryDecodeImage(ReadOnlySpan{byte}, int, int, out string, out RmQRCodeDecodeInfo)"/>).
/// </summary>
/// <remarks>
/// Every overload accepts an rMQR matrix with or without a light quiet zone: the
/// dark bounding box (finder corner top-left, sub-finder corner bottom-right, timing
/// patterns on all four edges) locates the core, so uniform and asymmetric borders
/// are stripped automatically. Reed-Solomon corrections are applied at full block
/// strength (⌊ecc/2⌋ per block) and reported in <see cref="RmQRCodeDecodeInfo.ErrorsCorrected"/>.
/// Numeric, Alphanumeric and Byte segments (ISO-8859-1 / UTF-8, with or without ECI)
/// are supported, plus Kanji segments decoded as JIS X 0208; the generator never emits
/// Kanji, so it is a read-only mode here. A Kanji cell outside the JIS X 0208
/// repertoire fails the whole symbol with
/// <see cref="DecodeStatus.UnmappedCharacter"/> rather than substituting a
/// replacement character.
/// </remarks>
public static class RmQRCodeDecoder
{
    /// <summary>
    /// Decodes the text content of an <see cref="RmQRCodeData"/> matrix.
    /// </summary>
    /// <param name="data">The rMQR code data.</param>
    /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
    /// <returns>True when decoding succeeded.</returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static bool TryDecode(RmQRCodeData data, out string text) => TryDecode(data, out text, out _);

    /// <summary>
    /// Decodes the text content of an <see cref="RmQRCodeData"/> matrix with diagnostics.
    /// </summary>
    /// <param name="data">The rMQR code data.</param>
    /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
    /// <param name="info">Diagnostic information (status, version, ECC level, corrected errors).</param>
    /// <returns>True when decoding succeeded.</returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static bool TryDecode(RmQRCodeData data, out string text, out RmQRCodeDecodeInfo info)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));

        var width = data.GetCoreWidth();
        var height = data.GetCoreHeight();
        var rented = ArrayPool<byte>.Shared.Rent(width * height);
        try
        {
            var modules = rented.AsSpan(0, width * height);
            data.GetCoreData(modules);
            return TryDecodeCore(modules, width, height, out text, out info);
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
    /// order over <paramref name="width"/>, the format produced by
    /// <see cref="RmQRCodeGenerator.Create(ReadOnlySpan{char}, RmQREccLevel, Span{byte}, in RmQRCodeGeneratorOptions)"/>.
    /// A light quiet zone border (uniform or not) is detected and skipped automatically.
    /// </param>
    /// <param name="width">Matrix width in modules (including any quiet zone).</param>
    /// <param name="height">Matrix height in modules (including any quiet zone).</param>
    /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
    /// <param name="info">Diagnostic information (status, version, ECC level, corrected errors).</param>
    /// <returns>True when decoding succeeded.</returns>
    /// <exception cref="ArgumentException"></exception>
    public static bool TryDecode(ReadOnlySpan<byte> modules, int width, int height, out string text, out RmQRCodeDecodeInfo info)
    {
        ValidateMatrix(modules, width, height);
        if (!TryLocateCore(modules, width, height, out var left, out var top, out var coreWidth, out var coreHeight))
        {
            text = string.Empty;
            info = new RmQRCodeDecodeInfo(DecodeStatus.InvalidMatrix, 0, default, 0);
            return false;
        }

        if (left == 0 && top == 0 && coreWidth == width && coreHeight == height)
            return TryDecodeCore(modules.Slice(0, width * height), width, height, out text, out info);

        var rented = ArrayPool<byte>.Shared.Rent(coreWidth * coreHeight);
        try
        {
            var core = rented.AsSpan(0, coreWidth * coreHeight);
            CopyCoreWindow(modules, width, left, top, coreWidth, coreHeight, core);
            return TryDecodeCore(core, coreWidth, coreHeight, out text, out info);
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
    /// <param name="modules">Module matrix, one byte per module (0 = light, non-zero = dark), flat row-major order over <paramref name="width"/>; a light quiet zone border is detected and skipped automatically.</param>
    /// <param name="width">Matrix width in modules (including any quiet zone).</param>
    /// <param name="height">Matrix height in modules (including any quiet zone).</param>
    /// <param name="destination">Destination buffer for decoded characters. Use <see cref="GetMaxDecodedLength"/> to size it.</param>
    /// <param name="charsWritten">Number of characters written to <paramref name="destination"/>.</param>
    /// <param name="info">Diagnostic information (status, version, ECC level, corrected errors).</param>
    /// <returns>True when decoding succeeded.</returns>
    /// <exception cref="ArgumentException"></exception>
    public static bool TryDecode(ReadOnlySpan<byte> modules, int width, int height, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info)
    {
        ValidateMatrix(modules, width, height);
        if (!TryLocateCore(modules, width, height, out var left, out var top, out var coreWidth, out var coreHeight))
        {
            charsWritten = 0;
            info = new RmQRCodeDecodeInfo(DecodeStatus.InvalidMatrix, 0, default, 0);
            return false;
        }

        if (left == 0 && top == 0 && coreWidth == width && coreHeight == height)
            return RmQRMatrixDecoder.DecodeMatrix(modules.Slice(0, width * height), width, height, destination, out charsWritten, out info) == DecodeStatus.Success;

        // Cores are at most 17 × 139 = 2,363 modules: pooled (as the generator), never escapes.
        var rented = ArrayPool<byte>.Shared.Rent(coreWidth * coreHeight);
        try
        {
            var core = rented.AsSpan(0, coreWidth * coreHeight);
            CopyCoreWindow(modules, width, left, top, coreWidth, coreHeight, core);
            return RmQRMatrixDecoder.DecodeMatrix(core, coreWidth, coreHeight, destination, out charsWritten, out info) == DecodeStatus.Success;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary>
    /// Detects and decodes an rMQR Code from grayscale image pixels.
    /// </summary>
    /// <param name="luminance">Grayscale pixels (0 = black, 255 = white), flat row-major order, width × height bytes. Transparent source pixels must be composited against white before conversion: the quiet zone is white by definition, and a symbol composited against black is not detected.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
    /// <param name="info">Diagnostic information (status, version, ECC level, corrected errors).</param>
    /// <returns>True when an rMQR Code was detected and decoded.</returns>
    /// <exception cref="ArgumentException"></exception>
    public static bool TryDecodeImage(ReadOnlySpan<byte> luminance, int width, int height, out string text, out RmQRCodeDecodeInfo info)
    {
        char[]? rentedChars = null;
        try
        {
            // Version is unknown until detection completes, so size for the maximum
            var maxChars = RmQRMatrixDecoder.GetMaxCharCount(RmQRVersion.R17x139);
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
    /// Detects and decodes an rMQR Code from grayscale image pixels into a
    /// caller-provided buffer without per-call heap allocation.
    /// </summary>
    /// <param name="luminance">Grayscale pixels (0 = black, 255 = white), flat row-major order, width × height bytes. Transparent source pixels must be composited against white before conversion: the quiet zone is white by definition, and a symbol composited against black is not detected.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="destination">Destination buffer for decoded characters. Use <see cref="GetMaxDecodedLength"/> (with <see cref="RmQRVersion.R17x139"/> when the version is unknown) to size it.</param>
    /// <param name="charsWritten">Number of characters written to <paramref name="destination"/>.</param>
    /// <param name="info">Diagnostic information (status, version, ECC level, corrected errors).</param>
    /// <returns>True when an rMQR Code was detected and decoded.</returns>
    /// <exception cref="ArgumentException"></exception>
    public static bool TryDecodeImage(ReadOnlySpan<byte> luminance, int width, int height, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info)
    {
        // long arithmetic: dimensions are caller-controlled and width·height can overflow int
        if (width < 1 || height < 1 || luminance.Length < (long)width * height)
            throw new ArgumentException($"Luminance buffer too small: required {(long)width * height}, got {luminance.Length}", nameof(luminance));

        return RmQRImageDecoder.DecodeLuminance(luminance, width, height, destination, out charsWritten, out info) == DecodeStatus.Success;
    }

    /// <summary>
    /// Calculates the maximum possible decoded character count for an rMQR version,
    /// across ECC levels and encoding modes. Use to size the destination buffer for
    /// the allocation-free <see cref="TryDecode(ReadOnlySpan{byte}, int, int, Span{char}, out int, out RmQRCodeDecodeInfo)"/> overload.
    /// </summary>
    /// <param name="version">rMQR version.</param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public static int GetMaxDecodedLength(RmQRVersion version)
    {
        if (!RmQRConstants.IsValidVersion(version))
            throw new ArgumentOutOfRangeException(nameof(version), $"Invalid rMQR version: {version}");

        return RmQRMatrixDecoder.GetMaxCharCount(version);
    }

    private static void ValidateMatrix(ReadOnlySpan<byte> modules, int width, int height)
    {
        // long arithmetic: dimensions are caller-controlled and width × height can overflow int
        if (width < 1 || height < 1 || modules.Length < (long)width * height)
            throw new ArgumentException($"Module buffer too small: required {(long)Math.Max(width, 0) * Math.Max(height, 0)}, got {modules.Length}", nameof(modules));
    }

    private static bool TryDecodeCore(ReadOnlySpan<byte> core, int width, int height, out string text, out RmQRCodeDecodeInfo info)
    {
        char[]? rentedChars = null;
        try
        {
            var maxChars = RmQRConstants.TryGetVersion(height, width, out var version) ? RmQRMatrixDecoder.GetMaxCharCount(version) : 0;
            Span<char> chars = maxChars == 0
                ? default
                : (rentedChars = ArrayPool<char>.Shared.Rent(maxChars)).AsSpan(0, maxChars);

            var status = RmQRMatrixDecoder.DecodeMatrix(core, width, height, chars, out var charsWritten, out info);
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
    /// Locates the core inside an input that may carry a light border. rMQR has
    /// dark modules at all four core corners (finder top-left, corner pattern
    /// top-right and bottom-left, sub-finder bottom-right) and timing patterns on
    /// every edge, so the dark bounding box IS the core; the border need not be
    /// uniform. The box must be an rMQR size.
    /// </summary>
    private static bool TryLocateCore(ReadOnlySpan<byte> modules, int width, int height, out int left, out int top, out int coreWidth, out int coreHeight)
    {
        left = width;
        top = -1;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < height; y++)
        {
            var row = modules.Slice(y * width, width);
            // First dark module in the row (netstandard2.0 has no IndexOfAnyExcept).
            var first = -1;
            for (var x = 0; x < width; x++)
            {
                if (row[x] != 0)
                {
                    first = x;
                    break;
                }
            }
            if (first < 0)
                continue;
            if (top < 0)
                top = y;
            bottom = y;
            if (first < left)
                left = first;
            for (var x = width - 1; x > right; x--)
            {
                if (row[x] != 0)
                {
                    right = x;
                    break;
                }
            }
        }

        if (top < 0)
        {
            coreWidth = coreHeight = 0;
            return false; // all light
        }

        coreWidth = right - left + 1;
        coreHeight = bottom - top + 1;
        return RmQRConstants.TryGetVersion(coreHeight, coreWidth, out _);
    }

    /// <summary>Copies the core window (rows are not contiguous inside the bordered input) into a contiguous buffer.</summary>
    private static void CopyCoreWindow(ReadOnlySpan<byte> modules, int width, int left, int top, int coreWidth, int coreHeight, Span<byte> destination)
    {
        for (var y = 0; y < coreHeight; y++)
        {
            modules.Slice((top + y) * width + left, coreWidth).CopyTo(destination.Slice(y * coreWidth, coreWidth));
        }
    }
}
