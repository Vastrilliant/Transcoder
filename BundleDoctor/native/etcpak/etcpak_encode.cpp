// Tiny raw-RGBA stdin -> raw ETC2 encoder bridge around wolfpld/etcpak's
// ProcessRGB compressor. Replaces the old google/etc2comp-backed
// etc2encode.cpp with the same wire protocol so the managed side (see
// ../../Etc2Encoder.cs) barely had to change.
//
// Input:  12-byte little-endian header {uint32 width, uint32 height, uint32 format}
//         followed by width*height*4 RGBA8 bytes (tightly packed, no row padding).
// format: 45 = ETC2 RGB8, 47 = ETC2 RGBA8. (Same Unity TextureFormat values
//         TextureCodec.cs already uses everywhere else.)
// Output: raw ETC2 block bytes only, laid out block-row-major - identical
//         layout etc2comp produced, so the block-count size check in
//         Etc2Encoder.cs needed no changes.
//
// etcpak's CompressEtc2Rgb/CompressEtc2Rgba (unlike etc2comp's Etc::Encode)
// assume the source buffer's width/height are already multiples of 4 and do
// not pad internally - see ProcessRGB.cpp's block-transpose loads, which
// read a full 4-pixel column stride per block. Unity's own ETC2 texture
// dimensions are block-aligned in the overwhelming majority of cases, but to
// stay strictly behavior-compatible with the old etc2comp path (which padded
// via Etc::Encode's extendedWidth/extendedHeight internally) we pad up to
// the next multiple of 4 ourselves below, replicating edge pixels, before
// handing the buffer to etcpak.
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <vector>
#include <thread>
#include <algorithm>
#include <limits>

#include "ProcessRGB.hpp"

namespace {

bool ReadExact(void* ptr, size_t size)
{
    return std::fread(ptr, 1, size, stdin) == size;
}

bool WriteExact(const void* ptr, size_t size)
{
    return std::fwrite(ptr, 1, size, stdout) == size;
}

// Pads an RGBA8 image (tightly packed, row-major) up to (paddedW, paddedH)
// by replicating the last real column/row into the new blocks-worth of
// margin. paddedW/paddedH are always multiples of 4 and >= width/height.
std::vector<uint32_t> PadToBlockGrid(
    const uint32_t* src, uint32_t width, uint32_t height,
    uint32_t paddedW, uint32_t paddedH)
{
    std::vector<uint32_t> out(static_cast<size_t>(paddedW) * paddedH);
    for (uint32_t y = 0; y < paddedH; ++y)
    {
        uint32_t srcY = std::min(y, height - 1);
        const uint32_t* srcRow = src + static_cast<size_t>(srcY) * width;
        uint32_t* dstRow = out.data() + static_cast<size_t>(y) * paddedW;
        for (uint32_t x = 0; x < paddedW; ++x)
        {
            uint32_t srcX = std::min(x, width - 1);
            dstRow[x] = srcRow[srcX];
        }
    }
    return out;
}

// Splits the padded image into row-bands (each a multiple of 4 rows) across
// worker threads, mirroring the tiling Application.cpp does for its own
// multithreaded compression, without pulling in etcpak's TaskDispatch.
void CompressThreaded(const std::vector<uint32_t>& padded, uint32_t paddedW, uint32_t paddedH,
                       std::vector<uint64_t>& dst, bool rgba, bool useHeuristics)
{
    const uint32_t totalBlockRows = paddedH / 4;
    const uint32_t blocksPerRow = paddedW / 4;
    const uint64_t elemsPerBlock = rgba ? 2 : 1;

    unsigned int threadCount = std::max(1u, std::thread::hardware_concurrency());
    threadCount = std::min<unsigned int>(threadCount, std::max(1u, totalBlockRows));

    std::vector<std::thread> workers;
    workers.reserve(threadCount);

    uint32_t rowsPerThread = (totalBlockRows + threadCount - 1) / threadCount;

    for (unsigned int t = 0; t < threadCount; ++t)
    {
        uint32_t blockRowStart = t * rowsPerThread;
        if (blockRowStart >= totalBlockRows) break;
        uint32_t blockRowEnd = std::min(blockRowStart + rowsPerThread, totalBlockRows);
        uint32_t bandBlockRows = blockRowEnd - blockRowStart;
        if (bandBlockRows == 0) continue;

        const uint32_t* srcPtr = padded.data() + static_cast<size_t>(blockRowStart) * 4 * paddedW;
        uint64_t* dstPtr = dst.data() + static_cast<size_t>(blockRowStart) * blocksPerRow * elemsPerBlock;
        uint32_t blocks = blocksPerRow * bandBlockRows;

        workers.emplace_back([=, &padded]() {
            if (rgba)
                CompressEtc2Rgba(srcPtr, dstPtr, blocks, paddedW, useHeuristics);
            else
                CompressEtc2Rgb(srcPtr, dstPtr, blocks, paddedW, useHeuristics);
        });
    }

    for (auto& w : workers) w.join();
}

} // namespace

int main()
{
    std::uint32_t width = 0, height = 0, format = 0;
    if (!ReadExact(&width, sizeof(width)) ||
        !ReadExact(&height, sizeof(height)) ||
        !ReadExact(&format, sizeof(format)))
    {
        std::fprintf(stderr, "etcpak_encode: truncated header\n");
        return 2;
    }

    if (width == 0 || height == 0)
    {
        std::fprintf(stderr, "etcpak_encode: invalid dimensions %ux%u\n", width, height);
        return 2;
    }
    if (format != 45 && format != 47)
    {
        std::fprintf(stderr, "etcpak_encode: unsupported format %u (expected 45 or 47)\n", format);
        return 2;
    }

    const std::uint64_t pixelCount64 = static_cast<std::uint64_t>(width) * height;
    if (pixelCount64 > (std::numeric_limits<size_t>::max() / 4))
    {
        std::fprintf(stderr, "etcpak_encode: image is too large\n");
        return 2;
    }
    const size_t pixelCount = static_cast<size_t>(pixelCount64);

    std::vector<uint32_t> rgba(pixelCount);
    if (!ReadExact(rgba.data(), pixelCount * 4))
    {
        std::fprintf(stderr, "etcpak_encode: truncated RGBA payload\n");
        return 2;
    }

    const uint32_t paddedW = (width + 3u) & ~3u;
    const uint32_t paddedH = (height + 3u) & ~3u;
    std::vector<uint32_t> padded = PadToBlockGrid(rgba.data(), width, height, paddedW, paddedH);

    const bool rgba8 = (format == 47);
    const uint64_t elemsPerBlock = rgba8 ? 2 : 1;
    const uint64_t blockCount =
        static_cast<uint64_t>(paddedW / 4) * (paddedH / 4);

    std::vector<uint64_t> encoded(static_cast<size_t>(blockCount * elemsPerBlock));

    // useHeuristics=true matches etcpak's own CLI default (fast heuristic
    // mode selector); it's the setting etcpak's README benchmarks assume.
    CompressThreaded(padded, paddedW, paddedH, encoded, rgba8, /*useHeuristics=*/true);

    const bool ok = WriteExact(encoded.data(), encoded.size() * sizeof(uint64_t));
    if (!ok)
    {
        std::fprintf(stderr, "etcpak_encode: failed writing output\n");
        return 4;
    }

    return 0;
}
