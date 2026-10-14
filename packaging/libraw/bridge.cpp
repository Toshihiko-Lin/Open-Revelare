// Stable, application-owned C ABI. LibRaw structures never cross into managed code.
#include <libraw/libraw.h>
#include <cstring>
#ifdef _WIN32
#define API extern "C" __declspec(dllexport)
#else
#define API extern "C" __attribute__((visibility("default")))
#endif

API const char* or_raw_version() { return LibRaw::version(); }
API LibRaw* or_raw_create() { try { return new LibRaw; } catch (...) { return nullptr; } }
API void or_raw_close(LibRaw* r) { delete r; }
API int or_raw_open(LibRaw* r, void* data, size_t size) { return r->open_buffer(data, size); }
API int or_raw_open_file(LibRaw* r, const char* path) { return r->open_file(path); }
#ifdef _WIN32
API int or_raw_open_file_wide(LibRaw* r, const wchar_t* path) { return r->open_file(path); }
#endif
API int or_raw_unpack(LibRaw* r) { return r->unpack(); }
API const char* or_raw_error(int code) { return libraw_strerror(code); }
API int or_raw_value(LibRaw* r, int key) {
    const auto& s = r->imgdata.sizes;
    switch (key) {
    case 0: return s.width;
    case 1: return s.height;
    case 2: return s.raw_width;
    case 3: return s.raw_height;
    case 4: return r->imgdata.color.maximum;
    default: return 0;
    }
}
API const char* or_raw_model(LibRaw* r, int model) {
    return model ? r->imgdata.idata.model : r->imgdata.idata.make;
}
API void or_raw_matrix(LibRaw* r, float* values) {
    std::memcpy(values, r->imgdata.color.rgb_cam, sizeof(float) * 12);
}
API void or_raw_wb(LibRaw* r, float* values) {
    std::memcpy(values, r->imgdata.color.cam_mul, sizeof(float) * 4);
}
API void or_raw_crop(LibRaw* r, int* values) {
    const auto& c = r->imgdata.sizes.raw_inset_crops[0];
    values[0] = c.cleft; values[1] = c.ctop;
    values[2] = c.cwidth; values[3] = c.cheight;
}
API int or_raw_process(LibRaw* r, int fbdd, int half, const unsigned* crop) {
    auto& p = r->imgdata.params;
    p.use_camera_wb = p.use_auto_wb = 0;
    for (int i = 0; i < 4; ++i) p.user_mul[i] = 1.f;
    p.output_color = 0;
    p.output_bps = 16;
    p.no_auto_bright = 1;
    p.user_qual = 3; // AHD
    p.gamm[0] = p.gamm[1] = 1.;
    for (int i = 2; i < 6; ++i) p.gamm[i] = 0.;
    p.fbdd_noiserd = fbdd;
    p.half_size = half;
    if (crop) for (int i = 0; i < 4; ++i) p.cropbox[i] = crop[i];
    return r->dcraw_process();
}
API libraw_processed_image_t* or_raw_image(LibRaw* r, int* error) {
    return r->dcraw_make_mem_image(error);
}
API void or_raw_image_free(libraw_processed_image_t* image) { LibRaw::dcraw_clear_mem(image); }
API const void* or_raw_image_data(libraw_processed_image_t* image, int* info) {
    info[0] = image->width; info[1] = image->height;
    info[2] = image->colors; info[3] = image->bits;
    info[4] = image->data_size; info[5] = image->type;
    return image->data;
}
