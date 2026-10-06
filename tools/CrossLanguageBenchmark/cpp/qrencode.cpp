// libqrencode's CLI for the cross-language benchmark: Standard QR and Micro QR encode, pinned to the case's level and version, against
// the libqrencode Ubuntu ships. libqrencode writes no rMQR and does not decode, so the rest report unsupported.
//
// The timed call is the whole life of a symbol, as a caller writes it: QRcode_encodeString, or QRcode_encodeStringMQR for Micro QR, from
// the text, then QRcode_free. The text is case-sensitive with QR_MODE_8 as the hint, so libqrencode splits it into numeric, alphanumeric
// and 8-bit segments by its own rules and writes the 8-bit ones as the UTF-8 bytes with no ECI. It takes the version as a minimum and
// grows it when the text does not fit, which verification would reject.

#include "protocol.hpp"

#include <memory>

#include <qrencode.h>

using xlang::Args;
using xlang::Operation;

namespace {

struct Settings
{
    bool micro;
    int version;
    QRecLevel level;
};

struct CodeDeleter
{
    void operator()(QRcode* code) const { QRcode_free(code); }
};
using Code = std::unique_ptr<QRcode, CodeDeleter>;

Code Encode(const std::string& text, const Settings& settings)
{
    return Code(settings.micro ? QRcode_encodeStringMQR(text.c_str(), settings.version, settings.level, QR_MODE_8, 1)
                               : QRcode_encodeString(text.c_str(), settings.version, settings.level, QR_MODE_8, 1));
}

// Bit 0 of each module's byte is 1 for a dark module (qrencode.h).
bool IsDark(const QRcode& code, int row, int col)
{
    return code.data[static_cast<size_t>(row) * code.width + col] & 1;
}

std::optional<Operation> Load(const Args& args)
{
    if (args.op != "encode" || (args.symbology != "qr" && args.symbology != "microqr"))
        return std::nullopt;
    if (!args.ecc || !args.version)
        throw std::invalid_argument("encode needs --ecc and --version");

    static const std::string levels = "LMQH";
    const auto level = levels.find(*args.ecc);
    if (args.ecc->size() != 1 || level == std::string::npos)
        throw std::invalid_argument("--ecc must be L, M, Q or H, got " + *args.ecc);
    const bool micro = args.symbology == "microqr";
    if (micro && (args.version->size() != 2 || (*args.version)[0] != 'M'))
        throw std::invalid_argument("a Micro QR version is M1 to M4, got " + *args.version);
    const Settings settings{micro, micro ? (*args.version)[1] - '0' : std::stoi(*args.version), static_cast<QRecLevel>(QR_ECLEVEL_L + level)};

    auto text = std::make_shared<const std::string>(xlang::ReadText(args.input));
    return Operation{
        [text, settings]() -> uint64_t {
            xlang::Escape(text->data());
            const auto code = Encode(*text, settings);
            // The symbol's width and its centre module, so the fold depends on the content.
            return code ? static_cast<uint64_t>(code->width) * 2 + IsDark(*code, code->width / 2, code->width / 2) : 0;
        },
        [text, settings]() -> std::string {
            const auto code = Encode(*text, settings);
            if (!code)
                return xlang::Failed;
            return xlang::Matrix(code->width, code->width, [&](int row, int col) { return IsDark(*code, row, col); });
        },
    };
}

} // namespace

int main(int argc, char** argv)
{
    return xlang::Run("libqrencode", QRcode_APIVersionString(), argc, argv, Load);
}
