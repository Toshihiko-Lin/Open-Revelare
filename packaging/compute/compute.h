#pragma once
#include <cstdint>
#include <cstring>
#include <stdexcept>
#include <string>
#include <array>
#include <algorithm>
#include <limits>

// Resource pressure is recoverable: only this operation should use the CPU.
struct resource_unavailable : std::runtime_error {
    using std::runtime_error::runtime_error;
};
using BufferPlan = std::array<size_t, 9>; // eight device buffers plus optional readback

inline size_t plan_bytes(const BufferPlan& plan, size_t max_buffer, size_t budget) {
    // Kernels address float elements with signed 32-bit indices on both APIs.
    max_buffer = std::min(max_buffer, size_t(std::numeric_limits<int32_t>::max()) * sizeof(float));
    size_t total = 0;
    for (size_t bytes : plan) {
        if (bytes > max_buffer || bytes > budget - total)
            throw resource_unavailable("GPU working set exceeds device memory budget");
        total += bytes;
    }
    return total;
}

inline BufferPlan resize_plan(int sw, int sh, int dw, int dh, int xc, int yc, bool readback) {
    return {size_t(sw)*sh*12, size_t(dw)*sh*12, size_t(dw)*dh*12,
        size_t(xc)*4, size_t(dw)*12, size_t(yc)*4, size_t(dh)*12, 0,
        readback ? size_t(dw)*dh*12 : 0};
}

inline BufferPlan sharpen_plan(int w, int h, int count, bool readback) {
    return {size_t(w)*h*12, size_t(w)*h*4, size_t(w)*h*12,
        size_t(count)*4, 4, 0, 0, 0, readback ? size_t(w)*h*12 : 0};
}

inline BufferPlan delivery_plan(int sw, int sh, int dw, int dh, int xc, int yc, int count, bool readback) {
    auto plan = resize_plan(sw,sh,dw,dh,xc,yc,readback);
    plan[0] = std::max(plan[0],size_t(dw)*dh*12); // reuse uploaded source for final output
    plan[4] = std::max(plan[4],size_t(dw)*dh*4);  // metadata then blur intermediate
    plan[7] = size_t(count)*4;
    return plan;
}

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
#define PRECISE
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
#define PRECISE precise
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

// Red-fastest tetrahedral interpolation, using the CPU's expression order.
// weights: domain scales followed by biases. auxiliary: RGB cube.
KERNEL(film_lut) {
    int pixel = int(tid.y)*p.sw + int(tid.x);
    if (int(tid.x) >= p.sw || pixel >= p.dw) return;
    int last = p.radius - 1;
    PRECISE float fr = clamp(input[pixel*3]*weights[0]+weights[3], 0.0f, float(last));
    PRECISE float fg = clamp(input[pixel*3+1]*weights[1]+weights[4], 0.0f, float(last));
    PRECISE float fb = clamp(input[pixel*3+2]*weights[2]+weights[5], 0.0f, float(last));
    int r0 = min(int(fr), last-1), g0 = min(int(fg), last-1), b0 = min(int(fb), last-1);
    PRECISE float dr = fr-r0, dg = fg-g0, db = fb-b0;
    int sg = p.radius*3, sb = p.radius*p.radius*3;
    int base = r0*3 + g0*sg + b0*sb;
    int ca, cb;
    PRECISE float w0, wa, wb, w1;
    if (dr >= dg) {
        if (dg >= db) { ca=base+3; cb=base+3+sg; w0=1.0f-dr; wa=dr-dg; wb=dg-db; w1=db; }
        else if (dr >= db) { ca=base+3; cb=base+3+sb; w0=1.0f-dr; wa=dr-db; wb=db-dg; w1=dg; }
        else { ca=base+sb; cb=base+3+sb; w0=1.0f-db; wa=db-dr; wb=dr-dg; w1=dg; }
    } else {
        if (db >= dg) { ca=base+sb; cb=base+sg+sb; w0=1.0f-db; wa=db-dg; wb=dg-dr; w1=dr; }
        else if (db >= dr) { ca=base+sg; cb=base+sg+sb; w0=1.0f-dg; wa=dg-db; wb=db-dr; w1=dr; }
        else { ca=base+sg; cb=base+3+sg; w0=1.0f-dg; wa=dg-dr; wb=dr-db; w1=db; }
    }
    int end = base+3+sg+sb;
    for (int c=0; c<3; ++c) {
        PRECISE float value = w0*auxiliary[base+c] + wa*auxiliary[ca+c] + wb*auxiliary[cb+c] + w1*auxiliary[end+c];
        output[pixel*3+c] = value;
    }
}
)SHADER";
