# BlazorWasm sample

Blazor WebAssembly sample for FeatherQR (the SkiaSharp.QrCode package). The UI mirrors [FeatherQR.Playground](../../src/FeatherQR.Playground) (the pure-WASM [GitHub Pages playground](https://guitarrapc.github.io/FeatherQR/)), but demonstrates the Blazor-specific integration:

- **Live preview** renders directly onto a [SkiaSharp.Views.Blazor](https://www.nuget.org/packages/SkiaSharp.Views.Blazor) `SKCanvasView` with `SymbolRenderer`, every control change repaints the surface.
- **Download PNG / SVG** exports through the symbology's image builder at the selected export size (`QRCodeImageBuilder` for Standard QR, `MicroQRCodeImageBuilder` for Micro QR).
- [QrOptions.cs](QrOptions.cs) holds the page state; [QrImageFactory.cs](QrImageFactory.cs) translates it into library calls shared by both paths.

## Symbology (Standard QR and Micro QR)

Like the Playground, the **Symbology** control switches between Standard QR (versions 1–40) and Micro QR (M1–M4). Changing symbology:

- rebuilds the **Error correction** and **Version** option lists for the active symbology;
- resets version to **Auto**;
- sets the quiet zone to the specification default (**4** modules for Standard QR, **2** for Micro QR).

**Micro QR** uses `MicroQRCodeGenerator` / `MicroQRCodeImageBuilder` under the hood. The page hides **Finder pattern** and **Logo** panels (single finder, no ECC headroom for overlays — same as the Playground). Module shape, colors, and gradients remain available.

Capacity and version/ECC constraints are documented in the main README [Micro QR FAQ](../../README.md#does-it-support-micro-qr-or-rmqr).

## Run

SkiaSharp.Views.Blazor links the native Skia library into the WASM binary, which requires the wasm-tools workload:

```bash
# once
dotnet workload install wasm-tools

dotnet run --project samples/BlazorWasm
```
