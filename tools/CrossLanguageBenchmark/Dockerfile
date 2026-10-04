# syntax=docker/dockerfile:1
# The cross-language benchmark's image: every CLI, the collector and the corpus, built once from pinned toolchains,
# for linux/amd64 and linux/arm64. See .github/docs/plans/cross-language-benchmark-plan.md.
#
#   docker build -f tools/CrossLanguageBenchmark/Dockerfile --build-arg XLANG_COMMIT=$(git rev-parse HEAD) -t featherqr-xlang .
#   docker run --rm --cpuset-cpus 2,3 --memory 4g -e XLANG_IMAGE=$(docker image inspect -f '{{.Id}}' featherqr-xlang) -v xlang-out:/out featherqr-xlang run
#
# On Docker Desktop for Windows, /out is a named volume rather than a host folder: through the Windows bind mount, a file written
# a moment before was read back as zero bytes, and a copied file arrived with bytes changed. Results leave through standard output:
#   docker run --rm -v xlang-out:/out --entrypoint tar featherqr-xlang cf - -C /out . > results.tar

# Rust: the shipped default target, plus x86-64-v3 on x64. The binaries run on the final stage's newer glibc.
FROM rust:1.99.0-slim-bookworm@sha256:452176c0cefca88c0b3184ce85a4eb03e3d4fa05d2afb5366abcba853221019e AS rust
WORKDIR /src
COPY tools/CrossLanguageBenchmark/rust/ ./
RUN cargo build --release --locked --target-dir /build/default \
    && if [ "$(uname -m)" = x86_64 ]; then RUSTFLAGS="-C target-cpu=x86-64-v3" cargo build --release --locked --target-dir /build/x86-64-v3; fi \
    && for dir in /build/*; do mkdir -p /opt/xlang/rust/$(basename $dir) && cp $dir/release/rqrr-cli $dir/release/fast_qr-cli /opt/xlang/rust/$(basename $dir)/; done

# FeatherQR's CLI under NativeAOT: the default target, plus x86-64-v3 on x64. ILC comes with the SDK, pinned below by digest,
# and clang only links. zlib1g-dev is left to match the base image's zlib1g. The final stage has the same base, so the binaries
# find the same glibc.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS aot
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang=1:18.0-59~exp2 zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json opensource.snk README.md LICENSE ./
COPY src/FeatherQR/ src/FeatherQR/
COPY tools/CrossLanguageBenchmark/dotnet/cli/ tools/CrossLanguageBenchmark/dotnet/cli/
RUN dotnet publish tools/CrossLanguageBenchmark/dotnet/cli -c Release -p:XlangNativeAot=true -o /opt/xlang/featherqr-aot/default \
    && if [ "$(uname -m)" = x86_64 ]; then dotnet publish tools/CrossLanguageBenchmark/dotnet/cli -c Release -p:XlangNativeAot=true -p:IlcInstructionSet=x86-64-v3 -o /opt/xlang/featherqr-aot/x86-64-v3; fi \
    && find /opt/xlang/featherqr-aot -type f ! -name featherqr-cli -delete

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29

RUN apt-get update \
    && apt-get install -y --no-install-recommends hyperfine=1.18.0-2build1 \
    && rm -rf /var/lib/apt/lists/*

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json opensource.snk README.md LICENSE ./
COPY src/FeatherQR/ src/FeatherQR/
COPY tests/FeatherQR.Tests/Shared/NearestNeighbourRenderer.cs tests/FeatherQR.Tests/Shared/
COPY tools/CrossLanguageBenchmark/ tools/CrossLanguageBenchmark/

# The reference project is built here so its packages are restored; BenchmarkDotNet rebuilds it when it runs.
RUN dotnet publish tools/CrossLanguageBenchmark/dotnet/cli -c Release -o /opt/xlang/featherqr-jit \
    && dotnet publish tools/CrossLanguageBenchmark/collector -c Release -o /opt/xlang/collector \
    && dotnet build tools/CrossLanguageBenchmark/dotnet/reference -c Release \
    && dotnet /opt/xlang/collector/collector.dll corpus --out /opt/xlang/corpus \
    && cp tools/CrossLanguageBenchmark/clis.tsv /opt/xlang/clis.tsv

COPY --from=rust /opt/xlang/rust/ /opt/xlang/rust/
COPY --from=aot /opt/xlang/featherqr-aot/ /opt/xlang/featherqr-aot/

# The GC is set explicitly, so neither the container's CPU and memory limits nor the CPU's cache size choose it.
# NativeAOT reads the same settings.
ENV DOTNET_gcServer=0 \
    DOTNET_gcConcurrent=0 \
    DOTNET_GCHeapHardLimit=0x40000000 \
    DOTNET_GCgen0size=0x2000000

ARG XLANG_COMMIT=unknown
ENV XLANG_COMMIT=${XLANG_COMMIT}

WORKDIR /opt/xlang
ENTRYPOINT ["dotnet", "/opt/xlang/collector/collector.dll"]
