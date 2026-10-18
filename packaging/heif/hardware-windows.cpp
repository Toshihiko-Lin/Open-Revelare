#define NOMINMAX
#include "hardware.h"
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <codecapi.h>
#include <strmif.h>
#include <wrl/client.h>
#include <algorithm>
#include <chrono>
#include <cstring>
#include <string>

using Microsoft::WRL::ComPtr;
namespace or_hevc {
namespace {
void check(HRESULT hr, const char* operation) {
    if (FAILED(hr)) throw std::runtime_error(std::string(operation) + " (HRESULT " + std::to_string(uint32_t(hr)) + ")");
}
struct MediaFoundationLibrary {
    HMODULE handle = nullptr;
    MediaFoundationLibrary() {
        handle = LoadLibraryExW(L"mfplat.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!handle) check(HRESULT_FROM_WIN32(GetLastError()), "Media Foundation is unavailable");
    }
    ~MediaFoundationLibrary() { if (handle) FreeLibrary(handle); }
};
struct Runtime {
    // mfplat is delay-loaded: Windows N without Media Feature Pack must still
    // load this HEIC DLL and use x265, without a missing-DLL loader failure.
    MediaFoundationLibrary library;
    bool com = false, mf = false;
    Runtime() {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        com = SUCCEEDED(hr);
        if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) check(hr, "Initialize HEVC COM");
        hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
        if (FAILED(hr)) { if (com) CoUninitialize(); check(hr, "Initialize Media Foundation"); }
        mf = true;
    }
    ~Runtime() { if (mf) MFShutdown(); if (com) CoUninitialize(); }
};
struct Activations {
    IMFActivate** values = nullptr;
    UINT32 count = 0;
    ~Activations() { for (UINT32 i = 0; i < count; ++i) values[i]->Release(); CoTaskMemFree(values); }
};
class WindowsEncoder final : public HardwareEncoder {
    Runtime runtime;
    ComPtr<IMFActivate> activation;
    ComPtr<IMFTransform> transform;
    ComPtr<IMFMediaEventGenerator> events;
    ComPtr<ICodecAPI> codec;
    int width, height;
    bool asynchronous = false;
    unsigned input_credits = 0;
    LONGLONG frame = 0;
    Packets headers;

    void ui4(const GUID& key, ULONG value) {
        VARIANT v; VariantInit(&v); v.vt = VT_UI4; v.ulVal = value;
        check(codec->SetValue(&key, &v), "Configure hardware HEVC");
    }
    void boolean(const GUID& key, bool value) {
        VARIANT v; VariantInit(&v); v.vt = VT_BOOL; v.boolVal = value ? VARIANT_TRUE : VARIANT_FALSE;
        check(codec->SetValue(&key, &v), "Configure low-delay HEVC");
    }
    ComPtr<IMFMediaType> media_type(const GUID& subtype, Color c) {
        ComPtr<IMFMediaType> type;
        check(MFCreateMediaType(&type), "Create HEVC media type");
        check(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video), "Set video type");
        check(type->SetGUID(MF_MT_SUBTYPE, subtype), "Set HEVC subtype");
        check(MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, width, height), "Set HEVC dimensions");
        check(MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, 30, 1), "Set HEVC rate");
        check(MFSetAttributeRatio(type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1), "Set HEVC aspect");
        check(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive), "Set progressive HEVC");
        check(type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, c.full_range ? MFNominalRange_0_255 : MFNominalRange_16_235), "Set HEVC range");
        check(type->SetUINT32(MF_MT_VIDEO_PRIMARIES, c.primaries == 9 ? MFVideoPrimaries_BT2020 : MFVideoPrimaries_Unknown), "Set HEVC primaries");
        check(type->SetUINT32(MF_MT_TRANSFER_FUNCTION, c.transfer == 16 ? MFVideoTransFunc_2084 : MFVideoTransFunc_Unknown), "Set HEVC transfer");
        check(type->SetUINT32(MF_MT_YUV_MATRIX, c.matrix == 9 ? MFVideoTransferMatrix_BT2020_10 : MFVideoTransferMatrix_BT709), "Set HEVC matrix");
        return type;
    }
    MediaEventType next_event(std::chrono::steady_clock::time_point deadline) {
        while (std::chrono::steady_clock::now() < deadline) {
            ComPtr<IMFMediaEvent> event;
            HRESULT hr = events->GetEvent(MF_EVENT_FLAG_NO_WAIT, &event);
            if (hr == MF_E_NO_EVENTS_AVAILABLE) { Sleep(1); continue; }
            check(hr, "Read HEVC hardware event");
            HRESULT status = S_OK;
            check(event->GetStatus(&status), "Read HEVC event status");
            check(status, "HEVC hardware event failed");
            MediaEventType type = MEUnknown;
            check(event->GetType(&type), "Read HEVC event type");
            return type;
        }
        throw std::runtime_error("Hardware HEVC timed out; retrying with software encoding");
    }
    Packets output() {
        MFT_OUTPUT_STREAM_INFO info{};
        check(transform->GetOutputStreamInfo(0, &info), "Query HEVC output buffer");
        ComPtr<IMFSample> sample;
        if (!(info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES)) {
            check(MFCreateSample(&sample), "Create HEVC output sample");
            ComPtr<IMFMediaBuffer> buffer;
            DWORD size = std::max(info.cbSize, DWORD(size_t(width) * height * 6 + 65536));
            check(MFCreateAlignedMemoryBuffer(size, info.cbAlignment ? info.cbAlignment - 1 : 0, &buffer), "Create HEVC output buffer");
            check(sample->AddBuffer(buffer.Get()), "Attach HEVC output buffer");
        }
        MFT_OUTPUT_DATA_BUFFER output{};
        output.pSample = sample.Get();
        DWORD status = 0;
        HRESULT hr = transform->ProcessOutput(0, 1, &output, &status);
        if (output.pEvents) output.pEvents->Release();
        // When the transform supplies the sample, ownership passes to the caller.
        if (!sample && output.pSample) sample.Attach(output.pSample);
        check(hr, "Receive HEVC encoded tile");
        if (!sample) throw std::runtime_error("Hardware HEVC returned no sample");
        ComPtr<IMFMediaBuffer> buffer;
        check(sample->ConvertToContiguousBuffer(&buffer), "Read HEVC output");
        BYTE* bytes = nullptr; DWORD length = 0;
        check(buffer->Lock(&bytes, nullptr, &length), "Lock HEVC bitstream");
        Packets packets;
        try { packets = annex_b(bytes, length); }
        catch (...) { buffer->Unlock(); throw; }
        buffer->Unlock();
        bool has_headers = false;
        for (const auto& nal : packets) if (nal.size() >= 2 && ((nal[0] >> 1) & 63) == 32) has_headers = true;
        if (has_headers) {
            headers.clear();
            for (const auto& nal : packets) {
                int type = (nal[0] >> 1) & 63;
                if (type >= 32 && type <= 34) headers.push_back(nal);
            }
        } else {
            if (headers.empty()) {
                ComPtr<IMFMediaType> type;
                check(transform->GetOutputCurrentType(0, &type), "Read HEVC parameter sets");
                UINT32 size = 0;
                check(type->GetBlobSize(MF_MT_MPEG_SEQUENCE_HEADER, &size), "Read HEVC headers");
                std::vector<uint8_t> data(size);
                check(type->GetBlob(MF_MT_MPEG_SEQUENCE_HEADER, data.data(), size, nullptr), "Read HEVC sequence header");
                headers = annex_b(data.data(), data.size());
            }
            packets.insert(packets.begin(), headers.begin(), headers.end());
        }
        return packets;
    }
public:
    WindowsEncoder(IMFActivate* selected, int w, int h, int quality, Color color) : activation(selected), width(w), height(h) {
        check(activation->ActivateObject(IID_PPV_ARGS(&transform)), "Activate hardware HEVC encoder");
        ComPtr<IMFAttributes> attrs;
        check(transform->GetAttributes(&attrs), "Query HEVC capabilities");
        UINT32 async = FALSE;
        attrs->GetUINT32(MF_TRANSFORM_ASYNC, &async);
        asynchronous = async != FALSE;
        if (asynchronous) {
            check(attrs->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE), "Unlock hardware HEVC encoder");
            check(transform.As(&events), "Query HEVC event queue");
        }
        check(transform.As(&codec), "Query HEVC quality controls");
        // Refuse a device that cannot honor quality control, rather than quietly
        // replacing the user's quality with a low video bitrate preset.
        auto configure_photo = [&] {
            // Set these both before and after negotiation: drivers may snapshot or
            // reset quality controls while setting media types. CBR with matching
            // QP bounds avoids the driver's much coarser video-quality scale.
            // QP 0 means "automatic" in some hardware drivers, so retain QP >= 1.
            ULONG q = ULONG(std::clamp((100 - quality) / 2 - 4, 1, 51));
            ui4(CODECAPI_AVEncCommonRateControlMode, eAVEncCommonRateControlMode_CBR);
            ui4(CODECAPI_AVEncVideoMinQP, q);
            ui4(CODECAPI_AVEncVideoMaxQP, q);
            VARIANT qp; VariantInit(&qp); qp.vt = VT_UI8;
            ULONGLONG q64 = q;
            qp.ullVal = q64 | (q64 << 16) | (q64 << 32) | (q64 << 48);
            HRESULT qp_status = codec->SetValue(&CODECAPI_AVEncVideoEncodeQP, &qp);
            if (qp_status == E_INVALIDARG) {
                // Some HEVC MFTs use UI4 rather than the generic UI8 QP contract.
                qp.vt = VT_UI4; qp.ulVal = q;
                qp_status = codec->SetValue(&CODECAPI_AVEncVideoEncodeQP, &qp);
            }
            check(qp_status, "Set still-image HEVC quantizer");
        };
        configure_photo();
        ui4(CODECAPI_AVEncCommonRateControlMode, eAVEncCommonRateControlMode_Quality);
        ui4(CODECAPI_AVEncMPVGOPSize, 2);
        if (codec->IsSupported(&CODECAPI_AVEncMPVDefaultBPictureCount) == S_OK)
            ui4(CODECAPI_AVEncMPVDefaultBPictureCount, 0);
        if (codec->IsSupported(&CODECAPI_AVLowLatencyMode) == S_OK) boolean(CODECAPI_AVLowLatencyMode, true);
        auto out = media_type(MFVideoFormat_HEVC, color);
        check(out->SetUINT32(MF_MT_AVG_BITRATE, 20000000), "Set HEVC bitrate ceiling");
        check(out->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH265VProfile_Main_420_10), "Select Main10");
        check(out->SetUINT32(MF_MT_MPEG2_LEVEL, eAVEncH265VLevel4), "Select bounded tile level");
        check(transform->SetOutputType(0, out.Get(), 0), "Set Main10 hardware output");
        auto in = media_type(MFVideoFormat_P010, color);
        check(in->SetUINT32(MF_MT_DEFAULT_STRIDE, width * 2), "Set P010 stride");
        check(transform->SetInputType(0, in.Get(), 0), "Set 10-bit hardware input");
        configure_photo();
        check(transform->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0), "Begin HEVC encoding");
        check(transform->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0), "Start HEVC tiles");
    }
    ~WindowsEncoder() override {
        if (transform) {
            transform->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
            transform->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
        }
        if (activation) activation->ShutdownObject();
    }
    Packets encode(const heif_image* image) override {
        auto pixels = p010(image, width, height);
        ComPtr<IMFMediaBuffer> buffer;
        DWORD bytes = DWORD(pixels.size() * sizeof(uint16_t));
        check(MFCreateMemoryBuffer(bytes, &buffer), "Allocate P010 tile");
        BYTE* destination = nullptr;
        check(buffer->Lock(&destination, nullptr, nullptr), "Lock P010 tile");
        std::memcpy(destination, pixels.data(), bytes);
        buffer->Unlock();
        check(buffer->SetCurrentLength(bytes), "Set P010 tile length");
        ComPtr<IMFSample> sample;
        check(MFCreateSample(&sample), "Create P010 sample");
        check(sample->AddBuffer(buffer.Get()), "Attach P010 tile");
        check(sample->SetSampleTime(frame * 333333), "Timestamp HEVC tile");
        check(sample->SetSampleDuration(333333), "Set HEVC tile duration");
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
        if (asynchronous) {
            while (!input_credits) {
                auto event = next_event(deadline);
                if (event == METransformNeedInput) ++input_credits;
                else if (event == METransformHaveOutput) throw std::runtime_error("Unexpected queued HEVC output");
            }
            --input_credits;
        }
        ui4(CODECAPI_AVEncVideoForceKeyFrame, 1);
        check(transform->ProcessInput(0, sample.Get(), 0), "Submit hardware HEVC tile");
        ++frame;
        if (asynchronous) {
            for (;;) {
                auto event = next_event(deadline);
                if (event == METransformNeedInput) ++input_credits;
                else if (event == METransformHaveOutput) break;
            }
        }
        return output();
    }
};
}
std::unique_ptr<HardwareEncoder> create_hardware_encoder(int w, int h, int quality, Color color) {
    Runtime runtime;
    Activations list;
    MFT_REGISTER_TYPE_INFO input{MFMediaType_Video, MFVideoFormat_P010}, output{MFMediaType_Video, MFVideoFormat_HEVC};
    check(MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
        &input, &output, &list.values, &list.count), "Enumerate hardware Main10 encoders");
    std::string reason = "No hardware Main10 HEVC encoder available";
    for (UINT32 i = 0; i < list.count; ++i) {
        try { return std::make_unique<WindowsEncoder>(list.values[i], w, h, quality, color); }
        catch (const std::exception& ex) { reason = ex.what(); list.values[i]->ShutdownObject(); }
    }
    throw std::runtime_error(reason);
}
}

