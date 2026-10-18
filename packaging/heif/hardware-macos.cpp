#include "hardware.h"
#include <VideoToolbox/VideoToolbox.h>
#include <CoreMedia/CoreMedia.h>
#include <CoreVideo/CoreVideo.h>
#include <algorithm>
#include <cstring>
#include <string>

namespace or_hevc {
namespace {
void check(OSStatus status, const char* operation) {
    if (status != noErr) throw std::runtime_error(std::string(operation) + " (OSStatus " + std::to_string(status) + ")");
}
template<class T> struct CF {
    T value = nullptr;
    CF() = default;
    explicit CF(T v) : value(v) {}
    CF(const CF&) = delete;
    ~CF() { if (value) CFRelease(value); }
};
CFDictionaryRef dictionary(const void** keys, const void** values, CFIndex count) {
    return CFDictionaryCreate(kCFAllocatorDefault, keys, values, count, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
}
class MacEncoder final : public HardwareEncoder {
    VTCompressionSessionRef session = nullptr;
    int width, height;
    Color color;
    int64_t frame = 0;
    Packets packets;
    OSStatus callback_status = noErr;
    std::string callback_error;
    bool received = false;

    static void callback(void* context, void*, OSStatus status, VTEncodeInfoFlags, CMSampleBufferRef sample) {
        auto& e = *static_cast<MacEncoder*>(context);
        e.callback_status = status;
        try {
            if (status != noErr || !sample || !CMSampleBufferDataIsReady(sample))
                throw std::runtime_error("VideoToolbox returned no complete HEVC tile");
            CMFormatDescriptionRef description = CMSampleBufferGetFormatDescription(sample);
            size_t count = 0, size = 0;
            const uint8_t* parameter = nullptr;
            int prefix_size = 0;
            check(CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(description, 0, &parameter, &size, &count, &prefix_size), "Read HEVC parameter sets");
            for (size_t i = 0; i < count; ++i) {
                check(CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(description, i, &parameter, &size, nullptr, nullptr), "Read Main10 parameter set");
                e.packets.emplace_back(parameter, parameter + size);
            }
            if (prefix_size != 4) throw std::runtime_error("Unexpected VideoToolbox NAL length format");
            CMBlockBufferRef block = CMSampleBufferGetDataBuffer(sample);
            if (!block) throw std::runtime_error("Missing VideoToolbox bitstream");
            size_t length = CMBlockBufferGetDataLength(block);
            std::vector<uint8_t> bytes(length);
            check(CMBlockBufferCopyDataBytes(block, 0, length, bytes.data()), "Copy HEVC bitstream");
            for (size_t offset = 0; offset < length;) {
                if (length - offset < 4) throw std::runtime_error("Truncated HEVC NAL length");
                uint32_t n = (uint32_t(bytes[offset]) << 24) | (uint32_t(bytes[offset + 1]) << 16)
                    | (uint32_t(bytes[offset + 2]) << 8) | bytes[offset + 3];
                offset += 4;
                if (n < 2 || n > length - offset) throw std::runtime_error("Invalid VideoToolbox NAL length");
                e.packets.emplace_back(bytes.data() + offset, bytes.data() + offset + n);
                offset += n;
            }
            e.received = true;
        } catch (const std::exception& ex) { e.callback_error = ex.what(); }
    }
    void property(CFStringRef key, CFTypeRef value) { check(VTSessionSetProperty(session, key, value), "Configure hardware HEVC"); }
    void color_properties(CVPixelBufferRef pixel = nullptr) {
        CFStringRef primaries = color.primaries == 9 ? kCVImageBufferColorPrimaries_ITU_R_2020 : nullptr;
        CFStringRef transfer = color.transfer == 16 ? kCVImageBufferTransferFunction_SMPTE_ST_2084_PQ : nullptr;
        CFStringRef matrix = color.matrix == 9 ? kCVImageBufferYCbCrMatrix_ITU_R_2020 : kCVImageBufferYCbCrMatrix_ITU_R_709_2;
        if (pixel) {
            if (primaries) CVBufferSetAttachment(pixel, kCVImageBufferColorPrimariesKey, primaries, kCVAttachmentMode_ShouldPropagate);
            if (transfer) CVBufferSetAttachment(pixel, kCVImageBufferTransferFunctionKey, transfer, kCVAttachmentMode_ShouldPropagate);
            CVBufferSetAttachment(pixel, kCVImageBufferYCbCrMatrixKey, matrix, kCVAttachmentMode_ShouldPropagate);
        } else {
            if (primaries) property(kVTCompressionPropertyKey_ColorPrimaries, primaries);
            if (transfer) property(kVTCompressionPropertyKey_TransferFunction, transfer);
            property(kVTCompressionPropertyKey_YCbCrMatrix, matrix);
        }
    }
public:
    MacEncoder(int w, int h, int quality, Color c) : width(w), height(h), color(c) {
        try {
            const void* keys[] = {kVTVideoEncoderSpecification_RequireHardwareAcceleratedVideoEncoder};
            const void* values[] = {kCFBooleanTrue};
            CF<CFDictionaryRef> specification(dictionary(keys, values, 1));
            check(VTCompressionSessionCreate(kCFAllocatorDefault, width, height, kCMVideoCodecType_HEVC,
                specification.value, nullptr, nullptr, callback, this, &session), "Create hardware Main10 encoder");
            property(kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_HEVC_Main10_AutoLevel);
            property(kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse);
            int interval = 2, delay = 0;
            CF<CFNumberRef> key_interval(CFNumberCreate(nullptr, kCFNumberIntType, &interval));
            CF<CFNumberRef> frame_delay(CFNumberCreate(nullptr, kCFNumberIntType, &delay));
            property(kVTCompressionPropertyKey_MaxKeyFrameInterval, key_interval.value);
            property(kVTCompressionPropertyKey_MaxFrameDelayCount, frame_delay.value);
            float q = std::clamp(quality, 1, 100) / 100.f;
            CF<CFNumberRef> quality_value(CFNumberCreate(nullptr, kCFNumberFloatType, &q));
            property(kVTCompressionPropertyKey_Quality, quality_value.value);
            color_properties();
            check(VTCompressionSessionPrepareToEncodeFrames(session), "Prepare hardware Main10");
            CF<CFTypeRef> hardware;
            check(VTSessionCopyProperty(session, kVTCompressionPropertyKey_UsingHardwareAcceleratedVideoEncoder,
                kCFAllocatorDefault, &hardware.value), "Verify hardware encoder selection");
            if (!hardware.value || CFGetTypeID(hardware.value) != CFBooleanGetTypeID()
                || !CFBooleanGetValue(static_cast<CFBooleanRef>(hardware.value)))
                throw std::runtime_error("VideoToolbox did not select a hardware encoder");
        } catch (...) {
            if (session) { VTCompressionSessionInvalidate(session); CFRelease(session); session = nullptr; }
            throw;
        }
    }
    ~MacEncoder() override { if (session) { VTCompressionSessionInvalidate(session); CFRelease(session); } }
    Packets encode(const heif_image* image) override {
        auto data = p010(image, width, height);
        CF<CVPixelBufferRef> pixel;
        OSType format = color.full_range ? kCVPixelFormatType_420YpCbCr10BiPlanarFullRange : kCVPixelFormatType_420YpCbCr10BiPlanarVideoRange;
        const void* keys[] = {kCVPixelBufferIOSurfacePropertiesKey};
        CF<CFDictionaryRef> surface(dictionary(nullptr, nullptr, 0));
        const void* values[] = {surface.value};
        CF<CFDictionaryRef> attributes(dictionary(keys, values, 1));
        check(CVPixelBufferCreate(kCFAllocatorDefault, width, height, format, attributes.value, &pixel.value), "Allocate Main10 pixel surface");
        check(CVPixelBufferLockBaseAddress(pixel.value, 0), "Lock Main10 pixel surface");
        for (size_t plane = 0; plane < 2; ++plane) {
            auto* destination = static_cast<uint8_t*>(CVPixelBufferGetBaseAddressOfPlane(pixel.value, plane));
            size_t stride = CVPixelBufferGetBytesPerRowOfPlane(pixel.value, plane);
            const auto* source = data.data() + (plane ? size_t(width) * height : 0);
            int rows = plane ? height / 2 : height;
            for (int y = 0; y < rows; ++y) std::memcpy(destination + y * stride, source + size_t(y) * width, size_t(width) * 2);
        }
        CVPixelBufferUnlockBaseAddress(pixel.value, 0);
        color_properties(pixel.value);
        packets.clear(); callback_error.clear(); callback_status = noErr; received = false;
        const void* force_keys[] = {kVTEncodeFrameOptionKey_ForceKeyFrame};
        const void* force_values[] = {kCFBooleanTrue};
        CF<CFDictionaryRef> force_keyframe(dictionary(force_keys, force_values, 1));
        check(VTCompressionSessionEncodeFrame(session, pixel.value, CMTimeMake(frame++, 30), CMTimeMake(1, 30),
            force_keyframe.value, nullptr, nullptr), "Submit hardware HEVC tile");
        check(VTCompressionSessionCompleteFrames(session, kCMTimeInvalid), "Complete hardware HEVC tile");
        check(callback_status, "Encode Main10 tile");
        if (!callback_error.empty()) throw std::runtime_error(callback_error);
        if (!received) throw std::runtime_error("VideoToolbox did not emit the independent tile");
        return std::move(packets);
    }
};
}
std::unique_ptr<HardwareEncoder> create_hardware_encoder(int w, int h, int quality, Color color) {
    return std::make_unique<MacEncoder>(w, h, quality, color);
}
}
