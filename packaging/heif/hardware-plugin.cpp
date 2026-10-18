#include "hardware.h"
#include <libheif/heif_plugin.h>
#include <algorithm>
#include <cstring>
#include <mutex>
#include <string>

namespace or_hevc {
namespace {
constexpr heif_error ok{heif_error_Ok, heif_suberror_Unspecified, "OK"};
constexpr heif_error unsupported{heif_error_Usage_error, heif_suberror_Unsupported_parameter, "Unsupported hardware encoder parameter"};
struct Encoder {
    int quality = 90;
    std::unique_ptr<HardwareEncoder> session;
    Packets packets;
    size_t next = 0;
    std::string error;
    int width = 0, height = 0;
};
const char* name() { return "OpenRevelare hardware HEVC Main10"; }
heif_error create(void** out) {
    try { *out = new Encoder; return ok; }
    catch (...) { *out = nullptr; return {heif_error_Memory_allocation_error, heif_suberror_Unspecified, "Hardware encoder allocation failed"}; }
}
void destroy(void* p) { delete static_cast<Encoder*>(p); }
heif_error quality(void* p, int q) { static_cast<Encoder*>(p)->quality = std::clamp(q, 1, 100); return ok; }
heif_error get_quality(void* p, int* q) { *q = static_cast<Encoder*>(p)->quality; return ok; }
heif_error lossless(void*, int value) { return value ? unsupported : ok; }
heif_error get_zero(void*, int* value) { *value = 0; return ok; }
heif_error logging(void*, int) { return ok; }
const heif_encoder_parameter** parameters(void*) { static const heif_encoder_parameter* empty[] = {nullptr}; return empty; }
heif_error set_integer(void*, const char*, int) { return unsupported; }
heif_error get_integer(void*, const char*, int*) { return unsupported; }
heif_error set_string(void*, const char*, const char*) { return unsupported; }
heif_error get_string(void*, const char*, char*, int) { return unsupported; }
void colorspace(heif_colorspace* cs, heif_chroma* chroma) { *cs = heif_colorspace_YCbCr; *chroma = heif_chroma_420; }
void colorspace2(void*, heif_colorspace* cs, heif_chroma* chroma) { colorspace(cs, chroma); }

heif_error encode(void* p, const heif_image* image, heif_image_input_class) {
    auto& e = *static_cast<Encoder*>(p);
    try {
        int w = heif_image_get_width(image, heif_channel_Y), h = heif_image_get_height(image, heif_channel_Y);
        Color color;
        heif_color_profile_nclx* nclx = nullptr;
        if (heif_image_get_nclx_color_profile(image, &nclx).code == heif_error_Ok && nclx) {
            color = {int(nclx->color_primaries), int(nclx->transfer_characteristics), int(nclx->matrix_coefficients), nclx->full_range_flag != 0};
            heif_nclx_color_profile_free(nclx);
        }
        if (!e.session || e.width != w || e.height != h) {
            e.session = create_hardware_encoder(w, h, e.quality, color);
            e.width = w; e.height = h;
        }
        e.packets = e.session->encode(image);
        e.next = 0;
        bool vps = false, sps = false, pps = false, idr = false;
        for (const auto& nal : e.packets) {
            if (nal.size() < 2) throw std::runtime_error("Truncated hardware HEVC NAL");
            int type = (nal[0] >> 1) & 63;
            vps |= type == 32; sps |= type == 33; pps |= type == 34;
            idr |= type == 19 || type == 20;
            if (type <= 31 && type != 19 && type != 20)
                throw std::runtime_error("Hardware HEVC produced a dependent tile");
            if (type == 33) {
                // SPS RBSP: sps_video_parameter_set_id/sublayers, then profile_tier_level.
                // Emulation prevention cannot precede these first two nonzero bytes.
                if (nal.size() < 4 || (nal[3] & 31) != 2)
                    throw std::runtime_error("Hardware HEVC did not produce Main10");
            }
        }
        if (!vps || !sps || !pps || !idr) throw std::runtime_error("Incomplete independent hardware HEVC tile");
        return ok;
    } catch (const std::exception& ex) {
        e.error = ex.what();
        return {heif_error_Encoder_plugin_error, heif_suberror_Unspecified, e.error.c_str()};
    }
}
heif_error compressed(void* p, uint8_t** bytes, int* size, heif_encoded_data_type* type) {
    auto& e = *static_cast<Encoder*>(p);
    *bytes = nullptr; *size = 0;
    if (e.next < e.packets.size()) {
        auto& packet = e.packets[e.next++];
        *bytes = packet.data(); *size = int(packet.size());
        if (type) *type = ((packet[0] >> 1) & 63) >= 32 ? heif_encoded_data_type_HEVC_header : heif_encoded_data_type_HEVC_image;
    }
    return ok;
}
const heif_encoder_plugin plugin = {
    4, heif_compression_HEVC, "or-hardware", 10, 1, 0, name, nullptr, nullptr,
    create, destroy, quality, get_quality, lossless, get_zero, logging, get_zero,
    parameters, set_integer, get_integer, set_integer, get_integer, set_string, get_string,
    colorspace, encode, compressed, colorspace2, nullptr,
    LIBHEIF_MAKE_VERSION(1,21,0), nullptr, nullptr, nullptr, nullptr, 0
};
}
void register_hardware_encoder() {
    static std::once_flag once;
    std::call_once(once, [] {
        auto init = heif_init(nullptr);
        if (init.code != heif_error_Ok) throw std::runtime_error(init.message);
        auto registered = heif_register_encoder_plugin(&plugin);
        if (registered.code != heif_error_Ok) throw std::runtime_error(registered.message);
    });
}
}
