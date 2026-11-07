# Core image output: a set size, pixel input, styling and a PNG reader

## Purpose

Since 2.0.0 the core writes SVG, PNG, raw pixels and terminal text with no other package ([Image output](../specs/qrcode-symbologies.md#image-output)). On 2026-11-06 the maintainer asked what the core still cannot do that `FeatherQR.SkiaSharp` can, and which of those gaps can close. Five came out of the comparison, and this plan sizes each, gives it a design, and names the decisions the maintainer rules on:

1. An image of a set size in pixels. The core writes the symbol at a whole number of pixels a module and nothing else, so a 512 by 512 PNG is out of reach.
2. Reading pixels that are not luminance. The decoders take a `Gray8` luminance span alone, and the conversion from RGBA is internal, so a caller with pixels from a browser canvas or an operating system's image API has to convert them by hand, and gets the white compositing wrong unless they read the documentation.
3. Reading a PNG file. The core cannot read the PNG it writes, nor any other.
4. Module and finder pattern shapes. Circles, rounded squares and smaller modules, with the finder patterns drawn as their own shapes.
5. An icon over the centre of a Standard QR code.

The scope row "Image output in the core with no dependency" says styling and a set image size stay in `FeatherQR.SkiaSharp`, and names "a request for styling without SkiaSharp" as the reason to revisit. The maintainer's question is that request, so the row changes with this plan whatever the rulings are, to say what was decided and why.

When the plan completes, its durable content moves into [qrcode-symbologies.md](../specs/qrcode-symbologies.md) (Image output, the package seam table, the scope rows, lessons) and this file is deleted.

## Scope

| In | Out |
|---|---|
| A set width and height on `SymbolImageOptions`, honoured by SVG, PNG and the pixel buffer, with the symbol filling the canvas as the rendering package's `WithSize` fills it | Anti-aliased module edges at a set size. Each module stays whole pixels, snapped at pixel centres, so the PNG stays 1-bit and adjacent modules differ by at most one pixel. Padding only where the canvas and the symbol differ in aspect |
| The luminance conversion as public API, in the shape it has | New decode overloads that take pixels and a layout. One conversion call serves them all, and the overloads can be added on request |
| Module shapes, module scale and finder pattern shapes, as values on the options, in every format together (D5) | Gradients. A gradient needs a truecolour raster, and the SVG form alone would break the rule that every format writes the same image |
| A cleared icon area on Standard QR, sized in modules, with its rectangle reported, and an image embedded in the SVG (D7) | Compositing an icon into the PNG or the pixel buffer. The core would need the icon's pixels and a truecolour PNG. The caller has the rectangle and the pixel buffer, and can composite in whatever decodes their icon |
| A PNG reader, if the maintainer takes it (D3) | JPEG, WebP and HEIC readers. Photographs come in those, and each is a codec of its own |
| | Captions and text beside an icon. The rendering package draws them with a font |
| | ANSI colour or shapes in the terminal text |

## Where things stand

- `SymbolImageWriter` writes the three data types through `SymbolImageOptions` (`ModulePixelSize`, 8 by default through a stored offset, `DarkColor`, `LightColor`). The writers are `SvgWriter`, `PngEncoder` with `PngWriter`, `PixelWriter` and `HalfBlockText` under `Internals/ImageWriters`, 2,135 lines over 12 files, of which the PNG's encoder, deflate and Huffman code lengths are 1,000. The PNG is 1-bit, greyscale for black on white and a two-colour palette otherwise, one deflate block with dynamic codes. Its matcher searches runs and copies of the scanline above at 1, 2, 4 and 8 pixels a module, and looks positions up by their bytes at the other sizes (2026-11-07).
- The rendering package sizes an image three ways: `WithModulePixelSize` alone gives the symbol's own size, `WithSize` alone fits the symbol into the canvas at a fractional module size, and both together centre the symbol at the given module size on whole pixels, `(canvas - content) / 2` from the left and top, and paint the rest in the clear colour when one is set and the background colour otherwise (`QRImageLayout.CreateLayout`). A canvas smaller than the content with a pinned module size throws `InvalidOperationException`. With `WithSize` alone the modules are drawn without anti-aliasing at fractional coordinates, so each pixel belongs to the module its centre falls in, and adjacent modules differ by at most one pixel. A console probe on 2026-11-07 computed that rule from the fit and compared it with the builder's `WithSize` pixels on the five symbols above at eight canvases (512, 300, 1,000, 777, 200 and 64 square, 600 by 400 and 1,024 by 160): 0 of the pixels differed. The builder's images decoded with this library's decoder except where a module fell under one pixel (version 40 at 1,024 by 160 and at 64 by 64, rMQR R17x139 at 64 by 64).
- At 8 pixels a module the two packages write the same pixels. A console probe on 2026-11-06 decoded the core's PNG and the builder's for Standard QR versions 1, 10 and 40, Micro QR M4 and rMQR R17x139 and compared every pixel. The core's PNG was 3.5 to 5.2 times smaller and its SVG 5 to 8 times smaller, and the core wrote the PNG about 140 to 220 times and the SVG 400 to 600 times faster, the median after warming, with the builder's construction and canvas inside its figure.
- The decoders read one luminance form, `TryDecodeImage(ReadOnlySpan<byte> luminance, int width, int height, ...)`, with its bytes and several-symbols twins, 20 members over the three decoders and `SymbolDecoder`. `LuminanceConverter.Convert(pixels, width, height, rowBytes, PixelLayout layout, bool premultipliedAlpha, luminance)` is internal and single-entry, with AVX2, NEON, 128-bit and scalar tiers that write identical bytes, and the rendering package reaches it through `InternalsVisibleTo`. The package seam table and the scope row "Luminance conversion API" both say its promotion is a visibility change that waits for a concrete request. `PixelLayout` is already public, as the layout of `TryWritePixels`.
- The rendering package's styling is three `ModuleShape` classes (square, circle, rounded square, each with `RequiresAntialiasing`), a module scale from 0 to 1, four `FinderPatternShape` classes (square as five rectangles, circle as a stroked oval and a centre oval, two rounded forms as even-odd paths), and `IconData` sized in modules (`IconSizeModules`, `IconBorderModules`, `MaxCoreOccupancyPercent`, snapped to the grid) or as a percentage of the side with a border in pixels. A module shape never reaches a finder pattern, which is always drawn as one continuous shape, because a reader finds a symbol by the 1:1:3:1:1 run and a gap in the finder's centre splits it ([Data model direction](../specs/qrcode-symbologies.md#data-model-direction), styling). Setting `IconSizeModules` switches the icon's sizing to modules and the percent and pixel members stop applying, which is this library's precedent for one option member switching another off.
- The styled decodability tests in the rendering package render each shape at each scale on each symbology and decode the result with this library's decoder, and a finder-shape argument is held by a with-and-without byte comparison, since a dropped argument falls back to the plain square and still decodes.
- The core has no rasterizer. The pixel and PNG writers expand each module row's first scanline from the packed module bits and copy it for the module's other rows, which is what keeps the PNG at one bit and its matcher cheap.

## Design

### A set size (item 1)

Two members on `SymbolImageOptions`, `Width` and `Height` in pixels, 0 by default, which means the symbol's own size. When a size is given, the symbol fills the canvas as the rendering package's `WithSize` fills it: one uniform scale, the smaller of the two the sides allow, the symbol with its quiet zone centred on whole pixels, and each pixel belonging to the module its centre falls in. So adjacent modules differ by at most one pixel, every module is still whole pixels, the PNG stays 1-bit, and a square symbology in a square canvas has no padding at all. Padding, in the light colour, appears only where the canvas and the symbol differ in aspect, as it does in the rendering package. `ModulePixelSize` then stops applying, as the percent members of `IconData` stop applying once `IconSizeModules` is set. One side of 0 with the other given follows the symbol's aspect, which is what an rMQR caller with a width in mind wants, and a square symbology gives a square. A canvas that gives any module under one pixel throws `ArgumentOutOfRangeException` on the options, since modules then vanish and nothing reads the result, where the rendering package draws it anyway.

```csharp
byte[] png = SymbolImageWriter.GetPngBytes(qr, new SymbolImageOptions { Width = 512, Height = 512 });
string svg = SymbolImageWriter.GetSvgString(rmqr, new SymbolImageOptions { Width = 1024 });
```

```text
public int Height { get; set; }
public int Width { get; set; }
```

At 512 by 512 with the default quiet zone, version 1 (29 modules) gets modules of 17 and 18 pixels, version 10 (65) of 7 and 8, and version 40 (185) of 2 and 3, and each fills the canvas. The first design of this plan kept every module at one size and padded the rest, which at version 40 left 370 pixels of symbol in a 512 canvas, and the maintainer ruled against it on 2026-11-07: a caller who asks for 512 pixels and gets a smaller symbol with a margin is let down, and the gap the plan set out to close stays open. The rule above is the rendering package's, measured: the pixel-centre rule computed from the fit matched the builder's `WithSize` output pixel for pixel on the five symbols at eight canvases (Where things stand), so the core at a set size writes the image the builder writes by default today, and a parity test holds it. Whether modules of two and three pixels read was measured with it: every builder image whose modules were one pixel or more decoded.

Each format carries the canvas as follows. The SVG root's `width` and `height` are the canvas, and where the aspect differs the `viewBox` and the background rectangle grow by the pad in module units, so the one path is unchanged and the renderer scales and snaps the drawing itself. The PNG expands each module row's first scanline from the modules' pixel spans instead of one fixed count, and keeps its 1-bit packing, its runs and its copies of the scanline above, since the rows inside a module row still repeat. The pixel buffer is the canvas, rows packed. The terminal text has no size.

Uniform modules centred in a larger canvas, the rendering package's `WithSize` with `WithModulePixelSize`, is left out until asked for. It would be one more member on the options that chooses between filling and padding, and no caller has asked.

Shapes not taken: `ModulePixelSize = 0` meaning "fit" is a magic value where 0 throws today. Overloads with a width and height would add 7 calls a data type, 21 in all. Fractional modules with anti-aliased edges would leave 1-bit and blur the edges a reader samples.

### Pixel input (item 2)

`LuminanceConverter.Convert` becomes public in the `FeatherQR` namespace, in the shape it has. One member, the one the package seam table names as the promotion. The caller keeps the buffer, as the allocation contract wants, and the conversion gets the tiers and the white compositing the rendering package gets.

```csharp
byte[] luminance = new byte[width * height];
LuminanceConverter.Convert(rgba, width, height, rowBytes, PixelLayout.Rgba8888, premultipliedAlpha: false, luminance);
QRCodeDecoder.TryDecodeImage(luminance, width, height, out string text, out var info);
```

```text
public static class LuminanceConverter
{
    public static void Convert(ReadOnlySpan<byte> pixels, int width, int height, int rowBytes, PixelLayout layout, bool premultipliedAlpha, Span<byte> luminance);
}
```

The rendering package's `BitmapLuminanceConverter` then calls the public member, and the seam table loses its first row. The documentation states what the bytes mean, BT.601 over pixels composited against white, which the contract test already holds.

The shape not taken: decode overloads on the decoders that take pixels, a stride and a layout. There are 20 luminance members, so 20 overloads, each renting a luminance buffer inside, and the name `TryDecodeImage` would then mean two inputs. They can be added on request over the one conversion.

```csharp
// The shape not taken, for comparison.
QRCodeDecoder.TryDecodeImage(rgba, width, height, rowBytes, PixelLayout.Rgba8888, out string text, out var info);
```

### A PNG reader (item 3)

The core can read a PNG only with its own inflate, since the runtime's `DeflateStream` calls the host's native library and the core takes no dependency beyond its own code. A reader is the inflate (fixed and dynamic codes, stored blocks, the 32 KiB window), the chunk walk with CRC checks, the five scanline filters, the colour types and bit depths (greyscale, truecolour, palette, each with and without alpha, at 1 to 16 bits), the seven-pass interlace, and the limits a file from outside needs, since a header can declare a size the file does not hold (the stream read limits row records a 47-byte PNG declaring 16,384 by 16,384 pixels). Its size is of the order of the PNG writer's thousand lines, and its tests need a corpus of PNGs from other writers, since reading only what this writer writes would prove little.

What it buys is reading PNG files with the core alone. Photographs from a phone are JPEG or HEIC, and a browser or an operating system decodes a PNG into RGBA pixels that item 2 already reads. So the reader is the largest item and the one with the least reach, and it is in this plan as a decision (D3), with its public shape designed only if it is taken.

### Module and finder pattern shapes (item 4)

Values on `SymbolImageOptions`: `ModuleStyle` (an enum, `Square`, `Circle`, `RoundedSquare`), `ModuleScalePercent` (1 to 100, 100 by default), `CornerRadiusPercent` for the rounded square, and `FinderPatternStyle` (`Square`, `Circle`, `RoundedSquare`, `RoundedSquareCircle`, the four the rendering package has). Enums rather than classes, since the core has no canvas for a caller's `Draw` to reach, and the names end in `Style` so that a consumer with both packages open does not have `ModuleShape` twice.

```csharp
var options = new SymbolImageOptions { ModuleStyle = ModuleStyle.Circle, ModuleScalePercent = 90, FinderPatternStyle = FinderPatternStyle.Circle };
string svg = SymbolImageWriter.GetSvgString(qr, options);
byte[] png = SymbolImageWriter.GetPngBytes(qr, options);
```

The finder patterns never take the module style, by construction: the writer draws every finder from its own stamp or path and skips its modules in the module pass, as the rendering package's `DrawModules` skips them. The rMQR sub-finder and corner patterns are read as shapes too and have not been tested under styling in either package, so the decodability tests cover them before any style reaches rMQR.

SVG: a square at 100 % keeps the stroked run path. Any other style writes one `<defs>` entry for the module (a `<circle>` or a `<rect>` with `rx`) and a `<use>` for each dark module, and each finder as the rendering package's geometry (a stroked oval and a centre oval, or an even-odd path). At version 40 that is one element a dark module, 15,933 on the benchmark's symbol against 8,826 runs, which is the price of a shape with no runs to merge.

PNG and pixels: a module's stamp is rasterized once per module size with coverage at its edges, two sizes at most under a set size, and pasted at each dark module. A finder's 7 by 7 module stamp likewise, three times. Coverage means grey levels, so the PNG leaves 1-bit: greyscale at 4 or 8 bits for black on white, and a palette of the blend levels otherwise, both still one deflate block. The first phase measures what 16 levels (a 4-bit image) and 256 levels cost in bytes against the rendering package's PNG at the same size, and the rasterizer's cost against the writer's time. The scanlines inside a module row no longer repeat, so the copy-above match fails there, and the matcher that looks positions up by their bytes finds the previous module's stamp row instead, at the distance of one module. The 1-bit path stays for the square at 100 %, so nothing the core writes today changes.

Every format honours a style in the same phase (D5). A style the SVG draws and the PNG does not would be one set of options for two images, which the review of 2026-11-06 ruled out for colours.

### An icon area (item 5)

On Standard QR, the writer can clear a centred area of modules for an icon and tell the caller where it is, with no image decoding and the PNG still 1-bit: `IconSizeModules`, `IconBorderModules` and `MaxCoreOccupancyPercent` on the options, the rendering package's module-based sizing with its grid snap and its occupancy check, and a `GetIconRectangle(data, options)` on `SymbolImageWriter` that returns the icon's rectangle in pixels of the image the options describe. The caller draws their logo into the pixel buffer, or over the PNG in whatever decodes their logo. For the SVG, the writer can also embed an image the caller gives as bytes with a media type, as a `data:` URI inside an `<image>` element, since that needs no decoding (D7).

```csharp
var options = new SymbolImageOptions { IconSizeModules = 7, IconBorderModules = 1 };
byte[] pixels = new byte[SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Rgba8888, options)];
SymbolImageWriter.TryWritePixels(qr, pixels, PixelLayout.Rgba8888, out int width, out int height, options);
var rect = SymbolImageWriter.GetIconRectangle(qr, options);   // where to draw the logo, in pixels
```

Micro QR and rMQR get no icon, as in the rendering package: their capacity and error correction leave no room for one.

## Open decisions

| # | Question | Recommendation |
|---|---|---|
| D1 | A set size: which release, and the shape | Ruled 2026-11-07. Items 1 and 2 before the 2.0.0 Phase 7 freeze, since each is small, each is additive, and each changes a scope row that says "stays in the rendering package" or "waits for a request". The shape first proposed, every module at one size and the rest padded, was refused: a caller who asks for 512 pixels and gets 370 pixels of symbol in a margin is let down, and the plan then leaves open the gap it set out to close. The shape taken is the rendering package's `WithSize`: the symbol fills the canvas, each pixel belongs to the module its centre falls in, `ModulePixelSize` is switched off by a size, one side of 0 follows the aspect, and a module under one pixel is refused. Measured the same day to match the builder's pixels exactly (Where things stand) |
| D2 | Pixel input: the one public conversion, or decode overloads | Ruled as recommended 2026-11-07. The one conversion, `LuminanceConverter.Convert` public as it is. The overloads can follow on request |
| D3 | A PNG reader | Ruled as recommended 2026-11-07. Out of this plan, recorded in the scope row with the reasons: a browser or an operating system decodes a PNG into pixels that item 2 reads, photographs are not PNG, and the reader is the largest item. Taken only on a request that names a case where no image decoder is available and the input is PNG |
| D4 | Styling in the core at all | Ruled as recommended 2026-11-07. Yes, after 2.0.0, as additive work in 2.1.0: the shapes measured first (phase 3a), and the maintainer rules on the PNG's bit depth and the SVG's size from the figures before phase 3b builds the public members |
| D5 | Styled output in every format together, or SVG first | Ruled as recommended 2026-11-07. Together. SVG first would ship a style the PNG refuses for the same options |
| D6 | The enum names | Ruled as recommended 2026-11-07. `ModuleStyle` and `FinderPatternStyle`, so that `FeatherQR.ModuleShape` and `FeatherQR.SkiaSharp.ModuleShape` never both exist |
| D7 | The icon: the cleared area and its rectangle alone, or also an image embedded in the SVG | Ruled as recommended 2026-11-07. Both, in one phase. The area is the part every format shares, and the SVG embedding is a `data:` URI with no decoding. Raster compositing stays out |
| D8 | Where the rendering package's styled decodability tests go | Ruled as recommended 2026-11-07. Shared: the test inputs (symbology, style, scale, finder) become a data source both packages' tests read, so a style that decodes through one writer and not the other is a failing test, not a difference nobody sees |

## Phases

Each phase follows the test-first workflow, and a phase that changes `src` appends a Progress log entry with Done, Lessons and the measured deltas.

| # | Phase | Contents | Exit |
|---|---|---|---|
| 0 | Decisions | Done 2026-11-07: D2 to D8 as recommended, D1 reshaped from padding to filling the canvas, and the design rewritten with it | This file reflects the rulings |
| 1 | A set size | Tests first: parity with the rendering package's `WithSize`, pixel for pixel, on the three symbologies at square and non-square canvases, one side of 0, a canvas that gives a module under one pixel, the pad's colour and alpha in each format where the aspect differs, and the symbol's own size unchanged at no set size. Then `Width` and `Height`, the SVG's viewBox and background, the PNG's expansion from the modules' pixel spans, the pixel canvas, the approved listing, the README and the Image output section | The parity test holds on every symbology. `NetStandardBuildParityTest.ImageOutput` holds the bytes across targets. The benchmark's shapes at no set size are unchanged beyond noise, and the PNG at a set size is measured in bytes and time against the builder's |
| 2 | Pixel input | Tests first: the public call's contract (BT.601, white compositing, a stride wider than the row, a premultiplied buffer, the argument checks), red while the type is internal. Then the visibility change and the namespace move, `BitmapLuminanceConverter` on the public member, the approved listings of both packages, the seam table's first row removed, the scope row rewritten, README and migration notes | The rendering project builds with the `InternalsVisibleTo` grant removed for this type. The decode benchmarks are unchanged beyond noise |
| 3a | Styling measured | No change to `src` beyond an internal probe. A stamp rasterizer for the circle and the rounded square with coverage, the PNG at 4 and 8 bits, and the SVG with `<use>`, measured on the benchmark's shapes against the rendering package's PNG and SVG at the same size and style: bytes, time, and whether the output decodes with this library's decoder and with the readers the sweep uses | A table of bytes and time per style and depth, the decodability per style, and the maintainer's ruling on the depth and on D4 |
| 3b | Styling | Tests first: the shared decodability data source (D8) read by a core test that writes each style through each format and decodes it, the with-and-without byte comparison for each style member, finder patterns never styled (a planted fault that lets the module style reach a finder fails the test), rMQR's sub-finder and corner patterns under each style, and the 1-bit path unchanged for the square at 100 %. Then the options members, the enums, the stamps, the SVG forms, the PNG depths, the approved listing and docs | Every styled output decodes where the rendering package's does. Today's outputs are byte for byte unchanged. The benchmark's plain shapes are unchanged beyond noise |
| 4 | The icon area | Tests first: the grid snap and the occupancy check against the rendering package's `GetIconRects` at a whole module size, the cleared area in each format, the rectangle reported, Micro QR and rMQR refusing the members, and the SVG's embedded image. Then the members, `GetIconRectangle`, the SVG `<image>`, the listing and docs | A symbol with the area cleared decodes at the occupancy the rendering package allows, and the rectangle matches the renderer's for the same modules |
| 5 | A PNG reader | Only if D3 takes it. Its own design, corpus and limits first, then the inflate, the filters, the colour types and the interlace, each test-first | Reads the corpus, refuses the hostile files, and reads the core's own PNG at every module size and style |
| 6 | Fold | The Image output section gains the size, the styles and the icon area with their figures, the seam table and the scope rows are rewritten, lessons go to the specs, and this file and its index entry are deleted | No figure in the specs without its run, and no copy of the plan left |

Phases 1 and 2 are independent and each lands before the Phase 7 freeze of the 2.0.0 plan, which waits for them (D1). Phases 3a, 3b and 4 are after 2.0.0 and depend on phase 1, since a styled stamp and an icon rectangle are placed on the canvas the size gives. Phase 3a can end the styling at the maintainer's ruling. Phase 4 can run before 3b. Phase 5 is independent of all and waits for D3.

## Progress log
