namespace FeatherQR.Internals.ImageDecoders;

internal static partial class FinderPatternFinder
{
    /// <summary>
    /// The hits of the strided rows a scan kept (<see cref="FindCandidatesKeepingRowHits(ReadOnlySpan{byte}, int, int, byte, in GreyLevels, Span{FinderPattern}, out int, Span{FinderPattern}, Span{int}, int, KeptRows)"/>), for a scan of the same image to merge where its own scan of those rows would, in the order found: a row's hits do not depend on the list they go to, nor on the stride that reached the row.
    /// </summary>
    internal readonly ref struct KeptRows
    {
        private readonly ReadOnlySpan<FinderPattern> _hits;
        private readonly ReadOnlySpan<int> _rowEnds;
        private readonly int _stride;

        /// <param name="hits">The kept hits, row after row.</param>
        /// <param name="rowEnds">The end of each strided row's hits in <paramref name="hits"/>, -1 for a row not kept.</param>
        /// <param name="stride">The stride of the scan that kept them.</param>
        public KeptRows(ReadOnlySpan<FinderPattern> hits, ReadOnlySpan<int> rowEnds, int stride)
        {
            _hits = hits;
            _rowEnds = rowEnds;
            _stride = stride;
        }

        /// <summary>Whether no row was kept: the default value.</summary>
        public bool IsEmpty => _rowEnds.IsEmpty;

        public Reader Read() => new(this);

        /// <summary>Gives out the kept rows asked for in the order of the image's rows, top down.</summary>
        public ref struct Reader(KeptRows rows)
        {
            private readonly KeptRows _rows = rows;
            private int _next;
            private int _start;

            /// <summary>The hits of row <paramref name="y"/>, when it was kept.</summary>
            public bool TryRow(int y, out ReadOnlySpan<FinderPattern> hits)
            {
                var rowEnds = _rows._rowEnds;
                var stride = _rows._stride;
                // The rows passed over leave their hits behind, a row not kept none
                while (_next < rowEnds.Length && _next * stride < y)
                {
                    if (rowEnds[_next] >= 0)
                        _start = rowEnds[_next];
                    _next++;
                }
                if (_next >= rowEnds.Length || _next * stride != y || rowEnds[_next] < 0)
                {
                    hits = default;
                    return false;
                }
                var end = rowEnds[_next];
                hits = _rows._hits.Slice(_start, end - _start);
                _start = end;
                _next++;
                return true;
            }
        }
    }
}
