# The cross-language benchmark's C and C++ CLIs. zxing-cpp, libzint and quirc are built from the pinned sources the image passes in
# (ZXING_SOURCE, with its bundled libzint, ZINT_SOURCE and QUIRC_SOURCE). The Release build type is left at CMake's defaults, so the
# default build is what a user's release build gets, and the image adds an x86-64-v3 build through CMAKE_C(XX)_FLAGS.
# zbar and libqrencode are linked against the libraries Ubuntu ships (libzbar-dev, libqrencode-dev), so they have one build only.
cmake_minimum_required(VERSION 3.16)
project(xlang-cpp C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(XLANG_BUILD "default" CACHE STRING "How the build was compiled, as the protocol's build member reports it")
set(XLANG_ZBAR_BUILD "unknown" CACHE STRING "The Ubuntu package zbar-cli runs against, as its build member reports it")
set(XLANG_QRENCODE_BUILD "unknown" CACHE STRING "The Ubuntu package qrencode-cli runs against, as its build member reports it")

set(ZXING_READERS ON CACHE BOOL "" FORCE)
set(ZXING_WRITERS NEW CACHE STRING "" FORCE)
set(ZXING_C_API OFF CACHE BOOL "" FORCE)
set(ZXING_EXAMPLES OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
add_subdirectory(${ZXING_SOURCE} zxing-cpp EXCLUDE_FROM_ALL)

set(ZINT_SHARED OFF CACHE BOOL "" FORCE)
set(ZINT_STATIC ON CACHE BOOL "" FORCE)
set(ZINT_FRONTEND OFF CACHE BOOL "" FORCE)
set(ZINT_USE_GS1SE OFF CACHE BOOL "" FORCE)
set(ZINT_USE_PNG OFF CACHE BOOL "" FORCE)
set(ZINT_USE_QT OFF CACHE BOOL "" FORCE)
set(ZINT_UNINSTALL OFF CACHE BOOL "" FORCE)
add_subdirectory(${ZINT_SOURCE} zint EXCLUDE_FROM_ALL)

# quirc's library is the four C files its Makefile builds, compiled here so that the build flags are the image's.
add_library(quirc STATIC ${QUIRC_SOURCE}/lib/decode.c ${QUIRC_SOURCE}/lib/identify.c ${QUIRC_SOURCE}/lib/quirc.c ${QUIRC_SOURCE}/lib/version_db.c)
target_include_directories(quirc PUBLIC ${QUIRC_SOURCE}/lib)
target_link_libraries(quirc PUBLIC m)
# quirc_version() still returns 1.0 at the v1.2 tag, while its Makefile's LIB_VERSION follows the release.
file(STRINGS ${QUIRC_SOURCE}/Makefile QUIRC_LIB_VERSION REGEX "^LIB_VERSION = ")
string(REGEX REPLACE "^LIB_VERSION = " "" QUIRC_LIB_VERSION "${QUIRC_LIB_VERSION}")

add_executable(zxingcpp-cli zxingcpp.cpp)
target_link_libraries(zxingcpp-cli PRIVATE ZXing::ZXing)
# QRCode::Decode, the matrix decoder, is declared in an internal header.
target_include_directories(zxingcpp-cli PRIVATE ${ZXING_SOURCE}/core/src)
target_compile_definitions(zxingcpp-cli PRIVATE XLANG_BUILD="${XLANG_BUILD}")

add_executable(zint-cli zint.cpp)
target_link_libraries(zint-cli PRIVATE zint-static)
target_compile_definitions(zint-cli PRIVATE XLANG_BUILD="${XLANG_BUILD}")

add_executable(quirc-cli quirc.cpp)
target_link_libraries(quirc-cli PRIVATE quirc)
target_compile_definitions(quirc-cli PRIVATE XLANG_BUILD="${XLANG_BUILD}" QUIRC_LIB_VERSION="${QUIRC_LIB_VERSION}")

add_executable(zbar-cli EXCLUDE_FROM_ALL zbar.cpp)
target_link_libraries(zbar-cli PRIVATE zbar)
target_compile_definitions(zbar-cli PRIVATE XLANG_BUILD="${XLANG_ZBAR_BUILD}")

add_executable(qrencode-cli EXCLUDE_FROM_ALL qrencode.cpp)
target_link_libraries(qrencode-cli PRIVATE qrencode)
target_compile_definitions(qrencode-cli PRIVATE XLANG_BUILD="${XLANG_QRENCODE_BUILD}")
