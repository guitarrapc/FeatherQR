using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using FeatherQR.SkiaSharp;
using FeatherQR.Tests;
using SkiaSharp;

namespace FeatherQR.Playground;

/// <summary>
/// The photograph panel: the generated image printed on a card and photographed through the camera the image decode sweep uses, then read back.
/// </summary>
public static partial class QrInterop
{
    private static PhotoCard? s_photoCard;
    private static string s_lastPhoto = "{}";

    /// <summary>
    /// Sets the card the photograph panel photographs: the generated image, and where its symbol lies in it.
    /// Returns an empty string, or <c>{"error":"..."}</c>.
    /// </summary>
    /// <param name="imageBytes">The generated PNG.</param>
    /// <param name="symbology">"qr", "microqr" or "rmqr": the decoder the photographs are read with.</param>
    /// <param name="matrixWidth">Modules across, quiet zone included, as the generate stats report them.</param>
    /// <param name="matrixHeight">Modules down, quiet zone included.</param>
    /// <param name="quietZone">The quiet zone, in modules.</param>
    [JSExport]
    public static string SetPhotoCard(byte[] imageBytes, string symbology, int matrixWidth, int matrixHeight, int quietZone)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(imageBytes)
                ?? throw new ArgumentException("The generated image could not be decoded.");
            s_photoCard = PhotoCard.From(bitmap, symbology, matrixWidth, matrixHeight, quietZone);
            return "";
        }
        catch (Exception ex)
        {
            s_photoCard = null;
            return JsonSerializer.Serialize(new ErrorPayload(ToUserFacingMessage(ex)), PlaygroundJsonContext.Default.ErrorPayload);
        }
    }

    /// <summary>
    /// Photographs the card under <paramref name="photoJson"/> (see <see cref="PhotoRequest"/>) and reads the photograph.
    /// Returns the photograph as a PNG, or as the JPEG it was read from when the request asks for JPEG compression, or <c>{"error":"..."}</c> (first byte 0x7B).
    /// <see cref="GetLastPhoto"/> then returns its read.
    /// </summary>
    [JSExport]
    public static byte[] Photograph(string photoJson)
    {
        try
        {
            var (image, payload) = TakeAndRead(photoJson, keepImage: true);
            s_lastPhoto = JsonSerializer.Serialize(payload, PlaygroundJsonContext.Default.PhotoPayload);
            return image!;
        }
        catch (Exception ex)
        {
            return SerializeError(ex);
        }
    }

    /// <summary>The read of the last <see cref="Photograph"/> call as JSON (see <see cref="PhotoPayload"/>).</summary>
    [JSExport]
    public static string GetLastPhoto() => s_lastPhoto;

    /// <summary>
    /// Photographs the card and reads it, without returning the image: the limit search's call.
    /// Returns <see cref="PhotoPayload"/> as JSON, or <c>{"error":"..."}</c>.
    /// </summary>
    [JSExport]
    public static string PhotographRead(string photoJson)
    {
        try
        {
            var (_, payload) = TakeAndRead(photoJson, keepImage: false);
            return JsonSerializer.Serialize(payload, PlaygroundJsonContext.Default.PhotoPayload);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new ErrorPayload(ToUserFacingMessage(ex)), PlaygroundJsonContext.Default.ErrorPayload);
        }
    }

    private static (byte[]? Image, PhotoPayload Payload) TakeAndRead(string photoJson, bool keepImage)
    {
        var card = s_photoCard ?? throw new InvalidOperationException("Generate a code first.");
        var request = JsonSerializer.Deserialize(photoJson, PlaygroundJsonContext.Default.PhotoRequest)
            ?? throw new InvalidOperationException("Photograph JSON deserialized to null.");

        var stopwatch = Stopwatch.StartNew();
        // The camera traces nine rays a pixel, so the photograph's size is held to what a browser renders in a moment.
        var pixelsPerModule = Math.Clamp(request.PixelsPerModule, 1f, card.MaxPixelsPerModule);
        var shot = new CameraRenderer.Shot(
            pixelsPerModule,
            TiltDegrees: Math.Clamp(request.Tilt, 0f, 80f),
            TiltAxisDegrees: request.TiltAxis,
            RollDegrees: request.Roll,
            BowModules: Math.Clamp(request.Bow, -8f, 8f),
            Distortion: -Math.Clamp(request.Barrel, 0f, 40f) / 100f,
            BlurSigma: pixelsPerModule * Math.Clamp(request.Blur, 0f, 2f),
            NoiseSigma: Math.Clamp(request.Noise, 0f, 100f),
            SceneScale: request.Scene ? 2.5f : 1f,
            Seed: request.Seed);
        var depth = Math.Clamp(request.LightDepth, 0f, 100f) / 100f;
        Func<int, int, float[]>? light = request.Light switch
        {
            "ramp" => (width, height) => UnevenLightingRenderer.Illumination(width, height, pixelsPerModule, UnevenLight.Ramp, request.LightDirection, depth),
            "shadow" => (width, height) => UnevenLightingRenderer.Illumination(width, height, pixelsPerModule, UnevenLight.Shadow, request.LightDirection, depth),
            _ => null,
        };
        var photo = CameraRenderer.Render(card.Columns, card.Rows, card.QuietZone, shot, channels: 3, card.Sample, light);
        var renderMs = ElapsedMs(stopwatch);

        using var taken = ToBitmap(photo);
        byte[]? image = null;
        SKBitmap? compressed = null;
        try
        {
            var quality = Math.Clamp(request.Jpeg, 0, 100);
            if (quality > 0)
            {
                image = Encode(taken, SKEncodedImageFormat.Jpeg, quality);
                compressed = SKBitmap.Decode(image) ?? throw new InvalidOperationException("The JPEG photograph could not be decoded.");
            }
            else if (keepImage)
            {
                image = Encode(taken, SKEncodedImageFormat.Png, 100);
            }

            stopwatch.Restart();
            var read = ReadPhotograph(compressed ?? taken, card.Symbology);
            var readMs = ElapsedMs(stopwatch);
            return (image, new PhotoPayload(read, Math.Round(pixelsPerModule, 2), renderMs, readMs, photo.Width, photo.Height));
        }
        finally
        {
            compressed?.Dispose();
        }
    }

    /// <summary>The photograph read by the decoder of the card's symbology, as <see cref="Decode"/> reports a read.</summary>
    private static DecodePayload ReadPhotograph(SKBitmap bitmap, string symbology)
    {
        var image = new DecodedImage(bitmap.Width, bitmap.Height, 1);
        switch (symbology)
        {
            case "microqr":
                {
                    var ok = MicroQRCodeDecoder.TryDecode(bitmap, out var text, out var info);
                    return new DecodePayload(ok, ok ? text : null, info.Status.ToString(), ok ? "microqr" : null, (int)info.Version, ok ? info.EccLevel.ToString() : null,
                        info.MaskPattern, info.ErrorsCorrected, 0, ok ? Flatten(info.Corners) : null, image.Width, image.Height);
                }
            case "rmqr":
                {
                    var ok = RmQRCodeDecoder.TryDecode(bitmap, out var text, out var info);
                    var gs1 = ok ? ReadGs1(text, info.Fnc1) : default;
                    return new DecodePayload(ok, ok ? text : null, info.Status.ToString(), ok ? "rmqr" : null, (int)info.Version, ok ? info.EccLevel.ToString() : null,
                        -1, info.ErrorsCorrected, 0, ok ? Flatten(info.Corners) : null, image.Width, image.Height,
                        Fnc1: ok ? Fnc1Label(info.Fnc1) : null, ApplicationIndicator: ok ? info.Fnc1.ApplicationIndicator : null,
                        Gs1Hri: gs1.Hri, Gs1Elements: gs1.Elements, Gs1Error: gs1.Error, Gs1Check: gs1.Check);
                }
            default:
                {
                    if (QRCodeDecoder.TryDecode(bitmap, out var text, out var info))
                        return StandardPayload(text, info, 0, image);
                    return new DecodePayload(false, null, info.Status.ToString(), null, info.Version, null, info.MaskPattern, info.ErrorsCorrected, 0,
                        ImageWidth: image.Width, ImageHeight: image.Height);
                }
        }
    }

    private static SKBitmap ToBitmap(CameraRenderer.Photograph photo)
    {
        var bitmap = new SKBitmap(new SKImageInfo(photo.Width, photo.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var span = bitmap.GetPixelSpan();
        var pixels = photo.Pixels;
        for (var y = 0; y < photo.Height; y++)
        {
            var row = span.Slice(y * bitmap.RowBytes, photo.Width * 4);
            for (var x = 0; x < photo.Width; x++)
            {
                var from = (y * photo.Width + x) * 3;
                row[x * 4] = pixels[from];
                row[x * 4 + 1] = pixels[from + 1];
                row[x * 4 + 2] = pixels[from + 2];
                row[x * 4 + 3] = 255;
            }
        }
        return bitmap;
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    /// <summary>
    /// The generated image as the face of a printed card. Its symbol sits where the image builder drew it: a uniform scale, centred on whole pixels.
    /// The card's paper beyond the image is white, and transparent pixels are laid over white.
    /// </summary>
    private sealed class PhotoCard
    {
        private readonly byte[] rgb;
        private readonly int width;
        private readonly int height;
        private readonly double left;
        private readonly double top;
        private readonly double scale;

        private PhotoCard(byte[] rgb, int width, int height, string symbology, int columns, int rows, int quietZone, double left, double top, double scale)
        {
            this.rgb = rgb;
            this.width = width;
            this.height = height;
            Symbology = symbology;
            Columns = columns;
            Rows = rows;
            QuietZone = quietZone;
            this.left = left;
            this.top = top;
            this.scale = scale;
        }

        public string Symbology { get; }

        /// <summary>Modules across, quiet zone excluded.</summary>
        public int Columns { get; }

        /// <summary>Modules down, quiet zone excluded.</summary>
        public int Rows { get; }

        public int QuietZone { get; }

        /// <summary>The module size that keeps a front-on photograph's longer side near 1,200 pixels.</summary>
        public float MaxPixelsPerModule => 1200f / (Math.Max(Columns, Rows) + 2 * QuietZone + 3);

        public static PhotoCard From(SKBitmap source, string symbology, int matrixWidth, int matrixHeight, int quietZone)
        {
            quietZone = Math.Max(0, quietZone);
            if (matrixWidth - 2 * quietZone < 1 || matrixHeight - 2 * quietZone < 1)
                throw new ArgumentException($"A symbol of {matrixWidth}x{matrixHeight} modules has no room for a quiet zone of {quietZone}.");

            using var flat = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            using (var canvas = new SKCanvas(flat))
            {
                canvas.Clear(SKColors.White);
                using var image = SKImage.FromBitmap(source);
                canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
            }
            var span = flat.GetPixelSpan();
            var rgb = new byte[source.Width * source.Height * 3];
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    var from = y * flat.RowBytes + x * 4;
                    var to = (y * source.Width + x) * 3;
                    rgb[to] = span[from];
                    rgb[to + 1] = span[from + 1];
                    rgb[to + 2] = span[from + 2];
                }
            }

            // The image builder's fit: one scale for both axes, the symbol centred, its corner on a whole pixel
            var scale = Math.Min((double)source.Width / matrixWidth, (double)source.Height / matrixHeight);
            var left = Math.Floor((source.Width - matrixWidth * scale) / 2 + 1e-6);
            var top = Math.Floor((source.Height - matrixHeight * scale) / 2 + 1e-6);
            return new PhotoCard(rgb, source.Width, source.Height, symbology, matrixWidth - 2 * quietZone, matrixHeight - 2 * quietZone, quietZone, left, top, scale);
        }

        /// <summary>The image's colour at module coordinates (<paramref name="u"/>, <paramref name="v"/>), bilinear, printed: black reflects as ink and white as paper do.</summary>
        public void Sample(double u, double v, Span<float> colour)
        {
            var x = left + (u + QuietZone) * scale - 0.5;
            var y = top + (v + QuietZone) * scale - 0.5;
            var x0 = (int)Math.Floor(x);
            var y0 = (int)Math.Floor(y);
            var fx = (float)(x - x0);
            var fy = (float)(y - y0);
            const float Range = (CameraRenderer.Paper - CameraRenderer.Ink) / 255f;
            for (var c = 0; c < 3; c++)
            {
                var top0 = Lerp(Channel(x0, y0, c), Channel(x0 + 1, y0, c), fx);
                var bottom = Lerp(Channel(x0, y0 + 1, c), Channel(x0 + 1, y0 + 1, c), fx);
                colour[c] = CameraRenderer.Ink + Lerp(top0, bottom, fy) * Range;
            }
        }

        private float Channel(int x, int y, int c)
            => x < 0 || y < 0 || x >= width || y >= height ? 255f : rgb[(y * width + x) * 3 + c];

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}

/// <summary>How the photograph panel takes a photograph: the camera, the light and the file.</summary>
public sealed record PhotoRequest
{
    /// <summary>The module size at the symbol's centre, in pixels.</summary>
    public float PixelsPerModule { get; init; } = 4f;
    /// <summary>The angle between the card and the image plane, in degrees.</summary>
    public float Tilt { get; init; }
    /// <summary>The direction of the axis the card is tilted about, in degrees: 0 runs along the symbol's rows.</summary>
    public float TiltAxis { get; init; }
    /// <summary>The turn of the whole image, in degrees.</summary>
    public float Roll { get; init; }
    /// <summary>Barrel distortion where a front-on photograph puts the symbol's corners, in percent.</summary>
    public float Barrel { get; init; }
    /// <summary>How far the card's left and right edges stand off its centre column, in modules. Positive bends them toward the camera.</summary>
    public float Bow { get; init; }
    /// <summary>Gaussian blur, in modules.</summary>
    public float Blur { get; init; }
    /// <summary>Gaussian sensor noise, in grey levels.</summary>
    public float Noise { get; init; }
    /// <summary>How the light falls: none, ramp (dimming across the image) or shadow (a soft edge through the middle).</summary>
    public string Light { get; init; } = "none";
    /// <summary>How much light the dimmest part loses, in percent.</summary>
    public float LightDepth { get; init; } = 50f;
    /// <summary>The direction the light falls off towards, in degrees: 0 is to the right, 90 downwards.</summary>
    public float LightDirection { get; init; }
    /// <summary>The card in a scene of clutter instead of on a plain table.</summary>
    public bool Scene { get; init; }
    /// <summary>JPEG quality the photograph is saved at before it is read, or 0 for none.</summary>
    public int Jpeg { get; init; }
    /// <summary>Seeds the noise and the scene.</summary>
    public int Seed { get; init; }
}

/// <summary>A photograph's read, serialized to the page script.</summary>
/// <param name="Read">The read as <see cref="QrInterop.Decode"/> reports one, its corners in the photograph's pixels.</param>
/// <param name="PixelsPerModule">The module size the photograph was taken at, after the panel's cap.</param>
/// <param name="RenderMs">The time the camera took.</param>
/// <param name="ReadMs">The time the decoder took.</param>
/// <param name="Width">The photograph's width in pixels.</param>
/// <param name="Height">The photograph's height in pixels.</param>
public sealed record PhotoPayload(DecodePayload Read, double PixelsPerModule, double RenderMs, double ReadMs, int Width, int Height);
