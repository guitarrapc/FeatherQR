# Async API: writing and reading images without holding a thread

## Purpose

The maintainer decided on 2026-10-15 that 2.0.0 ships an async API, for callers that cannot hold a thread: web APIs, and UI apps such as WinUI. This plan holds the analysis the design starts from. The design and its phases are written after GS1 / FNC1 (Phase 6c of [featherqr-2.0.0-plan.md](featherqr-2.0.0-plan.md)), and nothing is built before then.

## Where it stands (2026-10-22)

- The analysis is done: where generation and reading block, by setting, measured on x64 (2026-10-15).
- The design is proposed as decisions with recommendations, A1 to A11 below, and waits on the maintainer's rulings (2026-10-22). MessagePack for C# v4 was read again from its source the same day, which added A11 and changed the recommendations of A5 and A10.
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

## The surface

The I/O is all in the SkiaSharp package. The core and the GS1 package take and return spans, strings and arrays, and touch no stream.

- Fourteen public methods write to a `Stream`. `SaveTo(Stream)` and `SaveToSvg(Stream)` on `SymbolImageBuilderBase<TSelf>`, shared by the three symbologies' builders, and `SavePng` and `SaveSvg` on each builder, for data and for text, twelve in all.
- The `IBufferWriter<byte>` forms (`SaveTo`, `SaveToSvg`, `WriteImage`, `WritePng`, `WriteSvg`) write synchronously into the caller's buffer, and a `PipeWriter` is one, so the caller's `FlushAsync` already makes them async.
- No public method reads a stream. The three image decoders take an `SKBitmap`, which the caller decodes from a file or a stream.

## What others chose

Three libraries that compute synchronously and do I/O around it chose the same shape, the pipe of the analysis: the I/O asynchronous, the computation whole and synchronous.

- MessagePack for C# v4, read from a local clone of its v4 branch (head b002c096) on 2026-10-22:
  - It writes with the synchronous serializer into the pipe's memory and awaits only `FlushAsync`, where its source puts backpressure and cancellation. The `Stream` forms wrap the stream with `PipeWriter.Create`, and on a failure complete the writer with the exception, so that the bytes written are discarded rather than flushed. Its own ASP.NET Core output formatter writes synchronously into `Response.BodyWriter` and awaits `FlushAsync` with `RequestAborted`, and its input formatter awaits `DeserializeAsync` on `Request.BodyReader`.
  - It reads in two passes, a scan for the end of a message and then a synchronous deserialize of the buffered message, and in one pass when the reader is already complete. Its source gives the reason it rejected true async: checking and refilling the buffer at each element costs too much, which .NET 11's runtime async may only improve somewhat, while a message is a clear block that is never very large.
  - It keeps synchronous `Stream` forms on purpose. Its source says synchronous and asynchronous I/O are different things, and sync-over-async is no substitute. The synchronous read reads the stream to its end into a pooled array. On a stream that can seek it compares the remaining length with the cap before reading and rents that size. Otherwise it starts at 4,096 bytes, doubles, and reads at most one byte past the cap, a fix made after a 100 MB cap let 128 MB in. An exposable `MemoryStream` is read in place, with no copy and no cap, and its async form checks the token up front, since nothing there awaits. On a stream that can seek, the pipe's read-ahead is given back with `Seek`.
  - `MaxBufferedMessageSize`, 64 MiB by default, caps what the `Stream` and `PipeReader` entries buffer, and a header that declares more is refused before it is buffered. The span entries are exempt, since the caller owns the buffer there. Under compression the cap counts the compressed bytes and the processor's own cap bounds the decoded size, and the documentation asks for both on untrusted input.
  - The token is observed by `ReadAsync` and `FlushAsync`, and between the messages of a batch already buffered, never inside one message.
  - Writes return `Task` and reads `ValueTask<T>`. On netstandard it references System.IO.Pipelines, and Microsoft.Bcl.AsyncInterfaces too on netstandard2.0. It has no net8.0 target, and net8.0 consumers get its netstandard2.1 build.
  - Its tests feed the pipe whole, 7 bytes at a time and a byte at a time, and read a stream that cannot seek and returns at most 7 bytes a read. None refuses synchronous I/O.
- System.Text.Json has `SerializeAsync` for a `Stream`, and for a `PipeWriter` since .NET 9, all of them returning `Task` and taking the token last with a default. On netstandard its package takes System.IO.Pipelines for the `PipeWriter` forms.
- ImageSharp's `LoadAsync` copies a stream it cannot seek into memory with `CopyToAsync`, then decodes. Its `EncodeAsync` encodes synchronously, into a memory buffer when the destination cannot seek, and copies that out with `CopyToAsync`. It passes the token into the synchronous encode.

## Decisions

| | Decision | Options and facts | Recommendation | Ruling |
|---|---|---|---|---|
| A1 | The output's shape | `SaveToAsync(Stream, CancellationToken)`, a `PipeWriter` form that writes and flushes, or both. A `PipeWriter` already works through `SaveTo(IBufferWriter<byte>)` and the caller's flush, the pattern of MessagePack v4's own ASP.NET Core output formatter | The `Stream` forms only, with the `PipeWriter` pattern in the docs. A `PipeWriter` form is additive later | |
| A2 | Dependencies | `PipeWriter` and `PipeReader` are in the net10.0 shared framework but not in net8.0's, where only ASP.NET Core's carries them, nor in netstandard, where they are the System.IO.Pipelines package (checked in the installed reference packs, 2026-10-15). `ValueTask` needs System.Threading.Tasks.Extensions on netstandard2.0. `IBufferWriter<byte>` comes with System.Memory, which the library already references there. MessagePack v4 references System.IO.Pipelines on netstandard and returns `ValueTask<T>` from its reads, whose `MemoryStream` path completes synchronously | None added. `Task` and `Task<T>` rather than `ValueTask`, for calls that take milliseconds and seldom complete synchronously. An image is one block, which `Stream.WriteAsync` and `ReadAsync` carry without a pipe | |
| A3 | The input's shape | A `Stream`, a `PipeReader` or a `ReadOnlySequence<byte>`. A one-symbol read and a read for every symbol | `DecodeAsync(Stream, CancellationToken)` on the three image decoders, reading the stream to its end asynchronously and decoding synchronously, and `DecodeSymbolsAsync(Stream, CancellationToken)` for Standard QR. This is MessagePack v4's one pass over a complete reader, with no scan for an end and no read-ahead to give back. The docs say the stream is read to its end | |
| A4 | The result type | An async method cannot have `out` parameters, so the read returns its result. One type a symbology, its text and its decode info, serves several symbols, an async read and a one-symbol result. A failed read returns a result whose info carries the status, or none | `QRCodeDecodeResult`, `MicroQRCodeDecodeResult` and `RmQRCodeDecodeResult`, each its `Text` and its `Info`. A one-symbol read returns a result in every case, its `Info.Status` saying why it failed and its `Text` empty, so the reason `TryDecode` hands back today is kept. A read for every symbol returns the symbols it read. These names settle the call for several symbols | |
| A5 | Cancellation inside the computation | The token checked only around the I/O, also between the symbols of a read for every symbol, or also between a decode's passes. MessagePack v4 observes it in its reads and flushes and between the messages of a batch, never inside one message. A read for every symbol takes up to 100 ms on x64 and longer on WebAssembly, and a client that goes leaves it running. A one-symbol read took 7.4 ms at most of the reads measured, against 51 ms for Skia to decode the same photograph, which no token stops. A render cannot be stopped inside Skia's encode, and took 32 ms at most of the renders measured | Checked before the decode starts and between the symbols of a read for every symbol, never inside one symbol's read. The one-symbol reads keep their instructions with no generic policy, and the read for every symbol pays one check a symbol. How long one symbol takes on the sheets is measured in the build, and a check between passes is added only if that is too long. A render checks the token before it starts only. The tokens are on the async calls alone, and synchronous `CancellationToken` overloads stay out of 2.0.0 | |
| A6 | WebAssembly | A Web Worker, or a decode that yields part way | Neither in the library. A decode that yields is the true async the analysis rules out, and a Web Worker is the page's choice. The Playground stays as it is | |
| A7 | An async method that only moves the computation off the thread | .NET's guidance is not to expose one, and on a server it frees no thread | None, for `Create`, `TryDecodeImage` on luminance, `ToBitmap` and the helpers that return bytes | |
| A8 | Which existing methods get an async twin | The fourteen that take a `Stream`, or the two on the builders' base only | All fourteen, so that a method that writes to a `Stream` has an `...Async` twin and the obvious synchronous call has an obvious fix | |
| A9 | The coordinates a stream read reports | The image's pixels as encoded, which `SKBitmap.Decode` gives, or turned by the EXIF orientation | The pixels as encoded, as a caller gets them today. A caller that shows the image turned maps the corners, as the Playground does | |
| A10 | A synchronous twin of the stream read | `Decode(Stream)` beside `DecodeAsync(Stream)`, and `DecodeSymbols(Stream)` beside `DecodeSymbolsAsync(Stream)`, or none. A synchronous caller has `SKBitmap.Decode(Stream)` today, so it is not stranded without them. MessagePack v4 keeps its synchronous `Stream` forms on purpose, since synchronous and asynchronous I/O are different things and sync-over-async is no substitute | Tied to A11. If the stream reads carry limits, the synchronous twins too, so that a synchronous caller gets the limits and every method that takes a `Stream` has both forms, as A8 gives the writes. Without limits, none: a synchronous caller decodes into an `SKBitmap` and calls `TryDecode`, as today | |
| A11 | Limits on a stream read | None, documented, as today when the caller decodes the image. Fixed limits on the stream reads. Or limits the caller sets. The decoders' only size limit is `int.MaxValue` pixels. A 1-bit PNG of 40,000 × 40,000 pixels holds 200,040,000 bytes of rows, which zlib at level 9 deflates to 194,447 (measured 2026-10-22), and decodes to 1.6 GB at one grey byte a pixel and 6.4 GB at four. Kestrel caps a request body at 30,000,000 bytes by default, which bounds the encoded bytes but not the pixels. MessagePack v4 caps what its stream entries buffer, refuses a header that declares more before buffering it, and exempts the span entries, whose buffer the caller owns | Fixed limits on the stream reads alone, on the encoded bytes and on the pixels, the pixels checked from the image's header before any pixel is allocated. The `SKBitmap` and luminance entries keep today's limit, so a caller with a larger image decodes it and calls `TryDecode`. The values are set in the build from the largest camera images, and a parameter for them is additive later. A stream over a limit, or one that is not an image, throws `InvalidDataException`, since `DecodeStatus` says what an image held and such a stream gives no image | |

Notes for the build, not decisions:

- A stream read rents its buffer from the pool, and whether it decodes straight into one grey channel, a quarter of the 48 MB a 12-megapixel photograph takes as colour, is measured then.
- An exposable `MemoryStream` is decoded in place, with the token checked up front. A stream that can seek has its remaining length checked against the limit before it is read, and the buffer is rented to that size. A stream that cannot seek is read to at most one byte past the limit.
- Nothing an async write renders reaches the stream when the render fails. The PNG is encoded whole before it is written, as now, and the async SVG renders into pooled memory before it is written, where the synchronous one streams. A write cancelled part way leaves part of the image behind, as in MessagePack v4.
- Whether the async writes flush the stream at the end is decided then. MessagePack v4's `Stream` forms do.
- Every `await` takes `ConfigureAwait(false)`.
- The tests use a stream that refuses synchronous reads and writes, which MessagePack v4's tests lack, and one that hands its bytes over a few at a time.

## Phases, provisional

| Phase | Work | Output |
|---|---|---|
| 0 | The decisions above | The maintainer's rulings |
| 1 | Output: the async twins A8 rules, cancellation before the render and during the write | Tests with a stream that refuses synchronous writes, the approved API listing |
| 2 | Input: the result types, `DecodeAsync` for the three symbologies and `DecodeSymbolsAsync` with the call for several symbols ([image-decode-followups-plan.md](image-decode-followups-plan.md), Phase 3), the limits A11 rules, and the synchronous twins if A10 rules them | Tests with a stream that refuses synchronous reads and one that hands bytes over a few at a time, a stream over each limit and one that is not an image refused before any pixel is allocated, the time and memory of a stream read against decoding the bitmap first |
| 3 | Cancellation between the symbols of a read for every symbol (A5), and the time one symbol takes on the sheets of 60 codes | A cancelled read for every symbol stops within one symbol, and the one-symbol reads run the instructions they ran on the report shapes |
| 4 | `docs/`, the README, the API page and the specs | The feature documented |

## What has to stay true

- The synchronous calls keep their instructions on the report shapes and allocate what they allocate now.
- No public API beyond what the design rules, and no dependency added to a target without a decision (A2).
- No switch in `src` that only a test or a measurement uses.

## Documents to change

- [qrcode-symbologies.md](../specs/qrcode-symbologies.md) (a scope row and the public API direction), the renderer and decoder specs, `docs/`, the README and the API page.

## Progress log

### The analysis (2026-10-15)

Done, at the maintainer's request. The premise was that an async API is needed where a thread cannot be held, in web APIs and in WinUI. The measurement narrows it: on the web what blocks is I/O, which async frees, and on a UI thread it is computation, which only moving the work off the thread frees. Probes ran in scratch worktrees and nothing in the repository changed.

### The design proposed (2026-10-22)

Done, once GS1 / FNC1 had shipped. The public surface was taken stock of: the I/O is all in the SkiaSharp package, fourteen methods that write to a `Stream` and none that reads one. Three libraries that compute synchronously and do I/O around it were read for the shape they chose, from their documentation and their source: MessagePack for C# v4, System.Text.Json and ImageSharp. All three keep the computation whole and synchronous and make only the I/O asynchronous. The questions of the analysis became decisions A1 to A10, each with a recommendation, and the phases are sketched.

### MessagePack v4 read from its source (2026-10-22)

Done, at the maintainer's request, from a local clone of the v4 branch (head b002c096): the serializer's `Stream`, async write and async read entries, its options, its ASP.NET Core formatters and its async tests. The source confirms the pipe on both sides and states why true async was rejected. It also showed three things the proposal lacked. A stream entry caps what it buffers, since taking the caller's I/O over takes over its limits too. The synchronous `Stream` forms are kept on purpose. The token is observed between messages, never inside one. A11 was added, the recommendations of A5 and A10 changed, A1 to A3 gained its facts, and the notes for the build gained its handling of failed writes and of streams it can read in place. The size of a deflated 1-bit PNG in A11 was measured with Node's zlib in the scratch directory, and nothing in the repository changed but this plan.
