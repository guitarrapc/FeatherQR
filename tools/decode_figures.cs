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
// unless it decodes. The figures carry pictures and labels only; their text is in
// .github/docs/specs/standardqr-decoder.md, whose numbered notes match the numbers on the boxes.
// A box is green when, without its stage, the input would not read, or would read only after a grid
// fails. The boxes follow the image-level outline in .github/docs/specs/standardqr-spec-map.md.

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

// The picture on the left, the path through the outline's stages on the right. marks lists the boxes
// that carry the design record's numbered notes, in the notes' order; Bar is the matrix decode.
void InputFigure(string file, Func<StringBuilder, double, double, double, string[]> picture, int[] marks, string states, string defs = "")
{
    const double PX = 24, PY = 20, PS = 260;
    const double BW = 132, BH = 62, BG = 20, RG = 30;
    const double FX = PX + PS + 32, FW = 4 * BW + 3 * BG;
    var body = new StringBuilder();

    body.Append($"<rect class=\"box\" x=\"{PX}\" y=\"{PY}\" width=\"{PS}\" height=\"{PS}\" rx=\"8\"/>");
    var legend = picture(body, PX, PY, PS);
    var lx = PX; var ly = PY + PS + 24;
    foreach (var item in legend)
    {
        var parts = item.Split('|');
        var width = 20 + parts[1].Length * 6.0 + 18;
        if (lx + width > PX + PS + 10)
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
            "predr" => $"<circle class=\"predr\" cx=\"{F(lx + 6)}\" cy=\"{F(ly - 4)}\" r=\"4\"/>",
            "est" => Cross((lx + 6, ly - 4), 4, "estx"),
            "estl" => Line((lx, ly - 4), (lx + 14, ly - 4), "est"),
            "row" => Line((lx, ly - 4), (lx + 14, ly - 4), "row"),
            "tick" => Line((lx + 2, ly - 4), (lx + 12, ly - 4), "tick"),
            "err" => $"<rect class=\"err\" x=\"{F(lx + 1)}\" y=\"{F(ly - 10)}\" width=\"12\" height=\"12\"/>",
            "lat" => Line((lx, ly - 4), (lx + 14, ly - 4), "lat"),
            "blk" => $"<rect class=\"blk\" x=\"{F(lx + 1)}\" y=\"{F(ly - 10)}\" width=\"12\" height=\"12\"/>",
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

    // Three rows of four stages, then the matrix decode every grid goes through
    (double X, double Y) At(int b) => (FX + (b % 4) * (BW + BG), PY + (b / 4) * (BH + RG));
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
        var cls = b >= 8 ? "flowd" : "flow";
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
    var barY = PY + 3 * BH + 2 * RG + 14;
    {
        var (box, t) = Classes(states[Bar]);
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
    File.WriteAllText(Path.Combine(outDir, file), sb.ToString());
}

// Clean
InputFigure("decode-input-clean.svg",
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
    "UUUSUUUSUSCSU");

// Rotated or mirrored
InputFigure("decode-input-rotated.svg",
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
    "UUUSKUUSUCCSK");

// Keystone: tilted back about the horizontal axis, so the left finder line runs into the distance
InputFigure("decode-input-keystone.svg",
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
    "UUUSKKKSKCCSU");

// Grey edges and wrong modules
InputFigure("decode-input-degraded.svg",
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
    "UUUSUUUSUCCSK",
    "<filter id=\"soft\" x=\"-5%\" y=\"-5%\" width=\"110%\" height=\"110%\"><feGaussianBlur stdDeviation=\"1.2\"/></filter>");

// Uneven lighting
InputFigure("decode-input-lighting.svg",
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
    "KUUSUUUSUSCSU");

// Low density
InputFigure("decode-input-low-density.svg",
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
            var b = map.Map(k, 6.5); var bx = x + 20 + Math.Round(b.X) * cell;
            sb.Append(Line((bx, y + 20 + map.Map(0, 6).Y * cell - 2), (bx, y + 20 + map.Map(0, 7).Y * cell + 2), "tick"));
            var d = map.Map(6.5, k); var dy = y + 20 + Math.Round(d.Y) * cell;
            sb.Append(Line((x + 20 + map.Map(6, 0).X * cell - 2, dy), (x + 20 + map.Map(7, 0).X * cell + 2, dy), "tick"));
        }
        return ["tick|module boundary", "blk|pixel"];
    },
    [1, 3, 3],
    "UKUKSSSSSSSSU");

// Light on dark
InputFigure("decode-input-light-on-dark.svg",
    (sb, x, y, s) =>
    {
        var map = Square(25, x + 20, y + 20, s - 40);
        sb.Append(Symbol(map, v2, "#ffffff", "#1f2328"));
        foreach (var f in new[] { tl, tr, bl })
            sb.Append(Dot(map.Map(f.U, f.V), 5));
        return ["det|found centre"];
    },
    [0, 0],
    "KUUSUUUSUSCSU");

// Large version: the mesh
InputFigure("decode-input-large-version.svg",
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
                sb.Append(r == 6 || c == 6 ? $"<circle class=\"predr\" cx=\"{F(p.X)}\" cy=\"{F(p.Y)}\" r=\"3\"/>" : Dot(p, 3));
            }
        return ["det|searched node", "predr|extrapolated node", "lat|mesh"];
    },
    [4, 8, Bar],
    "UUUSKUUUCCCSK");

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
