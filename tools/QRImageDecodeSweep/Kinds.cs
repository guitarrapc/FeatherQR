using FeatherQR.Tests;
using SkiaSharp;

namespace QRImageDecodeSweep;

internal static class Kinds
{
    private static float Range(Random random, float lo, float hi) => lo + (float)random.NextDouble() * (hi - lo);

    public static Kind[] All(string symbology)
    {
        var qz = Symbologies.QuietZone(symbology);
        var list = new List<Kind>();
        (string Label, float Lo, float Hi)[] bands = [("1.00-1.25", 1f, 1.25f), ("1.25-1.50", 1.25f, 1.5f), ("1.50-2.00", 1.5f, 2f), ("2.00-3.00", 2f, 3f), ("3.00-6.00", 3f, 6f)];

        foreach (var (Label, Lo, Hi) in bands)
        {
            list.Add(new Kind($"01 crisp nearest-neighbour {Label}", (s, d, e, random) =>
            {
                var ppm = Range(random, Lo, Hi);
                var (isDark, columns, rows) = Pixels.Padded(s, qz);
                var (lum, w, h) = NearestNeighbourRenderer.Render(isDark, columns, rows, ppm, (float)random.NextDouble(), (float)random.NextDouble());
                return new Rendered(lum, w, h, ppm);
            }));
        }
        foreach (var (Label, Lo, Hi) in bands)
        {
            list.Add(new Kind($"02 anti-aliased path {Label}", (s, d, e, random) =>
            {
                var ppm = Range(random, Lo, Hi);
                var (isDark, columns, rows) = Pixels.Padded(s, qz);
                var (lum, w, h) = AntiAliasedRenderer.Render(isDark, columns, rows, ppm, (float)random.NextDouble(), (float)random.NextDouble());
                return new Rendered(lum, w, h, ppm);
            }));
        }
        foreach (var (Label, Lo, Hi) in new (string Label, float Lo, float Hi)[] { ("1.50-2.00", 1.5f, 2f), ("2.00-2.50", 2f, 2.5f), ("2.50-3.50", 2.5f, 3.5f) })
        {
            list.Add(new Kind($"03 bilinear upscale of 1px/module {Label}", (s, d, e, random) =>
            {
                var ppm = Range(random, Lo, Hi);
                var (isDark, columns, rows) = Pixels.Padded(s, qz);
                var (lum, w, h) = NearestNeighbourRenderer.Render(isDark, columns, rows, 1f, 0f, 0f);
                var (lum2, w2, h2) = Pixels.Resize(lum, w, h, (int)MathF.Round(w * ppm), (int)MathF.Round(h * ppm), new SKSamplingOptions(SKFilterMode.Linear));
                return new Rendered(lum2, w2, h2, ppm);
            }));
        }
        foreach (var (Label, Lo, Hi) in new (string Label, float Lo, float Hi)[] { ("1.50-2.50", 1.5f, 2.5f), ("2.50-4.00", 2.5f, 4f) })
        {
            list.Add(new Kind($"04 downscale of 8px/module (linear+mipmap) {Label}", (s, d, e, random) =>
            {
                var ppm = Range(random, Lo, Hi);
                var (isDark, columns, rows) = Pixels.Padded(s, qz);
                var (lum, w, h) = NearestNeighbourRenderer.Render(isDark, columns, rows, 8f, 0f, 0f);
                var (lum2, w2, h2) = Pixels.Resize(lum, w, h, (int)MathF.Round(w * ppm / 8f), (int)MathF.Round(h * ppm / 8f), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                return new Rendered(lum2, w2, h2, ppm);
            }));
        }
        list.Add(new Kind("05 right-angle turn, crisp 1.50-3.00", (s, d, e, random) =>
        {
            var ppm = Range(random, 1.5f, 3f);
            var (isDark, columns, rows) = Pixels.Padded(s, qz);
            var (lum, w, h) = NearestNeighbourRenderer.Render(isDark, columns, rows, ppm, (float)random.NextDouble(), (float)random.NextDouble());
            var (Luminance, Width, Height) = NearestNeighbourRenderer.Turn(lum, w, h, 1 + random.Next(3), mirror: false);
            return new Rendered(Luminance, Width, Height, ppm);
        }));
        list.Add(new Kind("06 mirrored (+ any right-angle turn), crisp 1.50-3.00", (s, d, e, random) =>
        {
            var ppm = Range(random, 1.5f, 3f);
            var (isDark, columns, rows) = Pixels.Padded(s, qz);
            var (lum, w, h) = NearestNeighbourRenderer.Render(isDark, columns, rows, ppm, (float)random.NextDouble(), (float)random.NextDouble());
            var (Luminance, Width, Height) = NearestNeighbourRenderer.Turn(lum, w, h, random.Next(4), mirror: true);
            return new Rendered(Luminance, Width, Height, ppm);
        }));
        // The supersampled renderer draws four quiet-zone modules round every symbology, Micro QR and rMQR included, where the kinds above draw their two.
        // It is left as the tests have it: these kinds measure what the rotation tests draw, and a wider quiet zone is within either specification.
        foreach (var (Label, Lo, Hi) in new (string Label, float Lo, float Hi)[] { ("2.00-3.00", 2f, 3f), ("3.00-6.00", 3f, 6f) })
        {
            list.Add(new Kind($"07 supersampled, any rotation {Label}", (s, d, e, random) =>
            {
                var ppm = Range(random, Lo, Hi);
                var (lum, w, h) = SupersampledRenderer.Render((r, c) => s[r, c], s.Width, s.Height, ppm, Range(random, 0f, 360f));
                return new Rendered(lum, w, h, ppm);
            }));
        }
        list.Add(new Kind("08 supersampled, any rotation, keystone <= 6 % 2.00-6.00", (s, d, e, random) =>
        {
            var ppm = Range(random, 2f, 6f);
            var (lum, w, h) = SupersampledRenderer.Render((r, c) => s[r, c], s.Width, s.Height, ppm, Range(random, 0f, 360f), Range(random, 0f, 0.06f));
            return new Rendered(lum, w, h, ppm);
        }));
        list.Add(new Kind("09 supersampled, any rotation, keystone 6-20 % 3.00-6.00", (s, d, e, random) =>
        {
            var ppm = Range(random, 3f, 6f);
            var (lum, w, h) = SupersampledRenderer.Render((r, c) => s[r, c], s.Width, s.Height, ppm, Range(random, 0f, 360f), Range(random, 0.06f, 0.20f));
            return new Rendered(lum, w, h, ppm);
        }));
        list.Add(new Kind("10 JPEG q50-90 round trip, crisp 2.00-4.00", (s, d, e, random) =>
        {
            var ppm = Range(random, 2f, 4f);
            var (isDark, columns, rows) = Pixels.Padded(s, qz);
            var (lum, w, h) = NearestNeighbourRenderer.Render(isDark, columns, rows, ppm, (float)random.NextDouble(), (float)random.NextDouble());
            using var bitmap = Pixels.ToGrayBitmap(lum, w, h);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 50 + random.Next(41));
            using var decoded = SKBitmap.Decode(data);
            var (Luminance, Width, Height) = Pixels.ToLuminance(decoded);
            return new Rendered(Luminance, Width, Height, ppm);
        }));
        list.Add(new Kind("11 the library's own image writer", (s, d, e, random) => e.NativeRender?.Invoke(d, s, random)));
        return [.. list];
    }
}
