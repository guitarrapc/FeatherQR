using TUnit.Assertions.Enums;
using FeatherQR.Internals.ImageDecoders;

namespace FeatherQR.Tests;

/// <summary>
/// Whichever <see cref="LuminanceConverter"/> vector tier this machine runs (the AVX2
/// kernel on x86-64, the NEON kernel on ARM64) against the portable per-pixel loop,
/// byte for byte, over every pixel layout the converter accepts, premultiplied and
/// straight alpha, and alpha shapes that pin the contract corners: fully opaque, fully
/// transparent, transparent-or-opaque only, and arbitrary partial alpha.
/// </summary>
/// <remarks>
/// Widths straddle every vector step of both kernels (8, 16 and 32) and include the
/// degenerate narrow cases, and rows are tested both tightly packed and padded, because
/// <c>SKPixmap.RowBytes</c> may exceed width × 4.
/// <para>
/// The alpha shapes are not decoration: each kernel routes them to different code. The
/// NEON tier alone has four row modes — no alpha, premultiplied, an optimistic pass for
/// fully opaque rows, and a per-block classifier that whitens or composites — and each
/// carries its own block and tail arithmetic, so a shape that is not exercised is a
/// kernel that is not tested.
/// </para>
/// </remarks>
public class LuminanceConverterParityTest
{
    private const int Bgra = 0;
    private const int Rgba = 1;
    private const int Rgb888x = 2;

    /// <summary>
    /// ARGB channel order. No vector kernel and none of the scalar tier's three
    /// constant-offset arms accept it, so it is the only layout that reaches
    /// <c>RowsGeneral</c> — shipped code that every other layout routes around.
    /// </summary>
    private const int Argb = 3;

    /// <summary>
    /// Widths at or above this reach a vector kernel on every machine that has one:
    /// AVX2 takes any width, and it is the NEON tier's block size. The coverage floors
    /// below are counted against it, so widening a tier's width gate fails a test
    /// instead of turning its cases into green skips.
    /// </summary>
    private const int AlwaysVectorWidth = 16;

    public static IEnumerable<int> Layouts() => [Bgra, Rgba, Rgb888x, Argb];

    private static (int R, int G, int B, int A) Offsets(int layout) => layout switch
    {
        Bgra => (2, 1, 0, 3),
        Rgba => (0, 1, 2, 3),
        Rgb888x => (0, 1, 2, -1),
        _ => (1, 2, 3, 0),
    };

    /// <summary>Alpha shapes: 0 opaque, 1 transparent, 2 transparent-or-opaque, 3 arbitrary partial.</summary>
    /// <summary>
    /// <paramref name="alphaOffset"/> matters: writing the shaped alpha at a fixed byte 3
    /// puts it on the BLUE channel for a layout whose alpha is byte 0, so the four alpha
    /// shapes degenerate into four samples of one shape.
    /// </summary>
    private static byte[] MakePixels(int width, int height, int rowBytes, int alphaShape, int seed, int alphaOffset = 3)
    {
        var colour = alphaOffset == 0 ? 1 : 0;
        var pixels = new byte[rowBytes * height];
        var state = (uint)seed * 2654435761u + 7u;
        for (var i = 0; i + 4 <= pixels.Length; i += 4)
        {
            state = state * 1664525u + 1013904223u;
            var r = state >> 8;
            // Mostly saturated black or white as a QR render is, plus mid-tones so the
            // channel weights actually matter.
            var mono = (r & 3) == 0 ? (byte)(r >> 16) : (byte)((r & 4) == 0 ? 0 : 255);
            pixels[i + colour] = mono;
            pixels[i + colour + 1] = (byte)(mono ^ (byte)((r >> 5) & 3));
            pixels[i + colour + 2] = (byte)(mono ^ (byte)((r >> 7) & 7));
            pixels[i + (alphaOffset < 0 ? 3 : alphaOffset)] = alphaShape switch
            {
                0 => 255,
                1 => 0,
                2 => (r & 3) == 0 ? (byte)0 : (byte)255,
                _ => (r & 15) == 0 ? (byte)(r >> 20) : (byte)255,
            };
        }
        return pixels;
    }

    /// <summary>
    /// The specification, written the obvious way: one pixel at a time, plain indexing,
    /// no specialization. Slow and not shipped — it exists so the shipped scalar tier
    /// has something to be checked against.
    /// </summary>
    private static byte[] NaiveReference(byte[] pixels, int width, int height, int rowBytes, int ro, int go, int bo, int ao, bool premultiplied)
    {
        var result = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = y * rowBytes + x * 4;
                int r = pixels[p + ro];
                int g = pixels[p + go];
                int b = pixels[p + bo];

                if (ao >= 0)
                {
                    int a = pixels[p + ao];
                    if (a != 255)
                    {
                        if (premultiplied)
                        {
                            r += 255 - a;
                            g += 255 - a;
                            b += 255 - a;
                        }
                        else
                        {
                            r = (r * a + 255 * (255 - a)) / 255;
                            g = (g * a + 255 * (255 - a)) / 255;
                            b = (b * a + 255 * (255 - a)) / 255;
                        }
                    }
                }

                result[y * width + x] = (byte)((77 * r + 150 * g + 29 * b) >> 8);
            }
        }
        return result;
    }

    /// <summary>
    /// The scalar tier against that naive reference. This is not redundant with the
    /// vector-parity test below: that one uses the scalar tier <em>as</em> its expected
    /// value, so once the scalar tier stopped being a plain per-pixel loop — it
    /// specializes per layout, walks by ref and takes four pixels per iteration — an
    /// error shared by both tiers would pass unnoticed. This test is what pins the
    /// scalar tier to the specification.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Layouts))]
    public async Task ScalarTier_MatchesNaiveReference_EveryLayoutAndAlphaShape(int layout)
    {
        var (ro, go, bo, ao) = Offsets(layout);
        // 4, 5, 7, 8 straddle the four-pixel step and its remainder.
        int[] widths = [1, 2, 3, 4, 5, 6, 7, 8, 9, 15, 16, 17, 43, 139, 376];
        var alphaShapes = layout == Rgb888x ? new[] { 3 } : [0, 1, 2, 3];

        foreach (var alphaShape in alphaShapes)
        {
            foreach (var premultiplied in ao < 0 ? new[] { false } : [false, true])
            {
                foreach (var width in widths)
                {
                    foreach (var height in new[] { 1, 2, 5 })
                    {
                        foreach (var pad in new[] { 0, 12 })
                        {
                            var rowBytes = width * 4 + pad;
                            var pixels = MakePixels(width, height, rowBytes, alphaShape, width * 7 + height + pad + alphaShape, ao);

                            var expected = NaiveReference(pixels, width, height, rowBytes, ro, go, bo, ao, premultiplied);

                            var actual = new byte[width * height];
                            actual.AsSpan().Fill(0xA5);
                            LuminanceConverter.ConvertRgbaForTest(pixels, actual, width, height, rowBytes, ro, go, bo, ao, premultiplied, LuminanceConverter.ConvertTier.Scalar);

                            await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching)
                                .Because($"layout {layout}, {width}x{height}, rowBytes {rowBytes}, alphaShape {alphaShape}, premultiplied {premultiplied}");
                        }
                    }
                }
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Layouts))]
    public async Task VectorTier_MatchesScalarTier_EveryLayoutAndAlphaShape(int layout)
    {
        if (!LuminanceConverter.IsAvx2TierSupported && !LuminanceConverter.IsAdvSimdTierSupported)
        {
            Skip.Test("No vector tier (AVX2 or NEON) on this machine");
            return;
        }

        var (ro, go, bo, ao) = Offsets(layout);
        if (!LuminanceConverter.IsVectorTierTakenForLayout(ro, go, bo))
        {
            Skip.Test($"No vector kernel converts layout {layout} here");
            return;
        }

        // 16, 17, 20 and 24 straddle the NEON block and its overlapping tail (a width of
        // 20 makes the final block redo 12 pixels the main loop already wrote). Widths
        // below 16 reach a vector kernel only on AVX2 — the NEON tier declines them — so
        // they are skipped rather than silently compared scalar-against-scalar.
        int[] widths = [1, 3, 4, 5, 7, 8, 9, 15, 16, 17, 20, 24, 31, 32, 33, 43, 63, 139, 376];
        int[] heights = [1, 2, 5];
        var covered = 0;
        var floor = 0;
        var alphaShapes = layout == Rgb888x ? new[] { 3 } : [0, 1, 2, 3];

        foreach (var alphaShape in alphaShapes)
        {
            foreach (var premultiplied in ao < 0 ? new[] { false } : [false, true])
            {
                foreach (var width in widths)
                {
                    if (width >= AlwaysVectorWidth)
                        floor += heights.Length * 2;
                    if (!LuminanceConverter.IsVectorTierTaken(width, ro, go, bo))
                        continue;

                    foreach (var height in heights)
                    {
                        foreach (var pad in new[] { 0, 12 })
                        {
                            covered++;
                            var rowBytes = width * 4 + pad;
                            var pixels = MakePixels(width, height, rowBytes, alphaShape, width * 7 + height + pad + alphaShape, ao);

                            var expected = new byte[width * height];
                            LuminanceConverter.ConvertRgbaForTest(pixels, expected, width, height, rowBytes, ro, go, bo, ao, premultiplied, LuminanceConverter.ConvertTier.Scalar);

                            // Poison the destination so a kernel that skips a pixel is caught.
                            var actual = new byte[width * height];
                            actual.AsSpan().Fill(0xA5);
                            LuminanceConverter.ConvertRgbaForTest(pixels, actual, width, height, rowBytes, ro, go, bo, ao, premultiplied, LuminanceConverter.ConvertTier.Vector);

                            await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching)
                                .Because($"layout {layout}, {width}x{height}, rowBytes {rowBytes}, alphaShape {alphaShape}, premultiplied {premultiplied}");
                        }
                    }
                }
            }
        }

        // A floor, not "> 0": every width at or above AlwaysVectorWidth must reach a
        // vector kernel on any machine that has one, so a widened width gate fails here
        // instead of quietly skipping its way to green.
        await Assert.That(covered).IsGreaterThanOrEqualTo(floor)
            .Because($"layout {layout}: only {covered} of at least {floor} cases reached a vector kernel");
    }

    /// <summary>
    /// The pin must refuse, not fall back. Without this the guard itself is untested:
    /// it can only fire where the tier is absent, which is exactly where every other
    /// test skips first.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Layouts))]
    public async Task PinningAnAbsentVectorTier_Throws(int layout)
    {
        const int Width = 8;
        const int Height = 2;
        var (ro, go, bo, ao) = Offsets(layout);
        if (LuminanceConverter.IsVectorTierTaken(Width, ro, go, bo))
        {
            Skip.Test($"A vector kernel does take width {Width} for layout {layout} here");
            return;
        }

        var rowBytes = Width * 4;
        var pixels = MakePixels(Width, Height, rowBytes, alphaShape: 0, seed: 9, ao);
        await Assert.That(() => LuminanceConverter.ConvertRgbaForTest(
            pixels, new byte[Width * Height], Width, Height, rowBytes, ro, go, bo, ao, false, LuminanceConverter.ConvertTier.Vector))
            .Throws<PlatformNotSupportedException>();
    }

    /// <summary>
    /// Every tier walks by ref with no bounds check of its own, so the extents are
    /// validated once at the entry point. `main` got this for free from per-row
    /// re-slicing; the ref walk that replaced it does not, and a short destination
    /// silently corrupted the bytes after it (in practice a pooled rental) instead of
    /// throwing.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Layouts))]
    public async Task UndersizedBuffers_Throw(int layout)
    {
        const int Width = 8;
        const int Height = 4;
        var (ro, go, bo, ao) = Offsets(layout);
        var rowBytes = Width * 4;
        var pixels = MakePixels(Width, Height, rowBytes, alphaShape: 0, seed: 5, ao);

        await Assert.That(() => LuminanceConverter.ConvertRgbaForTest(
            pixels, new byte[Width * Height - 1], Width, Height, rowBytes, ro, go, bo, ao, false, LuminanceConverter.ConvertTier.Scalar))
            .Throws<ArgumentException>();

        await Assert.That(() => LuminanceConverter.ConvertRgbaForTest(
            pixels.AsSpan(0, rowBytes * Height - 1).ToArray(), new byte[Width * Height], Width, Height, rowBytes, ro, go, bo, ao, false, LuminanceConverter.ConvertTier.Scalar))
            .Throws<ArgumentException>();
    }

    /// <summary>
    /// The extent check itself must not overflow. Both products it forms —
    /// width × height and (height − 1) × rowBytes + width × 4 — exceed <see cref="int"/>
    /// for dimensions that are still perfectly representable, and an <c>int</c> multiply
    /// wraps them to a small (or negative) number that any buffer satisfies. The guard
    /// then waves the call through to a tier that walks by ref with no bounds check of
    /// its own, so the wrap does not degrade to a slow path, it reads and writes
    /// gigabytes past both buffers.
    /// </summary>
    /// <remarks>
    /// The dimensions are chosen so the wrapped product lands near zero rather than
    /// merely somewhere smaller: 65536 × 65536 is exactly 2^32, and 999999 × 4295 is
    /// 2^32 + 28409. Neither case allocates the buffers those numbers describe — the
    /// point is that the call is rejected before anything is touched.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(Layouts))]
    public async Task OverflowingExtents_Throw(int layout)
    {
        var (ro, go, bo, ao) = Offsets(layout);

        // width × height wraps to 0, so any destination looks large enough
        await Assert.That(() => LuminanceConverter.ConvertRgbaForTest(
            new byte[64], new byte[64], 65536, 65536, 65536 * 4, ro, go, bo, ao, false, LuminanceConverter.ConvertTier.Scalar))
            .Throws<ArgumentException>();

        // width × height fits and the destination is honestly sized; only the pixel
        // extent wraps, to 28425 bytes against a real requirement of ~4 GB
        await Assert.That(() => LuminanceConverter.ConvertRgbaForTest(
            new byte[28425], new byte[4 * 1_000_000], 4, 1_000_000, 4295, ro, go, bo, ao, false, LuminanceConverter.ConvertTier.Scalar))
            .Throws<ArgumentException>();
    }

    /// <summary>
    /// The vector loops write a whole block of luminance bytes at a time (32 on AVX2,
    /// 16 on NEON) and finish the row with an overlapping block, so a block-count that
    /// rounds the wrong way writes past the caller's destination. The destination is
    /// given a poisoned tail beyond the bytes the converter owns, and that tail must
    /// come back untouched.
    /// </summary>
    /// <remarks>
    /// Comparing only the first width × height bytes cannot see this: an over-write
    /// lands past the compared region and every asserted byte is still correct.
    /// Verified by mutation — rounding <c>blockEnd</c> up in
    /// <c>LuminanceConverter.X86.cs</c> leaves the parity assertions green and is
    /// caught only by the tail.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(Layouts))]
    public async Task VectorTier_WritesNothingPastTheDestination(int layout)
    {
        if (!LuminanceConverter.IsAvx2TierSupported && !LuminanceConverter.IsAdvSimdTierSupported)
        {
            Skip.Test("No vector tier (AVX2 or NEON) on this machine");
            return;
        }

        const byte Poison = 0x5A;
        const int TailBytes = 64;

        var (ro, go, bo, ao) = Offsets(layout);
        if (!LuminanceConverter.IsVectorTierTakenForLayout(ro, go, bo))
        {
            Skip.Test($"No vector kernel converts layout {layout} here");
            return;
        }

        var covered = 0;
        var floor = 0;
        // Every alpha shape, not just one per alpha mode: the straight-alpha and
        // premultiplied kernels are separate loops with their own block arithmetic, and
        // within straight alpha the opaque, whitening and compositing paths are three
        // more (verified by mutation — an over-run in an uncovered path passed the whole
        // class). Shape 0 is opaque, 2 is transparent-or-opaque (whitening), 3 is
        // arbitrary partial alpha (compositing).
        var alphaModes = ao < 0 ? new[] { false } : [false, true];

        foreach (var premultiplied in alphaModes)
        {
            foreach (var alphaShape in ao < 0 ? new[] { 3 } : [0, 1, 2, 3])
            {
                foreach (var width in new[] { 1, 7, 8, 9, 16, 17, 20, 24, 31, 32, 33, 40, 139, 376 })
                {
                    if (width >= AlwaysVectorWidth)
                        floor += 4;
                    if (!LuminanceConverter.IsVectorTierTaken(width, ro, go, bo))
                        continue;

                    foreach (var height in new[] { 1, 3 })
                        // A padded row as well as a tight one: the exactly-sized source below is
                        // what proves the last block does not read into the next row's padding.
                        foreach (var pad in new[] { 0, 12 })
                        {
                            covered++;
                            var rowBytes = width * 4 + pad;
                            var pixels = MakePixels(width, height, rowBytes, alphaShape, width + alphaShape, ao);

                            var expected = new byte[width * height];
                            LuminanceConverter.ConvertRgbaForTest(pixels, expected, width, height, rowBytes, ro, go, bo, ao, premultiplied, LuminanceConverter.ConvertTier.Scalar);

                            // Destination is longer than the converter's region; only the first
                            // width × height bytes are handed over as its span.
                            var backing = new byte[width * height + TailBytes];
                            backing.AsSpan().Fill(Poison);
                            var destination = backing.AsSpan(0, width * height);

                            // Exactly sized source too, so an overread past rowBytes × height is
                            // not silently satisfied by the next object on the heap.
                            var exact = pixels.AsSpan(0, rowBytes * height);
                            LuminanceConverter.ConvertRgbaForTest(exact, destination, width, height, rowBytes, ro, go, bo, ao, premultiplied, LuminanceConverter.ConvertTier.Vector);

                            await Assert.That(backing.AsSpan(0, width * height).ToArray())
                                .IsEquivalentTo(expected, CollectionOrdering.Matching)
                                .Because($"layout {layout}, {width}x{height}, alphaShape {alphaShape}, premultiplied {premultiplied}");

                            for (var i = width * height; i < backing.Length; i++)
                            {
                                await Assert.That(backing[i]).IsEqualTo(Poison)
                                    .Because($"layout {layout}, {width}x{height}, rowBytes {rowBytes}, alphaShape {alphaShape}, premultiplied {premultiplied}: wrote {i - width * height + 1} byte(s) past the destination");
                            }
                        }
                }
            }
        }

        await Assert.That(covered).IsGreaterThanOrEqualTo(floor)
            .Because($"layout {layout}: only {covered} of at least {floor} cases reached a vector kernel");
    }

    /// <summary>
    /// One alpha shape per ROW rather than per image, so the straight-alpha kernel's row
    /// mode actually changes mid-image.
    /// </summary>
    /// <remarks>
    /// Every other case in this file fills a whole image from ONE alpha shape, so the
    /// NEON tier's row mode is decided on row 0 and, whichever way it goes, the decision
    /// is never revisited on a row that would decide differently. Mixing the shapes by
    /// row is what makes the transitions themselves a designed case: an optimistic row
    /// that turns out to carry alpha and must be redone by the classifier at y &gt; 0, an
    /// opaque row reached THROUGH the classifier, and the sticky composite-only mode
    /// latching on a later row and then having to stay exact over an opaque one.
    /// <para>
    /// The row order below walks all of them: opaque (stays optimistic),
    /// transparent-or-opaque (optimistic falls, classifier whitens, sticky mode stays
    /// off), opaque again (classified all-255 block), partial (classifier composites and
    /// latches the sticky mode), opaque again (composite-only over an opaque row), and
    /// partial once more.
    /// </para>
    /// <para>
    /// Honest about what this adds: the row modes are fail-SAFE by construction — every
    /// mode but the optimistic one is a specialization of the exact composite, so picking
    /// the wrong mode costs speed, not bytes. The one mode that can be wrong is the
    /// optimistic pass, and its accept test is per row. Verified by mutation: hoisting
    /// the row/dest refs out of the row loop, and deciding the row mode once from row 0
    /// (<c>y &gt; 0 || opaque</c>), both fail here — but both also happen to fail the
    /// uniform test above, on the single 16x5 partial-alpha case whose first row the
    /// generator happens to make opaque. That is luck of the seed, not coverage; this
    /// test is what makes the transitions deliberate.
    /// </para>
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(Layouts))]
    public async Task VectorTier_MatchesScalarTier_WhenTheAlphaShapeChangesPerRow(int layout)
    {
        if (!LuminanceConverter.IsAvx2TierSupported && !LuminanceConverter.IsAdvSimdTierSupported)
        {
            Skip.Test("No vector tier (AVX2 or NEON) on this machine");
            return;
        }

        var (ro, go, bo, ao) = Offsets(layout);
        if (!LuminanceConverter.IsVectorTierTakenForLayout(ro, go, bo))
        {
            Skip.Test($"No vector kernel converts layout {layout} here");
            return;
        }
        if (ao < 0)
        {
            Skip.Test("Rgb888x carries no alpha, so it has no row modes to change");
            return;
        }

        // The row modes latch, so the sequence is walked in order and also rotated: a
        // shape that only ever appears after the sticky mode is on would otherwise never
        // be seen by the classifier at all.
        int[][] rowShapes =
        [
            [0, 2, 0, 3, 0, 3],
            [0, 0, 3, 2, 0, 1],
            [3, 0, 0, 2, 1, 0],
            [1, 0, 2, 0, 3, 0],
        ];

        var covered = 0;
        var floor = 0;
        foreach (var premultiplied in new[] { false, true })
        {
            foreach (var shapes in rowShapes)
            {
                foreach (var width in new[] { 16, 17, 20, 24, 32, 33, 43, 139 })
                {
                    floor += 2; // every width here is at or above AlwaysVectorWidth
                    if (!LuminanceConverter.IsVectorTierTaken(width, ro, go, bo))
                        continue;

                    foreach (var pad in new[] { 0, 12 })
                    {
                        covered++;
                        var height = shapes.Length;
                        var rowBytes = width * 4 + pad;
                        var pixels = new byte[rowBytes * height];
                        for (var y = 0; y < height; y++)
                        {
                            var row = MakePixels(width, 1, rowBytes, shapes[y], width * 13 + y * 7 + pad, ao);
                            row.AsSpan(0, rowBytes).CopyTo(pixels.AsSpan(y * rowBytes));
                        }

                        var expected = new byte[width * height];
                        LuminanceConverter.ConvertRgbaForTest(pixels, expected, width, height, rowBytes, ro, go, bo, ao, premultiplied, LuminanceConverter.ConvertTier.Scalar);

                        var actual = new byte[width * height];
                        actual.AsSpan().Fill(0xA5);
                        LuminanceConverter.ConvertRgbaForTest(pixels, actual, width, height, rowBytes, ro, go, bo, ao, premultiplied, LuminanceConverter.ConvertTier.Vector);

                        await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching)
                            .Because($"layout {layout}, {width}x{height}, rowBytes {rowBytes}, rowShapes [{string.Join(",", shapes)}], premultiplied {premultiplied}");
                    }
                }
            }
        }

        await Assert.That(covered).IsGreaterThanOrEqualTo(floor)
            .Because($"layout {layout}: only {covered} of at least {floor} cases reached a vector kernel");
    }
}
