// quirc's CLI for the cross-language benchmark: Standard QR decode, from 8-bit grey pixels or from a bare module matrix. rqrr-cli runs
// rqrr, its Rust port, through the same two calls. quirc reads Standard QR only, so every other symbology and encode report unsupported.
//
// - decode-image: the frame loop of quirc's README, on one struct quirc sized to the image before timing: quirc_begin, a copy of the
//   pixels into quirc's own buffer (its API takes no caller buffer), quirc_end, then quirc_extract and quirc_decode on each code found
//   until one decodes.
// - decode-matrix: quirc_decode over a quirc_code whose cell bitmap holds the bare symbol, which is what quirc_extract fills in.

#include "protocol.hpp"

#include <cstring>
#include <memory>

#include "quirc.h"

using xlang::Args;
using xlang::Operation;

namespace {

struct QuircDeleter
{
    void operator()(quirc* q) const { quirc_destroy(q); }
};

std::optional<std::string> Decode(const quirc_code& code)
{
    quirc_data data;
    if (quirc_decode(&code, &data) != QUIRC_SUCCESS)
        return std::nullopt;
    return std::string(reinterpret_cast<const char*>(data.payload), static_cast<size_t>(data.payload_len));
}

std::optional<std::string> DecodeImage(quirc* q, const xlang::Image& image)
{
    std::memcpy(quirc_begin(q, nullptr, nullptr), image.pixels.data(), image.pixels.size());
    quirc_end(q);
    const int count = quirc_count(q);
    for (int i = 0; i < count; ++i) {
        quirc_code code;
        quirc_extract(q, i, &code);
        if (auto text = Decode(code))
            return text;
    }
    return std::nullopt;
}

std::optional<Operation> Load(const Args& args)
{
    if (args.symbology != "qr")
        return std::nullopt;

    if (args.op == "decode-matrix") {
        const auto image = xlang::ReadPgm(args.input);
        if (image.width != image.height || image.width > QUIRC_MAX_GRID_SIZE)
            throw std::invalid_argument(args.input + " is not a Standard QR symbol quirc can hold");
        auto code = std::make_shared<quirc_code>();
        std::memset(code.get(), 0, sizeof *code);
        code->size = image.width;
        for (int i = 0; i < image.width * image.height; ++i)
            if (image.pixels[static_cast<size_t>(i)] < 128)
                code->cell_bitmap[i >> 3] |= static_cast<uint8_t>(1 << (i & 7));
        return Operation{
            [code] {
                xlang::Escape(code.get());
                const auto text = Decode(*code);
                return text ? xlang::FoldText(*text) : 0;
            },
            [code] {
                const auto text = Decode(*code);
                return text ? xlang::Decoded(*text) : xlang::Failed;
            },
        };
    }

    if (args.op == "decode-image") {
        auto image = std::make_shared<const xlang::Image>(xlang::ReadPgm(args.input));
        auto q = std::shared_ptr<quirc>(quirc_new(), QuircDeleter());
        if (!q || quirc_resize(q.get(), image->width, image->height) < 0)
            throw std::runtime_error("quirc could not allocate for " + args.input);
        return Operation{
            [image, q] {
                xlang::Escape(image->pixels.data());
                const auto text = DecodeImage(q.get(), *image);
                return text ? xlang::FoldText(*text) : 0;
            },
            [image, q] {
                const auto text = DecodeImage(q.get(), *image);
                return text ? xlang::Decoded(*text) : xlang::Failed;
            },
        };
    }

    return std::nullopt;
}

} // namespace

int main(int argc, char** argv)
{
    // quirc_version() still returns 1.0 at the v1.2 tag, so the version comes from quirc's Makefile (CMakeLists.txt).
    return xlang::Run("quirc", QUIRC_LIB_VERSION, argc, argv, Load);
}
