// libzint's CLI for the cross-language benchmark: Standard QR, Micro QR and rMQR encode, pinned to the case's level and version.
// libzint does not decode, so every decode reports unsupported.
//
// The timed call is the whole life of a symbol, as a caller writes it: ZBarcode_Create, the options, ZBarcode_Encode to the module
// rows, ZBarcode_Delete. Input is UTF-8 (UNICODE_MODE), so libzint inserts an ECI where the text needs one.

#include "protocol.hpp"
#include "zint_options.hpp"

#include <memory>

#include <zint.h>

using xlang::Args;
using xlang::Operation;

namespace {

struct Settings
{
    int symbology, ecc, version;
};

struct SymbolDeleter
{
    void operator()(zint_symbol* symbol) const { ZBarcode_Delete(symbol); }
};
using Symbol = std::unique_ptr<zint_symbol, SymbolDeleter>;

// The modules are bit-packed per row (common.h, z_module_is_set).
bool IsDark(const zint_symbol& symbol, int row, int col)
{
    return (symbol.encoded_data[row][col >> 3] >> (col & 7)) & 1;
}

Symbol Encode(const std::string& text, const Settings& settings)
{
    Symbol symbol(ZBarcode_Create());
    symbol->symbology = settings.symbology;
    symbol->option_1 = settings.ecc;
    symbol->option_2 = settings.version;
    symbol->input_mode = UNICODE_MODE;
    if (ZBarcode_Encode(symbol.get(), reinterpret_cast<const unsigned char*>(text.data()), static_cast<int>(text.size())) >= ZINT_ERROR)
        return nullptr;
    return symbol;
}

std::optional<Operation> Load(const Args& args)
{
    if (args.op != "encode")
        return std::nullopt;
    int symbology;
    if (args.symbology == "qr")
        symbology = BARCODE_QRCODE;
    else if (args.symbology == "microqr")
        symbology = BARCODE_MICROQR;
    else if (args.symbology == "rmqr")
        symbology = BARCODE_RMQR;
    else
        return std::nullopt;
    if (!args.ecc || !args.version)
        throw std::invalid_argument("encode needs --ecc and --version");

    auto text = std::make_shared<const std::string>(xlang::ReadText(args.input));
    const Settings settings{symbology, xlang::ZintEccLevel(*args.ecc), xlang::ZintVersion(args.symbology, *args.version)};
    return Operation{
        [text, settings]() -> uint64_t {
            xlang::Escape(text->data());
            const auto symbol = Encode(*text, settings);
            // The symbol's width and its centre module, so the fold depends on the content.
            return symbol ? static_cast<uint64_t>(symbol->width) * 2 + IsDark(*symbol, symbol->rows / 2, symbol->width / 2) : 0;
        },
        [text, settings]() -> std::string {
            const auto symbol = Encode(*text, settings);
            if (!symbol)
                return xlang::Failed;
            return xlang::Matrix(symbol->width, symbol->rows, [&](int row, int col) { return IsDark(*symbol, row, col); });
        },
    };
}

} // namespace

int main(int argc, char** argv)
{
    const int version = ZBarcode_Version();
    const std::string text = std::to_string(version / 10000) + "." + std::to_string(version / 100 % 100) + "." + std::to_string(version % 100);
    return xlang::Run("libzint", text, argc, argv, Load);
}
