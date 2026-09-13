// The macOS presenter shim. Objective-C++ behind a C ABI, so the managed side owns no AppKit
// or Metal object lifetimes (§11.3, §12). Fixed-format upload only: what arrives is RGBA-half
// linear extended sRGB and what leaves is the same bytes on a tagged CAMetalLayer; ColorSync
// performs the only monitor transform (D-006, I3).
//
// COMPILED BY CI, NEVER RUN ON HARDWARE. This file was written against the AppKit / Metal /
// Core Animation headers as documented; ci.yml and release.yml build it on GitHub's macOS
// runner (packaging/macos/build-macos-presenter.sh). Everything the managed side depends on —
// result codes, struct layouts, the create/present contract — is pinned by tests on both sides.
// What no test covers is whether the window server does with these bytes what the docs say
// (README, D-012 / D-026 checklist); shipping without that verification is a user decision.

#import <AppKit/AppKit.h>
#import <Metal/Metal.h>
#import <QuartzCore/CAMetalLayer.h>

#include <algorithm>
#include <cstring>
#include <mutex>
#include <string>
#include <string_view>

#include "OpenRevelarePresentationMacOS.h"

namespace
{
constexpr size_t kMaximumDisplayIdBytes = 1024;

bool IsValidDimension(uint32_t value) noexcept
{
    return value != 0 && value <= static_cast<uint32_t>(INT32_MAX);
}

bool ReadDisplayId(const char* value, std::string_view& result) noexcept
{
    if (value == nullptr) return false;
    size_t length = 0;
    while (length < kMaximumDisplayIdBytes && value[length] != '\0') ++length;
    if (length == 0 || length == kMaximumDisplayIdBytes) return false;
    result = std::string_view(value, length);
    return true;
}

void CopyUtf8(char* destination, size_t capacity, NSString* source)
{
    std::memset(destination, 0, capacity);
    if (source == nil) return;
    const char* utf8 = [source UTF8String];
    if (utf8 == nullptr) return;
    std::strncpy(destination, utf8, capacity - 1);
}

NSScreen* ScreenFor(NSView* view)
{
    if (view == nil) return nil;
    NSScreen* screen = view.window.screen;
    return screen != nil ? screen : NSScreen.mainScreen;
}

uint32_t DirectDisplayIdOf(NSScreen* screen)
{
    if (screen == nil) return 0;
    NSNumber* number = screen.deviceDescription[@"NSScreenNumber"];
    return number != nil ? number.unsignedIntValue : 0;
}
} // namespace

// A view whose backing layer is the CAMetalLayer, sized to its bounds by AppKit. It swallows
// hit-testing so the Avalonia overlay above (crop handles, selections) keeps receiving events,
// mirroring the Win32 child's HTTRANSPARENT.
@interface OrwmPresenterView : NSView
@end

@implementation OrwmPresenterView
- (BOOL)wantsUpdateLayer { return YES; }
- (NSView*)hitTest:(NSPoint)point { return nil; }
- (BOOL)isOpaque { return YES; }
@end

struct OrwmPresenter
{
    explicit OrwmPresenter(bool request_extended_range) noexcept
        : request_extended_range_(request_extended_range)
    {
        std::memset(&diagnostics_, 0, sizeof(diagnostics_));
        diagnostics_.struct_size = sizeof(diagnostics_);
        diagnostics_.abi_version = ORWM_ABI_VERSION;
        diagnostics_.last_result = ORWM_E_NOT_INITIALIZED;
        diagnostics_.extended_range_requested = request_extended_range ? 1u : 0u;
        diagnostics_.pixel_format = static_cast<uint32_t>(MTLPixelFormatRGBA16Float);
    }

    ~OrwmPresenter()
    {
        std::lock_guard<std::mutex> guard(mutex_);
        [view_ removeFromSuperview];
        view_ = nil;
        layer_ = nil;
        queue_ = nil;
        device_ = nil;
    }

    int32_t Initialize(NSView* parent, uint32_t width, uint32_t height)
    {
        std::lock_guard<std::mutex> guard(mutex_);
        if (parent == nil) return SetResult(ORWM_E_INVALID_ARGUMENT);
        if (!IsValidDimension(width) || !IsValidDimension(height)) return SetResult(ORWM_E_INVALID_SIZE);

        device_ = MTLCreateSystemDefaultDevice();
        if (device_ == nil) return SetResult(ORWM_E_METAL);
        queue_ = [device_ newCommandQueue];
        if (queue_ == nil) return SetResult(ORWM_E_METAL);

        layer_ = [CAMetalLayer layer];
        layer_.device = device_;
        layer_.pixelFormat = MTLPixelFormatRGBA16Float;
        layer_.framebufferOnly = NO;      // the drawable's texture is written by a blit, not a render pass
        layer_.opaque = YES;
        layer_.presentsWithTransaction = NO;
        layer_.displaySyncEnabled = YES;

        // The tag that makes the window server treat these bytes as what they are. Without it
        // the layer is assumed sRGB and every value above one is clipped before ColorSync runs.
        CGColorSpaceRef color_space = CGColorSpaceCreateWithName(kCGColorSpaceExtendedLinearSRGB);
        if (color_space == nullptr)
        {
            color_space_failure_ = true;
            return SetResult(ORWM_E_COLOR_SPACE);
        }
        layer_.colorspace = color_space;
        CGColorSpaceRelease(color_space);
        diagnostics_.color_space_was_set = 1;

        // D-012's candidate rule, decided on the managed side from the contract's headroom.
        layer_.wantsExtendedDynamicRangeContent = request_extended_range_ ? YES : NO;
        diagnostics_.layer_is_extended_range = layer_.wantsExtendedDynamicRangeContent ? 1u : 0u;

        view_ = [[OrwmPresenterView alloc] initWithFrame:NSMakeRect(0, 0, width, height)];
        view_.wantsLayer = YES;
        view_.layer = layer_;
        view_.layerContentsRedrawPolicy = NSViewLayerContentsRedrawNever;
        view_.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
        [parent addSubview:view_];

        width_ = width;
        height_ = height;
        layer_.drawableSize = CGSizeMake(width, height);
        diagnostics_.width = width;
        diagnostics_.height = height;
        initialized_ = true;
        return SetResult(ORWM_OK);
    }

    int32_t Resize(uint32_t width, uint32_t height, double backing_scale)
    {
        std::lock_guard<std::mutex> guard(mutex_);
        if (!IsValidDimension(width) || !IsValidDimension(height)) return SetResult(ORWM_E_INVALID_SIZE);
        if (!initialized_ || layer_ == nil) return SetResult(ORWM_E_NOT_INITIALIZED);
        if (!(backing_scale > 0.0)) return SetResult(ORWM_E_INVALID_ARGUMENT);

        // width/height are PHYSICAL pixels (the managed side rounds them from the logical
        // viewport, the same way the Win32 host does). The view is laid out in points.
        layer_.contentsScale = backing_scale;
        layer_.drawableSize = CGSizeMake(width, height);
        view_.frame = NSMakeRect(0, 0, width / backing_scale, height / backing_scale);
        width_ = width;
        height_ = height;
        diagnostics_.width = width;
        diagnostics_.height = height;
        return SetResult(ORWM_OK);
    }

    int32_t Present(
        const void* bytes,
        size_t byte_count,
        uint32_t row_pitch,
        uint32_t width,
        uint32_t height,
        const OrwmPresentationContract* frame_contract,
        const OrwmPresentationContract* current_contract)
    {
        std::lock_guard<std::mutex> guard(mutex_);

        int32_t validation = ValidatePresentation(
            bytes, byte_count, row_pitch, width, height, frame_contract, current_contract);
        if (validation != ORWM_OK)
        {
            ++diagnostics_.rejected_present_count;
            return SetResult(validation);
        }

        std::string_view validated_display_id;
        if (!ReadDisplayId(frame_contract->display_id_utf8, validated_display_id))
        {
            ++diagnostics_.rejected_present_count;
            return SetResult(ORWM_E_INVALID_ARGUMENT);
        }
        std::string accepted_display_id(validated_display_id);

        @autoreleasepool
        {
            id<CAMetalDrawable> drawable = [layer_ nextDrawable];
            if (drawable == nil)
            {
                ++diagnostics_.dropped_drawable_count;
                return SetResult(ORWM_E_DRAWABLE_UNAVAILABLE);
            }

            // Straight into the drawable's texture. replaceRegion is a synchronous CPU upload,
            // which is the honest counterpart of the Win32 shim's Map/memcpy/CopyResource: a
            // fixed copy, no pass, nothing that could reinterpret a value.
            MTLRegion region = MTLRegionMake2D(0, 0, width, height);
            [drawable.texture replaceRegion:region
                                mipmapLevel:0
                                  withBytes:bytes
                                bytesPerRow:row_pitch];

            id<MTLCommandBuffer> command = [queue_ commandBuffer];
            if (command == nil) return SetResult(ORWM_E_METAL);
            [command presentDrawable:drawable];
            [command commit];
        }

        bound_display_id_.swap(accepted_display_id);
        bound_revision_ = frame_contract->revision;
        diagnostics_.last_contract_revision = bound_revision_;
        StoreDiagnosticDisplayId(bound_display_id_);
        ++diagnostics_.successful_present_count;
        return SetResult(ORWM_OK);
    }

    int32_t QueryDiagnostics(OrwmDiagnostics* output)
    {
        if (output == nullptr || output->struct_size < sizeof(OrwmDiagnostics)) return ORWM_E_INVALID_ARGUMENT;
        std::lock_guard<std::mutex> guard(mutex_);
        if (layer_ != nil)
            diagnostics_.layer_is_extended_range = layer_.wantsExtendedDynamicRangeContent ? 1u : 0u;
        *output = diagnostics_;
        return ORWM_OK;
    }

private:
    static constexpr uint32_t kBytesPerPixel = 8;   // RGBA-half

    int32_t ValidatePresentation(
        const void* bytes,
        size_t byte_count,
        uint32_t row_pitch,
        uint32_t width,
        uint32_t height,
        const OrwmPresentationContract* frame_contract,
        const OrwmPresentationContract* current_contract) const noexcept
    {
        if (!initialized_ || layer_ == nil || queue_ == nil) return ORWM_E_NOT_INITIALIZED;
        if (bytes == nullptr || frame_contract == nullptr || current_contract == nullptr) return ORWM_E_INVALID_ARGUMENT;
        if (frame_contract->struct_size < sizeof(OrwmPresentationContract) ||
            current_contract->struct_size < sizeof(OrwmPresentationContract))
            return ORWM_E_INVALID_ARGUMENT;
        if ((frame_contract->request_extended_range != 0) != request_extended_range_ ||
            (current_contract->request_extended_range != 0) != request_extended_range_)
            return ORWM_E_CONTRACT_DISPLAY_MISMATCH;

        std::string_view frame_display_id;
        std::string_view current_display_id;
        if (!ReadDisplayId(frame_contract->display_id_utf8, frame_display_id) ||
            !ReadDisplayId(current_contract->display_id_utf8, current_display_id))
            return ORWM_E_INVALID_ARGUMENT;
        if (frame_display_id != current_display_id ||
            (!bound_display_id_.empty() && frame_display_id != bound_display_id_))
            return ORWM_E_CONTRACT_DISPLAY_MISMATCH;
        if (frame_contract->revision != current_contract->revision || frame_contract->revision < bound_revision_)
            return ORWM_E_STALE_REVISION;
        if (!IsValidDimension(width) || !IsValidDimension(height) || width != width_ || height != height_)
            return ORWM_E_INVALID_SIZE;

        const uint64_t tight_row_bytes = static_cast<uint64_t>(width) * kBytesPerPixel;
        if (tight_row_bytes > UINT32_MAX || row_pitch < tight_row_bytes) return ORWM_E_INVALID_ROW_PITCH;
        const uint64_t required = static_cast<uint64_t>(height - 1) * row_pitch + tight_row_bytes;
        if (required > SIZE_MAX || byte_count < static_cast<size_t>(required)) return ORWM_E_BUFFER_TOO_SMALL;
        return ORWM_OK;
    }

    void StoreDiagnosticDisplayId(const std::string& display_id) noexcept
    {
        std::memset(diagnostics_.last_display_id_utf8, 0, sizeof(diagnostics_.last_display_id_utf8));
        const size_t maximum = sizeof(diagnostics_.last_display_id_utf8) - 1;
        const size_t copy_length = std::min(maximum, display_id.size());
        std::memcpy(diagnostics_.last_display_id_utf8, display_id.data(), copy_length);
        diagnostics_.display_id_was_truncated = display_id.size() > copy_length ? 1u : 0u;
    }

    int32_t SetResult(int32_t result) noexcept
    {
        diagnostics_.last_result = result;
        return result;
    }

    std::mutex mutex_;
    bool request_extended_range_ = false;
    bool initialized_ = false;
    bool color_space_failure_ = false;
    uint32_t width_ = 0;
    uint32_t height_ = 0;
    std::string bound_display_id_;
    uint64_t bound_revision_ = 0;

    id<MTLDevice> device_ = nil;
    id<MTLCommandQueue> queue_ = nil;
    CAMetalLayer* layer_ = nil;
    OrwmPresenterView* view_ = nil;
    OrwmDiagnostics diagnostics_{};
};

extern "C" ORWM_API int32_t orwm_probe(void* view_handle, OrwmDisplayProbe* probe)
{
    if (probe == nullptr || probe->struct_size < sizeof(OrwmDisplayProbe)) return ORWM_E_INVALID_ARGUMENT;
    @autoreleasepool
    {
        NSView* view = (__bridge NSView*)view_handle;
        NSScreen* screen = ScreenFor(view);

        std::memset(probe, 0, sizeof(*probe));
        probe->struct_size = sizeof(*probe);
        probe->abi_version = ORWM_ABI_VERSION;
        if (screen == nil)
        {
            probe->last_result = ORWM_E_VIEW;
            probe->backing_scale_factor = 1.0;
            probe->maximum_edr_value = 1.0;
            probe->maximum_potential_edr_value = 1.0;
            probe->maximum_reference_edr_value = 1.0;
            return ORWM_OK;   // a probe that found no screen is a valid, unreliable answer
        }

        probe->direct_display_id = DirectDisplayIdOf(screen);
        probe->backing_scale_factor = screen.backingScaleFactor;
        probe->maximum_edr_value = screen.maximumExtendedDynamicRangeColorComponentValue;
        probe->maximum_potential_edr_value = screen.maximumPotentialExtendedDynamicRangeColorComponentValue;
        probe->maximum_reference_edr_value = screen.maximumReferenceExtendedDynamicRangeColorComponentValue;
        CopyUtf8(probe->localized_name_utf8, sizeof(probe->localized_name_utf8), screen.localizedName);
        CopyUtf8(probe->color_space_name_utf8, sizeof(probe->color_space_name_utf8), screen.colorSpace.localizedName);
        probe->last_result = ORWM_OK;
        return ORWM_OK;
    }
}

extern "C" ORWM_API int32_t orwm_create(
    void* parent_view,
    uint32_t request_extended_range,
    uint32_t width,
    uint32_t height,
    OrwmPresenter** out_presenter)
{
    if (out_presenter == nullptr) return ORWM_E_INVALID_ARGUMENT;
    *out_presenter = nullptr;
    if (parent_view == nullptr) return ORWM_E_INVALID_ARGUMENT;

    try
    {
        auto* presenter = new (std::nothrow) OrwmPresenter(request_extended_range != 0);
        if (presenter == nullptr) return ORWM_E_INTERNAL;
        const int32_t result = presenter->Initialize((__bridge NSView*)parent_view, width, height);
        if (result != ORWM_OK)
        {
            delete presenter;
            return result;
        }
        *out_presenter = presenter;
        return ORWM_OK;
    }
    catch (...)
    {
        return ORWM_E_INTERNAL;
    }
}

extern "C" ORWM_API int32_t orwm_resize(OrwmPresenter* presenter, uint32_t width, uint32_t height, double backing_scale)
{
    if (presenter == nullptr) return ORWM_E_INVALID_ARGUMENT;
    try { return presenter->Resize(width, height, backing_scale); }
    catch (...) { return ORWM_E_INTERNAL; }
}

extern "C" ORWM_API int32_t orwm_present(
    OrwmPresenter* presenter,
    const void* bytes,
    size_t byte_count,
    uint32_t row_pitch,
    uint32_t width,
    uint32_t height,
    const OrwmPresentationContract* frame_contract,
    const OrwmPresentationContract* current_contract)
{
    if (presenter == nullptr) return ORWM_E_INVALID_ARGUMENT;
    try { return presenter->Present(bytes, byte_count, row_pitch, width, height, frame_contract, current_contract); }
    catch (...) { return ORWM_E_INTERNAL; }
}

extern "C" ORWM_API int32_t orwm_query_diagnostics(OrwmPresenter* presenter, OrwmDiagnostics* diagnostics)
{
    if (presenter == nullptr) return ORWM_E_INVALID_ARGUMENT;
    try { return presenter->QueryDiagnostics(diagnostics); }
    catch (...) { return ORWM_E_INTERNAL; }
}

extern "C" ORWM_API void orwm_destroy(OrwmPresenter* presenter)
{
    try { delete presenter; }
    catch (...) { /* C ABI boundary: destruction must never unwind into the caller. */ }
}
