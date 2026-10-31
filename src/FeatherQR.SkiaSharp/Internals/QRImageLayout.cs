using SkiaSharp;

namespace FeatherQR.SkiaSharp.Internals;

/// <summary>
/// Shared canvas layout math for the image builders: resolves the output image info and the content rectangle from explicit size and/or module pixel size, for square (Standard / Micro QR) and rectangular (rMQR) matrices.
/// </summary>
internal static class QRImageLayout
{
    /// <summary>Sub-pixel slack absorbed before the centering offset is floored, so a whole-number offset is not lost to the pixel below it. Sits between the arithmetic error and the closest a real offset can come to a pixel boundary: only one axis is ever off-centre, and its offset is a multiple of <c>1 / (2 × the dimension that constrained the fit)</c>, so the tightest case is <c>1 / (2 × max(matrixWidth, matrixHeight))</c>.</summary>
    private const double CenteringEpsilon = 1e-6;

    /// <summary>
    /// Calculates a centered layout with square modules for square or rectangular symbols.
    /// </summary>
    /// <remarks>
    /// A module pixel size fixes the content dimensions. An explicit canvas is fitted uniformly and centered on whole pixels.
    /// With neither, defaultSize supplies the aspect-derived canvas and the content rectangle covers it.
    /// </remarks>
    internal static (SKImageInfo info, SKRect contentRect) CreateLayout(int matrixWidth, int matrixHeight, Vector2Slim? explicitSize, int? modulePixelSize, Vector2Slim defaultSize)
    {
        // The default canvas is already aspect-derived. Letterboxing it here again would turn rounding slack into padding.
        // The renderer instead paints the full background and fits the modules uniformly inside it.
        if (modulePixelSize is null)
        {
            var size = explicitSize ?? defaultSize;
            var info = new SKImageInfo(size.X, size.Y);
            if (explicitSize is null)
                return (info, SKRect.Create(0, 0, size.X, size.Y));

            // Uniform scale, centered on whole pixels. The builder paints the leftover pad.
            // The same fit as SymbolRenderer.GetLetterboxedArea, in double: placing on integer pixels needs precision that drawing does not.
            var scale = Math.Min((double)size.X / matrixWidth, (double)size.Y / matrixHeight);
            var contentWidth = matrixWidth * scale;
            var contentHeight = matrixHeight * scale;
            // Unreachable with the epsilon in place, but a negative offset would be drawn rather than clipped.
            var fittedLeft = Math.Max(0f, (float)Math.Floor((size.X - contentWidth) / 2 + CenteringEpsilon));
            var fittedTop = Math.Max(0f, (float)Math.Floor((size.Y - contentHeight) / 2 + CenteringEpsilon));
            return (info, SKRect.Create(fittedLeft, fittedTop, (float)contentWidth, (float)contentHeight));
        }

        int contentWidthPx, contentHeightPx;
        try
        {
            contentWidthPx = checked(matrixWidth * modulePixelSize.Value);
            contentHeightPx = checked(matrixHeight * modulePixelSize.Value);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException("Calculated image size overflowed. Reduce module pixel size or QR version.", ex);
        }

        if (explicitSize is null)
            return (new SKImageInfo(contentWidthPx, contentHeightPx), SKRect.Create(0, 0, contentWidthPx, contentHeightPx));

        var canvasWidth = explicitSize.Value.X;
        var canvasHeight = explicitSize.Value.Y;
        if (canvasWidth < contentWidthPx || canvasHeight < contentHeightPx)
        {
            throw new InvalidOperationException(
                $"Canvas size {canvasWidth}x{canvasHeight} is smaller than QR content size {contentWidthPx}x{contentHeightPx} " +
                $"(QR matrix size {matrixWidth}x{matrixHeight} * module pixel size {modulePixelSize.Value}).");
        }

        // Use integer offsets so content stays on whole pixels (odd padding may be 1px asymmetric).
        var left = (canvasWidth - contentWidthPx) / 2;
        var top = (canvasHeight - contentHeightPx) / 2;
        return (
            new SKImageInfo(canvasWidth, canvasHeight),
            SKRect.Create(left, top, contentWidthPx, contentHeightPx));
    }

    internal static bool ContentCoversCanvas(SKRect contentRect, SKImageInfo info)
    {
        return contentRect.Left <= 0 && contentRect.Top <= 0
            && contentRect.Right >= info.Width && contentRect.Bottom >= info.Height;
    }
}
