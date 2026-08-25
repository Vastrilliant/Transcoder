cmake_minimum_required(VERSION 3.15)
project(astcenc_ztranscoder_wrapper)

set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

set(ASTCENC_CLI OFF CACHE BOOL "" FORCE)
set(ASTCENC_UNITTEST OFF CACHE BOOL "" FORCE)
set(ASTCENC_SHAREDLIB OFF CACHE BOOL "" FORCE)
set(ASTCENC_DECOMPRESSOR OFF CACHE BOOL "" FORCE)

add_subdirectory(upstream)

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

