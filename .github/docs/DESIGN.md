# Library Design

FeatherQR aims to be the best QR code library for C#.
We aim to provide encoding and decoding in pure C#, support for NativeAOT and WebAssembly, performance backed by measurements, and an API that feels natural from the first line.

This document records the design principles that guide FeatherQR.
Designs for individual features and implementations are recorded in [specs/](specs/).

## Principles

### Keep dependencies out of the core

The QR code encoding and decoding core uses only the BCL and is separate from external rendering libraries.
The packages enforce this boundary.
A build that adds a dependency to the core fails the package dependency graph check.

- `FeatherQR` implements the QR code core in pure C#, including the algorithms it needs.
- `FeatherQR.SkiaSharp` references SkiaSharp for rendering.

The name reflects this design.
"Lightweight" means no dependencies in the core, no allocations on hot paths, and safe behavior under trimming and NativeAOT.
It makes no claim about assembly size.

### Zero Allocation

We aim for zero allocation on hot paths while maintaining performance.
We avoid unnecessary memory allocations and provide APIs that write results directly into caller-provided buffers.

### Measure performance

We measure performance continuously and plan and implement optimizations based on end-to-end measurements and microbenchmarks.
We use `Span<T>`, `Memory<T>`, `stackalloc` and SIMD, and inspect benchmark disassembly to check the generated CPU instructions and branches.
The available SIMD instruction sets can differ between JIT, AOT and WebAssembly.
We therefore base SIMD implementations on performance measurements.

### API-Driven Development

We design an API that feels natural to users before implementing it, then find a way to combine that usability with performance.
Users can start with one line of code and move gradually to lower-level control as needed.
QR codes have multiple standards, including Standard QR, Micro QR and rMQR.
We study each standard, evaluate its requirements and implement support that conforms to it.
We design the APIs with consistent operations, predictable behavior and ease of use across standards.

```csharp
// DO
QRCodeGenerator.Create(/*....*/);
MicroQRCodeGenerator.Create(/*....*/);
QRCodeDecoder.TryDecode(/*....*/);
MicroQRCodeDecoder.TryDecode(/*....*/);

// AVOID
QRCodeGenerator.CreateQRCode(/*....*/);
MicroQRCodeGenerator.CreateMicroQRCode(/*....*/);
QRCodeDecoder.TryDecodeQRCode(/*....*/);
MicroQRCodeDecoder.TryDecodeMicroQRCode(/*....*/);
```

### Support multiple platforms

We treat NativeAOT and WebAssembly as first-class runtime environments alongside JIT on Linux, macOS and Windows for x64 and ARM64.
We use no reflection or dynamic code generation and continuously verify builds and behavior on NativeAOT and WebAssembly.

## Playground

The Playground demonstrates how to use the library and verifies WebAssembly support.
Its AOT-compiled WebAssembly encodes and decodes in the browser, without a server.

---

# ライブラリデザイン

FeatherQRは、C#における最高のQRコードライブラリを目指します。
純粋なC#によるエンコードとデコード、NativeAOTとWebAssemblyへの対応、計測に裏付けられた性能、そして最初の1行から気持ちよく使えるAPIを追求します。

この文書は、FeatherQRがこのライブラリであり続けるためのデザイン原則を記録するものです。個々の機能や実装の設計については[specs/](specs/)に記録します。

## 原則

### コアに依存を作らない

QRコードのエンコードとデコードのコアはBCLだけで実装し、外部レンダリングと分離します。
この境界はパッケージで強制します。コアに依存が加わるビルドは、パッケージの依存グラフを検査するゲートで止まります。
- `FeatherQR`がQRコードのコア実装であり純粋なC#で、必要なアルゴリズムは自分で実装します。
- `FeatherQR.SkiaSharp`はレンダラーとしてSkiaSharpを参照するパッケージです。

名前の意味もここにあります。軽量とは、コアに依存がないこと、ホットパスで割り当てをしないこと、トリミングとNativeAOTで安全に動くことです。アセンブリの大きさの主張ではありません。

### ゼロアロケーション

ホットパスのゼロアロケーションを追求し、パフォーマンスと最小アロケーションの両立を目指します。
不要なメモリアロケーションを許容せず、呼び出し側が用意した領域へ直接結果を書き込めるAPIを提供します。

### 性能を計測する

常に性能を計測して、最適化は推測ではなくEnd-to-End経路/マイクロベンチマーク経路の測定に基づいて計画、実装します。
`Span<T>`、`Memory<T>`、`stackalloc`、SIMDといった高速化手法だけでなく、ベンチマークによる逆アセンブル結果から、生成されたCPU命令や分岐まで確認して性能を追求します。
SIMDはJIT、AOT、WebAssemblyでそれぞれ利用できる命令セットが異なりえます。そのため、性能計測に基づいて実装します。

### API駆動開発

実装に先立って、使う人にとって自然で気持ちのよいAPIを設計し、その手触りと性能を両立する方法を考えます。
1行で使い始められ、必要に応じて低レベルな制御へ段階的に降りられるAPIを提供します。
QRコードはStandardQR、MicroQR、rMQRなど複数のQRコード規格があり、各規格に適合するように調査、検討、実装します。
ユーザーに提供するAPIは、規格が異なっても一貫した対称性、予測可能性、使いやすさを意識して設計します。

```csharp
// DO
QRCodeGenerator.Create(/*....*/);
MicroQRCodeGenerator.Create(/*....*/);
QRCodeDecoder.TryDecode(/*....*/);
MicroQRCodeDecoder.TryDecode(/*....*/);

// AVOID
QRCodeGenerator.CreateQRCode(/*....*/);
MicroQRCodeGenerator.CreateMicroQRCode(/*....*/);
QRCodeDecoder.TryDecodeQRCode(/*....*/);
MicroQRCodeDecoder.TryDecodeMicroQRCode(/*....*/);
```

### マルチプラットフォーム

Linux/macOS/Windowsに対するJIT(x64/ARM64)に限らず、NativeAOT、WebAssemblyを第一級の実行環境として扱います。
リフレクションや動的コード生成は使わず、NativeAOTとWebAssemblyでのビルドと動作を継続的に検証します。

## プレイグラウンド

Playgroundは、このライブラリの利用例を示すものでWASM対応を担保するものです。
AOTコンパイルされたWebAssemblyが、サーバーに依存せずブラウザ上でエンコードとデコードを実行します。
