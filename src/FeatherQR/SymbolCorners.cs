namespace FeatherQR;

/// <summary>
/// Where a decoded symbol sits in the image: the four outer corners of its module area, quiet zone excluded, in the input image's continuous pixel coordinates (see <see cref="ImagePoint"/>).
/// </summary>
/// <remarks>
/// <para>
/// The corners are named in the symbol's own frame, not the image's: <see cref="TopLeft"/> is the corner beside the finder pattern that defines the symbol's top-left, wherever it landed in the picture. A rotated capture therefore reports rotated corners, and a mirrored capture reports them in reversed winding: seen on screen (y down), the corners of a symbol as printed run clockwise, and those of a mirrored capture run counter-clockwise. The four points form a general quadrilateral under perspective.
/// </para>
/// <para>
/// They are estimates, and how tight depends on how much the decoder had to infer. At <b>8 pixels per module or more</b>: Standard QR, fitted on three finders and an alignment pattern, holds every corner within half a module, flat, rotated, mirrored or keystoned. Micro QR and rMQR fit from a single finder and match that only flat and at right angles; an oblique rotation takes whichever axis angle still decodes and a keystone comes from a bounded search, so their far corners can be off by about a module, or a module and a half on an rMQR symbol foreshortened by 2 % or more.
/// </para>
/// <para>
/// Two things loosen those figures. Below 8 pixels per module, read the bound in pixels: the corners are anchored on pattern centres that resolve to about a pixel at any module size, so the error stays near four pixels while the module shrinks under it (about 0.9 module at 3 pixels per module, on every symbology). And a Standard QR symbol whose bottom-right alignment pattern was not located — version 1 has none, and elsewhere a smudge over one module loses it — is fitted on the finders alone and held to about a module and a half, at any density. Error correction still reports <see cref="DecodeStatus.Success"/>, and nothing here says which fit ran.
/// </para>
/// <para>
/// Populated only when the decode succeeded from an image. A matrix-level decode has no image and a failed image decode located nothing worth reporting; both leave the value at its default, which <see cref="IsEmpty"/> reports.
/// </para>
/// </remarks>
public readonly record struct SymbolCorners
{
    internal SymbolCorners(ImagePoint topLeft, ImagePoint topRight, ImagePoint bottomRight, ImagePoint bottomLeft)
    {
        TopLeft = topLeft;
        TopRight = topRight;
        BottomRight = bottomRight;
        BottomLeft = bottomLeft;
    }

    /// <summary>The corner beside the symbol's top-left finder pattern.</summary>
    public ImagePoint TopLeft { get; }

    /// <summary>The corner at the symbol's top-right.</summary>
    public ImagePoint TopRight { get; }

    /// <summary>The corner at the symbol's bottom-right.</summary>
    public ImagePoint BottomRight { get; }

    /// <summary>The corner at the symbol's bottom-left.</summary>
    public ImagePoint BottomLeft { get; }

    /// <summary>
    /// <c>true</c> when no corners were reported: the decode did not succeed, or it ran on a module matrix rather than an image.
    /// </summary>
    public bool IsEmpty => Equals(default);
}
