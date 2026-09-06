using System.Buffers;
using System.Runtime.CompilerServices;
using FeatherQR.Internals;
using FeatherQR.Internals.MicroQR;

namespace FeatherQR;

/// <summary>
/// Represents Micro QR code data as a 2D boolean matrix (versions M1-M4,
/// 11×11 to 17×17 modules).
/// </summary>
/// <remarks>
/// Storage mirrors <see cref="QRCodeData"/>: core modules only (no quiet zone),
/// bit-packed MSB-first in flat row-major order; the quiet zone is virtual and
/// always reads light. Serialization uses the "QRX" container:
/// <c>"QRX" + symbol type (1 byte) + width (1 byte) + height (1 byte) + packed core bits</c>.
/// The legacy "QRR" format remains exclusive to Standard QR.
/// </remarks>
public sealed class MicroQRCodeData
{
    private readonly byte[] _bits;
    private readonly int _baseSize;
    private readonly int _quietZoneSize;
    private readonly int _size;

    /// <summary>Gets the matrix side length in modules, including the quiet zone.</summary>
    public int Size => _size;

    /// <summary>Gets the Micro QR version (M1-M4).</summary>
    public MicroQRVersion Version { get; }

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
            if ((uint)row >= (uint)_size || (uint)col >= (uint)_size)
                throw new IndexOutOfRangeException();

            var coreRow = row - _quietZoneSize;
            var coreCol = col - _quietZoneSize;
            if ((uint)coreRow >= (uint)_baseSize || (uint)coreCol >= (uint)_baseSize)
                return false; // virtual quiet zone

            var bitIndex = coreRow * _baseSize + coreCol;
            return (_bits[bitIndex >> 3] & (1 << (7 - (bitIndex & 7)))) != 0;
        }
    }

    /// <summary>
    /// Initializes an empty matrix for the specified version.
    /// </summary>
    /// <param name="version">Micro QR version (M1-M4).</param>
    /// <param name="quietZoneSize">
    /// Quiet zone width in modules. The Micro QR specification requires a quiet
    /// zone of 2 modules (narrower than Standard QR's 4).
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the version is not M1-M4 or the quiet zone size is out of range.</exception>
    public MicroQRCodeData(MicroQRVersion version, int quietZoneSize)
    {
        if ((uint)((int)version - 1) > 3)
            throw new ArgumentOutOfRangeException(nameof(version), $"Invalid Micro QR version: {version}");
        ValidateQuietZone(quietZoneSize);

        Version = version;
        _baseSize = MicroQRConstants.SizeFromVersion(version);
        _quietZoneSize = quietZoneSize;
        _size = _baseSize + quietZoneSize * 2;
        _bits = new byte[(_baseSize * _baseSize + 7) / 8];
    }

    /// <summary>
    /// Deserializes Micro QR data previously produced by <see cref="GetRawData()"/>.
    /// </summary>
    /// <param name="rawData">The serialized "QRX" data.</param>
    /// <param name="quietZoneSize">Quiet zone width to apply; independent of the serialized data.</param>
    /// <exception cref="InvalidDataException">Thrown when the header or dimensions are invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the payload is truncated.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the quiet zone size is out of range.</exception>
    public MicroQRCodeData(byte[] rawData, int quietZoneSize) : this(rawData.AsSpan(), quietZoneSize)
    {
    }

    /// <inheritdoc cref="MicroQRCodeData(byte[], int)"/>
    public MicroQRCodeData(ReadOnlySpan<byte> rawData, int quietZoneSize)
    {
        ValidateQuietZone(quietZoneSize);
        if (rawData.Length < 6)
            throw new InvalidDataException($"Invalid Micro QR code data: too short ({rawData.Length} bytes).");
        if (rawData[0] != 0x51 || rawData[1] != 0x52 || rawData[2] != 0x58) // "QRX"
            throw new InvalidDataException("Invalid Micro QR code data: header mismatch.");
        if (rawData[3] != MicroQRConstants.SymbolTypeMicroQR)
            throw new InvalidDataException($"Invalid Micro QR code data: unexpected symbol type {rawData[3]}.");

        int width = rawData[4];
        int height = rawData[5];
        var version = MicroQRConstants.VersionFromSize(width);
        if (width != height || version == 0)
            throw new InvalidDataException($"Invalid Micro QR code size: {width}x{height}.");

        Version = version;
        _baseSize = width;
        _quietZoneSize = quietZoneSize;
        _size = _baseSize + quietZoneSize * 2;

        var totalBits = _baseSize * _baseSize;
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
        // Same bounds as MicroQRCodeGenerator: negative widths break the virtual
        // quiet-zone translation, and a hard cap keeps size arithmetic overflow-free.
        if (quietZoneSize < 0 || quietZoneSize > 10_000)
            throw new ArgumentOutOfRangeException(nameof(quietZoneSize), $"Quiet zone size must be 0-10000, got {quietZoneSize}");
    }

    /// <summary>Gets the serialized size in bytes ("QRX" header + packed core bits).</summary>
    public int GetRawDataSize() => 6 + (_baseSize * _baseSize + 7) / 8;

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
        destination[3] = MicroQRConstants.SymbolTypeMicroQR;
        destination[4] = (byte)_baseSize;
        destination[5] = (byte)_baseSize;
        _bits.CopyTo(destination.Slice(6));
    }

    /// <summary>
    /// Gets an upper bound on the number of rectangles <see cref="GetModuleRectangles"/>
    /// can return, suitable for sizing a pooled buffer for
    /// <see cref="TryGetModuleRectangles"/>. O(1), no matrix scan.
    /// </summary>
    public int GetModuleRectanglesMaxCount() => ModuleRunScanner.GetMaxRunCount(_baseSize, _baseSize);

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
    /// size of one module; <see cref="Size"/> gives the total extent in modules.
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
        var view = new MicroQRMatrixView(this);
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
        var view = new MicroQRMatrixView(this);
        return ModuleRunScanner.TryScan(in view, destination, out written);
    }

    /// <summary>Gets the core matrix side length (quiet zone excluded).</summary>
    internal int GetCoreSize() => _baseSize;

    /// <summary>Reads a core module without quiet-zone translation (caller guarantees bounds).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool GetCoreModule(int coreRow, int coreCol)
    {
        var bitIndex = coreRow * _baseSize + coreCol;
        return (_bits[bitIndex >> 3] & (1 << (7 - (bitIndex & 7)))) != 0;
    }

    /// <summary>
    /// Unpacks the core matrix into a byte-per-module buffer (0 = light, 1 = dark),
    /// the format consumed by <see cref="MicroQRCodeDecoder"/>.
    /// </summary>
    internal void GetCoreData(Span<byte> destination)
    {
        var totalModules = _baseSize * _baseSize;
        if (destination.Length < totalModules)
            throw new ArgumentException($"Destination span too small: required {totalModules} bytes, got {destination.Length}", nameof(destination));

        ModuleBitPacker.Unpack(_bits, destination.Slice(0, totalModules));
    }

    /// <summary>
    /// Packs a byte-per-module core matrix (0 = light, non-zero = dark) into the
    /// internal bit representation.
    /// </summary>
    internal void SetCoreData(ReadOnlySpan<byte> source)
    {
        var totalModules = _baseSize * _baseSize;
        if (source.Length != totalModules)
            throw new ArgumentException($"Source span size mismatch: expected {totalModules} bytes (baseSize={_baseSize}), got {source.Length} bytes");

        // Replace, don't merge: Pack writes every packed byte (padding bits zero), so
        // repeated calls cannot leak dark modules from an earlier matrix.
        ModuleBitPacker.Pack(source, _bits);
    }
}
