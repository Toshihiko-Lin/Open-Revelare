#pragma once
#include <libheif/heif.h>
#include <cstdint>
#include <memory>
#include <stdexcept>
#include <vector>

namespace or_hevc {
using Nal = std::vector<uint8_t>;
using Packets = std::vector<Nal>;

struct Color {
    int primaries = 2, transfer = 2, matrix = 1;
    bool full_range = true;
};

// An export owns one session. It reuses bounded 1024x1024 P010 surfaces across
// tiles, independently of the compute/shader session used by preview rendering.
class HardwareEncoder {
public:
    virtual ~HardwareEncoder() = default;
    virtual Packets encode(const heif_image* image) = 0;
};
std::unique_ptr<HardwareEncoder> create_hardware_encoder(int width, int height, int quality, Color color);
void register_hardware_encoder();

inline std::vector<uint16_t> p010(const heif_image* image, int width, int height) {
    if (width % 2 || height % 2 || heif_image_get_bits_per_pixel_range(image, heif_channel_Y) != 10)
        throw std::runtime_error("Hardware HEVC requires even-sized 10-bit YCbCr 4:2:0");
    std::vector<uint16_t> pixels(size_t(width) * height * 3 / 2);
    int ys = 0, us = 0, vs = 0;
    const auto* y = heif_image_get_plane_readonly(image, heif_channel_Y, &ys);
    const auto* u = heif_image_get_plane_readonly(image, heif_channel_Cb, &us);
    const auto* v = heif_image_get_plane_readonly(image, heif_channel_Cr, &vs);
    if (!y || !u || !v) throw std::runtime_error("Missing hardware HEVC input planes");
    for (int row = 0; row < height; ++row) {
        auto* source = reinterpret_cast<const uint16_t*>(y + size_t(row) * ys);
        auto* dest = pixels.data() + size_t(row) * width;
        for (int x = 0; x < width; ++x) dest[x] = uint16_t(source[x] << 6);
    }
    auto* chroma = pixels.data() + size_t(width) * height;
    for (int row = 0; row < height / 2; ++row) {
        auto* cb = reinterpret_cast<const uint16_t*>(u + size_t(row) * us);
        auto* cr = reinterpret_cast<const uint16_t*>(v + size_t(row) * vs);
        auto* dest = chroma + size_t(row) * width;
        for (int x = 0; x < width / 2; ++x) {
            dest[x * 2] = uint16_t(cb[x] << 6);
            dest[x * 2 + 1] = uint16_t(cr[x] << 6);
        }
    }
    return pixels;
}

// Media Foundation returns Annex B, while VideoToolbox returns length-prefixed
// NALs. libheif's plugin ABI expects individual NALs without either prefix.
inline Packets annex_b(const uint8_t* data, size_t size) {
    Packets result;
    auto prefix = [&](size_t p) -> size_t {
        if (p + 3 <= size && data[p] == 0 && data[p + 1] == 0) {
            if (data[p + 2] == 1) return 3;
            if (p + 4 <= size && data[p + 2] == 0 && data[p + 3] == 1) return 4;
        }
        return 0;
    };
    size_t p = 0;
    while (p < size && !prefix(p)) ++p;
    while (p < size) {
        size_t start = p + prefix(p), end = start;
        while (end < size && !prefix(end)) ++end;
        size_t payload_end = end;
        while (payload_end > start && data[payload_end - 1] == 0) --payload_end;
        if (payload_end > start) result.emplace_back(data + start, data + payload_end);
        p = end;
    }
    if (result.empty()) throw std::runtime_error("Hardware HEVC returned no Annex B NALs");
    return result;
}
}
