# Measuring against readers and writers outside .NET

## Purpose

`FeatherQR.Benchmark` compares this library with other .NET libraries under BenchmarkDotNet. The most widely used libraries are not .NET: zxing-cpp, the Rust crates, the JVM readers. This plan compares their speed with this library's on the same machine and inputs.

Each language's benchmark framework computes a different statistic, so their numbers do not compare. BenchmarkDotNet subtracts a measured overhead and averages after removing outliers, criterion fits a slope over growing iteration counts, Go's `testing.B` averages one run grown to a second, and JMH averages over forked processes. A few percent between two libraries is within the disagreement between two frameworks.

Timing each library's CLI from outside is uniform, but process start, runtime start and JIT far outweigh a decode of tens of microseconds.

So the plan keeps the CLI, moves the clock inside it, and shares the loop and the statistics:

- Every library gets a small CLI with one protocol: read a corpus, verify, warm up, time batches, print raw samples.
- The timing loop is the same few lines in every language, with no framework.
- One collector computes every statistic, so every row uses the same formula.
- An outside check that trusts no timing code validates each CLI's loop (below).
- One Docker image holds everything, so toolchains, library versions and build flags are the same wherever it runs.

## What was already measured

NativeAOT's default build drops this library's AVX2 tier. A probe on the Windows box (Ryzen 9 7950X3D, .NET 10, ILCompiler 10.0.9, win-x64) printed what the code sees at run time:

| Build | `Avx2` | `Bmi2.X64` | `Gfni` | `Avx512F` | `Vector256` accelerated | `Vector<byte>.Count` |
|---|---|---|---|---|---|---|
| JIT | true | true | true | true | true | 32 |
| NativeAOT, default | false | false | true | false | false | 16 |
| NativeAOT, `IlcInstructionSet=x86-64-v3` | true | true | true | false | true | 32 |
| NativeAOT, `x86-64-v4` or `native` | true | true | true | true | true | 32 |

The default target is the OS's minimum instruction set: `IsSupported` for AVX2, BMI2 and AVX-512 reads false even on a CPU that has them, while GFNI is still detected at run time. This library dispatches on `Avx2.IsSupported` at 31 sites and on `Vector256.IsHardwareAccelerated` at 29, so a default NativeAOT publish runs the SSSE3 or scalar tier of each of those kernels, and the BMI2 placers never run.

With AVX but not AVX2 in the target (`x86-64-v2,avx`), `Avx2`, `Bmi2` and `Fma` become run-time checks and read true, so the kernels gated on `Avx2.IsSupported` come back. `Vector256.IsHardwareAccelerated` stays false, so the kernels gated on it do not. The [instruction-set probe](references/nativeaot-instruction-set-probe.md) repeats this on the current code (2026-10-02) and adds the startup check a target triggers on a CPU that lacks it, and each kernel's tier.

A rough first measurement (same box, loaded by other work, median of 21 batches, three interleaved rounds, to be replaced by phase 3) found default NativeAOT 2.6x slower than JIT on a URL-sized Standard QR encode, about 4x on a clean image decode and on rMQR matrix decode, and 1.5 to 2.6x elsewhere. `x86-64-v2,avx` recovers most of the encode gap and almost none of the image decode gap. `x86-64-v3` and `native` are within the box's noise of JIT on every shape. The target is the application's compile setting, so no library-side switch reaches a default publish. The library controls its 128-bit tiers, which default NativeAOT, ARM64 and WebAssembly all run. The README calls NativeAOT fully supported and does not mention this.

## Scope

| In | Out |
|---|---|
| Speed of encode (payload to module matrix) and decode (matrix to text, and 8-bit grey image to text), single thread, steady state | Accuracy. Read rates do not depend on timing or language and stay with `tools/QRImageDecodeSweep`, which can later add readers through the same CLIs |
| Cold first call as a separate metric: process start to first result, minus the same CLI started with nothing to do | Rendering to PNG/SVG, file decoding, colour conversion. Every library draws differently, and none of it is QR work |
| FeatherQR under JIT and NativeAOT (default and `x86-64-v3`) | Multi-threaded throughput |
| linux-x64 and linux-arm64, in one Docker image built for both | Windows and macOS hosts, where Docker runs a Linux VM and an amd64 image on Apple silicon is emulated. Neither number is used |
| A manual CI workflow that builds the image and runs the set | Running it per PR. It is slow, the runner is noisy, and it answers a question rather than gating a change |

## Libraries

| Language | Library | Measured | Symbologies | Why |
|---|---|---|---|---|
| C++ | zxing-cpp | decode, encode | QR, Micro QR, rMQR | The usual reference reader and the accuracy yardstick |
| C | libzint | encode | QR, Micro QR, rMQR | One of few Micro QR and rMQR writers |
| Rust | rqrr | decode | QR | A quirc port: the fast end for clean images |
| Rust | fast_qr, qrcode | encode | QR (qrcode also Micro QR) | Speed-focused writers |
| Java | BoofCV | decode | QR, Micro QR | Strong reader. JIT against JIT |
| Java | ZXing | decode, encode | QR | The original, widely deployed |
| Go, C | gozxing, go-qrcode, libqrencode, zbar | as listed | QR (libqrencode also Micro QR) | Only if the first set leaves a question |

C++ was avoided for test oracles because its builds depend on the machine. That does not apply inside the image, where the build runs once, on a pinned base, from a pinned commit.

## What has to stay true

- Every library times the same unit of work. Decode starts from bytes in memory: a module matrix, or an 8-bit grey image read from a binary PGM before timing (every language reads PGM in a few lines, so no image library enters the loop). Encode ends at a module matrix. Version and error correction level are pinned. Each library chooses its own mask.
- An encode row counts only when every library chose the same symbol version for that payload.
- Each CLI prints its results for verification before timing: the decoded text, or the encoded matrix, which the collector reads back with FeatherQR's matrix decoder. An input a library cannot read is reported as unreadable and not timed, because a failure path has a different cost. The loop folds each result into a printed checksum, so no compiler can drop the work.
- Multi-format readers are restricted to the symbology under test, as this library's decoders are per symbology. Other options stay at their defaults.
- Build flags are stated in two columns: the shipped defaults (Rust and Go target baseline x86-64, NativeAOT the OS minimum), and every native build at `x86-64-v3`. .NET JIT and the JVM pick instructions on the host and have one column.
- .NET and the JVM size their GC from the container's CPU and memory limits (the JVM picks SerialGC on one CPU), so both get an explicit GC mode and heap, and the container gets a fixed CPU set and memory limit.
- Only ratios within one run are used, because hosted runners change CPU model between jobs. Libraries run interleaved (A, B, C, A, B, C) in one container on one CPU set, and each result records the image digest, `lscpu` and the kernel.

## The outside check

A CLI's own loop can be wrong: a batch too short for the clock, a warmup that ends before tiering does, or work the compiler removed. So each CLI also runs N and 2N iterations as whole processes, timed from outside with hyperfine. T(2N) minus T(N) is N iterations of work, and everything fixed (process start, runtime start, corpus load, JIT) cancels if warmup finishes within N. When the difference disagrees with the self-timed median beyond the spread measured in phase 1, the CLI's loop is suspect.

## Phases

Each phase appends a Progress log entry with Done / Lessons / numbers.

| # | Priority | Phase | Contents | Exit |
|---|---|---|---|---|
| 1 | P0 | Protocol and FeatherQR | The protocol (arguments, JSON), the collector, the corpus, a FeatherQR CLI on the core package, the Docker skeleton, the outside check | The CLI's medians agree with BenchmarkDotNet's `Simple*` rows (same inputs, same machine) and with the outside check, within a spread stated from the runs, which becomes the tolerance for every later CLI |
| 2 | P0 | First outside library | rqrr and fast_qr in Rust, Cargo.lock pinned, both flag columns | Verified, outside check passes, interleaved rows against FeatherQR |
| 3 | P1 | NativeAOT | FeatherQR as JIT, NativeAOT default and NativeAOT `x86-64-v3`, steady state and cold start. The probe above repeated on linux-x64 and linux-arm64 | The gap between the AOT arms measured per shape, and the kernels behind the default arm's gap listed with the tier they fall to (128-bit or scalar) from the [tier table](../specs/qrcode-simd-tiers.md). A separate change documents `IlcInstructionSet` for NativeAOT users in the README and user docs, with the number behind it. Kernels that fell to scalar got 128-bit tiers for 2.0.0 ([the 128-bit round](../specs/qrcode-symbologies.md#the-128-bit-round)) |
| 4 | P1 | zxing-cpp and libzint | Built in the image from pinned commits. All three symbologies. The same inputs through the ZXingCpp NuGet under BenchmarkDotNet, to measure the wrapper's cost | Verified rows for QR, Micro QR and rMQR, and the wrapper's cost stated |
| 5 | P2 | JVM | BoofCV and ZXing, Maven-pinned, GC and heap set, warmup long enough for C2 | As phase 2 |
| 6 | P2 | CI | A `workflow_dispatch` workflow on ubuntu-24.04 and ubuntu-24.04-arm, image cached, results uploaded as artifacts | Two runs of the same commit agree within the phase 1 tolerance on ratios |
| 7 | P3 | Others | Go and apt-packaged C libraries, only for a question the first set left open | Per library, as phase 2 |
| 8 | P2 | Fold | Method, decisions and lessons into a spec. Results stay in CI artifacts and the spec's summary, never the README. This plan deleted | Nothing is only here |

Phase 1 comes first because it tests the method: if a plain timing loop cannot reproduce BenchmarkDotNet's numbers for this library, no other row can be trusted.

## Open decisions

- The harness is proposed to live in `tools/CrossLanguageBenchmark/`, with one folder per language plus the Dockerfile and collector, next to `QRImageDecodeSweep`, which already runs non-.NET tools.
- Code and XML docs under `src/` never name other libraries, but the harness code cannot avoid it. Whether that rule covers `tools/` is undecided.
- Pushing the image to GHCR by digest makes every result traceable to exact binaries, but publishes an image under the repository. The alternative is to rebuild it in each run from the build cache and record the digest without publishing it.

## Progress log

(none yet)
