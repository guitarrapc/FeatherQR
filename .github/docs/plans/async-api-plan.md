# Async API: writing and reading images without holding a thread

## Purpose

The maintainer decided on 2026-10-15 that 2.0.0 ships an async API, for callers that cannot hold a thread: web APIs, and UI apps such as WinUI. This plan holds its goals, the analysis they come from, and the design as the decisions the maintainer ruled.

## Where it stands (2026-10-25)

- The analysis is done: where generation and reading block, by setting, measured on x64 (2026-10-15).
- The goals are set (2026-10-25), from the decisions as ruled.
- The design is decided: A1 to A14 below, all ruled on 2026-10-25.
- Phase 1, the output, is done (2026-10-25): the fourteen async writes.
- Phase 2, the input, is done (2026-10-25): the result types, the stream reads with their limits and their synchronous twins, and the call for several symbols.
- Phase 3, cancellation between symbols, is done (2026-10-25).
- Phase 4, the docs, is done (2026-10-25). Every phase is done. The plan folds into the specs at the 2.0.0 freeze.
- A review of the branch (2026-10-26) fixed what it found in the code, the tests and the docs, and left three design questions to the maintainer.
- The call for several symbols in one image (Phase 3 of [image-decode-followups-plan.md](image-decode-followups-plan.md)) returns the result type an async read must return, since an async method cannot have `out` parameters. That type is ruled (A4), and the call's names are settled in that Phase 3.

## Goals

Set on 2026-10-25, from the decisions below as ruled.

### Why

- A web server refuses synchronous I/O by default. Under Kestrel `SavePng(content, Response.Body)` and `SKBitmap.Decode(Request.Body)` throw, and they are the calls a caller writes first. The ways around them, `GetPngBytes` returned through `Results.File`, `SaveTo(Response.BodyWriter)` with a flush the caller adds, or `CopyToAsync` into memory before decoding, are not what a caller finds.
- A synchronous write or read holds a pool thread for as long as the client takes to receive or send the image.
- A client that goes should stop a long read. A read for every symbol takes up to 100 ms on x64.
- 2.0.0 is where it fits. An async read cannot have `out` parameters and returns a result type, the type the call for several symbols returns, so their names are settled together before the freeze.

A UI awaits an async call on its own thread as it is. The call frees the thread while it waits on I/O, and moves off the thread before it renders or decodes (A14). The calls with no I/O stay synchronous, and a UI moves them off its thread with `Task.Run` (A7).

### What it does

- An `...Async` twin for each of the fourteen methods that write to a `Stream` (A8). The image is rendered and encoded synchronously, and the write alone is asynchronous.
- `DecodeAsync(Stream)` on the three image decoders and `DecodeSymbolsAsync(Stream)` for Standard QR (A3). The stream is read to its end asynchronously, then decoded and read synchronously.
- A result type a symbology, its text, its decode info and whether it read (A4).
- Fixed limits on a stream read, 256 MiB of encoded bytes and 268,435,456 pixels (16,384 × 16,384) declared by its header (A11), and the synchronous twins of the stream reads, which carry the limits too (A10).
- The token checked before a render or a decode starts, during the I/O, and between the symbols of a read for every symbol (A5).
- An async call made on a UI thread, under a `SynchronizationContext`, moves to the pool before it renders or decodes, so that a UI awaits it as it is. An ASP.NET Core request has no context and takes no extra hop (A14).
- The docs show a `PipeWriter`, such as `Response.BodyWriter`, written through `SaveTo(IBufferWriter<byte>)` and flushed by the caller (A1).

### What it does not

- True async: no `await` in the middle of a computation, and no decode that gives the thread back part way (A6).
- Async methods that only move the computation off the thread, for `Create`, `TryDecodeImage` on luminance, `ToBitmap` and the helpers that return bytes (A7).
- `PipeWriter` and `PipeReader` forms, a new package, or `ValueTask` (A1 to A3).
- A token or a time budget on the synchronous calls, in 2.0.0 (A5).
- A stop inside one symbol's read or inside Skia's encode (A5).
- A Web Worker, or a decode that yields, on WebAssembly. The Playground stays as it is (A6).
- The EXIF orientation. A stream read reports the pixels as encoded (A9).
- Async in the core and GS1 packages, which do no I/O, and for camera frames, which come as luminance.
- An `IAsyncEnumerable` read for every symbol (A12).

### The API

The calls the recommendations give, in the SkiaSharp package unless marked.

```csharp
// FeatherQR: a result type a symbology, shaped as the *DecodeInfo types (readonly record struct, internal constructor, get-only)
public readonly record struct QRCodeDecodeResult       // string Text, QRCodeDecodeInfo Info, bool Success
public readonly record struct MicroQRCodeDecodeResult  // string Text, MicroQRCodeDecodeInfo Info, bool Success
public readonly record struct RmQRCodeDecodeResult     // string Text, RmQRCodeDecodeInfo Info, bool Success

// Writing. SymbolImageBuilderBase<TSelf>:
public Task SaveToAsync(Stream output, CancellationToken cancellationToken = default)
public Task SaveToSvgAsync(Stream output, CancellationToken cancellationToken = default)

// QRCodeImageBuilder. MicroQRCodeImageBuilder and RmQRCodeImageBuilder have the same four with their level and default size.
public static Task SavePngAsync(string content, Stream output, QREccLevel eccLevel = QREccLevel.M, int size = 512, CancellationToken cancellationToken = default)
public static Task SavePngAsync(QRCodeData qrCodeData, Stream output, int size = 512, CancellationToken cancellationToken = default)
public static Task SaveSvgAsync(string content, Stream output, QREccLevel eccLevel = QREccLevel.M, int size = 512, CancellationToken cancellationToken = default)
public static Task SaveSvgAsync(QRCodeData qrCodeData, Stream output, int size = 512, CancellationToken cancellationToken = default)

// Reading. QRCodeImageDecoder, in its extension(QRCodeDecoder) block, so that QRCodeDecoder.DecodeAsync(...) works on C# 14:
public static Task<QRCodeDecodeResult> DecodeAsync(Stream image, CancellationToken cancellationToken = default)
public static Task<QRCodeDecodeResult[]> DecodeSymbolsAsync(Stream image, CancellationToken cancellationToken = default)
// MicroQRCodeImageDecoder and RmQRCodeImageDecoder, in theirs:
public static Task<MicroQRCodeDecodeResult> DecodeAsync(Stream image, CancellationToken cancellationToken = default)
public static Task<RmQRCodeDecodeResult> DecodeAsync(Stream image, CancellationToken cancellationToken = default)

// The synchronous twins (A10):
public static QRCodeDecodeResult Decode(Stream image)
public static QRCodeDecodeResult[] DecodeSymbols(Stream image)
public static MicroQRCodeDecodeResult Decode(Stream image)
public static RmQRCodeDecodeResult Decode(Stream image)
```

- One symbol from memory is read with `TryDecode` and `TryDecodeImage`, as now (A13). The calls above read a stream or read several symbols, and return the result type.
- A one-symbol read returns a result in every case. On a failure `Info.Status` says why and `Text` is empty. A read for every symbol returns the symbols it read, an empty array when none.
- A caller tells a read's success by `Success`, on the synchronous and the async reads alike. It is false for a failed read and for a default value, such as an entry of a room the span form left unwritten.
- A stream over a limit, or one that is not an image, throws `InvalidDataException`. A null stream throws `ArgumentNullException`, and a stream that cannot write or read `ArgumentException`, as `SaveTo(Stream)` does now.
- The calls for several symbols on a bitmap and on luminance, `DecodeSymbols(SKBitmap)` and the core's array and span forms, are Phase 3 of [image-decode-followups-plan.md](image-decode-followups-plan.md) and return the same `QRCodeDecodeResult`.

### How a caller writes

Each write as a caller would write it. Micro QR and rMQR write the same way through `MicroQRCodeImageBuilder` and `RmQRCodeImageBuilder`.

A web API that returns a PNG (`SavePngAsync`). The token is the request's, and the response is written without holding a thread:

```csharp
app.MapGet("/qr", async (string text, HttpResponse response, CancellationToken cancellationToken) =>
{
    response.ContentType = "image/png";
    await QRCodeImageBuilder.SavePngAsync(text, response.Body, cancellationToken: cancellationToken);
});
```

The same as an SVG (`SaveSvgAsync`):

```csharp
app.MapGet("/qr.svg", async (string text, HttpResponse response, CancellationToken cancellationToken) =>
{
    response.ContentType = "image/svg+xml";
    await QRCodeImageBuilder.SaveSvgAsync(text, response.Body, cancellationToken: cancellationToken);
});
```

A styled image from a web API (`SaveToAsync` on the builder, and `SaveToSvgAsync` for an SVG):

```csharp
app.MapGet("/tickets/{id}", async (string id, HttpResponse response, CancellationToken cancellationToken) =>
{
    var builder = new QRCodeImageBuilder($"https://example.com/tickets/{id}")
        .WithSize(512, 512)
        .WithModuleShape(CircleModuleShape.Default, 0.85f)
        .WithColors(SKColors.MidnightBlue, SKColors.White);

    response.ContentType = "image/png";
    await builder.SaveToAsync(response.Body, cancellationToken);
});
```

The response's `PipeWriter`, with no async call of the library (`SaveTo(IBufferWriter<byte>)`, as now, and the caller's flush):

```csharp
response.ContentType = "image/png";
builder.SaveTo(response.BodyWriter);
await response.BodyWriter.FlushAsync(cancellationToken);
```

A file saved from a desktop app, awaited on the UI thread (`SaveToAsync`). The render moves off the UI thread on its own (A14):

```csharp
// path and cancellationToken come from the app
await using var file = File.Create(path);
await builder.SaveToAsync(file, cancellationToken);
```

Files written in a batch, synchronously (`SavePng`, as now):

```csharp
foreach (var (name, url) in tickets)
{
    using var file = File.Create($"{name}.png");
    QRCodeImageBuilder.SavePng(url, file, QREccLevel.Q, size: 1024);
}
```

A write that cannot be laid out throws `InvalidOperationException` and writes nothing. A cancelled write throws `OperationCanceledException`, and one cancelled part way leaves part of the image in the stream. A null stream throws `ArgumentNullException`, and one that cannot write `ArgumentException`.

### How a caller reads

Each read as a caller would write it. With C# 14 the SkiaSharp calls read as `QRCodeDecoder.Xxx`, and on older versions as `QRCodeImageDecoder.Xxx`. The names of the calls for several symbols are drafts (A4). Micro QR and rMQR read the same way through `MicroQRCodeDecoder` and `RmQRCodeDecoder`, without the calls for several symbols.

An image a web API is sent, read without holding a thread (`DecodeAsync`). The token is the request's, so a client that goes stops the read:

```csharp
app.MapPost("/scan", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    try
    {
        var result = await QRCodeDecoder.DecodeAsync(request.Body, cancellationToken);
        return result.Success
            ? Results.Ok(result.Text)
            : Results.UnprocessableEntity(result.Info.Status.ToString());
    }
    catch (InvalidDataException)
    {
        // The body is not an image, or is over a limit
        return Results.BadRequest();
    }
});
```

Every code on a sheet sent to a web API (`DecodeSymbolsAsync`):

```csharp
app.MapPost("/scan-sheet", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    // InvalidDataException is handled as above
    var results = await QRCodeDecoder.DecodeSymbolsAsync(request.Body, cancellationToken);
    return Results.Ok(results.Select(r => r.Text));
});
```

A file opened in a desktop app, awaited on the UI thread (`DecodeAsync`). The read awaits the file, and the decode moves off the UI thread on its own (A14):

```csharp
// path and cancellationToken come from the app
await using var image = File.OpenRead(path);
var result = await QRCodeDecoder.DecodeAsync(image, cancellationToken);
statusText.Text = result.Success ? result.Text : $"No code: {result.Info.Status}";
```

Files read in a batch, synchronously (`Decode(Stream)` and `DecodeSymbols(Stream)`):

```csharp
foreach (var path in Directory.EnumerateFiles(folder, "*.png"))
{
    using var image = File.OpenRead(path);
    var result = QRCodeDecoder.Decode(image);
    Console.WriteLine($"{Path.GetFileName(path)}: {(result.Success ? result.Text : result.Info.Status.ToString())}");
}

using var sheet = File.OpenRead("sheet.jpg");
foreach (var result in QRCodeDecoder.DecodeSymbols(sheet))
    Console.WriteLine($"{result.Text} at {result.Info.Corners.TopLeft}");
```

A bitmap the app already holds (`TryDecode`, as now), and every code in it (`DecodeSymbols(SKBitmap)`):

```csharp
using var bitmap = SKBitmap.Decode("ticket.png");
if (QRCodeDecoder.TryDecode(bitmap, out var text, out var info))
    Console.WriteLine($"{text} (version {info.Version})");
else
    Console.WriteLine($"No code: {info.Status}");

QRCodeDecodeResult[] results = QRCodeDecoder.DecodeSymbols(bitmap);
Console.WriteLine($"{results.Length} codes");
```

Camera frames, the luminance a capture hands over, one byte a pixel in packed rows (`TryDecodeImage` into a buffer). Frames come many a second, so the text is written into a buffer allocated once, and a string is made only when the code changes:

```csharp
// Allocated once: room for the longest text a QR code holds
private readonly char[] _text = new char[QRCodeDecoder.GetMaxDecodedLength(40)];
private string _last = "";

void OnFrame(ReadOnlySpan<byte> luminance, int width, int height)
{
    if (QRCodeDecoder.TryDecodeImage(luminance, width, height, _text, out int written, out _)
        && !_text.AsSpan(0, written).SequenceEqual(_last))
    {
        _last = new string(_text, 0, written);
        OnCodeChanged(_last);
    }
}
```

The codes of one frame, as many as the app expects (`DecodeImageSymbols` into a room). The search ends when the room is full, so an app that expects two codes pays for two:

```csharp
// Allocated once
private readonly QRCodeDecodeResult[] _room = new QRCodeDecodeResult[2];

void OnFrame(ReadOnlySpan<byte> luminance, int width, int height)
{
    int count = QRCodeDecoder.DecodeImageSymbols(luminance, width, height, _room);
    foreach (var result in _room.AsSpan(0, count))
        OnCode(result.Text);
}
```

A matrix the app holds, such as one it generated, to check what it says (`TryDecode` on `QRCodeData`, and on the modules of a grid sampled elsewhere):

```csharp
QRCodeData data = QRCodeGenerator.Create("https://example.com", QREccLevel.M);
if (QRCodeDecoder.TryDecode(data, out var text))
    Debug.Assert(text == "https://example.com");
```

A read that finds no code is not an exception: `Success` or the `bool` says so, and `Info.Status` says why. `InvalidDataException` is for a stream that is not an image or is over a limit, `OperationCanceledException` for a cancelled read, and the argument exceptions for a null stream or one that cannot read.

### Performance targets

Measured in the build against the costs below (2026-10-15, x64). 10 % is the project's regression threshold.

| | Target | Baseline or check |
|---|---|---|
| P1 | No synchronous `Read`, `Write` or `Flush` on the caller's stream | A test stream that throws on each |
| P2 | An async write takes at most 10 % more than its synchronous twin writing to a `MemoryStream` | PNG of a URL at 512 px 4.6 ms, of version 40 at 1,024 px 32 ms, SVG 0.65 ms and 18.5 ms |
| P3 | An async write allocates a fixed amount more than its synchronous twin, under 1 KB whatever the image's size, its buffers pooled | The P2 shapes with the memory diagnoser |
| P4 | `DecodeAsync` on a `MemoryStream` takes at most 10 % more than decoding into an `SKBitmap` and calling `TryDecode` | The 12-megapixel photograph, 51 ms to decode and 6.3 to 7.4 ms to read |
| P5 | A stream read holds at its peak no more than that pair holds, plus the encoded bytes | The same photograph, 48 MB of colour and 12 MB of luminance, less if it decodes straight to grey |
| P6 | An image over the pixel limit is refused before any pixel is allocated | The 1-bit PNG of A11, 194,447 bytes that decode to 1.6 GB of grey |
| P7 | Once the image is decoded, the longest stretch of a read for every symbol between two checks of the token is 10 ms at most on x64, on images the size of the sheets. The stretch grows with the image, since the steps that pass over the whole image, a reading's row scan before its first try among them, run between two checks | The seven sheets of 60 codes, 33 to 100 ms a sheet |
| P8 | The synchronous calls keep their instructions on the report shapes and allocate what they allocate now | As in What has to stay true |
| P9 | An async call made under a `SynchronizationContext` renders or decodes on a pool thread, and one made with none takes no extra hop | A single-threaded context in the tests, and the thread the computation runs on |

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
| WinUI, WPF, MAUI | A PNG of a URL is fine on the UI thread. A 1,024 px PNG of version 40 (32 ms) takes more than a 16 ms frame | An async write to a file, which moves its render off the UI thread (A14). For a call with no I/O, the application's `Task.Run` |
| Blazor WebAssembly and the Playground | One thread, so a `Task.Run` blocks the page too | Not by async I/O. A Web Worker does, or work that yields part way |
| Writing files in a batch | Little | Little |

In a web app an async method that only runs the work through `Task.Run` makes things worse: it moves the work from one pool thread to another and frees none. What web and UI callers need from the library is async I/O, and calls a caller can move off its own thread easily. They do not need an async form of the computation.

### Reading, by setting

Unlike generation, the library reads no stream today. The core takes luminance and the SkiaSharp package a bitmap, and getting the image is the caller's I/O. An async read means the library takes that I/O over.

| Setting | What blocks | Whether async helps |
|---|---|---|
| A web API that reads an uploaded photograph | `SKBitmap.Decode(Request.Body)` reads synchronously and throws by default. A 12-megapixel photograph keeps the thread about 60 ms (51 to decode the JPEG, 6 to 7 to read), and up to 100 ms on a sheet read for every symbol, whatever the call looks like | Yes, for reading the body, which a caller does today with `await Request.Body.CopyToAsync(...)` first. Cancellation would also let a long read stop when the client goes |
| Scanning from a camera (WinUI, WPF, MAUI, phones) | Frames usually arrive on a capture thread, not the UI thread. A 1080p frame takes 1 to 7 ms against 33 ms a frame at 30 frames a second, with less room on a phone, and most frames hold no code | No, there is no I/O. What helps is the allocation-free span call, which exists, the time a frame takes, and perhaps dropping a stale read when a new frame comes |
| Opening or pasting an image in a desktop app | Reading the file, decoding a large JPEG (51 ms) and then the read take more than a frame | An async read of the file, which moves its decode off the UI thread (A14). For a bitmap in hand, the application's `Task.Run` |
| Blazor WebAssembly and the Playground | The page stops while it reads. The Playground reads on the page's thread through a synchronous `[JSExport]` | Not by async I/O. The browser can decode the JPEG off the thread (`createImageBitmap`), but the read itself needs a Web Worker or work that yields |
| Reading files in a batch | Little | The caller's parallelism |

### True async or a pipe

True async awaits input in the middle of the computation: a decoder that runs out of bytes part way awaits a refill and resumes where it stopped. A pipe reads the input until it is complete and then computes over all of it synchronously, and writes the same way round, the output computed whole and then written asynchronously. This library takes the pipe, as a shape rather than the `System.IO.Pipelines` types (A1, A3):

- Output has nothing to await part way. Skia encodes a PNG whole and synchronously, so the bytes exist only once the encode is done. Rendering into the writer's buffer synchronously and then flushing asynchronously is the whole of it. `SaveTo(IBufferWriter<byte>)` already writes into a `PipeWriter`, so what is missing is the flush and an asynchronous write to a `Stream`.
- Input decodes only whole, and the read walks the whole image many times. Reading until the input is complete and then reading the symbol synchronously is the only shape that makes sense.
- Where true async would matter is not I/O: stopping a long read, and giving the thread back part way on WebAssembly's single thread. Both mean checking a token, or yielding, inside the read (A5, A6).

## The surface

The I/O is all in the SkiaSharp package. The core and the GS1 package take and return spans, strings and arrays, and touch no stream.

- Fourteen public methods write to a `Stream`. `SaveTo(Stream)` and `SaveToSvg(Stream)` on `SymbolImageBuilderBase<TSelf>`, shared by the three symbologies' builders, and `SavePng` and `SaveSvg` on each builder, for data and for text, twelve in all.
- The `IBufferWriter<byte>` forms (`SaveTo`, `SaveToSvg`, `WriteImage`, `WritePng`, `WriteSvg`) write synchronously into the caller's buffer, and a `PipeWriter` is one, so the caller's `FlushAsync` already makes them async.
- No public method reads a stream. The three image decoders take an `SKBitmap`, which the caller decodes from a file or a stream.

## Decisions

| | Decision | Options and facts | Recommendation | Ruling |
|---|---|---|---|---|
| A1 | The output's shape | `SaveToAsync(Stream, CancellationToken)`, a `PipeWriter` form that writes and flushes, or both. A `PipeWriter`, such as `Response.BodyWriter`, already works through `SaveTo(IBufferWriter<byte>)` and the caller's `FlushAsync`. A `PipeWriter` form would add nothing a `Stream` lacks here: Skia encodes a PNG whole before any byte is written, so there is nothing to flush in pieces, and the SVG an async write holds whole can be held in pooled segments without the package (A2) | The `Stream` forms only, with the `PipeWriter` pattern in the docs. A `PipeWriter` form is a new overload, additive when asked | Ruled 2026-10-25: as recommended |
| A2 | Dependencies | `PipeWriter` and `PipeReader` are in the net10.0 shared framework but not in net8.0's, where only ASP.NET Core's carries them, nor in netstandard, where they are the System.IO.Pipelines package (checked in the installed reference packs 10.0.9, 8.0.22 and 8.0.28, 2026-10-25). `ValueTask` needs System.Threading.Tasks.Extensions on netstandard2.0, and `IAsyncEnumerable<T>` Microsoft.Bcl.AsyncInterfaces. `IBufferWriter<byte>` comes with System.Memory, which the library already references there | None added. `Task` and `Task<T>` rather than `ValueTask`, for calls that take milliseconds and seldom complete synchronously. An image is one block, which `Stream.WriteAsync` and `ReadAsync` carry without a pipe | Ruled 2026-10-25: as recommended |
| A3 | The input's shape | A `Stream`, a `PipeReader` or a `ReadOnlySequence<byte>`. A one-symbol read and a read for every symbol. The decode reads the encoded image from memory once it is all there, so a `Stream` read copies it once out of the caller's buffers into the read's. A `PipeReader` form could save that one copy at most, against 51 ms to decode a 12-megapixel photograph, and a caller holding `Request.BodyReader` has `Request.Body` over the same body | `DecodeAsync(Stream, CancellationToken)` on the three image decoders, reading the stream to its end asynchronously and decoding synchronously, and `DecodeSymbolsAsync(Stream, CancellationToken)` for Standard QR. The stream is the whole image, so there is no end to find inside it and nothing read past it to give back. The docs say the stream is read to its end. A `PipeReader` form is additive when asked | Ruled 2026-10-25: as recommended |
| A4 | The result type | An async method cannot have `out` parameters, so the read returns its result. One type a symbology, its text and its decode info, serves several symbols, an async read and a one-symbol result. A failed read returns a result whose info carries the status, or none. In an array of several symbols every result is a success, and only a one-symbol read can fail. `DecodeStatus.Success` is 0, so a default result has a successful status and a null text, as an entry of a new room has where the span form wrote nothing | `QRCodeDecodeResult`, `MicroQRCodeDecodeResult` and `RmQRCodeDecodeResult`, each its `Text`, its `Info` and a `bool Success`. A one-symbol read returns a result in every case, its `Info.Status` saying why it failed and its `Text` empty, as `TryDecode` leaves its text, so the reason `TryDecode` hands back today is kept. `Success` is true for a symbol read alone, and false for a failed read and for a default value, which a property computed from `Info.Status` would get wrong. `DecodeStatus` keeps its numbers, since a renumbering would change what callers stored or compared as integers. A read for every symbol returns the symbols it read. These names settle the call for several symbols | Ruled 2026-10-25: as recommended. The names of the calls for several symbols stay drafts until Phase 3 of the follow-ups plan |
| A5 | Cancellation inside the computation | The token checked only around the I/O, also between the symbols of a read for every symbol, or also between a decode's passes. A read for every symbol takes up to 100 ms on x64 and longer on WebAssembly, and a client that goes leaves it running. A one-symbol read took 7.4 ms at most of the reads measured, against 51 ms for Skia to decode the same photograph, which no token stops. A render cannot be stopped inside Skia's encode, and took 32 ms at most of the renders measured | Checked before the decode starts and between the symbols of a read for every symbol, never inside one symbol's read. The one-symbol reads keep their instructions with no generic policy, and the read for every symbol pays one check a symbol. How long the stretch between two checks runs on the sheets is measured in the build, and a check between passes is added only if that is too long. A render checks the token before it starts only. The tokens are on the async calls alone, and synchronous `CancellationToken` overloads stay out of 2.0.0 | Ruled 2026-10-25: as recommended |
| A6 | WebAssembly | A Web Worker, or a decode that yields part way | Neither in the library. A decode that yields is the true async the analysis rules out, and a Web Worker is the page's choice. The Playground stays as it is | Ruled 2026-10-25: as recommended |
| A7 | An async method that only moves the computation off the thread | .NET's guidance is not to expose one, and on a server it frees no thread | None, for `Create`, `TryDecodeImage` on luminance, `ToBitmap` and the helpers that return bytes | Ruled 2026-10-25: as recommended |
| A8 | Which existing methods get an async twin | The fourteen that take a `Stream`, or the two on the builders' base only | All fourteen, so that a method that writes to a `Stream` has an `...Async` twin and the obvious synchronous call has an obvious fix | Ruled 2026-10-25: as recommended |
| A9 | The coordinates a stream read reports | The image's pixels as encoded, which `SKBitmap.Decode` gives, or turned by the EXIF orientation | The pixels as encoded, as a caller gets them today. A caller that shows the image turned maps the corners, as the Playground does | Ruled 2026-10-25: as recommended |
| A10 | A synchronous twin of the stream read | `Decode(Stream)` and `DecodeSymbols(Stream)`, the async names without `Async`, returning the same results. `TryDecode(Stream, out QRCodeDecodeResult)`, a `bool` as `TryDecode` has, but neither the async read's shape nor the bitmap overload's. `TryDecode(Stream, out string, out QRCodeDecodeInfo)`, the bitmap overload's shape, with no result type. Or none: a synchronous caller has `SKBitmap.Decode(Stream)` today, so it is not stranded. Blocking on the async read is no substitute for a twin, since it holds the thread and pays for the async I/O as well | Tied to A11. If the stream reads carry limits, `Decode(Stream)` on the three decoders and `DecodeSymbols(Stream)`, so that a synchronous caller gets the limits, every method that takes a `Stream` has both forms, as A8 gives the writes, and each twin differs from its async form by `Async` and `Task` alone. Without limits, none: a synchronous caller decodes into an `SKBitmap` and calls `TryDecode`, as today | Ruled 2026-10-25: as recommended. A11 puts limits on the stream reads, so `Decode(Stream)` and `DecodeSymbols(Stream)` ship |
| A11 | Limits on a stream read | None, documented, as today when the caller decodes the image. Fixed limits on the stream reads. Or limits the caller sets. The decoders' only size limit is `int.MaxValue` pixels. A 1-bit PNG of 40,000 × 40,000 pixels holds 200,040,000 bytes of rows, which zlib at level 9 deflates to 194,447 (measured 2026-10-22), and decodes to 1.6 GB at one grey byte a pixel and 6.4 GB at four. Kestrel caps a request body at 30,000,000 bytes by default, which bounds the encoded bytes but not the pixels | Fixed limits on the stream reads alone, where the library takes the caller's I/O over, on the encoded bytes and on the pixels, the pixels checked from the image's header before any pixel is allocated. The `SKBitmap` and luminance entries get memory the caller has already allocated and keep today's limit, so a caller with a larger image decodes it and calls `TryDecode`. The values are set in the build from the largest camera images, and a parameter for them is additive later. A stream over a limit, or one that is not an image, throws `InvalidDataException`, since `DecodeStatus` says what an image held and such a stream gives no image | Ruled 2026-10-25: as recommended |
| A12 | What the async read for every symbol returns | `Task<QRCodeDecodeResult[]>`, or `IAsyncEnumerable<QRCodeDecodeResult>` handing each symbol over as it is read. The stream is read whole before the first symbol can be found, and the symbols are then found by synchronous computation over one image with no I/O between them. So each step of an `IAsyncEnumerable` would complete synchronously on the same thread, and reading them all takes the time the array takes. What it could give is an early stop, the search ending when the caller breaks, and only if the search could resume between symbols, its pooled buffers held by an object across each yield and the entry the one-symbol call shares reshaped. Otherwise it runs the whole search and yields from an array. The synchronous `DecodeSymbols(SKBitmap)` returns an array. An array gives a count, indexing and LINQ on every target, and `IAsyncEnumerable<T>` needs a package on netstandard2.0 (A2). The two cannot share a name and parameters, so the choice is made for 2.0.0 | `Task<QRCodeDecodeResult[]>`, the array form in a `Task`, as A10's twins are. A caller who wants fewer symbols has the span form's room on luminance ([image-decode-followups-plan.md](image-decode-followups-plan.md), M2). An `IAsyncEnumerable` fits a stream of several images, frames or a multipart upload, where the results arrive over I/O, and that is another call | Ruled 2026-10-25: `Task<QRCodeDecodeResult[]>`, no `IAsyncEnumerable` |
| A13 | How the reads are named | Keep `TryDecode` for one symbol from memory, a `bool` and `out` parameters as now, and return the result type from a read of a stream and a read for several symbols. Or a design with no past to keep: `Decode` and `DecodeSymbols` on every input returning the result type, and `Try` only for the forms that write into the caller's `Span<char>`, so that a bitmap and a stream read alike and every async form is its synchronous form's exact twin. The library has 24 `Try` reads, 18 that hand out a `string` and 6 that write into a span. That design replaces the 18, and their callers rewrite an `if` and an `out var`, which a token replacement cannot do. A `Try` suits an input in memory, since a `false` leaves it as it was. A stream is consumed by the read whatever it finds, and an async read cannot have `out` parameters | The `Try` forms stay. One symbol from memory is read with `TryDecode` or `TryDecodeImage`, and a read from a stream and a read for several symbols return the result type. A `Decode(SKBitmap)` returning the result type is additive when asked | Ruled 2026-10-25: as recommended. The design with no past was laid out beside it and not taken |
| A14 | Where an async call computes | An async method runs on its caller's thread until an `await` that does not complete at once. An async write renders and encodes before it writes, so it renders on the caller's thread, up to 32 ms of the renders measured. An async read decodes after its reads: on the pool when a read completed later, and on the caller's thread when every read completed at once, as a `MemoryStream` or data already buffered does, 51 ms and 6 to 7 ms for the 12-megapixel photograph. A UI would then wrap an async call in `Task.Run` to keep its frames. Options: compute wherever the call is, move to the pool before computing on every call, or move only when the call runs under a `SynchronizationContext` or a `TaskScheduler` other than the default, as a UI thread or a Blazor component does and an ASP.NET Core request does not | Move only under a context. A UI awaits the call as it is and never renders or decodes on its thread, and an ASP.NET Core request, with no context, takes no extra hop. A call with no I/O stays synchronous (A7), so this adds no method | Ruled 2026-10-25: as recommended |

Notes for the build, not decisions:

- A stream read rents its buffer from the pool, and decodes the image as `SKBitmap.Decode` does and reads it as `TryDecode` reads that bitmap, so it reads what the bitmap call reads (Phase 2). Decoding straight into one grey channel, a quarter of the 48 MB a 12-megapixel photograph takes as colour, was left out: it would put Skia's grey in place of the library's own luminance conversion, which would need a sweep of its own.
- An exposable `MemoryStream` is decoded in place, with the token checked up front. A stream that can seek has its remaining length checked against the limit before it is read, and the buffer is rented to that size. A stream that cannot seek is read to at most one byte past the limit (Phase 2).
- Nothing an async write renders reaches the stream when the render fails. The PNG is encoded whole before it is written, as now, and the async SVG renders into pooled memory before it is written, where the synchronous one streams. A write cancelled part way leaves part of the image behind.
- The async writes do not flush the stream, since the synchronous ones do not (Phase 1).
- Every `await` takes `ConfigureAwait(false)`.
- An async call moves to the pool under a context through an awaiter of the library's own, which completes at once with neither a context nor a scheduler of the caller's, and otherwise queues the rest of the call to the pool (Phase 1).
- The tests use a stream that refuses synchronous reads and writes, and one that hands its bytes over a few at a time.

## Phases

| Phase | Work | Output |
|---|---|---|
| 0 | The decisions above | The maintainer's rulings, given on 2026-10-25 |
| 1 | Done 2026-10-25. Output: the async twins A8 rules, cancellation before the render and during the write, and the move off a context A14 rules | Tests with a stream that refuses synchronous writes, P2, P3 and P9, the approved API listing |
| 2 | Done 2026-10-25. Input: the result types, `DecodeAsync` for the three symbologies and `DecodeSymbolsAsync` with the call for several symbols ([image-decode-followups-plan.md](image-decode-followups-plan.md), Phase 3), the limits A11 rules, the synchronous twins (A10), and the move off a context | Tests with a stream that refuses synchronous reads and one that hands bytes over a few at a time, a stream over each limit and one that is not an image refused before any pixel is allocated, a default result whose `Success` is false, P4 to P6 and P9 |
| 3 | Done 2026-10-25. Cancellation between the symbols of a read for every symbol (A5) | A cancelled read for every symbol stops at the next check, P7 and P8 |
| 4 | Done 2026-10-25. `docs/`, the README, the API page and the specs | The feature documented |

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

Done, once GS1 / FNC1 had shipped. The public surface was taken stock of: the I/O is all in the SkiaSharp package, fourteen methods that write to a `Stream` and none that reads one. The questions of the analysis became decisions A1 to A10, each with a recommendation, and the phases are sketched.

### Limits, synchronous twins and cancellation between symbols (2026-10-22)

Done, at the maintainer's request. Taking the caller's I/O over takes over its limits too, so A11 was added: the decoders' only size limit is `int.MaxValue` pixels, and a 1-bit PNG small enough for any request body decodes to gigabytes. A10 was tied to it, so that a synchronous caller gets the limits as well. A5 moved its checks from between a decode's passes to between symbols, which leaves the one-symbol reads' instructions alone. The notes for the build gained what a failed or cancelled write leaves, and how a stream the read can see whole is read. The deflated PNG was measured with Node's zlib in the scratch directory, and nothing in the repository changed but this plan.

### Goals (2026-10-25)

Done, at the maintainer's request: why the API is built, what it does and does not, the API in full and the performance targets, written from the recommendations. The maintainer's questions changed three things. The synchronous twin of a stream read is `Decode(Stream)`, returning the result type, so that each twin differs from its async form by `Async` and `Task` alone (A10). The pipe types were weighed again and stay out: an image is one block, Skia encodes it whole, and the decode reads it from memory once it is all there (A1, A3). The read for every symbol returns an array, not an `IAsyncEnumerable`, since its symbols come from one image with no I/O between them (A12), which the maintainer ruled the same day. The plan no longer credits its choices to other libraries, and the installed reference packs were checked again for System.IO.Pipelines.

### The result type and the names ruled (2026-10-25)

Done, at the maintainer's request. The decode calls were listed as they stand and as they will be, and beside them a design with no past to keep: `Decode` on every input, and `Try` only where the caller lends the buffer. The maintainer kept the `Try` forms (A13). Laying out `Success` showed a trap the result type would carry: `DecodeStatus.Success` is 0, so a default result reads as a success with a null text. A4 was ruled with a `Success` that is false for a default value. Each read was then written out as a caller would use it, under the API in the goals.

### Writing examples and where an async call computes (2026-10-25)

Done, at the maintainer's request: each write written out as a caller would use it. The maintainer found the desktop read, an async call wrapped in `Task.Run`, wrong to ask of a caller. An async method runs on its caller's thread until an `await` that does not complete at once, so an async write renders on the UI thread, and an async read decodes there when its reads complete at once. A14 was added: an async call made under a context moves to the pool before it computes, and one made with none does not move, so that a UI awaits the call as it is and a server pays nothing. The desktop examples were rewritten to await the calls as they are, and the settings tables of the analysis point at A14. The maintainer ruled A14 as recommended the same day.

### All decisions ruled (2026-10-25)

Done, at the maintainer's request. A1 to A3 and A5 to A11 were ruled as recommended, after A4 and A12 to A14 earlier the same day. A11 puts limits on the stream reads, so A10's synchronous twins ship. The goals now state the design as ruled, and Phase 1, the output, is next.

### Phase 1: the output (2026-10-25)

Done, at the maintainer's request.

What was done:

- The fourteen async writes: `SaveToAsync` and `SaveToSvgAsync` on `SymbolImageBuilderBase<TSelf>`, and `SavePngAsync` and `SaveSvgAsync`, for data and for text, on each of the three builders. The approved API listing gained these fourteen and nothing else, and the Playground's API page was regenerated.
- A call checks its arguments before it returns a task, as the synchronous call checks them, and the rest happens in the task: the generation, the layout, the render and the write.
- Under a synchronization context or a task scheduler of the caller's own the call moves to the pool before it renders (A14), through an awaiter of the library's own that completes at once when there is neither.
- A PNG is encoded into a pooled buffer of its size, and the native image and its encoding are released before the write. An SVG document is rendered into a pooled buffer that doubles as it grows. Each is then sent in one async write, through the memory overload on every target but netstandard2.0, which has only the array overload.
- The token is checked before the render and after it, and handed to the write.
- The writes do not flush the stream. A probe counted the synchronous `SaveTo` and `SaveToSvg` on a stream of its own, and neither flushed it.
- `SymbolImageAsyncWriteTest`, 54 cases: the bytes of each twin through a stream that refuses synchronous writes and flushes (20 calls across the three symbologies, a JPEG and a 2,048 px PNG among them), a token canceled before the call and during the write, argument errors thrown before a task exists, a layout and an icon that fail the render with nothing written, and the thread the render runs on under a single-threaded synchronization context, under an exclusive task scheduler and with neither. Five planted faults each turned their tests red: no move off the context, a synchronous SVG write, no token handed to the write, a null stream reported through the task, and no check of the token.
- `QRCodeImageAsyncWrite` in the benchmark project, each shape's synchronous call its baseline.

Measured with BenchmarkDotNet in process, .NET 10.0.9, x64, Ryzen 9 7950X3D, writing to a `MemoryStream`:

| Shape | Sync | Async | Ratio | Allocated, sync / async |
|---|---|---|---|---|
| PNG of the URL, 512 px | 4,709 μs | 4,924 μs | 1.05 | 3.17 / 3.24 KB |
| PNG of version 40, 1,024 px | 22.48 ms | 22.27 ms | 0.99 | 3.17 / 3.29 KB |
| SVG of the URL | 516 μs | 534 μs | 1.04 | 5.86 / 5.92 KB |
| SVG of version 40 | 18,023 μs | 18,486 μs | 1.03 | 6.02 / 6.09 KB |

P2 and P3 hold: at most 5 % more time, and at most about 120 bytes more allocated. The version 40 PNG first read 1.08, with nine times the synchronous call's deviation, and a rerun of the pair read 0.99, which the table gives. P1 and P9 are the tests above. The full suite passes on net10.0 (34,986 passed, 402 skipped) and net8.0 (34,892 passed, 402 skipped).

Lessons:

- The synchronous `SaveToSvg` writes in small pieces: 5,523 writes for the URL's 16,218-byte document and 196,373 for version 40's 578,239 bytes, counted on a stream of its own. The async write sends each document in one write.
- The synchronous `SaveToSvg` leaves part of a document in the stream when an icon is refused while the symbol is drawn, which the test of the async write checks as its premise. The async write leaves nothing.
- A stream that overrides only the memory overload of `WriteAsync` serves a call of the array overload with a synchronous write (a probe on .NET 10), so the memory overload is the one that reaches a stream that writes only asynchronously.
- A test that waited for a write to begin hung for 30 minutes when a planted fault failed the call before it wrote. It waits for the call as well now.
- Restoring a planted fault with a file copy kept the backup's timestamp, so the next full run tested the fault's build: 13 failures, the fault's exact set. The timestamps were refreshed, and both frameworks pass.

Left for later: no test host runs the netstandard builds of the SkiaSharp package, so the netstandard2.0 array write is compiled and listed but not run. `docs/`, the README and the specs are Phase 4.

### Phase 2: the input (2026-10-25)

Done, at the maintainer's request.

What was done:

- The result types `QRCodeDecodeResult`, `MicroQRCodeDecodeResult` and `RmQRCodeDecodeResult` in the core, each its `Text`, its `Info` and a `Success` that is false for a default value (A4).
- The call for several symbols, under its draft names: `DecodeImageSymbols` on luminance in an array form and a span form, and `DecodeSymbols(SKBitmap)`. The internal entry writes the result type, so the span form writes into the caller's room as it is, and the array form gives the entry a room that doubles where the caller's room would fill, so the image is read once.
- The stream reads on the SkiaSharp decoders: `DecodeAsync(Stream)` on the three, `DecodeSymbolsAsync(Stream)` for Standard QR, and their synchronous twins `Decode(Stream)` and `DecodeSymbols(Stream)` (A10). Arguments are checked before a task is returned. The stream is read to its end, then the call moves off a caller's context (A14), decodes the image under the limits and reads the bitmap.
- The limits (A11): 256 MiB of encoded bytes and 268,435,456 pixels (16,384 × 16,384). The pixel limit holds the 200-megapixel sensors of current phones ([Samsung](https://news.samsung.com/us/samsung-introduces-the-200-megapixel-image-sensor-for-the-ultimate-high-resolution-experience-in-flagship-smartphones/)) and a 151-megapixel medium-format back, 14,204 × 10,652 ([Pop Photo](https://www.popphoto.com/phase-one-150-megapixel-camera-review/)). A 200-megapixel phone's JPEG was reported at 50 to 60 MB by an owner ([Samsung Community](https://eu.community.samsung.com/t5/galaxy-s24-series/dimensioni-foto/td-p/11433474)) and is put at about 120 MB by a [file-size estimator](https://miniwebtool.com/it/stimatore-dimensioni-file-foto/), so the byte limit holds it twice over. At the pixel limit a colour bitmap takes 1 GiB, four times the byte limit. A stream over either throws `InvalidDataException`, as does one that holds no image, the pixels checked from the header before any pixel is allocated.
- The approved API listings gained the three types, the two core calls and the nine SkiaSharp calls and nothing else, and the Playground's API page was regenerated.
- Tests: `ImageStreamDecodeTest`, 80 cases, holds every stream read to what `SKBitmap.Decode(Stream)` and `TryDecode` give, on PNG, JPEG and blank images of the three symbologies, through a stream that refuses synchronous reads, one that hands over 7 bytes a read, one that can seek, an exposed `MemoryStream` read from its position and one that hides its array. The synchronous twins are read through a stream that refuses async reads. It covers a header declaring 40,000 × 40,000 pixels and one a row over the limit, a seekable stream over the byte limit refused with no read, a stream that never ends stopped one byte past 256 MiB, argument errors before a task exists, cancellation before the call and during a read, and the decode off a synchronization context and a task scheduler and not with neither. `DecodeImageSymbolsTest`, 10 cases, covers the array form past its first room, the span form with room for all, one and none, argument errors, and a default result. Four planted faults each turned their tests red: no move off the context, an exposed `MemoryStream` read from its start, no token handed to the reads, and a synchronous twin made by blocking on the async read. The two limits were not removed as faults, since their tests would then allocate without bound, and the pixel test asserts the declared size in the message for that reason.
- `QRCodeImageStreamDecode` in the benchmark project, the bitmap pair its baseline.

Measured with BenchmarkDotNet in process, .NET 10.0.9, x64, Ryzen 9 7950X3D, on a 4032 × 3024 JPEG the benchmark makes:

| Shape | Bitmap pair | Stream read | Ratio | Allocated, pair / read |
|---|---|---|---|---|
| Exposed `MemoryStream`, async | 59.26 ms | 58.65 ms | 0.99 | 2.93 / 1.42 KB |
| Exposed `MemoryStream`, synchronous twin | 59.26 ms | 58.64 ms | 0.99 | 2.93 / 1.05 KB |
| `MemoryStream` over an array, async | 58.83 ms | 57.80 ms | 0.98 | 3,334 / 1,445 B |
| 512 px PNG of the URL, async | 474 μs | 492 μs | 1.04 | 2,978 / 1,084 B |

On `lots/image007` of BoofCV's set (4032 × 3024, 1,162,360 bytes), timed in a scratch probe, the median of 21 calls after 5: the pair 79.0 ms, the synchronous twin on an exposed stream 78.0, `DecodeAsync` on it 78.4, on a `MemoryStream` over the array 81.4 and on a stream that cannot seek, 64 KB a read, 80.4, every one with the pair's result. P4 holds, at most 4 % more time. P5 holds by construction: the read holds the encoded bytes, none for an exposed `MemoryStream`, while the bitmap decodes, and returns them before the luminance is made, and the bitmap and the luminance are the pair's. The peak was not measured. P6 and P9 are the tests above. The full suite passes on net10.0 (35,076 passed, 402 skipped) and net8.0 (34,982 passed, 402 skipped).

Lessons:

- `SKBitmap.Decode(Stream)` disposes the stream it is given: a probe found it unreadable afterwards, and the benchmark's first baseline failed for reusing one. The stream reads leave it open.
- A stream that overrides only the memory overload of `ReadAsync` serves the array overload with a synchronous read (a probe on .NET 10), as for the writes.
- Skia reports a PNG header's size without its pixels, so a header with one row of data is enough to test the pixel limit. A test of the limit that asserts only the exception type passes for the wrong reason when the decode fails some other way, so it asserts the size in the message.
- A test that compared the limits' constants with the documented numbers was replaced by tests of the behaviour at those numbers.

Left for later: the netstandard2.0 reads are compiled and listed but run by no test host, as the writes are. The Playground and `docs/` for the call for several symbols, and its names, are Phase 3 of [image-decode-followups-plan.md](image-decode-followups-plan.md).

### Phase 3: cancellation between symbols (2026-10-25)

Done, at the maintainer's request.

What was done:

- The internal entry for every symbol takes a token and checks it before each symbol it tries, in each reading's loop, never inside one symbol's read (A5). Every reading enters that loop once at least, so an image where nothing reads sees the token too. The public synchronous calls pass no token, and the one-symbol reads are not touched: the change is in the entry's own code alone.
- `DecodeSymbolsAsync(Stream)` hands its token through the read of the bitmap to the search, which the docs now say.
- A check at the start of each reading, before its row scan, was tried and taken out: the longest stretch past a cancel was the same with it, 4.05 to 6.99 ms on the seven sheets, as without it, 4.23 to 6.07.
- `SeveralSymbolsCancellationTest`, 5 cases: a span form canceled from another thread once the first symbol is in the room stops with some of the 48 symbols read and not all (10 runs out of 10), a token canceled before the call on a sheet and on a blank image, the token reaching the search through the bitmap read, and the stream read handing its token to the read. Three planted faults each turned their tests red: no check before a try, the bitmap read dropping the token, and the stream read dropping it.

Measured with a scratch probe that sees the internal entry, .NET 10.0.9, x64, Ryzen 9 7950X3D, on the seven sheets of 60 codes in BoofCV's `lots`, converted to grey by Skia: each read canceled at every half millisecond of its run from a thread that spins to the moment, and the time from the cancel to the exception taken. The time includes that thread's wake-up, so it bounds the stretch between two checks from above.

| Sheet | Read | Cancels | Longest past a cancel | Median |
|---|---|---|---|---|
| `image001` | 46.7 ms | 94 | 5.48 ms | 0.31 ms |
| `image002` | 34.0 ms | 69 | 4.37 ms | 0.56 ms |
| `image003` | 49.3 ms | 99 | 4.43 ms | 0.40 ms |
| `image004` | 32.7 ms | 66 | 4.23 ms | 0.48 ms |
| `image005` | 42.0 ms | 84 | 6.07 ms | 0.69 ms |
| `image006` | 44.4 ms | 89 | 4.79 ms | 0.34 ms |
| `image007` | 97.1 ms | 195 | 5.93 ms | 1.11 ms |

P7 holds, at most 6.07 ms against 10, so no check was added between passes. P8 holds: the one-symbol reads run the code they ran, and the allocation tests pass in Release. The full suite passes on net10.0 (35,081 passed, 402 skipped) and net8.0 (34,987 passed, 402 skipped).

Lessons:

- The longest stretch comes early, a cancel at the very start most often: the first reading scans every row of the full image before its first try.
- The test written for the reading-start check assumed a blank image never reaches a try. It does, once a reading, which a planted fault showed when the test passed without the check. Its premise was rewritten, and it now holds the check before each try to readings that find no symbol.

Left for later: Phase 4, the docs.

### Phase 4: the docs (2026-10-25)

Done, at the maintainer's request.

What was done:

- README: a section, Async and stream I/O, with the web API calls, how the calls behave and the `PipeWriter` pattern, a paragraph and an example for the read for every symbol under Decoders, the async save beside the stream save, the stream read in the Micro QR and rMQR examples, and the ASP.NET Core and image questions of the FAQ. The luminance example called `TryDecodeImage` on `QRCodeImageDecoder`, which has no such member, and now calls it on `QRCodeDecoder`. Every new example was compiled and run in a scratch web app.
- `docs/migration.md`: two sections in 2.0.0, the async writes and stream reads, with the move from `SKBitmap.Decode(stream)` and what differs, and several symbols in one image.
- [qrcode-symbologies.md](../specs/qrcode-symbologies.md): the reads' naming rule and the async I/O rules under Public API direction, the result types' constructors and the token overload under Package seam, the stream reads outside the allocation contract, and scope rows for several symbols, the async API and the stream read limits.
- The decoder specs name the stream read as an input, and [standardqr-decoder.md](../specs/standardqr-decoder.md) and the spec map give the public calls for several symbols, the room that grows and the token.
- The API page was regenerated with each phase.

Lessons:

- The pixel count the limit was set against had been written as 16,320 × 12,240. No source states it: it was worked back from 200 megapixels. The specs and Phase 2's record now give the sensor's 200 million pixels, which Samsung's announcement states.

Left for later: the plan folds into the specs at the 2.0.0 freeze. The names of the calls for several symbols wait on their ruling and the Playground's support for them on Phase 3 of [image-decode-followups-plan.md](image-decode-followups-plan.md).

### Review of the branch (2026-10-26)

Done, at the maintainer's request. Four reviewers read the branch for the caller's view of the async and synchronous calls, blocking on async work, security, correctness and the tests and docs. Their claims were merged into 27, and three verifiers tried to refute each one. Two fell: a context of the base `SynchronizationContext` type moves the call, as the docs say a context does, and the token is not checked after the image decode, which A5 rules.

What was fixed in the code:

- A stream read takes the bounds and the pixels from one Skia codec. `SKBitmap.DecodeBounds` and `SKBitmap.Decode` each made a codec over the bytes, so the file was parsed twice. On an icon file of 65,535 images of 1 × 1 pixels (6,684,576 bytes) the two took 393 ms and the one codec 195 ms. `SKBitmap.Decode` over a span is itself a codec over the pinned bytes, and on the 613 images under the tests' fixtures, `docs/images` and `samples` the one codec gave the same bitmaps (SkiaSharp 4.148.0, scratch probe).
- A format Skia cannot encode threw `NullReferenceException` from every raster output, the async write by faulting its task. SkiaSharp 4.148.0 encodes PNG, JPEG and WebP and returns no data for the other formats. The outputs now throw `NotSupportedException`, the async write by faulting its task, and the docs say which formats encode.
- A stream with nothing left from its position says so, where the message said it held no image.
- An exposed `MemoryStream` positioned past its end is left there, as a read leaves it, instead of moved back to its end.

What was fixed in the tests: the read context tests block the context's one thread on the task, so a decode sent back to it would hang them. The test streams override only the memory overloads of the async I/O. Both limits are held at their boundary. An exposed `MemoryStream` over part of an array, a call that throws where it should fault its task, and a flush are each caught. The cancel test for several symbols failed 3 of 8 suite runs on 4 processors in a verifier's run, since its cancel comes from another thread, and it now tries until a cancel lands between the symbols. Eleven planted faults each turned tests red, nine of them faults the tests had let pass.

What was fixed in the docs: P7 is scoped to the images it was measured on. The README's Errors bullet names the static helpers' argument checks, and its stream example writes one image a file. The migration guide says that a span used again keeps the entries past the count. The specs name the array overloads on netstandard2.0, the content that faults the task, the stream reads' allocations and the public calls for several symbols. The XML docs say an exposed `MemoryStream` is read in place and a default result has a null text.

The full suite passes on net10.0 (35,245 passed, 401 skipped) and net8.0 (35,151 passed, 401 skipped).

Left to the maintainer:

- Under a synchronization context the async writes read the builder after the call has returned, so a builder changed before the await renders as changed, and an icon bitmap disposed before the await ended the process with an access violation in three verifiers' runs. With no context the render runs inside the call. The read of an exposed `MemoryStream` reads its buffer after the call has returned in the same way.
- The pixel limit holds the bitmap to 1 GiB, but Skia decodes a cut PNG as a success, so a body of 47 bytes declaring 16,384 × 16,384 pixels drove the peak memory to about 1.5 GiB, and to about 3 GiB for an interlaced image of 16 bits a channel, in the verifiers' runs (Windows, .NET 10).
- The static async helpers take the token after their optional parameters, so `SavePngAsync(content, stream, token)` does not compile and `SavePngAsync(content, stream, default)` binds `default` to the level.
