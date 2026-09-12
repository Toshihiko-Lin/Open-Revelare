#pragma once

#include <stddef.h>
#include <stdint.h>

#if defined(__GNUC__)
#  define ORWM_API __attribute__((visibility("default")))
#else
#  define ORWM_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

/*
 * The macOS sibling of the Win32 orwp_* shim: create / resize / present / query / destroy, plus
 * orwm_probe because the display facts (EDR headroom, colour space) live on AppKit objects the
 * managed side cannot reach on its own.
 *
 * Every struct here has a matching Marshal.SizeOf assertion in
 * OpenRevelare.Presentation.MacOS.Tests; the static_asserts below are the native half of that
 * pin. Change one side and the other fails before a byte crosses.
 */

enum
{
    ORWM_ABI_VERSION = 1,
    ORWM_PROBE_NAME_CAPACITY = 128,
    ORWM_DIAGNOSTIC_DISPLAY_ID_CAPACITY = 256
};

typedef enum OrwmResult
{
    ORWM_OK = 0,
    ORWM_E_INVALID_ARGUMENT = -1,
    ORWM_E_INVALID_SIZE = -3,
    ORWM_E_INVALID_ROW_PITCH = -4,
    ORWM_E_BUFFER_TOO_SMALL = -5,
    ORWM_E_CONTRACT_DISPLAY_MISMATCH = -7,
    ORWM_E_STALE_REVISION = -8,
    ORWM_E_VIEW = -9,
    ORWM_E_METAL = -10,
    ORWM_E_COLOR_SPACE = -11,
    ORWM_E_NOT_INITIALIZED = -13,
    ORWM_E_INTERNAL = -14,
    ORWM_E_DRAWABLE_UNAVAILABLE = -15
} OrwmResult;

typedef struct OrwmPresentationContract
{
    uint32_t struct_size;
    uint32_t request_extended_range;
    const char* display_id_utf8;
    uint64_t revision;
} OrwmPresentationContract;

typedef struct OrwmDisplayProbe
{
    uint32_t struct_size;
    uint32_t abi_version;
    int32_t last_result;
    uint32_t direct_display_id;
    double backing_scale_factor;
    double maximum_edr_value;            /* NSScreen.maximumExtendedDynamicRangeColorComponentValue */
    double maximum_potential_edr_value;  /* NSScreen.maximumPotentialExtendedDynamicRangeColorComponentValue */
    double maximum_reference_edr_value;  /* NSScreen.maximumReferenceExtendedDynamicRangeColorComponentValue */
    char localized_name_utf8[ORWM_PROBE_NAME_CAPACITY];
    char color_space_name_utf8[ORWM_PROBE_NAME_CAPACITY];
} OrwmDisplayProbe;

typedef struct OrwmDiagnostics
{
    uint32_t struct_size;
    uint32_t abi_version;
    int32_t last_result;
    uint32_t extended_range_requested;
    uint32_t width;
    uint32_t height;
    uint32_t pixel_format;               /* MTLPixelFormat, expected MTLPixelFormatRGBA16Float (115) */
    uint32_t color_space_was_set;
    uint32_t layer_is_extended_range;    /* CAMetalLayer.wantsExtendedDynamicRangeContent as read back */
    uint64_t successful_present_count;
    uint64_t rejected_present_count;
    uint64_t dropped_drawable_count;
    uint64_t last_contract_revision;
    uint32_t display_id_was_truncated;
    char last_display_id_utf8[ORWM_DIAGNOSTIC_DISPLAY_ID_CAPACITY];
} OrwmDiagnostics;

#ifdef __cplusplus
static_assert(sizeof(OrwmPresentationContract) == 24, "managed NativePresentationContract pins 24");
static_assert(sizeof(OrwmDisplayProbe) == 304, "managed NativeDisplayProbe pins 304");
static_assert(sizeof(OrwmDiagnostics) == 336, "managed NativeDiagnostics pins 336");
#endif

typedef struct OrwmPresenter OrwmPresenter;

/* Reads the screen behind view_handle's window. view_handle is an NSView*. */
ORWM_API int32_t orwm_probe(void* view_handle, OrwmDisplayProbe* probe);

/*
 * Creates a CAMetalLayer-backed child view inside parent_view. The layer is RGBA16Float,
 * tagged kCGColorSpaceExtendedLinearSRGB, and asks for extended-range content iff
 * request_extended_range is non-zero (D-012's candidate rule, decided by the managed contract).
 */
ORWM_API int32_t orwm_create(
    void* parent_view,
    uint32_t request_extended_range,
    uint32_t width,
    uint32_t height,
    OrwmPresenter** out_presenter);

ORWM_API int32_t orwm_resize(
    OrwmPresenter* presenter,
    uint32_t width,
    uint32_t height,
    double backing_scale);

/*
 * Fixed-format upload only: RGBA-half rows are copied into the drawable's texture and
 * presented. There is deliberately no shader, transfer function, gamut map, matrix or ICC
 * operation here — ColorSync does the last hop (D-006, §11.3).
 */
ORWM_API int32_t orwm_present(
    OrwmPresenter* presenter,
    const void* bytes,
    size_t byte_count,
    uint32_t row_pitch,
    uint32_t width,
    uint32_t height,
    const OrwmPresentationContract* frame_contract,
    const OrwmPresentationContract* current_contract);

ORWM_API int32_t orwm_query_diagnostics(OrwmPresenter* presenter, OrwmDiagnostics* diagnostics);

ORWM_API void orwm_destroy(OrwmPresenter* presenter);

#ifdef __cplusplus
}
#endif
