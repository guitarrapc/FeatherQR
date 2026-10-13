using FeatherQR.Internals.ImageDecoders;
using FeatherQR.SkiaSharp;
using FeatherQR.SkiaSharp.Internals;

namespace FeatherQR.Tests;

/// <summary>
/// The selection from a list the caller keeps (<see cref="FinderPatternFinder.FindCandidatesStrided"/>, then <see cref="FinderPatternFinder.TrySelectFromList"/>) is the search's own (<see cref="FinderPatternFinder.TryFind(ReadOnlySpan{byte}, int, int, byte, Span{FinderPattern}, in GreyLevels, Span{FinderPattern}, out int, out bool)"/>): the same triple, the same candidates in the same order, and the skipped rows scanned in the same cases.
/// The list is longer than the search's, so its first <see cref="FinderPatternFinder.MaxFinderCandidates"/> are held to the search's whole list.
/// </summary>
public class FinderKeptListParityTest
{
    private const int ListLength = 512;

    public static IEnumerable<Func<(string Name, byte[] Luminance, int Width, int Height)>> Scenes()
    {
        // One symbol: the strided scan's selection stands
        foreach (var (version, pixels, degrees, keystone) in new[] { (1, 4f, 0f, 0f), (2, 3f, 30f, 0f), (6, 4f, 0f, 0.2f), (14, 3f, 17f, 0f), (25, 4f, 23f, 0.15f), (40, 3f, 0f, 0f), (40, 8f, 0f, 0f) })
        {
            yield return () =>
            {
                var qr = QRCodeGenerator.Create($"FQR KEPT v{version}", QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(version), QuietZoneSize = 0 });
                var (luminance, side) = SupersampledRenderer.Render(qr, pixels, degrees, keystone);
                return ($"v{version} {pixels}px {degrees}deg {keystone}", luminance, side, side);
            };
        }

        // Many symbols: more finders than the search's list holds, and selections that span symbols
        foreach (var (across, down, keystone) in new[] { (2, 2, 0f), (3, 3, 0.12f), (4, 4, 0f), (6, 10, 0f) })
        {
            yield return () =>
            {
                var (luminance, width, height, _) = TripleSelectionDecodeTest.RenderGrid(across, down, keystone, static i => $"FQR KEPT {i}");
                return ($"grid {across}x{down} {keystone}", luminance, width, height);
            };
        }

        // A real finder seen on one row beside a false candidate confirmed on two: the confirmed three are a doubtful triple, and the search scans the skipped rows before it chooses (FinderPatternSelectionTest)
        foreach (var (version, level, pixels, content) in new[] { (19, QREccLevel.H, 195, "HELLO WORLD"), (34, QREccLevel.M, 312, "HELLO") })
        {
            yield return () =>
            {
                var qr = QRCodeGenerator.Create(content, level, new QRCodeGeneratorOptions { Version = version });
                using var bitmap = new QRCodeImageBuilder(qr).WithSize(pixels, pixels).ToBitmap();
                var luminance = new byte[bitmap.Width * bitmap.Height];
                BitmapLuminanceConverter.Convert(bitmap, luminance);
                return ($"one-row finder v{version}", luminance, bitmap.Width, bitmap.Height);
            };
        }

        // A keystoned triple that scores poorly, and a false candidate the sweep confirms over less height: the strided triple stands (FinderPatternSelectionTest)
        foreach (var (content, level, version, pixels, degrees, keystone) in new[]
        {
            ("FQR40080", QREccLevel.L, 25, 5.8564177f, 343.29602f, 0.13052568f), ("FQR96565", QREccLevel.Q, 21, 4.240097f, 308.87686f, 0.15028752f),
            ("FQR83466", QREccLevel.M, 14, 4.7899146f, 174.98384f, 0.18623035f), ("FQR82781", QREccLevel.L, 24, 4.98759f, 268.47095f, 0.16650929f),
        })
        {
            yield return () =>
            {
                var qr = QRCodeGenerator.Create(content, level, new QRCodeGeneratorOptions { Version = version, QuietZoneSize = 0 });
                var (luminance, side) = SupersampledRenderer.Render(qr, pixels, degrees, keystone);
                return ($"swept false candidate v{version}", luminance, side, side);
            };
        }

        // A stride that crosses two finders of the symbol once and a lone one: the search returns its strided triple, rows unscanned
        yield return () =>
        {
            var (luminance, width, height, _) = TripleSelectionDecodeTest.RenderTall(destroyData: false);
            return ("tall", luminance, width, height);
        };

        // No symbol: the search scans every row before it gives up
        foreach (var block in new[] { 1, 2, 3, 4, 6 })
        {
            yield return () =>
            {
                const int Side = 300;
                var random = new Random(4711 + block);
                var luminance = new byte[Side * Side];
                for (var y = 0; y < Side; y += block)
                {
                    for (var x = 0; x < Side; x += block)
                    {
                        var value = random.Next(2) == 0 ? (byte)0 : (byte)255;
                        for (var dy = 0; dy < block && y + dy < Side; dy++)
                            luminance.AsSpan((y + dy) * Side + x, Math.Min(block, Side - x)).Fill(value);
                    }
                }
                return ($"noise {block}px", luminance, Side, Side);
            };
        }
    }

    [Test]
    [MethodDataSource(nameof(Scenes))]
    public async Task SelectionFromAKeptList_IsTheSearchsOwn(string name, byte[] luminance, int width, int height)
    {
        var threshold = Binarizer.ComputeOtsuThreshold(luminance, out var grey);

        var searched = new FinderPattern[3];
        var held = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var found = FinderPatternFinder.TryFind(luminance, width, height, threshold, searched, grey, held, out var heldCount, out var everyRowScanned);

        var selected = new FinderPattern[3];
        var kept = new FinderPattern[ListLength];
        var keptCount = FinderPatternFinder.FindCandidatesStrided(luminance, width, height, threshold, kept, grey);
        var keptEveryRow = false;
        var keptFound = FinderPatternFinder.TrySelectFromList(luminance, width, height, threshold, grey, kept, ref keptCount, ref keptEveryRow, selected);

        await Assert.That(keptFound).IsEqualTo(found).Because(name);
        await Assert.That(keptEveryRow).IsEqualTo(everyRowScanned).Because(name);
        await Assert.That(keptCount).IsLessThan(ListLength).Because($"{name}: the kept list has room left, so it left nothing out");
        await Assert.That(Math.Min(keptCount, FinderPatternFinder.MaxFinderCandidates)).IsEqualTo(heldCount).Because(name);
        for (var i = 0; i < heldCount; i++)
            await Assert.That(Fields(kept[i])).IsEqualTo(Fields(held[i])).Because($"{name}: candidate {i}");
        if (found)
        {
            for (var i = 0; i < 3; i++)
                await Assert.That(Fields(selected[i])).IsEqualTo(Fields(searched[i])).Because($"{name}: finder {i} of the triple");
        }
    }

    private static (float X, float Y, float ModuleSize, int Count) Fields(FinderPattern pattern) => (pattern.X, pattern.Y, pattern.ModuleSize, pattern.Count);
}
