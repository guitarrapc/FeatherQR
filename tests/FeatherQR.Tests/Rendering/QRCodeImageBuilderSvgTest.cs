using System.Globalization;
using SkiaSharp;
using FeatherQR.SkiaSharp.Internals;
using System.Buffers;
using System.Text;
using System.Xml.Linq;
using FeatherQR.SkiaSharp;

namespace FeatherQR.Tests;

/// <summary>
/// SVG output goes through <see cref="SKSvgCanvas"/> instead of a raster surface,
/// sharing the same layout and render pipeline as raster output. These tests pin
/// the SVG document structure (viewport, background, modules) and verify that
/// builder options (colors, gradients, icons, layout) are reflected in the output.
/// </summary>
public class QRCodeImageBuilderSvgTest
{
    private const string TestContent = "https://example.com/svg-test";

    [Test]
    public async Task SaveToSvg_ProducesWellFormedSvgDocument()
    {
        using var stream = new MemoryStream();
        new QRCodeImageBuilder(TestContent)
            .WithSize(512, 512)
            .SaveToSvg(stream);

        var doc = ParseSvg(stream.ToArray());
        await Assert.That(doc.Root!.Name.LocalName).IsEquivalentTo("svg");
        await Assert.That(doc.Root.Attribute("width")?.Value).IsEquivalentTo("512");
        await Assert.That(doc.Root.Attribute("height")?.Value).IsEquivalentTo("512");

        // Background rect plus dark module rects must be present.
        var ns = doc.Root.Name.Namespace;
        var rects = doc.Descendants(ns + "rect").ToArray();
        await Assert.That(rects.Length > 1).IsTrue().Because($"Expected background + module rects, got {rects.Length}.");
        await Assert.That(rects[0].Attribute("fill")?.Value).IsEquivalentTo("white");
    }

    [Test]
    public async Task SaveToSvg_RootHasViewBox_SoDocumentScalesWhenEmbedded()
    {
        // Without viewBox, an SVG embedded at a different size (img/CSS) keeps its
        // content at original coordinates instead of scaling.
        var svg = new QRCodeImageBuilder(TestContent)
            .WithSize(512, 512)
            .ToSvgString();

        var doc = XDocument.Parse(svg);
        await Assert.That(doc.Root!.Attribute("viewBox")?.Value).IsEquivalentTo("0 0 512 512");
    }

    [Test]
    public async Task SaveToSvg_PlainRectModules_UseCrispEdges()
    {
        // Default rect modules get shape-rendering=crispEdges to avoid antialiasing
        // seams between adjacent modules at non-integer display sizes.
        var svg = new QRCodeImageBuilder(TestContent)
            .WithSize(512, 512)
            .ToSvgString();

        var doc = XDocument.Parse(svg);
        await Assert.That(doc.Root!.Attribute("shape-rendering")?.Value).IsEquivalentTo("crispEdges");
    }

    [Test]
    public async Task SaveToSvg_NamedFinderShape_OpaqueBackground_KeepsTheTopLeftFinderDark()
    {
        // The opaque case, which is the one callers are told to use. On a non-opaque background the same
        // symbol loses its finder patterns entirely, which the test below pins.
        var svg = new QRCodeImageBuilder(TestContent)
            .WithSize(512, 512)
            .WithBackgroundColor(SKColors.White)
            .WithFinderPatternShape(RectangleFinderPatternShape.Default)
            .ToSvgString();

        var doc = XDocument.Parse(svg);
        var ns = doc.Root!.Name.Namespace;
        // Centre of the top-left finder: 4 quiet-zone modules plus 3.5 into its 7. QRCodeData.Size already
        // includes the quiet zone, so the module side is the canvas divided by it.
        var modules = QRCodeGenerator.Create(TestContent, QREccLevel.M).Size;
        var moduleSide = 512f / modules;
        var center = (4 + 3.5f) * moduleSide;

        // Size-bounded because the full-canvas background rect covers the same point, and dark because the
        // finder's light ring covers it too: neither bound alone would make this assertion mean its name.
        var inked = doc.Root.Descendants(ns + "rect").Any(r =>
            Covers(r, center, center)
            && Width(r) <= 7 * moduleSide
            && !string.Equals(r.Attribute("fill")?.Value, "white", StringComparison.OrdinalIgnoreCase));
        await Assert.That(inked).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SaveToSvg_StyledSymbol_NonOpaqueBackground_LosesTheFinderPatterns(bool namedFinderShape)
    {
        // Pins a known limitation so the documentation cannot go stale silently: a finder pattern drawn
        // as a shape of its own is cut out of its background, and SKSvgCanvas drops the layer that does
        // it. Both routes in are covered: naming a shape, and styling the modules, which substitutes one.
        // When the renderer stops erasing, this test fails, and the note in docs/migration.md and in
        // qrcode-symbologies.md goes with it. Whoever deletes it should keep something covering the named
        // arm: it is the only test that fails if ResolveFinderShape starts ignoring an explicit square.
        // Alpha 128 rather than 0, so the assertion covers the whole non-opaque range: at alpha 0 the
        // background rect is omitted entirely, which would hide an unbounded query's own mistake.
        var builder = new QRCodeImageBuilder(TestContent)
            .WithSize(512, 512)
            .WithBackgroundColor(SKColors.White.WithAlpha(128));
        var svg = (namedFinderShape
                ? builder.WithFinderPatternShape(RectangleFinderPatternShape.Default)
                : builder.WithModuleShape(CircleModuleShape.Default, sizePercent: 0.85f))
            .ToSvgString();

        var doc = XDocument.Parse(svg);
        var ns = doc.Root!.Name.Namespace;
        var modules = QRCodeGenerator.Create(TestContent, QREccLevel.M).Size;
        var moduleSide = 512f / modules;
        var center = (4 + 3.5f) * moduleSide;

        // Rects, because a built-in square finder is drawn as rects even when the modules around it are
        // ellipses: when this symbol keeps its finders, the covering elements are three rects.
        var covered = doc.Root.Descendants(ns + "rect")
            .Any(r => Covers(r, center, center) && Width(r) <= 7 * moduleSide);
        await Assert.That(covered).IsFalse();
    }

    [Test]
    public async Task SaveToSvg_GappedRectModules_KeepAntialiasing()
    {
        // Squares below full size leave gaps, so the seam crispEdges exists to remove is not there
        // and its jagged edges are all that is left. Shape alone does not decide this; the size does too.
        var svg = new QRCodeImageBuilder(TestContent)
            .WithSize(512, 512)
            .WithModuleShape(RectangleModuleShape.Default, sizePercent: 0.8f)
            .ToSvgString();

        var doc = XDocument.Parse(svg);
        await Assert.That(doc.Root!.Attribute("shape-rendering")).IsNull();
    }

    [Test]
    public async Task SaveToSvg_CustomShapes_KeepAntialiasing()
    {
        // Curved shapes need antialiasing; crispEdges would render them jagged.
        var svg = new QRCodeImageBuilder(TestContent)
            .WithSize(512, 512)
            .WithModuleShape(CircleModuleShape.Default, sizePercent: 0.9f)
            .ToSvgString();

        var doc = XDocument.Parse(svg);
        await Assert.That(doc.Root!.Attribute("shape-rendering")).IsNull();
        await Assert.That(doc.Root.Attribute("viewBox")).IsNotNull();
    }

    [Test]
    public async Task SaveToSvg_CustomFinderPattern_KeepsAntialiasing()
    {
        var svg = new QRCodeImageBuilder(TestContent)
            .WithSize(512, 512)
            .WithFinderPatternShape(RoundedRectangleFinderPatternShape.Default)
            .ToSvgString();

        var doc = XDocument.Parse(svg);
        await Assert.That(doc.Root!.Attribute("shape-rendering")).IsNull();
    }

    [Test]
    public async Task SaveToSvg_BuiltInIconShape_UsesCrispEdges()
    {
        // Built-in icon shapes draw rectangles, bitmaps, and text only, none of
        // which degrade under crispEdges, the QR modules stay seam-free.
        using var bitmap = new SKBitmap(32, 32);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Red);
        }
        var icon = IconData.FromImage(bitmap, iconSizePercent: 15, iconBorderWidth: 4);

        var svg = new QRCodeImageBuilder(TestContent)
            .WithSize(512, 512)
            .WithErrorCorrection(QREccLevel.H)
            .WithIcon(icon)
            .ToSvgString();

        var doc = XDocument.Parse(svg);
        await Assert.That(doc.Root!.Attribute("shape-rendering")?.Value).IsEquivalentTo("crispEdges");
    }

    [Test]
    public async Task SaveToSvg_BufferWriter_MatchesStreamOutput()
    {
        var builder = new QRCodeImageBuilder(TestContent).WithSize(256, 256);

        using var stream = new MemoryStream();
        builder.SaveToSvg(stream);

        var writer = new ArrayBufferWriter<byte>();
        builder.SaveToSvg(writer);

        await Assert.That(writer.WrittenSpan.ToArray()).IsEquivalentTo(stream.ToArray());
    }

    [Test]
    public void SaveToSvg_BufferWriter_Null_Throws()
    {
        var builder = new QRCodeImageBuilder(TestContent).WithSize(256, 256);

        Assert.Throws<ArgumentNullException>(() => builder.SaveToSvg((IBufferWriter<byte>)null!));
    }

    [Test]
    public async Task SaveToSvg_SegmentedBufferWriter_NeverRequestsOversizedSpan()
    {
        // Segmented writers (e.g. PipeWriter) cannot serve a GetSpan sized to the whole
        // document. This writer caps every request at 64 bytes and throws on larger
        // hints, pinning that SVG output is written in segments.
        var builder = new QRCodeImageBuilder(TestContent)
            .WithSize(256, 256)
            .WithGradient(new GradientOptions([SKColors.Blue, SKColors.Purple], GradientDirection.TopLeftToBottomRight));

        using var expected = new MemoryStream();
        builder.SaveToSvg(expected);

        var writer = new SegmentCappedBufferWriter(maxSegmentSize: 64);
        builder.SaveToSvg(writer);

        await Assert.That(writer.WrittenBytes).IsEquivalentTo(expected.ToArray());
    }

    [Test]
    public async Task SvgInjector_SingleByteWrites_InjectsAttributesOnce()
    {
        // A write may split the "<svg " marker at any position; the injector must
        // still find it and insert the attributes exactly once.
        var document = "<?xml version=\"1.0\" ?>\n<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"8\"><rect/></svg>";
        var payload = Encoding.UTF8.GetBytes(document);

        using var output = new MemoryStream();
        using (var injector = new SvgRootAttributeInjectorStream(output, "viewBox=\"0 0 8 8\" "))
        {
            foreach (var b in payload)
            {
                injector.Write([b], 0, 1);
            }
        }

        var result = Encoding.UTF8.GetString(output.ToArray());
        await Assert.That(result).IsEquivalentTo(document.Replace("<svg ", "<svg viewBox=\"0 0 8 8\" "));
    }

    [Test]
    public async Task SvgInjector_NoMarkerInLargeDocument_PassesThroughUnmodified()
    {
        // Marker absent beyond the scan window: the document must pass through unpatched.
        var payload = Encoding.UTF8.GetBytes(new string('x', 2048));

        using var output = new MemoryStream();
        using (var injector = new SvgRootAttributeInjectorStream(output, "viewBox=\"0 0 8 8\" "))
        {
            injector.Write(payload, 0, payload.Length);
        }

        await Assert.That(output.ToArray()).IsEquivalentTo(payload);
    }

    [Test]
    public async Task SvgInjector_NoMarkerInTinyDocument_FlushesOnDispose()
    {
        // A document that ends inside the scan window without a marker must still be
        // written out (on dispose), not silently dropped.
        var payload = Encoding.UTF8.GetBytes("tiny");

        using var output = new MemoryStream();
        using (var injector = new SvgRootAttributeInjectorStream(output, "viewBox=\"0 0 8 8\" "))
        {
            injector.Write(payload, 0, payload.Length);
        }

        await Assert.That(output.ToArray()).IsEquivalentTo(payload);
    }

    /// <summary>
    /// IBufferWriter that refuses size hints above a small cap, simulating a segmented
    /// writer (PipeWriter) that cannot provide one contiguous buffer for a whole document.
    /// </summary>
    private sealed class SegmentCappedBufferWriter(int maxSegmentSize) : IBufferWriter<byte>
    {
        private readonly MemoryStream _written = new();
        private readonly byte[] _segment = new byte[maxSegmentSize];

        public byte[] WrittenBytes => _written.ToArray();

        public void Advance(int count) => _written.Write(_segment, 0, count);

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint > _segment.Length)
                throw new InvalidOperationException($"Oversized buffer request: {sizeHint} > {_segment.Length}.");
            return _segment;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }

    [Test]
    public async Task GetSvgString_Content_MatchesToSvgString()
    {
        var expected = new QRCodeImageBuilder(TestContent)
            .WithSize(512, 512)
            .WithErrorCorrection(QREccLevel.H)
            .ToSvgString();

        await Assert.That(QRCodeImageBuilder.GetSvgString(TestContent, QREccLevel.H, size: 512)).IsEquivalentTo(expected);
    }

    [Test]
    public async Task WriteSvg_Content_MatchesGetSvgBytes()
    {
        var writer = new ArrayBufferWriter<byte>();
        QRCodeImageBuilder.WriteSvg(TestContent, writer, QREccLevel.M, size: 256);

        await Assert.That(writer.WrittenSpan.ToArray()).IsEquivalentTo(QRCodeImageBuilder.GetSvgBytes(TestContent, QREccLevel.M, size: 256));
    }

    [Test]
    public async Task SaveToSvg_LeavesStreamOpen()
    {
        using var stream = new MemoryStream();
        new QRCodeImageBuilder(TestContent)
            .WithSize(256, 256)
            .SaveToSvg(stream);

        await Assert.That(stream.CanWrite).IsTrue();
        await Assert.That(stream.Length > 0).IsTrue();
    }

    [Test]
    public void SaveToSvg_InvalidStream_Throws()
    {
        var builder = new QRCodeImageBuilder(TestContent).WithSize(256, 256);

        Assert.Throws<ArgumentNullException>(() => builder.SaveToSvg((Stream)null!));

        using var readOnly = new MemoryStream([0x00], writable: false);
        Assert.Throws<ArgumentException>(() => builder.SaveToSvg(readOnly));
    }

    [Test]
    public async Task ToSvgString_MatchesSaveToSvgOutput()
    {
        var builder = new QRCodeImageBuilder(TestContent).WithSize(256, 256);

        using var stream = new MemoryStream();
        builder.SaveToSvg(stream);

        await Assert.That(builder.ToSvgString()).IsEquivalentTo(Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Test]
    public async Task ToSvgString_IsDeterministic()
    {
        var builder = new QRCodeImageBuilder(TestContent)
            .WithSize(256, 256)
            .WithGradient(new GradientOptions([SKColors.Blue, SKColors.Purple], GradientDirection.TopLeftToBottomRight));

        await Assert.That(builder.ToSvgString()).IsEquivalentTo(builder.ToSvgString());
    }

    [Test]
    public async Task GetSvgBytes_Content_ProducesSvg()
    {
        var bytes = QRCodeImageBuilder.GetSvgBytes(TestContent, QREccLevel.H, size: 300);

        var doc = ParseSvg(bytes);
        await Assert.That(doc.Root!.Attribute("width")?.Value).IsEquivalentTo("300");
    }

    [Test]
    public async Task GetSvgBytes_QrCodeData_ProducesSvg()
    {
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var bytes = QRCodeImageBuilder.GetSvgBytes(qr, size: 300);

        var doc = ParseSvg(bytes);
        await Assert.That(doc.Root!.Attribute("width")?.Value).IsEquivalentTo("300");
    }

    [Test]
    public async Task SaveSvg_Content_WritesToStream()
    {
        using var stream = new MemoryStream();
        QRCodeImageBuilder.SaveSvg(TestContent, stream, QREccLevel.M, size: 256);

        var doc = ParseSvg(stream.ToArray());
        await Assert.That(doc.Root!.Attribute("width")?.Value).IsEquivalentTo("256");
    }

    [Test]
    public async Task SaveSvg_QrCodeData_WritesToStream()
    {
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        using var stream = new MemoryStream();
        QRCodeImageBuilder.SaveSvg(qr, stream, size: 256);

        var doc = ParseSvg(stream.ToArray());
        await Assert.That(doc.Root!.Attribute("width")?.Value).IsEquivalentTo("256");
    }

    [Test]
    public async Task WithColors_AreReflectedInSvg()
    {
        // Use colors without SVG named-color aliases; Skia writes named colors
        // (e.g. #000080 becomes "navy") when an exact match exists.
        var svg = new QRCodeImageBuilder(TestContent)
            .WithSize(256, 256)
            .WithColors(codeColor: SKColor.Parse("123456"), backgroundColor: SKColor.Parse("FEDCBA"))
            .ToSvgString();

        await Assert.That(svg).Contains("#123456");
        await Assert.That(svg).Contains("#FEDCBA");
    }

    [Test]
    public async Task WithGradient_EmitsLinearGradient()
    {
        var svg = new QRCodeImageBuilder(TestContent)
            .WithSize(256, 256)
            .WithGradient(new GradientOptions([SKColors.Blue, SKColors.Purple], GradientDirection.TopLeftToBottomRight))
            .ToSvgString();

        var doc = XDocument.Parse(svg);
        var ns = doc.Root!.Name.Namespace;
        await Assert.That(doc.Descendants(ns + "linearGradient")).IsNotEmpty();
    }

    [Test]
    public async Task WithIcon_EmbedsImageAsDataUri()
    {
        using var bitmap = new SKBitmap(32, 32);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Red);
        }
        var icon = IconData.FromImage(bitmap, iconSizePercent: 15, iconBorderWidth: 4);

        var svg = new QRCodeImageBuilder(TestContent)
            .WithSize(256, 256)
            .WithErrorCorrection(QREccLevel.H)
            .WithIcon(icon)
            .ToSvgString();

        var doc = XDocument.Parse(svg);
        var ns = doc.Root!.Name.Namespace;
        await Assert.That(doc.Descendants(ns + "image")).IsNotEmpty();
        await Assert.That(svg).Contains("base64");
    }

    [Test]
    public async Task WithModulePixelSize_SetsViewportToMatrixTimesPixelSize()
    {
        const int modulePixelSize = 10;
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);

        var svg = new QRCodeImageBuilder(qr)
            .WithModulePixelSize(modulePixelSize)
            .ToSvgString();

        var doc = XDocument.Parse(svg);
        var expected = (qr.Size * modulePixelSize).ToString();
        await Assert.That(doc.Root!.Attribute("width")?.Value).IsEquivalentTo(expected);
        await Assert.That(doc.Root.Attribute("height")?.Value).IsEquivalentTo(expected);
    }

    [Test]
    public async Task WithModulePixelSize_AndCanvas_PadsWithClearColor()
    {
        const int modulePixelSize = 4;
        var qr = QRCodeGenerator.Create(TestContent, QREccLevel.M);
        var contentSide = qr.Size * modulePixelSize;
        var canvasSide = contentSide + 40;

        var svg = new QRCodeImageBuilder(qr)
            .WithModulePixelSize(modulePixelSize)
            .WithSize(canvasSide, canvasSide)
            .WithClearColor(SKColor.Parse("102030"))
            .ToSvgString();

        var doc = XDocument.Parse(svg);
        var ns = doc.Root!.Name.Namespace;
        var rects = doc.Descendants(ns + "rect").ToArray();

        // First rect is the full-canvas clear, second is the QR background offset by the pad.
        await Assert.That(rects[0].Attribute("fill")?.Value).IsEquivalentTo("#102030");
        await Assert.That(rects[0].Attribute("width")?.Value).IsEquivalentTo(canvasSide.ToString());
        await Assert.That(rects[1].Attribute("fill")?.Value).IsEquivalentTo("white");
        await Assert.That(rects[1].Attribute("x")?.Value).IsEquivalentTo("20");
        await Assert.That(rects[1].Attribute("width")?.Value).IsEquivalentTo(contentSide.ToString());
    }

    private static XDocument ParseSvg(byte[] utf8Bytes)
        => XDocument.Parse(Encoding.UTF8.GetString(utf8Bytes));

    /// <summary>Whether the rect element covers the point, in the document's own coordinates.</summary>
    private static bool Covers(XElement rect, float x, float y)
    {
        var rx = Read(rect, "x");
        var ry = Read(rect, "y");
        return x >= rx && x <= rx + Read(rect, "width") && y >= ry && y <= ry + Read(rect, "height");
    }

    private static float Width(XElement rect) => Read(rect, "width");

    private static float Read(XElement element, string name)
        => float.TryParse(element.Attribute(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0f;
}
