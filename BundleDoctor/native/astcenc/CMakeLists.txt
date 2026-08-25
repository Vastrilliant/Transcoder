cmake_minimum_required(VERSION 3.15)
project(astcenc_bundledoctor_wrapper)

set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

# We only need the compressor core library, built for whatever SIMD ISA is
# native to the build machine (upstream/CMakeLists.txt falls back to
# ASTCENC_ISA_NATIVE=ON when no ISA option is set, which is exactly what we
# want here - a single-config non-universal build). ASTCENC_CLI/UNITTEST are
# off because we never build any of astcenc's own CLI or test sources; our
# own astcenc_encode.cpp below is the only executable this produces.
set(ASTCENC_CLI OFF CACHE BOOL "" FORCE)
set(ASTCENC_UNITTEST OFF CACHE BOOL "" FORCE)
set(ASTCENC_SHAREDLIB OFF CACHE BOOL "" FORCE)
set(ASTCENC_DECOMPRESSOR OFF CACHE BOOL "" FORCE)

add_subdirectory(upstream)

# upstream/Source/CMakeLists.txt names the static-lib target
# astc<codec>-<isa>-static; codec is "enc" whenever ASTCENC_DECOMPRESSOR is
# OFF (see upstream/Source/CMakeLists.txt), and isa is "native" per the
# fallback above.
add_executable(astcenc_encode astcenc_encode.cpp)

target_link_libraries(astcenc_encode
    PRIVATE
        astcenc-native-static)

target_include_directories(astcenc_encode
    PRIVATE
        upstream/Source)

if(CMAKE_SYSTEM_NAME STREQUAL "Linux" OR CMAKE_SYSTEM_NAME STREQUAL "Darwin")
    target_link_libraries(astcenc_encode PRIVATE pthread)
endif()
