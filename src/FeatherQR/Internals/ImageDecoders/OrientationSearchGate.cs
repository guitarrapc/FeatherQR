namespace FeatherQR.Internals.ImageDecoders;

/// <summary>
/// What a single-finder decoder asks before it searches a finder's orientations, the costly part of a failed candidate: whether to search at all.
/// The one-symbology calls always search (<see cref="SearchAlways"/>). The read of every symbology hands in a gate that knows what the other symbologies' symbols look like at a finder.
/// </summary>
/// <remarks>Given the orientation candidates the decoder found, so that a gate reads the finder's axes without a sweep of its own.</remarks>
internal interface IOrientationSearchGate
{
    /// <summary>Whether the decoder searches the finder's orientations in <paramref name="orientations"/>, found at the finder centred on (<paramref name="centerX"/>, <paramref name="centerY"/>).</summary>
    bool Search(in ImageView image, float centerX, float centerY, ReadOnlySpan<OrientationCandidate> orientations);
}

/// <summary>The gate of the one-symbology calls: every finder is searched.</summary>
internal readonly struct SearchAlways : IOrientationSearchGate
{
    public bool Search(in ImageView image, float centerX, float centerY, ReadOnlySpan<OrientationCandidate> orientations) => true;
}
