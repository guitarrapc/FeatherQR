using SkiaSharp;

namespace FeatherQR.SkiaSharp;

/// <summary>
/// Where the text sits relative to the icon.
/// </summary>
public enum TextVerticalAlignment
{
    /// <summary>
    /// Below the icon.
    /// </summary>
    Bottom = 0,

    /// <summary>
    /// Centered on the icon.
    /// </summary>
    Center = 1,

    /// <summary>
    /// Above the icon.
    /// </summary>
    Top = 2,
}

/// <summary>
/// How the icon at the center of a symbol is drawn.
/// </summary>
public abstract class IconShape
{
    /// <summary>
    /// Draws the icon.
    /// </summary>
    /// <param name="canvas">The canvas to render on.</param>
    /// <param name="rect">Where to draw the icon.</param>
    /// <param name="borderRect">The rectangular area for the border (if border width > 0).</param>
    /// <param name="backgroundColor">The color to fill the border with.</param>
    public abstract void Draw(SKCanvas canvas, SKRect rect, SKRect borderRect, SKColor backgroundColor);

    /// <summary>
    /// A copy that draws copies of the shape's images and fonts, for a render that runs after its call has returned.
    /// <c>null</c> for a shape of the caller's own, which cannot be copied.
    /// </summary>
    /// <param name="owned">Collects the copies, for the render to dispose.</param>
    internal virtual IconShape? CopyForLaterRender(List<IDisposable> owned) => null;

    internal static SKBitmap CopyForLaterRender(SKBitmap image, List<IDisposable> owned)
    {
        // A bitmap that draws nothing gives an empty copy, which the render refuses as it refuses the bitmap.
        // A copy made from its info would have pixels the bitmap lacks, and be drawn blank
        if (image.DrawsNothing)
        {
            var none = new SKBitmap();
            owned.Add(none);
            return none;
        }

        // The pixels are read as they are. SKBitmap.Copy draws them, which clears the colour of an unpremultiplied pixel with no alpha
        var copy = new SKBitmap(image.Info);
        owned.Add(copy);
        using var source = image.PeekPixels();
        using var target = copy.PeekPixels();
        if (source is null || target is null || !source.ReadPixels(target))
            throw new InvalidOperationException("The icon's image could not be copied.");
        return copy;
    }
}

/// <summary>
/// Draws an image as the icon.
/// </summary>
public sealed class ImageIconShape : IconShape
{
    private readonly SKBitmap _image;

    /// <summary>
    /// Creates an icon from an image.
    /// </summary>
    /// <param name="image">The image to draw. It is stretched to fill the icon area, so its aspect ratio is not preserved. The caller keeps ownership of it.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="image"/> is null.</exception>
    public ImageIconShape(SKBitmap image)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
    }

    /// <inheritdoc/>
    public override void Draw(SKCanvas canvas, SKRect rect, SKRect borderRect, SKColor backgroundColor)
    {
        if (borderRect != rect)
        {
            using var borderPaint = new SKPaint()
            {
                Color = backgroundColor,
                Style = SKPaintStyle.Fill,
            };
            canvas.DrawRect(borderRect, borderPaint);
        }

        // SKSamplingOptions.Default matches what the obsolete DrawBitmap(bitmap, rect) overload used.
        canvas.DrawBitmap(_image, rect, SKSamplingOptions.Default);
    }

    internal override IconShape CopyForLaterRender(List<IDisposable> owned) => new ImageIconShape(CopyForLaterRender(_image, owned));
}

/// <summary>
/// Draws an image as the icon, with a caption beside it.
/// </summary>
public sealed class ImageTextIconShape : IconShape
{
    private readonly SKBitmap _image;
    private readonly string _text;
    private readonly SKColor _textColor;
    private readonly SKFont _font;
    private readonly SKTextAlign _horizontalAlign;
    private readonly TextVerticalAlignment _verticalAlign;
    private readonly int _textPadding;

    /// <summary>
    /// Creates the icon from an image and a caption.
    /// </summary>
    /// <param name="image">The image to draw. Stretched to fill the icon area, so its aspect ratio is not kept.</param>
    /// <param name="text">The caption.</param>
    /// <param name="textColor">The caption color.</param>
    /// <param name="font">The caption font.</param>
    /// <param name="horizontalAlign">How the caption lines up horizontally.</param>
    /// <param name="verticalAlign">Where the caption sits relative to the image.</param>
    /// <param name="textPadding">Gap between the image and the caption, in pixels.</param>
    public ImageTextIconShape(SKBitmap image, string text, SKColor textColor, SKFont font, SKTextAlign horizontalAlign = SKTextAlign.Center, TextVerticalAlignment verticalAlign = TextVerticalAlignment.Bottom, int textPadding = 0)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _textColor = textColor;
        _font = font ?? throw new ArgumentNullException(nameof(font));
        _horizontalAlign = horizontalAlign;
        _verticalAlign = verticalAlign;
        _textPadding = textPadding;
    }

    internal override IconShape CopyForLaterRender(List<IDisposable> owned)
    {
        // Every setting the font carries, so the copy draws the caption as the caller's font does
        var font = new SKFont(_font.Typeface, _font.Size, _font.ScaleX, _font.SkewX)
        {
            Edging = _font.Edging,
            Hinting = _font.Hinting,
            Subpixel = _font.Subpixel,
            Embolden = _font.Embolden,
            LinearMetrics = _font.LinearMetrics,
            BaselineSnap = _font.BaselineSnap,
            ForceAutoHinting = _font.ForceAutoHinting,
            EmbeddedBitmaps = _font.EmbeddedBitmaps,
        };
        owned.Add(font);
        return new ImageTextIconShape(CopyForLaterRender(_image, owned), _text, _textColor, font, _horizontalAlign, _verticalAlign, _textPadding);
    }

    /// <inheritdoc/>
    public override void Draw(SKCanvas canvas, SKRect rect, SKRect borderRect, SKColor backgroundColor)
    {
        if (borderRect != rect)
        {
            using var borderPaint = new SKPaint()
            {
                Color = backgroundColor,
                Style = SKPaintStyle.Fill,
            };
            canvas.DrawRect(borderRect, borderPaint);
        }

        // SKSamplingOptions.Default matches what the obsolete DrawBitmap(bitmap, rect) overload used.
        canvas.DrawBitmap(_image, rect, SKSamplingOptions.Default);

        if (!string.IsNullOrEmpty(_text))
        {
            using var textPaint = new SKPaint
            {
                Color = _textColor,
                IsAntialias = true,
            };

            var textX = _horizontalAlign switch
            {
                SKTextAlign.Left => borderRect.Left,
                SKTextAlign.Center => borderRect.MidX,
                SKTextAlign.Right => borderRect.Right,
                _ => borderRect.Left,
            };

            // CapHeight: Capital letter height, to better align text vertically
            var textY = _verticalAlign switch
            {
                TextVerticalAlignment.Top => rect.Top - _textPadding,
                TextVerticalAlignment.Center => rect.MidY + (_font.Metrics.CapHeight / 2),
                TextVerticalAlignment.Bottom => rect.Bottom + _font.Metrics.CapHeight + _textPadding,
                _ => rect.Bottom + _font.Metrics.CapHeight + _textPadding,
            };

            canvas.DrawText(_text, textX, textY, _horizontalAlign, _font, textPaint);
        }
    }
}
