#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:project ../src/FeatherQR/FeatherQR.csproj

using System.Globalization;
using System.Text;
using FeatherQR;

// Draws the decode figures of the design records, one folder per symbology: a strip of the main path's
// stages on a clean symbol, and one figure per input class the image decoder reads.
//
//   dotnet run tools/decode_figures.cs -- .github/docs/images
//   dotnet run tools/decode_figures.cs -- <directory> --preview      also a page of every figure, light and dark
//
// Each input class is also rendered to pixels and decoded through the public API, and the run fails
// unless it decodes. The figures carry pictures and labels only; their text is in the symbology's
// design record (.github/docs/specs/*-decoder.md), whose numbered notes match the numbers on the boxes.
// A box is green when, without its stage, the input would not read, or would read only after more grids
// fail than with it; a grid is one sampling, and its transposed and coverage reads are the same grid.
// The boxes follow the image-level outline in the symbology's spec-to-code map.

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var root = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? ".";
var preview = args.Contains("--preview");
var qrDir = Path.Combine(root, "standardqr");
var microDir = Path.Combine(root, "microqr");
var rmqrDir = Path.Combine(root, "rmqr");
Directory.CreateDirectory(qrDir);
Directory.CreateDirectory(microDir);
Directory.CreateDirectory(rmqrDir);

const string Payload = "FeatherQR decoder";
var v2 = Matrix(QRCodeGenerator.Create(Payload, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(2), QuietZoneSize = 0 }));
const string MeshPayload = "FeatherQR decoder, a version 14 symbol for the mesh figure";
var v14 = Matrix(QRCodeGenerator.Create(MeshPayload, QREccLevel.M, new QRCodeGeneratorOptions { Version = QRVersionRange.Exactly(14), QuietZoneSize = 0 }));
// Its bottom-right alignment pattern painted over, so nothing anchors the four-point transform's fourth corner
for (var r = 73 - 9; r <= 73 - 5; r++)
    for (var c = 73 - 9; c <= 73 - 5; c++)
        v14[r, c] = false;

// Data modules read the wrong way in the damaged figure
(int R, int C)[] flips = [(10, 11), (12, 14), (14, 9), (15, 13), (11, 19), (19, 10), (21, 13), (13, 21)];
var damaged = (bool[,])v2.Clone();
foreach (var (r, c) in flips)
    damaged[r, c] = !damaged[r, c];

// Every input class, rendered and decoded
var failures = new List<string>();
void Check(string name, bool[,] m, string payload, Func<double, double, double, H> shape, int size, int supersample, Func<double, double, double>? light = null, bool invert = false, bool blur = false, bool expectCorrections = false, double spread = 0)
{
    var map = shape(0, 0, size);
    var luminance = Render(m, map, size, size, supersample, light, invert, blur, spread);
    var ok = QRCodeDecoder.TryDecodeImage(luminance, size, size, out var text, out var info);
    var corrected = info.ErrorsCorrected;
    var pass = ok && text == payload && (!expectCorrections || corrected > 0);
    Console.WriteLine($"{(pass ? "ok  " : "FAIL")} {name}: {size}x{size} px, {info.Status}, corrected {corrected}");
    if (!pass)
        failures.Add(name);
}

Check("clean", v2, Payload, (x, y, s) => Square(25, x, y, s), 33 * 4, 1);
Check("rotated and mirrored", Transposed(v2), Payload, (x, y, s) => Rotated(25, x, y, s, 30), 180, 4);
Check("keystone", v2, Payload, (x, y, s) => Keystone(25, x, y, s, 0.25), 150, 4);
Check("grey edges and wrong modules", damaged, Payload, (x, y, s) => Square(25, x, y, s), 109, 4, blur: true, expectCorrections: true);
// Thin rings: every dark edge moved inward by ThinSpread of a module
const double ThinTurn = 11, ThinSpread = -0.18;
const int ThinPx = 155;
Check("thin rings", v2, Payload, (x, y, s) => Rotated(25, x, y, s, ThinTurn), ThinPx, 4, spread: ThinSpread);
// The figure says the row scan finds each finder on a row that reads 1:1:3:1:1, and that the lines through its centre miss the ratio and read by like edges
{
    var map = Rotated(25, 0, 0, ThinPx, ThinTurn);
    var thin = Render(v2, map, ThinPx, ThinPx, 4, null, false, false, ThinSpread);
    var threshold = Otsu(thin);
    foreach (var f in new[] { (U: 3.5, V: 3.5), (U: 21.5, V: 3.5), (U: 3.5, V: 21.5) })
    {
        var (cx, cy) = map.Map(f.U, f.V);
        var rowReads = Enumerable.Range(-3, 7).Any(dy => FinderRuns(thin, ThinPx, (int)cx, (int)cy + dy, 1, 0, threshold) is { } runs && IsRatio(runs));
        var centreLines = new[] { (1, 0), (0, 1), (1, 1), (1, -1) }.All(d => FinderRuns(thin, ThinPx, (int)cx, (int)cy, d.Item1, d.Item2, threshold) is { } runs && !IsRatio(runs) && IsLikeEdges(runs));
        Console.WriteLine($"{(rowReads && centreLines ? "ok  " : "FAIL")} thin rings: finder at ({f.U}, {f.V}), a row reads the ratio {rowReads}, the centre's lines read by like edges only {centreLines}");
        if (!rowReads || !centreLines)
            failures.Add($"thin rings finder ({f.U}, {f.V})");
    }
}
Check("uneven lighting", v2, Payload, (x, y, s) => Square(25, x, y, s), 33 * 4, 1, light: Shadow(33 * 4, 0.5, 3 * 4));
// The figure says the global threshold falls between lit and shadowed paper, so shadowed paper reads as ink
{
    var lit = Render(v2, Square(25, 0, 0, 33 * 4), 33 * 4, 33 * 4, 1, Shadow(33 * 4, 0.5, 3 * 4), false, false);
    var threshold = Otsu(lit);
    var shadowedPaper = 220 * 0.5;
    var below = shadowedPaper < threshold;
    Console.WriteLine($"{(below ? "ok  " : "FAIL")} uneven lighting: global threshold {threshold}, shadowed paper {shadowedPaper}");
    if (!below)
        failures.Add("uneven lighting threshold");
}
Check("low density", v2, Payload, (x, y, s) => Square(25, x, y, s), 43, 1);
Check("light on dark", v2, Payload, (x, y, s) => Square(25, x, y, s), 33 * 4, 1, invert: true);
Check("large version", v14, MeshPayload, (x, y, s) => Keystone(73, x, y, s, 0.12), 81 * 4, 4);

// Micro QR: an M3 for the clean figure, so the larger size is tried first, and an M4 for the others
const string MicroPayload = "FEATHERQR";
var m3 = MicroMatrix(MicroQRCodeGenerator.Create(MicroPayload, MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M3, QuietZoneSize = 0 }));
const string MicroPayload4 = "FEATHERQR MICRO";
var m4 = MicroMatrix(MicroQRCodeGenerator.Create(MicroPayload4, MicroQREccLevel.M, new MicroQRCodeGeneratorOptions { Version = MicroQRVersion.M4, QuietZoneSize = 0 }));

void CheckMicro(string name, bool[,] m, string payload, Func<double, double, double, H> shape, int size, int supersample, bool blur = false)
{
    var luminance = Render(m, shape(0, 0, size), size, size, supersample, null, false, blur);
    var ok = MicroQRCodeDecoder.TryDecodeImage(luminance, size, size, out var text, out var info);
    var pass = ok && text == payload;
    Console.WriteLine($"{(pass ? "ok  " : "FAIL")} micro {name}: {size}x{size} px, {info.Status}, {info.Version}, corrected {info.ErrorsCorrected}");
    if (!pass)
        failures.Add("micro " + name);
}

// The inputs as rendered, shared with their figures
const double MicroTurn = 30, MicroShrink = 0.08;
const int MicroRotatedPx = 116, MicroKeystonePx = 78, MicroSnapPx = 41, MicroLowPx = 22, MicroGreyPx = 38;
const bool MicroGreyBlur = false;
CheckMicro("clean", m3, MicroPayload, (x, y, s) => Square(15, x, y, s, 2), 19 * 4, 1);
CheckMicro("rotated and mirrored", Transposed(m4), MicroPayload4, (x, y, s) => Rotated(17, x, y, s, MicroTurn, 2), MicroRotatedPx, 4);
CheckMicro("keystone", m4, MicroPayload4, (x, y, s) => Keystone(17, x, y, s, MicroShrink, 2), MicroKeystonePx, 4);
CheckMicro("grey edges", m4, MicroPayload4, (x, y, s) => Square(17, x, y, s, 2), MicroGreyPx, 4, blur: MicroGreyBlur);
CheckMicro("snapped scale", m4, MicroPayload4, (x, y, s) => Square(17, x, y, s, 2), MicroSnapPx, 1);
CheckMicro("low density", m4, MicroPayload4, (x, y, s) => Square(17, x, y, s, 2), MicroLowPx, 1);

// rMQR: an R11x77 for most figures; each input is a map in image pixels and the image's size
const string RmqrPayload = "FEATHERQR RMQR";
var r11 = RmqrMatrix(RmQRCodeGenerator.Create(RmqrPayload, RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x77, QuietZoneSize = 0 }));
// The strip draws a shorter symbol, so its panels stay legible
var r43 = RmqrMatrix(RmQRCodeGenerator.Create("FEATHERQR", RmQREccLevel.M, new RmQRCodeGeneratorOptions { Version = RmQRVersion.R11x43, QuietZoneSize = 0 }));

void CheckRmqr(string name, bool[,] m, string payload, (H Map, int Width, int Height) input, int supersample, bool blur = false)
{
    var luminance = Render(m, input.Map, input.Width, input.Height, supersample, null, false, blur);
    var ok = RmQRCodeDecoder.TryDecodeImage(luminance, input.Width, input.Height, out var text, out var info);
    var pass = ok && text == payload;
    Console.WriteLine($"{(pass ? "ok  " : "FAIL")} rmqr {name}: {input.Width}x{input.Height} px, {info.Status}, {info.Version}, corrected {info.ErrorsCorrected}");
    if (!pass)
        failures.Add("rmqr " + name);
}

// The inputs as rendered, shared with their figures
const double RmqrPitch = 4, RmqrTurn = 30, RmqrKeystonePitch = 8, RmqrShrink = 0.16, RmqrWide = 4, RmqrTall = 3, RmqrGreyPitch = 1.55, RmqrGreyTurn = 3, RmqrLowPitch = 1.2;
const char RmqrShrunkEdge = 'T';
const int RmqrSnapWidth = 137;
const bool RmqrGreyBlur = false;
// The 5 x 5 sub-finder in the bottom-right corner painted to paper
var r11Hidden = (bool[,])r11.Clone();
for (var r = r11.GetLength(0) - 5; r < r11.GetLength(0); r++)
    for (var c = r11.GetLength(1) - 5; c < r11.GetLength(1); c++)
        r11Hidden[r, c] = false;
var rmqrClean = RectUpright(r11, RmqrPitch, RmqrPitch);
var rmqrRotated = RectRotated(MirroredX(r11), RmqrPitch, RmqrTurn);
var rmqrKeystone = RectKeystone(r11, RmqrKeystonePitch, RmqrShrink, RmqrShrunkEdge);
var rmqrNonSquare = RectUpright(r11, RmqrWide, RmqrTall);
var rmqrGrey = RectRotated(r11, RmqrGreyPitch, RmqrGreyTurn);
var rmqrHidden = RectUpright(r11Hidden, RmqrPitch, RmqrPitch);
var rmqrSnapped = RectUpright(r11, RmqrSnapWidth / 81.0, RmqrSnapWidth / 81.0);
var rmqrLow = RectUpright(r11, RmqrLowPitch, RmqrLowPitch);
CheckRmqr("clean", r11, RmqrPayload, rmqrClean, 1);
CheckRmqr("rotated and mirrored", MirroredX(r11), RmqrPayload, rmqrRotated, 4);
CheckRmqr("keystone", r11, RmqrPayload, rmqrKeystone, 4);
CheckRmqr("non-square modules", r11, RmqrPayload, rmqrNonSquare, 1);
CheckRmqr("grey edges", r11, RmqrPayload, rmqrGrey, 4, RmqrGreyBlur);
CheckRmqr("hidden sub-finder", r11Hidden, RmqrPayload, rmqrHidden, 1);
CheckRmqr("snapped scale", r11, RmqrPayload, rmqrSnapped, 1);
CheckRmqr("low density", r11, RmqrPayload, rmqrLow, 1);

// ---------- Drawing ----------

const string Font = "-apple-system,'Segoe UI',Helvetica,Arial,sans-serif";
string Style() => $$"""
<style>
.card{fill:#ffffff;stroke:#d0d7de}
.box{fill:#f6f8fa;stroke:#d8dee4}
.h{font:600 12px {{Font}};fill:#1f2328}
.xs{font:11px {{Font}};fill:#59636e}
.lab{font:600 11px {{Font}};fill:#0969da;paint-order:stroke;stroke:#ffffff;stroke-width:3px;stroke-linejoin:round}
.estt{font:600 11px {{Font}};fill:#57606a;paint-order:stroke;stroke:#ffffff;stroke-width:3px;stroke-linejoin:round}
.rowt{font:600 11px {{Font}};fill:#8250df;paint-order:stroke;stroke:#ffffff;stroke-width:3px;stroke-linejoin:round}
.ink{fill:#1f2328}
.paper{fill:#ffffff}
.det{fill:#0969da}
.detl{stroke:#0969da;fill:none;stroke-width:1.6}
.detd{stroke:#0969da;fill:none;stroke-width:1.4;stroke-dasharray:5 3}
.pred{stroke:#bf8700;fill:none;stroke-width:1.6;stroke-dasharray:4 3}
.twopx{fill:#bf8700;fill-opacity:.35}
.predr{stroke:#bf8700;fill:#ffffff;stroke-width:1.6}
.est{stroke:#6e7781;fill:none;stroke-width:1.4;stroke-dasharray:3 3}
.estx{stroke:#57606a;fill:none;stroke-width:2.4}
.row{stroke:#8250df;fill:none;stroke-width:2.2;stroke-dasharray:5 3}
.tick{stroke:#0969da;fill:none;stroke-width:3;stroke-linecap:round}
.err{stroke:#cf222e;fill:none;stroke-width:1.8}
.pt{fill:#0969da;fill-opacity:.7}
.hist{fill:#d0d7de;stroke:#8c959f;stroke-width:1}
.thr{stroke:#bf8700;stroke-width:1.6;stroke-dasharray:3 2}
.sep{stroke:#d8dee4;stroke-width:1}
.fmt{fill:#0969da;fill-opacity:.55}
.lat{stroke:#0969da;stroke-opacity:.85;stroke-width:1.4;fill:none}
.blk{stroke:#8250df;stroke-opacity:.35;stroke-width:.8;fill:none}
.arrow{stroke:#8c959f;stroke-width:1.6;fill:none}
.num{font:600 10px {{Font}};fill:#ffffff}
.bk{fill:#dafbe1;stroke:#4ac26b;stroke-width:1.2}.bkt{font:600 12px {{Font}};fill:#1f2328}
.bu{fill:#f6f8fa;stroke:#d0d7de}.but{font:600 12px {{Font}};fill:#1f2328}
.bc{fill:#ffffff;stroke:#bf8700;stroke-dasharray:4 3}.bct{font:600 12px {{Font}};fill:#7d4e00}
.bs{fill:#ffffff;stroke:#d8dee4;stroke-dasharray:2 3}.bst{font:600 12px {{Font}};fill:#8c959f}
.flow{stroke:#8c959f;stroke-width:1.4;fill:none}
.flowd{stroke:#bf8700;stroke-width:1.4;fill:none;stroke-dasharray:4 3}
</style>
""";

string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
string Poly(H map, double u0, double v0, double u1, double v1)
{
    var a = map.Map(u0, v0); var b = map.Map(u1, v0); var c = map.Map(u1, v1); var d = map.Map(u0, v1);
    return $"M{F(a.X)} {F(a.Y)}L{F(b.X)} {F(b.Y)}L{F(c.X)} {F(c.Y)}L{F(d.X)} {F(d.Y)}Z";
}
string Symbol(H map, bool[,] m, string ink = "#1f2328", string paper = "#ffffff")
{
    var rows = m.GetLength(0); var cols = m.GetLength(1);
    var path = new StringBuilder();
    for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
            if (m[r, c])
                path.Append(Poly(map, c, r, c + 1, r + 1));
    return $"<path fill=\"{paper}\" stroke=\"#d0d7de\" d=\"{Poly(map, -2, -2, cols + 2, rows + 2)}\"/><path fill=\"{ink}\" stroke=\"{ink}\" stroke-width=\"0.35\" d=\"{path}\"/>";
}
// A symbol whose dark edges all moved by spread of a module, outward when positive: each dark module grown by it, or the
// light modules grown into the ink, with the round corners the render's distance rule gives. An affine map only
string SpreadSymbol(H map, bool[,] m, double spread)
{
    string P(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
    var rows = m.GetLength(0); var cols = m.GetLength(1);
    var reach = Math.Abs(spread);
    var grown = new StringBuilder();
    for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
            if (m[r, c] == spread > 0)
                grown.Append($"<rect x=\"{F(c - reach)}\" y=\"{F(r - reach)}\" width=\"{F(1 + 2 * reach)}\" height=\"{F(1 + 2 * reach)}\" rx=\"{F(reach)}\"/>");
    var paper = $"<path fill=\"#ffffff\" stroke=\"#d0d7de\" d=\"{Poly(map, -2, -2, cols + 2, rows + 2)}\"/>";
    var transform = $"matrix({P(map.A)} {P(map.D)} {P(map.B)} {P(map.E)} {P(map.C)} {P(map.F)})";
    return spread > 0
        ? $"{paper}<g transform=\"{transform}\" fill=\"#1f2328\">{grown}</g>"
        : $"{paper}<g transform=\"{transform}\"><rect fill=\"#1f2328\" x=\"{F(reach)}\" y=\"{F(reach)}\" width=\"{F(cols - 2 * reach)}\" height=\"{F(rows - 2 * reach)}\"/><g fill=\"#ffffff\">{grown}</g></g>";
}
string Dot((double X, double Y) p, double r = 4, string cls = "det") => $"<circle class=\"{cls}\" cx=\"{F(p.X)}\" cy=\"{F(p.Y)}\" r=\"{F(r)}\"/>";
string Line((double X, double Y) a, (double X, double Y) b, string cls) => $"<line class=\"{cls}\" x1=\"{F(a.X)}\" y1=\"{F(a.Y)}\" x2=\"{F(b.X)}\" y2=\"{F(b.Y)}\"/>";
// A line read by like edges, styled in place: a class would enter every figure's style block
string LikeLine((double X, double Y) a, (double X, double Y) b) => $"<line stroke=\"#0969da\" stroke-width=\"2.4\" stroke-linecap=\"round\" x1=\"{F(a.X)}\" y1=\"{F(a.Y)}\" x2=\"{F(b.X)}\" y2=\"{F(b.Y)}\"/>";
string Text(double x, double y, string s, string cls, string anchor = "start") => $"<text class=\"{cls}\" x=\"{F(x)}\" y=\"{F(y)}\" text-anchor=\"{anchor}\">{Esc(s)}</text>";
string Cross((double X, double Y) p, double s, string cls) => Line((p.X - s, p.Y - s), (p.X + s, p.Y + s), cls) + Line((p.X - s, p.Y + s), (p.X + s, p.Y - s), cls);
string Window(H map, (double U, double V) centre, double half, string cls) => $"<path class=\"{cls}\" d=\"{Poly(map, centre.U - half, centre.V - half, centre.U + half, centre.V + half)}\"/>";
string Marker(double cx, double cy, int n) => $"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"8\" fill=\"#1a7f37\"/><text class=\"num\" x=\"{F(cx)}\" y=\"{F(cy + 3.5)}\" text-anchor=\"middle\">{n}</text>";

List<string> Wrap(string s, int max)
{
    var lines = new List<string>();
    var line = new StringBuilder();
    foreach (var word in s.Split(' '))
    {
        if (line.Length > 0 && line.Length + 1 + word.Length > max)
        {
            lines.Add(line.ToString());
            line.Clear();
        }
        if (line.Length > 0)
            line.Append(' ');
        line.Append(word);
    }
    if (line.Length > 0)
        lines.Add(line.ToString());
    return lines;
}

// Finder centres and the bottom-right alignment centre of a version 2 grid
(double U, double V) tl = (3.5, 3.5), tr = (21.5, 3.5), bl = (3.5, 21.5), al = (18.5, 18.5);

(double X, double Y) ParallelogramAlignment(H map, int dim)
{
    var a = map.Map(3.5, 3.5); var b = map.Map(dim - 3.5, 3.5); var c = map.Map(3.5, dim - 3.5);
    var cornerX = b.X + c.X - a.X; var cornerY = b.Y + c.Y - a.Y;
    var k = 1 - 3.0 / (dim - 7);
    return (a.X + k * (cornerX - a.X), a.Y + k * (cornerY - a.Y));
}

string LabelAway(H map, (double U, double V) f, string label, double distance)
{
    var p = map.Map(f.U, f.V);
    var g = map.Map((tl.U + tr.U + bl.U) / 3, (tl.V + tr.V + bl.V) / 3);
    var dx = p.X - g.X; var dy = p.Y - g.Y;
    var n = Math.Sqrt(dx * dx + dy * dy);
    return Text(p.X + dx / n * distance, p.Y + dy / n * distance + 4, label, "lab", "middle");
}

string Histogram(double x, double y, double w, double h)
{
    var sb = new StringBuilder($"M{F(x)} {F(y + h)}");
    for (var i = 0; i <= 60; i++)
    {
        var t = i / 60.0;
        var v = Math.Exp(-Math.Pow(t - 0.18, 2) / 0.004) * 0.75 + Math.Exp(-Math.Pow(t - 0.86, 2) / 0.003) + 0.06;
        sb.Append($"L{F(x + t * w)} {F(y + h - v * h)}");
    }
    sb.Append($"L{F(x + w)} {F(y + h)}Z");
    return $"<path class=\"hist\" d=\"{sb}\"/>";
}

string Card(int width, int height, string defs = "")
    => $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">{Style()}<defs>{defs}<marker id=\"ah\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"7\" markerHeight=\"7\" orient=\"auto-start-reverse\"><path d=\"M0 0L10 5L0 10z\" fill=\"#8c959f\"/></marker></defs>"
        + $"<rect class=\"card\" x=\"0.5\" y=\"0.5\" width=\"{width - 1}\" height=\"{height - 1}\" rx=\"12\"/>";

// ---------- The stage strip ----------
{
    const int W = 1080, H0 = 250;
    var sb = new StringBuilder();
    sb.Append(Card(W, H0,
        "<filter id=\"lens\" x=\"-5%\" y=\"-5%\" width=\"110%\" height=\"110%\"><feGaussianBlur stdDeviation=\"0.8\"/></filter><linearGradient id=\"light\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"><stop offset=\"0\" stop-color=\"#ffffff\" stop-opacity=\".35\"/><stop offset=\"1\" stop-color=\"#000000\" stop-opacity=\".22\"/></linearGradient>"));
    const double P = 150, S = 36, Gap = 28, X0 = 24, Y0 = 20;
    string[] labels = ["Luminance", "Global threshold", "Finder triple", "Corner from the triangle", "Alignment search", "Matrix decode"];
    for (var i = 0; i < 6; i++)
    {
        var x = X0 + i * (P + Gap);
        sb.Append($"<rect class=\"box\" x=\"{F(x)}\" y=\"{F(Y0)}\" width=\"{P}\" height=\"{P + S}\" rx=\"8\"/>");
        sb.Append(Line((x, Y0 + P), (x + P, Y0 + P), "sep"));
        var map = Square(25, x + 10, Y0 + 10, P - 20, 2);
        var sy = Y0 + P;
        switch (i)
        {
            case 0:
                sb.Append($"<g filter=\"url(#lens)\">{Symbol(map, v2, "#4a4a48", "#e7e3db")}</g>");
                sb.Append($"<path fill=\"url(#light)\" d=\"{Poly(map, -2, -2, 27, 27)}\"/>");
                sb.Append(Histogram(x + 20, sy + 7, P - 40, 22));
                break;
            case 1:
                sb.Append(Symbol(map, v2));
                sb.Append(Histogram(x + 20, sy + 7, P - 40, 22));
                var tx = x + 20 + 0.52 * (P - 40);
                sb.Append(Line((tx, sy + 4), (tx, sy + 31), "thr"));
                break;
            case 2:
                sb.Append($"<g opacity=\"0.3\">{Symbol(map, v2)}</g>");
                sb.Append(Line(map.Map(-1.5, tl.V), map.Map(8.5, tl.V), "detl"));
                foreach (var f in new[] { tl, tr, bl })
                    sb.Append(Dot(map.Map(f.U, f.V)));
                var unit = 15.0; var bx = x + (P - 7 * unit) / 2; var by = sy + 7;
                int[] runs = [1, 1, 3, 1, 1];
                var rx = bx;
                for (var k = 0; k < runs.Length; k++)
                {
                    var wdt = runs[k] * unit;
                    sb.Append(k % 2 == 0
                        ? $"<rect class=\"ink\" x=\"{F(rx)}\" y=\"{F(by)}\" width=\"{F(wdt)}\" height=\"9\"/>"
                        : $"<rect fill=\"#ffffff\" stroke=\"#8c959f\" x=\"{F(rx)}\" y=\"{F(by)}\" width=\"{F(wdt)}\" height=\"9\"/>");
                    sb.Append(Text(rx + wdt / 2, by + 22, runs[k].ToString(CultureInfo.InvariantCulture), "xs", "middle"));
                    rx += wdt;
                }
                break;
            case 3:
                sb.Append($"<g opacity=\"0.3\">{Symbol(map, v2)}</g>");
                sb.Append(Line(map.Map(tl.U, tl.V), map.Map(tr.U, tr.V), "detl"));
                sb.Append(Line(map.Map(tl.U, tl.V), map.Map(bl.U, bl.V), "detl"));
                sb.Append(Line(map.Map(tr.U, tr.V), map.Map(bl.U, bl.V), "est"));
                foreach (var f in new[] { tl, tr, bl })
                    sb.Append(Dot(map.Map(f.U, f.V)));
                sb.Append(LabelAway(map, tl, "TL", 13));
                sb.Append(LabelAway(map, tr, "TR", 13));
                sb.Append(LabelAway(map, bl, "BL", 13));
                sb.Append(Line((x + 26, sy + 18), (x + 46, sy + 18), "est"));
                sb.Append(Text(x + 52, sy + 22, "longest side", "xs"));
                break;
            case 4:
                sb.Append($"<g opacity=\"0.3\">{Symbol(map, v2)}</g>");
                var pts = new StringBuilder();
                for (var r = 0; r < 25; r++)
                    for (var c = 0; c < 25; c++)
                    {
                        var p = map.Map(c + 0.5, r + 0.5);
                        pts.Append($"M{F(p.X - 0.9)} {F(p.Y)}a0.9 0.9 0 1 0 1.8 0a0.9 0.9 0 1 0 -1.8 0");
                    }
                sb.Append($"<path class=\"pt\" d=\"{pts}\"/>");
                sb.Append(Window(map, al, 3, "pred"));
                foreach (var f in new[] { tl, tr, bl })
                    sb.Append(Dot(map.Map(f.U, f.V)));
                sb.Append($"<circle class=\"detl\" cx=\"{F(map.Map(al.U, al.V).X)}\" cy=\"{F(map.Map(al.U, al.V).Y)}\" r=\"3.5\"/>");
                sb.Append(Dot(map.Map(al.U, al.V), 2));
                sb.Append($"<rect class=\"pred\" x=\"{F(x + 10)}\" y=\"{F(sy + 12)}\" width=\"10\" height=\"10\"/>");
                sb.Append(Text(x + 25, sy + 21, "search", "xs"));
                sb.Append(Dot((x + 70, sy + 17), 2.2, "pt"));
                sb.Append(Text(x + 77, sy + 21, "sample point", "xs"));
                break;
            case 5:
                sb.Append($"<path class=\"paper\" stroke=\"#d0d7de\" d=\"{Poly(map, -2, -2, 27, 27)}\"/>");
                var cells = new StringBuilder();
                var fmt = new StringBuilder();
                for (var r = 0; r < 25; r++)
                    for (var c = 0; c < 25; c++)
                    {
                        var isFormat = (r == 8 && (c <= 8 || c >= 17) && c != 6) || (c == 8 && (r <= 8 || r >= 18) && r != 6);
                        if (isFormat)
                            fmt.Append(Poly(map, c + 0.12, r + 0.12, c + 0.88, r + 0.88));
                        else if (v2[r, c])
                            cells.Append(Poly(map, c + 0.12, r + 0.12, c + 0.88, r + 0.88));
                    }
                sb.Append($"<path class=\"ink\" d=\"{cells}\"/><path class=\"fmt\" d=\"{fmt}\"/>");
                sb.Append($"<rect class=\"fmt\" x=\"{F(x + 26)}\" y=\"{F(sy + 12)}\" width=\"10\" height=\"10\"/>");
                sb.Append(Text(x + 42, sy + 21, "format information", "xs"));
                break;
        }
        sb.Append(Text(x + P / 2, Y0 + P + S + 22, labels[i], "h", "middle"));
        if (i < 5)
        {
            var ax = x + P + 6; var ay = Y0 + P / 2;
            sb.Append($"<path class=\"arrow\" d=\"M{F(ax)} {F(ay)}h{Gap - 12}m-5 -5l5 5l-5 5\"/>");
        }
    }
    sb.Append("</svg>");
    File.WriteAllText(Path.Combine(qrDir, "decode-overview.svg"), sb.ToString());
}

// ---------- The Micro QR stage strip ----------
{
    const int W = 1080, H0 = 250;
    var sb = new StringBuilder();
    sb.Append(Card(W, H0,
        "<filter id=\"lens\" x=\"-5%\" y=\"-5%\" width=\"110%\" height=\"110%\"><feGaussianBlur stdDeviation=\"0.8\"/></filter><linearGradient id=\"light\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"><stop offset=\"0\" stop-color=\"#ffffff\" stop-opacity=\".35\"/><stop offset=\"1\" stop-color=\"#000000\" stop-opacity=\".22\"/></linearGradient>"));
    const double P = 150, S = 36, Gap = 28, X0 = 24, Y0 = 20;
    string[] labels = ["Luminance", "Global threshold", "Finder candidates", "Module sizes and centre", "Sizes M4 to M1", "Matrix decode"];
    // The single finder's centre, in the modules of an M3
    (double U, double V) centre = (3.5, 3.5);
    for (var i = 0; i < 6; i++)
    {
        var x = X0 + i * (P + Gap);
        sb.Append($"<rect class=\"box\" x=\"{F(x)}\" y=\"{F(Y0)}\" width=\"{P}\" height=\"{P + S}\" rx=\"8\"/>");
        sb.Append(Line((x, Y0 + P), (x + P, Y0 + P), "sep"));
        // Room for an M4 outline around the M3: the quiet zone is two modules
        var map = Square(15, x + 10, Y0 + 10, P - 20, 2);
        var sy = Y0 + P;
        switch (i)
        {
            case 0:
                sb.Append($"<g filter=\"url(#lens)\">{Symbol(map, m3, "#4a4a48", "#e7e3db")}</g>");
                sb.Append($"<path fill=\"url(#light)\" d=\"{Poly(map, -2, -2, 17, 17)}\"/>");
                sb.Append(Histogram(x + 20, sy + 7, P - 40, 22));
                break;
            case 1:
                sb.Append(Symbol(map, m3));
                sb.Append(Histogram(x + 20, sy + 7, P - 40, 22));
                var tx = x + 20 + 0.52 * (P - 40);
                sb.Append(Line((tx, sy + 4), (tx, sy + 31), "thr"));
                break;
            case 2:
                sb.Append($"<g opacity=\"0.3\">{Symbol(map, m3)}</g>");
                sb.Append(Line(map.Map(-1.5, centre.V), map.Map(8.5, centre.V), "detl"));
                sb.Append(Dot(map.Map(centre.U, centre.V)));
                var unit = 15.0; var bx = x + (P - 7 * unit) / 2; var by = sy + 7;
                int[] runs = [1, 1, 3, 1, 1];
                var rx = bx;
                for (var k = 0; k < runs.Length; k++)
                {
                    var wdt = runs[k] * unit;
                    sb.Append(k % 2 == 0
                        ? $"<rect class=\"ink\" x=\"{F(rx)}\" y=\"{F(by)}\" width=\"{F(wdt)}\" height=\"9\"/>"
                        : $"<rect fill=\"#ffffff\" stroke=\"#8c959f\" x=\"{F(rx)}\" y=\"{F(by)}\" width=\"{F(wdt)}\" height=\"9\"/>");
                    sb.Append(Text(rx + wdt / 2, by + 22, runs[k].ToString(CultureInfo.InvariantCulture), "xs", "middle"));
                    rx += wdt;
                }
                break;
            case 3:
                // Each size pairs the dark ring's inner edge on one side with its outer edge on the other: six modules
                sb.Append($"<g opacity=\"0.3\">{Symbol(map, m3)}</g>");
                sb.Append(Line(map.Map(1, centre.V - 0.25), map.Map(7, centre.V - 0.25), "tick"));
                sb.Append(Line(map.Map(centre.U + 0.25, 1), map.Map(centre.U + 0.25, 7), "tick"));
                sb.Append(Dot(map.Map(centre.U, centre.V)));
                sb.Append(Line((x + 26, sy + 18), (x + 46, sy + 18), "tick"));
                sb.Append(Text(x + 52, sy + 22, "six modules", "xs"));
                break;
            case 4:
                sb.Append($"<g opacity=\"0.3\">{Symbol(map, m3)}</g>");
                foreach (var size in new[] { 17, 13, 11 })
                    sb.Append($"<path class=\"est\" d=\"{Poly(map, 0, 0, size, size)}\"/>");
                sb.Append($"<path class=\"detl\" d=\"{Poly(map, 0, 0, 15, 15)}\"/>");
                sb.Append(Dot(map.Map(centre.U, centre.V)));
                sb.Append(Line((x + 12, sy + 18), (x + 30, sy + 18), "est"));
                sb.Append(Text(x + 35, sy + 22, "other sizes", "xs"));
                sb.Append(Line((x + 96, sy + 18), (x + 110, sy + 18), "detl"));
                sb.Append(Text(x + 114, sy + 22, "reads", "xs"));
                break;
            case 5:
                sb.Append($"<path class=\"paper\" stroke=\"#d0d7de\" d=\"{Poly(map, -2, -2, 17, 17)}\"/>");
                var cells = new StringBuilder();
                var fmt = new StringBuilder();
                for (var r = 0; r < 15; r++)
                    for (var c = 0; c < 15; c++)
                    {
                        // Format information: row 8 columns 1-8, column 8 rows 1-7
                        var isFormat = (r == 8 && c >= 1 && c <= 8) || (c == 8 && r >= 1 && r <= 7);
                        if (isFormat)
                            fmt.Append(Poly(map, c + 0.12, r + 0.12, c + 0.88, r + 0.88));
                        else if (m3[r, c])
                            cells.Append(Poly(map, c + 0.12, r + 0.12, c + 0.88, r + 0.88));
                    }
                sb.Append($"<path class=\"ink\" d=\"{cells}\"/><path class=\"fmt\" d=\"{fmt}\"/>");
                sb.Append($"<rect class=\"fmt\" x=\"{F(x + 26)}\" y=\"{F(sy + 12)}\" width=\"10\" height=\"10\"/>");
                sb.Append(Text(x + 42, sy + 21, "format information", "xs"));
                break;
        }
        sb.Append(Text(x + P / 2, Y0 + P + S + 22, labels[i], "h", "middle"));
        if (i < 5)
        {
            var ax = x + P + 6; var ay = Y0 + P / 2;
            sb.Append($"<path class=\"arrow\" d=\"M{F(ax)} {F(ay)}h{Gap - 12}m-5 -5l5 5l-5 5\"/>");
        }
    }
    sb.Append("</svg>");
    File.WriteAllText(Path.Combine(microDir, "decode-overview.svg"), sb.ToString());
}

// ---------- The rMQR stage strip ----------
{
    // Two rows of three: a wide symbol needs wide panels
    const double P = 320, PH = 116, S = 36, Gap = 28, X0 = 24, Y0 = 20, RowGap = 48;
    const int W = 1080, H0 = (int)(Y0 + 2 * (PH + S) + RowGap + 22 + 20);
    var sb = new StringBuilder();
    sb.Append(Card(W, H0,
        "<filter id=\"lens\" x=\"-5%\" y=\"-5%\" width=\"110%\" height=\"110%\"><feGaussianBlur stdDeviation=\"0.8\"/></filter><linearGradient id=\"light\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"><stop offset=\"0\" stop-color=\"#ffffff\" stop-opacity=\".35\"/><stop offset=\"1\" stop-color=\"#000000\" stop-opacity=\".22\"/></linearGradient>"));
    string[] labels = ["Luminance", "Global threshold", "Finder candidates", "Finder-side format copy", "Sub-finder", "Isotropic grid"];
    int rows = r43.GetLength(0), cols = r43.GetLength(1);
    (double U, double V) finder = (3.5, 3.5), sub = (cols - 2.5, rows - 2.5);
    for (var i = 0; i < 6; i++)
    {
        var x = X0 + i % 3 * (P + Gap); var y = Y0 + i / 3 * (PH + S + RowGap);
        sb.Append($"<rect class=\"box\" x=\"{F(x)}\" y=\"{F(y)}\" width=\"{P}\" height=\"{PH + S}\" rx=\"8\"/>");
        sb.Append(Line((x, y + PH), (x + P, y + PH), "sep"));
        var pitch = (P - 20) / (cols + 4);
        var map = H.Affine(x + 10 + 2 * pitch, y + (PH - (rows + 4) * pitch) / 2 + 2 * pitch, pitch, 0, 0, pitch);
        var sy = y + PH;
        switch (i)
        {
            case 0:
                sb.Append($"<g filter=\"url(#lens)\">{Symbol(map, r43, "#4a4a48", "#e7e3db")}</g>");
                sb.Append($"<path fill=\"url(#light)\" d=\"{Poly(map, -2, -2, cols + 2, rows + 2)}\"/>");
                sb.Append(Histogram(x + 40, sy + 7, P - 80, 22));
                break;
            case 1:
                sb.Append(Symbol(map, r43));
                sb.Append(Histogram(x + 40, sy + 7, P - 80, 22));
                var tx = x + 40 + 0.52 * (P - 80);
                sb.Append(Line((tx, sy + 4), (tx, sy + 31), "thr"));
                break;
            case 2:
                sb.Append($"<g opacity=\"0.3\">{Symbol(map, r43)}</g>");
                sb.Append(Line(map.Map(-1.5, finder.V), map.Map(8.5, finder.V), "detl"));
                sb.Append(Dot(map.Map(finder.U, finder.V)));
                var unit = 15.0; var bx = x + (P - 7 * unit) / 2; var by = sy + 7;
                int[] runs = [1, 1, 3, 1, 1];
                var rx = bx;
                for (var k = 0; k < runs.Length; k++)
                {
                    var wdt = runs[k] * unit;
                    sb.Append(k % 2 == 0
                        ? $"<rect class=\"ink\" x=\"{F(rx)}\" y=\"{F(by)}\" width=\"{F(wdt)}\" height=\"9\"/>"
                        : $"<rect fill=\"#ffffff\" stroke=\"#8c959f\" x=\"{F(rx)}\" y=\"{F(by)}\" width=\"{F(wdt)}\" height=\"9\"/>");
                    sb.Append(Text(rx + wdt / 2, by + 22, runs[k].ToString(CultureInfo.InvariantCulture), "xs", "middle"));
                    rx += wdt;
                }
                break;
            case 3:
                // Finder side: rows 1-5 of columns 8-10, then rows 1-3 of column 11
                sb.Append($"<g opacity=\"0.3\">{Symbol(map, r43)}</g>");
                var fmt = new StringBuilder();
                for (var c = 8; c <= 11; c++)
                    for (var r = 1; r <= (c == 11 ? 3 : 5); r++)
                        fmt.Append(Poly(map, c + 0.1, r + 0.1, c + 0.9, r + 0.9));
                sb.Append($"<path class=\"fmt\" d=\"{fmt}\"/>");
                sb.Append($"<rect class=\"fmt\" x=\"{F(x + 90)}\" y=\"{F(sy + 12)}\" width=\"10\" height=\"10\"/>");
                sb.Append(Text(x + 106, sy + 21, "format copy", "xs"));
                break;
            case 4:
                sb.Append($"<g opacity=\"0.3\">{Symbol(map, r43)}</g>");
                sb.Append(Window(map, sub, 3.5, "pred"));
                sb.Append(Dot(map.Map(sub.U, sub.V)));
                sb.Append($"<rect class=\"pred\" x=\"{F(x + 90)}\" y=\"{F(sy + 12)}\" width=\"10\" height=\"10\"/>");
                sb.Append(Text(x + 106, sy + 21, "search", "xs"));
                sb.Append(Dot((x + 170, sy + 17), 4));
                sb.Append(Text(x + 180, sy + 21, "found centre", "xs"));
                break;
            case 5:
                sb.Append($"<g opacity=\"0.3\">{Symbol(map, r43)}</g>");
                var pts = new StringBuilder();
                for (var r = 0; r < rows; r++)
                    for (var c = 0; c < cols; c++)
                    {
                        var p = map.Map(c + 0.5, r + 0.5);
                        pts.Append($"M{F(p.X - 0.9)} {F(p.Y)}a0.9 0.9 0 1 0 1.8 0a0.9 0.9 0 1 0 -1.8 0");
                    }
                sb.Append($"<path class=\"pt\" d=\"{pts}\"/>");
                sb.Append(Line(map.Map(finder.U, finder.V), map.Map(sub.U, sub.V), "detl"));
                sb.Append(Dot(map.Map(finder.U, finder.V)));
                sb.Append(Dot(map.Map(sub.U, sub.V)));
                sb.Append(Line((x + 60, sy + 18), (x + 80, sy + 18), "detl"));
                sb.Append(Text(x + 86, sy + 22, "finder to sub-finder", "xs"));
                sb.Append(Dot((x + 210, sy + 17), 2.2, "pt"));
                sb.Append(Text(x + 217, sy + 21, "sample point", "xs"));
                break;
        }
        sb.Append(Text(x + P / 2, y + PH + S + 22, labels[i], "h", "middle"));
        if (i % 3 < 2)
        {
            var ax = x + P + 6; var ay = y + PH / 2;
            sb.Append($"<path class=\"arrow\" d=\"M{F(ax)} {F(ay)}h{Gap - 12}m-5 -5l5 5l-5 5\"/>");
        }
        else if (i == 2)
        {
            var nx = X0 + P / 2; var ny = Y0 + PH + S + RowGap;
            var mid = y + PH + S + 36;
            sb.Append($"<path class=\"arrow\" d=\"M{F(x + P / 2)} {F(y + PH + S + 28)}V{F(mid)}H{F(nx)}V{F(ny - 4)}m-5 -5l5 5l5 -5\"/>");
        }
    }
    sb.Append("</svg>");
    File.WriteAllText(Path.Combine(rmqrDir, "decode-overview.svg"), sb.ToString());
}

// ---------- The input figures ----------

// The image-level outline's stages, in its order; the matrix decode every grid goes through is the bar under them
string[] qrBoxes =
[
    "Binarization pass",
    "Finder triple",
    "Corner from the triangle",
    "Low density",
    "Finder centres and module sizes",
    "Finders' frame and dimension candidates",
    "Alignment search",
    "Mesh over the alignment lattice",
    "Four-point transform",
    "Coverage re-read",
    "Parallelogram or frame grid",
    "Alternative triples",
];
// The matrix decode under the stages, in marks; its state is the last in a states string
const int Bar = -1;

// The picture on the left, the path through the outline's stages on the right. marks lists the boxes
// that carry the design record's numbered notes, in the notes' order; Bar is the matrix decode.
void InputFigure(string path, string[] boxes, Func<StringBuilder, double, double, double, string[]> picture, int[] marks, string states, int dashedFrom, string defs = "")
    => Figure(path, boxes, (sb, x, y, w, h) => picture(sb, x, y, w), 0, marks, states, dashedFrom, defs);

// A wide symbol's figure: the picture across the top, pictureHeight high, and the path under it
void InputFigureWide(string path, string[] boxes, Func<StringBuilder, double, double, double, double, string[]> picture, double pictureHeight, int[] marks, string states, int dashedFrom, string defs = "")
    => Figure(path, boxes, picture, pictureHeight, marks, states, dashedFrom, defs);

void Figure(string path, string[] boxes, Func<StringBuilder, double, double, double, double, string[]> picture, double wideHeight, int[] marks, string states, int dashedFrom, string defs)
{
    const double PX = 24, PY = 20, PS = 260;
    const double BW = 132, BH = 62, BG = 20, RG = 30;
    const double FW = 4 * BW + 3 * BG;
    var wide = wideHeight > 0;
    var pw = wide ? FW : PS; var ph = wide ? wideHeight : PS;
    var body = new StringBuilder();

    body.Append($"<rect class=\"box\" x=\"{PX}\" y=\"{PY}\" width=\"{F(pw)}\" height=\"{F(ph)}\" rx=\"8\"/>");
    var legend = picture(body, PX, PY, pw, ph);
    var lx = PX; var ly = PY + ph + 24;
    foreach (var item in legend)
    {
        var parts = item.Split('|');
        var width = 20 + parts[1].Length * 6.0 + 18;
        if (lx + width > PX + pw + 10)
        {
            lx = PX;
            ly += 20;
        }
        body.Append(parts[0] switch
        {
            "det" => Dot((lx + 6, ly - 4), 4),
            "detl" => Line((lx, ly - 4), (lx + 14, ly - 4), "detl"),
            "detd" => Line((lx, ly - 4), (lx + 14, ly - 4), "detd"),
            "pred" => $"<rect class=\"pred\" x=\"{F(lx + 1)}\" y=\"{F(ly - 10)}\" width=\"12\" height=\"12\"/>",
            "twopx" => $"<rect class=\"twopx\" x=\"{F(lx + 1)}\" y=\"{F(ly - 10)}\" width=\"12\" height=\"12\"/>",
            "predr" => $"<circle class=\"predr\" cx=\"{F(lx + 6)}\" cy=\"{F(ly - 4)}\" r=\"4\"/>",
            "est" => Cross((lx + 6, ly - 4), 4, "estx"),
            "estl" => Line((lx, ly - 4), (lx + 14, ly - 4), "est"),
            "row" => Line((lx, ly - 4), (lx + 14, ly - 4), "row"),
            "like" => LikeLine((lx, ly - 4), (lx + 14, ly - 4)),
            "tick" => Line((lx + 2, ly - 4), (lx + 12, ly - 4), "tick"),
            "err" => $"<rect class=\"err\" x=\"{F(lx + 1)}\" y=\"{F(ly - 10)}\" width=\"12\" height=\"12\"/>",
            "lat" => Line((lx, ly - 4), (lx + 14, ly - 4), "lat"),
            "blk" => $"<rect class=\"blk\" x=\"{F(lx + 1)}\" y=\"{F(ly - 10)}\" width=\"12\" height=\"12\"/>",
            "px" => $"<rect fill=\"none\" stroke=\"#8c959f\" x=\"{F(lx + 1)}\" y=\"{F(ly - 10)}\" width=\"12\" height=\"12\"/>",
            "fmt" => $"<rect class=\"fmt\" x=\"{F(lx + 1)}\" y=\"{F(ly - 10)}\" width=\"12\" height=\"12\"/>",
            "shade" => $"<rect fill=\"#1f2328\" fill-opacity=\".35\" x=\"{F(lx + 1)}\" y=\"{F(ly - 10)}\" width=\"12\" height=\"12\"/>",
            _ => "",
        });
        body.Append(Text(lx + 20, ly, parts[1], "xs"));
        lx += width;
    }

    var numbers = new Dictionary<int, List<int>>();
    for (var i = 0; i < marks.Length; i++)
    {
        if (!numbers.TryGetValue(marks[i], out var list))
            numbers[marks[i]] = list = [];
        list.Add(i + 1);
    }

    // Rows of four stages, then the matrix decode every grid goes through; beside the picture, or under a wide one
    var FX = wide ? PX : PX + PS + 32;
    var FY = wide ? ly + 28 : PY;
    (double X, double Y) At(int b) => (FX + (b % 4) * (BW + BG), FY + (b / 4) * (BH + RG));
    (string Box, string Text) Classes(char state) => state switch
    {
        'K' => ("bk", "bkt"),
        'U' => ("bu", "but"),
        'C' => ("bc", "bct"),
        _ => ("bs", "bst"),
    };
    for (var b = 0; b < boxes.Length; b++)
    {
        var (x, y) = At(b);
        var (box, t) = Classes(states[b]);
        body.Append($"<rect class=\"{box}\" x=\"{F(x)}\" y=\"{F(y)}\" width=\"{BW}\" height=\"{BH}\" rx=\"8\"/>");
        var lines = Wrap(boxes[b], 18);
        for (var l = 0; l < lines.Count; l++)
            body.Append(Text(x + 10, y + 20 + l * 15, lines[l], t));
        if (numbers.TryGetValue(b, out var ns))
            for (var k = 0; k < ns.Count; k++)
                body.Append(Marker(x + BW - 12 - (ns.Count - 1 - k) * 19, y, ns[k]));
    }
    for (var b = 0; b < boxes.Length - 1; b++)
    {
        var (x, y) = At(b);
        var cls = b >= dashedFrom ? "flowd" : "flow";
        if (b % 4 == 3)
        {
            var (nx, ny) = At(b + 1);
            body.Append($"<path class=\"{cls}\" marker-end=\"url(#ah)\" d=\"M{F(x + BW / 2)} {F(y + BH + 2)}V{F(y + BH + RG / 2)}H{F(nx + BW / 2)}V{F(ny - 4)}\"/>");
        }
        else
        {
            body.Append($"<path class=\"{cls}\" marker-end=\"url(#ah)\" d=\"M{F(x + BW + 2)} {F(y + BH / 2)}H{F(x + BW + BG - 4)}\"/>");
        }
    }
    var rows = (boxes.Length + 3) / 4;
    var barY = FY + rows * BH + (rows - 1) * RG + 14;
    {
        var (box, t) = Classes(states[boxes.Length]);
        body.Append($"<rect class=\"{box}\" x=\"{F(FX)}\" y=\"{F(barY)}\" width=\"{F(FW)}\" height=\"30\" rx=\"8\"/>");
        body.Append(Text(FX + 10, barY + 20, "Matrix decode", t));
        if (numbers.TryGetValue(Bar, out var ns))
            for (var k = 0; k < ns.Count; k++)
                body.Append(Marker(FX + 118 + k * 19, barY + 15, ns[k]));
    }
    var ky = barY + 30 + 26;
    (string Box, string Label)[] key = [("bk", "key"), ("bu", "runs"), ("bc", "only if needed"), ("bs", "skipped")];
    var kx = FX;
    foreach (var (box, label) in key)
    {
        body.Append($"<rect class=\"{box}\" x=\"{F(kx)}\" y=\"{F(ky - 11)}\" width=\"18\" height=\"14\" rx=\"4\"/>");
        body.Append(Text(kx + 26, ky, label, "xs"));
        kx += 26 + label.Length * 6.0 + 22;
    }

    var w = (int)Math.Ceiling(FX + FW + 24);
    var h = (int)Math.Ceiling(Math.Max(ly, ky) + 20);
    var sb = new StringBuilder();
    sb.Append(Card(w, h, defs));
    sb.Append(body);
    sb.Append("</svg>");
    File.WriteAllText(path, sb.ToString());
}

// Clean
InputFigure(Path.Combine(qrDir, "decode-input-clean.svg"), qrBoxes,
    (sb, x, y, s) =>
    {
        var map = Square(25, x + 20, y + 20, s - 40);
        sb.Append(Symbol(map, v2));
        sb.Append(Line(map.Map(tl.U, tl.V), map.Map(tr.U, tr.V), "detl"));
        sb.Append(Line(map.Map(tl.U, tl.V), map.Map(bl.U, bl.V), "detl"));
        sb.Append(Line(map.Map(tr.U, tr.V), map.Map(bl.U, bl.V), "est"));
        sb.Append(Window(map, al, 3, "pred"));
        foreach (var f in new[] { tl, tr, bl })
            sb.Append(Dot(map.Map(f.U, f.V), 5));
        sb.Append(Dot(map.Map(al.U, al.V), 3.5));
        return ["det|found centre", "detl|finder line", "estl|longest side", "pred|alignment search"];
    },
    [],
    "UUUSUUUSUSCSU", 8);

// Rotated or mirrored
InputFigure(Path.Combine(qrDir, "decode-input-rotated.svg"), qrBoxes,
    (sb, x, y, s) =>
    {
        var map = Rotated(25, x + 20, y + 20, s - 40, 30);
        sb.Append(Symbol(map, v2));
        var c = map.Map(tl.U, tl.V);
        var reach = (map.Map(1, 0).X - map.Map(0, 0).X) / Math.Cos(Math.PI / 6) * 5.4;
        sb.Append(Line(map.Map(tl.U, tl.V), map.Map(tr.U, tr.V), "detl"));
        sb.Append(Line(map.Map(tl.U, tl.V), map.Map(bl.U, bl.V), "detl"));
        sb.Append(Line(map.Map(tr.U, tr.V), map.Map(bl.U, bl.V), "est"));
        sb.Append(Line((c.X - reach, c.Y), (c.X + reach, c.Y), "row"));
        sb.Append(Text(c.X + reach + 4, c.Y + 4, "image row", "rowt"));
        foreach (var f in new[] { tl, tr, bl })
            sb.Append(Dot(map.Map(f.U, f.V), 5));
        sb.Append(LabelAway(map, tl, "TL", 16));
        return ["det|found centre", "detl|finder line", "row|image row", "estl|longest side"];
    },
    [4, Bar],
    "UUUSKUUSUCCSK", 8);

// Keystone: tilted back about the horizontal axis, so the left finder line runs into the distance
InputFigure(Path.Combine(qrDir, "decode-input-keystone.svg"), qrBoxes,
    (sb, x, y, s) =>
    {
        var map = Keystone(25, x + 20, y + 20, s - 40, 0.25);
        sb.Append(Symbol(map, v2));
        var a = map.Map(tl.U, tl.V); var b = map.Map(tr.U, tr.V); var c = map.Map(bl.U, bl.V);
        sb.Append(Line(a, b, "detl"));
        sb.Append(Line(a, c, "detl"));
        sb.Append(Line(map.Map(-1.1, tl.V - 0.5), map.Map(-1.1, tl.V + 0.5), "tick"));
        sb.Append(Line(map.Map(-1.1, bl.V - 0.5), map.Map(-1.1, bl.V + 0.5), "tick"));
        var est = ParallelogramAlignment(map, 25);
        sb.Append($"<path class=\"est\" d=\"M{F(b.X)} {F(b.Y)}L{F(b.X + c.X - a.X)} {F(b.Y + c.Y - a.Y)}L{F(c.X)} {F(c.Y)}\"/>");
        sb.Append(Cross(est, 5, "estx"));
        sb.Append(Window(map, al, 3, "pred"));
        var pa = map.Map(al.U, al.V);
        sb.Append($"<path class=\"detd\" d=\"M{F(a.X)} {F(a.Y)}L{F(b.X)} {F(b.Y)}L{F(pa.X)} {F(pa.Y)}L{F(c.X)} {F(c.Y)}Z\"/>");
        foreach (var f in new[] { tl, tr, bl })
            sb.Append(Dot(map.Map(f.U, f.V), 5));
        sb.Append(Dot(pa, 3.5));
        return ["det|found centre", "tick|one module, near and far", "pred|alignment search", "est|parallelogram estimate", "detd|four-point anchors"];
    },
    [4, 5, 6, 8, 10],
    "UUUSKKKSKCCSU", 8);

// Grey edges and wrong modules
InputFigure(Path.Combine(qrDir, "decode-input-degraded.svg"), qrBoxes,
    (sb, x, y, s) =>
    {
        var map = Square(25, x + 20, y + 20, s - 40);
        sb.Append($"<g filter=\"url(#soft)\">{Symbol(map, damaged)}</g>");
        foreach (var (r, c) in flips)
            sb.Append($"<path class=\"err\" d=\"{Poly(map, c - 0.1, r - 0.1, c + 1.1, r + 1.1)}\"/>");
        foreach (var f in new[] { tl, tr, bl })
            sb.Append(Dot(map.Map(f.U, f.V), 4));
        return ["det|refined centre", "err|wrong module"];
    },
    [9, Bar],
    "UUUSUUUSUCCSK", 8,
    "<filter id=\"soft\" x=\"-5%\" y=\"-5%\" width=\"110%\" height=\"110%\"><feGaussianBlur stdDeviation=\"1.2\"/></filter>");

// Thin rings: the top-left finder's row that reads 1:1:3:1:1, and the column through its centre, which reads only by like edges
InputFigure(Path.Combine(qrDir, "decode-input-thin-rings.svg"), qrBoxes,
    (sb, x, y, s) =>
    {
        var map = Rotated(25, x + 20, y + 20, s - 40, ThinTurn);
        sb.Append(SpreadSymbol(map, v2, ThinSpread));
        // In the render's pixels, then scaled into the picture
        var render = Rotated(25, 0, 0, ThinPx, ThinTurn);
        var thin = Render(v2, render, ThinPx, ThinPx, 4, null, false, false, ThinSpread);
        var threshold = Otsu(thin);
        var (cx, cy) = render.Map(tl.U, tl.V);
        var row = (int)cy + Enumerable.Range(-3, 7).First(dy => FinderRuns(thin, ThinPx, (int)cx, (int)cy + dy, 1, 0, threshold) is { } runs && IsRatio(runs));
        var k = (s - 40) / ThinPx;
        var reach = 5.5 * render.Map(1, 0).X - 5.5 * render.Map(0, 0).X;
        (double X, double Y) At(double px, double py) => (x + 20 + px * k, y + 20 + py * k);
        sb.Append(Line(At(cx - reach, row + 0.5), At(cx + reach, row + 0.5), "row"));
        sb.Append(LikeLine(At((int)cx + 0.5, cy - reach), At((int)cx + 0.5, cy + reach)));
        foreach (var f in new[] { tl, tr, bl })
            sb.Append(Dot(map.Map(f.U, f.V), 4));
        return ["det|found centre", "row|a row reading 1:1:3:1:1", "like|read by like edges"];
    },
    [1],
    "UKUSUUUSUCCSU", 8);

// Uneven lighting
InputFigure(Path.Combine(qrDir, "decode-input-lighting.svg"), qrBoxes,
    (sb, x, y, s) =>
    {
        var map = Square(25, x + 20, y + 20, s - 40);
        sb.Append(Symbol(map, v2));
        // The rendered shadow: across its edge along the diagonal, the light falls by half
        var scale = (s - 40) / (33.0 * 4);
        var from = x + 20 + (33 * 4 / 2.0 - 6) * scale; var to = x + 20 + (33 * 4 / 2.0 + 6) * scale;
        var fromY = from - x + y; var toY = to - x + y;
        sb.Append($"<linearGradient id=\"shadow\" gradientUnits=\"userSpaceOnUse\" x1=\"{F(from)}\" y1=\"{F(fromY)}\" x2=\"{F(to)}\" y2=\"{F(toY)}\"><stop offset=\"0\" stop-color=\"#000000\" stop-opacity=\"0\"/><stop offset=\"0.5\" stop-color=\"#000000\" stop-opacity=\".25\"/><stop offset=\"1\" stop-color=\"#000000\" stop-opacity=\".5\"/></linearGradient>");
        sb.Append($"<path fill=\"url(#shadow)\" d=\"{Poly(map, -4, -4, 29, 29)}\"/>");
        // The blocks the regional threshold averages over: two modules a side at the rendered density
        var blocks = new StringBuilder();
        for (var k = -2; k <= 27; k += 2)
        {
            blocks.Append(Line(map.Map(k, -2), map.Map(k, 27), "blk"));
            blocks.Append(Line(map.Map(-2, k), map.Map(27, k), "blk"));
        }
        sb.Append(blocks);
        return ["shade|soft shadow", "blk|regional threshold block"];
    },
    [0, 0],
    "KUUSUUUSUSCSU", 8);

// Low density
InputFigure(Path.Combine(qrDir, "decode-input-low-density.svg"), qrBoxes,
    (sb, x, y, s) =>
    {
        // The render itself, pixel by pixel
        const int px = 43;
        var raster = Render(v2, Square(25, 0, 0, px), px, px, 1, null, false, false);
        var cell = (s - 40) / px;
        var dark = new StringBuilder();
        for (var r = 0; r < px; r++)
            for (var c = 0; c < px; c++)
                if (raster[r * px + c] < 128)
                    dark.Append($"M{F(x + 20 + c * cell)} {F(y + 20 + r * cell)}h{F(cell)}v{F(cell)}h{F(-cell)}Z");
        sb.Append($"<rect fill=\"#ffffff\" stroke=\"#d0d7de\" x=\"{F(x + 20)}\" y=\"{F(y + 20)}\" width=\"{F(px * cell)}\" height=\"{F(px * cell)}\"/>");
        sb.Append($"<path class=\"ink\" d=\"{dark}\"/>");
        var pixelGrid = new StringBuilder();
        for (var k = 0; k <= px; k++)
        {
            pixelGrid.Append($"M{F(x + 20 + k * cell)} {F(y + 20)}v{F(px * cell)}");
            pixelGrid.Append($"M{F(x + 20)} {F(y + 20 + k * cell)}h{F(px * cell)}");
        }
        sb.Append($"<path stroke=\"#8c959f\" stroke-opacity=\".45\" stroke-width=\".5\" fill=\"none\" d=\"{pixelGrid}\"/>");
        // Module boundaries read off the two timing patterns: module row 6 and module column 6, finder to finder
        var map = Square(25, 0, 0, px);
        for (var k = 7; k <= 18; k++)
        {
            var b = map.Map(k, 6.5); var bx = x + 20 + Math.Round(b.X) * cell;
            sb.Append(Line((bx, y + 20 + map.Map(0, 6).Y * cell - 2), (bx, y + 20 + map.Map(0, 7).Y * cell + 2), "tick"));
            var d = map.Map(6.5, k); var dy = y + 20 + Math.Round(d.Y) * cell;
            sb.Append(Line((x + 20 + map.Map(6, 0).X * cell - 2, dy), (x + 20 + map.Map(7, 0).X * cell + 2, dy), "tick"));
        }
        return ["tick|module boundary", "px|pixel"];
    },
    [1, 3, 3],
    "UKUKSSSSSSSSU", 8);

// Light on dark
InputFigure(Path.Combine(qrDir, "decode-input-light-on-dark.svg"), qrBoxes,
    (sb, x, y, s) =>
    {
        var map = Square(25, x + 20, y + 20, s - 40);
        sb.Append(Symbol(map, v2, "#ffffff", "#1f2328"));
        foreach (var f in new[] { tl, tr, bl })
            sb.Append(Dot(map.Map(f.U, f.V), 5));
        return ["det|found centre"];
    },
    [0, 0],
    "KUUSUUUSUSCSU", 8);

// Large version: the mesh, with the bottom-right alignment pattern lost
InputFigure(Path.Combine(qrDir, "decode-input-large-version.svg"), qrBoxes,
    (sb, x, y, s) =>
    {
        var map = Keystone(73, x + 20, y + 20, s - 40, 0.12);
        sb.Append(Symbol(map, v14));
        // Annex E alignment coordinates of version 14
        int[] coords = [6, 26, 46, 66];
        var lattice = new StringBuilder();
        foreach (var cc in coords)
        {
            lattice.Append(Line(map.Map(coords[0] + 0.5, cc + 0.5), map.Map(coords[^1] + 0.5, cc + 0.5), "lat"));
            lattice.Append(Line(map.Map(cc + 0.5, coords[0] + 0.5), map.Map(cc + 0.5, coords[^1] + 0.5), "lat"));
        }
        sb.Append(lattice);
        // The coordinate-6 row and column are extrapolated, and the painted-over node is predicted from the others
        foreach (var r in coords)
            foreach (var c in coords)
            {
                var p = map.Map(c + 0.5, r + 0.5);
                sb.Append(r == 6 || c == 6 || (r == 66 && c == 66) ? $"<circle class=\"predr\" cx=\"{F(p.X)}\" cy=\"{F(p.Y)}\" r=\"3\"/>" : Dot(p, 3));
            }
        return ["det|found node", "predr|predicted node", "lat|mesh"];
    },
    [7],
    "UUUSUUUKCCCSU", 8);

// ---------- The Micro QR input figures ----------

// The Micro QR image-level outline's stages, in its order
string[] microBoxes =
[
    "Binarization pass",
    "Finder candidates",
    "Module sizes and centre",
    "Sizes M4 to M1",
    "Coverage re-read",
    "Timing frame",
    "Low density",
    "Arbitrary orientation",
    "Scale search",
    "Perspective search",
];
// Past the first size's grid, every stage runs only when the grids before it failed
const int MicroDashedFrom = 3;

// A render drawn pixel by pixel, filling the picture, each pixel at its own grey; returns the size of one pixel
double Raster(StringBuilder sb, bool[,] m, int px, int supersample, bool blur, double x, double y, double s)
{
    var dim = m.GetLength(0);
    var raster = Render(m, Square(dim, 0, 0, px, 2), px, px, supersample, null, false, blur);
    var cell = (s - 40) / px;
    var levels = new SortedDictionary<byte, StringBuilder>();
    for (var r = 0; r < px; r++)
        for (var c = 0; c < px; c++)
        {
            var v = raster[r * px + c];
            if (v >= 220)
                continue;
            if (!levels.TryGetValue(v, out var path))
                levels[v] = path = new StringBuilder();
            path.Append($"M{F(x + 20 + c * cell)} {F(y + 20 + r * cell)}h{F(cell)}v{F(cell)}h{F(-cell)}Z");
        }
    sb.Append($"<rect fill=\"#ffffff\" stroke=\"#d0d7de\" x=\"{F(x + 20)}\" y=\"{F(y + 20)}\" width=\"{F(px * cell)}\" height=\"{F(px * cell)}\"/>");
    // Ink 30 and paper 220 drawn as the figures' ink and white
    foreach (var (v, path) in levels)
    {
        var g = (int)Math.Round(0x1f + (255 - 0x1f) * (v - 30) / 190.0);
        sb.Append($"<path fill=\"#{g:x2}{g:x2}{g:x2}\" d=\"{path}\"/>");
    }
    var pixelGrid = new StringBuilder();
    for (var k = 0; k <= px; k++)
    {
        pixelGrid.Append($"M{F(x + 20 + k * cell)} {F(y + 20)}v{F(px * cell)}");
        pixelGrid.Append($"M{F(x + 20)} {F(y + 20 + k * cell)}h{F(px * cell)}");
    }
    sb.Append($"<path stroke=\"#8c959f\" stroke-opacity=\".45\" stroke-width=\".5\" fill=\"none\" d=\"{pixelGrid}\"/>");
    return cell;
}

// The first pixel of module k along an axis of a crisp render: the first whose centre lies past the module's edge
static int FirstPixel(double edge) => (int)Math.Ceiling(edge - 0.5);

// Clean: an M3, so the M4 grid comes first
InputFigure(Path.Combine(microDir, "decode-input-clean.svg"), microBoxes,
    (sb, x, y, s) =>
    {
        var map = Square(15, x + 20, y + 20, s - 40, 2);
        sb.Append(Symbol(map, m3));
        sb.Append($"<path class=\"est\" d=\"{Poly(map, 0, 0, 17, 17)}\"/>");
        sb.Append($"<path class=\"detl\" d=\"{Poly(map, 0, 0, 15, 15)}\"/>");
        sb.Append(Dot(map.Map(3.5, 3.5), 5));
        return ["det|found centre", "estl|M4 grid", "detl|M3 grid"];
    },
    [],
    "UUUUSCSCCCU", MicroDashedFrom);

// Rotated or mirrored: the finder's axes from the angular sweep
InputFigure(Path.Combine(microDir, "decode-input-rotated.svg"), microBoxes,
    (sb, x, y, s) =>
    {
        var map = Rotated(17, x + 20, y + 20, s - 40, MicroTurn, 2);
        sb.Append(Symbol(map, Transposed(m4)));
        var c = map.Map(3.5, 3.5);
        var reach = Math.Sqrt(Math.Pow(map.Map(1, 0).X - map.Map(0, 0).X, 2) + Math.Pow(map.Map(1, 0).Y - map.Map(0, 0).Y, 2)) * 5;
        var rays = new StringBuilder();
        for (var deg = 0; deg < 180; deg += 15)
        {
            var rad = deg * Math.PI / 180;
            rays.Append(Line((c.X - reach * Math.Cos(rad), c.Y - reach * Math.Sin(rad)), (c.X + reach * Math.Cos(rad), c.Y + reach * Math.Sin(rad)), "row"));
        }
        sb.Append($"<g opacity=\".35\">{rays}</g>");
        sb.Append(Line(map.Map(-1, 3.5), map.Map(8, 3.5), "detl"));
        sb.Append(Line(map.Map(3.5, -1), map.Map(3.5, 8), "detl"));
        sb.Append(Dot(c, 5));
        return ["det|found centre", "row|sweep direction", "detl|finder axes"];
    },
    [7, Bar],
    "UUUUCCSKCCK", MicroDashedFrom);

// Keystone: the grid a finder's axes and sizes give, against the symbol's edge
InputFigure(Path.Combine(microDir, "decode-input-keystone.svg"), microBoxes,
    (sb, x, y, s) =>
    {
        var map = Keystone(17, x + 20, y + 20, s - 40, MicroShrink, 2);
        sb.Append(Symbol(map, m4));
        var o = map.Map(3.5, 3.5); var pu = map.Map(4.5, 3.5); var pv = map.Map(3.5, 4.5);
        double ux = pu.X - o.X, uy = pu.Y - o.Y, vx = pv.X - o.X, vy = pv.Y - o.Y;
        (double X, double Y) Affine(double u, double v) => (o.X + (u - 3.5) * ux + (v - 3.5) * vx, o.Y + (u - 3.5) * uy + (v - 3.5) * vy);
        var a = Affine(0, 0); var b = Affine(17, 0); var cc = Affine(17, 17); var d = Affine(0, 17);
        sb.Append($"<path class=\"est\" d=\"M{F(a.X)} {F(a.Y)}L{F(b.X)} {F(b.Y)}L{F(cc.X)} {F(cc.Y)}L{F(d.X)} {F(d.Y)}Z\"/>");
        sb.Append($"<path class=\"detl\" d=\"{Poly(map, 0, 0, 17, 17)}\"/>");
        sb.Append(Dot(o, 5));
        return ["det|found centre", "estl|grid from the finder's axes", "detl|symbol edge"];
    },
    [4, 7, 8, 9, Bar],
    "UUUUKCSKKCK", MicroDashedFrom);

// Grey edges: each pixel at its own grey
InputFigure(Path.Combine(microDir, "decode-input-grey-edges.svg"), microBoxes,
    (sb, x, y, s) =>
    {
        Raster(sb, m4, MicroGreyPx, 4, MicroGreyBlur, x, y, s);
        return ["px|pixel, at its grey"];
    },
    [4, Bar],
    "UUUUKCSCCCK", MicroDashedFrom);

// Snapped scale: whole pixels a module at the finder, the symbol's own pitch along the timing patterns
InputFigure(Path.Combine(microDir, "decode-input-snapped.svg"), microBoxes,
    (sb, x, y, s) =>
    {
        var cell = Raster(sb, m4, MicroSnapPx, 1, false, x, y, s);
        var map = Square(17, 0, 0, MicroSnapPx, 2);
        (double X, double Y) P(double px, double py) => (x + 20 + px * cell, y + 20 + py * cell);
        // Timing boundaries along row 0 and column 0, where the render put them
        for (var k = 7; k <= 17; k++)
        {
            var e = FirstPixel(map.Map(k, 0).X);
            sb.Append(Line(P(e, FirstPixel(map.Map(0, 0).Y) - 0.6), P(e, FirstPixel(map.Map(0, 1).Y) + 0.6), "tick"));
            sb.Append(Line(P(FirstPixel(map.Map(0, 0).X) - 0.6, e), P(FirstPixel(map.Map(1, 0).X) + 0.6, e), "tick"));
        }
        // The finder in whole pixels: its first pixel and its width over seven modules
        var start = FirstPixel(map.Map(0, 0).X);
        var pitch = (FirstPixel(map.Map(7, 0).X) - start) / 7.0;
        var far = start + 17 * pitch;
        sb.Append($"<path class=\"est\" d=\"M{F(P(start, start).X)} {F(P(start, start).Y)}H{F(P(far, far).X)}V{F(P(far, far).Y)}H{F(P(start, start).X)}Z\"/>");
        var edge0 = map.Map(0, 0); var edge1 = map.Map(17, 17);
        sb.Append($"<path class=\"detl\" d=\"M{F(P(edge0.X, edge0.Y).X)} {F(P(edge0.X, edge0.Y).Y)}H{F(P(edge1.X, edge1.Y).X)}V{F(P(edge1.X, edge1.Y).Y)}H{F(P(edge0.X, edge0.Y).X)}Z\"/>");
        return ["tick|timing boundary", "estl|grid from the finder's size", "detl|symbol edge"];
    },
    [5],
    "UUUUSKSCCCU", MicroDashedFrom);

// Low density: module boundaries read off the timing patterns, at whole pixels
InputFigure(Path.Combine(microDir, "decode-input-low-density.svg"), microBoxes,
    (sb, x, y, s) =>
    {
        var cell = Raster(sb, m4, MicroLowPx, 1, false, x, y, s);
        var map = Square(17, 0, 0, MicroLowPx, 2);
        (double X, double Y) P(double px, double py) => (x + 20 + px * cell, y + 20 + py * cell);
        var row0 = FirstPixel(map.Map(0, 0).Y); var row1 = FirstPixel(map.Map(0, 1).Y);
        for (var k = 7; k <= 17; k++)
        {
            var e = FirstPixel(map.Map(k, 0).X);
            sb.Append(Line(P(e, row0 - 0.6), P(e, row1 + 0.6), "tick"));
            sb.Append(Line(P(row0 - 0.6, e), P(row1 + 0.6, e), "tick"));
            // A module two pixels wide
            if (k < 17 && FirstPixel(map.Map(k + 1, 0).X) - e == 2)
            {
                sb.Append($"<rect class=\"twopx\" x=\"{F(P(e, row0).X)}\" y=\"{F(P(e, row0).Y)}\" width=\"{F(2 * cell)}\" height=\"{F((row1 - row0) * cell)}\"/>");
                sb.Append($"<rect class=\"twopx\" x=\"{F(P(row0, e).X)}\" y=\"{F(P(row0, e).Y)}\" width=\"{F((row1 - row0) * cell)}\" height=\"{F(2 * cell)}\"/>");
            }
        }
        return ["tick|module boundary", "twopx|two-pixel module", "px|pixel"];
    },
    [6],
    "UUUUSCKCCCU", MicroDashedFrom);

// ---------- The rMQR input figures ----------

// The rMQR image-level outline's stages, in its order
string[] rmqrBoxes =
[
    "Binarization pass",
    "Finder candidates",
    "Axis-aligned frames",
    "Finder outline",
    "Arbitrary orientation",
    "Low density",
    "Finder-side format copy",
    "Sub-finder",
    "Isotropic grid",
    "Anisotropic grid",
    "Perimeter trace",
    "Perspective search",
    "Unrefined frame",
    "Coverage re-read",
];

// The wide panel's inner width, and the tallest a picture may be
const double WidePanel = 4 * 132 + 3 * 20, WideMaxHeight = 260;

// The scale that fits an input's image into the wide panel, and the panel height it needs
(double Scale, double Height) WideFit((H Map, int Width, int Height) input)
{
    var k = Math.Min((WidePanel - 40) / input.Width, (WideMaxHeight - 40) / input.Height);
    return (k, input.Height * k + 40);
}

// The input's map drawn in the panel at (x, y): centred, at the fitted scale
H WideMap((H Map, int Width, int Height) input, double x, double y)
{
    var (k, h) = WideFit(input);
    return input.Map.Scaled(k, x + (WidePanel - input.Width * k) / 2, y + 20);
}

// A rendered input drawn pixel by pixel in the panel, each pixel at its own grey; returns the size of one pixel
double RasterWide(StringBuilder sb, bool[,] m, (H Map, int Width, int Height) input, int supersample, bool blur, double x, double y)
{
    var raster = Render(m, input.Map, input.Width, input.Height, supersample, null, false, blur);
    var (cell, _) = WideFit(input);
    var ox = x + (WidePanel - input.Width * cell) / 2; var oy = y + 20;
    var levels = new SortedDictionary<byte, StringBuilder>();
    for (var r = 0; r < input.Height; r++)
        for (var c = 0; c < input.Width; c++)
        {
            var v = raster[r * input.Width + c];
            if (v >= 220)
                continue;
            if (!levels.TryGetValue(v, out var path))
                levels[v] = path = new StringBuilder();
            path.Append($"M{F(ox + c * cell)} {F(oy + r * cell)}h{F(cell)}v{F(cell)}h{F(-cell)}Z");
        }
    sb.Append($"<rect fill=\"#ffffff\" stroke=\"#d0d7de\" x=\"{F(ox)}\" y=\"{F(oy)}\" width=\"{F(input.Width * cell)}\" height=\"{F(input.Height * cell)}\"/>");
    foreach (var (v, path) in levels)
    {
        var g = (int)Math.Round(0x1f + (255 - 0x1f) * (v - 30) / 190.0);
        sb.Append($"<path fill=\"#{g:x2}{g:x2}{g:x2}\" d=\"{path}\"/>");
    }
    var pixelGrid = new StringBuilder();
    for (var k = 0; k <= input.Width; k++)
        pixelGrid.Append($"M{F(ox + k * cell)} {F(oy)}v{F(input.Height * cell)}");
    for (var k = 0; k <= input.Height; k++)
        pixelGrid.Append($"M{F(ox)} {F(oy + k * cell)}h{F(input.Width * cell)}");
    sb.Append($"<path stroke=\"#8c959f\" stroke-opacity=\".45\" stroke-width=\".5\" fill=\"none\" d=\"{pixelGrid}\"/>");
    return cell;
}

// Past the isotropic grid, every stage runs only when the grids before it failed
const int RmqrDashedFrom = 8;

// The finder-side format copy: rows 1-5 of columns 8-10, then rows 1-3 of column 11
string FormatCopy(H map)
{
    var fmt = new StringBuilder();
    for (var c = 8; c <= 11; c++)
        for (var r = 1; r <= (c == 11 ? 3 : 5); r++)
            fmt.Append(Poly(map, c + 0.1, r + 0.1, c + 0.9, r + 0.9));
    return $"<path class=\"fmt\" d=\"{fmt}\"/>";
}

// Module coordinates of the two centres in an R11x77
(double U, double V) rFinder = (3.5, 3.5), rSub = (77 - 2.5, 11 - 2.5);

// A tick across the edge at each end of every dark run of an R11x77 the perimeter trace meets: the top row from the first
// column past the separator, the left column below the finder, and the bottom row; drawn mirrored left to right when asked
string TracedRunEnds(H map, bool[,] m, bool mirrored)
{
    (double X, double Y) At(double column, double row) => map.Map(mirrored ? 77 - column : column, row);
    var ticks = new StringBuilder();
    void RunEnds(bool alongRow, int line, int first, int last)
    {
        bool Dark(int k) => alongRow ? m[line, k] : m[k, line];
        for (var k = first; k <= last; k++)
        {
            if (!Dark(k) || (k > first && Dark(k - 1)))
                continue;
            var end = k;
            while (end < last && Dark(end + 1))
                end++;
            foreach (var b in new[] { k, end + 1 })
            {
                var (p, q) = alongRow ? (At(b, line + 0.15), At(b, line + 0.85)) : (At(line + 0.15, b), At(line + 0.85, b));
                ticks.Append($"M{F(p.X)} {F(p.Y)}L{F(q.X)} {F(q.Y)}");
            }
        }
    }
    RunEnds(true, 0, 8, 76);
    RunEnds(false, 0, 8, 10);
    RunEnds(true, 10, 0, 76);
    return $"<path class=\"tick\" style=\"stroke-width:1.5\" d=\"{ticks}\"/>";
}

// Clean
InputFigureWide(Path.Combine(rmqrDir, "decode-input-clean.svg"), rmqrBoxes,
    (sb, x, y, w, h) =>
    {
        var map = WideMap(rmqrClean, x, y);
        sb.Append(Symbol(map, r11));
        sb.Append(FormatCopy(map));
        sb.Append(Line(map.Map(rFinder.U, rFinder.V), map.Map(rSub.U, rSub.V), "detd"));
        sb.Append(Dot(map.Map(rFinder.U, rFinder.V), 5));
        sb.Append(Dot(map.Map(rSub.U, rSub.V), 4));
        return ["det|found centre", "fmt|format copy", "detd|finder to sub-finder"];
    },
    WideFit(rmqrClean).Height, [], "UUUCCSUUUCCCCSU", RmqrDashedFrom);

// Rotated or mirrored: in the mirror the finder is at the right end and the sub-finder at the left
InputFigureWide(Path.Combine(rmqrDir, "decode-input-rotated.svg"), rmqrBoxes,
    (sb, x, y, w, h) =>
    {
        var map = WideMap(rmqrRotated, x, y);
        sb.Append(Symbol(map, MirroredX(r11)));
        (double U, double V) finder = (77 - 3.5, 3.5);
        var c = map.Map(finder.U, finder.V);
        var unit = Math.Sqrt(Math.Pow(map.Map(1, 0).X - map.Map(0, 0).X, 2) + Math.Pow(map.Map(1, 0).Y - map.Map(0, 0).Y, 2));
        var rays = new StringBuilder();
        for (var deg = 0; deg < 180; deg += 15)
        {
            var rad = deg * Math.PI / 180;
            rays.Append(Line((c.X - 5 * unit * Math.Cos(rad), c.Y - 5 * unit * Math.Sin(rad)), (c.X + 5 * unit * Math.Cos(rad), c.Y + 5 * unit * Math.Sin(rad)), "row"));
        }
        sb.Append($"<g opacity=\".35\">{rays}</g>");
        sb.Append($"<path class=\"detl\" d=\"{Poly(map, 71, 1, 76, 6)}\"/>");
        sb.Append(TracedRunEnds(map, r11, mirrored: true));
        sb.Append(Dot(c, 5));
        return ["det|found centre", "row|sweep direction", "detl|finder outline", "tick|run end traced"];
    },
    WideFit(rmqrRotated).Height, [3, 10], "UUUUCSUUSSUCCCU", RmqrDashedFrom);

// Keystone: the square frame the finder's run lengths give, against the outline of its light ring, and the run ends the trace meets on the three edges it follows
InputFigureWide(Path.Combine(rmqrDir, "decode-input-keystone.svg"), rmqrBoxes,
    (sb, x, y, w, h) =>
    {
        var map = WideMap(rmqrKeystone, x, y);
        sb.Append(Symbol(map, r11));
        var f = map.Map(rFinder.U, rFinder.V);
        var ux = map.Map(rFinder.U + 1, rFinder.V).X - f.X; var vy = map.Map(rFinder.U, rFinder.V + 1).Y - f.Y;
        sb.Append($"<path class=\"est\" d=\"M{F(f.X - 2.5 * ux)} {F(f.Y - 2.5 * vy)}H{F(f.X + 2.5 * ux)}V{F(f.Y + 2.5 * vy)}H{F(f.X - 2.5 * ux)}Z\"/>");
        sb.Append($"<path class=\"detl\" d=\"{Poly(map, 1, 1, 6, 6)}\"/>");
        sb.Append(TracedRunEnds(map, r11, mirrored: false));
        sb.Append(Dot(f, 5));
        return ["det|found centre", "estl|square frame", "detl|finder outline", "tick|run end traced"];
    },
    WideFit(rmqrKeystone).Height, [3, 10], "UUUKCSUSSSKCCCU", RmqrDashedFrom);

// Non-square modules: a module size along each axis, six modules each
InputFigureWide(Path.Combine(rmqrDir, "decode-input-non-square.svg"), rmqrBoxes,
    (sb, x, y, w, h) =>
    {
        var map = WideMap(rmqrNonSquare, x, y);
        sb.Append(Symbol(map, r11));
        sb.Append(Line(map.Map(1, rFinder.V - 0.25), map.Map(7, rFinder.V - 0.25), "tick"));
        sb.Append(Line(map.Map(rFinder.U + 0.25, 1), map.Map(rFinder.U + 0.25, 7), "tick"));
        sb.Append(Dot(map.Map(rFinder.U, rFinder.V), 4));
        return ["det|found centre", "tick|six modules along each axis"];
    },
    WideFit(rmqrNonSquare).Height, [2], "UUKCCSUUUCCCCSU", RmqrDashedFrom);

// Grey edges: each pixel at its own grey
InputFigureWide(Path.Combine(rmqrDir, "decode-input-grey-edges.svg"), rmqrBoxes,
    (sb, x, y, w, h) =>
    {
        RasterWide(sb, r11, rmqrGrey, 4, RmqrGreyBlur, x, y);
        var map = WideMap(rmqrGrey, x, y);
        sb.Append(Line(map.Map(rFinder.U, rFinder.V), map.Map(rSub.U, rSub.V), "detd"));
        sb.Append(Dot(map.Map(rFinder.U, rFinder.V), 4));
        sb.Append(Dot(map.Map(rSub.U, rSub.V), 3.5));
        return ["px|pixel, at its grey", "det|found centre", "detd|finder to sub-finder"];
    },
    WideFit(rmqrGrey).Height, [7, 8, 13], "UUUCCUUKKCCCSKU", RmqrDashedFrom);

// Hidden sub-finder: painted to paper, so no grid can be anchored on it
InputFigureWide(Path.Combine(rmqrDir, "decode-input-hidden-sub-finder.svg"), rmqrBoxes,
    (sb, x, y, w, h) =>
    {
        var map = WideMap(rmqrHidden, x, y);
        sb.Append(Symbol(map, r11Hidden));
        sb.Append(FormatCopy(map));
        sb.Append($"<path class=\"pred\" d=\"{Poly(map, 72, 6, 77, 11)}\"/>");
        sb.Append(TracedRunEnds(map, r11Hidden, mirrored: false));
        sb.Append(Dot(map.Map(rFinder.U, rFinder.V), 5));
        return ["det|found centre", "fmt|format copy", "pred|sub-finder, painted out", "tick|run end traced"];
    },
    WideFit(rmqrHidden).Height, [10, 12], "UUUCCSUUSSUSCSU", RmqrDashedFrom);

// Snapped scale: modules one or two whole pixels wide, and the format copy they carry
InputFigureWide(Path.Combine(rmqrDir, "decode-input-snapped.svg"), rmqrBoxes,
    (sb, x, y, w, h) =>
    {
        RasterWide(sb, r11, rmqrSnapped, 1, false, x, y);
        sb.Append($"<g opacity=\".75\">{FormatCopy(WideMap(rmqrSnapped, x, y))}</g>");
        return ["fmt|format copy", "px|pixel"];
    },
    WideFit(rmqrSnapped).Height, [6, 7], "UUUCCSKUUCCCCSU", RmqrDashedFrom);

// Low density: module boundaries read off the timing patterns, at whole pixels
InputFigureWide(Path.Combine(rmqrDir, "decode-input-low-density.svg"), rmqrBoxes,
    (sb, x, y, w, h) =>
    {
        var cell = RasterWide(sb, r11, rmqrLow, 1, false, x, y);
        var ox = x + (WidePanel - rmqrLow.Width * cell) / 2; var oy = y + 20;
        (double X, double Y) P(double px, double py) => (ox + px * cell, oy + py * cell);
        var map = rmqrLow.Map;
        var row0 = FirstPixel(map.Map(0, 0).Y); var row1 = FirstPixel(map.Map(0, 1).Y);
        var col0 = FirstPixel(map.Map(0, 0).X); var col1 = FirstPixel(map.Map(1, 0).X);
        for (var k = 7; k <= 77; k++)
        {
            var e = FirstPixel(map.Map(k, 0).X);
            sb.Append(Line(P(e, row0 - 0.6), P(e, row1 + 0.6), "tick"));
            if (k < 77 && FirstPixel(map.Map(k + 1, 0).X) - e == 2)
                sb.Append($"<rect class=\"twopx\" x=\"{F(P(e, row0).X)}\" y=\"{F(P(e, row0).Y)}\" width=\"{F(2 * cell)}\" height=\"{F((row1 - row0) * cell)}\"/>");
        }
        for (var k = 7; k <= 11; k++)
        {
            var e = FirstPixel(map.Map(0, k).Y);
            sb.Append(Line(P(col0 - 0.6, e), P(col1 + 0.6, e), "tick"));
            if (k < 11 && FirstPixel(map.Map(0, k + 1).Y) - e == 2)
                sb.Append($"<rect class=\"twopx\" x=\"{F(P(col0, e).X)}\" y=\"{F(P(col0, e).Y)}\" width=\"{F((col1 - col0) * cell)}\" height=\"{F(2 * cell)}\"/>");
        }
        return ["tick|module boundary", "twopx|two-pixel module", "px|pixel"];
    },
    WideFit(rmqrLow).Height, [1, 5], "UKUCCKCCCCCCCSU", RmqrDashedFrom);

// Preview: every figure on a light and on a dark page
if (preview)
{
    var files = new[] { qrDir, microDir, rmqrDir }
        .SelectMany(d => Directory.GetFiles(d, "*.svg").OrderBy(p => Path.GetFileName(p) == "decode-overview.svg" ? 0 : 1).ThenBy(p => p, StringComparer.Ordinal))
        .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
        .ToArray();
    var html = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><title>Decode figures</title><style>body{margin:0;font-family:system-ui,sans-serif}section{padding:24px}section.dark{background:#0d1117;color:#e6edf3}img{display:block;max-width:100%;height:auto;margin:0 0 20px}</style></head><body>");
    foreach (var theme in new[] { "light", "dark" })
    {
        html.Append($"<section class=\"{theme}\"><h2>{theme} page</h2>");
        foreach (var f in files)
            html.Append($"<img src=\"{f}\" alt=\"{f}\">");
        html.Append("</section>");
    }
    html.Append("</body></html>");
    File.WriteAllText(Path.Combine(root, "preview.html"), html.ToString());
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"did not decode: {string.Join(", ", failures)}");
    return 1;
}
Console.WriteLine($"written to {root}");
return 0;

// ---------- Geometry and rendering ----------

static bool[,] RmqrMatrix(RmQRCodeData qr)
{
    var m = new bool[qr.Height, qr.Width];
    for (var r = 0; r < qr.Height; r++)
        for (var c = 0; c < qr.Width; c++)
            m[r, c] = qr[r, c];
    return m;
}

// The symbol seen in a mirror: columns reversed
static bool[,] MirroredX(bool[,] m)
{
    var rows = m.GetLength(0); var cols = m.GetLength(1);
    var t = new bool[rows, cols];
    for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
            t[r, cols - 1 - c] = m[r, c];
    return t;
}

// A rectangular symbol upright, its modules pitchX by pitchY pixels, with a two-module quiet zone
static (H Map, int Width, int Height) RectUpright(bool[,] m, double pitchX, double pitchY, int qz = 2)
{
    var rows = m.GetLength(0); var cols = m.GetLength(1);
    var width = (int)Math.Round((cols + 2 * qz) * pitchX); var height = (int)Math.Round((rows + 2 * qz) * pitchY);
    return (H.Affine(qz * pitchX, qz * pitchY, pitchX, 0, 0, pitchY), width, height);
}

// The same turned by degrees about the image centre, the image just holding the symbol and its quiet zone
static (H Map, int Width, int Height) RectRotated(bool[,] m, double pitch, double degrees, int qz = 2)
{
    var rows = m.GetLength(0); var cols = m.GetLength(1);
    var rad = degrees * Math.PI / 180;
    var cos = Math.Cos(rad); var sin = Math.Sin(rad);
    double lw = (cols + 2 * qz) * pitch, lh = (rows + 2 * qz) * pitch;
    var width = (int)Math.Ceiling(Math.Abs(cos) * lw + Math.Abs(sin) * lh); var height = (int)Math.Ceiling(Math.Abs(sin) * lw + Math.Abs(cos) * lh);
    double ux = pitch * cos, uy = pitch * sin, vx = -pitch * sin, vy = pitch * cos;
    return (H.Affine(width / 2.0 - cols / 2.0 * ux - rows / 2.0 * vx, height / 2.0 - cols / 2.0 * uy - rows / 2.0 * vy, ux, uy, vx, vy), width, height);
}

// The same in perspective: one edge ('T', 'B', 'L' or 'R') of the symbol with its quiet zone shortened by
// shrink of its length, half at each end; the rMQR tests give that half as their tilt
static (H Map, int Width, int Height) RectKeystone(bool[,] m, double pitch, double shrink, char edge, int qz = 2)
{
    var rows = m.GetLength(0); var cols = m.GetLength(1);
    double w = (cols + 2 * qz) * pitch, h = (rows + 2 * qz) * pitch;
    double iw = shrink * w / 2, ih = shrink * h / 2;
    (double X, double Y) p0 = (0, 0), p1 = (w, 0), p2 = (w, h), p3 = (0, h);
    switch (edge)
    {
        case 'T': p0 = (iw, 0); p1 = (w - iw, 0); break;
        case 'B': p3 = (iw, h); p2 = (w - iw, h); break;
        case 'L': p0 = (0, ih); p3 = (0, h - ih); break;
        default: p1 = (w, ih); p2 = (w, h - ih); break;
    }
    return (H.QuadRect(cols + 2 * qz, rows + 2 * qz, p0, p1, p2, p3).Translated(qz, qz), (int)Math.Ceiling(w), (int)Math.Ceiling(h));
}

static bool[,] MicroMatrix(MicroQRCodeData qr)
{
    var m = new bool[qr.Size, qr.Size];
    for (var r = 0; r < qr.Size; r++)
        for (var c = 0; c < qr.Size; c++)
            m[r, c] = qr[r, c];
    return m;
}

static bool[,] Matrix(QRCodeData qr)
{
    var m = new bool[qr.Size, qr.Size];
    for (var r = 0; r < qr.Size; r++)
        for (var c = 0; c < qr.Size; c++)
            m[r, c] = qr[r, c];
    return m;
}

static bool[,] Transposed(bool[,] m)
{
    var n = m.GetLength(0);
    var t = new bool[n, n];
    for (var r = 0; r < n; r++)
        for (var c = 0; c < n; c++)
            t[c, r] = m[r, c];
    return t;
}

// A symbol of dim modules with a quiet zone of qz, upright, filling the square box
static H Square(int dim, double x, double y, double size, int qz = 4)
{
    var s = size / (dim + 2 * qz);
    return H.Affine(x + qz * s, y + qz * s, s, 0, 0, s);
}

// The same, turned by degrees about the box centre and scaled to stay inside it
static H Rotated(int dim, double x, double y, double size, double degrees, int qz = 4)
{
    var rad = degrees * Math.PI / 180;
    var cos = Math.Cos(rad); var sin = Math.Sin(rad);
    var s = size / (Math.Abs(cos) + Math.Abs(sin)) / (dim + 2 * qz);
    var cx = x + size / 2; var cy = y + size / 2;
    double ux = s * cos, uy = s * sin, vx = -s * sin, vy = s * cos;
    return H.Affine(cx - dim / 2.0 * (ux + vx), cy - dim / 2.0 * (uy + vy), ux, uy, vx, vy);
}

// The same, tilted away at the bottom: the far edge shorter by shrink of the near one. Standard QR's
// envelope counts the whole shrink; the perspective tests of all three give the half at each end as their tilt
static H Keystone(int dim, double x, double y, double size, double shrink, int qz = 4)
{
    var inset = shrink * size / 2;
    var outer = H.Quad(dim + 2 * qz, (x, y), (x + size, y), (x + size - inset, y + size * 0.86), (x + inset, y + size * 0.86));
    return outer.Translated(qz, qz);
}

// Otsu's threshold of an 8-bit image, dark below it
static int Otsu(byte[] pixels)
{
    var histogram = new long[256];
    foreach (var p in pixels)
        histogram[p]++;
    double total = pixels.Length, sum = 0;
    for (var i = 0; i < 256; i++)
        sum += i * (double)histogram[i];
    double backgroundSum = 0, backgroundWeight = 0, best = -1;
    var threshold = 0;
    for (var t = 0; t < 256; t++)
    {
        backgroundWeight += histogram[t];
        if (backgroundWeight == 0)
            continue;
        var foregroundWeight = total - backgroundWeight;
        if (foregroundWeight == 0)
            break;
        backgroundSum += t * (double)histogram[t];
        var meanBack = backgroundSum / backgroundWeight;
        var meanFore = (sum - backgroundSum) / foregroundWeight;
        var between = backgroundWeight * foregroundWeight * (meanBack - meanFore) * (meanBack - meanFore);
        if (between > best)
        {
            best = between;
            threshold = t + 1;
        }
    }
    return threshold;
}

// The five runs through (x, y) along (dx, dy) at the threshold, dark centre first, as a cross-check walks them; null off a dark pixel
static int[]? FinderRuns(byte[] image, int size, int x, int y, int dx, int dy, int threshold)
{
    bool Inside(int px, int py) => px >= 0 && py >= 0 && px < size && py < size;
    bool Dark(int px, int py) => Inside(px, py) && image[py * size + px] < threshold;
    int Walk(ref int px, ref int py, int sx, int sy, bool dark)
    {
        var n = 0;
        while (Inside(px, py) && Dark(px, py) == dark)
        {
            n++;
            px += sx;
            py += sy;
        }
        return n;
    }
    if (!Dark(x, y))
        return null;
    int bx = x, by = y, fx = x + dx, fy = y + dy;
    var centre = Walk(ref bx, ref by, -dx, -dy, true) + Walk(ref fx, ref fy, dx, dy, true);
    var r1 = Walk(ref bx, ref by, -dx, -dy, false);
    var r0 = Walk(ref bx, ref by, -dx, -dy, true);
    var r3 = Walk(ref fx, ref fy, dx, dy, false);
    var r4 = Walk(ref fx, ref fy, dx, dy, true);
    return [r0, r1, centre, r3, r4];
}

// 1:1:3:1:1, each run within half a module of its share of the total
static bool IsRatio(int[] r)
{
    double module = r.Sum() / 7.0, variance = module / 2;
    return r.All(x => x > 0) && Math.Abs(r[0] - module) < variance && Math.Abs(r[1] - module) < variance && Math.Abs(r[3] - module) < variance && Math.Abs(r[4] - module) < variance && Math.Abs(r[2] - 3 * module) < 3 * variance;
}

// The distances between edges of the same polarity at 2, 4, 4 and 2 modules, each within half a module, the edges moved by under a quarter of one, and a module of 2 px or more
static bool IsLikeEdges(int[] r)
{
    var twelve = r[0] + 2 * (r[1] + r[2] + r[3]) + r[4];
    return r.All(x => x > 0) && twelve >= 24
        && Math.Abs(24 * (r[0] + r[1]) - 4 * twelve) < twelve && Math.Abs(24 * (r[1] + r[2]) - 8 * twelve) < twelve
        && Math.Abs(24 * (r[2] + r[3]) - 8 * twelve) < twelve && Math.Abs(24 * (r[3] + r[4]) - 4 * twelve) < twelve
        && Math.Abs(24 * (r[1] + r[3] - r[0] - r[2] - r[4]) + 6 * twelve) < 5 * twelve;
}

// A soft-edged shadow over the lower right: the light falls by depth across an edge of the given width
static Func<double, double, double> Shadow(double size, double depth, double edge)
    => (x, y) =>
    {
        var t = ((x + y) / 2 - size * 0.5) / edge + 0.5;
        t = Math.Clamp(t, 0, 1);
        return 1 - depth * t * t * (3 - 2 * t);
    };

static bool IsDarkModule(bool[,] m, int r, int c) => r >= 0 && c >= 0 && r < m.GetLength(0) && c < m.GetLength(1) && m[r, c];

// Whether grid point (u, v) is ink once every dark edge has moved by spread of a module, outward when positive: a point
// changes when a module of the other colour lies within reach of it, measured to that module's nearest point
static bool IsInk(bool[,] m, double u, double v, double spread)
{
    var c = (int)Math.Floor(u); var r = (int)Math.Floor(v);
    var own = IsDarkModule(m, r, c);
    if (spread == 0 || own == spread > 0)
        return own;
    double fu = u - c, fv = v - r, reach = Math.Abs(spread);
    for (var dr = -1; dr <= 1; dr++)
        for (var dc = -1; dc <= 1; dc++)
        {
            if ((dr == 0 && dc == 0) || IsDarkModule(m, r + dr, c + dc) == own)
                continue;
            var du = dc < 0 ? fu : dc > 0 ? 1 - fu : 0;
            var dv = dr < 0 ? fv : dr > 0 ? 1 - fv : 0;
            if (du * du + dv * dv < reach * reach)
                return !own;
        }
    return own;
}

static byte[] Render(bool[,] m, H map, int width, int height, int supersample, Func<double, double, double>? light, bool invert, bool blur, double spread = 0)
{
    const double Ink = 30, Paper = 220;
    var inverse = map.Inverse();
    var pixels = new double[width * height];
    for (var py = 0; py < height; py++)
        for (var px = 0; px < width; px++)
        {
            var sum = 0.0;
            for (var j = 0; j < supersample; j++)
                for (var i = 0; i < supersample; i++)
                {
                    var (u, v) = inverse.Map(px + (i + 0.5) / supersample, py + (j + 0.5) / supersample);
                    sum += IsInk(m, u, v, spread) ? Ink : Paper;
                }
            pixels[py * width + px] = sum / (supersample * supersample);
        }
    if (blur)
    {
        var copy = (double[])pixels.Clone();
        for (var py = 1; py < height - 1; py++)
            for (var px = 1; px < width - 1; px++)
            {
                var sum = 0.0;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                        sum += copy[(py + dy) * width + px + dx];
                pixels[py * width + px] = sum / 9;
            }
    }
    var result = new byte[width * height];
    for (var py = 0; py < height; py++)
        for (var px = 0; px < width; px++)
        {
            var value = pixels[py * width + px];
            if (light is not null)
                value *= light(px + 0.5, py + 0.5);
            if (invert)
                value = 255 - value;
            result[py * width + px] = (byte)Math.Clamp(Math.Round(value), 0, 255);
        }
    return result;
}

// A projective map from grid coordinates (u, v) to image coordinates
readonly record struct H(double A, double B, double C, double D, double E, double F, double G, double Hh, double I)
{
    public (double X, double Y) Map(double u, double v)
    {
        var w = G * u + Hh * v + I;
        return ((A * u + B * v + C) / w, (D * u + E * v + F) / w);
    }

    public static H Affine(double ox, double oy, double ux, double uy, double vx, double vy) => new(ux, vx, ox, uy, vy, oy, 0, 0, 1);

    // The square [0, side]² onto the quad p0 (0, 0), p1 (side, 0), p2 (side, side), p3 (0, side)
    public static H Quad(double side, (double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3)
    {
        double dx1 = p1.X - p2.X, dx2 = p3.X - p2.X, dx3 = p0.X - p1.X + p2.X - p3.X;
        double dy1 = p1.Y - p2.Y, dy2 = p3.Y - p2.Y, dy3 = p0.Y - p1.Y + p2.Y - p3.Y;
        double g = 0, h = 0;
        if (Math.Abs(dx3) > 1e-12 || Math.Abs(dy3) > 1e-12)
        {
            var den = dx1 * dy2 - dx2 * dy1;
            g = (dx3 * dy2 - dx2 * dy3) / den;
            h = (dx1 * dy3 - dx3 * dy1) / den;
        }
        double a = p1.X - p0.X + g * p1.X, b = p3.X - p0.X + h * p3.X;
        double d = p1.Y - p0.Y + g * p1.Y, e = p3.Y - p0.Y + h * p3.Y;
        return new(a / side, b / side, p0.X, d / side, e / side, p0.Y, g / side, h / side, 1);
    }

    // The rectangle [0, cols] × [0, rows] onto the quad p0 (0, 0), p1 (cols, 0), p2 (cols, rows), p3 (0, rows)
    public static H QuadRect(double cols, double rows, (double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3)
    {
        var unit = Quad(1, p0, p1, p2, p3);
        return new(unit.A / cols, unit.B / rows, unit.C, unit.D / cols, unit.E / rows, unit.F, unit.G / cols, unit.Hh / rows, unit.I);
    }

    // The map drawn at scale k and moved by (dx, dy)
    public H Scaled(double k, double dx, double dy) => new(k * A + dx * G, k * B + dx * Hh, k * C + dx * I, k * D + dy * G, k * E + dy * Hh, k * F + dy * I, G, Hh, I);

    // The map of (u + du, v + dv)
    public H Translated(double du, double dv) => this with { C = A * du + B * dv + C, F = D * du + E * dv + F, I = G * du + Hh * dv + I };

    public H Inverse()
    {
        var det = A * (E * I - F * Hh) - B * (D * I - F * G) + C * (D * Hh - E * G);
        return new(
            (E * I - F * Hh) / det, (C * Hh - B * I) / det, (B * F - C * E) / det,
            (F * G - D * I) / det, (A * I - C * G) / det, (C * D - A * F) / det,
            (D * Hh - E * G) / det, (B * G - A * Hh) / det, (A * E - B * D) / det);
    }
}
