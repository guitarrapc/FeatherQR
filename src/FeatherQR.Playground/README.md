# FeatherQR.Playground

Browser playground for FeatherQR, published to GitHub Pages: https://guitarrapc.github.io/FeatherQR/

A static `Microsoft.NET.Sdk.WebAssembly` app (no Blazor, no server). The page script loads the
.NET runtime via `_framework/dotnet.js` and calls `[JSExport]` methods on `QrInterop` to render
QR codes with the real SkiaSharp native library compiled to WebAssembly.

## Local development

The SkiaSharp native library is linked into `dotnet.native.wasm` **only on publish**
(see the `_IsPublishing` condition in the csproj). This keeps solution builds fast and free of
the Emscripten toolchain, but it means `dotnet run`/`dotnet build` outputs cannot render —
always go through `dotnet publish`:

```bash
# once
dotnet workload install wasm-tools

# fast inner loop (no AOT, no trimming)
dotnet publish src/FeatherQR.Playground/FeatherQR.Playground.csproj -c Debug -p:PlaygroundSoftFingerprint=true -o publish/playground

# serve the static output (any static file server works)
dotnet serve -d publish/playground/wwwroot   # or: python -m http.server -d publish/playground/wwwroot
```

`-p:PlaygroundSoftFingerprint=true` emits both fingerprinted and plain filenames so the page
works on hosts without import-map rewriting (GitHub Pages, plain static servers).

## Public API page (`/api/index.html`)

The **API** link in the header opens a filterable listing of every public type, with the doc
comments for each, generated from the built assembly by `tools/public_api.cs`.

Link to it as `api/index.html`, never `api/`. The dev server behind F5 and `dotnet run` has no
default document for a subdirectory and falls back to the Playground page instead, so `api/`
answers 200 with the wrong page rather than a visible 404. GitHub Pages resolves either form.

`wwwroot/api/index.html` **is committed**, so it is simply there: F5 in Visual Studio, `dotnet
run`, and `dotnet publish` all serve it with no extra step, on a fresh clone included. Regenerate
it after changing the public API:

```bash
dotnet run tools/public_api.cs -- --html -o src/FeatherQR.Playground/wwwroot/api/index.html
```

The release workflow runs the same command before publishing, so the Pages site is always current
even if the committed copy has drifted. Drop `--html -o ...` for the plain-text listing on stdout.

### Source links

Passing `--source-links` turns every type and member name into a link to its declaration on
GitHub, line anchor included. The file and line come from the sequence points in the PDB, and the
repository and commit from the SourceLink map the .NET SDK embeds there, so nothing needs to be
configured and no package is added.

**Only the release workflow passes it.** SourceLink pins the commit that was built: a page
generated locally would link to whatever HEAD was at the time, which is often a commit that has
never been pushed, and every link would 404. The header shows the commit the links point at.

Members without IL have no sequence points, so fields and enum values are never links. A type has
none of its own either, and links to the file its first member is in, without a line anchor.

Committing a generated file is deliberate. Generating it during the build was tried and does not
work: `wwwroot` is globbed when the project is evaluated, so a file written by a target is invisible
to the static web asset pipeline, and the target that would write it runs in more than one project
instance, which starts two generators at once over the same output. The listing is sorted and
stable, so the committed file also gives every PR a readable diff of the public surface, without a
CI gate that fails on intentional changes.

The doc text is read from the XML documentation file the library ships, so the page shows exactly
what a consumer sees in IntelliSense. `<summary>` is always visible; `<remarks>` folds behind a
**Notes** toggle, because it usually runs several times longer than the summary it explains and
would otherwise bury the signatures. Filtering opens only the Notes it matched inside.

Nothing validates the listing and no build fails when the surface changes. Breaking changes are
caught by package validation against the released baseline, which is the only surface check worth
failing a build over.

Publish to a **clean output directory** (delete it between publishes). Re-publishing into the
same `-o` directory leaves the previous build's fingerprinted files behind; the
`_CopyDotnetJsFallback` target detects this and fails with an explicit "publish to a clean
directory" error (two `dotnet.*.js` entry files cannot both be the fallback).

After deleting `obj/` (or on a fresh clone), the **first** publish can emit BOTH the build-phase
and the relinked publish-phase native bundles, two `dotnet.native.*.wasm` files, and the
`dotnet.js` fallback may bind the non-relinked one, which fails at runtime with
`DllNotFoundException: libSkiaSharp`. Delete the output directory and publish a second time
(warm `obj/`): the output converges to the single relinked bundle.

The production build adds AOT + full trimming:

```bash
dotnet publish src/FeatherQR.Playground/FeatherQR.Playground.csproj -c Release -p:PlaygroundSoftFingerprint=true -o publish/playground
```

Do not pass `-r browser-wasm`: the WebAssembly SDK already defaults to it, and as a CLI global
property it propagates to the multi-targeted library reference, which would then demand the
`wasm-tools-net8` workload.

## Deploy

The `build-playground` / `deploy-playground` jobs in
[.github/workflows/release.yaml](../../.github/workflows/release.yaml) publish to the
`github-pages` environment as part of every release tag push (`X.Y.Z`). To redeploy a tag,
re-run its release workflow run. GitHub Pages must be configured with
**Source: GitHub Actions** in the repository settings.

## Performance benchmark

The "Performance benchmark" panel generates many unique codes sequentially (content suffixed
with `#1`, `#2`, …) to demo library throughput under load, in two modes:

- **Encode only**, the zero-allocation `Create(text, ecc, Span<byte>)` overload in a
  tight loop (pooled text/module buffers, no per-iteration allocation).
- **Full pipeline**, encode + Skia render + PNG encode with the current visual settings.

The page script chains `BenchmarkBatch` calls sized to ~150ms of wall clock, so progress
renders and Cancel stays responsive while everything runs single-threaded on the WASM runtime.

## Photograph the code

The "Photograph the code" panel prints the generated image on a card and photographs it, then reads the
photograph with the decoder of its symbology, to show where a design stops reading. The sliders set the
module size, the tilt and its direction, the rotation, a barrel lens, a curved card, blur, sensor noise,
uneven light (a ramp or a shadow edge), a cluttered background and JPEG compression. "Find the limit" raises
one effect step by step over the other settings, a few seeds a step, and reports the last step every seed
read, the first miss and the step from which nothing reads.

The camera is `tests/FeatherQR.Tests/Shared/CameraRenderer.cs` and the light `UnevenLightingRenderer.cs`,
linked into this project: the renderers the image decode sweep and the decode tests photograph symbols with.
The card's face here is the generated image (`PhotoSimulation.cs`), where the sweep's is the black and white
matrix, so a change to either renderer changes both. The photograph is held near 1,200 px a side by capping
the module size, since the camera traces nine rays a pixel on the page's thread.

## Share links

The Share button stores the full playground state (compressed with `CompressionStream`,
base64url) in the URL hash, nothing is sent to a server. Uploaded logo images are excluded
from share links; the link falls back to the built-in logo.

The wire format remains `#1.<base64url>` for deflate-raw UTF-8 JSON and `#0.<base64url>` for
plain UTF-8 JSON. The prefix selects the encoding, while the JSON's `v: 1` selects the state
schema. Versionless links are read as v1 for compatibility. Missing fields keep the current
defaults and unknown fields are ignored, but unsupported schema versions and invalid known
fields are refused before controls change. A failed restore shows a message and starts the
Playground with defaults instead of stopping WebAssembly initialization.

The decoder limits the hash to 16,384 characters and the decompressed JSON to 256 KiB of
UTF-8. The Share button also limits the complete URL to 8,192 characters. A different binary
or query-string encoding would still require the same state validation, so the existing
format is retained to keep old links readable.

State collection, control application, derived visibility and preset conversion live in
`wwwroot/state-controls.js`. The host in `main.js` supplies select and gradient rendering
hooks and connects events to WebAssembly. This keeps state handling testable without
starting the runtime.

Run the dependency-free codec, schema and control-state regression tests with Node.js 22 or later:

```bash
node --experimental-default-type=module --test tests/FeatherQR.Playground.Tests/*.test.mjs
```

The `build-playground` CI job runs these tests before publishing.
