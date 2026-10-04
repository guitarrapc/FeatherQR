# The cross-language benchmark's C++ CLIs: zxing-cpp and libzint, built from the pinned sources the image passes in
# (ZXING_SOURCE, with its bundled libzint, and ZINT_SOURCE). The Release build type is left at CMake's defaults, so the
# default build is what a user's release build gets. The image adds an x86-64-v3 build through CMAKE_C(XX)_FLAGS.
cmake_minimum_required(VERSION 3.16)
project(xlang-cpp C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(XLANG_BUILD "default" CACHE STRING "How the build was compiled, as the protocol's build member reports it")

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

add_executable(zxingcpp-cli zxingcpp.cpp)
target_link_libraries(zxingcpp-cli PRIVATE ZXing::ZXing)
# QRCode::Decode, the matrix decoder, is declared in an internal header.
target_include_directories(zxingcpp-cli PRIVATE ${ZXING_SOURCE}/core/src)
target_compile_definitions(zxingcpp-cli PRIVATE XLANG_BUILD="${XLANG_BUILD}")

add_executable(zint-cli zint.cpp)
target_link_libraries(zint-cli PRIVATE zint-static)
target_compile_definitions(zint-cli PRIVATE XLANG_BUILD="${XLANG_BUILD}")
