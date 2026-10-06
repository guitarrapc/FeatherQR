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

A rough first measurement (same box, loaded by other work, median of 21 batches, three interleaved rounds, before the 128-bit tiers) found default NativeAOT 2.6x slower than JIT on a URL-sized Standard QR encode, about 4x on a clean image decode and on rMQR matrix decode, and 1.5 to 2.6x elsewhere. `x86-64-v2,avx` recovers most of the encode gap and almost none of the image decode gap. `x86-64-v3` and `native` are within the box's noise of JIT on every shape. Phase 3 replaced it (Progress log): with the 128-bit tiers, default NativeAOT runs 1.03 to 2.00 times the JIT by shape, Standard QR encode the most, and `x86-64-v3` 0.99 to 1.23. `x86-64-v2,avx` was not measured again. The target is the application's compile setting, so no library-side switch reaches a default publish. The library controls its 128-bit tiers, which default NativeAOT, ARM64 and WebAssembly all run. The README calls NativeAOT fully supported and does not mention this.

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
| Rust | fast_qr, qrcode | encode | QR (qrcode also Micro QR) | fast_qr is speed-focused, and qrcode is the most downloaded (phase 7) |
| Java | BoofCV | decode | QR, Micro QR | Strong reader. JIT against JIT |
| Java | ZXing | decode, encode | QR | The original, widely deployed |
| C | quirc | decode | QR | rqrr's original, built from source: whether rqrr's speed is quirc's method or the port (phase 7) |
| C | zbar, libqrencode | zbar decode, libqrencode encode | QR (libqrencode also Micro QR) | The reader and writer Linux distributions ship, linked against Ubuntu's packages (phase 7) |
| Go | gozxing, go-qrcode | gozxing decode and encode, go-qrcode encode | QR | A garbage-collected runtime compiled ahead of time. gozxing ports ZXing, so against ZXing it separates the language from the code (phase 7) |

C++ was avoided for test oracles because its builds depend on the machine. That does not apply inside the image, where the build runs once, on a pinned base, from a pinned commit.

## What has to stay true

- Every library times the same unit of work. Decode starts from bytes in memory: a module matrix, or an 8-bit grey image read from a binary PGM before timing (every language reads PGM in a few lines, so no image library enters the loop). Encode ends at a module matrix. Version and error correction level are pinned. Each library chooses its own mask.
- An encode row counts only when every library chose the same symbol version for that payload.
- Each CLI prints its results for verification before timing: the decoded text, or the encoded matrix, which the collector reads back with FeatherQR's matrix decoder. An input a library cannot read is reported as unreadable and not timed, because a failure path has a different cost. The loop folds each result into a printed checksum, so no compiler can drop the work.
- Multi-format readers are restricted to the symbology under test, as this library's decoders are per symbology. Other options stay at their defaults.
- Build flags are stated in two columns: the shipped defaults (Rust and Go target baseline x86-64, NativeAOT the OS minimum), and every native build at `x86-64-v3`. .NET JIT and the JVM pick instructions on the host and have one column, and so do libraries linked from a distribution's packages (zbar, libqrencode), whose shipped build is the distribution's.
- .NET and the JVM size their GC from the container's CPU and memory limits (the JVM picks SerialGC on one CPU), so both get an explicit GC mode and heap, and the container gets a fixed CPU set and memory limit.
- Only ratios within one run are used, because hosted runners change CPU model between jobs. Libraries run interleaved (A, B, C, A, B, C) in one container on one CPU set, and each result records the image digest, `lscpu` and the kernel.

## The outside check

A CLI's own loop can be wrong: a batch too short for the clock, a warmup that ends before tiering does, or work the compiler removed. So each CLI also runs N and 2N iterations as whole processes, timed from outside with hyperfine. T(2N) minus T(N) is N iterations of work, and everything fixed (process start, runtime start, corpus load, JIT) cancels if warmup finishes within N. When the difference disagrees with the self-timed median beyond the spread measured in phase 1, the CLI's loop is suspect.

## Protocol

The harness lives in `tools/CrossLanguageBenchmark/`: the Dockerfile, `clis.tsv` (each CLI's name and command), the collector, and one folder per language. `dotnet/cli/Protocol.cs` is the reference implementation of the protocol, which FeatherQR's CLI and the ZXingCpp package's CLI share, and its comments carry the details. `rust/src/lib.rs`, `cpp/protocol.hpp`, `jvm/src/main/java/xlang/Protocol.java` and `go/protocol/protocol.go` port it. Code and XML docs elsewhere never name other libraries, but this folder's code does, because every CLI in it wraps one (decided 2026-10-02, for this folder only).

The collector's `corpus` command writes the inputs and `manifest.tsv`, one line per operation on a case. The cases are the payloads and symbols of the benchmark project's `Simple*` classes: five Standard QR payloads, three Micro QR and three rMQR, each encoded, decoded as a matrix and decoded as an image, 33 entries in all. FeatherQR encodes them, and the corpus command fails if FeatherQR cannot read its own inputs back.

Every CLI is called as `<cli> <mode> <op> <symbology> <input> [--ecc E] [--version V]` and prints one JSON object on stdout.

- `op` is `encode`, `decode-matrix` or `decode-image`, and `symbology` is `qr`, `microqr` or `rmqr`. Encode is pinned to the level and version given (`3`, `M2`, `R7x43`). Decode takes neither.
- The encode input is the payload's UTF-8 bytes. The matrix input is a binary PGM of the bare symbol, one pixel per module, 0 for dark. The image input is a binary PGM of the symbol drawn at 8 pixels per module inside its specified quiet zone.
- `run` makes one call and prints its result for verification. A call that fails is reported and not timed, since failing has a cost of its own. It then warms up for 3 s and at least 3 calls, sizes a batch to 20 ms from the warmup's second half, and times 30 batches with a monotonic clock. It prints each batch's nanoseconds and its call count. A CLI may raise the warmup and the batch length to floors of its own, as the JVM CLIs do (10 s and 100 ms, set in `clis.tsv`).
- `fixed` makes N calls and prints only the checksum, for the outside check. `cold` makes one call and prints its result. `noop` loads the input and makes no call. The collector times the last two from outside, and their difference is the cold first call.
- `status` is `ok`, `failed` (no decode, or the encoder refused) or `unsupported` (the library has no such operation). A decode prints its text as UTF-8 in hex, so no CLI needs a JSON string escaper. An encode prints its matrix as rows of 0 and 1, with whatever quiet zone the library returns.
- `build` names how the CLI was compiled: Rust's flags, or for FeatherQR `jit` or `nativeaot` with its instruction-set target. FeatherQR's CLI also prints `isa`, the instruction sets its code sees, so every run repeats the probe above on the machine it ran on.
- Every call's result is folded into the printed checksum through its content (the text's length and last character, or the matrix size and its centre module), so no compiler can drop the work.
- A call's fold is 0 only when the call fails, so `run` and `fixed` count those calls and print the count as `failedCalls`. Verification sees one call, and a library can fail the calls after it: ZXing's matrix decoder unmasks the matrix it is given and leaves it so, and decoding the same matrix again failed (phase 7).

The collector's `run` command launches one process per entry, CLI and round, and rotates which CLI goes first in each round. It rejects a process whose verification fails, or any of whose timed calls failed: a decode must print the payload, and an encoded matrix, cut to its dark modules' bounding box, must decode through FeatherQR's matrix decoder to the payload in the pinned version and level. A process's per-call times are its batches' times over their calls, and an entry's median is the median of its process medians. `outside` checks that three calls of `fixed` succeed, sizes N to one second of calls from that median, or to the CLI's whole warmup where the CLI raised it, and runs hyperfine with no shell, in rounds whose command order alternates. A measurement is disturbed by other load, and taken again up to three attempts, when its per-call time is not positive, when its range (the quartiles of T(2N) against those of T(N)) exceeds 20 % of the call, or when its fixed cost, T(N) minus the N calls, is below zero by more than 3 % of T(N). A fixed cost cannot be negative, so a negative one means the longer 2N runs caught more of the load. `compare` holds the self-timed medians of the run it is given against the outside check and BenchmarkDotNet, and judges each CLI by the phase 1 tolerance. An entry whose fixed mode failed fails the verdict. An entry still disturbed after its attempts stays out of it, and the verdict then reads inconclusive. `cold` verifies each entry's `cold` output as `run` does, then times the `cold` and `noop` processes with hyperfine, interleaved as `run` is and with their order alternating. Untimed runs first leave the binary and the input in the page cache, so the difference is the runtime's and the library's first call, not the disk's.

The container gets two CPUs (`--cpuset-cpus`), so the runtime's background compiler and GC threads do not take the measured thread's CPU, and 4 GB of memory. .NET runs workstation non-concurrent GC with a 1 GiB heap limit and a 32 MiB generation 0 budget, set in the image so that the container's limits and the CPU's cache size do not choose them.

## Phases

Each phase appends a Progress log entry with Done / Lessons / numbers.

| # | Priority | Phase | Contents | Exit |
|---|---|---|---|---|
| 1 | P0 | Protocol and FeatherQR | The protocol (arguments, JSON), the collector, the corpus, a FeatherQR CLI on the core package, the Docker skeleton, the outside check | The CLI's medians agree with BenchmarkDotNet over the same calls and inputs on the same machine, and with the outside check, within a spread stated from the runs, which becomes the tolerance for every later CLI |
| 2 | P0 | First outside library | rqrr and fast_qr in Rust, Cargo.lock pinned, both flag columns | Verified, outside check passes, interleaved rows against FeatherQR |
| 3 | P1 | NativeAOT | FeatherQR as JIT, NativeAOT default and NativeAOT `x86-64-v3`, steady state and cold start. The probe above repeated on linux-x64 and linux-arm64 | The gap between the AOT arms measured per shape, and the kernels behind the default arm's gap listed with the tier they fall to (128-bit or scalar) from the [tier table](../specs/qrcode-simd-tiers.md). A separate change documents `IlcInstructionSet` for NativeAOT users in the README and user docs, with the number behind it. Kernels that fell to scalar got 128-bit tiers for 2.0.0 ([the 128-bit round](../specs/qrcode-symbologies.md#the-128-bit-round)) |
| 4 | P1 | zxing-cpp and libzint | Built in the image from pinned commits. All three symbologies. The same inputs through the ZXingCpp NuGet under BenchmarkDotNet, to measure the wrapper's cost | Verified rows for QR, Micro QR and rMQR, and the wrapper's cost stated |
| 5 | P2 | JVM | BoofCV and ZXing, Maven-pinned, GC and heap set, warmup long enough for C2 | As phase 2 |
| 6 | P2 | CI | A `workflow_dispatch` workflow on ubuntu-24.04 and ubuntu-24.04-arm, image built in each run, results uploaded as artifacts | Two runs of the same commit agree within the phase 1 tolerance on ratios |
| 7 | P3 | Others | Go and apt-packaged C libraries, only for a question the first set left open | Per library, as phase 2 |
| 8 | P2 | Fold | Method, decisions and lessons into a spec. Results stay in CI artifacts and the spec's summary, never the README. This plan deleted | Nothing is only here |

Phase 1 comes first because it tests the method: if a plain timing loop cannot reproduce BenchmarkDotNet's numbers for this library, no other row can be trusted.

## Open decisions

- Whether each corpus entry rotates through many inputs in random order instead of one, so that a branch on the data costs what it does on real input. It would change every number, and the phase 3 finding is the only measurement of the effect.

## Progress log

### Phase 1: protocol and FeatherQR (2026-10-02)

Done:

- The harness described under Protocol: FeatherQR's CLI (`dotnet/cli`), the collector (`corpus`, `run`, `outside`, `compare`), a BenchmarkDotNet project over the CLI's calls (`dotnet/reference`), and the Dockerfile with the SDK image pinned by digest and hyperfine by package version. The three projects are in the solution, so CI builds them.
- The exit compares against that BenchmarkDotNet project instead of the `Simple*` rows. `Simple*`'s image rows start from an RGBA `SKBitmap` and convert it to grey inside the timed call, and `Simple*` needs SkiaSharp's native library, which the image does not carry. The project compiles the CLI's own source over the same corpus files, so a disagreement can come only from the loop or the statistics.
- Measured on the Windows box (Ryzen 9 7950X3D) in Docker Desktop's WSL2 VM (kernel 6.6.87.2), with two CPUs and 4 GB, .NET 10.0.12 and SDK 10.0.401, at commit 4f4fd4b. Two runs of five rounds each, with BenchmarkDotNet's default job at three launches between them, then the outside check against the first run (N sized to one second of calls, two rounds of five runs per command). Every one of the 33 entries verified in every process.

| Measure, over the 33 entries | Median | Largest |
|---|---:|---:|
| Range of an entry's five process medians over their median (run 1, run 2) | 4.0 %, 2.5 % | 19.9 %, 11.5 % |
| Run 2 against run 1 | 1.3 % | 2.8 % |
| Self-timed against BenchmarkDotNet (run 1, run 2) | 0.6 %, 0.8 % | 4.8 %, 6.3 % |
| Outside check against self-timed (run 1) | 1.0 % | 6.5 % |

Every disagreement above 3.6 % was on a call under 0.3 µs: Micro QR's numeric matrix decode, rMQR's numeric encode and matrix decode, and rMQR's alphanumeric encode. The rMQR byte matrix decode sat 3 % below both other measurements in both runs.

The tolerance for every later CLI: each entry's self-timed median is within 7 % of its outside check, and the signed median of those disagreements over a CLI's entries is within 2 % (−0.9 % here). The first bounds a broken loop on one entry, the second a bias over all entries, such as a cost per call. Phase 2 changed the second test from the median of the disagreements' sizes, which measures noise rather than bias. Two runs of the same build differed by up to 2.8 % on an entry, so a smaller difference between two libraries on one entry is not a result on this box. Phase 6 checks the same tolerance on the runners.

Lessons:

- One process is not a measurement. An entry's process medians ranged up to 19.9 % apart within one run, while the median of five agreed with BenchmarkDotNet within 6.3 % and with the next run within 2.8 %.
- The warmup has to outlast tiering. In a trial with a 300 ms warmup, the median batch ran 61 % of its 20 ms target and the shortest 30 %, because the warmup's second half was still slower than the batches after it. With 3 s, the median batch ran 98 % of the target and nine in ten ran more than 92 %.
- A process's fixed cost (start, runtime, corpus load, JIT) was 193 to 325 ms. Timed whole, a process running a 1 µs call 300,000 times would measure that fixed cost as much as the calls, which is why the outside check takes the difference of N and 2N.
- Through Docker Desktop's Windows bind mount (virtiofs), a file written a moment before was intermittently read back as zero bytes: a hyperfine export read by the process that wrote it, and a run's results read by the next container. In phase 2, results copied into the bind mount also arrived with bytes changed. The collector keeps hyperfine's exports on the container's own disk, and on Windows `/out` is a named volume whose results leave through a container's standard output (`tar cf -`), never through a bind mount. Linux runners mount natively.
- The run took 10 minutes, BenchmarkDotNet at three launches 31 minutes and the outside check 23 minutes, for one CLI and 33 entries. Phase 6 has to budget for that per CLI or narrow the outside check.

### Phase 2: rqrr and fast_qr (2026-10-02 to 2026-10-03)

Done:

- `rust/`: one Cargo package with a binary per library (`rqrr-cli`, `fast_qr-cli`) and the protocol in `src/lib.rs`, ported from FeatherQR's CLI. Both crates are pinned with `=` and by `Cargo.lock`, rqrr without its default `image` feature. The release profile is left at its defaults.
- The image builds them with Rust 1.99.0 twice: the default target, and `-C target-cpu=x86-64-v3` on x64 only. The collector skips a CLI whose binary is not in the image.
- rqrr decodes a matrix through its `Grid` over a `BitGrid` that borrows the modules, and an image through its grey entry point (`prepare_from_greyscale`, which copies and binarizes the image), taking the first grid that decodes. fast_qr encodes through `QRBuilder` pinned to the level and version, as its own benchmark calls it.
- `run.md` gains a table of every CLI against the first, entry by entry. `compare` judges every CLI against the phase 1 tolerance, and `outside` measures a disturbed entry again (see Protocol).
- Measured on the same box and setup as phase 1, on Standard QR's 15 entries, the only ones either library offers, in five rounds with FeatherQR interleaved. Every process verified, including fast_qr's Unicode payload, which it writes as UTF-8 bytes with no ECI header.

| CLI | Entries | Median µs | Over FeatherQR's |
|---|---|---:|---:|
| fast_qr, default and x86-64-v3 | encode | 21.2 to 45.2 | 25 to 33 |
| rqrr, default and x86-64-v3 | matrix decode | 22.8 to 199.5 | 79 to 329 |
| rqrr, default | image decode | 2,460 to 3,609 | 221 to 244 |
| rqrr, x86-64-v3 | image decode | 1,673 to 2,615 | 160 to 166 |

The x86-64-v3 build ran rqrr's image decode 28 to 32 % faster and moved no other entry by more than 3.2 %. The Libraries table expected rqrr at the fast end for clean images. On these 8 pixels per module images it is the slow end by two orders of magnitude, and fast_qr's own README puts its version 3 encode at 82 µs (level H, its machine), the same order as the 36 µs here, so the harness is not the cause.

The outside check ran three times, because the box was busy during this phase (an entry's process medians ranged up to 62 % apart, against 19.9 % in phase 1). Under the final rules, every one of the 30 entry and CLI pairs had at least one undisturbed measurement, every undisturbed measurement was within 6.6 % of its self-timed median, and each CLI's signed median stayed within 1.3 % in every run.

Lessons:

- Load that comes and goes lands on whichever command is running, and moves the fixed cost the other way. The measurements that read 4.5 % or more high had fixed costs of −23 to −523 ms, those that read 4.2 % or more low had +22 to +95 ms, and the 72 that agreed within 3 % had −29 to +54 ms. A fixed cost cannot be negative, so a negative one beyond the noise is now one of the two signs of a disturbed measurement, beside a wide range. The positive side would need the CLI's true fixed cost, which timing the protocol's `noop` mode could give. It was not added, since that side stayed within 6.6 %.
- The minimum of the process times is not the cure. Differences of minima agreed within 5.1 % for the Rust CLIs, but put FeatherQR's phase 1 entries up to 8.0 % off, against 6.5 % for medians, because a JIT process's speed varies from process to process and minima pair a fast N process with a fast 2N process.
- The tolerance's second test has to measure bias, not noise. As the median of the disagreements' sizes, it failed CLIs whose two to four undisturbed entries happened to scatter, while no CLI's signed median moved past 1.3 %.
- A Rust CLI's fixed cost is a few milliseconds of process start, and its estimate lay within the check's noise of zero (−29 to +54 ms where the check agreed within 3 %), against 193 to 325 ms for FeatherQR on the JIT.
- One run with the outside check took 13.5 minutes for the run and 19 to 24 minutes for each outside check, over five CLIs on 15 entries.

### Phase 3: NativeAOT, the linux-arm64 half (2026-10-03)

Done:

- The image publishes FeatherQR's CLI under NativeAOT in a stage of its own, from the same SDK digest with clang to link: the default target as `featherqr-aot-default`, and on x64 the `x86-64-v3` target as `featherqr-aot-v3`. The CLI's `build` member names the target, and its `isa` member prints the instruction sets its code sees (see Protocol).
- The collector's `cold` command measures the cold first call (see Protocol).
- The probe on linux-arm64 is in the [probe reference](references/nativeaot-instruction-set-probe.md#linux-arm64-2026-10-03). The JIT and default NativeAOT take the same tier in all 28 kernels, so on ARM64 the default arm loses no tier and no kernel is behind its gap. Both runs below recorded the same `isa` for both builds on every entry: AdvSimd and the dot product.
- Measured on an Apple M2 (MacBook Air, fanless, 4 performance and 4 efficiency cores, on AC power) under Docker Desktop 4.44.3 on macOS 26.6.2. Its VM runs the arm64 image natively (kernel 6.10.14-linuxkit, 8 CPUs, 7.65 GiB), so these are development measurements like phase 1's, and phase 6's ubuntu-24.04-arm runner gives the arm64 rows. The container had two CPUs and 4 GB. The image was built from 3f296bb plus this phase's changes, with .NET 10.0.12 and ILCompiler 10.0.12. Two runs of five rounds with the JIT and default NativeAOT interleaved, and after the first run the outside check and the cold first call. Every one of the 33 entries verified in every process.

Steady state, NativeAOT over the JIT, each entry's ratio taken as the geometric mean of its two runs:

| Operation | Entries | Median | Range |
|---|---:|---:|---:|
| Encode | 11 | 1.03 | 0.94 to 1.08 |
| Matrix decode | 11 | 1.12 | 1.04 to 1.58 |
| Image decode | 11 | 1.09 | 0.99 to 1.14 |

The Micro QR byte matrix decode read 1.59 and 1.57, and every NativeAOT process was slower than every JIT process (0.525 to 0.580 µs against 0.320 to 0.357 µs in the first run). rMQR's byte matrix decode goes through the same byte payload code and read 1.16 and 1.08, so that code is not the cause on its own. The cause was Micro QR's codeword extraction, found natively on the same M2 afterwards. Its loop branched on each module's value. Decoding one symbol over and over lets the branch predictor learn that symbol's 192 modules, and ILC laid the loop out with one more taken branch per module than the JIT did with its synthesized profile, which the predictor learned less well. Both compilers emitted the same loop body. The extraction alone took 0.22 to 0.27 µs under the JIT and 0.44 to 0.51 µs under NativeAOT on the repeated symbol, and 0.95 to 1.09 µs under both on 1,024 symbols in random order. It now gathers the bits in a register without that branch. M4's matrix decode went from 0.31 to 0.28 µs under the JIT and from 0.55 to 0.30 µs under NativeAOT on the repeated symbol, and from 0.94 and 1.07 µs to 0.33 µs under both on changing symbols. M2 and M3 decoded changing symbols 2.3 to 2.8 times faster. No other single entry's ratio is a result on this box, because two runs of the same image put an entry's ratio up to 22.7 % apart (below).

Cold first call, from five rounds of ten timed runs per command:

| Measure, over the 33 entries | JIT | NativeAOT default |
|---|---:|---:|
| Start (`noop`): process, runtime and input load | 17.1 to 19.0 ms | 0.88 to 0.98 ms |
| First call (`cold` minus `noop`), encode | 9.1 to 20.9 ms | 0.04 to 0.31 ms |
| First call, matrix decode | 10.6 to 16.8 ms | 0.05 to 0.14 ms |
| First call, image decode | 25.9 to 33.6 ms | 0.05 to 0.18 ms |

Under the JIT the first call costs as much as the runtime's start or more, and Standard QR's costs more than Micro QR's on every operation (encode 19.0 to 20.9 ms against 9.1 to 9.8 ms). Under NativeAOT, start and first call together take about 1 ms on every entry.

How well this box measures:

| Measure, over the 33 entries | JIT | NativeAOT default |
|---|---:|---:|
| Range of an entry's five process medians over their median, median (first run) | 15.0 % | 16.2 % |
| Second run against the first, median and largest | 4.0 %, 10.9 % | 5.4 %, 13.5 % |
| Outside check against self-timed, median and largest | 3.6 %, 9.2 % | 3.8 %, 14.1 % |
| Outside check against self-timed, signed median | −1.4 % | −1.2 % |

Under the phase 1 tolerance, both CLIs fail the outside check on its bound per entry and pass it on bias. The entries beyond 7 % disagreed by about as much as their own processes did. For example, NativeAOT's Standard QR URL encode ran 2.78 µs in three processes and 3.29 to 3.38 µs in two, and the outside check's 3.08 µs fell between. So the loop shows no bias, and this box cannot resolve a single entry to 7 %.

Lessons:

- This Mac measured about four times noisier than the Windows box. An entry's process medians ranged 15 % apart at the median against 4 %, and two runs agreed within 4 to 5 % at the median against 1.3 %. The runs did not separate the causes. Desktop apps took about 1.5 of the 8 cores, and Docker Desktop's VM CPUs are host threads that macOS can move between cores of either kind, so `--cpuset-cpus` pins the measured thread to a VM CPU, not to a core. A box's noise has to be measured on that box before its per-entry ratios are read. Here only ratios over many entries, and gaps beyond about 25 %, are results.
- On NativeAOT the negative fixed-cost test flags load as intended. Its true fixed cost is about 1 ms, so three of the 66 measurements read −31 to −47 ms, all on NativeAOT, and were measured again. Their per-call times were within 2.3 % of the self-timed medians.
- `-p:PublishAot=true` on the command line reaches the library's netstandard2.0 build as a global property and fails it (NETSDK1207), so the CLI switches NativeAOT on from a property of its own.
- The run took 20 minutes, the outside check 46 minutes and the cold first call 2 minutes, for two CLIs on 33 entries.
- A benchmark that repeats one input lets the branch predictor learn a branch on the data. It can understate that branch's cost several times over, and show a gap between two builds that real input does not have. Micro QR's extraction is the case found here. The corpus has one input per entry, as the BenchmarkDotNet projects do, so a gap on a single entry is worth one run with the inputs rotated before it is read as code generation.

### Phase 3: NativeAOT, the linux-x64 half (2026-10-03)

Done:

- `clis.tsv` gains `featherqr-jit-noavx`, the JIT CLI under `DOTNET_EnableAVX=0`, as an x64 diagnostic arm. Its `isa` matched default NativeAOT's on every process (`Vector128`, 16-byte vectors, GFNI, no AVX2, BMI2 or `Vector256`). Against the JIT it is the cost of the tiers default NativeAOT loses. Default NativeAOT against it is the cost of ahead-of-time code at the same tiers. The outside check skips it, since its binary is the JIT arm's.
- Measured on the Windows box as in phase 1, at 357762d, which includes main's encoder changes up to #448 and so is not the code the arm64 half measured. Two runs of five rounds over the four arms, with the outside check on the three binaries and the cold first call on all four between them. Every one of the 33 entries verified in every process. The NativeAOT `isa` matched the probe's table: the default target sees GFNI and none of AVX2, BMI2 and `Vector256`, and `x86-64-v3` sees all three but not AVX-512.

Steady state, each entry's ratio taken as the geometric mean of its two runs, median and range over the entries of each group:

| Shape | Tier loss (JIT without AVX over JIT) | Ahead-of-time code (default NativeAOT over JIT without AVX) | Default NativeAOT over JIT | x86-64-v3 NativeAOT over JIT | Default over x86-64-v3 |
|---|---:|---:|---:|---:|---:|
| Standard QR encode | 1.62 (1.59 to 1.78) | 1.23 | 2.00 (1.95 to 2.18) | 1.23 | 1.61 (1.59 to 1.76) |
| Standard QR matrix decode | 1.10 | 1.16 | 1.27 | 1.10 | 1.14 |
| Standard QR image decode | 1.19 | 1.15 | 1.37 | 1.08 | 1.26 |
| Micro QR encode | 1.22 (1.04 to 1.31) | 1.04 | 1.21 (1.09 to 1.36) | 1.09 | 1.11 |
| Micro QR matrix decode | 1.09 | 0.93 | 1.03 | 0.99 | 1.03 |
| Micro QR image decode | 1.17 | 1.14 | 1.34 | 1.10 | 1.21 |
| rMQR encode | 1.12 (1.05 to 1.27) | 1.04 | 1.17 (1.10 to 1.27) | 1.06 | 1.11 |
| rMQR matrix decode | 1.16 (1.14 to 1.36) | 1.11 | 1.31 (1.26 to 1.49) | 1.22 | 1.04 (1.04 to 1.33) |
| rMQR image decode | 1.22 | 1.20 | 1.46 (1.40 to 1.56) | 1.13 | 1.28 |

The two causes multiply: Standard QR encode loses 1.62 to its tiers and 1.23 to ahead-of-time code, and runs at 2.00. The x86-64-v3 publish recovers the tiers, and what stays (0.99 to 1.26 over the JIT by entry, 1.10 at the median) is ahead-of-time code, which has no tiered recompilation or dynamic PGO. The Micro QR byte matrix decode that read 1.58 on arm64 read 1.03 here.

The kernels behind the default arm's gap, by shape, from the kernels each shape's clean path reaches and their cells in the [tier table](../specs/qrcode-simd-tiers.md). Every one falls to a 128-bit tier. None falls to scalar.

| Shape | Kernels that change tier, x86-64-v3 to default |
|---|---|
| Standard QR encode | `TextAnalyzer` (`Avx2` to `Sse2`, run twice per encode), `ModulePlacerExpandBits` and `ModulePlacerMaskCode` (`Avx2` to `Ssse3`). `EccBinaryEncoder` (`GfniV256` to `Gfni`) only where a block has more than 16 ECC codewords, here the version 4 Unicode entry |
| Micro QR encode | `TextAnalyzer`, `MicroQRModulePlacer` (`Avx2Pext` to `Ssse3`), `ModuleBitPacker` (`Avx2` to `Ssse3`) |
| rMQR encode | `TextAnalyzer`, `RmQRModulePlacer` (`Avx2` to `Ssse3`), `ModuleBitPacker`, and `EccBinaryEncoder` for the four-block R17x139 entry |
| Matrix decode, all three | `EccBinaryDecoder` (`GfniV256` to `Vector128`), and for rMQR `RmQRExtractCodewords` (`Avx2Pext` to `Vector128`) |
| Image decode, all three | `Binarizer` and `FinderRowEdges` (`Vector256` to `Vector128`), then the matrix decode's kernels. Standard QR adds `PerspectiveGridSampler` (`Vector256` to `Sse2`) and, from version 2, `AlignmentRowMask` (`Vector256` to `Vector128`) |

Cold first call, from five rounds of ten timed runs per command:

| Measure, over the 33 entries | JIT | JIT without AVX | NativeAOT default | NativeAOT x86-64-v3 |
|---|---:|---:|---:|---:|
| Start (`noop`) | 24.8 to 28.4 ms | 25.2 to 29.1 ms | 2.0 to 2.3 ms | 2.0 to 2.3 ms |
| First call, encode | 11.3 to 25.5 ms | 10.1 to 22.0 ms | 0.05 to 0.27 ms | 0.07 to 0.23 ms |
| First call, matrix decode | 13.5 to 20.2 ms | 15.0 to 20.1 ms | 0.07 to 0.19 ms | 0.05 to 0.15 ms |
| First call, image decode | 29.4 to 37.5 ms | 29.4 to 37.3 ms | 0.09 to 0.25 ms | 0.07 to 0.19 ms |

How well this box measured: an entry's process medians ranged 2.9 to 4.0 % apart at the median in the second run. The first run caught load in one round across all arms, which the median of five absorbed. The second run agreed with the first within 1.4 to 1.7 % at the median per arm (8.4 % at most, on the JIT), and the default-over-JIT ratio within 1.1 % (8.7 % at most). The outside check's signed median was −1.3 % for the JIT, −1.5 % for default NativeAOT and −1.4 % for x86-64-v3, and both NativeAOT arms passed with every entry within 4.3 %. The JIT's Micro QR numeric encode read 10.0 % low against the first run, whose median that load had raised. Against the second run, the six JIT entries beyond 4 % all came within 5.0 % on a recheck.

Lessons:

- On x64 the gap has two causes, and only one of them is the library's tiers. The JIT without AVX takes exactly default NativeAOT's tiers, so it separates them without a second compiler: on Standard QR encode the tiers cost 1.62 and ahead-of-time code 1.23. A publish for `x86-64-v3` removes the first and leaves the second.
- An entry that fails the outside check by its bound is first checked against a second run. The JIT's worst entry failed against a run whose median other load had raised, and agreed against the next. The loop is the same code in every .NET arm, and the two NativeAOT arms passed in the same check.
- The outside check reads about 1 % below the self-timed median on every .NET arm, on both machines (−0.9 % in phase 1, −1.2 to −1.5 % here and on arm64). The Rust CLIs' signed medians ranged −1.3 to +0.5 % over their three checks. The cause is not known. Every .NET arm shares it, so ratios between them are unaffected.
- Tracing which kernels each shape reaches found that a Standard QR encode with a pinned version, which every encode entry here is, analyses the text twice: once to resolve the version and once to encode. That is a separate change to the library. Done 2026-10-04: Standard QR and Micro QR, which had the same double analysis, now encode from the analysis that resolved the version, so the entries measured above predate it (see the Performance lessons in [standardqr-encoder.md](../specs/standardqr-encoder.md)).
- Stopping the shell that ran `docker run` leaves its container running, still on the measured CPUs. The measuring scripts name their container, so a stop reaches it.
- The four arms took 40 minutes per run, the outside check on three of them 64 minutes, and the cold first call 6 minutes.

### Phase 4: zxing-cpp and libzint (2026-10-04 to 2026-10-05)

Done:

- `cpp/`: `zxingcpp-cli` and `zint-cli` with the protocol in `protocol.hpp`, ported from `Protocol.cs`, which the .NET protocol moved into so that a second .NET CLI shares it. zxing-cpp is pinned to v3.1.0 (885baaf), the commit the ZXingCpp package 0.5.2 ships. libzint is pinned to 2.16.0 (55541e1), the commit zxing-cpp bundles as its writer, and is built from zxing-cpp's submodule. The image builds both with g++ 13.3 on the final stage's base, at CMake's Release defaults and with `-march=x86-64-v3`.
- zxing-cpp decodes images through `ReadBarcode`, with the formats restricted to the symbology and every other option at its default. It decodes matrices through `QRCode::Decode`, the decoder behind `ReadBarcode`, which is not public API: the public API reads only images. It encodes through `CreateBarcodeFromText`, which writes with the bundled libzint (`ZBarcode_Encode_and_Buffer`), copies the module buffer into a `BitMatrix` and builds a `Barcode` around it. libzint encodes through `ZBarcode_Create`, `ZBarcode_Encode` and `ZBarcode_Delete`, with UTF-8 input, so it inserts an ECI where the text needs one. Every level and version is pinned, and libzint keeps a level it is given.
- `dotnet/zxingcpp/`: the ZXingCpp package behind the same protocol, as shipped (`zxingcpp-net`) and with its `libZXing.so` replaced by one built here from the same commit and flags (`zxingcpp-net-own`), so that against `zxingcpp-default` the second differs only by the wrapper. Its reader is held to one symbol, as the native `ReadBarcode` is, since the package's reader otherwise looks for every symbol. The package has no matrix decoder. The reference project's `ZXingCppEntries` times the package's binary under BenchmarkDotNet.
- A call whose verification fails is now reported and not timed in all three protocol implementations, as What has to stay true asks. The package's CLI had crashed by timing a call that throws.
- Measured on the Windows box as in phase 1, from cbe7e57 plus this phase's changes. Two runs of five rounds over seven CLIs (FeatherQR on the JIT, both zxing-cpp builds, both libzint builds, both package arms), the outside check and the cold first call between them, and BenchmarkDotNet at three launches. Every process verified except one entry, below.

Steady state over FeatherQR on the JIT, each entry's ratio taken as the geometric mean of its two runs, median and range by group:

| Shape | zxing-cpp | libzint | ZXingCpp package |
|---|---:|---:|---:|
| Standard QR encode | 45 (37 to 57) | 23 (18 to 30) | 39 (34 to 44) |
| Standard QR matrix decode | 11 (9.9 to 12) | | |
| Standard QR image decode | 4.9 (4.6 to 5.2) | | 4.9 (4.8 to 5.3) |
| Micro QR encode | 26 (25 to 27) | 9.1 (8.9 to 9.9) | 26 (26 to 27) |
| Micro QR matrix decode | 7.1 (6.7 to 8.3) | | |
| Micro QR image decode | 5.3 (4.5 to 5.6) | | 5.6 (4.7 to 5.8) |
| rMQR encode | 25 (24 to 28) | 11 (7.5 to 12) | 25 (24 to 28) |
| rMQR matrix decode | 16 (15 to 21) | | |
| rMQR image decode | 7.5 (6.8 to 8.5) | | 7.6 (7.0 to 8.5) |

In absolute terms zxing-cpp decoded the images in 20 to 161 µs and FeatherQR in 3.8 to 19 µs, and libzint encoded in 1.1 to 34 µs and FeatherQR in 0.14 to 1.2 µs.

- zxing-cpp's writer takes 1.9 to 3.2 times libzint's encode (median 2.0 for Standard QR, 2.8 for Micro QR, 2.5 for rMQR), for the buffer, the copy and the `Barcode` it adds. zxing-cpp compiles its bundled libzint with `-Os`, but a libzint built with `-Os` timed within 3 % of the `-O3` one on three payloads, so the compiler setting is not the gap.
- `-march=x86-64-v3` moved zxing-cpp's image decodes and encodes by 4 % or less, sped its matrix decodes by up to 17 % (Standard QR 4 to 17 %), and slowed libzint by 4 to 8 %.
- The wrapper's cost: on image decode `zxingcpp-net-own` ran 1.02 to 1.09 times `zxingcpp-default` (median 1.03), 0.7 to 4.8 µs per call (median 1.4). On Micro QR and rMQR encode it ran 0.99 to 1.01, nothing measurable. On Standard QR encode it ran 0.92 to 0.94, the same native code 6 to 8 % faster inside the .NET process. The cause is not known. The package's own binary ran within 6 % of the one built here on every entry and 1.00 at the median, so its build costs nothing either.
- The package cannot encode non-ASCII text. It passes the string's UTF-16 length as the UTF-8 byte count, so the native writer reads a prefix: on Linux the Unicode payload was encoded silently cut after "Пр", and on Windows the cut fell inside a character and threw "Invalid UTF-8 in input". Verification rejected the entry in every process. The package's byte overload takes the UTF-8 bytes whole, but writes them as binary data (ECI 899) rather than text.
- Cold first call: zxing-cpp and libzint start in 0.9 to 1.4 ms and make their first call in 0.04 to 0.40 ms. The package starts with the .NET runtime in 21 to 23 ms, and its first call takes 0.6 to 3.3 ms. FeatherQR on the JIT starts in 22 to 25 ms, and its first call takes 11 to 34 ms.

How well this box measured: the second run agreed with the first within 2.1 to 2.8 % at the median per CLI. BenchmarkDotNet over the package agreed with the package's CLI within 1.8 % at the median against the first run (6.0 % at most) and 0.5 % against the second (1.6 % at most). The outside check, run between the two runs, read 1.2 to 2.3 % below the first on every CLI and 0.5 to 1.8 % above the second on every CLI. Against the second run every CLI passed except FeatherQR, whose Micro QR numeric and Standard QR URL matrix decodes, at 0.17 and 0.42 µs, read 7.4 and 7.1 % off. Against the first run, four CLIs had one entry 8.2 to 11.1 % off, and libzint's x86-64-v3 build a signed median of −2.3 %.

Lessons:

- The outside check's offset from the self-timed median is drift between the two measurements, not a property of .NET. Phase 3 read every .NET arm about 1 % low, and phase 2 the Rust CLIs between −1.3 and +0.5 %. Here every CLI, native ones included, read low against the run before the check and high against the run after it. The check is held against the run nearest in time, or against both.
- A library's writer can be another library. zxing-cpp's encode is libzint's with more work around it, so libzint measured alone is what separates the two.
- A wrapper's options have to be matched before its cost is read. The package's reader looks for every symbol unless told to stop at one, which the native single-symbol call does by itself.
- A package's native binary has to be swapped for one built here, from the same commit and flags, before the difference is the wrapper's. Here the swap changed nothing, but that was a measurement, not an assumption.
- Verification found a wrapper bug that a benchmark would have hidden: the package encodes a cut-off Unicode text without an error on Linux. Encode verification decodes what was written, not only whether the call returned.
- The seven CLIs took 53 minutes per run, the outside check 112 minutes over all seven, BenchmarkDotNet 21 minutes for 22 entries at three launches, and the cold first call 5 minutes.

### Phase 5: BoofCV and ZXing on the JVM (2026-10-05)

Done:

- `jvm/`: one Maven project with a main class per library (`xlang.BoofCvCli`, `xlang.ZxingCli`) and the protocol in `Protocol.java`, packed into one jar by the shade plugin. BoofCV is pinned to 1.5.0 and ZXing to 3.5.4, and every plugin to an exact version. The image builds the jar with Maven 3.10.0 on JDK 25 and runs it on the JRE of the same release (25.0.4.1), each image pinned by digest. `clis.tsv` starts both with G1 and a fixed 1 GiB heap.
- BoofCV decodes Standard QR and Micro QR images through the detector `FactoryFiducial` builds for each, with its default configuration, built once and reused for every image as a caller reading frames reuses it. It reads neither rMQR nor a module matrix through its API. ZXing does Standard QR only: `Encoder.encode` to the module matrix (the encoder behind `QRCodeWriter`, without its scaling to pixels), `Decoder.decode` over a `BitMatrix`, and `QRCodeReader` over the grey pixels through `HybridBinarizer`, with no hints. Text that ISO-8859-1 cannot hold is encoded as UTF-8 with an ECI, as a caller sets it.
- The timed call reads its input through a volatile field, since C2 can inline the call into the loop and could otherwise treat the input as constant.
- A CLI may now raise the run's warmup and batch length to floors of its own (see Protocol), and the outside check sizes N to contain such a CLI's whole warmup. The JVM CLIs set 10 s and 100 ms. Every other CLI keeps 3 s and 20 ms, so the earlier phases' rows still compare.
- Measured on the Windows box as in phase 1, from 62086cb plus this phase's changes. Two runs of five rounds over FeatherQR on the JIT, BoofCV and ZXing, the outside check on the two JVM CLIs and the cold first call between them. Every process verified.

Over FeatherQR on the JIT, each entry's ratio the geometric mean of its two runs:

| Shape | BoofCV | ZXing |
|---|---:|---:|
| Standard QR encode | | 46 (36 to 67) |
| Standard QR matrix decode | | 46 (26 to 56) |
| Standard QR image decode | 29 (27 to 31) | 11 (9.1 to 12) |
| Micro QR image decode | 40 (34 to 45) | |

In absolute terms BoofCV decoded the Standard QR images in 299 to 462 µs and the Micro QR images in 162 to 224 µs, and ZXing encoded in 26 to 82 µs, decoded matrices in 11 to 32 µs and images in 88 to 185 µs.

Correction (phase 7): the matrix decode row timed ZXing's failure path, not its decode. ZXing's `Decoder.decode` unmasks the matrix it is given and leaves it so, the CLI passed the same matrix to every call, and every call after the first failed. Decoding a copy per call, ZXing decodes matrices in 1.4 to 9.6 µs, 12 (5.1 to 18) times FeatherQR (phase 7). The JVM starts in 60 to 64 ms for ZXing and 114 to 140 ms for BoofCV, against 27 to 29 ms for FeatherQR on the JIT, and makes its first call in 2.6 to 20 ms for ZXing and 21 to 33 ms for BoofCV, against 12 to 38 ms.

How well this box measured: the second run agreed with the first within 0.9 % at the median for FeatherQR, 1.5 % for ZXing and 3.0 % for BoofCV, and an entry's process medians ranged 3.8 to 4.5 % apart at the median in the second run. BoofCV passed the outside check against both runs (every entry within 5.5 %, signed medians +1.7 and +0.3 %). ZXing had no bias (signed medians −0.9 and −1.1 %), but its image decodes read low beyond the bound: one entry against the first run (7.1 %) and two against the second (8.1 and 10.7 %). That fits a 30 s trace in which ZXing's image decode dropped about 10 % around 11 s, after the 10 s floor. Its image decode rows may read up to 10 % slow. A 20 s floor for ZXing would put its window past that drop, at about three more hours of outside check.

How the warmup got there:

- With the protocol's 3 s warmup and 20 ms batches, the outside check read ZXing's image decode below every self-timed process (qr-alphanumeric 103 µs against process medians of 114 to 122 µs), and with a 4 s N read four of BoofCV's entries 7.8 to 13.7 % above.
- Traced over 15 to 30 s in 1 s means, BoofCV's Standard QR decode stayed near 400 µs for 8 to 9 s and then near 355 µs, ZXing's dropped about 10 % around 11 s in one trace, and both swung about 5 % from second to second after that. G1, ParallelGC and SerialGC all showed the same drop. ParallelGC swung least, G1 added bumps of about 10 %. G1 stays, since it is what a server JVM runs by default.
- Lowering C2's tier 4 thresholds tenfold did not visibly move the drop, but the box was busy during that test, so whether the drop follows call counts rather than seconds is not settled.
- Running every CLI at 10 s and 100 ms worked but more than tripled each run, and BoofCV still failed with a 4 s N, because N did not contain its warmup. A floor per CLI and an N that contains the warmup it reports fixed both without touching the other CLIs.

Lessons:

- A JVM library's warmup is a property of the library, not of the JVM: ZXing's encode was steady within 3 s, BoofCV's image decode only after 8 to 9 s, and ZXing's image decode later still. A fixed warmup for every CLI is either too short for the slowest or too long for the rest, so each CLI states its own floor and the outside check follows it.
- The outside check's premise is checked, not assumed: N has to contain the warmup. A fixed-cost estimate cannot show a warmup longer than N, which reads as a per-call cost.
- Running anything on the measured CPUs during a run contaminates it. A 35 s experiment during a run doubled to quadrupled the experiment's own times, and the run was stopped and repeated.
- The first attempt and the redo, hours apart on the same image, put FeatherQR 7 to 8 % apart too, while the redo's two runs agreed within 0.9 %, so a comparison holds only within one run.
- The three CLIs took 36 minutes per run, the outside check 50 minutes on BoofCV's 8 entries and 93 minutes on ZXing's 15 with N at 10 s, and the cold first call 8 minutes.

### Phase 6: CI (2026-10-05 to 2026-10-06)

Done:

- `.github/workflows/cross-language-benchmark.yaml`: a `workflow_dispatch` workflow on ubuntu-24.04 and ubuntu-24.04-arm (four vCPUs each, since the repository is public). It builds the image, runs the collector in the container with the development box's two CPUs and 4 GB, and uploads the results as an artifact, with `run.md` in the job summary. Its inputs choose the CLIs, the entries, the rounds, the cold first call and the outside check, which is off by default because it takes hours with every CLI. The collector's exit 1 (an entry failed verification, as the ZXingCpp package's Unicode encode always does) is a warning, and only a failure to measure fails the job. The step script ran under WSL against a Linux path before the workflow was committed.
- The image is built in every run, neither cached nor published, which settles the open decision on GHCR. A build without cache took 91 s on the development box (8 VM CPUs, base images already pulled) and is expected to take about 5 minutes on a hosted runner, against two to three hours of measuring, and every result records the image ID it ran. Builds of one commit come from pinned sources and toolchains but are not byte-identical, so the image IDs differ.
- The collector's `agree` command holds two runs against each other on ratios, each CLI's median over FeatherQR's entry by entry, under the phase 1 tolerance. On phase 5's two runs it put ZXing within 6.7 % and BoofCV within 4.1 %.

- Two runs of the workflow, both with three rounds, every CLI and the cold first call: [37321094156](https://github.com/guitarrapc/FeatherQR/actions/runs/37321094156) at 1df5b10 and [37340573682](https://github.com/guitarrapc/FeatherQR/actions/runs/37340573682) at 08c4b48. Between the two commits only `src/FeatherQR.Benchmark` changed, which the image does not contain, so both measured the same code. Every process verified except the ZXingCpp package's Unicode encode, as in phase 4. Building the image took 1.8 to 3.5 minutes and measuring 69 minutes on arm64 and 84 to 92 minutes on x64.
- The runners were ubuntu-24.04-arm on a Neoverse-N2 both times (4 cores, AdvSimd and the dot product), and ubuntu-24.04 on an AMD EPYC 7763 (Zen 3: AVX2 and BMI2, no GFNI or AVX-512) for the first run and an AMD EPYC 9V45 (Zen 5, which adds GFNI and AVX-512) for the second, each with 2 cores of 2 threads.

| Measure | arm64, first and second | x64, first (Zen 3) and second (Zen 5) |
|---|---:|---:|
| Range of an entry's three process medians over their median, median over the entries | 0.5 %, 0.4 % | 0.8 %, 3.2 % |
| `agree`: largest entry deviation over all CLIs | 3.4 % | 45.4 % |
| `agree`: signed medians of the CLIs | −0.1 to +0.3 % | −6.4 to +33.5 % |

On arm64 every CLI agreed under the phase 1 tolerance, so the exit holds there: the second run reproduced every CLI's ratio to FeatherQR within 3.4 % on every entry. On x64 every CLI disagreed, because the CPU changed. The ratios moved in both directions and by CLI, as medians over a shape's entries: default NativeAOT's Standard QR encode over the JIT went from 1.93 to 2.19, libzint's Standard QR encode over FeatherQR from 21 to 17, zxing-cpp's from 43 to 33, rqrr's matrix decode from 206 to 290. The x64 exit needs a second run that lands on the same CPU model as one of these.

Lessons:

- A ratio between two libraries is a property of the CPU as well as of the libraries. Zen 3 and Zen 5 put the same entry up to 45 % apart, so an x64 result names its CPU model, and two runs are held against each other only on the same model. `agree` now says when the models differ. The design's premise, ratios within one run, holds: what does not hold is comparing ratios across runs on different models.
- A hosted runner can measure as quietly as the development box or more so. On the Neoverse-N2 and the EPYC 7763, the process medians of an entry ranged under 1 % at the median, against 2.9 to 4.0 % on the development box. The EPYC 9V45 ranged 3.2 %.
- Phase 7 found that zxing-java's matrix decode timed ZXing's failure path in every run, both of these included. Their zxing-java matrix decode rows are not ZXing's decode.

### Phase 7: quirc, zbar, libqrencode, the qrcode crate and Go (2026-10-05 to 2026-10-06)

The questions the first set left: whether rqrr's slow decode is quirc's method or the port, whether a reader sits at the fast end for clean images as rqrr was expected to, whether libzint is the fast end of the native writers, and the Rust row's qrcode crate, which phase 2 never built. Go was added beside them as a garbage-collected runtime compiled ahead of time, with gozxing against ZXing to separate the language from the code.

Done:

- `cpp/`: `quirc-cli`, `zbar-cli` and `qrencode-cli`. quirc is pinned to v1.2 (542848d) and built from its four library sources, at CMake's Release defaults and at `x86-64-v3`, as zxing-cpp is. zbar 0.23.93 and libqrencode 4.1.1 are Ubuntu noble's packages, pinned by package version, which their CLIs link against, so they have the shipped column only.
- `rust/`: `qrcode-cli`, the qrcode crate 0.14.1 without its default features, in both columns.
- `go/`: the protocol in `protocol/protocol.go`, `gozxing-cli` (gozxing 0.1.1) and `go-qrcode-cli` (skip2/go-qrcode at its last commit, from 2020), each at the version `go get` resolves, with go.sum checking their content. Go 1.27.1 builds them without cgo, at the default level and at `GOAMD64=v3`, and the collector keeps Go's defaults (`GOGC=100`, `GOMAXPROCS` from the container's two CPUs).
- How each is called. quirc decodes an image through its README's loop on one `struct quirc` sized before timing: a copy of the pixels into its buffer, `quirc_end`, then `quirc_extract` and `quirc_decode` until a code decodes. It decodes a matrix through `quirc_decode` over a `quirc_code` filled with the bare symbol. zbar's image scanner is made once with every symbology but QR Code off, and each call wraps the pixels in a Y800 image without a copy. libqrencode writes through `QRcode_encodeString` or `QRcode_encodeStringMQR` with the `QR_MODE_8` hint, then `QRcode_free`. The qrcode crate writes through `QrCode::with_version`. gozxing is called as ZXing is. go-qrcode writes through `NewWithForcedVersion` and `Bitmap`, where it adds the error correction and picks the mask.
- The protocol's `failedCalls`, and `outside`'s check of three `fixed` calls (see Protocol). Every CLI and entry of the phase 6 image was run with 1 and 200 calls in `fixed`, holding the checksums against each other: only ZXing's matrix decode failed calls after the first, decoding once in 200 calls on four entries and 50 times on the numeric one. `ZxingCli` and `gozxing-cli` now decode a copy of the matrix in every call, which gozxing makes with `BitMatrix.Xor` onto an empty matrix since it has no `Clone`.
- Measured on the Windows box as in phase 1, from 08c4b48 plus this phase's changes: two runs of five rounds over FeatherQR on the JIT, the ten new CLIs and the default builds of the four earlier CLIs they answer to (rqrr, libzint, fast_qr, ZXing), the outside check on the new CLIs and on ZXing's matrix decode, and the cold first call between them. Every process verified except two of the qrcode crate's entries (below), and no timed call failed.

Over FeatherQR on the JIT, default builds, each entry's ratio the geometric mean of its two runs, median and range by shape:

| Shape | quirc | zbar | libqrencode | qrcode crate | gozxing | go-qrcode |
|---|---:|---:|---:|---:|---:|---:|
| Standard QR encode | | | 22 (18 to 27) | 134 (107 to 146) | 83 (55 to 132) | 138 (79 to 232) |
| Standard QR matrix decode | 9.7 (5.5 to 12) | | | | 18 (9.2 to 24) | |
| Standard QR image decode | 38 (36 to 39) | 54 (47 to 61) | | | 13 (11 to 15) | |
| Micro QR encode | | | 6.7 (5.6 to 8.8) | 17 (14 to 20) | | |

In the same runs the earlier CLIs read: rqrr 238 on image decode and 231 on matrix decode, libzint 22 (18 to 28) on Standard QR encode and 9.0 on Micro QR encode, fast_qr 32, ZXing 45 on encode, 12 (5.1 to 18) on matrix decode and 10 on image decode. The qrcode crate's Standard QR column is four entries and its Micro QR column two.

- quirc against rqrr: quirc decodes the images in 0.16 of rqrr's time and the matrices in 0.03 to 0.07, so rqrr's slow decode is mostly the port's. quirc itself decodes the images in 369 to 551 µs, 38 times FeatherQR and slower than zxing-cpp (4.9 in phase 4), and zbar in 451 to 952 µs. No reader measured sits at a fast end for clean images.
- libqrencode against libzint: 0.98 (0.95 to 1.00) on Standard QR encode and 0.74 (0.64 to 0.91) on Micro QR. The two are the fast end of the native writers, at 22 times FeatherQR on Standard QR. fast_qr takes 1.44 times libzint's time, ZXing 2.05, gozxing 3.78, go-qrcode 6.24 and the qrcode crate 6.29.
- gozxing against ZXing: 1.28 on image decode, 1.50 on matrix decode and 1.84 on encode, so the same code runs faster on the JVM than compiled ahead of time by Go.
- `x86-64-v3` sped quirc's image decode by 18 % and moved every other new CLI by 5 % or less at the median.
- The qrcode crate, the most downloaded Rust QR crate, wrote two of its eight entries wrong, and verification kept them out of the timing. Its segment optimiser takes byte pairs in Shift JIS's double-byte ranges for Kanji, so it wrote the Unicode payload's UTF-8 Chinese and Japanese as Kanji: zxing-cpp read the symbol as other text and FeatherQR did not decode it. Its M3-L symbol for `HELLO WORLD 14` decodes in neither zxing-cpp nor FeatherQR, while libzint's and libqrencode's symbols for the same case decode in both.
- ZXing's matrix decode, decoding a copy per call: 1.4 to 9.6 µs, 12 (5.1 to 18) times FeatherQR. Phase 5's failure path took 11 to 32 µs, longer than the decode.
- Cold first call: the Go CLIs start in 1.3 to 1.5 ms and make their first call in 0.5 ms or less, quirc starts in 1.0 to 1.2 ms and makes its first image decode in 0.34 to 0.60 ms (about one call), libqrencode and the qrcode crate make theirs in 0.24 ms or less, and zbar, which loads more shared libraries, starts in 2.2 to 2.3 ms and makes its first call in 0.7 to 1.2 ms. FeatherQR on the JIT starts in 23 to 25 ms and makes its first call in 12 to 35 ms.

How well this box measured: run 2 ran 4 to 6 % faster than run 1 on every CLI (FeatherQR 4.7 %), while each CLI's ratios agreed: every CLI but the qrcode crate's `x86-64-v3` build within the phase 1 tolerance, at most 6.1 % on an entry and 1.4 % on a signed median. The outside check, run between the two, read 1 to 5 % below run 1 and 1 to 5 % above run 2 on every CLI. Against the geometric mean of the two runs, every CLI but the qrcode crate had every entry within 7 % (at most 6.9 %, ZXing's alphanumeric matrix decode) and a signed median within 1.8 %. The qrcode crate's Micro QR entries agreed within 2 %, and its Standard QR encodes read up to 10.6 % (default) and 16.1 % (`x86-64-v3`) low. Its time per call switches between two levels about 20 % apart, holding each for 100 to 400 ms, in every process: on the Wi-Fi payload, 127 to 131 µs against 155 to 170 µs. Measured as the largest over the smallest median of five consecutive batches, it shifted 24 to 26 % within a process at the 90th percentile, against 3 to 14 % for the other CLIs. Switching off glibc's heap trimming and its dynamic mmap threshold left it unchanged, and the cause is not known, so the crate's Standard QR rows are good to about 10 %.

Lessons:

- Verifying one call does not verify the timed ones. A library can change its input: ZXing's matrix decoder unmasks the matrix in place, and phase 5's runs and both of phase 6's timed its failure path. The failure took longer than the decode, so the numbers looked plausible. Counting the calls that fail costs one comparison per call and found it on its first run.
- When the box drifts between two runs, an outside check run between them falls between them too. Its offset against either run is the drift, so it is held against both, here their geometric mean, as phase 4 suggested.
- A library's own speed can switch between levels. Then neither the self-timed median nor the outside check resolves it to 7 %, and its rows carry the width of the switch.
- Through Docker Desktop's bind mount, a source file just written on the host was read in the container with NUL bytes, as phase 1 found for output files. Sources now go into a container as a tar stream on standard input.
- Each run took 62 to 63 minutes over 15 CLIs, the outside check 56 minutes on the new CLIs' 85 entries and 32 minutes on ZXing's five matrix entries with N at 10 s, and the cold first call 3 minutes.
