// zxing-cpp's CLI for the cross-language benchmark: Standard QR, Micro QR and rMQR, encode and decode.
//
// - decode-image: ReadBarcode, the library's single-symbol entry point, with the formats restricted to the symbology and every
//   other option at its default.
// - decode-matrix: QRCode::Decode over a BitMatrix of the bare symbol. It is the decoder behind ReadBarcode, but not public API:
//   the public API reads only images. It tells the symbology from the matrix size.
// - encode: CreateBarcodeFromText, which writes through the libzint bundled with zint-cli's commit (encode, a one-pixel-per-module
//   buffer, and the library's Barcode around it), so against zint-cli it is what zxing-cpp adds.

#include "protocol.hpp"
#include "zint_options.hpp"

#include <memory>

#include "Barcode.h"
#include "BitMatrix.h"
#include "CreateBarcode.h"
#include "DecoderResult.h"
#include "ReadBarcode.h"
#include "Version.h"
#include "qrcode/QRDecoder.h"

using namespace ZXing;
using xlang::Args;
using xlang::Operation;

static BarcodeFormat ReadFormat(const std::string& symbology)
{
    if (symbology == "qr")
        return BarcodeFormat::QRCodeModel2;
    if (symbology == "microqr")
        return BarcodeFormat::MicroQRCode;
    return BarcodeFormat::RMQRCode;
}

static BarcodeFormat CreateFormat(const std::string& symbology)
{
    if (symbology == "qr")
        return BarcodeFormat::QRCode;
    if (symbology == "microqr")
        return BarcodeFormat::MicroQRCode;
    return BarcodeFormat::RMQRCode;
}

// The symbol's size and its centre module, so the fold depends on the content.
static uint64_t FoldSymbol(const ImageView& symbol)
{
    if (symbol.width() == 0)
        return 0;
    return static_cast<uint64_t>(symbol.width()) * 2 + (*symbol.data(symbol.width() / 2, symbol.height() / 2) != 0);
}

static std::optional<Operation> Load(const Args& args)
{
    if (args.symbology != "qr" && args.symbology != "microqr" && args.symbology != "rmqr")
        return std::nullopt;

    if (args.op == "encode") {
        if (!args.ecc || !args.version)
            throw std::invalid_argument("encode needs --ecc and --version");
        auto text = std::make_shared<const std::string>(xlang::ReadText(args.input));
        // The level and version in libzint's numbering, which the writer hands to libzint as they are.
        const std::string options = "ecLevel=" + *args.ecc + ",version=" + std::to_string(xlang::ZintVersion(args.symbology, *args.version));
        auto opts = std::make_shared<CreatorOptions>(CreateFormat(args.symbology), options);
        return Operation{
            [text, opts] {
                xlang::Escape(text->data());
                return FoldSymbol(CreateBarcodeFromText(*text, *opts).symbol());
            },
            [text, opts] {
                try {
                    const auto barcode = CreateBarcodeFromText(*text, *opts);
                    const auto symbol = barcode.symbol();
                    // symbol() is a luminance image: a dark module is 0.
                    return xlang::Matrix(symbol.width(), symbol.height(), [&](int row, int col) { return *symbol.data(col, row) < 128; });
                } catch (const std::exception&) {
                    return xlang::Failed;
                }
            },
        };
    }

    if (args.op == "decode-matrix") {
        const auto image = xlang::ReadPgm(args.input);
        auto bits = std::make_shared<BitMatrix>(image.width, image.height);
        for (int y = 0; y < image.height; ++y)
            for (int x = 0; x < image.width; ++x)
                if (image.pixels[static_cast<size_t>(y) * image.width + x] < 128)
                    bits->set(x, y);
        return Operation{
            [bits] {
                xlang::Escape(bits.get());
                const auto result = QRCode::Decode(*bits);
                return result.isValid() ? xlang::FoldText(result.content().utf8()) : 0;
            },
            [bits] {
                const auto result = QRCode::Decode(*bits);
                return result.isValid() ? xlang::Decoded(result.content().utf8()) : xlang::Failed;
            },
        };
    }

    if (args.op == "decode-image") {
        auto image = std::make_shared<const xlang::Image>(xlang::ReadPgm(args.input));
        auto opts = std::make_shared<const ReaderOptions>(ReaderOptions().formats(ReadFormat(args.symbology)));
        return Operation{
            [image, opts] {
                xlang::Escape(image->pixels.data());
                const auto barcode = ReadBarcode({image->pixels.data(), image->width, image->height, ImageFormat::Lum}, *opts);
                return barcode.isValid() ? xlang::FoldText(barcode.text()) : 0;
            },
            [image, opts] {
                const auto barcode = ReadBarcode({image->pixels.data(), image->width, image->height, ImageFormat::Lum}, *opts);
                return barcode.isValid() ? xlang::Decoded(barcode.text()) : xlang::Failed;
            },
        };
    }

    return std::nullopt;
}

int main(int argc, char** argv)
{
    return xlang::Run("zxing-cpp", ZXING_VERSION_STR, argc, argv, Load);
}
