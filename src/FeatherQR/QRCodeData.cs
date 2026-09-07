using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FeatherQR;

/// <summary>
/// A Standard QR code as a module matrix, ready to render, serialize or decode.
/// </summary>
/// <remarks>
/// The matrix is bit-packed and sized by version, from 21 × 21 at version 1 to 177 × 177 at version 40, plus the quiet zone.
/// Serialization writes a "QRR" header, the size, then the packed modules.
/// </remarks>
public sealed class QRCodeData
{
    // =====================================================================
    // Memory Layout
    // =====================================================================
    //
    // QRCodeData stores CORE modules only (no quiet zone), bit-packed in the
    // exact layout of the "QRR" serialization payload: a continuous MSB-first
    // bit stream in flat row-major module order, zero-padded to a whole byte.
    //
    //   bitIndex = coreRow * _baseSize + coreCol
    //   dark(coreRow, coreCol) = (_bits[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1
    //
    // ┌─────────────────────────────────────────────────────────┐
    // │ Example (Version 2, QuietZone = 4)                      │
    // ├─────────────────────────────────────────────────────────┤
    // │ _baseSize: 25 (core QR modules, no quiet zone)          │
    // │ _quietZoneSize: 4 (border width)                        │
    // │ _size: 33 (25 + 4*2, including quiet zone)              │
    // │ _bits.Length: 79 bytes (ceil(25 * 25 / 8))              │
    // │   (previous byte-per-module layout: 1,089 bytes)        │
    // └─────────────────────────────────────────────────────────┘
    //
    // The quiet zone is VIRTUAL: it is all-light by definition, so the public
    // indexer answers false for coordinates outside the core area instead of
    // storing border modules. _size/_quietZoneSize only affect coordinate
    // translation. The COORDINATE SPACE seen through the public indexer is
    // unchanged from the byte-per-module days:
    //
    // Visual Representation (33×33 with QuietZone=4):
    //
    //     0   1   2   3   4   5 ... 28  29  30  31  32
    //   ┌───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┐
    // 0 │ Q │ Q │ Q │ Q │ Q │ Q │...│ Q │ Q │ Q │ Q │ Q │ ← QuietZone (row 0-3)
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┤
    // 1 │ Q │ Q │ Q │ Q │ Q │ Q │...│ Q │ Q │ Q │ Q │ Q │
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┤
    // 2 │ Q │ Q │ Q │ Q │ Q │ Q │...│ Q │ Q │ Q │ Q │ Q │
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┤
    // 3 │ Q │ Q │ Q │ Q │ Q │ Q │...│ Q │ Q │ Q │ Q │ Q │
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┤
    // 4 │ Q │ Q │ Q │ Q │ C │ C │...│ C │ Q │ Q │ Q │ Q │ ← Core starts (col 4-28)
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┤
    // 5 │ Q │ Q │ Q │ Q │ C │ C │...│ C │ Q │ Q │ Q │ Q │
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┤
    //   │...│...│...│...│...│...│...│...│...│...│...│...│
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┤
    // 28│ Q │ Q │ Q │ Q │ C │ C │...│ C │ Q │ Q │ Q │ Q │ ← Core ends
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┤
    // 29│ Q │ Q │ Q │ Q │ Q │ Q │...│ Q │ Q │ Q │ Q │ Q │ ← QuietZone (row 29-32)
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┤
    // 30│ Q │ Q │ Q │ Q │ Q │ Q │...│ Q │ Q │ Q │ Q │ Q │
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┤
    // 31│ Q │ Q │ Q │ Q │ Q │ Q │...│ Q │ Q │ Q │ Q │ Q │
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┼───┤
    // 32│ Q │ Q │ Q │ Q │ Q │ Q │...│ Q │ Q │ Q │ Q │ Q │
    //   └───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┘
    //     ↑               ↑                       ↑       ↑
    //   col 0           col 4                   col 28  col 32
    //
    // Q = QuietZone (white border, VIRTUAL — not stored, indexer returns false)
    // C = Core modules (stored bit-packed in _bits)
    //
    // ┌─────────────────────────────────────────────────────────┐
    // │ Bit Mapping (core only, row-major, MSB-first)           │
    // ├─────────────────────────────────────────────────────────┤
    // │ coreRow  = row - _quietZoneSize                         │
    // │ coreCol  = col - _quietZoneSize                         │
    // │   (outside 0..baseSize-1 → quiet zone → false)          │
    // │ bitIndex = coreRow × _baseSize + coreCol                │
    // │ dark     = (_bits[bitIndex >> 3] >> (7 - (bitIndex &    │
    // │             7))) & 1                                    │
    // │                                                         │
    // │ Example: Access (row=5, col=6) with QuietZone=4         │
    // │   → coreRow = 1, coreCol = 2                            │
    // │   → bitIndex = 1 × 25 + 2 = 27                          │
    // │   → _bits[3], bit 4 (= 7 - (27 & 7))                    │
    // └─────────────────────────────────────────────────────────┘
    //
    // ┌─────────────────────────────────────────────────────────┐
    // │ Memory Layout (without QuietZone)                       │
    // ├─────────────────────────────────────────────────────────┤
    // │ Version: 2                                              │
    // │ _baseSize: 25                                           │
    // │ _quietZoneSize: 0                                       │
    // │ _size: 25 (same as _baseSize)                           │
    // │ _bits.Length: 79 bytes — IDENTICAL to the QuietZone=4   │
    // │ case: only core modules are stored, so the quiet zone   │
    // │ costs no memory and only changes coordinate translation │
    // └─────────────────────────────────────────────────────────┘
    //
    //     0   1   2   3   4 ... 20  21  22  23  24
    //   ┌───┬───┬───┬───┬───┬───┬───┬───┬───┬───┐
    // 0 │ C │ C │ C │ C │ C │...│ C │ C │ C │ C │ ← Core only
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┼───┤
    // 1 │ C │ C │ C │ C │ C │...│ C │ C │ C │ C │
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┼───┤
    //   │...│...│...│...│...│...│...│...│...│...│
    //   ├───┼───┼───┼───┼───┼───┼───┼───┼───┼───┤
    // 24│ C │ C │ C │ C │ C │...│ C │ C │ C │ C │
    //   └───┴───┴───┴───┴───┴───┴───┴───┴───┴───┘
    //
    // When _quietZoneSize = 0:
    //   - _size == _baseSize, no coordinate translation
    //   - bitIndex = row × _size + col directly
    //
    // Why this layout (measured in the MatrixStorage kernel benchmark):
    // - 8.7x smaller per-instance allocation (34,225 -> 3,944 bytes at v40/qz4),
    //   which was essentially 100% of Create's allocation.
    // - _bits IS the serialization payload, so GetRawData collapses to
    //   header + copy (~800x) and deserialization to the mirror copy, the
    //   previous implementation round-tripped through a byte-per-module
    //   temporary (a ~31 KB stackalloc at v40).
    // - The cost is one shift+mask per indexer read (~+45% on a raw
    //   full-matrix sweep), negligible under real rendering work.
    //
    // =====================================================================
    // Serialization / Deserialization
    // =====================================================================
    //
    // ┌──────────────────────────────────────────────────┐
    // │ Serialization (GetRawData)                       │
    // ├──────────────────────────────────────────────────┤
    // │ _bits (already the packed payload)               │
    // │   ↓ copy                                         │
    // │ rawData ("QRR" + size byte + _bits)              │
    // └──────────────────────────────────────────────────┘
    //
    // ┌──────────────────────────────────────────────────┐
    // │ Deserialization (Constructor)                    │
    // ├──────────────────────────────────────────────────┤
    // │ rawData ("QRR" + size byte + packed core bits)   │
    // │   ↓ copy (padding bits masked to zero)           │
    // │ _bits                                            │
    // └──────────────────────────────────────────────────┘

    // Core modules, bit-packed as the QRR payload (MSB-first, flat row-major,
    // zero-padded tail). See the layout notes above.
    private byte[] _bits;
    private int _size; // module count per side (including quiet zone if present)
    private int _baseSize; // module count without quiet zone
    private int _quietZoneSize; // quiet zone size in modules

    /// <summary>
    /// Side length in modules, quiet zone included.
    /// </summary>
    public int Size => _size;

    /// <summary>
    /// The QR code version, 1 to 40.
    /// </summary>
    public int Version { get; private set; }

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
        internal set
        {
            var coreRow = row - _quietZoneSize;
            var coreCol = col - _quietZoneSize;
            if ((uint)coreRow >= (uint)_baseSize || (uint)coreCol >= (uint)_baseSize)
                throw new ArgumentOutOfRangeException(nameof(row), $"Position ({row}, {col}) is outside the core area; quiet zone modules are not writable.");

            var bitIndex = coreRow * _baseSize + coreCol;
            var mask = (byte)(1 << (7 - (bitIndex & 7)));
            if (value)
            {
                _bits[bitIndex >> 3] |= mask;
            }
            else
            {
                _bits[bitIndex >> 3] &= (byte)~mask;
            }
        }
    }

    /// <summary>
    /// Creates an empty matrix sized for the given version.
    /// </summary>
    /// <param name="version">The QR code version, 1 to 40.</param>
    /// <param name="quietZoneSize">Width of the light border in modules. The standard asks for 4; 0 leaves the QR code hard to scan.</param>
    public QRCodeData(int version, int quietZoneSize)
    {
        Version = version;
        _baseSize = SizeFromVersion(version);
        _quietZoneSize = quietZoneSize;
        _size = _baseSize + (quietZoneSize * 2);
        _bits = new byte[PayloadBytes(_baseSize)];
    }

    /// <summary>
    /// Restores a QR code from bytes written by <see cref="GetRawData()"/>.
    /// </summary>
    /// <remarks>
    /// The serialized form is a "QRR" header (3 bytes), the base size (1 byte), then the bit-packed modules.
    /// It holds the core modules only, so the quiet zone is chosen again here and need not match the one the code was serialized with.
    /// </remarks>
    /// <param name="rawData">The serialized QR code.</param>
    /// <param name="quietZoneSize">Width of the light border in modules. 4 is the standard width.</param>
    /// <exception cref="InvalidDataException">Thrown when the data is not a serialized QR code.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the data ends before the matrix is filled.</exception>
    public QRCodeData(byte[] rawData, int quietZoneSize) : this(rawData.AsSpan(), quietZoneSize)
    {
    }

    /// <summary>
    /// Restores a QR code from bytes written by <see cref="GetRawData()"/>, without copying them into an array first.
    /// </summary>
    /// <remarks>
    /// The serialized form is a "QRR" header (3 bytes), the base size (1 byte), then the bit-packed modules.
    /// It holds the core modules only, so the quiet zone is chosen again here and need not match the one the code was serialized with.
    /// </remarks>
    /// <param name="rawDataSpan">The serialized QR code.</param>
    /// <param name="quietZoneSize">Width of the light border in modules. 4 is the standard width.</param>
    /// <exception cref="InvalidDataException">Thrown when the data is not a serialized QR code.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the data ends before the matrix is filled.</exception>
    public QRCodeData(ReadOnlySpan<byte> rawDataSpan, int quietZoneSize)
    {
        // Validate minimum size
        if (rawDataSpan.Length < 4)
            throw new InvalidDataException($"Invalid QR code data: too short ({rawDataSpan.Length} bytes).");

        // Validate header
        if (rawDataSpan[0] != 0x51 || rawDataSpan[1] != 0x52 || rawDataSpan[2] != 0x52)
            throw new InvalidDataException("Invalid QR code data: header mismatch.");

        // Read and validate size
        var baseSizeFromFile = (int)rawDataSpan[3];
        if (baseSizeFromFile < 21 || baseSizeFromFile > 177)
            throw new InvalidDataException($"Invalid QR code size: {baseSizeFromFile}.");

        // set version from size
        Version = VersionFromSize(baseSizeFromFile);
        _baseSize = baseSizeFromFile;
        _quietZoneSize = quietZoneSize;
        _size = _baseSize + (_quietZoneSize * 2);
        var coreTotalBits = _baseSize * _baseSize; // core data size (without quiet zone)

        // The payload IS the internal representation, copy it directly.
        var payloadBytes = PayloadBytes(_baseSize);
        var available = rawDataSpan.Length - 4;
        if (available < payloadBytes)
            throw new InvalidOperationException($"Insufficient data: expected {coreTotalBits} bits, got {Math.Max(available, 0) * 8}.");

        _bits = rawDataSpan.Slice(4, payloadBytes).ToArray();

        // Mask the padding bits of the final byte to zero so instances are
        // canonical regardless of input tail garbage (the serializer always
        // writes zero padding).
        var remainder = coreTotalBits & 7;
        if (remainder != 0)
        {
            _bits[^1] &= (byte)(0xFF << (8 - remainder));
        }
    }

    /// <summary>
    /// How many bytes <see cref="GetRawData()"/> produces for this QR code.
    /// </summary>
    public int GetRawDataSize()
    {
        var totalBits = _baseSize * _baseSize; // only core data without quiet zone
        var paddingBits = GetPaddingBits(totalBits);
        var dataBytes = (totalBits + paddingBits) / 8;
        var headerSize = 3 + 1; // signature length + 1 byte for size
        var totalSize = headerSize + dataBytes;
        return totalSize;
    }

    /// <summary>
    /// Serializes the QR code so it can be stored, sent or cached.
    /// </summary>
    /// <remarks>
    /// Writes a "QRR" header (3 bytes), the base size (1 byte), then the bit-packed modules.
    /// The quiet zone is not written; pick its width again when restoring through <see cref="QRCodeData(byte[], int)"/>.
    /// </remarks>
    public byte[] GetRawData()
    {
        var result = new byte[GetRawDataSize()];
        WriteRawData(result);
        return result;
    }

    /// <summary>
    /// Serializes the QR code into a buffer writer, without allocating a byte array.
    /// </summary>
    /// <param name="writer">Where to write the bytes.</param>
    /// <returns>The number of bytes written.</returns>
    /// <remarks>
    /// The quiet zone is not written; pick its width again when restoring.
    /// </remarks>
    public int GetRawData(IBufferWriter<byte> writer)
    {
        var totalSize = GetRawDataSize();
        var buffer = writer.GetSpan(totalSize);
        WriteRawData(buffer);
        writer.Advance(totalSize);
        return totalSize;
    }

    /// <summary>
    /// Writes the "QRR" header and the payload.
    /// The internal representation is already the serialized payload, so this is a header write plus one copy.
    /// </summary>
    private void WriteRawData(Span<byte> destination)
    {
        destination[0] = 0x51; // 'Q'
        destination[1] = 0x52; // 'R'
        destination[2] = 0x52; // 'R'
        destination[3] = (byte)_baseSize; // only core size without quiet zone
        _bits.CopyTo(destination.Slice(4));
    }

    /// <summary>
    /// Tells whether a module belongs to one of the three finder patterns, so a renderer can style them apart.
    /// </summary>
    /// <param name="row">Row, within the code itself, with the quiet zone excluded.</param>
    /// <param name="col">Column, within the code itself, with the quiet zone excluded.</param>
    /// <returns><c>true</c> when the module is inside a finder pattern.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsFinderPattern(int row, int col)
    {
        var coreSize = GetCoreSize();

        // Early exit for most common case (middle area)
        if (row >= 7 && row < coreSize - 7 && col >= 7 && col < coreSize - 7)
            return false;

        // Top-left (0,0 to 6,6)
        if (row < 7 && col < 7) return true;

        // Top-right (0, coreSize-7 to coreSize-1, coreSize-1)
        if (row < 7 && col >= coreSize - 7) return true;

        // Bottom-left (coreSize-7, 0 to coreSize-1,6)
        if (row >= coreSize - 7 && col < 7) return true;

        return false;
    }

    /// <summary>
    /// Tells which finder pattern a module belongs to, so each corner can be styled separately.
    /// </summary>
    /// <param name="row">Row, within the code itself, with the quiet zone excluded.</param>
    /// <param name="col">Column, within the code itself, with the quiet zone excluded.</param>
    /// <returns>0 for top-left, 1 for top-right, 2 for bottom-left, or -1 when the module is outside all three.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetFinderPatternIndex(int row, int col)
    {
        var coreSize = GetCoreSize();

        // Top-left (0,0 to 6,6)
        if (row < 7 && col < 7) return 0;

        // Top-right (0, coreSize-7 to coreSize-1, coreSize-1)
        if (row < 7 && col >= coreSize - 7) return 1;

        // Bottom-left (coreSize-7, 0 to coreSize-1,6)
        if (row >= coreSize - 7 && col < 7) return 2;

        return -1;
    }

    /// <summary>
    /// Gets an upper bound on the number of rectangles <see cref="GetModuleRectangles"/> can return, suitable for sizing a pooled buffer for <see cref="TryGetModuleRectangles"/>.
    /// O(1), no matrix scan.
    /// </summary>
    public int GetModuleRectanglesMaxCount() => Internals.ModuleRunScanner.GetMaxRunCount(_baseSize, _baseSize);

    /// <summary>
    /// The dark modules as merged rectangles, for drawing the QR code with any graphics API: SVG paths, draw calls, vector output.
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
        var view = new Internals.StandardQrMatrixView(this);
        return Internals.ModuleRunScanner.ScanToArray(in view);
    }

    /// <summary>
    /// Writes the rectangles of <see cref="GetModuleRectangles"/> into the buffer you provide, without allocating.
    /// </summary>
    /// <param name="destination">Buffer to receive the rectangles. Size it with <see cref="GetModuleRectanglesMaxCount"/>.</param>
    /// <param name="written">The number of rectangles written, or 0 when the buffer is too small.</param>
    /// <returns><c>false</c> only when <paramref name="destination"/> cannot hold every rectangle.</returns>
    public bool TryGetModuleRectangles(Span<ModuleRect> destination, out int written)
    {
        var view = new Internals.StandardQrMatrixView(this);
        return Internals.ModuleRunScanner.TryScan(in view, destination, out written);
    }

    /// <summary>
    /// Gets the core data size (without quiet zone).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetCoreSize() => _baseSize;

    /// <summary>
    /// Gets the module state at the specified core position (excluding quiet zone), skipping the quiet-zone coordinate translation of the public indexer.
    /// Caller must guarantee 0 &lt;= coreRow/coreCol &lt; core size.
    /// </summary>
    /// <param name="coreRow">Row index in core data (0-based, excluding quiet zone)</param>
    /// <param name="coreCol">Column index in core data (0-based, excluding quiet zone)</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool GetCoreModule(int coreRow, int coreCol)
    {
        var bitIndex = coreRow * _baseSize + coreCol;
        return (_bits[bitIndex >> 3] & (1 << (7 - (bitIndex & 7)))) != 0;
    }

    /// <summary>
    /// Copies core data (without quiet zone) to the destination buffer as one byte (0/1) per module.
    /// </summary>
    /// <param name="destination">Destination buffer (must be at least baseSize * baseSize bytes)</param>
    /// <exception cref="ArgumentException"></exception>
    internal void GetCoreData(Span<byte> destination)
    {
        var totalModules = _baseSize * _baseSize;
        if (destination.Length < totalModules)
            throw new ArgumentException($"Destination span size too small: expected at least {totalModules} bytes (baseSize={_baseSize}), got {destination.Length} bytes");

        // Unpack 8 modules per step: replicate the payload byte across a ulong,
        // isolate each bit on its byte's diagonal, then OR-cascade down to bit 0
        // so byte k of the ulong becomes module bit (7-k) as 0/1. The gather
        // constants assume little-endian byte order (ulong byte k = memory
        // offset k); reverse on big-endian targets, the check is a JIT-time
        // constant, so little-endian codegen is unaffected.
        ref var destRef = ref MemoryMarshal.GetReference(destination);
        var m = 0;
        for (; m + 8 <= totalModules; m += 8)
        {
            var spread = (_bits[m >> 3] * 0x0101010101010101UL) & 0x0102040810204080UL;
            spread |= spread >> 4;
            spread |= spread >> 2;
            spread |= spread >> 1;
            spread &= 0x0101010101010101UL;
            if (!BitConverter.IsLittleEndian)
            {
                spread = BinaryPrimitives.ReverseEndianness(spread);
            }
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destRef, m), spread);
        }
        for (; m < totalModules; m++)
        {
            destination[m] = (byte)((_bits[m >> 3] >> (7 - (m & 7))) & 1);
        }
    }

    /// <summary>
    /// Sets the core data (without quiet zone) from a one-byte-per-module (0/1) source buffer.
    /// </summary>
    /// <param name="source">Source buffer (must be exactly baseSize * baseSize bytes)</param>
    /// <exception cref="ArgumentException"></exception>
    internal void SetCoreData(ReadOnlySpan<byte> source)
    {
        var totalModules = _baseSize * _baseSize;
        if (source.Length != totalModules)
            throw new ArgumentException($"Source span size mismatch: expected {totalModules} bytes (baseSize={_baseSize}), got {source.Length} bytes");

        // The payload bit stream is flat row-major module order, the same
        // order as the source buffer, so pack 8 modules per byte via the
        // MSB multiply-gather (byte k of a ulong of 0/1 bytes lands at bit
        // 56+(7-k) after * 0x8040...01). The gather constant assumes
        // little-endian byte order (source[m+k] = ulong byte k); reverse on
        // big-endian targets, the check is a JIT-time constant, so
        // little-endian codegen is unaffected.
        ref var srcRef = ref MemoryMarshal.GetReference(source);
        var m = 0;
        for (; m + 8 <= totalModules; m += 8)
        {
            var u = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref srcRef, m));
            if (!BitConverter.IsLittleEndian)
            {
                u = BinaryPrimitives.ReverseEndianness(u);
            }
            _bits[m >> 3] = (byte)((u * 0x8040201008040201UL) >> 56);
        }
        if (m < totalModules)
        {
            byte b = 0;
            for (var i = 0; m + i < totalModules; i++)
            {
                if (source[m + i] != 0) b |= (byte)(1 << (7 - i));
            }
            _bits[m >> 3] = b;
        }
    }

    /// <summary>Serialized payload length in bytes for a core of the given size (bits rounded up to whole bytes).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PayloadBytes(int baseSize)
    {
        var totalBits = baseSize * baseSize;
        return (totalBits + GetPaddingBits(totalBits)) / 8;
    }

    /// <summary>
    /// Calculates the number of modules per side (excluding quiet zone) for a given QR code version.
    /// </summary>
    /// <param name="version">QR code version (must be in the range 1-40)</param>
    /// <returns>The number of modules per side for the specified QR code version.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SizeFromVersion(int version) => 21 + (version - 1) * 4;

    /// <summary>
    /// Calculate version from size (without quiet zone) Formula: size = 21 + (version - 1) * 4 Inverse: version = (size - 21) / 4 + 1
    /// </summary>
    /// <param name="sizeWithoutQuietZone"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int VersionFromSize(int sizeWithoutQuietZone) => (sizeWithoutQuietZone - 21) / 4 + 1;

    /// <summary>
    /// Calculate number of padding bits needed to align totalBits to next byte boundary.
    /// </summary>
    /// <param name="totalBits"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetPaddingBits(int totalBits)
    {
        var remainder = totalBits % 8;
        return (8 - remainder) % 8;
    }
}
