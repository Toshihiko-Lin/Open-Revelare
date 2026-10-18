#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#include <array>
#include <memory>
#include "compute.h"

namespace {
std::runtime_error failure(NSError* error) {
    return std::runtime_error(error ? error.localizedDescription.UTF8String : "Metal compute failed");
}
struct Session {
    id<MTLDevice> device;
    id<MTLCommandQueue> queue;
    id<MTLComputePipelineState> resize, horizontal, vertical, film;
    std::array<id<MTLBuffer>,8> buffers{};

    void clear_buffers() { buffers = {}; }
    void prepare(const BufferPlan& required) {
        // Leave half of Metal's recommended working set to presentation/other work.
        size_t budget = size_t(device.recommendedMaxWorkingSetSize / 2);
        plan_bytes(required, device.maxBufferLength, budget);
        BufferPlan retained{};
        for (size_t i = 0; i < buffers.size(); ++i)
            retained[i] = std::max(required[i], size_t(buffers[i] ? buffers[i].length : 0));
        try { plan_bytes(retained, device.maxBufferLength, budget); }
        catch (const resource_unavailable&) { clear_buffers(); }
    }

    Session() {
        device = MTLCreateSystemDefaultDevice();
        if (!device) throw std::runtime_error("No Metal GPU is available");
        queue = [device newCommandQueue];
        if (!queue) throw std::runtime_error("Cannot create Metal compute queue");
        MTLCompileOptions* options = [MTLCompileOptions new];
        // Keep the CPU's float operation order; do not contract multiply/add.
        options.fastMathEnabled = NO;
        NSError* error = nil;
        id<MTLLibrary> library = [device newLibraryWithSource:[NSString stringWithUTF8String:kernels]
            options:options error:&error];
        if (!library) throw failure(error);
        auto compile = [&](NSString* name) {
            id<MTLFunction> function = [library newFunctionWithName:name];
            if (!function) throw std::runtime_error("Metal compute function is missing");
            id<MTLComputePipelineState> result = [device newComputePipelineStateWithFunction:function error:&error];
            if (!result) throw failure(error);
            return result;
        };
        resize = compile(@"resize_axis");
        horizontal = compile(@"sharpen_horizontal");
        vertical = compile(@"sharpen_vertical");
        film = compile(@"film_lut");
    }
    void buffer(int slot, size_t count, const void* source = nullptr) {
        size_t bytes = count * sizeof(float);
        if (bytes == 0 || bytes > device.maxBufferLength)
            throw std::runtime_error("Compute buffer size exceeds Metal limits");
        if (!buffers[slot] || buffers[slot].length < bytes) {
            // Apple Silicon/integrated GPUs share memory. Discrete Intel-Mac GPUs
            // need managed resources and explicit CPU/GPU synchronization.
            MTLResourceOptions storage = device.hasUnifiedMemory ? MTLResourceStorageModeShared : MTLResourceStorageModeManaged;
            buffers[slot] = [device newBufferWithLength:bytes options:storage];
            if (!buffers[slot]) throw std::bad_alloc();
        }
        if (source) {
            std::memcpy(buffers[slot].contents,source,bytes);
            if (!device.hasUnifiedMemory) [buffers[slot] didModifyRange:NSMakeRange(0,bytes)];
        }
    }
    void encode(id<MTLCommandBuffer> command, id<MTLComputePipelineState> pipeline,
        Params p, int src, int aux, int weights, int meta, int dst) {
        id<MTLComputeCommandEncoder> encoder = [command computeCommandEncoder];
        if (!encoder) throw std::runtime_error("Cannot create Metal compute encoder");
        [encoder setComputePipelineState:pipeline];
        [encoder setBuffer:buffers[src] offset:0 atIndex:0];
        [encoder setBuffer:buffers[aux] offset:0 atIndex:1];
        [encoder setBuffer:buffers[weights] offset:0 atIndex:2];
        [encoder setBuffer:buffers[meta] offset:0 atIndex:3];
        [encoder setBuffer:buffers[dst] offset:0 atIndex:4];
        [encoder setBytes:&p length:sizeof(p) atIndex:5];
        int w = pipeline == resize ? p.dw : p.sw;
        int h = pipeline == resize ? p.dh : p.sh;
        [encoder dispatchThreadgroups:MTLSizeMake((w+15)/16,(h+15)/16,1)
            threadsPerThreadgroup:MTLSizeMake(16,16,1)];
        [encoder endEncoding];
    }
    void read(id<MTLCommandBuffer> command, float* output, size_t count, int slot = 2) {
        if (!command) throw std::runtime_error("Cannot create Metal command buffer");
        if (!device.hasUnifiedMemory) {
            id<MTLBlitCommandEncoder> blit = [command blitCommandEncoder];
            if (!blit) throw std::runtime_error("Cannot create Metal readback encoder");
            [blit synchronizeResource:buffers[slot]];
            [blit endEncoding];
        }
        [command commit];
        [command waitUntilCompleted];
        if (command.status != MTLCommandBufferStatusCompleted) throw failure(command.error);
        std::memcpy(output,buffers[slot].contents,count*sizeof(float));
        size_t retained = 0;
        for (auto b : buffers) if (b) retained += b.length;
        if (retained > 128 * 1024 * 1024) buffers = {};
    }
};
}

OR_GPU void* or_gpu_create(char* error, int error_size) {
    @autoreleasepool {
        try { return new Session(); }
        catch (const std::exception& e) { set_error(error,error_size,e.what()); return nullptr; }
    }
}
OR_GPU void or_gpu_destroy(void* session) { @autoreleasepool { delete static_cast<Session*>(session); } }

OR_GPU int or_gpu_resize(void* session, const float* input, int sw, int sh, float* output, int dw, int dh,
    const int* xm, const float* xw, int xcount, const int* ym, const float* yw, int ycount,
    char* error, int error_size) {
    @autoreleasepool {
        try {
            auto& s = *static_cast<Session*>(session);
            s.prepare(resize_plan(sw,sh,dw,dh,xcount,ycount,false));
            s.buffer(0,size_t(sw)*sh*3,input); s.buffer(1,size_t(dw)*sh*3); s.buffer(2,size_t(dw)*dh*3);
            s.buffer(3,xcount,xw); s.buffer(4,size_t(dw)*3,xm);
            s.buffer(5,ycount,yw); s.buffer(6,size_t(dh)*3,ym);
            id<MTLCommandBuffer> command = [s.queue commandBuffer];
            if (!command) throw std::runtime_error("Cannot create Metal command buffer");
            Params p{sw,sh,dw,sh,0,0,0,0,0};
            s.encode(command,s.resize,p,0,0,3,4,1);
            p.sw = dw; p.dh = dh; p.vertical = 1;
            s.encode(command,s.resize,p,1,1,5,6,2);
            s.read(command,output,size_t(dw)*dh*3);
            return 0;
        } catch (const resource_unavailable& e) { static_cast<Session*>(session)->clear_buffers(); set_error(error,error_size,e.what()); return 2; }
        catch (const std::bad_alloc& e) { static_cast<Session*>(session)->clear_buffers(); set_error(error,error_size,e.what()); return 2; }
        catch (const std::exception& e) { set_error(error,error_size,e.what()); return 1; }
    }
}

OR_GPU int or_gpu_sharpen(void* session, const float* input, float* output, int w, int h,
    const float* kernel, int count, float amount, float threshold, float limit, char* error, int error_size) {
    @autoreleasepool {
        try {
            auto& s = *static_cast<Session*>(session);
            s.prepare(sharpen_plan(w,h,count,false));
            s.buffer(0,size_t(w)*h*3,input); s.buffer(1,size_t(w)*h); s.buffer(2,size_t(w)*h*3);
            s.buffer(3,count,kernel); s.buffer(4,1);
            id<MTLCommandBuffer> command = [s.queue commandBuffer];
            if (!command) throw std::runtime_error("Cannot create Metal command buffer");
            Params p{w,h,w,h,0,count/2,amount,threshold,limit};
            s.encode(command,s.horizontal,p,0,0,3,4,1);
            s.encode(command,s.vertical,p,0,1,3,4,2);
            s.read(command,output,size_t(w)*h*3);
            return 0;
        } catch (const resource_unavailable& e) { static_cast<Session*>(session)->clear_buffers(); set_error(error,error_size,e.what()); return 2; }
        catch (const std::bad_alloc& e) { static_cast<Session*>(session)->clear_buffers(); set_error(error,error_size,e.what()); return 2; }
        catch (const std::exception& e) { set_error(error,error_size,e.what()); return 1; }
    }
}

OR_GPU int or_gpu_cube_lut(void* session, const float* input, float* output, int pixels,
    const float* lut, int edge, const float* parameters, char* error, int error_size) {
    @autoreleasepool {
        try {
            auto& s = *static_cast<Session*>(session);
            size_t bytes = size_t(pixels)*12;
            size_t lut_count = size_t(edge)*edge*edge*3;
            s.prepare({bytes,lut_count*4,bytes,6*4,4,0,0,0,0});
            s.buffer(0,size_t(pixels)*3,input); s.buffer(1,lut_count,lut);
            s.buffer(2,size_t(pixels)*3); s.buffer(3,6,parameters); s.buffer(4,1);
            id<MTLCommandBuffer> command = [s.queue commandBuffer];
            if (!command) throw std::runtime_error("Cannot create Metal command buffer");
            int width = std::min(pixels,1024);
            Params p{width,(pixels+width-1)/width,pixels,1,0,edge,0,0,0};
            s.encode(command,s.film,p,0,1,3,4,2);
            s.read(command,output,size_t(pixels)*3);
            return 0;
        } catch (const resource_unavailable& e) { static_cast<Session*>(session)->clear_buffers(); set_error(error,error_size,e.what()); return 2; }
        catch (const std::bad_alloc& e) { static_cast<Session*>(session)->clear_buffers(); set_error(error,error_size,e.what()); return 2; }
        catch (const std::exception& e) { set_error(error,error_size,e.what()); return 1; }
    }
}

OR_GPU int or_gpu_resize_sharpen(void* session, const float* input, int sw, int sh, float* output, int dw, int dh,
    const int* xm, const float* xw, int xcount, const int* ym, const float* yw, int ycount,
    const float* kernel, int count, float amount, float threshold, float limit, char* error, int error_size) {
    @autoreleasepool {
        try {
            auto& s = *static_cast<Session*>(session);
            s.prepare(delivery_plan(sw,sh,dw,dh,xcount,ycount,count,false));
            s.buffer(0,std::max(size_t(sw)*sh*3,size_t(dw)*dh*3));
            s.buffer(0,size_t(sw)*sh*3,input);
            s.buffer(1,size_t(dw)*sh*3); s.buffer(2,size_t(dw)*dh*3);
            s.buffer(3,xcount,xw); s.buffer(4,std::max(size_t(dw)*3,size_t(dw)*dh));
            s.buffer(4,size_t(dw)*3,xm); s.buffer(5,ycount,yw); s.buffer(6,size_t(dh)*3,ym);
            s.buffer(7,count,kernel);
            id<MTLCommandBuffer> command = [s.queue commandBuffer];
            if (!command) throw std::runtime_error("Cannot create Metal command buffer");
            Params p{sw,sh,dw,sh,0,0,0,0,0};
            s.encode(command,s.resize,p,0,0,3,4,1);
            p.sw=dw; p.dh=dh; p.vertical=1;
            s.encode(command,s.resize,p,1,1,5,6,2);
            p = Params{dw,dh,dw,dh,0,count/2,amount,threshold,limit};
            s.encode(command,s.horizontal,p,2,2,7,6,4);
            s.encode(command,s.vertical,p,2,4,7,6,0);
            s.read(command,output,size_t(dw)*dh*3,0);
            return 0;
        } catch (const resource_unavailable& e) { static_cast<Session*>(session)->clear_buffers(); set_error(error,error_size,e.what()); return 2; }
        catch (const std::bad_alloc& e) { static_cast<Session*>(session)->clear_buffers(); set_error(error,error_size,e.what()); return 2; }
        catch (const std::exception& e) { set_error(error,error_size,e.what()); return 1; }
    }
}
