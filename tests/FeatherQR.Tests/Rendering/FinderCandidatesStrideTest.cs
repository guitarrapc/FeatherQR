using SkiaSharp;
using FeatherQR.SkiaSharp.Internals;
using FeatherQR.SkiaSharp;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// <see cref="FinderPatternFinder.FindCandidates"/> (strided) against
/// <see cref="FinderPatternFinder.FindCandidatesFullSweep"/> on real renders across
/// the documented module-size envelope.
/// </summary>
/// <remarks>
/// Striding is not bit-identical by construction: fewer rows merge into a candidate,
/// so its sub-pixel centre and its <c>Count</c> both move. The invariant that matters
/// to the decoders is that no *confirmed* candidate of a full sweep disappears, since
/// that is what they rank and try.
/// <para>
/// The load-bearing case is a rotated symbol with a second finder-like pattern in the
/// frame. Rotation narrows the band of rows that survives the cross-checks, and a
/// second pattern removes any image-wide "we found something" signal that a fallback
/// could key on — so this is where the stride has to stand on its own. It is also the
/// exact shape that a confirmation-triggered fallback got wrong: the decoy confirmed
/// on its own and suppressed the pass the real symbol needed.
/// </para>
/// </remarks>
public class FinderCandidatesStrideTest
{
    public static IEnumerable<int> ModulePixelSizes() => [3, 4, 5, 8, 13];

    private static (byte[] Luminance, int Width, int Height, byte Threshold) ToLuminance(SKBitmap bitmap)
    {
        var luminance = new byte[bitmap.Width * bitmap.Height];
        BitmapLuminanceConverter.Convert(bitmap, luminance);
        return (luminance, bitmap.Width, bitmap.Height, Binarizer.ComputeOtsuThreshold(luminance));
    }

    private static (byte[] Luminance, int Width, int Height, byte Threshold) Render(RmQRCodeData data, int modulePixelSize)
    {
        using var bitmap = new RmQRCodeImageBuilder(data).WithModulePixelSize(modulePixelSize).ToBitmap();
        return ToLuminance(bitmap);
    }

    /// <summary>Draws a bare 7x7 finder pattern: dark ring, light ring, 3x3 dark core.</summary>
    private static void DrawDecoyFinder(SKCanvas canvas, float x, float y, float modulePixelSize)
    {
        using var dark = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        using var light = new SKPaint { Color = SKColors.White, IsAntialias = false };
        canvas.DrawRect(SKRect.Create(x, y, 7 * modulePixelSize, 7 * modulePixelSize), dark);
        canvas.DrawRect(SKRect.Create(x + modulePixelSize, y + modulePixelSize, 5 * modulePixelSize, 5 * modulePixelSize), light);
        canvas.DrawRect(SKRect.Create(x + 2 * modulePixelSize, y + 2 * modulePixelSize, 3 * modulePixelSize, 3 * modulePixelSize), dark);
    }

    /// <summary>
    /// Renders the symbol rotated by <paramref name="degrees"/> into a larger canvas,
    /// optionally with three unrelated finder patterns in the margin.
    /// </summary>
    private static SKBitmap RenderRotated(RmQRCodeData data, int modulePixelSize, float degrees, bool withDecoys)
    {
        var widthPx = data.Width * modulePixelSize;
        var heightPx = data.Height * modulePixelSize;
        var canvasW = widthPx + heightPx + 8 * 12 + 40;
        var canvasH = widthPx + heightPx + 8 * 12 + 40;

        var bitmap = new SKBitmap(canvasW, canvasH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        if (withDecoys)
        {
            // 12 px/module, comfortably above the symbol's own module size, so each
            // decoy is confirmed on many rows by the strided pass.
            DrawDecoyFinder(canvas, 8, 8, 12);
            DrawDecoyFinder(canvas, canvasW - 8 - 7 * 12, 8, 12);
            DrawDecoyFinder(canvas, 8, canvasH - 8 - 7 * 12, 12);
        }

        canvas.Save();
        canvas.Translate(canvasW / 2f, canvasH / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-widthPx / 2f, -heightPx / 2f);
        SymbolRenderer.Render(canvas, SKRect.Create(0, 0, widthPx, heightPx), data, SKColors.Black, SKColors.White);
        canvas.Restore();
        canvas.Flush();
        return bitmap;
    }

    /// <summary>
    /// Every candidate a full sweep confirmed must still be found by the strided pass,
    /// within a tolerance that allows the merged centre to move but not the candidate
    /// to disappear.
    /// </summary>
    private static bool KeptEveryConfirmedCandidate(
        ReadOnlySpan<FinderPattern> full, int fullCount,
        ReadOnlySpan<FinderPattern> strided, int stridedCount,
        float centreTolerance, float moduleTolerance, out string lost)
    {
        for (var i = 0; i < fullCount; i++)
        {
            if (full[i].Count < 2)
                continue;

            var kept = false;
            for (var j = 0; j < stridedCount; j++)
            {
                if (Math.Abs(strided[j].X - full[i].X) <= centreTolerance
                    && Math.Abs(strided[j].Y - full[i].Y) <= centreTolerance
                    && Math.Abs(strided[j].ModuleSize - full[i].ModuleSize) <= moduleTolerance)
                {
                    kept = true;
                    break;
                }
            }

            if (!kept)
            {
                lost = $"({full[i].X}, {full[i].Y}) module {full[i].ModuleSize} seen on {full[i].Count} rows";
                return false;
            }
        }

        lost = string.Empty;
        return true;
    }

    [Test]
    [MethodDataSource(nameof(ModulePixelSizes))]
    public async Task StridedPath_KeepsEveryConfirmedCandidateOfAFullSweep(int modulePixelSize)
    {
        // Both extremes of the rMQR shape range: the narrowest and the widest symbol.
        var symbols = new[]
        {
            RmQRCodeGenerator.Create("RMQR 43", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 }),
            RmQRCodeGenerator.Create("27", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x27 }),
            RmQRCodeGenerator.Create(string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog?! ", 4)).Substring(0, 150), RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R17x139 }),
        };

        foreach (var data in symbols)
        {
            var (luminance, width, height, threshold) = Render(data, modulePixelSize);

            var full = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
            var fullCount = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, full);

            var strided = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
            var stridedCount = FinderPatternFinder.FindCandidates(luminance, width, height, threshold, strided);

            await Assert.That(fullCount).IsGreaterThan(0)
                .Because($"{data.Version} at {modulePixelSize} px/module must have a findable finder at all");

            var kept = KeptEveryConfirmedCandidate(full, fullCount, strided, stridedCount, 1f, 0.75f, out var lost);
            await Assert.That(kept).IsTrue()
                .Because($"{data.Version} at {modulePixelSize} px/module: strided scan lost the confirmed candidate at {lost}");
        }
    }

    /// <summary>
    /// The stride has to land in the band on its own, at every rotation and with other
    /// finder-like patterns in frame. This is the case a fallback cannot rescue: a
    /// decoy satisfies any image-wide confirmation signal on the real symbol's behalf,
    /// so if the stride steps over the symbol's band the symbol is simply gone.
    /// </summary>
    [Test]
    [Arguments(RmQRVersion.R7x43, "RMQR 43", 3)]
    [Arguments(RmQRVersion.R7x43, "RMQR 43", 4)]
    [Arguments(RmQRVersion.R11x27, "27", 3)]
    public async Task RotatedSymbol_AlongsideOtherFinderPatterns_KeepsEveryConfirmedCandidate(
        RmQRVersion version, string content, int modulePixelSize)
    {
        var data = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });

        for (var degrees = 0; degrees < 90; degrees++)
        {
            using var bitmap = RenderRotated(data, modulePixelSize, degrees, withDecoys: true);
            var (luminance, width, height, threshold) = ToLuminance(bitmap);

            var full = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
            var fullCount = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, threshold, full);

            var strided = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
            var stridedCount = FinderPatternFinder.FindCandidates(luminance, width, height, threshold, strided);

            // Looser than the axis-aligned case: under rotation the strided pass merges
            // a shorter run of rows, so the centre of gravity moves further.
            var kept = KeptEveryConfirmedCandidate(full, fullCount, strided, stridedCount, 2f, 1f, out var lost);
            await Assert.That(kept).IsTrue()
                .Because($"{version} at {modulePixelSize} px/module rotated {degrees} deg with 3 decoy finders: strided scan lost the confirmed candidate at {lost}");
        }
    }

    /// <summary>
    /// A pattern the strided pass confirms elsewhere in the image must not stand in for
    /// the symbol's own detection: the same frame with and without the decoys has to
    /// yield the same candidate for the symbol.
    /// </summary>
    [Test]
    public async Task DecoyPatterns_DoNotChangeWhatIsFoundForTheSymbol()
    {
        var data = RmQRCodeGenerator.Create("RMQR 43", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R7x43 });

        for (var degrees = 0; degrees < 90; degrees += 3)
        {
            using var withDecoys = RenderRotated(data, 3, degrees, withDecoys: true);
            using var withoutDecoys = RenderRotated(data, 3, degrees, withDecoys: false);

            var (lumA, wA, hA, tA) = ToLuminance(withoutDecoys);
            var alone = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
            var aloneCount = FinderPatternFinder.FindCandidates(lumA, wA, hA, tA, alone);

            var (lumB, wB, hB, tB) = ToLuminance(withDecoys);
            var mixed = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
            var mixedCount = FinderPatternFinder.FindCandidates(lumB, wB, hB, tB, mixed);

            for (var i = 0; i < aloneCount; i++)
            {
                var found = false;
                for (var j = 0; j < mixedCount; j++)
                {
                    if (Math.Abs(mixed[j].X - alone[i].X) <= 2f && Math.Abs(mixed[j].Y - alone[i].Y) <= 2f)
                    {
                        found = true;
                        break;
                    }
                }
                await Assert.That(found).IsTrue()
                    .Because($"rotated {degrees} deg: candidate ({alone[i].X}, {alone[i].Y}) is found alone but not with decoy finders in frame");
            }
        }
    }

    /// <summary>
    /// End-to-end cover for the same scenario through the public API: a symbol that
    /// decodes alone must still decode with other finder patterns in frame.
    /// </summary>
    /// <remarks>
    /// The sharp detector for a too-coarse stride is the candidate-level test above —
    /// this one stays green at stride 6 even with the retry disabled, because these
    /// particular renders happen to decode anyway. It is here so the public path has
    /// coverage of the decoy scenario at all, not as the stride's regression pin.
    /// </remarks>
    [Test]
    [Arguments(RmQRVersion.R7x43, "RMQR 43", 3)]
    [Arguments(RmQRVersion.R7x43, "RMQR 43", 4)]
    [Arguments(RmQRVersion.R11x27, "27", 3)]
    public async Task DecodeThroughThePublicApi_SurvivesEveryRotation_WithOtherFinderPatternsInFrame(
        RmQRVersion version, string content, int modulePixelSize)
    {
        var data = RmQRCodeGenerator.Create(content, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = version });
        var buffer = new char[RmQRCodeDecoder.GetMaxDecodedLength(version)];

        for (var degrees = 0; degrees < 90; degrees++)
        {
            using var alone = RenderRotated(data, modulePixelSize, degrees, withDecoys: false);
            var (lumA, wA, hA, _) = ToLuminance(alone);
            var decodesAlone = RmQRCodeDecoder.TryDecodeImage(lumA, wA, hA, buffer, out var aloneChars, out _);

            if (!decodesAlone)
                continue; // outside what this render can express at all; nothing to preserve

            using var mixed = RenderRotated(data, modulePixelSize, degrees, withDecoys: true);
            var (lumB, wB, hB, _) = ToLuminance(mixed);
            var decodesMixed = RmQRCodeDecoder.TryDecodeImage(lumB, wB, hB, buffer, out var mixedChars, out var mixedInfo);

            await Assert.That(decodesMixed).IsTrue()
                .Because($"{version} at {modulePixelSize} px/module rotated {degrees} deg decodes alone but not with three decoy finder patterns in frame (status {mixedInfo.Status})");
            await Assert.That(mixedChars).IsEqualTo(aloneChars);
        }
    }

    /// <summary>An image with nothing in it yields no candidate on either path.</summary>
    [Test]
    public async Task EmptyImage_YieldsNoCandidateOnEitherPath()
    {
        const int width = 320;
        const int height = 200;
        var luminance = new byte[width * height];
        luminance.AsSpan().Fill(255);

        var full = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var fullCount = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, 128, full);

        var strided = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var stridedCount = FinderPatternFinder.FindCandidates(luminance, width, height, 128, strided);

        await Assert.That(fullCount).IsEqualTo(0);
        await Assert.That(stridedCount).IsEqualTo(0);
    }

    /// <summary>
    /// The strided pass must never invent a candidate a full sweep does not see: it
    /// scans a subset of the rows, so its candidates are a subset of the sweep's.
    /// </summary>
    [Test]
    public async Task NoiseImage_ProducesNoCandidateAFullSweepDoesNotAlsoFind()
    {
        const int width = 320;
        const int height = 200;
        var luminance = new byte[width * height];
        var state = 12345u;
        for (var i = 0; i < luminance.Length; i++)
        {
            state = state * 1664525u + 1013904223u;
            luminance[i] = (byte)(state >> 24);
        }

        var full = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var fullCount = FinderPatternFinder.FindCandidatesFullSweep(luminance, width, height, 128, full);

        var strided = new FinderPattern[FinderPatternFinder.MaxFinderCandidates];
        var stridedCount = FinderPatternFinder.FindCandidates(luminance, width, height, 128, strided);

        for (var i = 0; i < stridedCount; i++)
        {
            var matched = false;
            for (var j = 0; j < fullCount; j++)
            {
                if (Math.Abs(full[j].X - strided[i].X) <= 1f && Math.Abs(full[j].Y - strided[i].Y) <= 1f)
                {
                    matched = true;
                    break;
                }
            }
            await Assert.That(matched).IsTrue()
                .Because($"noise image: strided candidate ({strided[i].X}, {strided[i].Y}) is not seen by a full sweep");
        }
    }
}
