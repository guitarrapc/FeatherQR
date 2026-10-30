namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// The hits of the strided rows of an image's four full-size readings, kept by one read for another that scans the same readings: the global threshold of each polarity and the regional binarization of each.
/// </summary>
/// <remarks>
/// <para>
/// The Standard QR read for every symbol and the single-finder scan read the same four binarizations at the same thresholds, and each begins a reading with a strided scan of its rows and may sweep every row after it. The two stride differently, so neither's list is the other's, but a row's hits are the row's: the first keeps the hits of the rows its stride reached (<see cref="FinderPatternFinder.KeptRows"/>), and the second merges them wherever its own strided scan or sweep comes to one of those rows, scanning only the rows between. Each of its lists stays bit for bit what its own scan leaves.
/// </para>
/// <para>
/// A reading the first read did not reach, or whose rows did not fit the room, is not kept, and the second scans it itself. The rooms are the caller's, rented: a quarter of a candidate list of hits a reading, 65,536 bytes in all, under the large object heap's threshold, and the row ends of the first read's stride.
/// </para>
/// </remarks>
internal readonly ref struct SharedRowHits
{
    public const int Readings = 4;

    public const int Positive = 0;

    public const int Negative = 1;

    public const int RegionalPositive = 2;

    public const int RegionalNegative = 3;

    private readonly Span<FinderPattern> _hits;
    private readonly Span<int> _rowEnds;
    private readonly Span<int> _strides;

    /// <param name="hits">Room for <see cref="Readings"/> readings' hits, one slice each.</param>
    /// <param name="rowEnds">Room for <see cref="Readings"/> readings' row ends, one slice each.</param>
    /// <param name="strides">The stride each reading's rows were kept at, 0 where none were. Rows made over rooms a read has filled see what it kept.</param>
    public SharedRowHits(Span<FinderPattern> hits, Span<int> rowEnds, Span<int> strides)
    {
        _hits = hits;
        _rowEnds = rowEnds;
        _strides = strides;
    }

    /// <summary>Rows over rooms with nothing kept in them yet.</summary>
    public static SharedRowHits OverEmptyRooms(Span<FinderPattern> hits, Span<int> rowEnds, Span<int> strides)
    {
        strides.Clear();
        return new SharedRowHits(hits, rowEnds, strides);
    }

    /// <summary>Whether nothing is handed through this: the default value.</summary>
    public bool IsEmpty => _strides.IsEmpty;

    /// <summary>The hits one reading has room for.</summary>
    public int HitRoom => _hits.Length / Readings;

    /// <summary>The row ends one reading has room for.</summary>
    public int RowRoom => _rowEnds.Length / Readings;

    /// <summary>
    /// The rooms a reading's strided scan keeps its rows' hits and their ends in, when the reading is one of the four, not kept yet, and its <paramref name="rowCount"/> rows fit.
    /// </summary>
    public bool TryBuffers(int reading, int rowCount, out Span<FinderPattern> hits, out Span<int> rowEnds)
    {
        if (IsEmpty || reading < 0 || reading >= Readings || _strides[reading] != 0 || rowCount > RowRoom)
        {
            hits = default;
            rowEnds = default;
            return false;
        }
        hits = _hits.Slice(reading * HitRoom, HitRoom);
        rowEnds = _rowEnds.Slice(reading * RowRoom, RowRoom);
        return true;
    }

    /// <summary>Marks a reading's rows kept, scanned at <paramref name="stride"/>. Its hits and their ends are already in place.</summary>
    public void Keep(int reading, int stride) => _strides[reading] = stride;

    /// <summary>A reading's kept rows, for a scan of an image <paramref name="height"/> rows tall.</summary>
    public bool TryTake(int reading, int height, out FinderPatternFinder.KeptRows rows)
    {
        if (IsEmpty || reading < 0 || reading >= Readings || _strides[reading] == 0)
        {
            rows = default;
            return false;
        }
        var stride = _strides[reading];
        rows = new FinderPatternFinder.KeptRows(_hits.Slice(reading * HitRoom, HitRoom), _rowEnds.Slice(reading * RowRoom, FinderPatternFinder.StridedRowCount(height, stride)), stride);
        return true;
    }
}
