# Image output from the core package

## Purpose

The core split took SkiaSharp out of `FeatherQR` so that encoding and decoding need no graphics library. It also left the core with no way to produce an image: a caller with `FeatherQR` alone gets a module matrix and `GetModuleRectangles`, and has to write an image format before seeing a symbol. This plan adds output with no dependency to the core, for all three symbologies: SVG, PNG, text for a terminal and a raw pixel buffer. Styled output (module and finder shapes, gradients, icons, captions) stays in `FeatherQR.SkiaSharp`.

The output was Out of 2.0.0 from 2026-09-06, waiting for a concrete request. On 2026-11-05 the maintainer moved it in: the split exists to take the rendering dependency off the core, so a core that cannot render at all leaves the split half done in the major release that introduces it.

It records where things stand, the design, the decisions the maintainer rules on, and the phases. When it completes, its durable content moves into the specs and this file is deleted.

## Scope

| In | Out |
|---|---|
| SVG, PNG, text for a terminal and a raw pixel buffer from `QRCodeData`, `MicroQRCodeData` and `RmQRCodeData` | BMP. PNG is read wherever BMP is, and is smaller |
| A dark and a light colour, each with alpha, a whole number of pixels per module, and the quiet zone the data carries | PDF and EPS. SVG is the vector output, and print tools convert it |
| | JPEG, WebP, GIF, TIFF and other raster formats. Lossy, or no gain for two colours |
| | ANSI colour escapes in text. The terminal's colours belong to the caller, and inversion covers dark themes (C8) |
| | Module shapes, finder shapes, gradients, icons and captions. `FeatherQR.SkiaSharp` draws them |
| | `Stream` and async output. The core does no I/O (C3) |
| | Compression from the runtime. The PNG writer carries its own deflate (C7) |
| | A data URI or Base64 form. It is one BCL call on the bytes |

## Where things stand

- The three data types hold the matrix with the quiet zone chosen at generation (`QuietZoneSize`: 4 modules for Standard QR and 2 for Micro QR and rMQR by default), an indexer, and `GetModuleRectangles`, whose rectangles are maximal horizontal runs of dark modules in module units, quiet zone included.
- `FeatherQR.SkiaSharp` writes PNG, JPEG, WebP and SVG through Skia at a target size in pixels (512 by default), from a builder or from static calls on each `*ImageBuilder`: `GetPngBytes`, `GetSvgString`, `GetSvgBytes`, `WritePng(IBufferWriter<byte>)`, `WriteSvg(IBufferWriter<byte>)`, `SavePng(Stream)`, `SaveSvg(Stream)` and the async forms.
- Calls that take a data object are static: `QRCodeDecoder.TryDecode(QRCodeData, ...)` in the core, and `QRCodeImageBuilder.GetPngBytes(QRCodeData, int size)` and `SymbolRenderer.Render(...)` in `FeatherQR.SkiaSharp`. The data types' own members are the matrix and its geometry, none with options: the indexer, `GetModuleRectangles`, `GetRawData`, `IsFinderPattern`.
- The core has no `Stream` member and no colour type. `System.Drawing.Color` is not in netstandard2.0: its reference assembly has `RectangleF` and no `KnownColor`. A colour in the core is therefore its own type or plain numbers.
- The runtime's `DeflateStream` calls a native library: in .NET 10.0.14 it is zlib-ng 2.2.5 inside `System.IO.Compression.Native.dll`. Its output is whatever the host's library writes.
- The internal `PixelLayout` (`Gray8`, `Rgba8888`, `Bgra8888`, `Rgb888x`) describes the image decoders' input. The scope decisions already allow promoting it as a visibility change.

## Design

### Shared by every output

- Pixels per module is a whole number. Every module is a sharp square of that many pixels, so PNG stays 1-bit and the output decodes at any density. A target size in pixels, as the SkiaSharp builders take, needs fractional modules or padding, and that stays the renderer's.
- The quiet zone is the data's. The generator option already sets it, and a second setting on the output would be a second place to disagree.
- Colours: a dark and a light colour, black on white by default, each with alpha. Text has no colours.
- Every output is computed from the data alone, and the same data and options give the same bytes on every target (C7).

### Where the calls live (C3)

Decided 2026-11-05: one static class, `SymbolImageWriter`, with seven calls per data type. It is a thin public layer over one internal writer that reads the matrix. The calls take the options as `in SymbolImageOptions options = default` and use the renderer's verbs. `GetSvgBytes` is left out, since `WriteSvg` gives UTF-8 without a string and `Encoding.UTF8.GetBytes` turns the string into bytes. There is no `Stream` or async form.

```csharp
QRCodeData qr = QRCodeGenerator.Create("https://example.com", QREccLevel.M);

string svg = SymbolImageWriter.GetSvgString(qr);
byte[] png = SymbolImageWriter.GetPngBytes(qr, new SymbolImageOptions { ModulePixelSize = 10 });
SymbolImageWriter.WritePng(qr, bufferWriter);
Console.Write(SymbolImageWriter.GetText(qr));

// A Gray8 buffer is the decoders' luminance input.
byte[] gray = new byte[SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Gray8)];
SymbolImageWriter.TryWritePixels(qr, gray, PixelLayout.Gray8, out int width, out int height);
QRCodeDecoder.TryDecodeImage(gray, width, height, out string text, out _);
```

```csharp
public static class SymbolImageWriter
{
    public static string GetSvgString(QRCodeData data, in SymbolImageOptions options = default);
    public static int WriteSvg(QRCodeData data, IBufferWriter<byte> writer, in SymbolImageOptions options = default);
    public static byte[] GetPngBytes(QRCodeData data, in SymbolImageOptions options = default);
    public static int WritePng(QRCodeData data, IBufferWriter<byte> writer, in SymbolImageOptions options = default);
    public static string GetText(QRCodeData data, bool invert = false);
    public static int GetPixelBufferSize(QRCodeData data, PixelLayout layout, in SymbolImageOptions options = default);
    public static bool TryWritePixels(QRCodeData data, Span<byte> destination, PixelLayout layout, out int width, out int height, in SymbolImageOptions options = default);
    // The same seven for MicroQRCodeData and for RmQRCodeData.
}
```

The data types get no instance methods, for three reasons:

- Calls that take a data object are static in this library: `QRCodeDecoder.TryDecode(QRCodeData)`, and the renderer's `QRCodeImageBuilder.GetPngBytes(QRCodeData, size)`. The data types' own members are views of the matrix that take no options.
- The output does not differ by symbology, so one `Symbol*` type serves all three, as `SymbolDecoder` does.
- The class name tells this output from the renderer's. `qr.GetPngBytes()` next to `QRCodeImageBuilder.GetPngBytes(qr)` would be one name for two different images.

The cost is that typing `qr.` does not list the calls, so the README and a line in each data type's summary point to the class. Extension methods were not taken either: they serve types their author cannot change.

### Options and colour (C4)

```csharp
public record struct SymbolImageOptions
{
    public static SymbolImageOptions Default => default;
    public int ModulePixelSize { get; set; }    // 8 when not set (C5)
    public SymbolColor DarkColor { get; set; }  // opaque black when not set
    public SymbolColor LightColor { get; set; } // opaque white when not set
}

public readonly record struct SymbolColor(byte R, byte G, byte B, byte A = 255);
```

```csharp
var options = new SymbolImageOptions
{
    ModulePixelSize = 10,
    DarkColor = new SymbolColor(0x1E, 0x3A, 0x8A),
    LightColor = new SymbolColor(0, 0, 0, 0), // no background
};
string svg = SymbolImageWriter.GetSvgString(qr, options);
byte[] png = SymbolImageWriter.GetPngBytes(qr, options);
```

- The options have the generator options' shape: a mutable `record struct` whose `default` is the documented default through the offset rule `QuietZoneSize` uses. That covers the colours too, since `default(SymbolColor)` is transparent black.
- The colour is a positional `readonly record struct`, as `ModuleRect` is. Alpha is 255 unless given, so an opaque colour is three numbers. `new SymbolColor()` is transparent black, and its docs say so.
- Not taken: a `uint`, where 0x000000 is transparent black and the byte order has to be learned; separate alpha properties; named colours such as `Black` or `Transparent`, which the growth rule leaves out.
- Text takes no options, only `invert` (C8).

### SVG

- The root has a `viewBox` in modules, `width` and `height` in pixels (modules times pixels per module), and `shape-rendering="crispEdges"`. No XML declaration or DOCTYPE, so the same text embeds in HTML.
- The background is one `<rect>` in the light colour, left out when the light colour is fully transparent.
- The dark modules are one `<path>` of strokes one module wide, one stroke along the centre line of each horizontal run of dark modules (C6): `<path stroke="#000" d="M4 4.5h7m2 0h1m1 0h1...">`. A run costs one relative move and one `h`. The first move carries the only `.5`, and every later coordinate is a whole number. The stroke width is the SVG default of 1, and the fill stays the default, which paints nothing on a line, so neither is written.
- A colour is `#RRGGBB`, with an opacity attribute when its alpha is below 255. The dark colour is the path's `stroke`.

### PNG

- Black on white, both opaque: 1-bit greyscale. Any other pair: 1-bit indexed with a two-entry palette, dark first, so the bits are the same in both modes (dark 0, light 1). `tRNS` follows when an alpha is below 255: the dark alpha alone when only the dark colour has one, both otherwise.
- The chunks are `IHDR`, `PLTE` and `tRNS` when needed, `IDAT` and `IEND`. No colour profile or physical size.
- The deflate is our own (C7), in one zlib stream and one deflate block, with row filter None.
- Matches are searched only in the first scanline of each module row: byte runs at distance 1 (at least 4 bytes), and copies of the row above at the scanline's stride (at least 8). The other scanlines of the module row repeat it, so each is one copy at the stride, written without comparing. A copy runs on into the next row's matching prefix.
- A scanline longer than 32,768 bytes is past the reach of a copy, so an image that wide is searched for runs alone, scanline by scanline.
- The writer holds three packed scanlines and allocates nothing beyond its output once warm. Adler-32 of the repeated scanlines is computed in closed form.
- The Huffman codes are dynamic (C7). The counting pass records its symbols, a run of the same copy as one entry, and the writing pass replays them. Past 2^20 entries the writing pass matches the image again instead, which bounds the record at 8 MB. The exact length of the file is known before it is written.

### Text

- Half blocks, two module rows per line: space, `▀`, `▄` and `█`. By default a dark module is a filled glyph, for a light background. Inverted, a light module is, for a dark background. The last line of an odd row count has a light lower half.
- Lines end with `\n`.
- In Phase 0 an iPhone read it from the app's terminal, and Android looked right to the maintainer.

### Pixel buffer

- Written into the caller's span. A sizing call gives its length.
- The layout is `PixelLayout`, the decoders' internal enum made public in the `FeatherQR` namespace with its values numbered (C9):

```csharp
public enum PixelLayout { Gray8 = 0, Rgba8888 = 1, Bgra8888 = 2, Rgb888x = 3 }
```

```csharp
int length = SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Bgra8888, options);
byte[] pixels = new byte[length];
SymbolImageWriter.TryWritePixels(qr, pixels, PixelLayout.Bgra8888, out int width, out int height, options);
// A row is width * 4 bytes, with no padding between rows.
```

- Rows are packed: a row is the width times the bytes a pixel.
- Alpha is straight, not premultiplied. With opaque colours the two are the same.
- `Gray8` writes each colour's BT.601 luminance over white, as the decoders read transparent pixels. `Rgb888x` writes 255 in its padding byte, so with opaque colours it equals `Rgba8888`.
- `TryWritePixels` returns `false` only when the span is short, and then reports a width and height of 0 and writes nothing. An undefined layout, a side past `int.MaxValue` pixels or a buffer past the largest array throws.
- The first pixel row of a module row is written from the runs of the stored bits, and the module's other rows are copies of it.

## Decisions

| # | Decision | Ruling |
|---|---|---|
| C1 | Release | Decided 2026-11-05: 2.0.0, as Phase 6j of the [2.0.0 plan](featherqr-2.0.0-plan.md), with the freeze waiting for it |
| C2 | Formats | Decided 2026-11-05: SVG, PNG, text for a terminal and a raw pixel buffer. The rest are Out under Scope, each with its reason |
| C3 | Where the calls live, and their names | Decided 2026-11-05: one static `SymbolImageWriter` with seven calls per data type, and no new members on the data types (Design, "Where the calls live"). The renderer's PNG (Skia at a target size) and this one (sharp 1-bit at a module size) differ, and the docs of both say so |
| C4 | Options and the colour type | Decided 2026-11-05: `SymbolImageOptions` and `SymbolColor` as sketched under Design, "Options and colour". The options are a mutable `record struct` with the offset rule, as the generator options are. The colour is a positional `readonly record struct` of R, G, B and A, with A 255 unless given, since netstandard2.0 has no `System.Drawing.Color`. `SymbolColor` follows the naming rule for a type shared by the three symbologies |
| C5 | Pixels per module: default and bounds | Decided 2026-11-05: 8 by default: version 1 is 232 pixels square and version 40 1,480. At least 1. No fixed largest: a call whose output would pass the largest array throws `ArgumentOutOfRangeException`. For version 40 with the default quiet zone at four bytes a pixel, that is past 125 pixels a module (125 gives 2,139,062,500 bytes, under the 2,147,483,591 an array holds). Every output also refuses an image side past `int.MaxValue` pixels, which SVG meets first: its size does not grow with the module size, and the largest quiet zone of 10,000 modules brings the side within reach |
| C6 | SVG path | Decided 2026-11-05: the smallest output that stays fast to write. Phase 0 picked strokes along each horizontal run (Design, SVG). They are 35 to 45 % smaller than filled runs at the same time, and smaller than an outline trace everywhere but version 1 (924 against 859 bytes), where the trace is slower and from version 10 up 3 to 4 times slower. Chromium 152 draws them pixel for pixel like filled runs at every size of 1 px a module and over, and Chrome and Safari on iPhone did the same on the maintainer's devices. `fill="none"` (12 bytes) changed no pixel, so it is not written |
| C7 | PNG compression | Decided 2026-11-05: our own deflate, nothing from the runtime. The core takes no dependency beyond its own code, and the PNG writer, deflate included, is small enough to write. `DeflateStream` would also make the bytes the host's, since it calls the host's native library. Ours are the same on every target, and any SIMD path must write the same bytes as the scalar one. Stored blocks without compression are not an option: a version 40 symbol at 8 pixels a module is 275,368 bytes stored. Phase 0 picked filter None and matching on the first scanline of each module row only (Design, PNG). A general hash matcher was 4 to 7 times slower, needed a 32 KB window of the image, and was no smaller at 8 px. Dynamic Huffman codes make the output 2 to 4 times smaller than fixed codes from 2 px a module up, at 1.4 to 1.8 times the time, through a counting pass before the writing pass. That broke the one-pass rule and was clearly slower, so it went to the maintainer, who ruled for dynamic codes on 2026-11-05 |
| C8 | Text | Decided 2026-11-05: half blocks, an `invert` parameter (`false` by default: dark modules filled, for a light background), the data's quiet zone, `\n`. No ANSI escapes and no full-block mode |
| C9 | Pixel layout | Decided 2026-11-05: promote the internal `PixelLayout` with its four values, as sketched under Design, "Pixel buffer". Rows packed, alpha straight. `Gray8` uses the decoders' weights, (77 R + 150 G + 29 B) / 256 over white, so it equals the luminance the decoders compute from the `Rgba8888` output. Dropping `Rgb888x` would need a second enum, since the decoders keep it |
| C10 | The SkiaSharp package | Decided 2026-11-05: unchanged. Its docs point to the core for plain output, and its plain SVG is not routed through the core |

## Phases

Each phase follows the test-first workflow, regenerates `PublicAPI.approved.txt`, updates the affected specs and docs in the same change, and appends a Progress log entry.

| # | Phase | Contents | Exit |
|---|---|---|---|
| 0 | Premises | No change to `src`. In a probe, measure C6's three path forms and C7's encoder shapes for bytes and time, with `DeflateStream` as a size yardstick in the probe only. Check the stroked path in three browsers, read half-block text from a screen with an iPhone and an Android phone in light and dark themes, and confirm C5's bound | C6's form and C7's shape picked, and the browser and phone checks pass |
| 1 | Options, colour and SVG | The option and colour types, the sizing rules and SVG for the three data types | Every version and level of the three symbologies parses back to its own matrix, and a browser shows one of each symbology that a phone reads |
| 2 | PNG | The PNG writer for the three data types. It reads module rows from the packed matrix, and finding the SVG's runs with bit scans over those rows is measured here too | An independent PNG decoder reads every case back to the matrix at several module sizes and colour pairs, and both readers decode it. The `IBufferWriter<byte>` path allocates nothing beyond its output |
| 3 | Pixel buffer | The pixel writer and the public `PixelLayout` | Every layout matches the matrix pixel for pixel. The span path allocates nothing |
| 4 | Text | The text output | The tests pin the glyphs and inversion, and the phone check passes in both themes |
| 5 | Docs and fold | README section on images without SkiaSharp, `docs/`, the API page, the core package's description (it says rendering lives in `FeatherQR.SkiaSharp`), the scope rows in [qrcode-symbologies.md](../specs/qrcode-symbologies.md), then this plan folded into the specs and deleted | The approved API listing carries the calls |

## Progress log

### Phase 0, the SVG path form (2026-11-05)

Done. A probe outside `src` wrote each path form for 20 symbols of each kind at 8 px a module and parsed every path back to its own matrix: 500 documents, no mismatch. Mean bytes, with gzip in parentheses, and the median time to write one document:

| Symbol | Filled runs | Stroked runs | Outline trace | Encode, for scale |
|---|---|---|---|---|
| Standard QR v1-M | 1,483 (351), 1.7 µs | 924 (343), 1.6-2.4 µs | 859 (387), 2.9 µs | 0.8 µs |
| Standard QR v10-M | 9,480 (1,198), 8.9 µs | 5,320 (1,110), 8.2 µs | 6,579 (1,676), 25.7 µs | 3.0 µs |
| Standard QR v40-M | 86,828 (8,459), 113 µs | 47,714 (7,651), 117 µs | 62,530 (12,345), 480 µs | 22.6 µs |
| Micro QR M4-L | 1,061 (304), 0.8 µs | 690 (298), 0.8 µs | 735 (342), 2.0 µs | 0.2 µs |
| rMQR R17x139-M | 7,416 (853), 6.1 µs | 4,165 (797), 6.3 µs | 5,518 (1,276), 19.8 µs | 1.1 µs |

The PNG probe ran on the same machine at the same time, so the times compare forms, not absolute speed. The plain form, one `M x y h w v1 h-w z` per run, was 116,322 bytes at version 40, and the filled runs above already take 25 % off it with relative moves and the fill's implicit close.

The render check drew every form in Chromium 152 at 1, 1.5, 2, 2.37, 3, 4, 5.5 and 8 px a module. Stroked runs matched filled runs at every size, at most 1 grey level apart on anti-aliased edges, and at whole sizes every pixel was pure black or white on its module. `fill="none"` changed no pixel. Below 1 px a module (0.75) the two forms differ, and the stroked one leaves fewer light seams between dark rows.

The maintainer opened the same check in Chrome and in Safari on iPhone, and both reported the stroked form the same as the filled one. Firefox looked right to the maintainer. The half-block text, printed in the app's terminal plain and inverted, read with an iPhone, and Android looked right too.

Lessons.

- A stroked run is a move and a length, so it costs about half a filled run, which needs a move and three lengths. That half is the 35 to 45 %.
- An outline trace writes a corner at nearly every module edge of a QR pattern, so it only wins on the smallest symbol and is 3 to 4 times slower from version 10 up.
- Drawing an SVG into a canvas at a size other than its own `width` and `height` resamples a bitmap. The first render check measured that resampling, and only a check that rewrites `width` and `height` to the target size measures the paths.
- At fractional sizes Chromium leaves grey pixels on edges even with `shape-rendering="crispEdges"`, the same for every form.
- The probe writer reads one module at a time, and a version 40 symbol takes about 5 times its encode to write. Whether finding runs with bit scans over packed rows closes that is Phase 1's to measure.

Benchmarks: no change to `src`.

### Phase 0, the PNG deflate shape (2026-11-05)

Done. A second probe wrote 1-bit greyscale PNGs of the same 20 symbols of each kind at 1, 2, 3, 4, 8 and 10 px a module. It tried filter None and Up, a row matcher and a general hash matcher (3-byte hash, 32 KB window, chains of 64), fixed and dynamic Huffman codes, and the runtime's `ZLibStream` on the same rows as a yardstick only. Every PNG had its chunk CRCs checked, its IDAT inflated back to the scanlines byte for byte, and its pixels decoded by an independent decoder and compared with the matrix: 5,400 PNGs across every combination, and 9,600 for the picked shape at eight pairs of minimum lengths. The picked shape's sweep was run twice and gave the same sizes, and it allocated nothing per call.

Mean bytes at 8 px a module:

| Symbol | Stored | Picked, fixed codes | Picked, dynamic codes | Hash, dynamic | `ZLibStream` Optimal | `ZLibStream` SmallestSize |
|---|---|---|---|---|---|---|
| Standard QR v1-M | 7,028 | 397 | 198 | 226 | 241 | 238 |
| Standard QR v10-M | 34,388 | 2,904 | 856 | 922 | 1,030 | 1,008 |
| Standard QR v40-M | 275,368 | 27,910 | 7,309 | 7,210 | 8,364 | 7,657 |
| Micro QR M4-L | 3,764 | 321 | 167 | 188 | 196 | 194 |
| rMQR R17x139-M | 24,260 | 2,231 | 643 | 642 | 705 | 666 |

Median µs for one PNG at 8 px on an otherwise idle machine. The `ZLibStream` column times only the deflate of rows filtered beforehand:

| Symbol | Picked, fixed codes | Picked, dynamic codes | Hash, dynamic | `ZLibStream` Optimal |
|---|---|---|---|---|
| Standard QR v1-M | 4.9 | 8.8 | 43.3 | 18.8 |
| Standard QR v10-M | 38.6 | 53.7 | 316 | 103 |
| Standard QR v40-M | 492 | 796 | 3,413 | 770 |
| Micro QR M4-L | 3.6 | 6.4 | 29.1 | 12.3 |
| rMQR R17x139-M | 26.7 | 37.2 | 206 | 57.5 |

The probe's code, in lines without blanks or comments: the picked matcher 113, dynamic Huffman 214 (89 of them for the code lengths, with a package-merge fallback past 15 bits), fixed Huffman 43, the bit writer with the length and distance tables 112, CRC-32 and Adler-32 41.

Lessons.

- With dynamic codes at 8 px only the bytes 0x00 and 0xFF occur, so a literal costs about a bit, and a match shorter than about 4 bytes costs more than its literals. Minimum lengths of 4 for runs and 8 for row copies took version 40 from 8,556 to 7,309 bytes.
- Filter Up was larger than None in every combination: 10,794 against 8,545 bytes for version 40 at 8 px with the row matcher and dynamic codes.
- Ending each copy at its scanline's end cost one token per module row, 4 to 8 % from 2 px up. Letting it run into the next row's matching prefix took that back.
- At module sizes that neither divide 8 nor are a multiple of it (3 and 10 here), the module edges fall at a different bit in each byte, and the hash matcher beats the picked shape by about 22 % at 10 px (version 40: 11,216 against 14,560 bytes). The default 8 and the sizes 1, 2 and 4 are not affected. Phase 2 can try copy distances that follow the bit phase.
- The hash matcher's chains always reached their limit of 64, since 1-bit rows have few distinct byte triples. `ZLibStream` at SmallestSize took 32 ms for version 40 at 8 px, 40 times its Optimal level.
- The probe's bit writer costs about 9 ns a literal, and it packs rows a bit at a time, so the times above are the probe's, not the shape's. Phase 2 measures the real writer.

Benchmarks: no change to `src`.

### Phase 0 complete (2026-11-05)

Done. The maintainer ruled C7's last point for dynamic Huffman codes, so every decision from C1 to C10 is ruled. The exit holds: C6's form and C7's shape are picked, and the browser and phone checks passed. Phase 1 starts from the stroked-run SVG and the decided option, colour and writer shapes.

Benchmarks: no change to `src`.

### Phase 1, options, colour and SVG (2026-11-05)

Done. `SymbolImageOptions`, `SymbolColor` and `SymbolImageWriter` with `GetSvgString` and `WriteSvg(IBufferWriter<byte>)` for the three data types, in the shapes C3 and C4 decided. The writer strokes the runs of `ModuleRunEnumerator`, the scan `GetModuleRectangles` and the renderer already share. The approved listing grows from 43 to 46 exported types and is the same on all three targets.

The tests:

- Every version and level of the three symbologies (160 Standard QR, 8 Micro QR, 64 rMQR), and quiet zones of 0, 1 and 25, read back to their own matrix through a path reader of the test's own.
- The frame and the first two rows of a Micro QR M1 are pinned as text worked by hand.
- Colours come out in short and long hex, a transparent colour leaves its element out, and the opacity of every alpha from 1 to 254 scales back to its byte.
- `WriteSvg` appends the UTF-8 of the string and returns its length. Null arguments throw, and so does an image side past `int.MaxValue` pixels.
- In Release, `WriteSvg` allocates nothing into a writer with room, and `GetSvgString` allocates the string alone.
- The type-shape rules that named "the three generator options" now name the option structs and cover `SymbolImageOptions`, and the assignment test has its table.

The full suite in Release: 31,307 passed, 401 skipped (the ARM64 tiers on an x64 machine), none failed.

Chromium 152 drew one symbol of each symbology from the writer, and a navy one on a transparent ground, at 1, 2, 4 and 8 px a module, and the core's own image decoder read all 16 captures. The maintainer then read every symbol on the same page with a phone from a screen, so the exit holds and Phase 1 is complete.

Benchmarks, from the new `SymbolSvgWrite`:

| Case | Mean | Allocated |
|---|---|---|
| Standard QR v1-2, string | 1.10 µs | 1,912 B |
| Standard QR v40, string | 76.1 µs | 95,949 B |
| Standard QR v40, writer | 60.6 µs | 0 B |
| Micro QR M4, string | 0.72 µs | 1,376 B |
| rMQR R17x139, string | 5.04 µs | 8,256 B |

The run scan alone, through `TryGetModuleRectangles`, took 29.5 µs of the 60.6 µs at version 40, so the scan and the text are about half each. No existing hot path changed.

Lessons.

- The type-shape tests listed the settable structs as "the three generator options", so a fourth was refused until it was added on purpose, which is what the list is for. The rules now speak of option structs.
- A version 40 string is about 48,000 characters and lands on the large object heap. The writer form does not allocate it.
- Two local traps, neither in CI: the listing tool's shell glob picked up a stale `net8.0` build and refused to write the listing, so the three targets were passed by name. Copies of the benchmark project under `output/` stopped the out-of-process benchmark toolchain, so the numbers above were taken in-process.

### Phase 2, PNG (2026-11-05)

Done. `GetPngBytes` and `WritePng(IBufferWriter<byte>)` for the three data types, in the shape Phase 0 picked: 1-bit, filter None, one deflate block with dynamic Huffman codes, and matches searched in each module row's first scanline only. Scanlines are packed from the stored module bits, which the three matrix views now expose, with run lengths found a 64-bit window at a time. The approved listing gains the six calls.

The tests:

- Every version and level of the three symbologies at the default size, and module sizes 1, 2, 3, 5, 7, 9, 13 and 33 at quiet zones of 0, 1 and 4. Each file's chunk CRCs are checked with the test's own CRC, the inflated stream equals scanlines the test builds, and SkiaSharp's decode equals the matrix.
- Greyscale for black on white, a palette with the dark colour first otherwise, and `tRNS` with both alphas or the dark one alone.
- Scanlines of 32,768, 32,769 and 33,001 bytes, from a matrix the test builds, either side of a copy's reach.
- An image with more symbols than the record holds, matched again by the writing pass. A fault planted in that path failed this test alone.
- The core's decoder reads each symbology, and ZXing reads Standard QR at 1, 3 and 8 px.
- The code-length builder gives complete codes within 15 and 7 bits, Huffman-optimal where the limit does not bind. Under Fibonacci counts it holds the limit, and no more frequent symbol gets a longer code.
- The netstandard2.0 and netstandard2.1 builds write this host's SVG and PNG byte for byte.
- Two files are pinned by SHA-256. In Release, `WritePng` allocates nothing into a writer with room, and `GetPngBytes` the array alone.

The full suite in Release: 31,579 passed, 401 skipped, none failed.

Benchmarks, from `SymbolImageWrite` (in-process, the first version against the shipped one):

| Case | First version | Shipped | Allocated |
|---|---|---|---|
| Standard QR v1-2, 8 px | 7.87 µs | 5.35 µs | 224 B |
| Standard QR v40, 8 px | 700 µs | 272 µs | 7,339 B |
| Standard QR v40, 8 px, writer | 702 µs | 272 µs | 0 B |
| Standard QR v40, 3 px | 323 µs | 211 µs | 7,409 B |
| Micro QR M4, 8 px | 6.06 µs | 4.32 µs | 192 B |
| rMQR R17x139, 8 px | 30.9 µs | 19.7 µs | 672 B |

The SVG cases did not move (version 40 into a writer, 68.1 µs). No existing hot path changed.

Lessons.

- The first version spent most of its time at byte positions the matcher refuses: two prefix compares at nearly every byte of a first scanline. One compare of 4 or 8 bytes first, as long as the shortest run or copy taken, rules those positions out with no change to the output, and took version 40 from about 730 to about 400 µs in the probe.
- Matching a second time cost about as much as recording the symbols does. The counting pass now records them and the writing pass replays them, and with literals appended without a look at the entry before, version 40 came to about 255 µs in the probe and version 1 from 8 to 5.5 µs.
- Finding the SVG's runs with bit scans over the packed rows took the same time as the shared module-at-a-time enumerator, 66 against 64 to 70 µs into a writer at version 40: the text, not the scan, is the SVG's cost. The SVG keeps the shared enumerator, and with it the geometry `GetModuleRectangles` reports.
- The SIMD layout rule matches calls on a class named `Crc32` as the Arm intrinsic, so the CRC class is `PngCrc`.
- A quick probe with tiered compilation off timed the writers 2.5 to 3 times slower than the benchmark. With tiering on and a long warm-up it agreed.

### Phase 3, the pixel buffer (2026-11-05)

Done. `GetPixelBufferSize` and `TryWritePixels` for the three data types, and `PixelLayout` moved from the decoders' internals to the `FeatherQR` namespace with its four values numbered and its documentation free of library names. The decoders and the SkiaSharp adapter read the same enum, and the conversion kernel stays internal. The approved listing gains the enum and the six calls, and the two scope rows in [qrcode-symbologies.md](../specs/qrcode-symbologies.md) that called the enum internal now say it is public.

The tests:

- Every version and level of the three symbologies matches the matrix pixel for pixel: Standard QR at 2 px in RGBA, Micro QR at 8 px in all four layouts, and rMQR at 3 px in BGRA.
- Every layout at 1, 3 and 8 px, with quiet zones of 0 and 4 and both colours below full alpha, matches each layout's byte order written out in the test.
- The reported size, width and height are right, and bytes past the image stay as they were.
- `Gray8` equals what the decoders' luminance kernel computes from the `Rgba8888` buffer for 64 random colour pairs, alphas included, and the three decoders read their symbol from it.
- A short span returns `false`, reports no size and writes nothing. An undefined layout, a null symbol and a buffer past the largest array throw (version 40 in RGBA fits at 125 px, not at 126).
- The netstandard2.0 and netstandard2.1 builds write this host's pixels byte for byte in all four layouts.
- In Release, `TryWritePixels` and `GetPixelBufferSize` allocate nothing.

The full suite in Release: 31,821 passed, 401 skipped, none failed.

Benchmarks, from `SymbolImageWrite` (in-process), each into a buffer kept across calls:

| Case | Mean | Allocated |
|---|---|---|
| Standard QR v1-2, 8 px, RGBA | 6.05 µs | 0 B |
| Standard QR v40, 8 px, RGBA (8.8 MB) | 335 µs | 0 B |
| Standard QR v40, 8 px, Gray8 (2.2 MB) | 136 µs | 0 B |

No existing hot path changed.

Lessons.

- Moving the enum up to the `FeatherQR` namespace needed no change at any use: the code that named it sits in namespaces below `FeatherQR`, where the parent's types are in scope.
- A pixel buffer is mostly copies: each module row's first pixel row is written from the runs of the stored bits and copied for the rest of the module's height, so the time grows with the buffer: 335 µs for 8.8 MB in RGBA, 136 µs for 2.2 MB in Gray8.
