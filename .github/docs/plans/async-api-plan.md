# Async API: writing and reading images without holding a thread

## Purpose

The maintainer decided on 2026-10-15 that 2.0.0 ships an async API, for callers that cannot hold a thread: web APIs, and UI apps such as WinUI. This plan holds the analysis the design starts from. The design and its phases are written after GS1 / FNC1 (Phase 6c of [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md)), and nothing is built before then.

## Where it stands (2026-10-15)

- The analysis is done: where generation and reading block, by setting, measured on x64.
- Not designed: the API's shape, the result type, cancellation. The questions are listed below.
- The call for several symbols in one image (Phase 3 of [image-decode-followups-plan.md](image-decode-followups-plan.md)) returns the result type an async read must return, since an async method cannot have `out` parameters. Its names are settled with this plan.

## The analysis

### What async can free

An `await` frees the thread only while it waits on I/O. While the library computes, the thread is busy whatever the call looks like. In this library the I/O is writing an encoded image, through the builders' `SaveTo(Stream)` and `SaveTo(IBufferWriter<byte>)` and the static `SavePng` family, and getting an image to read. The caller does the second today: the core reads a span of luminance and the SkiaSharp package an `SKBitmap`. Generating, rendering and reading are computation.

### What the calls cost

Measured on 2026-10-15 on x64 with AVX2 and the .NET 10 JIT, the median of repeated calls after warming:

| Call | Time |
|---|---|
| Generate a 55-character URL, version chosen | 0.007 ms |
| Generate version 40, 4,000 characters | 0.062 ms |
| PNG of the URL, 512 px | 4.6 ms |
| PNG of version 40, 1,024 px | 32 ms |
| SVG of the URL / of version 40 | 0.65 ms / 18.5 ms |
| Read a 512 px render of the URL (`TryDecode(SKBitmap)`) | 0.16 ms |
| JPEG to `SKBitmap`, a 12-megapixel photograph (`lots/image007` of BoofCV's set, 4032 × 3024) | 51 ms |
| Read that photograph, one symbol | 6.3 to 7.4 ms |
| Read a 1080p frame of luminance with a code (a BoofCV photograph of one code scaled to 1,920 px wide) | 3.0 ms |
| Read a 1080p frame without a code (smoothed noise) | 7.0 ms |
| Read a 1080p frame of one level | 0.85 ms |
| Read every symbol (the internal entry): BoofCV's photographs on average / the seven sheets of 60 codes (2026-10-14) | 6.5 ms / 33 to 100 ms |

Most of the time is Skia encoding and decoding the image, not the symbol. A PNG of a URL takes 4.6 ms where the URL's symbol takes 0.007, and a 12-megapixel photograph takes 51 ms to become a bitmap and 6 to 7 to read. A camera frame without a code costs more than one with a code, since the read runs every pass before it gives up.

### Generation, by setting

| Setting | What blocks | Whether async helps |
|---|---|---|
| A web API that returns a PNG or an SVG (tickets, two-factor enrolment, payments) | Not the computation, 0.6 to 5 ms for a URL. `SavePng(content, Response.Body)` writes synchronously, and Kestrel refuses synchronous I/O by default, so the obvious call throws. `GetPngBytes` returned through `Results.File`, or `SaveTo(Response.BodyWriter)` followed by an `await FlushAsync()` the caller adds, work today but are not what a caller finds first | Yes, for the write. The clearest demand of all |
| Blazor Server, Azure Functions | As a web API | As a web API |
| WinUI, WPF, MAUI | A PNG of a URL is fine on the UI thread. A 1,024 px PNG of version 40 (32 ms) takes more than a 16 ms frame | The application's `Task.Run`. An async method in the library would hold the same `Task.Run` |
| Blazor WebAssembly and the Playground | One thread, so a `Task.Run` blocks the page too | Not by async I/O. A Web Worker does, or work that yields part way |
| Writing files in a batch | Little | Little |

In a web app an async method that only runs the work through `Task.Run` makes things worse: it moves the work from one pool thread to another and frees none. What web and UI callers need from the library is async I/O, and calls a caller can move off its own thread easily. They do not need an async form of the computation.

### Reading, by setting

Unlike generation, the library reads no stream today. The core takes luminance and the SkiaSharp package a bitmap, and getting the image is the caller's I/O. An async read means the library takes that I/O over.

| Setting | What blocks | Whether async helps |
|---|---|---|
| A web API that reads an uploaded photograph | `SKBitmap.Decode(Request.Body)` reads synchronously and throws by default. A 12-megapixel photograph keeps the thread about 60 ms (51 to decode the JPEG, 6 to 7 to read), and up to 100 ms on a sheet read for every symbol, whatever the call looks like | Yes, for reading the body, which a caller does today with `await Request.Body.CopyToAsync(...)` first. Cancellation would also let a long read stop when the client goes |
| Scanning from a camera (WinUI, WPF, MAUI, phones) | Frames usually arrive on a capture thread, not the UI thread. A 1080p frame takes 1 to 7 ms against 33 ms a frame at 30 frames a second, with less room on a phone, and most frames hold no code | No, there is no I/O. What helps is the allocation-free span call, which exists, the time a frame takes, and perhaps dropping a stale read when a new frame comes |
| Opening or pasting an image in a desktop app | Reading the file, decoding a large JPEG (51 ms) and then the read take more than a frame | The application's `Task.Run` |
| Blazor WebAssembly and the Playground | The page stops while it reads. The Playground reads on the page's thread through a synchronous `[JSExport]` | Not by async I/O. The browser can decode the JPEG off the thread (`createImageBitmap`), but the read itself needs a Web Worker or work that yields |
| Reading files in a batch | Little | The caller's parallelism |

### True async or a pipe

MessagePack for C# v4 settled the same question for deserialization ([pull request 2291](https://github.com/MessagePack-CSharp/MessagePack-CSharp/pull/2291), closed on 2026-08-20). It rejected a decoder that awaits a refill in the middle of a value, which costs too much. It reads a `PipeReader` until a message is complete, then deserializes the complete buffer synchronously, and skips the first step when the reader is already complete. This library fits that shape better still:

- Output has nothing to await part way. Skia encodes a PNG whole and synchronously, so the bytes exist only once the encode is done. Rendering into the writer's buffer synchronously and then flushing asynchronously is the whole of it. `SaveTo(IBufferWriter<byte>)` already writes into a `PipeWriter`, so what is missing is the flush and an asynchronous write to a `Stream`.
- Input decodes only whole, and the read walks the whole image many times. Reading until the input is complete and then reading the symbol synchronously is the only shape that makes sense.
- Where true async would matter is not I/O: stopping a long read, and giving the thread back part way on WebAssembly's single thread. Both mean checking a token, or yielding, between the passes.

## Direction, provisional

- Output: async writes on the builders and the static `Save*` family, to a `Stream` and to a pipe, with rendering kept synchronous.
- Input: an async read in the SkiaSharp package that reads a stream to its end and then reads synchronously.
- No async form of a computation alone (`Create`, `TryDecodeImage` on luminance). It would only wrap `Task.Run`, which a web app must not pay and a UI app can write.
- A result type, since an async read cannot use `out`. It is the type the call for several symbols returns.

## Questions

| | Question | Notes |
|---|---|---|
| A1 | The output's shape: `SaveToAsync(Stream, CancellationToken)`, an overload that takes a `PipeWriter` and flushes it, or both | A `PipeWriter` already works through `SaveTo(IBufferWriter<byte>)` with the caller's flush |
| A2 | Dependencies | `PipeWriter` and `PipeReader` are in the net10.0 shared framework but not in net8.0's, where only ASP.NET Core's carries them, nor in netstandard, where they are the System.IO.Pipelines package (checked in the installed reference packs, 2026-10-15). `ValueTask` needs System.Threading.Tasks.Extensions on netstandard2.0. `IBufferWriter<byte>` comes with System.Memory, which the library already references there |
| A3 | The input's shape | `Stream`, `PipeReader` or `ReadOnlySequence<byte>`. One symbol and every symbol. How the reason for a failure comes back, which `info.Status` carries today |
| A4 | The result type | One type for several symbols, for an async read and for a one-symbol call that returns its result. Its name settles the names of the call for several symbols |
| A5 | Cancellation and yielding inside the computation | Checked between passes, and only if the one-symbol calls keep their instructions on the report shapes. [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md) has `CancellationToken` decode overloads out of 2.0.0, so this reopens that row |
| A6 | WebAssembly | A Web Worker or yielding. The Playground is the only WebAssembly caller in the repository |
| A7 | An async method that only moves the computation off the thread, for UI callers' convenience | .NET's guidance is not to expose one. A caller writes the `Task.Run` |

## What has to stay true

- The synchronous calls keep their instructions on the report shapes and allocate what they allocate now.
- No public API beyond what the design rules, and no dependency added to a target without a decision (A2).
- No switch in `src` that only a test or a measurement uses.

## Documents to change

- [qrcode-symbologies.md](../specs/qrcode-symbologies.md) (a scope row and the public API direction), the renderer and decoder specs, `docs/`, the README and the API page.

## Progress log

### The analysis (2026-10-15)

Done, at the maintainer's request. The premise was that an async API is needed where a thread cannot be held, in web APIs and in WinUI. The measurement narrows it: on the web what blocks is I/O, which async frees, and on a UI thread it is computation, which only moving the work off the thread frees. Probes ran in scratch worktrees and nothing in the repository changed.
