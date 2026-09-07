using System.Buffers;
using System.Runtime.CompilerServices;
using FeatherQR.Internals;
using FeatherQR.Internals.MicroQR;

namespace FeatherQR;

/// <summary>
/// A Micro QR code as a module matrix, ready to render, serialize or decode.
/// </summary>
/// <remarks>
/// The matrix is bit-packed and sized by version, from 11 × 11 at M1 to 17 × 17 at M4, plus the quiet zone.
/// Serialization writes a "QRX" header carrying the symbology and dimensions, then the packed modules.
/// </remarks>
public sealed class MicroQRCodeData
{
    // =====================================================================
    // Memory Layout
    // =====================================================================
    //
    // MicroQRCodeData stores CORE modules only (no quiet zone), bit-packed
    // MSB-first in flat row-major order, zero-padded to a whole byte, and the
    // packed bits ARE the serialization payload. See QRCodeData for the
    // measurements that motivate the layout.
    //
    //   bitIndex = coreRow * _baseSize + coreCol
    //   dark(coreRow, coreCol) = (_bits[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1
    //
    // Micro QR is square, so _baseSize is both the extent and the row stride.
    // Sizes are M1 = 11, M2 = 13, M3 = 15, M4 = 17 modules per side, and the
    // specification asks for a 2-module quiet zone (Standard QR uses 4).
    //
    // ┌─────────────────────────────────────────────────────────┐
    // │ Example (M2, QuietZone = 2)                             │
    // ├─────────────────────────────────────────────────────────┤
    // │ _baseSize: 13 (core modules, no quiet zone)             │
    // │ _quietZoneSize: 2 (border width)                        │
    // │ _size: 17 (13 + 2*2, including quiet zone)              │
    // │ _bits.Length: 22 bytes (ceil(13 * 13 / 8))              │
    // │   (byte-per-module over the same 17×17: 289 bytes)      │
    // └─────────────────────────────────────────────────────────┘
    //
    // The quiet zone is VIRTUAL: it is all-light by definition, so the public
    // indexer answers false outside the core area instead of storing border
    // modules. _size/_quietZoneSize only affect coordinate translation.
    //
    // Visual Representation (17×17 with QuietZone=2):
    //
    //     0   1   2   3 ... 13  14  15  16
    //   ┌───┬───┬───┬───┬───┬───┬───┬───┬───┐
    // 0 │ Q │ Q │ Q │ Q │...│ Q │ Q │ Q │ Q │ ← QuietZone (row 0-1)
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┤
    // 1 │ Q │ Q │ Q │ Q │...│ Q │ Q │ Q │ Q │
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┤
    // 2 │ Q │ Q │ C │ C │...│ C │ C │ Q │ Q │ ← Core starts (row/col 2-14)
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┤
    //   │...│...│...│...│...│...│...│...│...│
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┤
    // 14│ Q │ Q │ C │ C │...│ C │ C │ Q │ Q │ ← Core ends
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┤
    // 15│ Q │ Q │ Q │ Q │...│ Q │ Q │ Q │ Q │ ← QuietZone (row 15-16)
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┤
    // 16│ Q │ Q │ Q │ Q │...│ Q │ Q │ Q │ Q │
    //   └───┴───┴───┴───┴───┴───┴───┴───┴───┘
    //
    // Q = QuietZone (VIRTUAL — not stored, indexer returns false)
    // C = Core modules (stored bit-packed in _bits)
    //
    // ┌─────────────────────────────────────────────────────────┐
    // │ Bit Mapping (core only, row-major, MSB-first)           │
    // ├─────────────────────────────────────────────────────────┤
    // │ coreRow  = row - _quietZoneSize                         │
    // │ coreCol  = col - _quietZoneSize                         │
    // │   (outside 0.._baseSize-1 → quiet zone → false)         │
    // │ bitIndex = coreRow × _baseSize + coreCol                │
    // │                                                         │
    // │ Example: Access (row=4, col=5) at M2 with QuietZone=2   │
    // │   → coreRow = 2, coreCol = 3                            │
    // │   → bitIndex = 2 × 13 + 3 = 29                          │
    // │   → _bits[3], bit 2 (= 7 - (29 & 7))                    │
    // └─────────────────────────────────────────────────────────┘
    //
    // =====================================================================
    // Serialization / Deserialization
    // =====================================================================
    //
    // The "QRX" container names the symbology and both dimensions, so one
    // reader can tell a Micro QR payload from an rMQR one. Standard QR keeps
    // its own 4-byte "QRR" header, which carries a single size byte.
    //
    // ┌──────────────────────────────────────────────────────────┐
    // │ Serialization (GetRawData)                               │
    // ├──────────────────────────────────────────────────────────┤
    // │ _bits (already the packed payload)                       │
    // │   ↓ copy                                                 │
    // │ rawData ("QRX" + type + width + height + _bits)          │
    // │           3B     1B      1B       1B                     │
    // │ width == height == _baseSize, Micro QR being square      │
    // └──────────────────────────────────────────────────────────┘
    //
    // ┌──────────────────────────────────────────────────────────┐
    // │ Deserialization (Constructor)                            │
    // ├──────────────────────────────────────────────────────────┤
    // │ rawData ("QRX" + type + width + height + packed bits)    │
    // │   ↓ copy (padding bits masked to zero)                   │
    // │ _bits                                                    │
    // └──────────────────────────────────────────────────────────┘

    private readonly byte[] _bits;
    private readonly int _baseSize;
    private readonly int _quietZoneSize;
    private readonly int _size;

    /// <summary>Side length in modules, quiet zone included.</summary>
    public int Size => _size;

    /// <summary>The Micro QR code version, M1 to M4.</summary>
    public MicroQRVersion Version { get; }

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
    /// Creates an empty matrix sized for the given version.
    /// </summary>
    /// <param name="version">The Micro QR code version.</param>
    /// <param name="quietZoneSize">Width of the light border in modules. The specification asks for 2, narrower than the 4 Standard QR uses.</param>
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
    /// Restores a Micro QR code from bytes written by <see cref="GetRawData()"/>.
    /// </summary>
    /// <remarks>
    /// The serialized form is a "QRX" header (3 bytes), the symbology (1 byte), the width and height (1 byte each), then the bit-packed modules.
    /// It holds the core modules only, so the quiet zone is chosen again here and need not match the one the code was serialized with.
    /// </remarks>
    /// <param name="rawData">The serialized Micro QR code.</param>
    /// <param name="quietZoneSize">Width of the light border in modules. 2 is the standard width.</param>
    /// <exception cref="InvalidDataException">Thrown when the data is not a serialized Micro QR code.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the data ends before the matrix is filled.</exception>
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

    /// <summary>How many bytes <see cref="GetRawData()"/> produces for this Micro QR code.</summary>
    public int GetRawDataSize() => 6 + (_baseSize * _baseSize + 7) / 8;

    /// <summary>
    /// Serializes the Micro QR code so it can be stored, sent or cached.
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
    /// Serializes the Micro QR code into a buffer writer, without allocating a byte array.
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
    /// Gets an upper bound on the number of rectangles <see cref="GetModuleRectangles"/> can return, suitable for sizing a pooled buffer for <see cref="TryGetModuleRectangles"/>.
    /// O(1), no matrix scan.
    /// </summary>
    public int GetModuleRectanglesMaxCount() => ModuleRunScanner.GetMaxRunCount(_baseSize, _baseSize);

    /// <summary>
    /// The dark modules as merged rectangles, for drawing the Micro QR code with any graphics API: SVG paths, draw calls, vector output.
    /// </summary>
    /// <returns>Rectangles that are disjoint and cover exactly the dark modules.</returns>
    /// <remarks>
    /// <para>
    /// Coordinates match the indexer: one unit is one module, the origin is the top-left corner including the quiet zone, <see cref="ModuleRect.X"/> is the column and <see cref="ModuleRect.Y"/> the row.
    /// Scale by the pixel size of one module.
    /// Consumers scale by the pixel size of one module; <see cref="Size"/> gives the total extent in modules.
    /// </para>
    /// <para>
    /// The three guarantees above are contractual, but the shape and order of the decomposition are not, and may change between versions.
    /// The decomposition shape and ordering are unspecified and may change between versions (currently maximal horizontal runs in row-major order, the same merge the built-in renderer draws).
    /// </para>
    /// </remarks>
    public ModuleRect[] GetModuleRectangles()
    {
        var view = new MicroQRMatrixView(this);
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
    /// Unpacks the core matrix into a byte-per-module buffer (0 = light, 1 = dark), the format consumed by <see cref="MicroQRCodeDecoder"/>.
    /// </summary>
    internal void GetCoreData(Span<byte> destination)
    {
        var totalModules = _baseSize * _baseSize;
        if (destination.Length < totalModules)
            throw new ArgumentException($"Destination span too small: required {totalModules} bytes, got {destination.Length}", nameof(destination));

        ModuleBitPacker.Unpack(_bits, destination.Slice(0, totalModules));
    }

    /// <summary>
    /// Packs a byte-per-module core matrix (0 = light, non-zero = dark) into the internal bit representation.
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
