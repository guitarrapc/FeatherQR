using SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// Renders a symbol whose modules are not square, and measures that they are not.
/// </summary>
/// <remarks>
/// Symbols like this reach the decoders from thermal printers, laser markers and perspective, but the
/// library no longer draws one by accident: the builders and the renderer fit any area they are given
/// into a square. A canvas scale over the symbol's own area is the one way left to stretch it, so the
/// decoder tests go through here and check the stretch is really in the pixels.
/// </remarks>
internal static class StretchedSymbol
{
    /// <summary>
    /// Draws <paramref name="draw"/> into a <paramref name="width"/> x <paramref name="height"/> area under a
    /// canvas scale of (<paramref name="scaleX"/>, <paramref name="scaleY"/>), on white with a margin.
    /// </summary>
    public static SKBitmap Render(int width, int height, float scaleX, float scaleY, Action<SKCanvas, SKRect> draw, int margin = 20)
    {
        var bitmap = new SKBitmap(new SKImageInfo(
            (int)Math.Ceiling(width * scaleX) + margin * 2,
            (int)Math.Ceiling(height * scaleY) + margin * 2,
            SKColorType.Bgra8888,
            SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Translate(margin, margin);
        canvas.Scale(scaleX, scaleY);
        draw(canvas, SKRect.Create(0, 0, width, height));
        canvas.Flush();
        return bitmap;
    }

    /// <summary>
    /// The dark run through the centre stone of the top-left finder, across and down, in pixels.
    /// </summary>
    /// <remarks>
    /// The first dark pixel in reading order is the finder's outer corner in all three symbologies,
    /// since the quiet zone is light. The ring's runs from there are seven modules on each axis, which
    /// puts the centre of the three-module stone halfway along them. Vertical over horizontal is the
    /// module aspect, whatever drew the symbol and wherever it sits.
    /// </remarks>
    public static (int Horizontal, int Vertical) FinderStoneRuns(SKBitmap bitmap)
    {
        bool Dark(int x, int y) => x >= 0 && y >= 0 && x < bitmap.Width && y < bitmap.Height && bitmap.GetPixel(x, y).Red < 128;

        for (var y0 = 0; y0 < bitmap.Height; y0++)
        {
            for (var x0 = 0; x0 < bitmap.Width; x0++)
            {
                if (!Dark(x0, y0))
                    continue;

                var ringAcross = 0;
                while (Dark(x0 + ringAcross, y0)) ringAcross++;
                var ringDown = 0;
                while (Dark(x0, y0 + ringDown)) ringDown++;

                var cx = x0 + ringAcross / 2;
                var cy = y0 + ringDown / 2;
                var left = 0;
                while (Dark(cx - left - 1, cy)) left++;
                var right = 0;
                while (Dark(cx + right + 1, cy)) right++;
                var up = 0;
                while (Dark(cx, cy - up - 1)) up++;
                var down = 0;
                while (Dark(cx, cy + down + 1)) down++;
                return (left + right + 1, up + down + 1);
            }
        }

        throw new InvalidOperationException("The bitmap has no dark pixel.");
    }
}
