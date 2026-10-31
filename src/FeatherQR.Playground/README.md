# FeatherQR Playground

[Open the Playground](https://guitarrapc.github.io/FeatherQR/) to create and read QR codes in your browser.
It runs FeatherQR and SkiaSharp through WebAssembly. Text and images are processed locally, without uploading them to a server.

## Using the Playground

- **Create codes:** choose QR Code, Micro QR or rMQR, then adjust colors, shapes and error correction. Download the result as PNG or SVG.
- **Check readability:** each generated image is decoded back as a self-check. Use **Decode an image** to read an existing image.
- **Simulate a photograph:** **Photograph the code** applies tilt, blur, noise, lighting and other camera effects. **Find the limit** increases one effect to explore when decoding fails.
- **Measure throughput:** **Performance benchmark** runs either encoding alone or the full encode, render and PNG pipeline. Results measure the WebAssembly runtime in your browser, not native .NET performance.
- **Browse the API:** the [API reference](https://guitarrapc.github.io/FeatherQR/api/index.html) lists public types and members with their documentation.

Gradients, shapes and logos can reduce readability. Check the result with the scanners you intend to use, especially before printing.

### Sharing a design

**Share link** stores the content and settings in the URL. Anyone with the link can restore them.
Uploaded logo images are not included; shared designs use the built-in logo in their place.

Very long content may exceed the share-link limit. An invalid or unsupported link shows a message and opens the Playground with defaults.

## Local development

Run the following from the repository root with the .NET 10 SDK and Python 3 installed:

```bash
dotnet workload install wasm-tools
dotnet publish src/FeatherQR.Playground/FeatherQR.Playground.csproj -c Debug -p:PlaygroundSoftFingerprint=true -o publish/playground
python3 -m http.server 8000 --bind 127.0.0.1 --directory publish/playground/wwwroot
```

Open `http://localhost:8000`. Rendering requires **publish**: `dotnet build` and `dotnet run` do not link the native SkiaSharp library.

- Publish to a **clean output directory** each time to avoid stale fingerprinted assets.
- Keep `PlaygroundSoftFingerprint=true` when serving from a plain static server or GitHub Pages.
- Do not add `-r browser-wasm`; the WebAssembly SDK supplies it. Passing it globally also affects the multi-targeted library projects.

For a production build with AOT and trimming, use a separate clean directory:

```bash
dotnet publish src/FeatherQR.Playground/FeatherQR.Playground.csproj -c Release -p:PlaygroundSoftFingerprint=true -o publish/playground-release
```

If a first publish after clearing `obj/` fails at runtime with `DllNotFoundException: libSkiaSharp`, check for duplicate `dotnet.native.*.wasm` files in the output. Clear the publish directory and publish again while retaining `obj/`.

### Verification

Run the JavaScript tests with Node.js 22 or later:

```bash
node --experimental-default-type=module --test tests/FeatherQR.Playground.Tests/*.test.mjs
```

Then verify generation, decode preview, the benchmark panel and a share-link round trip in the published app.
The `build-playground` job in [build.yaml](../../.github/workflows/build.yaml) runs the JavaScript tests and publishes Release without AOT.

### Updating the API reference

The generated `wwwroot/api/index.html` is committed. Regenerate it after public API changes:

```bash
dotnet run tools/public_api.cs -- --html -o src/FeatherQR.Playground/wwwroot/api/index.html
```

Use `api/index.html` for links; some development servers do not resolve `api/` to the index page.
The release workflow regenerates the page with `--source-links` to link declarations to the published commit. Omit that option locally to avoid links to unpublished commits.

### State and share-link format

- [state-controls.js](wwwroot/state-controls.js) collects and applies control state, synchronizes visibility and converts presets.
- [share-state.js](wwwroot/share-state.js) validates the shared state before it reaches the controls.
- [share-payload.js](wwwroot/share-payload.js) encodes and decodes URL payloads.
- [main.js](wwwroot/main.js) connects events, rendering hooks and WebAssembly.

Links use `#1.<base64url>` for deflate-raw UTF-8 JSON or `#0.<base64url>` for uncompressed UTF-8 JSON.
The JSON schema version is `v: 1`; versionless links remain supported. Missing fields retain initial defaults, unknown fields are ignored, and invalid known fields or unsupported versions are rejected.
The limits are 8,192 characters for generated URLs, 16,384 characters for incoming hashes and 256 KiB for decoded JSON.

## Deployment

The [release workflow](../../.github/workflows/release.yaml) publishes to GitHub Pages on release tag pushes.
Configure Pages with **Source: GitHub Actions**. To redeploy a tag, rerun its release workflow.
