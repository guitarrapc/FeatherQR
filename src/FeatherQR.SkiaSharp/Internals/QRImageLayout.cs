using SkiaSharp;

namespace FeatherQR.SkiaSharp.Internals;

/// <summary>
/// Shared canvas layout math for the image builders: resolves the output image info and the content rectangle from explicit size and/or module pixel size, for square (Standard / Micro QR) and rectangular (rMQR) matrices.
/// </summary>
internal static class QRImageLayout
{
    /// <summary>
    /// Rectangular-aware layout.
    /// With a module pixel size the content is exactly <c>matrixWidth × matrixHeight</c> modules at that size (centered in an explicit canvas on whole pixels).
    /// With only an explicit canvas size the symbol is fitted with a uniform module scale and centered on whole pixels (letterbox), never stretched non-uniformly: a symbol whose modules are not square stops being findable well before it stops being drawn.
    /// With neither, <paramref name="defaultSize"/> is the symbology's own aspect-derived canvas and the content rectangle is the whole canvas: the renderer paints the background over all of it and draws the symbol at a uniform module scale inside (rMQR: <c>SymbolRenderer.GetLetterboxedArea</c>), so the height rounding costs at most a few pixels of background at the sides; letterboxing here again would only turn that background into a padded band on a canvas the caller never asked for.
    /// </summary>
    internal static (SKImageInfo info, SKRect contentRect) CreateLayout(int matrixWidth, int matrixHeight, Vector2Slim? explicitSize, int? modulePixelSize, Vector2Slim defaultSize)
    {
        if (modulePixelSize is null)
        {
            var size = explicitSize ?? defaultSize;
            var info = new SKImageInfo(size.X, size.Y);
            if (explicitSize is null)
                return (info, SKRect.Create(0, 0, size.X, size.Y));

            // Uniform scale, centered on whole pixels; the builder paints the leftover pad.
            var fitted = SymbolRenderer.GetLetterboxedArea(SKRect.Create(0, 0, size.X, size.Y), matrixWidth, matrixHeight);
            return (info, SKRect.Create((float)Math.Floor(fitted.Left), (float)Math.Floor(fitted.Top), fitted.Width, fitted.Height));
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
