// Tiny raw-RGBA stdin -> raw ASTC encoder bridge around ARM's astcenc
// (Source/astcenc.h public API). Same "raw bytes over stdio" shape as
// ../etcpak/etcpak_encode.cpp so this integrates with the existing subprocess
// pattern in BundleDoctor without inventing a second protocol style; see
// ../../AstcEncoder.cs for the managed side.
//
// Input:  20-byte little-endian header
//             uint32 width
//             uint32 height
//             uint32 block_x   (ASTC footprint width,  e.g. 4/6/8)
//             uint32 block_y   (ASTC footprint height, e.g. 4/6/8)
//             float32 quality  (astcenc search-effort 0..100; see
//                               ASTCENC_PRE_* presets in astcenc.h)
//         followed by width*height*4 RGBA8 bytes (tightly packed, no row
//         padding). Unlike etcpak, no pre-padding to a block-aligned grid is
//         needed here: astcenc_compress_image already clamps its per-block
//         texel reads to (dim_x, dim_y) internally (see get_block_count_safe
//         / the end_x/end_y clamps in astcenc_entry.cpp), so partial edge
//         blocks are handled by the library itself.
// Output: raw 16-byte ASTC blocks, block-row-major - ceil(width/block_x) *
//         ceil(height/block_y) of them. This is the same per-block physical
//         layout Unity/KTX containers expect, and what AstcSharp's
//         AstcEncoder.CompressImage already produced, so nothing above
//         AstcEncoder.cs (TextureCodec.cs, Program.cs) has to change.
//
// Color profile: encodes with ASTCENC_PRF_LDR (linear), not
// ASTCENC_PRF_LDR_SRGB. The profile only affects astcenc's internal
// perceptual error weighting during endpoint search, not the raw bytes
// stored in - or decoded back out of - a block, so this does not change
// decode correctness; it just means block *selection* isn't sRGB-weighted.
// AstcSharp's own encoder (the thing this replaces) does not expose or
// document a profile choice either, so PRF_LDR is the closest
// "no extra assumptions" match. Flip to ASTCENC_PRF_LDR_SRGB below if a
// texture-quality comparison ever shows it helps.
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <thread>
#include <vector>
#include <algorithm>
#include <limits>

#include "astcenc.h"

namespace {

bool ReadExact(void* ptr, size_t size)
{
    return std::fread(ptr, 1, size, stdin) == size;
}

bool WriteExact(const void* ptr, size_t size)
{
    return std::fwrite(ptr, 1, size, stdout) == size;
}

uint32_t ReadU32LE(const uint8_t* p)
{
    return static_cast<uint32_t>(p[0]) |
           (static_cast<uint32_t>(p[1]) << 8) |
           (static_cast<uint32_t>(p[2]) << 16) |
           (static_cast<uint32_t>(p[3]) << 24);
}

float ReadF32LE(const uint8_t* p)
{
    uint32_t bits = ReadU32LE(p);
    float value;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

size_t BlockCountAlong(uint32_t dim, uint32_t block)
{
    return (static_cast<size_t>(dim) + block - 1) / block;
}

} // namespace

int main()
{
    uint8_t header[20];
    if (!ReadExact(header, sizeof(header)))
    {
        std::fprintf(stderr, "astcenc_encode: truncated header\n");
        return 2;
    }

    const uint32_t width   = ReadU32LE(header + 0);
    const uint32_t height  = ReadU32LE(header + 4);
    const uint32_t blockX  = ReadU32LE(header + 8);
    const uint32_t blockY  = ReadU32LE(header + 12);
    const float    quality = ReadF32LE(header + 16);

    if (width == 0 || height == 0)
    {
        std::fprintf(stderr, "astcenc_encode: invalid dimensions %ux%u\n", width, height);
        return 2;
    }
    if (blockX < 4 || blockX > 12 || blockY < 4 || blockY > 12)
    {
        std::fprintf(stderr, "astcenc_encode: unsupported footprint %ux%u\n", blockX, blockY);
        return 2;
    }
    if (!(quality >= ASTCENC_PRE_FASTEST && quality <= ASTCENC_PRE_EXHAUSTIVE))
    {
        std::fprintf(stderr, "astcenc_encode: quality %f out of range [0,100]\n", static_cast<double>(quality));
        return 2;
    }

    const uint64_t pixelCount64 = static_cast<uint64_t>(width) * height;
    if (pixelCount64 > (std::numeric_limits<size_t>::max() / 4))
    {
        std::fprintf(stderr, "astcenc_encode: image is too large\n");
        return 2;
    }
    const size_t pixelCount = static_cast<size_t>(pixelCount64);

    std::vector<uint8_t> rgba(pixelCount * 4);
    if (!ReadExact(rgba.data(), rgba.size()))
    {
        std::fprintf(stderr, "astcenc_encode: truncated RGBA payload\n");
        return 2;
    }

    astcenc_config config{};
    astcenc_error status = astcenc_config_init(
        ASTCENC_PRF_LDR, blockX, blockY, 1, quality, 0, &config);
    if (status != ASTCENC_SUCCESS)
    {
        std::fprintf(stderr, "astcenc_encode: config_init failed: %s\n", astcenc_get_error_string(status));
        return 3;
    }

    const unsigned int threadCount = std::max(1u, std::thread::hardware_concurrency());

    astcenc_context* context = nullptr;
    status = astcenc_context_alloc(&config, threadCount, &context, nullptr);
    if (status != ASTCENC_SUCCESS)
    {
        std::fprintf(stderr, "astcenc_encode: context_alloc failed: %s\n", astcenc_get_error_string(status));
        return 3;
    }

    void* slices[1] = { rgba.data() };
    astcenc_image image{};
    image.dim_x = width;
    image.dim_y = height;
    image.dim_z = 1;
    image.data_type = ASTCENC_TYPE_U8;
    image.data = slices;

    // Identity swizzle: our wire format is already RGBA, so no channel
    // reordering is needed going into the compressor (unlike the ETC2 path,
    // which has to fix up etcpak's BGRA-order source buffer expectation).
    astcenc_swizzle swizzle{ ASTCENC_SWZ_R, ASTCENC_SWZ_G, ASTCENC_SWZ_B, ASTCENC_SWZ_A };

    const size_t blocksX = BlockCountAlong(width, blockX);
    const size_t blocksY = BlockCountAlong(height, blockY);
    std::vector<uint8_t> encoded(blocksX * blocksY * 16);

    // astcenc's own multithreading model: every worker calls
    // astcenc_compress_image against the *same* context/image/output with a
    // unique thread_index; the library partitions the block grid across
    // threads internally (see astcenc_entry.cpp), so this is not a data race.
    std::vector<std::thread> workers;
    workers.reserve(threadCount);
    std::vector<astcenc_error> results(threadCount, ASTCENC_SUCCESS);

    for (unsigned int t = 0; t < threadCount; ++t)
    {
        workers.emplace_back([&, t]() {
            results[t] = astcenc_compress_image(
                context, &image, &swizzle, encoded.data(), encoded.size(), t);
        });
    }
    for (auto& w : workers) w.join();

    astcenc_context_free(context);

    for (astcenc_error r : results)
    {
        if (r != ASTCENC_SUCCESS)
        {
            std::fprintf(stderr, "astcenc_encode: compress_image failed: %s\n", astcenc_get_error_string(r));
            return 3;
        }
    }

    if (!WriteExact(encoded.data(), encoded.size()))
    {
        std::fprintf(stderr, "astcenc_encode: failed writing output\n");
        return 4;
    }

    return 0;
}
