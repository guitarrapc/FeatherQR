#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/FeatherQR.SkiaSharp/FeatherQR.SkiaSharp.csproj
using SkiaSharp;
using FeatherQR;
using FeatherQR.SkiaSharp;

// Verification sample for the Corners member on the decode results (2.0.0):
// - A successful image decode reports where the symbol sits, in the image's pixel coordinates.
// - The corners are named in the SYMBOL's own frame, so a rotated capture reports a rotated
//   quadrilateral, not the image-axis bounding box, and TopLeft stays the corner beside the
//   finder pattern that defines the symbol's top-left.
// - A mirrored capture reports the same four points in reversed winding, which is how a caller
//   detects mirroring without the library exposing a separate flag.
// Every scene is written twice: the input the decoder saw, and the same image with the reported
// outline drawn over it, so the two can be compared side by side.

const string content = "https://github.com/guitarrapc/FeatherQR";
const int pixelsPerModule = 8;
const int margin = 48;
var outputDirectory = Path.Combine(Environment.CurrentDirectory, "samples", "Dotfiles", "output", "decode-corners");
Directory.CreateDirectory(outputDirectory);

// One flat symbol, rendered without a quiet zone of its own so the module area is the whole
// bitmap; each scene then draws it onto a white canvas through a different matrix.
var qrData = QRCodeGenerator.Create(content, QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 0 });
using var flat = new QRCodeImageBuilder(qrData)
    .WithModulePixelSize(pixelsPerModule)
    .WithColors(SKColors.Black, SKColors.White)
    .ToBitmap();

// Each scene transforms the symbol about its own origin; Compose does the placing, so a matrix
// here only has to say what the camera did, not where the result lands on the canvas.
var scenes = new (string Name, string What, SKMatrix Matrix)[]
{
    ("1-flat", "as printed", SKMatrix.Identity),
    ("2-rotated", "rotated 30°", SKMatrix.CreateRotationDegrees(30)),
    ("3-mirrored", "mirrored (front camera)", SKMatrix.CreateScale(-1, 1)),
    ("4-keystone", "keystone, bottom edge foreshortened", Keystone(flat, 0.12f)),
};

foreach (var (name, what, matrix) in scenes)
{
    using var scene = Compose(flat, matrix);

    if (!QRCodeImageDecoder.TryDecode(scene, out var text, out var info))
    {
        Console.WriteLine($"{name,-12} {what}: no decode ({info.Status})");
        continue;
    }

    // Corners is populated on success only; a matrix-level decode or a failure leaves it empty.
    var corners = info.Corners;

    // y grows downward, so a symbol as printed winds clockwise and gives a positive cross
    // product; a mirrored capture reverses that.
    var cross = (corners.TopRight.X - corners.TopLeft.X) * (corners.BottomLeft.Y - corners.TopLeft.Y)
              - (corners.TopRight.Y - corners.TopLeft.Y) * (corners.BottomLeft.X - corners.TopLeft.X);
    var mirrored = cross < 0;

    SaveBitmap(scene, Path.Combine(outputDirectory, $"{name}_input.png"));
    using var annotated = Annotate(scene, corners);
    SaveBitmap(annotated, Path.Combine(outputDirectory, $"{name}_corners.png"));

    Console.WriteLine($"{name,-12} {what}");
    Console.WriteLine($"  text     : {text}");
    Console.WriteLine($"  symbol   : version {info.Version}, ECC {info.EccLevel}, {info.ErrorsCorrected} codewords corrected");
    Console.WriteLine($"  corners  : TL {Format(corners.TopLeft)}  TR {Format(corners.TopRight)}");
    Console.WriteLine($"             BL {Format(corners.BottomLeft)}  BR {Format(corners.BottomRight)}");
    Console.WriteLine($"  winding  : {(mirrored ? "counter-clockwise, so the capture is mirrored" : "clockwise, so the capture is not mirrored")}");
    Console.WriteLine();
}

Console.WriteLine($"Saved input and annotated images to: {outputDirectory}");

static string Format(ImagePoint point) => $"({point.X,7:F1}, {point.Y,7:F1})";

/// <summary>Draws the reported outline over a copy of the scene: the quadrilateral, a marker on TopLeft, and a label per corner.</summary>
static SKBitmap Annotate(SKBitmap scene, SymbolCorners corners)
{
    var annotated = scene.Copy();
    using var canvas = new SKCanvas(annotated);

    using var builder = new SKPathBuilder();
    builder.MoveTo(corners.TopLeft.X, corners.TopLeft.Y);
    builder.LineTo(corners.TopRight.X, corners.TopRight.Y);
    builder.LineTo(corners.BottomRight.X, corners.BottomRight.Y);
    builder.LineTo(corners.BottomLeft.X, corners.BottomLeft.Y);
    builder.Close();
    using var path = builder.Detach();

    using var outline = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = new SKColor(0xFF, 0x3B, 0x30), IsAntialias = true };
    canvas.DrawPath(path, outline);

    // TopLeft is the corner beside the finder pattern that defines the symbol's top-left; marking
    // it is what makes a rotation or a mirror visible at a glance.
    using var marker = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(0x00, 0x7A, 0xFF), IsAntialias = true };
    canvas.DrawCircle(corners.TopLeft.X, corners.TopLeft.Y, 7, marker);

    using var font = new SKFont(SKTypeface.Default, 15);
    using var label = new SKPaint { Color = new SKColor(0x00, 0x7A, 0xFF), IsAntialias = true };
    var centreX = (corners.TopLeft.X + corners.BottomRight.X) / 2;
    var centreY = (corners.TopLeft.Y + corners.BottomRight.Y) / 2;
    foreach (var (point, name) in new[]
    {
        (corners.TopLeft, "TL"),
        (corners.TopRight, "TR"),
        (corners.BottomRight, "BR"),
        (corners.BottomLeft, "BL"),
    })
    {
        // Push each label a little way outward from the symbol's centre so it never sits on the code.
        var dx = point.X - centreX;
        var dy = point.Y - centreY;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        var scale = length > 0 ? 18f / length : 0f;
        canvas.DrawText(name, point.X + dx * scale, point.Y + dy * scale + 5, SKTextAlign.Center, font, label);
    }

    canvas.Flush();
    return annotated;
}

/// <summary>
/// Draws the flat symbol onto a white canvas through <paramref name="matrix"/>, sizing the canvas
/// from where that matrix actually puts the symbol and leaving a <see cref="margin"/> all round:
/// the decoder needs a light quiet zone, and the annotated copy draws its labels just outside the
/// outline. A rotation about the centre would otherwise push two corners off the top-left edge.
/// </summary>
static SKBitmap Compose(SKBitmap flat, SKMatrix matrix)
{
    ReadOnlySpan<SKPoint> square =
    [
        new(0, 0),
        new(flat.Width, 0),
        new(flat.Width, flat.Height),
        new(0, flat.Height),
    ];

    float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
    foreach (var corner in square)
    {
        var mapped = Project(matrix, corner);
        minX = MathF.Min(minX, mapped.X);
        minY = MathF.Min(minY, mapped.Y);
        maxX = MathF.Max(maxX, mapped.X);
        maxY = MathF.Max(maxY, mapped.Y);
    }

    var placed = SKMatrix.CreateTranslation(margin - minX, margin - minY).PreConcat(matrix);
    var width = (int)MathF.Ceiling(maxX - minX) + margin * 2;
    var height = (int)MathF.Ceiling(maxY - minY) + margin * 2;

    var scene = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
    using var canvas = new SKCanvas(scene);
    canvas.Clear(SKColors.White);
    canvas.SetMatrix(placed);
    canvas.DrawBitmap(flat, 0, 0, SKSamplingOptions.Default);
    canvas.Flush();
    return scene;
}

/// <summary>A point through a matrix, dividing by the perspective term so a keystone is honoured.</summary>
static SKPoint Project(SKMatrix m, SKPoint p)
{
    var w = m.Persp0 * p.X + m.Persp1 * p.Y + m.Persp2;
    return new SKPoint(
        (m.ScaleX * p.X + m.SkewX * p.Y + m.TransX) / w,
        (m.SkewY * p.X + m.ScaleY * p.Y + m.TransY) / w);
}

/// <summary>
/// Keystone: a perspective term in y, so the far (bottom) edge is foreshortened by
/// <paramref name="strength"/> of its width, as when the code is tilted away from the camera.
/// x is centred first, so the symbol narrows about its own axis instead of shearing sideways.
/// </summary>
static SKMatrix Keystone(SKBitmap flat, float strength)
{
    float w = flat.Width, h = flat.Height;
    var perspective = SKMatrix.Identity;
    perspective.Persp1 = strength / h;
    return SKMatrix.CreateTranslation(w / 2, 0)
        .PreConcat(perspective)
        .PreConcat(SKMatrix.CreateTranslation(-w / 2, 0));
}

static void SaveBitmap(SKBitmap bitmap, string outputPath)
{
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
    data.SaveTo(stream);
}
