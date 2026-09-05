using System.Buffers;
using System.Runtime.CompilerServices;
using FeatherQR.Internals;
using FeatherQR.Internals.RmQR;

namespace FeatherQR;

/// <summary>
/// Represents rMQR code data as a 2D boolean matrix (versions R7x43-R17x139,
/// rectangular: 7-17 modules high, 27-139 modules wide).
/// </summary>
/// <remarks>
/// Storage mirrors <see cref="MicroQRCodeData"/>: core modules only (no quiet
/// zone), bit-packed MSB-first in flat row-major order; the quiet zone is virtual
/// and always reads light. Serialization uses the "QRX" container:
/// <c>"QRX" + symbol type (1 byte, 2 = rMQR) + width (1 byte) + height (1 byte) + packed core bits</c>.
/// Micro QR (symbol type 1) and the legacy Standard QR "QRR" streams are rejected.
/// </remarks>
public class RmQRCodeData
{
    private readonly byte[] _bits;
    private readonly int _coreWidth;
    private readonly int _coreHeight;
    private readonly int _quietZoneSize;
    private readonly int _width;
    private readonly int _height;

    /// <summary>Gets the matrix width in modules, including the quiet zone.</summary>
    public int Width => _width;

    /// <summary>Gets the matrix height in modules, including the quiet zone.</summary>
    public int Height => _height;

    /// <summary>Gets the rMQR version (R7x43-R17x139).</summary>
    public RmQRVersion Version { get; }

    /// <summary>
    /// Gets the module state at the specified position (quiet zone included).
    /// Quiet zone positions always read false.
    /// </summary>
    /// <param name="row">Row index (0-based, including quiet zone if present).</param>
    /// <param name="col">Column index (0-based, including quiet zone if present).</param>
    /// <returns>True if the module is dark, false if light.</returns>
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
    /// Initializes an empty (all light) matrix for the specified version.
    /// </summary>
    /// <param name="version">rMQR version (R7x43-R17x139).</param>
    /// <param name="quietZoneSize">
    /// Quiet zone width in modules. The rMQR specification requires a quiet zone
    /// of 2 modules on every side (narrower than Standard QR's 4).
    /// </param>
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
    /// Deserializes rMQR data previously produced by <see cref="GetRawData()"/>.
    /// </summary>
    /// <param name="rawData">The serialized "QRX" data.</param>
    /// <param name="quietZoneSize">Quiet zone width to apply; independent of the serialized data.</param>
    /// <exception cref="InvalidDataException">Thrown when the header, symbol type or dimensions are invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the payload is truncated.</exception>
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

    /// <summary>Gets the serialized size in bytes ("QRX" header + packed core bits).</summary>
    public int GetRawDataSize() => 6 + (_coreWidth * _coreHeight + 7) / 8;

    /// <summary>
    /// Serializes the core modules (quiet zone excluded) to a new byte array.
    /// </summary>
    public byte[] GetRawData()
    {
        var result = new byte[GetRawDataSize()];
        WriteRawData(result);
        return result;
    }

    /// <summary>
    /// Writes the serialized data to the specified buffer writer without
    /// intermediate allocation.
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
    /// Gets an upper bound on the number of rectangles <see cref="GetModuleRectangles"/>
    /// can return, suitable for sizing a pooled buffer for
    /// <see cref="TryGetModuleRectangles"/>. O(1), no matrix scan.
    /// </summary>
    public int GetModuleRectanglesMaxCount() => ModuleRunScanner.GetMaxRunCount(_coreWidth, _coreHeight);

    /// <summary>
    /// Gets the dark modules as merged rectangles in module coordinates, for rendering
    /// with any graphics API without SkiaSharp (SVG path data, draw calls, vector output).
    /// </summary>
    /// <returns>Rectangles that are disjoint and cover exactly the dark modules.</returns>
    /// <remarks>
    /// <para>
    /// Coordinates use the same space as the indexer (<c>this[row, col]</c>): one unit is
    /// one module, origin at the top-left including the quiet zone, <see cref="ModuleRect.X"/>
    /// is the column and <see cref="ModuleRect.Y"/> is the row. Consumers scale by the pixel
    /// size of one module; <see cref="Width"/> and <see cref="Height"/> give the total
    /// extent in modules.
    /// </para>
    /// <para>
    /// Three properties are contractual: rectangles never overlap, cover only dark modules,
    /// and cover every dark module. The decomposition shape and ordering are unspecified and
    /// may change between versions (currently maximal horizontal runs in row-major order,
    /// the same merge the built-in renderer draws).
    /// </para>
    /// </remarks>
    public ModuleRect[] GetModuleRectangles()
    {
        var view = new RmQRMatrixView(this);
        return ModuleRunScanner.ScanToArray(in view);
    }

    /// <summary>
    /// Writes the dark modules as merged rectangles into a caller-provided buffer.
    /// Same contract as <see cref="GetModuleRectangles"/> without allocations.
    /// </summary>
    /// <param name="destination">Buffer to receive the rectangles. Size it with <see cref="GetModuleRectanglesMaxCount"/>.</param>
    /// <param name="written">The number of rectangles written, or 0 when the buffer is too small.</param>
    /// <returns>True on success; false only when <paramref name="destination"/> cannot hold every rectangle.</returns>
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
    /// Unpacks the core matrix into a byte-per-module buffer (0 = light, 1 = dark),
    /// row-major over the core width, the format consumed by the matrix decoder.
    /// </summary>
    internal void GetCoreData(Span<byte> destination)
    {
        var totalModules = _coreWidth * _coreHeight;
        if (destination.Length < totalModules)
            throw new ArgumentException($"Destination span too small: required {totalModules} bytes, got {destination.Length}", nameof(destination));

        ModuleBitPacker.Unpack(_bits, destination.Slice(0, totalModules));
    }

    /// <summary>
    /// Packs a byte-per-module core matrix (0 = light, non-zero = dark; row-major
    /// over the core width) into the internal bit representation.
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
