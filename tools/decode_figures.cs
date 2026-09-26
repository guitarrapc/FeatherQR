#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:project ../src/FeatherQR/FeatherQR.csproj

using System.Globalization;
using System.Text;
using FeatherQR;

// Draws the Standard QR decode figures of the design record: a strip of the main path's stages on a
// clean symbol, and one figure per input class the image decoder reads.
//
//   dotnet run tools/decode_figures.cs -- .github/docs/images/standardqr
//   dotnet run tools/decode_figures.cs -- <directory> --preview      also a page of every figure, light and dark
//
// Each input class is also rendered to pixels and decoded through the public API, and the run fails
// unless it decodes. A box is green when, without its stage, the input would not read, or would read
// only after a grid fails. The boxes follow the image-level outline in
// .github/docs/specs/standardqr-spec-map.md, and change with it.

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var outDir = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? ".";
var preview = args.Contains("--preview");
Directory.CreateDirectory(outDir);

const string Payload = "FeatherQR decoder";
var v2 = Matrix(QRCodeGenerator.Create(Payload, QREccLevel.M, new QRCodeGeneratorOptions(version: QRVersionRange.Exactly(2), quietZoneSize: 0)));
const string MeshPayload = "FeatherQR decoder, a version 14 symbol for the mesh figure";
var v14 = Matrix(QRCodeGenerator.Create(MeshPayload, QREccLevel.M, new QRCodeGeneratorOptions(version: QRVersionRange.Exactly(14), quietZoneSize: 0)));

// Data modules read the wrong way in the damaged figure
(int R, int C)[] flips = [(10, 11), (12, 14), (14, 9), (15, 13), (11, 19), (19, 10), (21, 13), (13, 21)];
var damaged = (bool[,])v2.Clone();
foreach (var (r, c) in flips)
    damaged[r, c] = !damaged[r, c];

// Every input class, rendered and decoded
var failures = new List<string>();
void Check(string name, bool[,] m, string payload, Func<double, double, double, H> shape, int size, int supersample, Func<double, double, double>? light = null, bool invert = false, bool blur = false, bool expectCorrections = false)
{
    var dim = m.GetLength(0);
    var map = shape(0, 0, size);
    var luminance = Render(m, dim, map, size, size, supersample, light, invert, blur);
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
Check("uneven lighting", v2, Payload, (x, y, s) => Square(25, x, y, s), 33 * 4, 1, light: Shadow(33 * 4, 0.5, 3 * 4));
// The figure says the global threshold falls between lit and shadowed paper, so shadowed paper reads as ink
{
    var lit = Render(v2, 25, Square(25, 0, 0, 33 * 4), 33 * 4, 33 * 4, 1, Shadow(33 * 4, 0.5, 3 * 4), false, false);
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

// ---------- Drawing ----------

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
.rowt{font:600 11px {{Font}};fill:#8250df;paint-order:stroke;stroke:#ffffff;stroke-width:3px;stroke-linejoin:round}
.ink{fill:#1f2328}
.paper{fill:#ffffff}
.det{fill:#0969da}
.detl{stroke:#0969da;fill:none;stroke-width:1.6}
.detd{stroke:#0969da;fill:none;stroke-width:1.4;stroke-dasharray:5 3}
.pred{stroke:#bf8700;fill:none;stroke-width:1.6;stroke-dasharray:4 3}
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
.bk{fill:#dafbe1;stroke:#4ac26b;stroke-width:1.2}.bkt{font:600 12px {{Font}};fill:#1f2328}.bks{font:11px {{Font}};fill:#116329}
.bu{fill:#f6f8fa;stroke:#d0d7de}.but{font:600 12px {{Font}};fill:#1f2328}.bus{font:11px {{Font}};fill:#59636e}
.bc{fill:#ffffff;stroke:#bf8700;stroke-dasharray:4 3}.bct{font:600 12px {{Font}};fill:#7d4e00}.bcs{font:11px {{Font}};fill:#9a6700}
.bs{fill:#ffffff;stroke:#d8dee4;stroke-dasharray:2 3}.bst{font:600 12px {{Font}};fill:#8c959f}.bss{font:11px {{Font}};fill:#afb8c1}
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
    var dim = m.GetLength(0);
    var path = new StringBuilder();
    for (var r = 0; r < dim; r++)
        for (var c = 0; c < dim; c++)
            if (m[r, c])
                path.Append(Poly(map, c, r, c + 1, r + 1));
    return $"<path fill=\"{paper}\" stroke=\"#d0d7de\" d=\"{Poly(map, -2, -2, dim + 2, dim + 2)}\"/><path fill=\"{ink}\" stroke=\"{ink}\" stroke-width=\"0.35\" d=\"{path}\"/>";
}
string Dot((double X, double Y) p, double r = 4, string cls = "det") => $"<circle class=\"{cls}\" cx=\"{F(p.X)}\" cy=\"{F(p.Y)}\" r=\"{F(r)}\"/>";
string Line((double X, double Y) a, (double X, double Y) b, string cls) => $"<line class=\"{cls}\" x1=\"{F(a.X)}\" y1=\"{F(a.Y)}\" x2=\"{F(b.X)}\" y2=\"{F(b.Y)}\"/>";
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

string Card(int width, int height, string title, string subtitle, string defs = "")
    => $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">{Style()}<defs>{defs}<marker id=\"ah\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"7\" markerHeight=\"7\" orient=\"auto-start-reverse\"><path d=\"M0 0L10 5L0 10z\" fill=\"#8c959f\"/></marker></defs>"
        + $"<rect class=\"card\" x=\"0.5\" y=\"0.5\" width=\"{width - 1}\" height=\"{height - 1}\" rx=\"12\"/>" + Text(24, 34, title, "t") + Text(24, 54, subtitle, "st");

// ---------- The stage strip ----------
{
    const int W = 1080, H0 = 356;
    var sb = new StringBuilder();
    sb.Append(Card(W, H0, "Standard QR image decode, stage by stage", "A clean symbol, read in the first pass: the input, then stages of the image-level outline in the spec-to-code map.",
        "<filter id=\"lens\" x=\"-5%\" y=\"-5%\" width=\"110%\" height=\"110%\"><feGaussianBlur stdDeviation=\"0.8\"/></filter><linearGradient id=\"light\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"><stop offset=\"0\" stop-color=\"#ffffff\" stop-opacity=\".35\"/><stop offset=\"1\" stop-color=\"#000000\" stop-opacity=\".22\"/></linearGradient>"));
    const double P = 150, S = 36, Gap = 28, X0 = 24, Y0 = 74;
    string[][] labels =
    [
        ["Luminance", "a grey image comes in"],
        ["Global threshold", "one Otsu split of the", "histogram"],
        ["Finder triple", "1:1:3:1:1 along a line", "through each centre"],
        ["Corner from the triangle", "top-left is opposite the", "longest side"],
        ["Alignment search", "where the frame predicts;", "then the four-point transform"],
        ["Matrix decode", "format, unmask, deinterleave,", "Reed-Solomon, text"],
    ];
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

// ---------- The input figures ----------

// The image-level outline's stages, in its order; the matrix decode every grid goes through is the bar under them
string[] boxes =
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
const int Bar = 12;

void InputFigure(string file, string title, string subtitle, Func<StringBuilder, double, double, double, string[]> picture, (int Box, string Short, string Text)[] notes, string states, string[] tags, string defs = "")
{
    const int W = 1080;
    var body = new StringBuilder();

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
        body.Append(parts[0] switch
        {
            "det" => Dot((lx + 6, lyy - 4), 4),
            "detl" => Line((lx, lyy - 4), (lx + 14, lyy - 4), "detl"),
            "detd" => Line((lx, lyy - 4), (lx + 14, lyy - 4), "detd"),
            "pred" => $"<rect class=\"pred\" x=\"{F(lx + 1)}\" y=\"{F(lyy - 10)}\" width=\"12\" height=\"12\"/>",
            "predr" => $"<circle class=\"predr\" cx=\"{F(lx + 6)}\" cy=\"{F(lyy - 4)}\" r=\"4\"/>",
            "est" => Cross((lx + 6, lyy - 4), 4, "estx"),
            "estl" => Line((lx, lyy - 4), (lx + 14, lyy - 4), "est"),
            "row" => Line((lx, lyy - 4), (lx + 14, lyy - 4), "row"),
            "tick" => Line((lx + 2, lyy - 4), (lx + 12, lyy - 4), "tick"),
            "err" => $"<rect class=\"err\" x=\"{F(lx + 1)}\" y=\"{F(lyy - 10)}\" width=\"12\" height=\"12\"/>",
            "lat" => Line((lx, lyy - 4), (lx + 14, lyy - 4), "lat"),
            "blk" => $"<rect class=\"blk\" x=\"{F(lx + 1)}\" y=\"{F(lyy - 10)}\" width=\"12\" height=\"12\"/>",
            "shade" => $"<rect fill=\"#1f2328\" fill-opacity=\".35\" x=\"{F(lx + 1)}\" y=\"{F(lyy - 10)}\" width=\"12\" height=\"12\"/>",
            _ => "",
        });
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

    const double MX = 384, MY = 88;
    body.Append(Text(MX, MY, "What the decoder does", "h"));
    var y = MY + 28.0;
    for (var i = 0; i < notes.Length; i++)
    {
        body.Append(numbers[i] > 0 ? Marker(MX + 8, y - 4, numbers[i]) : $"<circle cx=\"{F(MX + 8)}\" cy=\"{F(y - 4)}\" r=\"2.2\" fill=\"#59636e\"/>");
        foreach (var line in Wrap(notes[i].Text, 96))
        {
            body.Append(Text(MX + 24, y, line, "sm"));
            y += 17;
        }
        y += 9;
    }

    // The path: two rows of the outline's stages, and the matrix decode under them
    var fy = Math.Max(lyy + 44, y + 20);
    body.Append(Text(24, fy, "Path through the pipeline", "h"));
    body.Append(Text(24, fy + 18, "Every pass runs this path: the global threshold, the inverted image, then the regional binarization (see the shared passes).", "xs"));
    const double BW = 152, BH = 80, BG = 24;
    var r1 = fy + 34; var r2 = r1 + BH + 34;
    (double X, double Y) At(int b) => (24 + (b % 6) * (BW + BG), b < 6 ? r1 : r2);
    (string Box, string T, string S) Classes(char state) => state switch
    {
        'K' => ("bk", "bkt", "bks"),
        'U' => ("bu", "but", "bus"),
        'C' => ("bc", "bct", "bcs"),
        _ => ("bs", "bst", "bss"),
    };
    for (var b = 0; b < boxes.Length; b++)
    {
        var (x, by) = At(b);
        var (box, t, s) = Classes(states[b]);
        body.Append($"<rect class=\"{box}\" x=\"{F(x)}\" y=\"{F(by)}\" width=\"{BW}\" height=\"{BH}\" rx=\"8\"/>");
        var titleLines = Wrap(boxes[b], 21);
        for (var l = 0; l < titleLines.Count; l++)
            body.Append(Text(x + 10, by + 19 + l * 15, titleLines[l], t));
        var ty = by + 19 + titleLines.Count * 15 + 6;
        if (shorts.TryGetValue(b, out var items))
        {
            for (var k = 0; k < items.Count; k++)
            {
                body.Append(Marker(x + 18, ty - 4 + k * 17, items[k].N));
                body.Append(Text(x + 30, ty + k * 17, items[k].Short, s));
            }
        }
        else if (tags[b].Length > 0)
        {
            body.Append(Text(x + 10, ty, tags[b], s));
        }
    }
    for (var b = 0; b < boxes.Length - 1; b++)
    {
        if (b == 5)
            continue;
        var (x, by) = At(b);
        var cls = b >= 8 ? "flowd" : "flow";
        body.Append($"<path class=\"{cls}\" marker-end=\"url(#ah)\" d=\"M{F(x + BW + 2)} {F(by + BH / 2)}H{F(x + BW + BG - 4)}\"/>");
    }
    {
        var (x5, y5) = At(5); var (x6, y6) = At(6);
        var mid = y5 + BH + 17;
        body.Append($"<path class=\"flow\" marker-end=\"url(#ah)\" d=\"M{F(x5 + BW / 2)} {F(y5 + BH + 2)}V{F(mid)}H{F(x6 + BW / 2)}V{F(y6 - 4)}\"/>");
    }
    var barY = r2 + BH + 14;
    {
        var (box, t, s) = Classes(states[Bar]);
        body.Append($"<rect class=\"{box}\" x=\"24\" y=\"{F(barY)}\" width=\"1032\" height=\"32\" rx=\"8\"/>");
        body.Append(Text(34, barY + 21, "Matrix decode", t));
        var bx = 140.0;
        if (shorts.TryGetValue(Bar, out var items))
        {
            foreach (var (n, text) in items)
            {
                body.Append(Marker(bx + 8, barY + 16, n));
                body.Append(Text(bx + 20, barY + 20, text, s));
                bx += 20 + text.Length * 6.2 + 20;
            }
        }
        body.Append(Text(1046, barY + 20, "every grid, then transposed unless that settles it", "xs", "end"));
    }
    var ky = barY + 32 + 26;
    (string Box, string Label)[] key = [("bk", "decides: without it, this input would not read, or only after a grid fails"), ("bu", "runs"), ("bc", "only if needed"), ("bs", "not reached for this input")];
    var kx = 24.0;
    foreach (var (box, label) in key)
    {
        body.Append($"<rect class=\"{box}\" x=\"{F(kx)}\" y=\"{F(ky - 11)}\" width=\"18\" height=\"14\" rx=\"4\"/>");
        body.Append(Text(kx + 26, ky, label, "xs"));
        kx += 26 + label.Length * 6.0 + 26;
    }

    var h = (int)Math.Ceiling(ky + 24);
    var sb = new StringBuilder();
    sb.Append(Card(W, h, title, subtitle, defs));
    sb.Append(body);
    sb.Append("</svg>");
    File.WriteAllText(Path.Combine(outDir, file), sb.ToString());
}

string[] Tags(params (int Box, string Tag)[] tags)
{
    var all = Enumerable.Repeat("", 13).ToArray();
    foreach (var (b, t) in tags)
        all[b] = t;
    return all;
}

// Clean
InputFigure("decode-input-clean.svg",
    "Clean: a screenshot, a render or a flat scan",
    "Upright, crisp and flat. The first grid decodes; nothing past the main path runs.",
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
    [
        (-1, "", "Only two grey levels: one global threshold splits ink from paper, and the stages that need grey levels stay off."),
        (-1, "", "The strided row scan crosses each finder's centre band; the column, the row again and the falling diagonal confirm each candidate."),
        (-1, "", "The vertex opposite the longest side is the top-left finder; the cross product tells the other two apart."),
        (-1, "", "Measured in whole pixels, the module sizes agree at both ends of each finder line, so the finders' frame is the parallelogram of the centres."),
        (-1, "", "The alignment pattern is found where the frame predicts and anchors the fourth corner. The first grid decodes."),
    ],
    "UUUSUUUSUSCSU",
    Tags((0, "global threshold"), (3, "not low density"), (4, "whole pixels"), (5, "parallelogram"), (7, "version 2"), (9, "no grey levels"), (10, "if the grid fails")));

// Rotated or mirrored
InputFigure("decode-input-rotated.svg",
    "Rotated or mirrored",
    "Any angle, either way round. The module sizes and the corner come from the symbol's own lines, not from image rows.",
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
    [
        (-1, "", "A finder reads 1:1:3:1:1 along any line through its centre, so the row scan meets it at any angle."),
        (-1, "", "The longest side of the centres' triangle is still the diagonal, so the corner does not depend on the angle."),
        (4, "finder lines", "Module sizes are measured along the finder lines. An image row cuts a turned ring on the slant and reads it up to √2 too long."),
        (-1, "", "The transform carries the turn into the grid; sampling is the same as upright."),
        (Bar, "transposed", "A mirrored capture has the same finders and a transposed grid: a grid that does not settle is decoded again transposed."),
    ],
    "UUUSKUUSUCCSK",
    Tags((0, "global threshold"), (3, "not low density"), (7, "version 2"), (9, "if the grid fails"), (10, "if the grid fails")));

// Keystone: tilted back about the horizontal axis, so the left finder line runs into the distance
InputFigure("decode-input-keystone.svg",
    "Keystone: the symbol in perspective",
    "A flat symbol tilted away at the bottom. Near modules draw larger than far ones.",
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
        sb.Append(Text(est.X + 9, est.Y + 16, "parallelogram", "estt"));
        sb.Append(Window(map, al, 3, "pred"));
        var pa = map.Map(al.U, al.V);
        sb.Append($"<path class=\"detd\" d=\"M{F(a.X)} {F(a.Y)}L{F(b.X)} {F(b.Y)}L{F(pa.X)} {F(pa.Y)}L{F(c.X)} {F(c.Y)}Z\"/>");
        foreach (var f in new[] { tl, tr, bl })
            sb.Append(Dot(map.Map(f.U, f.V), 5));
        sb.Append(Dot(pa, 3.5));
        return ["det|found centre", "tick|one module, near and far", "pred|search where the frame predicts", "est|parallelogram estimate", "detd|four-point anchors"];
    },
    [
        (4, "near and far", "Along a finder line that runs into the distance, the module size differs at its two ends (here the left line)."),
        (5, "one plane", "One plane fits the three centres and both lines' changes of size: the finders' frame."),
        (6, "frame's prediction", "The frame predicts where the alignment pattern sits and how the grid turns there; the parallelogram of the centres drifts from it as the tilt grows."),
        (8, "anchored", "The three centres and the alignment centre anchor a four-point transform, which samples the grid."),
        (10, "other grids", "If the anchored grid fails, the parallelogram grid is tried; with no alignment pattern found, the parallelogram comes first and the frame after it."),
        (-1, "", "Perspective draws a finder taller or shorter than it is wide, as here: the column cross-check accepts that stretch within a window of its row, and past it only when the rising diagonal reads 1:1:3:1:1 too, which a harder tilt needs."),
        (-1, "", "Whenever the frame foreshortens, each finder line's module count divides by the geometric mean of its two end sizes, the count the frame implies. On a version this small the plain mean snaps to the same size; on a large one it would come up short."),
        (-1, "", "The corner the shape names can then be wrong: each other corner is decoded once its timing patterns read. With more than three candidates, further triples follow, each decoded once, from the first of its corners, the shape's included, whose timing patterns read."),
    ],
    "UUUSKKKSKCCSU",
    Tags((0, "global threshold"), (1, "within its row's window"), (2, "the shape's corner"), (3, "not low density"), (7, "version 2"), (9, "if the grid fails"), (11, "three candidates")));

// Grey edges and wrong modules
InputFigure("decode-input-degraded.svg",
    "Grey edges and wrong modules",
    "Anti-aliased or resampled edges, and a few modules read the wrong way.",
    (sb, x, y, s) =>
    {
        var map = Square(25, x + 20, y + 20, s - 40);
        sb.Append($"<g filter=\"url(#soft)\">{Symbol(map, damaged)}</g>");
        foreach (var (r, c) in flips)
            sb.Append($"<path class=\"err\" d=\"{Poly(map, c - 0.1, r - 0.1, c + 1.1, r + 1.1)}\"/>");
        foreach (var f in new[] { tl, tr, bl })
            sb.Append(Dot(map.Map(f.U, f.V), 4));
        return ["det|centre, sub-pixel", "err|module read wrong"];
    },
    [
        (-1, "", "Grey pixels at the edges give the histogram values between its two levels, which switches the grey-level stages on."),
        (-1, "", "Each finder centre moves to the centroid of its centre square's darkness, a fraction of a pixel, and the module sizes are measured again to sub-pixel edges; here the grid reads without them."),
        (9, "if the grid fails", "If the grid still fails, it is read again by coverage: each module centre interpolated and split halfway between the two levels."),
        (Bar, "Reed-Solomon", "Modules read the wrong way are Reed-Solomon's: each block corrects up to its capacity."),
        (-1, "", "At a lower density, a finder whose runs just miss 1:1:3:1:1 is measured again from the grey levels before it is refused."),
        (-1, "", "Heavy blur or heavy damage is out of scope."),
    ],
    "UUUSUUUSUCCSK",
    Tags((0, "global threshold"), (3, "not low density"), (4, "sub-pixel"), (7, "version 2"), (10, "if the grid fails")),
    "<filter id=\"soft\" x=\"-5%\" y=\"-5%\" width=\"110%\" height=\"110%\"><feGaussianBlur stdDeviation=\"1.5\"/></filter>");

// Uneven lighting
InputFigure("decode-input-lighting.svg",
    "Uneven lighting: a soft-edged shadow",
    "A shadow over part of the symbol: the global threshold falls between lit and shadowed paper, so shadowed paper reads as ink.",
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
        return ["shade|shadow, soft edge", "blk|blocks of the regional threshold"];
    },
    [
        (0, "regional pass", "When no one threshold splits ink from paper, the global passes read nothing and the regional pass runs: each pixel is read against the mean black point of the blocks around its own."),
        (0, "positive first", "It binarizes the positive image first and runs the same path on the result; here that reads, so the negative is never binarized."),
        (-1, "", "The result has only two levels, so the grey-level stages stay off: the centres are not refined and no grid is read by coverage."),
        (-1, "", "Skipped after a verdict on the content from a global pass, for an image too small to hold one neighbourhood of blocks, and for a polarity it would leave as the global threshold did."),
        (-1, "", "A hard-edged shadow is out of scope."),
    ],
    "KUUSUUUSUSCSU",
    Tags((3, "not low density"), (4, "two levels"), (7, "version 2"), (9, "no grey levels"), (10, "if the grid fails")));

// Low density
InputFigure("decode-input-low-density.svg",
    "Low density: near one pixel a module",
    "A crisp render near one pixel per module: each module is one or two pixels wide.",
    (sb, x, y, s) =>
    {
        // The render itself, pixel by pixel
        const int px = 43;
        var raster = Render(v2, 25, Square(25, 0, 0, px), px, px, 1, null, false, false);
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
            var b = map.Map(k, 6.5); var bx = x + 20 + Math.Round(b.X) * cell; var byy = y + 20 + b.Y * cell;
            sb.Append(Line((bx, y + 20 + map.Map(0, 6).Y * cell - 2), (bx, y + 20 + map.Map(0, 7).Y * cell + 2), "tick"));
            var d = map.Map(6.5, k); var dy = y + 20 + Math.Round(d.Y) * cell;
            sb.Append(Line((x + 20 + map.Map(6, 0).X * cell - 2, dy), (x + 20 + map.Map(7, 0).X * cell + 2, dy), "tick"));
        }
        return ["tick|module boundary on a timing pattern", "blk|one pixel"];
    },
    [
        (1, "whole pattern", "A finder's runs come out in whole pixels, a module one or two of them, too coarse for a ratio check. A crisp finder is taken when the row above or below repeats its runs pixel for pixel and all 49 of its modules read, through the edges its row and column measured."),
        (3, "boundaries", "For an upright or right-angle symbol, the module boundaries are read off the two timing patterns, finder to finder, and the finders' own lines, and the grid is sampled between them, at every module wherever its edges fell."),
        (3, "counted dimension", "Both timing lines must read as timing patterns of the same dimension, which names the version."),
        (-1, "", "It is tried first for every corner: when it reads, nothing after it runs."),
    ],
    "UKUKSSSSSSSSU",
    Tags((0, "global threshold"), (4, "not reached"), (5, "not reached"), (6, "not reached"), (7, "not reached"), (8, "not reached"), (9, "not reached"), (10, "not reached"), (11, "not reached")));

// Light on dark
InputFigure("decode-input-light-on-dark.svg",
    "Light on dark",
    "Reflectance reversed: light modules on a dark background, as in a dark-mode screen.",
    (sb, x, y, s) =>
    {
        var map = Square(25, x + 20, y + 20, s - 40);
        sb.Append(Symbol(map, v2, "#ffffff", "#1f2328"));
        foreach (var f in new[] { tl, tr, bl })
            sb.Append(Dot(map.Map(f.U, f.V), 5));
        return ["det|found centre, in the inverted image"];
    },
    [
        (0, "inverted pass", "In the positive image the finders' rings do not read 1:1:3:1:1, so the global positive pass reads nothing."),
        (0, "mirrored histogram", "The inverted pass decodes the image inverted, its histogram mirrored rather than counted again, and runs the same path."),
        (-1, "", "From the finder triple on, it is the clean path."),
    ],
    "KUUSUUUSUSCSU",
    Tags((3, "not low density"), (4, "whole pixels"), (5, "parallelogram"), (7, "version 2"), (9, "no grey levels"), (10, "if the grid fails")));

// Large version: the mesh
InputFigure("decode-input-large-version.svg",
    "Large version: the mesh",
    "Version 14 and up, in perspective: a mesh of local anchors is tried before one transform across the whole symbol.",
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
        foreach (var r in coords)
            foreach (var c in coords)
            {
                var p = map.Map(c + 0.5, r + 0.5);
                sb.Append(r == 6 || c == 6 ? $"<circle class=\"predr\" cx=\"{F(p.X)}\" cy=\"{F(p.Y)}\" r=\"3.5\"/>" : Dot(p, 3.5));
            }
        return ["det|interior node, searched", "predr|edge node, extrapolated", "lat|mesh"];
    },
    [
        (4, "along the lines", "Module sizes are measured along each finder line. In perspective a module is not square: here it is shorter than it is wide, and sizes from image rows would count the left line short."),
        (-1, "", "From version 14 the alignment patterns form a lattice of 4 × 4 positions or more."),
        (-1, "", "The interior nodes are searched around a prediction carried from the finders node by node; missing ones are searched again through a homography anchored on the three finder centres and the farthest node found."),
        (-1, "", "The row and column at coordinate 6, which lie on the timing patterns, are not searched; once the mesh is kept, they are extrapolated from the interior nodes."),
        (-1, "", "The mesh is kept when enough of the searched nodes are found, and each cell is sampled between its four nodes."),
        (8, "if the mesh fails", "The mesh grid is decoded first; one that does not settle falls back to the four-point transform."),
        (Bar, "Reed-Solomon", "Here the mesh grid reads with corrections, which Reed-Solomon absorbs; without them, the four-point transform would read the symbol instead."),
        (-1, "", "That transform needs no corrections here: a flat symbol in perspective is a plane, which one transform can map."),
    ],
    "UUUSKUUUCCCSK",
    Tags((0, "global threshold"), (3, "not low density"), (7, "kept, its grid decodes"), (9, "if the 4-point grid fails"), (10, "if the re-read fails too")));

// Preview: every figure on a light and on a dark page
if (preview)
{
    string[] files = ["decode-overview.svg", "decode-input-clean.svg", "decode-input-rotated.svg", "decode-input-keystone.svg", "decode-input-degraded.svg", "decode-input-lighting.svg", "decode-input-low-density.svg", "decode-input-light-on-dark.svg", "decode-input-large-version.svg"];
    var html = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><title>Decode figures</title><style>body{margin:0;font-family:system-ui,sans-serif}section{padding:24px}section.dark{background:#0d1117;color:#e6edf3}img{display:block;max-width:100%;height:auto;margin:0 0 20px}</style></head><body>");
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

if (failures.Count > 0)
{
    Console.Error.WriteLine($"did not decode: {string.Join(", ", failures)}");
    return 1;
}
Console.WriteLine($"written to {outDir}");
return 0;

// ---------- Geometry and rendering ----------

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

// The same, tilted away at the bottom: the far edge shorter by shrink of the near one
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

// A soft-edged shadow over the lower right: the light falls by depth across an edge of the given width
static Func<double, double, double> Shadow(double size, double depth, double edge)
    => (x, y) =>
    {
        var t = ((x + y) / 2 - size * 0.5) / edge + 0.5;
        t = Math.Clamp(t, 0, 1);
        return 1 - depth * t * t * (3 - 2 * t);
    };

static byte[] Render(bool[,] m, int dim, H map, int width, int height, int supersample, Func<double, double, double>? light, bool invert, bool blur)
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
                    var dark = u >= 0 && v >= 0 && u < dim && v < dim && m[(int)v, (int)u];
                    sum += dark ? Ink : Paper;
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
