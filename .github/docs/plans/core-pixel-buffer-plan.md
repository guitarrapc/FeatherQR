# The core's pixel buffer in another library's memory: image size, row stride, 24-bit layouts

## Purpose

A caller who shows or reads a symbol through another graphics library already gets there with the core alone, but each crossing costs a buffer and a copy, or a conversion inside that library. This plan removes the copy and the conversion where a measured case pays for them, with three additions to the core: the image's size before it is written, a row stride on the pixel writer, and two 24-bit layouts. It follows the survey of renderer support beyond SkiaSharp (2026-11-09), where the maintainer chose compiled samples over adapter packages. The samples for System.Drawing, WPF and ImageSharp are the consumers this plan is measured against.

When the plan completes, its decisions and lessons move into [qrcode-symbologies.md](../specs/qrcode-symbologies.md#image-output) and this file is deleted.

## What the core does today

Measured on 2026-11-09 with file-based console probes that reference the core project, on Windows 11 x64 with AVX2. The machine ran at 57 % CPU load, so the times below are ratios to read, not figures to quote.

- `TryWritePixels` writes rows packed, and the image's width and height are known only once it has written them. `GetPixelBufferSize` returns bytes. Every sample therefore writes into a buffer of its own and copies it into the other library's image: `SystemDrawing.cs` row by row into `LockBits` memory, `Wpf.cs` through `BitmapSource.Create`, `ImageSharp.cs` through `Image.LoadPixelData`. `ConsoleAppCore` writes a Micro QR code into a buffer of its own and copies it row by row into the icon area of a QR code's buffer.
- Allocating such a buffer and copying it row by row into another array cost 42 to 52 % of `TryWritePixels` itself, on Standard QR version 10 and version 40 at 8 pixels a module (520 and 1,480 pixels a side, `Bgra8888`).
- `LuminanceConverter.Convert` reads four layouts, `Gray8` and three of four bytes. System.Drawing loads a JPEG it wrote as `Format24bppRgb`, which holds blue, green and red in memory with rows padded to 4 bytes (16 bytes for 5 pixels). The sample asks `LockBits` for `Format32bppArgb`, which converts. That conversion and its copy cost 28 to 34 % on top of the conversion to luminance and the decode, where `LockBits` in the bitmap's own format took no measurable time.
- WPF decodes the same JPEG and the BMP as `Bgr32` and the PNG as `Bgra32`, both four bytes, so WPF needs no 24-bit layout.
- Every four-byte source measured on 2026-11-09 fills the fourth byte with 255, so `Bgra8888` reads it: GDI `BitBlt` from the screen into a 32-bit DIB section, `CopyFromScreen` into `Format32bppArgb` and `Format32bppRgb`, a `Format32bppRgb` bitmap cleared to white, and WPF's `Bgr32` from a JPEG. A buffer whose fourth byte is 0 would read as all white through `Bgra8888` and decode nothing with no error, and through `Rgb888x` it decodes with red and blue exchanged (a blue module reads 76 instead of 28). None of those sources hands such a buffer over. The review on 2026-11-10 measured one that does: a 32-bit BMP written with that byte 0, which GDI+ loads as `Format32bppRgb` and WPF as `Bgr32`.
- `LockBits` hands a bottom-up BMP over top row first, with a positive stride. A buffer whose rows run bottom to top still decodes on all three symbologies, with the corners reported in the buffer's rows: Standard QR at 148 pixels reported its top-left corner at y 132. Unity textures and OpenGL read-backs store rows bottom to top by their own documentation. Neither has a sample or a CI build here.

## Scope

| In | Out |
|---|---|
| The image's width and height for given options before writing, for the three data types (Phase 1) | Rows bottom to top, in the writer or the converter. No measured source in the samples' libraries hands them over. Reconsider for a Unity or OpenGL consumer with a build that runs it |
| A row stride on `TryWritePixels`, for the three data types, so a symbol is written into another library's image, or into a region of a larger buffer, in place (Phase 2) | A layout of three colour bytes and an undefined fourth (blue, green, red, x). Every source measured on 2026-11-09 sets that byte to 255. Reconsider for a source measured to hand over 0 or garbage there. Moved In on 2026-11-10 as `Bgr888x` (Review below) |
| `Rgb24` and `Bgr24` (names per D4) in `PixelLayout`, read by `LuminanceConverter.Convert` and written by `TryWritePixels` (Phase 3), with vector tiers for the conversion if D5 asks for them (Phase 4) | Premultiplied alpha in the writer. It changes only colours with an alpha below 255, and no sample writes one into a premultiplied image |
| The three samples and `ConsoleAppCore` rewritten on the new calls, and the docs | Decode overloads that take pixels, a stride and a layout. The scope row on the luminance conversion API keeps them on request |
| | Adapter packages for other libraries. The samples stand in for them |

Every layout `PixelLayout` names is one both the writer and the converter take, so the 24-bit layouts arrive in both. A layout only one side took would throw on the other at run time.

## Decisions

The maintainer ruled all five on 2026-11-10. The call sites below are the comparison each ruling was made on.

| # | Decision | Ruling |
|---|---|---|
| D1 | 2.0.0 or 2.1.0 | 2.0.0, every phase, as Phase 6l of the 2.0.0 plan. The Phase 7 freeze waits for the whole plan, and no phase moves to 2.1.0. The plan had proposed letting Phases 3 and 4 slip to 2.1.0, and the maintainer chose to make the release with all of them. `SymbolImageWriter`, `PixelLayout` and `LuminanceConverter` became public after `2.0.0-preview.8`, so no release has shipped them and their shape is free until the freeze |
| D2 | The shape of the size query | A, below |
| D3 | The shape of the stride | A, below |
| D4 | The 24-bit layouts' names | A, below |
| D5 | Does the 24-bit conversion ship without vector tiers? | Only if its portable loop on x64 with AVX2 is not slower than what a caller does today, a conversion to 32 bits in the other library followed by the AVX2 four-byte tier. Otherwise Phase 4 lands first. Under D1 both land before the freeze either way, so D5 decides only the order |

### D2, the size query

The call site is the ImageSharp sample's encode. Today:

```csharp
var pixels = new byte[SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Rgba8888, options)];
SymbolImageWriter.TryWritePixels(qr, pixels, PixelLayout.Rgba8888, out var width, out var height, options);
var image = Image.LoadPixelData<Rgba32>(pixels, width, height); // a second copy of the image
```

A, a tuple of the size every format writes, as `GetIconRectangle` returns its rectangle:

```csharp
var (width, height) = SymbolImageWriter.GetImageSize(qr, options);
var image = new Image<Rgba32>(width, height);
image.DangerousTryGetSinglePixelMemory(out var memory);
SymbolImageWriter.TryWritePixels(qr, MemoryMarshal.AsBytes(memory.Span), PixelLayout.Rgba8888, out _, out _, options);
```

```
public static (int Width, int Height) GetImageSize(QRCodeData data, in SymbolImageOptions options = default);   // and MicroQRCodeData, RmQRCodeData
```

B, the size beside the byte count:

```csharp
SymbolImageWriter.GetPixelBufferSize(qr, PixelLayout.Rgba8888, out var width, out var height, options);
```

```
public static int GetPixelBufferSize(QRCodeData data, PixelLayout layout, out int width, out int height, in SymbolImageOptions options = default);   // and the other two
```

A, because the size does not depend on the layout and is also the PNG's and the SVG's size, which B ties to a pixel buffer. The precedent is `GetIconRectangle`'s `(int X, int Y, int Width, int Height)`.

Revised on 2026-11-10 after Phase 1 ran: the call returns an `ImageSize` struct, and `GetIconRectangle` an `ImageRect`, both with `Deconstruct`, so the call site above reads the same. The maintainer ruled a tuple out of a public return value, `out` parameters out of a `Get` call, and the library's own type over `System.Drawing.Size`. The reasons are in the Pixel buffer record.

```
public static ImageSize GetImageSize(QRCodeData data, in SymbolImageOptions options = default);   // and MicroQRCodeData, RmQRCodeData
public static ImageRect GetIconRectangle(QRCodeData data, in SymbolImageOptions options);
```

### D3, the stride

The call site is `ConsoleAppCore`'s icon composition. Today:

```csharp
var iconPixels = new byte[SymbolImageWriter.GetPixelBufferSize(micro, PixelLayout.Rgba8888, iconOptions)];
SymbolImageWriter.TryWritePixels(micro, iconPixels, PixelLayout.Rgba8888, out _, out _, iconOptions);
for (var row = 0; row < iconHeight; row++)
    iconPixels.AsSpan(row * iconWidth * 4, iconWidth * 4).CopyTo(composed.AsSpan(((iconY + row) * composedWidth + iconX) * 4));
```

A, an overload with the stride before the layout, in the order `LuminanceConverter.Convert` takes them:

```csharp
var at = (iconY * composedWidth + iconX) * 4;
SymbolImageWriter.TryWritePixels(micro, composed.AsSpan(at), composedWidth * 4, PixelLayout.Rgba8888, out _, out _, iconOptions);
```

```
public static bool TryWritePixels(QRCodeData data, Span<byte> destination, int rowBytes, PixelLayout layout, out int width, out int height, in SymbolImageOptions options = default);   // and the other two
```

B, a value type describing the buffer:

```csharp
SymbolImageWriter.TryWritePixels(micro, composed.AsSpan(at), new PixelBufferFormat(PixelLayout.Rgba8888, composedWidth * 4), out _, out _, iconOptions);
```

A, because B adds a public type for two values, and keeping the converter symmetric would move `Convert` onto that type as well. No release has shipped `Convert`, so that move breaks no caller, but it buys no call site anything A does not. The packed overload stays beside A, since a caller who has not asked for the size first does not know a row's length. The overload's rules follow `Convert`'s: rows start `rowBytes` apart, the bytes between a row's last pixel and the next row are never written, the last row may end at its last pixel, a short destination returns `false` and writes nothing, and a stride below one row throws `ArgumentOutOfRangeException`. The existing overload is this one at a stride of one row.

### D4, the 24-bit names

The call site is the System.Drawing sample's decode, which today asks `LockBits` to convert:

```csharp
var layout = bitmap.PixelFormat switch
{
    PixelFormat.Format24bppRgb => PixelLayout.Bgr24,   // blue, green, red in memory despite the name
    PixelFormat.Format32bppArgb or PixelFormat.Format32bppRgb => PixelLayout.Bgra8888,
    _ => (PixelLayout?)null,
};
var locked = bitmap.LockBits(rect, ImageLockMode.ReadOnly, bitmap.PixelFormat);   // no conversion
```

A, `Rgb24 = 4` and `Bgr24 = 5`, named by bits a pixel as `Gray8` is. B, `Rgb888 = 4` and `Bgr888 = 5`, named by bits a channel as the four-byte layouts are. A, because `Rgb888` sits one letter from `Rgb888x`, and a caller who picks the wrong one reads every pixel at the wrong offset and decodes nothing, with no error. A also matches the names the WPF and ImageSharp callers map from. The XML docs name no other library, so the samples and the README's FAQ carry the mapping from each library's formats.

## Phases

Each phase follows the test-first workflow, regenerates `PublicAPI.approved.txt`, updates the samples it names and the [Image output](../specs/qrcode-symbologies.md#image-output) record in the same change, and appends a Progress log entry with Done, Lessons and its measurements on an idle machine.

| # | Phase | Contents | Exit |
|---|---|---|---|
| 1 | Image size | D2 on the three data types. The ImageSharp sample writes into its image's memory, and copies only where that memory is not one block. The System.Drawing sample writes into `LockBits` memory, whose stride at four bytes a pixel is one row | The size equals what `TryWritePixels` reports, the PNG's header and the SVG's size, on every equivalence class below. The samples' encode times measured before and after |
| 2 | Row stride | D3 on the three data types. `ConsoleAppCore`'s icon is written in place | The packed overload's output and time unchanged (the image output benchmark). Padding and the area around a region keep their bytes |
| 3 | 24-bit layouts | D4 in the writer, plain and styled, and in `LuminanceConverter.Convert` as a portable loop. The System.Drawing sample reads a 24-bit bitmap in its own format | D5's comparison on 520 and 1,480 pixels. Phase 4 runs before this phase merges if the loop loses |
| 4 | Vector tiers for 24 bits | The conversion's tiers on AVX2, NEON and 128 bits, each kept only if it beats the portable loop on its own build, WebAssembly timed interpreted and AOT. The kernel's row in `SimdTiers` and the generated table | Every tier's bytes equal the portable loop's |
| 5 | Docs and fold | The Image output and Pixel buffer sections, the scope rows this plan touches, the README's FAQ, the 2.0.0 plan's row. This file folded and deleted | Nothing of this plan left only here |

## Tests

The equivalence classes every new call is tested over:

- Standard QR, Micro QR and rMQR, each plain, styled, at a set size that is not whole pixels, and with an icon area on Standard QR.
- Every layout, the 24-bit ones from Phase 3 on.
- A stride of exactly one row, one padded by a byte count that is not a multiple of the pixel size, a stride below one row, a destination one byte short, and a destination whose last row ends at its last pixel.
- A region inside a larger buffer filled with a sentinel, which must keep every byte outside the region and every padding byte.
- For the 24-bit layouts: the writer's bytes equal `Rgb888x`'s with every fourth byte dropped, and the converter's luminance equals its reading of the same colours as `Rgb888x`, on row widths around each tier's block size.
- `NetStandardBuildParityTest` covers the new outputs, and the span forms allocate nothing once warm.

## Progress log

Phases 1 to 4 ran on 2026-11-10 in one session, and Phase 5's docs with them. Left: the ARM64 tiers' timing (Phase 4), then the fold and the deletion of this file (Phase 5).

All times are from .NET 10 on x64 with AVX2, a Ryzen 9 7950X3D that was also running other sessions' builds, so each comparison is interleaved or alternated and none is a figure to quote alone.

### Phase 1, image size

Done:

- `GetImageSize` on the three data types, a named tuple at first and an `ImageSize` since D2's revision the same day, with `GetIconRectangle` returning an `ImageRect`, refusing the options every writer refuses. `SymbolImageWriterImageSizeTest` holds it to the size `TryWritePixels` reports, the PNG's header and the SVG's root on the three symbologies, plain, styled, at a set size that is not whole pixels, with one side given, and with an icon area.
- The System.Drawing sample writes into `LockBits` memory, and the ImageSharp sample into the image's memory when that is one block.

Measured, console probes, the median of 15 interleaved rounds, at 520 and 1,480 pixels a side:

| Encode | Before | After |
|---|---|---|
| System.Drawing | 343 and 3,378 µs | 252 and 1,763 µs, of which the `Bitmap` alone is 206 and 1,283 |
| ImageSharp 3.1.12 | 190 to 360 and 2,442 µs | 38 to 40 and 1,510 µs |

Lessons:

- ImageSharp 3.2.0 and later fail a Release build without a license key, so the probe took 3.1.12. The gate was left alone.
- ImageSharp holds an image past 4 MiB in several blocks (checked on 4.1.3: 1,024 pixels a side is one block and 1,025 is not). Above that the sample writes into a buffer the image wraps. Making the image first and then loading it from a buffer took 3,192 µs against `LoadPixelData`'s 3,019 at 1,480 pixels.
- WPF was measured and left: a `WriteableBitmap` written in place took 663 and 3,213 µs where `BitmapSource.Create` from a buffer took 409 and 2,915.
- The tuple was the plan's own pick, made from one call site and one precedent that was itself unshipped. Shown the three shapes side by side, the maintainer took none of the two the plan had weighed. The types went into `TypeShapeTest`'s list of values the library alone builds, and the internal class that held the size limits gave up the name `ImageSize`.
- An icon that fits version 3 at level H in the plan's head did not: 30 percent of its 29 modules allows 8, and 7 with a border are 9. The tests use version 5.

### Phase 2, row stride

Done:

- The `rowBytes` overload on the three data types, with the rules D3 lists. `SymbolImageWriterPixelStrideTest` holds the rows to the packed overload's, the bytes between rows and around a region to what they held, the exact length and one byte short, and the throw.
- `ConsoleAppCore` writes the Micro QR code into the icon area in place. Its 16 output files kept their bytes.

Measured, a probe built against a worktree of the commit before and against the change, alternating processes on two pinned cores, the fastest of five or six a side:

| Shape | Before | After |
|---|---|---|
| Version 1, RGBA | 3.50 to 3.58 µs | 3.60 µs |
| Version 40, RGBA | 153 µs | 152 µs |
| Version 40, `Gray8` | 107 µs | 108 µs |
| Version 40, RGBA, 512 by 512 | 98 to 103 µs | 96 to 99 µs |
| Version 40, RGBA, styled | 1.78 ms | 1.73 ms |
| Version 40, RGBA, the stride overload | | 151 µs |

Lessons:

- The first shape, one method with a flag for the packed rows, took 300 to 330 µs at version 40, twice the time, and so did the second, the loops in a method both entries inlined. With `DOTNET_TieredPGO=0` every build took 305 to 365 µs, so the loss was the profile's. The loops are now a method that is never inlined.
- The benchmark in process read the doubling on a loaded machine, where it could have been the load. The alternating probe settled it.
- A test that fills the destination with one byte did not catch a writer that copied a row's padding into the next row. The stride test fills it with bytes that differ from their neighbours.
- A planted fault's file, put back with its old time, is older than the build made from the fault, so the next build was skipped and the suite ran the fault. The file is touched after it is put back.

### Phase 3, 24-bit layouts

Done:

- `Rgb24 = 4` and `Bgr24 = 5`, in the writer, plain and styled, and in `LuminanceConverter.Convert`.
- The System.Drawing sample reads a 24-bit bitmap in its own format, and a 32-bit one without a conversion.

Measured, D5's comparison, the same probe as Phase 1's, with the portable loop and no vector tier:

| Decode of a 24-bit `Bitmap`, 520 and 1,480 pixels | Time |
|---|---|
| `LockBits` at 32 bits, a copy, the four-byte AVX2 tier (before) | 327 and 2,970 µs |
| `LockBits` at 24 bits, a copy, the portable loop | 265 and 2,137 µs |
| The same two, read from the locked memory with no copy | 208 and 1,706 µs, 189 and 1,566 µs |

So D5's condition held, and Phase 3 could have merged before Phase 4.

Lessons:

- Version 40 as `Rgb24` took 381 µs with each run filled by copies from its first pixel, and 288 with a run of up to nine pixels stored as overlapping words and a longer one copied from its first eight, against RGBA's 152.
- The layouts each library's formats map to were checked with a navy symbol, luminance 58, and 78 with red and blue exchanged. The README's FAQ has the table.

### Phase 4, vector tiers

Done:

- The kernel row `LuminanceConverterRgb24`: AVX2, the ARM64 dot product, and a 128-bit tier that spreads the pixels with SSSE3, the ARM64 table lookup or PackedSimd. A process with none of these, the netstandard builds included, takes the per-pixel loop.
- `LuminanceConverterRgb24ParityTest` enters each tier this machine has, at every width from its narrowest to 140 pixels, with the source ending where readable memory ends. `tests/FeatherQR.AotAnalysis` has the parity check and the timing shapes.

Measured, `--time`, version 40 as BGR with padded rows, the tier against the portable loop:

| Build | 555 pixels a side | 1,480 pixels a side |
|---|---|---|
| x64 JIT, AVX2 | 27.8 µs against 296 | 193 µs against 2,213 |
| x64 JIT, `DOTNET_EnableAVX2=0`, SSSE3 | 61 to 67 µs against 282 to 296 | 461 to 481 µs against 2,200 to 2,213 |
| x64 default NativeAOT publish, SSSE3, three processes | 46 to 48 µs against 217 to 223 | 334 to 363 µs against 1,532 to 1,563 |
| WebAssembly interpreted, `--no-liftoff`, three processes a side | 404 to 428 µs against 901 to 1,735 | |
| WebAssembly AOT-compiled, the fastest of five processes a side | 181 µs against 287 | 1,350 µs against 1,968 |

Every tier measured beat the portable loop, so each stays.

Not done:

- The ARM64 tiers are not timed, since the session had no ARM64 machine. Their parity checks run on CI's ARM64 runners, with and without the dot product, which have not run this branch yet either. To time them: `dotnet build tests/FeatherQR.AotAnalysis -c Release`, then `dotnet tests/FeatherQR.AotAnalysis/bin/Release/net10.0/FeatherQR.AotAnalysis.dll --time --shape LuminanceConverterRgb24`, once as it is and once with `DOTNET_EnableArm64Dp=0`. A tier stays only if the dispatch's rows beat their `-scalar` rows.

### Review, 2026-11-10

An adversarial review of the branch, each finding checked by three verifiers, changed:

- `Bgr888x = 6`, ruled by the maintainer the same day. A 32-bit BMP written with its fourth byte 0 loads in GDI+ as `Format32bppRgb`, and the System.Drawing sample read it in place as `Bgra8888`, so every pixel was transparent, the image white and the decode failed with no error. Before this branch the sample had asked `LockBits` for `Format32bppArgb`, which converts. The sample reads `Format32bppRgb` as `Bgr888x` now, and the README maps it and WPF's `Bgr32` to it. Both read a navy symbol at luminance 58 as `Bgr888x`. The BMP the sample saves through GDI+ from a `Format32bppArgb` bitmap holds 255 in the fourth byte of all 87,616 pixels, which is why the sample's own run had passed. The converter reads it with the four-byte tiers, BGRA with the alpha handling off. The spec's Left out row for this layout went.
- The ARM64 cell had been `AdvSimdDp` or `Scalar`, a scalar cell for a missing CPU feature, which the tier table's rules exclude. The 128-bit tier now spreads with the ARM64 table lookup too, as the four-byte kernel's portable tier serves ARM64 without the dot product.
- The dispatch's gate is a predicate the parity test holds with a floor, where a test had compared the tier list with the same flags it was built from.
- `NetStandardBuildParityTest` runs `LuminanceConverter.Convert` on the netstandard builds at every layout, which no test did before for any layout.
- The default NativeAOT publish was run: the `X64Sse` tier report and parity pass, and its timing is in the table above. The WebAssembly report and parity passed here interpreted after `Bgr888x`, and AOT-compiled before the review. The `x86-64-v3` and ARM64 reports run on CI's `aot-analysis` jobs.
- The x64 JIT's code for the AVX2 and SSSE3 three-byte kernels was read. Each step is its loads, the byte shuffles and the multiply-adds, with no call or bounds check in the loop.
- The benchmark's stride shape writes the 8-pixel image, the allocation test's stride pads every image, and comments, the spec and this log were corrected where they said more than the code or the record.

### Phase 5, docs

Done: the Pixel buffer section, the Image output table, four Left out rows (one of which the review removed) and the scope row in `qrcode-symbologies.md`, the generated tier table, the README (the raw pixel section and the FAQ's layout table), `docs/migration.md`, and the 2.0.0 plan's row.

The generated API page, `src/FeatherQR.Playground/wwwroot/api/index.html`, was regenerated with D2's revision.

Not done:

- The fold and the deletion of this file, which wait for the ARM64 timing.
