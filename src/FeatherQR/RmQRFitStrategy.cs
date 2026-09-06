namespace FeatherQR;

/// <summary>
/// How <c>RmQRCodeGenerator</c> chooses among the rMQR versions that can hold the content when no exact version is requested. rMQR sizes are two-dimensional, so "smallest" is a policy: fewest modules, narrowest, or shortest.
/// </summary>
public enum RmQRFitStrategy
{
    /// <summary>Fewest modules (height × width); ties prefer the smaller height, i.e. the wider symbol. The default.</summary>
    MinimizeArea = 0,

    /// <summary>Smallest width; ties prefer the smaller height.</summary>
    MinimizeWidth = 1,

    /// <summary>Smallest height; ties prefer the smaller width.</summary>
    MinimizeHeight = 2,
}
