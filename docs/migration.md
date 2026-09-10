# Migration

One section per release, newest first. Each section lists what changed in that release and only that release; read the sections between your version and the one you are moving to.

| Upgrading to | What it means for existing code |
|---|---|
| [2.0.0](#200) | **Breaking.** Three packages instead of one, new namespaces (`FeatherQR`, `FeatherQR.SkiaSharp`), `TryDecode(SKBitmap)` moved to the rendering package, the deprecated members removed, one naming rule applied (`ECCLevel` to `QREccLevel`, `CreateQrCode` to `Create`, and friends — with a replacement script), and the result and option types unified (immutable, sealed). The `SkiaSharp.QrCode` install line keeps working |
| [1.2.0](#120) | **Additive**, one decoder behaviour change (Kanji segments decode instead of failing). rMQR, generator options structs, version ranges, `Try`-only sizing, two `[Obsolete]` warnings |
| [1.1.0](#110) | Source compatible, **binary breaking**: the image builders share a base class, recompile |
| [1.0.0](#100) | **Breaking.** The obsolete `QrCode` class is removed |
| [0.11.0](#0110) | Icon handling changes (`IconData` takes an `IconShape`) |
| [0.9.0](#090) | **Breaking.** `QrCode` becomes `QRCodeImageBuilder`; namespaces and removed features |

## 2.0.0

The library that shipped as one `SkiaSharp.QrCode` package is now a dependency-free core plus a SkiaSharp rendering package, so a project that only needs module matrices no longer carries the SkiaSharp native library. Nothing about encoding or decoding behavior changed; the work is in `using` lines and, if you want it, the package reference.

### Packages

| Package | Contents | Depends on |
|---|---|---|
| `FeatherQR` | Generators, decoders, data types, options, `GetModuleRectangles` | nothing on .NET 8+; `System.Memory` / `System.Runtime.CompilerServices.Unsafe` on .NET Standard |
| `FeatherQR.SkiaSharp` | Image builders, `SymbolRenderer`, `SKCanvas` extensions, `IconData` and shapes, `SKBitmap` decoding | `FeatherQR`, `SkiaSharp` |
| `SkiaSharp.QrCode` | Nothing. A compatibility metapackage | `FeatherQR.SkiaSharp` |

An existing `<PackageReference Include="SkiaSharp.QrCode" />` keeps working: it now resolves `FeatherQR.SkiaSharp` and `FeatherQR` transitively. Switch the reference to `FeatherQR.SkiaSharp` when convenient, or to `FeatherQR` alone if you never render images. All three ship at the same version from the same release.

### Namespaces

The root namespace `SkiaSharp.QrCode` is gone from the assemblies, and `SkiaSharp.QrCode.Image` is folded into the rendering package:

| 1.x | 2.0.0 |
|---|---|
| `using SkiaSharp.QrCode;` | `using FeatherQR;` |
| `using SkiaSharp.QrCode.Image;` | `using FeatherQR.SkiaSharp;` |

Type names change too, under [Renames](#renames) below. Inside your own `namespace FeatherQR.Something` a bare `SkiaSharp` would bind to `FeatherQR.SkiaSharp`; put `using SkiaSharp;` above the namespace declaration, as usual, and nothing changes.

### `TryDecode(SKBitmap)` moved

The bitmap overloads are no longer static members of `QRCodeDecoder`, `MicroQRCodeDecoder` and `RmQRCodeDecoder`, because those types live in the core package that knows nothing about SkiaSharp. They are C# 14 extension members in `FeatherQR.SkiaSharp`:

```csharp
using SkiaSharp;
using FeatherQR;
using FeatherQR.SkiaSharp;

using var bitmap = SKBitmap.Decode("qr.png");

// Every language version: the enclosing class name.
if (QRCodeImageDecoder.TryDecode(bitmap, out var text, out var info)) { }

// C# 14 (net10.0 default, or <LangVersion>14</LangVersion>): the 1.x spelling still compiles.
if (QRCodeDecoder.TryDecode(bitmap, out text, out info)) { }
```

The classes are `QRCodeImageDecoder`, `MicroQRCodeImageDecoder` and `RmQRCodeImageDecoder`; both overloads (`out string` and `out string, out …DecodeInfo`) exist on each. A net8.0 project defaults to C# 12, so if the 1.x spelling stops compiling there, either add `using FeatherQR.SkiaSharp;` and switch to the class name, or raise `LangVersion`. This is not a missing overload.

The luminance overloads, `TryDecodeImage(ReadOnlySpan<byte> luminance, int width, int height, …)`, are unchanged and remain in the core; they are the way to decode from any image library other than SkiaSharp. Composite transparent pixels against white before converting.

### The announced removals

The members deprecated in 1.2.0 are gone, and so are the parameter list generator overloads that shipped in 1.1.1. Every generator now takes its configuration as one options struct, and that struct has a default, so the shortest call is unchanged:

```csharp
// the only shape, and the shortest call is still two arguments
var data = QRCodeGenerator.Create("content", QREccLevel.M);
```

| Removed | Replacement |
|---|---|
| `CreateQrCode(text, ecc, utf8BOM, eciMode, requestedVersion, quietZoneSize)` and its `string` / destination siblings | `Create(text, ecc, in QRCodeGeneratorOptions)` |
| `CreateMicroQRCode(text, ecc, requestedVersion, quietZoneSize)` and its `string` / destination siblings | `Create(text, ecc, in MicroQRCodeGeneratorOptions)` |
| `QRCodeGenerator.GetRequiredBufferSize`, `MicroQRCodeGenerator.GetRequiredBufferSize` | `TryGetRequiredBufferSize` on all three generators |
| `Compression` | Nothing. No API ever accepted or returned it; compress the bytes from `GetRawData()` yourself |

Each argument becomes a property, and the mapping is mechanical:

```csharp
// before
var data = QRCodeGenerator.CreateQrCode(text, ECCLevel.M, utf8BOM: true, eciMode: EciMode.Utf8, requestedVersion: 5, quietZoneSize: 0);

// after (the renames below land in the same release)
var data = QRCodeGenerator.Create(text, QREccLevel.M, new QRCodeGeneratorOptions
{
    Utf8Bom = true,
    EciMode = EciMode.Utf8,
    Version = 5,
    QuietZoneSize = 0,
});
```

| Argument | Property |
|---|---|
| `utf8BOM` | `Utf8Bom` |
| `eciMode` | `EciMode` |
| `quietZoneSize` | `QuietZoneSize` |
| `requestedVersion` (Standard QR, `int`) | `Version` |
| `requestedVersion` (Micro QR, `MicroQRVersion?`) | `Version` |

**One trap: `requestedVersion: -1` is not `Version = -1`.** The parameter list spelled "pick the smallest version that fits" as `-1`; `QRVersionRange` spells it `Any` and rejects `-1` deliberately, so that a defaulted or mistyped field cannot pass for automatic selection. A variable that may hold `-1` needs the branch:

```csharp
Version = version == -1 ? QRVersionRange.Any : QRVersionRange.Exactly(version),
```

Micro QR's `null` needs no branch: `MicroQRVersion?` converts implicitly, and `null` means `Any`.

**A pinned version is checked against the content**, which the parameter list did not do: `Version = 1` with content that does not fit version 1 throws an `ArgumentException` naming the version, ECC level and mode, where `requestedVersion: 1` used to fail deeper inside the encoder with `ArgumentOutOfRangeException (Parameter 'length')`.

### Renames

One rule decides every public name: **a prefix says which symbology (`QR`, `MicroQR`, `RmQR`), and no prefix means all three**. A noun that literally denotes *a code* keeps its `{Sym}Code` form, so `QRCodeData`, `QRCodeGenerator`, `QRCodeDecoder`, `QRCodeGeneratorOptions`, `QRCodeCalculatedSize`, `QRCodeDecodeInfo`, `QRCodeImageBuilder` and their Micro QR and rMQR siblings are unchanged. What moved is everything else.

| 1.x | 2.0.0 |
|---|---|
| `ECCLevel` | `QREccLevel` |
| `QRCodeSegmentation` | `QRSegmentation` |
| `QRCodeVersionRange` | `QRVersionRange` |
| `QRCodeDecodeStatus` | `DecodeStatus` |
| `QRCodeGenerator.CreateQrCode` | `QRCodeGenerator.Create` |
| `MicroQRCodeGenerator.CreateMicroQRCode` | `MicroQRCodeGenerator.Create` |
| `RmQRCodeGenerator.CreateRmQRCode` | `RmQRCodeGenerator.Create` |
| `QRCodeCalculatedSize.QrSize`, `MicroQRCodeCalculatedSize.QrSize` | `.Size` |
| `QRCodeGeneratorOptions.Utf8BOM` | `.Utf8Bom` |
| `QRCodeRenderer` | `SymbolRenderer` |
| `QRCodeImageBuilderBase<TSelf>` | `SymbolImageBuilderBase<TSelf>` |
| `QRCodeExtensions` | `SKCanvasExtensions` |
| `Vector2Slim` | Removed from the public surface (it appeared in no public signature) |

`DecodeStatus`, `SymbolRenderer` and `SymbolImageBuilderBase<TSelf>` lost their prefix because they serve all three symbologies; `QRCodeExtensions` is named after the type it extends, as the BCL does. The `Create` methods dropped the part that repeated the class name, which is also what removes the `Qr` / `QR` casing inconsistency they carried.

Every rename is a whole-word replacement. Order does not matter as written, because every pattern is `\b`-anchored and none is a prefix of another; put the longer name first if you add rules of your own.

```shell
# bash, from your repository root; adjust the file glob to taste
grep -rlZ --include='*.cs' -e ECCLevel -e QRCode -e CreateQrCode -e CreateMicroQRCode -e CreateRmQRCode -e QrSize -e Utf8BOM . \
  | xargs -0 sed -i \
    -e 's/\bCreateQrCode\b/Create/g' \
    -e 's/\bCreateMicroQRCode\b/Create/g' \
    -e 's/\bCreateRmQRCode\b/Create/g' \
    -e 's/\bQRCodeSegmentation\b/QRSegmentation/g' \
    -e 's/\bQRCodeVersionRange\b/QRVersionRange/g' \
    -e 's/\bQRCodeDecodeStatus\b/DecodeStatus/g' \
    -e 's/\bECCLevel\b/QREccLevel/g' \
    -e 's/\bQrSize\b/Size/g' \
    -e 's/\bUtf8BOM\b/Utf8Bom/g' \
    -e 's/\bQRCodeRenderer\b/SymbolRenderer/g' \
    -e 's/\bQRCodeImageBuilderBase\b/SymbolImageBuilderBase/g' \
    -e 's/\bQRCodeExtensions\b/SKCanvasExtensions/g'
```

Four cautions if you run it as-is.

- **It rewrites names other libraries also use.** `ECCLevel` and `CreateQrCode` are spelled the same in **QRCoder** (`QRCodeGenerator.ECCLevel`, and four `CreateQrCode` overloads), and `QrSize` and `Utf8BOM` are generic enough to collide with your own code; those hits need putting back. Review the diff rather than committing the run.
- **`sed -i` as written is GNU sed.** On macOS and BSD, write `sed -i ''` instead, or run it through `gsed`.
- **It does not move namespaces.** The `using SkiaSharp.QrCode;` → `using FeatherQR;` change from the [namespace change](#namespaces) is the other mechanical edit, and it is not in the script.
- **It is a rename script, not a migration script.** It does not perform the [announced removals](#the-announced-removals) above, which the compiler will point at.

### The `string` overloads are gone

`Create` takes `ReadOnlySpan<char>`. The `string` overloads were removed because `string` converts to `ReadOnlySpan<char>` implicitly, so the calls that used them keep compiling:

```csharp
var data = QRCodeGenerator.Create("https://example.com", QREccLevel.M);   // unchanged
```

The one spelling that needs an edit is a named argument: `plainText:` becomes `textSpan:`. A null `string` behaves as it always did — the removed overload called `AsSpan()` on it, which is null-safe, so null encodes an empty symbol on both sides of the upgrade.

**Except on .NET Framework and other netstandard2.0 consumers.** See [Older language versions](#older-language-versions) below; that conversion is not available there before C# 14.

### Older language versions

Some 2.0.0 spellings need C# features your project may not have enabled. They affect the `netstandard2.0` asset, which is what .NET Framework 4.6.2+ binds, and the `init` ones affect `netstandard2.1` as well. Neither is about the runtime — the compiled library works fine on both; it is about what your compiler will let you write.

| Your project targets | Default language version | A `string` where a `ReadOnlySpan<char>` is expected (`Create`, `TryGetRequiredBufferSize`) | Any `{ … }` initializer (`QRCodeGeneratorOptions`, `IconData`) |
|---|---|---|---|
| net472 / net48 / netstandard2.0 | 7.3 | needs C# 14 | needs C# 9 |
| netstandard2.1 | 8.0 | works | needs C# 9 |
| net8.0 | 12.0 | works | works |
| net10.0 | 14.0 | works | works |

`string` converts to `ReadOnlySpan<char>` through a conversion the compiler synthesizes, and on netstandard2.0 the span comes from the `System.Memory` package rather than the framework, where only C# 14 synthesizes it. Every version through C# 13 reports `CS1503`. `init` accessors are a C# 9 feature, so an object initializer that assigns one reports `CS8370` or `CS8400` below that.

`IconData` carries one constraint the table cannot express, because it is about your *compiler* rather than your language version. `Icon` is a `required` member, and the compiler marks every constructor of such a type that is not annotated `[SetsRequiredMembers]` as unusable to compilers that do not understand required members, so a compiler older than Roslyn 4.3 (before VS 2022 17.3 / .NET SDK 6.0.4xx) reports `CS0619: 'IconData.IconData()' is obsolete: 'Constructors of types with required members are not supported in this version of your compiler.'` Raising `<LangVersion>` does not help there — only a newer SDK does, and the boundary is not the C# 11 line: Roslyn 4.3 caps out at C# 10 and already consumes required members. On any compiler from 4.3 onward the table applies as written, and `<LangVersion>9</LangVersion>` is enough. The `IconData` constructor below carries that annotation, so it stays usable even on the older compilers.

You have two ways forward.

**Raise the language version.** One line, and every example in this guide then compiles as written. C# 14 needs the .NET 10 SDK; C# 9 does not.

```xml
<LangVersion>14</LangVersion>
```

**Or keep your language version** and use the spellings that work everywhere. Each option struct has a constructor taking the same settings as optional parameters, and `IconData` has one taking the icon plus the rest as optional parameters, so nothing is out of reach:

```csharp
using System;                       // for AsSpan
using SkiaSharp;
using FeatherQR;
using FeatherQR.SkiaSharp;

var data = QRCodeGenerator.Create("https://example.com".AsSpan(), QREccLevel.M,
    new QRCodeGeneratorOptions(quietZoneSize: 2, version: 5));

// IconData too. Below C# 9 this is the only way to reach a shape the FromImage
// factories cannot build, since both of them hardcode ImageIconShape.
var icon = new IconData(new ImageTextIconShape(logo, "FooBar", SKColors.Black, font), iconSizePercent: 15);
```

The constructor exists for exactly this case. On C# 9 and above, prefer the object initializer: it names only the settings it changes and does not depend on the parameter order.

### Results, options and sealing

The three symbologies now describe their results the same way, and the option objects are immutable.

**The sizing and decode results are the same kind of value on all three symbologies**: a `readonly record struct` the library builds. They keep `ToString()`, `==` and `IEquatable<T>`, so logging and comparing them is unchanged. `QRCodeCalculatedSize` is the one that moves, and it loses two things a caller may have used:

| Gone | Instead |
|---|---|
| The public constructor | The library builds it; you receive it from `TryGetRequiredBufferSize` |
| `IsValid` | The `bool` that `TryGetRequiredBufferSize` already returned |

Deconstruction goes with the positional record, so read the three members by name:

```csharp
// before
var (bufferSize, qrSize, version) = QRCodeGenerator.GetRequiredBufferSize(text, ECCLevel.M);

// after
if (!QRCodeGenerator.TryGetRequiredBufferSize(text, QREccLevel.M, out var size))
    return;
// size.BufferSize, size.Size, size.Version
```

`BufferSize` sizes the destination for the `Span<byte>` overload of `Create`, so whatever you rented or reused for it takes the new spelling and nothing else changes.

**`IconData` properties are `init`-only**, so configuration happens at construction. Code that adjusted an instance afterwards uses `with`, which is also how you vary an instance you did not build yourself (below C# 9, use the constructor described under [Older language versions](#older-language-versions)):

```csharp
// before
var icon = IconData.FromImage(logo);
icon.IconSizePercent = 20;

// after
var icon = IconData.FromImage(logo) with { IconSizePercent = 20 };
```

**`GradientOptions` is immutable and compares by value.** The constructor now copies the arrays it is given, `Colors` and `ColorPositions` are read back as `ReadOnlySpan<T>`, and two gradients with the same colours are equal — the generated record equality it replaces compared the arrays by reference, so identical gradients reported as different. `ColorPositions` is an empty span rather than `null` when the colours are evenly distributed. Constructing is unchanged, and it is still a record, so `with` still varies the direction:

```csharp
var rotated = GradientOptions.Default with { Direction = GradientDirection.BottomToTop };
```

What changes is code that *read* `Colors` / `ColorPositions` as arrays, or replaced them through `with`. The colours are chosen at construction now; `with { Colors = ... }` no longer compiles, and reading gives you a span:

```csharp
// before
var colors = options.Colors;                      // SKColor[], and the instance's own array
var evenly = options.ColorPositions is null;
var recolored = options with { Colors = [SKColors.Red, SKColors.Blue] };

// after
SKColor[] colors = options.Colors.ToArray();      // a copy, because the original is not yours
var evenly = options.ColorPositions.IsEmpty;
var recolored = new GradientOptions([SKColors.Red, SKColors.Blue], options.Direction, options.ColorPositions);
```

**Why `with` varies the direction but not the colours.** `Colors` and `ColorPositions` have to agree in length, and `with` sets one member at a time; making them `init` would let a caller build a gradient whose stops no longer match its colours and only find out when it is drawn. `Direction` carries no such pairing, so it stays `init`, and the pair is set together through the constructor, which refuses a mismatch on the spot.

The constructor takes the same shapes the properties hand back — `ReadOnlySpan<SKColor>` and `ReadOnlySpan<float>`, with an empty span meaning "distribute evenly" — so building one gradient from another translates nothing either. Arrays convert implicitly, so `new GradientOptions(myColors, direction)` is unchanged.

Two details if you passed the stops explicitly before: `null` is no longer a value you can pass (omit the argument, or pass `default` or `[]`), and an empty array now means "evenly distributed" where it used to be an `ArgumentException`. Stops are one per colour, so when the new colour count differs from the old, supply new stops or omit them — a non-empty span of a different length is still an `ArgumentException`.

A `null` colour array is the one case whose *exception type* moved. It used to be an `ArgumentNullException`; a null array converts to an empty span, so it is now the same `ArgumentException` as any other array too short to make a gradient. Code that catches `ArgumentNullException` specifically has to widen to `ArgumentException`.

**Sealed:** `QRCodeData`, `MicroQRCodeData`, `RmQRCodeData`, the three image builders, `IconData` and `GradientOptions`. None had a designed extension point. The shape hierarchies (`ModuleShape`, `FinderPatternShape`, `IconShape`) are still open and are the supported way to change how a symbol is drawn.

### Symbol position in the decode result

Additive: nothing to migrate, but new in 2.0.0. `QRCodeDecodeInfo`, `MicroQRCodeDecodeInfo` and `RmQRCodeDecodeInfo` gain `Corners`, a `SymbolCorners` holding the four outer corners of the symbol's module area (quiet zone excluded) as `ImagePoint`s in the input image's continuous pixel coordinates — (0, 0) is the top-left corner of the top-left pixel, so the values plot directly on the bitmap or luminance buffer you decoded.

```csharp
if (QRCodeImageDecoder.TryDecode(bitmap, out var text, out var info))
{
    var c = info.Corners;
    using var builder = new SKPathBuilder();
    builder.MoveTo(c.TopLeft.X, c.TopLeft.Y);
    builder.LineTo(c.TopRight.X, c.TopRight.Y);
    builder.LineTo(c.BottomRight.X, c.BottomRight.Y);
    builder.LineTo(c.BottomLeft.X, c.BottomLeft.Y);
    builder.Close();
    using var path = builder.Detach();
    canvas.DrawPath(path, outline);
}
```

Three things the contract fixes:

- **The corners are named in the symbol's frame.** `TopLeft` is the corner beside the finder that defines the symbol's top-left, wherever the camera put it. A rotated capture reports a rotated quadrilateral; a mirrored one reports the same corners in reversed winding (counter-clockwise on screen where a symbol as printed runs clockwise), which is how you can tell:

  ```csharp
  // y grows downward, so a symbol as printed gives a positive cross product.
  var mirrored = (c.TopRight.X - c.TopLeft.X) * (c.BottomLeft.Y - c.TopLeft.Y)
               - (c.TopRight.Y - c.TopLeft.Y) * (c.BottomLeft.X - c.TopLeft.X) < 0;
  ```
- **They are estimates from the fitted geometry, at 8 pixels per module or more.** Standard QR holds every corner within half a module, including under keystone. Below 8 pixels per module, read the bound in pixels rather than modules, on every symbology: the corners sit on pattern centres that resolve to about a pixel whatever the module size is, so the error stays near four pixels while the module shrinks under it (about 0.9 module at 3 pixels per module). Separately, and at any density, if the bottom-right alignment pattern cannot be located — version 1 never has one, and on any other version a smudge over a single module is enough — the fit falls back to the three finders and that corner is held to about a module and a half, while the decode still reports `Success`. Micro QR and rMQR fit from a single finder: half a module flat and at right angles, about a module for an oblique rotation or a keystone, and up to a module and a half on an rMQR symbol whose top or bottom edge is foreshortened by 2 % or more (nine versions exceed a module at 2 %, and not only the short ones).
- **They exist only for a successful image decode.** A matrix-level `TryDecode` has no image and a failed image decode located nothing worth reporting; both leave `Corners` at its default, and `Corners.IsEmpty` says so.

[samples/Dotfiles/DecodeCorners.cs](../samples/Dotfiles/DecodeCorners.cs) runs all of this over a flat, a rotated, a mirrored and a keystoned capture, and writes each one with the reported outline drawn on it.

### Module styling no longer reaches the finder patterns

**Behavior change, and the reason to upgrade if you style your codes.** `WithModuleShape(shape, sizePercent)` now styles the data modules only. Before, it also redrew the finder patterns, and that silently produced symbols nothing could read:

```csharp
// 1.x and 2.0.0-preview.2: renders, but no decoder finds this symbol. Not FeatherQR's, not ZXing's, not a phone's.
new MicroQRCodeImageBuilder("https://githu")
    .WithModuleShape(RectangleModuleShape.Default, 0.92f)
    .ToBitmap();
```

A decoder does not read a finder pattern module by module. It scans lines looking for the 1:1:3:1:1 run of dark and light that only a solid concentric pattern produces, and that is how it finds the symbol at all. Shrink the modules by two percent and the three-module dark centre becomes three runs with slivers of white between them, so the ratio exists nowhere in the image. The same is true of a Standard QR render, which ZXing also fails to read, so the symbol was at fault rather than any one decoder.

Your calls do not change, and codes that already decoded still look the same: at 100% square modules the finder was already solid, and that path is untouched, byte for byte. What changes is that a styled symbol now keeps a solid finder instead of a decorative, undetectable one.

To style the finders, ask for it and get a shape that stays detectable, now on all three builders rather than Standard QR alone:

```csharp
new MicroQRCodeImageBuilder("https://githu")
    .WithModuleShape(CircleModuleShape.Default, 0.85f)
    .WithFinderPatternShape(CircleFinderPatternShape.Default)
    .ToBitmap();
```

The built-in finder shapes reshape the concentric rings without breaking them, which is the property a decoder needs, so all four decode down to 75% module size. `SymbolRenderer.Render` and the `SKCanvas.Render` extensions gained the matching optional `finderPatternShape` parameter for Micro QR and rMQR. If you were relying on the old look, read the matrix through the `[row, col]` indexer on the data type and draw it yourself.

### `WithSize(w, h)` fits the symbol instead of stretching it

**Behavior change, and the second reason to upgrade.** A canvas whose width and height differ used to stretch a Standard QR or Micro QR symbol across both axes, so the modules stopped being square:

```csharp
// 1.x and 2.0.0-preview.2: a 900x450 image that no reader finds. Ours, ZXing's or a phone's.
new QRCodeImageBuilder("https://example.com")
    .WithSize(900, 450)
    .ToByteArray();
```

Module centres stayed correct and nothing threw, which is what made it hard to notice: the data was intact and the image looked like a QR code. What broke was detection. A decoder finds the symbol by scanning for the 1:1:3:1:1 run of dark and light through a finder pattern, and that ratio only survives on one axis once the cells are rectangular. Measured on a 33-module symbol, plain renders stopped decoding past 1.67:1 and styled ones past 1.25:1, in our decoder and in ZXing alike.

The symbol is now fitted into the canvas with one uniform module scale and centered, which is what the rectangular rMQR builder always did. Square canvases are unaffected, and so are `WithModulePixelSize` and the static helpers, none of which ever took two different values.

If you were passing different values to get a code that filled a non-square frame, you were not getting one. Draw the code into the part of the frame it belongs in:

```csharp
using var surface = SKSurface.Create(new SKImageInfo(900, 450));
surface.Canvas.Clear(SKColors.White);
surface.Canvas.Render(QRCodeGenerator.Create("https://example.com", QREccLevel.M), SKRect.Create(0, 0, 450, 450));
```

### Padding around the symbol defaults to the background color

**Behavior change.** When the canvas is larger than the symbol, the leftover is painted with `clearColor` if you set one and with `backgroundColor` otherwise. It used to be transparent when `clearColor` was omitted, which a JPEG turns into black bands and which costs the PNG its alpha-free encoding:

```csharp
// 8-pixel modules on a 400x400 canvas leave padding around the symbol.
// 1.x: that padding was transparent. 2.0.0: it is the background.
new QRCodeImageBuilder("https://example.com")
    .WithModulePixelSize(8)
    .WithSize(400, 400)
    .ToByteArray();
```

Transparent surroundings are still available, by name:

```csharp
new QRCodeImageBuilder("https://example.com")
    .WithModulePixelSize(8)
    .WithSize(400, 400)
    .WithColors(backgroundColor: SKColors.White, clearColor: SKColors.Transparent)
    .ToByteArray();
```

This also reaches rMQR, whose `WithSize` padding was transparent by default and is now the background, matching what `WithWidth` already produced.

## 1.2.0

Additive except for one decoder behaviour change. Nothing in 1.2.0 breaks source or binary compatibility; the two `[Obsolete]` warnings announce removals that land in 2.0.0.

### Kanji mode decoding

`QRCodeDecoder`, `MicroQRCodeDecoder` and `RmQRCodeDecoder` now read ISO/IEC 18004 Kanji mode segments (Standard QR all versions, Micro QR M3 / M4, rMQR all versions). Nothing about encoding changed: the generators still write Japanese text as UTF-8 in Byte mode.

- **Behaviour change, not source or binary breaking.** A symbol carrying a Kanji segment previously decoded to `QRCodeDecodeStatus.UnsupportedContent`; it now returns `Success` with the decoded text. Code that branches on `UnsupportedContent` to hand the symbol to another reader will stop taking that branch, and `TryDecode` now writes characters into a destination span where it previously wrote none.
- **The mapping is JIS X 0208, not CP932.** The two disagree on seven Shift_JIS cells (0x815F, 0x8160, 0x8161, 0x817C, 0x8191, 0x8192, 0x81CA: reverse solidus, wave dash, double vertical line, minus sign, and the cent / pound / not signs). If you compare output against a CP932-based reader such as ZXing.Net, expect those seven to differ.
- **CP932-only characters are rejected, not substituted.** Within the Kanji-mode range CP932 defines 83 characters JIS X 0208 does not: the NEC row 13 block (circled digits, roman numerals, unit ligatures). A Kanji segment containing one fails the whole symbol with the new `QRCodeDecodeStatus.UnmappedCharacter`, which is deliberately distinct from `UnsupportedContent` so a caller can route just these symbols to a CP932-capable reader.
- **ECI 20 (Shift_JIS) Byte segments are still unsupported** and still report `UnsupportedContent`.

### sizing is Try-only

**`GetRequiredBufferSize` is `[Obsolete]` on `QRCodeGenerator` and `MicroQRCodeGenerator`, and will be removed in 2.0.0.** `TryGetRequiredBufferSize` replaces it on all three generators. rMQR has no throwing sizing method at all — it was never released with one.

```csharp
// before
var size = MicroQRCodeGenerator.GetRequiredBufferSize(userInput, MicroQREccLevel.L, quietZoneSize: 2);

// after
if (!MicroQRCodeGenerator.TryGetRequiredBufferSize(userInput, MicroQREccLevel.L, out var size, new MicroQRCodeGeneratorOptions { QuietZoneSize = 2 }))
    return "Content does not fit a Micro QR symbol.";
```

**Why the throwing form is going away, rather than being kept as a convenience.** "The content does not fit" is a data-dependent answer, not a defect: Micro QR holds 5 digits at M1 and rMQR 5–150 bytes, so any caller handling input it did not choose meets an overflow as an ordinary outcome. Reporting that with an exception also costs one to two orders of magnitude more than the encode it is reporting on. This is the shape the modern BCL uses wherever a caller sizes or formats into its own buffer — `Utf8Formatter.TryFormat`, `Utf8Parser.TryParse`, `IUtf8SpanFormattable.TryFormat`, `Base64.EncodeToUtf8` — none of which has a throwing twin. `Parse` / `TryParse` was the wrong precedent to copy.

**Nothing breaks on upgrade.** The obsolete overloads still work and still behave exactly as they did in v1.1.1 — same signatures, same exceptions, same messages. You get a compiler warning (CS0618), not an error, and you have until 2.0.0 to act on it.

- **`false` means the content does not fit, and nothing else.** For Micro QR that includes content whose encoding mode the requested version or ECC level does not offer, since the text is what picks the mode.
- **Invalid arguments still throw**: an undefined ECC level, a `Version` and `Height` that disagree, a Micro QR version and ECC level that cannot be combined, a negative quiet zone, or `EciMode.Iso8859_1` declared over content that is not Latin-1. This mirrors the BCL's own configurable `Try` overloads (`int.TryParse` with a malformed `NumberStyles`, `Dictionary.TryGetValue` with a null key) and keeps a caller from reporting a configuration mistake as "content too long".
- **`true` does not promise the following `Create` call cannot throw** — only that no length-related error can. Pass the returned `Version` back through the options struct to skip the fit; a destination buffer that is too small still throws.
- **Pass `Segmentation` the same value you will encode with** (all three symbologies): the two modes can select different versions, so a buffer sized under one can be too small for the other.
- **The `options` parameter is optional on all three `TryGetRequiredBufferSize` overloads**, so `TryGetRequiredBufferSize(text, ecc, out var size)` is the shortest correct call. This is possible only because there is exactly one sizing overload per generator; the `Create` overloads still require an explicit options value on Standard QR and Micro QR, where the released parameter lists would otherwise make the call ambiguous.

### generator options

`QRCodeGenerator` and `MicroQRCodeGenerator` gained an overload of every entry point that takes an options struct instead of a parameter list. **No behaviour changed**: the parameter list overloads keep their signatures, their exceptions and their output, and the options overloads are an additional way to spell the same calls.

One resolution detail, since adding overloads can move a call: `Create…(text, eccLevel, default)` now binds to the options overload rather than to the third parameter of the parameter list, because a candidate that needs no optional-parameter substitution wins. The symbol it produces is identical, since `default` meant "all defaults" under both readings. It also *fixes* three shapes that did not compile before: `CreateQrCode(text, ecc, default)`, `CreateQrCode(span, ecc, default)` and `CreateMicroQRCode(span, ecc, default)` were ambiguous between the `bool` / `MicroQRVersion?` overload and the `Span<byte>` destination overload, and now resolve.

```csharp
// unchanged, and still the shortest correct call
var a = QRCodeGenerator.CreateQrCode("https://example.com", ECCLevel.M);

// the same thing with options
var b = QRCodeGenerator.CreateQrCode("https://example.com", ECCLevel.M, new QRCodeGeneratorOptions
{
    EciMode = EciMode.Utf8,
    QuietZoneSize = 0,
});
```

New options go on the struct from now on; the parameter lists are frozen at their current shape. `QRCodeGeneratorOptions.Default` is `default`, so an option you do not set keeps the value the parameter list would have applied — including the quiet zone, which is 4 for Standard QR and 2 for Micro QR.

The options parameter has **no default value** on these two symbologies, so pass `QRCodeGeneratorOptions.Default` explicitly if you want the defaults through that overload. Giving it one would make `CreateQrCode(text, eccLevel)` ambiguous between the two sets.

#### version ranges

`QRCodeGeneratorOptions.Version` is a `QRCodeVersionRange`, not a single version, and Micro QR has `MicroQRVersionRange`. A pinned version is the degenerate case, so there is one setting rather than two that could contradict each other.

```csharp
Version = 15                                  // pin version 15
Version = new(10, 20)                         // versions 10 to 20, both inclusive
Version = QRCodeVersionRange.AtLeast(10)      // 10 or larger
Version = configuredVersion                   // an int?; null means automatic
// omitted                                    // automatic, exactly as before
```

Bounds are **inclusive**, unlike C#'s `..` range syntax whose end is exclusive. They are validated when the range is constructed, so an impossible one is rejected before any generator sees it.

Two things behave differently from the `requestedVersion` parameter, and only through the options overloads:

- **A pinned version is checked against the content.** `Version = 1` with content that does not fit version 1 throws an `ArgumentException` naming the version, ECC level and mode. The `requestedVersion` parameter still behaves as it did, failing inside the encoder with `ArgumentOutOfRangeException (Parameter 'length')`.
- **`GetRequiredBufferSize` honours the version.** The parameter list overload has no version parameter and is unchanged; the options overload reports the version the range resolves to, and `TryGetRequiredBufferSize` returns `false` when no version in the range holds the content.

`-1` means automatic only in `QRCodeImageBuilder.WithVersion(int)`, which is unchanged. It is **not** accepted by the range type: use `null`, or leave `Version` unset. A `-1` that reached a range would otherwise silently produce an automatically sized symbol where a pinned one was asked for.

#### ecc boost

`QRCodeGeneratorOptions.BoostEccLevel` (Standard QR only, off by default) treats the requested ECC level as a minimum: the version is chosen for it as before, then the level is raised as far as that version's spare capacity allows. The symbol size never changes, and sizing is unaffected — only the emitted format information (and possibly the mask) differs. `QRCodeImageBuilder` exposes the same switch as `WithErrorCorrectionBoost()`, which pairs well with `WithIcon`.

```csharp
// Requests M as the floor; the symbol may come out as Q or H at the same size.
var data = QRCodeGenerator.CreateQrCode("https://example.com", ECCLevel.M,
    new QRCodeGeneratorOptions { BoostEccLevel = true });
```

Nothing to migrate: with the option unset, every call produces the exact symbol it produced before.

#### mask pattern pinning

`QRCodeGeneratorOptions.MaskPattern` and `MicroQRCodeGeneratorOptions.MaskPattern` (`null` = automatic) pin a specific data mask pattern instead of the automatic selection — one of eight for Standard QR (penalty-scored), one of four for Micro QR (edge-scored); the two numberings are unrelated. Any pattern is a valid symbol; pinning exists to reproduce a symbol produced elsewhere byte-for-byte (`QRCodeDecodeInfo.MaskPattern` / `MicroQRCodeDecodeInfo.MaskPattern` report the pattern a decoder saw) and to exercise scanners against every pattern. The builders expose the same setting as `WithMaskPattern(int?)`. Values outside the symbology's range are rejected when the option is set, like `Version`. rMQR has a single fixed mask, so it has no such option.

Nothing to migrate: with the option unset, every call produces the exact symbol it produced before.

#### image builders

`QRCodeImageBuilder.WithVersion` and the Micro QR equivalent gained an overload taking the range type. `WithVersion(int)` and `WithVersion(MicroQRVersion)` are unchanged, including `WithVersion(-1)` meaning automatic.

One behaviour change: `WithVersion(n)` followed by `ToByteArray()` with content too large for version *n* now throws an `ArgumentException` that names the problem, where it previously failed inside the encoder with `ArgumentOutOfRangeException (Parameter 'length')`.

### rMQR

rMQR (ISO/IEC 23941) is new in v1.2.0, so nothing here is a migration. Its generator takes an options struct rather than a parameter list, and unlike the other two it has no parameter list overloads at all:

```csharp
var data = RmQRCodeGenerator.CreateRmQRCode("https://example.com", RmQREccLevel.M);

var constrained = RmQRCodeGenerator.CreateRmQRCode("https://example.com", RmQREccLevel.M, new RmQRCodeGeneratorOptions
{
    Height = RmQRHeight.H9,
    Segmentation = RmQRSegmentation.Optimal,
});
```

Because there is nothing to collide with, the options parameter is defaulted here: `CreateRmQRCode(text, eccLevel)` is the full-defaults call.

There is no version *range* for rMQR. Its 32 versions are not totally ordered (R7x43, R9x43 and R7x59 have no min/max relation), so fit is constrained with `FitStrategy` and `Height` instead.

#### mixed-mode segmentation

Set `Segmentation = RmQRSegmentation.Optimal` (rMQR), `QRCodeSegmentation.Optimal` (Standard QR) or `MicroQRSegmentation.Optimal` (Micro QR) to let the generator split mixed content into Numeric / Alphanumeric / Byte runs. It never selects a symbol with more core modules (Standard / Micro QR: a larger version) than `Single`, it emits the `Single` bit stream verbatim whenever splitting would not shrink it, and it additionally encodes content that overflows every version in a single mode — unless the minimal-bit plan would be misread on decode (a relocated byte order mark, or on Micro QR a Latin-1 run its charset heuristic would read as UTF-8), in which case it reports "does not fit" instead of corrupting; only that one plan is considered, so a costlier safe split is not searched for. On Micro QR the plan also respects each version's mode set (M1 is Numeric-only, M2 has no Byte mode).

Two things to know before opting in: on rMQR the quiet zone adds a fixed 4 modules to each dimension, so a symbol with fewer core modules can still render onto a *larger* grid with a different aspect ratio; and on all three symbologies `TryGetRequiredBufferSize` must be passed the same `Segmentation` as the encode, or the destination buffer can be too small.

```csharp
var optimal = RmQRCodeGenerator.CreateRmQRCode(
    "https://example.com/p/1234567890123456",
    RmQREccLevel.M,
    new RmQRCodeGeneratorOptions { Segmentation = RmQRSegmentation.Optimal });   // R15x43 instead of R11x77

var standard = QRCodeGenerator.CreateQrCode(
    "https://example.com/item?id=123456789012345678901234567890",
    ECCLevel.M,
    new QRCodeGeneratorOptions { Segmentation = QRCodeSegmentation.Optimal });   // version 3 instead of 4
```

### other 1.2.0 changes

- **The `Compression` enum is `[Obsolete]`, removed in 2.0.0.** No API has ever accepted or returned it: the serialization feature it named was removed before 1.0.0 and the enum was left behind. Compress the bytes from `GetRawData()` yourself, as shown under [removed features](#-removed-features).
- **`EciModeExtensions` is no longer public.** It was public through 1.1.1 with every member internal, so nothing could be called on it and nothing can break. It is internal now rather than deprecated.
- **XML documentation ships in the package.** Editors now show summaries, parameter help and exceptions for every public type, where earlier releases showed only signatures. Nothing to migrate.

## 1.1.0

### image builder base class

`QRCodeImageBuilder` and `MicroQRCodeImageBuilder` now derive from `QRCodeImageBuilderBase<TSelf>`, which carries the options every symbology shares (`WithSize`, `WithModulePixelSize`, `WithFormat`, `WithQuietZone`, `WithColors`, `WithModuleShape`, `WithGradient`) and the complete output surface (`SaveTo`, `SaveToSvg`, `ToSvgString`, `ToByteArray`, `ToImage`, `ToBitmap`). Symbology-specific options (`WithErrorCorrection`, `WithVersion`, and on Standard QR `WithIcon`, `WithFinderPatternShape`, `WithEciMode`) stay on the concrete builders.

- **Source compatible**, fluent chains compile unchanged; the self-referential type parameter keeps every method returning the concrete builder type.
- **Binary breaking**, the shared members moved to the base class, so assemblies compiled against an older version must be recompiled (no code changes needed).
- `WithQuietZone` no longer declares a default argument value (it was 4 for Standard QR, 2 for Micro QR, a value the builder already starts with). Calling `WithQuietZone()` with no argument no longer compiles; simply remove the call.

## 1.0.0

The `QrCode` class has been **removed** in v1.0.0. It was marked obsolete in v0.9.0; use `QRCodeImageBuilder` instead.

> **Default ECC level change:** `QrCode.GenerateImage()` defaulted to `ECCLevel.L`. `QRCodeImageBuilder` defaults to `ECCLevel.M`. Pass `WithErrorCorrection(ECCLevel.L)` or the `eccLevel` argument on static methods if you need the previous behavior.

### Basic: generate to stream

**Before (0.12.x and earlier):**

```csharp
using SkiaSharp.QrCode.Image;

var qrCode = new QrCode(content, new Vector2Slim(256, 256), SKEncodedImageFormat.Png);
using var stream = File.OpenWrite(path);
qrCode.GenerateImage(stream);
```

**After (v1.0.0):**

```csharp
using SkiaSharp.QrCode.Image;

using var stream = File.OpenWrite(path);
QRCodeImageBuilder.SavePng(content, stream, ECCLevel.L, size: 256);
```

Or with the builder pattern:

```csharp
using var stream = File.OpenWrite(path);
new QRCodeImageBuilder(content)
    .WithSize(256, 256)
    .WithErrorCorrection(ECCLevel.L)
    .SaveTo(stream);
```

### Format and quality

**Before:**

```csharp
var qrCode = new QrCode(content, new Vector2Slim(512, 512), SKEncodedImageFormat.Jpeg, quality: 90);
qrCode.GenerateImage(stream);
```

**After:**

```csharp
new QRCodeImageBuilder(content)
    .WithSize(512, 512)
    .WithFormat(SKEncodedImageFormat.Jpeg, quality: 90)
    .SaveTo(stream);
```

### Get bytes instead of writing to stream

**Before:**

```csharp
using var stream = new MemoryStream();
qrCode.GenerateImage(stream);
var bytes = stream.ToArray();
```

**After:**

```csharp
var bytes = QRCodeImageBuilder.GetPngBytes(content, ECCLevel.L, size: 256);
// Or with format:
var bytes = QRCodeImageBuilder.GetImageBytes(content, SKEncodedImageFormat.Png, ECCLevel.L, size: 256);
```

### Stream position (`resetStreamPosition`)

`QrCode.GenerateImage()` could rewind a seekable stream before writing (`resetStreamPosition: true` by default). `QRCodeImageBuilder` does not reset stream position. Reset manually when needed:

```csharp
if (stream.CanSeek)
    stream.Seek(0, SeekOrigin.Begin);

QRCodeImageBuilder.SavePng(content, stream, size: 256);
```

### Overlay QR code on a base image

`QrCode` had overloads to composite a QR code onto an existing image. Use SkiaSharp canvas drawing instead:

**Before:**

```csharp
var qrCode = new QrCode(content, new Vector2Slim(qrWidth, qrHeight), SKEncodedImageFormat.Png);
using var output = File.OpenWrite(path);
qrCode.GenerateImage(output, baseImageBytes, new Vector2Slim(canvasWidth, canvasHeight), new Vector2Slim(x, y));
```

**After:**

```csharp
using var baseBitmap = SKBitmap.Decode(baseImageBytes);
var info = new SKImageInfo(canvasWidth, canvasHeight);
using var surface = SKSurface.Create(info);
var canvas = surface.Canvas;

canvas.DrawBitmap(baseBitmap, 0, 0);

using (var qrBitmap = new QRCodeImageBuilder(content)
    .WithSize(qrWidth, qrHeight)
    .ToBitmap())
{
    canvas.DrawBitmap(qrBitmap, x, y);
}

using var image = surface.Snapshot();
using var data = image.Encode(SKEncodedImageFormat.Png, 100);
using var output = File.OpenWrite(path);
data.SaveTo(output);
```

## 0.11.0

Take advantage of new capabilities:

- **Logo customization** - Now you can customize center placed logos. Library offers icons with both images and text.

For complete migration details and examples, see [Release 0.11.0](https://github.com/guitarrapc/FeatherQR/releases/tag/0.11.0).

### ⚠️ IconData.Data changed Icon from SKBitmap to IconShape

**Before (0.10.0):**

```csharp
using var bitmap = SKBitmap.Decode(File.ReadAllBytes(iconPath));

// Old code
var icon = new IconData
{
    Icon = bitmap;
    IconSizePercent = 15,
    IconBorderWidth = 10
};
```

**After (0.11.0):**

```csharp
using var bitmap = SKBitmap.Decode(File.ReadAllBytes(iconPath));

// New code Image only (Short hand)
var icon = IconData.FromImage(bitmap, iconSizePercent: 15, iconBorderWidth: 10);

// New code Image only
var icon = new IconData
{
    Icon = new ImageIconShape(bitmap),
    IconSizePercent = 15,
    IconBorderWidth = 10
};

// New approach with text
var icon = new IconData
{
    Icon = new ImageTextIconShape(bitmap, "Text", SKColors.Black, font),
    IconSizePercent = 15,
    IconBorderWidth = 10
};
```

## 0.9.0

Take advantage of new capabilities:

- **Gradient colors** - Create eye-catching QR codes with color gradients
- **Enhanced customization** - More control over module shapes and colors
- **Better performance** - Dramatically faster generation with lower memory usage

For complete migration details and examples, see [Release 0.9.0](https://github.com/guitarrapc/FeatherQR/releases/tag/0.9.0).

### 🔄 Primary API Change: `QrCode` → `QRCodeImageBuilder`

The `QrCode` class was marked **obsolete** in v0.9.0 and **removed** in v1.0.0. Replace it with `QRCodeImageBuilder`. For full migration examples (stream output, format/quality, base-image overlay, and more), see [from before v1.0.0 to v1.0.0](#100).

### 🗑️ Remove `using` Statements

`QRCodeData` and `QRCodeRenderer` are no longer `IDisposable`:

**Before (0.8.0):**
```csharp
using var qrCodeData = QRCodeGenerator.CreateQrCode("Hello", ECCLevel.L);
using var renderer = new QRCodeRenderer();
renderer.Render(...);
```

**After (0.9.0):**
```csharp
var qrCodeData = QRCodeGenerator.CreateQrCode("Hello", ECCLevel.L);
QRCodeRenderer.Render(...);  // Now a static method
```

### 📦 Update Namespace for IconData

If using icons in QR codes:

```csharp
// Add this namespace
using SkiaSharp.QrCode.Image;
```

### 🚫 Removed Features

The following features have been removed:

- `forceUtf8` parameter
- ISO-8859-2 encoding support
- Compression feature
- Kanji encoding mode

If you were using these features, you'll need to adjust your code accordingly.

- `forceUtf8`: SkiaSharp.QrCode now automatically selects UTF-8 when needed.
- ISO-8859-2 and Kanji: not supported for ENCODING; UTF-8 is recommended for most use cases. Kanji segments produced by other encoders are read since v1.2.0, see [Kanji mode decoding](#kanji-mode-decoding).
- Compression: Removed to simplify the API and improve performance. Please handle compression externally if needed. The `Compression` enum itself outlived the feature and shipped unused through 1.1.1; it is `[Obsolete]` as of 1.2.0 and will be removed in 2.0.0.

Here's an example of how to handle compression externally using [NativeCompressions](https://github.com/Cysharp/NativeCompressions):

```csharp
// compression to zstandard ...
var qrCodeData = QRCodeGenerator.CreateQrCode("Hello", ECCLevel.L);
var src = qrCodeData.GetRawData();
var size = qrCodeData.GetRawDataSize();

var maxSize = NativeCompressions.Zstandard.GetMaxCompressedLength(size);
var compressed = new byte[maxSize];
NativeCompressions.Zstandard.Compress(src, compressed, NativeCompressions.ZstandardCompressionOptions.Default);

// decompression from zstandard ...
var decompressed = NativeCompressions.Zstandard.Decompress(compressed);

// render QR code
var qr = new QRCodeData(decompressed, 4);
var pngBytes = QRCodeImageBuilder.GetPngBytes(qr, 512);
File.WriteAllBytes(path, pngBytes);
```
