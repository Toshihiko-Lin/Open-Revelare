#pragma once

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(ORWP_NATIVE_EXPORTS)
#    define ORWP_API __declspec(dllexport)
#  else
#    define ORWP_API __declspec(dllimport)
#  endif
#  define ORWP_CALL __cdecl
#else
#  define ORWP_API
#  define ORWP_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

enum
{
    ORWP_ABI_VERSION = 1,
    ORWP_DIAGNOSTIC_DISPLAY_ID_CAPACITY = 256
};

typedef enum OrwpMode
{
    ORWP_MODE_INVALID = 0,
    ORWP_MODE_ADVANCED_COLOR = 1,
    ORWP_MODE_LEGACY = 2
} OrwpMode;

typedef enum OrwpResult
{
    ORWP_OK = 0,
    ORWP_E_INVALID_ARGUMENT = -1,
    ORWP_E_INVALID_MODE = -2,
    ORWP_E_INVALID_SIZE = -3,
    ORWP_E_INVALID_ROW_PITCH = -4,
    ORWP_E_BUFFER_TOO_SMALL = -5,
    ORWP_E_CONTRACT_MODE_MISMATCH = -6,
    ORWP_E_CONTRACT_DISPLAY_MISMATCH = -7,
    ORWP_E_STALE_REVISION = -8,
    ORWP_E_WINDOW = -9,
    ORWP_E_D3D = -10,
    ORWP_E_COLOR_SPACE = -11,
    ORWP_E_DEVICE_REMOVED = -12,
    ORWP_E_NOT_INITIALIZED = -13,
    ORWP_E_INTERNAL = -14
} OrwpResult;

/*
 * A frame carries its own contract and the caller supplies the environment's
 * current contract separately. orwp_present compares both before touching the
 * upload texture. display_id_utf8 is borrowed for the duration of the call.
 */
typedef struct OrwpPresentationContract
{
    uint32_t struct_size;
    uint32_t mode;
    const char* display_id_utf8;
    uint64_t revision;
} OrwpPresentationContract;

/*
 * POD diagnostics for P/Invoke and native tests. HRESULT fields are signed
 * 32-bit values. The caller must set struct_size before querying.
 */
typedef struct OrwpDiagnostics
{
    uint32_t struct_size;
    uint32_t abi_version;
    int32_t last_result;
    uint32_t mode;

    uint32_t width;
    uint32_t height;
    uint32_t dxgi_format;
    uint32_t dxgi_color_space;
    uint32_t color_space_support;
    uint32_t color_space_was_set;
    uint32_t feature_level;
    uint32_t using_warp;

    int32_t create_device_hr;
    int32_t create_swap_chain_hr;
    int32_t check_color_space_hr;
    int32_t set_color_space_hr;
    int32_t resize_buffers_hr;
    int32_t map_hr;
    int32_t present_hr;
    int32_t device_removed_reason;

    uint32_t adapter_luid_low;
    int32_t adapter_luid_high;
    uint64_t child_hwnd;
    uint64_t successful_present_count;
    uint64_t rejected_present_count;
    uint64_t last_contract_revision;
    uint32_t display_id_was_truncated;
    char last_display_id_utf8[ORWP_DIAGNOSTIC_DISPLAY_ID_CAPACITY];
} OrwpDiagnostics;

typedef struct OrwpPresenter OrwpPresenter;

ORWP_API int32_t ORWP_CALL orwp_create(
    void* parent_hwnd,
    uint32_t mode,
    uint32_t width,
    uint32_t height,
    OrwpPresenter** out_presenter);

ORWP_API int32_t ORWP_CALL orwp_resize(
    OrwpPresenter* presenter,
    uint32_t width,
    uint32_t height);

ORWP_API int32_t ORWP_CALL orwp_present(
    OrwpPresenter* presenter,
    const void* bytes,
    size_t byte_count,
    uint32_t row_pitch,
    uint32_t width,
    uint32_t height,
    const OrwpPresentationContract* frame_contract,
    const OrwpPresentationContract* current_contract);

ORWP_API int32_t ORWP_CALL orwp_query_diagnostics(
    OrwpPresenter* presenter,
    OrwpDiagnostics* diagnostics);

ORWP_API void ORWP_CALL orwp_destroy(OrwpPresenter* presenter);

#ifdef __cplusplus
}
#endif
