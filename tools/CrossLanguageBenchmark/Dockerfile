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
    && for dir in /build/*; do mkdir -p /opt/xlang/rust/$(basename $dir) && cp $dir/release/rqrr-cli $dir/release/fast_qr-cli $dir/release/qrcode-cli /opt/xlang/rust/$(basename $dir)/; done

# Go: gozxing and go-qrcode at the versions go.mod pins, with go.sum checking their content. Without cgo the binaries are static, so
# they run on the final stage's base: the default target (GOAMD64=v1 on x64, GOARM64=v8.0 on arm64), plus GOAMD64=v3 on x64.
FROM golang:1.27.1-bookworm@sha256:69a7b9788769bec032d238959b61854e9ae87f57be9029ec04e9885fabf99195 AS go
WORKDIR /src
COPY tools/CrossLanguageBenchmark/go/ ./
RUN CGO_ENABLED=0 go build -o /opt/xlang/go/default/ ./cmd/... \
    && if [ "$(uname -m)" = x86_64 ]; then CGO_ENABLED=0 GOAMD64=v3 go build -o /opt/xlang/go/x86-64-v3/ ./cmd/...; fi

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

# C and C++: zxing-cpp v3.1.0, the commit the ZXingCpp package 0.5.2 ships, and libzint 2.16.0, the commit zxing-cpp bundles as its
# writer, so libzint is built from zxing-cpp's own submodule, and quirc v1.2. Built on the final stage's base, so the binaries find the
# same C++ runtime: the default build, plus x86-64-v3 on x64. The default build is also made as a shared libZXing with the C API, which
# the ZXingCpp package's CLI loads in place of the package's own binary (zxingcpp-net-own), so the two differ only by the wrapper.
# zbar and libqrencode are Ubuntu's packages, which their CLIs link against, so they have the default build only. libzbar-dev pulls in
# ImageMagick and GTK, which stay in this stage: the final stage installs only the two runtime libraries.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS cpp
RUN apt-get update \
    && apt-get install -y --no-install-recommends g++=4:13.2.0-7ubuntu1 cmake=3.28.3-1build7 ninja-build=1.11.1-2 \
        libzbar-dev=0.23.93-4build3 libqrencode-dev=4.1.1-1build2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /deps/quirc
RUN git init -q \
    && git fetch -q --depth 1 https://github.com/dlbeer/quirc.git 542848dd6b9b0eaa9587bbf25b9bc67bd8a71fca \
    && git checkout -q FETCH_HEAD
WORKDIR /deps/zxing-cpp
RUN git init -q \
    && git fetch -q --depth 1 https://github.com/zxing-cpp/zxing-cpp.git 885baaf0840335153c1a487fa65f9c1388702c81 \
    && git checkout -q FETCH_HEAD \
    && git submodule update -q --init --depth 1 zint \
    && test "$(git -C zint rev-parse HEAD)" = 55541e139e62b9209b71cd9b0ba9010cec28b1d9
RUN cmake -S /deps/zxing-cpp -B /build/shared -G Ninja -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON -DZXING_C_API=ON \
        -DZXING_READERS=ON -DZXING_WRITERS=NEW -DZXING_EXAMPLES=OFF > /dev/null \
    && cmake --build /build/shared --target ZXing \
    && mkdir -p /opt/xlang/cpp/shared && cp -L /build/shared/core/libZXing.so /opt/xlang/cpp/shared/libZXing.so
COPY tools/CrossLanguageBenchmark/cpp/ /src/cpp/
RUN for build in default x86-64-v3; do \
        if [ $build = x86-64-v3 ] && [ "$(uname -m)" != x86_64 ]; then continue; fi; \
        flags=""; targets="zxingcpp-cli zint-cli quirc-cli"; \
        if [ $build = x86-64-v3 ]; then flags="-march=x86-64-v3"; else targets="$targets zbar-cli qrencode-cli"; fi; \
        cmake -S /src/cpp -B /build/$build -G Ninja -DCMAKE_BUILD_TYPE=Release "-DCMAKE_C_FLAGS=$flags" "-DCMAKE_CXX_FLAGS=$flags" \
            "-DXLANG_BUILD=${flags:-default}" -DZXING_SOURCE=/deps/zxing-cpp -DZINT_SOURCE=/deps/zxing-cpp/zint -DQUIRC_SOURCE=/deps/quirc \
            "-DXLANG_ZBAR_BUILD=Ubuntu libzbar0t64 $(dpkg-query -W -f='${Version}' libzbar0t64)" \
            "-DXLANG_QRENCODE_BUILD=Ubuntu libqrencode4 $(dpkg-query -W -f='${Version}' libqrencode4)" > /dev/null \
        && cmake --build /build/$build --target $targets \
        && mkdir -p /opt/xlang/cpp/$build && for target in $targets; do cp /build/$build/$target /opt/xlang/cpp/$build/; done \
        || exit 1; \
    done

# JVM: BoofCV and ZXing at the exact versions jvm/pom.xml pins, packed into one jar by Maven 3.10.0 on JDK 25.
# The final stage runs it on the JRE of the same release, copied from its own pinned image.
FROM maven:3-eclipse-temurin-25-noble@sha256:4020041efbd0e5df7a6727b1c60f449bd59936df94153098c4adddfd88cd3e7a AS jvm
WORKDIR /src
COPY tools/CrossLanguageBenchmark/jvm/ ./
RUN mvn -q -B package \
    && mkdir -p /opt/xlang/jvm && cp target/xlang-jvm.jar /opt/xlang/jvm/

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29

# hyperfine, and the zbar and libqrencode libraries the cpp stage's zbar-cli and qrencode-cli link against.
RUN apt-get update \
    && apt-get install -y --no-install-recommends hyperfine=1.18.0-2build1 libzbar0t64=0.23.93-4build3 libqrencode4=4.1.1-1build2 \
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
    && dotnet publish tools/CrossLanguageBenchmark/dotnet/zxingcpp -c Release -o /opt/xlang/zxingcpp-net \
    && dotnet publish tools/CrossLanguageBenchmark/collector -c Release -o /opt/xlang/collector \
    && dotnet build tools/CrossLanguageBenchmark/dotnet/reference -c Release \
    && dotnet /opt/xlang/collector/collector.dll corpus --out /opt/xlang/corpus \
    && cp tools/CrossLanguageBenchmark/clis.tsv /opt/xlang/clis.tsv

COPY --from=rust /opt/xlang/rust/ /opt/xlang/rust/
COPY --from=go /opt/xlang/go/ /opt/xlang/go/
COPY --from=aot /opt/xlang/featherqr-aot/ /opt/xlang/featherqr-aot/
COPY --from=cpp /opt/xlang/cpp/ /opt/xlang/cpp/
COPY --from=eclipse-temurin:25-jre-noble@sha256:d9a39a23634650173f1e2bbc176227af9728587ecf0f4b62d53e9355cd7a19ab /opt/java/openjdk /opt/java/openjdk
COPY --from=jvm /opt/xlang/jvm/ /opt/xlang/jvm/

# The ZXingCpp package's CLI again, with the package's libZXing replaced by the one built above from the same commit.
RUN cp -r /opt/xlang/zxingcpp-net /opt/xlang/zxingcpp-net-own \
    && cp /opt/xlang/cpp/shared/libZXing.so /opt/xlang/zxingcpp-net-own/runtimes/linux-$(dpkg --print-architecture | sed 's/amd64/x64/')/native/libZXing.so

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
