# Cross-Language Benchmark

This record covers `tools/CrossLanguageBenchmark/`, which times this library against readers and writers outside .NET on the same machine and inputs, and the `Cross-language benchmark` workflow that runs it on hosted runners. `FeatherQR.Benchmark` compares this library with other .NET libraries under BenchmarkDotNet. The most widely used libraries are not .NET, so this harness gives every library a small CLI that speaks one protocol, times the calls inside the process with the same loop in every language, and computes every statistic in one collector. What NativeAOT's instruction-set target costs this library is part of the [SIMD tier inventory](qrcode-symbologies.md#nativeaots-instruction-set-target), from measurements this harness made.

## What

### What it measures

| Measured | Not measured |
|---|---|
| Speed of encode (payload to module matrix) and decode (matrix to text, and 8-bit grey image to text), single thread, steady state | Accuracy. Read rates do not depend on timing or language and stay with `tools/QRImageDecodeSweep` ([qrcode-test-fixtures.md](qrcode-test-fixtures.md)), which could add readers through the same CLIs |
| The cold first call: process start to first result, minus the same CLI started with nothing to do | Rendering to PNG or SVG, file decoding, colour conversion. Every library draws differently, and none of it is QR work |
| FeatherQR under the JIT, the JIT without AVX, NativeAOT for the default target and NativeAOT for `x86-64-v3` | Multi-threaded throughput |
| linux-x64 and linux-arm64, from one Docker image built for both | Windows and macOS as platforms. Docker Desktop runs the Linux image in a VM there, which served for development, and an amd64 image on Apple silicon is emulated |

The corpus is the payloads and symbols of the benchmark project's `Simple*` classes: five Standard QR payloads (numeric, alphanumeric, a URL, Unicode text, a Wi-Fi string), three Micro QR and three rMQR. Each is encoded, decoded as a matrix and decoded as an image, 33 entries in all. FeatherQR writes the inputs, and the corpus command fails if FeatherQR cannot read its own inputs back.

### Libraries

| Language | Library | Pinned | Builds | Measured |
|---|---|---|---|---|
| .NET | FeatherQR | this repository | JIT, JIT with `DOTNET_EnableAVX=0` (x64), NativeAOT default, NativeAOT `x86-64-v3` (x64) | Everything |
| C++ | zxing-cpp | v3.1.0 (885baaf), the commit the ZXingCpp package 0.5.2 ships | Default, `x86-64-v3` | Encode, matrix and image decode, all three symbologies |
| C | libzint | 2.16.0 (55541e1), the submodule zxing-cpp bundles as its writer | Default, `x86-64-v3` | Encode, all three symbologies |
| .NET | ZXingCpp package | 0.5.2 | As shipped, and with its `libZXing.so` replaced by one built from the same commit and flags as the default zxing-cpp build | Encode and image decode, all three symbologies |
| C | quirc | v1.2 (542848d), built from its four library sources | Default, `x86-64-v3` | Standard QR matrix and image decode |
| C | zbar | 0.23.93, Ubuntu noble's package | As Ubuntu ships it | Standard QR image decode |
| C | libqrencode | 4.1.1, Ubuntu noble's package | As Ubuntu ships it | Standard QR and Micro QR encode |
| Rust | rqrr | 0.11.0 | Default, `x86-64-v3` | Standard QR matrix and image decode |
| Rust | fast_qr | 0.14.0 | Default, `x86-64-v3` | Standard QR encode |
| Rust | qrcode | 0.14.1 | Default, `x86-64-v3` | Standard QR and Micro QR encode |
| Java | BoofCV | 1.5.0 | JRE 25, G1 with a 1 GiB heap | Standard QR and Micro QR image decode |
| Java | ZXing | 3.5.4 | JRE 25, G1 with a 1 GiB heap | Standard QR encode, matrix and image decode |
| Go | gozxing | 0.1.1 | Go 1.27.1 default, `GOAMD64=v3` | Standard QR encode, matrix and image decode |
| Go | go-qrcode (skip2) | its last commit, da1b656 (2020) | Go 1.27.1 default, `GOAMD64=v3` | Standard QR encode |

Native libraries are built twice on x64: at the toolchain's default target, which for C, C++, Rust and Go is baseline x86-64 as a user's release build gets it, and at `x86-64-v3`. The .NET JIT and the JVM pick instructions on the host, and a distribution's package is built once by the distribution, so each of those has one build.

Each library answers a question:

- zxing-cpp is the usual reference reader and the accuracy yardstick, and libzint is one of few Micro QR and rMQR writers. zxing-cpp writes through the libzint it bundles, so libzint measured alone is what separates the two.
- The ZXingCpp package is zxing-cpp behind a .NET wrapper, and its two builds separate the wrapper's cost from the native library's.
- rqrr ports quirc and was expected to be the fast end for clean images. It was the slow end, so quirc measures whether that is the method or the port.
- zbar and libqrencode are the reader and writer Linux distributions ship, so they are linked against Ubuntu's packages rather than built here.
- fast_qr is a speed-focused writer, and qrcode is the most downloaded Rust QR crate.
- BoofCV is a strong reader, and ZXing is the original, widely deployed one. Both run on a JIT, as this library does.
- Go is a garbage-collected runtime compiled ahead of time. gozxing ports ZXing, so against ZXing it separates the language from the code, and go-qrcode is a widely used Go writer.

Each library is called as its own documentation or tests call it, at default options:

- Multi-format readers are restricted to the symbology under test, as this library's decoders are per symbology. The ZXingCpp package's reader is also held to one symbol, which the native single-symbol call does by itself.
- zxing-cpp decodes a matrix through `QRCode::Decode`, the decoder behind its image reader, which is not public API: the public API reads only images.
- BoofCV's detector and zbar's image scanner are made once and reused, as a caller reading frames reuses them. quirc reuses one `struct quirc`, and its API takes no caller buffer, so each call copies the pixels into quirc's.
- ZXing writes text ISO-8859-1 cannot hold as UTF-8 with an ECI, as a caller sets it. gozxing's default character set is already UTF-8, so it needs no hint and writes no ECI. libzint takes UTF-8 input and inserts an ECI where the text needs one. libqrencode, fast_qr, qrcode and go-qrcode write the UTF-8 bytes with no ECI.
- ZXing's and gozxing's matrix decoders unmask the matrix they are given in place and leave it unmasked, so each call decodes its own copy, a few dozen words.
- A writer's whole call is timed as a caller writes it: libzint's create, encode and delete, libqrencode's encode and free, go-qrcode's constructor and `Bitmap`, where it adds the error correction and picks the mask.
- libzint keeps the level it is given. libqrencode takes the version as a minimum and would grow it for text that does not fit, which verification would reject.

### Protocol

Every CLI is called as `<cli> <mode> <op> <symbology> <input> [--ecc E] [--version V]` and prints one JSON object. [Protocol.cs](../../../tools/CrossLanguageBenchmark/dotnet/cli/Protocol.cs) is the reference implementation, and its comments carry the details. `rust/src/lib.rs`, `cpp/protocol.hpp`, `jvm/src/main/java/xlang/Protocol.java` and `go/protocol/protocol.go` port it.

- The unit of work is the same for every library. Decode starts from bytes in memory: a module matrix, or an 8-bit grey image drawn at 8 pixels per module inside the symbology's quiet zone. Both are read from a binary PGM before timing, since every language reads PGM in a few lines and no image library enters the loop. Encode starts from the payload's UTF-8 bytes and ends at a module matrix, pinned to the case's version and level. Each library chooses its own segments and mask.
- `run` makes one call and prints its result for verification. A call that fails is reported and not timed, because failing costs what failing costs. It then warms up for 3 s and at least 3 calls, sizes a batch to 20 ms from the warmup's second half, and times 30 batches with a monotonic clock. A CLI may raise the warmup and the batch length to floors of its own, as the JVM CLIs do (10 s and 100 ms).
- `fixed` makes N calls and prints only the checksum, for the outside check. `cold` makes one call and prints its result, and `noop` loads the input and makes no call.
- Every call's result is folded into the printed checksum through its content (the text's length and last character, or the matrix size and its centre module), so no compiler can drop the work. A fold is 0 only when the call fails, so `run` and `fixed` count those calls as `failedCalls`.
- A result's status is `ok`, `failed` (no decode, or the encoder refused) or `unsupported` (the library has no such operation). A decode prints its text as UTF-8 in hex, so no CLI needs a JSON string escaper, and an encode prints its matrix as rows of 0 and 1 with whatever quiet zone the library returns.
- Every result names the library, its version, the runtime and the build. FeatherQR's CLI also prints the instruction sets its code sees, so every run repeats the NativeAOT probe on the machine it ran on.

### Collector

The collector computes every statistic, so every row uses the same formula:

- `corpus` writes the inputs and `manifest.tsv`.
- `run` launches one process per entry, CLI and round, and rotates which CLI goes first in each round. It rejects a process whose verification fails or any of whose timed calls failed. A decode must print the payload, and an encoded matrix, cut to its dark modules' bounding box, must decode through FeatherQR's matrix decoder to the payload in the pinned version and level. A process's per-call times are its batches' times over their calls, and an entry's median is the median of its process medians. `run.md` tables every CLI against the first one listed.
- `outside` is the check that trusts no timing code in a CLI. It checks that three `fixed` calls succeed, then times whole processes of N and 2N calls with hyperfine, with no shell and the order alternating between rounds. T(2N) minus T(N) is N calls with everything fixed cancelled (process start, runtime start, corpus load, JIT), provided N contains the warmup. N is sized to one second of calls, or to the CLI's whole warmup where the CLI raised it. A measurement is disturbed, and taken again, up to three attempts in all, when its per-call time is not positive, when its range (the quartiles of T(2N) against those of T(N)) exceeds 20 % of the call, or when its fixed cost is below zero by more than 3 % of T(N).
- `compare` holds a run's medians against the outside check and against BenchmarkDotNet over the same calls. The reference project in `dotnet/reference` compiles the CLIs' own sources over the same corpus files, so a disagreement can come only from the loop or the statistics. The `Simple*` rows of `FeatherQR.Benchmark` would not do: their image rows convert an RGBA bitmap to grey inside the timed call, and they need SkiaSharp's native library, which the image does not carry. A CLI passes when every entry is within 7 % of its outside check and the signed median of those disagreements is within 2 %. The first bounds a broken loop on one entry and the second a bias over all entries, such as a cost per call. An entry whose `fixed` mode failed fails the verdict, and one still disturbed after its attempts stays out of it, which makes the verdict inconclusive.
- `agree` holds two runs against each other on ratios, each CLI's median over FeatherQR's entry by entry, under the same tolerance, and says when the runs differ in commit or CPU model.
- `cold` verifies each entry's `cold` output, then times the `cold` and `noop` processes with hyperfine, interleaved and with their order alternating, after untimed runs that leave the binary and the input in the page cache.

### Environment

- One image holds every CLI, the collector and the corpus, built from toolchains and base images pinned by digest and from libraries pinned by version, commit, lock file or package version. It builds for linux-x64 and linux-arm64, and on arm64 the `x86-64-v3` builds are absent and skipped.
- The container gets two CPUs (`--cpuset-cpus`), so the runtimes' background compiler and GC threads do not take the measured thread's CPU, and 4 GB of memory.
- .NET runs workstation non-concurrent GC with a 1 GiB heap limit and a 32 MiB generation 0 budget, and the JVM runs G1 with a fixed 1 GiB heap. Both are set because both runtimes size their collector from the container's limits (the JVM picks SerialGC on one CPU) or from the CPU's cache. Go keeps its defaults (`GOGC=100`, `GOMAXPROCS` from the container's CPUs), since it does not size its collector from the container's memory.
- Every result records the image ID, the commit, `lscpu`, the kernel and the .NET settings.
- The `Cross-language benchmark` workflow runs on `workflow_dispatch` only, on ubuntu-24.04 and ubuntu-24.04-arm. It builds the image in every run and uploads the results as an artifact, with `run.md` in the job summary. Its inputs choose the CLIs, the entries, the rounds, the cold first call and the outside check.

### Results

The summary is one run of every CLI: [37392043344](https://github.com/guitarrapc/FeatherQR/actions/runs/37392043344) at b6db98b (2026-10-06), five rounds, on an AMD EPYC 7763 (Zen 3) and a Neoverse-N2. Each runner has four vCPUs, on x64 two cores of two threads. The EPYC 7763 has AVX2 and BMI2 but neither GFNI nor AVX-512, so FeatherQR ran its tiers without GFNI there. Each cell is the median over a shape's entries of the library's median over FeatherQR's on the JIT, so above 1 is slower. An empty cell is an operation the library does not offer or was not run.

The run on linux-x64 (EPYC 7763) gave:

| Library | QR encode | QR matrix | QR image | Micro QR encode | Micro QR matrix | Micro QR image | rMQR encode | rMQR matrix | rMQR image |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| zxing-cpp | 43 | 13 | 4.5 | 26 | 8.2 | 5.1 | 25 | 18 | 7.1 |
| libzint | 20 | | | 9.4 | | | 11 | | |
| ZXingCpp package | 39 | | 4.6 | 27 | | 5.4 | 26 | | 7.3 |
| quirc | | 9.7 | 37 | | | | | | |
| zbar | | | 53 | | | | | | |
| libqrencode | 20 | | | 7.0 | | | | | |
| rqrr | | 212 | 207 | | | | | | |
| fast_qr | 29 | | | | | | | | |
| qrcode crate | 142 | | | 16 | | | | | |
| BoofCV | | | 28 | | | 36 | | | |
| ZXing | 43 | 12 | 9.1 | | | | | | |
| gozxing | 112 | 18 | 12 | | | | | | |
| go-qrcode | 131 | | | | | | | | |

The run on linux-arm64 (Neoverse-N2) gave:

| Library | QR encode | QR matrix | QR image | Micro QR encode | Micro QR matrix | Micro QR image | rMQR encode | rMQR matrix | rMQR image |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| zxing-cpp | 26 | 11 | 3.0 | 17 | 7.0 | 3.5 | 21 | 13 | 5.1 |
| libzint | 17 | | | 6.1 | | | 8.0 | | |
| ZXingCpp package | 24 | | 3.1 | 17 | | 3.8 | 22 | | 5.2 |
| quirc | | 8.7 | 27 | | | | | | |
| zbar | | | 46 | | | | | | |
| libqrencode | 9.8 | | | 5.0 | | | | | |
| rqrr | | 147 | 115 | | | | | | |
| fast_qr | 14 | | | | | | | | |
| qrcode crate | 79 | | | 12 | | | | | |
| BoofCV | | | 24 | | | 27 | | | |
| ZXing | 31 | 12 | 8.7 | | | | | | |
| gozxing | 57 | 15 | 11 | | | | | | |
| go-qrcode | 76 | | | | | | | | |

FeatherQR on the JIT took, by shape, on x64 and then arm64: Standard QR encode 1.4 to 2.3 µs and 2.8 to 4.4 µs, matrix decode 0.54 to 1.1 µs and 0.45 to 0.93 µs, image decode 19 to 29 µs and 17 to 29 µs, Micro QR encode 0.28 to 0.33 µs and 0.29 to 0.39 µs, rMQR encode 0.30 to 1.8 µs and 0.28 to 1.5 µs.

The tables read with these limits:

- A ratio holds for its CPU model. Two runs on a Zen 3 and a Zen 5 put the same entry up to 45 % apart, in both directions (Lessons learned).
- Rebuilding a native library's binary, with no change to its code, moved entries by 5 to 18 %, and one by 36 % (Lessons learned). So a difference under about 10 % between two native libraries, or two builds of one, on a few entries is not a result. The qrcode crate's Standard QR cell is good to about 10 % at best, because its own speed switches between two levels.
- Two runs of one build on the development box differed by up to 2.8 % on an entry, so there a smaller difference between two libraries on one entry is not a result either.
- The qrcode crate's cells leave out two entries it encodes wrongly, and the ZXingCpp package's Standard QR encode leaves out the Unicode payload, which it cuts short. Verification kept all three out of the timing (Lessons learned).
- ZXing's image decode may read up to 10 % slow: in a 30 s trace it dropped about 10 % around 11 s, past its 10 s warmup floor. A 20 s floor for ZXing would put its window past that drop, at about three more hours of outside check, and was not set.

Runs on the development box (a Ryzen 9 7950X3D under Docker Desktop's WSL2 VM) found the following, unless a runner is named. Its ratios differ from the runners' as one CPU model's differ from another's: some came within a few percent of the EPYC 7763's, and gozxing's Standard QR encode read 83 there against 112.

- No reader measured decodes clean images near FeatherQR's speed. The fastest, zxing-cpp, takes 4.9 to 7.5 times its time by shape on the development box and 3.0 to 7.1 on the runners. rqrr, expected at the fast end, is the slowest. quirc decodes the images in 0.16 of rqrr's time and the matrices in 0.03 to 0.07 of it, so rqrr's speed is mostly the port's, but quirc itself takes 38 times FeatherQR's time on the development box and 27 to 37 on the runners.
- fast_qr, the speed-focused writer, takes 29 to 32 times FeatherQR's time on x64. Its own README puts its version 3 encode at 82 µs (level H, on its machine), the same order as the 36 µs here, so the harness is not the cause.
- libzint and libqrencode are the fast end of the native writers. On x64 they are level on Standard QR (libqrencode 0.98 of libzint's time) and libqrencode is faster on Micro QR (0.74). On arm64 libqrencode takes 0.59 of libzint's time on Standard QR.
- zxing-cpp's writer takes 1.9 to 3.2 times libzint's time for the buffer, the copy and the `Barcode` it adds around libzint's encode. Its bundled libzint is compiled with `-Os`, but a libzint built with `-Os` timed within 3 % of the `-O3` one.
- The ZXingCpp package's wrapper costs 1.02 to 1.09 times zxing-cpp's time on image decode (median 1.03, 0.7 to 4.8 µs a call) and nothing measurable on Micro QR and rMQR encode. On Standard QR encode the same native code ran 6 to 8 % faster inside the .NET process, for a reason not known. The package's own binary ran within 6 % of the one built here on every entry.
- gozxing takes 1.28 times ZXing's time on image decode, 1.50 on matrix decode and 1.84 on encode, so the same code runs faster on the JVM than compiled ahead of time by Go.
- `x86-64-v3` sped rqrr's image decode by 28 to 32 % and quirc's by 17 to 18 %, on every entry. zxing-cpp's matrix decodes moved by up to 17 %, and the other builds' medians by 8 % or less, which a rebuild alone also moves (Lessons learned).
- The cold first call, from the same CI run on x64: the C, C++ and Rust CLIs start in 0.8 to 3.0 ms and the Go CLIs in 1.2 to 1.5 ms. Their first call takes 0.8 ms or less at the median, zbar's 1.7 ms, and rqrr's image decodes about one call's time (4 to 6 ms). NativeAOT starts FeatherQR in 2.8 to 3.4 ms with a first call of 0.4 ms at the median. The JIT starts it in 41 to 45 ms with a first call of 35 ms at the median (19 to 68 ms by entry). The ZXingCpp package starts with the .NET runtime in 38 to 42 ms, ZXing in 107 to 115 ms and BoofCV in 226 to 262 ms, with first calls of 4, 21 and 68 ms at the median. On the development box FeatherQR on the JIT started in 22 to 29 ms and made its first call in 11 to 38 ms, ZXing's JVM started in 60 to 64 ms and BoofCV's in 114 to 140 ms.

What NativeAOT's default target costs, and why, is under [NativeAOT's instruction-set target](qrcode-symbologies.md#nativeaots-instruction-set-target).

### How well it measures

| Machine | Range of an entry's process medians over their median, median over the entries | Two runs against each other |
|---|---:|---|
| Development box (Ryzen 9 7950X3D, Docker Desktop on WSL2) | 2.5 to 4.5 % | Absolute medians 1.3 % apart at the median and 2.8 % at most on a quiet day, ratios within the tolerance |
| Apple M2 (Docker Desktop on macOS) | 15 to 16 % | Absolute medians 4.0 to 5.4 % apart at the median |
| Neoverse-N2 runners | 0.4 to 0.5 % | Ratios within 3.4 % on every entry, two runs measuring the same code |
| EPYC 7763 runners | 0.8 % | Ratios within the tolerance for every CLI a rebuild did not move (below) |
| EPYC 9V45 runner | 3.2 % | |

No two x64 runs share both a CPU model and a commit. The two EPYC 7763 runs were a rebuild apart (2026-10-05 and 2026-10-06), and every CLI agreed between them except zxing-cpp and libzint, which the rebuild moved, and ZXing, whose matrix decode was corrected in between. On that and on the arm64 pair, the runners were accepted as reproducing ratios (2026-10-06).

The first measurement (2026-10-02, the development box) set the tolerance. FeatherQR's self-timed medians agreed with BenchmarkDotNet over the same calls within 0.6 to 0.8 % at the median (6.3 % at most), and with the outside check within 1.0 % at the median (6.5 % at most). Every disagreement above 3.6 % was on a call under 0.3 µs. Later, BenchmarkDotNet over the ZXingCpp package agreed with that package's CLI within 0.5 to 1.8 % at the median (6.0 % at most).

Since then the outside check has found no CLI's loop at fault. Where an entry fell beyond 7 % or a signed median beyond 2 %, a cause outside the loop explained it:

- The M2's noise. Its entries scattered as far as their own processes did.
- Drift between the check and the run it was held against. It was gone against the run on the check's other side, or against the two runs' geometric mean.
- ZXing's warmup drop past its floor.
- The qrcode crate's two speeds.

## Why

### Why a CLI per library with the clock inside

Each language's benchmark framework computes a different statistic, so their numbers do not compare. BenchmarkDotNet subtracts a measured overhead and averages after removing outliers, criterion fits a slope over growing iteration counts, Go's `testing.B` averages one run grown to a second, and JMH averages over forked processes. A few percent between two libraries is within the disagreement between two frameworks. Timing each library's CLI from outside is uniform, but process start, runtime start and JIT far outweigh a decode of tens of microseconds. So the CLI stays, the clock moves inside it, and the loop and the statistics are shared. The first measurement tested the method: if a plain timing loop could not reproduce BenchmarkDotNet's numbers for this library, no other row could be trusted.

### Why an outside check

A CLI's own loop can be wrong: a batch too short for the clock, a warmup that ends before tiering does, or work the compiler removed. The difference of two whole-process times needs no timing code in the CLI, and everything fixed cancels, so it checks the loop without trusting it.

### Why one Docker image

Toolchains, library versions and build flags are then the same wherever it runs. C++ was avoided for test oracles because its builds depend on the machine, which does not apply inside an image that builds once from a pinned base and pinned commits.

### Why only ratios within one run

Hosted runners change CPU model between jobs, and a machine drifts by several percent between runs. Libraries run interleaved in one container on one CPU set, so a ratio between two of them in one run sees the same machine. Absolute times and ratios from different runs are compared only on the same CPU model and the same commit.

### Why the version and level are pinned

A writer that picked another version would be timing another symbol. Every writer is held to the version and level FeatherQR chose, verification rejects a symbol in any other, and each library still chooses its own segments and mask.

### Why each CLI may raise its warmup

A JVM library's warmup is a property of the library, not of the JVM: ZXing's encode was steady within 3 s, BoofCV's image decode only after 8 to 9 s, and ZXing's image decode later still. A fixed warmup for every CLI is either too short for the slowest or too long for the rest, and every CLI at 10 s more than tripled a run.

## Decisions

| Decision | Choice | Why |
|---|---|---|
| Naming other libraries in code | Allowed in `tools/CrossLanguageBenchmark/` only (2026-10-02). Code and XML docs elsewhere never name other libraries | Every CLI in the folder wraps one |
| Inputs per entry | One input per entry, as the BenchmarkDotNet projects use | Rotating many inputs in random order would make a branch on the data cost what it does on real input, but it would change every number. A gap on a single entry is worth one run with the inputs rotated before it is read as code generation (Lessons learned). Not decided further |
| The tolerance | Every entry within 7 % of its outside check, the signed median within 2 % | Set from the first measurement's spread. The second test was first the median of the disagreements' sizes, which measures noise rather than bias |
| A disturbed outside measurement | Taken again, up to three attempts, on a range above 20 % or a fixed cost below −3 % of T(N) | A fixed cost cannot be negative, so a negative one beyond the noise means the longer runs caught more of the load |
| JVM settings | G1 with a fixed 1 GiB heap, warmup floor 10 s, batch floor 100 ms, for the JVM CLIs only | G1 is what a server JVM runs by default. ParallelGC swung least and G1 added bumps of about 10 %, but all three collectors showed the same warmup drop |
| zbar and libqrencode | Ubuntu's packages, one build | They are measured as distributions ship them. A source build would give an `x86-64-v3` column of a build nobody runs |
| Go | Static binaries without cgo, Go's GC defaults | Go does not size its collector from the container |
| The image in CI | Built in every run, neither cached nor published | It takes 2 to 4 minutes against hours of measuring, and every result records the image ID it ran |
| CI workflow | Manual dispatch only, outside check off by default, a failed verification a warning, only a failure to measure fails the job | A full run takes hours, a runner answers a question rather than gating a change, and the ZXingCpp package's Unicode encode always fails verification |
| Comparisons with other libraries | Kept in CI artifacts and this record's summary, never the README | The README states what the library does. A ratio holds for one CPU model. What NativeAOT's target costs this library is in the README, since the application chooses the target |

## Lessons learned

### Measuring

- One process is not a measurement. In the first run an entry's process medians ranged up to 19.9 % apart, while the median of five agreed with BenchmarkDotNet within 6.3 % and with the next run within 2.8 %.
- The warmup has to outlast tiering. With a 300 ms warmup the median batch ran 61 % of its 20 ms target and the shortest 30 %, because the warmup's second half was still slower than the batches after it. With 3 s, the median batch ran 98 % of the target.
- FeatherQR's fixed cost on the JIT (process start, runtime start, corpus load, JIT) was 193 to 325 ms, against about 1 ms under NativeAOT and a few milliseconds for a Rust or C process. Timed whole, a process running a 1 µs call 300,000 times would measure that fixed cost as much as the calls, which is why the outside check takes a difference.
- Load that comes and goes lands on whichever command is running and moves the fixed cost the other way. The measurements that read 4.5 % or more high had fixed costs of −23 to −523 ms, and those that agreed within 3 % had −29 to +54 ms, which made a negative fixed cost a sign of disturbance. Under NativeAOT, whose true fixed cost is about 1 ms, it flagged load as intended: three of 66 measurements read −31 to −47 ms, and measured again they came within 2.3 % of the self-timed medians.
- The minimum of the process times is not the cure. It put FeatherQR's entries up to 8.0 % off against 6.5 % for medians, because a JIT process's speed varies from process to process and minima pair a fast N process with a fast 2N one.
- The outside check's premise is checked, not assumed: N has to contain the warmup. A fixed-cost estimate cannot show a warmup longer than N, which reads as a per-call cost. BoofCV failed with a 4 s N until N followed its warmup.
- The outside check's offset from the self-timed median is drift between the two measurements. Every CLI, native ones included, read low against the run before the check and high against the run after it, so the check is held against the run nearest in time, or against both runs' geometric mean.
- Verifying one call does not verify the timed ones. A library can change its input: ZXing's matrix decoder unmasks the matrix in place, and in every run from the CLI's first until the check was added, on the development box and in CI, the calls after the first timed its failure path. That was every such call on four entries and three in four on the numeric one, at 11 to 32 µs, longer than the 1.4 to 9.6 µs decode. The numbers looked plausible. Counting the calls that fail costs one comparison per call and found it on its first run (2026-10-05).
- A benchmark that repeats one input lets the branch predictor learn a branch on the data. It can understate that branch's cost several times over and show a gap between two builds that real input does not have, as Micro QR's codeword extraction did ([microqr-decoder.md](microqr-decoder.md)).
- A native binary's time depends on where its code lands, and rebuilding moves it even when the library's code does not change. Changing only the harness's code moved zxing-cpp's matrix decodes by 5 to 14 % and one libzint entry by 36 % on Zen 3, and rqrr's entries by up to 18 % on arm64, while one build's processes agreed within 0.3 %. The old and new images alternated on the development box repeated the zxing-cpp shift. So runs are held against each other only on the same commit, and a difference under about 10 % between two builds of a native library on a few entries, such as 4 to 8 % for libzint at `x86-64-v3`, is not a result about the build flags.
- A ratio is a property of the CPU as well as of the libraries. A Zen 3 and a Zen 5 runner put default NativeAOT's Standard QR encode over the JIT at 1.93 and 2.19, libzint's Standard QR encode over FeatherQR at 21 and 17, and rqrr's matrix decode at 206 and 290.
- A library's own speed can switch between levels. The qrcode crate's Standard QR encode of the Wi-Fi payload held about 130 µs or about 160 µs for 100 to 400 ms at a time in every process, which switching off glibc's heap trimming and dynamic mmap threshold did not change. Neither the self-timed median nor the outside check then resolves it to 7 %, and its rows carry the width of the switch.

### Runtimes

- The JVM keeps getting faster for seconds after a .NET or native CLI is steady, and its per-call time swings by about 5 % from second to second. Traced over 15 to 30 s, BoofCV's Standard QR decode stayed near 400 µs for 8 to 9 s and then near 355 µs. Lowering C2's tier 4 thresholds tenfold did not visibly move ZXing's drop, but the box was busy during that test, so whether it follows call counts or seconds is not settled.
- The timed call reads its input through a volatile field on the JVM, since C2 can inline the call into the loop and could otherwise treat the input as constant. The C++ CLIs pass the input through an empty `asm` barrier and Rust through `black_box`. Go calls through a func value, which its compiler does not inline into the loop.
- Under the JIT, FeatherQR's first call costs as much as the runtime's start or more, and Standard QR's costs more than Micro QR's. Under NativeAOT, start and first call together take about 1 to 4 ms.
- `-p:PublishAot=true` on the command line reaches the library's netstandard2.0 build as a global property and fails it (NETSDK1207), so the CLI switches NativeAOT on through a property of its own.

### Libraries and wrappers

- A library's writer can be another library. zxing-cpp's encode is libzint's with more work around it.
- A wrapper's options have to be matched before its cost is read. The ZXingCpp package's reader looks for every symbol unless told to stop at one.
- A package's native binary has to be swapped for one built from the same commit and flags before the difference is the wrapper's. Here the swap changed nothing, but that was a measurement, not an assumption.
- Verification found bugs a benchmark would have hidden. The ZXingCpp package passes a string's UTF-16 length as its UTF-8 byte count, so the native writer reads a prefix: on Linux the Unicode payload was silently cut after its first Cyrillic letters, and on Windows the cut fell inside a character and threw. Its byte overload takes the UTF-8 whole but writes it as binary data (ECI 899). The qrcode crate's segment optimiser takes byte pairs in Shift JIS's double-byte ranges for Kanji, so it wrote UTF-8 Chinese and Japanese as Kanji, which zxing-cpp read as other text and FeatherQR did not decode. Its M3-L symbol for `HELLO WORLD 14` decodes in neither zxing-cpp nor FeatherQR, while libzint's and libqrencode's do in both.

### Machines

- A box's noise has to be measured on that box before its per-entry ratios are read. An Apple M2 measured about four times noisier than the development box. The runs did not separate the causes: desktop apps took about 1.5 of its 8 cores, and Docker Desktop's VM CPUs are host threads macOS can move between cores of either kind, so `--cpuset-cpus` pins the measured thread to a VM CPU, not to a core. There only ratios over many entries, and gaps beyond about 25 %, were results. A hosted runner measured as quietly as the development box or more.
- Running anything on the measured CPUs during a run contaminates it. A 35 s experiment during a run doubled to quadrupled the experiment's own times, and the run was repeated.
- Stopping the shell that ran `docker run` leaves its container running on the measured CPUs, so the measuring scripts name their container.
- Through Docker Desktop's Windows bind mount (virtiofs), a file written a moment before was intermittently read back as zero bytes, a copied file arrived with bytes changed, and a source file just saved on the host was read with NUL bytes. Results leave a named volume through a container's standard output (`tar cf -`), and sources enter through standard input. Linux runners mount natively.
- One run of every CLI took 2 hours 48 minutes on the x64 runner and 2 hours 3 minutes on arm64 at five rounds. The outside check takes about 40 s per entry and CLI at a 1 s N, and a JVM CLI's entries six minutes each at a 10 s N, so in CI it is run narrowed to the CLIs in question.
