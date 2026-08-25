cmake_minimum_required(VERSION 3.10)
project(ZTranscoderEtcpakEncode LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

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
        target_compile_options(etcpak_encode PRIVATE -msse4.1)
    endif()
    target_link_libraries(etcpak_encode PRIVATE pthread)
endif()

