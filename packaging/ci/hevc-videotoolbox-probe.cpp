// Hosted Macs can expose VideoToolbox without a usable Main10 hardware encoder.
// Report the exact unsupported property so a safe fallback is distinguishable
// from a configuration regression; this probe does not change the encoder policy.
#include <VideoToolbox/VideoToolbox.h>
#include <cstdio>

static void report(VTCompressionSessionRef session, CFStringRef key, CFTypeRef value) {
    char name[128]{};
    CFStringGetCString(key, name, sizeof(name), kCFStringEncodingUTF8);
    std::printf("VideoToolbox %s: %d\n", name, int(VTSessionSetProperty(session, key, value)));
}
int main() {
    const void* keys[] = {kVTVideoEncoderSpecification_RequireHardwareAcceleratedVideoEncoder};
    const void* values[] = {kCFBooleanTrue};
    auto specification = CFDictionaryCreate(nullptr, keys, values, 1, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    VTCompressionSessionRef session = nullptr;
    OSStatus status = VTCompressionSessionCreate(nullptr, 1024, 1024, kCMVideoCodecType_HEVC,
        specification, nullptr, nullptr, nullptr, nullptr, &session);
    CFRelease(specification);
    std::printf("VideoToolbox hardware session: %d\n", int(status));
    if (status || !session) return 0; // No device is a valid result on CI.
    report(session, kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_HEVC_Main10_AutoLevel);
    report(session, kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse);
    int interval = 2, delay = 0;
    float quality = .9f;
    auto key_interval = CFNumberCreate(nullptr, kCFNumberIntType, &interval);
    auto frame_delay = CFNumberCreate(nullptr, kCFNumberIntType, &delay);
    auto q = CFNumberCreate(nullptr, kCFNumberFloatType, &quality);
    report(session, kVTCompressionPropertyKey_MaxKeyFrameInterval, key_interval);
    report(session, kVTCompressionPropertyKey_MaxFrameDelayCount, frame_delay);
    report(session, kVTCompressionPropertyKey_Quality, q);
    report(session, kVTCompressionPropertyKey_ColorPrimaries, kCVImageBufferColorPrimaries_ITU_R_2020);
    report(session, kVTCompressionPropertyKey_TransferFunction, kCVImageBufferTransferFunction_SMPTE_ST_2084_PQ);
    report(session, kVTCompressionPropertyKey_YCbCrMatrix, kCVImageBufferYCbCrMatrix_ITU_R_2020);
    CFRelease(key_interval); CFRelease(frame_delay); CFRelease(q);
    std::printf("VideoToolbox prepare: %d\n", int(VTCompressionSessionPrepareToEncodeFrames(session)));
    VTCompressionSessionInvalidate(session); CFRelease(session);
}
