#:project ../../../../../src/FeatherQR/FeatherQR.csproj
#:property TargetFramework=net10.0
#:property Nullable=enable

// The agreed prototype of the Standard QR decode figures, kept for the plan that turns it into a tool.
//
//   dotnet run .github/docs/plans/references/decode-figures-prototype/gen.cs -- <output-directory>
//
// Writes decode-overview.svg (the stage strip), four decode-input-*.svg (one input class each) and
// preview.html (every figure on a light and on a dark page). The damaged symbol is decoded first,
// and the run fails unless Reed-Solomon corrects it.
using System.Globalization;
using System.Text;
using FeatherQR;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);

var qr = QRCodeGenerator.Create("FeatherQR decoder", QREccLevel.M, new QRCodeGeneratorOptions(version: QRVersionRange.Exactly(2), quietZoneSize: 0));
var dim = qr.Size;
var modules = new bool[dim, dim];
for (var r = 0; r < dim; r++)
    for (var c = 0; c < dim; c++)
        modules[r, c] = qr[r, c];
Console.WriteLine($"version {qr.Version}, dim {dim}");

// Damaged copy: data modules flipped, then checked to decode through Reed-Solomon
(int R, int C)[] flips = [(10, 11), (12, 14), (14, 9), (15, 13), (11, 19), (19, 10), (21, 13), (13, 21)];
var damaged = (bool[,])modules.Clone();
foreach (var (r, c) in flips)
    damaged[r, c] = !damaged[r, c];
{
    var flat = new byte[dim * dim];
    for (var r = 0; r < dim; r++)
        for (var c = 0; c < dim; c++)
            flat[r * dim + c] = damaged[r, c] ? (byte)1 : (byte)0;
    var ok = QRCodeDecoder.TryDecode(flat, dim, out var text, out var info);
    Console.WriteLine($"damaged decode: {ok} '{text}' corrected {info.ErrorsCorrected}");
    if (!ok || info.ErrorsCorrected == 0)
        throw new InvalidOperationException("damaged symbol must decode with corrections");
}

const string Font = "-apple-system,'Segoe UI',Helvetica,Arial,sans-serif";
string Style() => $$"""
<style>
.card{fill:#ffffff;stroke:#d0d7de}
.box{fill:#f6f8fa;stroke:#d8dee4}
.t{font:600 16px {{Font}};fill:#1f2328}
.st{font:13px {{Font}};fill:#59636e}
.h{font:600 12px {{Font}};fill:#1f2328}
.sm{font:12px {{Font}};fill:#1f2328}
.xs{font:11px {{Font}};fill:#59636e}
.lab{font:600 11px {{Font}};fill:#0969da;paint-order:stroke;stroke:#ffffff;stroke-width:3px;stroke-linejoin:round}
.estt{font:600 11px {{Font}};fill:#57606a;paint-order:stroke;stroke:#ffffff;stroke-width:3px;stroke-linejoin:round}
.ink{fill:#1f2328}
.paper{fill:#ffffff}
.det{fill:#0969da}
.detl{stroke:#0969da;fill:none;stroke-width:1.6}
.detd{stroke:#0969da;fill:none;stroke-width:1.4;stroke-dasharray:5 3}
.pred{stroke:#bf8700;fill:none;stroke-width:1.6;stroke-dasharray:4 3}
.predf{fill:#bf8700}
.est{stroke:#6e7781;fill:none;stroke-width:1.4;stroke-dasharray:3 3}
.estx{stroke:#57606a;fill:none;stroke-width:2.4}
.row{stroke:#8250df;fill:none;stroke-width:2.2;stroke-dasharray:5 3}
.rowt{font:600 11px {{Font}};fill:#8250df;paint-order:stroke;stroke:#ffffff;stroke-width:3px;stroke-linejoin:round}
.tick{stroke:#0969da;fill:none;stroke-width:3;stroke-linecap:round}
.pt{fill:#0969da;fill-opacity:.7}
.hist{fill:#d0d7de;stroke:#8c959f;stroke-width:1}
.thr{stroke:#bf8700;stroke-width:1.6;stroke-dasharray:3 2}
.sep{stroke:#d8dee4;stroke-width:1}
.err{stroke:#cf222e;fill:none;stroke-width:1.8}
.grid{stroke:#0969da;stroke-opacity:.35;stroke-width:.6;fill:none}
.fmt{fill:#0969da;fill-opacity:.55}
.arrow{stroke:#8c959f;stroke-width:1.6;fill:none}
.ck{fill:#0969da}.ckt{font:600 12px {{Font}};fill:#ffffff}.ckg{font:11px {{Font}};fill:#ddf4ff}
.cu{fill:#ddf4ff;stroke:#54aeff}.cut{font:12px {{Font}};fill:#0a3069}.cug{font:11px {{Font}};fill:#0969da}
.cc{fill:#ffffff;stroke:#bf8700;stroke-dasharray:4 3}.cct{font:12px {{Font}};fill:#7d4e00}.ccg{font:11px {{Font}};fill:#9a6700}
.cs{fill:#f6f8fa;stroke:#d8dee4}.cst{font:12px {{Font}};fill:#8c959f}.csg{font:11px {{Font}};fill:#8c959f}
</style>
""";

string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

// Square-to-quad projective map (Heckbert), grid (u, v) in modules over [0, dim]
Func<double, double, (double X, double Y)> Quad((double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3)
{
    double x0 = p0.X, y0 = p0.Y, x1 = p1.X, y1 = p1.Y, x2 = p2.X, y2 = p2.Y, x3 = p3.X, y3 = p3.Y;
    double dx1 = x1 - x2, dx2 = x3 - x2, dx3 = x0 - x1 + x2 - x3;
    double dy1 = y1 - y2, dy2 = y3 - y2, dy3 = y0 - y1 + y2 - y3;
    double g = 0, h = 0;
    if (Math.Abs(dx3) > 1e-9 || Math.Abs(dy3) > 1e-9)
    {
        var den = dx1 * dy2 - dx2 * dy1;
        g = (dx3 * dy2 - dx2 * dy3) / den;
        h = (dx1 * dy3 - dx3 * dy1) / den;
    }
    double a = x1 - x0 + g * x1, b = x3 - x0 + h * x3, c = x0;
    double d = y1 - y0 + g * y1, e = y3 - y0 + h * y3, f = y0;
    return (u, v) =>
    {
        var s = u / dim;
        var t = v / dim;
        var w = g * s + h * t + 1;
        return ((a * s + b * t + c) / w, (d * s + e * t + f) / w);
    };
}

Func<double, double, (double X, double Y)> Rotated(double cx, double cy, double size, double degrees)
{
    var rad = degrees * Math.PI / 180;
    var cos = Math.Cos(rad);
    var sin = Math.Sin(rad);
    var scale = size / dim;
    return (u, v) =>
    {
        var x = (u - dim / 2.0) * scale;
        var y = (v - dim / 2.0) * scale;
        return (cx + x * cos - y * sin, cy + x * sin + y * cos);
    };
}

Func<double, double, (double X, double Y)> Square(double x, double y, double size)
    => Quad((x, y), (x + size, y), (x + size, y + size), (x, y + size));

string Poly(Func<double, double, (double X, double Y)> map, double u0, double v0, double u1, double v1)
{
    var a = map(u0, v0); var b = map(u1, v0); var c = map(u1, v1); var d = map(u0, v1);
    return $"M{F(a.X)} {F(a.Y)}L{F(b.X)} {F(b.Y)}L{F(c.X)} {F(c.Y)}L{F(d.X)} {F(d.Y)}Z";
}

// The symbol: paper quad with a quiet zone, then dark modules as one path
string Symbol(Func<double, double, (double X, double Y)> map, bool[,] m, string inkClass = "ink", double seam = 0.35, string? extra = null)
{
    var sb = new StringBuilder();
    sb.Append($"<path class=\"paper\" stroke=\"#d0d7de\" d=\"{Poly(map, -2, -2, dim + 2, dim + 2)}\"/>");
    var path = new StringBuilder();
    for (var r = 0; r < dim; r++)
        for (var c = 0; c < dim; c++)
            if (m[r, c])
                path.Append(Poly(map, c, r, c + 1, r + 1));
    sb.Append($"<path class=\"{inkClass}\" stroke=\"currentColor\" stroke-width=\"{F(seam)}\" {extra ?? ""} d=\"{path}\"/>");
    return sb.ToString();
}

string Dot((double X, double Y) p, double r = 4, string cls = "det") => $"<circle class=\"{cls}\" cx=\"{F(p.X)}\" cy=\"{F(p.Y)}\" r=\"{F(r)}\"/>";
string Ring((double X, double Y) p, double r, string cls) => $"<circle class=\"{cls}\" cx=\"{F(p.X)}\" cy=\"{F(p.Y)}\" r=\"{F(r)}\"/>";
string Line((double X, double Y) a, (double X, double Y) b, string cls) => $"<line class=\"{cls}\" x1=\"{F(a.X)}\" y1=\"{F(a.Y)}\" x2=\"{F(b.X)}\" y2=\"{F(b.Y)}\"/>";
string Text(double x, double y, string s, string cls, string anchor = "start") => $"<text class=\"{cls}\" x=\"{F(x)}\" y=\"{F(y)}\" text-anchor=\"{anchor}\">{Esc(s)}</text>";
string Cross((double X, double Y) p, double s, string cls) => Line((p.X - s, p.Y - s), (p.X + s, p.Y + s), cls) + Line((p.X - s, p.Y + s), (p.X + s, p.Y - s), cls);

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

var tl = (U: 3.5, V: 3.5);
var tr = (U: dim - 3.5, V: 3.5);
var bl = (U: 3.5, V: dim - 3.5);
var al = (U: dim - 6.5, V: dim - 6.5);


// Parallelogram estimate of the alignment centre, as the decoder forms it from the three centres
(double X, double Y) ParallelogramAlignment(Func<double, double, (double X, double Y)> map)
{
    var a = map(tl.U, tl.V); var b = map(tr.U, tr.V); var c = map(bl.U, bl.V);
    var cornerX = b.X + c.X - a.X; var cornerY = b.Y + c.Y - a.Y;
    var k = 1 - 3.0 / (dim - 7);
    return (a.X + k * (cornerX - a.X), a.Y + k * (cornerY - a.Y));
}

string Window(Func<double, double, (double X, double Y)> map, (double U, double V) centre, double half, string cls)
    => $"<path class=\"{cls}\" d=\"{Poly(map, centre.U - half, centre.V - half, centre.U + half, centre.V + half)}\"/>";

// A label placed outside the triangle, away from its centroid
string LabelAway(Func<double, double, (double X, double Y)> map, (double U, double V) f, string label, double distance)
{
    var p = map(f.U, f.V);
    var g = map((tl.U + tr.U + bl.U) / 3, (tl.V + tr.V + bl.V) / 3);
    var dx = p.X - g.X; var dy = p.Y - g.Y;
    var n = Math.Sqrt(dx * dx + dy * dy);
    return Text(p.X + dx / n * distance, p.Y + dy / n * distance + 4, label, "lab", "middle");
}

// Two-hump luminance histogram, with the edge greys between the humps
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

// ---------- Figure A: the stages as pictures, a clean symbol ----------
{
    const int W = 1080, H = 356;
    var sb = new StringBuilder();
    sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{W}\" height=\"{H}\" viewBox=\"0 0 {W} {H}\" color=\"#1f2328\">");
    sb.Append(Style());
    sb.Append("<defs><filter id=\"lens\" x=\"-5%\" y=\"-5%\" width=\"110%\" height=\"110%\"><feGaussianBlur stdDeviation=\"0.8\"/></filter>");
    sb.Append("<linearGradient id=\"light\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"><stop offset=\"0\" stop-color=\"#ffffff\" stop-opacity=\".35\"/><stop offset=\"1\" stop-color=\"#000000\" stop-opacity=\".22\"/></linearGradient></defs>");
    sb.Append($"<rect class=\"card\" x=\"0.5\" y=\"0.5\" width=\"{W - 1}\" height=\"{H - 1}\" rx=\"12\"/>");
    sb.Append(Text(24, 34, "Standard QR image decode, stage by stage", "t"));
    sb.Append(Text(24, 54, "A clean symbol, read in the first pass. Each panel is one stage of the pipeline outline.", "st"));

    const double P = 150, S = 36, Gap = 28, X0 = 24, Y0 = 74;
    string[][] labels =
    [
        ["Luminance", "a grey image comes in"],
        ["Global threshold", "one Otsu split of the", "histogram"],
        ["Finder triple", "1:1:3:1:1 along a line", "through each centre"],
        ["Corner from the triangle", "top-left is opposite the", "longest side"],
        ["Alignment and grid", "searched where the frame", "predicts; 4-point transform"],
        ["Matrix decode", "format, unmask, deinterleave,", "Reed-Solomon, text"],
    ];
    for (var i = 0; i < 6; i++)
    {
        var x = X0 + i * (P + Gap);
        sb.Append($"<rect class=\"box\" x=\"{F(x)}\" y=\"{F(Y0)}\" width=\"{P}\" height=\"{P + S}\" rx=\"8\"/>");
        sb.Append(Line((x, Y0 + P), (x + P, Y0 + P), "sep"));
        var inner = (P - 20) / (dim + 4.0);
        var map = Square(x + 10 + 2 * inner, Y0 + 10 + 2 * inner, inner * dim);
        var sy = Y0 + P;
        switch (i)
        {
            case 0:
                sb.Append($"<path fill=\"#e7e3db\" d=\"{Poly(map, -2, -2, dim + 2, dim + 2)}\"/>");
                sb.Append($"<g filter=\"url(#lens)\">{Symbol(map, modules, "ink", extra: "style=\"fill:#4a4a48;stroke:#4a4a48\"").Replace("class=\"paper\" stroke=\"#d0d7de\"", "fill=\"#e7e3db\" stroke=\"none\"")}</g>");
                sb.Append($"<path fill=\"url(#light)\" d=\"{Poly(map, -2, -2, dim + 2, dim + 2)}\"/>");
                sb.Append(Histogram(x + 20, sy + 7, P - 40, 22));
                break;
            case 1:
                sb.Append(Symbol(map, modules));
                sb.Append(Histogram(x + 20, sy + 7, P - 40, 22));
                var tx = x + 20 + 0.52 * (P - 40);
                sb.Append(Line((tx, sy + 4), (tx, sy + 31), "thr"));
                break;
            case 2:
                sb.Append($"<g opacity=\"0.3\">{Symbol(map, modules)}</g>");
                sb.Append(Line(map(-1.5, tl.V), map(8.5, tl.V), "detl"));
                foreach (var f in new[] { tl, tr, bl })
                    sb.Append(Dot(map(f.U, f.V)));
                // The run pattern a line through a centre reads
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
                sb.Append($"<g opacity=\"0.3\">{Symbol(map, modules)}</g>");
                sb.Append(Line(map(tl.U, tl.V), map(tr.U, tr.V), "detl"));
                sb.Append(Line(map(tl.U, tl.V), map(bl.U, bl.V), "detl"));
                sb.Append(Line(map(tr.U, tr.V), map(bl.U, bl.V), "est"));
                foreach (var f in new[] { tl, tr, bl })
                    sb.Append(Dot(map(f.U, f.V)));
                sb.Append(LabelAway(map, tl, "TL", 13));
                sb.Append(LabelAway(map, tr, "TR", 13));
                sb.Append(LabelAway(map, bl, "BL", 13));
                sb.Append(Line((x + 26, sy + 18), (x + 46, sy + 18), "est"));
                sb.Append(Text(x + 52, sy + 22, "longest side", "xs"));
                break;
            case 4:
                sb.Append($"<g opacity=\"0.3\">{Symbol(map, modules)}</g>");
                var pts = new StringBuilder();
                for (var r = 0; r < dim; r++)
                    for (var c = 0; c < dim; c++)
                    {
                        var p = map(c + 0.5, r + 0.5);
                        pts.Append($"M{F(p.X - 0.9)} {F(p.Y)}a0.9 0.9 0 1 0 1.8 0a0.9 0.9 0 1 0 -1.8 0");
                    }
                sb.Append($"<path class=\"pt\" d=\"{pts}\"/>");
                sb.Append(Window(map, al, 3, "pred"));
                foreach (var f in new[] { tl, tr, bl })
                    sb.Append(Dot(map(f.U, f.V)));
                sb.Append(Ring(map(al.U, al.V), 3.5, "detl"));
                sb.Append(Dot(map(al.U, al.V), 2));
                sb.Append($"<rect class=\"pred\" x=\"{F(x + 10)}\" y=\"{F(sy + 12)}\" width=\"10\" height=\"10\"/>");
                sb.Append(Text(x + 25, sy + 21, "search", "xs"));
                sb.Append(Dot((x + 70, sy + 17), 2.2, "pt"));
                sb.Append(Text(x + 77, sy + 21, "sample point", "xs"));
                break;
            case 5:
                sb.Append($"<path class=\"paper\" stroke=\"#d0d7de\" d=\"{Poly(map, -2, -2, dim + 2, dim + 2)}\"/>");
                var cells = new StringBuilder();
                var fmt = new StringBuilder();
                for (var r = 0; r < dim; r++)
                    for (var c = 0; c < dim; c++)
                    {
                        var isFormat = (r == 8 && (c <= 8 || c >= dim - 8) && c != 6) || (c == 8 && (r <= 8 || r >= dim - 7) && r != 6);
                        if (isFormat)
                            fmt.Append(Poly(map, c + 0.12, r + 0.12, c + 0.88, r + 0.88));
                        else if (modules[r, c])
                            cells.Append(Poly(map, c + 0.12, r + 0.12, c + 0.88, r + 0.88));
                    }
                sb.Append($"<path class=\"ink\" d=\"{cells}\"/><path class=\"fmt\" d=\"{fmt}\"/>");
                sb.Append($"<rect class=\"fmt\" x=\"{F(x + 26)}\" y=\"{F(sy + 12)}\" width=\"10\" height=\"10\"/>");
                sb.Append(Text(x + 42, sy + 21, "format information", "xs"));
                break;
        }
        var ly = Y0 + P + S + 22;
        for (var l = 0; l < labels[i].Length; l++)
            sb.Append(Text(x + P / 2, ly + l * 16, labels[i][l], l == 0 ? "h" : "xs", "middle"));
        if (i < 5)
        {
            var ax = x + P + 6; var ay = Y0 + P / 2;
            sb.Append($"<path class=\"arrow\" d=\"M{F(ax)} {F(ay)}h{Gap - 12}m-5 -5l5 5l-5 5\"/>");
        }
    }
    sb.Append("</svg>");
    File.WriteAllText(Path.Combine(outDir, "decode-overview.svg"), sb.ToString());
}

// ---------- Figures B: one input class each ----------
(string Title, string Sub)[] boxes =
[
    ("Global threshold", "Otsu split of the histogram"),
    ("Finder triple", "row scan, then cross-checks"),
    ("Corner from the triangle", "opposite the longest side"),
    ("Centres and module sizes", "along both finder lines"),
    ("Frame and dimension", "finders' frame, estimate"),
    ("Alignment search", "where the frame predicts"),
    ("Mesh", "version 14+, alignment lattice"),
    ("Four-point transform", "samples module centres"),
    ("Matrix decode", "format to text, mirror retry"),
    ("If the grid fails", "other grids, corners, triples"),
];

string FlowStyle() => $$"""
<style>
.num{font:600 10px {{Font}};fill:#ffffff}
.bk{fill:#dafbe1;stroke:#4ac26b;stroke-width:1.2}.bkt{font:600 13px {{Font}};fill:#1f2328}.bks{font:11px {{Font}};fill:#116329}
.bu{fill:#f6f8fa;stroke:#d0d7de}.but{font:600 13px {{Font}};fill:#1f2328}.bus{font:11px {{Font}};fill:#59636e}
.bc{fill:#ffffff;stroke:#bf8700;stroke-dasharray:4 3}.bct{font:600 13px {{Font}};fill:#7d4e00}.bcs{font:11px {{Font}};fill:#9a6700}
.bs{fill:#ffffff;stroke:#d8dee4;stroke-dasharray:2 3}.bst{font:600 13px {{Font}};fill:#8c959f}.bss{font:11px {{Font}};fill:#afb8c1}
.flow{stroke:#8c959f;stroke-width:1.4;fill:none}
.flowd{stroke:#bf8700;stroke-width:1.4;fill:none;stroke-dasharray:4 3}
</style>
""";

string Marker(double cx, double cy, int n)
    => $"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"8\" fill=\"#1a7f37\"/><text class=\"num\" x=\"{F(cx)}\" y=\"{F(cy + 3.5)}\" text-anchor=\"middle\">{n}</text>";

void InputFigure(string file, string title, string subtitle, Func<StringBuilder, double, double, double, string[]> picture, (int Box, string Short, string Text)[] notes, string states)
{
    const int W = 1080;
    var body = new StringBuilder();

    // Picture and its legend
    const double PX = 24, PY = 74, PS = 330;
    body.Append($"<rect class=\"box\" x=\"{PX}\" y=\"{PY}\" width=\"{PS}\" height=\"{PS}\" rx=\"8\"/>");
    var legend = picture(body, PX, PY, PS);
    var lx = PX; var lyy = PY + PS + 24;
    foreach (var item in legend)
    {
        var parts = item.Split('|');
        var width = 20 + parts[1].Length * 6.0 + 18;
        if (lx + width > PX + PS + 10)
        {
            lx = PX;
            lyy += 20;
        }
        switch (parts[0])
        {
            case "det": body.Append(Dot((lx + 6, lyy - 4), 4)); break;
            case "detl": body.Append(Line((lx, lyy - 4), (lx + 14, lyy - 4), "detl")); break;
            case "detd": body.Append(Line((lx, lyy - 4), (lx + 14, lyy - 4), "detd")); break;
            case "pred": body.Append($"<rect class=\"pred\" x=\"{F(lx + 1)}\" y=\"{F(lyy - 10)}\" width=\"12\" height=\"12\"/>"); break;
            case "est": body.Append(Cross((lx + 6, lyy - 4), 4, "estx")); break;
            case "estl": body.Append(Line((lx, lyy - 4), (lx + 14, lyy - 4), "est")); break;
            case "row": body.Append(Line((lx, lyy - 4), (lx + 14, lyy - 4), "row")); break;
            case "tick": body.Append(Line((lx + 2, lyy - 4), (lx + 12, lyy - 4), "tick")); break;
            case "err": body.Append($"<rect class=\"err\" x=\"{F(lx + 1)}\" y=\"{F(lyy - 10)}\" width=\"12\" height=\"12\"/>"); break;
        }
        body.Append(Text(lx + 20, lyy, parts[1], "xs"));
        lx += width;
    }

    // Numbers follow the notes, which are written in flow order
    var shorts = new Dictionary<int, List<(int N, string Short)>>();
    var numbers = new int[notes.Length];
    var next = 0;
    for (var i = 0; i < notes.Length; i++)
    {
        if (notes[i].Box < 0)
            continue;
        numbers[i] = ++next;
        if (!shorts.TryGetValue(notes[i].Box, out var list))
            shorts[notes[i].Box] = list = [];
        list.Add((next, notes[i].Short));
    }

    // What the decoder does
    const double MX = 384, MY = 88;
    body.Append(Text(MX, MY, "What the decoder does", "h"));
    var y = MY + 28.0;
    for (var i = 0; i < notes.Length; i++)
    {
        var lines = Wrap(notes[i].Text, 96);
        body.Append(numbers[i] > 0 ? Marker(MX + 8, y - 4, numbers[i]) : $"<circle cx=\"{F(MX + 8)}\" cy=\"{F(y - 4)}\" r=\"2.2\" fill=\"#59636e\"/>");
        foreach (var line in lines)
        {
            body.Append(Text(MX + 24, y, line, "sm"));
            y += 17;
        }
        y += 9;
    }

    // Path through the pipeline
    var fy = Math.Max(lyy + 44, y + 20);
    body.Append(Text(24, fy, "Path through the pipeline", "h"));
    body.Append(Text(24, fy + 18, "Every pass runs this path: the global positive, the global negative, then the regional passes (see the shared passes).", "xs"));
    const double BW = 176, BH = 68, BG = 38;
    var r1 = fy + 34; var r2 = r1 + BH + 34;
    (double X, double Y) At(int b) => (24 + (b % 5) * (BW + BG), b < 5 ? r1 : r2);
    for (var b = 0; b < boxes.Length; b++)
    {
        var (x, by) = At(b);
        var (box, t, s) = states[b] switch
        {
            'K' => ("bk", "bkt", "bks"),
            'U' => ("bu", "but", "bus"),
            'C' => ("bc", "bct", "bcs"),
            _ => ("bs", "bst", "bss"),
        };
        body.Append($"<rect class=\"{box}\" x=\"{F(x)}\" y=\"{F(by)}\" width=\"{BW}\" height=\"{BH}\" rx=\"8\"/>");
        body.Append(Text(x + 12, by + 22, boxes[b].Title, t));
        if (shorts.TryGetValue(b, out var items))
        {
            for (var k = 0; k < items.Count; k++)
            {
                body.Append(Marker(x + 20, by + 38 + k * 16, items[k].N));
                body.Append(Text(x + 33, by + 42 + k * 16, items[k].Short, s));
            }
        }
        else
        {
            body.Append(Text(x + 12, by + 44, boxes[b].Sub, s));
        }
    }
    for (var b = 0; b < boxes.Length - 1; b++)
    {
        if (b == 4)
            continue;
        var (x, by) = At(b);
        var cls = b == 8 ? "flowd" : "flow";
        body.Append($"<path class=\"{cls}\" marker-end=\"url(#ah)\" d=\"M{F(x + BW + 3)} {F(by + BH / 2)}H{F(x + BW + BG - 5)}\"/>");
    }
    {
        var (x4, y4) = At(4); var (x5, y5) = At(5);
        var mid = y4 + BH + 17;
        body.Append($"<path class=\"flow\" marker-end=\"url(#ah)\" d=\"M{F(x4 + BW / 2)} {F(y4 + BH + 3)}V{F(mid)}H{F(x5 + BW / 2)}V{F(y5 - 5)}\"/>");
    }
    var ky = r2 + BH + 30;
    (string Box, string Label)[] key = [("bk", "does the work here (numbers match the notes)"), ("bu", "runs"), ("bc", "only if needed"), ("bs", "not reached for this input")];
    var kx = 24.0;
    foreach (var (box, label) in key)
    {
        body.Append($"<rect class=\"{box}\" x=\"{F(kx)}\" y=\"{F(ky - 11)}\" width=\"18\" height=\"14\" rx=\"4\"/>");
        body.Append(Text(kx + 26, ky, label, "xs"));
        kx += 26 + label.Length * 6.0 + 26;
    }

    var h = (int)Math.Ceiling(ky + 24);
    var sb = new StringBuilder();
    sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{W}\" height=\"{h}\" viewBox=\"0 0 {W} {h}\" color=\"#1f2328\">");
    sb.Append(Style());
    sb.Append(FlowStyle());
    sb.Append("<defs><filter id=\"soft\" x=\"-5%\" y=\"-5%\" width=\"110%\" height=\"110%\"><feGaussianBlur stdDeviation=\"1.5\"/></filter>");
    sb.Append("<marker id=\"ah\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"7\" markerHeight=\"7\" orient=\"auto-start-reverse\"><path d=\"M0 0L10 5L0 10z\" fill=\"#8c959f\"/></marker></defs>");
    sb.Append($"<rect class=\"card\" x=\"0.5\" y=\"0.5\" width=\"{W - 1}\" height=\"{h - 1}\" rx=\"12\"/>");
    sb.Append(Text(24, 34, title, "t"));
    sb.Append(Text(24, 54, subtitle, "st"));
    sb.Append(body);
    sb.Append("</svg>");
    File.WriteAllText(Path.Combine(outDir, file), sb.ToString());
}

// Clean
InputFigure("decode-input-clean.svg",
    "Clean: a screenshot, a render or a flat scan",
    "Upright, crisp and flat. The first grid decodes; nothing past the main path runs.",
    (sb, x, y, s) =>
    {
        var inner = (s - 40) / (dim + 4.0);
        var map = Square(x + 20 + 2 * inner, y + 20 + 2 * inner, inner * dim);
        sb.Append(Symbol(map, modules));
        sb.Append(Line(map(tl.U, tl.V), map(tr.U, tr.V), "detl"));
        sb.Append(Line(map(tl.U, tl.V), map(bl.U, bl.V), "detl"));
        sb.Append(Line(map(tr.U, tr.V), map(bl.U, bl.V), "est"));
        sb.Append(Window(map, al, 3, "pred"));
        foreach (var f in new[] { tl, tr, bl })
            sb.Append(Dot(map(f.U, f.V), 5));
        sb.Append(Dot(map(al.U, al.V), 3.5));
        return ["det|found centre", "detl|finder line", "estl|longest side", "pred|alignment search"];
    },
    [
        (-1, "", "Only two grey levels: one global threshold splits ink from paper, and the stages that need grey levels stay off."),
        (-1, "", "The strided row scan crosses each finder's centre band; the column, the row again and the falling diagonal confirm each candidate."),
        (-1, "", "The vertex opposite the longest side is the top-left finder; the cross product tells the other two apart."),
        (-1, "", "Module sizes agree at both ends of each finder line, so the finders' frame is the parallelogram of the centres."),
        (-1, "", "The alignment pattern is found where the frame predicts and anchors the fourth corner. The first grid decodes."),
    ],
    "UUUUUUSUUC");

// Rotated
InputFigure("decode-input-rotated.svg",
    "Rotated or mirrored",
    "Any angle. The geometry is read along the symbol's own lines, never along image rows.",
    (sb, x, y, s) =>
    {
        var size = (s - 40) / 1.366 * dim / (dim + 4.0);
        var map = Rotated(x + s / 2, y + s / 2, size, 30);
        sb.Append(Symbol(map, modules));
        var c = map(tl.U, tl.V);
        var reach = size / dim * 5.4;
        sb.Append(Line(map(tl.U, tl.V), map(tr.U, tr.V), "detl"));
        sb.Append(Line(map(tl.U, tl.V), map(bl.U, bl.V), "detl"));
        sb.Append(Line(map(tr.U, tr.V), map(bl.U, bl.V), "est"));
        sb.Append(Line((c.X - reach, c.Y), (c.X + reach, c.Y), "row"));
        sb.Append(Text(c.X + reach + 4, c.Y + 4, "image row", "rowt"));
        foreach (var f in new[] { tl, tr, bl })
            sb.Append(Dot(map(f.U, f.V), 5));
        sb.Append(LabelAway(map, tl, "TL", 16));
        return ["det|found centre", "detl|finder line", "row|image row", "estl|longest side"];
    },
    [
        (1, "any line", "A finder reads 1:1:3:1:1 along any line through its centre, so the row scan meets it at any angle."),
        (2, "longest side", "The longest side of the centres' triangle is still the diagonal, so the corner does not depend on the angle."),
        (3, "finder lines", "Module sizes are measured along the finder lines. An image row cuts a turned ring on the slant and reads it up to √2 too long."),
        (-1, "", "The transform carries the turn into the grid; sampling is the same as upright."),
        (8, "transposed", "A mirrored capture has the same finders and a transposed grid: a grid that does not read is decoded again transposed."),
    ],
    "UKKKUUSUKC");

// Keystone: tilted back about the horizontal axis, so the left finder line runs into the distance
InputFigure("decode-input-keystone.svg",
    "Keystone: the symbol in perspective",
    "A flat symbol tilted away at the bottom. Near modules draw larger than far ones.",
    (sb, x, y, s) =>
    {
        var m = 28.0;
        var full = Quad((x + m, y + m + 8), (x + s - m, y + m + 8), (x + s - m - 56, y + s - m - 18), (x + m + 56, y + s - m - 18));
        var map = Quad(full(2.2, 2.2), full(dim - 2.2, 2.2), full(dim - 2.2, dim - 2.2), full(2.2, dim - 2.2));
        sb.Append(Symbol(map, modules));
        var a = map(tl.U, tl.V); var b = map(tr.U, tr.V); var c = map(bl.U, bl.V);
        sb.Append(Line(a, b, "detl"));
        sb.Append(Line(a, c, "detl"));
        sb.Append(Line(map(-1.1, tl.V - 0.5), map(-1.1, tl.V + 0.5), "tick"));
        sb.Append(Line(map(-1.1, bl.V - 0.5), map(-1.1, bl.V + 0.5), "tick"));
        var est = ParallelogramAlignment(map);
        sb.Append($"<path class=\"est\" d=\"M{F(b.X)} {F(b.Y)}L{F(b.X + c.X - a.X)} {F(b.Y + c.Y - a.Y)}L{F(c.X)} {F(c.Y)}\"/>");
        sb.Append(Cross(est, 5, "estx"));
        sb.Append(Text(est.X + 9, est.Y + 16, "parallelogram", "estt"));
        sb.Append(Window(map, al, 3, "pred"));
        var pa = map(al.U, al.V);
        sb.Append($"<path class=\"detd\" d=\"M{F(a.X)} {F(a.Y)}L{F(b.X)} {F(b.Y)}L{F(pa.X)} {F(pa.Y)}L{F(c.X)} {F(c.Y)}Z\"/>");
        foreach (var f in new[] { tl, tr, bl })
            sb.Append(Dot(map(f.U, f.V), 5));
        sb.Append(Dot(pa, 3.5));
        return ["det|found centre", "tick|one module, near and far", "pred|search where the frame predicts", "est|parallelogram estimate", "detd|four-point anchors"];
    },
    [
        (1, "column may stretch", "Perspective draws a finder taller or shorter than it is wide; the column cross-check accepts that stretch."),
        (1, "rising diagonal", "A column far off its row is taken only when the rising diagonal reads 1:1:3:1:1 too, as every line through a stretched finder does."),
        (3, "near and far", "Along a finder line that runs into the distance, the module size differs at its two ends (here the left line)."),
        (4, "one plane", "One plane fits the three centres and both lines' changes of size: the finders' frame."),
        (4, "geometric mean", "Each line's module count divides by the geometric mean of its two end sizes, the count the frame implies; their plain mean would put the count short."),
        (5, "frame's prediction", "The frame predicts where the alignment pattern sits and how the grid turns there; the parallelogram of the centres drifts from it as the tilt grows."),
        (7, "anchored", "The three centres and the alignment centre anchor a four-point transform, which samples the grid."),
        (9, "other grids", "If the anchored grid fails, the parallelogram grid is tried; with no alignment pattern found, the parallelogram comes first and the frame after it."),
        (9, "corners, triples", "Tilted hard: other corners, then other triples, each only when its timing patterns read."),
    ],
    "UKUKKKSKUC");

// Degraded
InputFigure("decode-input-degraded.svg",
    "Grey edges and wrong modules",
    "Anti-aliased or resampled edges, and a few modules read the wrong way.",
    (sb, x, y, s) =>
    {
        var inner = (s - 40) / (dim + 4.0);
        var map = Square(x + 20 + 2 * inner, y + 20 + 2 * inner, inner * dim);
        sb.Append($"<g filter=\"url(#soft)\">{Symbol(map, damaged)}</g>");
        foreach (var (r, c) in flips)
            sb.Append($"<path class=\"err\" d=\"{Poly(map, c - 0.1, r - 0.1, c + 1.1, r + 1.1)}\"/>");
        foreach (var f in new[] { tl, tr, bl })
            sb.Append(Dot(map(f.U, f.V), 4));
        return ["det|centre, sub-pixel", "err|module read wrong"];
    },
    [
        (-1, "", "Grey pixels at the edges give the histogram values between its two levels, which switches the grey-level stages on."),
        (1, "grey re-measure", "A finder whose runs miss 1:1:3:1:1 by about a pixel is measured again from the grey levels before it is refused."),
        (3, "sub-pixel centres", "Each finder centre moves to the centroid of its centre square's darkness, a fraction of a pixel."),
        (8, "Reed-Solomon", "Modules read the wrong way are Reed-Solomon's: each block corrects up to its capacity."),
        (9, "coverage re-read", "If the grid still fails, it is read again by coverage: each module centre interpolated and split halfway between the two levels."),
        (-1, "", "Uneven light is the regional pass. Heavy blur or damage is out of scope."),
    ],
    "UKUKUUSUKC");

// Preview page: every figure on a light and on a dark page
{
    string[] files = ["decode-overview.svg", "decode-input-clean.svg", "decode-input-rotated.svg", "decode-input-keystone.svg", "decode-input-degraded.svg"];
    var html = new StringBuilder();
    html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Decode figures</title><style>body{margin:0;font-family:system-ui,sans-serif}section{padding:24px}section.dark{background:#0d1117;color:#e6edf3}section.light{background:#ffffff;color:#1f2328}img{display:block;max-width:100%;height:auto;margin:0 0 20px}</style></head><body>");
    foreach (var theme in new[] { "light", "dark" })
    {
        html.Append($"<section class=\"{theme}\"><h2>{theme} page</h2>");
        foreach (var f in files)
            html.Append($"<img src=\"{f}\" alt=\"{f}\">");
        html.Append("</section>");
    }
    html.Append("</body></html>");
    File.WriteAllText(Path.Combine(outDir, "preview.html"), html.ToString());
}
Console.WriteLine("written");
