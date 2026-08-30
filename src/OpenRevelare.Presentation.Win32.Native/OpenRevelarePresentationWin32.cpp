#include <windows.h>

#include <d3d11.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

#include <algorithm>
#include <cstring>
#include <iterator>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <string_view>

#include "OpenRevelarePresentationWin32.h"

using Microsoft::WRL::ComPtr;

static_assert(sizeof(void*) == 8, "This shim has an x64-only ABI.");
static_assert(sizeof(HRESULT) == sizeof(int32_t), "HRESULT diagnostics require 32-bit HRESULT.");

namespace
{
constexpr wchar_t kWindowClassName[] =
    L"OpenRevelare.Presentation.Win32.Native.Presenter.v1";
constexpr size_t kMaximumDisplayIdBytes = 1024;

HINSTANCE g_module = nullptr;
std::once_flag g_window_class_once;
ATOM g_window_class_atom = 0;

int32_t HResultValue(HRESULT hr) noexcept
{
    return static_cast<int32_t>(hr);
}

bool IsValidMode(uint32_t mode) noexcept
{
    return mode == ORWP_MODE_ADVANCED_COLOR || mode == ORWP_MODE_LEGACY;
}

bool IsValidDimension(uint32_t value) noexcept
{
    return value != 0 && value <= static_cast<uint32_t>((std::numeric_limits<int>::max)());
}

LRESULT CALLBACK PresenterWindowProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam)
{
    switch (message)
    {
    case WM_NCHITTEST:
        return HTTRANSPARENT;
    case WM_ERASEBKGND:
        return 1;
    case WM_PAINT:
    {
        PAINTSTRUCT paint{};
        BeginPaint(hwnd, &paint);
        EndPaint(hwnd, &paint);
        return 0;
    }
    default:
        return DefWindowProcW(hwnd, message, wparam, lparam);
    }
}

bool EnsureWindowClass()
{
    std::call_once(g_window_class_once, []
    {
        WNDCLASSEXW window_class{};
        window_class.cbSize = sizeof(window_class);
        window_class.style = CS_HREDRAW | CS_VREDRAW;
        window_class.lpfnWndProc = PresenterWindowProc;
        window_class.hInstance = g_module != nullptr ? g_module : GetModuleHandleW(nullptr);
        window_class.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        window_class.lpszClassName = kWindowClassName;
        g_window_class_atom = RegisterClassExW(&window_class);
        if (g_window_class_atom == 0 && GetLastError() == ERROR_CLASS_ALREADY_EXISTS)
        {
            g_window_class_atom = 1;
        }
    });

    return g_window_class_atom != 0;
}

bool ReadDisplayId(const char* value, std::string_view& result) noexcept
{
    if (value == nullptr)
    {
        return false;
    }

    size_t length = 0;
    while (length < kMaximumDisplayIdBytes && value[length] != '\0')
    {
        ++length;
    }

    if (length == 0 || length == kMaximumDisplayIdBytes)
    {
        return false;
    }

    result = std::string_view(value, length);
    return true;
}

HRESULT FindHardwareAdapterForMonitor(HMONITOR monitor, ComPtr<IDXGIAdapter1>& result)
{
    result.Reset();

    ComPtr<IDXGIFactory2> factory;
    HRESULT hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory));
    if (FAILED(hr))
    {
        return hr;
    }

    for (UINT adapter_index = 0;; ++adapter_index)
    {
        ComPtr<IDXGIAdapter1> adapter;
        hr = factory->EnumAdapters1(adapter_index, &adapter);
        if (hr == DXGI_ERROR_NOT_FOUND)
        {
            break;
        }
        if (FAILED(hr))
        {
            return hr;
        }

        DXGI_ADAPTER_DESC1 adapter_desc{};
        if (FAILED(adapter->GetDesc1(&adapter_desc)) ||
            (adapter_desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0)
        {
            continue;
        }

        for (UINT output_index = 0;; ++output_index)
        {
            ComPtr<IDXGIOutput> output;
            hr = adapter->EnumOutputs(output_index, &output);
            if (hr == DXGI_ERROR_NOT_FOUND)
            {
                break;
            }
            if (FAILED(hr))
            {
                return hr;
            }

            DXGI_OUTPUT_DESC output_desc{};
            if (SUCCEEDED(output->GetDesc(&output_desc)) && output_desc.Monitor == monitor)
            {
                result = adapter;
                return S_OK;
            }
        }
    }

    return DXGI_ERROR_NOT_FOUND;
}
} // namespace

struct OrwpPresenter
{
    explicit OrwpPresenter(uint32_t mode) noexcept
        : mode_(mode)
    {
        std::memset(&diagnostics_, 0, sizeof(diagnostics_));
        diagnostics_.struct_size = sizeof(diagnostics_);
        diagnostics_.abi_version = ORWP_ABI_VERSION;
        diagnostics_.last_result = ORWP_E_NOT_INITIALIZED;
        diagnostics_.mode = mode;
        diagnostics_.dxgi_format = static_cast<uint32_t>(Format());
        diagnostics_.dxgi_color_space = static_cast<uint32_t>(ColorSpace());
        diagnostics_.create_device_hr = HResultValue(S_FALSE);
        diagnostics_.create_swap_chain_hr = HResultValue(S_FALSE);
        diagnostics_.check_color_space_hr = HResultValue(S_FALSE);
        diagnostics_.set_color_space_hr = HResultValue(S_FALSE);
        diagnostics_.resize_buffers_hr = HResultValue(S_FALSE);
        diagnostics_.map_hr = HResultValue(S_FALSE);
        diagnostics_.present_hr = HResultValue(S_FALSE);
        diagnostics_.device_removed_reason = HResultValue(S_OK);
    }

    ~OrwpPresenter() noexcept
    {
        std::lock_guard<std::mutex> guard(mutex_);
        ResetGraphics();
        if (child_ != nullptr)
        {
            DestroyWindow(child_);
            child_ = nullptr;
        }
    }

    int32_t Initialize(HWND parent, uint32_t width, uint32_t height)
    {
        std::lock_guard<std::mutex> guard(mutex_);

        if (parent == nullptr || !IsWindow(parent))
        {
            return SetResult(ORWP_E_INVALID_ARGUMENT);
        }
        if (!IsValidMode(mode_))
        {
            return SetResult(ORWP_E_INVALID_MODE);
        }
        if (!IsValidDimension(width) || !IsValidDimension(height))
        {
            return SetResult(ORWP_E_INVALID_SIZE);
        }
        if (!EnsureWindowClass())
        {
            return SetResult(ORWP_E_WINDOW);
        }

        parent_ = parent;
        child_ = CreateWindowExW(
            0,
            kWindowClassName,
            L"",
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0,
            0,
            static_cast<int>(width),
            static_cast<int>(height),
            parent_,
            nullptr,
            g_module != nullptr ? g_module : GetModuleHandleW(nullptr),
            nullptr);
        if (child_ == nullptr)
        {
            return SetResult(ORWP_E_WINDOW);
        }
        diagnostics_.child_hwnd = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(child_));

        HRESULT hr = CreateDevice();
        diagnostics_.create_device_hr = HResultValue(hr);
        if (FAILED(hr))
        {
            return SetResult(DeviceFailureResult(hr));
        }

        hr = CreateSwapChain(width, height);
        if (FAILED(hr))
        {
            return SetResult(DeviceFailureResult(hr));
        }

        width_ = width;
        height_ = height;
        diagnostics_.width = width;
        diagnostics_.height = height;
        initialized_ = true;
        return SetResult(ORWP_OK);
    }

    int32_t Resize(uint32_t width, uint32_t height)
    {
        std::lock_guard<std::mutex> guard(mutex_);

        if (!IsValidDimension(width) || !IsValidDimension(height))
        {
            return SetResult(ORWP_E_INVALID_SIZE);
        }
        if (!initialized_ || swap_chain_ == nullptr)
        {
            return SetResult(ORWP_E_NOT_INITIALIZED);
        }
        if (width == width_ && height == height_)
        {
            return SetResult(ResizeChildWindow(width, height));
        }

        const uint32_t old_width = width_;
        const uint32_t old_height = height_;
        staging_.Reset();
        back_buffer_.Reset();

        HRESULT hr = swap_chain_->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, 0);
        diagnostics_.resize_buffers_hr = HResultValue(hr);
        if (FAILED(hr))
        {
            AcquireBuffers(old_width, old_height);
            return SetResult(DeviceFailureResult(hr));
        }

        hr = AcquireBuffers(width, height);
        if (FAILED(hr))
        {
            initialized_ = false;
            return SetResult(DeviceFailureResult(hr));
        }

        width_ = width;
        height_ = height;
        diagnostics_.width = width;
        diagnostics_.height = height;

        return SetResult(ResizeChildWindow(width, height));
    }

    int32_t Present(
        const void* bytes,
        size_t byte_count,
        uint32_t row_pitch,
        uint32_t width,
        uint32_t height,
        const OrwpPresentationContract* frame_contract,
        const OrwpPresentationContract* current_contract)
    {
        std::lock_guard<std::mutex> guard(mutex_);

        int32_t validation = ValidatePresentation(
            bytes,
            byte_count,
            row_pitch,
            width,
            height,
            frame_contract,
            current_contract);
        if (validation != ORWP_OK)
        {
            ++diagnostics_.rejected_present_count;
            return SetResult(validation);
        }

        std::string_view validated_display_id;
        if (!ReadDisplayId(frame_contract->display_id_utf8, validated_display_id))
        {
            ++diagnostics_.rejected_present_count;
            return SetResult(ORWP_E_INVALID_ARGUMENT);
        }
        // Allocate/copy borrowed contract data before Map. After this point the
        // upload path cannot discover a new contract-validation failure.
        std::string accepted_display_id(validated_display_id);

        const uint32_t tight_row_bytes = TightRowBytes(width);
        D3D11_MAPPED_SUBRESOURCE mapped{};
        HRESULT hr = context_->Map(staging_.Get(), 0, D3D11_MAP_WRITE, 0, &mapped);
        diagnostics_.map_hr = HResultValue(hr);
        if (FAILED(hr))
        {
            return SetResult(DeviceFailureResult(hr));
        }

        const auto* source = static_cast<const uint8_t*>(bytes);
        auto* destination = static_cast<uint8_t*>(mapped.pData);
        for (uint32_t y = 0; y < height; ++y)
        {
            std::memcpy(
                destination + static_cast<size_t>(y) * mapped.RowPitch,
                source + static_cast<size_t>(y) * row_pitch,
                tight_row_bytes);
        }
        context_->Unmap(staging_.Get(), 0);

        // Fixed, same-format copy only. There is deliberately no shader, render
        // target view, transfer function, gamut map, matrix, or ICC operation.
        context_->CopyResource(back_buffer_.Get(), staging_.Get());

        hr = swap_chain_->Present(1, 0);
        diagnostics_.present_hr = HResultValue(hr);
        if (FAILED(hr))
        {
            return SetResult(DeviceFailureResult(hr));
        }

        bound_display_id_.swap(accepted_display_id);
        bound_revision_ = frame_contract->revision;
        diagnostics_.last_contract_revision = bound_revision_;
        StoreDiagnosticDisplayId(
            std::string_view(bound_display_id_.data(), bound_display_id_.size()));
        ++diagnostics_.successful_present_count;
        return SetResult(ORWP_OK);
    }

    int32_t QueryDiagnostics(OrwpDiagnostics* output)
    {
        if (output == nullptr || output->struct_size < sizeof(OrwpDiagnostics))
        {
            return ORWP_E_INVALID_ARGUMENT;
        }

        std::lock_guard<std::mutex> guard(mutex_);
        diagnostics_.child_hwnd = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(child_));
        *output = diagnostics_;
        return ORWP_OK;
    }

private:
    DXGI_FORMAT Format() const noexcept
    {
        return mode_ == ORWP_MODE_ADVANCED_COLOR
            ? DXGI_FORMAT_R16G16B16A16_FLOAT
            : DXGI_FORMAT_B8G8R8A8_UNORM;
    }

    DXGI_COLOR_SPACE_TYPE ColorSpace() const noexcept
    {
        return mode_ == ORWP_MODE_ADVANCED_COLOR
            ? DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709
            : DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
    }

    uint32_t BytesPerPixel() const noexcept
    {
        return mode_ == ORWP_MODE_ADVANCED_COLOR ? 8u : 4u;
    }

    uint32_t TightRowBytes(uint32_t width) const noexcept
    {
        return width * BytesPerPixel();
    }

    int32_t ValidatePresentation(
        const void* bytes,
        size_t byte_count,
        uint32_t row_pitch,
        uint32_t width,
        uint32_t height,
        const OrwpPresentationContract* frame_contract,
        const OrwpPresentationContract* current_contract) const noexcept
    {
        if (!initialized_ || context_ == nullptr || staging_ == nullptr ||
            back_buffer_ == nullptr || swap_chain_ == nullptr)
        {
            return ORWP_E_NOT_INITIALIZED;
        }
        if (bytes == nullptr || frame_contract == nullptr || current_contract == nullptr)
        {
            return ORWP_E_INVALID_ARGUMENT;
        }
        if (frame_contract->struct_size < sizeof(OrwpPresentationContract) ||
            current_contract->struct_size < sizeof(OrwpPresentationContract))
        {
            return ORWP_E_INVALID_ARGUMENT;
        }
        if (!IsValidMode(frame_contract->mode) || !IsValidMode(current_contract->mode) ||
            frame_contract->mode != mode_ || current_contract->mode != mode_)
        {
            return ORWP_E_CONTRACT_MODE_MISMATCH;
        }

        std::string_view frame_display_id;
        std::string_view current_display_id;
        if (!ReadDisplayId(frame_contract->display_id_utf8, frame_display_id) ||
            !ReadDisplayId(current_contract->display_id_utf8, current_display_id))
        {
            return ORWP_E_INVALID_ARGUMENT;
        }
        if (frame_display_id != current_display_id ||
            (!bound_display_id_.empty() && frame_display_id != bound_display_id_))
        {
            return ORWP_E_CONTRACT_DISPLAY_MISMATCH;
        }
        if (frame_contract->revision != current_contract->revision ||
            frame_contract->revision < bound_revision_)
        {
            return ORWP_E_STALE_REVISION;
        }
        if (!IsValidDimension(width) || !IsValidDimension(height) ||
            width != width_ || height != height_)
        {
            return ORWP_E_INVALID_SIZE;
        }

        const uint64_t tight_row_bytes =
            static_cast<uint64_t>(width) * static_cast<uint64_t>(BytesPerPixel());
        if (tight_row_bytes > (std::numeric_limits<uint32_t>::max)() ||
            row_pitch < tight_row_bytes)
        {
            return ORWP_E_INVALID_ROW_PITCH;
        }

        const uint64_t required_bytes =
            static_cast<uint64_t>(height - 1) * static_cast<uint64_t>(row_pitch) +
            tight_row_bytes;
        if (required_bytes > static_cast<uint64_t>((std::numeric_limits<size_t>::max)()) ||
            byte_count < static_cast<size_t>(required_bytes))
        {
            return ORWP_E_BUFFER_TOO_SMALL;
        }

        return ORWP_OK;
    }

    HRESULT CreateDevice()
    {
        ComPtr<IDXGIAdapter1> selected_adapter;
        HMONITOR monitor = MonitorFromWindow(parent_, MONITOR_DEFAULTTONEAREST);
        HRESULT adapter_hr = monitor == nullptr
            ? HRESULT_FROM_WIN32(ERROR_NOT_FOUND)
            : FindHardwareAdapterForMonitor(monitor, selected_adapter);

        static constexpr D3D_FEATURE_LEVEL feature_levels[] =
        {
            D3D_FEATURE_LEVEL_11_1,
            D3D_FEATURE_LEVEL_11_0,
            D3D_FEATURE_LEVEL_10_1,
            D3D_FEATURE_LEVEL_10_0
        };

        D3D_FEATURE_LEVEL selected_feature_level = D3D_FEATURE_LEVEL_10_0;
        HRESULT hr = selected_adapter == nullptr
            ? (FAILED(adapter_hr) ? adapter_hr : DXGI_ERROR_NOT_FOUND)
            : D3D11CreateDevice(
                selected_adapter.Get(),
                D3D_DRIVER_TYPE_UNKNOWN,
                nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                feature_levels,
                static_cast<UINT>(std::size(feature_levels)),
                D3D11_SDK_VERSION,
                &device_,
                &selected_feature_level,
                &context_);

        if (FAILED(hr))
        {
            // WARP is an explicit software-rendering fallback, not a silent selection of a
            // different physical output adapter. The display contract still binds every frame
            // to the probed monitor id/revision and cross-screen changes recreate this device.
            device_.Reset();
            context_.Reset();
            hr = D3D11CreateDevice(
                nullptr,
                D3D_DRIVER_TYPE_WARP,
                nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                feature_levels,
                static_cast<UINT>(std::size(feature_levels)),
                D3D11_SDK_VERSION,
                &device_,
                &selected_feature_level,
                &context_);
            diagnostics_.using_warp = SUCCEEDED(hr) ? 1u : 0u;
        }
        diagnostics_.feature_level = static_cast<uint32_t>(selected_feature_level);
        if (FAILED(hr))
        {
            return hr;
        }

        ComPtr<IDXGIDevice> dxgi_device;
        hr = device_.As(&dxgi_device);
        if (FAILED(hr))
        {
            return hr;
        }

        ComPtr<IDXGIAdapter> actual_adapter;
        hr = dxgi_device->GetAdapter(&actual_adapter);
        if (FAILED(hr))
        {
            return hr;
        }

        ComPtr<IDXGIAdapter1> actual_adapter1;
        if (SUCCEEDED(actual_adapter.As(&actual_adapter1)))
        {
            DXGI_ADAPTER_DESC1 desc{};
            if (SUCCEEDED(actual_adapter1->GetDesc1(&desc)))
            {
                diagnostics_.adapter_luid_low = desc.AdapterLuid.LowPart;
                diagnostics_.adapter_luid_high = desc.AdapterLuid.HighPart;
            }
        }

        return actual_adapter->GetParent(IID_PPV_ARGS(&factory_));
    }

    int32_t ResizeChildWindow(uint32_t width, uint32_t height) noexcept
    {
        if (!SetWindowPos(
                child_,
                nullptr,
                0,
                0,
                static_cast<int>(width),
                static_cast<int>(height),
                SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE))
        {
            return ORWP_E_WINDOW;
        }
        return ORWP_OK;
    }

    HRESULT CreateSwapChain(uint32_t width, uint32_t height)
    {
        color_space_failure_ = false;
        DXGI_SWAP_CHAIN_DESC1 description{};
        description.Width = width;
        description.Height = height;
        description.Format = Format();
        description.Stereo = FALSE;
        description.SampleDesc.Count = 1;
        description.SampleDesc.Quality = 0;
        description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        description.BufferCount = 2;
        description.Scaling = DXGI_SCALING_STRETCH;
        description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        description.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        description.Flags = 0;

        ComPtr<IDXGISwapChain1> swap_chain1;
        HRESULT hr = factory_->CreateSwapChainForHwnd(
            device_.Get(), child_, &description, nullptr, nullptr, &swap_chain1);
        diagnostics_.create_swap_chain_hr = HResultValue(hr);
        if (FAILED(hr))
        {
            return hr;
        }

        factory_->MakeWindowAssociation(child_, DXGI_MWA_NO_ALT_ENTER);
        hr = swap_chain1.As(&swap_chain_);
        if (FAILED(hr))
        {
            return hr;
        }

        if (mode_ == ORWP_MODE_ADVANCED_COLOR)
        {
            UINT support = 0;
            hr = swap_chain_->CheckColorSpaceSupport(ColorSpace(), &support);
            diagnostics_.check_color_space_hr = HResultValue(hr);
            diagnostics_.color_space_support = support;
            if (FAILED(hr) ||
                (support & DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT) == 0)
            {
                color_space_failure_ = true;
                return FAILED(hr) ? hr : DXGI_ERROR_UNSUPPORTED;
            }

            hr = swap_chain_->SetColorSpace1(ColorSpace());
            diagnostics_.set_color_space_hr = HResultValue(hr);
            if (FAILED(hr))
            {
                color_space_failure_ = true;
                return hr;
            }
            diagnostics_.color_space_was_set = 1;
        }

        return AcquireBuffers(width, height);
    }

    HRESULT AcquireBuffers(uint32_t width, uint32_t height)
    {
        back_buffer_.Reset();
        staging_.Reset();

        HRESULT hr = swap_chain_->GetBuffer(0, IID_PPV_ARGS(&back_buffer_));
        if (FAILED(hr))
        {
            return hr;
        }

        D3D11_TEXTURE2D_DESC staging_description{};
        staging_description.Width = width;
        staging_description.Height = height;
        staging_description.MipLevels = 1;
        staging_description.ArraySize = 1;
        staging_description.Format = Format();
        staging_description.SampleDesc.Count = 1;
        staging_description.SampleDesc.Quality = 0;
        staging_description.Usage = D3D11_USAGE_STAGING;
        staging_description.BindFlags = 0;
        staging_description.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        staging_description.MiscFlags = 0;
        return device_->CreateTexture2D(&staging_description, nullptr, &staging_);
    }

    int32_t DeviceFailureResult(HRESULT hr) noexcept
    {
        if (hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET ||
            hr == DXGI_ERROR_DEVICE_HUNG)
        {
            if (device_ != nullptr)
            {
                diagnostics_.device_removed_reason =
                    HResultValue(device_->GetDeviceRemovedReason());
            }
            return ORWP_E_DEVICE_REMOVED;
        }

        if (color_space_failure_)
        {
            return ORWP_E_COLOR_SPACE;
        }

        return ORWP_E_D3D;
    }

    void StoreDiagnosticDisplayId(std::string_view display_id) noexcept
    {
        std::memset(
            diagnostics_.last_display_id_utf8,
            0,
            sizeof(diagnostics_.last_display_id_utf8));
        const size_t maximum = sizeof(diagnostics_.last_display_id_utf8) - 1;
        const size_t copy_length = (std::min)(maximum, display_id.size());
        std::memcpy(
            diagnostics_.last_display_id_utf8,
            display_id.data(),
            copy_length);
        diagnostics_.display_id_was_truncated = display_id.size() > copy_length ? 1u : 0u;
    }

    int32_t SetResult(int32_t result) noexcept
    {
        diagnostics_.last_result = result;
        return result;
    }

    void ResetGraphics() noexcept
    {
        if (context_ != nullptr)
        {
            context_->ClearState();
            context_->Flush();
        }
        staging_.Reset();
        back_buffer_.Reset();
        swap_chain_.Reset();
        factory_.Reset();
        context_.Reset();
        device_.Reset();
        initialized_ = false;
    }

    std::mutex mutex_;
    uint32_t mode_ = ORWP_MODE_INVALID;
    HWND parent_ = nullptr;
    HWND child_ = nullptr;
    uint32_t width_ = 0;
    uint32_t height_ = 0;
    bool initialized_ = false;
    bool color_space_failure_ = false;
    std::string bound_display_id_;
    uint64_t bound_revision_ = 0;

    ComPtr<ID3D11Device> device_;
    ComPtr<ID3D11DeviceContext> context_;
    ComPtr<IDXGIFactory2> factory_;
    ComPtr<IDXGISwapChain3> swap_chain_;
    ComPtr<ID3D11Texture2D> back_buffer_;
    ComPtr<ID3D11Texture2D> staging_;
    OrwpDiagnostics diagnostics_{};
};

extern "C" BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

extern "C" ORWP_API int32_t ORWP_CALL orwp_create(
    void* parent_hwnd,
    uint32_t mode,
    uint32_t width,
    uint32_t height,
    OrwpPresenter** out_presenter)
{
    if (out_presenter == nullptr)
    {
        return ORWP_E_INVALID_ARGUMENT;
    }
    *out_presenter = nullptr;
    if (parent_hwnd == nullptr)
    {
        return ORWP_E_INVALID_ARGUMENT;
    }
    if (!IsValidMode(mode))
    {
        return ORWP_E_INVALID_MODE;
    }

    try
    {
        std::unique_ptr<OrwpPresenter> presenter(
            new (std::nothrow) OrwpPresenter(mode));
        if (presenter == nullptr)
        {
            return ORWP_E_INTERNAL;
        }

        const int32_t result = presenter->Initialize(
            static_cast<HWND>(parent_hwnd), width, height);
        if (result != ORWP_OK)
        {
            return result;
        }

        *out_presenter = presenter.release();
        return ORWP_OK;
    }
    catch (...)
    {
        return ORWP_E_INTERNAL;
    }
}

extern "C" ORWP_API int32_t ORWP_CALL orwp_resize(
    OrwpPresenter* presenter,
    uint32_t width,
    uint32_t height)
{
    if (presenter == nullptr)
    {
        return ORWP_E_INVALID_ARGUMENT;
    }
    try
    {
        return presenter->Resize(width, height);
    }
    catch (...)
    {
        return ORWP_E_INTERNAL;
    }
}

extern "C" ORWP_API int32_t ORWP_CALL orwp_present(
    OrwpPresenter* presenter,
    const void* bytes,
    size_t byte_count,
    uint32_t row_pitch,
    uint32_t width,
    uint32_t height,
    const OrwpPresentationContract* frame_contract,
    const OrwpPresentationContract* current_contract)
{
    if (presenter == nullptr)
    {
        return ORWP_E_INVALID_ARGUMENT;
    }
    try
    {
        return presenter->Present(
            bytes,
            byte_count,
            row_pitch,
            width,
            height,
            frame_contract,
            current_contract);
    }
    catch (...)
    {
        return ORWP_E_INTERNAL;
    }
}

extern "C" ORWP_API int32_t ORWP_CALL orwp_query_diagnostics(
    OrwpPresenter* presenter,
    OrwpDiagnostics* diagnostics)
{
    if (presenter == nullptr)
    {
        return ORWP_E_INVALID_ARGUMENT;
    }
    try
    {
        return presenter->QueryDiagnostics(diagnostics);
    }
    catch (...)
    {
        return ORWP_E_INTERNAL;
    }
}

extern "C" ORWP_API void ORWP_CALL orwp_destroy(OrwpPresenter* presenter)
{
    try
    {
        delete presenter;
    }
    catch (...)
    {
        // C ABI boundary: destruction must never unwind into the caller.
    }
}
