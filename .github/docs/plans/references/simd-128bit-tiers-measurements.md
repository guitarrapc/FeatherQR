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

## Phase 2: near ports

Kernels alone are timed inside one build: the new tier through the dispatch, beside the scalar or the old path through its own entry. Three runs each; the range of the three.

### FinderRowEdges against the mask walk

µs a search, every row, FinderCandidates:

| Build | v40 at 3 px, mask walk | edge list | 740 × 740 noise, mask walk | edge list |
|---|---|---|---|---|
| NativeAOT default | 495.3-503.5 | 212.2-214.4 | 4,842.8-4,885.1 | 2,610.0-2,629.0 |
| WebAssembly AOT | 773.6-776.2 | 187.2-191.7 | 7,619.8-7,728.9 | 3,743.7-3,787.8 |
| WebAssembly interpreted | 5,362.1-5,514.1 | 1,480.4-1,530.5 | 50,580.6-54,414.1 | 24,017.9-24,563.5 |

Image decode, each shape over `LocalBinarizer` from its run, median of three alternations, the edge list against the mask walk (raw µs of the mask walk's build):

| Shape | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| image/qr-v40-3px | 0.790 (492.8) | 0.591 (714.2) | 0.553 (3,197.2) |
| image/qr-v40-3.4px | 0.793 (568.4) | 0.610 (800.1) | 0.566 (3,420.2) |
| image/qr-v40-4px-rot17 | 0.813 (752.8) | 0.591 (1,131.9) | 0.584 (4,489.7) |
| image/qr-v40-4px-soft | 0.705 (591.4) | 0.531 (889.4) | 0.541 (4,498.4) |
| image/qr-v25-4px-keystone15 | 0.584 (674.2) | 0.360 (1,173.4) | 0.378 (5,353.7) |
| image/qr-v6-4px | 0.837 (29.7) | 0.601 (47.8) | 0.643 (271.4) |
| image/none-noise | 0.661 (8,585.5) | 0.549 (11,689.1) | 0.580 (78,615.0) |
| image/none-gradient | 0.954 (939.2) | 0.591 (2,200.9) | 0.880 (5,683.9) |
| image/micro-m4-8px | 0.903 (11.4) | 0.622 (18.6) | 0.748 (85.0) |
| image/rmqr-r7x43-8px | 0.856 (13.4) | 0.605 (21.8) | 0.726 (128.0) |
| image/rmqr-r17x139-8px | 0.818 (74.6) | 0.575 (115.3) | 0.648 (616.6) |
| image/rmqr-r17x139-4px-keystone15 | 0.953 (208.4) | 0.672 (382.9) | 0.937 (2,549.9) |
| bitmap/qr-v40-3px | 0.847 (819.2) | 0.759 (1,053.6) | 0.686 (4,261.0) |
| bitmap/rmqr-r17x139-8px | 0.959 (261.5) | 0.796 (330.4) | 0.825 (1,313.0) |

The whole-phase run against the build before phase 2 (every `encode/`, `matrix/`, `image/` and `bitmap/` shape, three alternations) was taken while the machine swung between speed states: `encode/qr-v40-byte-L` on default NativeAOT, whose code is identical in both builds, read 102-139 µs before and 98-161 after. Only the image rows stand out of that spread, version 40 at 3 px 666-671 → 392-501 µs default NativeAOT, 692-832 → 451-515 WebAssembly AOT, 3,147-4,704 → 1,702-2,155 interpreted.

### WebAssembly steps

`TextAnalyzer`, µs, scalar pass → the tier:

| Text | Interpreted | AOT |
|---|---|---|
| 2,900 ASCII chars (Byte) | 8.53-8.60 → 1.12-1.15 | 3.85-5.29 → 0.32-0.41 |
| 2,900 digits | 15.7-16.1 → 5.86-5.89 | 4.57-5.11 → 0.44-0.62 |
| the short URL | 0.211-0.212 → 0.199-0.208 | 0.069-0.074 → 0.028-0.033 |

`ModuleBitPacker`, an R17x139 core, µs, scalar → the step: pack 1.195-1.199 → 0.524-0.532 interpreted, 0.289-0.292 → 0.132-0.133 AOT; unpack 2.136-2.157 → 0.514-0.529 interpreted, 0.588-0.599 → 0.156-0.157 AOT. The span entries (`encode/`, `matrix/`) never call it: only the data-object API does, `Create` returning `RmQRCodeData` or `MicroQRCodeData` and `TryDecode` taking one (`data/` shapes). Those, µs, same-state runs, before → after: interpreted R17x139 encode 19.15-19.26 → 18.52-18.64, decode 24.47-24.54 → 22.91-22.92, M4 encode 3.63-3.70 → 3.56-3.63, decode 3.94-4.00 → 3.70-3.77; AOT R17x139 encode 3.43-3.63 → 3.22-3.46, decode 6.99-7.46 → 6.52-6.99, M4 decode 0.94-1.05 → 0.91-0.97.

`RmQRModulePlacer`, an R17x139 symbol, µs, the portable path → the dispatch: interpreted 4.02-4.03 → 3.84-3.86, AOT 0.893-0.899 → 0.833-0.844 (with the swizzle; the portable shuffle gave 4.05-4.12 → 3.87-4.06 and 0.91-1.15 → 0.86-1.11).

The expand itself, 16 bits to 16 module bytes, 4,096 times, µs:

| Form | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| two SWAR spreads of 8 (the scalar placers) | 8.64-8.65 | 9.96-10.09 | 64.97-65.22 |
| portable constant shuffle, bit kept, min with 1 | 47.8-48.2 | 5.08-5.17 | 24.53-24.61 |
| WebAssembly's swizzle for the shuffle | - | 5.07-5.17 | 21.58-22.15 |
| each byte broadcast by a 64-bit multiply | 4.48 | 7.79-7.87 | 46.39-46.42 |

(The portable shuffle on default NativeAOT is the SSE2 software fallback; the x64 tiers use `Ssse3.Shuffle`.)

### Left scalar

| Kernel | Build | Measured |
|---|---|---|
| `MicroQRModulePlacer` | WebAssembly | An M4 symbol, µs, scalar → the vector unpack: AOT 0.270-0.274 → 0.246-0.248, interpreted 1.419-1.441 → 1.586-1.591 (the vector core entered directly: 1.58-1.74). Both paths are traced whole by the jiterpreter; the expand alone wins 2.6x interpreted (above), so the loss is elsewhere in the vector core, not found |
| `MicroQRByteSegment` | WebAssembly | The Byte codeword encoder for 14 chars, µs, scalar → a vector step: interpreted 0.234-0.242 → 0.149-0.166, AOT 0.039-0.040 → 0.030-0.033; an M4 Byte encode is 3.4 and 0.6 µs, so 2.5 and 1.5 % |
| `ModulePlacerExpandBits` | WebAssembly | A version 40 message, 2.85-3.76 µs of a 606-659 µs encode AOT (0.5 %), 4.27-7.19 of 1,354-2,049 interpreted (0.3 %) |
| `StructuredAppendParity`, `StructuredAppendScanner` | x64, WebAssembly | Phase 1: parity 1.2 % of a 45,000-character set without AVX, 0.7-0.8 % with AVX2 (7.95 of 1,197 µs JIT, 11.7 of 1,511 NativeAOT `x86-64-v3`), 0.8 % on WebAssembly AOT; scanner 0.2 % or less, 0.1 % |

## Phase 3: image decode

Each shape runs in its own process, three runs, the new tier and the build before it alternating (end to end) or the dispatch and the scalar entry alternating (kernels). The range of the three; ratios are of their medians. Why one process a shape: below.

### Kernels alone

µs a call, the scalar entry → the dispatch (the new tier on these builds), ratio:

| Kernel | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| Histogram, v40 at 3 px | 166-168 → 17.3-18.0 (0.11) | 181-183 → 15.9-16.0 (0.09) | 403-423 → 63.1-64.3 (0.16) |
| Histogram, v40 at 3.4 px | 199-206 → 22.3-23.0 (0.11) | 222-225 → 19.8-20.4 (0.09) | 522-537 → 80.9-82.0 (0.16) |
| Histogram, v40 at 4 px rotated | 326-330 → 152-161 (0.48) | 388-390 → 117-123 (0.31) | 1,025-1,046 → 505-522 (0.51) |
| Histogram, v40 at 4 px soft | 172-181 → 171-171 (0.99) | 218-219 → 226-227 (1.04) | 836-845 → 835-842 (0.99) |
| Histogram, 740 × 740 noise | 154-156 → 157-157 (1.01) | 203-224 → 210-218 (1.07) | 838-849 → 841-1,226 (0.99) |
| Histogram, 740 × 740 gradient | 267-272 → 246-261 (0.93) | 252-271 → 282-297 (1.11) | 876-1,492 → 876-1,208 (0.99) |
| Luminance, v40 at 3 px, opaque | 266-284 → 58.4-58.6 (0.21) | 315-341 → 205-213 (0.62) | 1,105-1,855 → 599-1,022 (0.54) |
| Luminance, R17x139 at 8 px, opaque | 166-173 → 36.3-36.6 (0.22) | 205-224 → 129-133 (0.62) | 681-1,253 → 374-648 (0.58) |
| Luminance, v40 at 3 px, straight alpha | 313-332 → 129-132 (0.41) | 370-404 → 369-371 (1.00) | 1,196-1,216 → 1,196-1,267 (1.01) |
| Mesh sampler, v40 at 3 px (against the column table) | 57.3-58.0 → 28.9-29.2 (0.50) | 120-124 → 35.7-36.7 (0.30) | 637-785 → 203-321 (0.41) |

The interpreted luminance and mesh rows spread widely between runs, as the machine's load changed during them.

### End to end

Against the build before phase 3 (HEAD `f3fb2f8`, the same harness), ratio of medians (base range → new range, µs):

| Shape | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| image/qr-v40-3px | 0.57 (349-357 → 199-205) | 0.60 (411-421 → 245-249) | 0.79 (1,714-1,718 → 1,356-1,363) |
| image/qr-v40-3.4px | 0.54 (398-408 → 215-220) | 0.56 (462-467 → 257-261) | 0.76 (1,890-1,943 → 1,429-1,468) |
| image/qr-v40-4px-rot17 | 0.76 (516-547 → 387-405) | 0.59 (641-669 → 369-393) | 0.78 (2,393-2,449 → 1,905-1,968) |
| image/qr-v40-4px-soft | 0.99 (376-390 → 376-383) | 1.02 (471-483 → 478-485) | 1.01 (2,251-2,261 → 2,258-2,278) |
| image/qr-v25-4px-keystone15 | 0.75 (316-347 → 230-284) | 0.70 (373-394 → 267-272) | 0.90 (1,876-1,934 → 1,684-1,764) |
| image/qr-v6-4px | 0.66 (23.0-23.3 → 15.3-15.4) | 0.67 (29.0-29.3 → 19.4-19.6) | 0.80 (166-173 → 131-136) |
| image/qr-v25-4px-bowed | 0.84 (510-529 → 406-443) | 0.72 (559-575 → 399-425) | 0.92 (3,579-3,812 → 3,140-3,543) |
| image/qr-v40-3.5px-bowed | 0.89 (1,000-1,013 → 899-906) | 0.76 (1,091-1,112 → 822-839) | 0.93 (7,148-7,637 → 6,524-6,672) |
| image/none-noise | 1.00 (5,393-5,425 → 5,409-5,446) | 0.99 (6,299-6,400 → 6,323-6,363) | 1.00 (41,381-42,913 → 41,046-44,124) |
| image/none-gradient | 1.01 (831-858 → 812-855) | 1.02 (1,086-1,133 → 1,114-1,170) | 1.00 (4,487-4,518 → 4,496-4,509) |
| image/micro-m4-8px | 0.63 (9.96-10.2 → 6.04-6.36) | 0.65 (11.8-12.0 → 7.58-7.72) | 0.76 (55.4-56.2 → 42.4-50.4) |
| image/rmqr-r7x43-8px | 0.64 (11.2-11.4 → 7.16-7.34) | 0.60 (13.4-13.6 → 8.06-8.19) | 0.74 (71.7-72.8 → 53.0-53.7) |
| image/rmqr-r17x139-8px | 0.56 (59.8-60.4 → 33.6-34.3) | 0.53 (65.1-67.4 → 34.9-35.6) | 0.65 (326-346 → 212-217) |
| image/rmqr-r17x139-4px-keystone15 | 0.96 (178-194 → 167-177) | 0.78 (254-265 → 206-208) | 0.91 (2,164-2,221 → 1,926-2,089) |
| bitmap/qr-v40-3px | 0.42 (620-629 → 246-278) | 0.61 (748-754 → 455-459) | 0.70 (2,775-2,790 → 1,880-2,030) |
| bitmap/rmqr-r17x139-8px | 0.33 (220-229 → 66.6-71.8) | 0.61 (272-279 → 165-166) | 0.56 (1,023-1,065 → 593-600) |

The `image/` shapes hand over luminance, so they move with the histogram and, where the mesh reads (the bowed shapes), with the mesh sampler; the `bitmap/` shapes add the conversion. The two bowed shapes are new: a symbol bent off the plane, read only through the mesh after the anchored transform fails. Before the port the mesh was 9.8 % (version 25) and 11.4 % (version 40) of those decodes on WebAssembly AOT (V8 sampler, self time).

### One process a shape

The first whole-phase run timed all shapes in one process per build, three alternations, divided by two unchanged kernels from the same run. Interpreted, those kernels read about 1.8x slower in every base run than in every new run (`kernel/LocalBinarizer` 2,699-2,729 against 1,488-1,509 µs), and alone in a process both builds gave 1,317-1,338. The base build's image shapes run more scalar code before them, so what a shape costs interpreted depends on what the process ran first; the jiterpreter's trace budget is the likely cause, not confirmed. The same run read `image/qr-v40-3px` base at 2,860-2,894 µs, against 1,714-1,718 alone, and the soft shape 1.34 new/base, against 1.01 alone. Every number above is one shape to a process.

### Measured and refused

| What | Build | Measured |
|---|---|---|
| Histogram, the dense blocks' group walk in its own method, called per dense block | WebAssembly AOT, NativeAOT default | Noise 229-265 → 308-345 µs and 157-159 → 194-197 |
| Histogram, only the untested stretch in its own method | WebAssembly AOT, NativeAOT default | Tier/scalar per process, five alternations: soft 1.02 → 1.11 and 1.02 → 1.11, noise 1.02 → 1.13 on default NativeAOT; the gradient 0.90 → 0.69 there |
| Luminance, the vector composite for partially transparent blocks | WebAssembly | 1.5x slower than the per-pixel formula, AOT and interpreted; those blocks take the formula there, and a row in the composite mode the scalar loop |

The two histogram variants were measured while another session's test runs loaded the machine (60 % when checked). Timed in one process, every shape ran slower than alone, the scalar gradient 325-852 µs against 252-271, so only the differences that held across the alternations are listed.

## Phase 4: encode lanes and mask scoring

Each shape alone in its own process, three runs, the two sides alternating: the range of the three, ratios of their medians.

### Mask selection

µs a call, the scalar bit-packed paths → the dispatch (the 128-bit tier on these builds):

| Version | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| 1 | 2.14-2.17 → 1.28-1.29 (0.59) | 2.68-2.75 → 1.32-1.33 (0.49) | 12.7-13.6 → 8.89-9.03 (0.70) |
| 6 | 4.10-4.17 → 2.51-2.61 (0.63) | 5.02-5.13 → 2.44-2.47 (0.49) | 23.7-24.1 → 17.0-58.2 (0.71) |
| 10 | 6.21-6.29 → 3.69-3.73 (0.59) | 7.41-7.54 → 3.49-3.62 (0.46) | 36.1-36.7 → 24.9-25.6 (0.69) |

The AVX2 tier's two- and three-word SoA scorers on two rows a vector, versions 12-40, measured and removed:

| Version | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| 12 | 26.0-26.2 → 27.6-28.0 (1.06) | 23.3-23.6 → 36.1-36.7 (1.55) | 234-257 → 523-535 (2.23) |
| 20 | 39.9-40.6 → 38.0-47.9 (0.94) | 37.2-37.4 → 52.3-53.2 (1.41) | 321-360 → 772-787 (2.44) |
| 27 | 52.5-54.1 → 52.9-69.2 (1.08) | 49.9-51.1 → 69.0-70.8 (1.40) | 443-499 → 1,017-1,031 (2.19) |
| 28 | 54.1-54.2 → 90.9-93.6 (1.68) | 52.1-137 → 111-136 (2.22) | 438-488 → 2,484-2,656 (5.80) |
| 40 | 76.8-78.2 → 127-128 (1.64) | 73.8-74.5 → 156-157 (2.11) | 624-639 → 3,368-3,511 (5.41) |

The popcount on default NativeAOT, the SSSE3 nibble table against the SWAR count of each 16-bit lane (a build with the SSSE3 branch compiled out), versions 1, 6 and 10: 1.27-1.29, 2.51-2.54 and 3.69-3.71 µs against 1.53-1.54, 3.02-3.06 and 4.43-4.52. The `DOTNET_EnableSSSE3=0` knob reaches neither NativeAOT nor the .NET 10 JIT (both still report GFNI, which needs SSSE3); the .NET 8 JIT honours it, and the tests ran the SWAR branch there.

WebAssembly AOT with the portable 64-bit lane shifts: versions 1, 6 and 10 at 21.5-22.1, 39.8-40.5 and 55.8-59.4 µs against scalar's 2.66-2.67, 4.97-5.30 and 7.44-25.9. V8's sampler put 56 % of the time in corlib's `Vector128.ShiftRightLogical` for 64-bit lanes and 5 % in `ShiftLeft`, calls into the software fallback; the interpreted build ran the same code at 8.8 µs. With `PackedSimd`'s shifts: the first table.

### Structured Append walks

The planner alone (`kernel/StructuredAppendPlan-*`, the generator's `TryPlan` for the set) was 655.6-663.4 of a 2,161-2,180 µs Optimal set on default NativeAOT (30 % of the medians), 753-760 of 3,281-3,322 on WebAssembly AOT (23 %), and 3,430-3,483 of 20,366-21,466 interpreted (17 %); on the UTF-8 set 32 %, 24 % and 49 %.

Where the walk starts to pay. A set held at one version and level, sixteen symbols of mixed content, so its chunks average what a symbol holds: 510 characters in 16 chunks at 2-L (32 a chunk), 660 in 15 at 3-M (44), 825 in 15 at 3-L (55), 960 in 15 at 4-M (64), 1,200 in 15 at 4-L (80). The planner with its walks against its scalar probes only, µs, the walk taken from 20 characters on every build:

| Average chunk | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| 32 | 9.0-9.3 → 10.1-10.2 (1.10) | 9.2-9.2 → 8.7-8.8 (0.95) | 64.7-66.9 → 79.9-80.7 (1.21) |
| 44 | 11.4-11.7 → 10.3-10.6 (0.91) | 11.6-11.8 → 9.7-9.9 (0.85) | 75.4-76.1 → 82.7-85.7 (1.11) |
| 55 | 15.9-16.0 → 13.7-13.8 (0.86) | 15.7-21.0 → 12.3-12.8 (0.78) | 98.0-99.0 → 103.9-110.1 (1.06) |
| 64 | 18.6-19.0 → 15.6-15.9 (0.83) | 17.7-17.9 → 14.4-15.1 (0.81) | 111.0-114.1 → 113.2-119.6 (1.06) |
| 80 | 26.6-27.0 → 18.3-18.4 (0.69) | 24.1-24.4 → 16.4-16.8 (0.68) | 145.0-148.2 → 130.7-133.3 (0.90) |

With the gate at 40 characters on x64 and 80 on WebAssembly:

| Average chunk | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| 32 | 9.00-9.13 → 9.23-9.37 (1.02) | 9.25-9.81 → 9.16-9.78 (0.98) | 65.0-67.0 → 65.4-66.8 (1.01) |
| 44 | 11.4-11.6 → 10.4-10.5 (0.91) | 11.7-11.8 → 11.9-12.7 (1.01) | 75.9-77.6 → 76.2-77.4 (0.99) |
| 55 | 15.8-16.2 → 13.8-13.9 (0.86) | 15.5-18.7 → 15.7-22.5 (1.00) | 98.9-101 → 99.1-102 (1.02) |
| 64 | 19.1-19.7 → 15.5-15.7 (0.81) | 18.0-18.0 → 18.1-19.3 (1.01) | 110-123 → 110-121 (0.95) |
| 80 | 26.2-26.5 → 18.2-18.3 (0.69) | 24.0-24.5 → 16.7-16.8 (0.69) | 145-146 → 129-497 (0.91) |

### End to end

Against the build before phase 4 (HEAD `3ef15fc`, the same harness), ratio of medians (base range → new range, µs):

| Shape | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| encode/qr-v1-num-L | 0.68 (2.2-2.2 → 1.5-1.5) | 0.69 (3.7-3.7 → 2.5-2.6) | 0.80 (16.6-17.2 → 13.2-14.1) |
| encode/qr-v1-alnum-M | 0.61 (2.5-2.6 → 1.6-1.6) | 0.67 (4.4-4.5 → 2.9-3.0) | 0.76 (18.7-19.0 → 14.4-14.5) |
| encode/qr-v6-url-M | 0.69 (4.8-4.8 → 3.3-3.4) | 0.83 (13.5-14.1 → 11.1-11.6) | 0.94 (45.6-45.7 → 40.3-45.2) |
| encode/qr-v20-byte-M | 0.99 (43.4-44.4 → 43.3-43.4) | 1.01 (105.8-107.7 → 105.8-193.6) | 1.02 (466.9-517.5 → 473.1-511.8) |
| encode/qr-v40-byte-L | 0.99 (94.6-96.5 → 93.9-94.7) | 0.99 (364.3-374.3 → 362.9-370.3) | 0.97 (1,327.8-1,355.2 → 1,283.6-1,390.6) |
| encode/qr-v40-byte-H | 0.99 (91.5-92.1 → 90.9-91.8) | 1.01 (330.3-331.7 → 330.4-337.6) | 1.00 (1,048.9-1,125.8 → 1,044.9-1,198.1) |
| encode/sa-byte-45k-single | 0.99 (1,611-1,615 → 1,604-1,621) | 0.99 (2,838-2,895 → 2,833-2,867) | 1.01 (20,116-20,297 → 20,197-20,421) |
| encode/sa-mixed-40k-single | 1.00 (1,392-1,427 → 1,386-1,394) | 1.01 (2,511-2,555 → 2,514-2,569) | 1.00 (17,032-17,669 → 16,809-19,095) |
| encode/sa-mixed-40k-optimal | 0.83 (2,161-2,180 → 1,795-1,805) | 0.92 (3,281-3,322 → 3,023-3,122) | 0.95 (20,366-21,466 → 18,881-20,643) |
| encode/sa-utf8-15k-mixed-optimal | 0.76 (1,534-1,593 → 1,173-1,189) | 0.91 (2,373-2,512 → 2,157-2,404) | 0.70 (25,707-38,711 → 16,749-18,497) |
| the planner, mixed 40k | 0.53 (655.6-663.4 → 343.4-349.6) | 0.69 (753.1-759.8 → 522.0-532.9) | 0.75 (3,430-3,483 → 2,555-2,569) |
| the planner, UTF-8 15k | 0.31 (485.7-511.4 → 152.3-155.9) | 0.61 (569.7-612.0 → 353.7-356.7) | 0.26 (12,733-19,380 → 3,267-3,503) |

Micro QR M4 and rMQR R17x139 encodes, whose code did not change, read 0.99 to 1.01 (one default NativeAOT row 0.90 at 1.2 µs). The segmenter's lanes alone, before the walk joined them, moved the two Optimal sets 0.97 and 0.99 on default NativeAOT, 1.00 on WebAssembly AOT and 0.97 interpreted.

## Phase 5: GF(256) and bit planes

Kernel shapes each alone in its own process, three runs, the two sides alternating: the range of the three, ratios of their medians. The per-version and per-block probes: three runs of one process on the AOT-compiled builds, each shape in its own process interpreted, medians.

### Syndrome pass

µs a block, the scalar pass → the 128-bit tier:

| Block (bytes, ECC) | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| 148, 30 (version 40-L) | 3.06-3.10 → 0.18 (0.06) | 4.28-4.33 → 0.24-0.25 (0.06) | 12.28-12.36 → 1.76-1.78 (0.14) |
| 45, 30 (version 40-H) | 0.89 → 0.06-0.07 (0.07) | 1.29-1.31 → 0.10 (0.08) | 3.79-3.81 → 0.62-0.63 (0.16) |
| 24, 8 (Micro QR M4-L) | 0.12 → 0.02 (0.18) | 0.18-0.19 → 0.04 (0.23) | 0.58 → 0.21 (0.36) |

### Reed-Solomon encoder on WebAssembly

µs a block, the scalar kernel → the dispatch (the WebAssembly kernel for these blocks):

| Block (data bytes, ECC) | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|
| 118, 30 (version 40-L) | 1.93-1.95 → 0.24 (0.12) | 12.49-12.61 → 1.37-1.43 (0.11) |
| 15, 30 (version 40-H) | 0.28-0.29 → 0.07 (0.23) | 1.66-1.89 → 0.55-0.57 (0.34) |
| 16, 8 (Micro QR M4-L) | 0.11-0.15 → 0.06-0.07 (0.54) | 0.61-0.62 → 0.38-0.48 (0.64) |

The kernel past the size gate against the scalar kernel on small blocks (`probe/ecc-encode-*`), ratio of medians, WebAssembly AOT | interpreted:

| Data bytes | ECC 2 | 5 | 7 | 10 | 17 |
|---|---|---|---|---|---|
| 3 | 1.05 \| 2.39 | 0.98 \| 2.05 | 0.89 \| 1.80 | 0.84 \| 1.54 | 0.71 \| 1.48 |
| 5 | 0.98 \| 2.13 | 0.86 \| 1.72 | 0.82 \| 1.35 | 0.70 \| 1.13 | 0.61 \| 1.05 |
| 6 | 1.00 \| 2.18 | 0.85 \| 1.55 | 0.77 \| 1.30 | 0.68 \| 1.03 | 0.57 \| 0.98 |
| 9 | 0.89 \| 2.03 | 0.80 \| 1.39 | 0.71 \| 1.04 | 0.58 \| 0.83 | 0.47 \| 0.73 |
| 13 | 0.81 \| 1.78 | 0.71 \| 1.09 | 0.60 \| 0.83 | 0.49 \| 0.73 | 0.39 \| 0.56 |
| 16 | 0.73 \| 1.57 | 0.65 \| 0.94 | 0.56 \| 0.81 | 0.45 \| 0.57 | 0.36 \| 0.49 |

The kernel costs about 0.04 µs AOT-compiled and 0.35 µs interpreted before its first step, where the scalar kernel takes 0.15 µs on 3 × 2. From 90 data bytes × ECC codewords every block wins or ties interpreted (6 × 17 at 0.98 the closest), and under it every block loses but 16 × 5 (0.94). Loading the swizzle indices from constant data instead of `Vector128.Create` was slower interpreted: 0.38-0.52 µs against 0.37-0.46 on four of these blocks.

### rMQR codeword extraction

µs a symbol, the scalar walk → the 128-bit pair planes, pinned:

| Symbol | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| R17x139 | 1.06-1.07 → 0.21 (0.20) | 1.69-1.72 → 0.71 (0.41) | 3.95-4.00 → 2.16-2.20 (0.55) |
| R13x77 | 0.39-0.40 → 0.11 (0.27) | 0.64-0.65 → 0.36 (0.56) | 1.55-1.57 → 1.12-1.14 (0.72) |
| R7x43 | 0.07 → 0.05 (0.72) | 0.12 → 0.17 (1.45) | 0.37-0.38 → 0.55 (1.49) |
| R11x27 | 0.08 → 0.05 (0.60) | 0.13 → 0.14-0.15 (1.09) | 0.40-0.41 → 0.49-0.50 (1.22) |

Every version, pinned pair planes over the walk, with the stream bits per 8-column block (`probe/rmqr-extract-*`):

| Symbol | Bits per block | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|---|
| R7x43 | 17 | 0.71 | 1.45 | 1.47 |
| R7x59 | 21 | 0.61 | 1.18 | 1.31 |
| R7x77 | 26 | 0.51 | 1.03 | 1.20 |
| R7x99 | 27 | 0.49 | 0.99 | 1.16 |
| R7x139 | 30 | 0.43 | 0.87 | 1.05 |
| R9x43 | 28 | 0.57 | 1.13 | 1.29 |
| R9x59 | 33 | 0.50 | 0.91 | 1.11 |
| R9x77 | 39 | 0.40 | 0.80 | 0.98 |
| R9x99 | 41 | 0.39 | 0.77 | 0.96 |
| R9x139 | 44 | 0.34 | 0.68 | 0.85 |
| R11x27 | 30 | 0.61 | 1.08 | 1.22 |
| R11x43 | 41 | 0.46 | 0.88 | 1.07 |
| R11x59 | 47 | 0.40 | 0.73 | 0.93 |
| R11x77 | 54 | 0.34 | 0.66 | 0.84 |
| R11x99 | 55 | 0.33 | 0.65 | 0.81 |
| R11x139 | 59 | 0.29 | 0.56 | 0.71 |
| R13x27 | 42 | 0.47 | 0.89 | 1.03 |
| R13x43 | 55 | 0.37 | 0.74 | 0.92 |
| R13x59 | 60 | 0.33 | 0.62 | 0.82 |
| R13x77 | 68 | 0.28 | 0.56 | 0.72 |
| R13x99 | 70 | 0.28 | 0.56 | 0.72 |
| R13x139 | 74 | 0.25 | 0.50 | 0.64 |
| R15x43 | 68 | 0.33 | 0.65 | 0.84 |
| R15x59 | 74 | 0.28 | 0.55 | 0.72 |
| R15x77 | 82 | 0.25 | 0.50 | 0.65 |
| R15x99 | 84 | 0.25 | 0.50 | 0.64 |
| R15x139 | 88 | 0.22 | 0.45 | 0.58 |
| R17x43 | 81 | 0.29 | 0.58 | 0.76 |
| R17x59 | 88 | 0.27 | 0.50 | 0.67 |
| R17x77 | 98 | 0.23 | 0.46 | 0.63 |
| R17x99 | 98 | 0.23 | 0.46 | 0.60 |
| R17x139 | 103 | 0.21 | 0.41 | 0.53 |

WebAssembly takes the pair planes from 44 bits per block: every version from there wins interpreted (R11x59 at 0.93 the closest), and every version under it loses or ties there (R9x77 0.98, R9x99 0.96). AOT-compiled, versions from 33 bits a block win (R9x59 0.91), and the break-even falls between 27 and 33 (R7x99 0.99, R7x139 0.87, R11x27 1.08). A fit of the AOT-compiled times, 30 ns + 17 ns a block + 1.3 ns a block-row, puts most of a small symbol's cost in the per-block work: the four pairs' reversal and store, and their runs.

A `NoInlining` helper for the dispatch's new branch, measured against the branch inline on the interpreter: R17x139 2.30-2.35 against 2.17-2.23 µs, R13x77 1.27-1.30 against 1.13-1.16, the R17x139 matrix decode level (11.1 µs medians both).

### End to end

Against the build before phase 5 (HEAD `2820b5d`, the same harness), ratio of medians (base range → new range, µs):

| Shape | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| matrix/qr-v1-num-L | 0.68 (0.5-0.5 → 0.3-0.3) | 0.78 (0.9-1.0 → 0.7-0.8) | 0.87 (3.7-3.9 → 3.3-3.4) |
| matrix/qr-v6-url-M | 0.35 (2.8-5.4 → 1.1-1.1) | 0.54 (5.4-5.7 → 2.9-4.2) | 0.58 (16.4-27.7 → 9.7-9.7) |
| matrix/qr-v40-byte-L | 0.21 (93.4-116.2 → 19.0-19.3) | 0.25 (134.0-137.1 → 33.9-34.1) | 0.33 (389.4-395.2 → 128.5-129.5) |
| matrix/qr-v40-byte-H | 0.22 (88.1-88.5 → 19.5-19.7) | 0.32 (130.7-174.8 → 45.8-66.1) | 0.34 (378.8-388.0 → 129.4-132.1) |
| matrix/micro-m2-num | 0.92 (0.2-0.2 → 0.2-0.2) | 0.89 (0.3-0.5 → 0.3-0.3) | 0.96 (1.5-2.5 → 1.5-1.5) |
| matrix/micro-m4-byte | 0.65 (0.5-0.5 → 0.3-0.3) | 0.67 (0.7-0.7 → 0.5-0.5) | 0.82 (3.1-3.1 → 2.5-2.5) |
| matrix/rmqr-r7x43-num | 0.74 (0.3-0.3 → 0.2-0.2) | 0.77 (0.4-0.4 → 0.3-0.3) | 0.93 (2.2-4.4 → 2.0-3.7) |
| matrix/rmqr-r17x139-byte | 0.24 (4.3-4.3 → 1.0-1.1) | 0.28 (6.2-6.3 → 1.8-1.8) | 0.49 (21.8-35.0 → 10.8-11.4) |
| matrix/rmqr-r17x139-byte-corrected | 0.34 (7.9-8.0 → 2.7-2.7) | 0.41 (11.8-12.0 → 4.7-4.8) | 0.52 (38.0-44.4 → 19.3-40.5) |
| matrix/sa-byte-45k-set | 0.21 (1,558-1,594 → 328.9-330.9) | 0.31 (2,313-2,315 → 703.7-727.3) | 0.35 (6,544-6,867 → 2,220-2,305) |
| encode/qr-v1-num-L | unchanged | 0.97 (2.6-2.6 → 2.5-2.6) | 0.99 (13.6-13.7 → 13.5-13.6) |
| encode/qr-v1-alnum-M | unchanged | 0.97 (2.9-3.0 → 2.8-2.9) | 0.97 (14.4-14.8 → 14.2-14.2) |
| encode/qr-v6-url-M | unchanged | 0.86 (11.1-17.1 → 10.3-10.7) | 1.00 (40.2-48.3 → 38.5-95.7) |
| encode/qr-v20-byte-M | unchanged | 0.93 (107.1-110.5 → 99.2-101.0) | 0.92 (472.2-518.4 → 424.3-460.6) |
| encode/qr-v40-byte-L | unchanged | 0.89 (363.2-368.2 → 324.2-335.0) | 0.77 (1,291-1,347 → 978.5-1,028) |
| encode/qr-v40-byte-H | unchanged | 0.95 (330.7-343.0 → 316.4-326.4) | 0.91 (1,050-1,087 → 954.6-966.3) |
| encode/micro-m2-num | unchanged | 1.03 (0.4-0.4 → 0.4-0.4) | 1.00 (2.3-2.4 → 2.3-2.4) |
| encode/micro-m3-alnum | unchanged | 1.05 (0.5-0.8 → 0.5-1.0) | 0.99 (2.9-2.9 → 2.9-3.0) |
| encode/micro-m4-byte | unchanged | 0.83 (0.6-0.6 → 0.5-0.5) | 0.95 (3.4-5.3 → 3.3-4.7) |
| encode/rmqr-r7x43-num | unchanged | 1.00 (0.4-0.4 → 0.4-0.4) | 0.99 (2.4-2.4 → 2.4-2.4) |
| encode/rmqr-r11x59-alnum | unchanged | 0.72 (0.8-0.8 → 0.6-0.6) | 0.72 (4.7-4.9 → 3.4-3.4) |
| encode/rmqr-r17x139-byte | unchanged | 0.58 (3.1-3.2 → 1.8-1.9) | 0.53 (17.9-18.0 → 9.5-9.7) |
| encode/sa-byte-45k-single | unchanged | 0.77 (2,838-2,875 → 2,188-2,217) | 0.87 (20,223-21,500 → 16,578-17,910) |
| image/qr-v40-3px | 0.64 (205.9-210.7 → 134.6-146.6) | 0.60 (251.3-428.2 → 149.6-629.6) | 0.80 (1,345-1,360 → 1,077-1,105) |
| image/micro-m4-8px | 0.98 (6.3-6.4 → 6.2-6.4) | 0.95 (7.6-8.0 → 7.5-7.6) | 1.00 (43.3-43.5 → 41.6-47.7) |
| image/rmqr-r17x139-8px | 0.90 (34.3-34.6 → 30.9-31.0) | 0.85 (35.2-38.1 → 30.8-31.2) | 0.95 (212.1-224.5 → 204.5-210.1) |

Default NativeAOT is the run before the WebAssembly gates moved, which left its code as it was; its encodes, whose code did not change, read 0.97 to 1.05 there. The Micro QR M2 and M3 encodes, under the encoder's gate, read 1.03 and 1.05 on WebAssembly AOT in this run and 1.00 and 1.01 over seven alternations.

## Phase 6: rMQR value writers

Each shape alone in its own process, three runs: the range of the three, ratios of their medians.

### Shares

The writer alone (its harness shape, the call included) over the encode of the same content, µs. Default NativeAOT runs its SSE4.1 tier, WebAssembly the SWAR and table loops:

| Writer, content | NativeAOT default | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|---|
| Numeric, 12 digits (R7x43) | 0.007 / 0.177-0.188 (3.9 %) | 0.016 / 0.369-0.375 (4.3 %) | 0.060-0.062 / 2.435-2.455 (2.5 %) |
| Alphanumeric, 43 chars (R11x59) | 0.014-0.015 / 0.339-0.350 (4.1 %) | 0.040-0.041 / 0.599-0.612 (6.8 %) | 0.200-0.202 / 3.472-3.521 (5.7 %) |
| Numeric, 361 digits (version fitted) | 0.079-0.080 / 1.283-1.318 (6.1 %) | 0.103-0.105 / 1.948-1.958 (5.3 %) | 0.473-0.478 / 10.57-10.84 (4.5 %) |
| Alphanumeric, 120 chars (version fitted) | 0.030-0.031 / 0.791-0.865 (3.8 %) | 0.091-0.094 / 1.353-1.397 (6.6 %) | 0.480-0.496 / 7.034-7.237 (6.7 %) |

### The steps alone

µs, the SWAR or table loop → the WebAssembly step:

| Step, content | WebAssembly AOT | WebAssembly interpreted |
|---|---|---|
| Alphanumeric, 16 chars a step, 43 chars | 0.04 → 0.03 (0.78-0.80) | 0.20-0.21 → 0.17-0.18 (0.84-0.86) |
| Alphanumeric, 120 chars | 0.09-0.10 → 0.06 (0.62-0.64) | 0.47-0.50 → 0.36-0.38 (0.76-0.78) |
| Numeric, 24 digits a step, 361 digits | 0.10 → 0.09-0.10 (0.92) | 0.46-0.49 → 0.59-0.60 (1.26) |

The most the Alphanumeric step can take off an encode, its share times its saving: 2.5 % (120 chars AOT-compiled), 1.6 % (interpreted), 1.5 % and 0.9 % on the 43-character symbol.

### End to end, three forms of the Alphanumeric step

Against the build before phase 6 (HEAD `f67beb5`, the same harness), ratio of medians, WebAssembly AOT | interpreted:

| Encode | Inlined into the writer | Out of line, state returned | Its own encode method |
|---|---|---|---|
| Numeric, R7x43 | 0.97 \| 1.04 | 1.02 \| 0.99 | 1.01 \| 1.00 |
| Alphanumeric, R11x59 | 1.04 \| 0.98 | 1.01 \| 1.05 | 1.01 \| 0.98 |
| Byte, R17x139 | 1.04 \| 0.98 | 1.04 \| 0.97 | 0.98 \| 0.99 |
| Numeric, 361 digits | 1.07 \| 0.97 | 1.02 \| 0.99 | 1.02 \| 0.99 |
| Alphanumeric, 120 chars | 0.94 \| 1.04 | 1.00 \| 1.02 | 0.99 \| 0.97 |

Inlined, the Numeric encode on WebAssembly AOT read 1.926-1.948 µs before and 2.005-2.068 after, with no change to its own code. Out of line, the writer's call cost the 43-character step its gain (1.05 AOT-compiled, 1.25 interpreted alone). As its own method, the 120-character encode read 1.313-1.317 → 1.291-1.306 µs AOT-compiled.
