using System.Buffers;
using FeatherQR.Internals.RmQR;

namespace FeatherQR;

/// <summary>
/// Decodes an rMQR code (ISO/IEC 23941) back into text, correcting errors as it goes.
/// </summary>
/// <remarks>
/// Reads Numeric, Alphanumeric, Byte and Kanji mode across all 32 versions and both error correction levels.
/// A Byte segment with no ECI header is read as UTF-8 when the bytes are valid UTF-8, and as ISO-8859-1 otherwise.
/// Kanji is mapped through JIS X 0208, so a cell outside that repertoire fails the whole rMQR code with <see cref="DecodeStatus.UnmappedCharacter"/> rather than substituting a replacement character.
/// Reed-Solomon runs at full block strength, correcting up to ⌊ecc/2⌋ codewords per block, and reports the count in <see cref="RmQRCodeDecodeInfo.ErrorsCorrected"/>.
/// Both a plain matrix and one with a quiet zone are accepted; the border is located and stripped automatically.
/// Image scanning is a separate entry point, and <see cref="QRCodeDecoder"/> keeps scanning Standard QR only.
/// </remarks>
public static class RmQRCodeDecoder
{
    /// <summary>
    /// Decodes the text from an rMQR code.
    /// </summary>
    /// <param name="data">The rMQR code to decode.</param>
    /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
    /// <returns><c>true</c> when the rMQR code decoded.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
    public static bool TryDecode(RmQRCodeData data, out string text) => TryDecode(data, out text, out _);

    /// <summary>
    /// Decodes the text from an rMQR code and reports what it found.
    /// </summary>
    /// <param name="data">The rMQR code to decode.</param>
    /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
    /// <param name="info">What the attempt found: status, version, level and corrections.</param>
    /// <returns><c>true</c> when the rMQR code decoded.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
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
    /// Decodes the text from a module matrix.
    /// </summary>
    /// <param name="modules">The matrix: one byte per module, 0 light and non-zero dark, row-major. A light quiet zone border is skipped automatically.</param>
    /// <param name="width">Width in modules, quiet zone included.</param>
    /// <param name="height">Height in modules, quiet zone included.</param>
    /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
    /// <param name="info">What the attempt found: status, version, level and corrections.</param>
    /// <returns><c>true</c> when the rMQR code decoded.</returns>
    /// <exception cref="ArgumentException">Thrown when the buffer is smaller than the dimensions require.</exception>
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
    /// Decodes the text into the buffer you provide, without allocating.
    /// </summary>
    /// <param name="modules">The matrix: one byte per module, 0 light and non-zero dark, row-major. A light quiet zone border is skipped automatically.</param>
    /// <param name="width">Width in modules, quiet zone included.</param>
    /// <param name="height">Height in modules, quiet zone included.</param>
    /// <param name="destination">Destination buffer for decoded characters. Use <see cref="GetMaxDecodedLength"/> to size it.</param>
    /// <param name="charsWritten">How many characters were written.</param>
    /// <param name="info">What the attempt found: status, version, level and corrections.</param>
    /// <returns><c>true</c> when the rMQR code decoded.</returns>
    /// <exception cref="ArgumentException">Thrown when the buffer is smaller than the dimensions require.</exception>
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
    /// Finds and decodes an rMQR code in a grayscale image.
    /// </summary>
    /// <param name="luminance">Grayscale pixels (0 = black, 255 = white), flat row-major order, width × height bytes. Transparent source pixels must be composited against white before conversion: the quiet zone is white by definition, and an rMQR code composited against black is not detected.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="text">Decoded text, or an empty string when decoding fails.</param>
    /// <param name="info">What the attempt found: status, version, level and corrections.</param>
    /// <returns><c>true</c> when an rMQR code was found and decoded.</returns>
    /// <exception cref="ArgumentException">Thrown when the buffer is smaller than the dimensions require.</exception>
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
    /// Finds and decodes an rMQR code in a grayscale image, writing into the buffer you provide without allocating.
    /// </summary>
    /// <param name="luminance">Grayscale pixels (0 = black, 255 = white), flat row-major order, width × height bytes. Transparent source pixels must be composited against white before conversion: the quiet zone is white by definition, and an rMQR code composited against black is not detected.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="destination">Destination buffer for decoded characters. Use <see cref="GetMaxDecodedLength"/> (with <see cref="RmQRVersion.R17x139"/> when the version is unknown) to size it.</param>
    /// <param name="charsWritten">How many characters were written.</param>
    /// <param name="info">What the attempt found: status, version, level and corrections.</param>
    /// <returns><c>true</c> when an rMQR code was found and decoded.</returns>
    /// <exception cref="ArgumentException">Thrown when the buffer is smaller than the dimensions require.</exception>
    public static bool TryDecodeImage(ReadOnlySpan<byte> luminance, int width, int height, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info)
    {
        // long arithmetic: dimensions are caller-controlled and width·height can overflow int
        if (width < 1 || height < 1 || luminance.Length < (long)width * height)
            throw new ArgumentException($"Luminance buffer too small: required {(long)width * height}, got {luminance.Length}", nameof(luminance));

        return RmQRImageDecoder.DecodeLuminance(luminance, width, height, destination, out charsWritten, out info) == DecodeStatus.Success;
    }

    /// <summary>
    /// The most characters an rMQR code of this version can decode to, across every error correction level and mode.
    /// Use it to size the destination of the allocation-free overloads.
    /// </summary>
    /// <param name="version">The version to measure.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the version is not a defined value.</exception>
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
    /// Locates the core inside an input that may carry a light border. rMQR has dark modules at all four core corners (finder top-left, corner pattern top-right and bottom-left, sub-finder bottom-right) and timing patterns on every edge, so the dark bounding box IS the core; the border need not be uniform.
    /// The box must be an rMQR size.
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
