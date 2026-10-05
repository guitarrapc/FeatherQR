// The case's pinned level and version in libzint's numbering, which zxing-cpp's writer passes to its bundled libzint unchanged.
#pragma once

#include <stdexcept>
#include <string>

namespace xlang {

// libzint's option_1: L, M, Q, H as 1 to 4. rMQR has only M (2) and H (4).
inline int ZintEccLevel(const std::string& ecc)
{
    const std::string levels = "LMQH";
    const auto index = levels.find(ecc);
    if (ecc.size() != 1 || index == std::string::npos)
        throw std::invalid_argument("--ecc must be L, M, Q or H, got " + ecc);
    return static_cast<int>(index) + 1;
}

// libzint's option_2: the Standard QR version, M1 to M4 as 1 to 4, or an rMQR size by its index in the order libzint lists them (qr.c).
inline int ZintVersion(const std::string& symbology, const std::string& version)
{
    if (symbology == "qr")
        return std::stoi(version);
    if (symbology == "microqr" && version.size() == 2 && version[0] == 'M')
        return version[1] - '0';
    if (symbology == "rmqr") {
        static const char* names[32] = {
            "R7x43",  "R7x59",  "R7x77",  "R7x99",  "R7x139",  "R9x43",  "R9x59",  "R9x77",  "R9x99",  "R9x139", "R11x27",
            "R11x43", "R11x59", "R11x77", "R11x99", "R11x139", "R13x27", "R13x43", "R13x59", "R13x77", "R13x99", "R13x139",
            "R15x43", "R15x59", "R15x77", "R15x99", "R15x139", "R17x43", "R17x59", "R17x77", "R17x99", "R17x139",
        };
        for (int i = 0; i < 32; ++i)
            if (version == names[i])
                return i + 1;
    }
    throw std::invalid_argument("unknown " + symbology + " version " + version);
}

} // namespace xlang
