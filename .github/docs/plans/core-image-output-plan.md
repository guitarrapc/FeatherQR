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
| | A data URI or Base64 form. It is one BCL call on the bytes |

## Where things stand

- The three data types hold the matrix with the quiet zone chosen at generation (`QuietZoneSize`: 4 modules for Standard QR and 2 for Micro QR and rMQR by default), an indexer, and `GetModuleRectangles`, whose rectangles are maximal horizontal runs of dark modules in module units, quiet zone included.
- `FeatherQR.SkiaSharp` writes PNG, JPEG, WebP and SVG through Skia at a target size in pixels (512 by default), from a builder or from static calls on each `*ImageBuilder`: `GetPngBytes`, `GetSvgString`, `GetSvgBytes`, `WritePng(IBufferWriter<byte>)`, `WriteSvg(IBufferWriter<byte>)`, `SavePng(Stream)`, `SaveSvg(Stream)` and the async forms.
- The core has no `Stream` member and no colour type. `System.Drawing.Color` is not in netstandard2.0: its reference assembly has `RectangleF` and no `KnownColor`. A colour in the core is therefore its own type or plain numbers.
- `DeflateStream` is in all three targets. `ZLibStream` is in net10.0 only.
- The internal `PixelLayout` (`Gray8`, `Rgba8888`, `Bgra8888`, `Rgb888x`) describes the image decoders' input. The scope decisions already allow promoting it as a visibility change.

## Design

### Shared by every output

- Pixels per module is a whole number. Every module is a sharp square of that many pixels, so PNG stays 1-bit and the output decodes at any density. A target size in pixels, as the SkiaSharp builders take, needs fractional modules or padding, and that stays the renderer's.
- The quiet zone is the data's. The generator option already sets it, and a second setting on the output would be a second place to disagree.
- Colours: a dark and a light colour, black on white by default, each with alpha. Text has no colours.
- Every output is computed from the data alone, and the same data and options give the same bytes on every target (C7).

### SVG

- The root has a `viewBox` in modules, `width` and `height` in pixels (modules times pixels per module), and `shape-rendering="crispEdges"`. No XML declaration or DOCTYPE, so the same text embeds in HTML.
- The background is one `<rect>` in the light colour, left out when the light colour is fully transparent.
- The dark modules are one `<path>`. It is built from `GetModuleRectangles` unless Phase 0 finds an outline trace worth its code (C6).
- A colour is `#RRGGBB`, with `fill-opacity` when its alpha is below 255.

### PNG

- Black on white, both opaque: 1-bit greyscale. Any other pair: 1-bit indexed with a two-entry palette, plus `tRNS` when a colour's alpha is below 255.
- The chunks are `IHDR`, `PLTE` and `tRNS` when needed, `IDAT` and `IEND`. No colour profile or physical size.
- Compression is C7.

### Text

- Half blocks, two module rows per line: space, `▀`, `▄` and `█`. By default a dark module is a filled glyph, for a light background. Inverted, a light module is, for a dark background. The last line of an odd row count has a light lower half.
- Lines end with `\n`.
- Whether phones read it from a screen is Phase 0's, in both themes.

### Pixel buffer

- Written into the caller's span. A sizing call gives its length, and the layout is C9.

## Open decisions

| # | Decision | Recommendation |
|---|---|---|
| C1 | Release | Decided 2026-11-05: 2.0.0, as Phase 6j of the [2.0.0 plan](featherqr-2.0.0-plan.md), with the freeze waiting for it |
| C2 | Formats | Decided 2026-11-05: SVG, PNG, text for a terminal and a raw pixel buffer. The rest are Out under Scope, each with its reason |
| C3 | Where the calls live, and their names | Instance methods on the three data types, with the renderer's verbs: `GetSvgString`, `GetSvgBytes`, `WriteSvg(IBufferWriter<byte>)`, `GetPngBytes`, `WritePng(IBufferWriter<byte>)`, `GetText`, `GetPixelBufferSize` and `TryWritePixels(Span<byte>, ...)`. `GetModuleRectangles` and `GetRawData` are instance methods already, so the data's member list is where a caller looks, and no class is added. No `Stream` or async form, since the core does no I/O and `stream.Write(GetPngBytes())` is one line. The alternative is one static class (`SymbolImageWriter`) with an overload per data type, the shape of the renderer's static calls. Either way `QRCodeImageBuilder.GetPngBytes(data)` and `data.GetPngBytes()` differ (Skia at a target size against sharp 1-bit at a module size), and the docs of both say so |
| C4 | Options and the colour type | One `SymbolImageOptions` record struct (`ModulePixelSize`, `DarkColor`, `LightColor`) for SVG, PNG and the pixel buffer, passed as `in SymbolImageOptions options = default` as the generators take theirs, with `default` meaning black on white at the default size through the offset rule `QuietZoneSize` uses. Text takes only its inversion. The colour is a new `readonly record struct` of R, G, B and A, since netstandard2.0 has no `System.Drawing.Color`, and a `uint` hides alpha (0x000000 is transparent black). Its name is part of this decision |
| C5 | Pixels per module: default and bounds | 8 by default: version 1 is 232 pixels square and version 40 1,480. At least 1. Phase 0 sets the largest from the largest buffer an `int` length holds at four bytes a pixel, and anything larger is `ArgumentOutOfRangeException` |
| C6 | SVG path | Decided after Phase 0: the merged rectangles, unless an outline trace shrinks typical symbols enough to pay for its code. Phase 0 measures both on versions 1, 10 and 40 and on Micro QR and rMQR |
| C7 | PNG compression | Decided after Phase 0: `DeflateStream` behind a written zlib header and Adler-32, unless it fails one of three checks: the same bytes from every target framework, support on browser WebAssembly, and its NativeAOT size cost. If it fails one, a small deflate of our own. Stored blocks without compression are not an option: a version 40 symbol at 8 pixels a module is about 275 KB stored (1,480 rows of 186 bytes) |
| C8 | Text | Half blocks, an `invert` parameter (`false` by default: dark modules filled, for a light background), the data's quiet zone, `\n`. No ANSI escapes and no full-block mode |
| C9 | Pixel layout | Promote the internal `PixelLayout` with its four values, which also opens it to a public luminance input later. `Gray8` writes each colour's BT.601 luminance over white, as the decoders read transparent pixels. `Rgb888x` writes 255 in its padding byte |
| C10 | The SkiaSharp package | Unchanged. Its docs point to the core for plain output, and its plain SVG is not routed through the core |

## Phases

Each phase follows the test-first workflow, regenerates `PublicAPI.approved.txt`, updates the affected specs and docs in the same change, and appends a Progress log entry.

| # | Phase | Contents | Exit |
|---|---|---|---|
| 0 | Premises | No change to `src`. Measure C6 and C7, read half-block text from a screen with an iPhone and an Android phone in light and dark themes, and confirm C5's bound | C4 to C9 ruled |
| 1 | Options, colour and SVG | The option and colour types, the sizing rules and SVG for the three data types | Every version and level of the three symbologies parses back to its own matrix, and a browser shows one of each symbology that a phone reads |
| 2 | PNG | The PNG writer for the three data types | An independent PNG decoder reads every case back to the matrix at several module sizes and colour pairs, and both readers decode it. The `IBufferWriter<byte>` path allocates nothing beyond its output |
| 3 | Pixel buffer | The pixel writer and the public `PixelLayout` | Every layout matches the matrix pixel for pixel. The span path allocates nothing |
| 4 | Text | The text output | The tests pin the glyphs and inversion, and the phone check passes in both themes |
| 5 | Docs and fold | README section on images without SkiaSharp, `docs/`, the API page, the scope rows in [qrcode-symbologies.md](../specs/qrcode-symbologies.md), then this plan folded into the specs and deleted | The approved API listing carries the calls |

## Progress log
