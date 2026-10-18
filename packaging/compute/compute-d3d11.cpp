#define NOMINMAX
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <array>
#include <limits>
#include <memory>
#include "compute.h"

using Microsoft::WRL::ComPtr;
namespace {
void check(HRESULT hr) {
    if (hr == E_OUTOFMEMORY) throw resource_unavailable("DirectCompute allocation failed");
    if (FAILED(hr)) throw std::runtime_error("DirectCompute failed (HRESULT " + std::to_string(hr) + ")");
}
struct Buffer {
    ComPtr<ID3D11Buffer> value;
    ComPtr<ID3D11ShaderResourceView> srv;
    ComPtr<ID3D11UnorderedAccessView> uav;
    size_t bytes = 0;
};
struct Session {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11ComputeShader> resize, horizontal, vertical, film;
    ComPtr<ID3D11Buffer> constants, staging;
    size_t staging_bytes = 0;
    std::array<Buffer,8> buffers;
    ComPtr<IDXGIAdapter3> adapter;

    void clear_buffers() {
        context->ClearState();
        buffers = {};
        staging.Reset();
        staging_bytes = 0;
        context->Flush();
    }
    void prepare(const BufferPlan& required) {
        // Query the process budget each time: other applications may consume VRAM.
        size_t budget = 512ull * 1024 * 1024;
        DXGI_QUERY_VIDEO_MEMORY_INFO info{};
        if (adapter && SUCCEEDED(adapter->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info))) {
            size_t owned = staging_bytes;
            for (const auto& b : buffers) owned += b.bytes;
            size_t other = info.CurrentUsage > owned ? size_t(info.CurrentUsage) - owned : 0;
            size_t available = info.Budget > other ? size_t(info.Budget) - other : 0;
            budget = std::min(size_t(info.Budget / 2), available > 128ull*1024*1024 ? available - 128ull*1024*1024 : size_t(0));
        }
        plan_bytes(required, std::numeric_limits<UINT>::max(), budget);
        BufferPlan retained{};
        for (size_t i = 0; i < buffers.size(); ++i) retained[i] = std::max(required[i], buffers[i].bytes);
        retained[8] = std::max(required[8], staging_bytes);
        try { plan_bytes(retained, std::numeric_limits<UINT>::max(), budget); }
        catch (const resource_unavailable&) { clear_buffers(); }
    }

    Session() {
        D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_11_0;
        // Hardware only: WARP is a CPU implementation, not GPU acceleration.
        check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
            &level, 1, D3D11_SDK_VERSION, &device, nullptr, &context));
        ComPtr<IDXGIDevice> dxgi;
        ComPtr<IDXGIAdapter> base_adapter;
        if (SUCCEEDED(device.As(&dxgi)) && SUCCEEDED(dxgi->GetAdapter(&base_adapter)))
            base_adapter.As(&adapter);
        auto compile = [&](const char* entry, ComPtr<ID3D11ComputeShader>& shader) {
            ComPtr<ID3DBlob> code, errors;
            HRESULT hr = D3DCompile(kernels, std::strlen(kernels), "OpenRevelareCompute", nullptr, nullptr,
                entry, "cs_5_0", D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_IEEE_STRICTNESS |
                D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
            if (FAILED(hr)) throw std::runtime_error(errors ?
                static_cast<const char*>(errors->GetBufferPointer()) : "Compute shader compilation failed");
            check(device->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &shader));
        };
        compile("resize_axis", resize);
        compile("sharpen_horizontal", horizontal);
        compile("sharpen_vertical", vertical);
        compile("film_lut", film);
        D3D11_BUFFER_DESC desc{};
        desc.ByteWidth = sizeof(Params);
        desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        desc.Usage = D3D11_USAGE_DEFAULT;
        check(device->CreateBuffer(&desc, nullptr, &constants));
    }
    Buffer& buffer(int slot, size_t count, const void* source = nullptr) {
        size_t bytes = count * sizeof(float);
        if (bytes == 0 || bytes > std::numeric_limits<UINT>::max())
            throw std::runtime_error("Compute buffer size exceeds Direct3D limits");
        auto& b = buffers.at(slot);
        if (b.bytes < bytes) {
            b = Buffer{};
            D3D11_BUFFER_DESC desc{};
            desc.ByteWidth = UINT(bytes);
            desc.Usage = D3D11_USAGE_DEFAULT;
            desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
            desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
            desc.StructureByteStride = sizeof(float);
            check(device->CreateBuffer(&desc, nullptr, &b.value));
            check(device->CreateShaderResourceView(b.value.Get(), nullptr, &b.srv));
            check(device->CreateUnorderedAccessView(b.value.Get(), nullptr, &b.uav));
            b.bytes = bytes;
        }
        if (source) {
            D3D11_BOX box{0,0,0,UINT(bytes),1,1};
            context->UpdateSubresource(b.value.Get(), 0, &box, source, 0, 0);
        }
        return b;
    }
    void dispatch(ID3D11ComputeShader* shader, Params p, int src, int aux, int weights, int meta, int dst) {
        ID3D11ShaderResourceView* views[] = {buffers[src].srv.Get(), buffers[aux].srv.Get(),
            buffers[weights].srv.Get(), buffers[meta].srv.Get()};
        auto uav = buffers[dst].uav.Get();
        auto cb = constants.Get();
        context->UpdateSubresource(cb, 0, nullptr, &p, 0, 0);
        context->CSSetConstantBuffers(0,1,&cb);
        context->CSSetShaderResources(0,4,views);
        context->CSSetUnorderedAccessViews(0,1,&uav,nullptr);
        context->CSSetShader(shader,nullptr,0);
        int w = shader == resize.Get() ? p.dw : p.sw;
        int h = shader == resize.Get() ? p.dh : p.sh;
        context->Dispatch((w+15)/16,(h+15)/16,1);
        ID3D11UnorderedAccessView* no_uav = nullptr;
        ID3D11ShaderResourceView* no_views[4]{};
        context->CSSetUnorderedAccessViews(0,1,&no_uav,nullptr);
        context->CSSetShaderResources(0,4,no_views);
    }
    void read(int slot, float* destination, size_t count) {
        size_t bytes = count * sizeof(float);
        if (staging_bytes < bytes) {
            staging.Reset();
            D3D11_BUFFER_DESC desc{};
            desc.ByteWidth = UINT(bytes);
            desc.Usage = D3D11_USAGE_STAGING;
            desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            check(device->CreateBuffer(&desc,nullptr,&staging));
            staging_bytes = bytes;
        }
        D3D11_BOX box{0,0,0,UINT(bytes),1,1};
        context->CopySubresourceRegion(staging.Get(),0,0,0,0,buffers[slot].value.Get(),0,&box);
        D3D11_MAPPED_SUBRESOURCE mapped{};
        check(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
        std::memcpy(destination,mapped.pData,bytes);
        context->Unmap(staging.Get(),0);
        check(device->GetDeviceRemovedReason());
        size_t retained = staging_bytes;
        for (const auto& b : buffers) retained += b.bytes;
        // Cache modest preview buffers, but do not retain gigabytes after an export.
        if (retained > 128 * 1024 * 1024) {
            buffers = {};
            staging.Reset();
            staging_bytes = 0;
        }
    }
};
}

OR_GPU void* or_gpu_create(char* error, int error_size) {
    try { return new Session(); }
    catch (const std::exception& e) { set_error(error,error_size,e.what()); return nullptr; }
}
OR_GPU void or_gpu_destroy(void* session) { delete static_cast<Session*>(session); }

OR_GPU int or_gpu_resize(void* session, const float* input, int sw, int sh, float* output, int dw, int dh,
    const int* xm, const float* xw, int xcount, const int* ym, const float* yw, int ycount,
    char* error, int error_size) {
    try {
        auto& s = *static_cast<Session*>(session);
        s.prepare(resize_plan(sw,sh,dw,dh,xcount,ycount,true));
        s.buffer(0,size_t(sw)*sh*3,input);
        s.buffer(1,size_t(dw)*sh*3);
        s.buffer(2,size_t(dw)*dh*3);
        s.buffer(3,xcount,xw); s.buffer(4,size_t(dw)*3,xm);
        s.buffer(5,ycount,yw); s.buffer(6,size_t(dh)*3,ym);
        Params p{sw,sh,dw,sh,0,0,0,0,0};
        s.dispatch(s.resize.Get(),p,0,0,3,4,1);
        p.sw = dw; p.dh = dh; p.vertical = 1;
        s.dispatch(s.resize.Get(),p,1,1,5,6,2);
        s.read(2,output,size_t(dw)*dh*3);
        return 0;
    } catch (const resource_unavailable& e) { static_cast<Session*>(session)->clear_buffers(); set_error(error,error_size,e.what()); return 2; }
    catch (const std::bad_alloc& e) { static_cast<Session*>(session)->clear_buffers(); set_error(error,error_size,e.what()); return 2; }
    catch (const std::exception& e) { set_error(error,error_size,e.what()); return 1; }
}

OR_GPU int or_gpu_sharpen(void* session, const float* input, float* output, int w, int h,
    const float* kernel, int count, float amount, float threshold, float limit, char* error, int error_size) {
    try {
        auto& s = *static_cast<Session*>(session);
        s.prepare(sharpen_plan(w,h,count,true));
        s.buffer(0,size_t(w)*h*3,input); s.buffer(1,size_t(w)*h); s.buffer(2,size_t(w)*h*3);
        s.buffer(3,count,kernel); s.buffer(4,1);
        Params p{w,h,w,h,0,count/2,amount,threshold,limit};
        s.dispatch(s.horizontal.Get(),p,0,0,3,4,1);
        s.dispatch(s.vertical.Get(),p,0,1,3,4,2);
        s.read(2,output,size_t(w)*h*3);
        return 0;
    } catch (const resource_unavailable& e) { static_cast<Session*>(session)->clear_buffers(); set_error(error,error_size,e.what()); return 2; }
    catch (const std::bad_alloc& e) { static_cast<Session*>(session)->clear_buffers(); set_error(error,error_size,e.what()); return 2; }
    catch (const std::exception& e) { set_error(error,error_size,e.what()); return 1; }
}

OR_GPU int or_gpu_cube_lut(void* session, const float* input, float* output, int pixels,
    const float* lut, int edge, const float* parameters, char* error, int error_size) {
    try {
        auto& s = *static_cast<Session*>(session);
        size_t bytes = size_t(pixels)*12;
        size_t lut_count = size_t(edge)*edge*edge*3;
        s.prepare({bytes,lut_count*4,bytes,6*4,4,0,0,0,bytes});
        s.buffer(0,size_t(pixels)*3,input); s.buffer(1,lut_count,lut);
        s.buffer(2,size_t(pixels)*3); s.buffer(3,6,parameters); s.buffer(4,1);
        int width = std::min(pixels,1024);
        Params p{width,(pixels+width-1)/width,pixels,1,0,edge,0,0,0};
        s.dispatch(s.film.Get(),p,0,1,3,4,2);
        s.read(2,output,size_t(pixels)*3);
        return 0;
    } catch (const resource_unavailable& e) { static_cast<Session*>(session)->clear_buffers(); set_error(error,error_size,e.what()); return 2; }
    catch (const std::bad_alloc& e) { static_cast<Session*>(session)->clear_buffers(); set_error(error,error_size,e.what()); return 2; }
    catch (const std::exception& e) { set_error(error,error_size,e.what()); return 1; }
}

OR_GPU int or_gpu_resize_sharpen(void* session, const float* input, int sw, int sh, float* output, int dw, int dh,
    const int* xm, const float* xw, int xcount, const int* ym, const float* yw, int ycount,
    const float* kernel, int count, float amount, float threshold, float limit, char* error, int error_size) {
    try {
        auto& s = *static_cast<Session*>(session);
        s.prepare(delivery_plan(sw,sh,dw,dh,xcount,ycount,count,true));
        s.buffer(0,std::max(size_t(sw)*sh*3,size_t(dw)*dh*3));
        s.buffer(0,size_t(sw)*sh*3,input);
        s.buffer(1,size_t(dw)*sh*3); s.buffer(2,size_t(dw)*dh*3);
        s.buffer(3,xcount,xw); s.buffer(4,std::max(size_t(dw)*3,size_t(dw)*dh));
        s.buffer(4,size_t(dw)*3,xm); s.buffer(5,ycount,yw); s.buffer(6,size_t(dh)*3,ym);
        s.buffer(7,count,kernel);
        Params p{sw,sh,dw,sh,0,0,0,0,0};
        s.dispatch(s.resize.Get(),p,0,0,3,4,1);
        p.sw=dw; p.dh=dh; p.vertical=1;
        s.dispatch(s.resize.Get(),p,1,1,5,6,2);
        p = Params{dw,dh,dw,dh,0,count/2,amount,threshold,limit};
        s.dispatch(s.horizontal.Get(),p,2,2,7,6,4);
        s.dispatch(s.vertical.Get(),p,2,4,7,6,0);
        s.read(0,output,size_t(dw)*dh*3);
        return 0;
    } catch (const resource_unavailable& e) { static_cast<Session*>(session)->clear_buffers(); set_error(error,error_size,e.what()); return 2; }
    catch (const std::bad_alloc& e) { static_cast<Session*>(session)->clear_buffers(); set_error(error,error_size,e.what()); return 2; }
    catch (const std::exception& e) { set_error(error,error_size,e.what()); return 1; }
}
