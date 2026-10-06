// The cross-language benchmark's protocol for the C++ CLIs: the same arguments, timing loop and JSON as FeatherQR's CLI
// (tools/CrossLanguageBenchmark/dotnet/cli/Program.cs), which is the reference implementation.
// See .github/docs/plans/cross-language-benchmark-plan.md ("Protocol").
#pragma once

#include <algorithm>
#include <cctype>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <exception>
#include <fstream>
#include <functional>
#include <iterator>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>

namespace xlang {

struct Args
{
    std::string mode, op, symbology, input;
    std::optional<std::string> ecc, version;
    std::vector<std::string> options;

    std::optional<std::string> option(const std::string& name) const
    {
        for (size_t i = 0; i + 1 < options.size(); ++i)
            if (options[i] == name)
                return options[i + 1];
        return std::nullopt;
    }
};

// One case made into a call. The input is read and converted before any timing, so the timed call does only QR work.
// call is the timed unit of work, and its value is folded into the checksum, so the work cannot be dropped. It is never 0 for a call
// that succeeds and always 0 for one that fails, so the loop counts the calls that failed.
// describe makes one call and returns its result as protocol JSON members: the status, then the decoded text or the encoded matrix.
struct Operation
{
    std::function<uint64_t()> call;
    std::function<std::string()> describe;
};

inline const std::string Failed = "\"status\":\"failed\"";

// Tells the compiler the pointed-to input may be read or changed here, so a call cannot be hoisted out of the loop.
template <class T>
inline void Escape(const T* p)
{
    asm volatile("" : : "g"(p) : "memory");
}

// The fold for a decode: the text's length and last byte, so it depends on the content.
inline uint64_t FoldText(const std::string& text)
{
    return text.empty() ? 0 : text.size() * 31 + static_cast<unsigned char>(text.back());
}

inline std::string Decoded(const std::string& text)
{
    static const char* digits = "0123456789ABCDEF";
    std::string json = "\"status\":\"ok\",\"text\":\"";
    for (unsigned char c : text) {
        json += digits[c >> 4];
        json += digits[c & 15];
    }
    return json + "\"";
}

// Rows top to bottom, 1 for a dark module, with whatever quiet zone the library returns.
template <class IsDark>
std::string Matrix(int width, int height, IsDark isDark)
{
    std::string json = "\"status\":\"ok\",\"matrix\":{\"width\":" + std::to_string(width) + ",\"height\":" + std::to_string(height) + ",\"rows\":[";
    for (int row = 0; row < height; ++row) {
        json += row == 0 ? "\"" : ",\"";
        for (int col = 0; col < width; ++col)
            json += isDark(row, col) ? '1' : '0';
        json += '"';
    }
    return json + "]}";
}

// The payload's exact bytes.
inline std::string ReadText(const std::string& path)
{
    std::ifstream file(path, std::ios::binary);
    if (!file)
        throw std::runtime_error(path + ": cannot open");
    return {std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>()};
}

struct Image
{
    std::vector<uint8_t> pixels;
    int width = 0, height = 0;
};

// Binary PGM (P5, maxval 255): the corpus's one image format.
inline Image ReadPgm(const std::string& path)
{
    const std::string bytes = ReadText(path);
    size_t position = 0;
    auto token = [&]() {
        while (position < bytes.size()) {
            if (bytes[position] == '#') {
                while (position < bytes.size() && bytes[position] != '\n')
                    ++position;
            } else if (bytes[position] == ' ' || bytes[position] == '\t' || bytes[position] == '\n' || bytes[position] == '\r') {
                ++position;
            } else {
                break;
            }
        }
        const size_t start = position;
        while (position < bytes.size() && !std::isspace(static_cast<unsigned char>(bytes[position])))
            ++position;
        return bytes.substr(start, position - start);
    };
    if (token() != "P5")
        throw std::runtime_error(path + " is not a binary PGM (P5)");
    Image image;
    image.width = std::stoi(token());
    image.height = std::stoi(token());
    if (token() != "255")
        throw std::runtime_error(path + ": the corpus uses maxval 255");
    // Exactly one whitespace byte separates the header from the raster.
    ++position;
    if (bytes.size() - position != static_cast<size_t>(image.width) * image.height)
        throw std::runtime_error(path + ": raster size does not match the header");
    image.pixels.assign(bytes.begin() + position, bytes.end());
    return image;
}

inline int Usage()
{
    std::fputs("usage: <cli> <run|fixed|cold|noop> <encode|decode-matrix|decode-image> <qr|microqr|rmqr> <input> [--ecc E] [--version V] "
               "[--warmup-ms 3000 --batch-ms 20 --batches 30 | --iterations N]\n",
               stderr);
    return 2;
}

// Runs the protocol for one library. load returns no operation for one the library does not offer, and throws on a bad argument.
inline int Run(const std::string& library, const std::string& libraryVersion, int argc, char** argv,
               const std::function<std::optional<Operation>(const Args&)>& load)
{
    if (argc < 5)
        return Usage();
    Args args{argv[1], argv[2], argv[3], argv[4], std::nullopt, std::nullopt, {argv + 5, argv + argc}};
    args.ecc = args.option("--ecc");
    args.version = args.option("--version");

    std::string json = "{\"protocol\":1,\"library\":\"" + library + "\",\"libraryVersion\":\"" + libraryVersion + "\",\"runtime\":\"g++ " __VERSION__
                       "\",\"build\":\"" XLANG_BUILD "\",\"mode\":\"" + args.mode + "\"";

    std::optional<Operation> loaded;
    try {
        loaded = load(args);
    } catch (const std::exception& e) {
        std::fprintf(stderr, "%s\n", e.what());
        return 2;
    }
    if (!loaded) {
        std::printf("%s,\"status\":\"unsupported\"}\n", json.c_str());
        return 0;
    }
    auto& operation = *loaded;

    // Verification checks one call. A library can still fail the calls after it, for example by changing its input, so every timed call
    // that fails is counted, and the collector rejects a process with any.
    uint64_t sink = 0, failed = 0;
    bool timed = false;
    if (args.mode == "run") {
        const auto described = operation.describe();
        json += "," + described;
        // An input the library cannot handle is reported, not timed: a failing call costs what failing costs.
        timed = described != Failed;
    }
    if (args.mode == "run" && !timed) {
        // Reported above.
    } else if (args.mode == "run") {
        const double warmupMs = std::stod(args.option("--warmup-ms").value_or("3000"));
        const double batchMs = std::stod(args.option("--batch-ms").value_or("20"));
        const int batches = std::stoi(args.option("--batches").value_or("30"));
        auto& call = operation.call;

        // Warm up for the stated time and at least 3 calls. The batch size comes from the warmup's second half.
        using Clock = std::chrono::steady_clock;
        const auto warmup = std::chrono::duration_cast<Clock::duration>(std::chrono::duration<double, std::milli>(warmupMs));
        uint64_t calls = 0, halfCalls = 0;
        Clock::duration halfElapsed{}, elapsed{};
        const auto start = Clock::now();
        do {
            const uint64_t value = call();
            sink += value;
            failed += value == 0;
            ++calls;
            elapsed = Clock::now() - start;
            if (halfCalls == 0 && elapsed >= warmup / 2) {
                halfCalls = calls;
                halfElapsed = elapsed;
            }
        } while (calls < 3 || elapsed < warmup);
        const double perCall = calls > halfCalls ? std::chrono::duration<double>(elapsed - halfElapsed).count() / (calls - halfCalls)
                                                 : std::chrono::duration<double>(elapsed).count() / calls;
        const uint64_t batchCalls = std::max<uint64_t>(1, static_cast<uint64_t>(batchMs / 1000 / perCall));

        std::vector<int64_t> samples(batches);
        for (auto& sample : samples) {
            const auto t0 = Clock::now();
            for (uint64_t k = 0; k < batchCalls; ++k) {
                const uint64_t value = call();
                sink += value;
                failed += value == 0;
            }
            sample = std::chrono::duration_cast<std::chrono::nanoseconds>(Clock::now() - t0).count();
        }

        json += ",\"warmupCalls\":" + std::to_string(calls) + ",\"warmupNs\":" + std::to_string(std::chrono::duration_cast<std::chrono::nanoseconds>(elapsed).count())
                + ",\"batchCalls\":" + std::to_string(batchCalls) + ",\"batchNs\":[";
        for (size_t b = 0; b < samples.size(); ++b)
            json += (b == 0 ? "" : ",") + std::to_string(samples[b]);
        json += "],\"failedCalls\":" + std::to_string(failed);
    } else if (args.mode == "fixed") {
        const auto option = args.option("--iterations");
        if (!option)
            return Usage();
        const uint64_t iterations = std::stoull(*option);
        auto& call = operation.call;
        for (uint64_t k = 0; k < iterations; ++k) {
            const uint64_t value = call();
            sink += value;
            failed += value == 0;
        }
        json += ",\"status\":\"ok\",\"iterations\":" + std::to_string(iterations) + ",\"failedCalls\":" + std::to_string(failed);
    } else if (args.mode == "cold") {
        json += "," + operation.describe();
    } else if (args.mode == "noop") {
        json += ",\"status\":\"ok\"";
    } else {
        return Usage();
    }

    std::printf("%s,\"checksum\":\"%llu\"}\n", json.c_str(), static_cast<unsigned long long>(sink));
    return 0;
}

} // namespace xlang
