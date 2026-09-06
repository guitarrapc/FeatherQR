using System.Buffers;
using System.Runtime.CompilerServices;
using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;

namespace FeatherQR;

/// <summary>
/// An rMQR code as a module matrix, ready to render, serialize or decode.
/// </summary>
/// <remarks>
/// The matrix is bit-packed and rectangular, 7 to 17 modules high and 27 to 139 wide depending on version, plus the quiet zone.
/// Serialization writes a "QRX" header carrying the symbology and dimensions, then the packed modules.
/// </remarks>
public sealed class RmQRCodeData
{
    private readonly byte[] _bits;
    private readonly int _coreWidth;
    private readonly int _coreHeight;
    private readonly int _quietZoneSize;
    private readonly int _width;
    private readonly int _height;

    /// <summary>Width in modules, quiet zone included.</summary>
    public int Width => _width;

    /// <summary>Height in modules, quiet zone included.</summary>
    public int Height => _height;

    /// <summary>The rMQR code version.</summary>
    public RmQRVersion Version { get; }

    /// <summary>
    /// The module at the given position.
    /// </summary>
    /// <param name="row">Row, counted from the outer edge of the quiet zone.</param>
    /// <param name="col">Column, counted from the outer edge of the quiet zone.</param>
    /// <returns><c>true</c> when the module is dark.</returns>
    /// <remarks>
    /// Quiet zone positions always read <c>false</c>: the quiet zone is light by definition and is not stored.
    /// </remarks>
    public bool this[int row, int col]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)row >= (uint)_height || (uint)col >= (uint)_width)
                throw new IndexOutOfRangeException();

            var coreRow = row - _quietZoneSize;
            var coreCol = col - _quietZoneSize;
            if ((uint)coreRow >= (uint)_coreHeight || (uint)coreCol >= (uint)_coreWidth)
                return false; // virtual quiet zone

            var bitIndex = coreRow * _coreWidth + coreCol;
            return (_bits[bitIndex >> 3] & (1 << (7 - (bitIndex & 7)))) != 0;
        }
    }

    /// <summary>
    /// Creates an empty matrix sized for the given version.
    /// </summary>
    /// <param name="version">The rMQR code version.</param>
    /// <param name="quietZoneSize">Width of the light border in modules. The specification asks for 2, narrower than the 4 Standard QR uses.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the version is not an rMQR version or the quiet zone size is out of range.</exception>
    public RmQRCodeData(RmQRVersion version, int quietZoneSize)
    {
        if (!RmQRConstants.IsValidVersion(version))
            throw new ArgumentOutOfRangeException(nameof(version), $"Invalid rMQR version: {version}");
        ValidateQuietZone(quietZoneSize);

        Version = version;
        _coreWidth = RmQRConstants.GetWidth(version);
        _coreHeight = RmQRConstants.GetHeight(version);
        _quietZoneSize = quietZoneSize;
        _width = _coreWidth + quietZoneSize * 2;
        _height = _coreHeight + quietZoneSize * 2;
        _bits = new byte[(_coreWidth * _coreHeight + 7) / 8];
    }

    /// <summary>
    /// Restores an rMQR code from bytes written by <see cref="GetRawData()"/>.
    /// </summary>
    /// <remarks>
    /// The serialized form is a "QRX" header (3 bytes), the symbology (1 byte), the width and height (1 byte each), then the bit-packed modules.
    /// It holds the core modules only, so the quiet zone is chosen again here and need not match the one the code was serialized with.
    /// </remarks>
    /// <param name="rawData">The serialized rMQR code.</param>
    /// <param name="quietZoneSize">Width of the light border in modules. 2 is the standard width.</param>
    /// <exception cref="InvalidDataException">Thrown when the data is not a serialized rMQR code.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the data ends before the matrix is filled.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the quiet zone size is out of range.</exception>
    public RmQRCodeData(byte[] rawData, int quietZoneSize) : this(rawData.AsSpan(), quietZoneSize)
    {
    }

    /// <inheritdoc cref="RmQRCodeData(byte[], int)"/>
    public RmQRCodeData(ReadOnlySpan<byte> rawData, int quietZoneSize)
    {
        ValidateQuietZone(quietZoneSize);
        if (rawData.Length < 6)
            throw new InvalidDataException($"Invalid rMQR code data: too short ({rawData.Length} bytes).");
        if (rawData[0] != 0x51 || rawData[1] != 0x52 || rawData[2] != 0x58) // "QRX"
            throw new InvalidDataException("Invalid rMQR code data: header mismatch.");
        if (rawData[3] != RmQRConstants.SymbolTypeRmQR)
            throw new InvalidDataException($"Invalid rMQR code data: unexpected symbol type {rawData[3]}.");

        int width = rawData[4];
        int height = rawData[5];
        if (!RmQRConstants.TryGetVersion(height, width, out var version))
            throw new InvalidDataException($"Invalid rMQR code size: {width}x{height} (width x height).");

        Version = version;
        _coreWidth = width;
        _coreHeight = height;
        _quietZoneSize = quietZoneSize;
        _width = width + quietZoneSize * 2;
        _height = height + quietZoneSize * 2;

        var totalBits = width * height;
        var payloadBytes = (totalBits + 7) / 8;
        if (rawData.Length - 6 < payloadBytes)
            throw new InvalidOperationException($"Insufficient data: expected {totalBits} bits, got {Math.Max(rawData.Length - 6, 0) * 8}.");

        _bits = rawData.Slice(6, payloadBytes).ToArray();

        // Canonicalize: zero the padding bits of the final byte.
        var remainder = totalBits & 7;
        if (remainder != 0)
        {
            _bits[_bits.Length - 1] &= (byte)(0xFF << (8 - remainder));
        }
    }

    private static void ValidateQuietZone(int quietZoneSize)
    {
        // Same bounds as the Micro QR data type: negative widths break the virtual
        // quiet-zone translation, and a hard cap keeps size arithmetic overflow-free.
        if (quietZoneSize < 0 || quietZoneSize > 10_000)
            throw new ArgumentOutOfRangeException(nameof(quietZoneSize), $"Quiet zone size must be 0-10000, got {quietZoneSize}");
    }

    /// <summary>How many bytes <see cref="GetRawData()"/> produces for this rMQR code.</summary>
    public int GetRawDataSize() => 6 + (_coreWidth * _coreHeight + 7) / 8;

    /// <summary>
    /// Serializes the rMQR code so it can be stored, sent or cached.
    /// </summary>
    /// <remarks>
    /// Writes a "QRX" header (3 bytes), the symbology (1 byte), the width and height (1 byte each), then the bit-packed modules.
    /// The quiet zone is not written; pick its width again when restoring.
    /// </remarks>
    public byte[] GetRawData()
    {
        var result = new byte[GetRawDataSize()];
        WriteRawData(result);
        return result;
    }

    /// <summary>
    /// Serializes the rMQR code into a buffer writer, without allocating a byte array.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    public int GetRawData(IBufferWriter<byte> writer)
    {
        var totalSize = GetRawDataSize();
        var buffer = writer.GetSpan(totalSize);
        WriteRawData(buffer);
        writer.Advance(totalSize);
        return totalSize;
    }

    private void WriteRawData(Span<byte> destination)
    {
        destination[0] = 0x51; // 'Q'
        destination[1] = 0x52; // 'R'
        destination[2] = 0x58; // 'X'
        destination[3] = RmQRConstants.SymbolTypeRmQR;
        destination[4] = (byte)_coreWidth;
        destination[5] = (byte)_coreHeight;
        _bits.CopyTo(destination.Slice(6));
    }

    /// <summary>
    /// Gets an upper bound on the number of rectangles <see cref="GetModuleRectangles"/> can return, suitable for sizing a pooled buffer for <see cref="TryGetModuleRectangles"/>.
    /// O(1), no matrix scan.
    /// </summary>
    public int GetModuleRectanglesMaxCount() => ModuleRunScanner.GetMaxRunCount(_coreWidth, _coreHeight);

    /// <summary>
    /// The dark modules as merged rectangles, for drawing the rMQR code with any graphics API: SVG paths, draw calls, vector output.
    /// </summary>
    /// <returns>Rectangles that are disjoint and cover exactly the dark modules.</returns>
    /// <remarks>
    /// <para>
    /// Coordinates match the indexer: one unit is one module, the origin is the top-left corner including the quiet zone, <see cref="ModuleRect.X"/> is the column and <see cref="ModuleRect.Y"/> the row.
    /// Scale by the pixel size of one module.
    /// Consumers scale by the pixel size of one module; <see cref="Width"/> and <see cref="Height"/> give the total extent in modules.
    /// </para>
    /// <para>
    /// The three guarantees above are contractual, but the shape and order of the decomposition are not, and may change between versions.
    /// The decomposition shape and ordering are unspecified and may change between versions (currently maximal horizontal runs in row-major order, the same merge the built-in renderer draws).
    /// </para>
    /// </remarks>
    public ModuleRect[] GetModuleRectangles()
    {
        var view = new RmQRMatrixView(this);
        return ModuleRunScanner.ScanToArray(in view);
    }

    /// <summary>
    /// Writes the rectangles of <see cref="GetModuleRectangles"/> into the buffer you provide, without allocating.
    /// </summary>
    /// <param name="destination">Buffer to receive the rectangles. Size it with <see cref="GetModuleRectanglesMaxCount"/>.</param>
    /// <param name="written">The number of rectangles written, or 0 when the buffer is too small.</param>
    /// <returns><c>false</c> only when <paramref name="destination"/> cannot hold every rectangle.</returns>
    public bool TryGetModuleRectangles(Span<ModuleRect> destination, out int written)
    {
        var view = new RmQRMatrixView(this);
        return ModuleRunScanner.TryScan(in view, destination, out written);
    }

    /// <summary>Gets the core matrix width (quiet zone excluded).</summary>
    internal int GetCoreWidth() => _coreWidth;

    /// <summary>Gets the core matrix height (quiet zone excluded).</summary>
    internal int GetCoreHeight() => _coreHeight;

    /// <summary>Reads a core module without quiet-zone translation (caller guarantees bounds).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool GetCoreModule(int coreRow, int coreCol)
    {
        var bitIndex = coreRow * _coreWidth + coreCol;
        return (_bits[bitIndex >> 3] & (1 << (7 - (bitIndex & 7)))) != 0;
    }

    /// <summary>
    /// Unpacks the core matrix into a byte-per-module buffer (0 = light, 1 = dark), row-major over the core width, the format consumed by the matrix decoder.
    /// </summary>
    internal void GetCoreData(Span<byte> destination)
    {
        var totalModules = _coreWidth * _coreHeight;
        if (destination.Length < totalModules)
            throw new ArgumentException($"Destination span too small: required {totalModules} bytes, got {destination.Length}", nameof(destination));

        ModuleBitPacker.Unpack(_bits, destination.Slice(0, totalModules));
    }

    /// <summary>
    /// Packs a byte-per-module core matrix (0 = light, non-zero = dark; row-major over the core width) into the internal bit representation.
    /// </summary>
    internal void SetCoreData(ReadOnlySpan<byte> source)
    {
        var totalModules = _coreWidth * _coreHeight;
        if (source.Length != totalModules)
            throw new ArgumentException($"Source span size mismatch: expected {totalModules} bytes ({_coreWidth}x{_coreHeight}), got {source.Length} bytes");

        // Replace, don't merge: Pack writes every packed byte (padding bits zero), so
        // repeated calls cannot leak dark modules from an earlier matrix.
        ModuleBitPacker.Pack(source, _bits);
    }
}
