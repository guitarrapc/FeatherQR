using System.Buffers;
using FeatherQR.Internals.BinaryDecoders;
using FeatherQR.Internals.RmQR;

namespace FeatherQR;

/// <summary>
/// Decodes an rMQR code (ISO/IEC 23941) back into text, correcting errors as it goes.
/// </summary>
/// <remarks>
/// Reads Numeric, Alphanumeric, Byte and Kanji mode across all 32 versions and both error correction levels.
/// A Byte segment is read as its ECI header declares, for ISO-8859-1, UTF-8 and Shift_JIS.
/// Another assignment fails the text with <see cref="DecodeStatus.UnsupportedContent"/> and is named on <see cref="RmQRCodeDecodeInfo.EciAssignment"/>, and the bytes calls, <see cref="TryDecodeBytes(RmQRCodeData, out byte[], out RmQRCodeDecodeInfo)"/> and its overloads, read the symbol's bytes under any assignment for the caller to convert.
/// With no ECI header it is read as UTF-8 when the bytes are valid UTF-8, as Shift_JIS when they are well formed Shift_JIS that ISO-8859-1 text would not hold and hold no C0 control character but tab, CR, LF and, under FNC1, the separator 0x1D, and as ISO-8859-1 otherwise.
/// Kanji is mapped through JIS X 0208, so a cell outside that repertoire fails the whole rMQR code with <see cref="DecodeStatus.UnmappedCharacter"/> rather than substituting a replacement character.
/// Reed-Solomon runs at full block strength, correcting up to ⌊ecc/2⌋ codewords per block, and reports the count in <see cref="RmQRCodeDecodeInfo.ErrorsCorrected"/>.
/// FNC1 in first position (GS1 data) and in second position (an industry format with its application indicator) are read: the text is the data with each separator as U+001D, the element string in first position, a <c>%%</c> in an Alphanumeric segment is one <c>%</c>, and the mode is reported on <see cref="RmQRCodeDecodeInfo.Fnc1"/>.
/// Both a plain matrix and one with a quiet zone are accepted. The border is located and stripped automatically.
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
    /// <param name="info">What the attempt found: status, version, level and corrections, and on success the FNC1 mode when the symbol carries one (<see cref="RmQRCodeDecodeInfo.Fnc1"/>).</param>
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
    /// <param name="info">What the attempt found: status, version, level and corrections, and on success the FNC1 mode when the symbol carries one (<see cref="RmQRCodeDecodeInfo.Fnc1"/>).</param>
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
    /// <param name="charsWritten">How many characters were written, or 0 when the matrix does not decode, though <paramref name="destination"/> may still hold the characters read before the failure.</param>
    /// <param name="info">What the attempt found: status, version, level and corrections, and on success the FNC1 mode when the symbol carries one (<see cref="RmQRCodeDecodeInfo.Fnc1"/>).</param>
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
    /// <param name="info">What the attempt found: status, version, level and corrections, and on success where the symbol sits in the image (Corners) and the FNC1 mode when the symbol carries one (<see cref="RmQRCodeDecodeInfo.Fnc1"/>).</param>
    /// <returns><c>true</c> when an rMQR code was found and decoded.</returns>
    /// <exception cref="ArgumentException">Thrown when the buffer is smaller than the dimensions require.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
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
    /// <param name="charsWritten">How many characters were written, or 0 when the image does not decode, though <paramref name="destination"/> may still hold text from an attempt that was not kept.</param>
    /// <param name="info">What the attempt found: status, version, level and corrections, and on success where the symbol sits in the image (Corners) and the FNC1 mode when the symbol carries one (<see cref="RmQRCodeDecodeInfo.Fnc1"/>).</param>
    /// <returns><c>true</c> when an rMQR code was found and decoded.</returns>
    /// <exception cref="ArgumentException">Thrown when the buffer is smaller than the dimensions require.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    public static bool TryDecodeImage(ReadOnlySpan<byte> luminance, int width, int height, Span<char> destination, out int charsWritten, out RmQRCodeDecodeInfo info)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(width < 1 ? nameof(width) : nameof(height), $"Image size must be positive: got {width} x {height}.");
        // long arithmetic: dimensions are caller-controlled and width·height can overflow int
        if (luminance.Length < (long)width * height)
            throw new ArgumentException($"Luminance buffer too small: required {(long)width * height}, got {luminance.Length}", nameof(luminance));

        return RmQRImageDecoder.DecodeLuminance(luminance, width, height, destination, out charsWritten, out info) == DecodeStatus.Success;
    }

    /// <summary>
    /// Finds and decodes every rMQR code in a grayscale image.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each symbol is returned once, in the order found, with where it sits in the image in <see cref="RmQRCodeDecodeInfo.Corners"/>.
    /// Only symbols that decode are returned. One whose content cannot be given as text (<see cref="DecodeStatus.UnmappedCharacter"/>, <see cref="DecodeStatus.UnsupportedContent"/>) is left out, and so is one that lies inside a symbol already read.
    /// </para>
    /// <para>
    /// The search goes on past the first symbol it reads, so it takes longer than <see cref="TryDecodeImage(ReadOnlySpan{byte}, int, int, out string, out RmQRCodeDecodeInfo)"/> on an image of one symbol.
    /// </para>
    /// </remarks>
    /// <param name="luminance">Grayscale pixels (0 = black, 255 = white), flat row-major order, width × height bytes. Transparent source pixels must be composited against white before conversion: the quiet zone is white by definition, and an rMQR code composited against black is not detected.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <returns>The symbols read, an empty array when there is none.</returns>
    /// <exception cref="ArgumentException">Thrown when the buffer is smaller than the dimensions require.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    public static RmQRCodeDecodeResult[] DecodeImageSymbols(ReadOnlySpan<byte> luminance, int width, int height)
    {
        ThrowIfLuminanceDoesNotFit(luminance, width, height);
        return RmQRImageDecoder.DecodeSymbols(luminance, width, height);
    }

    /// <summary>
    /// Finds and decodes the rMQR codes in a grayscale image into the room you provide, as many as it holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads as <see cref="DecodeImageSymbols(ReadOnlySpan{byte}, int, int)"/> does, and stops once <paramref name="destination"/> is full, so a caller who expects a number of symbols pays for no more.
    /// A full room does not say whether the image held more.
    /// </para>
    /// <para>
    /// Nothing is allocated but the texts.
    /// </para>
    /// </remarks>
    /// <param name="luminance">Grayscale pixels (0 = black, 255 = white), flat row-major order, width × height bytes. Transparent source pixels must be composited against white before conversion: the quiet zone is white by definition, and an rMQR code composited against black is not detected.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="destination">Where to write the symbols read, from the start.</param>
    /// <returns>How many symbols were written.</returns>
    /// <exception cref="ArgumentException">Thrown when the buffer is smaller than the dimensions require.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    public static int DecodeImageSymbols(ReadOnlySpan<byte> luminance, int width, int height, Span<RmQRCodeDecodeResult> destination)
    {
        ThrowIfLuminanceDoesNotFit(luminance, width, height);
        return RmQRImageDecoder.DecodeSymbols(luminance, width, height, destination);
    }

    private static void ThrowIfLuminanceDoesNotFit(ReadOnlySpan<byte> luminance, int width, int height)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(width < 1 ? nameof(width) : nameof(height), $"Image size must be positive: got {width} x {height}.");
        // long arithmetic: dimensions are caller-controlled and width·height can overflow int
        if (luminance.Length < (long)width * height)
            throw new ArgumentException($"Luminance buffer too small: required {(long)width * height}, got {luminance.Length}", nameof(luminance));
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

    /// <summary>
    /// Decodes the bytes an rMQR code carries, as they are in the symbol, with no charset applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Byte segment gives its bytes as written, a byte order mark included. Numeric and Alphanumeric segments give the ASCII bytes of their characters, and a Kanji segment the two Shift_JIS bytes of each character, whether or not JIS X 0208 assigns it.
    /// Under FNC1 the separator is the byte 0x1D, as it is U+001D in the text. ECI headers are not bytes. The info reports them.
    /// </para>
    /// <para>
    /// Every ECI assignment reads, and <see cref="RmQRCodeDecodeInfo.EciAssignment"/> names the one the Byte and Kanji segments are under, <c>null</c> when they are under none, and the first declared when the symbol has neither.
    /// A designator number past the last assignment, 999999, fails with <see cref="DecodeStatus.UnsupportedContent"/>, as it does in the text calls.
    /// A symbol whose Byte and Kanji segments fall under two different assignments, no ECI counting as one, fails with <see cref="DecodeStatus.UnsupportedContent"/>, because its bytes have no single reading. The text calls read such a symbol when they have a charset for each assignment.
    /// </para>
    /// </remarks>
    /// <param name="data">The rMQR code to decode.</param>
    /// <param name="bytes">The decoded bytes, or an empty array when decoding fails.</param>
    /// <param name="info">What the attempt to read the bytes found, in the fields <see cref="TryDecode(RmQRCodeData, out string, out RmQRCodeDecodeInfo)"/> reports.</param>
    /// <returns><c>true</c> when the rMQR code decoded.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>.</exception>
    public static bool TryDecodeBytes(RmQRCodeData data, out byte[] bytes, out RmQRCodeDecodeInfo info)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));

        var width = data.GetCoreWidth();
        var height = data.GetCoreHeight();
        var rented = ArrayPool<byte>.Shared.Rent(width * height);
        var maxBytes = RmQRMatrixDecoder.GetMaxCharCount(data.Version);
        var rentedChars = ArrayPool<char>.Shared.Rent(maxBytes);
        try
        {
            var core = rented.AsSpan(0, width * height);
            data.GetCoreData(core);
            var chars = rentedChars.AsSpan(0, maxBytes);
            DecodeStatus status;
            int written;
            // The core as it stands, as the text call decodes it. A search for its border could cut a light edge row off.
            using (PayloadOutput.Bytes())
                status = RmQRMatrixDecoder.DecodeMatrix(core, width, height, chars, out written, out info);
            bytes = status == DecodeStatus.Success ? PayloadOutput.ToArray(chars.Slice(0, written)) : [];
            return status == DecodeStatus.Success;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rentedChars, clearArray: false);
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary>
    /// Decodes the bytes a module matrix carries, as <see cref="TryDecodeBytes(RmQRCodeData, out byte[], out RmQRCodeDecodeInfo)"/> reads them.
    /// </summary>
    /// <param name="modules">The matrix: one byte per module, 0 light and non-zero dark, row-major. A light quiet zone border is skipped automatically.</param>
    /// <param name="width">Width in modules, quiet zone included.</param>
    /// <param name="height">Height in modules, quiet zone included.</param>
    /// <param name="bytes">The decoded bytes, or an empty array when decoding fails.</param>
    /// <param name="info">What the attempt to read the bytes found, in the fields <see cref="TryDecode(ReadOnlySpan{byte}, int, int, out string, out RmQRCodeDecodeInfo)"/> reports.</param>
    /// <returns><c>true</c> when the rMQR code decoded.</returns>
    /// <exception cref="ArgumentException">Thrown when the buffer is smaller than the dimensions require.</exception>
    public static bool TryDecodeBytes(ReadOnlySpan<byte> modules, int width, int height, out byte[] bytes, out RmQRCodeDecodeInfo info)
    {
        var maxBytes = RmQRMatrixDecoder.GetMaxCharCount(RmQRVersion.R17x139);
        var rentedChars = ArrayPool<char>.Shared.Rent(maxBytes);
        try
        {
            var chars = rentedChars.AsSpan(0, maxBytes);
            bool success;
            int written;
            using (PayloadOutput.Bytes())
                success = TryDecode(modules, width, height, chars, out written, out info);
            bytes = success ? PayloadOutput.ToArray(chars.Slice(0, written)) : [];
            return success;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rentedChars, clearArray: false);
        }
    }

    /// <summary>
    /// Decodes the bytes a module matrix carries into the buffer you provide, as <see cref="TryDecodeBytes(RmQRCodeData, out byte[], out RmQRCodeDecodeInfo)"/> reads them.
    /// </summary>
    /// <param name="modules">The matrix: one byte per module, 0 light and non-zero dark, row-major. A light quiet zone border is skipped automatically.</param>
    /// <param name="width">Width in modules, quiet zone included.</param>
    /// <param name="height">Height in modules, quiet zone included.</param>
    /// <param name="destination">Destination buffer for the decoded bytes. <see cref="GetMaxDecodedLength"/> sizes it for bytes too.</param>
    /// <param name="bytesWritten">How many bytes were written. 0 when the matrix does not decode, and <paramref name="destination"/> is then left as it was.</param>
    /// <param name="info">What the attempt to read the bytes found, in the fields <see cref="TryDecode(ReadOnlySpan{byte}, int, int, Span{char}, out int, out RmQRCodeDecodeInfo)"/> reports.</param>
    /// <returns><c>true</c> when the rMQR code decoded.</returns>
    /// <exception cref="ArgumentException">Thrown when the buffer is smaller than the dimensions require.</exception>
    public static bool TryDecodeBytes(ReadOnlySpan<byte> modules, int width, int height, Span<byte> destination, out int bytesWritten, out RmQRCodeDecodeInfo info)
    {
        // No symbol holds more bytes than the largest version, so a longer destination needs no longer buffer.
        var room = Math.Min(destination.Length, RmQRMatrixDecoder.GetMaxCharCount(RmQRVersion.R17x139));
        var rentedChars = ArrayPool<char>.Shared.Rent(room);
        try
        {
            var chars = rentedChars.AsSpan(0, room);
            bool success;
            using (PayloadOutput.Bytes())
                success = TryDecode(modules, width, height, chars, out bytesWritten, out info);
            PayloadOutput.Narrow(chars.Slice(0, bytesWritten), destination);
            return success;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rentedChars, clearArray: false);
        }
    }

    /// <summary>
    /// Finds an rMQR code in a grayscale image and decodes its bytes, as <see cref="TryDecodeBytes(RmQRCodeData, out byte[], out RmQRCodeDecodeInfo)"/> reads them.
    /// </summary>
    /// <remarks>
    /// The image is searched as <see cref="TryDecodeImage(ReadOnlySpan{byte}, int, int, out string, out RmQRCodeDecodeInfo)"/> searches it, but the two calls do not judge every symbol's content alike.
    /// The bytes calls read symbols the text calls refuse, such as one under an assignment with no charset here, and refuse one whose bytes fall under two assignments, so in an image of several symbols the two calls can return different ones.
    /// </remarks>
    /// <param name="luminance">Grayscale pixels (0 = black, 255 = white), flat row-major order, width × height bytes. Transparent source pixels must be composited against white before conversion.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="bytes">The decoded bytes, or an empty array when decoding fails.</param>
    /// <param name="info">What the attempt to read the bytes found, in the fields <see cref="TryDecodeImage(ReadOnlySpan{byte}, int, int, out string, out RmQRCodeDecodeInfo)"/> reports.</param>
    /// <returns><c>true</c> when an rMQR code was found and decoded.</returns>
    /// <exception cref="ArgumentException">Thrown when the buffer is smaller than the dimensions require.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    public static bool TryDecodeImageBytes(ReadOnlySpan<byte> luminance, int width, int height, out byte[] bytes, out RmQRCodeDecodeInfo info)
    {
        var maxBytes = RmQRMatrixDecoder.GetMaxCharCount(RmQRVersion.R17x139);
        var rentedChars = ArrayPool<char>.Shared.Rent(maxBytes);
        try
        {
            var chars = rentedChars.AsSpan(0, maxBytes);
            bool success;
            int written;
            using (PayloadOutput.Bytes())
                success = TryDecodeImage(luminance, width, height, chars, out written, out info);
            bytes = success ? PayloadOutput.ToArray(chars.Slice(0, written)) : [];
            return success;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rentedChars, clearArray: false);
        }
    }

    /// <summary>
    /// Finds an rMQR code in a grayscale image and decodes its bytes into the buffer you provide, as <see cref="TryDecodeImageBytes(ReadOnlySpan{byte}, int, int, out byte[], out RmQRCodeDecodeInfo)"/> does.
    /// </summary>
    /// <param name="luminance">Grayscale pixels (0 = black, 255 = white), flat row-major order, width × height bytes. Transparent source pixels must be composited against white before conversion.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="destination">Destination buffer for the decoded bytes. <see cref="GetMaxDecodedLength"/> sizes it for bytes too.</param>
    /// <param name="bytesWritten">How many bytes were written. 0 when the image does not decode, and <paramref name="destination"/> is then left as it was.</param>
    /// <param name="info">What the attempt to read the bytes found, in the fields <see cref="TryDecodeImage(ReadOnlySpan{byte}, int, int, Span{char}, out int, out RmQRCodeDecodeInfo)"/> reports.</param>
    /// <returns><c>true</c> when an rMQR code was found and decoded.</returns>
    /// <exception cref="ArgumentException">Thrown when the buffer is smaller than the dimensions require.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    public static bool TryDecodeImageBytes(ReadOnlySpan<byte> luminance, int width, int height, Span<byte> destination, out int bytesWritten, out RmQRCodeDecodeInfo info)
    {
        // No symbol holds more bytes than the largest version, so a longer destination needs no longer buffer.
        var room = Math.Min(destination.Length, RmQRMatrixDecoder.GetMaxCharCount(RmQRVersion.R17x139));
        var rentedChars = ArrayPool<char>.Shared.Rent(room);
        try
        {
            var chars = rentedChars.AsSpan(0, room);
            bool success;
            using (PayloadOutput.Bytes())
                success = TryDecodeImage(luminance, width, height, chars, out bytesWritten, out info);
            PayloadOutput.Narrow(chars.Slice(0, bytesWritten), destination);
            return success;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rentedChars, clearArray: false);
        }
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
    /// Locates the core inside an input that may carry a light border. rMQR has dark modules at all four core corners (finder top-left, corner pattern top-right and bottom-left, sub-finder bottom-right) and timing patterns on every edge, so the dark bounding box IS the core. The border need not be uniform.
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
