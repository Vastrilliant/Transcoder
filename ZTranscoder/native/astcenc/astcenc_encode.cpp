
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

}

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

    astcenc_swizzle swizzle{ ASTCENC_SWZ_R, ASTCENC_SWZ_G, ASTCENC_SWZ_B, ASTCENC_SWZ_A };

    const size_t blocksX = BlockCountAlong(width, blockX);
    const size_t blocksY = BlockCountAlong(height, blockY);
    std::vector<uint8_t> encoded(blocksX * blocksY * 16);

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

