using SkiaSharp;

namespace QRImageDecodeSweep;

internal static class Pixels
{
    /// <summary>The symbol with its quiet zone as an isDark callback over the padded grid.</summary>
    public static (Func<int, int, bool> IsDark, int Columns, int Rows) Padded(Symbol symbol, int quietZone)
    {
        var columns = symbol.Width + 2 * quietZone;
        var rows = symbol.Height + 2 * quietZone;
        return ((row, column) =>
        {
            var r = row - quietZone;
            var c = column - quietZone;
            return r >= 0 && c >= 0 && r < symbol.Height && c < symbol.Width && symbol[r, c];
        }, columns, rows);
    }

    public static SKBitmap ToGrayBitmap(byte[] luminance, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
        var span = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
            luminance.AsSpan(y * width, width).CopyTo(span.Slice(y * bitmap.RowBytes, width));
        return bitmap;
    }

    public static (byte[] Luminance, int Width, int Height) ToLuminance(SKBitmap source)
    {
        using var gray = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(gray))
        {
            canvas.Clear(SKColors.White);
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        }
        var luminance = new byte[gray.Width * gray.Height];
        var span = gray.GetPixelSpan();
        for (var y = 0; y < gray.Height; y++)
            span.Slice(y * gray.RowBytes, gray.Width).CopyTo(luminance.AsSpan(y * gray.Width, gray.Width));
        return (luminance, gray.Width, gray.Height);
    }

    /// <summary>
    /// Writes every image of <paramref name="root"/>'s directories as the grey image <see cref="ToLuminance"/> hands the readers here, as PNG under the same directory and name.
    /// A reader run in its own harness converts a photograph to grey its own way, and reads at the edge of a decoder move with the conversion; given these files it reads the pixels every other reader was given.
    /// </summary>
    public static int WriteGrey(string root, string outDir)
    {
        var files = Directory.EnumerateDirectories(root).SelectMany(Directory.EnumerateFiles).Where(Corpus.IsImage).ToList();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 16) }, path =>
        {
            using var bitmap = SKBitmap.Decode(path) ?? throw new InvalidDataException($"cannot decode {path}");
            var (luminance, width, height) = ToLuminance(bitmap);
            using var gray = ToGrayBitmap(luminance, width, height);
            using var data = gray.Encode(SKEncodedImageFormat.Png, 100);
            var directory = Path.Combine(outDir, Path.GetFileName(Path.GetDirectoryName(path)!));
            Directory.CreateDirectory(directory);
            using var stream = File.Create(Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + ".png"));
            data.SaveTo(stream);
        });
        Console.WriteLine($"wrote {files.Count:N0} grey images under {outDir}");
        return 0;
    }

    public static (byte[] Luminance, int Width, int Height) Resize(byte[] luminance, int width, int height, int newWidth, int newHeight, SKSamplingOptions sampling)
    {
        using var source = ToGrayBitmap(luminance, width, height);
        using var resized = source.Resize(new SKImageInfo(newWidth, newHeight, SKColorType.Gray8, SKAlphaType.Opaque), sampling);
        return ToLuminance(resized);
    }
}
