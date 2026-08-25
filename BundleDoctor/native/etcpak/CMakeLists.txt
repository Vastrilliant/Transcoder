cmake_minimum_required(VERSION 3.10)
project(BundleDoctorEtcpakEncode LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Only the pieces of wolfpld/etcpak needed for ETC2 RGB8/RGBA8 compression
# are vendored here (ProcessRGB + its Dither/Tables support code) - not the
# full etcpak CLI, which additionally pulls in libpng, Tracy, and BC7/DXT
# encoders this project has no use for.
add_executable(etcpak_encode
    etcpak_encode.cpp
    ProcessRGB.cpp
    Dither.cpp
    Tables.cpp)

target_include_directories(etcpak_encode PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}")

if (MSVC)
    target_compile_options(etcpak_encode PRIVATE /O2 /W3)
else()
    include(CheckCXXCompilerFlag)
    check_cxx_compiler_flag("-msse4.1" HAS_MSSE41)

    target_compile_options(etcpak_encode PRIVATE -O3 -Wall -Wextra -pthread)
    if (HAS_MSSE41 AND NOT CMAKE_SYSTEM_PROCESSOR MATCHES "arm|aarch64")
        # etcpak's ETC2 path has a hand-written SSE4.1 fast path (ProcessRGB.cpp)
        # that it silently falls back from to a plain scalar loop without this -
        # worth ~2-3x throughput on x86_64 build hosts, which is what the CI/
        # proxy machines actually are. ARM builds use etcpak's NEON path instead,
        # picked up automatically via __ARM_NEON with no explicit flag needed.
        target_compile_options(etcpak_encode PRIVATE -msse4.1)
    endif()
    target_link_libraries(etcpak_encode PRIVATE pthread)
endif()
