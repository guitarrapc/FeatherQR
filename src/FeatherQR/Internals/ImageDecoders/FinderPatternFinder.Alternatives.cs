namespace FeatherQR.Internals.ImageDecoders;

internal static partial class FinderPatternFinder
{
    /// <summary>
    /// The triples of a candidate list to try after the selected one failed to decode, the best confirmed first: by the height its least confirmed member was seen over, then by the selection score.
    /// </summary>
    /// <remarks>
    /// The selection score measures a triple's shape and the spread of its module sizes, and a strong perspective breaks both: past 30 % keystone the finder at the wide edge is drawn half again as long as the others, and a false candidate inside the symbol makes a better-looking triangle with the other two.
    /// What perspective does not change is how many rows confirm a finder. A real finder's centre band is three modules tall whatever shape it is drawn, and a false candidate is a coincidence of runs that few rows repeat, so a triple is only as believable as its least confirmed member.
    /// Heights are in modules, rows over module size, as <c>ConfirmedHeight</c> counts them: a false candidate of twice the module size is confirmed on as many rows as a real finder.
    /// Over the renders past 30 % keystone that the selected triple fails on, this order puts the drawn triple first among the rest in every one where the list is a full sweep's, where the selection score ranks it anywhere from second to 142nd.
    /// It does not replace the selection: three well-confirmed finders from two symbols in one image are a triple here and a poor shape there, so each triple handed out still has to prove itself before it is decoded.
    /// </remarks>
    internal ref struct AlternativeTriples
    {
        private readonly Span<FinderPattern> _candidates;
        private readonly ReadOnlySpan<FinderPattern> _selected;

        /// <summary>Index of the least confirmed member of the triples being handed out; the other two come from before it.</summary>
        private int _weakest;

        private float _lastScore;
        private int _lastFirst;
        private int _lastSecond;

        /// <summary>Orders <paramref name="candidates"/> in place, most confirmed first; <paramref name="selected"/> is the triple already tried, which is never handed out again.</summary>
        public AlternativeTriples(Span<FinderPattern> candidates, ReadOnlySpan<FinderPattern> selected)
        {
            // Insertion sort, stable so that equal heights keep the scan order: netstandard2.0 has no Span.Sort, and the list is tiny (≤ 32)
            for (var i = 1; i < candidates.Length; i++)
            {
                var current = candidates[i];
                var height = ConfirmedModules(current);
                var j = i - 1;
                while (j >= 0 && ConfirmedModules(candidates[j]) < height)
                {
                    candidates[j + 1] = candidates[j];
                    j--;
                }
                candidates[j + 1] = current;
            }

            _candidates = candidates;
            _selected = selected;
            _weakest = 2;
            _lastScore = float.NegativeInfinity;
            _lastFirst = -1;
            _lastSecond = -1;
        }

        /// <summary>Writes the next triple into <paramref name="triple"/>; false when none is left.</summary>
        public bool TryNext(scoped Span<FinderPattern> triple)
        {
            while (_weakest < _candidates.Length)
            {
                // The next pair before the weakest member in (score, first, second) order: a selection pass over the pairs, no list to keep
                ref readonly var weakest = ref _candidates[_weakest];
                var bestScore = float.PositiveInfinity;
                var bestFirst = -1;
                var bestSecond = -1;
                for (var first = 0; first < _weakest - 1; first++)
                {
                    for (var second = first + 1; second < _weakest; second++)
                    {
                        var score = TripleScore(_candidates[first], _candidates[second], weakest);
                        if (IsBefore(_lastScore, _lastFirst, _lastSecond, score, first, second) && IsBefore(score, first, second, bestScore, bestFirst, bestSecond))
                        {
                            bestScore = score;
                            bestFirst = first;
                            bestSecond = second;
                        }
                    }
                }

                // Only triples with no extent are left for this member (three centres at one point score the maximum): on to the next member
                if (bestFirst < 0 || bestScore == float.MaxValue)
                {
                    _weakest++;
                    _lastScore = float.NegativeInfinity;
                    _lastFirst = _lastSecond = -1;
                    continue;
                }

                _lastScore = bestScore;
                _lastFirst = bestFirst;
                _lastSecond = bestSecond;
                if (IsSelected(_candidates[bestFirst]) && IsSelected(_candidates[bestSecond]) && IsSelected(weakest))
                    continue;

                triple[0] = _candidates[bestFirst];
                triple[1] = _candidates[bestSecond];
                triple[2] = weakest;
                return true;
            }
            return false;
        }

        // A candidate the selected triple holds; by the merge rule, since the complementary rescan can merge more rows into a candidate after the stride pass selected it
        private readonly bool IsSelected(in FinderPattern candidate)
        {
            foreach (var selected in _selected)
            {
                if (Math.Abs(selected.X - candidate.X) <= selected.ModuleSize && Math.Abs(selected.Y - candidate.Y) <= selected.ModuleSize)
                    return true;
            }
            return false;
        }

        private static bool IsBefore(float score, int first, int second, float otherScore, int otherFirst, int otherSecond)
            => score < otherScore || (score == otherScore && (first < otherFirst || (first == otherFirst && second < otherSecond)));

        private static float ConfirmedModules(in FinderPattern candidate) => candidate.Count / candidate.ModuleSize;
    }
}
