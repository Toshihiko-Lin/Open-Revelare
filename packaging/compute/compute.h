#pragma once
#include <cstdint>
#include <cstring>
#include <stdexcept>
#include <string>

#ifdef _WIN32
#define OR_GPU extern "C" __declspec(dllexport)
#else
#define OR_GPU extern "C" __attribute__((visibility("default")))
#endif

struct Params {
    int sw, sh, dw, dh;
    int vertical, radius;
    float amount, threshold;
    float limit;
    int reserved[3]{};
};
static_assert(sizeof(Params) == 48);

inline void set_error(char* error, int size, const char* message) {
    if (error && size > 0) {
        std::strncpy(error, message, size_t(size - 1));
        error[size - 1] = 0;
    }
}

// One shader source is compiled by both D3DCompile and Metal. The host computes
// resampling taps in double precision, shared with the CPU implementation.
inline constexpr char kernels[] = R"SHADER(
#ifdef __METAL_VERSION__
#include <metal_stdlib>
using namespace metal;
#pragma clang fp contract(off)
struct Params {
    int sw, sh, dw, dh;
    int vertical, radius;
    float amount, threshold;
    float limit;
    int reserved[3];
};
#define KERNEL(name) kernel void name( \
    const device float* input [[buffer(0)]], \
    const device float* auxiliary [[buffer(1)]], \
    const device float* weights [[buffer(2)]], \
    const device int* metadata [[buffer(3)]], \
    device float* output [[buffer(4)]], \
    constant Params& p [[buffer(5)]], uint2 tid [[thread_position_in_grid]])
#else
struct Params {
    int sw, sh, dw, dh;
    int vertical, radius;
    float amount, threshold;
    float limit;
    int3 reserved;
};
cbuffer Constants : register(b0) { Params p; }
StructuredBuffer<float> input : register(t0);
StructuredBuffer<float> auxiliary : register(t1);
StructuredBuffer<float> weights : register(t2);
StructuredBuffer<int> metadata : register(t3);
RWStructuredBuffer<float> output : register(u0);
#define KERNEL(name) [numthreads(16,16,1)] void name(uint3 tid : SV_DispatchThreadID)
#endif

#define LUMA(i) (0.2126f * input[(i)*3] + 0.7152f * input[(i)*3+1] + 0.0722f * input[(i)*3+2])
float clamp_float(float v, float lo, float hi) { return isnan(v) ? v : clamp(v, lo, hi); }

KERNEL(resize_axis) {
    int x = int(tid.x), y = int(tid.y);
    if (x >= p.dw || y >= p.dh) return;
    int axis = p.vertical != 0 ? y : x;
    int start = metadata[axis*3], offset = metadata[axis*3+1], count = metadata[axis*3+2];
    float r = 0.0f, g = 0.0f, b = 0.0f;
    for (int t = 0; t < count; ++t) {
        float w = weights[offset+t];
        if (w == 0.0f) continue;
        int i = (p.vertical != 0 ? (start+t)*p.sw+x : y*p.sw+start+t)*3;
        r += input[i]*w; g += input[i+1]*w; b += input[i+2]*w;
    }
    int d = (y*p.dw+x)*3;
    output[d] = r; output[d+1] = g; output[d+2] = b;
}

KERNEL(sharpen_horizontal) {
    int x = int(tid.x), y = int(tid.y);
    if (x >= p.sw || y >= p.sh) return;
    float sum = 0.0f;
    for (int k = -p.radius; k <= p.radius; ++k) {
        int i = y*p.sw + clamp(x+k, 0, p.sw-1);
        sum += LUMA(i) * weights[k+p.radius];
    }
    output[y*p.sw+x] = sum;
}

KERNEL(sharpen_vertical) {
    int x = int(tid.x), y = int(tid.y);
    if (x >= p.sw || y >= p.sh) return;
    int i = y*p.sw+x;
    float blurred = 0.0f;
    for (int k = -p.radius; k <= p.radius; ++k)
        blurred += auxiliary[clamp(y+k, 0, p.sh-1)*p.sw+x] * weights[k+p.radius];
    float delta = LUMA(i) - blurred;
    bool unchanged = delta > -p.threshold && delta < p.threshold;
    float add = 0.0f;
    if (!unchanged) {
        delta -= delta > 0.0f ? p.threshold : -p.threshold;
        add = clamp_float(delta*p.amount, -p.limit, p.limit);
    }
    // Below threshold the CPU leaves the original samples untouched, including
    // any nonfinite/out-of-range value. Preserve that branch explicitly.
    for (int c = 0; c < 3; ++c)
        output[i*3+c] = unchanged ? input[i*3+c] : clamp_float(input[i*3+c]+add, 0.0f, 1.0f);
}
)SHADER";
