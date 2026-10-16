#include <libheif/heif.h>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

#ifdef _WIN32
#define OR_API extern "C" __declspec(dllexport)
#else
#define OR_API extern "C" __attribute__((visibility("default")))
#endif

namespace {
void check(heif_error e) {
    if (e.code != heif_error_Ok) throw std::runtime_error(e.message ? e.message : "HEIC encoding failed");
}
template<class T, auto Free> using owned = std::unique_ptr<T, decltype(Free)>;
heif_error write_bytes(heif_context*, const void* data, size_t size, void* user) {
    try {
        auto& bytes = *static_cast<std::vector<uint8_t>*>(user);
        auto start = static_cast<const uint8_t*>(data);
        bytes.insert(bytes.end(), start, start + size);
        return {heif_error_Ok, heif_suberror_Unspecified, "OK"};
    } catch (...) {
        return {heif_error_Memory_allocation_error, heif_suberror_Unspecified, "HEIC output allocation failed"};
    }
}
}

// A flat ABI keeps libheif's versioned structs out of managed code. Files are returned
// in memory so Windows Unicode paths and atomic replacement stay with ExportFile.
OR_API int or_heif_encode_exif(const uint16_t* rgb, int width, int height, int quality,
                         int hdr, const uint8_t* icc, int icc_size,
                         const uint8_t* exif, int exif_size,
                         const uint8_t* xmp, int xmp_size,
                         void** output, size_t* output_size, char* error, int error_size) {
    *output = nullptr;
    *output_size = 0;
    try {
        owned<heif_context, heif_context_free> ctx(heif_context_alloc(), heif_context_free);
        if (!ctx) throw std::bad_alloc();
        heif_encoder* raw_encoder = nullptr;
        check(heif_context_get_encoder_for_format(ctx.get(), heif_compression_HEVC, &raw_encoder));
        owned<heif_encoder, heif_encoder_release> encoder(raw_encoder, heif_encoder_release);
        check(heif_encoder_set_lossy_quality(encoder.get(), quality));
        check(heif_encoder_set_parameter_string(encoder.get(), "chroma", "420"));
        check(heif_encoder_set_parameter_string(encoder.get(), "preset", "medium"));
        check(heif_encoder_set_parameter_string(encoder.get(), "x265:log-level", "-1"));
        heif_image* raw_image = nullptr;
        check(heif_image_create(width, height, heif_colorspace_RGB,
                               heif_chroma_interleaved_RRGGBB_LE, &raw_image));
        owned<heif_image, heif_image_release> image(raw_image, heif_image_release);
        check(heif_image_add_plane(image.get(), heif_channel_interleaved, width, height, 10));
        int stride = 0;
        auto plane = heif_image_get_plane(image.get(), heif_channel_interleaved, &stride);
        for (int y = 0; y < height; ++y)
            std::memcpy(plane + y * stride, rgb + size_t(y) * width * 3, size_t(width) * 6);
        owned<heif_color_profile_nclx, heif_nclx_color_profile_free> color(
            heif_nclx_color_profile_alloc(), heif_nclx_color_profile_free);
        if (!color) throw std::bad_alloc();
        // SDR's exact ICC describes arbitrary output primaries/TRCs. Unspecified NCLX
        // primaries/transfer prevent a contradictory sRGB tag for Adobe RGB or Rec709.
        check(heif_nclx_color_profile_set_color_primaries(color.get(), hdr ? 9 : 2));
        check(heif_nclx_color_profile_set_transfer_characteristics(color.get(), hdr ? 16 : 2));
        check(heif_nclx_color_profile_set_matrix_coefficients(color.get(), hdr ? 9 : 1));
        color->full_range_flag = 1;
        check(heif_image_set_nclx_color_profile(image.get(), color.get()));
        if (icc && icc_size > 0)
            check(heif_image_set_raw_color_profile(image.get(), "prof", icc, size_t(icc_size)));
        owned<heif_encoding_options, heif_encoding_options_free> options(
            heif_encoding_options_alloc(), heif_encoding_options_free);
        if (!options) throw std::bad_alloc();
        options->output_nclx_profile = color.get();
        options->save_two_colr_boxes_when_ICC_and_nclx_available = 1;
        options->macOS_compatibility_workaround_no_nclx_profile = 0;
        heif_image_handle* raw_handle = nullptr;
        check(heif_context_encode_image(ctx.get(), image.get(), encoder.get(), options.get(), &raw_handle));
        owned<heif_image_handle, heif_image_handle_release> handle(raw_handle, heif_image_handle_release);
        if (exif && exif_size > 0)
            check(heif_context_add_exif_metadata(ctx.get(), handle.get(), exif, exif_size));
        if (xmp && xmp_size > 0)
            check(heif_context_add_XMP_metadata(ctx.get(), handle.get(), xmp, xmp_size));
        std::vector<uint8_t> bytes;
        heif_writer writer{1, write_bytes};
        check(heif_context_write(ctx.get(), &writer, &bytes));
        auto result = std::make_unique<uint8_t[]>(bytes.size());
        std::memcpy(result.get(), bytes.data(), bytes.size());
        *output_size = bytes.size();
        *output = result.release();
        return 0;
    } catch (const std::exception& e) {
        if (error_size > 0) {
            std::strncpy(error, e.what(), size_t(error_size - 1));
            error[error_size - 1] = 0;
        }
        return 1;
    }
}
OR_API void or_heif_free(void* bytes) { delete[] static_cast<uint8_t*>(bytes); }
