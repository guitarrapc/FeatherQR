// zbar's CLI for the cross-language benchmark: Standard QR decode from 8-bit grey pixels, against the libzbar Ubuntu ships. zbar reads
// neither Micro QR nor rMQR, and has no public decoder for a bare module matrix, so the rest report unsupported.
//
// The image scanner is made once, with every symbology off but QR Code and every other option at its default, and reused, as a caller
// scanning frames reuses it. Each call wraps the pixels in a zbar image without a copy (Y800, zbar's grey format), scans it, takes the
// first symbol's text and destroys the image. zbar turns a symbol's bytes into UTF-8 text by its own guess at their character set.

#include "protocol.hpp"

#include <memory>

#include <zbar.h>

// Compiled as C++, zbar.h declares its C API inside namespace zbar.
using namespace zbar;
using xlang::Args;
using xlang::Operation;

namespace {

struct ScannerDeleter
{
    void operator()(zbar_image_scanner_t* scanner) const { zbar_image_scanner_destroy(scanner); }
};

std::optional<std::string> Scan(zbar_image_scanner_t* scanner, const xlang::Image& image)
{
    zbar_image_t* wrapped = zbar_image_create();
    zbar_image_set_format(wrapped, zbar_fourcc('Y', '8', '0', '0'));
    zbar_image_set_size(wrapped, static_cast<unsigned>(image.width), static_cast<unsigned>(image.height));
    zbar_image_set_data(wrapped, image.pixels.data(), image.pixels.size(), nullptr);
    std::optional<std::string> text;
    if (zbar_scan_image(scanner, wrapped) > 0) {
        const zbar_symbol_t* symbol = zbar_image_first_symbol(wrapped);
        text.emplace(zbar_symbol_get_data(symbol), zbar_symbol_get_data_length(symbol));
    }
    zbar_image_destroy(wrapped);
    return text;
}

std::optional<Operation> Load(const Args& args)
{
    if (args.op != "decode-image" || args.symbology != "qr")
        return std::nullopt;

    auto image = std::make_shared<const xlang::Image>(xlang::ReadPgm(args.input));
    auto scanner = std::shared_ptr<zbar_image_scanner_t>(zbar_image_scanner_create(), ScannerDeleter());
    zbar_image_scanner_set_config(scanner.get(), ZBAR_NONE, ZBAR_CFG_ENABLE, 0);
    zbar_image_scanner_set_config(scanner.get(), ZBAR_QRCODE, ZBAR_CFG_ENABLE, 1);
    return Operation{
        [image, scanner] {
            xlang::Escape(image->pixels.data());
            const auto text = Scan(scanner.get(), *image);
            return text ? xlang::FoldText(*text) : 0;
        },
        [image, scanner] {
            const auto text = Scan(scanner.get(), *image);
            return text ? xlang::Decoded(*text) : xlang::Failed;
        },
    };
}

} // namespace

int main(int argc, char** argv)
{
    unsigned major = 0, minor = 0, patch = 0;
    zbar_version(&major, &minor, &patch);
    const std::string version = std::to_string(major) + "." + std::to_string(minor) + "." + std::to_string(patch);
    return xlang::Run("zbar", version, argc, argv, Load);
}
