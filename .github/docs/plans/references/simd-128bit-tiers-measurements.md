# 128-bit tiers: phase 1 measurements

Measurements for [simd-128bit-tiers-plan.md](../simd-128bit-tiers-plan.md), taken on 2026-09-29, so later phases start from numbers instead of measuring again. This file is deleted with the plan.

## How they were taken

- **Machine.** Ryzen 9 7950X3D, Windows 11, .NET 10.0.9 (x64), .NET 10.0.11 WebAssembly runtime under Node.js 24.18. Load under 10 % when the timing runs started; runs one after another, never two at once.
- **Harness.** `--time` of `tests/FeatherQR.AotAnalysis` and `tests/FeatherQR.WasmReport` (`TierTiming.cs`): 300 ms warmup a shape, 11 rounds of 20 ms batches, shapes interleaved within a round, median per call. Images come from the test renderers, so the image shapes follow the benchmark's in kind, not in pixels.
- **Builds.**

  | Label | Build | 128-bit lowering |
  |---|---|---|
  | jit-avx2 | JIT | SSE4.2 and AVX2 |
  | jit-noavx | JIT, `DOTNET_EnableAVX=0` | up to SSE4.2 |
  | aot-v3 | NativeAOT, `IlcInstructionSet=x86-64-v3` | SSE4.2 and AVX2 |
  | aot-v2 | NativeAOT, `IlcInstructionSet=x86-64-v2` | up to SSE4.2 |
  | aot-default | NativeAOT, default target | SSE2 only (below) |
  | wasm-aot | WebAssembly, `RunAOTCompilation=true` | PackedSimd |
  | wasm-interp | WebAssembly, interpreter (the Blazor default) | PackedSimd |

- **Shares.** WebAssembly AOT: Node's V8 sampler (`--cpu-prof`, 250 µs), self time of each kernel's own methods. x64: kernel-alone timings of kernels called once per operation, and `DOTNET_EnableGFNI=0` for the syndrome pass (the only kernel it moves). The .NET sampler (`dotnet-trace`, `dotnet-sampled-thread-time`) is not used: it stops a thread only at GC-safe points, so a call-free kernel loop hands its samples to its caller, and on short shapes 90 % of samples land in the timing loop's GC poll.

## A default NativeAOT publish lowers portable vectors for SSE2

The default ILC target for x64 is the SSE2 baseline. SSSE3 to SSE4.2 and POPCNT are checked at run time, so an explicit `Ssse3.Shuffle` behind `Ssse3.IsSupported` runs, and the tier table sees the flags a `DOTNET_EnableAVX=0` JIT sees. A portable `Vector128` operation carries no such check, so ILC lowers it for SSE2: an operation SSE2 lacks becomes a sequence or a software fallback. `DOTNET_EnableAVX=0` lowers the same operations for SSE4.2, like `x86-64-v2`.

| Operation (4,096 vectors) | JIT, no AVX | NativeAOT v2 | NativeAOT default | WASM AOT | WASM interpreted |
|---|---|---|---|---|---|
| `Vector128.ConvertToInt32` (saturating) | 2.4 | 3.0 | **52.1** | **54.4** | 110 |
| `Vector128.ConvertToInt32Native` | 1.4 | 1.9 | 1.5 | 8.3 | **201** |
| the same, a scalar cast per lane | 7.6 | 12.6 | 7.5 | 19.2 | 137 |
| `Vector128.Shuffle`, variable index | 3.2 | 3.2 | **42.9** | 2.6 | 17.3 |
| `Vector128.ShuffleNative`, variable index | 2.4 | 2.4 | **42.7** | 2.6 | 17.2 |
| `BitOperations.PopCount`, 2 per vector | 1.7 | 1.7 | 3.2 | 4.1 | 10.0 |
| widen and multiply 16-bit pairs | 2.7 | 2.7 | 5.8 | 6.4 | 24.4 |

(µs a call; the conversion rows from a second run.)

## End to end, per build

Median µs a call. Ratios: `noavx/avx2` the JIT knob, `v2/v3` what the 256-bit tiers are worth with good 128-bit lowering, `aotdef/v2` what SSE2-only lowering costs, `aotdef/v3` the whole gap of a default publish.

| shape | jit-avx2 | jit-noavx | aot-v3 | aot-v2 | aot-default | wasm-aot | wasm-interp | noavx/avx2 | v2/v3 | aotdef/v2 | aotdef/v3 | wasmaot/v3 | interp/wasmaot |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| encode/qr-v1-num-L | 0.77 | 1.91 | 0.91 | 2.02 | 2.29 | 3.77 | 20.1 | 2.46 | 2.22 | 1.13 | 2.52 | 4.14 | 5.34 |
| encode/qr-v1-alnum-M | 0.85 | 2.17 | 0.98 | 2.24 | 2.65 | 4.49 | 22.0 | 2.57 | 2.27 | 1.19 | 2.70 | 4.57 | 4.89 |
| encode/qr-v6-url-M | 1.85 | 4.20 | 2.03 | 4.30 | 4.99 | 14.3 | 46.6 | 2.27 | 2.12 | 1.16 | 2.46 | 7.04 | 3.26 |
| encode/qr-v20-byte-M | 28.2 | 39.7 | 26.1 | 40.4 | 45.2 | 113 | 491 | 1.41 | 1.55 | 1.12 | 1.73 | 4.33 | 4.34 |
| encode/qr-v40-byte-L | 70.8 | 88.1 | 88.4 | 89.0 | 98.2 | 388 | 1344 | 1.25 | 1.01 | 1.10 | 1.11 | 4.39 | 3.46 |
| encode/qr-v40-byte-H | 73.5 | 83.9 | 90.5 | 85.9 | 94.5 | 347 | 1113 | 1.14 | 0.95 | 1.10 | 1.04 | 3.83 | 3.21 |
| encode/micro-m2-num | 0.14 | 0.17 | 0.19 | 0.19 | 0.20 | 0.40 | 2.50 | 1.19 | 1.01 | 1.04 | 1.05 | 2.11 | 6.31 |
| encode/micro-m3-alnum | 0.19 | 0.23 | 0.21 | 0.24 | 0.25 | 0.49 | 3.05 | 1.22 | 1.10 | 1.06 | 1.16 | 2.29 | 6.18 |
| encode/micro-m4-byte | 0.19 | 0.26 | 0.22 | 0.28 | 0.29 | 0.65 | 3.64 | 1.38 | 1.25 | 1.05 | 1.31 | 2.93 | 5.60 |
| encode/rmqr-r7x43-num | 0.15 | 0.15 | 0.17 | 0.18 | 0.18 | 0.40 | 2.48 | 1.01 | 1.02 | 1.00 | 1.02 | 2.30 | 6.19 |
| encode/rmqr-r11x59-alnum | 0.29 | 0.31 | 0.31 | 0.34 | 0.35 | 0.93 | 5.10 | 1.06 | 1.09 | 1.01 | 1.11 | 2.98 | 5.47 |
| encode/rmqr-r17x139-byte | 0.92 | 1.14 | 0.96 | 1.20 | 1.24 | 3.78 | 19.3 | 1.24 | 1.25 | 1.03 | 1.28 | 3.92 | 5.09 |
| encode/sa-byte-45k-single | 1197 | 1450 | 1511 | 1515 | 1642 | 3142 | 20800 | 1.21 | 1.00 | 1.08 | 1.09 | 2.08 | 6.62 |
| encode/sa-mixed-40k-single | 867 | 1276 | 1066 | 1310 | 1413 | 2765 | 17979 | 1.47 | 1.23 | 1.08 | 1.33 | 2.59 | 6.50 |
| encode/sa-mixed-40k-optimal | 1321 | 1903 | 1665 | 2063 | 2189 | 3592 | 20971 | 1.44 | 1.24 | 1.06 | 1.31 | 2.16 | 5.84 |
| encode/sa-utf8-15k-mixed-optimal | 886 | 1423 | 1040 | 1467 | 1542 | 2538 | 28492 | 1.61 | 1.41 | 1.05 | 1.48 | 2.44 | 11.23 |
| matrix/qr-v1-num-L | 0.27 | 0.46 | 0.30 | 0.49 | 0.49 | 1.03 | 3.96 | 1.69 | 1.61 | 1.00 | 1.61 | 3.36 | 3.86 |
| matrix/qr-v6-url-M | 0.81 | 2.73 | 0.93 | 2.83 | 2.82 | 5.63 | 17.4 | 3.38 | 3.04 | 1.00 | 3.03 | 6.05 | 3.08 |
| matrix/qr-v40-byte-L | 15.1 | 93.6 | 15.5 | 94.2 | 94.0 | 140 | 408 | 6.21 | 6.08 | 1.00 | 6.06 | 9.03 | 2.92 |
| matrix/qr-v40-byte-H | 15.7 | 88.2 | 16.1 | 88.9 | 88.3 | 136 | 396 | 5.62 | 5.53 | 0.99 | 5.49 | 8.45 | 2.91 |
| matrix/micro-m2-num | 0.15 | 0.19 | 0.17 | 0.21 | 0.22 | 0.35 | 1.61 | 1.27 | 1.27 | 1.05 | 1.33 | 2.09 | 4.59 |
| matrix/micro-m4-byte | 0.28 | 0.47 | 0.31 | 0.50 | 0.51 | 0.76 | 3.23 | 1.69 | 1.65 | 1.01 | 1.67 | 2.49 | 4.25 |
| matrix/rmqr-r7x43-num | 0.15 | 0.25 | 0.17 | 0.26 | 0.26 | 0.45 | 2.31 | 1.69 | 1.55 | 0.98 | 1.52 | 2.66 | 5.13 |
| matrix/rmqr-r17x139-byte | 0.66 | 4.29 | 0.76 | 4.33 | 4.28 | 6.60 | 23.0 | 6.54 | 5.69 | 0.99 | 5.63 | 8.67 | 3.49 |
| matrix/rmqr-r17x139-byte-corrected | 1.68 | 7.33 | 2.25 | 7.82 | 7.87 | 12.4 | 39.5 | 4.36 | 3.48 | 1.01 | 3.50 | 5.50 | 3.19 |
| matrix/sa-byte-45k-set | 266 | 1548 | 268 | 1533 | 1532 | 2441 | 6887 | 5.83 | 5.73 | 1.00 | 5.72 | 9.12 | 2.82 |
| image/qr-v40-3px | 92.4 | 391 | 107 | 480 | 690 | 893 | 3057 | 4.23 | 4.48 | 1.44 | 6.43 | 8.33 | 3.42 |
| image/qr-v40-3.4px | 103 | 465 | 118 | 558 | 777 | 990 | 3437 | 4.53 | 4.72 | 1.39 | 6.57 | 8.38 | 3.47 |
| image/qr-v40-4px-rot17 | 252 | 650 | 272 | 734 | 954 | 1297 | 4569 | 2.58 | 2.70 | 1.30 | 3.51 | 4.77 | 3.52 |
| image/qr-v40-4px-soft | 267 | 468 | 285 | 581 | 786 | 1081 | 4518 | 1.75 | 2.04 | 1.35 | 2.75 | 3.79 | 4.18 |
| image/qr-v25-4px-keystone15 | 168 | 508 | 199 | 645 | 739 | 1282 | 5187 | 3.03 | 3.24 | 1.15 | 3.71 | 6.45 | 4.04 |
| image/qr-v6-4px | 10.4 | 23.2 | 11.9 | 28.8 | 40.1 | 57.6 | 257 | 2.24 | 2.43 | 1.39 | 3.38 | 4.86 | 4.46 |
| image/none-noise | 4252 | 6270 | 4763 | 7972 | 9843 | 13421 | 77479 | 1.47 | 1.67 | 1.23 | 2.07 | 2.82 | 5.77 |
| image/none-gradient | 795 | 828 | 801 | 930 | 919 | 2214 | 5729 | 1.04 | 1.16 | 0.99 | 1.15 | 2.76 | 2.59 |
| image/micro-m4-8px | 4.50 | 9.72 | 5.05 | 11.0 | 13.0 | 21.3 | 84.0 | 2.16 | 2.18 | 1.18 | 2.57 | 4.21 | 3.95 |
| image/rmqr-r7x43-8px | 5.24 | 11.2 | 6.13 | 13.1 | 15.2 | 23.8 | 111 | 2.13 | 2.14 | 1.16 | 2.48 | 3.88 | 4.67 |
| image/rmqr-r17x139-8px | 20.3 | 61.7 | 24.0 | 73.0 | 90.2 | 130 | 582 | 3.04 | 3.04 | 1.24 | 3.76 | 5.42 | 4.47 |
| image/rmqr-r17x139-4px-keystone15 | 119 | 174 | 144 | 196 | 220 | 406 | 2493 | 1.46 | 1.36 | 1.12 | 1.53 | 2.81 | 6.15 |
| bitmap/qr-v40-3px | 122 | 653 | 139 | 779 | 993 | 1233 | 4128 | 5.36 | 5.62 | 1.27 | 7.17 | 8.90 | 3.35 |
| bitmap/rmqr-r17x139-8px | 37.5 | 217 | 41.6 | 252 | 277 | 342 | 1282 | 5.78 | 6.04 | 1.10 | 6.64 | 8.21 | 3.75 |

## Kernels alone, and the probe

Each kernel through its dispatch (what the build runs) and through its scalar entry. `kernel/LuminanceInverter-scalar` is the harness's own loop, since the kernel keeps its tiers inline. Probe rows are one operation over 4,096 vectors.

| shape | jit-avx2 | jit-noavx | aot-v3 | aot-v2 | aot-default | wasm-aot | wasm-interp |
|---|---|---|---|---|---|---|---|
| kernel/LuminanceInverter | 4.17 | 5.38 | 3.99 | 5.38 | 5.09 | 6.66 | 33.7 |
| kernel/LuminanceInverter-scalar | 158 | 160 | 159 | 156 | 157 | 332 | 816 |
| kernel/LocalBinarizer | 197 | 202 | 209 | 211 | 212 | 330 | 1420 |
| kernel/LocalBinarizer-scalar | 1879 | 1765 | 2365 | 2342 | 2348 | 1228 | 7941 |
| kernel/FinderRowMask-v40 | 249 | 248 | 481 | 493 | 497 | 865 | 5563 |
| kernel/FinderRowMask-v40-scalar | 811 | 825 | 1016 | 1061 | 1050 | 1126 | 5958 |
| kernel/FinderRowMask-noise | 3586 | 3662 | 4875 | 4886 | 4882 | 8027 | 52195 |
| kernel/FinderRowMask-noise-scalar | 5986 | 5898 | 6846 | 6881 | 6843 | 9282 | 51008 |
| kernel/AlignmentRowMask | 0.55 | 0.49 | 0.72 | 0.66 | 0.66 | 0.80 | 7.76 |
| kernel/AlignmentRowMask-scalar | 0.77 | 0.77 | 0.93 | 0.96 | 0.93 | 1.18 | 6.71 |
| kernel/QRSampleGrid | 31.6 | 31.2 | 28.1 | 31.3 | 236 | 230 | 256 |
| kernel/QRSampleGrid-scalar | 63.5 | 67.5 | 68.9 | 69.8 | 70.0 | 131 | 526 |
| kernel/MicroQRSampleGrid | 0.24 | 0.26 | 0.25 | 0.26 | 2.17 | 2.05 | 2.46 |
| kernel/MicroQRSampleGrid-scalar | 0.50 | 0.50 | 0.54 | 0.55 | 0.55 | 0.97 | 3.14 |
| kernel/RmQRSampleGrid | 1.52 | 1.64 | 1.66 | 1.64 | 18.9 | 16.2 | 11.5 |
| kernel/RmQRSampleGrid-scalar | 5.57 | 5.56 | 5.98 | 5.87 | 5.93 | 10.9 | 249 |
| kernel/RmQRSubFinderLattice | 0.25 | 0.29 | 0.29 | 0.29 | 3.12 | 3.14 | 1.39 |
| kernel/RmQRSubFinderLattice-scalar | 0.78 | 0.79 | 0.80 | 0.80 | 0.80 | 1.30 | 6.76 |
| kernel/RmQRLatin1Segment | 0.02 | 0.01 | 0.02 | 0.01 | 0.01 | 0.06 | 0.26 |
| kernel/RmQRLatin1Segment-scalar | 0.09 | 0.08 | 0.09 | 0.09 | 0.09 | 0.15 | 1.05 |
| probe/popcount-scalar | 1.70 | 1.69 | 1.69 | 1.70 | 3.21 | 4.11 | 9.99 |
| probe/popcount-swar | 2.87 | 2.85 | 2.88 | 2.88 | 2.86 | 18.3 | 38.3 |
| probe/popcount-shuffle | 2.94 | 2.64 | 2.45 | 2.67 | 75.3 | 6.44 | 14.7 |
| probe/popcount-shufflenative | 1.55 | 1.77 | 1.53 | 1.78 | 75.4 | 6.83 | 14.6 |
| probe/shuffle-variable | 2.89 | 3.17 | 3.17 | 3.18 | 42.9 | 2.56 | 17.3 |
| probe/shuffle-native | 2.36 | 2.36 | 2.37 | 2.37 | 42.7 | 2.55 | 17.2 |
| probe/dot16-portable | 2.72 | 2.71 | 2.72 | 2.74 | 5.83 | 6.37 | 24.4 |
| probe/pairwise-portable | 0.98 | 1.11 | 0.98 | 1.09 | 1.12 | 3.39 | 10.8 |
| probe/movemask-portable | 1.60 | 1.60 | 1.59 | 1.61 | 1.59 | 1.42 | 6.17 |
| probe/popcount-packedsimd | - | - | - | - | - | 2.01 | 8.36 |
| probe/shuffle-swizzle | - | - | - | - | - | 2.57 | 17.2 |
| probe/dot16-packedsimd | - | - | - | - | - | 1.33 | 9.49 |
| probe/pairwise-packedsimd | - | - | - | - | - | 1.15 | 8.48 |
| probe/movemask-packedsimd | - | - | - | - | - | 1.43 | 6.21 |

The kernels called once per operation, timed alone on the x64 builds (µs). Their share of an operation is their time over the operation's. `MaskCode` includes a copy that restores the unmasked matrix (the `-copy` rows).

| shape | jit-avx2 | jit-noavx | aot-v3 | aot-v2 | aot-default |
|---|---|---|---|---|---|
| kernel/Binarizer-v40-3px | 11.2 | 156 | 11.3 | 175 | 177 |
| kernel/Binarizer-v40-3.4px | 13.8 | 189 | 14.0 | 215 | 222 |
| kernel/Binarizer-v40-4px-rot17 | 103 | 306 | 104 | 348 | 350 |
| kernel/Binarizer-v40-4px-soft | 172 | 172 | 173 | 184 | 183 |
| kernel/Binarizer-noise | 163 | 168 | 170 | 165 | 164 |
| kernel/LuminanceConverter-v40-3px | 27.8 | 257 | 29.7 | 293 | 291 |
| kernel/LuminanceConverter-rmqr-r17x139-8px | 17.0 | 157 | 17.3 | 204 | 191 |
| kernel/MaskCode-v1 | 0.60 | 1.86 | 0.72 | 2.00 | 2.30 |
| kernel/MaskCode-v1-copy | 0.01 | 0.01 | 0.01 | 0.01 | 0.01 |
| kernel/MaskCode-v6 | 1.12 | 3.62 | 1.35 | 3.91 | 4.47 |
| kernel/MaskCode-v20 | 19.0 | 37.2 | 21.9 | 37.7 | 42.6 |
| kernel/MaskCode-v40 | 59.2 | 73.8 | 79.3 | 73.2 | 82.5 |
| kernel/MaskCode-v40-copy | 0.45 | 0.44 | 0.45 | 0.45 | 0.46 |
| kernel/StructuredAppendParity-byte-45k | 7.95 | 8.03 | 11.7 | 16.7 | 20.5 |
| kernel/StructuredAppendParity-utf8-15k | 9.52 | 7.70 | 8.51 | 9.32 | 9.67 |

## The syndrome pass on x64

`DOTNET_EnableGFNI=0` moves the syndrome pass alone from 256-bit GFNI to scalar, so the difference is its scalar cost less its GFNI cost. `DOTNET_EnableBMI2=0` did not move the rMQR extraction (still its PEXT tier), so the extraction's x64 cost is read from the no-AVX gap instead: 4.29 − 0.66 − 2.75 ≈ 0.9 µs on R17x139.

| shape | JIT AVX2 | JIT AVX2, `DOTNET_EnableGFNI=0` | difference |
|---|---|---|---|
| matrix/qr-v1-num-L | 0.283 | 0.447 | 0.16 |
| matrix/qr-v6-url-M | 0.855 | 2.768 | 1.91 |
| matrix/qr-v40-byte-L | 16.015 | 94.404 | 78.39 |
| matrix/qr-v40-byte-H | 17.012 | 93.321 | 76.31 |
| matrix/micro-m2-num | 0.164 | 0.186 | 0.02 |
| matrix/micro-m4-byte | 0.292 | 0.471 | 0.18 |
| matrix/rmqr-r7x43-num | 0.153 | 0.221 | 0.07 |
| matrix/rmqr-r17x139-byte | 0.709 | 3.459 | 2.75 |
| matrix/rmqr-r17x139-byte-corrected | 1.762 | 6.494 | 4.73 |
| matrix/sa-byte-45k-set | 276.936 | 1548.958 | 1272.02 |
| image/qr-v40-3px | 97.236 | 178.315 | 81.08 |
| image/qr-v6-4px | 10.581 | 12.092 | 1.51 |
| image/rmqr-r17x139-8px | 21.059 | 23.340 | 2.28 |
| bitmap/qr-v40-3px | 128.010 | 205.343 | 77.33 |
| bitmap/rmqr-r17x139-8px | 39.603 | 42.373 | 2.77 |

## Shares on WebAssembly AOT

Self time of each kernel's own methods, share of the timed loop in %, kernels at 1 % or more. `FinderRowPass` is the mask walk (what `FinderRowEdges` would replace); `FinderCrossChecks` is the scalar work the row pass hands candidates to. Kernels absent from every row have no self time of their own: `ModuleBitPacker`, the rMQR value writers and the Micro QR byte segment are inlined into their callers, and `QRSampleGridPiecewise` never ran. Part of every shape runs in the interpreter inside the AOT build (`mono_interp_exec_method`, generic value-type sharing: 33 % of a version 40 encode), which is not a SIMD matter.

| Shape | Kernels, % |
|---|---|
| bitmap/qr-v40-3px | LuminanceConverter 27.2, FinderRowPass 18.3, Binarizer 15.0, EccSyndromes 8.9, QRSampleGrid 7.5, FinderCrossChecks 4.9 |
| bitmap/rmqr-r17x139-8px | LuminanceConverter 61.0, Binarizer 12.4, FinderRowPass 10.4, FinderCrossChecks 3.8, RmQRSampleGrid 3.3, EccSyndromes 1.2 |
| encode/micro-m2-num | MicroQRModulePlacer 31.0, EccBinaryEncoder 7.6, MicroQRBinaryEncoder 2.0 |
| encode/micro-m3-alnum | MicroQRModulePlacer 31.6, EccBinaryEncoder 12.1, MicroQRBinaryEncoder 3.9 |
| encode/micro-m4-byte | MicroQRModulePlacer 35.9, EccBinaryEncoder 15.8, MicroQRBinaryEncoder 3.9 |
| encode/qr-v1-alnum-M | ModulePlacerMaskCode 61.1, EccBinaryEncoder 3.1 |
| encode/qr-v1-num-L | ModulePlacerMaskCode 63.1, EccBinaryEncoder 2.7 |
| encode/qr-v20-byte-M | ModulePlacerMaskCode 30.8, EccBinaryEncoder 8.8 |
| encode/qr-v40-byte-H | ModulePlacerMaskCode 18.5, EccBinaryEncoder 6.2 |
| encode/qr-v40-byte-L | ModulePlacerMaskCode 18.1, EccBinaryEncoder 12.9, TextAnalyzer 1.5 |
| encode/qr-v6-url-M | ModulePlacerMaskCode 34.9, EccBinaryEncoder 9.4 |
| encode/rmqr-r11x59-alnum | RmQRModulePlacer 29.8, EccBinaryEncoder 28.9 |
| encode/rmqr-r17x139-byte | EccBinaryEncoder 48.3, RmQRModulePlacer 27.2 |
| encode/rmqr-r7x43-num | RmQRModulePlacer 27.2, EccBinaryEncoder 12.0 |
| encode/sa-byte-45k-single | ModulePlacerMaskCode 34.2, EccBinaryEncoder 25.2, TextAnalyzer 4.9, ModeSegmenter 1.3 |
| encode/sa-mixed-40k-optimal | ModulePlacerMaskCode 26.0, EccBinaryEncoder 18.6, ModeSegmenter 18.4, TextAnalyzer 4.1 |
| encode/sa-mixed-40k-single | ModulePlacerMaskCode 34.3, EccBinaryEncoder 24.7, TextAnalyzer 4.9, ModeSegmenter 1.2 |
| encode/sa-utf8-15k-mixed-optimal | ModulePlacerMaskCode 27.0, EccBinaryEncoder 21.9, ModeSegmenter 13.4, StructuredAppendPlanner(walks) 2.4 |
| image/micro-m4-8px | Binarizer 29.2, FinderRowPass 27.7, FinderCrossChecks 9.3, MicroQRSampleGrid 5.6, EccSyndromes 1.5 |
| image/none-gradient | FinderRowPass 35.4, LocalBinarizer 20.5, Binarizer 12.4, FinderCrossChecks 2.1 |
| image/none-noise | FinderRowPass 36.0, FinderCrossChecks 21.7, QRSampleGrid 5.6, LocalBinarizer 3.6, AlignmentRowMask 1.8, Binarizer 1.8 |
| image/qr-v25-4px-keystone15 | FinderRowPass 53.2, Binarizer 13.4, FinderCrossChecks 7.3, QRSampleGrid 3.3, EccSyndromes 2.9 |
| image/qr-v40-3.4px | FinderRowPass 25.5, Binarizer 23.4, EccSyndromes 11.2, QRSampleGrid 8.8, FinderCrossChecks 7.2 |
| image/qr-v40-3px | FinderRowPass 25.0, Binarizer 21.2, EccSyndromes 11.6, QRSampleGrid 10.6, FinderCrossChecks 6.2 |
| image/qr-v40-4px-rot17 | Binarizer 31.5, FinderRowPass 26.5, EccSyndromes 8.5, QRSampleGrid 7.7, FinderCrossChecks 4.4 |
| image/qr-v40-4px-soft | FinderRowPass 30.5, Binarizer 20.7, EccSyndromes 10.2, QRSampleGrid 8.4, FinderCrossChecks 6.5 |
| image/qr-v6-4px | FinderRowPass 26.1, Binarizer 19.9, QRSampleGrid 9.9, FinderCrossChecks 9.8, EccSyndromes 4.1 |
| image/rmqr-r17x139-4px-keystone15 | Binarizer 26.2, FinderRowPass 20.0, RmQRSampleGrid 1.9, FinderCrossChecks 1.7, EccSyndromes 1.0 |
| image/rmqr-r17x139-8px | Binarizer 33.2, FinderRowPass 27.6, FinderCrossChecks 9.6, RmQRSampleGrid 5.3, EccSyndromes 2.7, RmQRExtractCodewords 1.4 |
| image/rmqr-r7x43-8px | Binarizer 30.8, FinderRowPass 25.9, FinderCrossChecks 11.8, RmQRSampleGrid 3.7 |
| matrix/micro-m2-num | EccSyndromes 13.2, EccDecoder(other) 3.2 |
| matrix/micro-m4-byte | EccSyndromes 30.7, EccDecoder(other) 1.8 |
| matrix/qr-v1-num-L | EccSyndromes 22.3 |
| matrix/qr-v40-byte-H | EccSyndromes 74.7 |
| matrix/qr-v40-byte-L | EccSyndromes 77.2 |
| matrix/qr-v6-url-M | EccSyndromes 49.3 |
| matrix/rmqr-r17x139-byte-corrected | EccSyndromes 52.2, EccDecoder(other) 24.4, RmQRExtractCodewords 14.2 |
| matrix/rmqr-r17x139-byte | EccSyndromes 58.3, RmQRExtractCodewords 27.1, EccDecoder(other) 1.1 |
| matrix/rmqr-r7x43-num | EccSyndromes 20.6, RmQRExtractCodewords 17.6, EccDecoder(other) 3.1 |
| matrix/sa-byte-45k-set | EccSyndromes 71.5 |

## Phase 1b: the samplers and the lattice after the fix

The old code (`HEAD` before the change) against the new, three alternations of each on each build, each shape divided by two kernels the change leaves alone (`LocalBinarizer`, `FinderRowMask`), median of the three. This machine switched between two speed states during the runs, which moves whole runs by up to 40 %; the division takes most of that out, not all of it (the interpreter rows most of all).

| Shape | JIT, AVX2 | JIT, no AVX | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|---|---|
| image/qr-v40-3px | 1.00 (94.43 → 91.42) | 0.88 (460.28 → 440.76) | 0.69 (709.64 → 478.73) | 0.77 (1106.37 → 873.73) | 1.15 (3363.46 → 4106.70) |
| image/qr-v6-4px | 1.04 (10.18 → 10.11) | 1.04 (25.77 → 32.24) | 0.72 (40.87 → 28.55) | 0.76 (77.85 → 57.20) | 1.01 (301.90 → 321.07) |
| image/micro-m4-8px | 1.00 (4.70 → 4.51) | 1.03 (12.10 → 13.77) | 0.88 (12.88 → 11.02) | 0.91 (25.67 → 23.60) | 1.26 (100.32 → 119.89) |
| image/rmqr-r17x139-8px | 1.01 (19.99 → 19.84) | 1.03 (62.55 → 79.59) | 0.81 (92.22 → 73.74) | 0.89 (157.85 → 140.78) | 1.23 (746.21 → 870.40) |
| image/rmqr-r17x139-4px-keystone15 | 1.01 (120.05 → 117.46) | 1.05 (180.28 → 183.50) | 0.95 (219.22 → 204.39) | 0.94 (503.53 → 453.50) | 1.07 (3119.73 → 2990.69) |
| bitmap/rmqr-r17x139-8px | 1.00 (38.13 → 37.00) | 0.97 (241.34 → 224.80) | 0.97 (270.49 → 253.25) | 0.90 (514.77 → 451.70) | 1.28 (1457.79 → 1792.92) |
| kernel/QRSampleGrid | 1.02 (31.28 → 30.93) | 0.88 (43.16 → 32.50) | 0.15 (237.35 → 34.10) | 0.15 (344.30 → 52.69) | 1.16 (259.25 → 280.28) |
| kernel/QRSampleGrid-scalar | 0.82 (64.64 → 50.94) | 0.78 (102.46 → 80.75) | 0.80 (68.67 → 53.78) | 1.08 (208.45 → 214.97) | 1.26 (544.13 → 616.90) |
| kernel/MicroQRSampleGrid | 0.95 (0.24 → 0.22) | 0.95 (0.38 → 0.36) | 0.13 (2.14 → 0.27) | 0.18 (3.73 → 0.65) | 1.25 (2.72 → 3.12) |
| kernel/MicroQRSampleGrid-scalar | 0.76 (0.51 → 0.37) | 0.68 (0.79 → 0.43) | 0.79 (0.54 → 0.42) | 1.07 (1.52 → 1.64) | 1.61 (3.65 → 4.82) |
| kernel/RmQRSampleGrid | 1.01 (1.52 → 1.47) | 0.91 (2.31 → 2.06) | 0.11 (18.71 → 1.96) | 0.13 (30.15 → 3.75) | 1.37 (11.71 → 14.68) |
| kernel/RmQRSampleGrid-scalar | 0.81 (5.54 → 4.30) | 0.77 (8.23 → 7.61) | 0.89 (5.82 → 5.21) | 1.10 (18.33 → 19.08) | 0.70 (269.92 → 196.08) |
| kernel/RmQRSubFinderLattice | 0.94 (0.25 → 0.23) | 0.88 (0.39 → 0.31) | 0.08 (3.10 → 0.24) | 0.12 (4.94 → 0.55) | 1.38 (1.47 → 1.89) |
| kernel/RmQRSubFinderLattice-scalar | 1.02 (0.80 → 0.79) | 1.07 (1.05 → 1.04) | 0.98 (0.78 → 0.79) | 1.10 (1.66 → 1.76) | 1.27 (7.27 → 8.59) |

Ratio new over old, with raw µs of the median run. Where the interpreter rows disagree with raw pairs from the same speed state, the raw pairs show the samplers 6 to 26 % slower, the lattice 20 to 55 %, and whole decodes within noise.

The conversion alone, µs per 4,096 vectors:

| Form | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| `Vector128.ConvertToInt32` (the old code) | 52.1 | 52.6 | 105 |
| `PackedSimd.ConvertToInt32Saturate`, unguarded | - | 6.7 | 105 |
| the same with `pmax`/`pmin` guards, inline | - | 8.0 | 122 |
| `VectorCast.ToInt32` | 1.7 | 8.0 | 127 |

`VectorCast.ToInt32` costs what the same operations written inline cost on every build but the interpreter, where it cost 4 % more. The follow-up below found why: the inline probe built the cap constant once, the helper on every call. `PixelIndex.Clamp` costs what the inline clamp costs everywhere, 1.5 % more interpreted.

Machine code per method, instructions before → after (ILC ARM64 built against the ARM64 runtime pack; the listings of one thread). Methods are told apart by parameter count: 7p Standard QR, 8p rMQR, 12p Micro QR; `SampleGridSimd` is Standard QR's 256-bit tier, which ILC compiles for ARM64 though it never runs there. ARM64's added instructions in the scalar samplers are the two limits converted to float before the loop; the loop bodies are the same length.

```
== ilc-x64
ClassifySubFinderLatticeScalar 15p      195 ->  195
ClassifySubFinderLatticeVector128 15p   443 ->  271
SampleGridScalar 12p                    111 ->  107
SampleGridScalar 7p                     113 ->  107
SampleGridScalar 8p                     108 ->  100
SampleGridSimd 7p                       524 ->  517
SampleGridSimd128 7p                    674 ->  404
SampleGridSimd128 8p                    656 ->  427
SampleGridVector128 12p                 404 ->  304
== ilc-v3
ClassifySubFinderLatticeScalar 15p      184 ->  184
ClassifySubFinderLatticeVector128 15p   247 ->  236
SampleGridScalar 12p                    107 ->  105
SampleGridScalar 7p                     106 ->  102
SampleGridScalar 8p                     101 ->   95
SampleGridSimd 7p                       234 ->  233
SampleGridSimd128 7p                    331 ->  309
SampleGridSimd128 8p                    347 ->  333
SampleGridVector128 12p                 296 ->  276
== ilc-arm64
ClassifySubFinderLatticeScalar 15p      100 ->  100
ClassifySubFinderLatticeVector128 15p   165 ->  165
SampleGridScalar 12p                     62 ->   66
SampleGridScalar 7p                      80 ->   82
SampleGridScalar 8p                      80 ->   82
SampleGridSimd 7p                       210 ->  212
SampleGridSimd128 7p                    242 ->  244
SampleGridSimd128 8p                    219 ->  219
SampleGridVector128 12p                 204 ->  202
== jit-avx2
ClassifySubFinderLatticeScalar 15p      170 ->  170
ClassifySubFinderLatticeVector128 15p   216 ->  209
SampleGridScalar 12p                     99 ->   91
SampleGridScalar 7p                     104 ->   94
SampleGridScalar 8p                     101 ->   95
SampleGridSimd 7p                       214 ->  203
SampleGridSimd128 8p                    307 ->  295
SampleGridVector128 12p                 269 ->  243
== jit-noavx
ClassifySubFinderLatticeScalar 15p      195 ->  195
ClassifySubFinderLatticeVector128 15p   287 ->  262
SampleGridScalar 12p                    111 ->  107
SampleGridScalar 7p                     113 ->  107
SampleGridScalar 8p                     108 ->  100
SampleGridSimd128 7p                    375 ->  331
SampleGridSimd128 8p                    390 ->  354
SampleGridVector128 12p                 313 ->  282
```

## Phase 1b follow-up: the interpreter

Builds: before 1b, 1b (`HEAD`), and now (`VectorCast.ToPixel` and `ToInt32Native`), alternated.

Kernels interpreted, raw µs, five alternations in one speed state (`LocalBinarizer` 1,328-1,348 µs in every run):

| Kernel | Before 1b | 1b | Now |
|---|---|---|---|
| QRSampleGrid | 240.8-243.3 | 257.3-259.8 | 236.1-238.7 |
| MicroQRSampleGrid | 2.29-2.30 | 2.73-2.77 | 2.32-2.34 |
| RmQRSampleGrid | 10.79-10.90 | 11.86-11.95 | 10.26-10.37 |
| RmQRSubFinderLattice | 1.29-1.30 | 1.66-1.68 | 1.29-1.31 |

Each build, three alternations, each shape over `LocalBinarizer` from its run, median, as a ratio to before 1b (1b → now):

| Shape | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| image/qr-v40-3px | 0.673 → 0.700 | 0.758 → 0.807 | 1.004 → 0.985 |
| image/qr-v6-4px | 0.698 → 0.735 | 0.795 → 0.843 | 1.020 → 0.985 |
| image/micro-m4-8px | 0.854 → 0.857 | 0.876 → 0.894 | 0.997 → 0.998 |
| image/rmqr-r17x139-8px | 0.795 → 0.827 | 0.856 → 0.873 | 1.017 → 0.993 |
| image/rmqr-r17x139-4px-keystone15 | 0.922 → 0.917 | 0.932 → 0.957 | 1.012 → 0.990 |
| kernel/QRSampleGrid | 0.144 → 0.125 | 0.171 → 0.169 | 1.065 → 0.976 |
| kernel/MicroQRSampleGrid | 0.128 → 0.114 | 0.209 → 0.205 | 1.193 → 1.013 |
| kernel/RmQRSampleGrid | 0.105 → 0.091 | 0.145 → 0.141 | 1.104 → 0.955 |
| kernel/RmQRSubFinderLattice | 0.079 → 0.067 | 0.161 → 0.138 | 1.289 → 0.993 |

The AOT image rows that read slower come mostly from the normalization. Raw, default NativeAOT `qr-v40-3px` over six alternations: 1b 472-583, now 464-499 µs, both builds split between two speed states. WebAssembly AOT: 660-664 against 670-671 µs, 1.5 %, where the unsigned conversion costs the Standard QR sampler 1 % (below).

The pixel conversion alone, µs per 4,096 vectors (coordinates from -1,024 to 5,120 into a 4,096-pixel line):

| Form | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| `PackedSimd.ConvertToInt32Saturate`, no clamp | - | 6.6-6.7 | 103-104 |
| before 1b: `Vector128.ConvertToInt32`, then clamp the integers | 55.8-56.1 | 53.9-55.2 | 131.7-134.1 |
| `pmax`, `pmin`, signed conversion, inline | - | 9.1-9.3 | 129.2-131.2 |
| `pmin`, unsigned conversion, inline | - | 10.7-10.9 | 131.4-132.4 |
| `VectorCast.ToPixel` | 2.3 | 10.7-10.9 | 126.0-127.3 |

Signed against unsigned inside the kernels, µs (the builds differ in nothing else):

| Kernel | WebAssembly AOT, signed → unsigned | Interpreted, signed → unsigned |
|---|---|---|
| QRSampleGrid | 37.2 → 37.6 | 235.6 → 236.0 |
| MicroQRSampleGrid | 0.40 → 0.40 | 2.50 → 2.31 |
| RmQRSampleGrid | 2.16 → 2.22 | 10.79 → 10.26 |

Interpreter ops in Micro QR's four-lane core, from its compiled code (`MONO_VERBOSE_METHOD`): 17 before 1b, 27 in 1b (the guards, and the cap built twice per step as `ldc.r4` and a splat), 13 now.

Scalar clamp forms. Interpreted, each shape over `LocalBinarizer`, as a ratio to before 1b; the vector tiers all use the signed `ToPixel` here:

| Shape | 1b's float-first form | One unsigned test |
|---|---|---|
| kernel/QRSampleGrid | 0.916 | 1.025 |
| kernel/MicroQRSampleGrid | 1.011 | 1.027 |
| kernel/QRSampleGrid-scalar | 1.056 | 1.068 |
| kernel/MicroQRSampleGrid-scalar | 1.356 | 1.117 |
| kernel/RmQRSampleGrid-scalar | 0.606 | 0.899 |

Default NativeAOT, raw µs from the fast runs: `QRSampleGrid-scalar` 53.6-54.5 against 45.1-45.7, `MicroQRSampleGrid-scalar` 0.4 against 0.3, `RmQRSampleGrid-scalar` 5.2-5.3 against 4.0-4.1. The integer tests with a sign check: `QRSampleGrid-scalar` 628 µs interpreted, against 505 before 1b. Micro QR with an overlapping last vector step instead of the scalar tail: 2.88 against 2.48 µs interpreted.

Machine code per method, instructions 1b → now (method names as in Phase 1b):

```
== ilc-x64
ClassifySubFinderLatticeVector128 15p   271 ->  255
SampleGridSimd128 7p                    404 ->  336
SampleGridSimd128 8p                    427 ->  364
SampleGridVector128 12p                 304 ->  286
== ilc-v3
ClassifySubFinderLatticeVector128 15p   236 ->  224
SampleGridSimd128 7p                    309 ->  294
SampleGridSimd128 8p                    333 ->  319
SampleGridVector128 12p                 276 ->  270
== ilc-arm64
ClassifySubFinderLatticeVector128 15p   165 ->  165
SampleGridSimd128 7p                    244 ->  239
SampleGridSimd128 8p                    219 ->  214
SampleGridVector128 12p                 202 ->  200
== jit-avx2
ClassifySubFinderLatticeVector128 15p   209 ->  200
SampleGridSimd128 8p                    295 ->  262
SampleGridVector128 12p                 243 ->  236
== jit-noavx
ClassifySubFinderLatticeVector128 15p   262 ->  247
SampleGridSimd128 7p                    331 ->  311
SampleGridSimd128 8p                    354 ->  338
SampleGridVector128 12p                 282 ->  275
```

The scalar samplers, the scalar lattice and Standard QR's 256-bit tier are unchanged on every target. The first ARM64 form (`fmax` with zero, `fmin`, `fcvtzs`) was +7 instructions: ILC rebuilt the zero constant before every `fmax` instead of keeping it in a register, which the unsigned conversion avoids.
